#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Linux Packaging Script for Comic Downloader GMTPC Avalonia
- Creates portable .tar.gz with explicit POSIX 0755 execute permissions and LF shebang.
- Creates standard Debian (.deb) package for Ubuntu / Debian.
"""

import os
import sys
import io
import tarfile
import gzip
import shutil
import time

def build_ar_header(filename: str, size: int, mode: int = 0o100644, mtime: int = 0, uid: int = 0, gid: int = 0) -> bytes:
    """Build a standard 60-byte UNIX ar archive header."""
    name_field = f"{filename:<16}".encode('ascii')[:16]
    mtime_field = f"{mtime:<12}".encode('ascii')[:12]
    uid_field = f"{uid:<6}".encode('ascii')[:6]
    gid_field = f"{gid:<6}".encode('ascii')[:6]
    mode_field = f"{oct(mode)[2:]:<8}".encode('ascii')[:8]
    size_field = f"{size:<10}".encode('ascii')[:10]
    trailer = b'`\n'
    header = name_field + mtime_field + uid_field + gid_field + mode_field + size_field + trailer
    assert len(header) == 60, f"Header length must be 60, got {len(header)}"
    return header

def create_ar_archive(out_path: str, members: list):
    """
    Create a standard Debian ar archive (.deb).
    members: list of (filename_str, bytes_data)
    """
    with open(out_path, 'wb') as f:
        f.write(b'!<arch>\n')
        for name, data in members:
            header = build_ar_header(name, len(data))
            f.write(header)
            f.write(data)
            if len(data) % 2 != 0:
                f.write(b'\n') # 2-byte alignment padding

def main():
    base_dir = os.path.dirname(os.path.abspath(__file__))
    publish_linux_dir = os.path.join(base_dir, "publish", "linux")
    desktop_bin = os.path.join(publish_linux_dir, "ComicDownloaderGMTPC.Desktop")
    icon_src = os.path.join(base_dir, "ComicDownloaderGMTPC.Android", "Icon.png")
    
    if not os.path.isfile(desktop_bin):
        print(f"[ERROR] Linux binary not found at: {desktop_bin}")
        sys.exit(1)
        
    print(f"[PACKAGING] Packaging Linux distributions in: {publish_linux_dir}")
    
    # 1. Copy icon to publish/linux
    icon_dst = os.path.join(publish_linux_dir, "comicdownloader.png")
    if os.path.isfile(icon_src):
        shutil.copyfile(icon_src, icon_dst)
    
    # 2. Generate run.sh with strictly LF line endings and robust environment setup
    run_sh_content = """#!/bin/bash
# ========================================================
# Launcher for Comic Downloader GMTPC (Linux Portable)
# ========================================================
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/dotnet_bundle_extract"
mkdir -p "$DOTNET_BUNDLE_EXTRACT_BASE_DIR" 2>/dev/null || true

export LD_LIBRARY_PATH="$SCRIPT_DIR:${LD_LIBRARY_PATH:-}"

# Check ICU Globalization fallback
if ! ldconfig -p 2>/dev/null | grep -q "libicu" && [ ! -f /usr/lib/x86_64-linux-gnu/libicuuc.so ] && [ ! -f /usr/lib/libicuuc.so ]; then
    export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
fi

chmod +x "$SCRIPT_DIR/ComicDownloaderGMTPC.Desktop" 2>/dev/null || true
exec "$SCRIPT_DIR/ComicDownloaderGMTPC.Desktop" "$@"
""".replace('\r\n', '\n').encode('utf-8')

    run_sh_path = os.path.join(publish_linux_dir, "run.sh")
    with open(run_sh_path, 'wb') as f:
        f.write(run_sh_content)
        
    # 3. Generate desktop entry file
    desktop_entry_content = """[Desktop Entry]
Name=Comic Downloader GMTPC
GenericName=Manga & Comic Downloader
Comment=Modern Cross-Platform Manga and Comic Downloader
Exec=./run.sh
Icon=comicdownloader
Terminal=false
Type=Application
Categories=Network;Utility;Graphics;
StartupNotify=true
StartupWMClass=ComicDownloaderGMTPC.Desktop
""".replace('\r\n', '\n').encode('utf-8')

    desktop_entry_path = os.path.join(publish_linux_dir, "comic-downloader.desktop")
    with open(desktop_entry_path, 'wb') as f:
        f.write(desktop_entry_content)

    # 4. Generate README-LINUX.txt
    readme_content = """========================================================
Comic Downloader GMTPC - Linux Portable & Debian Package
========================================================

1. HƯỚNG DẪN CHẠY BẢN PORTABLE (.tar.gz):
   - Giải nén file: tar -xvf ComicDownloaderGMTPC-linux-x64.tar.gz
   - Mở thư mục: cd ComicDownloaderGMTPC
   - Chạy ứng dụng: ./run.sh
   (Hoặc double-click vào file run.sh trong trình duyệt file)

