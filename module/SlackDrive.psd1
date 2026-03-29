@{
    RootModule = 'SlackDrive.dll'
    ModuleVersion = '0.1.0'
    GUID = 'a7b8c9d0-1234-5678-9abc-def012345678'
    Author = 'Yoshifumi Tsuda'
    CompanyName = ''
    Copyright = '(c) 2026 Yoshifumi Tsuda. All rights reserved.'
    Description = 'PowerShell Provider for Slack workspaces. Navigate channels, users, and messages like a file system using ls, cd, and Get-Content.'
    PowerShellVersion = '7.4'
    NestedModules = @('SlackDrive.psm1')
    FormatsToProcess = @('SlackDrive.Format.ps1xml')
    FunctionsToExport = @('New-SlackDrive', 'Set-SlackDriveSecret')
    CmdletsToExport = @('Import-SlackConfig', 'Edit-SlackConfig', 'Get-SlackConfigPath', 'Update-SlackCache', 'Open-SlackPage', 'Join-SlackChannel', 'Exit-SlackChannel', 'Find-SlackUser', 'Add-SlackRedirectUrl')
    AliasesToExport = @()
    PrivateData = @{
        PSData = @{
            Tags = @('Slack', 'Provider', 'PSProvider', 'Chat', 'API', 'NavigationProvider', 'Workspace')
            LicenseUri = 'https://github.com/yotsuda/SlackDrive/blob/main/LICENSE'
            ProjectUri = 'https://github.com/yotsuda/SlackDrive'
        }
    }
}
