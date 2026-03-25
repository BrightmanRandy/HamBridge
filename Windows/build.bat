@echo off
setlocal

:: ── HamBridge build script ─────────────────────────────────────────────────
:: Produces a single self-contained exe — no .NET runtime required on target.
:: Output: .\publish\HamBridge.exe
::
:: Requirements: .NET 8 SDK  (https://dotnet.microsoft.com/download)
:: Usage:        build.bat
:: ───────────────────────────────────────────────────────────────────────────

set PROJECT=HamBridgeWpf.csproj
set RID=win-x64
set CONFIG=Release
set OUTDIR=%~dp0publish

echo.
echo  Building HamBridge ...
echo  Output: %OUTDIR%\HamBridge.exe
echo.

dotnet publish "%PROJECT%" ^
    --configuration %CONFIG% ^
    --runtime %RID% ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=none ^
    -p:DebugSymbols=false ^
    --output "%OUTDIR%"

if %ERRORLEVEL% neq 0 (
    echo.
    echo  BUILD FAILED.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo  Build complete.  Run:  publish\HamBridge.exe
echo.
pause
