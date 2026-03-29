# Build and deploy script for SlackDrive
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$srcPath = Join-Path $projectRoot 'src\SlackDrive'
$modulePath = Join-Path $projectRoot 'module'
$deployPath = 'C:\Program Files\PowerShell\7\Modules\SlackDrive'

Write-Host 'Building SlackDrive (Release)...' -ForegroundColor Cyan

dotnet build $srcPath -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

# Copy DLL to module folder
$outputPath = Join-Path $srcPath 'bin\Release\net9.0'
foreach ($file in @('SlackDrive.dll', 'SlackDrive.pdb')) {
    $source = Join-Path $outputPath $file
    if (Test-Path $source) {
        Copy-Item $source $modulePath -Force
        Write-Host "  Copied $file to module/" -ForegroundColor Green
    }
}

Write-Host "`nBuild completed. Module is ready at: $modulePath" -ForegroundColor Green

# Deploy (exclude dev/debug files)
Write-Host "`nDeploying to $deployPath..." -ForegroundColor Cyan

if (-not (Test-Path $deployPath)) {
    New-Item -Path $deployPath -ItemType Directory -Force | Out-Null
    Write-Host "  Created $deployPath" -ForegroundColor Yellow
}

$exclude = @('*.dev.psd1', '*.pdb')
foreach ($file in Get-ChildItem "$modulePath\*" -File -Exclude $exclude) {
    try {
        Copy-Item $file.FullName $deployPath -Force
        Write-Host "  Deployed $($file.Name)" -ForegroundColor Green
    }
    catch {
        Write-Host "  Failed to copy $($file.Name): $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "  (File may be locked by another PowerShell session)" -ForegroundColor Yellow
    }
}

# Clean up stale files from previous builds
foreach ($stale in Get-ChildItem $deployPath -File | Where-Object { $_.Name -match '\.(dev\.psd1|pdb|deps\.json)$' }) {
    try {
        Remove-Item $stale.FullName -Force
        Write-Host "  Removed stale $($stale.Name)" -ForegroundColor Yellow
    }
    catch { }
}

Write-Host "`nDeployment completed." -ForegroundColor Green
Write-Host "Restart PowerShell and run: Import-SlackConfig" -ForegroundColor Yellow