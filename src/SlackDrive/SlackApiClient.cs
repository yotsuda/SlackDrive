using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SlackDrive;

public class SlackApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _token;
    private bool _disposed;

    public string Token => _token;

    public SlackApiClient(string token, string? cookie = null)
    {
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://slack.com/api/")
        };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrEmpty(cookie))
            _httpClient.DefaultRequestHeaders.Add("Cookie", cookie);
    }

    public async Task<JsonDocument> GetAsync(string endpoint, Dictionary<string, string>? queryParams = null)
    {
        var url = endpoint;
        if (queryParams != null && queryParams.Count > 0)
        {
            var query = string.Join("&", queryParams.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
            url = $"{endpoint}?{query}";
        }

        var response = await _httpClient.GetAsync(url);
        var content = await response.Content.ReadAsStringAsync();
        
        // 429 (Rate Limited) はボディに ok:false, error:ratelimited が含まれるのでパースして返す
        // 他のエラーは例外をスロー
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
        {
            response.EnsureSuccessStatusCode();
        }
        
        return JsonDocument.Parse(content);
    }

    public async Task<JsonDocument> PostAsync(string endpoint, object body)
    {
        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(endpoint, content);
        response.EnsureSuccessStatusCode();
        var responseContent = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(responseContent);
    }

    public async Task<SlackAuthTestResponse> TestAuthAsync()
    {
        var doc = await GetAsync("auth.test");
        var root = doc.RootElement;

        if (!root.GetProperty("ok").GetBoolean())
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
            throw new InvalidOperationException($"Slack API error: {error}");
        }

        return new SlackAuthTestResponse
        {
            Ok = true,
            Url = root.GetProperty("url").GetString() ?? "",
            Team = root.GetProperty("team").GetString() ?? "",
            User = root.GetProperty("user").GetString() ?? "",
            TeamId = root.GetProperty("team_id").GetString() ?? "",
            UserId = root.GetProperty("user_id").GetString() ?? ""
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

public class SlackAuthTestResponse
{
    public bool Ok { get; set; }
    public string Url { get; set; } = "";
    public string Team { get; set; } = "";
    public string User { get; set; } = "";
    public string TeamId { get; set; } = "";
    public string UserId { get; set; } = "";
}