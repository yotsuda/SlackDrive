using System.Text.Json;

namespace SlackDrive;

/// <summary>
/// 設定ファイルのルート。SlackDriveSettings を継承し、グローバル設定として機能する。
/// 各 PSDrive で未指定のプロパティはグローバル設定からカスケードされる。
/// </summary>
public class SlackDriveConfig : SlackDriveSettings
{
    public List<SlackDriveSettings>? PSDrives { get; set; }
}

public enum LoggingLevel
{
    Error,
    Info,
    Verbose
}

public class LoggingSettings
{
    public bool? Enabled { get; set; }

    private LoggingLevel? _level;
    public string? Level
    {
        get => _level?.ToString();
        set => _level = value?.Trim().ToLower() switch
        {
            null => null,
            "error" => LoggingLevel.Error,
            "info" or "information" => LoggingLevel.Info,
            "verbose" => LoggingLevel.Verbose,
            _ => throw new ArgumentException("Invalid logging level. Valid values are: Error, Info, Verbose.")
        };
    }
    internal LoggingLevel? InternalLogLevel => _level;
}

public class ProxySettings
{
    public string? Url { get; set; }
    public bool? UseDefaultWebProxy { get; set; }
    public bool? BypassProxyOnLocal { get; set; }
    public bool? UseDefaultCredentials { get; set; }
    public ProxyCredentials? Credentials { get; set; }
    public bool? Enabled { get; set; }
}

public class ProxyCredentials
{
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public class SlackDriveSettings
{
    public string? Name { get; set; }
    public string? Description { get; set; }

    // 認証
    public string? Token { get; set; }
    public string? RefreshToken { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? RedirectUrl { get; set; }
    public string? Scopes { get; set; }

    // オプション
    public bool? Enabled { get; set; }
    public int? RateLimitDelayMs { get; set; }
    public int? CacheExpiryMinutes { get; set; }

    // HTTP
    public ProxySettings? Proxy { get; set; }
    public LoggingSettings? Logging { get; set; }

    /// <summary>
    /// 未指定のプロパティをグローバル設定から補完する。
    /// </summary>
    internal void CascadeFromGlobalSettings(SlackDriveConfig? global)
    {
        if (global == null) return;

        ClientId ??= global.ClientId;
        ClientSecret ??= global.ClientSecret;
        RedirectUrl ??= global.RedirectUrl;
        Scopes ??= global.Scopes;
        RateLimitDelayMs ??= global.RateLimitDelayMs;
        CacheExpiryMinutes ??= global.CacheExpiryMinutes;
        Enabled ??= true;

        if (Proxy == null)
        {
            Proxy = global.Proxy;
        }
        else if (global.Proxy != null)
        {
            Proxy.Url ??= global.Proxy.Url;
            Proxy.UseDefaultWebProxy ??= global.Proxy.UseDefaultWebProxy;
            Proxy.BypassProxyOnLocal ??= global.Proxy.BypassProxyOnLocal;
            Proxy.UseDefaultCredentials ??= global.Proxy.UseDefaultCredentials;
            Proxy.Enabled ??= global.Proxy.Enabled;
            if (Proxy.Credentials == null)
            {
                Proxy.Credentials = global.Proxy.Credentials;
            }
            else if (global.Proxy.Credentials != null)
            {
                Proxy.Credentials.Username ??= global.Proxy.Credentials.Username;
                Proxy.Credentials.Password ??= global.Proxy.Credentials.Password;
            }
        }

        if (Logging == null)
        {
            Logging = global.Logging;
        }
        else if (global.Logging != null)
        {
            Logging.Enabled ??= global.Logging.Enabled;
            Logging.Level ??= global.Logging.Level;
        }
    }
}

public static class SlackDriveConfigManager
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string GetConfigFolderPath()
    {
        string moduleName = "SlackDrive";
        if (OperatingSystem.IsWindows())
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(documents, "PowerShell", "Modules", moduleName);
        }
        else
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".local", "share", "powershell", "Modules", moduleName);
        }
    }

    public static string GetConfigFilePath()
    {
        return Path.Combine(GetConfigFolderPath(), "SlackDriveConfig.json");
    }

    public static SlackDriveConfig? LoadConfig()
    {
        string path = GetConfigFilePath();
        if (!File.Exists(path)) return null;

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SlackDriveConfig>(json, _jsonOptions);
    }

    public static void SaveConfig(SlackDriveConfig config)
    {
        string path = GetConfigFilePath();
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string json = JsonSerializer.Serialize(config, _jsonOptions);
        File.WriteAllText(path, json);
    }

    public static void EnsureDefaultConfigExists()
    {
        string path = GetConfigFilePath();
        if (File.Exists(path)) return;

        var defaultConfig = new SlackDriveConfig
        {
            ClientId = "YOUR_CLIENT_ID",
            ClientSecret = "YOUR_CLIENT_SECRET",
            RateLimitDelayMs = 1000,
            CacheExpiryMinutes = 5,
            PSDrives = new List<SlackDriveSettings>
            {
                new SlackDriveSettings
                {
                    Name = "MySlack",
                    Description = "My Slack workspace",
                    Enabled = false
                }
            }
        };

        SaveConfig(defaultConfig);
    }
}
