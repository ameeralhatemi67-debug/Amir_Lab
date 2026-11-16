@echo off
setlocal
title Install Amir Lap
cd /d "%~dp0"
if not exist "AmirLap\AmirLap.exe" goto notextracted
if not exist "install.ps1" goto notextracted
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Launcher %*
if errorlevel 1 goto failed
exit /b 0

:notextracted
echo.
echo   Amir Lap's files are not next to this installer.
echo.
echo   1. Close this window.
echo   2. Right-click the zip you downloaded and choose "Extract All".
echo   3. Open the new folder and double-click "Install Amir Lap" there.
echo.
pause
exit /b 1

:failed
echo.
echo   The installer stopped. Please send a photo of the text above, or the file
echo   %TEMP%\AmirLap-install-log.txt, to Amir.
echo.
pause
exit /b 1
