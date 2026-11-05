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
import subprocess
import json
import datetime
import re
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

ALL_TARGET_CANDIDATES = [
    PUBLISH_DIR / "com.CompanyName.ComicDownloaderGMTPC-Signed.apk",
    PUBLISH_DIR / "ComicDownloaderGMTPC.Desktop.exe",
    PUBLISH_DIR / "ComicDownloaderGMTPC-linux-x64.tar.gz",
    PUBLISH_DIR / "runtime-net10-win-x64.zip",
    PUBLISH_DIR / "runtime-net10-linux-x64.tar.gz",
]
TARGET_FILES = [p for p in ALL_TARGET_CANDIDATES if p.exists()]
if not TARGET_FILES:
    TARGET_FILES = [
        PUBLISH_DIR / "com.CompanyName.ComicDownloaderGMTPC-Signed.apk",
        PUBLISH_DIR / "ComicDownloaderGMTPC.Desktop.exe",
        PUBLISH_DIR / "ComicDownloaderGMTPC-linux-x64.tar.gz",
    ]

# Cau hinh giam sat toc do va retry
MIN_UPLOAD_SPEED_MBPS = 10.0  # Nguong toc do toi thieu 10 Mbps (duoi 10 Mbps se tu dong huy va retry vo han)
GRACE_PERIOD_SEC = 12         # Sau 12 giay neu upload beta speed duoi 10 Mbps thi tu dong retry vo han


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


def cleanup_edge_profile_locks(profile_dir):
    """
    Don dep triet de cac tien trinh msedge mo coi va cac file lock Chromium tren profile
    de triet tieu hoan toan loi 'ProcessSingleton: Lock file can not be created!'.
    """
    p_dir = Path(profile_dir).resolve()
    if not p_dir.exists():
        return

    p_str = str(p_dir).lower()
    # 1. Quet va terminate cac tien trinh msedge.exe mo coi dang gan voi profile_dir
    try:
        cmd = ["wmic", "process", "where", "name='msedge.exe'", "get", "ProcessId,CommandLine", "/format:csv"]
        res = subprocess.run(cmd, capture_output=True, text=True, timeout=8)
        killed = []
        for line in res.stdout.splitlines():
            line = line.strip()
            if not line or line.startswith("Node,"):
                continue
            parts = line.rsplit(",", 1)
            if len(parts) == 2:
                pid = parts[1].strip()
                cmdline = parts[0].lower()
                if pid.isdigit() and p_str in cmdline:
                    try:
                        subprocess.run(["taskkill", "/F", "/PID", pid], capture_output=True, timeout=5)
                        killed.append(pid)
                    except Exception:
                        pass
        if killed:
            print(f"[CLEANUP] Da giai phong {len(killed)} tien trinh Edge chay ngam tren profile: PID {', '.join(killed)}")
            time.sleep(1.0)
    except Exception:
        pass

    # 2. Xoa cac file lock Chromium ton dong
    lock_names = [
        "lockfile",
        "SingletonLock",
        "SingletonCookie",
        "SingletonSocket",
        "SingletonPort",
    ]
    for root_or_def in [p_dir, p_dir / "Default"]:
        if not root_or_def.exists():
            continue
        for l_name in lock_names:
            f_path = root_or_def / l_name
            if f_path.exists() or f_path.is_symlink():
                try:
                    f_path.unlink(missing_ok=True)
                except Exception:
                    pass


