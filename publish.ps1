# Publish SlackDrive module to PowerShell Gallery.
# Usage: .\publish.ps1 -NuGetApiKey "your-api-key"
#        .\publish.ps1 -NuGetApiKey "your-api-key" -WhatIf

param(
    [Parameter(Mandatory)]
    [string]$NuGetApiKey,

    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$srcDir      = Join-Path $projectRoot 'src\SlackDrive'
$moduleDir   = Join-Path $projectRoot 'module'

# Build Release
Write-Host 'Building Release...' -ForegroundColor Cyan
dotnet build $srcDir -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

# Copy DLL to module folder
$binDir = Join-Path $srcDir 'bin\Release\net9.0'
Copy-Item (Join-Path $binDir 'SlackDrive.dll') $moduleDir -Force
Write-Host '  Copied SlackDrive.dll' -ForegroundColor Green

# Validate manifest
Write-Host 'Validating module manifest...' -ForegroundColor Cyan
$manifest = Test-ModuleManifest (Join-Path $moduleDir 'SlackDrive.psd1')
Write-Host "  Module: $($manifest.Name) v$($manifest.Version)" -ForegroundColor Green

# Publish
$publishParams = @{
    Path        = $moduleDir
    NuGetApiKey = $NuGetApiKey
}
if ($WhatIf) {
    $publishParams['WhatIf'] = $true
    Write-Host "`nDry-run publish..." -ForegroundColor Yellow
} else {
    Write-Host "`nPublishing to PowerShell Gallery..." -ForegroundColor Cyan
}

Publish-Module @publishParams

if (-not $WhatIf) {
    Write-Host "`nPublished successfully! https://www.powershellgallery.com/packages/SlackDrive" -ForegroundColor Green
}
