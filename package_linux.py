#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Linux Packaging Script for Comic Downloader GMTPC Avalonia
- Creates 100% portable Linux distribution package in 'publish/' root directory.
- Produces:
  1. 'ComicDownloaderGMTPC-linux-x64.tar.gz' (Smart Portable Launcher distribution)
  2. 'runtime-net10-linux-x64.tar.gz' (Portable .NET Avalonia 10 runtime package for GitHub Release)
  3. 'ComicDownloaderGMTPC' (Standalone Launcher script in publish/)
- Removed Debian (.deb) package as requested (Portable only).
"""

import os
import sys
import io
import tarfile
import gzip
import shutil
import time

def main():
    base_dir = os.path.dirname(os.path.abspath(__file__))
    publish_dir = os.path.join(base_dir, "publish")
    staging_dir = os.path.join(publish_dir, ".linux_staging")
    
    # Locate published binary in staging_dir or publish_dir
    desktop_bin_original = None
    candidates = [
        os.path.join(staging_dir, "ComicDownloaderGMTPC.Desktop"),
        os.path.join(staging_dir, "ComicDownloaderGMTPC"),
        os.path.join(publish_dir, "ComicDownloaderGMTPC.Desktop"),
        os.path.join(publish_dir, "ComicDownloaderGMTPC")
    ]
    for c in candidates:
        if os.path.isfile(c):
            desktop_bin_original = c
            break

    if not desktop_bin_original:
        print(f"[ERROR] Linux published binary not found in {staging_dir} or {publish_dir}")
        sys.exit(1)

    print(f"[PACKAGING] Packaging Linux Portable distributions directly in: {publish_dir}")
    print(f"[PACKAGING] Source binary: {desktop_bin_original}")

    # Read binary data
    with open(desktop_bin_original, 'rb') as f:
        desktop_bin_data = f.read()

    # Read icon data
    icon_src = os.path.join(base_dir, "ComicDownloaderGMTPC.Android", "Icon.png")
    icon_data = b''
    if os.path.isfile(icon_src):
        with open(icon_src, 'rb') as f:
            icon_data = f.read()

    # 1. Tao runtime-net10-linux-x64.tar.gz (Goi runtime de launcher tu dong tai ve)
    runtime_tar_gz_path = os.path.join(publish_dir, "runtime-net10-linux-x64.tar.gz")
    print(f" -> Creating Runtime package: {runtime_tar_gz_path}...")
    with gzip.GzipFile(runtime_tar_gz_path, 'wb', mtime=0) as gz_out:
        with tarfile.open(fileobj=gz_out, mode='w') as tar:
            # Binary executable bên trong net avalonia
            bin_info = tarfile.TarInfo(name="ComicDownloaderGMTPC")
            bin_info.type = tarfile.REGTYPE
            bin_info.size = len(desktop_bin_data)
            bin_info.mode = 0o755
            bin_info.mtime = int(time.time())
            tar.addfile(bin_info, io.BytesIO(desktop_bin_data))

            # Backup alias ComicDownloaderGMTPC.Desktop
            desk_info = tarfile.TarInfo(name="ComicDownloaderGMTPC.Desktop")
            desk_info.type = tarfile.REGTYPE
            desk_info.size = len(desktop_bin_data)
            desk_info.mode = 0o755
            desk_info.mtime = int(time.time())
            tar.addfile(desk_info, io.BytesIO(desktop_bin_data))

    print(f"    [OK] Generated {runtime_tar_gz_path} ({os.path.getsize(runtime_tar_gz_path) / 1024 / 1024:.2f} MB)")

    # 2. Noi dung Launcher Script thong minh tren Linux
    launcher_sh_content = """#!/usr/bin/env bash
# ========================================================
# Comic Downloader GMTPC - Linux Portable Launcher
# ========================================================
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUNTIME_DIR="$SCRIPT_DIR/.portable/bin/net avalonia"
TARGET_BIN="$RUNTIME_DIR/ComicDownloaderGMTPC"
if [ ! -f "$TARGET_BIN" ] && [ -f "$RUNTIME_DIR/ComicDownloaderGMTPC.Desktop" ]; then
    TARGET_BIN="$RUNTIME_DIR/ComicDownloaderGMTPC.Desktop"
fi

