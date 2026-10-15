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
    private readonly Stack<PictureSortHistoryEntry> _dualSortHistory = new();
    private static readonly HashSet<string> _validImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".avif", ".jfif"
    };

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

        // Bỏ chọn các item khác
        foreach (var it in DualSortSourceItems) it.IsSelected = false;
        foreach (var it in DualSortSortedItems) it.IsSelected = false;
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

        SelectDualSortItem(item);

        if (IsOneClickFastSort)
        {
            SortItemForward(item);
        }
    }

    [RelayCommand]
    public void OnSortedItemClicked(PictureSortItem? item)
    {
        if (item == null) return;

        SelectDualSortItem(item);

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
    // CHUYỂN ẢNH SANG SORTED & TRẢ LẠI GỐC
    // ==========================================
    [RelayCommand]
    public void SortSelectedForward()
    {
        var selected = DualSortSourceItems.FirstOrDefault(x => x.IsSelected) ?? DualSortSelectedPreview;
        if (selected != null && DualSortSourceItems.Contains(selected))
        {
            SortItemForward(selected);
        }
    }

    [RelayCommand]
    public void SortSelectedBackward()
    {
        var selected = DualSortSortedItems.FirstOrDefault(x => x.IsSelected) ?? DualSortSelectedPreview;
        if (selected != null && DualSortSortedItems.Contains(selected))
        {
            SortItemBackward(selected);
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

            // Xử lý trùng file
            destFile = GetUniqueFilePath(destFile);

            if (IsSortMoveMode)
            {
                File.Move(sourceFile, destFile, overwrite: true);
                _dualSortHistory.Push(new PictureSortHistoryEntry(sourceFile, destFile, IsMove: true, WasFromSourceToSorted: true));
            }
            else
            {
                File.Copy(sourceFile, destFile, overwrite: true);
                _dualSortHistory.Push(new PictureSortHistoryEntry(sourceFile, destFile, IsMove: false, WasFromSourceToSorted: true));
            }

            CanUndoSort = true;

            // Chuyển item sang collection sorted
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

            // Tự động chọn ảnh tiếp theo trong danh sách gốc để người dùng thao tác liên tục
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

            if (IsSortMoveMode)
            {
                File.Move(sourceFile, destFile, overwrite: true);
                _dualSortHistory.Push(new PictureSortHistoryEntry(sourceFile, destFile, IsMove: true, WasFromSourceToSorted: false));
            }
            else
            {
                File.Delete(sourceFile); // Nếu là copy mode thì trả về gốc đồng nghĩa xóa bản copy ở sorted
                _dualSortHistory.Push(new PictureSortHistoryEntry(destFile, sourceFile, IsMove: false, WasFromSourceToSorted: false));
            }

            CanUndoSort = true;

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
    // HOÀN TÁC (UNDO)
    // ==========================================
    [RelayCommand]
    public void UndoLastSort()
    {
        if (_dualSortHistory.Count == 0) return;

        var entry = _dualSortHistory.Pop();
        CanUndoSort = _dualSortHistory.Count > 0;

        try
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

                // Cập nhật collection
                var itemInSorted = DualSortSortedItems.FirstOrDefault(x => x.FilePath.Equals(entry.DestinationFilePath, StringComparison.OrdinalIgnoreCase));
                if (itemInSorted != null)
                {
                    DualSortSortedItems.Remove(itemInSorted);
                }

                var restoredItem = new PictureSortItem(entry.SourceFilePath);
                DualSortSourceItems.Add(restoredItem);
                SelectDualSortItem(restoredItem);
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
                SelectDualSortItem(restoredItem);
            }

            DualSortSourceCount = DualSortSourceItems.Count;
            DualSortSortedCount = DualSortSortedItems.Count;
            UpdateDualSortStatusText();

            OnPropertyChanged(nameof(FilteredDualSortSourceItems));
            OnPropertyChanged(nameof(FilteredDualSortSortedItems));

            DualSortStatusText = $"Đã hoàn tác thao tác với file '{Path.GetFileName(entry.SourceFilePath)}'.";
        }
        catch (Exception ex)
        {
            DualSortStatusText = $"Lỗi khi hoàn tác: {ex.Message}";
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
    // POPUP PREVIEW TRÊN DESKTOP & ANDROID
    // ==========================================
    public void ShowDualSortPreview(PictureSortItem? item)
    {
        if (!IsDualSortPreviewEnabled || item == null) return;
        DualSortPreviewItem = item;
        IsDualSortPreviewVisible = true;

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
                        if (DualSortPreviewItem == item && IsDualSortPreviewVisible)
                        {
                            DualSortPreviewBitmap = fullBmp;
                        }
                    });
                }
            }
            catch { }
        });
    }

    [RelayCommand]
    public void CloseDualSortPreview()
    {
        IsDualSortPreviewVisible = false;
        DualSortPreviewItem = null;
        DualSortPreviewBitmap = null;
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

