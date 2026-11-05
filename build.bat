@echo off
setlocal enabledelayedexpansion

:: ========================================================
:: BAT BUOC LUON LUON HIEN THI TRONG CUA SO COMMAND PROMPT
:: Giup nguoi dung truc tiep quan sat tien do tung giay
:: ========================================================
if not "%IN_VISIBLE_CMD%"=="1" (
    where python >nul 2>&1
    if %ERRORLEVEL% EQU 0 (
        if exist "%~dp0build_in_cmd.py" (
            python "%~dp0build_in_cmd.py" --target "build.bat" %*
            exit /b %ERRORLEVEL%
        )
    )
)

title Comic Downloader GMTPC - Build & Upload Beta (Cross-Platform)

echo ========================================================
echo   Building and Publishing Comic Downloader GMTPC Avalonia
echo   Cross-Platform Portable Packages in Unified \publish\
echo   1. Windows (win-x64, Smart Portable Launcher + Runtime in .portable\bin\net avalonia)
echo   2. Linux   (linux-x64, Smart Portable Launcher + Portable tar.gz)
echo   3. Android (net10.0-android, Signed Standalone APK)
echo   4. Upload  (Direct-to-S3 GitHub Release Beta Assets)
echo ========================================================

cd /d "%~dp0"

if not exist "publish" mkdir "publish"
if not exist "publish\.portable\bin\net avalonia" mkdir "publish\.portable\bin\net avalonia"

:: Terminate running instance if any to prevent write-lock on executable
taskkill /F /IM ComicDownloaderGMTPC.Desktop.exe >nul 2>&1

echo.
echo [1/4] Publishing Windows (win-x64, net avalonia runtimes.dll in .portable\bin\net avalonia + Native Host)...
dotnet publish ComicDownloaderGMTPC.Desktop\ComicDownloaderGMTPC.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o "publish\.portable\bin\net avalonia"
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Windows build failed!
    echo Cua so se giu nguyen 20 giay de ban kiem tra log loi...
    python -c "import time; time.sleep(20)"
    exit /b %ERRORLEVEL%
)

:: Chuan hoa file thanh net avalonia runtimes.dll theo yeu cau cua nguoi dung
copy /y "publish\.portable\bin\net avalonia\ComicDownloaderGMTPC.Desktop.dll" "publish\.portable\bin\net avalonia\net avalonia runtimes.dll" >nul 2>&1
copy /y "publish\.portable\bin\net avalonia\ComicDownloaderGMTPC.Desktop.runtimeconfig.json" "publish\.portable\bin\net avalonia\net avalonia runtimes.runtimeconfig.json" >nul 2>&1
copy /y "publish\.portable\bin\net avalonia\ComicDownloaderGMTPC.Desktop.deps.json" "publish\.portable\bin\net avalonia\net avalonia runtimes.deps.json" >nul 2>&1

:: Xoa sach toan bo file .exe ben trong .portable\bin\net avalonia\ (chi giu lai DLLs va runtime)
del /f /q "publish\.portable\bin\net avalonia\*.exe" >nul 2>&1
del /f /q "publish\.portable\bin\net avalonia\*.pdb" >nul 2>&1
del /f /q "publish\.portable\bin\net avalonia\*.xml" >nul 2>&1

:: Create runtime-net10-win-x64.zip for on-demand downloading
echo  -^> Creating Windows Runtime zip: publish\runtime-net10-win-x64.zip...
python -c "import shutil, os; shutil.make_archive(r'publish\runtime-net10-win-x64', 'zip', r'publish\.portable\bin\net avalonia')"
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Failed to create runtime-net10-win-x64.zip!
)

:: Bien dich Windows Native Host (ComicDownloaderGMTPC.Desktop.exe)
echo  -^> Compiling Windows Native Host Launcher (ComicDownloaderGMTPC.Desktop.exe)...
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
rc.exe /fo "Launcher\Windows\NativeHost.res" "Launcher\Windows\NativeHost.rc" >nul 2>&1
cl.exe /nologo /O2 /EHsc /W3 "Launcher\Windows\NativeHost.cpp" "Launcher\Windows\NativeHost.res" /Fe:"publish\ComicDownloaderGMTPC.Desktop.exe" /link /SUBSYSTEM:WINDOWS user32.lib shell32.lib >nul 2>&1
del /f /q "Launcher\Windows\NativeHost.obj" >nul 2>&1
del /f /q "Launcher\Windows\NativeHost.res" >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Windows Native Host compilation failed!
    echo Cua so se giu nguyen 20 giay de ban kiem tra log loi...
    python -c "import time; time.sleep(20)"
    exit /b %ERRORLEVEL%
)

