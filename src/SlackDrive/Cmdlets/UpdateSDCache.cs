using System.Management.Automation;

namespace SlackDrive;

[Cmdlet(VerbsData.Update, "SlackCache")]
public class UpdateSlackCacheCommand : PSCmdlet
{
    [Parameter(Position = 0, ValueFromPipeline = true)]
    public string? DriveName { get; set; }

    [Parameter]
    public SwitchParameter Channels { get; set; }

    [Parameter]
    public SwitchParameter Users { get; set; }

    protected override void ProcessRecord()
    {
        // ドライブを取得
        var drives = SessionState.Drive.GetAll()
            .Where(d => d.Provider.Name == "Slack")
            .Cast<SlackDriveInfo>();

        if (!string.IsNullOrEmpty(DriveName))
        {
            drives = drives.Where(d => d.Name.Equals(DriveName, StringComparison.OrdinalIgnoreCase));
        }

        var driveList = drives.ToList();
        if (driveList.Count == 0)
        {
            WriteWarning("No Slack drives found. Import SlackDrive module first.");
            return;
        }

        // 両方指定なしの場合は両方更新
        bool updateChannels = Channels || (!Channels && !Users);
        bool updateUsers = Users || (!Channels && !Users);

        foreach (var drive in driveList)
        {
            Host.UI.WriteLine($"Updating cache for {drive.Name}...");

            if (updateChannels)
            {
                Host.UI.WriteLine("  Fetching channels...");
                var channels = FetchChannelsForDrive(drive);
                drive.Cache.Channels = channels;
                SlackCacheManager.SaveChannels(drive.TeamId, channels.Values);
                Host.UI.WriteLine($"  Cached {channels.Count} channels");
            }

            if (updateUsers)
            {
                Host.UI.WriteLine("  Fetching users...");
                var users = FetchUsersForDrive(drive);
                drive.Cache.Users = users;
                SlackCacheManager.SaveUsers(drive.TeamId, users.Values);
                Host.UI.WriteLine($"  Cached {users.Count} users");
            }
        }

        Host.UI.WriteLine("Cache update complete.");
    }

    private Dictionary<string, SlackChannel> FetchChannelsForDrive(SlackDriveInfo drive)
    {
        var result = new Dictionary<string, SlackChannel>();
        string? cursor = null;
        int pageCount = 0;

        do
        {
            if (pageCount > 0) System.Threading.Thread.Sleep(3000);

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
                    IsPrivate = ch.TryGetProperty("is_private", out var priv) && priv.GetBoolean(),
                    IsArchived = ch.TryGetProperty("is_archived", out var arch) && arch.GetBoolean(),
                    IsMember = ch.TryGetProperty("is_member", out var mem) && mem.GetBoolean(),
                    MemberCount = ch.TryGetProperty("num_members", out var num) ? num.GetInt32() : 0,
                    Topic = ch.TryGetProperty("topic", out var topic) ? topic.GetProperty("value").GetString() : null,
                    Purpose = ch.TryGetProperty("purpose", out var purpose) ? purpose.GetProperty("value").GetString() : null
                };
                result[channel.Name] = channel;
            }

            cursor = root.TryGetProperty("response_metadata", out var meta) &&
                     meta.TryGetProperty("next_cursor", out var c)
                ? c.GetString()
                : null;
            if (string.IsNullOrEmpty(cursor)) cursor = null;
            pageCount++;

        } while (cursor != null);

        return result;
    }

    private Dictionary<string, SlackUser> FetchUsersForDrive(SlackDriveInfo drive)
    {
        var result = new Dictionary<string, SlackUser>();
        string? cursor = null;
        int pageCount = 0;

        do
        {
            if (pageCount > 0) System.Threading.Thread.Sleep(3000);

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
                    IsBot = u.TryGetProperty("is_bot", out var bot) && bot.GetBoolean(),
                    IsAdmin = u.TryGetProperty("is_admin", out var admin) && admin.GetBoolean(),
                    IsDeleted = u.TryGetProperty("deleted", out var del) && del.GetBoolean(),
                    TimeZone = u.TryGetProperty("tz", out var tz) ? tz.GetString() : null
                };
                result[user.Name] = user;
            }

            cursor = root.TryGetProperty("response_metadata", out var meta) &&
                     meta.TryGetProperty("next_cursor", out var c)
                ? c.GetString()
                : null;
            if (string.IsNullOrEmpty(cursor)) cursor = null;
            pageCount++;

        } while (cursor != null);

        return result;
    }
}