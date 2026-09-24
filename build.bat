@echo off
setlocal
echo ========================================================
echo   Building Comic Downloader GMTPC Avalonia for 3 OS:
echo   1. Windows (win-x64, standalone single-file exe)
echo   2. Linux (linux-x64)
echo   3. Android (net10.0-android)
echo ========================================================

if not exist "release\windows" mkdir "release\windows"
if not exist "release\linux" mkdir "release\linux"
if not exist "release\android" mkdir "release\android"

taskkill /F /IM ComicDownloaderGMTPC.Desktop.exe >nul 2>&1

echo.
echo [1/3] Publishing Windows (win-x64, standalone single-file exe)...
dotnet publish ComicDownloaderGMTPC.Desktop\ComicDownloaderGMTPC.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o release\windows
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Windows build failed!
    exit /b %ERRORLEVEL%
)

del /f /q "release\windows\*.dll" >nul 2>&1
del /f /q "release\windows\*.pdb" >nul 2>&1
del /f /q "release\windows\*.json" >nul 2>&1
del /f /q "release\windows\*.old" >nul 2>&1

if exist "languages.md" (
    copy /y "languages.md" "release\windows\languages.md" >nul
)
echo [OK] Windows build succeeded -^> release\windows\ComicDownloaderGMTPC.Desktop.exe

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
echo   [SUCCESS] All 3 OS builds completed cleanly with 0 errors!
echo   - Windows (win-x64, standalone): release\windows\ComicDownloaderGMTPC.Desktop.exe
echo   - Linux   (linux-x64, self-contained): release\linux\ComicDownloaderGMTPC.Desktop
echo   - Android (net10.0-android, package):  release\android\ComicDownloaderGMTPC.Android.dll
echo ========================================================
endlocal
