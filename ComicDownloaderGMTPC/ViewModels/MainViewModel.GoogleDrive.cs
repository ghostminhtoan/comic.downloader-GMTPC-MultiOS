using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Input.Platform;
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
    private string _currentDeviceDisplayName = string.Empty;

    [ObservableProperty]
    private bool _isRenamingDevice;

    [ObservableProperty]
    private string _customDeviceNameInput = string.Empty;

    [ObservableProperty]
    private bool _isCreatingNewFolder;

    [ObservableProperty]
    private string _newFolderNameInput = string.Empty;

    [ObservableProperty]
    private bool _isInputFolderIdVisible;

    [ObservableProperty]
    private string _customFolderIdInput = string.Empty;

    public ObservableCollection<GoogleDriveDeviceSlot> AvailableCloudDevices { get; } = new();

    [ObservableProperty]
    private GoogleDriveDeviceSlot? _selectedCloudDevice;

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

    partial void OnSelectedCloudDeviceChanged(GoogleDriveDeviceSlot? value)
    {
        if (value != null)
        {
            UpdateCloudFileInfoDisplay(value.QueueCloudFile, value.ReaderCloudFile);

            // Khi người dùng chọn một folder từ danh sách, tự động liên kết thiết bị với thư mục đó
            if (!string.IsNullOrEmpty(value.DeviceFolderId))
            {
                _gdriveService.SelectActiveFolder(value.DeviceFolderId, value.DeviceFolderName);
                CurrentDeviceDisplayName = _gdriveService.CurrentDeviceName;
            }

            // Tự động nạp thông tin file theo yêu cầu (Lazy Loading) nếu chưa có dữ liệu
            if (value.QueueCloudFile == null || !value.QueueCloudFile.Exists)
            {
                _ = Task.Run(async () =>
                {
                    await _gdriveService.PopulateSlotFilesAsync(value);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (SelectedCloudDevice == value)
                        {
                            UpdateCloudFileInfoDisplay(value.QueueCloudFile, value.ReaderCloudFile);
                        }
                    });
                });
            }
        }
    }

    private void UpdateCloudFileInfoDisplay(GoogleDriveFileInfo? queueFile, GoogleDriveFileInfo? readerFile)
    {
        if (queueFile != null && queueFile.Exists)
        {
            string timeStr = queueFile.ModifiedTime?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "Không rõ";
            double kb = queueFile.Size / 1024.0;
            GoogleDriveQueueCloudInfo = $"Đã có trên Cloud ({kb:F1} KB, sửa: {timeStr})";
        }
        else
        {
            GoogleDriveQueueCloudInfo = "Chưa có trên Cloud (Sẵn sàng tải lên)";
        }

        if (readerFile != null && readerFile.Exists)
        {
            string timeStr = readerFile.ModifiedTime?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "Không rõ";
            double kb = readerFile.Size / 1024.0;
            GoogleDriveReaderCloudInfo = $"Đã có trên Cloud ({kb:F1} KB, sửa: {timeStr})";
        }
        else
        {
            GoogleDriveReaderCloudInfo = "Chưa có trên Cloud (Sẵn sàng tải lên)";
        }
    }

    [RelayCommand]
    public void StartCreateNewFolder()
    {
        NewFolderNameInput = string.Empty;
        IsCreatingNewFolder = true;
        IsRenamingDevice = false;
        IsInputFolderIdVisible = false;
    }

    [RelayCommand]
    public void CancelCreateNewFolder()
    {
        IsCreatingNewFolder = false;
        NewFolderNameInput = string.Empty;
    }

    [RelayCommand]
    public async Task CreateNewCloudFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(NewFolderNameInput))
        {
            GoogleDriveStatusText = "⚠️ Vui lòng nhập tên thư mục mới.";
            return;
        }

        string folderName = NewFolderNameInput.Trim();
        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = $"⏳ Đang tạo/nhận diện thư mục [{folderName}] trên Google Drive...";
        try
        {
            var (ok, folderId, msg) = await _gdriveService.CreateOrSelectCloudFolderAsync(folderName);
            IsCreatingNewFolder = false;
            NewFolderNameInput = string.Empty;
            CurrentDeviceDisplayName = _gdriveService.CurrentDeviceName;

            if (ok)
            {
                string icon = folderName.Contains("Android") ? "📱"
                    : folderName.Contains("Linux") ? "🐧"
                    : folderName.Contains("macOS") || folderName.Contains("Mac") ? "🍎"
                    : "🖥️";

                var existingSlot = AvailableCloudDevices.FirstOrDefault(d => d.DeviceFolderId == folderId || d.DeviceFolderName.Equals(folderName, StringComparison.OrdinalIgnoreCase));
                if (existingSlot != null)
                {
                    existingSlot.DeviceFolderName = folderName;
                    existingSlot.DeviceFolderId = folderId;
                    existingSlot.DisplayName = $"⭐ {icon} {folderName} (Đang chọn - Sẵn sàng)";
                    existingSlot.IsCurrentDevice = true;
                    SelectedCloudDevice = existingSlot;
                }
                else
                {
                    var newSlot = new GoogleDriveDeviceSlot
                    {
                        DeviceFolderId = folderId,
                        DeviceFolderName = folderName,
                        DisplayName = $"⭐ {icon} {folderName} (Đang chọn - Sẵn sàng)",
                        IsCurrentDevice = true
                    };
                    AvailableCloudDevices.Insert(0, newSlot);
                    SelectedCloudDevice = newSlot;
                }

                GoogleDriveStatusText = "✅ " + msg;
                AddLog("SUCCESS", $"☁️ {msg}");
                ShowCenterToast("THƯ MỤC GOOGLE DRIVE", msg, "📁", "GOOGLE DRIVE", 3000);
            }
            else
            {
                GoogleDriveStatusText = "❌ " + msg;
                AddLog("ERROR", $"☁️ {msg}");
                ShowCenterToast("LỖI TẠO THƯ MỤC", msg, "❌", "GOOGLE DRIVE", 3200);
            }

            await RefreshGoogleDriveStatusAsync();
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
    public void StartRenameDevice()
    {
        CustomDeviceNameInput = _gdriveService.CurrentDeviceName;
        IsRenamingDevice = true;
        IsCreatingNewFolder = false;
        IsInputFolderIdVisible = false;
    }

    [RelayCommand]
    public async Task SaveCustomDeviceNameAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomDeviceNameInput)) return;

        string newName = CustomDeviceNameInput.Trim();
        if (string.Equals(newName, _gdriveService.CurrentDeviceName, StringComparison.OrdinalIgnoreCase))
        {
            IsRenamingDevice = false;
            return;
        }

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = $"⏳ Đang cập nhật và đổi tên thư mục [{newName}] trên Google Drive...";
        try
        {
            var (ok, msg) = await _gdriveService.RenameCloudFolderAsync(newName);
            CurrentDeviceDisplayName = _gdriveService.CurrentDeviceName;
            IsRenamingDevice = false;
            if (ok)
            {
                if (SelectedCloudDevice != null)
                {
                    SelectedCloudDevice.DeviceFolderName = newName;
                    string icon = newName.Contains("Android") ? "📱"
                        : newName.Contains("Linux") ? "🐧"
                        : "🖥️";
                    SelectedCloudDevice.DisplayName = $"⭐ {icon} {newName} (Đang chọn)";
                }

                GoogleDriveStatusText = "✅ " + msg;
                AddLog("SUCCESS", $"☁️ {msg}");
                ShowCenterToast("ĐỔI TÊN THƯ MỤC", msg, "📁", "GOOGLE DRIVE", 3000);
            }
            else
            {
                GoogleDriveStatusText = "⚠️ " + msg;
                AddLog("WARN", $"☁️ {msg}");
                ShowCenterToast("ĐỔI TÊN THƯ MỤC", msg, "⚠️", "GOOGLE DRIVE", 3000);
            }
            await RefreshGoogleDriveStatusAsync();
        }
        catch (Exception ex)
        {
            GoogleDriveStatusText = $"❌ Lỗi đổi tên: {ex.Message}";
        }
        finally
        {
            IsGoogleDriveBusy = false;
        }
    }

    [RelayCommand]
    public void CancelRenameDevice()
    {
        IsRenamingDevice = false;
    }

    [RelayCommand]
    public void StartInputFolderId()
    {
        CustomFolderIdInput = string.Empty;
        IsInputFolderIdVisible = true;
        IsCreatingNewFolder = false;
        IsRenamingDevice = false;
    }

    [RelayCommand]
    public void CancelInputFolderId()
    {
        IsInputFolderIdVisible = false;
        CustomFolderIdInput = string.Empty;
    }

    [RelayCommand]
    public async Task ConfirmConnectByFolderIdAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomFolderIdInput))
        {
            GoogleDriveStatusText = "⚠️ Vui lòng nhập Folder ID hoặc URL Google Drive.";
            return;
        }

        string input = CustomFolderIdInput.Trim();
        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = "⏳ Đang kiểm tra và liên kết thư mục qua ID trên Google Drive...";
        try
        {
            var (ok, slot, msg) = await _gdriveService.ConnectByFolderIdAsync(input);
            if (ok && slot != null)
            {
                IsInputFolderIdVisible = false;
                CustomFolderIdInput = string.Empty;
                CurrentDeviceDisplayName = slot.DeviceFolderName;

                var existing = AvailableCloudDevices.FirstOrDefault(d => d.DeviceFolderId == slot.DeviceFolderId);
                if (existing != null)
                {
                    existing.DeviceFolderName = slot.DeviceFolderName;
                    existing.DisplayName = slot.DisplayName;
                    existing.IsCurrentDevice = true;
                    SelectedCloudDevice = existing;
                }
                else
                {
                    AvailableCloudDevices.Insert(0, slot);
                    SelectedCloudDevice = slot;
                }

                UpdateCloudFileInfoDisplay(slot.QueueCloudFile, slot.ReaderCloudFile);

                GoogleDriveStatusText = "✅ " + msg;
                AddLog("SUCCESS", $"☁️ {msg}");
                ShowCenterToast("LIÊN KẾT THƯ MỤC ID", msg, "🔗", "GOOGLE DRIVE", 3000);
            }
            else
            {
                GoogleDriveStatusText = "❌ " + msg;
                AddLog("WARN", $"☁️ {msg}");
                ShowCenterToast("LỖI LIÊN KẾT ID", msg, "❌", "GOOGLE DRIVE", 3200);
            }
        }
        catch (Exception ex)
        {
            GoogleDriveStatusText = $"❌ Lỗi liên kết ID: {ex.Message}";
        }
        finally
        {
            IsGoogleDriveBusy = false;
        }
    }

    [RelayCommand]
    public async Task DeleteSelectedCloudFolderAsync()
    {
        if (SelectedCloudDevice == null || string.IsNullOrWhiteSpace(SelectedCloudDevice.DeviceFolderId))
        {
            GoogleDriveStatusText = "⚠️ Vui lòng chọn một thư mục cần xóa.";
            ShowCenterToast("CHƯA CHỌN THƯ MỤC", "Vui lòng chọn thư mục từ danh sách trước khi xóa.", "⚠️", "GOOGLE DRIVE", 2000);
            return;
        }

        var targetSlot = SelectedCloudDevice;
        if (targetSlot.IsLegacyRoot || targetSlot.DeviceFolderId.Equals(_gdriveService.CachedAppFolderId, StringComparison.OrdinalIgnoreCase))
        {
            GoogleDriveStatusText = "⚠️ Không thể xóa thư mục gốc của ứng dụng (\\GMTPC\\Comic Downloader GMTPC\\).";
            ShowCenterToast("KHÔNG THỂ XÓA", "Không được phép xóa thư mục gốc của ứng dụng.", "⛔", "GOOGLE DRIVE", 3000);
            return;
        }

        string folderName = targetSlot.DeviceFolderName;
        string folderId = targetSlot.DeviceFolderId;

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = $"⏳ Đang xóa thư mục [{folderName}] trên Google Drive...";
        try
        {
            var (ok, msg) = await _gdriveService.DeleteCloudFolderAsync(folderId);
            if (ok)
            {
                AvailableCloudDevices.Remove(targetSlot);
                SelectedCloudDevice = AvailableCloudDevices.FirstOrDefault(d => d.IsCurrentDevice) ?? AvailableCloudDevices.FirstOrDefault();
                CurrentDeviceDisplayName = _gdriveService.CurrentDeviceName;

                GoogleDriveStatusText = $"✅ Đã xóa thư mục [{folderName}] trên Google Drive.";
                AddLog("SUCCESS", $"☁️ Đã xóa thư mục [{folderName}] trên Google Drive.");
                ShowCenterToast("ĐÃ XÓA THƯ MỤC", $"Đã xóa thành công thư mục [{folderName}] khỏi Google Drive.", "🗑️", "GOOGLE DRIVE", 3000);
            }
            else
            {
                GoogleDriveStatusText = "❌ " + msg;
                AddLog("ERROR", $"☁️ Lỗi xóa thư mục: {msg}");
                ShowCenterToast("LỖI XÓA THƯ MỤC", msg, "❌", "GOOGLE DRIVE", 3200);
            }
        }
        catch (Exception ex)
        {
            GoogleDriveStatusText = $"❌ Lỗi xóa thư mục: {ex.Message}";
        }
        finally
        {
            IsGoogleDriveBusy = false;
        }
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
        CurrentDeviceDisplayName = _gdriveService.CurrentDeviceName;

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
                CurrentDeviceDisplayName = _gdriveService.CurrentDeviceName;
                GoogleDriveStatusText = status.StatusMessage;
            });
        };

        // Liên kết callback mở Google Drive Modal từ LibraryViewModel
        LibraryVm.OpenGoogleDriveAction = () => OpenGoogleDriveModal();

        // Cung cấp handler mở trình duyệt đa nền tảng (Avalonia Launcher / Android Intent / Process.Start)
        GoogleDriveSyncService.OpenBrowserAsyncHandler = async url => await LaunchWebUrlAsync(url);
    }

    [RelayCommand]
    public async Task OpenGoogleDriveModalAsync()
    {
        IsGoogleDriveModalOpen = true;
        IsGoogleDriveConnected = _gdriveService.IsConnected;
        GoogleDriveUserEmail = _gdriveService.UserEmail;
        GoogleDriveUserName = _gdriveService.UserName;
        IsGoogleDriveAutoSync = _gdriveService.AutoSyncEnabled;
        CurrentDeviceDisplayName = _gdriveService.CurrentDeviceName;

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
    public void UseCommunityCredentials()
    {
        _gdriveService.ApplyCommunityCredentials();
        var (cid, sec) = _gdriveService.GetClientCredentials();
        GoogleDriveCustomClientId = cid;
        GoogleDriveCustomClientSecret = sec;
        GoogleDriveStatusText = "⚡ Đã kích hoạt Client ID công khai (Quick Connect). Bạn có thể bấm 'ĐĂNG NHẬP GOOGLE DRIVE' ngay!";
        ShowCenterToast("QUICK CONNECT", "Đã áp dụng Client ID công khai sẵn sàng đăng nhập.", "⚡", "GOOGLE DRIVE", 2500);
    }

    [RelayCommand]
    public async Task ImportCredentialsFileAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null && topLevel.StorageProvider.CanOpen)
            {
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Chọn tệp credentials.json tải từ Google Cloud Console",
                    AllowMultiple = false,
                    FileTypeFilter = new List<Avalonia.Platform.Storage.FilePickerFileType>
                    {
                        new("Google OAuth Client JSON (*.json)") { Patterns = new[] { "*.json" } },
                        new("All Files (*.*)") { Patterns = new[] { "*" } }
                    }
                });

                if (files != null && files.Count > 0)
                {
                    await using var stream = await files[0].OpenReadAsync();
                    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                    string json = await reader.ReadToEndAsync();
                    var (ok, cid, sec, msg) = _gdriveService.ImportCredentialsFromJson(json);
                    if (ok)
                    {
                        GoogleDriveCustomClientId = cid;
                        GoogleDriveCustomClientSecret = sec;
                        GoogleDriveStatusText = "✅ " + msg;
                        ShowCenterToast("NHẬP THÀNH CÔNG", "Đã nạp Client ID từ tệp JSON.", "✓", "GOOGLE DRIVE", 2500);
                    }
                    else
                    {
                        GoogleDriveStatusText = "❌ " + msg;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            GoogleDriveStatusText = $"❌ Lỗi nhập tệp JSON: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task OpenGoogleCloudConsoleAsync()
    {
        await LaunchWebUrlAsync("https://console.cloud.google.com/apis/credentials");
    }

    [RelayCommand]
    public async Task OpenGoogleDriveApiLibraryAsync()
    {
        await LaunchWebUrlAsync("https://console.cloud.google.com/apis/library/drive.googleapis.com");
    }

    [RelayCommand]
    public async Task LoginGoogleDriveAsync()
    {
        if (IsGoogleDriveBusy) return;

        // Nếu chạy trên Android, HttpListener loopback không hoạt động -> tự động chuyển sang Manual Code Flow
        if (OperatingSystem.IsAndroid())
        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = "⏳ Đang mở trình duyệt để xác thực tài khoản Google...";
        try
        {
            var (ok, msg) = await _gdriveService.LoginWithBrowserAsync(
                GoogleDriveCustomClientId, 
                GoogleDriveCustomClientSecret,
                onAuthPrepared: (url, verifier, redirect) =>
                {
                    _lastManualCodeVerifier = verifier;
                    _lastManualRedirectUri = redirect;
                    GoogleDriveManualAuthUrl = url;
                    IsGoogleDriveManualCodeVisible = true;
                    GoogleDriveStatusText = "⏳ Đang chờ xác thực từ trình duyệt...\nNếu trình duyệt hiện '127.0.0.1 từ chối kết nối', bạn chỉ cần sao chép liên kết thanh địa chỉ rồi bấm '📋 Dán Mã & Xác Nhận' bên dưới.";
                });

            if (ok)
            {
                IsGoogleDriveConnected = _gdriveService.IsConnected;
                GoogleDriveUserEmail = _gdriveService.UserEmail;
                GoogleDriveUserName = _gdriveService.UserName;
                IsGoogleDriveManualCodeVisible = false;
                GoogleDriveStatusText = "✅ " + msg;
                AddLog("SUCCESS", $"☁️ {msg}");
                ShowCenterToast("KẾT NỐI THÀNH CÔNG", $"Đã kết nối tài khoản: {_gdriveService.UserEmail}\nThư mục: \\GMTPC\\Comic Downloader GMTPC\\", "☁️", "GOOGLE DRIVE", 3200);
                await RefreshGoogleDriveStatusAsync();
            }
            else
            {
                GoogleDriveStatusText = "⚠️ " + msg;
                AddLog("WARN", $"☁️ {msg}");
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
    public async Task StartManualLoginGoogleDriveAsync()
    {
        try
        {
            var (url, verifier, redirect) = _gdriveService.CreateAuthRequest(GoogleDriveCustomClientId);
            _lastManualCodeVerifier = verifier;
            _lastManualRedirectUri = redirect;
            GoogleDriveManualAuthUrl = url;
            IsGoogleDriveManualCodeVisible = true;
            GoogleDriveStatusText = "📋 Hãy cấp quyền trên trình duyệt, sao chép mã Authorization Code nhận được rồi dán vào ô bên dưới.";

            // Mở link trên trình duyệt mặc định qua hệ thống Cross-Platform LaunchWebUrlAsync
            await LaunchWebUrlAsync(url);
        }
        catch (Exception ex)
        {
            GoogleDriveStatusText = $"❌ Lỗi tạo link xác thực: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ReopenManualAuthUrlAsync()
    {
        if (!string.IsNullOrWhiteSpace(GoogleDriveManualAuthUrl))
        {
            await LaunchWebUrlAsync(GoogleDriveManualAuthUrl);
        }
    }

    [RelayCommand]
    public async Task CopyManualAuthUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(GoogleDriveManualAuthUrl)) return;
        try
        {
            var clipboard = GetClipboard();
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(GoogleDriveManualAuthUrl);
                ShowCenterToast("ĐÃ CHÉP LINK", "Đã chép link đăng nhập Google Drive vào bộ nhớ tạm.", "📋", "GOOGLE DRIVE", 2000);
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Không thể chép link: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task PasteManualCodeAsync()
    {
        try
        {
            var clipboard = GetClipboard();
            if (clipboard != null)
            {
                string? text = await clipboard.TryGetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    GoogleDriveManualCodeInput = text.Trim();
                    ShowCenterToast("ĐÃ DÁN MÃ", "Đang tự động xác thực với Google...", "📋", "GOOGLE DRIVE", 1500);

                    // Tự động kiểm tra và kích hoạt xác thực ngay nếu dán link hoặc code
                    await ConfirmManualCodeGoogleDriveAsync();
                }
                else
                {
                    ShowCenterToast("BỘ NHỚ TẠM TRỐNG", "Vui lòng sao chép liên kết trên thanh địa chỉ trình duyệt trước.", "⚠️", "GOOGLE DRIVE", 2000);
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Không thể đọc bộ nhớ tạm: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task ConfirmManualCodeGoogleDriveAsync()
    {
        if (string.IsNullOrWhiteSpace(GoogleDriveManualCodeInput))
        {
            GoogleDriveStatusText = "⚠️ Vui lòng dán mã ủy quyền (Code) hoặc URL callback từ trình duyệt vào ô.";
            return;
        }

        // Hủy bỏ bất kỳ HttpListener nào đang chạy ngầm để giải phóng cổng
        _gdriveService.CancelActiveBrowserLogin();

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = "⏳ Đang xác thực mã ủy quyền với Google...";
        try
        {
            string raw = GoogleDriveManualCodeInput.Trim();
            string code = raw;
            string redirectUri = !string.IsNullOrWhiteSpace(_lastManualRedirectUri) 
                ? _lastManualRedirectUri 
                : _gdriveService.LastRedirectUri;

            string verifier = !string.IsNullOrWhiteSpace(_lastManualCodeVerifier)
                ? _lastManualCodeVerifier
                : _gdriveService.LastCodeVerifier;

            // Nếu người dùng sao chép từ thanh địa chỉ trình duyệt không có http://
            if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                (raw.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)))
            {
                raw = "http://" + raw;
            }

            // Nếu người dùng dán toàn bộ URL (ví dụ http://127.0.0.1:41751/callback/?iss=...&code=XYZ...)
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
                    // Trích xuất chính xác redirect_uri từ URL callback: ví dụ http://127.0.0.1:41751/callback/
                    redirectUri = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(verifier))
            {
                GoogleDriveStatusText = "❌ Không tìm thấy Code Verifier. Vui lòng bấm 'ĐĂNG NHẬP GOOGLE DRIVE' để tạo phiên mới.";
                return;
            }

            var (ok, msg) = await _gdriveService.ExchangeCodeForTokensAsync(
                code,
                verifier,
                redirectUri,
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
                ShowCenterToast("LỖI XÁC THỰC", msg, "❌", "GOOGLE DRIVE", 3500);
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
        GoogleDriveStatusText = "⏳ Đang kiểm tra danh sách thiết bị & tệp tin trên Google Drive...";
        try
        {
            CurrentDeviceDisplayName = _gdriveService.CurrentDeviceName;
            var currentPreservedSlot = SelectedCloudDevice;
            string? prevSelectedId = currentPreservedSlot?.DeviceFolderId ?? _gdriveService.CachedDeviceFolderId;
            string? prevSelectedName = currentPreservedSlot?.DeviceFolderName ?? _gdriveService.CurrentDeviceName;

            var status = await _gdriveService.CheckCloudStatusAsync();

            // Nếu vừa tạo/đổi tên mà Google Search API chưa kịp cập nhật index, bảo tồn slot đang chọn
            if (currentPreservedSlot != null && !string.IsNullOrEmpty(currentPreservedSlot.DeviceFolderId) &&
                !status.AvailableDevices.Any(d => d.DeviceFolderId == currentPreservedSlot.DeviceFolderId))
            {
                status.AvailableDevices.Insert(0, currentPreservedSlot);
            }

            AvailableCloudDevices.Clear();
            foreach (var dev in status.AvailableDevices)
            {
                AvailableCloudDevices.Add(dev);
            }

            // Giữ lựa chọn thiết bị trước đó nếu còn hợp lệ, hoặc chọn thiết bị hiện tại
            GoogleDriveDeviceSlot? targetSlot = null;
            if (!string.IsNullOrEmpty(prevSelectedId))
            {
                targetSlot = AvailableCloudDevices.FirstOrDefault(d => d.DeviceFolderId == prevSelectedId);
            }
            if (targetSlot == null && !string.IsNullOrEmpty(prevSelectedName))
            {
                targetSlot = AvailableCloudDevices.FirstOrDefault(d => d.DeviceFolderName.Equals(prevSelectedName, StringComparison.OrdinalIgnoreCase));
            }
            targetSlot ??= AvailableCloudDevices.FirstOrDefault(d => d.IsCurrentDevice) ?? AvailableCloudDevices.FirstOrDefault();
            SelectedCloudDevice = targetSlot;

            if (targetSlot != null)
            {
                UpdateCloudFileInfoDisplay(targetSlot.QueueCloudFile, targetSlot.ReaderCloudFile);
            }
            else
            {
                UpdateCloudFileInfoDisplay(status.QueueCloudFile, status.ReaderCloudFile);
            }

            GoogleDriveStatusText = $"✅ Đã đồng bộ thông tin ({AvailableCloudDevices.Count} thiết bị phát hiện lúc {DateTime.Now:HH:mm:ss}).";
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

        string? targetFolderId = SelectedCloudDevice?.DeviceFolderId;
        if (string.IsNullOrEmpty(targetFolderId)) targetFolderId = _gdriveService.CachedDeviceFolderId;
        string targetName = SelectedCloudDevice?.DeviceFolderName ?? _gdriveService.CurrentDeviceName;

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = $"⏳ Đang chuẩn bị và tải autosave.md lên thư mục [{targetName}]...";
        try
        {
            SyncAutoSaveSettingsToService();
            var (ok, msg) = await _gdriveService.BackupQueueToCloudAsync(ComicBooks, targetFolderId);
            if (ok)
            {
                GoogleDriveStatusText = "✅ " + msg;
                AddLog("SUCCESS", $"☁️ {msg}");
                ShowCenterToast("SAO LƯU QUEUE THÀNH CÔNG", $"Đã tải lên tệp autosave.md ({ComicBooks.Count} truyện) vào slot [{targetName}].", "☁️", "GOOGLE DRIVE", 3000);
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

        string sourceName = SelectedCloudDevice?.DeviceFolderName ?? _gdriveService.CurrentDeviceName;
        string? sourceFolderId = SelectedCloudDevice?.DeviceFolderId;

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = $"⏳ Đang tải file autosave.md từ nguồn [{sourceName}] về máy...";
        try
        {
            var (ok, result, msg) = await _gdriveService.RestoreQueueFromCloudAsync(sourceFolderId);
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
                ShowCenterToast("PHỤC HỒI QUEUE THÀNH CÔNG", $"Đã khôi phục {result.Items.Count} truyện từ nguồn [{sourceName}] vào hàng chờ.", "☁️", "GOOGLE DRIVE", 3200);
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

        string? targetFolderId = SelectedCloudDevice?.DeviceFolderId;
        if (string.IsNullOrEmpty(targetFolderId)) targetFolderId = _gdriveService.CachedDeviceFolderId;
        string targetName = SelectedCloudDevice?.DeviceFolderName ?? _gdriveService.CurrentDeviceName;

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = $"⏳ Đang tạo bản sao lưu Markdown và tải lên thư mục [{targetName}]...";
        try
        {
            var (ok, msg) = await _gdriveService.BackupReaderToCloudAsync(targetFolderId);
            if (ok)
            {
                GoogleDriveStatusText = "✅ " + msg;
                AddLog("SUCCESS", $"☁️ {msg}");
                ShowCenterToast("SAO LƯU READER THÀNH CÔNG", $"Đã lưu dấu trang & lịch sử đọc vào slot [{targetName}].", "☁️", "GOOGLE DRIVE", 3000);
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

        string sourceName = SelectedCloudDevice?.DeviceFolderName ?? _gdriveService.CurrentDeviceName;
        string? sourceFolderId = SelectedCloudDevice?.DeviceFolderId;

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = $"⏳ Đang tải saved_reader_bookmarks.md từ nguồn [{sourceName}]...";
        try
        {
            var (ok, result, msg) = await _gdriveService.RestoreReaderFromCloudAsync(sourceFolderId);
            if (ok && result != null)
            {
                // Làm mới danh sách trong LibraryViewModel
                await LibraryVm.ReloadAllLibraryStateAsync();

                GoogleDriveStatusText = $"✅ {msg}";
                AddLog("SUCCESS", $"☁️ {msg}");
                ShowCenterToast("PHỤC HỒI READER THÀNH CÔNG", $"Đã khôi phục {result.RestoredBookmarks} dấu trang & {result.RestoredHistory} lịch sử đọc từ nguồn [{sourceName}].", "☁️", "GOOGLE DRIVE", 3200);
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

        string? targetFolderId = SelectedCloudDevice?.DeviceFolderId;
        if (string.IsNullOrEmpty(targetFolderId)) targetFolderId = _gdriveService.CachedDeviceFolderId;
        string targetName = SelectedCloudDevice?.DeviceFolderName ?? _gdriveService.CurrentDeviceName;

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = $"⏳ Đang sao lưu toàn diện Queue và Reader vào thư mục [{targetName}]...";
        try
        {
            SyncAutoSaveSettingsToService();
            var result = await _gdriveService.FullSyncUploadAsync(ComicBooks, targetFolderId);
            if (result.Success)
            {
                GoogleDriveStatusText = "✅ " + result.Message;
                AddLog("SUCCESS", $"☁️ {result.Message}");
                ShowCenterToast("ĐỒNG BỘ CLOUD THÀNH CÔNG", $"Đã sao lưu 2 trong 1 vào slot [{targetName}]:\n- {ComicBooks.Count} truyện chờ tải\n- Toàn bộ Dấu trang & Lịch sử đọc", "☁️", "GOOGLE DRIVE", 3500);
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

        string sourceName = SelectedCloudDevice?.DeviceFolderName ?? _gdriveService.CurrentDeviceName;
        string? sourceFolderId = SelectedCloudDevice?.DeviceFolderId;

        IsGoogleDriveBusy = true;
        GoogleDriveStatusText = $"⏳ Đang tải và phục hồi toàn diện từ nguồn [{sourceName}]...";
        try
        {
            var (result, qResult, rResult) = await _gdriveService.FullSyncRestoreAsync(sourceFolderId);
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
                ShowCenterToast("PHỤC HỒI CLOUD THÀNH CÔNG", $"Đã phục hồi từ nguồn [{sourceName}]:\n{result.Message}", "☁️", "GOOGLE DRIVE", 3500);
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
    public async Task OpenGoogleDriveFolderInBrowserAsync()
    {
        try
        {
            string url = !string.IsNullOrEmpty(_gdriveService.TargetFolderId)
                ? $"https://drive.google.com/drive/folders/{_gdriveService.TargetFolderId}"
                : "https://drive.google.com/drive/my-drive";

            await LaunchWebUrlAsync(url);
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Không thể mở trình duyệt: {ex.Message}");
        }
    }
}


