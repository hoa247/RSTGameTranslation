@echo off
REM One-click: close running RST, rebuild Debug, relaunch. Self-elevates (app needs admin).

REM --- Self-elevate if not already running as administrator ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator privileges...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
title RST - Build and Run

echo ============================================
echo  RST  -  Build ^& Run
echo ============================================
echo.

REM --- Locate dotnet ---
set "DOTNET=dotnet"
where dotnet >nul 2>&1
if %errorlevel% neq 0 set "DOTNET=C:\Program Files\dotnet\dotnet.exe"

echo [1/3] Closing running RST (if any)...
taskkill /IM rst_debug.exe /F >nul 2>&1
taskkill /IM rst.exe /F >nul 2>&1

echo [2/3] Building (Debug)...
echo.
"%DOTNET%" build RST.csproj -c Debug
if errorlevel 1 (
    echo.
    echo ############################################
    echo  BUILD FAILED - see errors above.
    echo ############################################
    echo.
    pause
    exit /b 1
)

echo.
echo [3/3] Build OK. Launching app...
start "" "%~dp0app\rst_debug.exe"

echo.
echo Done. You can close this window.
timeout /t 3 >nul
exit /b 0