:: Clean root publish intermediate files
del /f /q "publish\*.dll" >nul 2>&1
del /f /q "publish\*.pdb" >nul 2>&1
del /f /q "publish\*.xml" >nul 2>&1
del /f /q "publish\*.json" >nul 2>&1
del /f /q "publish\*.old" >nul 2>&1

echo [OK] Windows Portable succeeded:
echo      -^> publish\ComicDownloaderGMTPC.Desktop.exe (Native Host Launcher)
echo      -^> publish\.portable\bin\net avalonia\net avalonia runtimes.dll (100%% no .exe inside)
echo      -^> publish\runtime-net10-win-x64.zip

echo.
echo [2/4] Publishing Linux (linux-x64, portable .tar.gz and runtime)...
if exist "publish\.linux_staging" rd /s /q "publish\.linux_staging"
dotnet publish ComicDownloaderGMTPC.Desktop\ComicDownloaderGMTPC.Desktop.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "publish\.linux_staging"
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Linux build failed!
    echo Cua so se giu nguyen 20 giay de ban kiem tra log loi...
    python -c "import time; time.sleep(20)"
    exit /b %ERRORLEVEL%
)

:: Clean intermediate files in linux_staging
del /f /q "publish\.linux_staging\*.dll" >nul 2>&1
del /f /q "publish\.linux_staging\*.pdb" >nul 2>&1
del /f /q "publish\.linux_staging\*.xml" >nul 2>&1
del /f /q "publish\.linux_staging\*.json" >nul 2>&1

:: Package Linux distributions directly in publish/
if exist "package_linux.py" (
    python package_linux.py
    if !ERRORLEVEL! NEQ 0 (
        echo [ERROR] Linux packaging failed!
        echo Cua so se giu nguyen 20 giay de ban kiem tra log loi...
        python -c "import time; time.sleep(20)"
        exit /b !ERRORLEVEL!
    )
)

echo [OK] Linux portable distribution packages succeeded:
echo      -^> publish\ComicDownloaderGMTPC
echo      -^> publish\ComicDownloaderGMTPC-linux-x64.tar.gz
echo      -^> publish\runtime-net10-linux-x64.tar.gz

echo.
echo [3/4] Building and Packaging Android (net10.0-android, Signed Standalone APK)...
dotnet publish ComicDownloaderGMTPC.Android\ComicDownloaderGMTPC.Android.csproj -c Release -f net10.0-android -o publish
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Android build failed!
    echo Cua so se giu nguyen 20 giay de ban kiem tra log loi...
    python -c "import time; time.sleep(20)"
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
echo [4/4] Uploading Release Beta to GitHub Release (tag beta)...
set "DO_UPLOAD=1"
for %%I in (%*) do (
    if /I "%%~I"=="--no-upload" set "DO_UPLOAD=0"
    if /I "%%~I"=="--skip-upload" set "DO_UPLOAD=0"
)

if "!DO_UPLOAD!"=="0" (
    echo [INFO] Bo qua buoc upload release beta [tuy chon --no-upload duoc chi dinh].
    goto AFTER_UPLOAD
)

call "%~dp0upload beta.cmd" --no-pause
if !ERRORLEVEL! NEQ 0 (
    echo [CANH BAO] Upload beta gap su co hoac da bi huy boi nguoi dung.
)

:AFTER_UPLOAD

echo.
echo ========================================================
echo   [SUCCESS] All Platforms Published Cleanly in \publish\!
echo   - Windows Launcher:      publish\ComicDownloaderGMTPC.Desktop.exe
echo   - Windows Runtime:       publish\.portable\bin\net avalonia\
echo   - Windows Runtime Zip:   publish\runtime-net10-win-x64.zip
echo   - Linux Launcher:        publish\ComicDownloaderGMTPC
echo   - Linux Portable tar.gz: publish\ComicDownloaderGMTPC-linux-x64.tar.gz
echo   - Linux Runtime tar.gz:  publish\runtime-net10-linux-x64.tar.gz
echo   - Android APK:           publish\com.CompanyName.ComicDownloaderGMTPC-Signed.apk
echo   - Release Beta:          https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/tag/beta
echo ========================================================
echo.
echo [HOAN TAT] Tien trinh build va upload Release Beta da xong 100%%.
echo Cua so se tu dong dong sau 5 giay...
python -c "import time; time.sleep(5)"
endlocal
