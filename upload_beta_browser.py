#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Upload Release Beta via Microsoft Edge (Playwright)
Direct-to-S3 Multi-part Accelerated Upload (~100+ Mbps)
Persistent Automation Profile - Tu dong duy tri phien dang nhap vinh vien
Speed Guard (< 20 Mbps Auto-Kill & Retry) + Precision Completion Detection
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

# Cau hinh giam sat toc do va retry
MIN_UPLOAD_SPEED_MBPS = 20.0  # Nguong toc do toi thieu bat buoc (20 Mbps ~ 2.5 MB/s)
MAX_RETRIES = 5              # So lan thu lai neu toc do < 20 Mbps hoac bi stall
GRACE_PERIOD_SEC = 5         # Thoi gian khoi tao ban dau (cho handshake S3) truoc khi kiem tra toc do


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


JS_UPLOAD_TRACKER = """
(() => {
    window.__uploadTracker = {
        files: {},
        totalLoaded: 0,
        totalBytes: 0,
        activeCount: 0,
        completedCount: 0,
        failedCount: 0,
        startTime: 0
    };

    const origOpen = XMLHttpRequest.prototype.open;
    const origSend = XMLHttpRequest.prototype.send;

    XMLHttpRequest.prototype.open = function(method, url) {
        this.__reqUrl = url || '';
        this.__isS3Upload = (
            this.__reqUrl.includes('s3.amazonaws.com') ||
            this.__reqUrl.includes('github-production-release-asset') ||
            this.__reqUrl.includes('/upload/')
        );
        return origOpen.apply(this, arguments);
    };

    XMLHttpRequest.prototype.send = function(body) {
        const isPayload = this.__isS3Upload || (body && (
            (body instanceof Blob) ||
            (body instanceof File) ||
            (body instanceof FormData)
        ));

        if (isPayload) {
            const reqId = 'req_' + Math.random().toString(36).substring(2, 9);
            this.__reqId = reqId;

            if (!window.__uploadTracker.startTime) {
                window.__uploadTracker.startTime = Date.now();
            }
            window.__uploadTracker.activeCount++;

            let approxSize = 0;
            let filename = '';
            if (body instanceof Blob || body instanceof File) {
                approxSize = body.size;
                filename = body.name || '';
            } else if (body instanceof FormData) {
                try {
                    for (let [k, v] of body.entries()) {
                        if (v instanceof File || v instanceof Blob) {
                            approxSize = v.size;
                            filename = v.name || k;
                            break;
                        }
                    }
                } catch(e) {}
            }

            const rec = {
                id: reqId,
                url: this.__reqUrl,
                filename: filename,
                loaded: 0,
                total: approxSize,
                done: false
            };
            window.__uploadTracker.files[reqId] = rec;

            if (this.upload) {
                this.upload.addEventListener('progress', (e) => {
                    rec.loaded = e.loaded;
                    if (e.total && e.total > 0) rec.total = e.total;

                    let sumLoaded = 0;
                    let sumTotal = 0;
                    for (const id in window.__uploadTracker.files) {
                        sumLoaded += window.__uploadTracker.files[id].loaded;
                        sumTotal += window.__uploadTracker.files[id].total;
                    }
                    window.__uploadTracker.totalLoaded = sumLoaded;
                    if (sumTotal > window.__uploadTracker.totalBytes) {
                        window.__uploadTracker.totalBytes = sumTotal;
                    }
                });
            }

            this.addEventListener('load', () => {
                rec.loaded = rec.total;
                rec.done = true;
                window.__uploadTracker.activeCount = Math.max(0, window.__uploadTracker.activeCount - 1);
                window.__uploadTracker.completedCount++;
            });

            this.addEventListener('error', () => {
                window.__uploadTracker.activeCount = Math.max(0, window.__uploadTracker.activeCount - 1);
                window.__uploadTracker.failedCount++;
            });

            this.addEventListener('abort', () => {
                window.__uploadTracker.activeCount = Math.max(0, window.__uploadTracker.activeCount - 1);
            });
        }
        return origSend.apply(this, arguments);
    };
})();
"""


