# SlackDrive PowerShell Module
# Provides Slack workspace navigation as a PowerShell drive

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
