<#
.SYNOPSIS
    Installs Amir Lap for the current user (no administrator rights needed).

.DESCRIPTION
    - Copies the published AmirLap folder to %LOCALAPPDATA%\Programs\AmirLap
      (a running copy is asked to quit first; an update keeps your settings).
    - Adds a Start Menu shortcut and an entry in Settings > Apps > Installed apps.
    - Replaces File Toolbox (this app before it became a tool inside Amir Lap) if it is installed:
      its program folder, Start Menu shortcut and Installed apps entry are removed. Its settings,
      history and recipes stay where they are; Amir Lap copies them over the first time it starts.
    - If the Explorer menu, "Send to" or start-with-Windows were switched on for another copy
      (File Toolbox included), they are pointed at the installed Amir Lap.
    Nothing is switched on that was not on before: integrations are opt-in in Amir Lap's settings.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1 -Launch
#>
[CmdletBinding()]
param(
    # Folder holding AmirLap.exe (the published app). Default: the AmirLap folder next to this script.
    [string]$Source,
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\AmirLap'),
    [switch]$Launch,
    # The following exist so the installer can be tested without touching the real Start Menu and registry.
    [string]$StartMenuDir = [Environment]::GetFolderPath('Programs'),
    [string]$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AmirLap',
    [string]$ClassesRoot = 'HKCU:\Software\Classes',
    [string]$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    [string]$SendToDir = [Environment]::GetFolderPath('SendTo'),
    # Where File Toolbox was installed before the rename (replaced when found).
    [string]$LegacyInstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\FileToolbox'),
    [string]$LegacyUninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FileToolbox'
)

$ErrorActionPreference = 'Stop'
# (Windows PowerShell 5.1 does not set $PSScriptRoot inside param() defaults.)
if (-not $Source) { $Source = Join-Path $PSScriptRoot 'AmirLap' }
$sourceExe = Join-Path $Source 'AmirLap.exe'
if (-not (Test-Path $sourceExe)) { throw "AmirLap.exe was not found in '$Source'. Run tools\publish.ps1 first, or pass -Source." }
$Destination = [IO.Path]::GetFullPath($Destination)
$exe = Join-Path $Destination 'AmirLap.exe'
$ours = '(AmirLap|FileToolbox)\.exe'   # entries made by this app under either name

# Mirroring deletes files the source does not have, so only ever mirror into an empty folder or an earlier install.
if ((Test-Path $Destination) -and (Get-ChildItem $Destination -Force | Select-Object -First 1) -and -not (Test-Path $exe)) {
    throw "'$Destination' already contains other files and no Amir Lap installation. Choose an empty folder."
}

function Stop-Running([string]$processName, [string]$label) {
    $running = @(Get-Process $processName -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }
    Write-Host "Asking the running $label to quit..."
    $path = $running[0].Path
    if ($path) { Start-Process -FilePath $path -ArgumentList '--quit' -Wait }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Process $processName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    if (Get-Process $processName -ErrorAction SilentlyContinue) {
        throw "$label is still running. Quit it from its notification-area icon, then run the installer again."
    }
}

Stop-Running 'AmirLap' 'Amir Lap'
Stop-Running 'FileToolbox' 'File Toolbox'

Write-Host "Copying to $Destination ..."
New-Item -ItemType Directory -Force $Destination | Out-Null
& robocopy $Source $Destination /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying failed (robocopy exit code $LASTEXITCODE)." }
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $Destination -Force -ErrorAction SilentlyContinue
$uninstallScript = Join-Path $Destination 'uninstall.ps1'
if (-not (Test-Path $uninstallScript)) { throw "uninstall.ps1 is missing next to the installer." }

