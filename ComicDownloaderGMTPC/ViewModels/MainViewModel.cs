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
    private string _updateButtonLabel = "🚀 UPDATE STABLE";

    [ObservableProperty]
    private string _updateBetaButtonLabel = "🧪 UPDATE BETA";

    [ObservableProperty]
    private int _selectedRootTabIndex = 0;

    [ObservableProperty]
    private string _urlInput = string.Empty;

    [ObservableProperty]
    private string _modeSelection = "Single comic";

    [ObservableProperty]
    private bool _isAutoDownload;

    [ObservableProperty]
    private bool _isAutoRetry = true;

    [ObservableProperty]
    private bool _isPaused = false;

    [ObservableProperty]
    private bool _isCompactRow;

    // BACKGROUND EXECUTION & BUBBLE MODE (BONG BÓNG CHẠY NGẦM)
    [ObservableProperty]
    private bool _isBubbleMode = false;

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

    // CONCURRENT COMIC DOWNLOADS & IMAGE THREADS
    public int MaxSystemThreads => CpuTopologyHelper.GetMaxLogicalProcessorCount();
    public int MaxFolderToolThreads => Math.Max(64, MaxSystemThreads);

    [ObservableProperty]
    private int _concurrentComicDownloads = 2; // 1 đến 16

    [ObservableProperty]
    private int _imageDownloadThreads = 3;

    partial void OnConcurrentComicDownloadsChanged(int value)
    {
        _downloadEngine.ConcurrentComicDownloads = Math.Clamp(value, 1, 16);
        _downloadEngine.NotifyConcurrencyChanged();
    }

    partial void OnImageDownloadThreadsChanged(int value)
    {
        _downloadEngine.ImageDownloadThreads = Math.Clamp(value, 1, MaxSystemThreads);
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

    // AUTO SPLIT CHAPTERS (TỰ ĐỘNG TÁCH CHƯƠNG ĐỂ TẢI SONG SONG SIÊU NHANH)
    [ObservableProperty]
    private string _autoSplitChaptersSelection = "OFF";

    [RelayCommand]
    public async Task ApplyAutoSplitChaptersAsync()
    {
        if (string.Equals(AutoSplitChaptersSelection, "OFF", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(AutoSplitChaptersSelection, out int bucketSize) || bucketSize <= 0)
        {
            AddLog("WARN", "Vui lòng chọn một ngưỡng chia chương cụ thể (50, 100,...) thay vì OFF.");
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

    public async Task<int> SplitEligibleBooksAsync(int bucketSize)
    {
        if (bucketSize <= 0 || ComicBooks.Count == 0) return 0;

        var itemsToSplit = ComicBooks
            .Where(b => b != null && string.IsNullOrWhiteSpace(b.ChapterSelectionText) && b.Status != "Completed" && b.Status != "Hoàn tất")
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
                var ranges = new List<string>();

                for (int start = 1; start <= totalChaps; start += bucketSize)
                {
                    int end = Math.Min(start + bucketSize - 1, totalChaps);
                    ranges.Add($"{start}-{end}");
                }

                if (ranges.Count > 1)
                {
                    int insertIndex = ComicBooks.IndexOf(book);
                    if (insertIndex >= 0)
                    {
                        // Đánh dấu item cha
                        book.IsChecked = false;
                        book.Status = "Stopped";
                        book.DetailProgressText = $"Đã tách thành {ranges.Count} dải chương";

                        // Tạo các task con
                        int addedCount = 0;
                        foreach (var range in ranges)
                        {
                            var clone = new ComicBookItem
                            {
                                Title = book.Title,
                                Url = book.Url,
                                Domain = book.Domain,
                                CoverUrl = book.CoverUrl,
                                PreferredLanguage = book.PreferredLanguage,
                                ChapterSelectionText = range,
                                Status = "Waiting",
                                DetailProgressText = $"Chờ tải (Chương {range})",
                                IsChecked = true,
                                LocalDirectory = book.LocalDirectory
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

                            addedCount++;
                            ComicBooks.Insert(insertIndex + addedCount, clone);
                        }

                        totalSplit++;
                        AddLog("INFO", $"Đã tách '{book.Title}' ({totalChaps} chaps) thành {ranges.Count} task tải song song ({string.Join(", ", ranges.Take(3))}...).");
                    }
                }
            }
        }

        if (totalSplit > 0)
        {
            for (int i = 0; i < ComicBooks.Count; i++)
            {
                ComicBooks[i].Index = i + 1;
            }
            UpdateStats();
        }

        return totalSplit;
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
    private int _enhanceThreads = Math.Min(CpuTopologyHelper.GetMaxLogicalProcessorCount(), 16);

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

    [ObservableProperty]
    private bool _isThumbnailMode;

    [ObservableProperty]
    private int _parallelCheckCount = 8;

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
    public ObservableCollection<MissingScanItem> ScanResults { get; } = new();
    public ObservableCollection<LogMessageItem> Logs { get; } = new();
    public ObservableCollection<SourceSearchResultItem> SearchResults { get; } = new();

    public MainViewModel()
    {
        _downloadPathText = _downloadEngine.DownloadRoot;
        _langService.LanguageChanged += OnLanguageChanged;
        _downloadEngine.LogEmitted += OnLogEmitted;
        _downloadEngine.ProgressUpdated += OnProgressUpdated;
        _downloadEngine.PauseStateChanged += (paused) => Avalonia.Threading.Dispatcher.UIThread.Post(() => IsPaused = paused);
        DownloadEngineService.AndroidOpenFolderRequested += OnAndroidOpenFolderRequested;
        LoadUserPresetsFromDisk();

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
        LoadPasswordManagerSettings();
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
            IsBubbleMode = enabled;
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

    [RelayCommand]
    public void ForceExitApp()
    {
        IsExitConfirmOpen = false;
        try
        {
            Stop();
            StopManualSplit();
            StopEnhance();
            StopPacking();
            StopFolderTool();
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
        AppTitle = _langService.CurrentLanguage == "VI"
            ? "Comic-GMTPC Avalonia v1.0 - Tiếng Việt"
            : "Comic-GMTPC Avalonia v1.0 - English";
    }

    private void OnProgressUpdated()
    {
        DownloadSpeedText = _downloadEngine.CurrentSpeedText;
        UpdateStats();
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

        for (int i = 0; i < selected.Count; i++)
        {
            var sr = selected[i];
            int idx = startIndex + i;
            var book = await _scraperService.ScrapeBookAsync(sr.Url, idx, mangadexLang, mangadexFallback);
            if (!string.IsNullOrEmpty(sr.CoverUrl) && string.IsNullOrEmpty(book.CoverUrl))
            {
                book.CoverUrl = sr.CoverUrl;
            }
            ComicBooks.Add(book);
            AddLog("INFO", $"[{idx}] Đã nạp từ nguồn: {book.Title} ({book.TotalChapters} chaps) [{book.Domain}]");
        }

        UpdateStats();
        StartScanMissing();
        SelectedRootTabIndex = 1; // Tự động chuyển sang tab Download
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
        SelectedRootTabIndex = 1; // Chuyển sang Tab Download để theo dõi tiến độ
    }

    // ==========================================
    // GET LINK / EXTRACTION PIPELINE
    // ==========================================

    [RelayCommand]
    public async Task GetLinkAsync()
    {
        await ExtractUrlsFromTextAsync(UrlInput, clearExisting: true);
    }

    [RelayCommand]
    public async Task GetMoreAsync()
    {
        await ExtractUrlsFromTextAsync(UrlInput, clearExisting: false);
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
            SelectedRootTabIndex = 1; // Chuyển sang Tab Download để theo dõi tiến độ
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

    [RelayCommand]
    public async Task DownloadAllAsync()
    {
        if (ComicBooks.Count == 0)
        {
            AddLog("WARN", "Không có truyện nào trong hàng chờ để tải!");
            return;
        }

        // Tự động tách chương nếu người dùng chọn ngưỡng khác OFF
        if (!string.Equals(AutoSplitChaptersSelection, "OFF", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(AutoSplitChaptersSelection, out int bucketSize) && bucketSize > 0)
        {
            await SplitEligibleBooksAsync(bucketSize);
        }

        AddLog("INFO", $"Bắt đầu tải {ComicBooks.Count(b => b.IsChecked)} truyện...");
        await _downloadEngine.StartDownloadAsync(ComicBooks, ModeSelection);
        UpdateStats();
    }

    [RelayCommand]
    public async Task DownloadNewAsync()
    {
        // Tự động tách chương nếu người dùng chọn ngưỡng khác OFF
        if (!string.Equals(AutoSplitChaptersSelection, "OFF", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(AutoSplitChaptersSelection, out int bucketSize) && bucketSize > 0)
        {
            await SplitEligibleBooksAsync(bucketSize);
        }

        var newItems = ComicBooks.Where(b => b.IsChecked && (b.Status == "Waiting" || b.Status == "Chờ tải" || string.IsNullOrEmpty(b.Status))).ToList();
        if (newItems.Count == 0)
        {
            AddLog("INFO", "Không có truyện mới nào đang chờ tải.");
            return;
        }

        AddLog("INFO", $"Tiếp tục nạp thêm {newItems.Count} truyện vào hàng đợi tải...");
        await _downloadEngine.DownloadNewAsync(newItems, ModeSelection);
        UpdateStats();
    }

    [RelayCommand]
    public void PauseDownload()
    {
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
        _downloadEngine.TogglePause();
    }

    [RelayCommand]
    public void Stop()
    {
        _downloadEngine.Stop();
        AddLog("WARN", "Đã yêu cầu dừng tải.");
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
        AddLog("INFO", "Đã xóa sạch hàng chờ tải.");
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
    public void MoveUp(ComicBookItem? item)
    {
        var target = item ?? SelectedComic;
        if (target == null) return;

        int index = ComicBooks.IndexOf(target);
        if (index > 0)
        {
            ComicBooks.Move(index, index - 1);
            ReindexComicBooks();
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
        }
    }

    [RelayCommand]
    public void DeleteSelected()
    {
        var toRemove = ComicBooks.Where(b => b.IsChecked).ToList();
        if (toRemove.Count == 0 && SelectedComic != null)
        {
            toRemove.Add(SelectedComic);
        }

        foreach (var b in toRemove)
        {
            ComicBooks.Remove(b);
        }

        ReindexComicBooks();
        UpdateStats();
        AddLog("INFO", $"Đã xóa {toRemove.Count} truyện khỏi danh sách.");
    }

    [RelayCommand]
    public void DeleteItem(ComicBookItem? item)
    {
        var target = item ?? SelectedComic;
        if (target != null)
        {
            ComicBooks.Remove(target);
            ReindexComicBooks();
            UpdateStats();
            AddLog("INFO", $"Đã xóa '{target.Title}' khỏi danh sách.");
        }
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
                });

            if (!success)
            {
                AddLog("WARN", "[Cập nhật tự động] Không thể tải bản cập nhật Stable. Vui lòng kiểm tra lại kết nối mạng và thử lại sau.");
                UpdateButtonLabel = "🚀 UPDATE STABLE";
            }
            else if (OperatingSystem.IsWindows())
            {
                UpdateButtonLabel = "🔄 Đang khởi động lại...";
            }
            else
            {
                UpdateButtonLabel = "🚀 UPDATE STABLE";
            }
        }
        catch (Exception ex)
        {
            AddLog("ERROR", $"Lỗi cập nhật tự động: {ex.Message}");
            UpdateButtonLabel = "🚀 UPDATE STABLE";
        }
        finally
        {
            IsUpdating = false;
        }
    }

    [RelayCommand]
    public async Task AutoUpdateBetaAsync()
    {
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
                });

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

        AddLog("INFO", $"Đang trích xuất thông tin cho {targetUrls.Count} link truyện mới...");

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
        for (int i = 0; i < targetUrls.Count; i++)
        {
            string url = targetUrls[i];
            int currentIndex = startIndex + i;

            var book = await _scraperService.ScrapeBookAsync(url, currentIndex, mangadexLang, mangadexFallback);
            ComicBooks.Add(book);
            AddLog("INFO", $"[{currentIndex}] Đã nạp: {book.Title} ({book.TotalChapters} chaps) - Domain: {book.Domain}");
        }

        UpdateStats();
        StartScanMissing();

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

    private void UpdateStats()
    {
        TotalBooks = ComicBooks.Count;
        DownloadingCount = ComicBooks.Count(b => b.Status == "Downloading");
        CompletedCount = ComicBooks.Count(b => b.Status == "Completed");
        ErrorCount = ComicBooks.Count(b => b.Status == "Error");
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
        if (string.IsNullOrEmpty(PackerInputFolderPath) || !Directory.Exists(PackerInputFolderPath))
        {
            AddLog("WARN", "[Đóng gói file] Thư mục nguồn không tồn tại hoặc chưa được chọn!");
            return;
        }

        _downloadEngine.OpenDirectoryInExplorer(PackerInputFolderPath);
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
        if (string.IsNullOrWhiteSpace(PackerInputFolderPath) || !Directory.Exists(PackerInputFolderPath))
        {
            AddLog("WARN", "[Đóng gói file] Vui lòng chọn một thư mục nguồn hợp lệ!");
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
