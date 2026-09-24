using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly LanguageService _langService = LanguageService.Instance;
    private readonly ComicScraperService _scraperService = ComicScraperService.Instance;
    private readonly DownloadEngineService _downloadEngine = DownloadEngineService.Instance;
    private readonly MissingChapterScannerService _scannerService = MissingChapterScannerService.Instance;
    private CancellationTokenSource? _scanCts;

    [ObservableProperty]
    private string _appTitle = "Comic-GMTPC Avalonia v1.0 - Tiếng Việt";

    [ObservableProperty]
    private string _currentLanguage = "VI";

    [ObservableProperty]
    private string _urlInput = string.Empty;

    [ObservableProperty]
    private string _modeSelection = "Single comic"; // "Single comic" or "Multi-comic"

    [ObservableProperty]
    private bool _isAutoDownload;

    [ObservableProperty]
    private bool _isAutoRetry = true;

    [ObservableProperty]
    private bool _isCompactRow;

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

    public ObservableCollection<ComicBookItem> ComicBooks { get; } = new();
    public ObservableCollection<MissingScanItem> ScanResults { get; } = new();
    public ObservableCollection<LogMessageItem> Logs { get; } = new();

    public MainViewModel()
    {
        _downloadPathText = _downloadEngine.DownloadRoot;
        _langService.LanguageChanged += OnLanguageChanged;
        _downloadEngine.LogEmitted += OnLogEmitted;
        _downloadEngine.ProgressUpdated += UpdateStats;

        UpdateLanguageStrings();
        AddLog("INFO", "Hệ thống Comic Downloader GMTPC Avalonia đã khởi chạy thành công.");
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

    [RelayCommand]
    public void ToggleLanguage()
    {
        _langService.ToggleLanguage();
        AddLog("INFO", $"Chuyển đổi ngôn ngữ sang: {_langService.CurrentLanguage}");
    }

    [RelayCommand]
    public async Task GetLinkAsync()
    {
        ComicBooks.Clear();
        ScanResults.Clear();
        await ExtractUrlsInternalAsync();
    }

    [RelayCommand]
    public async Task GetMoreAsync()
    {
        await ExtractUrlsInternalAsync();
    }

    [RelayCommand]
    public void ClearLinks()
    {
        UrlInput = string.Empty;
        AddLog("INFO", "Đã xóa ô nhập link.");
    }

    [RelayCommand]
    public async Task DownloadAllAsync()
    {
        if (ComicBooks.Count == 0)
        {
            AddLog("WARN", "Không có truyện nào trong hàng chờ để tải!");
            return;
        }

        AddLog("INFO", $"Bắt đầu tải {ComicBooks.Count(b => b.IsChecked)} truyện...");
        await _downloadEngine.StartDownloadAsync(ComicBooks, ModeSelection);
        UpdateStats();
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
    public void StartScanMissing()
    {
        if (ComicBooks.Count == 0)
        {
            AddLog("WARN", "Chưa có danh sách truyện để quét chap thiếu!");
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

    private async Task ExtractUrlsInternalAsync()
    {
        if (string.IsNullOrWhiteSpace(UrlInput))
        {
            AddLog("WARN", "Vui lòng dán ít nhất 1 đường link truyện vào ô nhập!");
            return;
        }

        var lines = UrlInput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(l => l.Trim())
                            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"))
                            .Distinct()
                            .ToList();

        if (lines.Count == 0)
        {
            AddLog("WARN", "Không tìm thấy link hợp lệ nào.");
            return;
        }

        AddLog("INFO", $"Đang trích xuất thông tin cho {lines.Count} link truyện...");

        int startIndex = ComicBooks.Count + 1;
        for (int i = 0; i < lines.Count; i++)
        {
            string url = lines[i];
            int currentIndex = startIndex + i;

            var book = await _scraperService.ScrapeBookAsync(url, currentIndex);
            ComicBooks.Add(book);
            AddLog("INFO", $"[{currentIndex}] Đã nạp: {book.Title} ({book.TotalChapters} chaps) - Domain: {book.Domain}");
        }

        UpdateStats();

        // Auto scan missing chapters for newly extracted books
        StartScanMissing();

        if (IsAutoDownload)
        {
            AddLog("INFO", "Tự động kích hoạt tải xuống theo thiết lập 'Tự động tải'.");
            _ = DownloadAllAsync();
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
}
