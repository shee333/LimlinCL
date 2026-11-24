@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

set "PS1=%~dp0tools\build-release.ps1"
if not exist "%PS1%" (
    echo [ERROR] tools\build-release.ps1 not found
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
    echo [OK] Build finished. Output is in D:\MiencraftDEV\LimCL\product
) else (
    echo [FAILED] exit code %RC%
)
pause
exit /b %RC%
