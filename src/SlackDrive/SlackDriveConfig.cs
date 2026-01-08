using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlackDrive;

public class SlackDriveConfig
{
    public List<SlackDriveSettings>? PSDrives { get; set; }
    
    // グローバル設定（全ドライブに適用）
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public int? RateLimitDelayMs { get; set; }
    public int? CacheExpiryMinutes { get; set; }
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
    
    // オプション
    public bool? Enabled { get; set; }
    public int? RateLimitDelayMs { get; set; }
    public int? CacheExpiryMinutes { get; set; }
    
    // グローバル設定からカスケード
    internal void CascadeFromGlobalSettings(SlackDriveConfig? global)
    {
        if (global == null) return;
        
        ClientId ??= global.ClientId;
        ClientSecret ??= global.ClientSecret;
        RateLimitDelayMs ??= global.RateLimitDelayMs;
        CacheExpiryMinutes ??= global.CacheExpiryMinutes;
        Enabled ??= true;
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
            RateLimitDelayMs = 1000,
            CacheExpiryMinutes = 5,
            PSDrives = new List<SlackDriveSettings>
            {
                new SlackDriveSettings
                {
                    Name = "MySlack",
                    Token = "xoxb-your-bot-token-here",
                    Description = "My Slack workspace",
                    Enabled = false
                }
            }
        };
        
        SaveConfig(defaultConfig);
    }
}