# 1. Kiem tra xem bo thu vien .portable/bin/net avalonia da ton tai chua
if [ ! -f "$TARGET_BIN" ]; then
    echo "=============================================================="
    echo "  Comic Downloader GMTPC - Khởi tạo Thư viện Portable Linux"
    echo "=============================================================="
    echo "[*] Phát hiện chưa có thư viện tại: .portable/bin/net avalonia/"
    echo "[*] Đang tự động tải bộ thư viện .NET Avalonia 10 từ GitHub Release..."
    
    DOWNLOAD_URL="https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/beta/runtime-net10-linux-x64.tar.gz"
    TEMP_DIR="$SCRIPT_DIR/.tmp"
    mkdir -p "$TEMP_DIR"
    TEMP_FILE="$TEMP_DIR/runtime-linux-x64.tar.gz"
    
    if command -v curl >/dev/null 2>&1; then
        curl -L --progress-bar -o "$TEMP_FILE" "$DOWNLOAD_URL"
    elif command -v wget >/dev/null 2>&1; then
        wget --show-progress -O "$TEMP_FILE" "$DOWNLOAD_URL"
    else
        echo "[ERROR] Hệ thống thiếu cả curl và wget. Vui lòng cài đặt curl hoặc wget!"
        exit 1
    fi
    
    echo "[*] Đang giải nén vào: .portable/bin/net avalonia/..."
    mkdir -p "$RUNTIME_DIR"
    tar -xzf "$TEMP_FILE" -C "$RUNTIME_DIR"
    rm -f "$TEMP_FILE"
    
    TARGET_BIN="$RUNTIME_DIR/ComicDownloaderGMTPC"
    if [ ! -f "$TARGET_BIN" ] && [ -f "$RUNTIME_DIR/ComicDownloaderGMTPC.Desktop" ]; then
        TARGET_BIN="$RUNTIME_DIR/ComicDownloaderGMTPC.Desktop"
    fi
    chmod +x "$TARGET_BIN" 2>/dev/null || true
    echo "[*] Giải nén thành công 100%! Đang khởi động..."
fi

# 2. Thiet lap moi truong thuc thi va khoi chay ung dung
chmod +x "$TARGET_BIN" 2>/dev/null || true
export COMIC_DOWNLOADER_ROOT="$SCRIPT_DIR"
export DOTNET_ROOT="$RUNTIME_DIR"
export LD_LIBRARY_PATH="$RUNTIME_DIR:$SCRIPT_DIR:${LD_LIBRARY_PATH:-}"

if ! ldconfig -p 2>/dev/null | grep -q "libicu" && [ ! -f /usr/lib/x86_64-linux-gnu/libicuuc.so ] && [ ! -f /usr/lib/libicuuc.so ]; then
    export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
fi

exec "$TARGET_BIN" "$@"
""".replace('\r\n', '\n').encode('utf-8')

    # Generate desktop integration helper script
    integrate_script_content = """#!/bin/bash
# ========================================================
# Comic Downloader GMTPC - Desktop Integration Script
# Tự động tạo biểu tượng ngoài màn hình Desktop & Start Menu
# ========================================================
set -e

APP_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ICON_PATH="$APP_DIR/comicdownloader.png"
EXEC_PATH="$APP_DIR/ComicDownloaderGMTPC"

if [ ! -f "$EXEC_PATH" ]; then
    EXEC_PATH="$APP_DIR/run.sh"
fi

chmod +x "$APP_DIR/ComicDownloaderGMTPC" 2>/dev/null || true
chmod +x "$APP_DIR/run.sh" 2>/dev/null || true
chmod +x "$APP_DIR/AppRun" 2>/dev/null || true

DESKTOP_ENTRY="[Desktop Entry]
Name=Comic Downloader GMTPC
GenericName=Manga & Comic Downloader
Comment=Modern Cross-Platform Manga and Comic Downloader
Exec=\\"$EXEC_PATH\\"
Icon=$ICON_PATH
Terminal=false
Type=Application
Categories=Network;Utility;Graphics;
StartupNotify=true
StartupWMClass=ComicDownloaderGMTPC
"

# 1. Thêm vào Menu Ứng Dụng hệ thống (~/.local/share/applications)
MENU_DIR="$HOME/.local/share/applications"
mkdir -p "$MENU_DIR" 2>/dev/null || true
echo "$DESKTOP_ENTRY" > "$MENU_DIR/ComicDownloaderGMTPC.desktop"
chmod +x "$MENU_DIR/ComicDownloaderGMTPC.desktop"