2. HƯỚNG DẪN CÀI ĐẶT BẢN DEBIAN (.deb) CHO UBUNTU / DEBIAN / LINUX MINT:
   - Cài đặt bằng apt:
     sudo apt install ./ComicDownloaderGMTPC.deb
   - Khởi chạy trực tiếp từ Terminal bằng lệnh: comicdownloader
   - Hoặc tìm và mở "Comic Downloader GMTPC" trong menu ứng dụng hệ thống.

3. YÊU CẦU HỆ THỐNG / DEPENDENCIES:
   - Hỗ trợ x86_64 (amd64) trên Ubuntu 20.04+, Debian 11+, Fedora 34+, Arch Linux...
   - Yêu cầu thư viện cơ bản: libc6, libfontconfig1, libx11-6, libice6, libsm6, libxext6.
========================================================
""".replace('\r\n', '\n').encode('utf-8')

    readme_path = os.path.join(publish_linux_dir, "README-LINUX.txt")
    with open(readme_path, 'wb') as f:
        f.write(readme_content)

    # 5. Read binary and icon data
    with open(desktop_bin, 'rb') as f:
        desktop_bin_data = f.read()
    
    icon_data = b''
    if os.path.isfile(icon_dst):
        with open(icon_dst, 'rb') as f:
            icon_data = f.read()

    # 6. Build Portable .tar.gz
    tar_gz_path = os.path.join(publish_linux_dir, "ComicDownloaderGMTPC-linux-x64.tar.gz")
    tar_gz_alias = os.path.join(publish_linux_dir, "ComicDownloaderGMTPC.tar.gz")
    print(" -> Creating Portable tar.gz archive...")
    
    with gzip.GzipFile(tar_gz_path, 'wb', mtime=0) as gz_out:
        with tarfile.open(fileobj=gz_out, mode='w') as tar:
            # Root directory
            root_dir_info = tarfile.TarInfo(name="ComicDownloaderGMTPC")
            root_dir_info.type = tarfile.DIRTYPE
            root_dir_info.mode = 0o755
            root_dir_info.mtime = int(time.time())
            tar.addfile(root_dir_info)
            
            # Binary executable (0755)
            bin_info = tarfile.TarInfo(name="ComicDownloaderGMTPC/ComicDownloaderGMTPC.Desktop")
            bin_info.type = tarfile.REGTYPE
            bin_info.size = len(desktop_bin_data)
            bin_info.mode = 0o755
            bin_info.mtime = int(time.time())
            tar.addfile(bin_info, io.BytesIO(desktop_bin_data))
            
            # run.sh (0755)
            run_info = tarfile.TarInfo(name="ComicDownloaderGMTPC/run.sh")
            run_info.type = tarfile.REGTYPE
            run_info.size = len(run_sh_content)
            run_info.mode = 0o755
            run_info.mtime = int(time.time())
            tar.addfile(run_info, io.BytesIO(run_sh_content))
            
            # desktop entry (0755)
            d_info = tarfile.TarInfo(name="ComicDownloaderGMTPC/comic-downloader.desktop")
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
    print(f"    [OK] Generated {tar_gz_path} ({os.path.getsize(tar_gz_path) / 1024 / 1024:.2f} MB)")

    # 7. Build Debian .deb package
    deb_path = os.path.join(publish_linux_dir, "comicdownloadergmtpc_1.0.0_amd64.deb")
    deb_alias = os.path.join(publish_linux_dir, "ComicDownloaderGMTPC.deb")
    print(" -> Creating Debian package (.deb)...")
    
    # 7.1 debian-binary
    debian_binary = b"2.0\n"
    
    # 7.2 control.tar.gz
    installed_size_kb = int((len(desktop_bin_data) + len(icon_data) + 1024 * 1024) / 1024)
    control_content = f"""Package: comicdownloadergmtpc
Version: 1.0.0
Section: utils
Priority: optional
Architecture: amd64
Installed-Size: {installed_size_kb}
Maintainer: ghostminhtoan <ghostminhtoan@gmail.com>
Depends: libc6 (>= 2.27), libgcc-s1 | libgcc1, libfontconfig1, libx11-6, libice6, libsm6, libxext6
Description: Comic Downloader GMTPC - Modern Cross-Platform Manga & Comic Downloader
 Modern, fast, and multi-threaded manga/comic downloader and image processing suite.
""".replace('\r\n', '\n').encode('utf-8')

    postinst_content = """#!/bin/sh
