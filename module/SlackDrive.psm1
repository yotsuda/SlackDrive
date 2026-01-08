# SlackDrive PowerShell Module
# Provides Slack workspace navigation as a PowerShell drive

$script:ModuleRoot = $PSScriptRoot

# Import the assembly
$assemblyPath = Join-Path $script:ModuleRoot 'SlackDrive.dll'
if (Test-Path $assemblyPath) {
    Add-Type -Path $assemblyPath
}

# Helper function to create a new Slack drive
function New-SlackDrive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Token,

        [Parameter()]
        [string]$RefreshToken,

        [Parameter()]
        [string]$ClientId,

        [Parameter()]
        [string]$ClientSecret
    )

    New-PSDrive -Name $Name -PSProvider Slack -Root '/' -Token $Token -Scope Global
}

# Export functions
Export-ModuleMember -Function New-SlackDrive