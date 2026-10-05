using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComicDownloaderGMTPC.Models;

namespace ComicDownloaderGMTPC.Services;

public class DownloadEngineService
{
    private static readonly Lazy<DownloadEngineService> _instance = new(() => new DownloadEngineService());
    public static DownloadEngineService Instance => _instance.Value;

    private readonly HttpClient _httpClient;
    private readonly ComicScraperService _scraperService = ComicScraperService.Instance;
    private CancellationTokenSource? _cts;
    private bool _isDownloading;
    private volatile bool _isPaused;
    private TaskCompletionSource<bool>? _pauseTcs;
    private long _totalBytesDownloadedInWindow;
    private DateTime _lastSpeedCheckTime = DateTime.UtcNow;
    private string? _lastSuccessfulNettruyenCdnServer;

    public bool IsDownloading => _isDownloading;
    public bool IsPaused => _isPaused;
    public string DownloadRoot { get; set; }
    public string CurrentSpeedText { get; private set; } = "0.0 KB/s";

    public event Action<string, string>? LogEmitted;
    public event Action? ProgressUpdated;
    public event Action<bool>? PauseStateChanged;
    public static event Action<string>? AndroidOpenFolderRequested;
    public static event Action? OpenStorageSettingsRequested;

    public bool AutoSplitLongImages { get; set; } = false;
    public int AutoSplitHeight { get; set; } = 5000;
    public int AutoSplitQuality { get; set; } = 90;

    // SỐ TRUYỆN TẢI CÙNG LÚC & SỐ LUỒNG TẢI ẢNH
    public int ConcurrentComicDownloads { get; set; } = 2; // 1 đến 8 (mặc định 2)
    public int ImageDownloadThreads { get; set; } = 3; // 1 đến 16 (mặc định 3)

    private readonly System.Collections.Concurrent.ConcurrentQueue<ComicBookItem> _downloadQueue = new();
    private readonly HashSet<ComicBookItem> _enqueuedItems = new();
    private readonly object _queueLock = new();
    private string _activeMode = "Single comic";

    public void RequestOpenStorageSettings() => OpenStorageSettingsRequested?.Invoke();

    public DownloadEngineService()
    {
        var handler = DoHResolver.CreateBypassHandler();
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");

        DownloadRoot = InitDownloadRoot();
    }


    private string InitDownloadRoot()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string configFilePath = Path.Combine(appDir, "download_path.cfg");

        try
        {
            if (File.Exists(configFilePath))
            {
                string saved = File.ReadAllText(configFilePath).Trim();
                string normalizedSaved = NormalizeStoragePath(saved);

                if (!string.IsNullOrWhiteSpace(normalizedSaved))
                {
                    try
                    {
                        if (!Directory.Exists(normalizedSaved)) Directory.CreateDirectory(normalizedSaved);
                        if (!string.Equals(saved, normalizedSaved, StringComparison.Ordinal))
                        {
                            File.WriteAllText(configFilePath, normalizedSaved);
                        }
                        return normalizedSaved;
                    }
                    catch {}
                }
            }
        }
        catch {}

        if (OperatingSystem.IsAndroid())
        {
            string androidPath = GetDefaultAndroidDownloadPath();
            try { File.WriteAllText(configFilePath, androidPath); } catch {}
            return androidPath;
        }

