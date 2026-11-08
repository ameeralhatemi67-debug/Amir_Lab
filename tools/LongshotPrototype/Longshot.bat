@echo off
rem Longshot launcher: sets itself up on first run, then starts without a console window.
cd /d "%~dp0"
if not exist ".venv\Scripts\pythonw.exe" (
  echo First run: setting up Longshot ^(about a minute^)...
  python -m venv .venv || goto :fail
  ".venv\Scripts\python.exe" -m pip install --disable-pip-version-check -q -r requirements.txt || goto :fail
)
start "" ".venv\Scripts\pythonw.exe" "%~dp0longshot.py" %*
exit /b 0

:fail
echo.
echo Setup failed. Make sure Python 3.10 or newer is installed and on PATH.
pause
exit /b 1
