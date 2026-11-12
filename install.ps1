<#
.SYNOPSIS
    Smart Hot Corners - 1-Click Installer for Windows 11 / 10
.DESCRIPTION
    Installs HotCorners to %APPDATA%\WindowsHotCorners, configures auto-start on boot,
    and immediately launches the hot corners daemon.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "       ⚡ Smart Hot Corners for Windows 11 / 10           " -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host ""

$sourceDir = $PSScriptRoot
$targetDir = Join-Path $env:APPDATA "WindowsHotCorners"

# 1. Create target directory
if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    Write-Host "[+] Created install directory: $targetDir" -ForegroundColor Green
}

# 2. Stop any existing instance
Get-Process -Name HotCorners -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# 3. Check for or compile HotCorners.exe
$targetExe = Join-Path $targetDir "HotCorners.exe"
$targetVD = Join-Path $targetDir "VirtualDesktop.exe"
$targetCS = Join-Path $targetDir "HotCorners.cs"

# Copy VirtualDesktop.exe
$sourceVD = Join-Path $sourceDir "VirtualDesktop.exe"
if (Test-Path $sourceVD) {
    Copy-Item $sourceVD $targetVD -Force
    Write-Host "[+] Installed VirtualDesktop.exe" -ForegroundColor Green
} else {
    Write-Warning "VirtualDesktop.exe not found in installer directory."
}

# Copy or Compile HotCorners.exe
$sourceExe = Join-Path $sourceDir "HotCorners.exe"
$sourceCS = Join-Path $sourceDir "HotCorners.cs"
$sourceIco = Join-Path $sourceDir "HotCorners.ico"
$targetIco = Join-Path $targetDir "HotCorners.ico"

if (Test-Path $sourceCS) {
    Copy-Item $sourceCS $targetCS -Force
}
if (Test-Path $sourceIco) {
    Copy-Item $sourceIco $targetIco -Force
}

if (Test-Path $sourceExe) {
    Copy-Item $sourceExe $targetExe -Force
    Write-Host "[+] Installed HotCorners.exe" -ForegroundColor Green
} elseif (Test-Path $sourceCS) {
    Write-Host "[*] Compiling HotCorners.cs using built-in Windows C# compiler..." -ForegroundColor Yellow
    $csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if (-not (Test-Path $csc)) {
        $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    }
    if (Test-Path $sourceIco) {
        & $csc /target:winexe /optimize+ "/win32icon:$sourceIco" /out:$targetExe $targetCS | Out-Null
    } else {
        & $csc /target:winexe /optimize+ /out:$targetExe $targetCS | Out-Null
    }
    Write-Host "[+] Compiled and installed HotCorners.exe" -ForegroundColor Green
} else {
    throw "Neither HotCorners.exe nor HotCorners.cs found in $sourceDir."
}

# 4. Create Startup Shortcut
$startupDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$shortcutPath = Join-Path $startupDir "HotCorners.lnk"
$wsh = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $targetExe
$shortcut.WorkingDirectory = $targetDir
$shortcut.Description = "Smart Hot Corners for Windows 11"
$shortcut.Save()
Write-Host "[+] Configured Windows Startup shortcut: $shortcutPath" -ForegroundColor Green

# 5. Register in HKCU Run Key (redundant backup to startup folder)
$runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Set-ItemProperty -Path $runPath -Name 'SmartHotCorners' -Value "`"$targetExe`""
Write-Host "[+] Registered in Windows Registry Run key." -ForegroundColor Green

# 6. Launch the Hot Corners daemon immediately outside current job context
$res = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine = "`"$targetExe`""}
if ($res.ReturnValue -eq 0) {
    Write-Host "[+] HotCorners launched successfully! (PID: $($res.ProcessId))" -ForegroundColor Green
} else {
    Start-Process -FilePath $targetExe -WorkingDirectory $targetDir
    Write-Host "[+] HotCorners launched via Start-Process." -ForegroundColor Green
}

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  🎉 Installation Complete! Hot Corners is now active!   " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Mappings:" -ForegroundColor White
Write-Host "  ↖️  Top-Left Corner    --> Task View (Win + Tab)" -ForegroundColor Cyan
Write-Host "  ↙️  Bottom-Left Corner --> Slide to Previous Desktop (Ctrl + Win + Left)" -ForegroundColor Cyan
Write-Host "  ↘️  Bottom-Right Corner--> Slide to Next Desktop (Ctrl + Win + Right)" -ForegroundColor Cyan
Write-Host ""
Write-Host "It will automatically start on every reboot." -ForegroundColor Yellow
Write-Host ""

