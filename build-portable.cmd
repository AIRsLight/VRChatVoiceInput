@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-portable.ps1" %*
set "exitCode=%errorlevel%"
if not "%exitCode%"=="0" pause
exit /b %exitCode%
