@echo off
chcp 65001 >nul
title Upload Release Beta - Comic Downloader GMTPC
cd /d "%~dp0"

echo ========================================================
echo   Comic Downloader GMTPC - GitHub Release Beta Uploader
echo   Target: ghostminhtoan/comic.downloader-GMTPC-MultiOS
echo   Release Tag: beta
echo ========================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0upload_beta.ps1"

echo.
echo Nhấn phím bất kỳ để đóng cửa sổ này...
pause >nul