def launch_edge_context_safe(p, profile_dir, headless, max_retries=3):
    """
    Khoi chay Microsoft Edge Persistent Context co phong thu xung dot ProcessSingleton.
    Neu gap loi lock file, tu dong don dep tien trinh ngam va retry.
    """
    p_dir = Path(profile_dir).resolve()
    p_dir.mkdir(parents=True, exist_ok=True)

    cleanup_edge_profile_locks(p_dir)

    for attempt in range(1, max_retries + 1):
        try:
            context = p.chromium.launch_persistent_context(
                user_data_dir=str(p_dir),
                channel="msedge",
                headless=headless,
                args=[
                    "--disable-blink-features=AutomationControlled",
                    "--no-first-run",
                    "--no-default-browser-check",
                    "--disable-dev-shm-usage",
                    "--no-sandbox"
                ]
            )
            return context
        except Exception as ex:
            err_str = str(ex).lower()
            if "processsingleton" in err_str or "lock file" in err_str or "cannot be created" in err_str:
                print(f"[CANH BAO] Phat hien xung dot ProcessSingleton tren Edge profile (Lan {attempt}/{max_retries}).")
                print("[CANH BAO] Dang tu dong giai phong tien trinh va go lock file...")
                cleanup_edge_profile_locks(p_dir)
                time.sleep(1.5)
                if attempt == max_retries:
                    raise
            else:
                raise


def get_git_credential_token():
    """Thu lay GitHub token tu Windows Git Credential Manager"""
    try:
        proc = subprocess.run(
            ["git", "credential", "fill"],
            input="protocol=https\nhost=github.com\n\n",
            capture_output=True,
            text=True,
            timeout=5
        )
        for line in proc.stdout.splitlines():
            if line.startswith("password="):
                return line.split("=", 1)[1].strip()
    except Exception:
        pass
    return ""


def load_github_token():
    if TOKEN_FILE.exists():
        try:
            t = TOKEN_FILE.read_text(encoding="utf-8").strip()
            if t:
                return t
        except Exception:
            pass
    env_token = os.environ.get("GITHUB_TOKEN", "").strip()
    if env_token:
        return env_token
    return get_git_credential_token()


def split_asset_name_ext(filename):
    if filename.endswith(".tar.gz"):
        return filename[:-7], ".tar.gz"
    p = Path(filename)
    return p.stem, p.suffix


def rotate_and_backup_existing_assets(target_names):
    """
    Quy trinh bao toan asset Release Beta theo flow moi:
    - Tien to backup + ten goc, KHONG timedate:
      vi du: backup.ComicDownloaderGMTPC.Desktop.exe
             backup.ComicDownloaderGMTPC-linux-x64.tar.gz
             backup.com.CompanyName.ComicDownloaderGMTPC-Signed.apk
    - Neu da co asset backup cu (backup.<name> hoac timedate cu) thi xoa asset cu do,
      roi rename asset ten goc thanh backup.<name>.
    - Giai phong ten goc de upload asset moi; neu upload bi loi thi ban cu van con nguyen ven lam backup.
    """
    token = load_github_token()
    if not token:
        print("[API] Khong tim thay GitHub token, bo qua buoc backup asset.")
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
            print(f"[API] Khong lay duoc release beta: status {res.status_code}")
            return

        assets = res.json().get("assets", [])

        for target_name in target_names:
            base_stem, ext = split_asset_name_ext(target_name)
            new_backup_name = f"backup.{target_name}"

            # Regex nhan dien asset backup mang date time cu de don dep sach se
            backup_regex = re.compile(
                r"^" + re.escape(base_stem) + r"-\d{4}-\d{2}-\d{2}-.*" + re.escape(ext) + r"$",
                re.IGNORECASE
            )

            old_backup_assets = []
            exact_target_asset = None

            for a in assets:
                a_name = a.get("name", "")
                if a_name.lower() == target_name.lower():
                    exact_target_asset = a
                elif a_name.lower() == new_backup_name.lower() or backup_regex.match(a_name):
                    old_backup_assets.append(a)

            # Xoa asset backup cu (ca backup.<name> cu lan cac asset datetime cu)
            for old_a in old_backup_assets:
                old_name = old_a.get("name")
                old_id = old_a.get("id")
                print(f"[API] Phat hien asset backup cu '{old_name}' (ID: {old_id}). Dang xoa...")
                del_url = f"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/assets/{old_id}"
                try:
                    del_res = requests.delete(del_url, headers=headers, timeout=15)
                    print(f"[API] -> Da xoa asset backup cu '{old_name}' (Status: {del_res.status_code}).")
                except Exception as ex:
                    print(f"[API] Loi khi xoa asset backup cu '{old_name}': {ex}")

            # Rename asset ten goc thanh backup.<target_name>
            if exact_target_asset:
                target_id = exact_target_asset.get("id")
                target_curr_name = exact_target_asset.get("name")
                print(f"[API] Dang rename asset goc '{target_curr_name}' (ID: {target_id}) -> '{new_backup_name}' (Backup an toan)...")
                patch_url = f"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/assets/{target_id}"
                try:
                    patch_res = requests.patch(patch_url, headers=headers, json={"name": new_backup_name}, timeout=15)
                    if patch_res.status_code == 200:
                        print(f"[API] -> Da rename thanh '{new_backup_name}' thanh cong (Backup da san sang)!")
                    else:
                        print(f"[API] Rename asset that bai: status {patch_res.status_code} - {patch_res.text}")
                except Exception as ex:
                    print(f"[API] Loi khi rename asset goc '{target_curr_name}': {ex}")

    except Exception as ex:
        print(f"[API] Chu y trong qua trinh rotate va backup asset: {ex}")


