// ====================================================================================================
// FILE SUMMARY: MainViewModel.cs
// ----------------------------------------------------------------------------------------------------
// VAI TRÒ & MỤC ĐÍCH:
//   ViewModel trung tâm (Root ViewModel) điều phối toàn bộ các tab chức năng, dịch vụ nền, hàng đợi
//   và trạng thái giao diện ứng dụng Comic Downloader GMTPC Avalonia trên Windows, Linux và Android.
//
// CÁC TÍNH NĂNG & THÀNH PHẦN CHÍNH:
//   - Quản lý điều hướng tab: Nguồn truyện, Tìm kiếm, Tải về & Hàng đợi, Công cụ Tool, Thư viện đọc truyện.
//   - Trạng thái hệ thống: Thanh tiến trình tổng thể, trạng thái kết nối mạng, logging chi tiết.
//   - Quản lý vòng đời ứng dụng: Khởi tạo các dịch vụ nền, lưu cấu hình khi tắt và phục hồi khi mở lại.
//   - Phím tắt & Sự kiện toàn cục: Điều hướng trang, hủy tác vụ tải, dọn dẹp bộ nhớ đệm.
//
// LƯU Ý KỸ THUẬT & QUY TẮC BẢO TRÌ:
//   - Là partial class mẹ kết nối với các partial ViewModel chuyên biệt (ArchiveMerger, BatchRename, DualPictureSort...).
//   - Mọi tương tác từ luồng tải nền lên ObservableCollection giao diện bắt buộc dùng Dispatcher.UIThread.
// ====================================================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Media.Imaging;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.Services;
using ComicDownloaderGMTPC.Views;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly LanguageService _langService = LanguageService.Instance;
    private readonly ComicScraperService _scraperService = ComicScraperService.Instance;
    private readonly DownloadEngineService _downloadEngine = DownloadEngineService.Instance;
    private readonly MissingChapterScannerService _scannerService = MissingChapterScannerService.Instance;
    private readonly SourceSearchService _sourceSearchService = SourceSearchService.Instance;
    private readonly ImageEnhancerService _imageEnhancer = new();
    private readonly FilePackerService _filePacker = new();
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _enhanceCts;
    private CancellationTokenSource? _packerCts;
    private System.Threading.Timer? _previewDebounceTimer;
    private string _enhanceSampleImagePath = string.Empty;

    [ObservableProperty]
    private string _appTitle = "Comic-GMTPC Avalonia v1.0 - Tiếng Việt";

    [ObservableProperty]
    private string _currentLanguage = "VI";

    [ObservableProperty]
    private bool _isUpdating = false;

    [ObservableProperty]
    private string _updateButtonLabel = "🚀 UPDATE RELEASE";

    [ObservableProperty]
    private string _updateBetaButtonLabel = "🧪 UPDATE BETA";

    [ObservableProperty]
    private bool _hasAvailableReleaseUpdate = false;

    [ObservableProperty]
    private bool _hasAvailableBetaUpdate = false;

    [ObservableProperty]
    private int _selectedRootTabIndex = 0;

    [ObservableProperty]
    private int _selectedToolsTabIndex = 0;

    public LibraryViewModel LibraryVm { get; } = new();

    partial void OnSelectedRootTabIndexChanged(int value)
    {
        if (value == 4 && LibraryVm.AllComics.Count == 0 && !LibraryVm.IsScanning)
        {
            _ = LibraryVm.ScanLibraryAsync();
        }
        OnPropertyChanged(nameof(IsDualSortBatchToolbarRealVisible));
    }

    partial void OnSelectedToolsTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsDualSortBatchToolbarRealVisible));
    }

    [ObservableProperty]
    private string _urlInput = string.Empty;

    [ObservableProperty]
    private string _subfolderInput = string.Empty;

    [ObservableProperty]
    private bool _isAllSubfoldersLocked = false;

    partial void OnUrlInputChanged(string value)
    {
        CheckAndAutoExtractDamconuongRedirectDomain(value);
    }

    [ObservableProperty]
    private string[] _modeSelections = new[] { "Truyện đơn", "Nhiều truyện" };

    [ObservableProperty]
    private string _modeSelection = "Truyện đơn";

    [ObservableProperty]
    private bool _isAutoDownload;

    [ObservableProperty]
    private bool _isAutoRetry = true;

    [ObservableProperty]
    private bool _isDownloading = false;

    [ObservableProperty]
    private bool _isPaused = false;

    public event Action? ScrollToTopRequested;
    public event Action? ScrollToBottomRequested;

    [ObservableProperty]
    private bool _isCompactRow;

    // CHẾ ĐỘ TẢI THỦ CÔNG (TRỰC TIẾP / NHÚNG)
    [ObservableProperty]
    private bool _isManualDownloadMode = InstanceConfigService.Instance.IsManualDownloadMode;

    partial void OnIsManualDownloadModeChanged(bool value)
    {
        InstanceConfigService.Instance.IsManualDownloadMode = value;
        AddLog("INFO", value
            ? (_langService.CurrentLanguage == "VI" ? $"Đã BẬT chế độ tải thủ công ({SelectedManualDownloadMode})." : $"Manual download mode ENABLED ({SelectedManualDownloadMode}).")
            : (_langService.CurrentLanguage == "VI" ? "Đã TẮT chế độ tải thủ công (sử dụng mặc định của ứng dụng)." : "Manual download mode DISABLED (using app defaults)."));
    }

    [ObservableProperty]
    private string[] _manualDownloadModes = new[] { "Trực tiếp", "Nhúng" };

    [ObservableProperty]
    private string _selectedManualDownloadMode = InstanceConfigService.Instance.ManualDownloadMode == "Embedded" ? "Nhúng" : "Trực tiếp";

    partial void OnSelectedManualDownloadModeChanged(string value)
    {
        string mode = (value != null && (value.Contains("Nhúng", StringComparison.OrdinalIgnoreCase) || value.Contains("Embedded", StringComparison.OrdinalIgnoreCase)))
            ? "Embedded"
            : "Direct";
        InstanceConfigService.Instance.ManualDownloadMode = mode;
        if (IsManualDownloadMode)
        {
            AddLog("INFO", _langService.CurrentLanguage == "VI"
                ? $"Đã đổi chế độ tải cài sẵn sang: {value}"
                : $"Changed manual download preset to: {value}");
        }
    }

    // BACKGROUND EXECUTION & BUBBLE MODE (BONG BÓNG CHẠY NGẦM)
    [ObservableProperty]
    private bool _isBubbleMode = false;

    partial void OnIsBubbleModeChanged(bool value)
    {
        if (BackgroundExecutionService.Instance.IsBubbleMode != value)
        {
            BackgroundExecutionService.Instance.SetBubbleMode(value);
        }
    }

    [ObservableProperty]
    private bool _isExitConfirmOpen = false;

    [ObservableProperty]
    private string _bubbleStatusTitle = "Sẵn sàng";

    [ObservableProperty]
    private string _bubbleStatusDetail = "Không có tác vụ chạy ngầm";

    [ObservableProperty]
    private double _bubbleProgress = 0;

    [ObservableProperty]
    private string _bubbleProgressText = "0%";

    [ObservableProperty]
    private bool _isBackgroundWorking = false;

    // CONCURRENT COMIC DOWNLOADS, IMAGE THREADS & LINK CPU THREADS
    public int MaxSystemThreads => CpuTopologyHelper.GetMaxLogicalProcessorCount();
    public int MaxLinkCpuThreads => OperatingSystem.IsAndroid() ? 6 : MaxSystemThreads;
    public int MaxFolderToolThreads => Math.Max(64, MaxSystemThreads);

    [ObservableProperty]
    private int _linkCpuThreads = OperatingSystem.IsAndroid()
        ? 3
        : Math.Min(4, CpuTopologyHelper.GetMaxLogicalProcessorCount());

    [ObservableProperty]
    private int _concurrentComicDownloads = 2; // 1 đến 16

    [ObservableProperty]
    private int _imageDownloadThreads = 3; // 1 đến 16

    partial void OnLinkCpuThreadsChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, MaxLinkCpuThreads);
        if (_linkCpuThreads != clamped)
        {
            _linkCpuThreads = clamped;
            OnPropertyChanged(nameof(LinkCpuThreads));
        }

        _scraperService.MaxCpuThreads = clamped;
        ThumbnailPreviewService.Instance.SetMaxCpuThreads(clamped);
        ParallelCheckCount = clamped;
        QueueBackupService.Instance.CurrentLinkCpuThreads = clamped;
        RequestQueueAutoSave(250);
    }

    [RelayCommand]
    public void DecreaseLinkCpuThreads()
    {
        if (LinkCpuThreads > 1)
        {
            LinkCpuThreads--;
        }
    }

    [RelayCommand]
    public void IncreaseLinkCpuThreads()
    {
        if (LinkCpuThreads < MaxLinkCpuThreads)
        {
            LinkCpuThreads++;
        }
    }

    partial void OnConcurrentComicDownloadsChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, 16);
        if (_concurrentComicDownloads != clamped)
        {
            _concurrentComicDownloads = clamped;
            OnPropertyChanged(nameof(ConcurrentComicDownloads));
        }

        _downloadEngine.ConcurrentComicDownloads = clamped;
        QueueBackupService.Instance.CurrentConcurrentComicDownloads = clamped;
        _downloadEngine.NotifyConcurrencyChanged();
        RequestQueueAutoSave(250);
    }

    partial void OnImageDownloadThreadsChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, 16);
        if (_imageDownloadThreads != clamped)
        {
            _imageDownloadThreads = clamped;
            OnPropertyChanged(nameof(ImageDownloadThreads));
        }

        _downloadEngine.ImageDownloadThreads = clamped;
        QueueBackupService.Instance.CurrentImageDownloadThreads = clamped;
        RequestQueueAutoSave(250);
    }

    private void SyncAutoSaveSettingsToService()
    {
        int clampedCpu = Math.Clamp(LinkCpuThreads, 1, MaxLinkCpuThreads);
        int clampedConcurrent = Math.Clamp(ConcurrentComicDownloads, 1, 16);
        int clampedConnections = Math.Clamp(ImageDownloadThreads, 1, 16);

        _scraperService.MaxCpuThreads = clampedCpu;
        ThumbnailPreviewService.Instance.SetMaxCpuThreads(clampedCpu);
        ParallelCheckCount = clampedCpu;
        _downloadEngine.ConcurrentComicDownloads = clampedConcurrent;
        _downloadEngine.ImageDownloadThreads = clampedConnections;

        QueueBackupService.Instance.CurrentLinkCpuThreads = clampedCpu;
        QueueBackupService.Instance.CurrentConcurrentComicDownloads = clampedConcurrent;
        QueueBackupService.Instance.CurrentImageDownloadThreads = clampedConnections;
    }

    // AUTO PASTE CLIPBOARD MONITORING
    [ObservableProperty]
    private bool _isAutoPasteClipboard = false;

    private CancellationTokenSource? _autoPasteCts;
    private string _lastAutoPasteText = string.Empty;

    partial void OnIsAutoPasteClipboardChanged(bool value)
    {
        if (value)
        {
            StartAutoPasteClipboardMonitoring();
            AddLog("SUCCESS", "📋 Đã BẬT tính năng Tự dán (Auto Paste) - Mọi link sao chép sẽ tự động thêm vào Queue.");
        }
        else
        {
            StopAutoPasteClipboardMonitoring();
            AddLog("INFO", "📋 Đã TẮT tính năng Tự dán (Auto Paste).");
        }
    }

    private void StartAutoPasteClipboardMonitoring()
    {
        StopAutoPasteClipboardMonitoring();
        _autoPasteCts = new CancellationTokenSource();
        var ct = _autoPasteCts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(800, ct).ConfigureAwait(false);

                    var clipboard = GetClipboard();
                    if (clipboard != null)
                    {
                        string? text = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                        {
                            try
                            {
                                return await clipboard.TryGetTextAsync();
                            }
                            catch
                            {
                                return null;
                            }
                        });

                        if (!string.IsNullOrWhiteSpace(text) && text != _lastAutoPasteText)
                        {
                            _lastAutoPasteText = text;
                            if (text.Contains("http://", StringComparison.OrdinalIgnoreCase) || text.Contains("https://", StringComparison.OrdinalIgnoreCase))
                            {
                                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                                {
                                    AddLog("INFO", "📋 Phát hiện link mới từ Clipboard, đang tự động phân tích và thêm vào Queue...");
                                    await ExtractUrlsFromTextAsync(text, clearExisting: false);
                                });
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Safe swallow
                }
            }
        }, ct);
    }

    private void StopAutoPasteClipboardMonitoring()
    {
        _autoPasteCts?.Cancel();
        _autoPasteCts = null;
    }

    // AUTO SPLIT LONG IMAGES
    [ObservableProperty]
    private bool _isAddOrderPrefix = false;

    partial void OnIsAddOrderPrefixChanged(bool value)
    {
        _downloadEngine.AddOrderPrefixToTitle = value;
    }

    [ObservableProperty]
    private int _orderPrefixStartIndex = 1;

    partial void OnOrderPrefixStartIndexChanged(int value)
    {
        if (value < 1) value = 1;
        _downloadEngine.OrderPrefixStartIndex = value;
    }

    [ObservableProperty]
    private bool _isAutoProcessImages = false;

    partial void OnIsAutoProcessImagesChanged(bool value)
    {
        _downloadEngine.AutoProcessImages = value;
    }

    [ObservableProperty]
    private bool _isAutoSplitLongImages = false;

    [ObservableProperty]
    private int _autoSplitHeight = 5000;

    partial void OnIsAutoSplitLongImagesChanged(bool value)
    {
        _downloadEngine.AutoSplitLongImages = value;
    }

    partial void OnAutoSplitHeightChanged(int value)
    {
        _downloadEngine.AutoSplitHeight = Math.Max(100, value);
    }

    public ImageEnhancerOptions GetDownloadImageEnhancerOptions()
    {
        return new ImageEnhancerOptions
        {
            Contrast = EnhanceContrast,
            Brightness = EnhanceBrightness,
            Saturation = EnhanceSaturation,
            Sharpness = EnhanceSharpness,
            NoiseReduce = EnhanceNoiseReduce,
            Quality = EnhanceQuality,
            OutputFormat = "original",
            OverwriteOriginal = true
        };
    }


    // AUTO SCROLL TO LOWEST DOWNLOADING ITEM
    [ObservableProperty]
    private bool _isAutoScrollToActive = false;

    public event Action? AutoScrollRequested;

    partial void OnIsAutoScrollToActiveChanged(bool value)
    {
        if (value)
        {
            AutoScrollRequested?.Invoke();
        }
    }

    // AUTO DELETE COMPLETED COMICS (TỰ ĐỘNG XÓA TRUYỆN ĐÃ TẢI XONG)
    [ObservableProperty]
    private bool _isAutoDeleteCompleted = false;

    partial void OnIsAutoDeleteCompletedChanged(bool value)
    {
        if (value)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(CheckAndAutoDeleteCompleted);
        }
    }

    // AUTO SPLIT CHAPTERS (TỰ ĐỘNG TÁCH CHƯƠNG ĐỂ TẢI SONG SONG SIÊU NHANH)
    [ObservableProperty]
    private string _autoSplitChaptersSelection = "OFF";

    /// <summary>
    /// Gộp sạch toàn bộ các task con đã tách về lại truyện cha ban đầu, xóa bỏ hoàn toàn dải task con cũ.
    /// </summary>
    public void CollapseAllSplitBooks()
    {
        // 1. Tìm tất cả các task con trong ComicBooks và xóa sạch
        var childItems = ComicBooks
            .Where(b => b != null && (b.IsParallelSplitChild || !string.IsNullOrEmpty(b.ParallelSplitParentUrl)))
            .ToList();

        foreach (var child in childItems)
        {
            ComicBooks.Remove(child);
        }

        // 2. Khôi phục tất cả các truyện cha về trạng thái nguyên bản ban đầu
        foreach (var parent in ComicBooks)
        {
            if (parent != null && (parent.IsParallelSplitParent || (parent.ParallelSplitChildren != null && parent.ParallelSplitChildren.Count > 0)))
            {
                parent.IsParallelSplitParent = false;
                parent.IsParallelSplitChild = false;
                parent.IsParallelSplitCollapsed = false;
                parent.ParallelSplitParentUrl = string.Empty;
                if (parent.ParallelSplitChildren != null)
                {
                    parent.ParallelSplitChildren.Clear();
                }
                parent.IsChecked = true;
                parent.Status = "Waiting";
                parent.ChapterSelectionText = string.Empty;
                parent.DetailProgressText = "Sẵn sàng";
                parent.NotifySplitHierarchyChanged();
            }
        }

        ReindexComicBooks();
        UpdateStats();
    }

    [RelayCommand]
    public async Task ApplyAutoSplitChaptersAsync()
    {
        // Luôn luôn hoàn nguyên, gộp sạch các task con cũ trước để không bao giờ bị chồng dải chương
        CollapseAllSplitBooks();

        if (string.Equals(AutoSplitChaptersSelection, "OFF", StringComparison.OrdinalIgnoreCase))
        {
            AddLog("INFO", "Đã hủy tách chương và khôi phục toàn bộ truyện trong danh sách về trạng thái nguyên bản ban đầu.");
            return;
        }

        if (!int.TryParse(AutoSplitChaptersSelection, out int bucketSize) || bucketSize <= 0)
        {
            AddLog("WARN", "Vui lòng chọn một ngưỡng chia chương cụ thể (50, 100, 200,...) hoặc OFF.");
            return;
        }

        int splitCount = await SplitEligibleBooksAsync(bucketSize);
        if (splitCount > 0)
        {
            AddLog("SUCCESS", $"Đã tự động chia nhỏ {splitCount} bộ truyện theo ngưỡng {bucketSize} chương để tải song song siêu nhanh!");
        }
        else
        {
            AddLog("INFO", $"Không tìm thấy bộ truyện nào đủ số chương cần chia nhỏ (ngưỡng: {bucketSize} chương).");
        }
    }

    /// <summary>
    /// Thu gọn hoặc mở rộng danh sách các nhánh con của truyện cha trên giao diện
    /// </summary>
    [RelayCommand]
    public void ToggleSplitCollapse(ComicBookItem? parent)
    {
        if (parent == null || !parent.HasParallelSplitChildren) return;

        int parentIndex = ComicBooks.IndexOf(parent);
        if (parentIndex < 0) return;

        parent.IsParallelSplitCollapsed = !parent.IsParallelSplitCollapsed;
        parent.NotifySplitHierarchyChanged();

        if (parent.IsParallelSplitCollapsed)
        {
            // Thu gọn: Gỡ tất cả các task con ra khỏi danh sách hiển thị
            foreach (var child in parent.ParallelSplitChildren)
            {
                ComicBooks.Remove(child);
            }
            AddLog("INFO", $"Đã thu gọn {parent.ParallelSplitChildren.Count} nhánh con của truyện '{parent.Title}'.");
        }
        else
        {
            // Mở rộng: Chèn lại tất cả các task con ngay phía sau truyện cha
            for (int i = 0; i < parent.ParallelSplitChildren.Count; i++)
            {
                var child = parent.ParallelSplitChildren[i];
                if (!ComicBooks.Contains(child))
                {
                    ComicBooks.Insert(parentIndex + 1 + i, child);
                }
            }
            AddLog("INFO", $"Đã mở rộng {parent.ParallelSplitChildren.Count} nhánh con của truyện '{parent.Title}'.");
        }

        ReindexComicBooks();
        UpdateStats();
    }

    public async Task<int> SplitEligibleBooksAsync(int bucketSize)
    {
        if (bucketSize <= 0 || ComicBooks.Count == 0) return 0;

        var itemsToSplit = ComicBooks
            .Where(b => b != null && !b.IsParallelSplitChild && string.IsNullOrWhiteSpace(b.ChapterSelectionText) && b.Status != "Completed" && b.Status != "Hoàn tất")
            .ToList();

        if (itemsToSplit.Count == 0) return 0;

        int totalSplit = 0;

        foreach (var book in itemsToSplit)
        {
            // Nạp chapter nếu chưa có
            if (book.Chapters == null || book.Chapters.Count == 0)
            {
                try
                {
                    AddLog("INFO", $"Đang nạp danh sách chương cho '{book.Title}' để phân tích tách chương...");
                    var scraped = await _scraperService.ScrapeBookAsync(book.Url, book.Index, book.PreferredLanguage);
                    if (scraped != null && scraped.Chapters != null && scraped.Chapters.Count > 0)
                    {
                        book.Chapters = scraped.Chapters;
                        book.TotalChapters = scraped.Chapters.Count;
                    }
                }
                catch (Exception ex)
                {
                    AddLog("WARN", $"Không thể nạp chapters cho '{book.Title}': {ex.Message}");
                }
            }

            if (book.Chapters != null && book.Chapters.Count > bucketSize)
            {
                int totalChaps = book.Chapters.Count;
                var ranges = BuildAutoSplitRanges(book.Chapters, bucketSize);

                if (ranges.Count > 1)
                {
                    int insertIndex = ComicBooks.IndexOf(book);
                    if (insertIndex >= 0)
                    {
                        // Đánh dấu item cha
                        book.IsParallelSplitParent = true;
                        book.IsParallelSplitChild = false;
                        book.IsParallelSplitCollapsed = false; // Mặc định mở rộng để thấy rõ các task con
                        book.IsChecked = false;
                        book.Status = "Stopped";
                        book.DetailProgressText = $"Đã tách thành {ranges.Count} dải chương";

                        // Tạo các task con
                        var children = new List<ComicBookItem>();
                        int addedCount = 0;
                        foreach (var range in ranges)
                        {
                            var clone = new ComicBookItem
                            {
                                Title = book.Title,
                                Url = book.Url,
                                Domain = book.Domain,
                                CoverUrl = book.CoverUrl,
                                ThumbnailLocalPath = book.ThumbnailLocalPath,
                                OriginalCoverLocalPath = book.OriginalCoverLocalPath,
                                ThumbnailBitmap = book.ThumbnailBitmap,
                                HighResPreviewBitmap = book.HighResPreviewBitmap,
                                PreviewTagsText = book.PreviewTagsText,
                                LatestChapter = book.LatestChapter,
                                MissingChapters = book.MissingChapters,
                                PreferredLanguage = book.PreferredLanguage,
                                ChapterSelectionText = range,
                                Status = "Waiting",
                                DetailProgressText = $"Chờ tải (Chương {range})",
                                IsChecked = true,
                                Subfolder = book.Subfolder,
                                LocalDirectory = book.LocalDirectory,
                                IsParallelSplitChild = true,
                                IsParallelSplitParent = false,
                                ParallelSplitParentUrl = book.Url
                            };

                            var filter = ChapterRangeParser.Parse(range);
                            if (filter != null)
                            {
                                clone.Chapters = book.Chapters.Where(c => filter.IsMatch(c.Title, c.ChapterNumber)).ToList();
                                clone.TotalChapters = clone.Chapters.Count > 0 ? clone.Chapters.Count : bucketSize;
                            }
                            else
                            {
                                clone.Chapters = new List<ChapterItem>(book.Chapters);
                                clone.TotalChapters = book.TotalChapters;
                            }

                            children.Add(clone);
                            addedCount++;
                            ComicBooks.Insert(insertIndex + addedCount, clone);
                        }

                        book.ParallelSplitChildren = children;
                        book.NotifySplitHierarchyChanged();

                        totalSplit++;
                        AddLog("INFO", $"Đã tách '{book.Title}' ({totalChaps} chaps) thành {ranges.Count} task tải song song ({string.Join(", ", ranges.Take(3))}...).");
                    }
                }
            }
        }

        if (totalSplit > 0)
        {
            ReindexComicBooks();
            UpdateStats();
        }

        return totalSplit;
    }

    /// <summary>
    /// Xây dựng các dải chương (Auto Split Ranges) dựa trên giá trị ChapterNumber thực tế của các chương,
    /// tránh lỗi thừa dải chương khi danh sách chứa nhiều chương thập phân.
    /// </summary>
    private static List<string> BuildAutoSplitRanges(List<ChapterItem> chapters, int bucketSize)
    {
        if (chapters == null || chapters.Count == 0 || bucketSize <= 0)
            return new List<string>();

        var chapterNumbers = chapters
            .Select(c => c.ChapterNumber)
            .Where(n => n >= 0)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        if (chapterNumbers.Count == 0)
        {
            int total = chapters.Count;
            var fallbackRanges = new List<string>();
            for (int start = 1; start <= total; start += bucketSize)
            {
                int end = Math.Min(start + bucketSize - 1, total);
                fallbackRanges.Add($"{start}-{end}");
            }
            return fallbackRanges;
        }

        bool startsAtZero = chapterNumbers[0] < 1.0;
        double actualLastChapter = chapterNumbers[chapterNumbers.Count - 1];

        int GetBucketIndex(double num)
        {
            if (startsAtZero)
            {
                return (int)Math.Floor(num / bucketSize);
            }
            return (int)Math.Floor((Math.Max(1.0, num) - 1.0) / bucketSize);
        }

        int lastBucketIndex = GetBucketIndex(actualLastChapter);
        var bucketIndices = chapterNumbers
            .Select(GetBucketIndex)
            .Distinct()
            .OrderBy(b => b)
            .ToList();

        var ranges = new List<string>(bucketIndices.Count);
        foreach (int bIndex in bucketIndices)
        {
            double start = startsAtZero && bIndex == 0 ? 0 : (bIndex * bucketSize) + 1;
            double end = bIndex == lastBucketIndex
                ? actualLastChapter
                : (startsAtZero && bIndex == 0 ? bucketSize : (bIndex + 1) * bucketSize);

            string startStr = FormatChapterNum(start);
            string endStr = FormatChapterNum(end);

            if (startStr == endStr)
            {
                ranges.Add(startStr);
            }
            else
            {
                ranges.Add($"{startStr}-{endStr}");
            }
        }

        return ranges;
    }

    private static string FormatChapterNum(double val)
    {
        if (Math.Abs(val - Math.Round(val)) < 0.0001)
        {
            return ((int)Math.Round(val)).ToString();
        }
        return val.ToString("0.#");
    }

    // MANUAL SPLIT LONG IMAGES IN FOLDER
    [ObservableProperty]
    private string _manualSplitFolderPath = string.Empty;

    [ObservableProperty]
    private int _manualSplitHeight = 5000;

    [ObservableProperty]
    private int _manualSplitQuality = 90;

    [ObservableProperty]
    private int _manualSplitThreads = Math.Min(CpuTopologyHelper.GetMaxLogicalProcessorCount(), 16);

    [ObservableProperty]
    private bool _isManualSplitting = false;

    [ObservableProperty]
    private double _manualSplitProgress = 0;

    [ObservableProperty]
    private string _manualSplitProgressText = "0.0% (0 / 0)";

    [ObservableProperty]
    private string _manualSplitCountText = "0 ảnh";

    [ObservableProperty]
    private string _manualSplitErrorCountText = "0";

    [ObservableProperty]
    private string _manualSplitCurrentFileText = "Sẵn sàng.";

    [ObservableProperty]
    private ObservableCollection<string> _manualSplitLogs = new();

    private CancellationTokenSource? _manualSplitCts;
 
    // ==========================================
    // IMAGE ENHANCEMENT (BATCH CONVERT & LIVE PREVIEW)
    // ==========================================
    [ObservableProperty]
    private string _enhanceFolderPath = string.Empty;

    [ObservableProperty]
    private string _enhanceOutputFolderPath = string.Empty;

    [ObservableProperty]
    private float _enhanceContrast = 0f; // -100 to +100

    [ObservableProperty]
    private float _enhanceBrightness = 0f; // -100 to +100

    [ObservableProperty]
    private float _enhanceSaturation = 100f; // 0 to 200

    [ObservableProperty]
    private float _enhanceSharpness = 0f; // 0 to 10

    [ObservableProperty]
    private int _enhanceNoiseReduce = 0; // 0 to 5

    // 3 BỘ PRESET TÙY CHỌN NGƯỜI DÙNG (PRESET 1, 2, 3)
    [ObservableProperty] private string _userPreset1Name = "Preset 1";
    [ObservableProperty] private float _userPreset1Contrast = 0f;
    [ObservableProperty] private float _userPreset1Brightness = 0f;
    [ObservableProperty] private float _userPreset1Saturation = 100f;
    [ObservableProperty] private float _userPreset1Sharpness = 0f;
    [ObservableProperty] private int _userPreset1NoiseReduce = 0;

    [ObservableProperty] private string _userPreset2Name = "Preset 2";
    [ObservableProperty] private float _userPreset2Contrast = 0f;
    [ObservableProperty] private float _userPreset2Brightness = 0f;
    [ObservableProperty] private float _userPreset2Saturation = 100f;
    [ObservableProperty] private float _userPreset2Sharpness = 0f;
    [ObservableProperty] private int _userPreset2NoiseReduce = 0;

    [ObservableProperty] private string _userPreset3Name = "Preset 3";
    [ObservableProperty] private float _userPreset3Contrast = 0f;
    [ObservableProperty] private float _userPreset3Brightness = 0f;
    [ObservableProperty] private float _userPreset3Saturation = 100f;
    [ObservableProperty] private float _userPreset3Sharpness = 0f;
    [ObservableProperty] private int _userPreset3NoiseReduce = 0;

    [ObservableProperty]
    private int _enhanceQuality = 90; // 10 to 100

    [ObservableProperty]
    private string _enhanceOutputFormat = "original"; // "original", "jpg", "gif", "webp"

    partial void OnEnhanceOutputFormatChanged(string value)
    {
        OnPropertyChanged(nameof(IsEnhanceFormatOriginal));
        OnPropertyChanged(nameof(IsEnhanceFormatJpg));
        OnPropertyChanged(nameof(IsEnhanceFormatGif));
        OnPropertyChanged(nameof(IsEnhanceFormatWebp));
        TriggerLivePreviewDebounced();
    }

    public bool IsEnhanceFormatOriginal
    {
        get => EnhanceOutputFormat == "original";
        set { if (value) EnhanceOutputFormat = "original"; }
    }

    public bool IsEnhanceFormatJpg
    {
        get => EnhanceOutputFormat == "jpg";
        set { if (value) EnhanceOutputFormat = "jpg"; }
    }

    public bool IsEnhanceFormatGif
    {
        get => EnhanceOutputFormat == "gif";
        set { if (value) EnhanceOutputFormat = "gif"; }
    }

    public bool IsEnhanceFormatWebp
    {
        get => EnhanceOutputFormat == "webp";
        set { if (value) EnhanceOutputFormat = "webp"; }
    }

    [RelayCommand]
    public void SelectEnhanceFormat(string format)
    {
        if (!string.IsNullOrWhiteSpace(format))
        {
            EnhanceOutputFormat = format.ToLowerInvariant();
        }
    }

    [ObservableProperty]
    private int _enhanceThreads = Math.Clamp(CpuTopologyHelper.GetMaxLogicalProcessorCount() / 2, 2, 4);

    [ObservableProperty]
    private bool _enhanceOverwriteOriginal = false;

    [ObservableProperty]
    private double _enhancePreviewZoom = 1.0; // 0.25 to 5.0

    [ObservableProperty]
    private string _enhancePreviewZoomText = "100%";

    public double EnhancePreviewZoomPercent
    {
        get => Math.Round(EnhancePreviewZoom * 100);
        set
        {
            double zoom = Math.Clamp(value / 100.0, 0.1, 10.0);
            if (Math.Abs(EnhancePreviewZoom - zoom) > 0.001)
            {
                EnhancePreviewZoom = zoom;
            }
        }
    }

    partial void OnEnhancePreviewZoomChanged(double value)
    {
        EnhancePreviewZoomText = $"{Math.Round(value * 100)}%";
        OnPropertyChanged(nameof(EnhancePreviewZoomPercent));
    }

    [ObservableProperty]
    private bool _isEnhanceFullscreenVisible = false;

    [ObservableProperty]
    private string _enhanceBeforeInfoText = "Before: 0 x 0, 0 KB";

    [ObservableProperty]
    private string _enhanceAfterInfoText = "After: 0 x 0, 0 KB";

    [ObservableProperty]
    private string _enhanceClippingInfoText = "✅ Cân bằng";

    [ObservableProperty]
    private string _enhanceImageIndexText = "Chưa có ảnh";

    [ObservableProperty]
    private string _enhanceViewMode = "Dual"; // Cố định chế độ Dual View 2 ảnh song song

    [ObservableProperty]
    private bool _isDualView = true;

    [ObservableProperty]
    private bool _isSingleView = false;

    [ObservableProperty]
    private bool _isSplitView = false;

    [ObservableProperty]
    private bool _isSingleShowingBefore = false;

    [ObservableProperty]
    private bool _isSwappedDualOrder = false; // false: Before trước After sau, true: After trước Before sau

    [ObservableProperty]
    private bool _isImmersiveDualFocus = false; // Bật/Tắt chế độ Focus ẩn thanh công cụ

    public Bitmap? FirstDualImage => IsSwappedDualOrder ? EnhancePreviewResult : EnhancePreviewOriginal;
    public Bitmap? SecondDualImage => IsSwappedDualOrder ? EnhancePreviewOriginal : EnhancePreviewResult;

    public string FirstDualBadgeTitle => IsSwappedDualOrder ? "✨ ĐÃ TỐI ƯU (AFTER)" : "📷 ẢNH GỐC (BEFORE)";
    public string SecondDualBadgeTitle => IsSwappedDualOrder ? "📷 ẢNH GỐC (BEFORE)" : "✨ ĐÃ TỐI ƯU (AFTER)";

    public string FirstDualBadgeBg => IsSwappedDualOrder ? "#0284C7" : "#1E293B";
    public string SecondDualBadgeBg => IsSwappedDualOrder ? "#1E293B" : "#0284C7";

    public string FirstDualBorderBrush => IsSwappedDualOrder ? "#0284C7" : "#1E293B";
    public string SecondDualBorderBrush => IsSwappedDualOrder ? "#1E293B" : "#0284C7";

    public string SwapDualOrderButtonText => IsSwappedDualOrder ? "🔄 After ⇄ Before" : "🔄 Before ⇄ After";
    public string ImmersiveFocusButtonText => IsImmersiveDualFocus ? "⛶ Hiện công cụ" : "⛶ Focus";

    [ObservableProperty]
    private double _enhanceLiveFrameHeight = 480.0; // Chiều cao cố định của khung Live Preview đối chiếu Before / After (px)

    [RelayCommand]
    public void SetLiveFrameHeight(double height)
    {
        EnhanceLiveFrameHeight = Math.Clamp(height, 250.0, 1400.0);
    }

    [RelayCommand]
    public void SetLiveFrameHeight400() => EnhanceLiveFrameHeight = 400.0;

    [RelayCommand]
    public void SetLiveFrameHeight480() => EnhanceLiveFrameHeight = 480.0;

    [RelayCommand]
    public void SetLiveFrameHeight560() => EnhanceLiveFrameHeight = 560.0;

    [RelayCommand]
    public void SetLiveFrameHeight650() => EnhanceLiveFrameHeight = 650.0;

    [ObservableProperty]
    private double _enhanceSplitRatio = 0.5; // 0.0 to 1.0

    [ObservableProperty]
    private bool _isSplitVerticalOrientation = false; // false: Trái / Phải, true: Trên / Dưới

    [ObservableProperty]
    private string _splitOrientationMode = "Auto"; // "Auto", "Horizontal", "Vertical"

    public double SplitClipWidth => EnhanceImagePixelWidth * Math.Clamp(EnhanceSplitRatio, 0.0, 1.0);
    public double SplitClipHeight => EnhanceImagePixelHeight * Math.Clamp(EnhanceSplitRatio, 0.0, 1.0);
    public Thickness SplitDividerMarginHorizontal => new Thickness(Math.Max(0, EnhanceImagePixelWidth * Math.Clamp(EnhanceSplitRatio, 0.0, 1.0) - 16), 0, 0, 0);
    public Thickness SplitDividerMarginVertical => new Thickness(0, Math.Max(0, EnhanceImagePixelHeight * Math.Clamp(EnhanceSplitRatio, 0.0, 1.0) - 16), 0, 0);

    public GridLength SplitLeftLength => new GridLength(Math.Clamp(EnhanceSplitRatio, 0.01, 0.99), GridUnitType.Star);
    public GridLength SplitRightLength => new GridLength(Math.Clamp(1.0 - EnhanceSplitRatio, 0.01, 0.99), GridUnitType.Star);
    public GridLength SplitTopLength => new GridLength(Math.Clamp(EnhanceSplitRatio, 0.01, 0.99), GridUnitType.Star);
    public GridLength SplitBottomLength => new GridLength(Math.Clamp(1.0 - EnhanceSplitRatio, 0.01, 0.99), GridUnitType.Star);

    public string SplitOrientationText => IsSplitVerticalOrientation ? "↕ Trên / Dưới" : "↔ Trái / Phải";

    [ObservableProperty]
    private bool _isEnhanceLogExpanded = false;

    public string EnhanceLogToggleText => IsEnhanceLogExpanded ? "📋 Ẩn Log ▼" : "📋 Xem Log Chi Tiết ▲";

    [ObservableProperty]
    private bool _isLoupeEnabled = false;

    [ObservableProperty]
    private double _loupeZoom = 2.5;

    [ObservableProperty]
    private bool _canGoPrevious = false;

    [ObservableProperty]
    private bool _canGoNext = false;

    [ObservableProperty]
    private bool _isPortraitMode = false;

    public int EnhanceDualColumns => IsPortraitMode ? 1 : 2;
    public int EnhanceDualRows => IsPortraitMode ? 2 : 1;
    public bool IsDesktopLifetime => Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
    public bool IsAndroidPlatform => OperatingSystem.IsAndroid();

    [ObservableProperty]
    private string _enhanceImageCounterText = "0 / 0";

    [ObservableProperty]
    private string _enhanceImageFileNameText = "Chưa có ảnh";

    partial void OnIsPortraitModeChanged(bool value)
    {
        OnPropertyChanged(nameof(EnhanceDualColumns));
        OnPropertyChanged(nameof(EnhanceDualRows));
        OnPropertyChanged(nameof(DualSortThumbHeight));
        UpdateSplitOrientation();
    }

    partial void OnIsEnhanceLogExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(EnhanceLogToggleText));
    }

    partial void OnEnhanceSplitRatioChanged(double value)
    {
        OnPropertyChanged(nameof(SplitClipWidth));
        OnPropertyChanged(nameof(SplitClipHeight));
        OnPropertyChanged(nameof(SplitDividerMarginHorizontal));
        OnPropertyChanged(nameof(SplitDividerMarginVertical));
        OnPropertyChanged(nameof(SplitLeftLength));
        OnPropertyChanged(nameof(SplitRightLength));
        OnPropertyChanged(nameof(SplitTopLength));
        OnPropertyChanged(nameof(SplitBottomLength));
    }

    partial void OnSplitOrientationModeChanged(string value)
    {
        UpdateSplitOrientation();
    }

    partial void OnIsSplitVerticalOrientationChanged(bool value)
    {
        OnPropertyChanged(nameof(SplitOrientationText));
    }

    public void UpdateSplitOrientation()
    {
        if (SplitOrientationMode == "Auto")
        {
            // Tự động nhận diện: màn hình dọc hoặc ảnh đứng dài (Manga/Webtoon)
            bool isTallImage = EnhanceImagePixelHeight > 0 && EnhanceImagePixelWidth > 0 && (EnhanceImagePixelHeight > EnhanceImagePixelWidth * 1.1);
            IsSplitVerticalOrientation = IsPortraitMode || isTallImage;
        }
        else
        {
            IsSplitVerticalOrientation = SplitOrientationMode == "Vertical";
        }
    }

    partial void OnEnhanceViewModeChanged(string value)
    {
        IsDualView = value == "Dual";
        IsSingleView = value == "Single";
        IsSplitView = value == "Split";
        if (IsSplitView)
        {
            UpdateSplitOrientation();
        }
    }

    [ObservableProperty]
    private bool _isEnhancing = false;

    [ObservableProperty]
    private double _enhanceProgress = 0;

    [ObservableProperty]
    private string _enhanceProgressText = "0.0%";

    [ObservableProperty]
    private string _enhanceCountText = "0 ảnh";

    [ObservableProperty]
    private string _enhanceErrorCountText = "0";

    [ObservableProperty]
    private string _enhanceCurrentFileText = "Sẵn sàng.";

    [ObservableProperty]
    private Bitmap? _enhancePreviewOriginal;

    [ObservableProperty]
    private Bitmap? _enhancePreviewResult;

    [ObservableProperty]
    private double _enhanceImagePixelWidth = 800;

    [ObservableProperty]
    private double _enhanceImagePixelHeight = 1200;

    [ObservableProperty]
    private string _enhancePreviewInfoText = "Chưa chọn thư mục hoặc ảnh xem trước.";

    [ObservableProperty]
    private ObservableCollection<string> _enhanceLogs = new();

    partial void OnEnhanceContrastChanged(float value) => TriggerLivePreviewDebounced();
    partial void OnEnhanceBrightnessChanged(float value) => TriggerLivePreviewDebounced();
    partial void OnEnhanceSaturationChanged(float value) => TriggerLivePreviewDebounced();
    partial void OnEnhanceSharpnessChanged(float value) => TriggerLivePreviewDebounced();
    partial void OnEnhanceNoiseReduceChanged(int value) => TriggerLivePreviewDebounced();
    partial void OnEnhanceQualityChanged(int value) => TriggerLivePreviewDebounced();

    partial void OnIsSwappedDualOrderChanged(bool value)
    {
        OnPropertyChanged(nameof(FirstDualImage));
        OnPropertyChanged(nameof(SecondDualImage));
        OnPropertyChanged(nameof(FirstDualBadgeTitle));
        OnPropertyChanged(nameof(SecondDualBadgeTitle));
        OnPropertyChanged(nameof(FirstDualBadgeBg));
        OnPropertyChanged(nameof(SecondDualBadgeBg));
        OnPropertyChanged(nameof(FirstDualBorderBrush));
        OnPropertyChanged(nameof(SecondDualBorderBrush));
        OnPropertyChanged(nameof(SwapDualOrderButtonText));
    }

    partial void OnIsImmersiveDualFocusChanged(bool value)
    {
        OnPropertyChanged(nameof(ImmersiveFocusButtonText));
    }

    partial void OnEnhancePreviewOriginalChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(FirstDualImage));
        OnPropertyChanged(nameof(SecondDualImage));
    }

    partial void OnEnhancePreviewResultChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(FirstDualImage));
        OnPropertyChanged(nameof(SecondDualImage));
    }

    partial void OnEnhanceImagePixelWidthChanged(double value)
    {
        OnPropertyChanged(nameof(SplitClipWidth));
        OnPropertyChanged(nameof(SplitDividerMarginHorizontal));
        UpdateSplitOrientation();
    }

    partial void OnEnhanceImagePixelHeightChanged(double value)
    {
        OnPropertyChanged(nameof(SplitClipHeight));
        OnPropertyChanged(nameof(SplitDividerMarginVertical));
        UpdateSplitOrientation();
    }

    [RelayCommand]
    public void ToggleEnhanceLog()
    {
        IsEnhanceLogExpanded = !IsEnhanceLogExpanded;
    }

    [ObservableProperty]
    private bool _isPopupPreview = true;

    public string PopupPreviewButtonText => IsPopupPreview ? "👁️ POPUP PREVIEW: BẬT" : "👁️ POPUP PREVIEW: TẮT";
    public string PopupPreviewButtonBackground => IsPopupPreview ? "#064E3B" : "#1E293B";
    public string PopupPreviewButtonBorder => IsPopupPreview ? "#10B981" : "#475569";
    public string PopupPreviewButtonForeground => IsPopupPreview ? "#FDE047" : "#94A3B8";

    partial void OnIsPopupPreviewChanged(bool value)
    {
        if (!value)
        {
            HideHoverPreview();
        }
        OnPropertyChanged(nameof(PopupPreviewButtonText));
        OnPropertyChanged(nameof(PopupPreviewButtonBackground));
        OnPropertyChanged(nameof(PopupPreviewButtonBorder));
        OnPropertyChanged(nameof(PopupPreviewButtonForeground));
    }

    [ObservableProperty]
    private bool _isThumbnailMode;

    public List<string> QueueViewModes { get; } = new()
    {
        "List view",
        "Thumbnail view",
        "List + thumbnail column"
    };

    [ObservableProperty]
    private string _selectedQueueViewMode = "List view";

    public bool IsListViewMode => !IsThumbnailMode;

    public bool IsThumbnailColumnVisible => SelectedQueueViewMode == "List + thumbnail column";

    public IEnumerable<ComicBookItem> ActiveQueueItemsSource =>
        IsFilterActive && FilteredQueueItems != null ? FilteredQueueItems : ComicBooks;

    public IEnumerable<ComicBookItem>? ActiveThumbnailItemsSource => IsThumbnailMode ? ActiveQueueItemsSource : null;

    public event Action? VisibleThumbnailScanRequested;

    partial void OnSelectedQueueViewModeChanged(string value)
    {
        bool thumbMode = value == "Thumbnail view";
        if (IsThumbnailMode != thumbMode)
        {
            IsThumbnailMode = thumbMode;
        }
        OnPropertyChanged(nameof(IsListViewMode));
        OnPropertyChanged(nameof(IsThumbnailColumnVisible));
        OnPropertyChanged(nameof(ActiveThumbnailItemsSource));
        VisibleThumbnailScanRequested?.Invoke();
        if (thumbMode || value == "List + thumbnail column" || IsPopupPreview)
        {
            _ = ThumbnailPreviewService.Instance.PrefetchBatchAsync(ComicBooks);
        }
    }

    partial void OnIsThumbnailModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsListViewMode));
        OnPropertyChanged(nameof(IsThumbnailColumnVisible));
        OnPropertyChanged(nameof(ActiveThumbnailItemsSource));
        if (value && SelectedQueueViewMode != "Thumbnail view")
        {
            SelectedQueueViewMode = "Thumbnail view";
        }
        else if (!value && SelectedQueueViewMode == "Thumbnail view")
        {
            SelectedQueueViewMode = "List view";
        }
        VisibleThumbnailScanRequested?.Invoke();
    }

    public void TriggerThumbnailPrefetchIfNeeded()
    {
        VisibleThumbnailScanRequested?.Invoke();
        if (IsThumbnailMode || IsThumbnailColumnVisible || IsPopupPreview || !OperatingSystem.IsAndroid())
        {
            _ = ThumbnailPreviewService.Instance.PrefetchBatchAsync(ComicBooks);
        }
    }

    [ObservableProperty]
    private ComicBookItem? _activeHoverItem;

    [ObservableProperty]
    private bool _isHoverPreviewVisible;

    [RelayCommand]
    private void ToggleThumbnailMode()
    {
        SelectedQueueViewMode = SelectedQueueViewMode switch
        {
            "List view" => "Thumbnail view",
            "Thumbnail view" => "List + thumbnail column",
            _ => "List view"
        };
    }

    [RelayCommand]
    private void TogglePopupPreview()
    {
        IsPopupPreview = !IsPopupPreview;
        if (IsPopupPreview)
        {
            VisibleThumbnailScanRequested?.Invoke();
        }
    }

    [RelayCommand]
    private async Task RescanThumbnailsAsync()
    {
        if (ComicBooks.Count == 0)
        {
            return;
        }

        var checkedItems = ComicBooks.Where(b => b.IsChecked || b.IsSelectedInGrid).ToList();
        var missingItems = ComicBooks.Where(b => b.ThumbnailBitmap == null || !b.HasThumbnailFile).ToList();

        List<ComicBookItem> targetItems;
        if (missingItems.Count > 0 && checkedItems.Count == ComicBooks.Count)
        {
            // Ưu tiên quét lại các mục đang bị mất thumbnail trước, sau đó quét tiếp các mục còn lại
            targetItems = missingItems.Concat(ComicBooks.Except(missingItems)).ToList();
        }
        else if (checkedItems.Count > 0)
        {
            targetItems = checkedItems;
        }
        else
        {
            targetItems = ComicBooks.ToList();
        }

        int cpuThreads = Math.Clamp(LinkCpuThreads, 1, MaxLinkCpuThreads);
        AddLog("INFO", $"🔄 Đang quét lại thumbnail cho {targetItems.Count} truyện (Số luồng CPU: {cpuThreads})...");
        using var sem = new SemaphoreSlim(cpuThreads, cpuThreads);
        var tasks = targetItems.Select(async item =>
        {
            await sem.WaitAsync().ConfigureAwait(false);
            try
            {
                await ThumbnailPreviewService.Instance.RescanItemAsync(item).ConfigureAwait(false);
            }
            finally
            {
                sem.Release();
            }
        });
        await Task.WhenAll(tasks);
        AddLog("SUCCESS", $"✅ Đã hoàn tất quét lại thumbnail ({targetItems.Count} truyện).");
    }

    [RelayCommand]
    private void CloseHoverPreview()
    {
        HideHoverPreview();
    }

    public void ShowHoverPreview(ComicBookItem? item)
    {
        if (!IsPopupPreview || item == null)
        {
            HideHoverPreview();
            return;
        }
        ActiveHoverItem = item;
        IsHoverPreviewVisible = true;
        _ = ThumbnailPreviewService.Instance.EnsureHighResPreviewAsync(item);
    }

    public void HideHoverPreview()
    {
        IsHoverPreviewVisible = false;
        if (ActiveHoverItem != null)
        {
            ActiveHoverItem.HighResPreviewBitmap = null;
        }
        ActiveHoverItem = null;
    }

    [ObservableProperty]
    private int _parallelCheckCount = OperatingSystem.IsAndroid()
        ? 3
        : Math.Min(4, CpuTopologyHelper.GetMaxLogicalProcessorCount());

    [ObservableProperty]
    private string _statusSummary = "Sẵn sàng";

    [ObservableProperty]
    private int _totalBooks;

    [ObservableProperty]
    private int _downloadingCount;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private string _downloadSpeedText = "0.0 KB/s";

    [ObservableProperty]
    private string _downloadPathText = string.Empty;

    // SOURCE SEARCH
    [ObservableProperty]
    private string _sourceSearchKeyword = string.Empty;

    [ObservableProperty]
    private bool _isSearchingSource;

    [ObservableProperty]
    private bool _searchMangaDex = true;

    [ObservableProperty]
    private bool _searchTruyenqq = true;

    [ObservableProperty]
    private bool _searchNettruyen = true;

    [ObservableProperty]
    private bool _searchHako = true;

    [ObservableProperty]
    private string _filterStatus = "Tất cả";

    [ObservableProperty]
    private string _filterKeyword = string.Empty;

    [ObservableProperty]
    private ComicBookItem? _selectedComic;

    // MANGADEX LANGUAGE SELECTION MODAL PROMPT
    [ObservableProperty]
    private bool _isMangadexPromptVisible;

    [ObservableProperty]
    private bool _mangadexLangVi = true;

    [ObservableProperty]
    private bool _mangadexLangEn = false;

    [ObservableProperty]
    private bool _mangadexFallback = true;

    private TaskCompletionSource<MangadexLanguageChoice?>? _mangadexPromptTcs;

    [RelayCommand]
    public void SelectMangadexVi()
    {
        MangadexLangVi = true;
        MangadexLangEn = false;
    }

    [RelayCommand]
    public void SelectMangadexEn()
    {
        MangadexLangVi = false;
        MangadexLangEn = true;
    }

    [RelayCommand]
    public void ConfirmMangadexLanguage()
    {
        IsMangadexPromptVisible = false;
        var choice = new MangadexLanguageChoice
        {
            PrimaryLanguage = MangadexLangVi ? "vi" : "en",
            UseFallback = MangadexFallback
        };
        _mangadexPromptTcs?.TrySetResult(choice);
    }

    [RelayCommand]
    public void CancelMangadexLanguage()
    {
        IsMangadexPromptVisible = false;
        _mangadexPromptTcs?.TrySetResult(null);
    }

    public Task<MangadexLanguageChoice?> PromptMangadexLanguageAsync()
    {
        _mangadexPromptTcs?.TrySetCanceled();
        _mangadexPromptTcs = new TaskCompletionSource<MangadexLanguageChoice?>();
        IsMangadexPromptVisible = true;
        return _mangadexPromptTcs.Task;
    }

    public ObservableCollection<ComicBookItem> ComicBooks { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveQueueItemsSource))]
    [NotifyPropertyChangedFor(nameof(ActiveThumbnailItemsSource))]
    private ObservableCollection<ComicBookItem>? _filteredQueueItems;
    public ObservableCollection<MissingScanItem> ScanResults { get; } = new();
    public ObservableCollection<LogMessageItem> Logs { get; } = new();
    public ObservableCollection<SourceSearchResultItem> SearchResults { get; } = new();

    // ==========================================
    // BATCH COMIC SEARCH ENGINE (TÌM TRUYỆN HÀNG LOẠT)
    // ==========================================
    [ObservableProperty]
    private string _searchBookNamesInput = string.Empty;

    [ObservableProperty]
    private string _selectedSearchEngine = "Google";

    public List<string> SearchEngines { get; } = new() { "Google", "Bing", "DuckDuckGo", "Yahoo", "Yandex" };

    [ObservableProperty]
    private bool _isSearchOpenNewWindow = true;

    [ObservableProperty]
    private string _searchStatusMessage = "Nhập danh sách tên truyện, chọn domain và bấm 'TÌM KIẾM' để mở kết quả.";

    public ObservableCollection<SearchDomainItem> SearchDomains { get; } = new();

    public int SearchBookCount
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SearchBookNamesInput)) return 0;
            return SearchBookNamesInput.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Count(s => !string.IsNullOrWhiteSpace(s));
        }
    }

    public string SearchBookCountText => $"Số lượng: {SearchBookCount} truyện";

    // ==========================================
    // CENTER TOAST NOTIFICATION OVERLAY
    // ==========================================
    [ObservableProperty] private bool _isCenterToastVisible;
    [ObservableProperty] private string _centerToastTitle = string.Empty;
    [ObservableProperty] private string _centerToastMessage = string.Empty;
    [ObservableProperty] private string _centerToastIcon = "✨";
    [ObservableProperty] private string _centerToastBadge = "THÔNG BÁO";
    [ObservableProperty] private bool _hasAvailableUpdateInToast;
    [ObservableProperty] private string _toastUpdateButtonText = "⚡ CẬP NHẬT NGAY";
    [ObservableProperty] private string _toastUpdateTarget = "";
    private CancellationTokenSource? _toastCts;

    public void ShowCenterToast(string title, string message, string icon = "✨", string badge = "THÔNG BÁO", int durationMs = 3200)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _toastCts?.Cancel();
            _toastCts = new CancellationTokenSource();
            var token = _toastCts.Token;

            CenterToastTitle = title;
            CenterToastMessage = message;
            CenterToastIcon = icon;
            CenterToastBadge = badge;
            IsCenterToastVisible = true;

            if (durationMs > 0)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(durationMs, token);
                        if (!token.IsCancellationRequested)
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                IsCenterToastVisible = false;
                                HasAvailableUpdateInToast = false;
                            });
                        }
                    }
                    catch (OperationCanceledException) { }
                }, token);
            }
        });
    }

    [RelayCommand]
    public void DismissCenterToast()
    {
        _toastCts?.Cancel();
        IsCenterToastVisible = false;
        HasAvailableUpdateInToast = false;
    }

    [RelayCommand]
    public async Task ConfirmUpdateFromToastAsync()
    {
        IsCenterToastVisible = false;
        string target = ToastUpdateTarget;
        HasAvailableUpdateInToast = false;

        if (string.Equals(target, "beta", StringComparison.OrdinalIgnoreCase))
        {
            await AutoUpdateBetaAsync();
        }
        else
        {
            await AutoUpdateAsync();
        }
    }

    partial void OnSearchBookNamesInputChanged(string value)
    {
        OnPropertyChanged(nameof(SearchBookCount));
        OnPropertyChanged(nameof(SearchBookCountText));
    }

    private bool _isAutoSaveSuspended = true;
    private long _lastProgressAutoSaveTick;

    public MainViewModel()
    {
        _downloadPathText = _downloadEngine.DownloadRoot;
        _downloadEngine.BindLiveQueueSource(ComicBooks);
        ComicBooks.CollectionChanged += (s, e) =>
        {
            if (IsFilterActive)
            {
                RequestQueueFilter(50);
            }
        };
        _downloadEngine.AddOrderPrefixToTitle = IsAddOrderPrefix;
        _downloadEngine.OrderPrefixStartIndex = OrderPrefixStartIndex;
        _downloadEngine.GetImageEnhancerOptions = GetDownloadImageEnhancerOptions;
        _downloadEngine.AutoProcessImages = IsAutoProcessImages;
        _langService.LanguageChanged += OnLanguageChanged;
        _downloadEngine.LogEmitted += OnLogEmitted;
        _downloadEngine.ProgressUpdated += OnProgressUpdated;
        _downloadEngine.DownloadingStateChanged += (downloading) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsDownloading = downloading;
                if (!downloading)
                {
                    RequestQueueAutoSave(200);
                }
            });
        };
        _downloadEngine.PauseStateChanged += (paused) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsPaused = paused;
                RequestQueueAutoSave(200);
            });
        };
        DownloadEngineService.AndroidOpenFolderRequested += OnAndroidOpenFolderRequested;
        LoadUserPresetsFromDisk();
        InitializeSearchDomains();
        InitializeQueueAutoBackupAndRestore();
        InitGoogleDriveService();

        // Lắng nghe thay đổi trạng thái Modal của LibraryVm để tự động ẩn/hiện FAB
        LibraryVm.PropertyChanged += (s, e) =>
        {
            if (e?.PropertyName == nameof(LibraryViewModel.IsCacheDataModalOpen))
            {
                OnPropertyChanged(nameof(IsFloatingQuickControlVisible));
            }
        };

        // Tạm dừng các tác vụ nền không cần thiết (như kiểm tra Clipboard liên tục) khi người dùng mở Trình Đọc Truyện để hạ nhiệt máy
        ComicReaderService.Instance.ReaderActiveStateChanged += (isActive) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (isActive)
                {
                    StopAutoPasteClipboardMonitoring();
                }
                else if (IsAutoPasteClipboard)
                {
                    StartAutoPasteClipboardMonitoring();
                }
            });
        };

        // Lắng nghe lệnh từ Android Foreground Notification & Chat Head
        BackgroundExecutionService.NativeNotificationContextualAction1Requested = () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                var primary = BackgroundExecutionService.Instance.GetPrimaryTask();
                string taskId = primary?.Id ?? "download_queue";
                switch (taskId)
                {
                    case "image_enhancer":
                        StopEnhance();
                        break;
                    case "file_packer":
                        StopPacking();
                        break;
                    case "image_splitter":
                        StopManualSplit();
                        break;
                    case "folder_tools":
                        StopFolderTool();
                        break;
                    case "download_queue":
                    default:
                        await ToggleDownloadAsync();
                        break;
                }
            });
        };

        BackgroundExecutionService.NativeNotificationContextualAction2Requested = () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var primary = BackgroundExecutionService.Instance.GetPrimaryTask();
                string taskId = primary?.Id ?? "download_queue";
                if (taskId == "download_queue")
                {
                    TogglePause();
                }
            });
        };

        BackgroundExecutionService.NativeNotificationToggleDownloadRequested = () => Avalonia.Threading.Dispatcher.UIThread.Post(async () => await ToggleDownloadAsync());
        BackgroundExecutionService.NativeNotificationTogglePauseRequested = () => Avalonia.Threading.Dispatcher.UIThread.Post(TogglePause);

        // Lắng nghe sự kiện chạy ngầm & bong bóng
        var bg = BackgroundExecutionService.Instance;
        bg.TaskProgressChanged += OnBackgroundTaskProgressChanged;
        bg.AnyTaskRunningChanged += OnAnyTaskRunningChanged;
        bg.BubbleModeChanged += OnBubbleModeChanged;

        long lastEnhanceLogTick = 0;
        _imageEnhancer.LogEmitted += (level, msg) =>
        {
            long now = Environment.TickCount64;
            // Luôn ưu tiên hiển thị log lỗi và cảnh báo, các log thành công đơn lẻ được throttle 150ms để không nghẽn UI
            if (level == "ERROR" || level == "WARN" || level == "INFO" || now - lastEnhanceLogTick >= 150)
            {
                lastEnhanceLogTick = now;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    EnhanceLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [{level}] {msg}");
                    while (EnhanceLogs.Count > 300) EnhanceLogs.RemoveAt(EnhanceLogs.Count - 1);
                });
            }
        };

        long lastEnhanceProgressTick = 0;
        _imageEnhancer.ProgressUpdated += (pct, currentFile) =>
        {
            long now = Environment.TickCount64;
            if (pct >= 100.0 || now - lastEnhanceProgressTick >= 120)
            {
                lastEnhanceProgressTick = now;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    EnhanceProgress = pct;
                    EnhanceProgressText = $"{pct:0.0}%";
                    EnhanceCurrentFileText = currentFile;
                });
                BackgroundExecutionService.Instance.ReportProgress("image_enhancer", "Xử Lý Ảnh", $"Đang xử lý: {currentFile}", pct, true);
            }
        };

        UpdateLanguageStrings();
        InitFolderToolsService();
        try { InitArchiveMerger(); } catch { }
        LoadPasswordManagerSettings();
        _ = Task.Run(async () => await EnsureDamconuongRedirectDomainAsync().ConfigureAwait(false));
        AddLog("INFO", "Hệ thống Comic Downloader GMTPC Avalonia khởi chạy thành công (Hỗ trợ: Windows, Linux, Android).");
        SoundNotificationService.Instance.PlaySound(SoundNotificationType.Startup);
    }

    private void OnBackgroundTaskProgressChanged(BackgroundTaskInfo task)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (task.IsRunning)
                {
                    BubbleStatusTitle = task.Title;
                    BubbleStatusDetail = task.Detail;
                    BubbleProgress = task.ProgressPercentage;
                    BubbleProgressText = $"{task.ProgressPercentage:0}%";
                    IsBackgroundWorking = true;
                }
                else
                {
                    var primary = BackgroundExecutionService.Instance.GetPrimaryTask();
                    if (primary != null)
                    {
                        BubbleStatusTitle = primary.Title;
                        BubbleStatusDetail = primary.Detail;
                        BubbleProgress = primary.ProgressPercentage;
                        BubbleProgressText = $"{primary.ProgressPercentage:0}%";
                        IsBackgroundWorking = true;
                    }
                    else
                    {
                        BubbleStatusTitle = "Hoàn tất";
                        BubbleStatusDetail = task.Detail;
                        BubbleProgress = 100;
                        BubbleProgressText = "100%";
                        IsBackgroundWorking = false;
                    }
                }
            }
            catch
            {
                // Bảo vệ an toàn luồng UI
            }
        });
    }

    private void OnAnyTaskRunningChanged(bool anyRunning)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                IsBackgroundWorking = anyRunning;
                if (!anyRunning)
                {
                    BubbleStatusTitle = "Sẵn sàng";
                    BubbleStatusDetail = "Tất cả tác vụ nền đã hoàn thành";
                }
            }
            catch
            {
                // Bảo vệ an toàn luồng UI
            }
        });
    }

    private void OnBubbleModeChanged(bool enabled)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (IsBubbleMode != enabled)
            {
                IsBubbleMode = enabled;
            }
        });
    }

    [RelayCommand]
    public void OpenExitConfirm()
    {
        IsExitConfirmOpen = true;
    }

    [RelayCommand]
    public void CancelExitConfirm()
    {
        IsExitConfirmOpen = false;
    }

    [RelayCommand]
    public void MinimizeOrHideApp()
    {
        IsExitConfirmOpen = false;
        BackgroundExecutionService.Instance.MinimizeOrHide();
    }

    private void InitializeQueueAutoBackupAndRestore()
    {
        SyncAutoSaveSettingsToService();
        ComicBooks.CollectionChanged += OnComicBooksCollectionChanged;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushAutoSaveImmediately();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => FlushAutoSaveImmediately();
        _ = RestoreAutoSavedQueueOnStartupAsync();
    }

    private void ApplyRestoredQueueSettings(QueueRestoreResult? restoreResult)
    {
        if (restoreResult == null) return;

        if (restoreResult.ConcurrentComicDownloads.HasValue && restoreResult.ConcurrentComicDownloads.Value > 0)
        {
            ConcurrentComicDownloads = Math.Clamp(restoreResult.ConcurrentComicDownloads.Value, 1, 16);
        }
        if (restoreResult.ImageDownloadThreads.HasValue && restoreResult.ImageDownloadThreads.Value > 0)
        {
            ImageDownloadThreads = Math.Clamp(restoreResult.ImageDownloadThreads.Value, 1, 16);
        }
        if (restoreResult.LinkCpuThreads.HasValue && restoreResult.LinkCpuThreads.Value > 0)
        {
            LinkCpuThreads = Math.Clamp(restoreResult.LinkCpuThreads.Value, 1, MaxLinkCpuThreads);
        }

        SyncAutoSaveSettingsToService();
    }

    private async Task RestoreAutoSavedQueueOnStartupAsync()
    {
        _isAutoSaveSuspended = true;
        try
        {
            var restoreResult = await QueueBackupService.Instance.LoadAutoSaveQueueResultAsync().ConfigureAwait(false);
            if (restoreResult.HasValidStructure || restoreResult.Items.Count > 0)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ApplyRestoredQueueSettings(restoreResult);

                    foreach (var item in restoreResult.Items)
                    {
                        if (!ComicBooks.Any(b => b.Url.Equals(item.Url, StringComparison.OrdinalIgnoreCase) &&
                                                 string.Equals(b.ChapterSelectionText, item.ChapterSelectionText, StringComparison.OrdinalIgnoreCase)))
                        {
                            SubscribeComicBookItemAutosave(item);
                            ComicBooks.Add(item);
                        }
                    }

                    string fileName = !string.IsNullOrWhiteSpace(restoreResult.LoadedFromPath)
                        ? Path.GetFileName(restoreResult.LoadedFromPath)
                        : QueueBackupService.AutoSaveFileName;

                    if (ComicBooks.Count > 0)
                    {
                        ReindexComicBooks();
                        UpdateStats();
                        StartScanMissing();
                        TriggerThumbnailPrefetchIfNeeded();
                        AddLog("SUCCESS", $"🔄 [Auto Backup] Đã tự động khôi phục {ComicBooks.Count} truyện, tiến độ tải và cấu hình (Số truyện: {ConcurrentComicDownloads}, Connection: {ImageDownloadThreads}, Luồng CPU: {LinkCpuThreads}) từ '{fileName}'.");
                    }
                    else if (restoreResult.ConcurrentComicDownloads.HasValue || restoreResult.ImageDownloadThreads.HasValue || restoreResult.LinkCpuThreads.HasValue)
                    {
                        AddLog("INFO", $"🔄 [Auto Backup] Đã khôi phục cấu hình tải (Số truyện: {ConcurrentComicDownloads}, Connection: {ImageDownloadThreads}, Luồng CPU: {LinkCpuThreads}) từ '{fileName}'.");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                AddLog("WARN", $"[Auto Backup] Không thể đọc file autosave khi khởi động: {ex.Message}");
            });
        }
        finally
        {
            _isAutoSaveSuspended = false;
        }
    }

    private void OnComicBooksCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var oldItem in e.OldItems.OfType<ComicBookItem>())
            {
                UnsubscribeComicBookItemAutosave(oldItem);
            }
        }

        if (e.NewItems != null)
        {
            foreach (var newItem in e.NewItems.OfType<ComicBookItem>())
            {
                SubscribeComicBookItemAutosave(newItem);
                _downloadEngine.NotifyQueueOrderChanged(newItem);
            }
        }

        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move)
        {
            _downloadEngine.NotifyQueueOrderChanged();
        }

        if (!_isAutoSaveSuspended)
        {
            RequestQueueAutoSave(400);
        }
    }

    private void SubscribeComicBookItemAutosave(ComicBookItem? item)
    {
        if (item == null) return;
        item.PropertyChanged -= OnComicBookItemPropertyChanged;
        item.PropertyChanged += OnComicBookItemPropertyChanged;

        if (item.ParallelSplitChildren != null)
        {
            foreach (var child in item.ParallelSplitChildren)
            {
                if (child == null) continue;
                child.PropertyChanged -= OnComicBookItemPropertyChanged;
                child.PropertyChanged += OnComicBookItemPropertyChanged;
            }
        }
    }

    private void UnsubscribeComicBookItemAutosave(ComicBookItem? item)
    {
        if (item == null) return;
        item.PropertyChanged -= OnComicBookItemPropertyChanged;

        if (item.ParallelSplitChildren != null)
        {
            foreach (var child in item.ParallelSplitChildren)
            {
                if (child == null) continue;
                child.PropertyChanged -= OnComicBookItemPropertyChanged;
            }
        }
    }

    private void OnComicBookItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not ComicBookItem item || string.IsNullOrWhiteSpace(e.PropertyName))
            return;

        if (string.Equals(e.PropertyName, nameof(ComicBookItem.IsChecked), StringComparison.Ordinal))
        {
            _downloadEngine.NotifyQueueOrderChanged(item);
            RequestQueueAutoSave(300);
            return;
        }

        if (string.Equals(e.PropertyName, nameof(ComicBookItem.Status), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ComicBookItem.DownloadedChapters), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ComicBookItem.Subfolder), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ComicBookItem.IsSubfolderLocked), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ComicBookItem.ChapterSelectionText), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ComicBookItem.LocalDirectory), StringComparison.Ordinal))
        {
            RequestQueueAutoSave(400);
            return;
        }

        if (string.Equals(e.PropertyName, nameof(ComicBookItem.ProgressPercentage), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ComicBookItem.DownloadedPages), StringComparison.Ordinal))
        {
            long now = Environment.TickCount64;
            long progressAutoSaveInterval = OperatingSystem.IsAndroid() ? 15000 : 1800;
            if (now - _lastProgressAutoSaveTick >= progressAutoSaveInterval)
            {
                _lastProgressAutoSaveTick = now;
                RequestQueueAutoSave(500);
            }
        }
    }

    public void RequestQueueAutoSave(int delayMs = 500)
    {
        if (_isAutoSaveSuspended) return;
        SyncAutoSaveSettingsToService();
        QueueBackupService.Instance.TriggerDebouncedAutoSave(ComicBooks, delayMs);
        if (_gdriveService.IsConnected && _gdriveService.AutoSyncEnabled)
        {
            _gdriveService.TriggerDebouncedAutoSync(() => ComicBooks.ToList());
        }
    }

    public void FlushAutoSaveImmediately()
    {
        ReadProgressStore.Instance.FlushImmediately();
        if (_isAutoSaveSuspended) return;
        SyncAutoSaveSettingsToService();
        QueueBackupService.Instance.FlushAutoSaveImmediately(ComicBooks);
    }

    [RelayCommand]
    public void ForceExitApp()
    {
        IsExitConfirmOpen = false;
        try
        {
            FlushAutoSaveImmediately();
            Stop();
            StopManualSplit();
            StopEnhance();
            StopPacking();
            StopFolderTool();
            FlushAutoSaveImmediately();
        }
        catch {}

        BackgroundExecutionService.Instance.ForceExit();
    }

    private async void OnAndroidOpenFolderRequested(string path)
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel != null)
            {
                if (topLevel.Clipboard != null)
                {
                    await topLevel.Clipboard.SetTextAsync(path);
                }
                if (topLevel.Launcher != null)
                {
                    try
                    {
                        await topLevel.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
                    }
                    catch {}
                }
            }
            AddLog("SUCCESS", $"[Android] Đã sao chép đường dẫn vào Clipboard:\n{path}\nBạn có thể dán vào ứng dụng Quản Lý Tệp (Files / ZArchiver) để mở!");
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Lỗi thao tác thư mục Android: {ex.Message}");
        }
    }

    private void OnLanguageChanged()
    {
        UpdateLanguageStrings();
    }

    private void UpdateLanguageStrings()
    {
        CurrentLanguage = _langService.CurrentLanguage;
        bool isVi = _langService.IsVietnamese;

        AppTitle = isVi
            ? "Comic-GMTPC Avalonia v1.0 - Tiếng Việt"
            : "Comic-GMTPC Avalonia v1.0 - English";

        ModeSelections = isVi
            ? new[] { "Truyện đơn", "Nhiều truyện" }
            : new[] { "Single comic", "Multi-comic" };

        if (isVi)
        {
            if (ModeSelection != null && ModeSelection.Contains("Multi", StringComparison.OrdinalIgnoreCase))
                ModeSelection = "Nhiều truyện";
            else
                ModeSelection = "Truyện đơn";
        }
        else
        {
            if (ModeSelection != null && ModeSelection.Contains("Nhiều", StringComparison.OrdinalIgnoreCase))
                ModeSelection = "Multi-comic";
            else
                ModeSelection = "Single comic";
        }

        ManualDownloadModes = isVi
            ? new[] { "Trực tiếp", "Nhúng" }
            : new[] { "Direct", "Embedded" };

        bool isEmbedded = SelectedManualDownloadMode != null &&
            (SelectedManualDownloadMode.Contains("Nhúng", StringComparison.OrdinalIgnoreCase) ||
             SelectedManualDownloadMode.Contains("Embedded", StringComparison.OrdinalIgnoreCase));

        SelectedManualDownloadMode = isVi
            ? (isEmbedded ? "Nhúng" : "Trực tiếp")
            : (isEmbedded ? "Embedded" : "Direct");

        if (!IsUpdating)
        {
            UpdateButtonLabel = isVi ? "🚀 CẬP NHẬT RELEASE" : "🚀 UPDATE RELEASE";
            UpdateBetaButtonLabel = isVi ? "🧪 CẬP NHẬT BETA" : "🧪 UPDATE BETA";
        }

        UpdateDualSortLanguageStrings();
    }

    private void UpdateDualSortLanguageStrings()
    {
        OnPropertyChanged(nameof(DualSortBatchToggleBtnText));
        OnPropertyChanged(nameof(DualSortBatchButtonText));
        OnPropertyChanged(nameof(DualSortPreviewButtonText));
        OnPropertyChanged(nameof(DualSortPreviewButtonShortText));
    }

    private int _isProgressUiUpdatePending;

    private void OnProgressUpdated()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _isProgressUiUpdatePending, 1, 0) != 0)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                DownloadSpeedText = _downloadEngine.CurrentSpeedText;
                UpdateStats();
                if (IsAutoDeleteCompleted)
                {
                    CheckAndAutoDeleteCompleted();
                }

                long now = Environment.TickCount64;
                long progressAutoSaveInterval = OperatingSystem.IsAndroid() ? 15000 : 2000;
                if (now - _lastProgressAutoSaveTick >= progressAutoSaveInterval)
                {
                    _lastProgressAutoSaveTick = now;
                    RequestQueueAutoSave(500);
                }
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _isProgressUiUpdatePending, 0);
            }
        });
    }

    [RelayCommand]
    public void ToggleLanguage()
    {
        _langService.ToggleLanguage();
        AddLog("INFO", $"Chuyển đổi ngôn ngữ sang: {_langService.CurrentLanguage}");
    }

    // ==========================================
    // SOURCE SEARCH & DISCOVERY (Cơ chế get link qua Source)
    // ==========================================

    [RelayCommand]
    public async Task SearchSourceAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceSearchKeyword))
        {
            AddLog("WARN", "Vui lòng nhập tên truyện hoặc từ khóa tìm kiếm!");
            return;
        }

        var domains = new List<string>();
        if (SearchMangaDex) domains.Add("mangadex.org");
        if (SearchTruyenqq) domains.Add("truyenqq");
        if (SearchNettruyen) domains.Add("nettruyenviet10.com");
        if (SearchHako) domains.Add("hako.vn");

        if (domains.Count == 0)
        {
            AddLog("WARN", "Vui lòng chọn ít nhất một nguồn truyện (Source)!");
            return;
        }

        SearchResults.Clear();
        IsSearchingSource = true;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();

        AddLog("INFO", $"Đang tìm kiếm '{SourceSearchKeyword}' trên các nguồn: {string.Join(", ", domains)}...");

        try
        {
            var found = await _sourceSearchService.SearchAsync(SourceSearchKeyword, domains, _searchCts.Token);
            foreach (var item in found)
            {
                SearchResults.Add(item);
            }

            AddLog("SUCCESS", $"Tìm thấy {SearchResults.Count} truyện phù hợp từ các nguồn.");
        }
        catch (Exception ex)
        {
            AddLog("ERROR", $"Tìm kiếm nguồn gặp lỗi: {ex.Message}");
        }
        finally
        {
            IsSearchingSource = false;
        }
    }

    [RelayCommand]
    public void OpenExternalSearch(string isBing)
    {
        if (string.IsNullOrWhiteSpace(SourceSearchKeyword)) return;

        bool bing = isBing == "True" || isBing == "true";
        string targetDomain = SearchMangaDex ? "mangadex.org" : (SearchTruyenqq ? "truyenqqto.com" : "nettruyenviet10.com");
        string url = _sourceSearchService.BuildExternalSearchUrl(SourceSearchKeyword, targetDomain, bing);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsLinux())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", $"\"{url}\"") { UseShellExecute = true });
            }
        }
        catch {}
    }

    [RelayCommand]
    public async Task ImportSelectedSearchToQueueAsync()
    {
        var selected = SearchResults.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            AddLog("WARN", "Chưa chọn truyện nào từ kết quả tìm kiếm!");
            return;
        }

        AddLog("INFO", $"Đang thêm {selected.Count} truyện từ nguồn tìm kiếm vào hàng chờ tải...");
        int startIndex = ComicBooks.Count + 1;

        bool hasMangadex = selected.Any(s => DomainRoutingService.DetectDomain(s.Url).Contains("mangadex"));
        string mangadexLang = "vi";
        bool mangadexFallback = true;
        if (hasMangadex)
        {
            AddLog("INFO", "Phát hiện truyện MangaDex trong danh sách chọn. Vui lòng chọn ngôn ngữ tải...");
            var choice = await PromptMangadexLanguageAsync();
            if (choice == null)
            {
                AddLog("WARN", "Đã hủy thao tác nạp truyện MangaDex.");
                return;
            }
            mangadexLang = choice.PrimaryLanguage;
            mangadexFallback = choice.UseFallback;
        }

        int cpuThreads = Math.Clamp(LinkCpuThreads, 1, MaxLinkCpuThreads);
        _scraperService.MaxCpuThreads = cpuThreads;
        var scrapedBooks = new ComicBookItem?[selected.Count];

        if (selected.Count <= 1 || cpuThreads <= 1)
        {
            for (int i = 0; i < selected.Count; i++)
            {
                var sr = selected[i];
                int idx = startIndex + i;
                var book = await Task.Run(() => _scraperService.ScrapeBookAsync(sr.Url, idx, mangadexLang, mangadexFallback)).ConfigureAwait(true);
                scrapedBooks[i] = book;
            }
        }
        else
        {
            using var semaphore = new SemaphoreSlim(cpuThreads, cpuThreads);
            var tasks = new List<Task>(selected.Count);
            for (int i = 0; i < selected.Count; i++)
            {
                int localIdx = i;
                var sr = selected[localIdx];
                int bookIdx = startIndex + localIdx;
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var book = await _scraperService.ScrapeBookAsync(sr.Url, bookIdx, mangadexLang, mangadexFallback).ConfigureAwait(false);
                        scrapedBooks[localIdx] = book;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }
            await Task.WhenAll(tasks).ConfigureAwait(true);
        }

        string trimmedSubfolder = SubfolderInput?.Trim() ?? string.Empty;
        for (int i = 0; i < selected.Count; i++)
        {
            var sr = selected[i];
            var book = scrapedBooks[i];
            if (book == null) continue;
            int idx = startIndex + i;
            if (!string.IsNullOrEmpty(sr.CoverUrl) && string.IsNullOrEmpty(book.CoverUrl))
            {
                book.CoverUrl = sr.CoverUrl;
            }
            if (!string.IsNullOrWhiteSpace(trimmedSubfolder))
            {
                book.Subfolder = trimmedSubfolder;
            }
            ComicBooks.Add(book);
            AddLog("INFO", $"[{idx}] Đã nạp từ nguồn: {book.Title} ({book.TotalChapters} chaps) [{book.Domain}]");
        }

        UpdateStats();
        StartScanMissing();
        TriggerThumbnailPrefetchIfNeeded();
        SelectedRootTabIndex = 2; // Tự động chuyển sang tab Download
    }

    [RelayCommand]
    public async Task GetLinkFromSpecificUrlAsync(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            UrlInput = url;
        }

        if (string.IsNullOrWhiteSpace(UrlInput))
        {
            AddLog("WARN", "Vui lòng nhập đường dẫn URL của truyện!");
            return;
        }

        await GetLinkAsync();
        SelectedRootTabIndex = 2; // Chuyển sang Tab Download để theo dõi tiến độ
    }

    // ==========================================
    // GET LINK / EXTRACTION PIPELINE
    // ==========================================

    [RelayCommand]
    public async Task GetLinkAsync()
    {
        await ExtractUrlsFromTextAsync(UrlInput, clearExisting: true);
        if (ComicBooks.Count > 0)
        {
            SelectedRootTabIndex = 2; // Chuyển sang Tab Download (Index 2), tuyệt đối không mở Tab Search (Index 1)
        }
    }

    [RelayCommand]
    public async Task GetMoreAsync()
    {
        await ExtractUrlsFromTextAsync(UrlInput, clearExisting: false);
        if (ComicBooks.Count > 0)
        {
            SelectedRootTabIndex = 2; // Chuyển sang Tab Download (Index 2), tuyệt đối không mở Tab Search (Index 1)
        }
    }

    [RelayCommand]
    public async Task PasteLinkAsync()
    {
        try
        {
            var clipboard = GetClipboard();
            if (clipboard == null)
            {
                AddLog("WARN", "Không thể truy cập Clipboard hệ thống.");
                return;
            }

            string? text = await clipboard.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                AddLog("WARN", "Clipboard hiện đang trống hoặc không chứa văn bản.");
                return;
            }

            AddLog("INFO", "📋 Đang trích xuất liên kết từ Clipboard...");
            await ExtractUrlsFromTextAsync(text, clearExisting: false);
            SelectedRootTabIndex = 2; // Chuyển sang Tab Download để theo dõi tiến độ
        }
        catch (Exception ex)
        {
            AddLog("ERROR", $"Lỗi đọc Clipboard: {ex.Message}");
        }
    }

    [RelayCommand]
    public void ClearLinks()
    {
        UrlInput = string.Empty;
        AddLog("INFO", "Đã xóa ô nhập link.");
    }

    // ==========================================
    // DOWNLOAD PIPELINE
    // ==========================================

    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task DownloadAllAsync()
    {
        if (ComicBooks.Count == 0)
        {
            AddLog("WARN", "Không có truyện nào trong hàng chờ để tải!");
            return;
        }

        if (IsPaused)
        {
            ResumeDownload();
        }

        // Tự động mở rộng các truyện đang thu gọn để hiển thị thanh tiến trình các task con đang tải song song
        foreach (var book in ComicBooks.Where(b => b.IsParallelSplitParent && b.IsParallelSplitCollapsed).ToList())
        {
            ToggleSplitCollapse(book);
        }

        AddLog("INFO", $"Bắt đầu tải {ComicBooks.Count(b => b.IsChecked)} truyện...");
        if (IsAutoScrollToActive)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                AutoScrollRequested?.Invoke();
            });
        }
        await _downloadEngine.StartDownloadAsync(ComicBooks, ModeSelection);
        UpdateStats();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task DownloadNewAsync()
    {
        // Tự động mở rộng các truyện đang thu gọn để hiển thị thanh tiến trình các task con đang tải song song
        foreach (var book in ComicBooks.Where(b => b.IsParallelSplitParent && b.IsParallelSplitCollapsed).ToList())
        {
            ToggleSplitCollapse(book);
        }

        var newItems = ComicBooks.Where(b => b.IsChecked && (b.Status == "Waiting" || b.Status == "Chờ tải" || string.IsNullOrEmpty(b.Status))).ToList();
        if (newItems.Count == 0)
        {
            AddLog("INFO", "Không có truyện mới nào đang chờ tải.");
            return;
        }

        AddLog("INFO", $"Tiếp tục nạp thêm {newItems.Count} truyện vào hàng đợi tải...");
        if (IsAutoScrollToActive)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                AutoScrollRequested?.Invoke();
            });
        }
        await _downloadEngine.DownloadNewAsync(newItems, ModeSelection);
        UpdateStats();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task ToggleDownloadAsync()
    {
        if (IsDownloading || _downloadEngine.IsDownloading || _downloadEngine.IsStopping)
        {
            Stop();
        }
        else
        {
            await DownloadAllAsync();
        }
    }

    [RelayCommand]
    public void PauseDownload()
    {
        if (!_downloadEngine.IsDownloading)
        {
            AddLog("WARN", "Hiện tại không có tiến trình tải nào đang chạy để tạm dừng.");
            return;
        }
        _downloadEngine.Pause();
    }

    [RelayCommand]
    public void ResumeDownload()
    {
        _downloadEngine.Resume();
    }

    [RelayCommand]
    public void TogglePauseDownload()
    {
        if (!_downloadEngine.IsDownloading)
        {
            AddLog("WARN", "Hiện tại không có tiến trình tải nào đang chạy để tạm dừng.");
            return;
        }
        _downloadEngine.TogglePause();
    }

    [RelayCommand]
    public void TogglePause()
    {
        TogglePauseDownload();
    }

    [RelayCommand]
    public void ScrollToTop()
    {
        ScrollToTopRequested?.Invoke();
    }

    [RelayCommand]
    public void ScrollToBottom()
    {
        ScrollToBottomRequested?.Invoke();
    }

    [RelayCommand]
    public void Stop()
    {
        _downloadEngine.Stop();
        IsDownloading = false;
        IsPaused = false;
        foreach (var item in ComicBooks.Where(b => b.Status == "Downloading" || b.Status == "Waiting" || b.Status == "Chờ tải" || b.Status == "Đang tải"))
        {
            item.Status = "Stopped";
            item.StatusMessage = "Đã dừng bởi người dùng";
            if (item.ProgressPercentage <= 0 && item.DownloadedChapters <= 0 && item.DownloadedPages <= 0)
            {
                item.DetailProgressText = "Đã dừng";
            }
            if (item.IsParallelSplitParent && item.ParallelSplitChildren != null)
            {
                foreach (var child in item.ParallelSplitChildren)
                {
                    if (child.Status == "Downloading" || child.Status == "Waiting")
                    {
                        child.Status = "Stopped";
                        child.StatusMessage = "Đã dừng";
                        if (child.ProgressPercentage <= 0 && child.DownloadedChapters <= 0 && child.DownloadedPages <= 0)
                        {
                            child.DetailProgressText = "Đã dừng";
                        }
                    }
                }
            }
        }
        UpdateStats();
        AddLog("WARN", "⏹️ Đã dừng tiến trình tải thành công.");
        FlushAutoSaveImmediately();
    }

    [RelayCommand]
    public async Task BackupQueueAsync()
    {
        if (ComicBooks.Count == 0)
        {
            AddLog("WARN", "⚠️ Hàng chờ đang trống, không có truyện nào để sao lưu.");
            ShowCenterToast("HÀNG ĐỢI TRỐNG", "Không có truyện nào trong hàng đợi để sao lưu (.md).", "⚠️", "SAO LƯU MD", 2200);
            return;
        }

        try
        {
            SyncAutoSaveSettingsToService();
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null && topLevel.StorageProvider.CanSave)
            {
                IStorageFolder? suggestedFolder = null;
                try
                {
                    string autoDir = QueueBackupService.GetAutoSaveDirectory();
                    if (Directory.Exists(autoDir))
                    {
                        suggestedFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(autoDir);
                    }
                }
                catch { }

                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Chọn nơi lưu file sao lưu hàng đợi (.md)",
                    SuggestedFileName = "saved_queue.md",
                    DefaultExtension = "md",
                    ShowOverwritePrompt = true,
                    SuggestedStartLocation = suggestedFolder,
                    FileTypeChoices = new List<FilePickerFileType>
                    {
                        new("Markdown Queue Backup (*.md)") { Patterns = new[] { "*.md" } },
                        new("All Files (*.*)") { Patterns = new[] { "*" } }
                    }
                });

                if (file == null)
                {
                    AddLog("INFO", "Đã hủy chọn nơi lưu file sao lưu Markdown (.md).");
                    return;
                }

                string? localPath = file.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(localPath))
                {
                    string savedPath = await QueueBackupService.Instance.SaveQueueToMarkdownAsync(ComicBooks, localPath);
                    AddLog("SUCCESS", $"💾 Đã sao lưu thành công {ComicBooks.Count} truyện (kèm tiến độ & cấu hình tải) ra file Markdown: {savedPath}");
                    ShowCenterToast("SAO LƯU MD THÀNH CÔNG", $"Đã lưu {ComicBooks.Count} truyện vào:\n{savedPath}", "💾", "SAO LƯU MD", 2800);
                }
                else
                {
                    string content = QueueBackupService.Instance.BuildQueueMarkdownContent(ComicBooks);
                    await using var stream = await file.OpenWriteAsync();
                    stream.SetLength(0);
                    await using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
                    await writer.WriteAsync(content);
                    await writer.FlushAsync();
                    AddLog("SUCCESS", $"💾 Đã sao lưu thành công {ComicBooks.Count} truyện (kèm tiến độ & cấu hình tải) ra file Markdown: {file.Name}");
                    ShowCenterToast("SAO LƯU MD THÀNH CÔNG", $"Đã lưu {ComicBooks.Count} truyện vào file:\n{file.Name}", "💾", "SAO LƯU MD", 2800);
                }

                _ = QueueBackupService.Instance.SaveAutoBackupSnapshotAsync(ComicBooks);
                return;
            }

            string path = await QueueBackupService.Instance.SaveQueueToMarkdownAsync(ComicBooks);
            _ = QueueBackupService.Instance.SaveAutoBackupSnapshotAsync(ComicBooks);
            AddLog("SUCCESS", $"💾 Đã sao lưu thành công {ComicBooks.Count} truyện ra file Markdown: {path}");
        }
        catch (Exception ex)
        {
            AddLog("ERROR", $"❌ Lỗi khi sao lưu hàng chờ: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task RestoreQueueAsync()
    {
        try
        {
            QueueRestoreResult restoreResult = new();
            string sourceName = QueueBackupService.AutoSaveFileName;

            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null && topLevel.StorageProvider.CanOpen)
            {
                IStorageFolder? suggestedFolder = null;
                try
                {
                    string autoDir = QueueBackupService.GetAutoSaveDirectory();
                    if (Directory.Exists(autoDir))
                    {
                        suggestedFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(autoDir);
                    }
                }
                catch { }

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Chọn file Markdown (.md) để phục hồi hàng đợi & tiến độ",
                    AllowMultiple = false,
                    SuggestedStartLocation = suggestedFolder,
                    FileTypeFilter = new List<FilePickerFileType>
                    {
                        new("Markdown Queue Backup (*.md)") { Patterns = new[] { "*.md", "*.txt" } },
                        new("All Files (*.*)") { Patterns = new[] { "*" } }
                    }
                });

                if (files == null || files.Count == 0)
                {
                    AddLog("INFO", "Đã hủy chọn file phục hồi Markdown (.md).");
                    return;
                }

                var selectedFile = files[0];
                string? localPath = selectedFile.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
                {
                    restoreResult = await QueueBackupService.Instance.LoadQueueResultFromMarkdownAsync(localPath);
                    sourceName = localPath;
                }
                else
                {
                    await using var stream = await selectedFile.OpenReadAsync();
                    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                    string content = await reader.ReadToEndAsync();
                    restoreResult = QueueBackupService.Instance.ParseQueueWithMetadataFromMarkdownContent(content, selectedFile.Name);
                    sourceName = selectedFile.Name;
                }
            }
            else
            {
                restoreResult = await QueueBackupService.Instance.LoadAutoSaveQueueResultAsync();
                if (!string.IsNullOrWhiteSpace(restoreResult.LoadedFromPath)) sourceName = restoreResult.LoadedFromPath;
            }

            bool hasRestoredSettings = restoreResult.ConcurrentComicDownloads.HasValue
                || restoreResult.ImageDownloadThreads.HasValue
                || restoreResult.LinkCpuThreads.HasValue;
            ApplyRestoredQueueSettings(restoreResult);

            var restoredItems = restoreResult.Items;
            if (restoredItems.Count == 0)
            {
                if (hasRestoredSettings)
                {
                    RequestQueueAutoSave(200);
                    AddLog("SUCCESS", $"⚙️ Đã phục hồi cấu hình tải (Số truyện: {ConcurrentComicDownloads} | Connection: {ImageDownloadThreads} | Luồng CPU: {LinkCpuThreads}) từ: {sourceName}");
                    ShowCenterToast("ĐÃ PHỤC HỒI CẤU HÌNH", $"Đã khôi phục thiết lập tải từ:\n{Path.GetFileName(sourceName)}", "⚙️", "PHỤC HỒI MD", 2400);
                    return;
                }

                AddLog("WARN", "⚠️ File sao lưu không chứa truyện nào hoặc không đúng định dạng Markdown.");
                ShowCenterToast("KHÔNG CÓ DỮ LIỆU", "File Markdown đã chọn trống hoặc không chứa dữ liệu hàng đợi.", "⚠️", "PHỤC HỒI MD", 2400);
                return;
            }

            int addedCount = 0;
            int updatedCount = 0;
            _isAutoSaveSuspended = true;
            try
            {
                foreach (var item in restoredItems)
                {
                    var existing = ComicBooks.FirstOrDefault(b =>
                        b.Url.Equals(item.Url, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(b.ChapterSelectionText, item.ChapterSelectionText, StringComparison.OrdinalIgnoreCase));

                    if (existing == null)
                    {
                        SubscribeComicBookItemAutosave(item);
                        ComicBooks.Add(item);
                        addedCount++;
                    }
                    else
                    {
                        existing.Status = item.Status;
                        existing.StatusMessage = item.StatusMessage;
                        existing.DetailProgressText = item.DetailProgressText;
                        existing.ProgressPercentage = item.ProgressPercentage;
                        existing.DownloadedChapters = item.DownloadedChapters;
                        existing.TotalChapters = item.TotalChapters > 0 ? item.TotalChapters : existing.TotalChapters;
                        existing.DownloadedPages = item.DownloadedPages;
                        existing.TotalPages = item.TotalPages;
                        existing.Subfolder = item.Subfolder;
                        existing.IsSubfolderLocked = item.IsSubfolderLocked;
                        existing.LocalDirectory = !string.IsNullOrWhiteSpace(item.LocalDirectory) ? item.LocalDirectory : existing.LocalDirectory;
                        if (item.Chapters != null && item.Chapters.Count > 0)
                        {
                            existing.Chapters = item.Chapters;
                        }
                        updatedCount++;
                    }
                }
            }
            finally
            {
                _isAutoSaveSuspended = false;
            }

            ReindexComicBooks();
            UpdateStats();
            StartScanMissing();
            TriggerThumbnailPrefetchIfNeeded();
            RequestQueueAutoSave(200);
            AddLog("SUCCESS", $"📂 Đã phục hồi thành công {addedCount} truyện mới (cập nhật tiến độ {updatedCount} truyện) từ: {sourceName}");
            ShowCenterToast("PHỤC HỒI MD THÀNH CÔNG", $"Đã nạp {addedCount} truyện mới, cập nhật tiến độ {updatedCount} truyện từ:\n{Path.GetFileName(sourceName)}", "📂", "PHỤC HỒI MD", 2800);
        }
        catch (Exception ex)
        {
            AddLog("ERROR", $"❌ Lỗi khi phục hồi hàng chờ: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task RetryAsync()
    {
        var errorItems = ComicBooks.Where(b => b.Status == "Error" || b.Status == "Stopped").ToList();
        if (errorItems.Count == 0)
        {
            AddLog("INFO", "Không có truyện lỗi nào cần thử lại.");
            return;
        }

        foreach (var item in errorItems)
        {
            item.IsChecked = true;
            item.Status = "Waiting";
            item.StatusMessage = "Chờ thử lại...";
        }

        AddLog("INFO", $"Thử lại {errorItems.Count} truyện...");
        await _downloadEngine.StartDownloadAsync(errorItems, ModeSelection);
        UpdateStats();
    }

    [RelayCommand]
    public void ClearQueue()
    {
        ComicBooks.Clear();
        ScanResults.Clear();
        UpdateStats();
        FlushAutoSaveImmediately();
        AddLog("INFO", "Đã xóa sạch hàng chờ tải.");
    }

    [RelayCommand]
    public void ClearCompleted()
    {
        var completedItems = ComicBooks
            .Where(b => b != null && (string.Equals(b.Status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(b.Status, "Hoàn tất", StringComparison.OrdinalIgnoreCase) ||
                                     b.ProgressPercentage >= 100.0))
            .ToList();

        if (completedItems.Count == 0)
        {
            AddLog("INFO", "Không có truyện nào đã tải xong trong danh sách hàng đợi.");
            return;
        }

        foreach (var item in completedItems)
        {
            DeleteItem(item);
        }

        ReindexComicBooks();
        UpdateStats();
        RequestQueueAutoSave(200);
        AddLog("SUCCESS", $"Đã tự động dọn dẹp {completedItems.Count} mục đã tải xong khỏi danh sách.");
    }

    public void CheckAndAutoDeleteCompleted()
    {
        if (!IsAutoDeleteCompleted) return;

        var completedItems = ComicBooks
            .Where(b => b != null && (string.Equals(b.Status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(b.Status, "Hoàn tất", StringComparison.OrdinalIgnoreCase) ||
                                     b.ProgressPercentage >= 100.0))
            .ToList();

        if (completedItems.Count == 0) return;

        foreach (var item in completedItems)
        {
            DeleteItem(item);
        }

        ReindexComicBooks();
        UpdateStats();
        RequestQueueAutoSave(200);
        AddLog("SUCCESS", $"[Tự Xóa] Đã tự động dọn dẹp {completedItems.Count} mục đã hoàn tất khỏi hàng đợi.");
    }

    // ==========================================
    // DATA GRID SELECTION & ADVANCED OPTIONS
    // ==========================================

    [RelayCommand]
    public void SelectAll()
    {
        foreach (var item in ComicBooks) item.IsChecked = true;
    }

    [RelayCommand]
    public void UnselectAll()
    {
        foreach (var item in ComicBooks) item.IsChecked = false;
    }

    [RelayCommand]
    public void InvertSelection()
    {
        foreach (var item in ComicBooks) item.IsChecked = !item.IsChecked;
    }

    [RelayCommand]
    public void CheckSingleBook(ComicBookItem? item)
    {
        var target = item ?? SelectedComic;
        if (target != null) target.IsChecked = true;
    }

    [RelayCommand]
    public void UncheckSingleBook(ComicBookItem? item)
    {
        var target = item ?? SelectedComic;
        if (target != null) target.IsChecked = false;
    }

    [RelayCommand]
    public void ToggleCheckSingleBook(ComicBookItem? item)
    {
        var target = item ?? SelectedComic;
        if (target != null) target.IsChecked = !target.IsChecked;
    }

    [RelayCommand]
    public void CheckSelectedRows()
    {
        if (SelectedComic != null) SelectedComic.IsChecked = true;
    }

    [RelayCommand]
    public void UncheckSelectedRows()
    {
        if (SelectedComic != null) SelectedComic.IsChecked = false;
    }

    [RelayCommand]
    public void CheckErrors()
    {
        foreach (var item in ComicBooks)
        {
            item.IsChecked = (item.Status == "Error" || item.Status == "Stopped");
        }
        AddLog("INFO", "Đã đánh dấu các dòng truyện bị lỗi.");
    }

    [RelayCommand]
    public void CheckDuplicates()
    {
        var grouped = ComicBooks.GroupBy(b => b.Title.Trim().ToLowerInvariant())
                                .Where(g => g.Count() > 1)
                                .SelectMany(g => g)
                                .ToList();

        foreach (var item in ComicBooks) item.IsDuplicate = false;
        foreach (var item in grouped) item.IsDuplicate = true;

        AddLog("INFO", $"Kiểm tra trùng: Phát hiện {grouped.Count} truyện trùng tên trong danh sách.");
    }

    [RelayCommand]
    public void MoveToTop(ComicBookItem? item)
    {
        var target = item ?? SelectedComic;
        if (target == null) return;

        int index = ComicBooks.IndexOf(target);
        if (index > 0)
        {
            ComicBooks.Move(index, 0);
            if (target.IsParallelSplitParent && !target.IsParallelSplitCollapsed && target.ParallelSplitChildren != null)
            {
                int insertPos = 1;
                foreach (var child in target.ParallelSplitChildren)
                {
                    int childIdx = ComicBooks.IndexOf(child);
                    if (childIdx >= 0 && childIdx != insertPos)
                    {
                        ComicBooks.Move(childIdx, Math.Min(insertPos, ComicBooks.Count - 1));
                    }
                    insertPos++;
                }
            }
            ReindexComicBooks();
            UpdateStats();
            _downloadEngine.NotifyQueueOrderChanged(target);
            RequestQueueAutoSave(200);
            ShowCenterToast("ĐƯA LÊN ĐẦU", $"Đã đưa \"{target.Title}\" lên đầu hàng đợi (ưu tiên tải trước).", "▲", "HÀNG ĐỢI", 1800);
        }
        else if (index == 0)
        {
            _downloadEngine.NotifyQueueOrderChanged(target);
        }
    }

    [RelayCommand]
    public void MoveToBottom(ComicBookItem? item)
    {
        var target = item ?? SelectedComic;
        if (target == null) return;

        int index = ComicBooks.IndexOf(target);
        if (index >= 0 && index < ComicBooks.Count - 1)
        {
            ComicBooks.Move(index, ComicBooks.Count - 1);
            if (target.IsParallelSplitParent && !target.IsParallelSplitCollapsed && target.ParallelSplitChildren != null)
            {
                foreach (var child in target.ParallelSplitChildren)
                {
                    int childIdx = ComicBooks.IndexOf(child);
                    if (childIdx >= 0)
                    {
                        ComicBooks.Move(childIdx, ComicBooks.Count - 1);
                    }
                }
            }
            ReindexComicBooks();
            UpdateStats();
            _downloadEngine.NotifyQueueOrderChanged(target);
            RequestQueueAutoSave(200);
            ShowCenterToast("ĐƯA XUỐNG CUỐI", $"Đã đưa \"{target.Title}\" xuống cuối hàng đợi.", "▼", "HÀNG ĐỢI", 1800);
        }
    }

    [RelayCommand]
    public void OpenInBrowser(ComicBookItem? item)
    {
        var target = item ?? SelectedComic;
        if (target == null || string.IsNullOrWhiteSpace(target.Url)) return;
        OpenUrlCrossPlatform(target.Url);
    }

    [RelayCommand]
    public async Task CopyBookLinkAsync(ComicBookItem? item)
    {
        var target = item ?? SelectedComic;
        if (target == null || string.IsNullOrWhiteSpace(target.Url)) return;
        try
        {
            var cb = GetClipboard();
            if (cb != null)
            {
                await cb.SetTextAsync(target.Url);
                ShowCenterToast("ĐÃ SAO CHÉP LINK", $"Đã sao chép link truyện vào Clipboard:\n{target.Url}", "🔗", "CLIPBOARD", 2200);
                AddLog("INFO", $"Đã sao chép link: {target.Url}");
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Lỗi sao chép: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task CopyNameAndLinkAsync(ComicBookItem? item)
    {
        var target = item ?? SelectedComic;
        if (target == null) return;
        try
        {
            var cb = GetClipboard();
            if (cb != null)
            {
                string text = $"{target.Title}\t{target.Url}";
                await cb.SetTextAsync(text);
                ShowCenterToast("ĐÃ SAO CHÉP TÊN & LINK", $"Đã sao chép Tên + Link truyện vào Clipboard:\n{target.Title} - {target.Url}", "📑", "CLIPBOARD", 2200);
                AddLog("INFO", $"Đã sao chép Tên & Link: {target.Title} - {target.Url}");
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Lỗi sao chép: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task CopyChaptersLinkAsync(ComicBookItem? item)
    {
        var target = item ?? SelectedComic;
        if (target == null) return;
        try
        {
            var cb = GetClipboard();
            if (cb != null)
            {
                var links = target.Chapters?.Select(c => c.Url).Where(u => !string.IsNullOrWhiteSpace(u)).ToList() ?? new List<string>();
                if (links.Count > 0)
                {
                    string text = string.Join(Environment.NewLine, links);
                    await cb.SetTextAsync(text);
                    ShowCenterToast("ĐÃ SAO CHÉP LINK CHAPTERS", $"Đã sao chép {links.Count} link chapter của \"{target.Title}\" vào Clipboard.", "📚", "CLIPBOARD", 2200);
                    AddLog("INFO", $"Đã sao chép {links.Count} link chapter của '{target.Title}'.");
                }
                else
                {
                    string text = target.Url ?? string.Empty;
                    await cb.SetTextAsync(text);
                    ShowCenterToast("ĐÃ SAO CHÉP LINK", $"Chưa tải danh sách chap, đã sao chép link truyện vào Clipboard:\n{text}", "🔗", "CLIPBOARD", 2200);
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Lỗi sao chép: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task CopySelectedLinksAsync()
    {
        var checkedItems = ComicBooks.Where(b => b.IsChecked && !string.IsNullOrWhiteSpace(b.Url)).ToList();
        if (checkedItems.Count == 0)
        {
            ShowCenterToast("CHƯA CHỌN TRUYỆN", "Vui lòng tích chọn ít nhất 1 truyện để sao chép link.", "⚠️", "THÔNG BÁO", 2000);
            return;
        }
        try
        {
            var cb = GetClipboard();
            if (cb != null)
            {
                string text = string.Join(Environment.NewLine, checkedItems.Select(b => b.Url));
                await cb.SetTextAsync(text);
                ShowCenterToast("ĐÃ SAO CHÉP TẤT CẢ LINK", $"Đã sao chép {checkedItems.Count} link truyện đã chọn vào Clipboard.", "📋", "CLIPBOARD", 2200);
                AddLog("INFO", $"Đã sao chép {checkedItems.Count} link truyện đã chọn vào Clipboard.");
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Lỗi sao chép: {ex.Message}");
        }
    }

    [RelayCommand]
    public void SplitChapters50(ComicBookItem? item) => SplitBookParallelChapters(item, 50);

    [RelayCommand]
    public void SplitChapters100(ComicBookItem? item) => SplitBookParallelChapters(item, 100);

    [RelayCommand]
    public void SplitChapters200(ComicBookItem? item) => SplitBookParallelChapters(item, 200);

    [RelayCommand]
    public void SplitChapters300(ComicBookItem? item) => SplitBookParallelChapters(item, 300);

    [RelayCommand]
    public void SplitChapters400(ComicBookItem? item) => SplitBookParallelChapters(item, 400);

    public void SplitBookParallelChapters(ComicBookItem? item, int bucketSize)
    {
        var book = item ?? SelectedComic;
        if (book == null) return;
        if (book.IsParallelSplitChild)
        {
            ShowCenterToast("KHÔNG THỂ TÁCH", "Đây là nhánh con đã được tách, không thể tiếp tục tách nhỏ.", "⚠️", "THÔNG BÁO", 2500);
            return;
        }

        int totalChaps = book.TotalChapters;
        if (totalChaps <= 0 && book.Chapters != null && book.Chapters.Count > 0)
        {
            totalChaps = book.Chapters.Count;
        }

        if (totalChaps <= 0)
        {
            ShowCenterToast("CHƯA CÓ CHAPTER", $"Truyện \"{book.Title}\" chưa có thông tin chapter để tách.", "⚠️", "THÔNG BÁO", 2500);
            return;
        }

        if (totalChaps <= bucketSize)
        {
            ShowCenterToast("SỐ CHAPTER NHỎ", $"Truyện có {totalChaps} chapter, không cần tách ({bucketSize} chap/nhánh).", "ℹ️", "THÔNG BÁO", 2500);
            return;
        }

        var ranges = BuildAutoSplitRanges(book.Chapters ?? new(), bucketSize);
        if (ranges.Count <= 1)
        {
            ShowCenterToast("SỐ CHAPTER NHỎ", $"Không thể chia nhỏ hơn nữa với kích thước {bucketSize}.", "ℹ️", "THÔNG BÁO", 2500);
            return;
        }

        int insertIndex = ComicBooks.IndexOf(book);
        if (insertIndex < 0) return;

        book.IsParallelSplitParent = true;
        book.IsParallelSplitCollapsed = false;
        book.IsChecked = false; // Bỏ tick truyện cha

        // Dọn nhánh cũ nếu có
        if (book.ParallelSplitChildren != null)
        {
            foreach (var oldChild in book.ParallelSplitChildren.ToList())
            {
                ComicBooks.Remove(oldChild);
            }
            book.ParallelSplitChildren.Clear();
        }

        var children = new List<ComicBookItem>();
        int addedCount = 0;

        foreach (var range in ranges)
        {
            var clone = new ComicBookItem
            {
                Title = book.Title,
                Url = book.Url,
                Domain = book.Domain,
                CoverUrl = book.CoverUrl,
                ThumbnailLocalPath = book.ThumbnailLocalPath,
                OriginalCoverLocalPath = book.OriginalCoverLocalPath,
                ThumbnailBitmap = book.ThumbnailBitmap,
                HighResPreviewBitmap = book.HighResPreviewBitmap,
                PreviewTagsText = book.PreviewTagsText,
                LatestChapter = book.LatestChapter,
                MissingChapters = book.MissingChapters,
                Subfolder = book.Subfolder,
                IsSubfolderLocked = book.IsSubfolderLocked,
                Status = "Waiting",
                DetailProgressText = $"Chờ tải chap {range}",
                ChapterSelectionText = range,
                IsChecked = true,
                LocalDirectory = book.LocalDirectory,
                IsParallelSplitChild = true,
                IsParallelSplitParent = false,
                ParallelSplitParentUrl = book.Url
            };

            var filter = ChapterRangeParser.Parse(range);
            if (filter != null && book.Chapters != null && book.Chapters.Count > 0)
            {
                clone.Chapters = book.Chapters.Where(c => filter.IsMatch(c.Title, c.ChapterNumber)).ToList();
                clone.TotalChapters = clone.Chapters.Count > 0 ? clone.Chapters.Count : bucketSize;
            }
            else
            {
                clone.Chapters = book.Chapters != null ? new List<ChapterItem>(book.Chapters) : new();
                clone.TotalChapters = bucketSize;
            }

            children.Add(clone);
            addedCount++;
            ComicBooks.Insert(insertIndex + addedCount, clone);
        }

        book.ParallelSplitChildren = children;
        book.NotifySplitHierarchyChanged();
        ReindexComicBooks();
        UpdateStats();
        _downloadEngine.NotifyQueueOrderChanged();
        RequestQueueAutoSave(200);

        ShowCenterToast(
            "TÁCH CHƯƠNG THÀNH CÔNG!",
            $"Đã tách truyện \"{book.Title}\" ({totalChaps} chaps) thành {ranges.Count} nhánh song song ({bucketSize} chap/nhánh).",
            "🧩",
            "TÁCH CHƯƠNG",
            3200);
        AddLog("SUCCESS", $"Đã tách truyện '{book.Title}' ({totalChaps} chaps) thành {ranges.Count} nhánh ({bucketSize} chap/nhánh).");
    }

    [RelayCommand]
    public void MoveUp(ComicBookItem? item)
    {
        var target = item ?? SelectedComic;
        if (target == null) return;

        int index = ComicBooks.IndexOf(target);
        if (index > 0)
        {
            ComicBooks.Move(index, index - 1);
            ReindexComicBooks();
            _downloadEngine.NotifyQueueOrderChanged(target);
            RequestQueueAutoSave(200);
        }
    }

    [RelayCommand]
    public void MoveDown(ComicBookItem? item)
    {
        var target = item ?? SelectedComic;
        if (target == null) return;

        int index = ComicBooks.IndexOf(target);
        if (index >= 0 && index < ComicBooks.Count - 1)
        {
            ComicBooks.Move(index, index + 1);
            ReindexComicBooks();
            _downloadEngine.NotifyQueueOrderChanged(target);
            RequestQueueAutoSave(200);
        }
    }

    [RelayCommand]
    public void ReverseQueue()
    {
        if (ComicBooks.Count <= 1)
        {
            AddLog("INFO", "Hàng chờ có ít hơn 2 truyện, không cần đảo ngược.");
            return;
        }

        // Nhóm các mục theo khối phân cấp (cha và các con của cha nếu có)
        var groups = new List<List<ComicBookItem>>();
        List<ComicBookItem>? currentGroup = null;

        for (int i = 0; i < ComicBooks.Count; i++)
        {
            var item = ComicBooks[i];
            if (item.IsParallelSplitChild && currentGroup != null)
            {
                currentGroup.Add(item);
            }
            else
            {
                currentGroup = new List<ComicBookItem> { item };
                groups.Add(currentGroup);
            }
        }

        // Đảo ngược thứ tự các nhóm
        groups.Reverse();

        var flatList = new List<ComicBookItem>();
        foreach (var group in groups)
        {
            flatList.AddRange(group);
        }

        ComicBooks.Clear();
        for (int i = 0; i < flatList.Count; i++)
        {
            flatList[i].Index = i + 1;
            ComicBooks.Add(flatList[i]);
        }

        ReindexComicBooks();
        _downloadEngine.NotifyQueueOrderChanged();
        UpdateStats();
        RequestQueueAutoSave(150);
        AddLog("SUCCESS", $"Đã đảo ngược toàn bộ thứ tự {ComicBooks.Count} truyện trong hàng chờ.");
    }

    [RelayCommand]
    public void DeleteSelected()
    {
        var toRemove = ComicBooks.Where(b => b.IsChecked).ToList();
        if (toRemove.Count == 0 && SelectedComic != null)
        {
            toRemove.Add(SelectedComic);
        }

        if (toRemove.Count == 0) return;

        // Tập hợp toàn bộ các mục cần xóa, bao gồm cascade xóa các task con nếu cha bị xóa
        var allToRemove = new HashSet<ComicBookItem>(toRemove);
        foreach (var b in toRemove)
        {
            if (b.IsParallelSplitParent && b.ParallelSplitChildren != null)
            {
                foreach (var child in b.ParallelSplitChildren)
                {
                    allToRemove.Add(child);
                }
            }
        }

        // Xóa khỏi danh sách ComicBooks
        foreach (var b in allToRemove)
        {
            ComicBooks.Remove(b);
        }

        // Rà soát lại các truyện cha còn lại xem có task con nào bị xóa lẻ tẻ không
        foreach (var parent in ComicBooks.Where(b => b.IsParallelSplitParent).ToList())
        {
            if (parent.ParallelSplitChildren != null)
            {
                parent.ParallelSplitChildren.RemoveAll(c => allToRemove.Contains(c));
                if (parent.ParallelSplitChildren.Count == 0)
                {
                    parent.IsParallelSplitParent = false;
                    parent.IsParallelSplitChild = false;
                    parent.IsParallelSplitCollapsed = false;
                    parent.ParallelSplitParentUrl = string.Empty;
                    parent.IsChecked = true;
                    parent.Status = "Waiting";
                    parent.ChapterSelectionText = string.Empty;
                    parent.DetailProgressText = "Sẵn sàng";
                }
                parent.NotifySplitHierarchyChanged();
            }
        }

        ReindexComicBooks();
        UpdateStats();
        RequestQueueAutoSave(150);
        AddLog("INFO", $"Đã xóa {allToRemove.Count} mục khỏi danh sách.");
    }

    [RelayCommand]
    public void DeleteItem(ComicBookItem? item)
    {
        var target = item ?? SelectedComic;
        if (target == null) return;

        if (target.IsParallelSplitParent)
        {
            // Xóa cascade truyện cha cùng tất cả các task con của nó
            if (target.ParallelSplitChildren != null)
            {
                foreach (var child in target.ParallelSplitChildren)
                {
                    ComicBooks.Remove(child);
                }
                target.ParallelSplitChildren.Clear();
            }

            // Dọn sạch bất kỳ task con nào còn sót trong ComicBooks cùng link/Url
            var leftoverChildren = ComicBooks.Where(b => b.IsParallelSplitChild && (b.Url == target.Url || b.ParallelSplitParentUrl == target.Url)).ToList();
            foreach (var c in leftoverChildren)
            {
                ComicBooks.Remove(c);
            }

            ComicBooks.Remove(target);
            AddLog("INFO", $"Đã xóa truyện gốc '{target.Title}' cùng toàn bộ các nhánh con đã tách.");
        }
        else if (target.IsParallelSplitChild)
        {
            // Tìm truyện cha tương ứng
            var parent = ComicBooks.FirstOrDefault(b => b.IsParallelSplitParent && (b.Url == target.Url || b.Url == target.ParallelSplitParentUrl));
            if (parent != null && parent.ParallelSplitChildren != null)
            {
                parent.ParallelSplitChildren.Remove(target);
                if (parent.ParallelSplitChildren.Count == 0)
                {
                    // Nếu nhánh con vừa xóa là Completed (tức là đã tải xong toàn bộ), dọn sạch luôn truyện cha
                    if (string.Equals(target.Status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(target.Status, "Hoàn tất", StringComparison.OrdinalIgnoreCase) ||
                        target.ProgressPercentage >= 100.0)
                    {
                        ComicBooks.Remove(parent);
                        AddLog("INFO", $"Đã hoàn tất toàn bộ các nhánh của truyện '{parent.Title}', tự động dọn sạch truyện gốc khỏi hàng đợi.");
                    }
                    else
                    {
                        // Nếu người dùng xóa dở dang khi chưa hoàn tất thì khôi phục truyện cha về bình thường
                        parent.IsParallelSplitParent = false;
                        parent.IsParallelSplitChild = false;
                        parent.IsParallelSplitCollapsed = false;
                        parent.ParallelSplitParentUrl = string.Empty;
                        parent.IsChecked = true;
                        parent.Status = "Waiting";
                        parent.ChapterSelectionText = string.Empty;
                        parent.DetailProgressText = "Sẵn sàng";
                        parent.NotifySplitHierarchyChanged();
                    }
                }
                else
                {
                    parent.NotifySplitHierarchyChanged();
                }
            }

            ComicBooks.Remove(target);
            AddLog("INFO", $"Đã xóa nhánh chương '{target.ChapterSelectionText}' của truyện '{target.Title}'.");
        }
        else
        {
            ComicBooks.Remove(target);
            AddLog("INFO", $"Đã xóa '{target.Title}' khỏi danh sách.");
        }

        ReindexComicBooks();
        UpdateStats();
        RequestQueueAutoSave(150);
    }

    [RelayCommand]
    public void OpenBookFolder(ComicBookItem? item)
    {
        var target = item ?? SelectedComic;
        string path = target != null && !string.IsNullOrEmpty(target.LocalDirectory)
            ? target.LocalDirectory
            : _downloadEngine.DownloadRoot;

        _downloadEngine.OpenDirectoryInExplorer(path);
        AddLog("INFO", $"Mở thư mục: {path}");
    }

    [RelayCommand]
    public async Task ReadBookInLibraryAsync(ComicBookItem? item)
    {
        var target = item ?? SelectedComic;
        SelectedRootTabIndex = 4;

        await LibraryVm.ScanLibraryAsync();

        if (target != null)
        {
            var matched = LibraryVm.AllComics.FirstOrDefault(c =>
                (!string.IsNullOrWhiteSpace(target.LocalDirectory) &&
                 string.Equals(c.RootFolderPath, target.LocalDirectory, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(c.Title, target.Title, StringComparison.OrdinalIgnoreCase));

            if (matched != null)
            {
                LibraryVm.SelectedComic = matched;
                await LibraryVm.ContinueReadingSelectedComicAsync(matched);
                return;
            }
        }
    }

    [RelayCommand]
    public void OpenDownloadRoot()
    {
        _downloadEngine.OpenDirectoryInExplorer(_downloadEngine.DownloadRoot);
    }

    [RelayCommand]
    public async Task ChangeDownloadFolderAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null)
            {
                var options = new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Chọn thư mục lưu truyện tải về",
                    AllowMultiple = false
                };

                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
                if (folders != null && folders.Count > 0)
                {
                    var selected = folders[0];
                    string? path = selected.TryGetLocalPath() ?? selected.Path?.LocalPath ?? selected.Path?.ToString();
                    string normalized = DownloadEngineService.NormalizeStoragePath(path, selected.Name);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        string writable = _downloadEngine.EnsureWritableDownloadRoot(normalized);
                        _downloadEngine.SetDownloadRoot(writable);
                        DownloadPathText = _downloadEngine.DownloadRoot;
                        LibraryVm.LibraryRootPath = _downloadEngine.DownloadRoot;
                        AddLog("SUCCESS", $"Đã chọn thư mục tải mới: {_downloadEngine.DownloadRoot}");
                        return;
                    }
                }
            }
            else
            {
                AddLog("WARN", "Không thể mở bộ chọn thư mục hệ thống (StorageProvider).");
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Lỗi khi chọn thư mục tải: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task CopyDownloadPathAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(_downloadEngine.DownloadRoot);
                AddLog("SUCCESS", $"Đã sao chép đường dẫn tải vào Clipboard: {_downloadEngine.DownloadRoot}");
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Lỗi sao chép: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task AutoUpdateAsync()
    {
        HasAvailableReleaseUpdate = false;
        if (IsUpdating)
        {
            AddLog("WARN", "Tiến trình cập nhật đang chạy, vui lòng đợi...");
            return;
        }

        string[] updateUrls;
        string platformName;

        if (OperatingSystem.IsAndroid())
        {
            platformName = "Android APK";
            updateUrls = new[]
            {
                "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/releases/com.CompanyName.ComicDownloaderGMTPC-Signed.apk",
                "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/latest/download/com.CompanyName.ComicDownloaderGMTPC-Signed.apk"
            };
        }
        else if (OperatingSystem.IsLinux())
        {
            platformName = "Linux Portable .tar.gz";
            updateUrls = new[]
            {
                "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/releases/ComicDownloaderGMTPC-linux-x64.tar.gz",
                "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/latest/download/ComicDownloaderGMTPC-linux-x64.tar.gz"
            };
        }
        else
        {
            platformName = "Windows Standalone EXE";
            updateUrls = new[]
            {
                "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/releases/ComicDownloaderGMTPC.Desktop.exe",
                "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/latest/download/ComicDownloaderGMTPC.Desktop.exe"
            };
        }

        IsUpdating = true;
        UpdateButtonLabel = "⏳ Đang kết nối...";

        try
        {
            AddLog("INFO", $"🚀 Bắt đầu tự động tải bản cập nhật mới nhất cho {platformName}...");

            bool success = await AppUpdateService.Instance.DownloadAndInstallUpdateAsync(
                updateUrls,
                (level, msg) => AddLog(level, msg),
                (percent) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        UpdateButtonLabel = $"⏳ {percent:F0}%";
                    });
                },
                channel: "release");

            if (!success)
            {
                AddLog("WARN", "[Cập nhật tự động] Không thể tải bản cập nhật Stable. Vui lòng kiểm tra lại kết nối mạng và thử lại sau.");
                UpdateButtonLabel = "🚀 UPDATE RELEASE";
            }
            else if (OperatingSystem.IsWindows())
            {
                UpdateButtonLabel = "🔄 Đang khởi động lại...";
            }
            else
            {
                UpdateButtonLabel = "🚀 UPDATE RELEASE";
            }
        }
        catch (Exception ex)
        {
            AddLog("ERROR", $"Lỗi cập nhật tự động: {ex.Message}");
            UpdateButtonLabel = "🚀 UPDATE RELEASE";
        }
        finally
        {
            IsUpdating = false;
        }
    }

    [RelayCommand]
    public async Task CheckUpdateReleaseAsync()
    {
        if (IsUpdating)
        {
            AddLog("WARN", "Tiến trình cập nhật đang chạy, vui lòng đợi...");
            return;
        }

        string[] updateUrls = OperatingSystem.IsAndroid()
            ? new[] { "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/releases/com.CompanyName.ComicDownloaderGMTPC-Signed.apk" }
            : (OperatingSystem.IsLinux()
                ? new[] { "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/releases/ComicDownloaderGMTPC-linux-x64.tar.gz" }
                : new[] { "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/releases/ComicDownloaderGMTPC.Desktop.exe" });

        UpdateButtonLabel = "⏳ Kiểm tra...";
        try
        {
            AddLog("INFO", "🔍 [Release] Đang kết nối máy chủ GitHub kiểm tra mã hash bản cập nhật...");
            var check = await AppUpdateService.Instance.CheckUpdateAsync(updateUrls, "release");
            UpdateButtonLabel = "🚀 UPDATE RELEASE";

            if (check.Success)
            {
                if (check.HasUpdate)
                {
                    HasAvailableReleaseUpdate = true;
                    HasAvailableUpdateInToast = true;
                    ToastUpdateButtonText = "🚀 CẬP NHẬT RELEASE NGAY";
                    ToastUpdateTarget = "release";
                    AddLog("SUCCESS", $"🚀 [Release] {check.Message}");
                    ShowCenterToast("CẬP NHẬT RELEASE", check.Message, "🚀", "UPDATE RELEASE", 0);
                }
                else
                {
                    HasAvailableReleaseUpdate = false;
                    AddLog("INFO", $"🚀 [Release] {check.Message}");
                    ShowCenterToast("ĐÃ MỚI NHẤT", check.Message, "✅", "UPDATE RELEASE", 3200);
                }
            }
            else
            {
                HasAvailableReleaseUpdate = false;
                AddLog("WARN", $"🚀 [Release] {check.Message}");
                ShowCenterToast("KIỂM TRA THẤT BẠI", check.Message, "⚠️", "UPDATE RELEASE", 3500);
            }
        }
        catch (Exception ex)
        {
            HasAvailableReleaseUpdate = false;
            UpdateButtonLabel = "🚀 UPDATE RELEASE";
            AddLog("ERROR", $"Lỗi kiểm tra Release: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task CheckUpdateBetaAsync()
    {
        if (IsUpdating)
        {
            AddLog("WARN", "Tiến trình cập nhật đang chạy, vui lòng đợi...");
            return;
        }

        string[] updateUrls = OperatingSystem.IsAndroid()
            ? new[] { "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/beta/com.CompanyName.ComicDownloaderGMTPC-Signed.apk" }
            : (OperatingSystem.IsLinux()
                ? new[] { "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/beta/ComicDownloaderGMTPC-linux-x64.tar.gz" }
                : new[] { "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/beta/ComicDownloaderGMTPC.Desktop.exe" });

        UpdateBetaButtonLabel = "⏳ Kiểm tra...";
        try
        {
            AddLog("INFO", "🔍 [Beta] Đang kết nối máy chủ GitHub kiểm tra mã hash bản cập nhật...");
            var check = await AppUpdateService.Instance.CheckUpdateAsync(updateUrls, "beta");
            UpdateBetaButtonLabel = "🧪 UPDATE BETA";

            if (check.Success)
            {
                if (check.HasUpdate)
                {
                    HasAvailableBetaUpdate = true;
                    HasAvailableUpdateInToast = true;
                    ToastUpdateButtonText = "⚡ CẬP NHẬT BETA NGAY";
                    ToastUpdateTarget = "beta";
                    AddLog("SUCCESS", $"🧪 [Beta] {check.Message}");
                    ShowCenterToast("CẬP NHẬT BETA", check.Message, "🧪", "UPDATE BETA", 0);
                }
                else
                {
                    HasAvailableBetaUpdate = false;
                    AddLog("INFO", $"🧪 [Beta] {check.Message}");
                    ShowCenterToast("ĐÃ MỚI NHẤT", check.Message, "✅", "UPDATE BETA", 3200);
                }
            }
            else
            {
                HasAvailableBetaUpdate = false;
                AddLog("WARN", $"🧪 [Beta] {check.Message}");
                ShowCenterToast("KIỂM TRA THẤT BẠI", check.Message, "⚠️", "UPDATE BETA", 3500);
            }
        }
        catch (Exception ex)
        {
            HasAvailableBetaUpdate = false;
            UpdateBetaButtonLabel = "🧪 UPDATE BETA";
            AddLog("ERROR", $"Lỗi kiểm tra Beta: {ex.Message}");
        }
    }

    public async Task CheckAutoUpdateOnStartupAsync()
    {
        try
        {
            string[] betaUrls = OperatingSystem.IsAndroid()
                ? new[] { "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/beta/com.CompanyName.ComicDownloaderGMTPC-Signed.apk" }
                : (OperatingSystem.IsLinux()
                    ? new[] { "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/beta/ComicDownloaderGMTPC-linux-x64.tar.gz" }
                    : new[] { "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/beta/ComicDownloaderGMTPC.Desktop.exe" });

            var checkBeta = await AppUpdateService.Instance.CheckUpdateAsync(betaUrls, "beta");
            if (checkBeta.Success && checkBeta.HasUpdate)
            {
                HasAvailableBetaUpdate = true;
                HasAvailableUpdateInToast = true;
                ToastUpdateButtonText = "⚡ CẬP NHẬT BETA NGAY";
                ToastUpdateTarget = "beta";
                AddLog("INFO", $"🔔 [Tự động kiểm tra] Phát hiện bản cập nhật Beta mới: {checkBeta.Message}");
                ShowCenterToast("BẢN CẬP NHẬT BETA MỚI", checkBeta.Message, "🧪", "UPDATE BETA", 0);
                return;
            }

            string[] releaseUrls = OperatingSystem.IsAndroid()
                ? new[] { "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/releases/com.CompanyName.ComicDownloaderGMTPC-Signed.apk" }
                : (OperatingSystem.IsLinux()
                    ? new[] { "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/releases/ComicDownloaderGMTPC-linux-x64.tar.gz" }
                    : new[] { "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/releases/ComicDownloaderGMTPC.Desktop.exe" });

            var checkRelease = await AppUpdateService.Instance.CheckUpdateAsync(releaseUrls, "release");
            if (checkRelease.Success && checkRelease.HasUpdate)
            {
                HasAvailableReleaseUpdate = true;
                HasAvailableUpdateInToast = true;
                ToastUpdateButtonText = "🚀 CẬP NHẬT RELEASE NGAY";
                ToastUpdateTarget = "release";
                AddLog("INFO", $"🔔 [Tự động kiểm tra] Phát hiện bản cập nhật Release mới: {checkRelease.Message}");
                ShowCenterToast("BẢN CẬP NHẬT RELEASE MỚI", checkRelease.Message, "🚀", "UPDATE RELEASE", 0);
            }
        }
        catch { }
    }

    [RelayCommand]
    public async Task AutoUpdateBetaAsync()
    {
        HasAvailableBetaUpdate = false;
        if (IsUpdating)
        {
            AddLog("WARN", "Tiến trình cập nhật đang chạy, vui lòng đợi...");
            return;
        }

        string[] updateUrls;
        string platformName;

        if (OperatingSystem.IsAndroid())
        {
            platformName = "Android APK (Beta)";
            updateUrls = new[]
            {
                "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/beta/com.CompanyName.ComicDownloaderGMTPC-Signed.apk"
            };
        }
        else if (OperatingSystem.IsLinux())
        {
            platformName = "Linux Portable .tar.gz (Beta)";
            updateUrls = new[]
            {
                "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/beta/ComicDownloaderGMTPC.tar.gz",
                "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/beta/ComicDownloaderGMTPC-linux-x64.tar.gz"
            };
        }
        else
        {
            platformName = "Windows Standalone EXE (Beta)";
            updateUrls = new[]
            {
                "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/beta/ComicDownloaderGMTPC.Desktop.exe"
            };
        }

        IsUpdating = true;
        UpdateBetaButtonLabel = "⏳ Đang kết nối...";

        try
        {
            AddLog("INFO", $"🧪 Bắt đầu tự động tải bản cập nhật BETA cho {platformName}...");

            bool success = await AppUpdateService.Instance.DownloadAndInstallUpdateAsync(
                updateUrls,
                (level, msg) => AddLog(level, msg),
                (percent) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        UpdateBetaButtonLabel = $"⏳ {percent:F0}%";
                    });
                },
                channel: "beta");

            if (!success)
            {
                AddLog("WARN", "[Cập nhật Beta] Không thể tải bản cập nhật Beta. Vui lòng kiểm tra lại kết nối mạng và thử lại sau.");
                UpdateBetaButtonLabel = "🧪 UPDATE BETA";
            }
            else if (OperatingSystem.IsWindows())
            {
                UpdateBetaButtonLabel = "🔄 Đang khởi động lại...";
            }
            else
            {
                UpdateBetaButtonLabel = "🧪 UPDATE BETA";
            }
        }
        catch (Exception ex)
        {
            AddLog("ERROR", $"Lỗi cập nhật Beta: {ex.Message}");
            UpdateBetaButtonLabel = "🧪 UPDATE BETA";
        }
        finally
        {
            IsUpdating = false;
        }
    }

    private Avalonia.Controls.TopLevel? GetTopLevel()
    {
        if (ComicDownloaderGMTPC.Views.MainView.Instance != null)
        {
            var tl = Avalonia.Controls.TopLevel.GetTopLevel(ComicDownloaderGMTPC.Views.MainView.Instance);
            if (tl != null) return tl;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView)
        {
            return Avalonia.Controls.TopLevel.GetTopLevel(singleView.MainView);
        }
        return null;
    }

    private Avalonia.Input.Platform.IClipboard? GetClipboard()
    {
        return GetTopLevel()?.Clipboard;
    }

    // ==========================================
    // SCAN MISSING INTEGER CHAPTERS
    // ==========================================

    [RelayCommand]
    public void StartScanMissing()
    {
        if (ComicBooks.Count == 0)
        {
            return;
        }

        ScanResults.Clear();
        AddLog("INFO", $"Bắt đầu quét chap số nguyên thiếu với {ParallelCheckCount} tác vụ song song...");

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();

        foreach (var book in ComicBooks)
        {
            var result = _scannerService.ScanChapters(book);
            ScanResults.Add(result);
            book.MissingChapters = result.MissingIntegerChapters;
        }

        AddLog("SUCCESS", $"Đã quét xong chap thiếu cho {ScanResults.Count} truyện.");
    }

    [RelayCommand]
    public void ClearLogs()
    {
        Logs.Clear();
    }

    private void ReindexComicBooks()
    {
        for (int i = 0; i < ComicBooks.Count; i++)
        {
            ComicBooks[i].Index = i + 1;
            if (ComicBooks[i].OriginalIndex <= 0)
            {
                ComicBooks[i].OriginalIndex = i + 1;
            }
        }
    }

    private async Task ExtractUrlsFromTextAsync(string? rawText, bool clearExisting)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            AddLog("WARN", "Vui lòng dán ít nhất 1 đường link truyện!");
            return;
        }

        var rawTokens = rawText.Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(l => l.Trim())
                               .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"))
                               .Distinct()
                               .ToList();

        var candidateUrls = rawTokens.Where(t => t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                                 t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)).ToList();

        if (candidateUrls.Count == 0 && rawTokens.Count > 0)
        {
            candidateUrls = rawTokens;
        }

        if (clearExisting)
        {
            ComicBooks.Clear();
            ScanResults.Clear();
        }

        var existingUrls = new HashSet<string>(ComicBooks.Select(b => b.Url.Trim()), StringComparer.OrdinalIgnoreCase);
        var targetUrls = candidateUrls.Where(u => !existingUrls.Contains(u)).Distinct().ToList();

        if (targetUrls.Count == 0)
        {
            AddLog("INFO", "Tất cả link truyện vừa dán đã có sẵn trong danh sách (hoặc không tìm thấy link mới).");
            return;
        }

        int cpuThreads = Math.Clamp(LinkCpuThreads, 1, MaxLinkCpuThreads);
        _scraperService.MaxCpuThreads = cpuThreads;
        AddLog("INFO", $"Đang trích xuất thông tin cho {targetUrls.Count} link truyện mới (Số luồng CPU: {cpuThreads})...");

        bool hasMangadex = targetUrls.Any(l => DomainRoutingService.DetectDomain(l).Contains("mangadex"));
        string mangadexLang = "vi";
        bool mangadexFallback = true;

        if (hasMangadex)
        {
            AddLog("INFO", "Phát hiện liên kết MangaDex. Vui lòng chọn ngôn ngữ tải (Tiếng Việt / Tiếng Anh)...");
            var choice = await PromptMangadexLanguageAsync();
            if (choice == null)
            {
                AddLog("WARN", "Đã hủy thao tác lấy link MangaDex theo yêu cầu.");
                return;
            }
            mangadexLang = choice.PrimaryLanguage;
            mangadexFallback = choice.UseFallback;
            AddLog("INFO", $"Đã xác nhận ngôn ngữ MangaDex: {(mangadexLang == "vi" ? "Tiếng Việt" : "Tiếng Anh")} (Fallback: {(mangadexFallback ? "Bật" : "Tắt")})");
        }

        int startIndex = ComicBooks.Count + 1;
        var scrapedBooks = new ComicBookItem?[targetUrls.Count];

        if (targetUrls.Count <= 1 || cpuThreads <= 1)
        {
            for (int i = 0; i < targetUrls.Count; i++)
            {
                string url = targetUrls[i];
                int currentIndex = startIndex + i;
                var book = await Task.Run(() => _scraperService.ScrapeBookAsync(url, currentIndex, mangadexLang, mangadexFallback)).ConfigureAwait(true);
                scrapedBooks[i] = book;
            }
        }
        else
        {
            using var semaphore = new SemaphoreSlim(cpuThreads, cpuThreads);
            var tasks = new List<Task>(targetUrls.Count);
            for (int i = 0; i < targetUrls.Count; i++)
            {
                int localIdx = i;
                string url = targetUrls[localIdx];
                int currentIndex = startIndex + localIdx;
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var book = await _scraperService.ScrapeBookAsync(url, currentIndex, mangadexLang, mangadexFallback).ConfigureAwait(false);
                        scrapedBooks[localIdx] = book;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }
            await Task.WhenAll(tasks).ConfigureAwait(true);
        }

        string trimmedSubfolder = SubfolderInput?.Trim() ?? string.Empty;
        for (int i = 0; i < targetUrls.Count; i++)
        {
            var book = scrapedBooks[i];
            if (book == null) continue;
            int currentIndex = startIndex + i;
            if (!string.IsNullOrWhiteSpace(trimmedSubfolder))
            {
                book.Subfolder = trimmedSubfolder;
            }
            ComicBooks.Add(book);
            AddLog("INFO", $"[{currentIndex}] Đã nạp: {book.Title} ({book.TotalChapters} chaps) - Domain: {book.Domain}{(string.IsNullOrWhiteSpace(book.Subfolder) ? "" : $" [Subfolder: {book.Subfolder}]")}");
        }

        UpdateStats();
        StartScanMissing();
        TriggerThumbnailPrefetchIfNeeded();

        if (IsAutoDownload)
        {
            AddLog("INFO", "Tự động kích hoạt tải xuống theo thiết lập 'Tự động tải'.");
            if (_downloadEngine.IsDownloading)
            {
                _ = DownloadNewAsync();
            }
            else
            {
                _ = DownloadAllAsync();
            }
        }
    }

    [ObservableProperty]
    private bool _isSubfolderMoving = false;

    [ObservableProperty]
    private double _subfolderMoveProgress = 0.0;

    [ObservableProperty]
    private string _subfolderMoveProgressText = "0%";

    [ObservableProperty]
    private string _subfolderMoveStatusText = string.Empty;

    private string GetExpectedBookDirectory(ComicBookItem book, string subfolder)
    {
        string effectiveRoot = _downloadEngine.EnsureWritableDownloadRoot(_downloadEngine.DownloadRoot);
        string serverFolder = DomainRoutingService.GetServerFolderName(book.Domain, book.Url);
        string rawSub = string.IsNullOrWhiteSpace(subfolder) ? "[default]" : subfolder.Trim();
        string safeSub = DownloadEngineService.MakeSafeFilename(rawSub, 50);
        if (string.IsNullOrWhiteSpace(safeSub)) safeSub = "[default]";
        string safeBookName = DownloadEngineService.MakeSafeFilename(book.Title, 80);
        return Path.Combine(effectiveRoot, serverFolder, safeSub, safeBookName);
    }

    private string FindExistingBookDirectory(ComicBookItem book)
    {
        // 1. Kiểm tra LocalDirectory hiện tại nếu có và thư mục thực sự tồn tại
        if (!string.IsNullOrWhiteSpace(book.LocalDirectory) && Directory.Exists(book.LocalDirectory))
        {
            return book.LocalDirectory;
        }

        string effectiveRoot = _downloadEngine.EnsureWritableDownloadRoot(_downloadEngine.DownloadRoot);
        string serverFolder = DomainRoutingService.GetServerFolderName(book.Domain, book.Url);
        string serverDirPath = Path.Combine(effectiveRoot, serverFolder);
        string safeBookName = DownloadEngineService.MakeSafeFilename(book.Title, 80);

        if (!Directory.Exists(serverDirPath))
        {
            return string.Empty;
        }

        // 2. Kiểm tra thư mục dự kiến với Subfolder hiện tại
        string currentExpected = GetExpectedBookDirectory(book, book.Subfolder);
        if (Directory.Exists(currentExpected))
        {
            return currentExpected;
        }

        // 3. Kiểm tra trong thư mục [default]
        string defaultBookDir = Path.Combine(serverDirPath, "[default]", safeBookName);
        if (Directory.Exists(defaultBookDir))
        {
            return defaultBookDir;
        }

        // 4. Quét tất cả các subfolder con trong serverDirPath xem có subfolder nào chứa safeBookName
        try
        {
            foreach (var subDir in Directory.GetDirectories(serverDirPath))
            {
                string candidate = Path.Combine(subDir, safeBookName);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch { }

        return string.Empty;
    }

    private void MoveDirectoryContent(string sourceDir, string destDir, Action<int, int, string>? progress = null)
    {
        try
        {
            if (!Directory.Exists(sourceDir)) return;
            if (string.Equals(Path.GetFullPath(sourceDir).TrimEnd('\\', '/'), Path.GetFullPath(destDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            var allFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            int totalFiles = allFiles.Length;
            int movedCount = 0;

            if (totalFiles == 0)
            {
                progress?.Invoke(0, 0, "Không có tệp");
            }

            // Di chuyển tất cả file với try-catch trên từng file (đề phòng file đang được worker tải ghi dở)
            foreach (var file in allFiles)
            {
                try
                {
                    string relPath = Path.GetRelativePath(sourceDir, file);
                    string targetFilePath = Path.Combine(destDir, relPath);
                    string? targetSubDir = Path.GetDirectoryName(targetFilePath);
                    if (!string.IsNullOrEmpty(targetSubDir) && !Directory.Exists(targetSubDir))
                    {
                        Directory.CreateDirectory(targetSubDir);
                    }

                    if (File.Exists(targetFilePath))
                    {
                        File.Delete(targetFilePath);
                    }
                    File.Move(file, targetFilePath);
                }
                catch
                {
                    // Nếu file đang bận ghi, bỏ qua để tiến trình tải hiện tại tiếp tục
                }

                movedCount++;
                progress?.Invoke(movedCount, totalFiles, Path.GetFileName(file));
            }

            // Xóa thư mục con rỗng trong sourceDir
            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    {
                        Directory.Delete(dir, false);
                    }
                }
                catch { }
            }

            // Xóa sourceDir nếu đã rỗng
            if (Directory.Exists(sourceDir) && Directory.GetFileSystemEntries(sourceDir).Length == 0)
            {
                Directory.Delete(sourceDir, false);
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Lỗi khi di chuyển dữ liệu từ '{sourceDir}' sang '{destDir}': {ex.Message}");
        }
    }

    public async Task ApplyBookSubfolderAndMoveAsync(ComicBookItem book, string newSubfolder)
    {
        string rawNew = string.IsNullOrWhiteSpace(newSubfolder) ? "" : newSubfolder.Trim();
        string newTargetDir = GetExpectedBookDirectory(book, rawNew);
        string currentDir = FindExistingBookDirectory(book);

        // Cập nhật Subfolder và LocalDirectory ngay lập tức để worker tải truyện nhận diện ngay
        book.Subfolder = rawNew;
        book.LocalDirectory = newTargetDir;
        book.IsSubfolderLocked = true;

        if (book.ParallelSplitChildren != null)
        {
            foreach (var child in book.ParallelSplitChildren)
            {
                child.Subfolder = rawNew;
                child.LocalDirectory = newTargetDir;
                child.IsSubfolderLocked = true;
            }
        }

        // Nếu thư mục cũ đã tồn tại và khác thư mục mới thì tạm dừng tải, move toàn bộ dữ liệu đã tải sang thư mục mới, sau đó tiếp tục tải
        if (!string.IsNullOrWhiteSpace(currentDir) && Directory.Exists(currentDir) &&
            !string.Equals(Path.GetFullPath(currentDir).TrimEnd('\\', '/'), Path.GetFullPath(newTargetDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            bool wasDownloading = _downloadEngine.IsDownloading && !_downloadEngine.IsPaused;
            if (wasDownloading)
            {
                _downloadEngine.Pause();
                await Task.Delay(500).ConfigureAwait(false);
            }

            string prevStatus = book.Status;
            double prevProgress = book.ProgressPercentage;
            string prevDetail = book.DetailProgressText;
            string prevStatusMsg = book.StatusMessage;

            IsSubfolderMoving = true;
            SubfolderMoveProgress = 0;
            SubfolderMoveProgressText = "0%";
            SubfolderMoveStatusText = $"Đang dời '{book.Title}' sang '{(!string.IsNullOrWhiteSpace(rawNew) ? rawNew : "[default]")}'...";

            book.Status = "Moving";
            book.StatusMessage = $"Đang chuyển sang '{(!string.IsNullOrWhiteSpace(rawNew) ? rawNew : "[default]")}'...";
            book.DetailProgressText = "Đang dời file... 0%";
            book.ProgressPercentage = 0;

            try
            {
                AddLog("INFO", $"📦 Đang chuyển dữ liệu '{book.Title}' sang Subfolder mới: '{(!string.IsNullOrWhiteSpace(rawNew) ? rawNew : "[default]")}'...");

                await Task.Run(() =>
                {
                    MoveDirectoryContent(currentDir, newTargetDir, (current, total, fileName) =>
                    {
                        double pct = total > 0 ? (double)current / total * 100.0 : 100.0;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            SubfolderMoveProgress = pct;
                            SubfolderMoveProgressText = $"{pct:F0}% ({current}/{total})";
                            SubfolderMoveStatusText = $"[{book.Title}] Đang dời '{fileName}' ({current}/{total})";

                            book.ProgressPercentage = Math.Round(pct, 1);
                            book.DetailProgressText = $"📦 Dời {current}/{total} file • {pct:F0}%";
                            book.StatusMessage = $"Đang chuyển '{fileName}' ({current}/{total})";
                        });

                        BackgroundExecutionService.Instance.ReportProgress("move_folder", "Chuyển Thư Mục", $"[{book.Title}] {current}/{total} ({pct:F0}%)", pct, true);
                    });
                }).ConfigureAwait(false);

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    book.LocalDirectory = newTargetDir;
                    if (prevStatus == "Completed" || prevStatus == "Hoàn tất" || (book.TotalChapters > 0 && book.DownloadedChapters >= book.TotalChapters))
                    {
                        book.ProgressPercentage = 100.0;
                        book.DetailProgressText = "Hoàn tất";
                        book.Status = "Completed";
                    }
                    else
                    {
                        book.RestoreDownloadProgress();
                        if (book.ProgressPercentage == 0 && prevProgress > 0)
                        {
                            book.ProgressPercentage = prevProgress;
                            book.DetailProgressText = prevDetail;
                        }
                        book.StatusMessage = prevStatusMsg;
                        book.Status = (prevStatus == "Downloading" || prevStatus == "Moving") ? (wasDownloading ? "Downloading" : "Waiting") : prevStatus;
                    }
                });

                AddLog("SUCCESS", $"✅ Đã chuyển xong thư mục: {newTargetDir}");
            }
            finally
            {
                IsSubfolderMoving = false;
                BackgroundExecutionService.Instance.CompleteTask("move_folder", "Chuyển Thư Mục", "Đã dời xong thư mục");
                if (wasDownloading)
                {
                    _downloadEngine.Resume();
                }
            }
        }
        else
        {
            book.DetailProgressText = $"🔒 Đã khóa Subfolder '{book.DisplaySubfolder}'";
        }
    }

    [RelayCommand]
    public void ApplySubfolderToNew()
    {
        string targetSubfolder = SubfolderInput?.Trim() ?? string.Empty;
        string display = string.IsNullOrWhiteSpace(targetSubfolder) ? "[default]" : targetSubfolder;
        AddLog("INFO", $"📁 Đã thiết lập Subfolder mặc định cho các truyện mới thêm vào sau: '{display}' (Các truyện hiện có trong hàng đợi không bị thay đổi).");
    }

    [RelayCommand]
    public void ApplySubfolderToSelected()
    {
        ApplySubfolderToNew();
    }

    [RelayCommand]
    public async Task ApplySubfolderToAllAsync()
    {
        string targetSubfolder = SubfolderInput?.Trim() ?? string.Empty;
        var allBooks = ComicBooks.Where(b => !b.IsParallelSplitChild).ToList();

        if (allBooks.Count == 0)
        {
            AddLog("WARN", "Danh sách hàng đợi trống, không có truyện nào để áp dụng.");
            return;
        }

        bool wasDownloading = _downloadEngine.IsDownloading && !_downloadEngine.IsPaused;
        if (wasDownloading)
        {
            _downloadEngine.Pause();
            await Task.Delay(500).ConfigureAwait(false);
        }

        IsSubfolderMoving = true;
        SubfolderMoveProgress = 0;
        SubfolderMoveProgressText = "0%";
        SubfolderMoveStatusText = $"Bắt đầu dời toàn bộ danh sách sang Subfolder '{(!string.IsNullOrWhiteSpace(targetSubfolder) ? targetSubfolder : "[default]")}'...";

        try
        {
            int count = 0;
            int totalBooks = allBooks.Count;

            for (int bIdx = 0; bIdx < totalBooks; bIdx++)
            {
                var b = allBooks[bIdx];
                string rawNew = targetSubfolder;
                string newTargetDir = GetExpectedBookDirectory(b, rawNew);
                string currentDir = FindExistingBookDirectory(b);

                b.Subfolder = rawNew;
                b.LocalDirectory = newTargetDir;
                b.IsSubfolderLocked = true;

                if (b.ParallelSplitChildren != null)
                {
                    foreach (var child in b.ParallelSplitChildren)
                    {
                        child.Subfolder = rawNew;
                        child.LocalDirectory = newTargetDir;
                        child.IsSubfolderLocked = true;
                    }
                }

                if (!string.IsNullOrWhiteSpace(currentDir) && Directory.Exists(currentDir) &&
                    !string.Equals(Path.GetFullPath(currentDir).TrimEnd('\\', '/'), Path.GetFullPath(newTargetDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    string prevStatus = b.Status;
                    double prevProgress = b.ProgressPercentage;
                    string prevDetail = b.DetailProgressText;
                    string prevStatusMsg = b.StatusMessage;

                    b.Status = "Moving";
                    b.StatusMessage = $"Đang dời sang '{(!string.IsNullOrWhiteSpace(rawNew) ? rawNew : "[default]")}'...";
                    b.DetailProgressText = "Đang dời file... 0%";
                    b.ProgressPercentage = 0;

                    AddLog("INFO", $"📦 Đang chuyển dữ liệu ({bIdx + 1}/{totalBooks}) '{b.Title}' sang Subfolder mới: '{(!string.IsNullOrWhiteSpace(rawNew) ? rawNew : "[default]")}'...");

                    await Task.Run(() =>
                    {
                        MoveDirectoryContent(currentDir, newTargetDir, (current, total, fileName) =>
                        {
                            double filePct = total > 0 ? (double)current / total * 100.0 : 100.0;
                            double bookOverallPct = ((double)bIdx + (filePct / 100.0)) / totalBooks * 100.0;

                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                SubfolderMoveProgress = bookOverallPct;
                                SubfolderMoveProgressText = $"{bookOverallPct:F0}% ({bIdx + 1}/{totalBooks})";
                                SubfolderMoveStatusText = $"[{b.Title}] Đang dời '{fileName}' ({current}/{total})";

                                b.ProgressPercentage = Math.Round(filePct, 1);
                                b.DetailProgressText = $"📦 Dời {current}/{total} file • {filePct:F0}%";
                                b.StatusMessage = $"Đang chuyển '{fileName}' ({current}/{total})";
                            });

                            BackgroundExecutionService.Instance.ReportProgress("move_folder", "Chuyển Thư Mục", $"[{bIdx + 1}/{totalBooks}] {b.Title} ({bookOverallPct:F0}%)", bookOverallPct, true);
                        });
                    }).ConfigureAwait(false);

                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        b.LocalDirectory = newTargetDir;
                        if (prevStatus == "Completed" || prevStatus == "Hoàn tất" || (b.TotalChapters > 0 && b.DownloadedChapters >= b.TotalChapters))
                        {
                            b.ProgressPercentage = 100.0;
                            b.DetailProgressText = "Hoàn tất";
                            b.Status = "Completed";
                        }
                        else
                        {
                            b.RestoreDownloadProgress();
                            if (b.ProgressPercentage == 0 && prevProgress > 0)
                            {
                                b.ProgressPercentage = prevProgress;
                                b.DetailProgressText = prevDetail;
                            }
                            b.StatusMessage = prevStatusMsg;
                            b.Status = (prevStatus == "Downloading" || prevStatus == "Moving") ? (wasDownloading ? "Downloading" : "Waiting") : prevStatus;
                        }
                    });

                    AddLog("SUCCESS", $"✅ Đã chuyển xong thư mục ({bIdx + 1}/{totalBooks}): {newTargetDir}");
                }
                count++;
            }

            IsAllSubfoldersLocked = true;
            string display = string.IsNullOrWhiteSpace(targetSubfolder) ? "[default]" : targetSubfolder;
            AddLog("SUCCESS", $"🔒 Đã áp dụng Subfolder '{display}', tự động chuyển thư mục & KHÓA TẤT CẢ {count} truyện trong hàng đợi.");
        }
        finally
        {
            IsSubfolderMoving = false;
            BackgroundExecutionService.Instance.CompleteTask("move_folder", "Chuyển Thư Mục", "Đã dời xong thư mục");
            if (wasDownloading)
            {
                _downloadEngine.Resume();
            }
        }
    }

    [RelayCommand]
    public async Task ApplySingleBookSubfolderAsync(ComicBookItem? item)
    {
        if (item == null) return;
        await ApplyBookSubfolderAndMoveAsync(item, item.Subfolder);
        string display = string.IsNullOrWhiteSpace(item.Subfolder) ? "[default]" : item.Subfolder;
        AddLog("SUCCESS", $"🔒 Đã áp dụng Subfolder '{display}' và khóa cố định cho '{item.Title}'.");
    }

    // ==========================================
    // TAB SEARCH METHODS & COMMANDS
    // ==========================================

    private void InitializeSearchDomains()
    {
        SearchDomains.Clear();
        SearchDomains.Add(new SearchDomainItem { DisplayName = "truyenqq", Tag = "truyenqq", Host = "truyenqqko.com", IsChecked = true, Category = "Manga" });
        SearchDomains.Add(new SearchDomainItem { DisplayName = "nettruyen", Tag = "nettruyen", Host = "nettruyenviet10.com", IsChecked = false, Category = "Manga" });
        SearchDomains.Add(new SearchDomainItem { DisplayName = "nettruyen.tech", Tag = "nettruyen.tech", Host = "nettruyen.tech", IsChecked = false, Category = "Manga" });
        SearchDomains.Add(new SearchDomainItem { DisplayName = "thuviensach.vn", Tag = "thuviensach.vn", Host = "thuviensach.vn", IsChecked = false, Category = "Novel" });
        SearchDomains.Add(new SearchDomainItem { DisplayName = "doctruyen.us", Tag = "doctruyen.us", Host = "doctruyen.us", IsChecked = false, Category = "Novel" });
        SearchDomains.Add(new SearchDomainItem { DisplayName = "loppytoonn.com", Tag = "loppytoonn.com", Host = "loppytoonn.com", IsChecked = false, Category = "Manga" });
        SearchDomains.Add(new SearchDomainItem { DisplayName = "mangadex", Tag = "mangadex", Host = "mangadex.org", IsChecked = false, Category = "Manga" });
        SearchDomains.Add(new SearchDomainItem { DisplayName = "daomeoden", Tag = "daomeoden", Host = "daomeoden.net", IsChecked = false, Category = "Manga" });
        SearchDomains.Add(new SearchDomainItem { DisplayName = "moetruyen", Tag = "moetruyen", Host = "moetruyen.net", IsChecked = false, Category = "Manga" });
        SearchDomains.Add(new SearchDomainItem { DisplayName = "damconuong.shop", Tag = "damconuong.shop", Host = "damconuong.shop", IsChecked = false, Category = "Hentai" });
        SearchDomains.Add(new SearchDomainItem { DisplayName = "hentaivn", Tag = "hentaivn", Host = "vi-hentai.pro", IsChecked = false, Category = "Hentai" });
        SearchDomains.Add(new SearchDomainItem { DisplayName = "sayhentai", Tag = "sayhentai", Host = "sayhentai.cx", IsChecked = false, Category = "Hentai" });
        SearchDomains.Add(new SearchDomainItem { DisplayName = "hentaiforce", Tag = "hentaiforce", Host = "hentaiforce.net", IsChecked = false, Category = "Hentai" });
        SearchDomains.Add(new SearchDomainItem { DisplayName = "nhentai.net", Tag = "nhentai.net", Host = "nhentai.net", IsChecked = false, Category = "Hentai" });
        SearchDomains.Add(new SearchDomainItem { DisplayName = "nhentai", Tag = "nhentai", Host = "nhentai.net", IsChecked = false, Category = "Hentai" });
        SearchDomains.Add(new SearchDomainItem { DisplayName = "hentai2read", Tag = "hentai2read", Host = "hentai2read.com", IsChecked = false, Category = "Hentai" });
        SearchDomains.Add(new SearchDomainItem { DisplayName = "hentaiera", Tag = "hentaiera", Host = "hentaiera.com", IsChecked = false, Category = "Hentai" });
        SearchDomains.Add(new SearchDomainItem { DisplayName = "hako", Tag = "hako", Host = "ln.hako.vn", IsChecked = false, Category = "Novel" });
    }

    [RelayCommand]
    public void ClearSearchInput()
    {
        SearchBookNamesInput = string.Empty;
        SearchStatusMessage = "Đã xóa trắng danh sách tên truyện.";
    }

    [RelayCommand]
    public async Task PasteToSearchInputAsync()
    {
        try
        {
            var clipboard = GetClipboard();
            if (clipboard == null) return;
            string? text = await clipboard.TryGetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (string.IsNullOrWhiteSpace(SearchBookNamesInput))
                {
                    SearchBookNamesInput = text.Trim();
                }
                else
                {
                    SearchBookNamesInput = SearchBookNamesInput.TrimEnd() + Environment.NewLine + text.Trim();
                }
                SearchStatusMessage = $"📋 Đã dán tên truyện từ Clipboard ({SearchBookCount} truyện).";
            }
        }
        catch { }
    }

    [RelayCommand]
    public void SelectAllSearchDomains()
    {
        foreach (var d in SearchDomains) d.IsChecked = true;
    }

    [RelayCommand]
    public void UnselectAllSearchDomains()
    {
        foreach (var d in SearchDomains) d.IsChecked = false;
    }

    [RelayCommand]
    public void SelectCategorySearchDomains(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return;
        foreach (var d in SearchDomains)
        {
            d.IsChecked = string.Equals(d.Category, category, StringComparison.OrdinalIgnoreCase);
        }
    }

    [RelayCommand]
    public void SendTitleToSearch(ComicBookItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Title)) return;
        string title = item.Title.Trim();

        if (string.IsNullOrWhiteSpace(SearchBookNamesInput))
        {
            SearchBookNamesInput = title;
        }
        else
        {
            var existing = SearchBookNamesInput.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToList();
            if (!existing.Contains(title, StringComparer.OrdinalIgnoreCase))
            {
                SearchBookNamesInput = SearchBookNamesInput.TrimEnd() + Environment.NewLine + title;
            }
        }

        SearchStatusMessage = $"🔍 Đã thêm '{title}' vào Tab Tìm Kiếm.";
        AddLog("INFO", $"🔍 [Tab Search] Đã đưa tên truyện vào tìm kiếm: '{title}'");

        // Giữ nguyên tab hiện tại và hiển thị thông báo trung tâm màn hình
        ShowCenterToast(
            "ĐÃ THÊM VÀO TÌM TRUYỆN!",
            $"Đã đưa \"{title}\" vào Tab Search.\nSau khi thêm các truyện mong muốn xong, hãy chuyển sang TAB SEARCH để bắt đầu tìm truyện.",
            "🔍",
            "TÌM TRUYỆN",
            3500);
    }

    [RelayCommand]
    public async Task ExecuteBatchSearchAsync()
    {
        string rawInput = SearchBookNamesInput?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            SearchStatusMessage = "⚠️ Vui lòng nhập ít nhất 1 tên truyện vào danh sách.";
            return;
        }

        var bookNames = rawInput.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (bookNames.Count == 0)
        {
            SearchStatusMessage = "⚠️ Vui lòng nhập ít nhất 1 tên truyện vào danh sách.";
            return;
        }

        var selectedDomains = SearchDomains.Where(d => d.IsChecked).ToList();
        if (selectedDomains.Count == 0)
        {
            SearchStatusMessage = "⚠️ Vui lòng tick chọn ít nhất một domain nguồn để tìm kiếm.";
            return;
        }

        string engine = SelectedSearchEngine ?? "Google";
        bool openNewWindow = IsSearchOpenNewWindow;
        int openedCount = 0;
        bool isFirst = true;
        string? browserExe = null;
        string newWindowArg = "--new-window";

        if (openNewWindow && OperatingSystem.IsWindows())
        {
            browserExe = GetWindowsBrowserPath(out newWindowArg);
        }

        try
        {
            SearchStatusMessage = $"⏳ Đang mở tìm kiếm cho {bookNames.Count} truyện trên {selectedDomains.Count} domain...";

            foreach (string book in bookNames)
            {
                foreach (var domain in selectedDomains)
                {
                    string targetDomain = domain.Host;
                    string query = System.Net.WebUtility.UrlEncode($"site:{targetDomain} {book}");
                    string searchUrl = engine switch
                    {
                        "Bing" => $"https://www.bing.com/search?q={query}",
                        "DuckDuckGo" => $"https://duckduckgo.com/?q={query}",
                        "Yahoo" => $"https://search.yahoo.com/search?p={query}",
                        "Yandex" => $"https://yandex.com/search/?text={query}",
                        _ => $"https://www.google.com/search?q={query}"
                    };

                    if (openNewWindow && !string.IsNullOrEmpty(browserExe) && OperatingSystem.IsWindows())
                    {
                        try
                        {
                            if (isFirst)
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = browserExe,
                                    Arguments = $"{newWindowArg} \"{searchUrl}\"",
                                    UseShellExecute = false
                                });
                                isFirst = false;
                            }
                            else
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = browserExe,
                                    Arguments = $"\"{searchUrl}\"",
                                    UseShellExecute = false
                                });
                            }
                        }
                        catch
                        {
                            await LaunchWebUrlAsync(searchUrl);
                        }
                    }
                    else
                    {
                        await LaunchWebUrlAsync(searchUrl);
                    }

                    openedCount++;
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                SearchStatusMessage = $"✅ Đã mở {engine} Search cho {openedCount} lượt tìm kiếm ({bookNames.Count} truyện × {selectedDomains.Count} domain).";
                AddLog("SUCCESS", SearchStatusMessage);
            });
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                SearchStatusMessage = $"❌ Lỗi khi mở tìm kiếm: {ex.Message}";
                AddLog("WARN", SearchStatusMessage);
            });
        }
    }

    private static void OpenUrlCrossPlatform(string url)
    {
        _ = LaunchWebUrlAsync(url);
    }

    private string? GetWindowsBrowserPath(out string newWindowArg)
    {
        newWindowArg = "--new-window";
        try
        {
            if (!OperatingSystem.IsWindows()) return null;

            string[] candidates = {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Mozilla Firefox\firefox.exe",
                @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe"
            };

            foreach (var path in candidates)
            {
                if (System.IO.File.Exists(path))
                {
                    if (path.Contains("firefox", StringComparison.OrdinalIgnoreCase))
                    {
                        newWindowArg = "-new-window";
                    }
                    return path;
                }
            }
        }
        catch { }
        return null;
    }

    [RelayCommand]
    public void ToggleLockSubfolder(ComicBookItem? item)
    {
        if (item == null) return;
        item.IsSubfolderLocked = !item.IsSubfolderLocked;
        if (item.ParallelSplitChildren != null)
        {
            foreach (var child in item.ParallelSplitChildren)
            {
                child.IsSubfolderLocked = item.IsSubfolderLocked;
            }
        }
        AddLog("INFO", item.IsSubfolderLocked 
            ? $"🔒 Đã khóa Subfolder '{item.DisplaySubfolder}' cho '{item.Title}'." 
            : $"🔓 Đã mở khóa Subfolder cho '{item.Title}'.");
    }

    [RelayCommand]
    public void ToggleLockAllSubfolders()
    {
        IsAllSubfoldersLocked = !IsAllSubfoldersLocked;
        foreach (var book in ComicBooks)
        {
            book.IsSubfolderLocked = IsAllSubfoldersLocked;
            if (book.ParallelSplitChildren != null)
            {
                foreach (var child in book.ParallelSplitChildren)
                {
                    child.IsSubfolderLocked = IsAllSubfoldersLocked;
                }
            }
        }
        AddLog("INFO", IsAllSubfoldersLocked ? "🔒 Đã KHÓA toàn bộ Subfolder trong hàng đợi." : "🔓 Đã MỞ KHÓA toàn bộ Subfolder trong hàng đợi.");
    }



    private void UpdateStats()
    {
        TotalBooks = ComicBooks.Count;
        DownloadingCount = ComicBooks.Count(b => b.Status == "Downloading");
        CompletedCount = ComicBooks.Count(b => b.Status == "Completed");
        ErrorCount = ComicBooks.Count(b => b.Status == "Error");
        RecalculateDuplicates();
    }

    private void OnLogEmitted(string level, string message)
    {
        AddLog(level, message);
    }

    private void AddLog(string level, string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Logs.Insert(0, new LogMessageItem
            {
                Level = level,
                Message = message
            });

            while (Logs.Count > 300)
            {
                Logs.RemoveAt(Logs.Count - 1);
            }
        });
    }

    [RelayCommand]
    public async Task BrowseManualSplitFolderAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null)
            {
                var options = new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = _langService.CurrentLanguage == "VI"
                        ? "Chọn thư mục chứa ảnh dài để cắt"
                        : "Select folder containing long images to split",
                    AllowMultiple = false
                };

                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
                if (folders != null && folders.Count > 0)
                {
                    var selected = folders[0];
                    string? path = selected.TryGetLocalPath() ?? selected.Path?.LocalPath ?? selected.Path?.ToString();
                    string normalized = DownloadEngineService.NormalizeStoragePath(path, selected.Name);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        ManualSplitFolderPath = normalized;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Lỗi chọn thư mục cắt ảnh: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task StartManualSplitAsync()
    {
        if (string.IsNullOrWhiteSpace(ManualSplitFolderPath) || !Directory.Exists(ManualSplitFolderPath))
        {
            AddLog("WARN", "[Cắt ảnh dài] Vui lòng chọn một thư mục hợp lệ!");
            return;
        }

        IsManualSplitting = true;
        ManualSplitProgress = 0;
        ManualSplitProgressText = "0.0% (0 / 0)";
        ManualSplitCountText = "0 " + (_langService.CurrentLanguage == "VI" ? "ảnh" : "images");
        ManualSplitErrorCountText = "0";
        ManualSplitCurrentFileText = _langService.CurrentLanguage == "VI" ? "Đang quét thư mục..." : "Scanning folder...";
        ManualSplitLogs.Clear();
        ManualSplitLogs.Add($"[{DateTime.Now:HH:mm:ss}] Bắt đầu quét thư mục: {ManualSplitFolderPath}");

        _manualSplitCts = new CancellationTokenSource();
        BackgroundExecutionService.Instance.ReportProgress("image_splitter", "Cắt Ảnh Dài", "Bắt đầu quét thư mục ảnh dài...", 0, true);

        var progress = new Progress<ImageSplitterService.SplitProgressInfo>(info =>
        {
            if (info.TotalFiles > 0)
            {
                double pct = Math.Min(100.0, (double)info.ProcessedFiles / info.TotalFiles * 100.0);
                ManualSplitProgress = pct;
                string unit = _langService.CurrentLanguage == "VI" ? "ảnh" : "images";
                ManualSplitProgressText = $"{pct:0.0}% ({info.ProcessedFiles} / {info.TotalFiles} {unit})";
                ManualSplitCountText = $"{info.SplitCount} {unit}";
                ManualSplitErrorCountText = info.ErrorCount.ToString();
                BackgroundExecutionService.Instance.ReportProgress("image_splitter", "Cắt Ảnh Dài", $"{info.ProcessedFiles}/{info.TotalFiles} ảnh ({pct:0.0}%)", pct, true);
            }
            if (!string.IsNullOrEmpty(info.CurrentFile))
            {
                ManualSplitCurrentFileText = info.CurrentFile;
            }
            if (!string.IsNullOrEmpty(info.LogMessage))
            {
                ManualSplitLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {info.LogMessage}");
                while (ManualSplitLogs.Count > 300)
                {
                    ManualSplitLogs.RemoveAt(ManualSplitLogs.Count - 1);
                }
            }
        });

        try
        {
            var summary = await ImageSplitterService.Instance.ProcessFolderAsync(
                ManualSplitFolderPath,
                ManualSplitHeight,
                ManualSplitQuality,
                ManualSplitThreads,
                progress,
                _manualSplitCts.Token);

            if (summary.IsCancelled)
            {
                ManualSplitLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [Đã hủy] Tiến trình cắt ảnh đã dừng theo yêu cầu.");
                ManualSplitCurrentFileText = _langService.CurrentLanguage == "VI" ? "Đã dừng." : "Stopped.";
            }
            else
            {
                ManualSplitLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [Hoàn tất] Đã quét {summary.ProcessedFiles}/{summary.TotalFiles} ảnh. Cắt thành công: {summary.SplitCount}. Lỗi: {summary.ErrorCount}.");
                ManualSplitCurrentFileText = _langService.CurrentLanguage == "VI" ? "Hoàn tất." : "Completed.";
            }
        }
        catch (Exception ex)
        {
            ManualSplitLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [Lỗi] {ex.Message}");
            ManualSplitCurrentFileText = "Lỗi: " + ex.Message;
        }
        finally
        {
            IsManualSplitting = false;
            BackgroundExecutionService.Instance.CompleteTask("image_splitter", "Cắt Ảnh Dài", "Hoàn tất cắt ảnh dài");
        }
    }

    [RelayCommand]
    public void StopManualSplit()
    {
        if (_manualSplitCts != null && !_manualSplitCts.IsCancellationRequested)
        {
            _manualSplitCts.Cancel();
            ManualSplitLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] Đang gửi yêu cầu dừng tiến trình...");
        }
    }

    [RelayCommand]
    public void OpenManualSplitFolder()
    {
        if (!string.IsNullOrEmpty(ManualSplitFolderPath) && Directory.Exists(ManualSplitFolderPath))
        {
            _downloadEngine.OpenDirectoryInExplorer(ManualSplitFolderPath);
        }
        else
        {
            AddLog("WARN", "[Cắt ảnh dài] Thư mục không tồn tại hoặc chưa được chọn!");
        }
    }

    [RelayCommand]
    public void ClearManualSplitLogs()
    {
        ManualSplitLogs.Clear();
    }

    // ==========================================
    // IMAGE ENHANCEMENT COMMANDS & METHODS
    // ==========================================

    private void TriggerLivePreviewDebounced()
    {
        if (string.IsNullOrEmpty(_enhanceSampleImagePath) || !File.Exists(_enhanceSampleImagePath))
            return;

        _previewDebounceTimer?.Dispose();
        _previewDebounceTimer = new System.Threading.Timer(_ =>
        {
            UpdatePreviewResult();
        }, null, 120, Timeout.Infinite);
    }

    private void UpdatePreviewResult()
    {
        if (string.IsNullOrEmpty(_enhanceSampleImagePath) || !File.Exists(_enhanceSampleImagePath))
            return;

        try
        {
            var options = new ImageEnhancerOptions
            {
                Contrast = EnhanceContrast,
                Brightness = EnhanceBrightness,
                Saturation = EnhanceSaturation,
                Sharpness = EnhanceSharpness,
                NoiseReduce = EnhanceNoiseReduce,
                Quality = EnhanceQuality,
                OutputFormat = EnhanceOutputFormat
            };

            var stats = _imageEnhancer.GeneratePreviewStats(_enhanceSampleImagePath, options, maxDimension: 0);
            if (stats?.PreviewBytes != null && stats.PreviewBytes.Length > 0)
            {
                byte[] rawBytes = stats.PreviewBytes;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        using var ms = new MemoryStream(rawBytes);
                        EnhancePreviewResult = new Bitmap(ms);
                        EnhanceBeforeInfoText = $"Before: {stats.OrigWidth} x {stats.OrigHeight}, {stats.OriginalSizeBytes / 1024.0:F1} KB";
                        string sign = stats.SizeDeltaPercent >= 0 ? "+" : "";
                        EnhanceAfterInfoText = $"After: {stats.OrigWidth} x {stats.OrigHeight}, {stats.ProcessedSizeBytes / 1024.0:F1} KB ({sign}{stats.SizeDeltaPercent:F1}%)";
                        EnhanceClippingInfoText = stats.ClippingInfo;
                    }
                    catch
                    {
                    }
                });
            }
        }
        catch
        {
            // Bỏ qua lỗi xem trước tạm thời
        }
    }

    private void LoadSamplePreview(string imagePath)
    {
        try
        {
            if (!File.Exists(imagePath)) return;
            _enhanceSampleImagePath = imagePath;

            byte[] bytes = File.ReadAllBytes(imagePath);
            using var ms = new MemoryStream(bytes);
            var originalBmp = new Bitmap(ms);
            long origSize = bytes.Length;

            EnhanceImagePixelWidth = originalBmp.PixelSize.Width;
            EnhanceImagePixelHeight = originalBmp.PixelSize.Height;
            UpdateSplitOrientation();
            EnhancePreviewOriginal = originalBmp;
            EnhancePreviewInfoText = $"{Path.GetFileName(imagePath)} ({originalBmp.PixelSize.Width}x{originalBmp.PixelSize.Height})";
            EnhanceBeforeInfoText = $"Before: {originalBmp.PixelSize.Width} x {originalBmp.PixelSize.Height}, {origSize / 1024.0:F1} KB";

            ZoomFitPreview();
            UpdatePreviewResult();
        }
        catch (Exception ex)
        {
            EnhancePreviewInfoText = $"Không thể tải xem trước: {ex.Message}";
        }
    }

    private List<string> _enhanceFolderFiles = new();
    private int _enhanceCurrentFileIndex = -1;

    public void LoadFolderImages(string folderPath, string? selectedFile = null)
    {
        try
        {
            _enhanceFolderFiles = _imageEnhancer.GetAllImagesInFolder(folderPath);
            if (_enhanceFolderFiles.Count > 0)
            {
                if (!string.IsNullOrEmpty(selectedFile))
                {
                    _enhanceCurrentFileIndex = _enhanceFolderFiles.IndexOf(selectedFile);
                    if (_enhanceCurrentFileIndex < 0) _enhanceCurrentFileIndex = 0;
                }
                else
                {
                    _enhanceCurrentFileIndex = 0;
                }

                LoadSamplePreview(_enhanceFolderFiles[_enhanceCurrentFileIndex]);
            }
            else
            {
                if (!string.IsNullOrEmpty(selectedFile) && File.Exists(selectedFile))
                {
                    _enhanceFolderFiles = new List<string> { selectedFile };
                    _enhanceCurrentFileIndex = 0;
                    LoadSamplePreview(selectedFile);
                }
                else
                {
                    _enhanceCurrentFileIndex = -1;
                    EnhancePreviewOriginal = null;
                    EnhancePreviewResult = null;
                    EnhancePreviewInfoText = "Cây thư mục không có ảnh hợp lệ.";
                }
            }

            UpdateNavigationState();
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Lỗi tải danh sách ảnh: {ex.Message}");
        }
    }

    private void UpdateNavigationState()
    {
        CanGoPrevious = _enhanceCurrentFileIndex > 0;
        CanGoNext = _enhanceCurrentFileIndex < _enhanceFolderFiles.Count - 1;
        if (_enhanceCurrentFileIndex >= 0 && _enhanceCurrentFileIndex < _enhanceFolderFiles.Count)
        {
            EnhanceImageCounterText = $"{_enhanceCurrentFileIndex + 1} / {_enhanceFolderFiles.Count}";
            EnhanceImageFileNameText = Path.GetFileName(_enhanceFolderFiles[_enhanceCurrentFileIndex]);
            EnhanceImageIndexText = $"[{EnhanceImageCounterText}] {EnhanceImageFileNameText}";
        }
        else
        {
            EnhanceImageCounterText = "0 / 0";
            EnhanceImageFileNameText = "Chưa có ảnh";
            EnhanceImageIndexText = "Chưa có ảnh";
        }
    }

    [RelayCommand]
    public void PreviousImage()
    {
        if (_enhanceFolderFiles.Count == 0 || _enhanceCurrentFileIndex <= 0) return;
        _enhanceCurrentFileIndex--;
        LoadSamplePreview(_enhanceFolderFiles[_enhanceCurrentFileIndex]);
        UpdateNavigationState();
    }

    [RelayCommand]
    public void NextImage()
    {
        if (_enhanceFolderFiles.Count == 0 || _enhanceCurrentFileIndex >= _enhanceFolderFiles.Count - 1) return;
        _enhanceCurrentFileIndex++;
        LoadSamplePreview(_enhanceFolderFiles[_enhanceCurrentFileIndex]);
        UpdateNavigationState();
    }

    [RelayCommand]
    public async Task BrowseEnhanceFolderAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null)
            {
                var options = new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = _langService.CurrentLanguage == "VI"
                        ? "Chọn thư mục nguồn chứa ảnh để xử lý nâng cao"
                        : "Select input folder containing images to enhance",
                    AllowMultiple = false
                };

                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
                if (folders != null && folders.Count > 0)
                {
                    var selected = folders[0];
                    string? path = selected.TryGetLocalPath() ?? selected.Path?.LocalPath ?? selected.Path?.ToString();
                    string normalized = DownloadEngineService.NormalizeStoragePath(path, selected.Name);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        EnhanceFolderPath = normalized;

                        // Tự động gợi ý Output folder ở cùng cấp với thư mục nguồn
                        if (string.IsNullOrWhiteSpace(EnhanceOutputFolderPath))
                        {
                            EnhanceOutputFolderPath = GetSiblingFolder(normalized, "enhanced");
                        }

                        if (Directory.Exists(normalized))
                        {
                            LoadFolderImages(normalized);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Lỗi chọn thư mục nguồn xử lý ảnh: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task BrowseEnhanceOutputFolderAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null)
            {
                var options = new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = _langService.CurrentLanguage == "VI"
                        ? "Chọn thư mục đích để lưu ảnh sau khi xử lý"
                        : "Select output folder to save enhanced images",
                    AllowMultiple = false
                };

                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
                if (folders != null && folders.Count > 0)
                {
                    var selected = folders[0];
                    string? path = selected.TryGetLocalPath() ?? selected.Path?.LocalPath ?? selected.Path?.ToString();
                    string normalized = DownloadEngineService.NormalizeStoragePath(path, selected.Name);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        EnhanceOutputFolderPath = normalized;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Lỗi chọn thư mục đích: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task PickSampleImageAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null)
            {
                var options = new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = _langService.CurrentLanguage == "VI"
                        ? "Chọn ảnh làm mẫu xem trước Before / After"
                        : "Pick sample image for Before / After preview",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("Images")
                        {
                            Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp" }
                        }
                    }
                };

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);
                if (files != null && files.Count > 0)
                {
                    var file = files[0];
                    string? rawPath = file.TryGetLocalPath() ?? file.Path?.LocalPath ?? file.Path?.ToString();
                    string normalized = DownloadEngineService.NormalizeStoragePath(rawPath, file.Name);
                    if (!string.IsNullOrEmpty(normalized) && File.Exists(normalized))
                    {
                        string dir = Path.GetDirectoryName(normalized) ?? string.Empty;
                        if (string.IsNullOrEmpty(EnhanceFolderPath))
                        {
                            EnhanceFolderPath = dir;
                        }
                        LoadFolderImages(dir, normalized);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Lỗi chọn ảnh mẫu xem trước: {ex.Message}");
        }
    }

    [RelayCommand]
    public void OpenEnhanceFolder()
    {
        if (string.IsNullOrEmpty(EnhanceFolderPath) || !Directory.Exists(EnhanceFolderPath))
        {
            AddLog("WARN", "[Xử lý ảnh] Thư mục nguồn không tồn tại hoặc chưa được chọn!");
            return;
        }

        _downloadEngine.OpenDirectoryInExplorer(EnhanceFolderPath);
    }

    [RelayCommand]
    public void OpenEnhanceOutputFolder()
    {
        string target = EnhanceOverwriteOriginal ? EnhanceFolderPath : EnhanceOutputFolderPath;
        if (string.IsNullOrEmpty(target))
        {
            AddLog("WARN", "[Xử lý ảnh] Thư mục đích chưa được chọn!");
            return;
        }

        if (!Directory.Exists(target))
        {
            try { Directory.CreateDirectory(target); } catch { }
        }

        _downloadEngine.OpenDirectoryInExplorer(target);
    }

    [RelayCommand]
    public void ZoomInPreview()
    {
        EnhancePreviewZoom = Math.Min(5.0, EnhancePreviewZoom + 0.25);
    }

    [RelayCommand]
    public void ZoomOutPreview()
    {
        EnhancePreviewZoom = Math.Max(0.25, EnhancePreviewZoom - 0.25);
    }

    [RelayCommand]
    public void ResetZoomPreview()
    {
        EnhancePreviewZoom = 1.0;
    }

    [RelayCommand]
    public void ZoomFitPreview()
    {
        if (EnhanceImagePixelHeight > 0)
        {
            double availableH = IsPortraitMode ? (EnhanceLiveFrameHeight / 2.0) - 36.0 : EnhanceLiveFrameHeight - 36.0;
            if (availableH > 50)
            {
                double fitZoom = Math.Round(Math.Clamp(availableH / EnhanceImagePixelHeight, 0.1, 2.0), 2);
                EnhancePreviewZoom = Math.Max(0.15, fitZoom);
                return;
            }

            double h = EnhanceImagePixelHeight;
            if (h > 4000) EnhancePreviewZoom = 0.25;
            else if (h > 2500) EnhancePreviewZoom = 0.35;
            else if (h > 1800) EnhancePreviewZoom = 0.5;
            else if (h > 1200) EnhancePreviewZoom = 0.65;
            else if (h > 800) EnhancePreviewZoom = 0.85;
            else EnhancePreviewZoom = 1.0;
        }
        else
        {
            EnhancePreviewZoom = 0.75;
        }
    }

    [RelayCommand]
    public void SwapDualOrder()
    {
        IsSwappedDualOrder = !IsSwappedDualOrder;
    }

    [RelayCommand]
    public void ToggleImmersiveDualFocus()
    {
        IsImmersiveDualFocus = !IsImmersiveDualFocus;
    }

    [RelayCommand]
    public void SetViewModeDual() => EnhanceViewMode = "Dual";

    [RelayCommand]
    public void SetViewModeSingle() => EnhanceViewMode = "Dual";

    [RelayCommand]
    public void SetViewModeSplit() => EnhanceViewMode = "Dual";

    [RelayCommand]
    public void ToggleCompareMode()
    {
        SwapDualOrder();
    }

    [RelayCommand]
    public void ToggleSplitOrientation()
    {
        if (SplitOrientationMode == "Auto")
        {
            SplitOrientationMode = IsSplitVerticalOrientation ? "Horizontal" : "Vertical";
        }
        else if (SplitOrientationMode == "Horizontal")
        {
            SplitOrientationMode = "Vertical";
        }
        else
        {
            SplitOrientationMode = "Auto";
        }
    }

    [RelayCommand]
    public void SetSplitHorizontal() => SplitOrientationMode = "Horizontal";

    [RelayCommand]
    public void SetSplitVertical() => SplitOrientationMode = "Vertical";

    [RelayCommand]
    public void SetSplitAuto() => SplitOrientationMode = "Auto";

    [RelayCommand]
    public void ToggleLoupe()
    {
        IsLoupeEnabled = !IsLoupeEnabled;
    }

    [RelayCommand]
    public void ApplyPresetDefault()
    {
        EnhanceContrast = 0f;
        EnhanceBrightness = 0f;
        EnhanceSaturation = 100f;
        EnhanceSharpness = 0f;
        EnhanceNoiseReduce = 0;
        EnhanceLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [Preset] Mặc định (Gốc)");
    }

    [RelayCommand]
    public void ApplyPresetOldScan()
    {
        EnhanceContrast = 25f;
        EnhanceBrightness = 10f;
        EnhanceSaturation = 100f;
        EnhanceSharpness = 1.0f;
        EnhanceNoiseReduce = 1;
        EnhanceLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [Preset] Khử ố vàng giấy scan");
    }

    [RelayCommand]
    public void ApplyPresetWebtoon()
    {
        EnhanceContrast = 10f;
        EnhanceBrightness = 0f;
        EnhanceSaturation = 125f;
        EnhanceSharpness = 1.0f;
        EnhanceNoiseReduce = 0;
        EnhanceLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [Preset] Webtoon rực rỡ");
    }

    [RelayCommand]
    public void ApplyPresetNight()
    {
        EnhanceContrast = 12f;
        EnhanceBrightness = -15f;
        EnhanceSaturation = 90f;
        EnhanceSharpness = 0.5f;
        EnhanceNoiseReduce = 0;
        EnhanceLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [Preset] Đọc đêm dịu mắt");
    }

    [RelayCommand]
    public void SaveUserPreset1()
    {
        UserPreset1Contrast = EnhanceContrast;
        UserPreset1Brightness = EnhanceBrightness;
        UserPreset1Saturation = EnhanceSaturation;
        UserPreset1Sharpness = EnhanceSharpness;
        UserPreset1NoiseReduce = EnhanceNoiseReduce;
        EnhanceLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [Preset] Đã lưu thông số vào '{UserPreset1Name}'");
        SaveUserPresetsToDisk();
    }

    [RelayCommand]
    public void LoadUserPreset1()
    {
        EnhanceContrast = UserPreset1Contrast;
        EnhanceBrightness = UserPreset1Brightness;
        EnhanceSaturation = UserPreset1Saturation;
        EnhanceSharpness = UserPreset1Sharpness;
        EnhanceNoiseReduce = UserPreset1NoiseReduce;
        EnhanceLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [Preset] Đã tải thông số từ '{UserPreset1Name}'");
    }

    [RelayCommand]
    public void SaveUserPreset2()
    {
        UserPreset2Contrast = EnhanceContrast;
        UserPreset2Brightness = EnhanceBrightness;
        UserPreset2Saturation = EnhanceSaturation;
        UserPreset2Sharpness = EnhanceSharpness;
        UserPreset2NoiseReduce = EnhanceNoiseReduce;
        EnhanceLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [Preset] Đã lưu thông số vào '{UserPreset2Name}'");
        SaveUserPresetsToDisk();
    }

    [RelayCommand]
    public void LoadUserPreset2()
    {
        EnhanceContrast = UserPreset2Contrast;
        EnhanceBrightness = UserPreset2Brightness;
        EnhanceSaturation = UserPreset2Saturation;
        EnhanceSharpness = UserPreset2Sharpness;
        EnhanceNoiseReduce = UserPreset2NoiseReduce;
        EnhanceLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [Preset] Đã tải thông số từ '{UserPreset2Name}'");
    }

    [RelayCommand]
    public void SaveUserPreset3()
    {
        UserPreset3Contrast = EnhanceContrast;
        UserPreset3Brightness = EnhanceBrightness;
        UserPreset3Saturation = EnhanceSaturation;
        UserPreset3Sharpness = EnhanceSharpness;
        UserPreset3NoiseReduce = EnhanceNoiseReduce;
        EnhanceLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [Preset] Đã lưu thông số vào '{UserPreset3Name}'");
        SaveUserPresetsToDisk();
    }

    [RelayCommand]
    public void LoadUserPreset3()
    {
        EnhanceContrast = UserPreset3Contrast;
        EnhanceBrightness = UserPreset3Brightness;
        EnhanceSaturation = UserPreset3Saturation;
        EnhanceSharpness = UserPreset3Sharpness;
        EnhanceNoiseReduce = UserPreset3NoiseReduce;
        EnhanceLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [Preset] Đã tải thông số từ '{UserPreset3Name}'");
    }

    private void SaveUserPresetsToDisk()
    {
        try
        {
            string appDir = OperatingSystem.IsAndroid()
                ? DownloadEngineService.GetAppSpecificExternalPath()
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".comicdownloader");
            if (!Directory.Exists(appDir)) Directory.CreateDirectory(appDir);
            string file = Path.Combine(appDir, "user_presets.json");
            var data = new
            {
                P1Name = UserPreset1Name, P1Contrast = UserPreset1Contrast, P1Brightness = UserPreset1Brightness, P1Sat = UserPreset1Saturation, P1Sharp = UserPreset1Sharpness, P1Noise = UserPreset1NoiseReduce,
                P2Name = UserPreset2Name, P2Contrast = UserPreset2Contrast, P2Brightness = UserPreset2Brightness, P2Sat = UserPreset2Saturation, P2Sharp = UserPreset2Sharpness, P2Noise = UserPreset2NoiseReduce,
                P3Name = UserPreset3Name, P3Contrast = UserPreset3Contrast, P3Brightness = UserPreset3Brightness, P3Sat = UserPreset3Saturation, P3Sharp = UserPreset3Sharpness, P3Noise = UserPreset3NoiseReduce
            };
            string json = System.Text.Json.JsonSerializer.Serialize(data);
            File.WriteAllText(file, json);
        }
        catch { }
    }

    private void LoadUserPresetsFromDisk()
    {
        try
        {
            string appDir = OperatingSystem.IsAndroid()
                ? DownloadEngineService.GetAppSpecificExternalPath()
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".comicdownloader");
            string file = Path.Combine(appDir, "user_presets.json");
            if (File.Exists(file))
            {
                string json = File.ReadAllText(file);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("P1Name", out var p1n)) UserPreset1Name = p1n.GetString() ?? "Preset 1";
                if (root.TryGetProperty("P1Contrast", out var p1c)) UserPreset1Contrast = p1c.GetSingle();
                if (root.TryGetProperty("P1Brightness", out var p1b)) UserPreset1Brightness = p1b.GetSingle();
                if (root.TryGetProperty("P1Sat", out var p1s)) UserPreset1Saturation = p1s.GetSingle();
                if (root.TryGetProperty("P1Sharp", out var p1sh)) UserPreset1Sharpness = p1sh.GetSingle();
                if (root.TryGetProperty("P1Noise", out var p1nr)) UserPreset1NoiseReduce = p1nr.GetInt32();

                if (root.TryGetProperty("P2Name", out var p2n)) UserPreset2Name = p2n.GetString() ?? "Preset 2";
                if (root.TryGetProperty("P2Contrast", out var p2c)) UserPreset2Contrast = p2c.GetSingle();
                if (root.TryGetProperty("P2Brightness", out var p2b)) UserPreset2Brightness = p2b.GetSingle();
                if (root.TryGetProperty("P2Sat", out var p2s)) UserPreset2Saturation = p2s.GetSingle();
                if (root.TryGetProperty("P2Sharp", out var p2sh)) UserPreset2Sharpness = p2sh.GetSingle();
                if (root.TryGetProperty("P2Noise", out var p2nr)) UserPreset2NoiseReduce = p2nr.GetInt32();

                if (root.TryGetProperty("P3Name", out var p3n)) UserPreset3Name = p3n.GetString() ?? "Preset 3";
                if (root.TryGetProperty("P3Contrast", out var p3c)) UserPreset3Contrast = p3c.GetSingle();
                if (root.TryGetProperty("P3Brightness", out var p3b)) UserPreset3Brightness = p3b.GetSingle();
                if (root.TryGetProperty("P3Sat", out var p3s)) UserPreset3Saturation = p3s.GetSingle();
                if (root.TryGetProperty("P3Sharp", out var p3sh)) UserPreset3Sharpness = p3sh.GetSingle();
                if (root.TryGetProperty("P3Noise", out var p3nr)) UserPreset3NoiseReduce = p3nr.GetInt32();
            }
        }
        catch { }
    }

    [RelayCommand]
    public void ToggleFullscreenPreview()
    {
        IsEnhanceFullscreenVisible = !IsEnhanceFullscreenVisible;
    }

    [RelayCommand]
    public void CloseFullscreenPreview()
    {
        IsEnhanceFullscreenVisible = false;
    }

    [RelayCommand]
    public void OpenComparisonWindow()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                var win = new EnhanceComparisonWindow
                {
                    DataContext = this
                };
                win.Show();
            }
            else
            {
                IsEnhanceFullscreenVisible = true;
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Không thể mở cửa sổ riêng: {ex.Message}. Bật chế độ toàn màn hình trong ứng dụng.");
            IsEnhanceFullscreenVisible = true;
        }
    }

    [RelayCommand]
    public void ResetContrast() => EnhanceContrast = 0f;

    [RelayCommand]
    public void ResetBrightness() => EnhanceBrightness = 0f;

    [RelayCommand]
    public void ResetSaturation() => EnhanceSaturation = 100f;

    [RelayCommand]
    public void ResetSharpness() => EnhanceSharpness = 0f;

    [RelayCommand]
    public void ResetNoiseReduce() => EnhanceNoiseReduce = 0;

    [RelayCommand]
    public void ResetQuality() => EnhanceQuality = 90;

    [RelayCommand]
    public void ResetThreads() => EnhanceThreads = Math.Min(MaxSystemThreads, 16);

    [RelayCommand]
    public void ResetEnhanceSettings()
    {
        EnhanceContrast = 0f;
        EnhanceBrightness = 0f;
        EnhanceSaturation = 100f;
        EnhanceSharpness = 0f;
        EnhanceNoiseReduce = 0;
        EnhanceQuality = 90;
        EnhanceThreads = Math.Min(MaxSystemThreads, 16);
        EnhanceOverwriteOriginal = false;
        EnhanceOutputFormat = "original";

        UpdatePreviewResult();
        EnhanceLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [Đặt lại] Đã khôi phục toàn bộ thông số về giá trị mặc định.");
    }

    [RelayCommand]
    public async Task StartEnhanceAsync()
    {
        if (string.IsNullOrWhiteSpace(EnhanceFolderPath) || !Directory.Exists(EnhanceFolderPath))
        {
            AddLog("WARN", "[Xử lý ảnh] Vui lòng chọn một thư mục nguồn hợp lệ!");
            return;
        }

        string outDir = EnhanceOverwriteOriginal ? EnhanceFolderPath : EnhanceOutputFolderPath;
        if (!EnhanceOverwriteOriginal && string.IsNullOrWhiteSpace(outDir))
        {
            outDir = GetSiblingFolder(EnhanceFolderPath, "enhanced");
            EnhanceOutputFolderPath = outDir;
        }

        IsEnhancing = true;
        EnhanceProgress = 0;
        EnhanceProgressText = "0.0%";
        EnhanceCountText = "0 ảnh";
        EnhanceErrorCountText = "0";
        EnhanceCurrentFileText = _langService.CurrentLanguage == "VI" ? "Đang quét cây thư mục đa tầng..." : "Scanning multi-level folders...";
        EnhanceLogs.Clear();
        EnhanceLogs.Add($"[{DateTime.Now:HH:mm:ss}] Bắt đầu tiến trình xử lý ảnh: Nguồn = {EnhanceFolderPath}, Đích = {outDir}");

        _enhanceCts = new CancellationTokenSource();
        BackgroundExecutionService.Instance.ReportProgress("image_enhancer", "Xử Lý Ảnh", "Đang chuẩn bị xử lý ảnh...", 0, true);

        var options = new ImageEnhancerOptions
        {
            Contrast = EnhanceContrast,
            Brightness = EnhanceBrightness,
            Saturation = EnhanceSaturation,
            Sharpness = EnhanceSharpness,
            NoiseReduce = EnhanceNoiseReduce,
            Quality = EnhanceQuality,
            MaxThreads = EnhanceThreads,
            OverwriteOriginal = EnhanceOverwriteOriginal,
            OutputFormat = EnhanceOutputFormat
        };

        try
        {
            var (success, errors) = await _imageEnhancer.ProcessFolderAsync(
                EnhanceFolderPath,
                outDir,
                options,
                _enhanceCts.Token);

            EnhanceCountText = $"{success} ảnh";
            EnhanceErrorCountText = errors.ToString();
            EnhanceCurrentFileText = _langService.CurrentLanguage == "VI" ? "Hoàn tất." : "Completed.";

            if (errors > 0 && success == 0)
            {
                SoundNotificationService.Instance.PlaySound(SoundNotificationType.DownloadError);
            }
            else
            {
                SoundNotificationService.Instance.PlaySound(SoundNotificationType.DownloadFinish);
            }
        }
        catch (OperationCanceledException)
        {
            EnhanceLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [Đã dừng] Tiến trình xử lý ảnh đã dừng theo yêu cầu.");
            EnhanceCurrentFileText = _langService.CurrentLanguage == "VI" ? "Đã dừng." : "Stopped.";
        }
        catch (Exception ex)
        {
            EnhanceLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [Lỗi] {ex.Message}");
            EnhanceCurrentFileText = "Lỗi: " + ex.Message;
            SoundNotificationService.Instance.PlaySound(SoundNotificationType.DownloadError);
        }
        finally
        {
            IsEnhancing = false;
            BackgroundExecutionService.Instance.CompleteTask("image_enhancer", "Xử Lý Ảnh", "Hoàn tất xử lý ảnh");
        }
    }

    [RelayCommand]
    public void StopEnhance()
    {
        if (_enhanceCts != null && !_enhanceCts.IsCancellationRequested)
        {
            _enhanceCts.Cancel();
            EnhanceLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] Đang gửi yêu cầu dừng tiến trình xử lý...");
        }
    }

    [RelayCommand]
    public void ClearEnhanceLogs()
    {
        EnhanceLogs.Clear();
    }

    #region FILE PACKER (ĐÓNG GÓI FILE ZIP, CBZ, PDF)
    [ObservableProperty]
    private string _packerInputFolderPath = string.Empty;

    [ObservableProperty]
    private string _packerOutputFolderPath = string.Empty;

    [ObservableProperty]
    private bool _isPackerZip = false;

    [ObservableProperty]
    private bool _isPackerCbz = true;

    [ObservableProperty]
    private bool _isPackerPdf = false;

    [ObservableProperty]
    private bool _isPackerBatchSubfolders = true;

    [ObservableProperty]
    private bool _isPacking = false;

    [ObservableProperty]
    private double _packerProgress = 0;

    [ObservableProperty]
    private string _packerProgressText = "0%";

    [ObservableProperty]
    private string _packerCountText = "0 file";

    [ObservableProperty]
    private string _packerErrorCountText = "0";

    [ObservableProperty]
    private string _packerCurrentFileText = "Sẵn sàng.";

    [ObservableProperty]
    private bool _isPackerCompletedVisible;

    [ObservableProperty]
    private string _packerCompletedMessage = string.Empty;

    public ObservableCollection<string> PackerLogs { get; } = new();

    [RelayCommand]
    public void DismissPackerCompleted()
    {
        IsPackerCompletedVisible = false;
    }

    [RelayCommand]
    public async Task BrowsePackerInputFolderAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null)
            {
                var options = new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = _langService.CurrentLanguage == "VI"
                        ? "Chọn thư mục nguồn chứa ảnh hoặc các thư mục chapter cần đóng gói"
                        : "Select input folder containing images or chapters to pack",
                    AllowMultiple = false
                };

                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
                if (folders != null && folders.Count > 0)
                {
                    var selected = folders[0];
                    string? path = selected.TryGetLocalPath() ?? selected.Path?.LocalPath ?? selected.Path?.ToString();
                    string normalized = DownloadEngineService.NormalizeStoragePath(path, selected.Name);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        PackerInputFolderPath = normalized;
                        if (string.IsNullOrWhiteSpace(PackerOutputFolderPath))
                        {
                            PackerOutputFolderPath = GetSiblingFolder(normalized, "packed");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Lỗi chọn thư mục nguồn đóng gói: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task BrowsePackerInputFileAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null)
            {
                var options = new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = _langService.CurrentLanguage == "VI"
                        ? "Chọn file cần chuyển đổi (PDF, ZIP, CBZ, RAR, CBR)"
                        : "Select file to convert (PDF, ZIP, CBZ, RAR, CBR)",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("Tất cả định dạng hỗ trợ (*.pdf, *.zip, *.cbz, *.rar, *.cbr)")
                        {
                            Patterns = new[] { "*.pdf", "*.zip", "*.cbz", "*.rar", "*.cbr" }
                        },
                        new Avalonia.Platform.Storage.FilePickerFileType("PDF Document (*.pdf)")
                        {
                            Patterns = new[] { "*.pdf" }
                        },
                        new Avalonia.Platform.Storage.FilePickerFileType("Comic Archives (*.zip, *.cbz, *.rar, *.cbr)")
                        {
                            Patterns = new[] { "*.zip", "*.cbz", "*.rar", "*.cbr" }
                        }
                    }
                };

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);
                if (files != null && files.Count > 0)
                {
                    var selected = files[0];
                    string? path = selected.TryGetLocalPath() ?? selected.Path?.LocalPath ?? selected.Path?.ToString();
                    string normalized = DownloadEngineService.NormalizeStoragePath(path, selected.Name);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        PackerInputFolderPath = normalized;
                        if (string.IsNullOrWhiteSpace(PackerOutputFolderPath))
                        {
                            PackerOutputFolderPath = GetSiblingFolder(normalized, "packed");
                        }

                        if (LibraryScannerService.IsPdfFile(normalized))
                        {
                            IsPackerCbz = true;
                            IsPackerPdf = false;
                        }
                        else if (LibraryScannerService.IsArchiveFile(normalized))
                        {
                            IsPackerPdf = true;
                            IsPackerCbz = false;
                            IsPackerZip = false;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Lỗi chọn file chuyển đổi: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task BrowsePackerOutputFolderAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null)
            {
                var options = new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = _langService.CurrentLanguage == "VI"
                        ? "Chọn thư mục đích lưu file đóng gói (Zip, Cbz, Pdf)"
                        : "Select output folder to save packed files (Zip, Cbz, Pdf)",
                    AllowMultiple = false
                };

                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
                if (folders != null && folders.Count > 0)
                {
                    var selected = folders[0];
                    string? path = selected.TryGetLocalPath() ?? selected.Path?.LocalPath ?? selected.Path?.ToString();
                    string normalized = DownloadEngineService.NormalizeStoragePath(path, selected.Name);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        PackerOutputFolderPath = normalized;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Lỗi chọn thư mục đích đóng gói: {ex.Message}");
        }
    }

    [RelayCommand]
    public void OpenPackerInputFolder()
    {
        if (string.IsNullOrEmpty(PackerInputFolderPath) || (!Directory.Exists(PackerInputFolderPath) && !File.Exists(PackerInputFolderPath)))
        {
            AddLog("WARN", "[Đóng gói file] Thư mục hoặc file nguồn không tồn tại hoặc chưa được chọn!");
            return;
        }

        string target = File.Exists(PackerInputFolderPath)
            ? (Path.GetDirectoryName(PackerInputFolderPath) ?? PackerInputFolderPath)
            : PackerInputFolderPath;

        _downloadEngine.OpenDirectoryInExplorer(target);
    }

    [RelayCommand]
    public void OpenPackerOutputFolder()
    {
        if (string.IsNullOrEmpty(PackerOutputFolderPath))
        {
            AddLog("WARN", "[Đóng gói file] Thư mục đích chưa được chọn!");
            return;
        }

        if (!Directory.Exists(PackerOutputFolderPath))
        {
            try { Directory.CreateDirectory(PackerOutputFolderPath); } catch { }
        }

        _downloadEngine.OpenDirectoryInExplorer(PackerOutputFolderPath);
    }

    [RelayCommand]
    public async Task StartPackingAsync()
    {
        if (string.IsNullOrWhiteSpace(PackerInputFolderPath) || (!Directory.Exists(PackerInputFolderPath) && !File.Exists(PackerInputFolderPath)))
        {
            AddLog("WARN", "[Đóng gói file] Vui lòng chọn một thư mục hoặc tệp nguồn hợp lệ!");
            return;
        }

        if (!IsPackerZip && !IsPackerCbz && !IsPackerPdf)
        {
            AddLog("WARN", "[Đóng gói file] Vui lòng chọn ít nhất một định dạng (ZIP, CBZ hoặc PDF)!");
            return;
        }

        string outDir = string.IsNullOrWhiteSpace(PackerOutputFolderPath)
            ? GetSiblingFolder(PackerInputFolderPath, "packed")
            : PackerOutputFolderPath;
        PackerOutputFolderPath = outDir;

        IsPacking = true;
        IsPackerCompletedVisible = false;
        PackerProgress = 0;
        PackerProgressText = "0%";
        PackerCountText = "0 file";
        PackerErrorCountText = "0";
        PackerCurrentFileText = "Đang quét danh sách thư mục...";
        PackerLogs.Clear();
        PackerLogs.Add($"[{DateTime.Now:HH:mm:ss}] Bắt đầu tiến trình đóng gói: Nguồn = {PackerInputFolderPath}, Đích = {outDir}");

        _packerCts = new CancellationTokenSource();
        BackgroundExecutionService.Instance.ReportProgress("file_packer", "Đóng Gói File", "Bắt đầu đóng gói file...", 0, true);

        var options = new FilePackerOptions
        {
            CreateZip = IsPackerZip,
            CreateCbz = IsPackerCbz,
            CreatePdf = IsPackerPdf,
            PackSubfoldersIndividually = IsPackerBatchSubfolders
        };

        _filePacker.LogEmitted = (lvl, msg) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                PackerLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [{lvl}] {msg}");
            });
        };

        var prog = new Progress<(int current, int total, string currentFile)>(info =>
        {
            double pct = 0;
            if (info.total > 0)
            {
                pct = Math.Clamp((double)info.current / info.total * 100.0, 0.0, 100.0);
                PackerProgress = pct;
                PackerProgressText = $"{PackerProgress:F0}%";
            }
            PackerCurrentFileText = info.currentFile;
            BackgroundExecutionService.Instance.ReportProgress("file_packer", "Đóng Gói File", $"Đang nén: {info.currentFile} ({pct:F0}%)", pct, true);
        });

        try
        {
            var (success, errors, finalDir) = await _filePacker.ProcessPackingAsync(
                PackerInputFolderPath,
                outDir,
                options,
                prog,
                _packerCts.Token);

            PackerProgress = 100.0;
            PackerProgressText = "100%";
            PackerCountText = $"{success} file";
            PackerErrorCountText = errors.ToString();
            PackerCurrentFileText = _langService.CurrentLanguage == "VI" ? "Hoàn tất đóng gói." : "Packing completed.";
            PackerLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [HOÀN TẤT] Thành công: {success}, Lỗi: {errors}");

            IsPackerCompletedVisible = true;
            PackerCompletedMessage = _langService.CurrentLanguage == "VI"
                ? $"Đã tạo thành công {success} file nén (Lỗi: {errors}) tại thư mục:\n{finalDir}"
                : $"Successfully generated {success} packed files (Errors: {errors}) in:\n{finalDir}";

            AddLog("SUCCESS", $"[Đóng gói file] Hoàn tất đóng gói: {success} file thành công ({finalDir})");
        }
        catch (OperationCanceledException)
        {
            PackerLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [ĐÃ DỪNG] Tiến trình đóng gói đã dừng theo yêu cầu.");
            PackerCurrentFileText = "Đã dừng.";
        }
        catch (Exception ex)
        {
            PackerLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] [LỖI] {ex.Message}");
            PackerCurrentFileText = "Lỗi: " + ex.Message;
        }
        finally
        {
            IsPacking = false;
            BackgroundExecutionService.Instance.CompleteTask("file_packer", "Đóng Gói File", "Hoàn tất đóng gói file");
        }
    }

    [RelayCommand]
    public void StopPacking()
    {
        if (_packerCts != null && !_packerCts.IsCancellationRequested)
        {
            _packerCts.Cancel();
            PackerLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] Đang gửi yêu cầu dừng tiến trình đóng gói...");
        }
    }

    [RelayCommand]
    public void ClearPackerLogs()
    {
        PackerLogs.Clear();
    }
    #endregion

    public static string GetSiblingFolder(string inputPath, string defaultFolderName)
    {
        if (string.IsNullOrWhiteSpace(inputPath)) return string.Empty;
        string clean = inputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Directory.GetParent(clean);
        if (parent != null && !string.IsNullOrWhiteSpace(parent.FullName))
        {
            return Path.Combine(parent.FullName, defaultFolderName);
        }
        return Path.Combine(clean, defaultFolderName);
    }
}

public class MangadexLanguageChoice
{
    public string PrimaryLanguage { get; set; } = "vi";
    public bool UseFallback { get; set; } = true;
}