New-Item -ItemType Directory -Force $StartMenuDir | Out-Null
$shell = New-Object -ComObject WScript.Shell
try {
    # Replace File Toolbox: its program files, shortcut and Installed apps entry (never its user data).
    $replaced = @()
    $LegacyInstallDir = [IO.Path]::GetFullPath($LegacyInstallDir)
    if ($LegacyInstallDir -ne $Destination -and (Test-Path (Join-Path $LegacyInstallDir 'FileToolbox.exe'))) {
        Remove-Item $LegacyInstallDir -Recurse -Force
        $replaced += 'program folder'
    }
    $legacyLink = Join-Path $StartMenuDir 'File Toolbox.lnk'
    if ((Test-Path $legacyLink) -and $shell.CreateShortcut($legacyLink).TargetPath -match 'FileToolbox\.exe') {
        Remove-Item $legacyLink -Force
        $replaced += 'Start Menu shortcut'
    }
    if (Test-Path $LegacyUninstallKey) {
        Remove-Item $LegacyUninstallKey -Recurse -Force
        $replaced += 'Installed apps entry'
    }
    if ($replaced.Count -gt 0) { Write-Host "Replaced File Toolbox ($($replaced -join ', ')). Its settings and recipes are kept and copied into Amir Lap when it starts." }

    # Start Menu shortcut.
    $link = $shell.CreateShortcut((Join-Path $StartMenuDir 'Amir Lap.lnk'))
    $link.TargetPath = $exe
    $link.WorkingDirectory = $Destination
    $link.IconLocation = "$exe,0"
    $link.Description = 'Your desktop tools in one place: File Toolbox and more'
    $link.Save()

    # Point integrations that another copy registered at this installation (only entries of this app).
    $updated = 0
    if (Test-Path "$ClassesRoot\SystemFileAssociations") {
        foreach ($verb in Get-ChildItem "$ClassesRoot\SystemFileAssociations\*\shell\FileToolbox" -ErrorAction SilentlyContinue) {
            $commandKey = Join-Path $verb.PSPath 'command'
            $command = (Get-ItemProperty $commandKey -ErrorAction SilentlyContinue).'(default)'
            if ($command -and $command -match $ours -and $command -notlike "*$exe*") {
                Set-Item $commandKey -Value "`"$exe`" --open `"%1`""
                Set-ItemProperty $verb.PSPath -Name Icon -Value "`"$exe`",0"
                $updated++
            }
        }
    }
    # Start with Windows: File Toolbox used the value name "FileToolbox"; Amir Lap uses "AmirLap".
    $legacyRun = (Get-ItemProperty $RunKey -Name FileToolbox -ErrorAction SilentlyContinue).FileToolbox
    $currentRun = (Get-ItemProperty $RunKey -Name AmirLap -ErrorAction SilentlyContinue).AmirLap
    if (($legacyRun -match $ours) -or ($currentRun -and $currentRun -notlike "*$exe*")) {
        Set-ItemProperty $RunKey -Name AmirLap -Value "`"$exe`" --background"
        $updated++
    }
    if ($legacyRun -match $ours) { Remove-ItemProperty $RunKey -Name FileToolbox }
    $sendTo = Join-Path $SendToDir 'File Toolbox.lnk'
    if (Test-Path $sendTo) {
        $existing = $shell.CreateShortcut($sendTo)
        if ($existing.TargetPath -ne $exe -and $existing.TargetPath -match $ours) { $existing.TargetPath = $exe; $existing.IconLocation = "$exe,0"; $existing.Save(); $updated++ }
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
    DisplayName     = 'Amir Lap'
    DisplayVersion  = $version
    Publisher       = 'Amir Lap'
    InstallLocation = $Destination
    DisplayIcon     = "$exe,0"
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`""
    InstallDate     = (Get-Date -Format 'yyyyMMdd')
}
foreach ($name in $values.Keys) { Set-ItemProperty $UninstallKey -Name $name -Value $values[$name] }
Set-ItemProperty $UninstallKey -Name NoModify -Value 1 -Type DWord
Set-ItemProperty $UninstallKey -Name NoRepair -Value 1 -Type DWord
Set-ItemProperty $UninstallKey -Name EstimatedSize -Value $sizeKb -Type DWord

Write-Host "Amir Lap $version is installed in $Destination."
Write-Host 'Start it from the Start Menu. It lives in the notification area; right-click its icon for every tool.'
if ($Launch) { Start-Process -FilePath $exe }
