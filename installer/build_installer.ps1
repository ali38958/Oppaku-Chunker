# Oppaku MSI Installer Build Script
param(
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Oppaku - Windows MSI Installer Builder  " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

if (-not $SkipPublish) {
    Write-Host "`n[1/3] Publishing self-contained CLI executable..." -ForegroundColor Yellow
    dotnet publish src/Oppaku.Cli/Oppaku.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/cli
    if ($LASTEXITCODE -ne 0) { throw "Failed to publish CLI project." }

    Write-Host "`n[2/3] Publishing self-contained GUI executable..." -ForegroundColor Yellow
    dotnet publish src/Oppaku.Gui/Oppaku.Gui.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/gui
    if ($LASTEXITCODE -ne 0) { throw "Failed to publish GUI project." }
} else {
    Write-Host "`n[1-2/3] Skipping dotnet publish step..." -ForegroundColor DarkGray
}

Write-Host "`n[3/3] Compiling native Windows Installer (.msi)..." -ForegroundColor Yellow
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Host "Installing WiX toolset v5..." -ForegroundColor Cyan
    dotnet tool install --global wix --version 5.0.2
    wix extension add --global WixToolset.UI.wixext/5.0.2
}

wix build -ext WixToolset.UI.wixext installer/Oppaku.wxs -o publish/Oppaku-Setup.msi
if ($LASTEXITCODE -ne 0) { throw "WiX build failed." }

$msiFile = Get-Item "publish/Oppaku-Setup.msi"
$msiSizeMb = [math]::Round($msiFile.Length / 1MB, 2)

Write-Host "`n==========================================" -ForegroundColor Green
Write-Host " [SUCCESS] Installer generated at:" -ForegroundColor Green
Write-Host " $msiFile ($msiSizeMb MB)" -ForegroundColor White
Write-Host "==========================================" -ForegroundColor Green
