using System.Management.Automation;

namespace SlackDrive;

[Cmdlet(VerbsCommon.Get, "SlackConfigPath")]
[OutputType(typeof(string))]
public class GetSlackConfigPathCommand : PSCmdlet
{
    protected override void ProcessRecord()
    {
        WriteObject(SlackDriveConfigManager.GetConfigFilePath());
    }
}
