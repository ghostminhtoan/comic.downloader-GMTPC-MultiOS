@echo off
setlocal
chcp 65001 >nul
title Cai dat APK vao LDPlayer 4.0 - Comic Downloader GMTPC

set "ADB_PATH=T:\Program files\LDPlayer4.0\adb.exe"
set "APK_PATH=%~dp0publish\com.CompanyName.ComicDownloaderGMTPC-Signed.apk"

echo ========================================================
echo   Cai dat APK vao gia lap LDPlayer 4.0
echo   ADB: %ADB_PATH%
echo   APK: %APK_PATH%
echo ========================================================

if not exist "%ADB_PATH%" (
    echo [ERROR] Khong tim thay ADB tai: %ADB_PATH%
    pause
    exit /b 1
)

if not exist "%APK_PATH%" (
    echo [ERROR] Khong tim thay tap tin APK tai: %APK_PATH%
    echo Vui long chay build.bat truoc de tao file APK.
    pause
    exit /b 1
)

echo.
echo [1/2] Kiem tra ket noi gia lap...
"%ADB_PATH%" devices

echo.
echo [2/2] Dang cai dat APK moi nhat vao LDPlayer...
"%ADB_PATH%" install -r "%APK_PATH%"
if %ERRORLEVEL% EQU 0 (
    echo.
    echo [THANH CONG] Da cai dat thanh cong vao LDPlayer 4.0!
) else (
    echo.
    echo [LUU Y] Neu gap loi offline hoac device not found, hay dam bao:
    echo   1. LDPlayer da duoc mo.
    echo   2. Trong LDPlayer: Cai dat (⚙) -^> Other -^> ADB Debug -^> Chon Open / Bat.
)

echo.
pause
endlocal
