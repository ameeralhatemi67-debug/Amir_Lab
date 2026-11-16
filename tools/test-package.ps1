<#
.SYNOPSIS
    Tests the release zip the way a friend meets it: downloaded (every file carries the "from the
    internet" mark), extracted, and installed with a double-click on "Install Amir Lap.cmd".

.DESCRIPTION
    Checks the zip itself (standard "/" paths, the files people need at the top), then extracts it to a
    temp folder, marks every file as downloaded, runs the .cmd launcher (the real path a double-click
    takes) with the program folder, Start Menu and registry pointed at a sandbox, and checks that the
    installed copy is unblocked, starts, and runs all four tools. Finally it uninstalls.
    The real Start Menu, registry and Amir Lap data are not touched.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\test-package.ps1 -Zip artifacts\publish\AmirLap-0.8.0-win-x64.zip
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Zip)

$ErrorActionPreference = 'Stop'
$Zip = (Resolve-Path $Zip).Path
$sandbox = Join-Path $env:TEMP ("amirlap-package-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$reg = "HKCU:\Software\AmirLapTests\" + [IO.Path]::GetFileName($sandbox)
$extract = Join-Path $sandbox 'Downloads\Amir Lap'   # a folder with a space, like a real one
$dest = Join-Path $sandbox 'Programs\AmirLap'
$data = Join-Path $sandbox 'data'
$failures = [Collections.Generic.List[string]]::new()
function Check([bool]$ok, [string]$what) { if ($ok) { Write-Host "  ok   $what" } else { Write-Host "  FAIL $what"; $failures.Add($what) } }

try {
    Write-Host 'The zip:'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        $names = @($archive.Entries | ForEach-Object FullName)
        Check (@($names | Where-Object { $_ -match '\\' }).Count -eq 0) 'every path uses "/" (no backslashes)'
        $top = ($names | ForEach-Object { if ($_ -match '^(AmirLap-[^/]+)/Install Amir Lap\.cmd$') { $Matches[1] } } | Select-Object -First 1)
        Check ($top -ne $null) 'everything sits in one folder named for the version, so versions never mix'
        foreach ($need in 'Install Amir Lap.cmd', 'START HERE.txt', 'install.ps1', 'uninstall.ps1', 'AmirLap/AmirLap.exe', 'AmirLap/ftb.exe') {
            Check ($names -contains "$top/$need") "contains $need"
        }
        Check (@($names | Where-Object { $_ -notlike "$top/*" }).Count -eq 0) 'nothing sits outside that folder'
        Check (($names | Measure-Object Length -Maximum).Maximum -lt 120) 'paths are short enough to extract anywhere'
    }
    finally { $archive.Dispose() }

    Write-Host 'Extract, as downloaded:'
    New-Item -ItemType Directory -Force $extract | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($Zip, $extract)
    foreach ($file in Get-ChildItem $extract -Recurse -File) {
        Set-Content -Path $file.FullName -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
    }
    Check ((Get-Item (Join-Path $extract "$top\AmirLap\AmirLap.exe") -Stream Zone.Identifier -ErrorAction SilentlyContinue) -ne $null) 'the extracted files carry the downloaded mark'
    $launcher = Join-Path $extract "$top\Install Amir Lap.cmd"
    $launcherText = [IO.File]::ReadAllText($launcher)
    Check ($launcherText.Contains("`r`n") -and -not ($launcherText -replace "`r`n", '').Contains("`n")) 'launcher has Windows line endings'

    Write-Host 'Double-click the launcher (sandboxed):'
    if (Get-Process AmirLap -ErrorAction SilentlyContinue) { throw 'Another Amir Lap is running; quit it before this test.' }
    $env:AMIRLAP_DATA = $data
    $arguments = @(
        '-Destination', "`"$dest`"",
        '-StartMenuDir', "`"$(Join-Path $sandbox 'StartMenu')`"",
        '-UninstallKey', "$reg\Uninstall\AmirLap",
        '-ClassesRoot', "$reg\Classes",
        '-RunKey', "$reg\Run",
        '-SendToDir', "`"$(Join-Path $sandbox 'SendTo')`"",
        '-LegacyInstallDir', "`"$(Join-Path $sandbox 'Programs\FileToolbox')`"",
        '-LegacyUninstallKey', "$reg\Uninstall\FileToolbox"
    ) -join ' '
    # (Not Start-Process -Wait: that also waits for the app the installer starts, which stays running.)
    $run = Start-Process cmd.exe -ArgumentList "/c `"`"$launcher`" $arguments`"" -WorkingDirectory $env:TEMP -PassThru -WindowStyle Hidden
    Check ($run.WaitForExit(120000)) 'launcher finished by itself (the window closes after its countdown)'
    Check ($run.ExitCode -eq 0) "launcher finished with exit code 0 (got $($run.ExitCode))"
    $exe = Join-Path $dest 'AmirLap.exe'
    Check (Test-Path $exe) 'program files installed'
    Check (Test-Path (Join-Path $dest 'uninstall.ps1')) 'uninstaller installed'
    Check (Test-Path (Join-Path $sandbox 'StartMenu\Amir Lap.lnk')) 'Start Menu shortcut'
    Check ((Get-ItemProperty "$reg\Uninstall\AmirLap" -ErrorAction SilentlyContinue).DisplayName -eq 'Amir Lap') 'Installed apps entry'
    Check ($null -eq (Get-Item $exe -Stream Zone.Identifier -ErrorAction SilentlyContinue)) 'installed program is unblocked (no SmartScreen on first start)'
    Check ($null -eq (Get-Item (Join-Path $dest 'libvips-42.dll') -Stream Zone.Identifier -ErrorAction SilentlyContinue)) 'installed engines are unblocked'
    Check (Test-Path (Join-Path $env:TEMP 'AmirLap-install-log.txt')) 'an install log was saved for support'

    Write-Host 'The installed app:'
    $app = Get-Process AmirLap -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $app) { Start-Sleep -Seconds 4; $app = Get-Process AmirLap -ErrorAction SilentlyContinue | Select-Object -First 1 }
    Check ($null -ne $app) 'it was started by the installer and is running'
    Start-Sleep -Seconds 6
    $log = Get-ChildItem (Join-Path $data 'logs') -Filter 'toolbox-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $started = if ($log) { @(Select-String -Path $log.FullName -Pattern ' started\.$').Count } else { 0 }
    Check ($started -eq 4) "all four tools started: File Toolbox, Longshot, Pulse, Imp Studio (log says $started)"
    $errors = if ($log) { @(Select-String -Path $log.FullName -Pattern '\[(ERROR|FATAL)\]').Count } else { -1 }
    Check ($errors -eq 0) "no errors in the app's log (found $errors)"
    if ($app) {
        Start-Process $exe -ArgumentList '--quit' -Wait
        Check ((Get-Process AmirLap -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0 -or $app.WaitForExit(15000)) 'it quits on --quit'
    }

    Write-Host 'Running the launcher with the files missing (run from inside the zip):'
    $stray = Join-Path $sandbox 'stray'
    New-Item -ItemType Directory -Force $stray | Out-Null
    Copy-Item $launcher $stray
    $lonely = Start-Process cmd.exe -ArgumentList "/c echo.| `"$(Join-Path $stray 'Install Amir Lap.cmd')`"" -WorkingDirectory $env:TEMP -PassThru -Wait -WindowStyle Hidden
    Check ($lonely.ExitCode -eq 1) 'tells the person to extract first, and does not close silently'

    Write-Host 'Uninstall:'
    & (Join-Path $dest 'uninstall.ps1') -InstallDir $dest -UserDataDir (Join-Path $sandbox 'userdata') `
        -StartMenuDir (Join-Path $sandbox 'StartMenu') -UninstallKey "$reg\Uninstall\AmirLap" -ClassesRoot "$reg\Classes" `
        -RunKey "$reg\Run" -SendToDir (Join-Path $sandbox 'SendTo') | Out-Host
    Check (-not (Test-Path $dest)) 'program folder removed'
}
finally {
    Remove-Item Env:\AMIRLAP_DATA -ErrorAction SilentlyContinue
    Get-Process AmirLap -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$sandbox*" } | Stop-Process -Force -ErrorAction SilentlyContinue
    Set-Location $env:TEMP
    Remove-Item "HKCU:\Software\AmirLapTests\$([IO.Path]::GetFileName($sandbox))" -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Get-ChildItem 'HKCU:\Software\AmirLapTests' -ErrorAction SilentlyContinue)) { Remove-Item 'HKCU:\Software\AmirLapTests' -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) { Write-Host "$($failures.Count) check(s) failed."; exit 1 }
Write-Host 'The package installs the way a friend would meet it.'
