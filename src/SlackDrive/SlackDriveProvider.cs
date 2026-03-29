using System.Collections;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Provider;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SlackDrive;

[CmdletProvider("Slack", ProviderCapabilities.ShouldProcess)]
[OutputType(typeof(SlackChannel), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
[OutputType(typeof(SlackUser), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
[OutputType(typeof(SlackMessage), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
[OutputType(typeof(SlackFile), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
[OutputType(typeof(SlackChannel), ProviderCmdlet = ProviderCmdlet.GetItem)]
[OutputType(typeof(SlackUser), ProviderCmdlet = ProviderCmdlet.GetItem)]
public class SlackDriveProvider : NavigationCmdletProvider, IContentCmdletProvider
{
    private SlackDriveInfo Drive => (SlackDriveInfo)PSDriveInfo;

    #region Drive Management

    protected override PSDriveInfo NewDrive(PSDriveInfo drive)
    {
        if (drive == null)
            throw new ArgumentNullException(nameof(drive));

        // ImportSlackConfig / 直接渡しから来た場合は既に SlackDriveInfo
        if (drive is SlackDriveInfo slackDrive)
            return slackDrive;

        // New-PSDrive コマンドから来た場合（Token 直接指定）
        var dynamicParams = DynamicParameters as SlackDriveParameters;
        if (dynamicParams == null || string.IsNullOrEmpty(dynamicParams.Token))
            throw new ArgumentException("Token parameter is required");

        return new SlackDriveInfo(drive, dynamicParams.Token, dynamicParams.Cookie);
    }

    protected override object NewDriveDynamicParameters() => new SlackDriveParameters();

    protected override PSDriveInfo RemoveDrive(PSDriveInfo drive)
    {
        if (drive is SlackDriveInfo slackDrive)
            slackDrive.Dispose();
        return drive;
    }

    protected override Collection<PSDriveInfo>? InitializeDefaultDrives()
    {
        SlackDriveConfigManager.EnsureDefaultConfigExists();
        return base.InitializeDefaultDrives();
    }

    protected override void StopProcessing()
    {
        if (PSDriveInfo is SlackDriveInfo slackDrive)
            slackDrive.CancelAuthentication();
    }

    #endregion

    #region Path Operations

    protected override bool IsValidPath(string path) => true;

    protected override string MakePath(string parent, string child)
    {
        string result = base.MakePath(parent, child);
        // 末尾のセパレータを除去 (ドライブルート "X:\" は除く)
        if (result.EndsWith('\\') && result.Length > 1 && result[^2] != ':')
            result = result[..^1];
        return result;
    }

    protected override string NormalizeRelativePath(string path, string basePath)
    {
        string result = base.NormalizeRelativePath(path, basePath);
        // ルート直下のパスの先頭 "\" を除去 (タブ補完で ".\\Items" → ".\Items" に)
        if (result.StartsWith('\\') && result.Length > 1)
            result = result[1..];
        return result;
    }

    protected override bool HasChildItems(string path)
    {
        return IsItemContainer(path);
    }

    #endregion

    #region Item Operations

    protected override bool ItemExists(string path)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrEmpty(normalized)) return true;

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;

        var firstPart = parts[0].ToLower();

        if (parts.Length == 1)
            return firstPart is "channels" or "directmessages" or "users" or "files";

        if (parts.Length == 2)
        {
            return firstPart switch
            {
                "channels" => GetChannelByName(parts[1]) != null,
                "directmessages" => true, // DM は名前ベースで存在チェックしない
                "users" => GetUserByName(parts[1]) != null,
                _ => false
            };
        }

        // Channels/<channel>/<ts> or DirectMessages/<dm>/<ts> — message item
        // Channels/<channel>/<ts>/<replyTs> — thread reply item
        if ((parts.Length == 3 || parts.Length == 4) && firstPart is "channels" or "directmessages")
            return GetChannelByName(parts[1]) != null;

        return false;
    }

    protected override bool IsItemContainer(string path)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrEmpty(normalized)) return true;

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return true;
        var cat = parts[0].ToLower();
        if (parts.Length == 2 && cat is "channels" or "directmessages") return true;
        if (parts.Length == 3 && cat is "channels" or "directmessages") return true;
        return false;
    }

    protected override void GetItem(string path)
    {
        var normalized = NormalizePath(path);

        if (string.IsNullOrEmpty(normalized))
        {
            WriteItemObject(new { Root = @"\", Workspace = Drive.TeamName }, path, true);
            return;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        switch (parts[0].ToLower())
        {
            case "channels" when parts.Length == 2:
                var channel = GetChannelByName(parts[1]);
                if (channel != null)
                {
                    var detail = channel.ToDetail();
                    FetchChannelDetails(detail);
                    WriteItemObject(detail, path, true);
                }
                break;
            case "users" when parts.Length == 2:
                var user = GetUserByName(parts[1]);
                if (user != null) WriteItemObject(user, path, false);
                break;
        }
    }

    protected override void NewItem(string path, string itemTypeName, object newItemValue)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrEmpty(normalized)) return;

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cat = parts[0].ToLower();

        if (cat is not "channels" and not "directmessages") return;

        var text = newItemValue?.ToString();
        if (string.IsNullOrEmpty(text))
        {
            WriteError(new ErrorRecord(
                new ArgumentException("Message text is required. Use -Value \"your message\""),
                "MessageTextRequired",
                ErrorCategory.InvalidArgument,
                path));
            return;
        }

        if (parts.Length == 2)
        {
            // New-Item Channels\general -Value "text" → 新規投稿
            var channel = GetChannelByName(parts[1]);
            if (channel == null)
            {
                WriteError(new ErrorRecord(
                    new ItemNotFoundException($"Channel not found: {parts[1]}"),
                    "ChannelNotFound",
                    ErrorCategory.ObjectNotFound,
                    parts[1]));
                return;
            }

            if (!ShouldProcess($"#{channel.Name}", $"Post message: {text}"))
                return;

            PostMessage(channel.Id, text, null);
        }
        else if (parts.Length == 3)
        {
            // New-Item Channels\general\<msg> -Value "text" → スレッド返信
            var channel = GetChannelByName(parts[1]);
            if (channel == null)
            {
                WriteError(new ErrorRecord(
                    new ItemNotFoundException($"Channel not found: {parts[1]}"),
                    "ChannelNotFound",
                    ErrorCategory.ObjectNotFound,
                    parts[1]));
                return;
            }

            var threadTs = ResolveMessageIdentifier(channel.Id, parts[2]);
            if (threadTs == null)
            {
                WriteError(new ErrorRecord(
                    new ItemNotFoundException($"Message not found: {parts[2]}"),
                    "MessageNotFound",
                    ErrorCategory.ObjectNotFound,
                    parts[2]));
                return;
            }

            if (!ShouldProcess($"#{channel.Name} thread", $"Reply: {text}"))
                return;

            PostMessage(channel.Id, text, threadTs);
        }
    }

    private void PostMessage(string channelId, string text, string? threadTs)
    {
        var body = new Dictionary<string, object> { ["channel"] = channelId, ["text"] = text };
        if (threadTs != null) body["thread_ts"] = threadTs;

        var doc = Drive.Client.PostAsync("chat.postMessage", body).GetAwaiter().GetResult();
        var root = doc.RootElement;

        if (!root.GetProperty("ok").GetBoolean())
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
            WriteError(new ErrorRecord(
                new InvalidOperationException($"Failed to post message: {error}"),
                "PostMessageFailed",
                ErrorCategory.WriteError,
                channelId));
            return;
        }

        // 投稿されたメッセージを SlackMessage として返す
        var msg = root.GetProperty("message");
        var ts = msg.GetProperty("ts").GetString() ?? "";
        var userId = msg.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "";
        var users = Drive.Cache.Users ?? EnsureUsersLoadedSync();
        var userName = users.Values.FirstOrDefault(x => x.Id == userId)?.Name ?? userId;
        var rawText = msg.GetProperty("text").GetString() ?? "";

        var message = new SlackMessage
        {
            Ts = ts,
            UserId = userId,
            UserName = userName,
            Text = ResolveSlackMentions(rawText, users),
            Timestamp = DateTimeOffset.FromUnixTimeSeconds((long)double.Parse(ts.Split('.')[0])).LocalDateTime,
            ReplyCount = 0,
            Directory = $"{PSDriveInfo.Name}:\\Channels"
        };
        WriteItemObject(message, ts, false);
    }

    protected override void GetChildItems(string path, bool recurse)
    {
        var normalized = NormalizePath(path);

        if (string.IsNullOrEmpty(normalized))
        {
            WriteRootChildren(path);
            return;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parameters = DynamicParameters as SlackPaginationParameters;
        int first = parameters?.First ?? 20;

        try
        {
            switch (parts[0].ToLower())
            {
                case "channels":
                    if (parts.Length == 1)
                        WriteChannels(path);
                    else if (parts.Length == 2)
                        WriteChannelMessages(parts[1], path, first);
                    else if (parts.Length == 3)
                        WriteThreadMessages(parts[1], parts[2], path);
                    break;
                case "directmessages":
                    if (parts.Length == 1)
                        WriteDirectMessages(path);
                    else if (parts.Length == 2)
                        WriteChannelMessages(parts[1], path, first);
                    else if (parts.Length == 3)
                        WriteThreadMessages(parts[1], parts[2], path);
                    break;
                case "users":
                    if (parts.Length == 1)
                        WriteUsers(path);
                    break;
                case "files":
                    if (parts.Length == 1)
                        WriteFiles(path, first);
                    break;
            }
        }
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(ex, "SlackApiError",
                ErrorCategory.ConnectionError, path));
        }

        // -ExportCsv: 結果を CSV に出力
        if (!string.IsNullOrEmpty(parameters?.ExportCsv))
        {
            var csvPath = ExportChildItemsCsv(normalized, parameters.ExportCsv, parameters.CsvEncoding, first);
            if (csvPath != null)
                WriteWarning($"CSV exported: {csvPath}");
        }
    }

    protected override object? GetChildItemsDynamicParameters(string path, bool recurse)
    {
        return new SlackPaginationParameters();
    }

    // GetChildNames は未オーバーライド — PowerShell が GetChildItems から名前を自動抽出する

    protected override void InvokeDefaultAction(string path)
    {
        var normalized = NormalizePath(path);
        var url = GetSlackUrl(normalized);
        if (url != null)
            OpenWithShell(url);
    }

    #endregion

    #region Helper Methods

    private string EnsureDrivePrefix(string path)
    {
        var prefix = $"{PSDriveInfo.Name}:";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path : prefix + path;
    }

    private string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        // ドライブプレフィックスを除去
        if (path.Contains(':'))
            path = path[(path.IndexOf(':') + 1)..];
        return path.Replace('\\', '/').Trim('/');
    }

    /// <summary>
    /// パスから Slack Web URL を生成。
    /// </summary>
    internal string? GetSlackUrl(string normalizedPath)
    {
        var baseUrl = Drive.WorkspaceUrl.TrimEnd('/');
        if (string.IsNullOrEmpty(normalizedPath))
            return baseUrl;

        var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var category = parts[0].ToLower();

        switch (category)
        {
            case "channels" when parts.Length == 1:
                return baseUrl;
            case "channels" when parts.Length >= 2:
                var channel = GetChannelByName(parts[1]);
                if (channel == null) return null;
                if (parts.Length == 2)
                    return $"{baseUrl}/archives/{channel.Id}";
                // Channels/<channel>/<displayName> → message permalink
                var ts = ResolveMessageIdentifier(channel.Id, parts[2]);
                if (ts == null) return null;
                var tsForUrl = ts.Replace(".", "");
                return $"{baseUrl}/archives/{channel.Id}/p{tsForUrl}";
            case "users" when parts.Length >= 2:
                var user = GetUserByName(parts[1]);
                if (user == null) return null;
                return $"{baseUrl}/team/{user.Id}";
            default:
                return baseUrl;
        }
    }

    internal static void OpenWithShell(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void WriteRootChildren(string path)
    {
        WriteFolder(path, "Channels", "Channel list");
        WriteFolder(path, "DirectMessages", "Direct messages");
        WriteFolder(path, "Users", "User list");
        WriteFolder(path, "Files", "File list");
    }

    private void WriteFolder(string parentPath, string name, string description)
    {
        var directory = EnsureDrivePrefix(parentPath);
        var pso = new PSObject();
        pso.TypeNames.Insert(0, "SlackDrive.FolderInfo");
        pso.Properties.Add(new PSNoteProperty("Name", name));
        pso.Properties.Add(new PSNoteProperty("Description", description));
        pso.Properties.Add(new PSNoteProperty("Path", EnsureDrivePrefix(MakePath(parentPath, name))));
        pso.Properties.Add(new PSNoteProperty("Directory", directory));
        WriteItemObject(pso, MakePath(parentPath, name), isContainer: true);
    }

    private void WriteChannels(string path)
    {
        if (Force)
            Drive.Cache.ClearChannels();

        var showAll = (DynamicParameters as SlackPaginationParameters)?.All.IsPresent == true;

        Dictionary<string, SlackChannel> channels;
        if (showAll && Drive.IsUserToken)
        {
            // -All: 全チャンネルを取得（遅い）
            channels = FetchChannels();
        }
        else
        {
            channels = EnsureChannelsLoaded();
        }

        var directory = EnsureDrivePrefix(path);
        foreach (var channel in channels.Values.OrderBy(c => c.Name))
        {
            channel.Path = EnsureDrivePrefix(MakePath(path, channel.Name));
            channel.Directory = directory;
            WriteItemObject(channel, MakePath(path, channel.Name), isContainer: true);
        }
    }

    private void WriteDirectMessages(string path)
    {
        var dms = FetchDirectMessages();

        var directory = EnsureDrivePrefix(path);
        foreach (var dm in dms.OrderByDescending(d => d.Created))
        {
            dm.Path = EnsureDrivePrefix(MakePath(path, dm.Name));
            dm.Directory = directory;
            WriteItemObject(dm, MakePath(path, dm.Name), isContainer: true);
        }
    }

    private List<SlackChannel> FetchDirectMessages()
    {
        var result = new List<SlackChannel>();
        var users = Drive.Cache.Users;
        string? cursor = null;

        while (true)
        {
            var queryParams = new Dictionary<string, string>
            {
                ["types"] = "im,mpim",
                ["exclude_archived"] = "true",
                ["limit"] = "200"
            };
            if (cursor != null) queryParams["cursor"] = cursor;

            var doc = Drive.Client.GetAsync("conversations.list", queryParams).GetAwaiter().GetResult();
            var root = doc.RootElement;

            if (!root.GetProperty("ok").GetBoolean())
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
                if (error == "ratelimited")
                {
                    System.Threading.Thread.Sleep(5000);
                    continue;
                }
                throw new InvalidOperationException($"Failed to get direct messages: {error}");
            }

            foreach (var ch in root.GetProperty("channels").EnumerateArray())
            {
                var id = ch.GetProperty("id").GetString() ?? "";
                var isMpim = ch.TryGetProperty("is_mpim", out var mpim) && mpim.GetBoolean();

                // DM の表示名: ユーザー名 or mpim 名
                string name;
                if (isMpim)
                {
                    name = ch.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
                }
                else
                {
                    var userId = ch.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "";
                    name = users?.Values.FirstOrDefault(x => x.Id == userId)?.Name
                        ?? ResolveUserNameById(userId) ?? userId;
                }

                result.Add(new SlackChannel
                {
                    Id = id,
                    Name = name,
                    IsPrivate = true,
                    IsMember = true,
                    Created = ch.TryGetProperty("created", out var cr)
                        ? DateTimeOffset.FromUnixTimeSeconds(cr.GetInt64()).LocalDateTime
                        : DateTime.MinValue
                });
            }

            cursor = root.TryGetProperty("response_metadata", out var meta) &&
                     meta.TryGetProperty("next_cursor", out var c) &&
                     !string.IsNullOrEmpty(c.GetString())
                ? c.GetString()
                : null;

            if (cursor == null) break;
        }

        return result;
    }

    private string? ResolveUserNameById(string userId)
    {
        try
        {
            var queryParams = new Dictionary<string, string> { ["user"] = userId };
            var doc = Drive.Client.GetAsync("users.info", queryParams).GetAwaiter().GetResult();
            var root = doc.RootElement;
            if (!root.GetProperty("ok").GetBoolean()) return null;
            var user = root.GetProperty("user");
            return user.TryGetProperty("profile", out var p) &&
                   p.TryGetProperty("display_name", out var dn) &&
                   !string.IsNullOrEmpty(dn.GetString())
                ? dn.GetString()
                : user.TryGetProperty("name", out var n) ? n.GetString() : null;
        }
        catch { return null; }
    }

    private void WriteUsers(string path)
    {
        if (Force)
        {
            Drive.Cache.ClearUsers();
            Drive.UsersFetchTask = null;
            try { System.IO.File.Delete(GetUsersCacheFilePath()); } catch { }
        }

        var users = EnsureUsersLoaded();

        if (users == null)
        {
            WriteWarning("Building users cache in background. This may take a few minutes. Run 'ls' again shortly.");
            return;
        }

        WarnIfUsersCacheStale();

        var directory = EnsureDrivePrefix(path);
        foreach (var user in users.Values.Where(u => !u.IsDeleted).OrderBy(u => u.Name))
        {
            user.Path = EnsureDrivePrefix(MakePath(path, user.Name));
            user.Directory = directory;
            WriteItemObject(user, MakePath(path, user.Name), isContainer: false);
        }
    }

    private void WriteChannelMessages(string channelName, string path, int limit = 20)
    {
        var channel = GetChannelByName(channelName);
        if (channel == null)
        {
            WriteError(new ErrorRecord(
                new ItemNotFoundException($"Channel not found: {channelName}"),
                "ChannelNotFound",
                ErrorCategory.ObjectNotFound,
                channelName));
            return;
        }

        var queryParams = new Dictionary<string, string>
        {
            ["channel"] = channel.Id,
            ["limit"] = limit.ToString()
        };

        var doc = Drive.Client.GetAsync("conversations.history", queryParams).GetAwaiter().GetResult();
        var root = doc.RootElement;

        if (!root.GetProperty("ok").GetBoolean())
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
            if (error == "not_in_channel")
            {
                WriteWarning($"#{channelName}: Not a member. Use 'ls ... | Where-Object IsMember' to filter accessible channels.");
                return;
            }
            throw new InvalidOperationException($"Failed to get messages: {error}");
        }

        var users = Drive.Cache.Users ?? EnsureUsersLoadedSync();

        var directory = EnsureDrivePrefix(path);
        var messages = new List<SlackMessage>();
        foreach (var m in root.GetProperty("messages").EnumerateArray())
        {
            var userId = m.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "";
            var ts = m.GetProperty("ts").GetString() ?? "";

            // user_profile からユーザー名を取得（users.list 不要）
            string? userName = null;
            if (m.TryGetProperty("user_profile", out var up))
            {
                userName = up.TryGetProperty("display_name", out var dn) && !string.IsNullOrEmpty(dn.GetString())
                    ? dn.GetString()
                    : up.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            }
            userName ??= users?.Values.FirstOrDefault(x => x.Id == userId)?.Name;

            var rawText = m.GetProperty("text").GetString() ?? "";
            messages.Add(new SlackMessage
            {
                Ts = ts,
                UserId = userId,
                UserName = userName,
                Text = users != null ? ResolveSlackMentions(rawText, users) : rawText,
                Timestamp = DateTimeOffset.FromUnixTimeSeconds((long)double.Parse(ts.Split('.')[0])).LocalDateTime,
                ThreadTs = m.TryGetProperty("thread_ts", out var tts) ? tts.GetString() : null,
                ReplyCount = m.TryGetProperty("reply_count", out var rc) ? rc.GetInt32() : 0,
                Directory = directory
            });
        }

        // 短縮 ts + 著者 + 冒頭テキストで表示名を生成
        var tsValues = messages.Select(m => m.Ts).ToList();
        foreach (var message in messages)
        {
            var displayName = BuildMessageDisplayName(message, tsValues);
            message.Path = EnsureDrivePrefix(MakePath(path, displayName));
            WriteItemObject(message, MakePath(path, displayName), isContainer: true);
        }
    }

    private void WriteThreadMessages(string channelName, string displayName, string path)
    {
        var channel = GetChannelByName(channelName);
        if (channel == null)
        {
            WriteError(new ErrorRecord(
                new ItemNotFoundException($"Channel not found: {channelName}"),
                "ChannelNotFound",
                ErrorCategory.ObjectNotFound,
                channelName));
            return;
        }

        // 表示名から元の ts を逆引き
        var ts = ResolveMessageIdentifier(channel.Id, displayName);
        if (ts == null)
        {
            WriteError(new ErrorRecord(
                new ItemNotFoundException($"Message not found: {displayName}"),
                "MessageNotFound",
                ErrorCategory.ObjectNotFound,
                displayName));
            return;
        }

        var messages = FetchThread(channel.Id, ts);
        // 先頭メッセージ（親）はスキップして返信のみ表示
        var replies = messages.Skip(1).ToList();
        if (replies.Count == 0)
        {
            WriteWarning("No replies in this thread.");
            return;
        }

        var directory = EnsureDrivePrefix(path);
        var tsValues = replies.Select(m => m.Ts).ToList();
        foreach (var message in replies)
        {
            var replyDisplayName = BuildMessageDisplayName(message, tsValues);
            message.Path = EnsureDrivePrefix(MakePath(path, replyDisplayName));
            message.Directory = directory;
            WriteItemObject(message, MakePath(path, replyDisplayName), isContainer: false);
        }
    }


    private void WriteFiles(string path, int limit = 100)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["count"] = limit.ToString()
        };

        var doc = Drive.Client.GetAsync("files.list", queryParams).GetAwaiter().GetResult();
        var root = doc.RootElement;

        if (!root.GetProperty("ok").GetBoolean())
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
            throw new InvalidOperationException($"Failed to get files: {error}");
        }

        var directory = EnsureDrivePrefix(path);
        foreach (var f in root.GetProperty("files").EnumerateArray())
        {
            var file = new SlackFile
            {
                Id = f.GetProperty("id").GetString() ?? "",
                Name = f.GetProperty("name").GetString() ?? "",
                Title = f.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                MimeType = f.TryGetProperty("mimetype", out var mt) ? mt.GetString() ?? "" : "",
                Size = f.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                UrlPrivate = f.TryGetProperty("url_private", out var u) ? u.GetString() : null,
                Created = f.TryGetProperty("created", out var cr)
                    ? DateTimeOffset.FromUnixTimeSeconds(cr.GetInt64()).LocalDateTime
                    : DateTime.MinValue,
                UserId = f.TryGetProperty("user", out var uid) ? uid.GetString() ?? "" : ""
            };

            file.Path = EnsureDrivePrefix(MakePath(path, file.Name));
            file.Directory = directory;
            WriteItemObject(file, MakePath(path, file.Name), isContainer: false);
        }
    }

    private Dictionary<string, SlackChannel> FetchChannels()
    {
        var result = new Dictionary<string, SlackChannel>();
        string? cursor = null;

        while (true)
        {
            var queryParams = new Dictionary<string, string>
            {
                ["types"] = "public_channel,private_channel",
                ["exclude_archived"] = "true",
                ["limit"] = "200"
            };
            if (cursor != null) queryParams["cursor"] = cursor;

            var doc = Drive.Client.GetAsync("conversations.list", queryParams).GetAwaiter().GetResult();
            var root = doc.RootElement;

            if (!root.GetProperty("ok").GetBoolean())
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
                if (error == "ratelimited")
                {
                    System.Threading.Thread.Sleep(5000);
                    continue;
                }
                throw new InvalidOperationException($"Failed to get channels: {error}");
            }

            foreach (var ch in root.GetProperty("channels").EnumerateArray())
            {
                var channel = new SlackChannel
                {
                    Id = ch.GetProperty("id").GetString() ?? "",
                    Name = ch.GetProperty("name").GetString() ?? "",
                    IsPrivate = ch.TryGetProperty("is_private", out var p) && p.GetBoolean(),
                    IsArchived = ch.TryGetProperty("is_archived", out var a) && a.GetBoolean(),
                    IsMember = ch.TryGetProperty("is_member", out var m) && m.GetBoolean(),
                    MemberCount = ch.TryGetProperty("num_members", out var n) ? n.GetInt32() : 0,
                    Topic = ch.TryGetProperty("topic", out var t) && t.TryGetProperty("value", out var tv)
                        ? tv.GetString() : null,
                    Purpose = ch.TryGetProperty("purpose", out var pu) && pu.TryGetProperty("value", out var pv)
                        ? pv.GetString() : null
                };
                result[channel.Name] = channel;
            }

            cursor = root.TryGetProperty("response_metadata", out var meta) &&
                     meta.TryGetProperty("next_cursor", out var c) &&
                     !string.IsNullOrEmpty(c.GetString())
                ? c.GetString()
                : null;

            if (cursor == null) break;
        }

        return result;
    }

    private Dictionary<string, SlackUser> FetchUsers()
    {
        var result = new Dictionary<string, SlackUser>();
        string? cursor = null;

        while (true)
        {
            var queryParams = new Dictionary<string, string> { ["limit"] = "200" };
            if (cursor != null) queryParams["cursor"] = cursor;

            var doc = Drive.Client.GetAsync("users.list", queryParams).GetAwaiter().GetResult();
            var root = doc.RootElement;

            if (!root.GetProperty("ok").GetBoolean())
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
                if (error == "ratelimited")
                {
                    System.Threading.Thread.Sleep(5000);
                    continue;
                }
                throw new InvalidOperationException($"Failed to get users: {error}");
            }

            foreach (var u in root.GetProperty("members").EnumerateArray())
            {
                var profile = u.TryGetProperty("profile", out var p) ? p : default;
                var user = new SlackUser
                {
                    Id = u.GetProperty("id").GetString() ?? "",
                    Name = u.GetProperty("name").GetString() ?? "",
                    RealName = profile.TryGetProperty("real_name", out var rn) ? rn.GetString() ?? "" : "",
                    DisplayName = profile.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? "" : "",
                    Email = profile.TryGetProperty("email", out var em) ? em.GetString() : null,
                    IsBot = u.TryGetProperty("is_bot", out var b) && b.GetBoolean(),
                    IsAdmin = u.TryGetProperty("is_admin", out var a) && a.GetBoolean(),
                    IsDeleted = u.TryGetProperty("deleted", out var d) && d.GetBoolean()
                };
                result[user.Name] = user;
            }

            cursor = root.TryGetProperty("response_metadata", out var meta) &&
                     meta.TryGetProperty("next_cursor", out var c) &&
                     !string.IsNullOrEmpty(c.GetString())
                ? c.GetString()
                : null;

            if (cursor == null) break;
        }

        return result;
    }

    #region Users Disk Cache

    private static readonly JsonSerializerOptions _usersCacheJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private string GetUsersCacheFilePath()
    {
        var folder = SlackDriveConfigManager.GetConfigFolderPath();
        return System.IO.Path.Combine(folder, $"users-cache-{Drive.TeamId}.jsonl");
    }

    private Dictionary<string, SlackUser>? LoadUsersDiskCache()
    {
        var path = GetUsersCacheFilePath();
        if (!System.IO.File.Exists(path)) return null;

        var result = new Dictionary<string, SlackUser>();
        try
        {
            foreach (var line in System.IO.File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var user = JsonSerializer.Deserialize<SlackUser>(line, _usersCacheJsonOptions);
                    if (user != null)
                        result[user.Name] = user;
                }
                catch { /* skip malformed line */ }
            }
        }
        catch { /* file read error */ }
        return result.Count > 0 ? result : null;
    }

    private void SaveUsersDiskCache(Dictionary<string, SlackUser> users)
    {
        try
        {
            var path = GetUsersCacheFilePath();
            var folder = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !System.IO.Directory.Exists(folder))
                System.IO.Directory.CreateDirectory(folder);
            using var writer = new System.IO.StreamWriter(path, false, System.Text.Encoding.UTF8);
            foreach (var user in users.Values)
                writer.WriteLine(JsonSerializer.Serialize(user, _usersCacheJsonOptions));
        }
        catch { /* best effort */ }
    }

    private void WarnIfUsersCacheStale()
    {
        var path = GetUsersCacheFilePath();
        if (!System.IO.File.Exists(path)) return;

        var age = DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(path);
        if (age > TimeSpan.FromDays(7))
        {
            var cachedDate = System.IO.File.GetLastWriteTimeUtc(path).ToLocalTime().ToString("yyyy-MM-dd");
            WriteWarning($"Users cache is {age.Days} days old ({cachedDate}). Run 'ls Slack:\\Users -Force' to refresh.");
        }
    }

    #endregion

    /// <summary>
    /// client.userBoot (Slack Web Client 内部 API) からチャンネル・ユーザーを取得。
    /// conversations.list が enterprise_is_restricted で失敗する場合のフォールバック。
    /// </summary>
    private (Dictionary<string, SlackChannel>? Channels, Dictionary<string, SlackUser>? Users) FetchUserBoot()
    {
        var doc = Drive.Client.GetAsync("client.userBoot").GetAwaiter().GetResult();
        var root = doc.RootElement;

        if (!root.GetProperty("ok").GetBoolean())
            return (null, null);

        // Channels
        Dictionary<string, SlackChannel>? channels = null;
        if (root.TryGetProperty("channels", out var chArray))
        {
            channels = new Dictionary<string, SlackChannel>();
            foreach (var ch in chArray.EnumerateArray())
            {
                // IM / MPIM はスキップ
                if (ch.TryGetProperty("is_im", out var im) && im.GetBoolean()) continue;
                if (ch.TryGetProperty("is_mpim", out var mpim) && mpim.GetBoolean()) continue;

                var channel = new SlackChannel
                {
                    Id = ch.GetProperty("id").GetString() ?? "",
                    Name = ch.GetProperty("name").GetString() ?? "",
                    IsPrivate = ch.TryGetProperty("is_private", out var p) && p.GetBoolean(),
                    IsArchived = ch.TryGetProperty("is_archived", out var a) && a.GetBoolean(),
                    IsMember = true, // userBoot は参加チャンネルのみ返す
                    MemberCount = ch.TryGetProperty("num_members", out var n) ? n.GetInt32() : 0,
                    Topic = ch.TryGetProperty("topic", out var t) && t.TryGetProperty("value", out var tv)
                        ? tv.GetString() : null,
                    Purpose = ch.TryGetProperty("purpose", out var pu) && pu.TryGetProperty("value", out var pv)
                        ? pv.GetString() : null
                };
                channels[channel.Name] = channel;
            }
        }

        // Users (self プロパティのみ — 全ユーザーリストは含まれない)
        // ユーザー一覧は別途 users.list or キャッシュから取得する
        return (channels, null);
    }

    private Dictionary<string, SlackChannel> EnsureChannelsLoaded()
    {
        var channels = Drive.Cache.Channels;
        if (channels != null) return channels;

        try
        {
            channels = Drive.IsUserToken ? FetchMyChannels() : FetchChannels();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("enterprise_is_restricted"))
        {
            // Enterprise Grid 制限: client.userBoot (内部API) にフォールバック
            var bootData = FetchUserBoot();
            if (bootData.Channels is { Count: > 0 })
            {
                channels = bootData.Channels;
                if (bootData.Users is { Count: > 0 })
                    Drive.Cache.Users = bootData.Users;
            }
            else
            {
                throw;
            }
        }

        Drive.Cache.Channels = channels;
        return channels;
    }

    /// <summary>
    /// users.conversations で自分が参加しているチャンネルのみ取得。
    /// ユーザートークン向け。高速。
    /// </summary>
    private Dictionary<string, SlackChannel> FetchMyChannels()
    {
        var result = new Dictionary<string, SlackChannel>();
        string? cursor = null;

        while (true)
        {
            var queryParams = new Dictionary<string, string>
            {
                ["types"] = "public_channel,private_channel",
                ["exclude_archived"] = "true",
                ["limit"] = "200"
            };
            if (cursor != null) queryParams["cursor"] = cursor;

            var doc = Drive.Client.GetAsync("users.conversations", queryParams).GetAwaiter().GetResult();
            var root = doc.RootElement;

            if (!root.GetProperty("ok").GetBoolean())
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
                if (error == "ratelimited")
                {
                    System.Threading.Thread.Sleep(5000);
                    continue;
                }
                throw new InvalidOperationException($"Failed to get channels: {error}");
            }

            foreach (var ch in root.GetProperty("channels").EnumerateArray())
            {
                var channel = new SlackChannel
                {
                    Id = ch.GetProperty("id").GetString() ?? "",
                    Name = ch.GetProperty("name").GetString() ?? "",
                    IsPrivate = ch.TryGetProperty("is_private", out var p) && p.GetBoolean(),
                    IsArchived = ch.TryGetProperty("is_archived", out var a) && a.GetBoolean(),
                    IsMember = true, // users.conversations は参加チャンネルのみ返す
                    MemberCount = ch.TryGetProperty("num_members", out var n) ? n.GetInt32() : 0,
                    Topic = ch.TryGetProperty("topic", out var t) && t.TryGetProperty("value", out var tv)
                        ? tv.GetString() : null,
                    Purpose = ch.TryGetProperty("purpose", out var pu) && pu.TryGetProperty("value", out var pv)
                        ? pv.GetString() : null
                };
                result[channel.Name] = channel;
            }

            cursor = root.TryGetProperty("response_metadata", out var meta) &&
                     meta.TryGetProperty("next_cursor", out var c) &&
                     !string.IsNullOrEmpty(c.GetString())
                ? c.GetString()
                : null;

            if (cursor == null) break;
        }

        return result;
    }

    private Dictionary<string, SlackUser>? EnsureUsersLoaded()
    {
        var users = Drive.Cache.Users;
        if (users != null) return users;

        // ディスクキャッシュから読み込み
        users = LoadUsersDiskCache();
        if (users != null)
        {
            Drive.Cache.Users = users;
            return users;
        }

        // バックグラウンドタスクが完了していればキャッシュを確認
        if (Drive.UsersFetchTask is { IsCompleted: true })
        {
            Drive.UsersFetchTask = null;
            users = Drive.Cache.Users;
            if (users != null) return users;
        }

        // バックグラウンドフェッチを開始
        if (Drive.UsersFetchTask == null)
        {
            Drive.UsersFetchTask = Task.Run(() =>
            {
                Dictionary<string, SlackUser> fetched;
                try
                {
                    fetched = FetchUsers();
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("enterprise_is_restricted"))
                {
                    fetched = Drive.Cache.Users!;
                    if (fetched == null) throw;
                }
                Drive.Cache.Users = fetched;
                SaveUsersDiskCache(fetched);
            });
        }

        return null;
    }

    /// <summary>同期的にユーザーをロード（タブ補完・メンション解決用）。</summary>
    private Dictionary<string, SlackUser> EnsureUsersLoadedSync()
    {
        var users = EnsureUsersLoaded();
        if (users != null) return users;

        // バックグラウンドタスクの完了を待つ
        Drive.UsersFetchTask?.Wait();
        Drive.UsersFetchTask = null;
        return Drive.Cache.Users ?? new Dictionary<string, SlackUser>();
    }

    private SlackChannel? GetChannelByName(string name)
    {
        return EnsureChannelsLoaded().TryGetValue(name, out var channel) ? channel : null;
    }

    private void FetchChannelDetails(SlackChannel channel)
    {
        try
        {
            var queryParams = new Dictionary<string, string> { ["channel"] = channel.Id };
            var doc = Drive.Client.GetAsync("conversations.info", queryParams).GetAwaiter().GetResult();
            var root = doc.RootElement;

            if (!root.GetProperty("ok").GetBoolean()) return;

            var ch = root.GetProperty("channel");
            channel.MemberCount = ch.TryGetProperty("num_members", out var n) ? n.GetInt32() : 0;
            channel.Topic = ch.TryGetProperty("topic", out var t) && t.TryGetProperty("value", out var tv)
                ? tv.GetString() : channel.Topic;
            channel.Purpose = ch.TryGetProperty("purpose", out var pu) && pu.TryGetProperty("value", out var pv)
                ? pv.GetString() : channel.Purpose;
        }
        catch { /* best effort */ }
    }

    private SlackUser? GetUserByName(string name)
    {
        return EnsureUsersLoadedSync().TryGetValue(name, out var user) ? user : null;
    }

    private string ResolveUserName(Dictionary<string, SlackUser> users, string userId)
    {
        return users.Values.FirstOrDefault(x => x.Id == userId)?.Name ?? userId;
    }

    /// <summary>
    /// メッセージ本文中の Slack メンション記法を表示名に変換。
    /// <![CDATA[<@U03JURUCFRT>]]> → @username, <![CDATA[<#C1234|channel>]]> → #channel
    /// </summary>
    private string ResolveSlackMentions(string text, Dictionary<string, SlackUser> users)
    {
        // <@USERID> or <@USERID|name> (U=user, W=workspace user)
        text = Regex.Replace(text, @"<@([UW][A-Z0-9]+)(?:\|([^>]+))?>", m =>
        {
            var id = m.Groups[1].Value;
            var fallback = m.Groups[2].Success ? m.Groups[2].Value : null;
            var user = users.Values.FirstOrDefault(x => x.Id == id);
            var name = (!string.IsNullOrEmpty(user?.DisplayName) ? user.DisplayName : user?.Name)
                    ?? fallback ?? id;
            return $"@{name}";
        });

        // <#CHANNELID|channel-name>
        text = Regex.Replace(text, @"<#C[A-Z0-9]+\|([^>]+)>", m => $"#{m.Groups[1].Value}");

        // <!subteam^ID|@handle> (user groups)
        text = Regex.Replace(text, @"<!subteam\^[A-Z0-9]+\|@([^>]+)>", m => $"@{m.Groups[1].Value}");

        // <!here>, <!channel>, <!everyone>
        text = Regex.Replace(text, @"<!(\w+)(?:\|[^>]*)?>", m => $"@{m.Groups[1].Value}");

        // <URL> or <URL|label>
        text = Regex.Replace(text, @"<(https?://[^|>]+)(?:\|([^>]+))?>", m =>
            m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value);

        return text;
    }

    private string? ExportChildItemsCsv(string normalizedPath, string exportCsv, Encoding? csvEncoding, int first)
    {
        var encoding = csvEncoding ?? new UTF8Encoding(true);
        var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var category = parts[0].ToLower();

        // パスを解決
        var csvPath = exportCsv;
        if (!Path.IsPathRooted(csvPath))
        {
            try
            {
                var resolved = SessionState.Path.GetUnresolvedProviderPathFromPSPath(csvPath);
                csvPath = resolved;
            }
            catch { /* use as-is */ }
        }

        if (Directory.Exists(csvPath))
            csvPath = Path.Combine(csvPath, $"slack-{category}.csv");

        using var writer = new StreamWriter(csvPath, false, encoding);

        switch (category)
        {
            case "channels":
                if (parts.Length == 1)
                {
                    writer.WriteLine("Name,Id,IsPrivate,IsArchived,IsMember,MemberCount,Topic,Purpose");
                    foreach (var ch in EnsureChannelsLoaded().Values.OrderBy(c => c.Name))
                        writer.WriteLine($"{Csv(ch.Name)},{Csv(ch.Id)},{ch.IsPrivate},{ch.IsArchived},{ch.IsMember},{ch.MemberCount},{Csv(ch.Topic)},{Csv(ch.Purpose)}");
                }
                break;
            case "users":
                if (parts.Length == 1)
                {
                    writer.WriteLine("Name,Id,DisplayName,RealName,IsBot,IsAdmin,IsDeleted,Email");
                    foreach (var u in EnsureUsersLoadedSync().Values.OrderBy(u => u.Name))
                        writer.WriteLine($"{Csv(u.Name)},{Csv(u.Id)},{Csv(u.DisplayName)},{Csv(u.RealName)},{u.IsBot},{u.IsAdmin},{u.IsDeleted},{Csv(u.Email)}");
                }
                break;
        }

        return csvPath;

        static string Csv(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }

    /// <summary>
    /// メッセージの表示名を生成: {MMdd}_{HHmm}_{shortTs}_{author}_{sentence}
    /// </summary>
    private static string BuildMessageDisplayName(SlackMessage msg, List<string> allTs)
    {
        var shortTs = ComputeShortTs(msg.Ts, allTs);
        var date = msg.Timestamp.ToString("MMdd");
        var time = msg.Timestamp.ToString("HHmm");
        var author = msg.UserName ?? msg.UserId;
        var sentence = SanitizeForPath(FirstSentence(msg.Text, 30));

        return $"{date}_{time}_{shortTs}_{author}_{sentence}";
    }

    /// <summary>
    /// git の短縮ハッシュ方式で、一意になる最短の ts サフィックスを返す (最低4桁)。
    /// </summary>
    private static string ComputeShortTs(string ts, List<string> allTs)
    {
        // ドットを除去した数字列で比較
        var normalized = ts.Replace(".", "");
        var others = allTs.Where(t => t != ts).Select(t => t.Replace(".", "")).ToList();

        for (int len = 4; len < normalized.Length; len++)
        {
            var suffix = normalized[^len..];
            if (others.All(o => !o.EndsWith(suffix)))
                return suffix;
        }
        return normalized;
    }

    /// <summary>
    /// テキストの冒頭一文を取得 (maxLen 文字で切る)。
    /// </summary>
    private static string FirstSentence(string text, int maxLen)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (line.Length > maxLen)
            line = line[..maxLen];
        return line;
    }

    /// <summary>
    /// パスに使えない文字を除去/置換。
    /// </summary>
    private static string SanitizeForPath(string text)
    {
        // Windows パス不正文字 + プロバイダーセパレータ
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':' };
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (invalid.Contains(c)) continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// メッセージ識別子（生 ts または表示名）から元の ts を解決する。
    /// 生 ts: "1765763994.024389" → そのまま返す
    /// 表示名: "0329_0820_4959_author_text" → 3番目のセグメント (shortTs) でサフィックスマッチ
    /// </summary>
    private string? ResolveMessageIdentifier(string channelId, string identifier)
    {
        // 生の ts 形式 (数字.数字) ならそのまま返す
        if (Regex.IsMatch(identifier, @"^\d+\.\d+$"))
            return identifier;

        // 表示名の3番目のセグメント (index 2) が shortTs
        var segments = identifier.Split('_');
        var shortTs = segments.Length >= 3 ? segments[2] : segments[0];

        var queryParams = new Dictionary<string, string>
        {
            ["channel"] = channelId,
            ["limit"] = "30"
        };

        var doc = Drive.Client.GetAsync("conversations.history", queryParams).GetAwaiter().GetResult();
        var root = doc.RootElement;

        if (!root.GetProperty("ok").GetBoolean()) return null;

        foreach (var m in root.GetProperty("messages").EnumerateArray())
        {
            var ts = m.GetProperty("ts").GetString() ?? "";
            if (ts.Replace(".", "").EndsWith(shortTs))
                return ts;
        }
        return null;
    }

    #endregion

    #region IContentCmdletProvider

    public IContentReader GetContentReader(string path)
    {
        var normalized = NormalizePath(path);
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cat = parts.Length > 0 ? parts[0].ToLower() : "";

        if ((parts.Length != 3 && parts.Length != 4) || cat is not "channels" and not "directmessages")
            throw new InvalidOperationException($"Cannot read content from path: {path}");

        var channelName = parts[1];
        var channel = GetChannelByName(channelName);
        if (channel == null)
            throw new ItemNotFoundException($"Channel not found: {channelName}");

        var ts = ResolveMessageIdentifier(channel.Id, parts[2]);
        if (ts == null)
            throw new ItemNotFoundException($"Message not found: {parts[2]}");

        if (parts.Length == 4)
        {
            // スレッド返信の本文を返す
            var messages = FetchThread(channel.Id, ts);
            var replyTs = ResolveThreadReplyIdentifier(messages, parts[3]);
            var reply = messages.FirstOrDefault(m => m.Ts == replyTs);
            if (reply == null)
                throw new ItemNotFoundException($"Reply not found: {parts[3]}");
            return new SlackContentReader(reply.Text);
        }

        var thread = FetchThread(channel.Id, ts);
        var markdown = BuildMarkdown(channelName, channel.Id, thread);
        return new SlackContentReader(markdown);
    }

    /// <summary>スレッド返信の表示名から ts を逆引きする。</summary>
    private string? ResolveThreadReplyIdentifier(List<SlackMessage> messages, string identifier)
    {
        if (Regex.IsMatch(identifier, @"^\d+\.\d+$"))
            return identifier;

        var segments = identifier.Split('_');
        var shortTs = segments.Length >= 3 ? segments[2] : segments[0];

        foreach (var m in messages)
        {
            if (m.Ts.Replace(".", "").EndsWith(shortTs))
                return m.Ts;
        }
        return null;
    }

    public IContentWriter GetContentWriter(string path)
    {
        throw new PSNotSupportedException("SlackDrive is read-only.");
    }

    public void ClearContent(string path)
    {
        throw new PSNotSupportedException("SlackDrive is read-only.");
    }

    public object GetContentReaderDynamicParameters(string path) => null!;
    public object GetContentWriterDynamicParameters(string path) => null!;
    public object ClearContentDynamicParameters(string path) => null!;

    private List<SlackMessage> FetchThread(string channelId, string ts)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["channel"] = channelId,
            ["ts"] = ts
        };

        var doc = Drive.Client.GetAsync("conversations.replies", queryParams).GetAwaiter().GetResult();
        var root = doc.RootElement;

        if (!root.GetProperty("ok").GetBoolean())
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
            throw new InvalidOperationException($"Failed to get thread: {error}");
        }

        var users = EnsureUsersLoadedSync();

        var messages = new List<SlackMessage>();
        foreach (var m in root.GetProperty("messages").EnumerateArray())
        {
            var userId = m.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "";
            var msgTs = m.GetProperty("ts").GetString() ?? "";

            var rawText = m.GetProperty("text").GetString() ?? "";
            messages.Add(new SlackMessage
            {
                Ts = msgTs,
                UserId = userId,
                UserName = ResolveUserName(users, userId),
                Text = ResolveSlackMentions(rawText, users),
                Timestamp = DateTimeOffset.FromUnixTimeSeconds((long)double.Parse(msgTs.Split('.')[0])).DateTime,
                ThreadTs = m.TryGetProperty("thread_ts", out var tts) ? tts.GetString() : null,
                ReplyCount = m.TryGetProperty("reply_count", out var rc) ? rc.GetInt32() : 0
            });
        }

        return messages;
    }

    private string BuildMarkdown(string channelName, string channelId, List<SlackMessage> messages)
    {
        if (messages.Count == 0)
            return "(empty)";

        var sb = new StringBuilder();
        var parent = messages[0];
        var baseUrl = Drive.WorkspaceUrl.TrimEnd('/');
        var tsForUrl = parent.Ts.Replace(".", "");
        var permalink = $"{baseUrl}/archives/{channelId}/p{tsForUrl}";
        var replyInfo = messages.Count > 1 ? $" | Replies: {messages.Count - 1}" : "";

        // メタデータヘッダー
        sb.AppendLine($"# #{channelName}");
        sb.AppendLine();
        sb.AppendLine($"{parent.Timestamp:yyyy-MM-dd HH:mm} | @{parent.UserName}{replyInfo} | {permalink}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // 親投稿
        sb.AppendLine($"**{parent.UserName}** ({parent.Timestamp:yyyy-MM-dd HH:mm}):");
        sb.Append(parent.Text);

        if (messages.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("---");

            for (int i = 1; i < messages.Count; i++)
            {
                var reply = messages[i];
                sb.AppendLine();
                sb.AppendLine($"**{reply.UserName}** ({reply.Timestamp:yyyy-MM-dd HH:mm}):");
                sb.Append(reply.Text);
            }
        }

        return sb.ToString();
    }

    #endregion
}

