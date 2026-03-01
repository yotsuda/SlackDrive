@{
    RootModule = '..\src\SlackDrive\bin\Debug\net9.0\SlackDrive.dll'
    ModuleVersion = '0.1.0'
    GUID = 'a7b8c9d0-1234-5678-9abc-def012345678'
    Author = 'Yoshifumi Tsuda'
    Description = 'PowerShell Provider for Slack'
    PowerShellVersion = '7.0'
    FormatsToProcess = @('SlackDrive.Format.ps1xml')
    FunctionsToExport = @()
    CmdletsToExport = @('Edit-SlackConfig', 'Update-SlackCache')
    AliasesToExport = @()
}
