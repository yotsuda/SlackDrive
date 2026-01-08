@{
    RootModule = 'SlackDrive.dll'
    ModuleVersion = '0.1.0'
    GUID = 'a7b8c9d0-1234-5678-9abc-def012345678'
    Author = 'Yoshifumi Tsuda'
    CompanyName = ''
    Copyright = '(c) 2026 Yoshifumi Tsuda. All rights reserved.'
    Description = 'PowerShell Provider for Slack workspaces. Navigate channels, users, and messages like a file system.'
    PowerShellVersion = '7.0'
    NestedModules = @('SlackDrive.psm1')
    FormatsToProcess = @('SlackDrive.Format.ps1xml')
    FunctionsToExport = @('New-SlackDrive')
    CmdletsToExport = @('Edit-SDConfig', 'Update-SDCache')
    AliasesToExport = @()
    PrivateData = @{
        PSData = @{
            Tags = @('Slack', 'Provider', 'PSProvider', 'Chat')
            ProjectUri = 'https://github.com/yoshifumi-tsuda/SlackDrive'
        }
    }
}