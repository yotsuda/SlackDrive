using System.Management.Automation;
using System.Text.Json;

namespace SlackDrive;

[Cmdlet(VerbsCommon.Find, "SlackMessage")]
[OutputType(typeof(SlackMessage))]
public class FindSlackMessageCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Query { get; set; } = "";

    [Parameter]
    public string? Channel { get; set; }

    [Parameter]
    public DateTime? After { get; set; }

    [Parameter]
    public DateTime? Before { get; set; }

    [Parameter]
    public int Count { get; set; } = 20;

    [Parameter]
    [Alias("Workspace", "Path")]
    public string? Drive { get; set; }

    protected override void ProcessRecord()
    {
        var slackDrive = SlackDriveResolver.Resolve(this, Drive);
        if (slackDrive == null) return;

        // Build search query with modifiers
        var searchQuery = Query;
        if (!string.IsNullOrEmpty(Channel))
            searchQuery = $"in:#{Channel} {searchQuery}";
        if (After != null)
            searchQuery = $"after:{After.Value:yyyy-MM-dd} {searchQuery}";
        if (Before != null)
            searchQuery = $"before:{Before.Value:yyyy-MM-dd} {searchQuery}";

        var queryParams = new Dictionary<string, string>
        {
            ["query"] = searchQuery,
            ["count"] = Count.ToString(),
            ["sort"] = "timestamp",
            ["sort_dir"] = "desc"
        };

        JsonDocument doc;
        try
        {
            doc = slackDrive.Client.GetAsync("search.messages", queryParams).GetAwaiter().GetResult();
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

        var users = slackDrive.Cache.Users;
        var directory = $"{slackDrive.Name}:\\Search";

        var matches = root.GetProperty("messages").GetProperty("matches");
        var total = root.GetProperty("messages").GetProperty("total").GetInt32();

        if (total == 0)
        {
            WriteWarning($"No messages found for: {searchQuery}");
            return;
        }

        foreach (var m in matches.EnumerateArray())
        {
            var userId = m.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "";
            var ts = m.GetProperty("ts").GetString() ?? "";
            var userName = m.TryGetProperty("username", out var un) ? un.GetString() : null;
            userName ??= users?.Values.FirstOrDefault(x => x.Id == userId)?.Name ?? userId;

            var channelInfo = m.TryGetProperty("channel", out var ch) ? ch : default;
            var channelName = channelInfo.ValueKind != JsonValueKind.Undefined &&
                              channelInfo.TryGetProperty("name", out var cn) ? cn.GetString() : null;

            var rawText = m.GetProperty("text").GetString() ?? "";
            var message = new SlackMessage
            {
                Ts = ts,
                UserId = userId,
                UserName = userName,
                Text = users != null ? ResolveSlackMentions(rawText, users) : rawText,
                ThreadTs = m.TryGetProperty("thread_ts", out var tts) ? tts.GetString() : null,
                ReplyCount = m.TryGetProperty("reply_count", out var rc) ? rc.GetInt32() : 0,
                Directory = channelName != null ? $"{slackDrive.Name}:\\Channels\\{channelName}" : directory
            };

            WriteObject(message);
        }

        if (total > Count)
            WriteWarning($"Showing {Count} of {total} results. Use -Count to see more.");
    }

    private static string ResolveSlackMentions(string text, Dictionary<string, SlackUser> users)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<@([UW][A-Z0-9]+)(?:\|([^>]+))?>", m =>
        {
            var id = m.Groups[1].Value;
            var fallback = m.Groups[2].Success ? m.Groups[2].Value : null;
            var user = users.Values.FirstOrDefault(x => x.Id == id);
            var name = (!string.IsNullOrEmpty(user?.DisplayName) ? user.DisplayName : user?.Name)
                    ?? fallback ?? id;
            return $"@{name}";
        });
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<#C[A-Z0-9]+\|([^>]+)>", m => $"#{m.Groups[1].Value}");
        return text;
    }
}
