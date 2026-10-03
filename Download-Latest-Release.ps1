# ==============================================================================
# Comic Downloader GMTPC - Multi-OS GitHub Release Downloader
# Tự động tải trực tiếp các bản phát hành (Windows, Linux, Android) từ GitHub Release
# ==============================================================================

[CmdletBinding()]
param (
    [string]$OutputDir = "$PSScriptRoot\publish\release-downloads",
    [string]$Repo = "ghostminhtoan/comic.downloader-GMTPC-MultiOS",
    [string]$Tag = "releases"
)

$ErrorActionPreference = "Stop"
Write-Host "=== Comic Downloader GMTPC Multi-OS Release Downloader ===" -ForegroundColor Cyan
Write-Host "Target Release Tag: https://github.com/$Repo/releases/tag/$Tag"
Write-Host "Save Location: $OutputDir`n"

if (-not (Test-Path -Path $OutputDir)) {
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
}

$files = @(
    "ComicDownloaderGMTPC-Windows-x64.exe",
    "ComicDownloaderGMTPC-Linux-x64.tar.gz",
    "ComicDownloaderGMTPC-Linux-x64",
    "ComicDownloaderGMTPC-Android-Signed.apk"
)

$baseUrl = "https://github.com/$Repo/releases/download/$Tag"

foreach ($file in $files) {
    $url = "$baseUrl/$file"
    $targetPath = Join-Path -Path $OutputDir -ChildPath $file
    Write-Host "Downloading $file... " -NoNewline
    try {
        Invoke-WebRequest -Uri $url -OutFile $targetPath -UseBasicParsing
        $size = (Get-Item $targetPath).Length / 1MB
        Write-Host " [THÀNH CÔNG] ($([math]::Round($size, 2)) MB)" -ForegroundColor Green
    } catch {
        Write-Host " [BỎ QUA / CHƯA CÓ TRÊN RELEASE] ($($_.Exception.Message))" -ForegroundColor Yellow
    }
}

Write-Host "`nHoàn tất tải về! Toàn bộ file lưu tại: $OutputDir" -ForegroundColor Cyan
