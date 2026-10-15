using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.Services;
using SkiaSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ComicDownloaderGMTPC.ViewModels;

public record PictureSortHistoryEntry(
    string SourceFilePath,
    string DestinationFilePath,
    bool IsMove,
    bool WasFromSourceToSorted
);

public partial class MainViewModel
{
    private CancellationTokenSource? _dualSortThumbCts;
    private readonly Stack<List<PictureSortHistoryEntry>> _dualSortUndoBatches = new();
    private readonly Stack<List<PictureSortHistoryEntry>> _dualSortRedoBatches = new();
    private static readonly HashSet<string> _validImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".avif", ".jfif"
    };

    // ==========================================
    // CHỌN ẢNH BATCH THEO PART X
    // ==========================================
    [ObservableProperty]
    private bool _isBatchModeActive = false;

    [ObservableProperty]
    private int _batchPartNumber = 1;

    [ObservableProperty]
    private PictureSortItem? _batchStartItem;

    [ObservableProperty]
    private bool _isDualSortSelectAllActive = false;

    [ObservableProperty]
    private bool _canRedoSort = false;

    // ==========================================
    // FLOATING BATCH TOOLBAR ẨN / HIỆN
    // ==========================================
    [ObservableProperty]
    private bool _isDualSortBatchToolbarVisible = true;

    public bool IsDualSortBatchToolbarRealVisible =>
        SelectedRootTabIndex == 3 && SelectedToolsTabIndex == 6 && IsDualSortBatchToolbarVisible;

    public string DualSortBatchToggleBtnText => IsDualSortBatchToolbarVisible ? "⚡ BATCH TOOL: BẬT" : "⚡ BATCH TOOL: TẮT";
    public string DualSortBatchToggleBtnBg => IsDualSortBatchToolbarVisible ? "#0284C7" : "#1E293B";
    public string DualSortBatchToggleBtnBorder => IsDualSortBatchToolbarVisible ? "#38BDF8" : "#475569";
    public string DualSortBatchToggleBtnFg => IsDualSortBatchToolbarVisible ? "#FFFFFF" : "#94A3B8";

    partial void OnIsDualSortBatchToolbarVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDualSortBatchToolbarRealVisible));
        OnPropertyChanged(nameof(DualSortBatchToggleBtnText));
        OnPropertyChanged(nameof(DualSortBatchToggleBtnBg));
        OnPropertyChanged(nameof(DualSortBatchToggleBtnBorder));
        OnPropertyChanged(nameof(DualSortBatchToggleBtnFg));
    }

    [RelayCommand]
    public void ToggleDualSortBatchToolbarVisible()
    {
        IsDualSortBatchToolbarVisible = !IsDualSortBatchToolbarVisible;
    }

    public string DualSortBatchButtonText
    {
        get
        {
            if (!IsBatchModeActive)
                return $"⚡ Start (Part {BatchPartNumber})";
            if (BatchStartItem == null)
                return $"📍 Part {BatchPartNumber}: Đầu";
            return $"🏁 Part {BatchPartNumber}: Cuối";
        }
    }

    public string DualSortBatchButtonBg => IsBatchModeActive ? "#D97706" : "#1E293B";
    public string DualSortBatchButtonBorder => IsBatchModeActive ? "#F59E0B" : "#475569";
    public string DualSortBatchButtonFg => IsBatchModeActive ? "#FFFFFF" : "#38BDF8";
    public string DualSortPreviewButtonShortText => IsDualSortPreviewEnabled ? "👁️ PREVIEW: BẬT" : "👁️ PREVIEW: TẮT";

    public void RefreshBatchProperties()
    {
        OnPropertyChanged(nameof(DualSortBatchButtonText));
        OnPropertyChanged(nameof(DualSortBatchButtonBg));
        OnPropertyChanged(nameof(DualSortBatchButtonBorder));
        OnPropertyChanged(nameof(DualSortBatchButtonFg));
    }

    [RelayCommand]
    public void ToggleBatchSelectMode()
    {
        if (IsBatchModeActive)
        {
            // Bấm lại khi đang chờ -> Hủy part dở dang nhưng giữ nguyên các ảnh đã chọn
            IsBatchModeActive = false;
            BatchStartItem = null;
            DualSortStatusText = $"Đã hủy Part {BatchPartNumber}. Giữ nguyên các ảnh đã chọn trước đó.";
        }
        else
        {
            IsBatchModeActive = true;
            BatchStartItem = null;
            DualSortStatusText = $"Bắt đầu Part {BatchPartNumber}: Hãy chạm/click vào ảnh BẮT ĐẦU.";
        }
        RefreshBatchProperties();
    }

    [RelayCommand]
    public void ToggleSelectAll()
    {
        ObservableCollection<PictureSortItem> targetCollection;
        if (IsPortraitMode)
        {
            targetCollection = DualSortMobileActiveTab == 0 ? DualSortSourceItems : DualSortSortedItems;
        }
        else
        {
            bool hasSortedSelected = DualSortSortedItems.Any(x => x.IsSelected);
            targetCollection = hasSortedSelected ? DualSortSortedItems : DualSortSourceItems;
        }

        if (targetCollection.Count == 0) return;

        bool allSelected = targetCollection.All(x => x.IsSelected);
        bool newState = !allSelected;

        foreach (var it in targetCollection)
        {
            it.IsSelected = newState;
        }

        IsDualSortSelectAllActive = newState;
        DualSortStatusText = newState ? $"Đã chọn tất cả {targetCollection.Count} ảnh." : "Đã bỏ chọn tất cả ảnh.";
    }

    public void HandleBatchItemClick(PictureSortItem item, bool isSource)
    {
        if (!IsBatchModeActive) return;

        if (BatchStartItem == null)
        {
            // Chạm lần 1: Chọn ảnh bắt đầu Part X
            BatchStartItem = item;
            item.IsSelected = true;
            DualSortStatusText = $"Part {BatchPartNumber}: Đã chọn ảnh BẮT ĐẦU ('{item.FileName}'). Hãy chọn ảnh KẾT THÚC.";
            RefreshBatchProperties();
        }
        else
        {
            // Chạm lần 2: Chọn ảnh kết thúc Part X
            var targetList = isSource ? DualSortSourceItems : DualSortSortedItems;
            int idxStart = targetList.IndexOf(BatchStartItem);
            int idxEnd = targetList.IndexOf(item);

            if (idxStart >= 0 && idxEnd >= 0)
            {
                int minIdx = Math.Min(idxStart, idxEnd);
                int maxIdx = Math.Max(idxStart, idxEnd);

                for (int i = minIdx; i <= maxIdx; i++)
                {
                    targetList[i].IsSelected = true;
                }

                int countAdded = maxIdx - minIdx + 1;
                DualSortStatusText = $"Part {BatchPartNumber} xong! Đã chọn dải {countAdded} ảnh (tổng cộng {targetList.Count(x => x.IsSelected)} ảnh đã chọn).";

                // Kết thúc Part X: tăng Part, quay lại single mode, không làm mất selection
                BatchPartNumber++;
                BatchStartItem = null;
                IsBatchModeActive = false;
                RefreshBatchProperties();
            }
            else
            {
                BatchStartItem = item;
                item.IsSelected = true;
                DualSortStatusText = $"Đã đổi panel. Đã chọn ảnh BẮT ĐẦU ('{item.FileName}'). Hãy chọn ảnh KẾT THÚC.";
                RefreshBatchProperties();
            }
        }
    }

    // ==========================================
    // CẤU HÌNH THƯ MỤC
    // ==========================================
    [ObservableProperty]
    private string _dualSortSourcePath = string.Empty;

    [ObservableProperty]
    private string _dualSortSortedPath = string.Empty;

    [ObservableProperty]
    private bool _isAutoCreateSortedFolder = true;

    [ObservableProperty]
    private bool _isSortMoveMode = true; // Di chuyển ảnh (Move)

    [ObservableProperty]
    private bool _isSortCopyMode = false; // Sao chép ảnh (Copy)

    [ObservableProperty]
    private bool _isOneClickFastSort = false; // Chuyển ngay khi Click

    // ==========================================
    // TÌM KIẾM & BỘ LỌC
    // ==========================================
    [ObservableProperty]
    private string _dualSortSearchSource = string.Empty;

    [ObservableProperty]
    private string _dualSortSearchSorted = string.Empty;

    // ==========================================
    // DANH SÁCH ẢNH 2 BÊN
    // ==========================================
    [ObservableProperty]
    private ObservableCollection<PictureSortItem> _dualSortSourceItems = new();

    [ObservableProperty]
    private ObservableCollection<PictureSortItem> _dualSortSortedItems = new();

    [ObservableProperty]
    private int _dualSortSourceCount = 0;

    [ObservableProperty]
    private int _dualSortSortedCount = 0;

    [ObservableProperty]
    private string _dualSortStatusText = "Sẵn sàng. Hãy chọn thư mục nguồn để bắt đầu lọc ảnh.";

    [ObservableProperty]
    private bool _canUndoSort = false;

    // ==========================================
    // XEM TRƯỚC CHI TIẾT (PANEL PHẢI)
    // ==========================================
    [ObservableProperty]
    private PictureSortItem? _dualSortSelectedPreview;

    [ObservableProperty]
    private Bitmap? _dualSortPreviewBitmap;

    [ObservableProperty]
    private bool _isDualSortPreviewEnabled = true;

    [ObservableProperty]
    private bool _isDualSortPreviewVisible = false;

    [ObservableProperty]
    private PictureSortItem? _dualSortPreviewItem;

    private CancellationTokenSource? _dualSortAnimationCts;

    [ObservableProperty]
    private bool _isDualSortPreviewAnimated = false;

    [ObservableProperty]
    private string _dualSortPreviewAnimatedBadge = string.Empty;

    public string DualSortPreviewButtonText => IsDualSortPreviewEnabled ? "👁️ PREVIEW: BẬT" : "👁️ PREVIEW: TẮT";
    public string DualSortPreviewButtonBg => IsDualSortPreviewEnabled ? "#064E3B" : "#1E293B";
    public string DualSortPreviewButtonBorder => IsDualSortPreviewEnabled ? "#10B981" : "#475569";
    public string DualSortPreviewButtonFg => IsDualSortPreviewEnabled ? "#FDE047" : "#94A3B8";

    [RelayCommand]
    public void ToggleDualSortPreviewEnabled()
    {
        IsDualSortPreviewEnabled = !IsDualSortPreviewEnabled;
        if (!IsDualSortPreviewEnabled)
        {
            CloseDualSortPreview();
        }
        OnPropertyChanged(nameof(DualSortPreviewButtonText));
        OnPropertyChanged(nameof(DualSortPreviewButtonBg));
        OnPropertyChanged(nameof(DualSortPreviewButtonBorder));
        OnPropertyChanged(nameof(DualSortPreviewButtonFg));
    }

    [ObservableProperty]
    private bool _isDualSortLoading = false;

    // ==========================================
    // RESPONSIVE CHO MOBILE / ANDROID
    // ==========================================
    [ObservableProperty]
    private int _dualSortMobileActiveTab = 0; // 0: Gốc, 1: Đã lọc

    partial void OnDualSortMobileActiveTabChanged(int value)
    {
        OnPropertyChanged(nameof(MobileSourceTabBg));
        OnPropertyChanged(nameof(MobileSortedTabBg));
        OnPropertyChanged(nameof(IsMobileSourceTabVisible));
        OnPropertyChanged(nameof(IsMobileSortedTabVisible));
    }

    public string MobileSourceTabBg => DualSortMobileActiveTab == 0 ? "#0284C7" : "#1E293B";
    public string MobileSortedTabBg => DualSortMobileActiveTab == 1 ? "#059669" : "#1E293B";
    public bool IsMobileSourceTabVisible => DualSortMobileActiveTab == 0;
    public bool IsMobileSortedTabVisible => DualSortMobileActiveTab == 1;

    [ObservableProperty]
    private bool _isDualSortMobilePreviewExpanded = false;

    partial void OnDualSortSearchSourceChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredDualSortSourceItems));
    }

    partial void OnDualSortSearchSortedChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredDualSortSortedItems));
    }

    partial void OnIsSortMoveModeChanged(bool value)
    {
        if (value)
            IsSortCopyMode = false;
    }

    partial void OnIsSortCopyModeChanged(bool value)
    {
        if (value)
            IsSortMoveMode = false;
    }

    public IEnumerable<PictureSortItem> FilteredDualSortSourceItems
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DualSortSearchSource))
                return DualSortSourceItems;
            return DualSortSourceItems.Where(x => x.FileName.Contains(DualSortSearchSource, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IEnumerable<PictureSortItem> FilteredDualSortSortedItems
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DualSortSearchSorted))
                return DualSortSortedItems;
            return DualSortSortedItems.Where(x => x.FileName.Contains(DualSortSearchSorted, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ==========================================
    // BROWSE & OPEN FOLDER COMMANDS
    // ==========================================
    [RelayCommand]
    public async Task BrowseDualSortSourceFolderAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = _langService.CurrentLanguage == "VI" ? "Chọn thư mục ảnh gốc cần lọc" : "Select Source Picture Folder",
                AllowMultiple = false
            });

            if (folders != null && folders.Count > 0)
            {
                var selected = folders[0];
                string? raw = selected.TryGetLocalPath() ?? selected.Path?.LocalPath ?? selected.Path?.ToString();
                string normalized = DownloadEngineService.NormalizeStoragePath(raw, selected.Name);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    DualSortSourcePath = normalized;
                    if (IsAutoCreateSortedFolder)
                    {
                        string dirName = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        DualSortSortedPath = Path.Combine(normalized, $"{dirName}-sorted");
                        try
                        {
                            if (!Directory.Exists(DualSortSortedPath))
                            {
                                Directory.CreateDirectory(DualSortSortedPath);
                            }
                        }
                        catch { }
                    }
                    await LoadBothSortFoldersAsync();
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"[DualSort] Lỗi khi chọn thư mục gốc: {ex.Message}");
        }
    }

    [RelayCommand]
    public void OpenDualSortSourceFolder()
    {
        if (!string.IsNullOrWhiteSpace(DualSortSourcePath))
        {
            _downloadEngine.OpenDirectoryInExplorer(DualSortSourcePath);
        }
    }

    [RelayCommand]
    public async Task BrowseDualSortSortedFolderAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = _langService.CurrentLanguage == "VI" ? "Chọn thư mục lưu ảnh đã lọc" : "Select Sorted Picture Folder",
                AllowMultiple = false
            });

            if (folders != null && folders.Count > 0)
            {
                var selected = folders[0];
                string? raw = selected.TryGetLocalPath() ?? selected.Path?.LocalPath ?? selected.Path?.ToString();
                string normalized = DownloadEngineService.NormalizeStoragePath(raw, selected.Name);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    DualSortSortedPath = normalized;
                    await LoadSortedFolderOnlyAsync();
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"[DualSort] Lỗi khi chọn thư mục đã lọc: {ex.Message}");
        }
    }

    [RelayCommand]
    public void OpenDualSortSortedFolder()
    {
        if (!string.IsNullOrWhiteSpace(DualSortSortedPath))
        {
            _downloadEngine.OpenDirectoryInExplorer(DualSortSortedPath);
        }
    }

    // ==========================================
    // ĐIỀU HƯỚNG THƯ MỤC ANH EM (PREVIOUS / NEXT FOLDER)
    // ==========================================
    private List<string> GetDualSortSiblingFolders()
    {
        if (string.IsNullOrWhiteSpace(DualSortSourcePath))
            return new List<string>();

        try
        {
            string cleanSource = DualSortSourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(cleanSource))
                return new List<string>();

            string? parent = Path.GetDirectoryName(cleanSource);
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                return new List<string>();

            // Lấy toàn bộ thư mục con, bỏ qua thư mục ẩn, thư mục hệ thống và thư mục output "-sorted"
            var dirs = Directory.GetDirectories(parent)
                .Where(d =>
                {
                    string name = Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (string.IsNullOrWhiteSpace(name)) return false;
                    if (name.StartsWith(".") || name.StartsWith("$")) return false;
                    if (name.EndsWith("-sorted", StringComparison.OrdinalIgnoreCase)) return false;
                    return true;
                })
                .OrderBy(d => d, NaturalSortComparer.Instance)
                .ToList();

            return dirs;
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"[DualSort] Lỗi khi quét thư mục anh em: {ex.Message}");
            return new List<string>();
        }
    }

    [RelayCommand]
    public async Task DualSortPreviousFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(DualSortSourcePath))
        {
            DualSortStatusText = "Chưa chọn Thư mục gốc!";
            return;
        }

        var dirs = GetDualSortSiblingFolders();
        if (dirs.Count == 0)
        {
            DualSortStatusText = "Không tìm thấy thư mục nào trong thư mục cha!";
            return;
        }

        string cleanCurrent = Path.GetFullPath(DualSortSourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int currentIndex = dirs.FindIndex(d => string.Equals(Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), cleanCurrent, StringComparison.OrdinalIgnoreCase));

        if (currentIndex < 0)
        {
            // Nếu folder hiện tại không nằm trong danh sách (hoặc vừa tạo mới), nạp folder đầu tiên
            await SwitchDualSortSourceFolderAsync(dirs[0], 1, dirs.Count);
            return;
        }

        if (currentIndex == 0)
        {
            DualSortStatusText = $"Đã ở thư mục đầu tiên! (1/{dirs.Count})";
            return;
        }

        string targetDir = dirs[currentIndex - 1];
        await SwitchDualSortSourceFolderAsync(targetDir, currentIndex, dirs.Count);
    }

    [RelayCommand]
    public async Task DualSortNextFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(DualSortSourcePath))
        {
            DualSortStatusText = "Chưa chọn Thư mục gốc!";
            return;
        }

        var dirs = GetDualSortSiblingFolders();
        if (dirs.Count == 0)
        {
            DualSortStatusText = "Không tìm thấy thư mục nào trong thư mục cha!";
            return;
        }

        string cleanCurrent = Path.GetFullPath(DualSortSourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int currentIndex = dirs.FindIndex(d => string.Equals(Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), cleanCurrent, StringComparison.OrdinalIgnoreCase));

        if (currentIndex < 0)
        {
            // Nếu folder hiện tại không nằm trong danh sách, nạp folder đầu tiên
            await SwitchDualSortSourceFolderAsync(dirs[0], 1, dirs.Count);
            return;
        }

        if (currentIndex >= dirs.Count - 1)
        {
            DualSortStatusText = $"Đã ở thư mục cuối cùng! ({dirs.Count}/{dirs.Count})";
            return;
        }

        string targetDir = dirs[currentIndex + 1];
        await SwitchDualSortSourceFolderAsync(targetDir, currentIndex + 2, dirs.Count);
    }

    private async Task SwitchDualSortSourceFolderAsync(string targetDir, int humanIndex, int totalCount)
    {
        try
        {
            CloseDualSortPreview();

            DualSortSourcePath = targetDir;
            if (IsAutoCreateSortedFolder)
            {
                string dirName = Path.GetFileName(targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                DualSortSortedPath = Path.Combine(targetDir, $"{dirName}-sorted");
                try
                {
                    if (!Directory.Exists(DualSortSortedPath))
                    {
                        Directory.CreateDirectory(DualSortSortedPath);
                    }
                }
                catch { }
            }

            // Xóa Undo / Redo để bảo toàn an toàn dữ liệu file giữa các folder
            _dualSortUndoBatches.Clear();
            _dualSortRedoBatches.Clear();
            CanUndoSort = false;
            CanRedoSort = false;

            // Đặt lại chế độ Batch Part
            IsBatchModeActive = false;
            BatchStartItem = null;
            BatchPartNumber = 1;
            RefreshBatchProperties();

            await LoadBothSortFoldersAsync();

            string folderName = Path.GetFileName(targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            DualSortStatusText = $"[{humanIndex}/{totalCount}] Đã chuyển sang: {folderName}";
            AddLog("INFO", $"[DualSort] Chuyển thư mục ({humanIndex}/{totalCount}): {targetDir}");
        }
        catch (Exception ex)
        {
            DualSortStatusText = $"Lỗi khi chuyển thư mục: {ex.Message}";
            AddLog("WARN", $"[DualSort] Lỗi khi chuyển thư mục: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task RefreshDualSortFoldersAsync()
    {
        await LoadBothSortFoldersAsync();
    }

    // ==========================================
    // TẢI ẢNH VÀ THUMBNAIL TỪ 2 THƯ MỤC
    // ==========================================
    public async Task LoadBothSortFoldersAsync()
    {
        _dualSortThumbCts?.Cancel();
        _dualSortThumbCts = new CancellationTokenSource();
        var ct = _dualSortThumbCts.Token;

        IsDualSortLoading = true;
        DualSortStatusText = "Đang quét danh sách ảnh...";

        try
        {
            // 1. Quét thư mục gốc
            var sourceFiles = new List<string>();
            if (!string.IsNullOrWhiteSpace(DualSortSourcePath) && Directory.Exists(DualSortSourcePath))
            {
                sourceFiles = Directory.EnumerateFiles(DualSortSourcePath, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => _validImageExtensions.Contains(Path.GetExtension(f)))
                    .OrderBy(f => f, NaturalSortComparer.Instance)
                    .ToList();
            }

            // 2. Quét thư mục đã lọc
            var sortedFiles = new List<string>();
            if (!string.IsNullOrWhiteSpace(DualSortSortedPath) && Directory.Exists(DualSortSortedPath))
            {
                sortedFiles = Directory.EnumerateFiles(DualSortSortedPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => _validImageExtensions.Contains(Path.GetExtension(f)))
                    .OrderBy(f => f, NaturalSortComparer.Instance)
                    .ToList();
            }

            var srcItems = sourceFiles.Select(f => new PictureSortItem(f)).ToList();
            var srtItems = sortedFiles.Select(f => new PictureSortItem(f)).ToList();

            DualSortSourceItems = new ObservableCollection<PictureSortItem>(srcItems);
            DualSortSortedItems = new ObservableCollection<PictureSortItem>(srtItems);
            DualSortSourceCount = srcItems.Count;
            DualSortSortedCount = srtItems.Count;

            UpdateDualSortStatusText();
            OnPropertyChanged(nameof(FilteredDualSortSourceItems));
            OnPropertyChanged(nameof(FilteredDualSortSortedItems));

            // Tự động chọn ảnh đầu tiên nếu có
            if (srcItems.Count > 0)
            {
                SelectDualSortItem(srcItems[0]);
            }
            else if (srtItems.Count > 0)
            {
                SelectDualSortItem(srtItems[0]);
            }
            else
            {
                DualSortSelectedPreview = null;
                DualSortPreviewBitmap = null;
            }

            // 3. Khởi chạy luồng background giải mã thumbnail mượt mà
            _ = Task.Run(() => LoadThumbnailsBackgroundAsync(srcItems.Concat(srtItems).ToList(), ct), ct);
        }
        catch (Exception ex)
        {
            DualSortStatusText = $"Lỗi khi nạp ảnh: {ex.Message}";
        }
        finally
        {
            IsDualSortLoading = false;
        }
    }

    private async Task LoadSortedFolderOnlyAsync()
    {
        try
        {
            var sortedFiles = new List<string>();
            if (!string.IsNullOrWhiteSpace(DualSortSortedPath) && Directory.Exists(DualSortSortedPath))
            {
                sortedFiles = Directory.EnumerateFiles(DualSortSortedPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => _validImageExtensions.Contains(Path.GetExtension(f)))
                    .OrderBy(f => f, NaturalSortComparer.Instance)
                    .ToList();
            }

            var srtItems = sortedFiles.Select(f => new PictureSortItem(f)).ToList();
            DualSortSortedItems = new ObservableCollection<PictureSortItem>(srtItems);
            DualSortSortedCount = srtItems.Count;
            UpdateDualSortStatusText();
            OnPropertyChanged(nameof(FilteredDualSortSortedItems));

            _dualSortThumbCts?.Cancel();
            _dualSortThumbCts = new CancellationTokenSource();
            var ct = _dualSortThumbCts.Token;
            _ = Task.Run(() => LoadThumbnailsBackgroundAsync(srtItems, ct), ct);
        }
        catch (Exception ex)
        {
            DualSortStatusText = $"Lỗi khi nạp thư mục đã lọc: {ex.Message}";
        }
    }

    private async Task LoadThumbnailsBackgroundAsync(List<PictureSortItem> items, CancellationToken ct)
    {
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = OperatingSystem.IsAndroid() ? 2 : 4,
            CancellationToken = ct
        };

        try
        {
            await Parallel.ForEachAsync(items, parallelOptions, async (item, token) =>
            {
                if (token.IsCancellationRequested || !File.Exists(item.FilePath)) return;

                try
                {
                    // Đọc metadata và decode thumbnail bằng SkiaSharp tiết kiệm RAM
                    using var stream = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var codec = SKCodec.Create(stream);
                    if (codec != null)
                    {
                        int origW = codec.Info.Width;
                        int origH = codec.Info.Height;

                        // Tính kích thước thumbnail (tối đa cạnh dài 240px)
                        int maxDim = 240;
                        int thumbW = origW;
                        int thumbH = origH;
                        if (origW > maxDim || origH > maxDim)
                        {
                            if (origW >= origH)
                            {
                                thumbW = maxDim;
                                thumbH = Math.Max(1, (int)((float)origH / origW * maxDim));
                            }
                            else
                            {
                                thumbH = maxDim;
                                thumbW = Math.Max(1, (int)((float)origW / origH * maxDim));
                            }
                        }

                        var decodeInfo = new SKImageInfo(thumbW, thumbH, SKColorType.Rgba8888, SKAlphaType.Premul);
                        using var skBmp = SKBitmap.Decode(codec, decodeInfo);
                        if (skBmp != null)
                        {
                            using var skImg = SKImage.FromBitmap(skBmp);
                            using var data = skImg.Encode(SKEncodedImageFormat.Jpeg, 75);
                            using var ms = new MemoryStream();
                            data.SaveTo(ms);
                            ms.Position = 0;
                            var avaloniaBmp = new Bitmap(ms);

                            Dispatcher.UIThread.Post(() =>
                            {
                                item.UpdateDimensions(origW, origH);
                                item.Thumbnail = avaloniaBmp;
                            }, DispatcherPriority.Background);
                        }
                    }
                }
                catch { }
            });
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void UpdateDualSortStatusText()
    {
        DualSortStatusText = $"Đã tải {DualSortSourceCount} ảnh gốc, {DualSortSortedCount} ảnh đã lọc.";
    }

    // ==========================================
    // CHỌN ITEM & XEM TRƯỚC CHI TIẾT
    // ==========================================
    [RelayCommand]
    public void SelectDualSortItem(PictureSortItem? item)
    {
        if (item == null) return;

        item.IsSelected = true;
        DualSortSelectedPreview = item;

        // Tải ảnh xem trước chi tiết (Full size)
        Task.Run(() =>
        {
            try
            {
                if (File.Exists(item.FilePath))
                {
                    using var fs = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var fullBmp = new Bitmap(fs);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (DualSortSelectedPreview == item)
                        {
                            DualSortPreviewBitmap = fullBmp;
                        }
                    });
                }
            }
            catch { }
        });
    }

    // ==========================================
    // CLICK & DOUBLE CLICK INTERACTIONS
    // ==========================================
    [RelayCommand]
    public void OnSourceItemClicked(PictureSortItem? item)
    {
        if (item == null) return;

        if (IsBatchModeActive)
        {
            HandleBatchItemClick(item, isSource: true);
            return;
        }

        // Single Mode: Đổi trạng thái chọn (giữ nguyên các ảnh đã chọn khác)
        item.IsSelected = !item.IsSelected;
        DualSortSelectedPreview = item;

        if (IsOneClickFastSort)
        {
            SortItemForward(item);
        }
    }

    [RelayCommand]
    public void OnSortedItemClicked(PictureSortItem? item)
    {
        if (item == null) return;

        if (IsBatchModeActive)
        {
            HandleBatchItemClick(item, isSource: false);
            return;
        }

        item.IsSelected = !item.IsSelected;
        DualSortSelectedPreview = item;

        if (IsOneClickFastSort)
        {
            SortItemBackward(item);
        }
    }

    [RelayCommand]
    public void OnSourceItemDoubleClicked(PictureSortItem? item)
    {
        if (item == null) return;
        SortItemForward(item);
    }

    [RelayCommand]
    public void OnSortedItemDoubleClicked(PictureSortItem? item)
    {
        if (item == null) return;
        SortItemBackward(item);
    }

    // ==========================================
    // CHUYỂN ẢNH SANG SORTED & TRẢ LẠI GỐC (HỖ TRỢ CẢ SINGLE & BATCH)
    // ==========================================
    [RelayCommand]
    public void SortSelectedForward()
    {
        var selectedList = DualSortSourceItems.Where(x => x.IsSelected).ToList();
        if (selectedList.Count == 0 && DualSortSelectedPreview != null && DualSortSourceItems.Contains(DualSortSelectedPreview))
        {
            selectedList.Add(DualSortSelectedPreview);
        }

        if (selectedList.Count == 0)
        {
            DualSortStatusText = "Chưa chọn ảnh nào bên Thư mục gốc để chuyển!";
            return;
        }

        if (string.IsNullOrWhiteSpace(DualSortSortedPath))
        {
            DualSortStatusText = "Vui lòng chọn Thư mục đã lọc trước!";
            return;
        }

        try
        {
            if (!Directory.Exists(DualSortSortedPath))
            {
                Directory.CreateDirectory(DualSortSortedPath);
            }

            var batchHistory = new List<PictureSortHistoryEntry>();

            foreach (var item in selectedList)
            {
                try
                {
                    string sourceFile = item.FilePath;
                    string destFile = Path.Combine(DualSortSortedPath, item.FileName);
                    destFile = GetUniqueFilePath(destFile);

                    if (IsSortMoveMode)
                    {
                        File.Move(sourceFile, destFile, overwrite: true);
                        batchHistory.Add(new PictureSortHistoryEntry(sourceFile, destFile, IsMove: true, WasFromSourceToSorted: true));
                    }
                    else
                    {
                        File.Copy(sourceFile, destFile, overwrite: true);
                        batchHistory.Add(new PictureSortHistoryEntry(sourceFile, destFile, IsMove: false, WasFromSourceToSorted: true));
                    }

                    DualSortSourceItems.Remove(item);

                    var newItem = new PictureSortItem(destFile)
                    {
                        Thumbnail = item.Thumbnail,
                        Width = item.Width,
                        Height = item.Height,
                        ResolutionText = item.ResolutionText,
                        IsSelected = false
                    };
                    DualSortSortedItems.Insert(0, newItem);
                }
                catch (Exception ex)
                {
                    DualSortStatusText = $"Lỗi khi chuyển '{item.FileName}': {ex.Message}";
                }
            }

            if (batchHistory.Count > 0)
            {
                _dualSortUndoBatches.Push(batchHistory);
                _dualSortRedoBatches.Clear();
                CanUndoSort = true;
                CanRedoSort = false;
            }

            DualSortSourceCount = DualSortSourceItems.Count;
            DualSortSortedCount = DualSortSortedItems.Count;
            UpdateDualSortStatusText();

            OnPropertyChanged(nameof(FilteredDualSortSourceItems));
            OnPropertyChanged(nameof(FilteredDualSortSortedItems));

            DualSortStatusText = $"Đã chuyển {batchHistory.Count} ảnh sang Thư mục đã lọc.";
        }
        catch (Exception ex)
        {
            DualSortStatusText = $"Lỗi khi chuyển hàng loạt: {ex.Message}";
        }
    }

    [RelayCommand]
    public void SortSelectedBackward()
    {
        var selectedList = DualSortSortedItems.Where(x => x.IsSelected).ToList();
        if (selectedList.Count == 0 && DualSortSelectedPreview != null && DualSortSortedItems.Contains(DualSortSelectedPreview))
        {
            selectedList.Add(DualSortSelectedPreview);
        }

        if (selectedList.Count == 0)
        {
            DualSortStatusText = "Chưa chọn ảnh nào bên Thư mục đã lọc để trả lại!";
            return;
        }

        if (string.IsNullOrWhiteSpace(DualSortSourcePath))
        {
            DualSortStatusText = "Vui lòng chọn Thư mục gốc trước!";
            return;
        }

        try
        {
            if (!Directory.Exists(DualSortSourcePath))
            {
                Directory.CreateDirectory(DualSortSourcePath);
            }

            var batchHistory = new List<PictureSortHistoryEntry>();

            foreach (var item in selectedList)
            {
                try
                {
                    string sourceFile = item.FilePath;
                    string destFile = Path.Combine(DualSortSourcePath, item.FileName);
                    destFile = GetUniqueFilePath(destFile);

                    if (IsSortMoveMode)
                    {
                        File.Move(sourceFile, destFile, overwrite: true);
                        batchHistory.Add(new PictureSortHistoryEntry(sourceFile, destFile, IsMove: true, WasFromSourceToSorted: false));
                    }
                    else
                    {
                        File.Delete(sourceFile);
                        batchHistory.Add(new PictureSortHistoryEntry(destFile, sourceFile, IsMove: false, WasFromSourceToSorted: false));
                    }

                    DualSortSortedItems.Remove(item);

                    var newItem = new PictureSortItem(destFile)
                    {
                        Thumbnail = item.Thumbnail,
                        Width = item.Width,
                        Height = item.Height,
                        ResolutionText = item.ResolutionText,
                        IsSelected = false
                    };
                    DualSortSourceItems.Add(newItem);
                }
                catch (Exception ex)
                {
                    DualSortStatusText = $"Lỗi khi trả '{item.FileName}': {ex.Message}";
                }
            }

            if (batchHistory.Count > 0)
            {
                _dualSortUndoBatches.Push(batchHistory);
                _dualSortRedoBatches.Clear();
                CanUndoSort = true;
                CanRedoSort = false;
            }

            DualSortSourceCount = DualSortSourceItems.Count;
            DualSortSortedCount = DualSortSortedItems.Count;
            UpdateDualSortStatusText();

            OnPropertyChanged(nameof(FilteredDualSortSourceItems));
            OnPropertyChanged(nameof(FilteredDualSortSortedItems));

            DualSortStatusText = $"Đã trả {batchHistory.Count} ảnh về Thư mục gốc.";
        }
        catch (Exception ex)
        {
            DualSortStatusText = $"Lỗi khi trả hàng loạt: {ex.Message}";
        }
    }

    public void SortItemForward(PictureSortItem item)
    {
        if (string.IsNullOrWhiteSpace(DualSortSortedPath))
        {
            DualSortStatusText = "Vui lòng chọn Thư mục đã lọc trước!";
            return;
        }

        try
        {
            if (!Directory.Exists(DualSortSortedPath))
            {
                Directory.CreateDirectory(DualSortSortedPath);
            }

            string sourceFile = item.FilePath;
            string destFile = Path.Combine(DualSortSortedPath, item.FileName);
            destFile = GetUniqueFilePath(destFile);

            PictureSortHistoryEntry historyEntry;
            if (IsSortMoveMode)
            {
                File.Move(sourceFile, destFile, overwrite: true);
                historyEntry = new PictureSortHistoryEntry(sourceFile, destFile, IsMove: true, WasFromSourceToSorted: true);
            }
            else
            {
                File.Copy(sourceFile, destFile, overwrite: true);
                historyEntry = new PictureSortHistoryEntry(sourceFile, destFile, IsMove: false, WasFromSourceToSorted: true);
            }

            _dualSortUndoBatches.Push(new List<PictureSortHistoryEntry> { historyEntry });
            _dualSortRedoBatches.Clear();
            CanUndoSort = true;
            CanRedoSort = false;

            int oldIndex = DualSortSourceItems.IndexOf(item);
            DualSortSourceItems.Remove(item);

            var newItem = new PictureSortItem(destFile)
            {
                Thumbnail = item.Thumbnail,
                Width = item.Width,
                Height = item.Height,
                ResolutionText = item.ResolutionText
            };
            DualSortSortedItems.Insert(0, newItem);

            DualSortSourceCount = DualSortSourceItems.Count;
            DualSortSortedCount = DualSortSortedItems.Count;
            UpdateDualSortStatusText();

            OnPropertyChanged(nameof(FilteredDualSortSourceItems));
            OnPropertyChanged(nameof(FilteredDualSortSortedItems));

            if (DualSortSourceItems.Count > 0)
            {
                int nextIndex = Math.Clamp(oldIndex, 0, DualSortSourceItems.Count - 1);
                SelectDualSortItem(DualSortSourceItems[nextIndex]);
            }
            else
            {
                SelectDualSortItem(newItem);
            }
        }
        catch (Exception ex)
        {
            DualSortStatusText = $"Lỗi khi chuyển ảnh '{item.FileName}': {ex.Message}";
        }
    }

    public void SortItemBackward(PictureSortItem item)
    {
        if (string.IsNullOrWhiteSpace(DualSortSourcePath))
        {
            DualSortStatusText = "Vui lòng chọn Thư mục gốc trước!";
            return;
        }

        try
        {
            if (!Directory.Exists(DualSortSourcePath))
            {
                Directory.CreateDirectory(DualSortSourcePath);
            }

            string sourceFile = item.FilePath;
            string destFile = Path.Combine(DualSortSourcePath, item.FileName);
            destFile = GetUniqueFilePath(destFile);

            PictureSortHistoryEntry historyEntry;
            if (IsSortMoveMode)
            {
                File.Move(sourceFile, destFile, overwrite: true);
                historyEntry = new PictureSortHistoryEntry(sourceFile, destFile, IsMove: true, WasFromSourceToSorted: false);
            }
            else
            {
                File.Delete(sourceFile);
                historyEntry = new PictureSortHistoryEntry(destFile, sourceFile, IsMove: false, WasFromSourceToSorted: false);
            }

            _dualSortUndoBatches.Push(new List<PictureSortHistoryEntry> { historyEntry });
            _dualSortRedoBatches.Clear();
            CanUndoSort = true;
            CanRedoSort = false;

            int oldIndex = DualSortSortedItems.IndexOf(item);
            DualSortSortedItems.Remove(item);

            var newItem = new PictureSortItem(destFile)
            {
                Thumbnail = item.Thumbnail,
                Width = item.Width,
                Height = item.Height,
                ResolutionText = item.ResolutionText
            };
            DualSortSourceItems.Add(newItem);

            DualSortSourceCount = DualSortSourceItems.Count;
            DualSortSortedCount = DualSortSortedItems.Count;
            UpdateDualSortStatusText();

            OnPropertyChanged(nameof(FilteredDualSortSourceItems));
            OnPropertyChanged(nameof(FilteredDualSortSortedItems));

            if (DualSortSortedItems.Count > 0)
            {
                int nextIndex = Math.Clamp(oldIndex, 0, DualSortSortedItems.Count - 1);
                SelectDualSortItem(DualSortSortedItems[nextIndex]);
            }
            else
            {
                SelectDualSortItem(newItem);
            }
        }
        catch (Exception ex)
        {
            DualSortStatusText = $"Lỗi khi trả ảnh '{item.FileName}': {ex.Message}";
        }
    }

    // ==========================================
    // HOÀN TÁC (UNDO) & LÀM LẠI (REDO) HỖ TRỢ BATCH
    // ==========================================
    [RelayCommand]
    public void UndoLastSort()
    {
        if (_dualSortUndoBatches.Count == 0) return;

        var batch = _dualSortUndoBatches.Pop();
        CanUndoSort = _dualSortUndoBatches.Count > 0;

        var redoList = new List<PictureSortHistoryEntry>();

        try
        {
            foreach (var entry in batch)
            {
                if (entry.WasFromSourceToSorted)
                {
                    // Hành động trước: Gốc -> Sorted (dest)
                    if (entry.IsMove)
                    {
                        if (File.Exists(entry.DestinationFilePath))
                        {
                            File.Move(entry.DestinationFilePath, entry.SourceFilePath, overwrite: true);
                        }
                    }
                    else
                    {
                        if (File.Exists(entry.DestinationFilePath))
                        {
                            File.Delete(entry.DestinationFilePath);
                        }
                    }

                    var itemInSorted = DualSortSortedItems.FirstOrDefault(x => x.FilePath.Equals(entry.DestinationFilePath, StringComparison.OrdinalIgnoreCase));
                    if (itemInSorted != null)
                    {
                        DualSortSortedItems.Remove(itemInSorted);
                    }

                    var restoredItem = new PictureSortItem(entry.SourceFilePath);
                    DualSortSourceItems.Add(restoredItem);
                }
                else
                {
                    // Hành động trước: Sorted -> Gốc (dest)
                    if (entry.IsMove)
                    {
                        if (File.Exists(entry.DestinationFilePath))
                        {
                            File.Move(entry.DestinationFilePath, entry.SourceFilePath, overwrite: true);
                        }
                    }

                    var itemInSource = DualSortSourceItems.FirstOrDefault(x => x.FilePath.Equals(entry.DestinationFilePath, StringComparison.OrdinalIgnoreCase));
                    if (itemInSource != null)
                    {
                        DualSortSourceItems.Remove(itemInSource);
                    }

                    var restoredItem = new PictureSortItem(entry.SourceFilePath);
                    DualSortSortedItems.Add(restoredItem);
                }

                redoList.Add(entry);
            }

            _dualSortRedoBatches.Push(redoList);
            CanRedoSort = true;

            DualSortSourceCount = DualSortSourceItems.Count;
            DualSortSortedCount = DualSortSortedItems.Count;
            UpdateDualSortStatusText();

            OnPropertyChanged(nameof(FilteredDualSortSourceItems));
            OnPropertyChanged(nameof(FilteredDualSortSortedItems));

            DualSortStatusText = $"Đã hoàn tác (Undo) {batch.Count} ảnh thành công.";
        }
        catch (Exception ex)
        {
            DualSortStatusText = $"Lỗi khi hoàn tác: {ex.Message}";
        }
    }

    [RelayCommand]
    public void RedoLastSort()
    {
        if (_dualSortRedoBatches.Count == 0) return;

        var batch = _dualSortRedoBatches.Pop();
        CanRedoSort = _dualSortRedoBatches.Count > 0;

        var undoList = new List<PictureSortHistoryEntry>();

        try
        {
            foreach (var entry in batch)
            {
                if (entry.WasFromSourceToSorted)
                {
                    // Làm lại: Gốc -> Sorted
                    if (entry.IsMove)
                    {
                        if (File.Exists(entry.SourceFilePath))
                        {
                            File.Move(entry.SourceFilePath, entry.DestinationFilePath, overwrite: true);
                        }
                    }
                    else
                    {
                        if (File.Exists(entry.SourceFilePath))
                        {
                            File.Copy(entry.SourceFilePath, entry.DestinationFilePath, overwrite: true);
                        }
                    }

                    var itemInSource = DualSortSourceItems.FirstOrDefault(x => x.FilePath.Equals(entry.SourceFilePath, StringComparison.OrdinalIgnoreCase));
                    if (itemInSource != null)
                    {
                        DualSortSourceItems.Remove(itemInSource);
                    }

                    var newItem = new PictureSortItem(entry.DestinationFilePath);
                    DualSortSortedItems.Insert(0, newItem);
                }
                else
                {
                    // Làm lại: Sorted -> Gốc
                    if (entry.IsMove)
                    {
                        if (File.Exists(entry.SourceFilePath))
                        {
                            File.Move(entry.SourceFilePath, entry.DestinationFilePath, overwrite: true);
                        }
                    }
                    else
                    {
                        if (File.Exists(entry.SourceFilePath))
                        {
                            File.Delete(entry.SourceFilePath);
                        }
                    }

                    var itemInSorted = DualSortSortedItems.FirstOrDefault(x => x.FilePath.Equals(entry.SourceFilePath, StringComparison.OrdinalIgnoreCase));
                    if (itemInSorted != null)
                    {
                        DualSortSortedItems.Remove(itemInSorted);
                    }

                    var newItem = new PictureSortItem(entry.DestinationFilePath);
                    DualSortSourceItems.Add(newItem);
                }

                undoList.Add(entry);
            }

            _dualSortUndoBatches.Push(undoList);
            CanUndoSort = true;

            DualSortSourceCount = DualSortSourceItems.Count;
            DualSortSortedCount = DualSortSortedItems.Count;
            UpdateDualSortStatusText();

            OnPropertyChanged(nameof(FilteredDualSortSourceItems));
            OnPropertyChanged(nameof(FilteredDualSortSortedItems));

            DualSortStatusText = $"Đã làm lại (Redo) {batch.Count} ảnh thành công.";
        }
        catch (Exception ex)
        {
            DualSortStatusText = $"Lỗi khi làm lại (Redo): {ex.Message}";
        }
    }

    [RelayCommand]
    public void ToggleDualSortMobilePreview()
    {
        IsDualSortMobilePreviewExpanded = !IsDualSortMobilePreviewExpanded;
    }

    [RelayCommand]
    public void SelectDualSortMobileSourceTab()
    {
        DualSortMobileActiveTab = 0;
    }

    [RelayCommand]
    public void SelectDualSortMobileSortedTab()
    {
        DualSortMobileActiveTab = 1;
    }

    // ==========================================
    // POPUP PREVIEW TRÊN DESKTOP & ANDROID (HỖ TRỢ ẢNH ĐỘNG GIF & WEBP ANIMATED)
    // ==========================================
    public void ShowDualSortPreview(PictureSortItem? item)
    {
        if (!IsDualSortPreviewEnabled || item == null) return;

        // 1. Huỷ luồng animation trước đó nếu đang chạy
        _dualSortAnimationCts?.Cancel();
        _dualSortAnimationCts?.Dispose();
        _dualSortAnimationCts = null;

        DualSortPreviewItem = item;
        IsDualSortPreviewVisible = true;
        IsDualSortPreviewAnimated = false;
        DualSortPreviewAnimatedBadge = string.Empty;

        var cts = new CancellationTokenSource();
        _dualSortAnimationCts = cts;

        Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(item.FilePath)) return;

                string ext = Path.GetExtension(item.FilePath).ToLowerInvariant();
                bool isAnimated = false;
                int detectedFrameCount = 1;

                var durations = new List<int>();

                // 2. Nhận diện siêu tốc (0.1ms) bằng SKCodec xem có phải ảnh động (GIF hoặc WebP Animated)
                if (ext == ".gif" || ext == ".webp")
                {
                    try
                    {
                        using var stream = File.OpenRead(item.FilePath);
                        using var codec = SKCodec.Create(stream);
                        if (codec != null && codec.FrameCount > 1)
                        {
                            isAnimated = true;
                            detectedFrameCount = codec.FrameCount;
                            var infos = codec.FrameInfo;
                            if (infos != null)
                            {
                                foreach (var fi in infos)
                                {
                                    int d = fi.Duration;
                                    if (d <= 15) d = 100;
                                    durations.Add(d);
                                }
                            }
                        }
                    }
                    catch { }
                }

                // 3. Nạp nhanh frame đầu tiên để người dùng thấy ảnh ngay lập tức (0ms latency)
                try
                {
                    using var fs = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var initialBmp = new Bitmap(fs);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (DualSortPreviewItem == item && IsDualSortPreviewVisible && !cts.IsCancellationRequested)
                        {
                            DualSortPreviewBitmap = initialBmp;
                        }
                        else
                        {
                            initialBmp.Dispose();
                        }
                    });
                }
                catch { }

                // Nếu là ảnh tĩnh, không cần giải mã animation
                if (!isAnimated || cts.IsCancellationRequested) return;

                // 4. Nếu là ảnh động: Hiển thị badge 🎞️ GIF / 🎞️ WEBP
                string badge = ext == ".gif" ? $"🎞️ GIF ({detectedFrameCount}f)" : $"🎞️ WEBP ({detectedFrameCount}f)";
                Dispatcher.UIThread.Post(() =>
                {
                    if (DualSortPreviewItem == item && IsDualSortPreviewVisible && !cts.IsCancellationRequested)
                    {
                        IsDualSortPreviewAnimated = true;
                        DualSortPreviewAnimatedBadge = badge;
                    }
                });

                // 5. Giải mã toàn bộ chuỗi khung hình bằng SixLabors.ImageSharp (Bảo đảm 100% composition & delay)
                var frameList = new List<(Bitmap Bitmap, int DelayMs)>();
                try
                {
                    using var animatedImg = SixLabors.ImageSharp.Image.Load<Rgba32>(item.FilePath);
                    int totalFrames = animatedImg.Frames.Count;
                    if (totalFrames <= 1) return;

                    // Nếu quá nhiều frames (>120), nhảy bước step để tối ưu RAM tối đa cho mobile
                    int step = totalFrames > 120 ? (int)Math.Ceiling(totalFrames / 120.0) : 1;

                    for (int f = 0; f < totalFrames; f += step)
                    {
                        if (cts.IsCancellationRequested || !IsDualSortPreviewVisible || DualSortPreviewItem != item)
                            break;

                        var frame = animatedImg.Frames[f];
                        int delayMs = 100;
                        if (f < durations.Count)
                        {
                            delayMs = durations[f] * step;
                        }
                        else if (ext == ".gif")
                        {
                            var gifMeta = frame.Metadata.GetGifMetadata();
                            int delayHundredths = gifMeta?.FrameDelay ?? 10;
                            delayMs = delayHundredths * 10 * step;
                        }
                        if (delayMs <= 15) delayMs = 100; // Tránh delay = 0 hoặc quá thấp gây nghẽn CPU

                        using var frameImg = animatedImg.Frames.CloneFrame(f);
                        // Scale vừa khung Preview (Max 640px) để mượt mà và siêu nhẹ RAM
                        if (frameImg.Width > 640 || frameImg.Height > 640)
                        {
                            float scale = Math.Min(640f / frameImg.Width, 640f / frameImg.Height);
                            frameImg.Mutate(x => x.Resize((int)(frameImg.Width * scale), (int)(frameImg.Height * scale)));
                        }

                        using var ms = new MemoryStream();
                        frameImg.SaveAsPng(ms);
                        ms.Position = 0;
                        var frameBmp = new Bitmap(ms);
                        frameList.Add((frameBmp, delayMs));
                    }

                    if (cts.IsCancellationRequested || frameList.Count <= 1 || !IsDualSortPreviewVisible || DualSortPreviewItem != item)
                    {
                        foreach (var fl in frameList) fl.Bitmap.Dispose();
                        return;
                    }

                    // 6. Phát animation liên tục trên UI Thread
                    int currentIdx = 0;
                    while (!cts.IsCancellationRequested && IsDualSortPreviewVisible && DualSortPreviewItem == item)
                    {
                        var cur = frameList[currentIdx];
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!cts.IsCancellationRequested && IsDualSortPreviewVisible && DualSortPreviewItem == item)
                            {
                                DualSortPreviewBitmap = cur.Bitmap;
                            }
                        }, DispatcherPriority.Render);

                        await Task.Delay(cur.DelayMs, cts.Token);
                        currentIdx = (currentIdx + 1) % frameList.Count;
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
                finally
                {
                    // 7. Giải phóng bộ nhớ sạch sẽ, an toàn 100%
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (DualSortPreviewBitmap != null && frameList.Any(fl => fl.Bitmap == DualSortPreviewBitmap))
                        {
                            DualSortPreviewBitmap = null;
                        }
                        foreach (var fl in frameList)
                        {
                            fl.Bitmap.Dispose();
                        }
                    });
                }
            }
            catch (Exception) { }
        }, cts.Token);
    }

    [RelayCommand]
    public void CloseDualSortPreview()
    {
        _dualSortAnimationCts?.Cancel();
        _dualSortAnimationCts?.Dispose();
        _dualSortAnimationCts = null;

        IsDualSortPreviewVisible = false;
        DualSortPreviewItem = null;
        DualSortPreviewBitmap = null;
        IsDualSortPreviewAnimated = false;
        DualSortPreviewAnimatedBadge = string.Empty;
    }

    // ==========================================
    // KEEP PARENT & KEEP CHILD ACTIONS
    // ==========================================
    [RelayCommand]
    public async Task KeepParentAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(DualSortSortedPath) || !Directory.Exists(DualSortSortedPath))
            {
                DualSortStatusText = "Không tìm thấy thư mục Đã Lọc (Child) để thực hiện Keep Parent.";
                return;
            }

            string fullSource = Path.GetFullPath(DualSortSourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullSorted = Path.GetFullPath(DualSortSortedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(fullSource, fullSorted, StringComparison.OrdinalIgnoreCase))
            {
                DualSortStatusText = "Lỗi an toàn: Thư mục Gốc và Thư mục Đã Lọc trùng nhau!";
                return;
            }

            if (fullSource.StartsWith(fullSorted + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                DualSortStatusText = "Lỗi an toàn: Thư mục Đã Lọc không được là thư mục cha của Thư mục Gốc!";
                return;
            }

            IsDualSortLoading = true;
            DualSortStatusText = "Đang thực hiện Keep Parent (Chuyển thư mục child vào Thùng rác)...";

            await Task.Run(() =>
            {
                try
                {
                    // Chuyển vào thùng rác (Recycle Bin / Trash) an toàn cho Windows & Linux
                    RecycleBinService.DeleteToRecycleBin(fullSorted);
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        DualSortStatusText = $"Lỗi khi chuyển thư mục child vào Thùng rác: {ex.Message}";
                    });
                }
            });

            DualSortSortedItems.Clear();
            DualSortSortedCount = 0;
            OnPropertyChanged(nameof(FilteredDualSortSortedItems));

            DualSortStatusText = "✅ Keep Parent hoàn tất: Đã giữ lại thư mục Gốc và chuyển thư mục Đã Lọc (Child) vào Thùng rác.";
            AddLog("INFO", $"[DualSort] Keep Parent: Đã chuyển thư mục child '{fullSorted}' vào Thùng rác.");
        }
        catch (Exception ex)
        {
            DualSortStatusText = $"Lỗi Keep Parent: {ex.Message}";
        }
        finally
        {
            IsDualSortLoading = false;
        }
    }

    [RelayCommand]
    public async Task KeepChildAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(DualSortSourcePath) || !Directory.Exists(DualSortSourcePath))
            {
                DualSortStatusText = "Không tìm thấy thư mục Gốc (Parent).";
                return;
            }

            if (string.IsNullOrWhiteSpace(DualSortSortedPath) || !Directory.Exists(DualSortSortedPath))
            {
                DualSortStatusText = "Không tìm thấy thư mục Đã Lọc (Child).";
                return;
            }

            string fullSource = Path.GetFullPath(DualSortSourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullSorted = Path.GetFullPath(DualSortSortedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(fullSource, fullSorted, StringComparison.OrdinalIgnoreCase))
            {
                DualSortStatusText = "Lỗi an toàn: Thư mục Gốc và Thư mục Đã Lọc trùng nhau!";
                return;
            }

            if (fullSource.StartsWith(fullSorted + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                DualSortStatusText = "Lỗi an toàn: Thư mục Đã Lọc không được là thư mục cha của Thư mục Gốc!";
                return;
            }

            IsDualSortLoading = true;
            DualSortStatusText = "Đang thực hiện Keep Child (Chuyển file gốc vào Thùng rác, chuyển ảnh lọc về gốc và dọn child)...";

            await Task.Run(() =>
            {
                // 1. Chuyển toàn bộ file ảnh cũ ở parent vào Thùng rác (Recycle Bin / Trash)
                var sourceFiles = Directory.EnumerateFiles(fullSource, "*.*", SearchOption.TopDirectoryOnly).ToList();
                foreach (var file in sourceFiles)
                {
                    try
                    {
                        RecycleBinService.DeleteToRecycleBin(file);
                    }
                    catch { }
                }

                // 2. Move toàn bộ file ở child vào parent
                if (Directory.Exists(fullSorted))
                {
                    var childFiles = Directory.EnumerateFiles(fullSorted, "*.*", SearchOption.TopDirectoryOnly).ToList();
                    foreach (var file in childFiles)
                    {
                        try
                        {
                            string fileName = Path.GetFileName(file);
                            string destPath = Path.Combine(fullSource, fileName);
                            if (File.Exists(destPath))
                            {
                                RecycleBinService.DeleteToRecycleBin(destPath);
                            }
                            File.Move(file, destPath);
                        }
                        catch { }
                    }

                    // 3. Chuyển thư mục child vào Thùng rác
                    try
                    {
                        RecycleBinService.DeleteToRecycleBin(fullSorted);
                    }
                    catch { }
                }
            });

            // Nạp lại danh sách parent, child sẽ trống
            await LoadBothSortFoldersAsync();

            DualSortStatusText = "✅ Keep Child hoàn tất: Đã chuyển file gốc cũ vào Thùng rác, chuyển toàn bộ ảnh lọc vào thư mục gốc và dọn thư mục child.";
            AddLog("INFO", $"[DualSort] Keep Child: Đã dọn '{fullSource}', chuyển file từ child sang và dọn child '{fullSorted}' vào Thùng rác.");
        }
        catch (Exception ex)
        {
            DualSortStatusText = $"Lỗi Keep Child: {ex.Message}";
        }
        finally
        {
            IsDualSortLoading = false;
        }
    }

    private static string GetUniqueFilePath(string filePath)
    {
        if (!File.Exists(filePath)) return filePath;

        string dir = Path.GetDirectoryName(filePath) ?? string.Empty;
        string nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
        string ext = Path.GetExtension(filePath);
        int counter = 1;

        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{nameWithoutExt}_{counter}{ext}");
            counter++;
        } while (File.Exists(candidate));

        return candidate;
    }
}

