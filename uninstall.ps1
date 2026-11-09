<#
.SYNOPSIS
    Smart Hot Corners - 1-Click Uninstaller
#>

[CmdletBinding()]
param(
    [switch]$DeleteFiles
)

Write-Host "Stopping HotCorners..." -ForegroundColor Yellow
Get-Process -Name HotCorners -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# Remove Startup Shortcut
$startupDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$shortcutPath = Join-Path $startupDir "HotCorners.lnk"
if (Test-Path $shortcutPath) {
    Remove-Item $shortcutPath -Force -ErrorAction SilentlyContinue
    Write-Host "[+] Removed Startup shortcut." -ForegroundColor Green
}

# Remove Registry Run key
$runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $runPath -Name 'SmartHotCorners' -ErrorAction SilentlyContinue
Write-Host "[+] Removed Registry Run entry." -ForegroundColor Green

# Remove installed directory if requested
$targetDir = Join-Path $env:APPDATA "WindowsHotCorners"
if ($DeleteFiles -and (Test-Path $targetDir)) {
    Remove-Item -Path $targetDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "[+] Removed installed files from $targetDir." -ForegroundColor Green
}

Write-Host ""
Write-Host "[SUCCESS] Smart Hot Corners has been uninstalled." -ForegroundColor Green

