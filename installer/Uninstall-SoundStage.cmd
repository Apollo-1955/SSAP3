@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-SoundStage.ps1"
set "SOUNDSTAGE_EXIT=%ERRORLEVEL%"
echo.
if not "%SOUNDSTAGE_EXIT%"=="0" echo SoundStage uninstaller exit code: %SOUNDSTAGE_EXIT%
pause
exit /b %SOUNDSTAGE_EXIT%
