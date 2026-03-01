using System.Management.Automation;

namespace SlackDrive;

/// <summary>
/// Opens the corresponding Slack web page in the default browser.
/// </summary>
[Cmdlet(VerbsCommon.Open, "SlackPage")]
public class OpenSlackPageCmdlet : PSCmdlet
{
    [Parameter(Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    [Alias("PSPath")]
    public string? Path { get; set; }

    [Parameter]
    public string DriveName { get; set; } = "";

    protected override void ProcessRecord()
    {
        var drive = ResolveDrive();
        if (drive == null)
        {
            WriteError(new ErrorRecord(
                new ItemNotFoundException("No Slack drive found."),
                "NoDriveFound", ErrorCategory.ObjectNotFound, null));
            return;
        }

        // パスを正規化
        var normalized = "";
        if (!string.IsNullOrEmpty(Path))
        {
            normalized = Path;
            // ドライブプレフィックスを除去
            if (normalized.Contains(':'))
                normalized = normalized[(normalized.IndexOf(':') + 1)..];
            normalized = normalized.Replace('\\', '/').Trim('/');
        }

        var baseUrl = drive.WorkspaceUrl.TrimEnd('/');
        string? url;

        if (string.IsNullOrEmpty(normalized))
        {
            url = baseUrl;
        }
        else
        {
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var category = parts[0].ToLower();

            url = category switch
            {
                "channels" when parts.Length >= 3 => GetMessageUrl(drive, baseUrl, parts),
                "channels" when parts.Length == 2 => GetChannelUrl(drive, baseUrl, parts[1]),
                "users" when parts.Length >= 2 => GetUserUrl(drive, baseUrl, parts[1]),
                _ => baseUrl
            };
        }

        if (url != null)
            SlackDriveProvider.OpenWithShell(url);
    }

    private SlackDriveInfo? ResolveDrive()
    {
        var drives = SessionState.Drive.GetAll()
            .Where(d => d.Provider.Name == "Slack")
            .Cast<SlackDriveInfo>()
            .ToList();

        if (!string.IsNullOrEmpty(DriveName))
            return drives.FirstOrDefault(d => d.Name.Equals(DriveName, StringComparison.OrdinalIgnoreCase));

        // カレントロケーションが Slack ドライブならそれを使う
        try
        {
            var currentDrive = SessionState.Drive.Current;
            if (currentDrive is SlackDriveInfo slackDrive)
                return slackDrive;
        }
        catch { /* ignore */ }

        return drives.FirstOrDefault();
    }

    private static string? GetChannelUrl(SlackDriveInfo drive, string baseUrl, string channelName)
    {
        var channels = drive.Cache.Channels;
        if (channels != null && channels.TryGetValue(channelName, out var channel))
            return $"{baseUrl}/archives/{channel.Id}";
        return baseUrl;
    }

    private static string? GetMessageUrl(SlackDriveInfo drive, string baseUrl, string[] parts)
    {
        var channels = drive.Cache.Channels;
        if (channels != null && channels.TryGetValue(parts[1], out var channel))
        {
            var tsForUrl = parts[2].Replace(".", "");
            return $"{baseUrl}/archives/{channel.Id}/p{tsForUrl}";
        }
        return baseUrl;
    }

    private static string? GetUserUrl(SlackDriveInfo drive, string baseUrl, string userName)
    {
        var users = drive.Cache.Users;
        if (users != null && users.TryGetValue(userName, out var user))
            return $"{baseUrl}/team/{user.Id}";
        return baseUrl;
    }
}