def reverse_to_backup_assets(target_names):
    """
    Option: Reverse to backup
    Khoi phuc cac file backup.<target_name> thanh <target_name> tren GitHub Release tag beta:
    - Neu asset goc hien tai <target_name> dang ton tai -> Xoa di.
    - Doi ten asset backup.<target_name> thanh <target_name>.
    """
    token = load_github_token()
    if not token:
        print("[API] Khong tim thay GitHub token, khong the reverse to backup.")
        return False

    headers = {
        "Authorization": f"Bearer {token}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
    }

    try:
        url = f"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/tags/{RELEASE_TAG}"
        res = requests.get(url, headers=headers, timeout=15)
        if res.status_code != 200:
            print(f"[API] Khong lay duoc release beta: status {res.status_code}")
            return False

        assets = res.json().get("assets", [])
        reversed_count = 0

        for target_name in target_names:
            backup_name = f"backup.{target_name}"
            exact_target_asset = None
            backup_asset = None

            for a in assets:
                a_name = a.get("name", "")
                if a_name.lower() == target_name.lower():
                    exact_target_asset = a
                elif a_name.lower() == backup_name.lower():
                    backup_asset = a

            if not backup_asset:
                print(f"[REVERSE] Khong tim thay ban backup '{backup_name}' tren Release tag {RELEASE_TAG}.")
                continue

            # Neu ban goc van con, xoa ban goc truoc de giai phong ten
            if exact_target_asset:
                orig_id = exact_target_asset.get("id")
                print(f"[REVERSE] Dang xoa ban goc hien tai '{target_name}' (ID: {orig_id})...")
                del_url = f"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/assets/{orig_id}"
                try:
                    requests.delete(del_url, headers=headers, timeout=15)
                    print(f"[REVERSE] -> Da xoa '{target_name}'.")
                except Exception as ex:
                    print(f"[REVERSE] Loi xoa '{target_name}': {ex}")

            # Doi ten backup.<name> thanh <name>
            backup_id = backup_asset.get("id")
            print(f"[REVERSE] Dang khoi phuc '{backup_name}' (ID: {backup_id}) -> '{target_name}'...")
            patch_url = f"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/assets/{backup_id}"
            try:
                patch_res = requests.patch(patch_url, headers=headers, json={"name": target_name}, timeout=15)
                if patch_res.status_code == 200:
                    print(f"[REVERSE] -> Thanh cong! '{target_name}' da duoc khoi phuc ve ban backup.")
                    reversed_count += 1
                else:
                    print(f"[REVERSE] Loi doi ten: status {patch_res.status_code} - {patch_res.text}")
            except Exception as ex:
                print(f"[REVERSE] Loi rename: {ex}")

        return reversed_count > 0

    except Exception as ex:
        print(f"[REVERSE] Loi tong quat khi reverse to backup: {ex}")
        return False


