using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ComicDownloaderGMTPC.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ComicDownloaderGMTPC.ViewModels;

public class FolderItemDisplay
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Icon { get; set; } = "📁";
    public string SubInfo { get; set; } = "";
}

public class FolderQuickPathItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Icon { get; set; } = "📁";
}

public partial class MainViewModel
{
    private TaskCompletionSource<string?>? _folderPickerTcs;

    [ObservableProperty]
    private bool _isFolderPickerModalOpen;

    [ObservableProperty]
    private string _folderPickerModalTitle = "Chọn thư mục";

    [ObservableProperty]
    private string _folderPickerCurrentPath = "";

    [ObservableProperty]
    private string _folderPickerManualInput = "";

    [ObservableProperty]
    private string _folderPickerStatusMessage = "";

    [ObservableProperty]
    private bool _isFolderPickerCreatingNew;

    [ObservableProperty]
    private string _folderPickerNewFolderName = "";

    public ObservableCollection<FolderItemDisplay> FolderPickerSubFolders { get; } = new();
    public ObservableCollection<FolderQuickPathItem> FolderPickerQuickPaths { get; } = new();

    public void InitInAppFolderPicker()
    {
        FolderOrFilePickerService.InAppFolderPickerHandler = (initialPath, title, language) =>
        {
            return Dispatcher.UIThread.InvokeAsync(() => ShowInAppFolderPickerAsync(initialPath, title, language));
        };
    }

    public Task<string?> ShowInAppFolderPickerAsync(string? initialPath, string? title = null, string language = "VI")
    {
        _folderPickerTcs?.TrySetResult(null);
        _folderPickerTcs = new TaskCompletionSource<string?>();

        FolderPickerModalTitle = !string.IsNullOrWhiteSpace(title)
            ? title
            : (language == "VI" ? "📁 Chọn Thư Mục" : "📁 Select Folder");

        FolderPickerStatusMessage = "";
        IsFolderPickerCreatingNew = false;
        FolderPickerNewFolderName = "";

        // Tải danh sách Quick Paths
        BuildQuickPaths();

        // Xác định thư mục khởi đầu
        string target = !string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath)
            ? initialPath
            : GetDefaultStartingFolder();

        NavigateFolderPicker(target);

