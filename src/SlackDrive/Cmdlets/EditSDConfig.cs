using System.Diagnostics;
using System.Management.Automation;
using System.Runtime.InteropServices;

namespace SlackDrive;

[Cmdlet(VerbsData.Edit, "SDConfig")]
public class EditSDConfigCommand : PSCmdlet
{
    [Parameter(Position = 0)]
    [ValidateSet("Default", "Notepad", IgnoreCase = true)]
    public string? EditorType { get; set; }

    protected override void ProcessRecord()
    {
        SlackDriveConfigManager.EnsureDefaultConfigExists();
        string configFilePath = SlackDriveConfigManager.GetConfigFilePath();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string? editor = string.IsNullOrEmpty(EditorType) || EditorType.Equals("Notepad", StringComparison.OrdinalIgnoreCase)
                ? "notepad.exe"
                : null;

            try
            {
                if (editor != null)
                {
                    Process.Start(new ProcessStartInfo(editor, configFilePath) { UseShellExecute = true });
                }
                else
                {
                    Process.Start(new ProcessStartInfo(configFilePath) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "LaunchEditorFailed", ErrorCategory.ResourceUnavailable, configFilePath));
            }

            WriteWarning($"Please edit '{configFilePath}'. After saving, restart PowerShell and run `Import-Module SlackDrive` to apply changes.");
            return;
        }

        // Linux/macOS
        string? folder = Path.GetDirectoryName(configFilePath);
        string fileName = Path.GetFileName(configFilePath);

        SessionState.Path.PushCurrentLocation("default");
        SessionState.Path.SetLocation(folder!);

        WriteWarning($"Please edit './{fileName}'. After saving, restart PowerShell and run `Import-Module SlackDrive` to apply changes. Use `popd` to return.");
    }
}