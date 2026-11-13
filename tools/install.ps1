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

    Run from Explorer ("Run with PowerShell"), it shows each step as it goes, starts Amir Lap when
    it is done (which then says it is installed, where it lives and what to try), and closes after a
    short countdown. If something goes wrong, the window stays open with the reason. Before this,
    the window flashed and closed with nothing else on screen, and people ran it again and again.

.EXAMPLE
    Right-click install.ps1 > Run with PowerShell
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1 -Quiet -Launch
    (for scripts: plain output, no countdown, no welcome window)
#>
[CmdletBinding()]
param(
    # Folder holding AmirLap.exe (the published app). Default: the AmirLap folder next to this script.
    [string]$Source,
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\AmirLap'),
    # With -Quiet: start Amir Lap when done. (Without -Quiet it is always started, with its welcome.)
    [switch]$Launch,
    # For scripts and tests: plain output, errors thrown, no countdown, no welcome window.
    [switch]$Quiet,
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
$friendly = -not $Quiet

# ---- what the person running it sees -------------------------------------------------------------
# (ASCII only: Windows PowerShell 5.1 reads a script without a byte-order mark as the local code page.)

function Say([string]$text, [string]$color = '') {
    if ($friendly -and $color) { Write-Host $text -ForegroundColor $color } else { Write-Host $text }
}

# A numbered step: "  [2/4] Copying the app ...    done" on one line when friendly.
$script:stepCount = 4
$script:stepNumber = 0
function Step([string]$text) {
    $script:stepNumber++
    if ($friendly) { Write-Host ("  [{0}/{1}] {2} " -f $script:stepNumber, $script:stepCount, $text).PadRight(52) -NoNewline }
    else { Write-Host "$text ..." }
}
function Done([string]$note = 'done') {
    if ($friendly) { Write-Host $note -ForegroundColor Green }
}

function Wait-ThenClose([int]$seconds) {
    try {
        for ($left = $seconds; $left -gt 0; $left--) {
            Write-Host ("`r  This window closes in {0} s (press any key to close it now). " -f $left) -NoNewline -ForegroundColor DarkGray
            for ($i = 0; $i -lt 10; $i++) {
                if ([Console]::KeyAvailable) { [void][Console]::ReadKey($true); Write-Host ''; return }
                Start-Sleep -Milliseconds 100
            }
        }
        Write-Host ''
    }
    catch {
        Start-Sleep -Seconds 3 # no console to read keys from (an editor's terminal): just a moment to read
    }
}

function Stop-Running([string]$processName, [string]$label) {
    $running = @(Get-Process $processName -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return $false }
    if (-not $friendly) { Write-Host "Asking the running $label to quit..." }
    $path = $running[0].Path
    if ($path) { Start-Process -FilePath $path -ArgumentList '--quit' -Wait }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Process $processName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    if (Get-Process $processName -ErrorAction SilentlyContinue) {
        throw "$label is still running. Quit it from its notification-area icon (right-click it, then Quit), then run the installer again."
    }
    return $true
}

function Install-AmirLap {
    # (Windows PowerShell 5.1 does not set $PSScriptRoot inside param() defaults.)
    if (-not $Source) { $script:Source = Join-Path $PSScriptRoot 'AmirLap' }
    $sourceExe = Join-Path $Source 'AmirLap.exe'
    if (-not (Test-Path $sourceExe)) {
        throw "AmirLap.exe was not found in '$Source'. Extract the whole zip first (right-click it, then Extract All), and run install.ps1 from the extracted folder."
    }
    $dest = [IO.Path]::GetFullPath($Destination)
    $exe = Join-Path $dest 'AmirLap.exe'
    $ours = '(AmirLap|FileToolbox)\.exe'   # entries made by this app under either name
    $newVersion = (Get-Item $sourceExe).VersionInfo.ProductVersion.Split('+')[0]

    # Mirroring deletes files the source does not have, so only ever mirror into an empty folder or an earlier install.
    if ((Test-Path $dest) -and (Get-ChildItem $dest -Force | Select-Object -First 1) -and -not (Test-Path $exe)) {
        throw "'$dest' already contains other files and no Amir Lap installation. Choose an empty folder."
    }
    $wasInstalled = Test-Path $exe

    if ($friendly) {
        $Host.UI.RawUI.WindowTitle = 'Installing Amir Lap'
        Write-Host ''
        Write-Host '  Amir Lap' -ForegroundColor Cyan -NoNewline
        Write-Host ("  {0} {1}, for you only (no administrator rights needed)" -f $(if ($wasInstalled) { 'updating to' } else { 'installing' }), $newVersion)
        Write-Host ''
    }

    Step 'Closing Amir Lap if it is running'
    $closed = Stop-Running 'AmirLap' 'Amir Lap'
    $closedLegacy = Stop-Running 'FileToolbox' 'File Toolbox'
    Done $(if ($closed -or $closedLegacy) { 'done' } else { 'not running' })

    $sizeMb = [int]((Get-ChildItem $Source -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
    Step "Copying the app ($sizeMb MB)"
    New-Item -ItemType Directory -Force $dest | Out-Null
    & robocopy $Source $dest /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying failed (robocopy exit code $LASTEXITCODE). Is the disk full, or is a file in '$dest' open?" }
    Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $dest -Force -ErrorAction SilentlyContinue
    $uninstallScript = Join-Path $dest 'uninstall.ps1'
    if (-not (Test-Path $uninstallScript)) { throw "uninstall.ps1 is missing next to the installer. Extract the whole zip and try again." }
    # The copies are now installed program files: the "downloaded from the internet" mark that
    # the extracted zip carried is not kept on them (as with any installer's files).
    Get-ChildItem $dest -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
    Done

    Step 'Adding it to the Start Menu'
    New-Item -ItemType Directory -Force $StartMenuDir | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $notes = @()
    try {
        # Replace File Toolbox: its program files, shortcut and Installed apps entry (never its user data).
        $replaced = @()
        $legacyDir = [IO.Path]::GetFullPath($LegacyInstallDir)
        if ($legacyDir -ne $dest -and (Test-Path (Join-Path $legacyDir 'FileToolbox.exe'))) {
            Remove-Item $legacyDir -Recurse -Force
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
        if ($replaced.Count -gt 0) { $notes += "Replaced File Toolbox ($($replaced -join ', ')). Its settings and recipes are kept and copied into Amir Lap when it starts." }

        # Start Menu shortcut.
        $link = $shell.CreateShortcut((Join-Path $StartMenuDir 'Amir Lap.lnk'))
        $link.TargetPath = $exe
        $link.WorkingDirectory = $dest
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
        if ($updated -gt 0) { $notes += "Pointed $updated existing integration(s) at the installed copy." }
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
    Done

    # Settings > Apps > Installed apps.
    Step 'Adding it to Installed apps'
    $version = (Get-Item $exe).VersionInfo.ProductVersion.Split('+')[0]
    $sizeKb = [int]((Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    New-Item -Path $UninstallKey -Force | Out-Null
    $values = @{
        DisplayName     = 'Amir Lap'
        DisplayVersion  = $version
        Publisher       = 'Amir Lap'
        InstallLocation = $dest
        DisplayIcon     = "$exe,0"
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`""
        InstallDate     = (Get-Date -Format 'yyyyMMdd')
    }
    foreach ($name in $values.Keys) { Set-ItemProperty $UninstallKey -Name $name -Value $values[$name] }
    Set-ItemProperty $UninstallKey -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty $UninstallKey -Name NoRepair -Value 1 -Type DWord
    Set-ItemProperty $UninstallKey -Name EstimatedSize -Value $sizeKb -Type DWord
    Done

    foreach ($note in $notes) { Say "  $note" 'DarkGray' }

    if (-not $friendly) {
        Write-Host "Amir Lap $version is installed in $dest."
        Write-Host 'Start it from the Start Menu. It lives in the notification area; right-click its icon for every tool.'
        if ($Launch) { Start-Process -FilePath $exe }
        return
    }

    # Started with a word for it, so it opens its welcome: installed (or updated), where it lives, what to try.
    Start-Process -FilePath $exe -ArgumentList $(if ($wasInstalled) { '--updated' } else { '--installed' })
    Write-Host ''
    Write-Host ("  Amir Lap {0} is {1}." -f $version, $(if ($wasInstalled) { 'updated' } else { 'installed' })) -ForegroundColor Green
    Write-Host '  It is starting now and opens a welcome window. Afterwards it lives in the'
    Write-Host '  notification area, by the clock (bottom right), and in the Start Menu.'
    Write-Host ''
    Wait-ThenClose 12
}

try {
    Install-AmirLap
}
catch {
    if (-not $friendly) { throw }
    Write-Host ''
    Write-Host ''
    Write-Host '  Amir Lap could not be installed.' -ForegroundColor Red
    Write-Host ("  " + $_.Exception.Message) -ForegroundColor Red
    Write-Host ''
    Write-Host '  Fix the above and run install.ps1 again.'
    Write-Host '  Press Enter to close this window.'
    [void](Read-Host)
    exit 1
}
