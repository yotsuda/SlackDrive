using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Language;

namespace SlackDrive;

[Cmdlet(VerbsCommon.Join, "SlackChannel", SupportsShouldProcess = true)]
[OutputType(typeof(string))]
public class JoinSlackChannelCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [ArgumentCompleter(typeof(JoinableChannelCompleter))]
    public string[] Name { get; set; } = [];

    [Parameter]
    [Alias("Workspace", "Path")]
    public string? Drive { get; set; }

    protected override void ProcessRecord()
    {
        var slackDrive = SlackDriveResolver.Resolve(this, Drive);
        if (slackDrive == null) return;

        foreach (var channelName in Name)
        {
            var channels = slackDrive.Cache.Channels;
            if (channels == null)
            {
                WriteWarning("Channel cache is not available. Run 'ls <drive>:\\Channels -All' first.");
                return;
            }

            if (!channels.TryGetValue(channelName, out var channel))
            {
                WriteWarning($"Channel '{channelName}' not found.");
                continue;
            }

            if (channel.IsMember)
            {
                WriteWarning($"Already a member of #{channelName}.");
                continue;
            }

            if (channel.IsPrivate)
            {
                WriteWarning($"#{channelName} is a private channel. You must be invited by an existing member.");
                continue;
            }

            if (!ShouldProcess($"#{channelName}", "Join channel"))
                continue;

            try
            {
                var doc = slackDrive.Client.PostAsync("conversations.join",
                    new { channel = channel.Id }).GetAwaiter().GetResult();
                var root = doc.RootElement;

                if (root.GetProperty("ok").GetBoolean())
                {
                    channel.IsMember = true;
                    WriteObject($"Joined #{channelName}");
                }
                else
                {
                    var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
                    WriteWarning($"Failed to join #{channelName}: {error}");
                }
            }
            catch (Exception ex)
            {
                WriteWarning($"Failed to join #{channelName}: {ex.Message}");
            }
        }
    }
}

[Cmdlet(VerbsCommon.Exit, "SlackChannel", SupportsShouldProcess = true)]
[OutputType(typeof(string))]
public class ExitSlackChannelCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [ArgumentCompleter(typeof(MemberChannelCompleter))]
    public string[] Name { get; set; } = [];

    [Parameter]
    [Alias("Workspace", "Path")]
    public string? Drive { get; set; }

    protected override void ProcessRecord()
    {
        var slackDrive = SlackDriveResolver.Resolve(this, Drive);
        if (slackDrive == null) return;

        foreach (var channelName in Name)
        {
            var channels = slackDrive.Cache.Channels;
            if (channels == null)
            {
                WriteWarning("Channel cache is not available. Run 'ls <drive>:\\Channels' first.");
                return;
            }

            if (!channels.TryGetValue(channelName, out var channel))
            {
                WriteWarning($"Channel '{channelName}' not found.");
                continue;
            }

            if (!channel.IsMember)
            {
                WriteWarning($"Not a member of #{channelName}.");
                continue;
            }

            if (!ShouldProcess($"#{channelName}", "Leave channel"))
                continue;

            try
            {
                var doc = slackDrive.Client.PostAsync("conversations.leave",
                    new { channel = channel.Id }).GetAwaiter().GetResult();
                var root = doc.RootElement;

                if (root.GetProperty("ok").GetBoolean())
                {
                    channel.IsMember = false;
                    WriteObject($"Left #{channelName}");
                }
                else
                {
                    var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
                    WriteWarning($"Failed to leave #{channelName}: {error}");
                }
            }
            catch (Exception ex)
            {
                WriteWarning($"Failed to leave #{channelName}: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// -Drive / -Workspace / -Path パラメータから SlackDriveInfo を解決する共通ロジック。
/// </summary>
internal static class SlackDriveResolver
{
    public static SlackDriveInfo? Resolve(PSCmdlet cmdlet, string? driveParam)
    {
        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);

        var driveName = ResolveDriveName(driveParam);
        if (!string.IsNullOrEmpty(driveName))
        {
            ps.AddCommand("Get-PSDrive").AddParameter("Name", driveName)
                .AddParameter("ErrorAction", "SilentlyContinue");
        }
        else
        {
            ps.AddScript("(Get-Location).Drive");
        }

        var driveObj = ps.Invoke().FirstOrDefault()?.BaseObject;
        if (driveObj is SlackDriveInfo slackDrive)
            return slackDrive;

        // Fallback: first Slack drive
        ps.Commands.Clear();
        ps.AddCommand("Get-PSDrive");
        foreach (var d in ps.Invoke())
        {
            if (d.BaseObject is SlackDriveInfo sd)
                return sd;
        }

        cmdlet.WriteWarning("No Slack drive found. Run Import-SlackConfig or New-SlackDrive first.");
        return null;
    }

    public static SlackDriveInfo? ResolveFromFakeBound(IDictionary fakeBoundParameters)
    {
        var wsParam = fakeBoundParameters.Contains("Drive") ? fakeBoundParameters["Drive"]?.ToString()
                    : fakeBoundParameters.Contains("Workspace") ? fakeBoundParameters["Workspace"]?.ToString()
                    : fakeBoundParameters.Contains("Path") ? fakeBoundParameters["Path"]?.ToString()
                    : null;

        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);

        if (!string.IsNullOrEmpty(wsParam))
        {
            var driveName = ResolveDriveName(wsParam);
            if (!string.IsNullOrEmpty(driveName))
            {
                ps.AddCommand("Get-PSDrive").AddParameter("Name", driveName)
                    .AddParameter("ErrorAction", "SilentlyContinue");
                var result = ps.Invoke().FirstOrDefault()?.BaseObject as SlackDriveInfo;
                if (result != null) return result;
                ps.Commands.Clear();
            }
        }

        ps.AddScript("(Get-Location).Drive");
        var drive = ps.Invoke().FirstOrDefault()?.BaseObject as SlackDriveInfo;
        if (drive != null) return drive;

        ps.Commands.Clear();
        ps.AddCommand("Get-PSDrive");
        foreach (var d in ps.Invoke())
        {
            if (d.BaseObject is SlackDriveInfo sd) return sd;
        }
        return null;
    }

    /// <summary>
    /// "." → null (current drive), "Name:" / "Name:\path" → drive name, "Name" → drive name
    /// </summary>
    internal static string? ResolveDriveName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value == ".") return null;
        var colonIdx = value.IndexOf(':');
        return colonIdx > 0 ? value[..colonIdx] : value;
    }
}

