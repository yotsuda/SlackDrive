using System.Management.Automation;

namespace SlackDrive;

public class SlackDriveInfo : PSDriveInfo
{
    public SlackApiClient Client { get; }
    public string TeamId { get; }
    public string TeamName { get; }
    public string WorkspaceUrl { get; }
    public string BotUser { get; }
    public string BotUserId { get; }

    // Cache
    internal SlackCache Cache { get; }

    public SlackDriveInfo(
        PSDriveInfo driveInfo,
        SlackApiClient client,
        SlackAuthTestResponse authInfo)
        : base(driveInfo)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        TeamId = authInfo.TeamId;
        TeamName = authInfo.Team;
        WorkspaceUrl = authInfo.Url;
        BotUser = authInfo.User;
        BotUserId = authInfo.UserId;
        Cache = new SlackCache();
    }
}

public class SlackCache
{
    private Dictionary<string, SlackChannel>? _channels;
    private Dictionary<string, SlackUser>? _users;
    private DateTime _channelsCacheTime;
    private DateTime _usersCacheTime;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    public Dictionary<string, SlackChannel>? Channels
    {
        get => IsCacheValid(_channelsCacheTime) ? _channels : null;
        set
        {
            _channels = value;
            _channelsCacheTime = DateTime.UtcNow;
        }
    }

    public Dictionary<string, SlackUser>? Users
    {
        get => IsCacheValid(_usersCacheTime) ? _users : null;
        set
        {
            _users = value;
            _usersCacheTime = DateTime.UtcNow;
        }
    }

    public void Clear()
    {
        _channels = null;
        _users = null;
    }

    private bool IsCacheValid(DateTime cacheTime) =>
        DateTime.UtcNow - cacheTime < _cacheExpiry;
}