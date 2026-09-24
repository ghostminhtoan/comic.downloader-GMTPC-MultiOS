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

:: Dọn dẹp tiến trình cũ và giải phóng file lock an toàn
taskkill /F /IM ComicDownloaderGMTPC.Desktop.exe >nul 2>&1
if exist "release\windows\ComicDownloaderGMTPC.Desktop.exe.old" del /f /q "release\windows\ComicDownloaderGMTPC.Desktop.exe.old" >nul 2>&1
if exist "release\windows\ComicDownloaderGMTPC.Desktop.dll.old" del /f /q "release\windows\ComicDownloaderGMTPC.Desktop.dll.old" >nul 2>&1
if exist "release\windows\ComicDownloaderGMTPC.dll.old" del /f /q "release\windows\ComicDownloaderGMTPC.dll.old" >nul 2>&1
if exist "release\windows\ComicDownloaderGMTPC.Desktop.exe" ren "release\windows\ComicDownloaderGMTPC.Desktop.exe" "ComicDownloaderGMTPC.Desktop.exe.old" >nul 2>&1
if exist "release\windows\ComicDownloaderGMTPC.Desktop.dll" ren "release\windows\ComicDownloaderGMTPC.Desktop.dll" "ComicDownloaderGMTPC.Desktop.dll.old" >nul 2>&1
if exist "release\windows\ComicDownloaderGMTPC.dll" ren "release\windows\ComicDownloaderGMTPC.dll" "ComicDownloaderGMTPC.dll.old" >nul 2>&1

:: 1. BUILD WINDOWS (win-x64, self-contained)
echo.
echo [1/3] Publishing Windows (win-x64, self-contained)...
dotnet publish ComicDownloaderGMTPC.Desktop\ComicDownloaderGMTPC.Desktop.csproj -c Release -r win-x64 --self-contained true -o release\windows
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Windows build failed!
    exit /b %ERRORLEVEL%
)
if exist "languages.md" (
    copy /y "languages.md" "release\windows\languages.md" >nul
)
echo [OK] Windows build succeeded -^> release\windows\ComicDownloaderGMTPC.Desktop.exe

:: 2. BUILD LINUX (linux-x64, self-contained)
echo.
echo [2/3] Publishing Linux (linux-x64, self-contained)...
dotnet publish ComicDownloaderGMTPC.Desktop\ComicDownloaderGMTPC.Desktop.csproj -c Release -r linux-x64 --self-contained true -o release\linux
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Linux build failed!
    exit /b %ERRORLEVEL%
)
if exist "languages.md" (
    copy /y "languages.md" "release\linux\languages.md" >nul
)
echo [OK] Linux build succeeded -^> release\linux\ComicDownloaderGMTPC.Desktop

:: 3. BUILD ANDROID (net10.0-android, self-contained package)
echo.
echo [3/3] Building Android (net10.0-android, self-contained package)...
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
echo   [SUCCESS] All 3 OS self-contained builds completed cleanly with 0 errors!
echo   - Windows (win-x64, self-contained): release\windows\ComicDownloaderGMTPC.Desktop.exe
echo   - Linux   (linux-x64, self-contained): release\linux\ComicDownloaderGMTPC.Desktop
echo   - Android (net10.0-android, package):  release\android\ComicDownloaderGMTPC.Android.dll
echo ========================================================
endlocal
