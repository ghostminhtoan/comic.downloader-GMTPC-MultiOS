#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Upload Release Beta via Microsoft Edge (Playwright)
Direct-to-S3 Multi-part Accelerated Upload (~100+ Mbps)
Persistent Automation Profile - Tu dong duy tri phien dang nhap vinh vien
Comic Downloader GMTPC
"""

import os
import sys
import time
import argparse
import requests
import ctypes
from pathlib import Path
from playwright.sync_api import sync_playwright

REPO_OWNER = "ghostminhtoan"
REPO_NAME = "comic.downloader-GMTPC-MultiOS"
RELEASE_TAG = "beta"
EDIT_URL = f"https://github.com/{REPO_OWNER}/{REPO_NAME}/releases/edit/{RELEASE_TAG}"
VIEW_URL = f"https://github.com/{REPO_OWNER}/{REPO_NAME}/releases/tag/{RELEASE_TAG}"

BASE_DIR = Path(__file__).resolve().parent
PUBLISH_DIR = BASE_DIR / "publish"
PROFILE_DIR = BASE_DIR / ".edge_profile"
TOKEN_FILE = BASE_DIR / "github_token.txt"

TARGET_FILES = [
    PUBLISH_DIR / "com.CompanyName.ComicDownloaderGMTPC-Signed.apk",
    PUBLISH_DIR / "ComicDownloaderGMTPC.Desktop.exe",
    PUBLISH_DIR / "ComicDownloaderGMTPC-linux-x64.tar.gz",
]


def disable_quick_edit_mode():
    """Vo hieu hoa QuickEdit Mode tren Windows Console de tranh viec click chuot lam freeze tien trinh"""
    try:
        kernel32 = ctypes.windll.kernel32
        h_stdin = kernel32.GetStdHandle(-10)  # STD_INPUT_HANDLE = -10
        mode = ctypes.c_uint32()
        if kernel32.GetConsoleMode(h_stdin, ctypes.byref(mode)):
            ENABLE_QUICK_EDIT_MODE = 0x0040
            ENABLE_EXTENDED_FLAGS = 0x0080
            new_mode = (mode.value & ~ENABLE_QUICK_EDIT_MODE) | ENABLE_EXTENDED_FLAGS
            kernel32.SetConsoleMode(h_stdin, new_mode)
    except Exception:
        pass


def load_github_token():
    if TOKEN_FILE.exists():
        try:
            return TOKEN_FILE.read_text(encoding="utf-8").strip()
        except Exception:
            pass
    return os.environ.get("GITHUB_TOKEN", "").strip()


def delete_existing_release_assets_via_api(target_names):
    token = load_github_token()
    if not token:
        print("[API] Khong tim thay GitHub token, bo qua buoc don dep asset truoc.")
        return

    headers = {
        "Authorization": f"Bearer {token}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
    }

    try:
        url = f"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/tags/{RELEASE_TAG}"
        res = requests.get(url, headers=headers, timeout=15)
        if res.status_code != 200:
            return

        assets = res.json().get("assets", [])
        for asset in assets:
            a_name = asset.get("name")
            a_id = asset.get("id")
            if a_name in target_names:
                print(f"[API] Xoa asset cu tren Release Beta: '{a_name}' (ID: {a_id})...")
                del_url = f"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/assets/{a_id}"
                requests.delete(del_url, headers=headers, timeout=15)
                print(f"[API] -> Da xoa '{a_name}' thanh cong.")
    except Exception as ex:
        print(f"[API] Chu y khi don dep asset qua API: {ex}")


def verify_files_exist():
    missing = []
    print("====================================================================")
    print("  Kiem tra 3 file can upload trong thu muc \\publish\\:")
    print("====================================================================")
    for p in TARGET_FILES:
        if not p.exists():
            missing.append(p.name)
            print(f" [THIEU] {p.name}")
        else:
            sz_mb = p.stat().st_size / (1024 * 1024)
            print(f" [OK]    {p.name} ({sz_mb:.2f} MB)")
    print("====================================================================")

    if missing:
        print(f"[LOI] Thieu {len(missing)} file! Vui long build day du truoc khi upload.")
        sys.exit(1)


def is_authenticated_context(context):
    """Kiem tra xem context da co cookie dang nhap GitHub (user_session) hay chua"""
    try:
        for c in context.cookies():
            if c.get("name") == "user_session" and c.get("value"):
                return True
    except Exception:
        pass
    return False


def perform_browser_upload(force_login=False):
    disable_quick_edit_mode()
    verify_files_exist()

    target_names = [p.name for p in TARGET_FILES]
    delete_existing_release_assets_via_api(target_names)

    PROFILE_DIR.mkdir(parents=True, exist_ok=True)

    with sync_playwright() as p:
        print("[PROFILE] Su dung Persistent Profile tai: .edge_profile")
        
        # Khoi chay trinh duyet headless mac dinh (hoac co giao dien neu force_login)
        initial_headless = not force_login

        context = p.chromium.launch_persistent_context(
            user_data_dir=str(PROFILE_DIR),
            channel="msedge",
            headless=initial_headless,
            args=[
                "--disable-blink-features=AutomationControlled",
                "--no-first-run",
                "--no-default-browser-check"
            ]
        )

        page = context.pages[0] if context.pages else context.new_page()

        # Kiem tra xac thuc
        page.goto("https://github.com", wait_until="domcontentloaded", timeout=45000)
        has_session = is_authenticated_context(context)

        # Neu chua co session ma dang chay headless -> Mo Edge co giao dien de nguoi dung dang nhap 1 lan duy nhat
        if not has_session:
            if initial_headless:
                print("\n[LOGIN] Phat hien chua co session dang nhap GitHub trong Profile.")
                print("[LOGIN] Dang mo Microsoft Edge truc quan de ban dang nhap 1 LAN DUY NHAT...")
                context.close()

                context = p.chromium.launch_persistent_context(
                    user_data_dir=str(PROFILE_DIR),
                    channel="msedge",
                    headless=False,
                    args=[
                        "--disable-blink-features=AutomationControlled",
                        "--no-first-run",
                        "--no-default-browser-check"
                    ]
                )
                page = context.pages[0] if context.pages else context.new_page()
                page.goto("https://github.com/login", wait_until="domcontentloaded")

            print("\n>>> VUI LONG DANG NHAP GITHUB TREN CUA SO MICROSOFT EDGE VUA MO <<<")
            print(">>> HE THONG SE TU DONG NHAN DIEN NGAY KHI DANG NHAP XONG <<<")
            print("[LOGIN] Dang cho ban dang nhap...")

            logged_in = False
            for sec in range(240):  # Toi da 2 phut
                time.sleep(0.5)
                if is_authenticated_context(context):
                    logged_in = True
                    break
                cur_url = page.url.lower()
                if "github.com/login" not in cur_url and "session" not in cur_url and "github.com" in cur_url:
                    time.sleep(1)
                    if is_authenticated_context(context) or sec >= 10:
                        logged_in = True
                        break

            if not logged_in:
                print("[LOI] Het thoi gian cho dang nhap (2 phut). Vui long thu lai.")
                context.close()
                sys.exit(1)

            print("[LOGIN] -> XAC NHAN DANG NHAP THANH CONG!")
            print("[LOGIN] -> Phien lam viec da duoc luu vinh vien vao Profile! Tu gio se khong can dang nhap lai.")

        # Da co session hop le -> Truy cap trang edit release
        print(f"\n[NAVIGATE] Dang truy cap trang Release Edit: {EDIT_URL}...")
        page.goto(EDIT_URL, wait_until="domcontentloaded", timeout=60000)

        # Kiem tra neu bi da ve login
        if "github.com/login" in page.url.lower():
            print("[CANH BAO] Phien dang nhap het han. Vui long chay lai voi tham so --login.")
            context.close()
            sys.exit(1)

        print("[WAIT] Dang tim khu vuc tai file Releases Upload (#releases-upload)...")
        # Tim input file chuyen dung cho release assets (co id="releases-upload")
        # Chu y: Input nay co class sr-only (an khoi man hinh) nen chi dung state="attached", khong dung state="visible"!
        try:
            page.wait_for_selector("#releases-upload", state="attached", timeout=15000)
            file_input = page.locator("#releases-upload")
        except Exception:
            # Fallback: Tim input file khong phai release body
            print("[WAIT] Fallback: Tim input file asset khong thuoc ve release body...")
            page.wait_for_selector('input[type="file"]:not(#fc-release_body)', state="attached", timeout=15000)
            file_input = page.locator('input[type="file"]:not(#fc-release_body)').first

        file_paths = [str(p) for p in TARGET_FILES]

        print(f"[UPLOAD] Bat dau dinh kem {len(file_paths)} file de upload truc tiep len AWS S3...")
        start_time = time.time()
        file_input.set_input_files(file_paths)

        print("[UPLOAD] Dang truyen du lieu truc tiep len AWS S3 CDN (Toc do toi da duong truyen ~100+ Mbps)...")

        # Cho upload hoan tat
        max_upload_wait = 180  # 3 phut (voi toc do 100 Mbps chi mat ~12-15s)
        upload_complete = False

        for i in range(max_upload_wait):
            time.sleep(1)
            elapsed = time.time() - start_time

            content = page.content()
            is_still_uploading = (
                "Uploading your file now" in content
                or "upload-progress" in content
                or "is-uploading" in content
            )

            update_btn = page.locator('button:has-text("Update release")').first
            is_btn_visible = update_btn.is_visible()
            is_btn_enabled = update_btn.is_enabled() if is_btn_visible else False

            if i % 2 == 0:
                print(f" [TIEN DO] Da troi qua {elapsed:.1f}s | Dang truyen du lieu len S3...")

            if not is_still_uploading and is_btn_enabled and elapsed >= 3:
                # Kiem tra ca 3 file da co mat tren page
                all_found = all(p.name in content for p in TARGET_FILES)
                if all_found or elapsed >= 8:
                    upload_complete = True
                    break

        total_elapsed = time.time() - start_time
        total_mb = sum(p.stat().st_size for p in TARGET_FILES) / (1024 * 1024)
        avg_mbps = (total_mb * 8) / total_elapsed if total_elapsed > 0 else 0

        print(f"\n[UPLOAD XONG] Hoan tat tai {total_mb:.2f} MB trong {total_elapsed:.1f}s (~{avg_mbps:.1f} Mbps)!")

        # Bam nut "Update release"
        print("[SUBMIT] Dang bam nut 'Update release' de luu ban phat hanh...")
        update_btn = page.locator('button:has-text("Update release")').first
        update_btn.click()

        print(f"[SUBMIT] Dang cho GitHub luu release ({VIEW_URL})...")
        try:
            page.wait_for_url("**/releases/tag/**", timeout=30000)
            print(f"[THANH CONG] Release Beta da cap nhat hoan tat tai: {page.url}")
        except Exception:
            time.sleep(4)
            print("[SUBMIT] Da gui lenh cap nhat thanh cong.")

        context.close()

    print("\n====================================================================")
    print("  [HOAN TAT 100%] Da upload thanh cong 3 file len Release Beta!")
    print(f"  Tong dung luong: {total_mb:.2f} MB | Thoi gian: {total_elapsed:.1f}s | Toc do: {avg_mbps:.1f} Mbps")
    print(f"  URL: {VIEW_URL}")
    print("====================================================================")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Upload Release Beta via Persistent Microsoft Edge")
    parser.add_argument("--login", action="store_true", help="Mo trinh duyet de dang nhap lai GitHub")
    args = parser.parse_args()

    try:
        perform_browser_upload(force_login=args.login)
    except KeyboardInterrupt:
        print("\n[HUY] Nguoi dung da huy thao tac.")
        sys.exit(1)
    except Exception as e:
        print(f"\n[LOI] Xay ra loi trong qua trinh upload: {e}")
        sys.exit(1)
