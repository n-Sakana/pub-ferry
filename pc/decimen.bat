@echo off
rem A door, not a program. Everything that decides anything is in decimen.ps1.
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0decimen.ps1" %*
set "DECIMEN_EXIT=%ERRORLEVEL%"
rem Keep the console up so a startup error stays readable.
if not "%DECIMEN_EXIT%"=="0" pause
exit /b %DECIMEN_EXIT%
