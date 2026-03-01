using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Provider;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

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

        // InitializeDefaultDrives から来た場合は既に SlackDriveInfo
        if (drive is SlackDriveInfo slackDrive)
            return slackDrive;

        // New-PSDrive コマンドから来た場合
        var dynamicParams = DynamicParameters as SlackDriveParameters;
        if (dynamicParams == null || string.IsNullOrEmpty(dynamicParams.Token))
            throw new ArgumentException("Token parameter is required");

        var client = new SlackApiClient(dynamicParams.Token, dynamicParams.Cookie);

        try
        {
            var authInfo = client.TestAuthAsync().GetAwaiter().GetResult();
            WriteVerbose($"Connected to workspace: {authInfo.Team} as {authInfo.User}");
            return new SlackDriveInfo(drive, client, authInfo);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new InvalidOperationException($"Failed to authenticate: {ex.Message}", ex);
        }
    }

    protected override object NewDriveDynamicParameters() => new SlackDriveParameters();

    protected override PSDriveInfo RemoveDrive(PSDriveInfo drive)
    {
        if (drive is SlackDriveInfo slackDrive)
            slackDrive.Client.Dispose();
        return drive;
    }

    protected override Collection<PSDriveInfo>? InitializeDefaultDrives()
    {
        SlackDriveConfigManager.EnsureDefaultConfigExists();

        var config = SlackDriveConfigManager.LoadConfig();
        if (config?.PSDrives == null || config.PSDrives.Count == 0)
            return base.InitializeDefaultDrives();

        var drives = new Collection<PSDriveInfo>();

        foreach (var driveSettings in config.PSDrives)
        {
            driveSettings.CascadeFromGlobalSettings(config);

            if (driveSettings.Enabled != true) continue;
            if (string.IsNullOrEmpty(driveSettings.Name)) continue;

            // Token も ClientId もない場合はスキップ
            if (string.IsNullOrEmpty(driveSettings.Token) && string.IsNullOrEmpty(driveSettings.ClientId))
            {
                WriteWarning($"\"{driveSettings.Name}\": Neither Token nor ClientId specified. Skipping.");
                continue;
            }

            try
            {
                var authManager = new SlackAuthManager(driveSettings);
                string token = authManager.GetAccessToken();

                var client = new SlackApiClient(token);
                var authInfo = client.TestAuthAsync().GetAwaiter().GetResult();

                var driveInfo = new PSDriveInfo(
                    driveSettings.Name,
                    ProviderInfo,
                    @"\",
                    driveSettings.Description ?? $"Slack workspace: {authInfo.Team}",
                    null);

                var slackDrive = new SlackDriveInfo(driveInfo, client, authInfo);
                drives.Add(slackDrive);

                WriteVerbose($"Mounted {driveSettings.Name}: -> {authInfo.Team} ({authInfo.Url})");
            }
            catch (Exception ex)
            {
                WriteWarning($"\"{driveSettings.Name}\": Failed to connect - {ex.Message}");
            }
        }

        return drives;
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
            return firstPart is "channels" or "users" or "files";

        if (parts.Length == 2)
        {
            return firstPart switch
            {
                "channels" => GetChannelByName(parts[1]) != null,
                "users" => GetUserByName(parts[1]) != null,
                _ => false
            };
        }

        // Channels/<channel>/<ts> — message item
        if (parts.Length == 3 && firstPart == "channels")
            return GetChannelByName(parts[1]) != null;

        return false;
    }

    protected override bool IsItemContainer(string path)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrEmpty(normalized)) return true;

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return true;
        if (parts.Length == 2 && parts[0].ToLower() == "channels") return true;
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
                if (channel != null) WriteItemObject(channel, path, true);
                break;
            case "users" when parts.Length == 2:
                var user = GetUserByName(parts[1]);
                if (user != null) WriteItemObject(user, path, false);
                break;
        }
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
        var pagination = DynamicParameters as SlackPaginationParameters;
        int first = pagination?.First ?? 20;

        try
        {
            switch (parts[0].ToLower())
            {
                case "channels":
                    if (parts.Length == 1)
                        WriteChannels(path);
                    else if (parts.Length == 2)
                        WriteChannelMessages(parts[1], path, first);
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
    }

    protected override object? GetChildItemsDynamicParameters(string path, bool recurse)
    {
        return new SlackPaginationParameters();
    }

    protected override void GetChildNames(string path, ReturnContainers returnContainers)
    {
        var normalized = NormalizePath(path);

        if (string.IsNullOrEmpty(normalized))
        {
            WriteItemObject("Channels", MakePath(path, "Channels"), true);
            WriteItemObject("Users", MakePath(path, "Users"), true);
            WriteItemObject("Files", MakePath(path, "Files"), true);
            return;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        switch (parts[0].ToLower())
        {
            case "channels" when parts.Length == 1:
                foreach (var ch in EnsureChannelsLoaded().Values.OrderBy(c => c.Name))
                    WriteItemObject(ch.Name, MakePath(path, ch.Name), true);
                break;
            case "users" when parts.Length == 1:
                foreach (var u in EnsureUsersLoaded().Values.Where(u => !u.IsDeleted).OrderBy(u => u.Name))
                    WriteItemObject(u.Name, MakePath(path, u.Name), false);
                break;
        }
    }

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
                // Channels/<channel>/<ts> → message permalink
                var tsForUrl = parts[2].Replace(".", "");
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

        var channels = EnsureChannelsLoaded();

        var directory = EnsureDrivePrefix(path);
        foreach (var channel in channels.Values.OrderBy(c => c.Name))
        {
            channel.Path = EnsureDrivePrefix(MakePath(path, channel.Name));
            channel.Directory = directory;
            WriteItemObject(channel, MakePath(path, channel.Name), isContainer: true);
        }
    }

    private void WriteUsers(string path)
    {
        if (Force)
            Drive.Cache.ClearUsers();

        var users = EnsureUsersLoaded();

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

        var users = EnsureUsersLoaded();

        var directory = EnsureDrivePrefix(path);
        foreach (var m in root.GetProperty("messages").EnumerateArray())
        {
            var userId = m.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "";
            var ts = m.GetProperty("ts").GetString() ?? "";

            var message = new SlackMessage
            {
                Ts = ts,
                UserId = userId,
                UserName = users.Values.FirstOrDefault(x => x.Id == userId)?.Name,
                Text = m.GetProperty("text").GetString() ?? "",
                Timestamp = DateTimeOffset.FromUnixTimeSeconds((long)double.Parse(ts.Split('.')[0])).DateTime,
                ThreadTs = m.TryGetProperty("thread_ts", out var tts) ? tts.GetString() : null,
                ReplyCount = m.TryGetProperty("reply_count", out var rc) ? rc.GetInt32() : 0,
                Path = EnsureDrivePrefix(MakePath(path, ts)),
                Directory = directory
            };

            WriteItemObject(message, MakePath(path, ts), isContainer: false);
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
                    ? DateTimeOffset.FromUnixTimeSeconds(cr.GetInt64()).DateTime
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

        // ファイルキャッシュから読み込み (自 TeamId → 他キャッシュファイルの順)
        channels = LoadChannelsFromFileCache(Drive.TeamId);
        if (channels != null)
        {
            Drive.Cache.Channels = channels;
            return channels;
        }

        try
        {
            channels = FetchChannels();
            Drive.Cache.Channels = channels;
            SlackCacheManager.SaveChannels(Drive.TeamId, channels.Values);
            return channels;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("enterprise_is_restricted"))
        {
            // Enterprise Grid 制限: client.userBoot (内部API) にフォールバック
            var bootData = FetchUserBoot();
            if (bootData.Channels is { Count: > 0 })
            {
                channels = bootData.Channels;
                Drive.Cache.Channels = channels;
                SlackCacheManager.SaveChannels(Drive.TeamId, channels.Values);
                // Users も同時に取得できていればキャッシュ
                if (bootData.Users is { Count: > 0 })
                {
                    Drive.Cache.Users = bootData.Users;
                    SlackCacheManager.SaveUsers(Drive.TeamId, bootData.Users.Values);
                }
                return channels;
            }

            // userBoot も失敗した場合: 他キャッシュにフォールバック
            channels = LoadChannelsFromAnyCache();
            if (channels != null)
            {
                Drive.Cache.Channels = channels;
                return channels;
            }
            throw;
        }
    }

    private Dictionary<string, SlackUser> EnsureUsersLoaded()
    {
        var users = Drive.Cache.Users;
        if (users != null) return users;

        var fileCache = SlackCacheManager.LoadCache(Drive.TeamId);
        if (fileCache?.Users != null)
        {
            users = fileCache.Users.ToDictionary(u => u.Name);
            Drive.Cache.Users = users;
            return users;
        }

        try
        {
            users = FetchUsers();
            Drive.Cache.Users = users;
            SlackCacheManager.SaveUsers(Drive.TeamId, users.Values);
            return users;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("enterprise_is_restricted"))
        {
            // client.userBoot でチャンネルを取得した際に Users もキャッシュ済みかもしれない
            users = Drive.Cache.Users;
            if (users != null) return users;

            users = LoadUsersFromAnyCache();
            if (users != null)
            {
                Drive.Cache.Users = users;
                return users;
            }
            throw;
        }
    }

    /// <summary>指定 TeamId のキャッシュファイルからチャンネルを読み込む</summary>
    private static Dictionary<string, SlackChannel>? LoadChannelsFromFileCache(string teamId)
    {
        var fileCache = SlackCacheManager.LoadCache(teamId);
        if (fileCache?.Channels == null) return null;
        return fileCache.Channels.ToDictionary(c => c.Name);
    }

    /// <summary>キャッシュディレクトリ内の全ファイルからチャンネルを探す (Enterprise Grid フォールバック)</summary>
    private static Dictionary<string, SlackChannel>? LoadChannelsFromAnyCache()
    {
        var cacheDir = Path.Combine(SlackDriveConfigManager.GetConfigFolderPath(), "cache");
        if (!Directory.Exists(cacheDir)) return null;

        foreach (var file in Directory.GetFiles(cacheDir, "*.json"))
        {
            var teamId = Path.GetFileNameWithoutExtension(file);
            var channels = LoadChannelsFromFileCache(teamId);
            if (channels is { Count: > 0 }) return channels;
        }
        return null;
    }

    private static Dictionary<string, SlackUser>? LoadUsersFromAnyCache()
    {
        var cacheDir = Path.Combine(SlackDriveConfigManager.GetConfigFolderPath(), "cache");
        if (!Directory.Exists(cacheDir)) return null;

        foreach (var file in Directory.GetFiles(cacheDir, "*.json"))
        {
            var teamId = Path.GetFileNameWithoutExtension(file);
            var fileCache = SlackCacheManager.LoadCache(teamId);
            if (fileCache?.Users is { Count: > 0 })
                return fileCache.Users.ToDictionary(u => u.Name);
        }
        return null;
    }

    private SlackChannel? GetChannelByName(string name)
    {
        return EnsureChannelsLoaded().TryGetValue(name, out var channel) ? channel : null;
    }

    private SlackUser? GetUserByName(string name)
    {
        return EnsureUsersLoaded().TryGetValue(name, out var user) ? user : null;
    }

    private string ResolveUserName(Dictionary<string, SlackUser> users, string userId)
    {
        return users.Values.FirstOrDefault(x => x.Id == userId)?.Name ?? userId;
    }

    #endregion

    #region IContentCmdletProvider

    public IContentReader GetContentReader(string path)
    {
        var normalized = NormalizePath(path);
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 3 || parts[0].ToLower() != "channels")
            throw new InvalidOperationException($"Cannot read content from path: {path}");

        var channelName = parts[1];
        var ts = parts[2];

        var channel = GetChannelByName(channelName);
        if (channel == null)
            throw new ItemNotFoundException($"Channel not found: {channelName}");

        var messages = FetchThread(channel.Id, ts);
        var markdown = BuildMarkdown(channelName, channel.Id, messages);
        return new SlackContentReader(markdown);
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

        var users = EnsureUsersLoaded();

        var messages = new List<SlackMessage>();
        foreach (var m in root.GetProperty("messages").EnumerateArray())
        {
            var userId = m.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "";
            var msgTs = m.GetProperty("ts").GetString() ?? "";

            messages.Add(new SlackMessage
            {
                Ts = msgTs,
                UserId = userId,
                UserName = ResolveUserName(users, userId),
                Text = m.GetProperty("text").GetString() ?? "",
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
        sb.AppendLine($"> {parent.Timestamp:yyyy-MM-dd HH:mm} | @{parent.UserName}{replyInfo} | {permalink}");
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
                sb.AppendLine($"> **{reply.UserName}** ({reply.Timestamp:yyyy-MM-dd HH:mm}):");
                foreach (var line in reply.Text.Split('\n'))
                {
                    sb.AppendLine($"> {line}");
                }
            }
        }

        return sb.ToString();
    }

    #endregion
}

/// <summary>
/// Dynamic parameters for Get-ChildItem (-First).
/// </summary>
public class SlackPaginationParameters
{
    [Parameter]
    public int? First { get; set; }
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
