@echo off
setlocal
echo ========================================================
echo   Building Comic Downloader GMTPC Avalonia (Release)
echo ========================================================

dotnet build ComicDownloaderGMTPC.slnx -c Release
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Build failed!
    exit /b %ERRORLEVEL%
)

if exist "languages.md" (
    copy /y "languages.md" "ComicDownloaderGMTPC.Desktop\bin\Release\net10.0\" >nul
)

echo [SUCCESS] Build completed cleanly with 0 errors!
echo Output EXE: ComicDownloaderGMTPC.Desktop\bin\Release\net10.0\ComicDownloaderGMTPC.Desktop.exe
endlocal
