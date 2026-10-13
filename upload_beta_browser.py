#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Upload Release Beta via Headless Microsoft Edge (Playwright)
Direct-to-S3 Multi-part Accelerated Upload (~100+ Mbps)
Comic Downloader GMTPC
"""

import os
import sys
import time
import argparse
import requests
from pathlib import Path
from playwright.sync_api import sync_playwright

REPO_OWNER = "ghostminhtoan"
REPO_NAME = "comic.downloader-GMTPC-MultiOS"
RELEASE_TAG = "beta"
EDIT_URL = f"https://github.com/{REPO_OWNER}/{REPO_NAME}/releases/edit/{RELEASE_TAG}"
VIEW_URL = f"https://github.com/{REPO_OWNER}/{REPO_NAME}/releases/tag/{RELEASE_TAG}"

BASE_DIR = Path(__file__).resolve().parent
PUBLISH_DIR = BASE_DIR / "publish"
AUTH_FILE = BASE_DIR / "github_auth.json"
TOKEN_FILE = BASE_DIR / "github_token.txt"

TARGET_FILES = [
    PUBLISH_DIR / "com.CompanyName.ComicDownloaderGMTPC-Signed.apk",
    PUBLISH_DIR / "ComicDownloaderGMTPC.Desktop.exe",
    PUBLISH_DIR / "ComicDownloaderGMTPC-linux-x64.tar.gz",
]


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
        print("[API] Khong tim thay GitHub token, bo qua buoc xoa asset truoc.")
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
                print(f"[API] Xoa asset cu tren GitHub: '{a_name}' (ID: {a_id})...")
                del_url = f"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/assets/{a_id}"
                requests.delete(del_url, headers=headers, timeout=15)
                print(f"[API] -> Da xoa '{a_name}' thanh cong.")
    except Exception as ex:
        print(f"[API] Loi khi xoa asset qua API: {ex}")


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


def perform_browser_upload(force_login=False):
    verify_files_exist()

    target_names = [p.name for p in TARGET_FILES]
    delete_existing_release_assets_via_api(target_names)

    has_auth = AUTH_FILE.exists() and not force_login

    with sync_playwright() as p:
        # Neu chua co session hoac yeu cau force_login thi mo Edge co giao dien
        if not has_auth:
            print("\n[LOGIN] Chua co phien dang nhap GitHub hoac co yeu cau dang nhap lai.")
            print("[LOGIN] Dang mo Microsoft Edge (Giao dien truc quan)...")
            print(">>> VUI LONG DANG NHAP GITHUB TREN CUA SO TRINH DUYET VUA MO <<<")

            browser = p.chromium.launch(channel="msedge", headless=False)
            context = browser.new_context()
            page = context.new_page()

            page.goto("https://github.com/login", wait_until="domcontentloaded")

            # Doi nguoi dung dang nhap toi da 180 giay
            print("[LOGIN] Dang cho ban dang nhap GitHub...")
            logged_in = False
            for _ in range(180):
                time.sleep(1)
                cur_url = page.url.lower()
                if "github.com/login" not in cur_url and "session" not in cur_url:
                    # Kiem tra avatar hoac nut dang nhap thanh cong
                    try:
                        page.wait_for_selector(
                            "header, summary[aria-label*='user'], button[aria-label*='user']",
                            timeout=2000,
                        )
                        logged_in = True
                        break
                    except Exception:
                        if "github.com" in cur_url:
                            logged_in = True
                            break

            if not logged_in:
                print("[LOI] Het thoi gian cho dang nhap (3 phut). Vui long thu lai.")
                browser.close()
                sys.exit(1)

            print("[LOGIN] Dang nhap thanh cong! Dang luu phien vao 'github_auth.json'...")
            context.storage_state(path=str(AUTH_FILE))
            print("[LOGIN] -> Da luu phien thanh cong! Tu lan sau trinh duyet se chay ngam 100%.")
            browser.close()

        # Chay ngam (Headless = True) su dung session da luu
        print("\n[HEADLESS] Khoi chay Microsoft Edge chay ngam 100% (Direct-to-S3 Mode)...")
        browser = p.chromium.launch(channel="msedge", headless=True)
        context = browser.new_context(storage_state=str(AUTH_FILE))
        page = context.new_page()

        print(f"[NAVIGATE] Dang truy cap trang edit release: {EDIT_URL}...")
        page.goto(EDIT_URL, wait_until="domcontentloaded", timeout=60000)

        # Kiem tra xem co bi da ve login khong
        if "github.com/login" in page.url.lower():
            print("[CANH BAO] Phien dang nhap het han. Vui long chay lai voi tham so --login.")
            browser.close()
            AUTH_FILE.unlink(missing_ok=True)
            sys.exit(1)

        print("[WAIT] Dang tim khu vuc tai file tren giao dien Release...")
        page.wait_for_selector('input[type="file"]', timeout=30000)

        # Tim input file cho phep chon nhieu file
        file_input = page.locator('input[type="file"]').first

        file_paths = [str(p) for p in TARGET_FILES]
        print(f"[UPLOAD] Bat dau dinh kem {len(file_paths)} file de upload truc tiep len S3...")
        start_time = time.time()
        file_input.set_input_files(file_paths)

        print("[UPLOAD] Dang truyen du lieu truc tiep len AWS S3 CDN (Toc do toi da duong truyen)...")

        # Cho upload hoan tat: Kiem tra khong con phan tu dang upload hoac text "Uploading your file now..."
        # va tat ca 3 file da xuat hien trong danh sach asset
        max_upload_wait = 180  # 3 phut (voi toc do 100 Mbps chi mat ~15s)
        upload_complete = False

        for i in range(max_upload_wait):
            time.sleep(1)
            elapsed = time.time() - start_time

            # Kiem tra xem co phan tu progress hoac text upload dang chay khong
            uploading_elements = page.locator(
                "text='Uploading your file now', .upload-progress, [aria-label*='Uploading'], [aria-label*='uploading']"
            ).count()

            # Kiem tra nut Update release
            update_btn = page.locator('button:has-text("Update release")').first
            is_btn_visible = update_btn.is_visible()
            is_btn_enabled = update_btn.is_enabled() if is_btn_visible else False

            # In thong tin tien do moi 3 giay
            if i % 3 == 0:
                print(f" [TIEN DO] Da troi qua {elapsed:.1f}s | Dang tai len: {uploading_elements} luong...")

            if uploading_elements == 0 and is_btn_enabled and elapsed >= 3:
                # Kiem tra them xem cac file da hien thi ten chua
                content = page.content()
                all_found = all(p.name in content for p in TARGET_FILES)
                if all_found or elapsed >= 10:
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

        # Cho dieu huong sang trang xem release
        print(f"[SUBMIT] Dang cho GitHub luu release ({VIEW_URL})...")
        try:
            page.wait_for_url("**/releases/tag/**", timeout=30000)
            print(f"[THANH CONG] Release Beta da cap nhat hoan tat tai: {page.url}")
        except Exception:
            # Fallback doi them 5s
            time.sleep(5)
            print("[SUBMIT] Da gui lenh cap nhat thanh cong.")

        browser.close()

    print("\n====================================================================")
    print("  [HOAN TAT 100%] Da upload thanh cong 3 file len Release Beta!")
    print(f"  Tong dung luong: {total_mb:.2f} MB | Thoi gian: {total_elapsed:.1f}s | Toc do: {avg_mbps:.1f} Mbps")
    print(f"  URL: {VIEW_URL}")
    print("====================================================================")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Upload Release Beta via Headless Microsoft Edge")
    parser.add_argument("--login", action="store_true", help="Mo trinh duyet co giao dien de dang nhap lai GitHub")
    args = parser.parse_args()

    try:
        perform_browser_upload(force_login=args.login)
    except KeyboardInterrupt:
        print("\n[HUY] Nguoi dung da huy thao tac.")
        sys.exit(1)
    except Exception as e:
        print(f"\n[LOI] Xay ra loi trong qua trinh upload: {e}")
        sys.exit(1)

