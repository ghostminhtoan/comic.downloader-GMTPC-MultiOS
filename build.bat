@echo off
setlocal enabledelayedexpansion
echo ========================================================
echo   Building Comic Downloader GMTPC Avalonia for 3 OS:
echo   1. Windows (win-x64)
echo   2. Linux (linux-x64)
echo   3. Android (net10.0-android)
echo ========================================================

if not exist "release\windows" mkdir "release\windows"
if not exist "release\linux" mkdir "release\linux"
if not exist "release\android" mkdir "release\android"

:: 1. BUILD WINDOWS (win-x64)
echo.
echo [1/3] Publishing Windows (win-x64)...
dotnet publish ComicDownloaderGMTPC.Desktop\ComicDownloaderGMTPC.Desktop.csproj -c Release -r win-x64 --self-contained false -o release\windows
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Windows build failed!
    exit /b %ERRORLEVEL%
)
if exist "languages.md" (
    copy /y "languages.md" "release\windows\languages.md" >nul
)
echo [OK] Windows build succeeded -^> release\windows\ComicDownloaderGMTPC.Desktop.exe

:: 2. BUILD LINUX (linux-x64)
echo.
echo [2/3] Publishing Linux (linux-x64)...
dotnet publish ComicDownloaderGMTPC.Desktop\ComicDownloaderGMTPC.Desktop.csproj -c Release -r linux-x64 --self-contained false -o release\linux
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Linux build failed!
    exit /b %ERRORLEVEL%
)
if exist "languages.md" (
    copy /y "languages.md" "release\linux\languages.md" >nul
)
echo [OK] Linux build succeeded -^> release\linux\ComicDownloaderGMTPC.Desktop

:: 3. BUILD ANDROID (net10.0-android)
echo.
echo [3/3] Building Android (net10.0-android)...
dotnet build ComicDownloaderGMTPC.Android\ComicDownloaderGMTPC.Android.csproj -c Release -o release\android
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Android build failed!
    exit /b %ERRORLEVEL%
)
if exist "languages.md" (
    copy /y "languages.md" "release\android\languages.md" >nul
)
echo [OK] Android build succeeded -^> release\android\

echo.
echo ========================================================
echo   [SUCCESS] All 3 OS builds completed cleanly with 0 errors!
echo   - Windows: release\windows\ComicDownloaderGMTPC.Desktop.exe
echo   - Linux:   release\linux\ComicDownloaderGMTPC.Desktop
echo   - Android: release\android\ComicDownloaderGMTPC.Android.dll
echo ========================================================
endlocal
