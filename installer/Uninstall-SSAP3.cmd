@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-SSAP3.ps1"
set "SSAP3_EXIT=%ERRORLEVEL%"
echo.
if not "%SSAP3_EXIT%"=="0" echo SSAP3 uninstaller exit code: %SSAP3_EXIT%
pause
exit /b %SSAP3_EXIT%
