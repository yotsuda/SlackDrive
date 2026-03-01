using System.Collections;
using System.Collections.Generic;
using System.Management.Automation;
using System.Management.Automation.Language;

namespace SlackDrive;

/// <summary>
/// モジュール読み込み時に SlackPathCompleter を自動登録。
/// </summary>
public class SlackModuleInitializer : IModuleAssemblyInitializer, IModuleAssemblyCleanup
{
    public void OnImport()
    {
        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
        ps.AddScript("""
            $completer = [SlackDrive.SlackPathCompleter]::new()
            $cmds = @('Get-ChildItem','Set-Location','Get-Content','Get-Item',
                       'Push-Location','Resolve-Path','Test-Path','Invoke-Item')
            $sb = {
                param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
                $completer.CompleteArgument($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
            }
            Register-ArgumentCompleter -CommandName $cmds -ParameterName Path -ScriptBlock $sb
            Register-ArgumentCompleter -CommandName $cmds -ParameterName LiteralPath -ScriptBlock $sb
        """);
        ps.Invoke();
    }

    public void OnRemove(PSModuleInfo psModuleInfo) { }
}

/// <summary>
/// Slack provider path の Tab 補完。IArgumentCompleter 実装。
/// チャンネル名・ユーザー名・メッセージをキャッシュ/API から補完し、ワイルドカードもサポート。
/// </summary>
public class SlackPathCompleter : IArgumentCompleter
{
    public IEnumerable<CompletionResult> CompleteArgument(
        string commandName,
        string parameterName,
        string wordToComplete,
        CommandAst commandAst,
        IDictionary fakeBoundParameters)
    {
        try { return CompleteInternal(wordToComplete ?? ""); }
        catch { return []; }
    }

    private static List<CompletionResult> CompleteInternal(string wordToComplete)
    {
        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);

        // ── ドライブ名とパスを判定 ──
        string? driveName;
        string pathInDrive;

        var colonIdx = wordToComplete.IndexOf(':');
        if (colonIdx > 0)
        {
            // 絶対パス: "DriveName:\Channels\t"
            driveName = wordToComplete[..colonIdx];
            pathInDrive = wordToComplete[(colonIdx + 1)..].Replace('\\', '/').TrimStart('/');
        }
        else
        {
            // 相対パス: カレントロケーションから判定
            ps.AddScript("(Get-Location).Drive");
            var currentDrive = ps.Invoke().FirstOrDefault()?.BaseObject as PSDriveInfo;
            ps.Commands.Clear();

            if (currentDrive?.Provider?.Name != "Slack")
                return []; // Slack ドライブでなければスキップ

            driveName = currentDrive.Name;

            ps.AddScript("(Get-Location).ProviderPath");
            var provPath = ps.Invoke<string>().FirstOrDefault() ?? "";
            ps.Commands.Clear();

            var currentDir = provPath.Replace('\\', '/').Trim('/');
            pathInDrive = string.IsNullOrEmpty(currentDir)
                ? wordToComplete.Replace('\\', '/')
                : currentDir + "/" + wordToComplete.Replace('\\', '/');
        }

        // Slack ドライブか確認
        ps.AddCommand("Get-PSDrive").AddParameter("Name", driveName)
            .AddParameter("ErrorAction", "SilentlyContinue");
        var driveObj = ps.Invoke().FirstOrDefault()?.BaseObject;
        ps.Commands.Clear();

        if (driveObj is not SlackDriveInfo slackDrive) return [];

        // ── パス解析 ──
        var normalized = pathInDrive.TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // 入力テキストの「最後のセパレータまで」を補完テキストの prefix にする
        var lastSep = Math.Max(wordToComplete.LastIndexOf('\\'), wordToComplete.LastIndexOf('/'));
        var inputPrefix = lastSep >= 0 ? wordToComplete[..(lastSep + 1)] : "";
        // ドライブプレフィックスのみ ("UiPath:") の場合
        if (colonIdx >= 0 && lastSep < colonIdx)
        {
            inputPrefix = wordToComplete[..(colonIdx + 1)] + "\\";
        }

        var results = new List<CompletionResult>();

        // ── ルートレベル: Channels / Users / Files ──
        if (segments.Length == 0 || (segments.Length == 1 && !normalized.EndsWith('/')))
        {
            var pattern = MakeWildcard(segments.Length == 1 ? segments[0] : "");
            foreach (var name in new[] { "Channels", "Users", "Files" })
            {
                if (pattern.IsMatch(name))
                    results.Add(MakeResult(inputPrefix + name, name, CompletionResultType.ProviderContainer));
            }
            return results;
        }

        var category = segments[0].ToLower();

