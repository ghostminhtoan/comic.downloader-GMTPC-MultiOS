using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly LanguageService _langService = LanguageService.Instance;
    private readonly ComicScraperService _scraperService = ComicScraperService.Instance;
    private readonly DownloadEngineService _downloadEngine = DownloadEngineService.Instance;
    private readonly MissingChapterScannerService _scannerService = MissingChapterScannerService.Instance;
    private readonly SourceSearchService _sourceSearchService = SourceSearchService.Instance;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    private string _appTitle = "Comic-GMTPC Avalonia v1.0 - Tiếng Việt";

    [ObservableProperty]
    private string _currentLanguage = "VI";

    [ObservableProperty]
    private bool _isUpdating = false;

    [ObservableProperty]
    private string _updateButtonLabel = "🚀 CẬP NHẬT";

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
        _downloadEngine.AndroidOpenFolderRequested += OnAndroidOpenFolderRequested;

        UpdateLanguageStrings();
        AddLog("INFO", "Hệ thống Comic Downloader GMTPC Avalonia khởi chạy thành công (Hỗ trợ: Windows, Linux, Android).");
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
        if (SearchNettruyen) domains.Add("nettruyen.tech");
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
        string targetDomain = SearchMangaDex ? "mangadex.org" : (SearchTruyenqq ? "truyenqqto.com" : "nettruyen.tech");
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

        for (int i = 0; i < selected.Count; i++)
        {
            var sr = selected[i];
            int idx = startIndex + i;
            var book = await _scraperService.ScrapeBookAsync(sr.Url, idx);
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
                    string? path = selected.TryGetLocalPath() ?? selected.Path?.LocalPath;
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

        string updateUrl = OperatingSystem.IsAndroid()
            ? "https://github.com/ghostminhtoan/comic.downloader.gmtpc/releases/download/release/com.CompanyName.ComicDownloaderGMTPC-Signed.apk"
            : "https://github.com/ghostminhtoan/comic.downloader.gmtpc/releases/download/release/ComicDownloaderGMTPC.Desktop.exe";

        IsUpdating = true;
        UpdateButtonLabel = "⏳ Đang kết nối...";

        try
        {
            string platformName = OperatingSystem.IsAndroid() ? "Android APK" : "Windows Standalone EXE";
            AddLog("INFO", $"🚀 Bắt đầu tự động tải bản cập nhật mới nhất cho {platformName}...");

            bool success = await AppUpdateService.Instance.DownloadAndInstallUpdateAsync(
                updateUrl,
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
                // Fallback nếu có lỗi mạng đặc biệt: mở trình duyệt và sao chép link
                var topLevel = GetTopLevel();
                if (topLevel?.Launcher != null)
                {
                    await topLevel.Launcher.LaunchUriAsync(new Uri(updateUrl));
                }

                if (topLevel?.Clipboard != null)
                {
                    await topLevel.Clipboard.SetTextAsync(updateUrl);
                    AddLog("INFO", $"Đã sao chép link dự phòng vào Clipboard: {updateUrl}");
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("ERROR", $"Lỗi cập nhật tự động: {ex.Message}");
        }
        finally
        {
            IsUpdating = false;
            UpdateButtonLabel = "🚀 CẬP NHẬT";
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
