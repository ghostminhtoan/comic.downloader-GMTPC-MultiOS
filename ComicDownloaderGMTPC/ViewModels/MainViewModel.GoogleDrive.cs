using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class MainViewModel
{
    private readonly GoogleDriveSyncService _gdriveService = GoogleDriveSyncService.Instance;

    [ObservableProperty]
    private bool _isGoogleDriveModalOpen;

    [ObservableProperty]
    private bool _isGoogleDriveConnected;

    [ObservableProperty]
    private string _googleDriveUserEmail = string.Empty;

    [ObservableProperty]
    private string _googleDriveUserName = string.Empty;

    [ObservableProperty]
    private string _googleDriveStatusText = "Sẵn sàng đồng bộ";

    [ObservableProperty]
    private string _googleDriveQueueCloudInfo = "Chưa có thông tin";

    [ObservableProperty]
    private string _googleDriveReaderCloudInfo = "Chưa có thông tin";

    [ObservableProperty]
    private bool _isGoogleDriveAutoSync;

    [ObservableProperty]
    private bool _isGoogleDriveBusy;

    [ObservableProperty]
    private bool _isGoogleDriveManualCodeVisible;

    [ObservableProperty]
    private string _googleDriveManualAuthUrl = string.Empty;

    [ObservableProperty]
    private string _googleDriveManualCodeInput = string.Empty;

    [ObservableProperty]
    private string _googleDriveCustomClientId = string.Empty;

    [ObservableProperty]
    private string _googleDriveCustomClientSecret = string.Empty;

    [ObservableProperty]
    private bool _isGoogleDriveCredentialsExpanded;

    private string _lastManualCodeVerifier = string.Empty;
    private string _lastManualRedirectUri = string.Empty;

    partial void OnIsGoogleDriveAutoSyncChanged(bool value)
    {
        _gdriveService.AutoSyncEnabled = value;
    }

    /// <summary>
    /// Khởi tạo và đồng bộ trạng thái Google Drive ban đầu.
    /// </summary>
    private void InitGoogleDriveService()
    {
        IsGoogleDriveConnected = _gdriveService.IsConnected;
        GoogleDriveUserEmail = _gdriveService.UserEmail;
        GoogleDriveUserName = _gdriveService.UserName;
        IsGoogleDriveAutoSync = _gdriveService.AutoSyncEnabled;

        var (cid, secret) = _gdriveService.GetClientCredentials();
        GoogleDriveCustomClientId = cid;
        GoogleDriveCustomClientSecret = secret;

        _gdriveService.StatusChanged += status =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsGoogleDriveConnected = status.IsConnected;
                GoogleDriveUserEmail = status.AccountEmail;
                GoogleDriveUserName = status.AccountName;
                GoogleDriveStatusText = status.StatusMessage;
            });
        };

        // Liên kết callback mở Google Drive Modal từ LibraryViewModel
        LibraryVm.OpenGoogleDriveAction = () => OpenGoogleDriveModal();
    }

    [RelayCommand]
    public async Task OpenGoogleDriveModalAsync()
    {
        IsGoogleDriveModalOpen = true;
        IsGoogleDriveConnected = _gdriveService.IsConnected;
        GoogleDriveUserEmail = _gdriveService.UserEmail;
        GoogleDriveUserName = _gdriveService.UserName;
        IsGoogleDriveAutoSync = _gdriveService.AutoSyncEnabled;

        if (IsGoogleDriveConnected)
        {
            await RefreshGoogleDriveStatusAsync();
        }
        else
        {
            GoogleDriveStatusText = "Chưa kết nối tài khoản Google Drive. Bấm 'ĐĂNG NHẬP' để liên kết.";
        }
    }

    public void OpenGoogleDriveModal()
    {
        _ = OpenGoogleDriveModalAsync();
    }

    [RelayCommand]
    public void CloseGoogleDriveModal()
    {
        IsGoogleDriveModalOpen = false;
        IsGoogleDriveManualCodeVisible = false;
    }

    [RelayCommand]
    public void ToggleGoogleDriveCredentials()
    {
        IsGoogleDriveCredentialsExpanded = !IsGoogleDriveCredentialsExpanded;
    }

    [RelayCommand]
    public void SaveGoogleDriveCredentials()
    {
        _gdriveService.SaveCustomClientCredentials(GoogleDriveCustomClientId, GoogleDriveCustomClientSecret);
        GoogleDriveStatusText = "✅ Đã lưu cấu hình Client ID / Secret cá nhân thành công.";
        ShowCenterToast("LƯU CẤU HÌNH", "Đã lưu thông tin Google Client ID / Secret.", "💾", "GOOGLE DRIVE", 2000);
    }

    [RelayCommand]
    public async Task LoginGoogleDriveAsync()
    {
        if (IsGoogleDriveBusy) return;

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = "⏳ Đang mở trình duyệt để xác thực tài khoản Google...";
        try
        {
            var (ok, msg) = await _gdriveService.LoginWithBrowserAsync(GoogleDriveCustomClientId, GoogleDriveCustomClientSecret);
            if (ok)
            {
                IsGoogleDriveConnected = _gdriveService.IsConnected;
                GoogleDriveUserEmail = _gdriveService.UserEmail;
                GoogleDriveUserName = _gdriveService.UserName;
                GoogleDriveStatusText = "✅ " + msg;
                AddLog("SUCCESS", $"☁️ {msg}");
                ShowCenterToast("KẾT NỐI THÀNH CÔNG", $"Đã kết nối tài khoản: {_gdriveService.UserEmail}\nThư mục: \\GMTPC\\Comic Downloader GMTPC\\", "☁️", "GOOGLE DRIVE", 3200);
                await RefreshGoogleDriveStatusAsync();
            }
            else
            {
                GoogleDriveStatusText = "❌ " + msg;
                AddLog("WARN", $"☁️ Đăng nhập Google Drive thất bại: {msg}");
            }
        }
        catch (Exception ex)
        {
            GoogleDriveStatusText = $"❌ Lỗi đăng nhập: {ex.Message}";
        }
        finally
        {
            IsGoogleDriveBusy = false;
        }
    }

    [RelayCommand]
    public void StartManualLoginGoogleDrive()
    {
        try
        {
            var (url, verifier, redirect) = _gdriveService.CreateAuthRequest(GoogleDriveCustomClientId);
            _lastManualCodeVerifier = verifier;
            _lastManualRedirectUri = redirect;
            GoogleDriveManualAuthUrl = url;
            IsGoogleDriveManualCodeVisible = true;
            GoogleDriveStatusText = "📋 Hãy copy đường link xác thực bên dưới, mở trên trình duyệt rồi dán mã nhận được vào ô bên dưới.";

            // Mở link trên trình duyệt
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch { }
        }
        catch (Exception ex)
        {
            GoogleDriveStatusText = $"❌ Lỗi tạo link xác thực: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ConfirmManualCodeGoogleDriveAsync()
    {
        if (string.IsNullOrWhiteSpace(GoogleDriveManualCodeInput))
        {
            GoogleDriveStatusText = "⚠️ Vui lòng dán mã ủy quyền (Code) hoặc URL chuyển hướng vào ô.";
            return;
        }

        if (IsGoogleDriveBusy) return;

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = "⏳ Đang xác thực mã ủy quyền với Google...";
        try
        {
            string raw = GoogleDriveManualCodeInput.Trim();
            string code = raw;

            // Nếu người dùng dán toàn bộ URL (ví dụ http://localhost:port/callback/?code=XYZ...)
            if (raw.Contains("code="))
            {
                try
                {
                    var uri = new Uri(raw);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    string? parsedCode = query["code"];
                    if (!string.IsNullOrEmpty(parsedCode))
                    {
                        code = parsedCode;
                    }
                }
                catch { }
            }

            var (ok, msg) = await _gdriveService.ExchangeCodeForTokensAsync(
                code,
                _lastManualCodeVerifier,
                _lastManualRedirectUri,
                GoogleDriveCustomClientId,
                GoogleDriveCustomClientSecret);

            if (ok)
            {
                IsGoogleDriveConnected = _gdriveService.IsConnected;
                GoogleDriveUserEmail = _gdriveService.UserEmail;
                GoogleDriveUserName = _gdriveService.UserName;
                IsGoogleDriveManualCodeVisible = false;
                GoogleDriveManualCodeInput = string.Empty;
                GoogleDriveStatusText = "✅ " + msg;
                AddLog("SUCCESS", $"☁️ {msg}");
                ShowCenterToast("KẾT NỐI THÀNH CÔNG", $"Đã kết nối tài khoản: {_gdriveService.UserEmail}", "☁️", "GOOGLE DRIVE", 3000);
                await RefreshGoogleDriveStatusAsync();
            }
            else
            {
                GoogleDriveStatusText = "❌ " + msg;
            }
        }
        catch (Exception ex)
        {
            GoogleDriveStatusText = $"❌ Lỗi: {ex.Message}";
        }
        finally
        {
            IsGoogleDriveBusy = false;
        }
    }

    [RelayCommand]
    public void LogoutGoogleDrive()
    {
        _gdriveService.Logout();
        IsGoogleDriveConnected = false;
        GoogleDriveUserEmail = string.Empty;
        GoogleDriveUserName = string.Empty;
        GoogleDriveQueueCloudInfo = "Chưa kết nối";
        GoogleDriveReaderCloudInfo = "Chưa kết nối";
        GoogleDriveStatusText = "Đã đăng xuất Google Drive.";
        ShowCenterToast("ĐÃ ĐĂNG XUẤT", "Đã ngắt kết nối tài khoản Google Drive.", "ℹ️", "GOOGLE DRIVE", 2000);
    }

    [RelayCommand]
    public async Task RefreshGoogleDriveStatusAsync()
    {
        if (!_gdriveService.IsConnected || IsGoogleDriveBusy) return;

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = "⏳ Đang kiểm tra thư mục \\GMTPC\\Comic Downloader GMTPC\\ trên Google Drive...";
        try
        {
            var status = await _gdriveService.CheckCloudStatusAsync();
            if (status.QueueCloudFile.Exists)
            {
                string timeStr = status.QueueCloudFile.ModifiedTime?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "Không rõ";
                double kb = status.QueueCloudFile.Size / 1024.0;
                GoogleDriveQueueCloudInfo = $"Đã có trên Cloud ({kb:F1} KB, sửa: {timeStr})";
            }
            else
            {
                GoogleDriveQueueCloudInfo = "Chưa có trên Cloud (Sẵn sàng tải lên)";
            }

            if (status.ReaderCloudFile.Exists)
            {
                string timeStr = status.ReaderCloudFile.ModifiedTime?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "Không rõ";
                double kb = status.ReaderCloudFile.Size / 1024.0;
                GoogleDriveReaderCloudInfo = $"Đã có trên Cloud ({kb:F1} KB, sửa: {timeStr})";
            }
            else
            {
                GoogleDriveReaderCloudInfo = "Chưa có trên Cloud (Sẵn sàng tải lên)";
            }

            GoogleDriveStatusText = $"✅ Đã đồng bộ thông tin thư mục Google Drive ({DateTime.Now:HH:mm:ss}).";
        }
        catch (Exception ex)
        {
            GoogleDriveStatusText = $"⚠️ Lỗi kiểm tra trạng thái: {ex.Message}";
        }
        finally
        {
            IsGoogleDriveBusy = false;
        }
    }

    [RelayCommand]
    public async Task BackupQueueToGoogleDriveAsync()
    {
        if (!_gdriveService.IsConnected)
        {
            GoogleDriveStatusText = "⚠️ Vui lòng đăng nhập Google Drive trước khi sao lưu.";
            return;
        }

        if (IsGoogleDriveBusy) return;

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = "⏳ Đang chuẩn bị và tải file autosave.md lên Google Drive...";
        try
        {
            SyncAutoSaveSettingsToService();
            var (ok, msg) = await _gdriveService.BackupQueueToCloudAsync(ComicBooks);
            if (ok)
            {
                GoogleDriveStatusText = "✅ " + msg;
                AddLog("SUCCESS", $"☁️ {msg}");
                ShowCenterToast("SAO LƯU QUEUE THÀNH CÔNG", $"Đã tải lên tệp autosave.md ({ComicBooks.Count} truyện) vào Google Drive.", "☁️", "GOOGLE DRIVE", 2800);
                await RefreshGoogleDriveStatusAsync();
            }
            else
            {
                GoogleDriveStatusText = "❌ " + msg;
                AddLog("ERROR", $"☁️ Lỗi sao lưu Queue: {msg}");
            }
        }
        catch (Exception ex)
        {
            GoogleDriveStatusText = $"❌ Lỗi: {ex.Message}";
        }
        finally
        {
            IsGoogleDriveBusy = false;
        }
    }

    [RelayCommand]
    public async Task RestoreQueueFromGoogleDriveAsync()
    {
        if (!_gdriveService.IsConnected)
        {
            GoogleDriveStatusText = "⚠️ Vui lòng đăng nhập Google Drive trước khi phục hồi.";
            return;
        }

        if (IsGoogleDriveBusy) return;

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = "⏳ Đang tải file autosave.md từ Google Drive về máy...";
        try
        {
            var (ok, result, msg) = await _gdriveService.RestoreQueueFromCloudAsync();
            if (ok && result != null && result.Items.Count > 0)
            {
                ComicBooks.Clear();
                foreach (var item in result.Items)
                {
                    ComicBooks.Add(item);
                }

                if (result.ConcurrentComicDownloads.HasValue)
                {
                    ConcurrentComicDownloads = result.ConcurrentComicDownloads.Value;
                }
                if (result.ImageDownloadThreads.HasValue)
                {
                    ImageDownloadThreads = result.ImageDownloadThreads.Value;
                }
                if (result.LinkCpuThreads.HasValue)
                {
                    LinkCpuThreads = result.LinkCpuThreads.Value;
                }

                QueueBackupService.Instance.TriggerDebouncedAutoSave(ComicBooks);

                GoogleDriveStatusText = $"✅ {msg}";
                AddLog("SUCCESS", $"☁️ {msg}");
                ShowCenterToast("PHỤC HỒI QUEUE THÀNH CÔNG", $"Đã khôi phục {result.Items.Count} truyện từ Google Drive vào hàng chờ.", "☁️", "GOOGLE DRIVE", 3000);
            }
            else
            {
                GoogleDriveStatusText = "❌ " + msg;
            }
        }
        catch (Exception ex)
        {
            GoogleDriveStatusText = $"❌ Lỗi phục hồi Queue: {ex.Message}";
        }
        finally
        {
            IsGoogleDriveBusy = false;
        }
    }

    [RelayCommand]
    public async Task BackupReaderToGoogleDriveAsync()
    {
        if (!_gdriveService.IsConnected)
        {
            GoogleDriveStatusText = "⚠️ Vui lòng đăng nhập Google Drive trước khi sao lưu.";
            return;
        }

        if (IsGoogleDriveBusy) return;

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = "⏳ Đang tạo bản sao lưu Markdown và tải lên Google Drive...";
        try
        {
            var (ok, msg) = await _gdriveService.BackupReaderToCloudAsync();
            if (ok)
            {
                GoogleDriveStatusText = "✅ " + msg;
                AddLog("SUCCESS", $"☁️ {msg}");
                ShowCenterToast("SAO LƯU READER THÀNH CÔNG", "Đã lưu dấu trang & lịch sử đọc lên Google Drive.", "☁️", "GOOGLE DRIVE", 2800);
                await RefreshGoogleDriveStatusAsync();
            }
            else
            {
                GoogleDriveStatusText = "❌ " + msg;
            }
        }
        catch (Exception ex)
        {
            GoogleDriveStatusText = $"❌ Lỗi: {ex.Message}";
        }
        finally
        {
            IsGoogleDriveBusy = false;
        }
    }

    [RelayCommand]
    public async Task RestoreReaderFromGoogleDriveAsync()
    {
        if (!_gdriveService.IsConnected)
        {
            GoogleDriveStatusText = "⚠️ Vui lòng đăng nhập Google Drive trước khi phục hồi.";
            return;
        }

        if (IsGoogleDriveBusy) return;

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = "⏳ Đang tải saved_reader_bookmarks.md từ Google Drive...";
        try
        {
            var (ok, result, msg) = await _gdriveService.RestoreReaderFromCloudAsync();
            if (ok && result != null)
            {
                // Làm mới danh sách trong LibraryViewModel
                await LibraryVm.ReloadAllLibraryStateAsync();

                GoogleDriveStatusText = $"✅ {msg}";
                AddLog("SUCCESS", $"☁️ {msg}");
                ShowCenterToast("PHỤC HỒI READER THÀNH CÔNG", $"Đã khôi phục {result.RestoredBookmarks} dấu trang & {result.RestoredHistory} lịch sử đọc.", "☁️", "GOOGLE DRIVE", 3000);
            }
            else
            {
                GoogleDriveStatusText = "❌ " + msg;
            }
        }
        catch (Exception ex)
        {
            GoogleDriveStatusText = $"❌ Lỗi phục hồi Reader: {ex.Message}";
        }
        finally
        {
            IsGoogleDriveBusy = false;
        }
    }

    [RelayCommand]
    public async Task FullSyncUploadGoogleDriveAsync()
    {
        if (!_gdriveService.IsConnected)
        {
            GoogleDriveStatusText = "⚠️ Vui lòng đăng nhập Google Drive trước.";
            return;
        }

        if (IsGoogleDriveBusy) return;

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = "⏳ Đang sao lưu toàn diện cả Hàng chờ tải và Dấu trang / Lịch sử đọc lên Google Drive...";
        try
        {
            SyncAutoSaveSettingsToService();
            var result = await _gdriveService.FullSyncUploadAsync(ComicBooks);
            if (result.Success)
            {
                GoogleDriveStatusText = "✅ " + result.Message;
                AddLog("SUCCESS", $"☁️ {result.Message}");
                ShowCenterToast("ĐỒNG BỘ CLOUD THÀNH CÔNG", $"Đã sao lưu 2 trong 1 lên Google Drive:\n- {ComicBooks.Count} truyện chờ tải\n- Toàn bộ Dấu trang & Lịch sử đọc", "☁️", "GOOGLE DRIVE", 3500);
                await RefreshGoogleDriveStatusAsync();
            }
            else
            {
                GoogleDriveStatusText = "⚠️ " + result.Message;
            }
        }
        catch (Exception ex)
        {
            GoogleDriveStatusText = $"❌ Lỗi: {ex.Message}";
        }
        finally
        {
            IsGoogleDriveBusy = false;
        }
    }

    [RelayCommand]
    public async Task FullSyncRestoreGoogleDriveAsync()
    {
        if (!_gdriveService.IsConnected)
        {
            GoogleDriveStatusText = "⚠️ Vui lòng đăng nhập Google Drive trước.";
            return;
        }

        if (IsGoogleDriveBusy) return;

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = "⏳ Đang tải và phục hồi toàn diện cả Queue và Reader từ Google Drive...";
        try
        {
            var (result, qResult, rResult) = await _gdriveService.FullSyncRestoreAsync();
            if (result.Success)
            {
                if (qResult != null && qResult.Items.Count > 0)
                {
                    ComicBooks.Clear();
                    foreach (var item in qResult.Items) ComicBooks.Add(item);
                    if (qResult.ConcurrentComicDownloads.HasValue) ConcurrentComicDownloads = qResult.ConcurrentComicDownloads.Value;
                    if (qResult.ImageDownloadThreads.HasValue) ImageDownloadThreads = qResult.ImageDownloadThreads.Value;
                    if (qResult.LinkCpuThreads.HasValue) LinkCpuThreads = qResult.LinkCpuThreads.Value;
                    QueueBackupService.Instance.TriggerDebouncedAutoSave(ComicBooks);
                }

                await LibraryVm.ReloadAllLibraryStateAsync();

                GoogleDriveStatusText = "✅ " + result.Message;
                AddLog("SUCCESS", $"☁️ {result.Message}");
                ShowCenterToast("PHỤC HỒI CLOUD THÀNH CÔNG", result.Message, "☁️", "GOOGLE DRIVE", 3500);
            }
            else
            {
                GoogleDriveStatusText = "❌ " + result.Message;
            }
        }
        catch (Exception ex)
        {
            GoogleDriveStatusText = $"❌ Lỗi: {ex.Message}";
        }
        finally
        {
            IsGoogleDriveBusy = false;
        }
    }

    [RelayCommand]
    public void OpenGoogleDriveFolderInBrowser()
    {
        try
        {
            string url;
            if (!string.IsNullOrEmpty(_gdriveService.TargetFolderId))
            {
                url = $"https://drive.google.com/drive/folders/{_gdriveService.TargetFolderId}";
            }
            else
            {
                url = "https://drive.google.com/drive/my-drive";
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Không thể mở trình duyệt: {ex.Message}");
        }
    }
}

