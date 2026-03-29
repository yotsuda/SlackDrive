using System.Management.Automation;

namespace SlackDrive;

[Cmdlet(VerbsData.Import, "SlackConfig")]
[OutputType(typeof(SlackDriveInfo))]
public class ImportSlackConfigCommand : PSCmdlet
{
    protected override void ProcessRecord()
    {
        SlackDriveConfigManager.EnsureDefaultConfigExists();

        var config = SlackDriveConfigManager.LoadConfig();
        if (config?.PSDrives == null || config.PSDrives.Count == 0)
        {
            WriteWarning("No drives configured. Run Edit-SlackConfig to set up your Slack workspaces.");
            return;
        }

        // カレントドライブが Slack の場合、FileSystem に退避してから削除
        {
            using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
            ps.AddScript("if ((Get-Location).Provider.Name -eq 'Slack') { Set-Location $env:USERPROFILE }");
            ps.Invoke();
        }

        // Remove existing Slack drives that match config names
        foreach (var driveSettings in config.PSDrives)
        {
            if (string.IsNullOrEmpty(driveSettings.Name)) continue;
            try
            {
                using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
                ps.AddCommand("Get-PSDrive")
                    .AddParameter("Name", driveSettings.Name)
                    .AddParameter("ErrorAction", "SilentlyContinue");
                var existing = ps.Invoke();
                if (existing.Count > 0 && existing[0].BaseObject is PSDriveInfo drive &&
                    drive.Provider.Name == "Slack")
                {
                    ps.Commands.Clear();
                    ps.AddCommand("Remove-PSDrive").AddParameter("Name", driveSettings.Name);
                    ps.Invoke();
                    WriteVerbose($"Removed existing drive: {driveSettings.Name}");
                }
            }
            catch { /* ignore cleanup errors */ }
        }

        // Mount drives from config (no authentication yet)
        foreach (var driveSettings in config.PSDrives)
        {
            driveSettings.CascadeFromGlobalSettings(config);

            if (driveSettings.Enabled != true) continue;
            if (string.IsNullOrEmpty(driveSettings.Name)) continue;

            if (string.IsNullOrEmpty(driveSettings.Token) && string.IsNullOrEmpty(driveSettings.ClientId))
            {
                WriteWarning($"\"{driveSettings.Name}\": Neither Token nor ClientId specified. Skipping.");
                continue;
            }

            try
            {
                var baseDriveInfo = new PSDriveInfo(
                    driveSettings.Name,
                    SessionState.Provider.GetOne("Slack"),
                    @"\",
                    driveSettings.Description ?? driveSettings.Name,
                    null);

                SlackDriveInfo slackDrive;

                var proxy = driveSettings.Proxy;
                var logging = driveSettings.Logging;

                if (!string.IsNullOrEmpty(driveSettings.Token))
                {
                    slackDrive = new SlackDriveInfo(baseDriveInfo, driveSettings.Token,
                        proxy: proxy, logging: logging);
                }
                else
                {
                    var settings = driveSettings; // closure capture
                    slackDrive = new SlackDriveInfo(baseDriveInfo, ct =>
                    {
                        var authManager = new SlackAuthManager(settings);
                        return Task.FromResult(authManager.GetAccessToken(ct));
                    }, proxy: proxy, logging: logging);
                }

                SessionState.Drive.New(slackDrive, "global");
                WriteVerbose($"Mounted {driveSettings.Name}:");
            }
            catch (Exception ex)
            {
                WriteWarning($"\"{driveSettings.Name}\": Failed to mount - {ex.Message}");
            }
        }
    }
}
