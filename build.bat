@echo off
setlocal
cd /d "%~dp0"
title Building Pulse Optimizer

rem ---- 1. Make sure the .NET 8 SDK is installed ----
set "DOTNET=dotnet"
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
"%DOTNET%" --list-sdks 2>nul | findstr /r "^[89]\. ^1[0-9]\." >nul
if errorlevel 1 (
  echo Installing the .NET 8 SDK with winget...
  winget install -e --id Microsoft.DotNet.SDK.8 --accept-source-agreements --accept-package-agreements
  if errorlevel 1 goto :fail
  set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
)

rem ---- 2. Get PresentMon (Intel, MIT license); it is carried inside the exe ----
if not exist "src\PulseOptimizer\tools\PresentMon.exe" (
  echo Downloading PresentMon...
  powershell -NoProfile -ExecutionPolicy Bypass -File "tools\get-presentmon.ps1" -Out "src\PulseOptimizer\tools\PresentMon.exe"
  if errorlevel 1 echo PresentMon download failed. Frame-time measurement will be unavailable in this build.
)

rem ---- 3. Build one self-contained exe ----
echo Building...
"%DOTNET%" publish src\PulseOptimizer\PulseOptimizer.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
if errorlevel 1 goto :fail

echo.
echo Done: dist\PulseOptimizer.exe
pause
exit /b 0

:fail
echo.
echo Build failed. Scroll up for the first error.
pause
exit /b 1