/// <summary>
/// Join-SlackChannel 用: 未参加のパブリックチャンネルを補完。
/// </summary>
public class JoinableChannelCompleter : IArgumentCompleter
{
    public IEnumerable<CompletionResult> CompleteArgument(
        string commandName, string parameterName, string wordToComplete,
        CommandAst commandAst, IDictionary fakeBoundParameters)
    {
        var drive = SlackDriveResolver.ResolveFromFakeBound(fakeBoundParameters);
        if (drive?.Cache.Channels == null) yield break;

        var pattern = string.IsNullOrEmpty(wordToComplete)
            ? new WildcardPattern("*")
            : new WildcardPattern(wordToComplete + "*", WildcardOptions.IgnoreCase);

        foreach (var ch in drive.Cache.Channels.Values
            .Where(c => !c.IsMember && !c.IsPrivate && !c.IsArchived)
            .OrderBy(c => c.Name))
        {
            if (!pattern.IsMatch(ch.Name)) continue;
            var tooltip = string.IsNullOrEmpty(ch.Purpose)
                ? $"#{ch.Name} ({ch.MemberCount} members)"
                : $"#{ch.Name} ({ch.MemberCount} members) - {ch.Purpose}";
            yield return new CompletionResult(ch.Name, ch.Name,
                CompletionResultType.ParameterValue, tooltip);
        }
    }
}

/// <summary>
/// Exit-SlackChannel 用: 参加中のチャンネルを補完。
/// </summary>
public class MemberChannelCompleter : IArgumentCompleter
{
    public IEnumerable<CompletionResult> CompleteArgument(
        string commandName, string parameterName, string wordToComplete,
        CommandAst commandAst, IDictionary fakeBoundParameters)
    {
        var drive = SlackDriveResolver.ResolveFromFakeBound(fakeBoundParameters);
        if (drive?.Cache.Channels == null) yield break;

        var pattern = string.IsNullOrEmpty(wordToComplete)
            ? new WildcardPattern("*")
            : new WildcardPattern(wordToComplete + "*", WildcardOptions.IgnoreCase);

        foreach (var ch in drive.Cache.Channels.Values
            .Where(c => c.IsMember && !c.IsArchived)
            .OrderBy(c => c.Name))
        {
            if (!pattern.IsMatch(ch.Name)) continue;
            var tooltip = string.IsNullOrEmpty(ch.Purpose)
                ? $"#{ch.Name} ({ch.MemberCount} members)"
                : $"#{ch.Name} ({ch.MemberCount} members) - {ch.Purpose}";
            yield return new CompletionResult(ch.Name, ch.Name,
                CompletionResultType.ParameterValue, tooltip);
        }
    }
}
