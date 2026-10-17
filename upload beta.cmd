@echo off
chcp 65001 >nul
title Upload Release Beta (Direct-to-S3) - Comic Downloader GMTPC

cd /d "%~dp0"

:: Tu dong vo hieu hoa QuickEdit Mode tren Windows Console chong freeze tien trinh
powershell -NoProfile -Command "Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public class C{[DllImport(\"kernel32.dll\")]public static extern IntPtr GetStdHandle(int n);[DllImport(\"kernel32.dll\")]public static extern bool GetConsoleMode(IntPtr h,out uint m);[DllImport(\"kernel32.dll\")]public static extern bool SetConsoleMode(IntPtr h,uint m);public static void F(){IntPtr p=GetStdHandle(-10);uint m;GetConsoleMode(p,out m);SetConsoleMode(p,(m&~0x0040u)|0x0080u);}}';[C]::F();" >nul 2>&1

echo ========================================================
echo   Comic Downloader GMTPC - GitHub Release Beta Uploader
echo   Phuong thuc: Microsoft Edge Direct-to-S3 (~100+ Mbps)
echo   Target: ghostminhtoan/comic.downloader-GMTPC-MultiOS
echo   Release Tag: beta
echo ========================================================
echo.

:: Don dep file lock ton dong neu co truoc khi khoi dong
del /f /q "%~dp0.edge_profile\lockfile" 2>nul
del /f /q "%~dp0.edge_profile\Singleton*" 2>nul
del /f /q "%~dp0.edge_profile\Default\lockfile" 2>nul
del /f /q "%~dp0.edge_profile\Default\Singleton*" 2>nul


set "NO_PAUSE="
for %%I in (%*) do (
    if /I "%%~I"=="--no-pause" set "NO_PAUSE=1"
    if /I "%%~I"=="--skip-pause" set "NO_PAUSE=1"
)

if "%1"=="--legacy" goto USE_POWERSHELL
if "%1"=="--api" goto USE_POWERSHELL

where python >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo [INFO] Khoi chay tien trinh Microsoft Edge Direct-to-S3...
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
if defined NO_PAUSE goto END_OF_SCRIPT
echo Nhan phim bat ky de dong cua so nay...
pause >nul

:END_OF_SCRIPT