def perform_browser_upload(force_login=False):
    disable_quick_edit_mode()
    verify_files_exist()

    target_names = [p.name for p in TARGET_FILES]
    total_target_bytes = sum(p.stat().st_size for p in TARGET_FILES)
    total_target_mb = total_target_bytes / (1024 * 1024)

    PROFILE_DIR.mkdir(parents=True, exist_ok=True)

    with sync_playwright() as p:
        print("[PROFILE] Su dung Persistent Profile tai: .edge_profile")
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

        # Inject real-time XHR upload tracker ngay khi trang duoc khoi tao
        page.add_init_script(JS_UPLOAD_TRACKER)

        # Kiem tra xac thuc
        page.goto("https://github.com", wait_until="domcontentloaded", timeout=45000)
        has_session = is_authenticated_context(context)

        # Neu chua co session ma dang chay headless -> Mo Edge co giao dien de dang nhap 1 lan duy nhat
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
                page.add_init_script(JS_UPLOAD_TRACKER)
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

        # Vong lap Retry voi Co che Kiem Soat Toc Do (Speed Guard) & Nhan Dien Finish Chinh Xac
        upload_success = False
        final_elapsed = 0
        final_speed = 0

        for attempt in range(1, MAX_RETRIES + 1):
            print("\n====================================================================")
            print(f"  [LAN THU {attempt}/{MAX_RETRIES}] Khoi dong quy trinh upload Release Beta...")
            print("====================================================================")

            # Don dep cac asset cu tren release truoc khi upload de dam bao trang thai sach
            delete_existing_release_assets_via_api(target_names)

            print(f"[NAVIGATE] Dang truy cap trang Release Edit: {EDIT_URL}...")
            page.goto(EDIT_URL, wait_until="domcontentloaded", timeout=60000)

            # Kiem tra phien dang nhap
            if "github.com/login" in page.url.lower():
                print("[CANH BAO] Phien dang nhap het han. Vui long chay lai voi tham so --login.")
                context.close()
                sys.exit(1)

            print("[WAIT] Dang tim khu vuc tai file Releases Upload (#releases-upload)...")
            try:
                page.wait_for_selector("#releases-upload", state="attached", timeout=15000)
                file_input = page.locator("#releases-upload")
            except Exception:
                print("[WAIT] Fallback: Tim input file asset khong thuoc ve release body...")
                page.wait_for_selector('input[type="file"]:not(#fc-release_body)', state="attached", timeout=15000)
                file_input = page.locator('input[type="file"]:not(#fc-release_body)').first

            # Reset bo dem upload tracker trong browser
            page.evaluate("""() => {
                if (window.__uploadTracker) {
                    window.__uploadTracker.startTime = 0;
                    window.__uploadTracker.totalLoaded = 0;
                    window.__uploadTracker.totalBytes = 0;
                    window.__uploadTracker.files = {};
                    window.__uploadTracker.activeCount = 0;
                    window.__uploadTracker.completedCount = 0;
                    window.__uploadTracker.failedCount = 0;
                }
            }""")

            file_paths = [str(p) for p in TARGET_FILES]
            print(f"[UPLOAD] Bat dau dinh kem {len(file_paths)} file ({total_target_mb:.2f} MB) de truyen truc tiep len AWS S3...")
            file_input.set_input_files(file_paths)

            start_time = time.time()
            prev_loaded = 0
            retry_needed = False
            max_upload_wait = 180  # Toi da 3 phut cho moi lan thu

            for sec in range(1, max_upload_wait + 1):
                time.sleep(1.0)
                elapsed = time.time() - start_time

                # Lay thong so tu JS tracker
                tracker = page.evaluate("() => window.__uploadTracker || {}")
                total_loaded = tracker.get("totalLoaded", 0)

                # Tinh toan toc do trung binh va toc do tuc thoi
                avg_mbps = (total_loaded * 8) / (elapsed * 1_000_000) if elapsed > 0 else 0.0
                instant_delta = max(0, total_loaded - prev_loaded)
                instant_mbps = (instant_delta * 8) / (1.0 * 1_000_000)
                prev_loaded = total_loaded

                loaded_mb = total_loaded / (1024 * 1024)
                pct = min(100.0, (total_loaded / total_target_bytes) * 100) if total_target_bytes > 0 else 0

                # Kiem tra danh sach file da render tren DOM cua trang
                ul_text = ""
                try:
                    ul_loc = page.locator("ul.uploaded-files")
                    if ul_loc.count() > 0:
                        ul_text = ul_loc.inner_text()
                except Exception:
                    pass

                all_files_in_ul = all(p.name in ul_text for p in TARGET_FILES)

                # Kiem tra xem co phan tu uploading nao dang hien thi (loai bo hoan toan template an d-none)
                uploading_count = 0
                try:
                    uploading_count = page.locator(
                        "ul.uploaded-files .upload-progress:not(.d-none), ul.uploaded-files .is-uploading, file-attachment.is-uploading"
                    ).count()
                except Exception:
                    pass

                update_btn = page.locator('button:has-text("Update release")').first
                btn_ready = update_btn.is_visible() and update_btn.is_enabled()

                # In tien do ro rang tung giay
                status_desc = f"{loaded_mb:.1f}/{total_target_mb:.1f} MB ({pct:.1f}%) | TB: {avg_mbps:.1f} Mbps | Tuc thoi: {instant_mbps:.1f} Mbps | {elapsed:.1f}s"
                print(f" [TIEN DO] {status_desc}")

                # -------------------------------------------------------------
                # 1. SPEED GUARD: Kiem tra toc do, neu < 20 Mbps -> KILL & RETRY
                # -------------------------------------------------------------
                if elapsed >= GRACE_PERIOD_SEC:
                    is_stalled = (elapsed >= 8 and total_loaded == 0)
                    is_below_threshold = (avg_mbps < MIN_UPLOAD_SPEED_MBPS and total_loaded > 0)

                    if is_stalled or is_below_threshold:
                        print(f"\n[CANH BAO TOC DO] Toc do upload trung binh dat {avg_mbps:.2f} Mbps (< nguong yeu cau {MIN_UPLOAD_SPEED_MBPS} Mbps)!")
                        print(f"[KILL & RETRY] Tu dong huy upload (window.stop) va chuan bi thu lai lan {attempt + 1}/{MAX_RETRIES}...")
                        try:
                            page.evaluate("() => window.stop()")
                        except Exception:
                            pass
                        retry_needed = True
                        break

                # -------------------------------------------------------------
                # 2. FINISH GUARD: Nhan dien hoan tat 100% chuan xac
                # -------------------------------------------------------------
                is_tracker_done = (
                    tracker.get("completedCount", 0) >= len(TARGET_FILES) or
                    (total_loaded >= total_target_bytes * 0.99 and tracker.get("activeCount", 0) == 0)
                )

                if (is_tracker_done or all_files_in_ul) and uploading_count == 0 and btn_ready and elapsed >= 3:
                    final_elapsed = elapsed
                    final_speed = avg_mbps if avg_mbps > 0 else (total_target_mb * 8 / final_elapsed)
                    print(f"\n[XAC NHAN HOAN TAT] 100% du lieu ({total_target_mb:.2f} MB) da truyen len S3 trong {final_elapsed:.1f}s (Toc do: {final_speed:.1f} Mbps)!")
                    upload_success = True
                    break

            if upload_success:
                break
            elif retry_needed:
                print(f"[RETRY] Cho 2 giay de he thong on dinh truoc khi thu lai...")
                time.sleep(2)
            else:
                print(f"[TIMEOUT] Luot upload thu {attempt} qua 180s. Thu lai...")

        if not upload_success:
            print(f"\n[LOI] Da thu {MAX_RETRIES} lan nhung khong dat toc do > {MIN_UPLOAD_SPEED_MBPS} Mbps hoac bi loi mang.")
            context.close()
            sys.exit(1)

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
    print(f"  Tong dung luong: {total_target_mb:.2f} MB | Thoi gian: {final_elapsed:.1f}s | Toc do: {final_speed:.1f} Mbps")
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