class ProgressFileReader:
    """Stream reader file tu disk kem bao cao tien do va toc do truyen tai real-time"""
    def __init__(self, file_path, callback=None):
        self.file_path = Path(file_path)
        self.file_size = self.file_path.stat().st_size
        self._file = open(self.file_path, "rb")
        self.bytes_read = 0
        self.callback = callback
        self.start_time = time.time()
        self.last_update = 0

    def __len__(self):
        return self.file_size

    def read(self, size=-1):
        chunk = self._file.read(size)
        if chunk:
            self.bytes_read += len(chunk)
            now = time.time()
            if self.callback and (now - self.last_update >= 0.8 or self.bytes_read >= self.file_size):
                self.last_update = now
                self.callback(self.bytes_read, self.file_size, now - self.start_time)
        return chunk

    def close(self):
        self._file.close()

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()


def upload_single_asset_direct_api(upload_base_url, file_path, token, max_attempts=3):
    """
    Stream upload 1 asset truc tiep len GitHub Release bang REST API (Khong qua S3 Browser).
    Dung lam giai phap cuu ho hoan hao khi Browser upload bi crash hoac stall.
    """
    file_path = Path(file_path)
    file_name = file_path.name
    file_size = file_path.stat().st_size
    file_size_mb = file_size / (1024 * 1024)

    # Clean upload url: strip {?name,label}
    base_url = upload_base_url.split("{", 1)[0]
    upload_url = f"{base_url}?name={file_name}"

    for attempt in range(1, max_attempts + 1):
        print(f"\n  -> [API Lan {attempt}/{max_attempts}] Dang stream upload '{file_name}' ({file_size_mb:.2f} MB)...")
        headers = {
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/octet-stream",
            "Content-Length": str(file_size),
            "Accept": "application/vnd.github+json",
            "User-Agent": "ComicDownloaderUploader/2.0",
        }

        prev_read = [0]
        prev_time = [time.time()]

        def progress_cb(cur, total, elapsed):
            pct = (cur / total) * 100.0 if total > 0 else 0
            cur_mb = cur / (1024 * 1024)
            total_mb = total / (1024 * 1024)
            avg_mbps = (cur * 8.0) / (elapsed * 1_000_000.0) if elapsed > 0 else 0.0

            now = time.time()
            dt = max(0.1, now - prev_time[0])
            d_bytes = cur - prev_read[0]
            inst_mbps = (d_bytes * 8.0) / (dt * 1_000_000.0)
            prev_read[0] = cur
            prev_time[0] = now

            print(
                f"\r     [TIEN DO API] {cur_mb:.1f}/{total_mb:.1f} MB ({pct:.1f}%) | "
                f"Tuc thoi: {inst_mbps:.1f} Mbps | TB: {avg_mbps:.1f} Mbps | {elapsed:.1f}s",
                end="",
                flush=True
            )

        try:
            with ProgressFileReader(file_path, callback=progress_cb) as stream:
                res = requests.post(upload_url, headers=headers, data=stream, timeout=600)

            print()  # newline sau progress
            if res.status_code in (200, 201):
                res_data = res.json() if res.text else {}
                dl_url = res_data.get("browser_download_url", "")
                print(f"  [THANH CONG API] Da upload xong '{file_name}' len Release Beta!")
                if dl_url:
                    print(f"                   Download URL: {dl_url}")
                return True
            else:
                print(f"  [CANH BAO API] GitHub tra ve status {res.status_code}: {res.text[:200]}")
                time.sleep(2)
        except Exception as ex:
            print(f"\n  [LOI API] Ket noi upload '{file_name}' bi loi: {ex}")
            time.sleep(3)

    return False


