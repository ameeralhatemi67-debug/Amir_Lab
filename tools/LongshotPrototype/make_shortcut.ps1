# Adds Longshot to the Start menu with a keyboard shortcut (default Ctrl+Alt+S).
# Run it again after moving this folder.
#   powershell -ExecutionPolicy Bypass -File make_shortcut.ps1
#   powershell -ExecutionPolicy Bypass -File make_shortcut.ps1 -Hotkey "CTRL+ALT+L"
param([string]$Hotkey = "CTRL+ALT+S")

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$pyw = Join-Path $here ".venv\Scripts\pythonw.exe"
$py = Join-Path $here ".venv\Scripts\python.exe"
if (-not (Test-Path $pyw)) {
    Write-Host "Setting up Python packages..."
    python -m venv (Join-Path $here ".venv")
    & $py -m pip install --disable-pip-version-check -q -r (Join-Path $here "requirements.txt")
}
$ico = Join-Path $here "longshot.ico"
& $py (Join-Path $here "longshot.py") --make-icon $ico

$lnk = Join-Path ([Environment]::GetFolderPath("Programs")) "Longshot.lnk"
$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = $pyw
$s.Arguments = "`"$(Join-Path $here 'longshot.py')`""
$s.WorkingDirectory = $here
$s.IconLocation = $ico
$s.Hotkey = $Hotkey
$s.Description = "Scrolling screenshot: draw a box, then extend it downward"
$s.Save()
Write-Host "Done. Search 'Longshot' in Start, or press $Hotkey."
