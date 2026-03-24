using System.Management.Automation;

namespace SlackDrive;

public class SlackDriveInfo : PSDriveInfo, IDisposable
{
    private volatile SlackApiClient? _client;
    private readonly object _clientLock = new();
    private readonly string? _token;
    private readonly string? _cookie;
    private readonly ProxySettings? _proxy;
    private readonly LoggingSettings? _logging;
    private volatile Func<CancellationToken, Task<string>>? _authenticator;
    private SlackAuthTestResponse? _authInfo;

    public SlackApiClient Client
    {
        get
        {
            if (_client == null)
            {
                lock (_clientLock)
                {
                    if (_client == null)
                    {
                        var token = _token;
                        if (token == null && _authenticator != null)
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                            token = _authenticator(cts.Token).GetAwaiter().GetResult();
                            _authenticator = null;
                        }
                        if (string.IsNullOrEmpty(token))
                            throw new InvalidOperationException("No token available. Provide Token or ClientId/ClientSecret.");

                        _client = new SlackApiClient(token, _cookie, _proxy, _logging);
                        _authInfo = _client.TestAuthAsync().GetAwaiter().GetResult();
                    }
                }
            }
            return _client;
        }
    }

    public string TeamId => EnsureAuthInfo().TeamId;
    public string TeamName => EnsureAuthInfo().Team;
    public string WorkspaceUrl => EnsureAuthInfo().Url;
    public string BotUser => EnsureAuthInfo().User;
    public string BotUserId => EnsureAuthInfo().UserId;

    /// <summary>
    /// ユーザートークン (xoxp- / xoxc-) の場合 true。
    /// Client が未初期化の場合は token prefix で判定、
    /// authenticator 使用時は初回アクセスまで false を返す。
    /// </summary>
    public bool IsUserToken
    {
        get
        {
            if (_token != null)
                return _token.StartsWith("xoxp-") || _token.StartsWith("xoxc-");
            if (_client != null)
                return _client.Token.StartsWith("xoxp-") || _client.Token.StartsWith("xoxc-");
            // OAuth authenticator → ユーザートークンになる
            return _authenticator != null;
        }
    }

    /// <summary>Client が初期化済みかどうか。PKCE を発火せずに確認できる。</summary>
    public bool IsConnected => _client != null;

    internal SlackCache Cache { get; }

    /// <summary>直接トークン指定でマウント。</summary>
    public SlackDriveInfo(PSDriveInfo driveInfo, string token, string? cookie = null,
        ProxySettings? proxy = null, LoggingSettings? logging = null)
        : base(driveInfo)
    {
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _cookie = cookie;
        _proxy = proxy;
        _logging = logging;
        Cache = new SlackCache();
    }

    /// <summary>OAuth 遅延認証でマウント。認証は初回 API アクセス時に実行される。</summary>
    public SlackDriveInfo(PSDriveInfo driveInfo, Func<CancellationToken, Task<string>> authenticator,
        ProxySettings? proxy = null, LoggingSettings? logging = null)
        : base(driveInfo)
    {
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _proxy = proxy;
        _logging = logging;
        Cache = new SlackCache();
    }

    private SlackAuthTestResponse EnsureAuthInfo()
    {
        if (_authInfo != null) return _authInfo;
        // Client getter が _authInfo も初期化する
        _ = Client;
        return _authInfo!;
    }

    public void Dispose()
    {
        _client?.Dispose();
        GC.SuppressFinalize(this);
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

    public void ClearChannels() => _channels = null;
    public void ClearUsers() => _users = null;

    private bool IsCacheValid(DateTime cacheTime) =>
        DateTime.UtcNow - cacheTime < _cacheExpiry;
}