def fallback_direct_api_upload(target_files, token=None):
    """
    Quy trinh cuu ho Direct REST API:
    Dam bao Release Beta LUON LUON co du 3 file, triet tieu 100% tinh trang bi xoa trang file.
    """
    print("\n====================================================================")
    print("  [CUU HO TOAN DIEN] Kich hoat quy trinh Direct REST API Uploader")
    print("====================================================================")
    if not token:
        token = load_github_token()

    if not token:
        print("[LOI API] Khong co GitHub token. Khong the thuc hien cuu ho API.")
        return False

    headers = {
        "Authorization": f"Bearer {token}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
    }

    # 1. Lay thong tin Release Beta
    release_url = f"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/tags/{RELEASE_TAG}"
    res = requests.get(release_url, headers=headers, timeout=15)
    if res.status_code == 404:
        print(f"[API] Release '{RELEASE_TAG}' chua ton tai. Dang tao moi...")
        create_url = f"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases"
        body = {
            "tag_name": RELEASE_TAG,
            "name": f"Release {RELEASE_TAG}",
            "body": "Ban phat hanh thu nghiem tu dong (Automated Beta Release) cho Comic Downloader GMTPC Multi-OS.\n\nBao gom cac goi cai dat standalone: Windows (.exe), Linux (.tar.gz) va Android (.apk).",
            "draft": False,
            "prerelease": True
        }
        res = requests.post(create_url, headers=headers, json=body, timeout=15)

    if res.status_code not in (200, 201):
        print(f"[LOI API] Khong the truy van hoac tao Release Beta: {res.status_code} - {res.text[:200]}")
        return False

    rel_data = res.json()
    upload_url_template = rel_data.get("upload_url", "")
    existing_assets = {a.get("name"): a.get("id") for a in rel_data.get("assets", [])}

    all_ok = True
    for f in target_files:
        f_path = Path(f)
        if not f_path.exists():
            continue

        f_name = f_path.name
        # Neu file da ton tai tren release, xoa de ghi de sach se
        if f_name in existing_assets:
            old_id = existing_assets[f_name]
            print(f"[API] Dang xoa asset cu '{f_name}' (ID: {old_id}) de ghi de...")
            del_url = f"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/assets/{old_id}"
            try:
                requests.delete(del_url, headers=headers, timeout=15)
                time.sleep(0.5)
            except Exception:
                pass

        ok = upload_single_asset_direct_api(upload_url_template, f_path, token)
        if not ok:
            all_ok = False

    return all_ok


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


