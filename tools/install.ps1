<#
.SYNOPSIS
    Installs File Toolbox for the current user (no administrator rights needed).

.DESCRIPTION
    - Copies the published FileToolbox folder to %LOCALAPPDATA%\Programs\FileToolbox
      (a running copy is asked to quit first; an update keeps your settings).
    - Adds a Start Menu shortcut and an entry in Settings > Apps > Installed apps.
    - If the Explorer menu, "Send to" or start-with-Windows were switched on for another copy of
      File Toolbox, they are pointed at the installed one.
    Nothing is switched on that was not on before: integrations are opt-in in File Toolbox's Settings.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1 -Launch
#>
[CmdletBinding()]
param(
    # Folder holding FileToolbox.exe (the published app). Default: the FileToolbox folder next to this script.
    [string]$Source,
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\FileToolbox'),
    [switch]$Launch,
    # The following exist so the installer can be tested without touching the real Start Menu and registry.
    [string]$StartMenuDir = [Environment]::GetFolderPath('Programs'),
    [string]$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FileToolbox',
    [string]$ClassesRoot = 'HKCU:\Software\Classes',
    [string]$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    [string]$SendToDir = [Environment]::GetFolderPath('SendTo')
)

$ErrorActionPreference = 'Stop'
# (Windows PowerShell 5.1 does not set $PSScriptRoot inside param() defaults.)
if (-not $Source) { $Source = Join-Path $PSScriptRoot 'FileToolbox' }
$sourceExe = Join-Path $Source 'FileToolbox.exe'
if (-not (Test-Path $sourceExe)) { throw "FileToolbox.exe was not found in '$Source'. Run tools\publish.ps1 first, or pass -Source." }
$Destination = [IO.Path]::GetFullPath($Destination)
$exe = Join-Path $Destination 'FileToolbox.exe'

# Mirroring deletes files the source does not have, so only ever mirror into an empty folder or an earlier install.
if ((Test-Path $Destination) -and (Get-ChildItem $Destination -Force | Select-Object -First 1) -and -not (Test-Path $exe)) {
    throw "'$Destination' already contains other files and no File Toolbox installation. Choose an empty folder."
}

function Stop-RunningToolbox {
    $running = @(Get-Process FileToolbox -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }
    Write-Host 'Asking the running File Toolbox to quit...'
    $path = $running[0].Path
    if ($path) { Start-Process -FilePath $path -ArgumentList '--quit' -Wait }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Process FileToolbox -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    if (Get-Process FileToolbox -ErrorAction SilentlyContinue) {
        throw 'File Toolbox is still running. Quit it from its notification-area icon, then run the installer again.'
    }
}

Stop-RunningToolbox

Write-Host "Copying to $Destination ..."
New-Item -ItemType Directory -Force $Destination | Out-Null
& robocopy $Source $Destination /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying failed (robocopy exit code $LASTEXITCODE)." }
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $Destination -Force -ErrorAction SilentlyContinue
$uninstallScript = Join-Path $Destination 'uninstall.ps1'
if (-not (Test-Path $uninstallScript)) { throw "uninstall.ps1 is missing next to the installer." }

# Start Menu shortcut.
New-Item -ItemType Directory -Force $StartMenuDir | Out-Null
$shell = New-Object -ComObject WScript.Shell
try {
    $link = $shell.CreateShortcut((Join-Path $StartMenuDir 'File Toolbox.lnk'))
    $link.TargetPath = $exe
    $link.WorkingDirectory = $Destination
    $link.IconLocation = "$exe,0"
    $link.Description = 'Convert and edit files locally: drag a file and tap Shift'
    $link.Save()

    # Point integrations that another copy registered at this installation (only entries of File Toolbox).
    $updated = 0
    if (Test-Path "$ClassesRoot\SystemFileAssociations") {
        foreach ($verb in Get-ChildItem "$ClassesRoot\SystemFileAssociations\*\shell\FileToolbox" -ErrorAction SilentlyContinue) {
            $commandKey = Join-Path $verb.PSPath 'command'
            $command = (Get-ItemProperty $commandKey -ErrorAction SilentlyContinue).'(default)'
            if ($command -and $command -match 'FileToolbox\.exe' -and $command -notlike "*$exe*") {
                Set-Item $commandKey -Value "`"$exe`" --open `"%1`""
                Set-ItemProperty $verb.PSPath -Name Icon -Value "`"$exe`",0"
                $updated++
            }
        }
    }
    $runValue = (Get-ItemProperty $RunKey -Name FileToolbox -ErrorAction SilentlyContinue).FileToolbox
    if ($runValue -and $runValue -notlike "*$exe*") { Set-ItemProperty $RunKey -Name FileToolbox -Value "`"$exe`" --background"; $updated++ }
    $sendTo = Join-Path $SendToDir 'File Toolbox.lnk'
    if (Test-Path $sendTo) {
        $existing = $shell.CreateShortcut($sendTo)
        if ($existing.TargetPath -ne $exe) { $existing.TargetPath = $exe; $existing.IconLocation = "$exe,0"; $existing.Save(); $updated++ }
    }
    if ($updated -gt 0) { Write-Host "Pointed $updated existing integration(s) at the installed copy." }
}
finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
}

# Settings > Apps > Installed apps.
$version = (Get-Item $exe).VersionInfo.ProductVersion.Split('+')[0]
$sizeKb = [int]((Get-ChildItem $Destination -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
New-Item -Path $UninstallKey -Force | Out-Null
$values = @{
    DisplayName     = 'File Toolbox'
    DisplayVersion  = $version
    Publisher       = 'File Toolbox'
    InstallLocation = $Destination
    DisplayIcon     = "$exe,0"
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`""
    InstallDate     = (Get-Date -Format 'yyyyMMdd')
}
foreach ($name in $values.Keys) { Set-ItemProperty $UninstallKey -Name $name -Value $values[$name] }
Set-ItemProperty $UninstallKey -Name NoModify -Value 1 -Type DWord
Set-ItemProperty $UninstallKey -Name NoRepair -Value 1 -Type DWord
Set-ItemProperty $UninstallKey -Name EstimatedSize -Value $sizeKb -Type DWord

Write-Host "File Toolbox $version is installed in $Destination."
Write-Host 'Start it from the Start Menu. It lives in the notification area: drag a file and tap Shift.'
if ($Launch) { Start-Process -FilePath $exe }
