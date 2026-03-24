using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SlackDrive;

public class SlackAuthManager
{
    private readonly SlackDriveSettings _settings;
    private string? _accessToken;
    private string? _refreshToken;
    
    public string? AccessToken => _accessToken;
    public string? RefreshToken => _refreshToken;
    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);
    
    // 認証方式
    public bool IsDirectToken => !string.IsNullOrEmpty(_settings.Token);
    public bool IsConfidentialApp => !string.IsNullOrEmpty(_settings.ClientSecret);
    public bool IsPublicApp => !string.IsNullOrEmpty(_settings.ClientId) && 
                                string.IsNullOrEmpty(_settings.ClientSecret) &&
                                string.IsNullOrEmpty(_settings.Token);
    
    public SlackAuthManager(SlackDriveSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _accessToken = settings.Token;
        _refreshToken = settings.RefreshToken;
    }
    
    public string GetAccessToken()
    {
        // 1. 直接トークン指定
        if (IsDirectToken)
        {
            _accessToken = _settings.Token;
            return _accessToken!;
        }
        
        // 2. 既存のリフレッシュトークンがあれば使用
        if (!string.IsNullOrEmpty(_refreshToken))
        {
            try
            {
                return RefreshAccessToken();
            }
            catch
            {
                // リフレッシュ失敗時は再認証
            }
        }
        
        // 3. OAuth フローで認証 (Slack は PKCE + ClientSecret を併用)
        if (!string.IsNullOrEmpty(_settings.ClientId))
        {
            return AuthorizeWithOAuth();
        }
        
        throw new InvalidOperationException("No valid authentication method configured. Provide Token, or ClientId for OAuth flow.");
    }
    
    private string AuthorizeWithOAuth()
    {
        if (string.IsNullOrEmpty(_settings.ClientId))
            throw new InvalidOperationException("ClientId is required for OAuth flow.");
        
        // デフォルトのリダイレクト URL
        string redirectUrl = _settings.RedirectUrl ?? "http://localhost:8765/slack/callback";
        
        // PKCE 用の code_verifier 生成
        string codeVerifier = GenerateCodeVerifier();
        string codeChallenge = GenerateCodeChallenge(codeVerifier);
        
        // スコープ (カスタマイズ可能)
        string scopes = _settings.Scopes
            ?? "channels:read channels:history groups:read groups:history im:read im:history mpim:read mpim:history users:read search:read";
        
        // 認証 URL 構築
        string state = Guid.NewGuid().ToString("N");
        string authUrl = $"https://slack.com/oauth/v2/authorize" +
            $"?client_id={_settings.ClientId}" +
            $"&user_scope={Uri.EscapeDataString(scopes)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUrl)}" +
            $"&state={state}" +
            $"&code_challenge={codeChallenge}" +
            $"&code_challenge_method=S256";
        
        // HttpListener でコールバック待機
        string authorizationCode = WaitForAuthorizationCode(redirectUrl, state, authUrl);
        
        // トークン交換
        return ExchangeCodeForToken(authorizationCode, codeVerifier, redirectUrl);
    }
    
    private string WaitForAuthorizationCode(string redirectUrl, string expectedState, string authUrl)
    {
        var uri = new Uri(redirectUrl);
        
        // ngrok や外部 URL の場合は localhost:8765 で待機
        string listenerPrefix;
        if (uri.Host.Contains("ngrok") || uri.Host.Contains("localtunnel") || uri.Scheme == "https")
        {
            listenerPrefix = $"http://localhost:8765{uri.AbsolutePath}";
        }
        else
        {
            listenerPrefix = $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}";
        }
        if (!listenerPrefix.EndsWith("/")) listenerPrefix += "/";
        
        using var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add(listenerPrefix);
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            int port = uri.Host.Contains("ngrok") ? 8765 : uri.Port;
            string message = port <= 1024
                ? $"Failed to start HttpListener on port {port}. Administrative privileges may be required."
                : $"Failed to start HttpListener on port {port}. The port may be in use.";
            throw new InvalidOperationException(message, ex);
        }
        
        // ブラウザで認証 URL を開く
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        
        string? authorizationCode = null;
        
        try
        {
            var context = listener.GetContext();
            var query = context.Request.QueryString;
            
            string? code = query["code"];
            string? state = query["state"];
            string? error = query["error"];
            
            // エラーチェック
            if (!string.IsNullOrEmpty(error))
            {
                SendResponse(context, $"<h1>Authorization Failed</h1><p>Error: {error}</p>");
                throw new InvalidOperationException($"OAuth error: {error}");
            }
            
            // State 検証
            if (state != expectedState)
            {
                SendResponse(context, "<h1>Authorization Failed</h1><p>Invalid state parameter.</p>");
                throw new InvalidOperationException("Invalid state parameter - possible CSRF attack.");
            }
            
            if (string.IsNullOrEmpty(code))
            {
                SendResponse(context, "<h1>Authorization Failed</h1><p>No authorization code received.</p>");
                throw new InvalidOperationException("No authorization code received.");
            }
            
            authorizationCode = code;
            SendResponse(context, "<h1>Authorization Successful!</h1><p>You can close this window and return to PowerShell.</p>");
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
        
        return authorizationCode!;
    }
    
    private static void SendResponse(HttpListenerContext context, string html)
    {
        string fullHtml = $"<!DOCTYPE html><html><head><meta charset='utf-8'><title>SlackDrive</title></head><body>{html}</body></html>";
        byte[] buffer = Encoding.UTF8.GetBytes(fullHtml);
        context.Response.ContentType = "text/html; charset=UTF-8";
        context.Response.ContentLength64 = buffer.Length;
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        context.Response.Close();
    }
    
    private string ExchangeCodeForToken(string code, string codeVerifier, string redirectUrl)
    {
        using var client = new HttpClient();
        
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _settings.ClientId!,
            ["code"] = code,
            ["redirect_uri"] = redirectUrl,
            ["code_verifier"] = codeVerifier
        });
        
        // 機密アプリの場合は client_secret も追加
        if (!string.IsNullOrEmpty(_settings.ClientSecret))
        {
            content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _settings.ClientId!,
                ["client_secret"] = _settings.ClientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUrl,
                ["code_verifier"] = codeVerifier
            });
        }
        
        var response = client.PostAsync("https://slack.com/api/oauth.v2.access", content).Result;
        var body = response.Content.ReadAsStringAsync().Result;
        
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        
        if (!root.GetProperty("ok").GetBoolean())
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
            throw new InvalidOperationException($"Token exchange failed: {error}");
        }
        
        // User Token は authed_user.access_token に含まれる
        if (root.TryGetProperty("authed_user", out var authedUser))
        {
            _accessToken = authedUser.GetProperty("access_token").GetString();
        }
        else
        {
            // Bot Token fallback
            _accessToken = root.GetProperty("access_token").GetString();
        }
        
        if (root.TryGetProperty("refresh_token", out var rt))
        {
            _refreshToken = rt.GetString();
        }
        
        return _accessToken!;
    }
    
    public string RefreshAccessToken()
    {
        if (string.IsNullOrEmpty(_refreshToken))
            throw new InvalidOperationException("No refresh token available.");
        
        if (string.IsNullOrEmpty(_settings.ClientId))
            throw new InvalidOperationException("ClientId is required for token refresh.");
        
        using var client = new HttpClient();
        
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = _settings.ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _refreshToken
        };
        
        if (!string.IsNullOrEmpty(_settings.ClientSecret))
        {
            parameters["client_secret"] = _settings.ClientSecret;
        }
        
        var content = new FormUrlEncodedContent(parameters);
        var response = client.PostAsync("https://slack.com/api/oauth.v2.access", content).Result;
        var body = response.Content.ReadAsStringAsync().Result;
        
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        
        if (!root.GetProperty("ok").GetBoolean())
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
            throw new InvalidOperationException($"Token refresh failed: {error}");
        }
        
        _accessToken = root.GetProperty("access_token").GetString();
        
        if (root.TryGetProperty("refresh_token", out var rt))
        {
            _refreshToken = rt.GetString();
        }
        
        return _accessToken!;
    }
    
    private static string GenerateCodeVerifier()
    {
        var bytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
    
    private static string GenerateCodeChallenge(string codeVerifier)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(codeVerifier));
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}