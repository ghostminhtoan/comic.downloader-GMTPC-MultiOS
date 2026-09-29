@echo off
setlocal
echo ========================================================
echo   Building and Publishing Comic Downloader GMTPC Avalonia
echo   Cross-Platform Standalone Packages for 3 Platforms:
echo   1. Windows (win-x64, standalone single-file exe)
echo   2. Linux   (linux-x64, portable .tar.gz & deb package)
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
echo [2/3] Publishing Linux (linux-x64, standalone binary, portable .tar.gz and .deb)...
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

python package_linux.py
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Linux packaging failed!
    exit /b %ERRORLEVEL%
)

echo [OK] Linux distribution packages succeeded:
echo      -^> publish\linux\ComicDownloaderGMTPC.Desktop
echo      -^> publish\linux\ComicDownloaderGMTPC-linux-x64.tar.gz
echo      -^> publish\linux\comicdownloadergmtpc_1.0.0_amd64.deb

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
echo   - Windows: publish\windows\ComicDownloaderGMTPC.Desktop.exe
echo   - Linux Portable .tar.gz: publish\linux\ComicDownloaderGMTPC-linux-x64.tar.gz
echo   - Linux Debian Package:   publish\linux\comicdownloadergmtpc_1.0.0_amd64.deb
echo   - Linux Raw Binary:       publish\linux\ComicDownloaderGMTPC.Desktop
echo   - Android APK:            publish\android\com.CompanyName.ComicDownloaderGMTPC-Signed.apk
echo ========================================================
endlocal
