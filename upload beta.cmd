@echo off
chcp 65001 >nul
title Upload Release Beta - Comic Downloader GMTPC

cd /d "%~dp0"

:: ========================================================
:: CAU HINH TOC DO UPLOAD TOI THIEU (MEGABIT / GIAY)
:: Mac dinh la 20 Mbps. Neu toc do duoi nguong nay sau 4s warm-up,
:: tool se tu dong HUY va RETRY ket noi moi nhanh hon!
:: ========================================================
set MIN_SPEED_MBPS=20

echo ========================================================
echo   Comic Downloader GMTPC - GitHub Release Beta Uploader
echo   Target: ghostminhtoan/comic.downloader-GMTPC-MultiOS
echo   Release Tag: beta
echo   Toc do toi thieu yeu cau: %MIN_SPEED_MBPS% Mbps
echo ========================================================
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0upload_beta.ps1" -MinSpeedMbps %MIN_SPEED_MBPS%

echo.
echo Nhan phim bat ky de dong cua so nay...
pause >nul
