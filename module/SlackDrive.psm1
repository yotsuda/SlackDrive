# SlackDrive PowerShell Module
# Provides Slack workspace navigation as a PowerShell drive

$script:HasSecretManagement = $null -ne (Get-Module -ListAvailable -Name Microsoft.PowerShell.SecretManagement)

function New-SlackDrive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter()]
        [string]$Token,

        [Parameter()]
        [string]$SecretName,

        [Parameter()]
        [string]$Vault,

        [Parameter()]
        [string]$RefreshToken,

        [Parameter()]
        [string]$ClientId,

        [Parameter()]
        [string]$ClientSecret,

        [Parameter()]
        [string]$Cookie
    )

    # Resolve token: SecretManagement → direct Token parameter
    $resolvedToken = $Token
    if (-not $resolvedToken -and $SecretName) {
        if (-not $script:HasSecretManagement) {
            throw "SecretName was specified but Microsoft.PowerShell.SecretManagement is not installed. Install it with: Install-PSResource Microsoft.PowerShell.SecretManagement"
        }
        $getParams = @{ Name = $SecretName; AsPlainText = $true }
        if ($Vault) { $getParams['Vault'] = $Vault }
        $resolvedToken = Get-Secret @getParams
    }
    if (-not $resolvedToken) {
        throw "Token is required. Specify -Token directly or use -SecretName to retrieve from SecretManagement."
    }

    $params = @{
        Name       = $Name
        PSProvider = 'Slack'
        Root       = '\'
        Token      = $resolvedToken
        Scope      = 'Global'
    }
    if ($Cookie) { $params['Cookie'] = $Cookie }
    New-PSDrive @params
}

function Set-SlackDriveSecret {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Token,

        [Parameter()]
        [string]$Vault
    )

    if (-not $script:HasSecretManagement) {
        throw "Microsoft.PowerShell.SecretManagement is not installed. Install it with: Install-PSResource Microsoft.PowerShell.SecretManagement"
    }

    $setParams = @{ Name = $Name; Secret = $Token }
    if ($Vault) { $setParams['Vault'] = $Vault }
    Set-Secret @setParams
    Write-Host "Token saved as '$Name'. Use: New-SlackDrive -Name <DriveName> -SecretName '$Name'" -ForegroundColor Green
}

Export-ModuleMember -Function New-SlackDrive, Set-SlackDriveSecret