/// <summary>
/// Dynamic parameters for Get-ChildItem.
/// </summary>
public class SlackPaginationParameters
{
    [Parameter]
    public int? First { get; set; }

    [Parameter]
    public SwitchParameter All { get; set; }

    [Parameter]
    public string? ExportCsv { get; set; }

    [Parameter]
    [ArgumentCompleter(typeof(EncodingCompleter))]
    [EncodingArgumentTransformation]
    public Encoding? CsvEncoding { get; set; }
}

/// <summary>
/// Encoding 名のタブ補完。
/// </summary>
public class EncodingCompleter : IArgumentCompleter
{
    public IEnumerable<CompletionResult> CompleteArgument(
        string commandName, string parameterName, string wordToComplete,
        CommandAst commandAst, IDictionary fakeBoundParameters)
    {
        var pattern = string.IsNullOrEmpty(wordToComplete)
            ? new WildcardPattern("*")
            : new WildcardPattern(wordToComplete + "*", WildcardOptions.IgnoreCase);

        foreach (var ei in Encoding.GetEncodings().Where(e => pattern.IsMatch(e.Name)).OrderBy(e => e.Name))
        {
            yield return new CompletionResult(ei.Name, ei.Name, CompletionResultType.ParameterValue,
                $"CodePage:{ei.CodePage}  {ei.DisplayName}");
        }
    }
}

/// <summary>
/// Encoding 名文字列を System.Text.Encoding オブジェクトに変換。
/// </summary>
public class EncodingArgumentTransformationAttribute : ArgumentTransformationAttribute
{
    public override object Transform(EngineIntrinsics engineIntrinsics, object inputData)
    {
        if (inputData is string encodingName)
        {
            try { return Encoding.GetEncoding(encodingName); }
            catch (ArgumentException) { throw new ArgumentException($"Invalid encoding: {encodingName}"); }
        }
        return inputData;
    }
}

public class SlackDriveParameters
{
    [Parameter(Mandatory = true)]
    public string Token { get; set; } = "";

    [Parameter]
    public string? RefreshToken { get; set; }

    [Parameter]
    public string? ClientId { get; set; }

    [Parameter]
    public string? ClientSecret { get; set; }

    [Parameter]
    public string? Cookie { get; set; }
}