# 2. Thêm vào Màn hình Desktop (~/Desktop)
if [ -d "$HOME/Desktop" ]; then
    DESKTOP_FILE="$HOME/Desktop/ComicDownloaderGMTPC.desktop"
    echo "$DESKTOP_ENTRY" > "$DESKTOP_FILE"
    chmod +x "$DESKTOP_FILE"
    # Kích hoạt chế độ Allow Launching trên Ubuntu GNOME
    if command -v gio >/dev/null 2>&1; then
        gio set "$DESKTOP_FILE" metadata::trusted true 2>/dev/null || true
    fi
fi

# 3. Cập nhật cache ứng dụng
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$MENU_DIR" 2>/dev/null || true
fi

echo "========================================================"
echo " [SUCCESS] Đã tạo biểu tượng Comic Downloader GMTPC!"
echo " -> Đã thêm vào Menu Ứng Dụng hệ thống (Start Menu / Super key)"
if [ -d "$HOME/Desktop" ]; then
    echo " -> Đã thêm vào Màn hình Desktop (~/Desktop)"
fi
echo "========================================================"
""".replace('\r\n', '\n').encode('utf-8')

    # Generate portable desktop entry file
    desktop_entry_content = """[Desktop Entry]
Name=Comic Downloader GMTPC
GenericName=Manga & Comic Downloader
Comment=Modern Cross-Platform Manga and Comic Downloader
Exec=./ComicDownloaderGMTPC
Icon=comicdownloader
Terminal=false
Type=Application
Categories=Network;Utility;Graphics;
StartupNotify=true
StartupWMClass=ComicDownloaderGMTPC
""".replace('\r\n', '\n').encode('utf-8')

    # Generate README-LINUX.txt
    readme_content = """========================================================
Comic Downloader GMTPC - Linux Portable Distribution
========================================================

1. CÁCH CHẠY BẢN PORTABLE - KHÔNG CẦN CÀI ĐẶT:
   - Mở thư mục đã giải nén (ComicDownloaderGMTPC).
   - Nhấp đúp (Double-click) trực tiếp vào file: ComicDownloaderGMTPC (hoặc run.sh / AppRun).
   - Hoặc chạy từ Terminal: ./ComicDownloaderGMTPC
   - Nếu chưa có bộ thư viện tại .portable/bin/net avalonia/, app sẽ tự động tải về và giải nén.
   - Tùy chọn tiện ích: Chạy ./integrate-desktop.sh để tự động tạo icon ra màn hình Desktop và Start Menu!

2. TƯƠNG THÍCH HỆ THỐNG:
   - Tương thích 100% các bản phân phối Linux x86_64: Ubuntu 20.04/22.04/24.04/26.04, Debian 11/12, Linux Mint, Fedora, Arch Linux...
   - Toàn bộ dữ liệu cấu hình và thư viện được lưu trữ an toàn trong thư mục cục bộ .portable/
