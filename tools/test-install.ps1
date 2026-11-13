<#
.SYNOPSIS
    Tests install.ps1 and uninstall.ps1 end to end in a sandbox: a temp folder for the program,
    Start Menu, "Send to" and user data, and a throwaway HKCU branch for the registry. The real
    Start Menu, Explorer menu and startup entries are not touched.

.DESCRIPTION
    Checks: files copied; Start Menu shortcut and Installed-apps entry created; an installed File
    Toolbox (the app's earlier name) is replaced and its startup entry carried over; integrations
    left by another copy are re-pointed while unrelated entries are untouched; the installed app
    starts and quits on --quit; a second install (update) succeeds; uninstall removes everything it
    should and keeps user data unless asked, and still leaves unrelated entries alone.
    Requires a published build (tools\publish.ps1).
#>
[CmdletBinding()]
param([string]$Published)

$ErrorActionPreference = 'Stop'
# (Windows PowerShell 5.1 does not set $PSScriptRoot inside param() defaults.)
if (-not $Published) { $Published = Join-Path $PSScriptRoot '..\artifacts\publish' }
$Published = (Resolve-Path $Published).Path
$sandbox = Join-Path $env:TEMP ("amirlap-install-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$reg = "HKCU:\Software\AmirLapTests\" + [IO.Path]::GetFileName($sandbox)
$common = @{
    StartMenuDir = Join-Path $sandbox 'StartMenu'
    UninstallKey = "$reg\Uninstall\AmirLap"
    ClassesRoot  = "$reg\Classes"
    RunKey       = "$reg\Run"
    SendToDir    = Join-Path $sandbox 'SendTo'
}
$legacy = @{
    LegacyInstallDir   = Join-Path $sandbox 'Programs\FileToolbox'
    LegacyUninstallKey = "$reg\Uninstall\FileToolbox"
}
$dest = Join-Path $sandbox 'Programs\AmirLap'
$exe = Join-Path $dest 'AmirLap.exe'
$failures = [Collections.Generic.List[string]]::new()
function Check([bool]$ok, [string]$what) { if ($ok) { Write-Host "  ok   $what" } else { Write-Host "  FAIL $what"; $failures.Add($what) } }

try {
    # An installed File Toolbox (the earlier name) with start-with-Windows switched on.
    $oldExe = Join-Path $legacy.LegacyInstallDir 'FileToolbox.exe'
    New-Item -ItemType Directory -Force $legacy.LegacyInstallDir | Out-Null
    Set-Content $oldExe 'stand-in for the old program'
    New-Item -Path $legacy.LegacyUninstallKey -Force | Out-Null
    Set-ItemProperty $legacy.LegacyUninstallKey -Name DisplayName -Value 'File Toolbox'
    New-Item -ItemType Directory -Force $common.StartMenuDir | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut((Join-Path $common.StartMenuDir 'File Toolbox.lnk')); $lnk.TargetPath = $oldExe; $lnk.Save()

    # Integrations as File Toolbox would have left them, plus entries that are not ours.
    New-Item "$reg\Classes\SystemFileAssociations\.pdf\shell\FileToolbox\command" -Force | Out-Null
    Set-Item "$reg\Classes\SystemFileAssociations\.pdf\shell\FileToolbox\command" -Value "`"$oldExe`" --open `"%1`""
    New-Item "$reg\Classes\SystemFileAssociations\.png\shell\FileToolbox\command" -Force | Out-Null
    Set-Item "$reg\Classes\SystemFileAssociations\.png\shell\FileToolbox\command" -Value 'notepad.exe "%1"'   # same verb name, another program
    New-Item "$reg\Classes\SystemFileAssociations\.pdf\shell\OtherApp\command" -Force | Out-Null
    New-Item "$reg\Run" -Force | Out-Null
    Set-ItemProperty "$reg\Run" -Name FileToolbox -Value "`"$oldExe`" --background"
    Set-ItemProperty "$reg\Run" -Name OtherApp -Value 'other.exe'
    New-Item -ItemType Directory -Force $common.SendToDir | Out-Null
    $lnk = $shell.CreateShortcut((Join-Path $common.SendToDir 'File Toolbox.lnk')); $lnk.TargetPath = $oldExe; $lnk.Save()

    Write-Host 'Install (replacing File Toolbox):'
    & (Join-Path $Published 'install.ps1') -Source (Join-Path $Published 'AmirLap') -Destination $dest @common @legacy -Quiet | Out-Host
    Check (Test-Path $exe) 'program files copied'
    Check (Test-Path (Join-Path $dest 'uninstall.ps1')) 'uninstaller copied'
    Check (Test-Path (Join-Path $common.StartMenuDir 'Amir Lap.lnk')) 'Start Menu shortcut'
    $entry = Get-ItemProperty $common.UninstallKey
    Check ($entry.DisplayName -eq 'Amir Lap' -and $entry.InstallLocation -eq $dest) 'Installed apps entry'
    Check (-not (Test-Path $legacy.LegacyInstallDir)) 'old File Toolbox program folder removed'
    Check (-not (Test-Path (Join-Path $common.StartMenuDir 'File Toolbox.lnk'))) 'old File Toolbox shortcut removed'
    Check (-not (Test-Path $legacy.LegacyUninstallKey)) 'old File Toolbox Installed apps entry removed'
    $pdfCommand = (Get-ItemProperty "$reg\Classes\SystemFileAssociations\.pdf\shell\FileToolbox\command").'(default)'
    Check ($pdfCommand -like "*$exe*") 'Explorer entry re-pointed at Amir Lap'
    Check ((Get-ItemProperty "$reg\Classes\SystemFileAssociations\.png\shell\FileToolbox\command").'(default)' -eq 'notepad.exe "%1"') 'foreign verb untouched'
    $run = Get-ItemProperty "$reg\Run"
    Check ($run.AmirLap -like "*$exe*" -and $null -eq $run.FileToolbox) 'start with Windows carried over to Amir Lap'
    Check ($shell.CreateShortcut((Join-Path $common.SendToDir 'File Toolbox.lnk')).TargetPath -eq $exe) 'Send to re-pointed'

    Write-Host 'Run the installed app:'
    if (Get-Process AmirLap -ErrorAction SilentlyContinue) { throw 'Another Amir Lap is running; quit it before this test.' }
    $env:AMIRLAP_DATA = Join-Path $sandbox 'data'
    $app = Start-Process $exe -ArgumentList '--background' -PassThru
    Start-Sleep -Seconds 6
    Check (-not $app.HasExited) 'installed app starts'
    Start-Process $exe -ArgumentList '--quit' -Wait
    Check ($app.WaitForExit(10000)) 'installed app quits on --quit'
    Remove-Item Env:\AMIRLAP_DATA

    Write-Host 'Install again (update):'
    & (Join-Path $Published 'install.ps1') -Source (Join-Path $Published 'AmirLap') -Destination $dest @common @legacy -Quiet | Out-Host
    Check (Test-Path $exe) 'update keeps a working installation'
    Check ((Get-ItemProperty "$reg\Run").AmirLap -like "*$exe*") 'start with Windows kept on update'

    Write-Host 'Uninstall (keep user data):'
    $userData = Join-Path $sandbox 'userdata'
    New-Item -ItemType Directory -Force $userData | Out-Null
    Set-Content (Join-Path $userData 'settings.json') '{}'
    & (Join-Path $dest 'uninstall.ps1') -InstallDir $dest -UserDataDir $userData @common | Out-Host
    Check (-not (Test-Path $dest)) 'program folder removed'
    Check (-not (Test-Path (Join-Path $common.StartMenuDir 'Amir Lap.lnk'))) 'Start Menu shortcut removed'
    Check (-not (Test-Path $common.UninstallKey)) 'Installed apps entry removed'
    Check (-not (Test-Path "$reg\Classes\SystemFileAssociations\.pdf\shell\FileToolbox")) 'Explorer entry removed'
    Check (Test-Path "$reg\Classes\SystemFileAssociations\.png\shell\FileToolbox") 'foreign verb kept'
    Check (Test-Path "$reg\Classes\SystemFileAssociations\.pdf\shell\OtherApp") 'other app entry kept'
    Check ($null -eq (Get-ItemProperty "$reg\Run").AmirLap -and (Get-ItemProperty "$reg\Run").OtherApp -eq 'other.exe') 'startup entry removed, others kept'
    Check (-not (Test-Path (Join-Path $common.SendToDir 'File Toolbox.lnk'))) 'Send to removed'
    Check (Test-Path (Join-Path $userData 'settings.json')) 'user data kept by default'

    Write-Host 'Uninstall with -RemoveUserData (nothing installed any more):'
    & (Join-Path $Published 'uninstall.ps1') -InstallDir $dest -UserDataDir $userData -RemoveUserData @common | Out-Host
    Check (-not (Test-Path $userData)) 'user data removed on request'
}
finally {
    if ($shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    Set-Location $env:TEMP
    Remove-Item "HKCU:\Software\AmirLapTests\$([IO.Path]::GetFileName($sandbox))" -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Get-ChildItem 'HKCU:\Software\AmirLapTests' -ErrorAction SilentlyContinue)) { Remove-Item 'HKCU:\Software\AmirLapTests' -Force -ErrorAction SilentlyContinue }
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) { Write-Host "$($failures.Count) check(s) failed."; exit 1 }
Write-Host 'All install/uninstall checks passed.'
