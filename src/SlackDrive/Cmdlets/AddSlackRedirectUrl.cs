using System.Management.Automation;
using System.Text.Json;

namespace SlackDrive;

/// <summary>
/// Slack App の OAuth Redirect URL を追加する。
/// Slack の Web UI は HTTPS のみ受け付けるため、http://localhost を登録するにはこの cmdlet を使う。
/// xoxc トークン（ブラウザトークン）でマウントしたドライブが必要。
/// </summary>
[Cmdlet(VerbsCommon.Add, "SlackRedirectUrl", SupportsShouldProcess = true)]
public class AddSlackRedirectUrlCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string AppId { get; set; } = "";

    [Parameter(Position = 1)]
    public string Url { get; set; } = "http://localhost:8765/slack/callback";

    [Parameter]
    [Alias("Workspace", "Path")]
    public string? Drive { get; set; }

    protected override void ProcessRecord()
    {
        var slackDrive = SlackDriveResolver.Resolve(this, Drive);
        if (slackDrive == null) return;

        if (!slackDrive.IsUserToken)
        {
            WriteWarning("This command requires a browser token (xoxc-). Mount a drive with a browser token first.");
            return;
        }

        if (!ShouldProcess(Url, $"Add redirect URL to app {AppId}"))
            return;

        JsonDocument doc;
        try
        {
            doc = slackDrive.Client.PostFormAsync("developer.apps.oauth.addRedirectUrls",
                new Dictionary<string, string>
                {
                    ["app_id"] = AppId,
                    ["redirect_urls"] = Url
                }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(ex, "SlackApiError",
                ErrorCategory.ConnectionError, AppId));
            return;
        }

        var root = doc.RootElement;
        if (root.GetProperty("ok").GetBoolean())
        {
            WriteObject($"Added redirect URL: {Url}");
        }
        else
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
            WriteWarning($"Failed: {error}");
        }
    }
}