        // ── Channels 配下 (チャンネル名) ──
        if (category == "channels" &&
            ((segments.Length == 1 && normalized.EndsWith('/')) || segments.Length == 2)
            && !(segments.Length == 2 && normalized.EndsWith('/')))
        {
            var channels = GetChannels(slackDrive);
            if (channels == null) return results;

            var filter = segments.Length == 2 ? segments[1] : "";
            var pattern = MakeWildcard(filter);

            foreach (var ch in channels.Values.OrderBy(c => c.Name))
            {
                if (pattern.IsMatch(ch.Name))
                    results.Add(MakeResult(inputPrefix + ch.Name, ch.Name, CompletionResultType.ProviderContainer));
            }
        }
        // ── Channels/<channel>/messages (メッセージ) ──
        else if (category == "channels" && segments.Length >= 2 &&
                 (segments.Length == 3 || (segments.Length == 2 && normalized.EndsWith('/'))))
        {
            var channelName = segments[1];
            var channels = GetChannels(slackDrive);
            if (channels != null && channels.TryGetValue(channelName, out var channel))
            {
                var messages = FetchRecentMessages(slackDrive, channel.Id);
                if (messages != null)
                {
                    var users = GetUsers(slackDrive);
                    var filter = segments.Length == 3 ? segments[2] : "";
                    var pattern = MakeWildcard(filter);

                    foreach (var msg in messages)
                    {
                        var ts = msg.Ts;
                        if (!pattern.IsMatch(ts)) continue;

                        var userName = ResolveUserName(users, msg.UserId);
                        var text = msg.Text.Replace("\n", " ");
                        if (text.Length > 50) text = text[..47] + "...";

                        var listText = $"{msg.Timestamp:MM-dd HH:mm} @{userName}: {text}";
                        results.Add(new CompletionResult(
                            QuoteIfNeeded(inputPrefix + ts),
                            listText,
                            CompletionResultType.ProviderItem,
                            $"{msg.Timestamp:yyyy-MM-dd HH:mm} @{userName}\n{msg.Text}"));
                    }
                }
            }
        }
        // ── Users 配下 ──
        else if (category == "users" &&
                 ((segments.Length == 1 && normalized.EndsWith('/')) || segments.Length == 2))
        {
            var users = GetUsers(slackDrive);
            if (users == null) return results;

            var filter = segments.Length == 2 ? segments[1] : "";
            var pattern = MakeWildcard(filter);

            foreach (var u in users.Values.Where(u => !u.IsDeleted).OrderBy(u => u.Name))
            {
                if (pattern.IsMatch(u.Name))
                    results.Add(MakeResult(inputPrefix + u.Name, u.Name, CompletionResultType.ProviderItem));
            }
        }

        return results;
    }

    // ── ヘルパー ──

    private static WildcardPattern MakeWildcard(string filter)
    {
        if (string.IsNullOrEmpty(filter))
            return new WildcardPattern("*", WildcardOptions.IgnoreCase);
        if (filter.Contains('*') || filter.Contains('?'))
            return new WildcardPattern(filter, WildcardOptions.IgnoreCase);
        return new WildcardPattern(filter + "*", WildcardOptions.IgnoreCase);
    }

    private static string QuoteIfNeeded(string text)
    {
        if (text.IndexOfAny([' ', '(', ')', '{', '}', '[', ']', '&', '#', ';', '@']) >= 0)
            return "'" + text + "'";
        return text;
    }

    private static CompletionResult MakeResult(string completionText, string listItemText, CompletionResultType type)
    {
        return new CompletionResult(QuoteIfNeeded(completionText), listItemText, type, listItemText);
    }

    private static string ResolveUserName(Dictionary<string, SlackUser>? users, string userId)
    {
        if (users == null) return userId;
        return users.Values.FirstOrDefault(x => x.Id == userId)?.Name ?? userId;
    }

    private static Dictionary<string, SlackChannel>? GetChannels(SlackDriveInfo drive)
    {
        var channels = drive.Cache.Channels;
        if (channels != null) return channels;

        // ファイルキャッシュ (自 TeamId → 他キャッシュの順)
        channels = LoadChannelsFromFile(drive.TeamId);
        if (channels == null)
        {
            var cacheDir = Path.Combine(SlackDriveConfigManager.GetConfigFolderPath(), "cache");
            if (Directory.Exists(cacheDir))
            {
                foreach (var file in Directory.GetFiles(cacheDir, "*.json"))
                {
                    channels = LoadChannelsFromFile(Path.GetFileNameWithoutExtension(file));
                    if (channels is { Count: > 0 }) break;
                }
            }
        }

        if (channels != null) drive.Cache.Channels = channels;
        return channels;
    }

    private static Dictionary<string, SlackChannel>? LoadChannelsFromFile(string teamId)
    {
        var cache = SlackCacheManager.LoadCache(teamId);
        if (cache?.Channels == null) return null;
        return cache.Channels.ToDictionary(c => c.Name);
    }

    private static Dictionary<string, SlackUser>? GetUsers(SlackDriveInfo drive)
    {
        var users = drive.Cache.Users;
        if (users != null) return users;

        var cache = SlackCacheManager.LoadCache(drive.TeamId);
        if (cache?.Users == null) return null;

        users = cache.Users.ToDictionary(u => u.Name);
        drive.Cache.Users = users;
        return users;
    }

    private static List<SlackMessage>? FetchRecentMessages(SlackDriveInfo drive, string channelId)
    {
        try
        {
            var queryParams = new Dictionary<string, string>
            {
                ["channel"] = channelId,
                ["limit"] = "20"
            };

            var doc = drive.Client.GetAsync("conversations.history", queryParams).GetAwaiter().GetResult();
            var root = doc.RootElement;

            if (!root.GetProperty("ok").GetBoolean())
                return null;

            var messages = new List<SlackMessage>();
            foreach (var m in root.GetProperty("messages").EnumerateArray())
            {
                var userId = m.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "";
                var ts = m.GetProperty("ts").GetString() ?? "";

                messages.Add(new SlackMessage
                {
                    Ts = ts,
                    UserId = userId,
                    Text = m.GetProperty("text").GetString() ?? "",
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds(
                        (long)double.Parse(ts.Split('.')[0])).DateTime,
                    ReplyCount = m.TryGetProperty("reply_count", out var rc) ? rc.GetInt32() : 0
                });
            }
            return messages;
        }
        catch
        {
            return null;
        }
    }
}
