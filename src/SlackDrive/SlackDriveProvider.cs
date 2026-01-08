using System.Management.Automation;
using System.Management.Automation.Provider;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace SlackDrive;

[CmdletProvider("Slack", ProviderCapabilities.None)]
public class SlackDriveProvider : NavigationCmdletProvider
{
    #region Drive Management

    protected override PSDriveInfo NewDrive(PSDriveInfo drive)
    {
        if (drive == null)
            throw new ArgumentNullException(nameof(drive));

        // InitializeDefaultDrives から来た場合は既に SlackDriveInfo
        if (drive is SlackDriveInfo slackDrive)
        {
            return slackDrive;
        }

        // New-PSDrive コマンドから来た場合
        var dynamicParams = DynamicParameters as SlackDriveParameters;
        if (dynamicParams == null || string.IsNullOrEmpty(dynamicParams.Token))
            throw new ArgumentException("Token parameter is required");

        var client = new SlackApiClient(dynamicParams.Token);

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
        {
            slackDrive.Client.Dispose();
        }
        return drive;
    }

    protected override Collection<PSDriveInfo>? InitializeDefaultDrives()
    {
        SlackDriveConfigManager.EnsureDefaultConfigExists();
        
        var config = SlackDriveConfigManager.LoadConfig();
        if (config?.PSDrives == null || config.PSDrives.Count == 0)
        {
            return base.InitializeDefaultDrives();
        }
        
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
                // 認証マネージャーを使用してトークン取得
                var authManager = new SlackAuthManager(driveSettings);
                string token = authManager.GetAccessToken();
                
                var client = new SlackApiClient(token);
                var authInfo = client.TestAuthAsync().GetAwaiter().GetResult();
                
                var driveInfo = new PSDriveInfo(
                    driveSettings.Name,
                    ProviderInfo,
                    "/",
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

    #region Item Operations

    protected override bool IsValidPath(string path) => true;

    protected override bool ItemExists(string path)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath)) return true;

        var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;

