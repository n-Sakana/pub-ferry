@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0ferry.ps1" %*
set "FERRY_EXIT=%ERRORLEVEL%"
if not "%FERRY_EXIT%"=="0" (
    echo.
    echo Ferry stopped with exit code %FERRY_EXIT%.
    pause
)
endlocal & exit /b %FERRY_EXIT%
