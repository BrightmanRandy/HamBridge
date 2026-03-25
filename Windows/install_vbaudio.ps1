#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Silently downloads and installs VB-CABLE Virtual Audio Device.

.DESCRIPTION
    Downloads the VB-CABLE driver package from vb-audio.com, extracts it to a
    temp folder, and runs the installer silently.  A reboot is recommended but
    not forced — audio devices appear after a sign-out/sign-in on most systems.

    Run from an elevated PowerShell prompt:
        .\install_vbaudio.ps1

.NOTES
    VB-CABLE is freeware / donationware by VB-Audio Software.
    https://vb-audio.com/Cable/
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# ── config ────────────────────────────────────────────────────────────────────
$DownloadUrl = 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip'
$ZipName     = 'VBCABLE_Driver_Pack43.zip'
$TempDir     = Join-Path $env:TEMP 'vbaudio_install'
$ZipPath     = Join-Path $TempDir $ZipName

# ── helpers ───────────────────────────────────────────────────────────────────
function Write-Step([string]$msg) {
    Write-Host "`n  $msg" -ForegroundColor Cyan
}

# ── main ──────────────────────────────────────────────────────────────────────
Write-Host "`n  HamBridge — VB-CABLE Installer" -ForegroundColor Yellow
Write-Host "  ─────────────────────────────────"

# 1. Create temp dir
Write-Step "Creating temp directory: $TempDir"
New-Item -ItemType Directory -Force -Path $TempDir | Out-Null

# 2. Download
Write-Step "Downloading VB-CABLE driver package..."
Invoke-WebRequest -Uri $DownloadUrl -OutFile $ZipPath -UseBasicParsing

# 3. Extract
Write-Step "Extracting..."
Expand-Archive -Path $ZipPath -DestinationPath $TempDir -Force

# 4. Find installer (x64 preferred)
$installer = Get-ChildItem -Path $TempDir -Filter 'VBCABLE_Setup_x64.exe' -Recurse |
             Select-Object -First 1
if (-not $installer) {
    $installer = Get-ChildItem -Path $TempDir -Filter 'VBCABLE_Setup.exe' -Recurse |
                 Select-Object -First 1
}
if (-not $installer) {
    throw "Could not locate VBCABLE_Setup*.exe in extracted archive."
}

Write-Step "Running installer: $($installer.FullName)"

# /S = silent install (NSIS flag used by VB-Audio)
$proc = Start-Process -FilePath $installer.FullName -ArgumentList '/S' `
                      -Wait -PassThru -Verb RunAs
if ($proc.ExitCode -ne 0) {
    throw "Installer exited with code $($proc.ExitCode)."
}

# 5. Clean up
Write-Step "Cleaning up temp files..."
Remove-Item -Recurse -Force -Path $TempDir -ErrorAction SilentlyContinue

Write-Host "`n  VB-CABLE installed successfully." -ForegroundColor Green
Write-Host "  A sign-out / sign-in (or reboot) is recommended to activate the device.`n"

# Optional: prompt to reboot
$reply = Read-Host "  Reboot now? [y/N]"
if ($reply -match '^[Yy]') {
    Restart-Computer -Force
}
