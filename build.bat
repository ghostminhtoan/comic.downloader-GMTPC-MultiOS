@echo off
setlocal
echo ========================================================
echo   Building and Publishing Comic Downloader GMTPC Avalonia
echo   Standalone Executables ^& Packages for 3 Platforms:
echo   1. Windows (win-x64, standalone single-file exe)
echo   2. Linux   (linux-x64, standalone binary + .tar.gz package)
echo   3. Android (net10.0-android, single APK package)
echo ========================================================

if not exist "publish\windows" mkdir "publish\windows"
if not exist "publish\linux" mkdir "publish\linux"
if not exist "publish\android" mkdir "publish\android"

taskkill /F /IM ComicDownloaderGMTPC.Desktop.exe >nul 2>&1

echo.
echo [1/3] Publishing Windows (win-x64, standalone single-file exe)...
dotnet publish ComicDownloaderGMTPC.Desktop\ComicDownloaderGMTPC.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o publish\windows
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Windows build failed!
    exit /b %ERRORLEVEL%
)

del /f /q "publish\windows\*.dll" >nul 2>&1
del /f /q "publish\windows\*.pdb" >nul 2>&1
del /f /q "publish\windows\*.xml" >nul 2>&1
del /f /q "publish\windows\*.json" >nul 2>&1
del /f /q "publish\windows\*.old" >nul 2>&1

echo [OK] Windows standalone single-file exe succeeded:
echo      -^> publish\windows\ComicDownloaderGMTPC.Desktop.exe

echo.
echo [2/3] Publishing Linux (linux-x64, standalone binary + .tar.gz package)...
dotnet publish ComicDownloaderGMTPC.Desktop\ComicDownloaderGMTPC.Desktop.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o publish\linux
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Linux build failed!
    exit /b %ERRORLEVEL%
)

del /f /q "publish\linux\*.dll" >nul 2>&1
del /f /q "publish\linux\*.pdb" >nul 2>&1
del /f /q "publish\linux\*.xml" >nul 2>&1
del /f /q "publish\linux\*.json" >nul 2>&1
del /f /q "publish\linux\*.old" >nul 2>&1

:: Tạo launcher script cho Linux
(
echo #!/bin/bash
echo SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" ^&^& pwd)"
echo chmod +x "$SCRIPT_DIR/ComicDownloaderGMTPC.Desktop"
echo "$SCRIPT_DIR/ComicDownloaderGMTPC.Desktop" "$@"
) > "publish\linux\run.sh"

:: Đóng gói file .tar.gz cho Linux
if exist "publish\linux\ComicDownloaderGMTPC-linux-x64.tar.gz" del /f /q "publish\linux\ComicDownloaderGMTPC-linux-x64.tar.gz"
if exist "publish\linux\ComicDownloaderGMTPC.tar.gz" del /f /q "publish\linux\ComicDownloaderGMTPC.tar.gz"

tar -czf "publish\linux\ComicDownloaderGMTPC-linux-x64.tar.gz" -C "publish\linux" ComicDownloaderGMTPC.Desktop run.sh
copy /y "publish\linux\ComicDownloaderGMTPC-linux-x64.tar.gz" "publish\linux\ComicDownloaderGMTPC.tar.gz" >nul 2>&1

echo [OK] Linux binary and .tar.gz succeeded:
echo      -^> publish\linux\ComicDownloaderGMTPC.Desktop
echo      -^> publish\linux\ComicDownloaderGMTPC-linux-x64.tar.gz
echo      -^> publish\linux\ComicDownloaderGMTPC.tar.gz

echo.
echo [3/3] Building and Packaging Android (net10.0-android, single APK package)...
dotnet build ComicDownloaderGMTPC.Android\ComicDownloaderGMTPC.Android.csproj -c Release -o publish\android
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Android build failed!
    exit /b %ERRORLEVEL%
)

del /f /q "publish\android\*.dll" >nul 2>&1
del /f /q "publish\android\*.pdb" >nul 2>&1
del /f /q "publish\android\*.xml" >nul 2>&1
del /f /q "publish\android\*.json" >nul 2>&1
del /f /q "publish\android\*.so" >nul 2>&1

echo [OK] Android single APK package succeeded:
echo      -^> publish\android\com.CompanyName.ComicDownloaderGMTPC-Signed.apk

echo.
echo ========================================================
echo   [SUCCESS] All 3 Platforms published cleanly with 0 errors!
echo   - Windows (win-x64 exe):       publish\windows\ComicDownloaderGMTPC.Desktop.exe
echo   - Linux   (linux-x64 binary):  publish\linux\ComicDownloaderGMTPC.Desktop
echo   - Linux   (.tar.gz archive):   publish\linux\ComicDownloaderGMTPC-linux-x64.tar.gz
echo   - Android (single APK):        publish\android\com.CompanyName.ComicDownloaderGMTPC-Signed.apk
echo ========================================================
endlocal
