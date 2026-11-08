<#
.SYNOPSIS
    Removes Amir Lap for the current user.

.DESCRIPTION
    - Asks the running app to quit (running jobs are cancelled; finished results are kept).
    - Removes everything the app registered: the Explorer menu entries, the "Send to" shortcut,
      start with Windows, the Start Menu shortcut and the Installed apps entry. Only entries that
      point to Amir Lap are touched.
    - Deletes the installation folder.
    - Keeps your settings, recipes, brand styles, history and logs (%LOCALAPPDATA%\AmirLap)
      unless -RemoveUserData is given.
    Files the tools produced (next to your originals, or in Pictures\File Toolbox) are never touched.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File uninstall.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File uninstall.ps1 -RemoveUserData
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [switch]$RemoveUserData,
    # The following exist so the uninstaller can be tested without touching the real Start Menu and registry.
    [string]$StartMenuDir = [Environment]::GetFolderPath('Programs'),
    [string]$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AmirLap',
    [string]$ClassesRoot = 'HKCU:\Software\Classes',
    [string]$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    [string]$SendToDir = [Environment]::GetFolderPath('SendTo'),
    [string]$UserDataDir = (Join-Path $env:LOCALAPPDATA 'AmirLap')
)

$ErrorActionPreference = 'Stop'
if (-not $InstallDir) {
    $InstallDir = if (Test-Path (Join-Path $PSScriptRoot 'AmirLap.exe')) { $PSScriptRoot }
                  else { (Get-ItemProperty $UninstallKey -ErrorAction SilentlyContinue).InstallLocation }
}
if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\AmirLap' }
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$exe = Join-Path $InstallDir 'AmirLap.exe'

# 1. Quit the running app.
$running = @(Get-Process AmirLap -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
if ($running.Count -gt 0) {
    Write-Host 'Asking Amir Lap to quit...'
    Start-Process -FilePath $exe -ArgumentList '--quit' -Wait
    $deadline = (Get-Date).AddSeconds(15)
    while (($running | Where-Object { -not $_.HasExited }) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    foreach ($p in $running | Where-Object { -not $_.HasExited }) {
        Write-Warning 'Amir Lap did not quit in time; closing it.'
        $p.Kill()
        $p.WaitForExit(5000) | Out-Null
    }
}

# 2. Integrations (opt-in features), only where they point to Amir Lap. File Toolbox's Explorer
#    entry and "Send to" shortcut keep their names; they now start AmirLap.exe.
$removed = 0
foreach ($verb in Get-ChildItem "$ClassesRoot\SystemFileAssociations\*\shell\FileToolbox" -ErrorAction SilentlyContinue) {
    $command = (Get-ItemProperty (Join-Path $verb.PSPath 'command') -ErrorAction SilentlyContinue).'(default)'
    if ($command -match 'AmirLap\.exe') { Remove-Item $verb.PSPath -Recurse -Force; $removed++ }
}
$runValue = (Get-ItemProperty $RunKey -Name AmirLap -ErrorAction SilentlyContinue).AmirLap
if ($runValue -match 'AmirLap\.exe') { Remove-ItemProperty $RunKey -Name AmirLap; $removed++ }
$sendTo = Join-Path $SendToDir 'File Toolbox.lnk'
if (Test-Path $sendTo) {
    $shell = New-Object -ComObject WScript.Shell
    try { $target = $shell.CreateShortcut($sendTo).TargetPath } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    if ($target -match 'AmirLap\.exe') { Remove-Item $sendTo -Force; $removed++ }
}
if ($removed -gt 0) { Write-Host "Removed $removed Explorer/startup integration(s)." }

# 3. Shortcut and Installed apps entry.
$shortcut = Join-Path $StartMenuDir 'Amir Lap.lnk'
if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
if (Test-Path $UninstallKey) { Remove-Item $UninstallKey -Recurse -Force }

# 4. The installation folder (only if it really is one).
if (Test-Path $exe) {
    Set-Location $env:TEMP   # this script may live in the folder being removed
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "Removed $InstallDir."
}
elseif (Test-Path $InstallDir) {
    Write-Warning "'$InstallDir' does not contain AmirLap.exe; it was left alone."
}

# 5. User data.
if ($RemoveUserData) {
    if (Test-Path $UserDataDir) { Remove-Item $UserDataDir -Recurse -Force; Write-Host "Removed settings and history ($UserDataDir)." }
}
elseif (Test-Path $UserDataDir) {
    Write-Host "Your settings, recipes and history were kept in $UserDataDir (run with -RemoveUserData to delete them)."
}
Write-Host 'Amir Lap has been uninstalled.'
