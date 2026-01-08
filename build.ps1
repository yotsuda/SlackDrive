# Build script for SlackDrive
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$srcPath = Join-Path $projectRoot 'src\SlackDrive'
$modulePath = Join-Path $projectRoot 'module'

Write-Host "Building SlackDrive ($Configuration)..." -ForegroundColor Cyan

# Build the project
dotnet build $srcPath -c $Configuration

if ($LASTEXITCODE -ne 0) {
    throw "Build failed"
}

# Copy DLL to module folder
$outputPath = Join-Path $srcPath "bin\$Configuration\net9.0"
$dllFiles = @(
    'SlackDrive.dll',
    'SlackDrive.pdb'
)

foreach ($file in $dllFiles) {
    $source = Join-Path $outputPath $file
    if (Test-Path $source) {
        Copy-Item $source $modulePath -Force
        Write-Host "  Copied $file" -ForegroundColor Green
    }
}

Write-Host "`nBuild completed. Module is ready at: $modulePath" -ForegroundColor Green
Write-Host "`nTo use:" -ForegroundColor Yellow
Write-Host "  Import-Module $modulePath\SlackDrive.psd1"
Write-Host "  New-SlackDrive -Name MySlack -Token 'xoxb-...'"
Write-Host "  ls MySlack:\channels"