<#
.SYNOPSIS
    Builds Amir Lap for distribution: a self-contained win-x64 folder (no .NET install needed),
    the install/uninstall scripts beside it, and a zip of both.

.DESCRIPTION
    Output (default artifacts\publish):
      AmirLap\                the application: AmirLap.exe (tray app), ftb.exe (File Toolbox command line),
                              native engines, licences\ and docs\
      install.ps1, uninstall.ps1
      AmirLap-<version>-win-x64.zip

    Restoring a self-contained, ReadyToRun build needs the .NET runtime packs from NuGet the first time.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\publish.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -FrameworkDependent   # smaller; needs the .NET 9 Desktop Runtime
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Output,
    [switch]$FrameworkDependent,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Output) { $Output = Join-Path $root 'artifacts\publish' }
$app = Join-Path $Output 'AmirLap'

if (Test-Path $app) { Remove-Item $app -Recurse -Force }
New-Item -ItemType Directory -Force $app | Out-Null

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$readyToRun = if ($FrameworkDependent) { 'false' } else { 'true' }
$staging = Join-Path $Output '.staging'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

# Both programs share one folder and one runtime, so each is published on its own and then merged:
# the CLI first, then the desktop app with a forced overwrite. Where both carry the same file, the
# app's copy is the newer one (WPF's System.IO.Packaging, WindowsBase, ...) and must win; the CLI
# runs fine on those newer versions. (Publishing both into one folder keeps whichever file has the
# newer timestamp, which broke the app's start-up.)
foreach ($entry in @(@('cli', 'src\Toolbox.Cli\Toolbox.Cli.csproj'), @('app', 'src\Toolbox.Windows\Toolbox.Windows.csproj'))) {
    $name, $project = $entry
    Write-Host "Publishing $project ..."
    & dotnet publish (Join-Path $root $project) -c $Configuration -r win-x64 --self-contained $selfContained `
        "-p:PublishReadyToRun=$readyToRun" -o (Join-Path $staging $name) --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project." }
}
Copy-Item (Join-Path $staging 'cli\*') $app -Recurse -Force
Copy-Item (Join-Path $staging 'app\*') $app -Recurse -Force
Remove-Item $staging -Recurse -Force
# Debug symbols of third-party native libraries (SkiaSharp ships 110 MB of them) are not distributed;
# our own assemblies keep theirs so logged errors have line numbers.
Get-ChildItem $app -Filter *.pdb | Where-Object { $_.Name -notmatch '^(AmirLap|ftb|Toolbox\..*)\.pdb$' } | Remove-Item -Force

# Licences and notices travel with the binaries (libvips is LGPL and dynamically linked: its DLLs may be replaced).
$licences = New-Item -ItemType Directory -Force (Join-Path $app 'licences')
Copy-Item (Join-Path $root 'docs\THIRD_PARTY_LICENSES.md') $licences
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$notices = @{
    'libvips-bundle-THIRD-PARTY-NOTICES.md' = 'netvips.native.win-x64\8.18.7\THIRD-PARTY-NOTICES.md'
    'SkiaSharp-LICENSE.txt'                 = 'skiasharp\4.153.1\LICENSE.txt'
}
foreach ($name in $notices.Keys) {
    $source = Join-Path $packages $notices[$name]
    if (Test-Path $source) { Copy-Item $source (Join-Path $licences $name) }
    else { Write-Warning "Notice not found: $source" }
}

$docs = New-Item -ItemType Directory -Force (Join-Path $app 'docs')
foreach ($doc in 'README.md', 'docs\CAPABILITIES.md', 'docs\OCR_EVALUATION.md', 'docs\TESTING.md') {
    Copy-Item (Join-Path $root $doc) $docs
}

foreach ($script in 'install.ps1', 'uninstall.ps1') {
    Copy-Item (Join-Path $PSScriptRoot $script) $Output -Force
}
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $app -Force

# Smoke check of the published bits: the CLI loads every engine and reports the runtime features.
# It runs with a throwaway data folder so nothing is written to the user's settings or logs.
$smokeData = Join-Path $Output '.smoke-data'
$previousData = $env:AMIRLAP_DATA
$env:AMIRLAP_DATA = $smokeData
try {
    $features = & (Join-Path $app 'ftb.exe') features
    if ($LASTEXITCODE -ne 0) { throw "The published ftb.exe did not run." }
    Write-Host ($features -join [Environment]::NewLine)

    if (Get-Process AmirLap -ErrorAction SilentlyContinue) {
        Write-Warning 'Amir Lap is running, so the published app could not be start-tested. Quit it and publish again to check.'
    }
    else {
        $desktop = Start-Process (Join-Path $app 'AmirLap.exe') -ArgumentList '--background' -PassThru
        Start-Sleep -Seconds 6
        if ($desktop.HasExited) { throw "The published AmirLap.exe exited during start-up (code $($desktop.ExitCode)). See the Windows Application event log." }
        Start-Process (Join-Path $app 'AmirLap.exe') -ArgumentList '--quit' -Wait
        if (-not $desktop.WaitForExit(15000)) { $desktop.Kill(); throw 'The published AmirLap.exe did not quit on --quit.' }
        Write-Host 'Desktop app: starts and quits cleanly.'
    }
}
finally {
    $env:AMIRLAP_DATA = $previousData
    if (Test-Path $smokeData) { Remove-Item $smokeData -Recurse -Force }
}

# ProductVersion carries "+<commit>"; file names use the plain version.
$version = (Get-Item (Join-Path $app 'AmirLap.exe')).VersionInfo.ProductVersion.Split('+')[0]
$size = (Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("Amir Lap {0}: {1:N0} files, {2:N0} MB in {3}" -f $version, (Get-ChildItem $app -Recurse -File).Count, ($size / 1MB), $app)

if (-not $NoZip) {
    $zip = Join-Path $Output "AmirLap-$version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $app, (Join-Path $Output 'install.ps1'), (Join-Path $Output 'uninstall.ps1') -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("Zip: {0} ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
}