        return parts[0].ToLower() switch
        {
            "channels" or "users" or "files" => true,
            _ => false
        };
    }

    protected override void GetItem(string path)
    {
        var normalizedPath = NormalizePath(path);
        var drive = GetSlackDrive();

        if (string.IsNullOrEmpty(normalizedPath))
        {
            WriteItemObject(new { Root = "/", Workspace = drive.TeamName }, path, true);
            return;
        }

        var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

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
        var normalizedPath = NormalizePath(path);
        var drive = GetSlackDrive();

        if (string.IsNullOrEmpty(normalizedPath))
        {
            WriteItemObject(new { Name = "channels", Type = "Container" }, "channels", true);
            WriteItemObject(new { Name = "users", Type = "Container" }, "users", true);
            WriteItemObject(new { Name = "files", Type = "Container" }, "files", true);
            return;
        }

        var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        switch (parts[0].ToLower())
        {
            case "channels":
                if (parts.Length == 1)
                    GetChannels();
                else if (parts.Length == 2)
                    GetChannelMessages(parts[1]);
                break;
            case "users":
                if (parts.Length == 1)
                    GetUsers();
                break;
        }
    }

    protected override bool HasChildItems(string path)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath)) return true;

        var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 && parts[0].ToLower() is "channels" or "users" or "files";
    }

    protected override bool IsItemContainer(string path)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath)) return true;

        var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return true;
        if (parts.Length == 2 && parts[0].ToLower() == "channels") return true;
        return false;
    }

    protected override void GetChildNames(string path, ReturnContainers returnContainers)
    {
        var normalizedPath = NormalizePath(path);
        var drive = GetSlackDrive();

        if (string.IsNullOrEmpty(normalizedPath))
        {
            WriteItemObject("channels", "channels", true);
            WriteItemObject("users", "users", true);
            WriteItemObject("files", "files", true);
            return;
        }

        var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        switch (parts[0].ToLower())
        {
            case "channels":
                if (parts.Length == 1)
                {
                    var channels = drive.Cache.Channels ?? FetchChannels();
                    if (drive.Cache.Channels == null) drive.Cache.Channels = channels;
                    foreach (var channel in channels.Values.OrderBy(c => c.Name))
                    {
                        WriteItemObject(channel.Name, $"channels/{channel.Name}", true);
                    }
                }
                break;
            case "users":
                if (parts.Length == 1)
                {
                    var users = drive.Cache.Users ?? FetchUsers();
                    if (drive.Cache.Users == null) drive.Cache.Users = users;
                    foreach (var user in users.Values.OrderBy(u => u.Name))
                    {
                        WriteItemObject(user.Name, $"users/{user.Name}", false);
                    }
                }
                break;
        }
    }

    #endregion

    #region Content Operations (Get-Content)

    // Note: IContentCmdletProvider will be implemented later for Get-Content support

    #endregion

    #region Helper Methods

    private SlackDriveInfo GetSlackDrive()
    {
        return PSDriveInfo as SlackDriveInfo
            ?? throw new InvalidOperationException("Not connected to a Slack workspace");
    }

    private string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        return path.Trim('/', '\\').Replace('\\', '/');
    }

    private void GetChannels()
    {
        var drive = GetSlackDrive();
        var channels = drive.Cache.Channels;

        if (channels == null)
        {
            channels = FetchChannels();
            drive.Cache.Channels = channels;
        }

        foreach (var channel in channels.Values.OrderBy(c => c.Name))
        {
            WriteItemObject(channel, $"channels/{channel.Name}", true);
        }
    }

    private Dictionary<string, SlackChannel> FetchChannels()
    {
        var drive = GetSlackDrive();
        var result = new Dictionary<string, SlackChannel>();
        string? cursor = null;
        int pageCount = 0;

        do
        {
            // Rate limit対策: 2ページ目以降は1秒待機
            if (pageCount > 0)
            {
                System.Threading.Thread.Sleep(1000);
            }

            var queryParams = new Dictionary<string, string>
            {
                ["types"] = "public_channel,private_channel",
                ["limit"] = "200"
            };
            if (cursor != null) queryParams["cursor"] = cursor;

            var doc = drive.Client.GetAsync("conversations.list", queryParams).GetAwaiter().GetResult();
            var root = doc.RootElement;

            if (!root.GetProperty("ok").GetBoolean())
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
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
                    MemberCount = ch.TryGetProperty("num_members", out var n) ? n.GetInt32() : 0
                };
                result[channel.Name] = channel;
            }

            cursor = root.TryGetProperty("response_metadata", out var meta) &&
                     meta.TryGetProperty("next_cursor", out var c) &&
                     !string.IsNullOrEmpty(c.GetString())
                ? c.GetString()
                : null;

            pageCount++;
        } while (cursor != null);

        return result;
    }

    private SlackChannel? GetChannelByName(string name)
    {
        var drive = GetSlackDrive();
        var channels = drive.Cache.Channels ?? FetchChannels();
        drive.Cache.Channels = channels;
        return channels.TryGetValue(name, out var channel) ? channel : null;
    }

    private void GetUsers()
    {
        var drive = GetSlackDrive();
        var users = drive.Cache.Users;

        if (users == null)
        {
            users = FetchUsers();
            drive.Cache.Users = users;
        }

        foreach (var user in users.Values.Where(u => !u.IsDeleted).OrderBy(u => u.Name))
        {
            WriteItemObject(user, $"users/{user.Name}", false);
        }
    }

    private Dictionary<string, SlackUser> FetchUsers()
    {
        var drive = GetSlackDrive();
        var result = new Dictionary<string, SlackUser>();
        string? cursor = null;
        int pageCount = 0;

        do
        {
            // Rate limit対策: 2ページ目以降は1秒待機
            if (pageCount > 0)
            {
                System.Threading.Thread.Sleep(1000);
            }

            var queryParams = new Dictionary<string, string> { ["limit"] = "200" };
            if (cursor != null) queryParams["cursor"] = cursor;

            var doc = drive.Client.GetAsync("users.list", queryParams).GetAwaiter().GetResult();
            var root = doc.RootElement;

            if (!root.GetProperty("ok").GetBoolean())
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
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

            pageCount++;
        } while (cursor != null);

        return result;
    }

    private SlackUser? GetUserByName(string name)
    {
        var drive = GetSlackDrive();
        var users = drive.Cache.Users ?? FetchUsers();
        drive.Cache.Users = users;
        return users.TryGetValue(name, out var user) ? user : null;
    }

    private void GetChannelMessages(string channelName, int limit = 20)
    {
        var drive = GetSlackDrive();
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

        var doc = drive.Client.GetAsync("conversations.history", queryParams).GetAwaiter().GetResult();
        var root = doc.RootElement;

        if (!root.GetProperty("ok").GetBoolean())
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
            throw new InvalidOperationException($"Failed to get messages: {error}");
        }

        var users = drive.Cache.Users ?? FetchUsers();
        drive.Cache.Users = users;

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
                ReplyCount = m.TryGetProperty("reply_count", out var rc) ? rc.GetInt32() : 0
            };

            WriteItemObject(message, $"channels/{channelName}/{ts}", false);
        }
    }

    #endregion
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
}