# Deploy SlackDrive module to PowerShell 7 system modules directory.
# Run as Administrator (writes to Program Files).

param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$srcDir      = Join-Path $projectRoot 'src\SlackDrive'
$moduleDir   = Join-Path $projectRoot 'module'
$deployDir   = 'C:\Program Files\PowerShell\7\Modules\SlackDrive'

# Build
if (-not $NoBuild) {
    Write-Host 'Building...' -ForegroundColor Cyan
    dotnet build $srcDir -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

$binDir = Join-Path $srcDir 'bin\Release\net9.0'

# Copy
if (Test-Path $deployDir) { Remove-Item $deployDir -Recurse -Force }
New-Item $deployDir -ItemType Directory -Force | Out-Null

# DLL from build output
Copy-Item (Join-Path $binDir 'SlackDrive.dll')       $deployDir
Copy-Item (Join-Path $binDir 'SlackDrive.deps.json')  $deployDir

# Module files
Copy-Item (Join-Path $moduleDir 'SlackDrive.psd1')        $deployDir
Copy-Item (Join-Path $moduleDir 'SlackDrive.psm1')        $deployDir
Copy-Item (Join-Path $moduleDir 'SlackDrive.Format.ps1xml') $deployDir

Write-Host "Deployed to $deployDir" -ForegroundColor Green
Get-ChildItem $deployDir | Format-Table Name, Length -AutoSize
