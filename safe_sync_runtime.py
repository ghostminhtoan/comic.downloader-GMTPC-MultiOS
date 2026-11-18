#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Safe Sync Runtime Script
Đồng bộ các tệp tin .NET runtime / DLL từ staging sang thư mục đích an toàn,
cho phép ghi đè kể cả khi ComicDownloaderGMTPC đang mở và đang khóa các tệp DLL.
Trên Windows: Nếu file đích bị lock, đổi tên file cũ sang .old rồi copy file mới vào.
"""
import os
import sys
import shutil
import time
from pathlib import Path


def safe_sync(staging_dir_str, target_dir_str):
    staging_dir = Path(staging_dir_str)
    target_dir = Path(target_dir_str)

    if not staging_dir.exists():
        print(f"[WARN] Thư mục staging không tồn tại: {staging_dir}")
        return

    target_dir.mkdir(parents=True, exist_ok=True)

    # Quét toàn bộ file trong staging
    for root, _, files in os.walk(staging_dir):
        rel_root = Path(root).relative_to(staging_dir)
        dest_root = target_dir / rel_root
        dest_root.mkdir(parents=True, exist_ok=True)

        for fname in files:
            src_file = Path(root) / fname
            dest_file = dest_root / fname

            copied = False
            for attempt in range(3):
                try:
                    shutil.copy2(src_file, dest_file)
                    copied = True
                    break
                except (PermissionError, OSError):
                    # File đang bị app chạy khóa: Đổi tên file cũ sang .old để giải phóng tên file
                    old_name = f"{fname}.old_{int(time.time())}_{attempt}"
                    old_path = dest_root / old_name
                    try:
                        if dest_file.exists():
                            os.rename(dest_file, old_path)
                    except Exception:
                        pass
                    try:
                        shutil.copy2(src_file, dest_file)
                        copied = True
                        break
                    except Exception:
                        time.sleep(0.1)

            if not copied:
                print(f"[WARN] Không thể ghi đè file (vẫn tiếp tục): {dest_file}")

    # Dọn dẹp các file .old cũ nếu tiến trình đã nhả khóa
    for root, _, files in os.walk(target_dir):
        for fname in files:
            if ".old" in fname:
                old_file = Path(root) / fname
                try:
                    old_file.unlink()
                except Exception:
                    pass

    # Xóa sạch thư mục staging
    try:
        shutil.rmtree(staging_dir, ignore_errors=True)
    except Exception:
        pass


if __name__ == "__main__":
    if len(sys.argv) >= 3:
        safe_sync(sys.argv[1], sys.argv[2])
    else:
        print("Usage: python safe_sync_runtime.py <staging_dir> <target_dir>")
