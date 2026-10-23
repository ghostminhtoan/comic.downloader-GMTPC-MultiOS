#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
================================================================================
Launcher bat buoc luon luon hien thi tien trinh Build va Update Release Beta
trong cua so Command Prompt (Console Window) doc lap truc quan tren Windows Desktop.
================================================================================
"""
import os
import sys
import subprocess
from pathlib import Path

BASE_DIR = Path(__file__).resolve().parent


def run_in_visible_cmd(target_batch_name="build.bat", extra_args=None):
    if extra_args is None:
        extra_args = []

    target_path = BASE_DIR / target_batch_name
    if not target_path.exists():
        print(f"[ERROR] Khong tim thay tap tin: {target_path}")
        sys.exit(1)

    args_str = " ".join(f'"{a}"' if " " in a else a for a in extra_args)
    clean_title = target_batch_name.replace(".bat", "").replace(".cmd", "").upper()
    title = f"Comic Downloader GMTPC - {clean_title} (Progress Monitor)"

    # Cấu lệnh gọi batch script trong cửa sổ Command Prompt mới
    cmd_line = f'cmd.exe /c "title {title} && cd /d "{BASE_DIR}" && "{target_path}" {args_str}"'

    env = os.environ.copy()
    env["IN_VISIBLE_CMD"] = "1"

    # Flag CREATE_NEW_CONSOLE bắt buộc Windows cấp phát Console Window mới toanh trên Desktop
    creationflags = subprocess.CREATE_NEW_CONSOLE if os.name == "nt" else 0

    try:
        ret = subprocess.call(cmd_line, env=env, creationflags=creationflags)
        sys.exit(ret)
    except Exception as ex:
        print(f"[ERROR] Khong the khoi tao cua so Command Prompt: {ex}")
        sys.exit(1)


if __name__ == "__main__":
    args = sys.argv[1:]
    target_name = "build.bat"
    if len(args) >= 2 and args[0] == "--target":
        target_name = args[1]
        args = args[2:]
    run_in_visible_cmd(target_name, args)

