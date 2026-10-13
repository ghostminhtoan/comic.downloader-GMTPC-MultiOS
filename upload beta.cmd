@echo off
chcp 65001 >nul
title Upload Release Beta (Direct-to-S3) - Comic Downloader GMTPC

cd /d "%~dp0"

echo ========================================================
echo   Comic Downloader GMTPC - GitHub Release Beta Uploader
echo   Phuong thuc: Headless Microsoft Edge (Direct-to-S3 ~100+ Mbps)
echo   Target: ghostminhtoan/comic.downloader-GMTPC-MultiOS
echo   Release Tag: beta
echo ========================================================
echo.

if "%1"=="--legacy" goto USE_POWERSHELL
if "%1"=="--api" goto USE_POWERSHELL

where python >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo [INFO] Khoi chay tien trinh Headless Edge Direct-to-S3...
    python "%~dp0upload_beta_browser.py" %*
    if %ERRORLEVEL% EQU 0 goto FINISHED
    echo.
    echo [CANH BAO] Browser upload khong thanh cong. Chuyen sang fallback PowerShell API...
    echo.
)

:USE_POWERSHELL
set MIN_SPEED_MBPS=20
echo [INFO] Chay che do PowerShell API (Toc do yeu cau: %MIN_SPEED_MBPS% Mbps)...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0upload_beta.ps1" -MinSpeedMbps %MIN_SPEED_MBPS%

:FINISHED
echo.
echo Nhan phim bat ky de dong cua so nay...
pause >nul
