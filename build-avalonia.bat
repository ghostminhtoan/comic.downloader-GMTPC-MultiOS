@echo off
setlocal enabledelayedexpansion

echo ========================================================
echo   Building and Publishing Comic Downloader GMTPC Avalonia
echo   Cross-Platform Standalone Packages in Unified \publish\
echo   1. Windows (win-x64, Standalone Single-File EXE)
echo   2. Linux   (linux-x64, Standalone Binary, Portable tar.gz, DEB)
echo   3. Android (net10.0-android, Signed Standalone APK)
echo ========================================================

cd /d "%~dp0"

if not exist "publish" mkdir "publish"

:: Terminate running instance if any to prevent write-lock on executable
taskkill /F /IM ComicDownloaderGMTPC.Desktop.exe >nul 2>&1

echo.
echo [1/3] Publishing Windows (win-x64, standalone single-file exe)...
dotnet publish ComicDownloaderGMTPC.Desktop\ComicDownloaderGMTPC.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o publish
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Windows build failed!
    exit /b %ERRORLEVEL%
)

:: Clean intermediate files from publish root
del /f /q "publish\*.dll" >nul 2>&1
del /f /q "publish\*.pdb" >nul 2>&1
del /f /q "publish\*.xml" >nul 2>&1
del /f /q "publish\*.json" >nul 2>&1
del /f /q "publish\*.old" >nul 2>&1

echo [OK] Windows standalone single-file exe succeeded:
echo      -^> publish\ComicDownloaderGMTPC.Desktop.exe

echo.
echo [2/3] Publishing Linux (linux-x64, standalone binary, portable .tar.gz and .deb)...
dotnet publish ComicDownloaderGMTPC.Desktop\ComicDownloaderGMTPC.Desktop.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o publish
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Linux build failed!
    exit /b %ERRORLEVEL%
)

:: Clean intermediate files
del /f /q "publish\*.dll" >nul 2>&1
del /f /q "publish\*.pdb" >nul 2>&1
del /f /q "publish\*.xml" >nul 2>&1
del /f /q "publish\*.json" >nul 2>&1
del /f /q "publish\*.old" >nul 2>&1

:: Create Linux pure binary without .Desktop suffix, tar.gz and deb package
if exist "package_linux.py" (
    python package_linux.py
    if !ERRORLEVEL! NEQ 0 (
        echo [ERROR] Linux packaging failed!
        exit /b !ERRORLEVEL!
    )
) else (
    if exist "publish\ComicDownloaderGMTPC.Desktop" (
        copy /y "publish\ComicDownloaderGMTPC.Desktop" "publish\ComicDownloaderGMTPC" >nul 2>&1
    )
)

echo [OK] Linux standalone distribution packages succeeded:
echo      -^> publish\ComicDownloaderGMTPC
echo      -^> publish\ComicDownloaderGMTPC-linux-x64.tar.gz
echo      -^> publish\comicdownloadergmtpc_1.0.0_amd64.deb

echo.
echo [3/3] Building and Packaging Android (net10.0-android, Signed Standalone APK)...
dotnet publish ComicDownloaderGMTPC.Android\ComicDownloaderGMTPC.Android.csproj -c Release -f net10.0-android -o publish
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Android build failed!
    exit /b %ERRORLEVEL%
)

:: Clean temporary artifacts
del /f /q "publish\*.dll" >nul 2>&1
del /f /q "publish\*.pdb" >nul 2>&1
del /f /q "publish\*.xml" >nul 2>&1
del /f /q "publish\*.json" >nul 2>&1
del /f /q "publish\*.so" >nul 2>&1

echo [OK] Android signed standalone APK package succeeded:
echo      -^> publish\com.CompanyName.ComicDownloaderGMTPC-Signed.apk

echo.
echo ========================================================
echo   [SUCCESS] All Platforms Published Cleanly in \publish\!
echo   - Windows:               publish\ComicDownloaderGMTPC.Desktop.exe
echo   - Linux Executable:      publish\ComicDownloaderGMTPC
echo   - Linux Portable tar.gz: publish\ComicDownloaderGMTPC-linux-x64.tar.gz
echo   - Linux Debian Package:  publish\comicdownloadergmtpc_1.0.0_amd64.deb
echo   - Android APK:           publish\com.CompanyName.ComicDownloaderGMTPC-Signed.apk
echo ========================================================
endlocal