set -e
chmod 0755 /opt/comicdownloader/ComicDownloaderGMTPC.Desktop
chmod 0755 /opt/comicdownloader/run.sh
chmod 0755 /usr/bin/comicdownloader
if which update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q || true
fi
if which gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q /usr/share/icons/hicolor 2>/dev/null || true
fi
exit 0
""".replace('\r\n', '\n').encode('utf-8')

    postrm_content = """#!/bin/sh
set -e
if which update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q || true
fi
if which gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q /usr/share/icons/hicolor 2>/dev/null || true
fi
exit 0
""".replace('\r\n', '\n').encode('utf-8')

    control_tar_buf = io.BytesIO()
    with gzip.GzipFile(fileobj=control_tar_buf, mode='wb', mtime=0) as gz_ctl:
        with tarfile.open(fileobj=gz_ctl, mode='w') as tar:
            for name, data, mode in [
                ("./control", control_content, 0o644),
                ("./postinst", postinst_content, 0o755),
                ("./postrm", postrm_content, 0o755)
            ]:
                ti = tarfile.TarInfo(name=name)
                ti.type = tarfile.REGTYPE
                ti.size = len(data)
                ti.mode = mode
                ti.uname = "root"
                ti.gname = "root"
                tar.addfile(ti, io.BytesIO(data))
    control_tar_gz = control_tar_buf.getvalue()

    # 7.3 data.tar.gz
    # Launcher in /usr/bin/comicdownloader
    usr_bin_wrapper = """#!/bin/sh
exec /opt/comicdownloader/run.sh "$@"
""".replace('\r\n', '\n').encode('utf-8')

    # System desktop entry with absolute /opt path
    sys_desktop_entry = """[Desktop Entry]
Name=Comic Downloader GMTPC
GenericName=Manga & Comic Downloader
Comment=Modern Cross-Platform Manga and Comic Downloader
Exec=/opt/comicdownloader/run.sh
Icon=comicdownloader
Terminal=false
Type=Application
Categories=Network;Utility;Graphics;
StartupNotify=true
StartupWMClass=ComicDownloaderGMTPC.Desktop
""".replace('\r\n', '\n').encode('utf-8')

    data_tar_buf = io.BytesIO()
    with gzip.GzipFile(fileobj=data_tar_buf, mode='wb', mtime=0) as gz_data:
        with tarfile.open(fileobj=gz_data, mode='w') as tar:
            # Directories
            dirs = [
                "./opt",
                "./opt/comicdownloader",
                "./usr",
                "./usr/bin",
                "./usr/share",
                "./usr/share/applications",
                "./usr/share/pixmaps",
                "./usr/share/icons",
                "./usr/share/icons/hicolor",
                "./usr/share/icons/hicolor/256x256",
                "./usr/share/icons/hicolor/256x256/apps"
            ]
            for d in dirs:
                ti = tarfile.TarInfo(name=d)
                ti.type = tarfile.DIRTYPE
                ti.mode = 0o755
                ti.uname = "root"
                ti.gname = "root"
                tar.addfile(ti)

            # Files in /opt/comicdownloader
            files_opt = [
                ("./opt/comicdownloader/ComicDownloaderGMTPC.Desktop", desktop_bin_data, 0o755),
                ("./opt/comicdownloader/run.sh", run_sh_content, 0o755),
                ("./opt/comicdownloader/comic-downloader.desktop", desktop_entry_content, 0o644),
                ("./opt/comicdownloader/comicdownloader.png", icon_data, 0o644),
                ("./opt/comicdownloader/README-LINUX.txt", readme_content, 0o644),
                ("./usr/bin/comicdownloader", usr_bin_wrapper, 0o755),
                ("./usr/share/applications/comicdownloader.desktop", sys_desktop_entry, 0o644),
                ("./usr/share/pixmaps/comicdownloader.png", icon_data, 0o644),
                ("./usr/share/icons/hicolor/256x256/apps/comicdownloader.png", icon_data, 0o644)
            ]
            for name, data, mode in files_opt:
                if not data:
                    continue
                ti = tarfile.TarInfo(name=name)
                ti.type = tarfile.REGTYPE
                ti.size = len(data)
                ti.mode = mode
                ti.uname = "root"
                ti.gname = "root"
                tar.addfile(ti, io.BytesIO(data))

    data_tar_gz = data_tar_buf.getvalue()

    # 7.4 Combine into .deb AR archive
    create_ar_archive(deb_path, [
        ("debian-binary", debian_binary),
        ("control.tar.gz", control_tar_gz),
        ("data.tar.gz", data_tar_gz)
    ])
    shutil.copyfile(deb_path, deb_alias)
    print(f"    [OK] Generated {deb_path} ({os.path.getsize(deb_path) / 1024 / 1024:.2f} MB)")
    print("[SUCCESS] Linux distribution packaging completed successfully!")

if __name__ == "__main__":
    main()
