@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-SSAP3.ps1"
set "SSAP3_EXIT=%ERRORLEVEL%"
echo.
if not "%SSAP3_EXIT%"=="0" echo SSAP3 installer exit code: %SSAP3_EXIT%
pause
exit /b %SSAP3_EXIT%
