using System.Text.Json;

namespace SlackDrive;

public class SlackCacheFile
{
    public DateTime UpdatedAt { get; set; }
    public List<SlackChannel>? Channels { get; set; }
    public List<SlackUser>? Users { get; set; }
}

public static class SlackCacheManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string GetCacheFilePath(string workspaceId)
    {
        var cacheDir = Path.Combine(SlackDriveConfigManager.GetConfigFolderPath(), "cache");
        Directory.CreateDirectory(cacheDir);
        return Path.Combine(cacheDir, $"{workspaceId}.json");
    }

    public static SlackCacheFile? LoadCache(string workspaceId)
    {
        var path = GetCacheFilePath(workspaceId);
        if (!File.Exists(path)) return null;
        
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SlackCacheFile>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveCache(string workspaceId, SlackCacheFile cache)
    {
        var path = GetCacheFilePath(workspaceId);
        cache.UpdatedAt = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(cache, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static void SaveChannels(string workspaceId, IEnumerable<SlackChannel> channels)
    {
        var cache = LoadCache(workspaceId) ?? new SlackCacheFile();
        cache.Channels = channels.ToList();
        SaveCache(workspaceId, cache);
    }

    public static void SaveUsers(string workspaceId, IEnumerable<SlackUser> users)
    {
        var cache = LoadCache(workspaceId) ?? new SlackCacheFile();
        cache.Users = users.ToList();
        SaveCache(workspaceId, cache);
    }
}