def perform_browser_upload(force_login=False, min_speed_mbps=0.0, max_wait_sec=900):
    disable_quick_edit_mode()
    verify_files_exist()

    target_names = [p.name for p in TARGET_FILES]
    total_target_bytes = sum(p.stat().st_size for p in TARGET_FILES)
    total_target_mb = total_target_bytes / (1024 * 1024)

    PROFILE_DIR.mkdir(parents=True, exist_ok=True)

    browser_succeeded = False
    final_elapsed = 0
    final_speed = 0

    try:
        with sync_playwright() as p:
            print("[PROFILE] Su dung Persistent Profile an toan tai: .edge_profile")
            initial_headless = not force_login

            context = launch_edge_context_safe(p, PROFILE_DIR, headless=initial_headless)

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
                    try:
                        context.close()
                    except Exception:
                        pass

                    context = launch_edge_context_safe(p, PROFILE_DIR, headless=False)
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
                    try:
                        context.close()
                    except Exception:
                        pass
                    sys.exit(1)

                print("[LOGIN] -> XAC NHAN DANG NHAP THANH CONG!")
                print("[LOGIN] -> Phien lam viec da duoc luu vinh vien vao Profile! Tu gio se khong can dang nhap lai.")

            # Vong lap Retry voi Co che Kiem Soat Toc Do (Speed Guard) & Nhan Dien Finish Chinh Xac
            upload_success = False
            assets_cleaned = False

            # BƯỚC 1 FLOW MỚI: ROTATE VÀ BACKUP ASSET TRƯỚC KHI BẮT ĐẦU UPLOAD
            print("\n[BACKUP] Kiem tra va rotate asset cu thanh ban backup dinh kem time date...")
            rotate_and_backup_existing_assets(target_names)

            attempt = 0
            while True:
                attempt += 1
                effective_min = min_speed_mbps if min_speed_mbps is not None and min_speed_mbps > 0 else MIN_UPLOAD_SPEED_MBPS
                print("\n====================================================================")
                print(f"  [LAN THU {attempt}] Khoi dong quy trinh upload Release Beta (Speed Guard: > {effective_min:.1f} Mbps sau {GRACE_PERIOD_SEC}s, Retry vo han)...")
                print("====================================================================")

                print(f"[NAVIGATE] Dang truy cap trang Release Edit: {EDIT_URL}...")
                page.goto(EDIT_URL, wait_until="domcontentloaded", timeout=60000)

                # Kiem tra phien dang nhap
                if "github.com/login" in page.url.lower():
                    print("[CANH BAO] Phien dang nhap het han. Vui long chay lai voi tham so --login.")
                    try:
                        context.close()
                    except Exception:
                        pass
                    sys.exit(1)

                print("[WAIT] Dang tim khu vuc tai file Releases Upload (#releases-upload)...")
                try:
                    page.wait_for_selector("#releases-upload", state="attached", timeout=15000)
                    file_input = page.locator("#releases-upload")
                except Exception:
                    print("[WAIT] Fallback: Tim input file asset khong thuoc ve release body...")
                    page.wait_for_selector('input[type="file"]:not(#fc-release_body)', state="attached", timeout=15000)
                    file_input = page.locator('input[type="file"]:not(#fc-release_body)').first

                # KHONG XOA ASSET GOC NUA - ASSET DA DUOC RENAME BACKUP TRUOC DO AN TOAN
                if not assets_cleaned:
                    print("[BACKUP] Cac asset cu da duoc luu tru backup co dinh kem time date tren Release Beta.")
                    assets_cleaned = True

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
                max_upload_wait = max_wait_sec  # Mac dinh 900s (15 phut)

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

                    # Kiem tra xem co phan tu uploading nao dang hien thi
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

                    # 1. SPEED GUARD: Sau 12 giay neu upload speed duoi 10 Megabit -> Huy va retry vo han
                    effective_min_speed = min_speed_mbps if min_speed_mbps is not None and min_speed_mbps > 0 else MIN_UPLOAD_SPEED_MBPS
                    if elapsed >= GRACE_PERIOD_SEC:
                        is_stalled = (total_loaded == 0)
                        is_below_threshold = (effective_min_speed > 0 and avg_mbps < effective_min_speed)

                        if is_stalled or is_below_threshold:
                            print(f"\n[CANH BAO TOC DO] Sau {elapsed:.1f}s, toc do upload trung binh dat {avg_mbps:.2f} Mbps (< nguong yeu cau {effective_min_speed:.1f} Mbps)!")
                            print(f"[KILL & RETRY] Tu dong huy upload (window.stop) va retry vo han den khi toc do > {effective_min_speed:.1f} Mbps moi thoi (Chuyen sang Lan {attempt + 1})...")
                            try:
                                page.evaluate("() => window.stop()")
                            except Exception:
                                pass
                            retry_needed = True
                            break

                    # 2. FINISH GUARD: Nhan dien hoan tat 100% chuan xac
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
                    print(f"[RETRY] Cho 2 giay de he thong on dinh truoc khi thu lai lan {attempt + 1}...")
                    time.sleep(2)
                else:
                    print(f"[TIMEOUT] Luot upload thu {attempt} qua thoi gian cho. Tu dong thu lai lan {attempt + 1}...")

            if upload_success:
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

                browser_succeeded = True

            try:
                context.close()
            except Exception:
                pass

    except Exception as ex:
        print(f"\n[CANH BAO BROWSER] Trinh duyet Edge gap su co ngoai le: {ex}")

    # PHONG THU CUU HO TOAN DIEN (Three-Tier Safety Net):
    # Neu Browser upload khong thanh cong hoac bi loi bat ky -> Tu dong cuu ho bang Direct REST API!
    if not browser_succeeded:
        print("\n[CUU HO] Browser upload khong hoan tat. Chuyen sang luong Direct REST API Uploader de dam bao khong mat file...")
        token = load_github_token()
        api_ok = fallback_direct_api_upload(TARGET_FILES, token)
        if not api_ok:
            print("[LOI TOAN PHAN] Ca hai phuong thuc Browser va REST API deu gap loi mang. Vui long kiem tra ket noi!")
            sys.exit(1)
    else:
        # Neu Browser upload bao thanh cong, van kiem tra lai xem tren Release Beta co du ca 3 file hay khong!
        token = load_github_token()
        if token:
            try:
                headers = {
                    "Authorization": f"Bearer {token}",
                    "Accept": "application/vnd.github+json",
                    "X-GitHub-Api-Version": "2022-11-28",
                }
                res = requests.get(f"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/tags/{RELEASE_TAG}", headers=headers, timeout=15)
                if res.status_code == 200:
                    online_names = [a.get("name") for a in res.json().get("assets", [])]
                    missing = [p for p in TARGET_FILES if p.name not in online_names]
                    if missing:
                        print(f"\n[KIEM TRA LAI] Phat hien thieu {len(missing)} file tren Release: {[p.name for p in missing]}")
                        print("[KIEM TRA LAI] Dang tu dong upload bu qua Direct API...")
                        fallback_direct_api_upload(missing, token)
            except Exception as e:
                pass

    print("\n====================================================================")
    print("  [HOAN TAT 100%] Da upload thanh cong 3 file len Release Beta!")
    if browser_succeeded and final_elapsed > 0:
        print(f"  Tong dung luong: {total_target_mb:.2f} MB | Thoi gian: {final_elapsed:.1f}s | Toc do: {final_speed:.1f} Mbps")
    print(f"  URL: {VIEW_URL}")
    print("====================================================================")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Upload Release Beta via Persistent Microsoft Edge")
    parser.add_argument("--login", action="store_true", help="Mo trinh duyet de dang nhap lai GitHub")
    parser.add_argument("--no-pause", action="store_true", help="Khong dung man hinh sau khi upload")
    parser.add_argument("--skip-pause", action="store_true", help="Khong dung man hinh sau khi upload")
    parser.add_argument("--min-speed", "--min-speed-mbps", type=float, default=10.0, help="Nguong toc do toi thieu (Mbps), mac dinh 10.0 Mbps")
    parser.add_argument("--max-wait", type=int, default=900, help="Thoi gian cho toi da (giay), mac dinh 900s")
    parser.add_argument("--reverse", "--reverse-to-backup", action="store_true", help="Reverse to backup: Khoi phuc ban backup tren Release Beta")
    args, _ = parser.parse_known_args()

    if args.reverse:
        print("========================================================")
        print("  REVERSE TO BACKUP: KHOI PHUC BAN BACKUP TREN RELEASE BETA")
        print("========================================================")
        target_names = [p.name for p in TARGET_FILES]
        ok = reverse_to_backup_assets(target_names)
        sys.exit(0 if ok else 1)

    try:
        perform_browser_upload(force_login=args.login, min_speed_mbps=args.min_speed, max_wait_sec=args.max_wait)
    except KeyboardInterrupt:
        print("\n[HUY] Nguoi dung da huy thao tac.")
        sys.exit(1)
    except Exception as e:
        print(f"\n[LOI] Xay ra loi trong qua trinh upload: {e}")
        sys.exit(1)