========================================================
""".replace('\r\n', '\n').encode('utf-8')

    # 3. Tao ComicDownloaderGMTPC-linux-x64.tar.gz (Goi Portable phan phoi cho nguoi dung)
    tar_gz_path = os.path.join(publish_dir, "ComicDownloaderGMTPC-linux-x64.tar.gz")
    tar_gz_alias = os.path.join(publish_dir, "ComicDownloaderGMTPC.tar.gz")
    print(f" -> Creating Portable tar.gz archive in publish/: {tar_gz_path}...")
    
    with gzip.GzipFile(tar_gz_path, 'wb', mtime=0) as gz_out:
        with tarfile.open(fileobj=gz_out, mode='w') as tar:
            # Root directory (mode 0755)
            root_dir_info = tarfile.TarInfo(name="ComicDownloaderGMTPC")
            root_dir_info.type = tarfile.DIRTYPE
            root_dir_info.mode = 0o755
            root_dir_info.mtime = int(time.time())
            tar.addfile(root_dir_info)

            # .portable directory
            port_dir_info = tarfile.TarInfo(name="ComicDownloaderGMTPC/.portable")
            port_dir_info.type = tarfile.DIRTYPE
            port_dir_info.mode = 0o755
            port_dir_info.mtime = int(time.time())
            tar.addfile(port_dir_info)

            # .portable/bin directory
            bin_dir_info = tarfile.TarInfo(name="ComicDownloaderGMTPC/.portable/bin")
            bin_dir_info.type = tarfile.DIRTYPE
            bin_dir_info.mode = 0o755
            bin_dir_info.mtime = int(time.time())
            tar.addfile(bin_dir_info)

            # .portable/bin/net avalonia directory
            net_dir_info = tarfile.TarInfo(name="ComicDownloaderGMTPC/.portable/bin/net avalonia")
            net_dir_info.type = tarfile.DIRTYPE
            net_dir_info.mode = 0o755
            net_dir_info.mtime = int(time.time())
            tar.addfile(net_dir_info)
            
            # Launcher binary script: ComicDownloaderGMTPC (0755)
            launcher_info = tarfile.TarInfo(name="ComicDownloaderGMTPC/ComicDownloaderGMTPC")
            launcher_info.type = tarfile.REGTYPE
            launcher_info.size = len(launcher_sh_content)
            launcher_info.mode = 0o755
            launcher_info.mtime = int(time.time())
            tar.addfile(launcher_info, io.BytesIO(launcher_sh_content))
            
            # AppRun (0755)
            apprun_info = tarfile.TarInfo(name="ComicDownloaderGMTPC/AppRun")
            apprun_info.type = tarfile.REGTYPE
            apprun_info.size = len(launcher_sh_content)
            apprun_info.mode = 0o755
            apprun_info.mtime = int(time.time())
            tar.addfile(apprun_info, io.BytesIO(launcher_sh_content))

            # run.sh (0755)
            run_info = tarfile.TarInfo(name="ComicDownloaderGMTPC/run.sh")
            run_info.type = tarfile.REGTYPE
            run_info.size = len(launcher_sh_content)
            run_info.mode = 0o755
            run_info.mtime = int(time.time())
            tar.addfile(run_info, io.BytesIO(launcher_sh_content))

            # integrate-desktop.sh (0755)
            int_info = tarfile.TarInfo(name="ComicDownloaderGMTPC/integrate-desktop.sh")
            int_info.type = tarfile.REGTYPE
            int_info.size = len(integrate_script_content)
            int_info.mode = 0o755
            int_info.mtime = int(time.time())
            tar.addfile(int_info, io.BytesIO(integrate_script_content))
            
            # desktop entry (0755)
            d_info = tarfile.TarInfo(name="ComicDownloaderGMTPC/ComicDownloaderGMTPC.desktop")
            d_info.type = tarfile.REGTYPE
            d_info.size = len(desktop_entry_content)
            d_info.mode = 0o755
            d_info.mtime = int(time.time())
            tar.addfile(d_info, io.BytesIO(desktop_entry_content))
            
            # icon (0644)
            if icon_data:
                icon_info = tarfile.TarInfo(name="ComicDownloaderGMTPC/comicdownloader.png")
                icon_info.type = tarfile.REGTYPE
                icon_info.size = len(icon_data)
                icon_info.mode = 0o644
                icon_info.mtime = int(time.time())
                tar.addfile(icon_info, io.BytesIO(icon_data))
                
            # README (0644)
            readme_info = tarfile.TarInfo(name="ComicDownloaderGMTPC/README-LINUX.txt")
            readme_info.type = tarfile.REGTYPE
            readme_info.size = len(readme_content)
            readme_info.mode = 0o644
            readme_info.mtime = int(time.time())
            tar.addfile(readme_info, io.BytesIO(readme_content))

    shutil.copyfile(tar_gz_path, tar_gz_alias)
    print(f"    [OK] Generated {tar_gz_path} ({os.path.getsize(tar_gz_path) / 1024:.2f} KB)")

    # 4. Xuất file launcher ComicDownloaderGMTPC trực tiếp vào publish/
    publish_launcher = os.path.join(publish_dir, "ComicDownloaderGMTPC")
    with open(publish_launcher, 'wb') as f:
        f.write(launcher_sh_content)
    print(f"    [OK] Generated standalone launcher: {publish_launcher}")

    # Xóa sạch các gói .deb cũ nếu có trong publish để tuân thủ quy tắc 'chỉ cần mỗi phiên bản portable'
    for old_deb in ["comicdownloadergmtpc_1.0.0_amd64.deb", "ComicDownloaderGMTPC.deb"]:
        old_deb_path = os.path.join(publish_dir, old_deb)
        if os.path.isfile(old_deb_path):
            try:
                os.remove(old_deb_path)
                print(f"    [CLEANUP] Đã xóa gói .deb cũ: {old_deb}")
            except Exception:
                pass

    # Dọn dẹp staging folder nếu có
    if os.path.isdir(staging_dir):
        shutil.rmtree(staging_dir, ignore_errors=True)

    print("[SUCCESS] Linux Portable distribution packaging completed successfully in publish/!")

if __name__ == "__main__":
    main()