        string defaultPath = Path.Combine(appDir, "Downloads");
        try
        {
            if (!Directory.Exists(defaultPath)) Directory.CreateDirectory(defaultPath);
            return defaultPath;
        }
        catch
        {
            string docsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ComicDownloads");
            try { if (!Directory.Exists(docsPath)) Directory.CreateDirectory(docsPath); } catch {}
            return docsPath;
        }
    }

    public void SetDownloadRoot(string newPath)
    {
        if (string.IsNullOrWhiteSpace(newPath)) return;
        string normalized = NormalizeStoragePath(newPath);

        try
        {
            if (!Directory.Exists(normalized)) Directory.CreateDirectory(normalized);
            DownloadRoot = normalized;

            string configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "download_path.cfg");
            File.WriteAllText(configFilePath, normalized);
            LogEmitted?.Invoke("SUCCESS", $"Đã lưu cấu hình thư mục tải về: {normalized}");
        }
        catch (Exception ex)
        {
            LogEmitted?.Invoke("WARN", $"Không thể tạo thư mục '{normalized}': {ex.Message}. Đang chuyển về thư mục mặc định.");
            if (OperatingSystem.IsAndroid())
            {
                DownloadRoot = GetDefaultAndroidDownloadPath();
            }
        }
    }

    public static string NormalizeStoragePath(string? rawPath, string? folderName = null)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return OperatingSystem.IsAndroid() ? GetDefaultAndroidDownloadPath() : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads");
        }

        string path = rawPath.Trim().TrimEnd('/', '\\');

        if (OperatingSystem.IsAndroid())
        {
            string decoded = Uri.UnescapeDataString(path);

            if (decoded.StartsWith("/storage/", StringComparison.OrdinalIgnoreCase) ||
                decoded.StartsWith("/sdcard/", StringComparison.OrdinalIgnoreCase) ||
                decoded.StartsWith("/data/", StringComparison.OrdinalIgnoreCase))
            {
                if (decoded.StartsWith("/sdcard", StringComparison.OrdinalIgnoreCase))
                {
                    decoded = "/storage/emulated/0" + decoded.Substring(7);
                }
                return decoded.TrimEnd('/', '\\');
            }

            if (decoded.StartsWith("raw:", StringComparison.OrdinalIgnoreCase))
            {
                return decoded.Substring(4).TrimEnd('/', '\\');
            }

            int colonIdx = decoded.LastIndexOf(':');
            if (colonIdx >= 0 && colonIdx < decoded.Length - 1)
            {
                string volumePart = decoded.Substring(0, colonIdx);
                string subPath = decoded.Substring(colonIdx + 1).Trim().Trim('/', '\\');

                int slashBeforeColon = volumePart.LastIndexOfAny(new[] { '/', '%', ':' });
                string volumeId = slashBeforeColon >= 0 ? volumePart.Substring(slashBeforeColon + 1) : volumePart;

                if (volumeId.Equals("primary", StringComparison.OrdinalIgnoreCase) || volumeId.Equals("0", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(subPath))
                    {
                        return !string.IsNullOrWhiteSpace(folderName)
                            ? Path.Combine("/storage/emulated/0", folderName)
                            : "/storage/emulated/0";
                    }
                    return $"/storage/emulated/0/{subPath}";
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(volumeId, @"^[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}$"))
                {
                    if (string.IsNullOrWhiteSpace(subPath))
                    {
                        return !string.IsNullOrWhiteSpace(folderName)
                            ? Path.Combine($"/storage/{volumeId}", folderName)
                            : $"/storage/{volumeId}";
                    }
                    return $"/storage/{volumeId}/{subPath}";
                }
                else if (!string.IsNullOrWhiteSpace(subPath))
                {
                    return $"/storage/emulated/0/{subPath}";
                }
            }

            string searchStr = decoded.ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(folderName) && 
                !folderName.Equals("primary", StringComparison.OrdinalIgnoreCase) &&
                !folderName.Equals("0", StringComparison.OrdinalIgnoreCase))
            {
                if (searchStr.Contains("download")) return Path.Combine("/storage/emulated/0/Download", folderName);
                if (searchStr.Contains("document")) return Path.Combine("/storage/emulated/0/Documents", folderName);
                if (searchStr.Contains("picture")) return Path.Combine("/storage/emulated/0/Pictures", folderName);
                if (searchStr.Contains("dcim")) return Path.Combine("/storage/emulated/0/DCIM", folderName);
                return Path.Combine("/storage/emulated/0", folderName);
            }

            if (searchStr.Contains("download")) return "/storage/emulated/0/Download";
            if (searchStr.Contains("document")) return "/storage/emulated/0/Documents";
            if (searchStr.Contains("picture")) return "/storage/emulated/0/Pictures";

            return GetDefaultAndroidDownloadPath();
        }

        return path;
    }

    public static string GetDefaultAndroidDownloadPath()
    {
        string pub = "/storage/emulated/0/Download/ComicDownloads";
        if (CanWriteToDirectory(pub)) return pub;

        string appExt = GetAppSpecificExternalPath();
        if (CanWriteToDirectory(appExt)) return appExt;

        string docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ComicDownloads");
        try { if (!Directory.Exists(docs)) Directory.CreateDirectory(docs); } catch {}
        return docs;
    }

    public static string GetAppSpecificExternalPath()
    {
        string pkgName = "com.CompanyName.ComicDownloaderGMTPC";
        string path = $"/storage/emulated/0/Android/data/{pkgName}/files/Download";
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        }
        catch {}
        return path;
    }

    public static bool CanWriteToDirectory(string dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath)) return false;
        try
        {
            if (!Directory.Exists(dirPath))
            {
                Directory.CreateDirectory(dirPath);
            }
            string testFile = Path.Combine(dirPath, $".probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(testFile, new byte[] { 0x47, 0x4D, 0x54 });
            if (File.Exists(testFile))
            {
                File.Delete(testFile);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public string EnsureWritableDownloadRoot(string candidateRoot)
    {
        if (!OperatingSystem.IsAndroid())
        {
            return candidateRoot;
        }

        candidateRoot = NormalizeStoragePath(candidateRoot);

        if (CanWriteToDirectory(candidateRoot))
        {
            return candidateRoot;
        }

        string pubDownload = "/storage/emulated/0/Download/ComicDownloads";
        if (CanWriteToDirectory(pubDownload))
        {
            SetDownloadRoot(pubDownload);
            return pubDownload;
        }

        string appStorage = GetAppSpecificExternalPath();
        if (CanWriteToDirectory(appStorage))
        {
            SetDownloadRoot(appStorage);
            LogEmitted?.Invoke("WARN", $"[Bộ nhớ Android] Thư mục ngoài bị chặn Access Denied. Đã tự động kích hoạt thư mục an toàn:\n{appStorage}\nToàn bộ ảnh sẽ được tải đầy đủ vào đây!");
            return appStorage;
        }

        string docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ComicDownloads");
        try { if (!Directory.Exists(docs)) Directory.CreateDirectory(docs); } catch {}
        SetDownloadRoot(docs);
        return docs;
    }

    public void NotifyConcurrencyChanged()
    {
        ProgressUpdated?.Invoke();
    }

    public async Task StartDownloadAsync(IEnumerable<ComicBookItem> items, string mode, CancellationToken externalCt = default)
    {
        if (_isDownloading) return;

        _isDownloading = true;
        _activeMode = mode;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _cts.Token;
        _lastSpeedCheckTime = DateTime.UtcNow;
        _totalBytesDownloadedInWindow = 0;

        int concurrentComics = Math.Clamp(ConcurrentComicDownloads, 1, 16);
        int imageThreads = Math.Max(1, ImageDownloadThreads);

        lock (_queueLock)
        {
            while (_downloadQueue.TryDequeue(out _)) { }
            _enqueuedItems.Clear();

            var validItems = items.Where(item => item.IsChecked && item.Status != "Completed").ToList();
            if (validItems.Count == 0)
            {
                validItems = items.Where(item => item.IsChecked).ToList();
            }

            foreach (var item in validItems)
            {
                if (_enqueuedItems.Add(item))
                {
                    item.Status = "Waiting";
                    item.StatusMessage = "Đang chờ đến lượt tải...";
                    _downloadQueue.Enqueue(item);
                }
            }
        }

        if (_enqueuedItems.Count == 0)
        {
            LogEmitted?.Invoke("WARN", "Không có truyện nào được tích chọn để tải.");
            _isDownloading = false;
            return;
        }

        LogEmitted?.Invoke("INFO", $"Bắt đầu tải danh sách truyện (Song song: {concurrentComics} truyện, {imageThreads} luồng ảnh | Thư mục: {mode}) tới: {DownloadRoot}");

        try
        {
            BackgroundExecutionService.Instance.ReportProgress("download_queue", "Tải Truyện", "Bắt đầu tải danh sách truyện...", 0, true);

            var workerTasks = Enumerable.Range(0, concurrentComics).Select(async _ =>
            {
                while (!ct.IsCancellationRequested)
                {
                    ComicBookItem? item = null;
                    lock (_queueLock)
                    {
                        if (!_downloadQueue.TryDequeue(out item))
                        {
                            break;
                        }
                    }

                    if (item != null)
                    {
                        try
                        {
                            await DownloadComicBookAsync(item, _activeMode, imageThreads, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            LogEmitted?.Invoke("ERROR", $"Lỗi tải truyện '{item.Title}': {ex.Message}");
                        }
                    }
                }
            }).ToList();

            await Task.WhenAll(workerTasks).ConfigureAwait(false);
            LogEmitted?.Invoke("SUCCESS", "Hoàn tất toàn bộ tác vụ tải trong hàng chờ.");
            SoundNotificationService.Instance.PlaySound(SoundNotificationType.DownloadFinish);
        }
        catch (OperationCanceledException)
        {
            LogEmitted?.Invoke("WARN", "Tiến trình tải đã được người dùng dừng lại.");
        }
        catch (Exception ex)
        {
            LogEmitted?.Invoke("ERROR", $"Tiến trình tải gặp lỗi: {ex.Message}");
            SoundNotificationService.Instance.PlaySound(SoundNotificationType.DownloadError);
        }
        finally
        {
            _isDownloading = false;
            _isPaused = false;
            _pauseTcs?.TrySetResult(true);
            _pauseTcs = null;
            CurrentSpeedText = "0.0 KB/s";
            BackgroundExecutionService.Instance.CompleteTask("download_queue", "Tải Truyện", "Hoàn tất tải truyện");
            PauseStateChanged?.Invoke(false);
            ProgressUpdated?.Invoke();
        }
    }

    public async Task DownloadNewAsync(IEnumerable<ComicBookItem> items, string mode, CancellationToken externalCt = default)
    {
        var pendingItems = items.Where(item => item.IsChecked && item.Status != "Completed").ToList();
        if (pendingItems.Count == 0)
        {
            LogEmitted?.Invoke("WARN", "Không có truyện mới nào được tích chọn để tải tiếp.");
            return;
        }

        if (_isDownloading)
        {
            int enqueuedCount = 0;
            lock (_queueLock)
            {
                foreach (var item in pendingItems)
                {
                    if (_enqueuedItems.Add(item))
                    {
                        item.Status = "Waiting";
                        item.StatusMessage = "Đã xếp vào hàng chờ tải tiếp...";
                        _downloadQueue.Enqueue(item);
                        enqueuedCount++;
                    }
                }
            }
            LogEmitted?.Invoke("INFO", $"Đã nạp thêm {enqueuedCount} truyện mới vào tiến trình tải đang chạy.");
            ProgressUpdated?.Invoke();
        }
        else
        {
            await StartDownloadAsync(pendingItems, mode, externalCt).ConfigureAwait(false);
        }
    }

    public void Pause()
    {
        if (!_isDownloading || _isPaused) return;
        _isPaused = true;
        _pauseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        LogEmitted?.Invoke("WARN", "⏸️ Đã tạm dừng tải truyện. Bấm TIẾP TỤC để tiếp tục tiến trình.");
        BackgroundExecutionService.Instance.ReportProgress("download_queue", "Tải Truyện (Tạm dừng)", "Đã tạm dừng tải truyện...", 0, true);
        PauseStateChanged?.Invoke(true);
        ProgressUpdated?.Invoke();
    }

    public void Resume()
    {
        if (!_isPaused) return;
        _isPaused = false;
        _pauseTcs?.TrySetResult(true);
        _pauseTcs = null;
        LogEmitted?.Invoke("INFO", "▶️ Tiếp tục tiến trình tải truyện...");
        BackgroundExecutionService.Instance.ReportProgress("download_queue", "Tải Truyện", "Đang tiếp tục tải...", 0, true);
        PauseStateChanged?.Invoke(false);
        ProgressUpdated?.Invoke();
    }

    public void TogglePause()
    {
        if (_isPaused) Resume();
        else Pause();
    }

    private async Task WaitIfPausedAsync(CancellationToken ct)
    {
        var tcs = _pauseTcs;
        if (_isPaused && tcs != null)
        {
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            try
            {
                await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) {}
        }
    }

    public void Stop()
    {
        _isPaused = false;
        _pauseTcs?.TrySetCanceled();
        _pauseTcs = null;
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            LogEmitted?.Invoke("WARN", "Đang yêu cầu dừng tất cả tác vụ tải...");
        }
        PauseStateChanged?.Invoke(false);
    }

    public void OpenDirectoryInExplorer(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                path = DownloadRoot;
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
            {
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsAndroid())
            {
                AndroidOpenFolderRequested?.Invoke(path);
            }
        }
        catch (Exception ex)
        {
            LogEmitted?.Invoke("WARN", $"Không thể mở thư mục '{path}': {ex.Message}");
        }
    }

    private async Task DownloadComicBookAsync(ComicBookItem book, string mode, int imageThreads, CancellationToken ct)
    {
        book.Status = "Downloading";
        book.StatusMessage = "Đang khởi tạo thư mục và nạp danh sách chương...";
        book.DetailProgressText = "Đang khởi tạo...";
        ProgressUpdated?.Invoke();

        string effectiveRoot = EnsureWritableDownloadRoot(DownloadRoot);
        string serverFolder = DomainRoutingService.GetServerFolderName(book.Domain, book.Url);
        string serverDir = Path.Combine(effectiveRoot, serverFolder);
        string safeBookName = MakeSafeFilename(book.Title, 80);
        string bookDir = Path.Combine(serverDir, safeBookName);
        book.LocalDirectory = bookDir;

        try
        {
            bookDir = SafeCreateDirectory(bookDir);
            book.LocalDirectory = bookDir;

            // Trích xuất danh sách chapter nếu chưa có
            if (book.Chapters.Count == 0)
            {
                var refreshed = await _scraperService.ScrapeBookAsync(book.Url, book.Index, book.PreferredLanguage, true, ct).ConfigureAwait(false);
                book.Chapters = refreshed.Chapters;
                book.TotalChapters = refreshed.TotalChapters;
                if (!string.IsNullOrWhiteSpace(refreshed.StatusMessage))
                {
                    book.StatusMessage = refreshed.StatusMessage;
                }
            }

            if (book.Chapters.Count == 0)
            {
                book.Status = "Error";
                book.StatusMessage = "Không có chương nào để tải (Hoặc lỗi kết nối/bị chặn).";
                book.DetailProgressText = "0 chương";
                LogEmitted?.Invoke("ERROR", $"Truyện '{book.Title}' không thể tải vì 0 chương: {book.StatusMessage}");
                ProgressUpdated?.Invoke();
                return;
            }

            // XỬ LÝ CHỌN SỐ CHAPTER ĐỂ TẢI (CHẤP NHẬN CHỮ, SỐ NGUYÊN, SỐ THẬP PHÂN)
            List<ChapterItem> targetChapters = book.Chapters;
            if (!string.IsNullOrWhiteSpace(book.ChapterSelectionText))
            {
                var chapterFilter = ChapterRangeParser.Parse(book.ChapterSelectionText);
                if (chapterFilter != null)
                {
                    var filtered = new List<ChapterItem>();
                    for (int i = 0; i < book.Chapters.Count; i++)
                    {
                        var ch = book.Chapters[i];
                        if (chapterFilter.IsMatch(ch.Title))
                        {
                            filtered.Add(ch);
                        }
                    }

                    if (filtered.Count > 0)
                    {
                        targetChapters = filtered;
                        LogEmitted?.Invoke("INFO", $"[{book.Title}] Bộ lọc chapter '{book.ChapterSelectionText}': Đã chọn {filtered.Count}/{book.Chapters.Count} chương");
                    }
                    else
                    {
                        LogEmitted?.Invoke("WARN", $"[{book.Title}] Bộ lọc '{book.ChapterSelectionText}' không khớp chương nào. Tải toàn bộ {book.Chapters.Count} chương.");
                    }
                }
            }

            int totalChapters = Math.Max(1, targetChapters.Count);
            int completedChapters = 0;

            for (int chIdx = 0; chIdx < targetChapters.Count; chIdx++)
            {
                if (ct.IsCancellationRequested)
                {
                    book.Status = "Stopped";
                    book.StatusMessage = "Đã dừng bởi người dùng";
                    book.DetailProgressText = "Đã dừng";
                    ProgressUpdated?.Invoke();
                    return;
                }

                await WaitIfPausedAsync(ct).ConfigureAwait(false);

                var chapter = targetChapters[chIdx];
                string chapterDirName = MakeSafeChapterDirName(chapter.Title, chIdx + 1, 60);

                // Loại bỏ folder con thừa 'Full Gallery' / 'Single Chapter' cho các trang doujinshi / gallery 1 chapter
                bool isSingleGallery = targetChapters.Count == 1 &&
                    (string.Equals(chapter.Title, "Full Gallery", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(chapter.Title, "Single Chapter", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(chapter.Title, "Gallery", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(chapter.Title, "Chapter 1", StringComparison.OrdinalIgnoreCase) ||
                     (book.Domain != null && (book.Domain.Contains("hitomi") || book.Domain.Contains("e-hentai") || book.Domain.Contains("exhentai") || book.Domain.Contains("hentaiforce"))));

                string chapterDir;
                if (string.Equals(mode, "Multi-comic", StringComparison.OrdinalIgnoreCase))
                {
                    chapterDir = Path.Combine(serverDir, MakeSafeFilename($"{safeBookName}-{chapterDirName}", 100));
                }
                else
                {
                    chapterDir = isSingleGallery ? bookDir : Path.Combine(bookDir, chapterDirName);
                }

                chapterDir = SafeCreateDirectory(chapterDir);

                book.StatusMessage = $"Đang trích xuất ảnh {chapter.Title}...";
                book.UpdateProgress(completedChapters, totalChapters, 0, 0);
                ProgressUpdated?.Invoke();

                await WaitIfPausedAsync(ct).ConfigureAwait(false);
                var imageUrls = await _scraperService.ExtractChapterImageUrlsAsync(chapter.Url, book.Domain ?? string.Empty, ct).ConfigureAwait(false);
                if (imageUrls.Count == 0)
                {
                    for (int retryExtract = 1; retryExtract <= 2; retryExtract++)
                    {
                        if (ct.IsCancellationRequested) break;
                        await Task.Delay(800 * retryExtract, ct).ConfigureAwait(false);
                        imageUrls = await _scraperService.ExtractChapterImageUrlsAsync(chapter.Url, book.Domain ?? string.Empty, ct).ConfigureAwait(false);
                        if (imageUrls.Count > 0) break;
                    }
                }

                chapter.ImageUrls = imageUrls;
                chapter.TotalPages = imageUrls.Count;

                string manifestFile = Path.Combine(chapterDir, ".manifest.json");
                var manifest = LoadManifest(manifestFile);

                if (imageUrls.Count > 0)
                {
                    using var throttler = new SemaphoreSlim(imageThreads, imageThreads);
                    int downloadedCount = 0;

                    var downloadTasks = imageUrls.Select(async (pageUrl, pIdx) =>
                    {
                        if (ct.IsCancellationRequested) return;

                        await throttler.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            if (ct.IsCancellationRequested) return;
                            await WaitIfPausedAsync(ct).ConfigureAwait(false);

                            string pageExt = GetSafeImageExtensionFromUrl(pageUrl);
                            string pageFileName = $"{(pIdx + 1):D3}{pageExt}";
                            string pageFilePath = Path.Combine(chapterDir, pageFileName);

                            if (File.Exists(pageFilePath) && new FileInfo(pageFilePath).Length > 1024 && manifest.ContainsKey(pageFileName))
                            {
                                int currentDone = Interlocked.Increment(ref downloadedCount);
                                chapter.DownloadedPages = currentDone;
                                book.UpdateProgress(completedChapters, totalChapters, currentDone, imageUrls.Count);
                                return;
                            }

                            bool downloaded = await DownloadImageWithRetryAsync(pageUrl, pageFilePath, chapter.Url, ct).ConfigureAwait(false);
                            if (downloaded)
                            {
                                lock (manifest)
                                {
                                    manifest[pageFileName] = pageUrl;
                                }
                                int currentDone = Interlocked.Increment(ref downloadedCount);
                                chapter.DownloadedPages = currentDone;
                                book.UpdateProgress(completedChapters, totalChapters, currentDone, imageUrls.Count);
                            }

                            book.StatusMessage = $"Đang tải {chapter.Title}: {downloadedCount}/{imageUrls.Count} trang";
                            CalculateSpeed();
                            BackgroundExecutionService.Instance.ReportProgress("download_queue", "Tải Truyện", $"{book.Title} • {chapter.Title} ({downloadedCount}/{imageUrls.Count})", book.ProgressPercentage, true);
                            ProgressUpdated?.Invoke();
                        }
                        finally
                        {
                            throttler.Release();
                        }
                    });

                    await Task.WhenAll(downloadTasks).ConfigureAwait(false);

                    if (downloadedCount == 0)
                    {
                        chapter.Status = "Error";
                        LogEmitted?.Invoke("ERROR", $"[{book.Title}] Tải ảnh thất bại cho '{chapter.Title}' (0/{imageUrls.Count} trang tải được).");
                    }
                    else
                    {
                        completedChapters++;
                        chapter.Status = "Completed";
                        book.UpdateProgress(completedChapters, totalChapters, imageUrls.Count, imageUrls.Count);
                    }
                }
                else
                {
                    string txtPath = Path.Combine(chapterDir, "chapter_info.txt");
                    if (!File.Exists(txtPath))
                    {
                        await File.WriteAllTextAsync(txtPath, $"Title: {chapter.Title}\nURL: {chapter.Url}\nDate: {DateTime.Now}", ct).ConfigureAwait(false);
                    }
                    chapter.Status = "Error";
                    LogEmitted?.Invoke("WARN", $"[{book.Title}] Không thể bóc tách ảnh cho '{chapter.Title}'.");
                }

                SaveManifest(manifestFile, manifest);
                ProgressUpdated?.Invoke();
            }

            book.Status = "Completed";
            book.StatusMessage = $"Đã tải xong toàn bộ {completedChapters}/{totalChapters} chương";
            book.UpdateProgress(completedChapters, totalChapters, 0, 0);
            book.DetailProgressText = $"Hoàn tất {completedChapters}/{totalChapters} chaps • 100%";
            LogEmitted?.Invoke("SUCCESS", $"Đã hoàn tất truyện: '{book.Title}' ({completedChapters} chaps) -> {bookDir}");
        }
        catch (OperationCanceledException)
        {
            book.Status = "Stopped";
            book.StatusMessage = "Tác vụ tải đã dừng";
            book.DetailProgressText = "Đã dừng";
        }
        catch (Exception ex)
        {
            book.Status = "Error";
            book.StatusMessage = ex.Message;
            book.DetailProgressText = "Lỗi tải";
            LogEmitted?.Invoke("ERROR", $"Lỗi tải '{book.Title}': {ex.Message}");
            SoundNotificationService.Instance.PlaySound(SoundNotificationType.DownloadError);
        }
        finally
        {
            ProgressUpdated?.Invoke();
        }
    }

    private async Task<bool> DownloadImageWithRetryAsync(string imageUrl, string destinationPath, string refererUrl, CancellationToken ct)
    {
        bool isMangaDex = imageUrl.Contains("mangadex.network", StringComparison.OrdinalIgnoreCase) ||
                          imageUrl.Contains("mangadex.org", StringComparison.OrdinalIgnoreCase);

        bool isNettruyen = imageUrl.Contains("kcgsbok.com", StringComparison.OrdinalIgnoreCase) ||
                           imageUrl.Contains("/nettruyen/", StringComparison.OrdinalIgnoreCase) ||
                           (refererUrl != null && refererUrl.Contains("nettruyen", StringComparison.OrdinalIgnoreCase));

        var candidateUrls = new List<string> { imageUrl };
        if (isMangaDex)
        {
            var mdMatch = System.Text.RegularExpressions.Regex.Match(imageUrl, @"/(?<type>data|data-saver)/(?<hash>[a-f0-9]+)/(?<file>[^?#]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mdMatch.Success)
            {
                string mHash = mdMatch.Groups["hash"].Value;
                string mFile = mdMatch.Groups["file"].Value;
                string officialData = $"https://uploads.mangadex.org/data/{mHash}/{mFile}";
                string officialSaver = $"https://uploads.mangadex.org/data-saver/{mHash}/{mFile}";
                if (!candidateUrls.Contains(officialData, StringComparer.OrdinalIgnoreCase)) candidateUrls.Add(officialData);
                if (!candidateUrls.Contains(officialSaver, StringComparer.OrdinalIgnoreCase)) candidateUrls.Add(officialSaver);
            }
        }
        else if (isNettruyen)
        {
            var ntMatch = System.Text.RegularExpressions.Regex.Match(imageUrl, @"^(?<prefix>https?://)image\d*(?<suffix>\.[^/]+/.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (ntMatch.Success)
            {
                string prefix = ntMatch.Groups["prefix"].Value;
                string suffix = ntMatch.Groups["suffix"].Value;

                var candidateServers = new List<string> { "image2", "image1", "image3", "image4", "image" };
                if (!string.IsNullOrEmpty(_lastSuccessfulNettruyenCdnServer))
                {
                    candidateServers.Remove(_lastSuccessfulNettruyenCdnServer);
                    candidateServers.Insert(0, _lastSuccessfulNettruyenCdnServer);
                }

                foreach (var srv in candidateServers)
                {
                    string candidate = $"{prefix}{srv}{suffix}";
                    if (!candidateUrls.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        candidateUrls.Add(candidate);
                    }
                }

                if (!string.IsNullOrEmpty(_lastSuccessfulNettruyenCdnServer))
                {
                    string preferredUrl = $"{prefix}{_lastSuccessfulNettruyenCdnServer}{suffix}";
                    if (candidateUrls.Remove(preferredUrl))
                    {
                        candidateUrls.Insert(0, preferredUrl);
                    }
                }
            }
        }

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (ct.IsCancellationRequested) return false;

            byte[]? data = null;

            foreach (string targetUrl in candidateUrls)
            {
                if (ct.IsCancellationRequested) return false;

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, targetUrl);

                    string? effectiveReferer = refererUrl;
                    if (isMangaDex)
                    {
                        effectiveReferer = "https://mangadex.org/";
                    }
                    else if (isNettruyen)
                    {
                        effectiveReferer = !string.IsNullOrEmpty(refererUrl) ? refererUrl : "https://nettruyenviet10.com/";
                    }
                    else if (targetUrl.Contains("imggo.net", StringComparison.OrdinalIgnoreCase))
                    {
                        effectiveReferer = "https://daomeoden.net/";
                    }
                    else if (targetUrl.Contains("pubtranxzyzz", StringComparison.OrdinalIgnoreCase) ||
                             (refererUrl != null && refererUrl.Contains("sayhentai", StringComparison.OrdinalIgnoreCase)))
                    {
                        effectiveReferer = "https://sayhentai.cx/";
                    }
                    else if (targetUrl.Contains("hentaicdn.com", StringComparison.OrdinalIgnoreCase) || targetUrl.Contains("hentai.direct", StringComparison.OrdinalIgnoreCase))
                    {
                        effectiveReferer = "https://hentai2read.com/";
                    }
                    else if (targetUrl.Contains("vi-hentai", StringComparison.OrdinalIgnoreCase))
                    {
                        effectiveReferer = "https://vi-hentai.pro/";
                    }
                    else if (targetUrl.Contains("gold-usergeneratedcontent.net", StringComparison.OrdinalIgnoreCase) ||
                             targetUrl.Contains("hitomi.la", StringComparison.OrdinalIgnoreCase) ||
                             (refererUrl != null && refererUrl.Contains("hitomi", StringComparison.OrdinalIgnoreCase)))
                    {
                        effectiveReferer = "https://hitomi.la/";
                    }
                    else if (targetUrl.Contains("ehgt.org", StringComparison.OrdinalIgnoreCase) ||
                             targetUrl.Contains("e-hentai.org", StringComparison.OrdinalIgnoreCase) ||
                             targetUrl.Contains("exhentai.org", StringComparison.OrdinalIgnoreCase) ||
                             targetUrl.Contains("hath.network", StringComparison.OrdinalIgnoreCase) ||
                             (refererUrl != null && (refererUrl.Contains("e-hentai") || refererUrl.Contains("exhentai"))))
                    {
                        effectiveReferer = "https://e-hentai.org/";
                        req.Headers.TryAddWithoutValidation("Cookie", "nw=1; nw=always");
                    }
                    else if (targetUrl.Contains("hentaiforce", StringComparison.OrdinalIgnoreCase) ||
                             (refererUrl != null && refererUrl.Contains("hentaiforce", StringComparison.OrdinalIgnoreCase)))
                    {
                        effectiveReferer = "https://hentaiforce.net/";
                    }

                    if (!string.IsNullOrEmpty(effectiveReferer))
                    {
                        req.Headers.Add("Referer", effectiveReferer);
                    }

                    using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (res.IsSuccessStatusCode)
                    {
                        data = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                        if (data.Length > 0)
                        {
                            if (isNettruyen)
                            {
                                var m = System.Text.RegularExpressions.Regex.Match(targetUrl, @"https?://(?<srv>image\d*)\.", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (m.Success)
                                {
                                    _lastSuccessfulNettruyenCdnServer = m.Groups["srv"].Value;
                                }
                            }
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    throw;
                }
                catch
                {
                    // Thử candidate URL tiếp theo
                }
            }

            // Tầng 4 dự phòng cho MangaDex qua DoH + SniFragmentStream + Proxy
            if ((data == null || data.Length == 0) && isMangaDex)
            {
                foreach (string targetUrl in candidateUrls)
                {
                    if (ct.IsCancellationRequested) return false;
                    try
                    {
                        data = await MangaDexNetworkService.Instance.DownloadBytesAsync(targetUrl, "https://mangadex.org/", ct).ConfigureAwait(false);
                        if (data != null && data.Length > 0)
                        {
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (data != null && data.Length > 0)
            {
                try
                {
                    await File.WriteAllBytesAsync(destinationPath, data, ct).ConfigureAwait(false);
                    Interlocked.Add(ref _totalBytesDownloadedInWindow, data.Length);

                    if (AutoSplitLongImages && File.Exists(destinationPath))
                    {
                        try
                        {
                            bool wasSplit = ImageSplitterService.TrySplitImageFile(destinationPath, AutoSplitHeight, AutoSplitQuality);
                            if (wasSplit)
                            {
                                LogEmitted?.Invoke("INFO", $"[Cắt ảnh dài] Đã tự động cắt ảnh '{Path.GetFileName(destinationPath)}' (ngưỡng {AutoSplitHeight}px)");
                            }
                        }
                        catch { }
                    }

                    return true;
                }
                catch (UnauthorizedAccessException uex)
                {
                    LogEmitted?.Invoke("ERROR", $"[Lỗi quyền bộ nhớ] Từ chối truy cập ghi tệp '{Path.GetFileName(destinationPath)}': {uex.Message}");

                    if (OperatingSystem.IsAndroid())
                    {
                        try
                        {
                            string safeAppStorage = GetAppSpecificExternalPath();
                            string safeFallbackPath = destinationPath.Replace("/storage/emulated/0/Download/ComicDownloads", safeAppStorage, StringComparison.OrdinalIgnoreCase);
                            string fallbackDir = Path.GetDirectoryName(safeFallbackPath)!;
                            if (!Directory.Exists(fallbackDir)) Directory.CreateDirectory(fallbackDir);
                            File.WriteAllBytes(safeFallbackPath, data);
                            Interlocked.Add(ref _totalBytesDownloadedInWindow, data.Length);
                            LogEmitted?.Invoke("SUCCESS", $"[Lưu an toàn] Đã chuyển hướng lưu ảnh vào thư mục riêng của app: {Path.GetFileName(safeFallbackPath)}");
                            return true;
                        }
                        catch { }
                    }

                    return false;
                }
                catch { }
            }

            if (attempt == 3)
            {
                LogEmitted?.Invoke("WARN", $"Tải ảnh thất bại sau 3 lần '{imageUrl}'");
            }
            await Task.Delay(400 * attempt, ct).ConfigureAwait(false);
        }

        return false;
    }

    private void CalculateSpeed()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSpeedCheckTime).TotalSeconds;
        if (elapsed >= 1.0)
        {
            long bytes = Interlocked.Exchange(ref _totalBytesDownloadedInWindow, 0);
            double speedKb = (bytes / 1024.0) / elapsed;
            if (speedKb > 1024.0)
            {
                CurrentSpeedText = $"{(speedKb / 1024.0):F1} MB/s";
            }
            else
            {
                CurrentSpeedText = $"{speedKb:F1} KB/s";
            }
            _lastSpeedCheckTime = now;
        }
    }

    private static Dictionary<string, string> LoadManifest(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
            }
        }
        catch {}
        return new Dictionary<string, string>();
    }

    private static void SaveManifest(string path, Dictionary<string, string> manifest)
    {
        try
        {
            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch {}
    }

    public static string MakeSafeFilename(string name, int maxBytes = 80)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unnamed";
        
        // 1. Loại bỏ các ký tự không hợp lệ trên mọi hệ điều hành (Windows, Linux, Android)
        var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
        var sb = new System.Text.StringBuilder();
        foreach (char c in name)
        {
            if (invalidChars.Contains(c) || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|' || c < 32)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }

        string cleaned = System.Text.RegularExpressions.Regex.Replace(sb.ToString().Trim(), @"\s+", " ").Trim('.', ' ', '_');
        if (string.IsNullOrWhiteSpace(cleaned)) return "Comic";

        // 2. Giới hạn độ dài theo byte UTF-8 (Android NAME_MAX = 255 bytes, đặt maxBytes 80 để an toàn cho cả path con)
        byte[] utf8Bytes = System.Text.Encoding.UTF8.GetBytes(cleaned);
        if (utf8Bytes.Length <= maxBytes) return cleaned;

        // Cắt ngắn chuỗi an toàn tại ranh giới ký tự
        int targetBytes = Math.Max(16, maxBytes - 7);
        while (cleaned.Length > 0 && System.Text.Encoding.UTF8.GetByteCount(cleaned) > targetBytes)
        {
            cleaned = cleaned.Substring(0, cleaned.Length - 1);
        }
        cleaned = cleaned.TrimEnd('.', ' ', '_');

        string hash = BitConverter.ToString(System.Security.Cryptography.MD5.HashData(utf8Bytes)).Replace("-", "").Substring(0, 6).ToLowerInvariant();
        return $"{cleaned}_{hash}";
    }

    public static string MakeSafeChapterDirName(string? title, int fallbackIndex, int maxBytes = 60)
    {
        if (string.IsNullOrWhiteSpace(title)) return $"Chapter {fallbackIndex}";

        string t = title.Trim();
        // Nếu title chứa "Chapter 1 - Tên truyện dài dằng dặc", chỉ rút gọn lại Chapter 1
        var matchChapter = System.Text.RegularExpressions.Regex.Match(t, @"^(Chapter|Chương|Chap|Vol|Volume)\s*[\d\.]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (matchChapter.Success && t.Length > 40)
        {
            string prefix = matchChapter.Value.Trim();
            string rest = t.Substring(matchChapter.Length).Trim('-', ' ', ':', '_');
            if (rest.Length > 25)
            {
                rest = rest.Substring(0, 25).Trim();
            }
            t = string.IsNullOrWhiteSpace(rest) ? prefix : $"{prefix} - {rest}";
        }

        return MakeSafeFilename(t, maxBytes);
    }

    public static string SafeCreateDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            return path;
        }
        catch (Exception ex) when (ex is PathTooLongException || ex is IOException)
        {
            try
            {
                string? parent = Path.GetDirectoryName(path);
                string leaf = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(parent))
                {
                    string safeLeaf = MakeSafeFilename(leaf, 40);
                    string fallbackPath = Path.Combine(parent, safeLeaf);
                    if (!Directory.Exists(fallbackPath))
                    {
                        Directory.CreateDirectory(fallbackPath);
                    }
                    return fallbackPath;
                }
            }
            catch { }
            throw;
        }
    }

    public static string GetSafeImageExtensionFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return ".jpg";
        string path = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri != null)
        {
            path = uri.AbsolutePath;
        }
        else
        {
            int queryIndex = url.IndexOf('?');
            if (queryIndex >= 0) path = url.Substring(0, queryIndex);
        }

        string ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
        switch (ext)
        {
            case ".jpg":
            case ".jpeg":
            case ".png":
            case ".gif":
            case ".bmp":
            case ".webp":
            case ".avif":
                return ext;
            default:
                return ".jpg";
        }
    }
}

