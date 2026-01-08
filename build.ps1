# Build and deploy script for SlackDrive
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    
    [switch]$Deploy,
    
    [string]$DeployPath = "C:\Program Files\PowerShell\7\Modules\SlackDrive"
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
        Write-Host "  Copied $file to module/" -ForegroundColor Green
    }
}

Write-Host "`nBuild completed. Module is ready at: $modulePath" -ForegroundColor Green

# Deploy to PowerShell modules folder
if ($Deploy) {
    Write-Host "`nDeploying to $DeployPath..." -ForegroundColor Cyan
    
    # Create directory if not exists
    if (-not (Test-Path $DeployPath)) {
        New-Item -Path $DeployPath -ItemType Directory -Force | Out-Null
        Write-Host "  Created $DeployPath" -ForegroundColor Yellow
    }
    
    # Copy all module files
    $moduleFiles = Get-ChildItem $modulePath -File
    foreach ($file in $moduleFiles) {
        try {
            Copy-Item $file.FullName $DeployPath -Force
            Write-Host "  Deployed $($file.Name)" -ForegroundColor Green
        }
        catch {
            Write-Host "  Failed to copy $($file.Name): $($_.Exception.Message)" -ForegroundColor Red
            Write-Host "  (File may be locked by another PowerShell session)" -ForegroundColor Yellow
        }
    }
    
    Write-Host "`nDeployment completed." -ForegroundColor Green
    Write-Host "Restart PowerShell and run: Import-Module SlackDrive" -ForegroundColor Yellow
}
else {
    Write-Host "`nTo deploy, run: .\build.ps1 -Deploy" -ForegroundColor Yellow
}