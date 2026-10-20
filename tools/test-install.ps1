<#
.SYNOPSIS
    Tests install.ps1 and uninstall.ps1 end to end in a sandbox: a temp folder for the program,
    Start Menu, "Send to" and user data, and a throwaway HKCU branch for the registry. The real
    Start Menu, Explorer menu and startup entries are not touched.

.DESCRIPTION
    Checks: files copied; Start Menu shortcut and Installed-apps entry created; integrations left by
    another copy are re-pointed while unrelated entries are untouched; the installed app starts and
    quits on --quit; a second install (update) succeeds; uninstall removes everything it should and
    keeps user data unless asked, and still leaves unrelated entries alone.
    Requires a published build (tools\publish.ps1).
#>
[CmdletBinding()]
param([string]$Published)

$ErrorActionPreference = 'Stop'
# (Windows PowerShell 5.1 does not set $PSScriptRoot inside param() defaults.)
if (-not $Published) { $Published = Join-Path $PSScriptRoot '..\artifacts\publish' }
$Published = (Resolve-Path $Published).Path
$sandbox = Join-Path $env:TEMP ("ftb-install-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$reg = "HKCU:\Software\FileToolboxTests\" + [IO.Path]::GetFileName($sandbox)
$common = @{
    StartMenuDir = Join-Path $sandbox 'StartMenu'
    UninstallKey = "$reg\Uninstall\FileToolbox"
    ClassesRoot  = "$reg\Classes"
    RunKey       = "$reg\Run"
    SendToDir    = Join-Path $sandbox 'SendTo'
}
$dest = Join-Path $sandbox 'Programs\FileToolbox'
$exe = Join-Path $dest 'FileToolbox.exe'
$failures = [Collections.Generic.List[string]]::new()
function Check([bool]$ok, [string]$what) { if ($ok) { Write-Host "  ok   $what" } else { Write-Host "  FAIL $what"; $failures.Add($what) } }

try {
    # Integrations as an earlier (development) copy would have left them, plus entries that are not ours.
    $old = 'C:\old build\FileToolbox.exe'
    New-Item "$reg\Classes\SystemFileAssociations\.pdf\shell\FileToolbox\command" -Force | Out-Null
    Set-Item "$reg\Classes\SystemFileAssociations\.pdf\shell\FileToolbox\command" -Value "`"$old`" --open `"%1`""
    New-Item "$reg\Classes\SystemFileAssociations\.png\shell\FileToolbox\command" -Force | Out-Null
    Set-Item "$reg\Classes\SystemFileAssociations\.png\shell\FileToolbox\command" -Value 'notepad.exe "%1"'   # same verb name, another program
    New-Item "$reg\Classes\SystemFileAssociations\.pdf\shell\OtherApp\command" -Force | Out-Null
    New-Item "$reg\Run" -Force | Out-Null
    Set-ItemProperty "$reg\Run" -Name FileToolbox -Value "`"$old`" --background"
    Set-ItemProperty "$reg\Run" -Name OtherApp -Value 'other.exe'
    New-Item -ItemType Directory -Force $common.SendToDir | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut((Join-Path $common.SendToDir 'File Toolbox.lnk')); $lnk.TargetPath = $old; $lnk.Save()

    Write-Host 'Install:'
    & (Join-Path $Published 'install.ps1') -Source (Join-Path $Published 'FileToolbox') -Destination $dest @common | Out-Host
    Check (Test-Path $exe) 'program files copied'
    Check (Test-Path (Join-Path $dest 'uninstall.ps1')) 'uninstaller copied'
    Check (Test-Path (Join-Path $common.StartMenuDir 'File Toolbox.lnk')) 'Start Menu shortcut'
    $entry = Get-ItemProperty $common.UninstallKey
    Check ($entry.DisplayName -eq 'File Toolbox' -and $entry.InstallLocation -eq $dest) 'Installed apps entry'
    $pdfCommand = (Get-ItemProperty "$reg\Classes\SystemFileAssociations\.pdf\shell\FileToolbox\command").'(default)'
    Check ($pdfCommand -like "*$exe*") 'old Explorer entry re-pointed'
    Check ((Get-ItemProperty "$reg\Classes\SystemFileAssociations\.png\shell\FileToolbox\command").'(default)' -eq 'notepad.exe "%1"') 'foreign verb untouched'
    Check ((Get-ItemProperty "$reg\Run").FileToolbox -like "*$exe*") 'startup entry re-pointed'
    Check ($shell.CreateShortcut((Join-Path $common.SendToDir 'File Toolbox.lnk')).TargetPath -eq $exe) 'Send to re-pointed'

    Write-Host 'Run the installed app:'
    if (Get-Process FileToolbox -ErrorAction SilentlyContinue) { throw 'Another File Toolbox is running; quit it before this test.' }
    $env:FILETOOLBOX_DATA = Join-Path $sandbox 'data'
    $app = Start-Process $exe -ArgumentList '--background' -PassThru
    Start-Sleep -Seconds 6
    Check (-not $app.HasExited) 'installed app starts'
    Start-Process $exe -ArgumentList '--quit' -Wait
    Check ($app.WaitForExit(10000)) 'installed app quits on --quit'
    Remove-Item Env:\FILETOOLBOX_DATA

    Write-Host 'Install again (update):'
    & (Join-Path $Published 'install.ps1') -Source (Join-Path $Published 'FileToolbox') -Destination $dest @common | Out-Host
    Check (Test-Path $exe) 'update keeps a working installation'

    Write-Host 'Uninstall (keep user data):'
    $userData = Join-Path $sandbox 'userdata'
    New-Item -ItemType Directory -Force $userData | Out-Null
    Set-Content (Join-Path $userData 'settings.json') '{}'
    & (Join-Path $dest 'uninstall.ps1') -InstallDir $dest -UserDataDir $userData @common | Out-Host
    Check (-not (Test-Path $dest)) 'program folder removed'
    Check (-not (Test-Path (Join-Path $common.StartMenuDir 'File Toolbox.lnk'))) 'Start Menu shortcut removed'
    Check (-not (Test-Path $common.UninstallKey)) 'Installed apps entry removed'
    Check (-not (Test-Path "$reg\Classes\SystemFileAssociations\.pdf\shell\FileToolbox")) 'Explorer entry removed'
    Check (Test-Path "$reg\Classes\SystemFileAssociations\.png\shell\FileToolbox") 'foreign verb kept'
    Check (Test-Path "$reg\Classes\SystemFileAssociations\.pdf\shell\OtherApp") 'other app entry kept'
    Check ($null -eq (Get-ItemProperty "$reg\Run").FileToolbox -and (Get-ItemProperty "$reg\Run").OtherApp -eq 'other.exe') 'startup entry removed, others kept'
    Check (-not (Test-Path (Join-Path $common.SendToDir 'File Toolbox.lnk'))) 'Send to removed'
    Check (Test-Path (Join-Path $userData 'settings.json')) 'user data kept by default'

    Write-Host 'Uninstall with -RemoveUserData (nothing installed any more):'
    & (Join-Path $Published 'uninstall.ps1') -InstallDir $dest -UserDataDir $userData -RemoveUserData @common | Out-Host
    Check (-not (Test-Path $userData)) 'user data removed on request'
}
finally {
    if ($shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    Set-Location $env:TEMP
    Remove-Item "HKCU:\Software\FileToolboxTests\$([IO.Path]::GetFileName($sandbox))" -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Get-ChildItem 'HKCU:\Software\FileToolboxTests' -ErrorAction SilentlyContinue)) { Remove-Item 'HKCU:\Software\FileToolboxTests' -Force -ErrorAction SilentlyContinue }
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) { Write-Host "$($failures.Count) check(s) failed."; exit 1 }
Write-Host 'All install/uninstall checks passed.'
