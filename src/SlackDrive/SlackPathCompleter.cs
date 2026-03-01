using System.Management.Automation;

namespace SlackDrive;

/// <summary>
/// Slack provider path の Tab 補完。IModuleAssemblyInitializer で自動登録。
/// チャンネル名・ユーザー名をキャッシュから高速補完し、ワイルドカードもサポート。
/// </summary>
public class SlackPathCompleter : IModuleAssemblyInitializer, IModuleAssemblyCleanup
{
    public void OnImport()
    {
        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
        ps.AddScript("""
            $slackCompleter = {
                param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
                [SlackDrive.SlackPathCompleter]::Complete($wordToComplete)
            }
            $cmds = @('Get-ChildItem','Set-Location','Get-Content','Get-Item','Push-Location','Resolve-Path','Test-Path')
            Register-ArgumentCompleter -CommandName $cmds -ParameterName Path -ScriptBlock $slackCompleter
            Register-ArgumentCompleter -CommandName $cmds -ParameterName LiteralPath -ScriptBlock $slackCompleter
        """);
        ps.Invoke();
    }

    public void OnRemove(PSModuleInfo psModuleInfo) { }

    /// <summary>エントリーポイント (ScriptBlock から呼ばれる)</summary>
    public static List<CompletionResult> Complete(string wordToComplete)
    {
        try { return CompleteInternal(wordToComplete ?? ""); }
        catch { return new(); }
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
                return new(); // Slack ドライブでなければスキップ

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

        if (driveObj is not SlackDriveInfo slackDrive) return new();

        // ── パス解析 ──
        var normalized = pathInDrive.TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // 入力テキストの「最後のセパレータまで」を補完テキストの prefix にする
        var lastSep = Math.Max(wordToComplete.LastIndexOf('\\'), wordToComplete.LastIndexOf('/'));
        var inputPrefix = lastSep >= 0 ? wordToComplete[..(lastSep + 1)] : "";
        var filterText = lastSep >= 0 ? wordToComplete[(lastSep + 1)..] : wordToComplete;
        // ドライブプレフィックスのみ ("UiPath:") の場合
        if (colonIdx >= 0 && lastSep < colonIdx)
        {
            inputPrefix = wordToComplete[..(colonIdx + 1)] + "\\";
            filterText = wordToComplete[(colonIdx + 1)..].TrimStart('\\', '/');
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

        // ── Channels 配下 ──
        if (category == "channels" &&
            ((segments.Length == 1 && normalized.EndsWith('/')) || segments.Length == 2))
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

    private static CompletionResult MakeResult(string completionText, string listItemText, CompletionResultType type)
    {
        // スペースや特殊文字を含むパスはクォート
        if (completionText.IndexOfAny([' ', '(', ')', '{', '}', '[', ']', '&', '#', ';', '@']) >= 0)
            completionText = "'" + completionText + "'";
        return new CompletionResult(completionText, listItemText, type, listItemText);
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
}
