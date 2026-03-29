using System.Diagnostics;
using System.Net;
using System.Reflection;
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
    
    public string GetAccessToken(CancellationToken ct = default)
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
            return AuthorizeWithOAuth(ct);
        }

        throw new InvalidOperationException("No valid authentication method configured. Provide Token, or ClientId for OAuth flow.");
    }
    
    private string AuthorizeWithOAuth(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_settings.ClientId))
            throw new InvalidOperationException("ClientId is required for OAuth flow.");

        // デフォルトのリダイレクト URL (GitHub Pages → localhost リレー)
        string redirectUrl = _settings.RedirectUrl ?? "https://yotsuda.github.io/SlackDrive/oauth/callback.html";

        // PKCE 用の code_verifier 生成
        string codeVerifier = GenerateCodeVerifier();
        string codeChallenge = GenerateCodeChallenge(codeVerifier);

        // スコープ (カスタマイズ可能)
        string scopes = _settings.Scopes
            ?? "channels:read channels:history groups:read groups:history im:read im:history mpim:read mpim:history users:read files:read chat:write search:read.public search:read.private search:read.im search:read.mpim search:read.files search:read.users";

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
        string authorizationCode = WaitForAuthorizationCode(redirectUrl, state, authUrl, ct);

        // トークン交換
        return ExchangeCodeForToken(authorizationCode, codeVerifier, redirectUrl);
    }

    private string WaitForAuthorizationCode(string redirectUrl, string expectedState, string authUrl, CancellationToken ct)
    {
        // GitHub Pages リレー等の外部 URL の場合は localhost:8765 で待機
        var uri = new Uri(redirectUrl);
        string listenerPrefix;
        if (uri.Host != "localhost" && uri.Host != "127.0.0.1")
        {
            listenerPrefix = "http://localhost:8765/slack/callback/";
        }
        else
        {
            listenerPrefix = $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}";
            if (!listenerPrefix.EndsWith("/")) listenerPrefix += "/";
        }
        
        using var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add(listenerPrefix);
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            string message = uri.Port <= 1024
                ? $"Failed to start HttpListener on port {uri.Port}. Administrative privileges may be required."
                : $"Failed to start HttpListener on port {uri.Port}. The port may be in use.";
            throw new InvalidOperationException(message, ex);
        }
        
        // ブラウザで認証 URL を開く
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        // Ctrl+C でリスナーを停止し、GetContext をキャンセルする
        using var ctReg = ct.Register(() => listener.Stop());

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
                SendFailureResponse(context, error);
                throw new InvalidOperationException($"OAuth error: {error}");
            }

            // State 検証
            if (state != expectedState)
            {
                SendFailureResponse(context, "Invalid state parameter.");
                throw new InvalidOperationException("Invalid state parameter - possible CSRF attack.");
            }

            if (string.IsNullOrEmpty(code))
            {
                SendFailureResponse(context, "No authorization code received.");
                throw new InvalidOperationException("No authorization code received.");
            }

            authorizationCode = code;
            SendSuccessResponse(context);
        }
        catch (HttpListenerException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("OAuth authentication was cancelled.", ct);
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }

        return authorizationCode!;
    }
    
    private void SendSuccessResponse(HttpListenerContext context)
    {
        var html = LoadEmbeddedResource("SlackDrive.Resources.AuthSuccess.html");
        html = html.Replace("{{DRIVE_NAME}}", WebUtility.HtmlEncode(_settings.Name ?? "Slack"));
        html = html.Replace("{{VERSION}}", GetVersion());
        WriteResponse(context, html, 200);
    }

    private static void SendFailureResponse(HttpListenerContext context, string errorMessage)
    {
        var html = LoadEmbeddedResource("SlackDrive.Resources.AuthFailure.html");
        html = html.Replace("{{ERROR_MESSAGE}}", WebUtility.HtmlEncode(errorMessage));
        html = html.Replace("{{VERSION}}", GetVersion());
        WriteResponse(context, html, 400);
    }

    private static void WriteResponse(HttpListenerContext context, string html, int statusCode)
    {
        byte[] buffer = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=UTF-8";
        context.Response.ContentLength64 = buffer.Length;
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        context.Response.Close();
    }

    private static string LoadEmbeddedResource(string resourceName)
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string GetVersion()
    {
        return typeof(SlackAuthManager).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
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