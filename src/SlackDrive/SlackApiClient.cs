using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SlackDrive;

public class SlackApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _token;
    private readonly LoggingSettings? _logging;
    private bool _disposed;

    public string Token => _token;

    public SlackApiClient(string token, string? cookie = null,
        ProxySettings? proxy = null, LoggingSettings? logging = null)
    {
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _logging = logging;

        var handler = CreateHandler(proxy);
        _httpClient = handler != null
            ? new HttpClient(handler, disposeHandler: true)
            : new HttpClient();

        _httpClient.BaseAddress = new Uri("https://slack.com/api/");
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

        Log(LoggingLevel.Verbose, $"GET {url}");
        var sw = Stopwatch.StartNew();

        var response = await _httpClient.GetAsync(url);
        var content = await response.Content.ReadAsStringAsync();

        sw.Stop();
        Log(LoggingLevel.Verbose, $"  {(int)response.StatusCode} {response.StatusCode} ({sw.ElapsedMilliseconds}ms)");

        // 429 (Rate Limited) はボディに ok:false, error:ratelimited が含まれるのでパースして返す
        // 他のエラーは例外をスロー
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            Log(LoggingLevel.Error, $"  HTTP error: {(int)response.StatusCode} {response.StatusCode}");
            response.EnsureSuccessStatusCode();
        }

        return JsonDocument.Parse(content);
    }

    public async Task<JsonDocument> PostAsync(string endpoint, object body)
    {
        var json = JsonSerializer.Serialize(body);

        Log(LoggingLevel.Verbose, $"POST {endpoint}");
        var sw = Stopwatch.StartNew();

        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(endpoint, content);

        sw.Stop();
        Log(LoggingLevel.Verbose, $"  {(int)response.StatusCode} {response.StatusCode} ({sw.ElapsedMilliseconds}ms)");

        if (!response.IsSuccessStatusCode)
        {
            Log(LoggingLevel.Error, $"  HTTP error: {(int)response.StatusCode} {response.StatusCode}");
        }
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(responseContent);
    }

    public async Task<JsonDocument> PostFormAsync(string endpoint, Dictionary<string, string> fields)
    {
        Log(LoggingLevel.Verbose, $"POST {endpoint} (form-data)");
        var sw = Stopwatch.StartNew();

        var content = new MultipartFormDataContent();
        content.Add(new StringContent(_token), "token");
        foreach (var kvp in fields)
            content.Add(new StringContent(kvp.Value), kvp.Key);

        var response = await _httpClient.PostAsync(endpoint, content);
        sw.Stop();
        Log(LoggingLevel.Verbose, $"  {(int)response.StatusCode} {response.StatusCode} ({sw.ElapsedMilliseconds}ms)");

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

    private static HttpClientHandler? CreateHandler(ProxySettings? proxy)
    {
        if (proxy == null || proxy.Enabled != true) return null;

        var handler = new HttpClientHandler();

        if (!string.IsNullOrEmpty(proxy.Url))
        {
            handler.Proxy = new WebProxy(proxy.Url)
            {
                BypassProxyOnLocal = proxy.BypassProxyOnLocal ?? false
            };

            if (proxy.UseDefaultCredentials == true)
            {
                handler.Proxy.Credentials = CredentialCache.DefaultCredentials;
            }
            else if (proxy.Credentials != null &&
                     !string.IsNullOrEmpty(proxy.Credentials.Username))
            {
                handler.Proxy.Credentials = new NetworkCredential(
                    proxy.Credentials.Username, proxy.Credentials.Password);
            }

            handler.UseProxy = true;
        }
        else if (proxy.UseDefaultWebProxy == true)
        {
            handler.UseProxy = true;
            handler.UseDefaultCredentials = proxy.UseDefaultCredentials ?? false;
        }

        return handler;
    }

    private void Log(LoggingLevel level, string message)
    {
        if (_logging?.Enabled != true) return;
        if (_logging.InternalLogLevel == null) return;
        if (level > _logging.InternalLogLevel) return;

        Trace.WriteLine($"[SlackDrive] {level}: {message}");
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