        IsFolderPickerModalOpen = true;
        return _folderPickerTcs.Task;
    }

    private void BuildQuickPaths()
    {
        FolderPickerQuickPaths.Clear();

        if (OperatingSystem.IsAndroid())
        {
            // Các thư mục phổ biến trên Android
            AddQuickPathIfValid("📥 Tải về (Download)", "/storage/emulated/0/Download");
            AddQuickPathIfValid("🖼️ Hình ảnh (Pictures)", "/storage/emulated/0/Pictures");
            AddQuickPathIfValid("📷 Máy ảnh (DCIM)", "/storage/emulated/0/DCIM");
            AddQuickPathIfValid("📄 Tài liệu (Documents)", "/storage/emulated/0/Documents");
            AddQuickPathIfValid("🏠 Bộ nhớ trong", "/storage/emulated/0");

            // Thẻ nhớ ngoài (SD Card) nếu có
            try
            {
                var sdPaths = LibraryScannerService.AndroidGetExternalStoragePaths?.Invoke();
                if (sdPaths != null)
                {
                    int sdIdx = 1;
                    foreach (var sd in sdPaths)
                    {
                        if (!string.IsNullOrWhiteSpace(sd) && Directory.Exists(sd))
                        {
                            AddQuickPathIfValid($"💾 Thẻ SD {sdIdx++}", sd);
                        }
                    }
                }
            }
            catch { }
        }
        else
        {
            // Desktop / Linux
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            if (!string.IsNullOrEmpty(userProfile))
            {
                string downloads = Path.Combine(userProfile, "Downloads");
                AddQuickPathIfValid("📥 Downloads", downloads);
            }
            AddQuickPathIfValid("🖼️ Pictures", pictures);
            AddQuickPathIfValid("📄 Documents", myDocs);

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                    {
                        AddQuickPathIfValid($"💾 Ổ đĩa ({drive.Name.TrimEnd('\\')})", drive.RootDirectory.FullName);
                    }
                }
                catch { }
            }
            else
            {
                AddQuickPathIfValid("🏠 Home", userProfile);
                AddQuickPathIfValid("根 Thư mục gốc /", "/");
            }
        }
    }

    private void AddQuickPathIfValid(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path) || path.StartsWith("/storage/"))
            {
                FolderPickerQuickPaths.Add(new FolderQuickPathItem
                {
                    Name = name,
                    Path = path,
                    Icon = name.Split(' ')[0]
                });
            }
        }
        catch { }
    }

    private string GetDefaultStartingFolder()
    {
        if (OperatingSystem.IsAndroid())
        {
            if (Directory.Exists("/storage/emulated/0/Download")) return "/storage/emulated/0/Download";
            if (Directory.Exists("/storage/emulated/0/Pictures")) return "/storage/emulated/0/Pictures";
            if (Directory.Exists("/storage/emulated/0")) return "/storage/emulated/0";
            return "/sdcard";
        }

        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dl = Path.Combine(user, "Downloads");
        if (Directory.Exists(dl)) return dl;
        return AppDomain.CurrentDomain.BaseDirectory;
    }

    public void NavigateFolderPicker(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        string cleanPath = path.Trim();
        FolderPickerCurrentPath = cleanPath;
        FolderPickerManualInput = cleanPath;
        FolderPickerSubFolders.Clear();
        FolderPickerStatusMessage = "";

        try
        {
            if (!Directory.Exists(cleanPath))
            {
                FolderPickerStatusMessage = "Thư mục không tồn tại hoặc chưa được cấp quyền.";
                return;
            }

            var dirInfo = new DirectoryInfo(cleanPath);
            var subDirs = dirInfo.GetDirectories();

            var sorted = subDirs
                .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var d in sorted)
            {
                string countText = "";
                try
                {
                    int count = d.GetFileSystemInfos().Length;
                    countText = $"({count} mục)";
                }
                catch { }

                FolderPickerSubFolders.Add(new FolderItemDisplay
                {
                    Name = d.Name,
                    FullPath = d.FullName,
                    Icon = "📁",
                    SubInfo = countText
                });
            }

            if (FolderPickerSubFolders.Count == 0)
            {
                FolderPickerStatusMessage = "(Thư mục trống hoặc không có thư mục con)";
            }
        }
        catch (UnauthorizedAccessException)
        {
            FolderPickerStatusMessage = "⚠️ Không có quyền truy cập thư mục này.";
        }
        catch (Exception ex)
        {
            FolderPickerStatusMessage = $"⚠️ Lỗi: {ex.Message}";
        }
    }

    [RelayCommand]
    public void FolderPickerQuickSelect(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            NavigateFolderPicker(path);
        }
    }

    [RelayCommand]
    public void OpenFolderPickerSubItem(FolderItemDisplay item)
    {
        if (item != null && !string.IsNullOrWhiteSpace(item.FullPath))
        {
            NavigateFolderPicker(item.FullPath);
        }
    }

    [RelayCommand]
    public void FolderPickerGoUp()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(FolderPickerCurrentPath)) return;
            var parent = Directory.GetParent(FolderPickerCurrentPath);
            if (parent != null && parent.Exists)
            {
                NavigateFolderPicker(parent.FullName);
            }
        }
        catch { }
    }

    [RelayCommand]
    public void FolderPickerRefresh()
    {
        NavigateFolderPicker(FolderPickerCurrentPath);
    }

    [RelayCommand]
    public void FolderPickerNavigateManual()
    {
        if (!string.IsNullOrWhiteSpace(FolderPickerManualInput))
        {
            NavigateFolderPicker(FolderPickerManualInput);
        }
    }

    [RelayCommand]
    public void FolderPickerToggleCreateNew()
    {
        IsFolderPickerCreatingNew = !IsFolderPickerCreatingNew;
        FolderPickerNewFolderName = "";
    }

    [RelayCommand]
    public void FolderPickerConfirmCreateNew()
    {
        if (string.IsNullOrWhiteSpace(FolderPickerNewFolderName)) return;

        try
        {
            string newPath = Path.Combine(FolderPickerCurrentPath, FolderPickerNewFolderName.Trim());
            if (!Directory.Exists(newPath))
            {
                Directory.CreateDirectory(newPath);
            }
            IsFolderPickerCreatingNew = false;
            FolderPickerNewFolderName = "";
            NavigateFolderPicker(newPath);
        }
        catch (Exception ex)
        {
            FolderPickerStatusMessage = $"Không thể tạo thư mục: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ConfirmFolderPicker()
    {
        string chosen = !string.IsNullOrWhiteSpace(FolderPickerManualInput) && Directory.Exists(FolderPickerManualInput)
            ? FolderPickerManualInput.Trim()
            : FolderPickerCurrentPath.Trim();

        IsFolderPickerModalOpen = false;
        _folderPickerTcs?.TrySetResult(chosen);
    }

    [RelayCommand]
    public void CancelFolderPicker()
    {
        IsFolderPickerModalOpen = false;
        _folderPickerTcs?.TrySetResult(null);
    }
}
