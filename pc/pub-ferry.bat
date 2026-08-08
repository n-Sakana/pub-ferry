@echo off
rem A door, not a program. Everything that decides anything is in pub-ferry.ps1.
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0pub-ferry.ps1" %*
set "PUB_FERRY_EXIT=%ERRORLEVEL%"
rem Keep the console up so a startup error stays readable.
if not "%PUB_FERRY_EXIT%"=="0" pause
exit /b %PUB_FERRY_EXIT%
