using System.Management.Automation;
using System.Text.Json;

namespace SlackDrive;

[Cmdlet(VerbsCommon.Find, "SlackUser")]
[OutputType(typeof(SlackPeopleResult))]
public class FindSlackUserCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Query { get; set; } = "";

    [Parameter]
    [Alias("Workspace", "Path")]
    public string? Drive { get; set; }

    [Parameter]
    public int Count { get; set; } = 10;

    protected override void ProcessRecord()
    {
        var slackDrive = SlackDriveResolver.Resolve(this, Drive);
        if (slackDrive == null) return;

        JsonDocument doc;
        try
        {
            doc = slackDrive.Client.PostAsync("search.modules", new
            {
                query = Query,
                module = "people",
                count = Count
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(ex, "SlackApiError",
                ErrorCategory.ConnectionError, Query));
            return;
        }

        var root = doc.RootElement;
        if (!root.GetProperty("ok").GetBoolean())
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
            WriteWarning($"Search failed: {error}");
            return;
        }

        var cache = slackDrive.Cache.Users;
        var directory = $"{slackDrive.Name}:\\Users";

        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            var result = new SlackPeopleResult(directory, item);

            // メモリキャッシュに追加
            if (cache != null)
                cache[result.Name] = result.ToSlackUser();

            WriteObject(result);
        }
    }
}
