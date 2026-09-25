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
    private long _totalBytesDownloadedInWindow;
    private DateTime _lastSpeedCheckTime = DateTime.UtcNow;

    public bool IsDownloading => _isDownloading;
    public string DownloadRoot { get; set; }
    public string CurrentSpeedText { get; private set; } = "0.0 KB/s";

    public event Action<string, string>? LogEmitted;
    public event Action? ProgressUpdated;
    public event Action<string>? AndroidOpenFolderRequested;
    public static event Action? OpenStorageSettingsRequested;

    public void RequestOpenStorageSettings() => OpenStorageSettingsRequested?.Invoke();

    public DownloadEngineService()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");

        DownloadRoot = InitDownloadRoot();
    }

    private string InitDownloadRoot()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string configFilePath = Path.Combine(appDir, "download_path.cfg");

        // 1. Kiểm tra nếu người dùng đã tùy chọn thư mục trước đó
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
                        // Cập nhật lại config nếu trước đó bị lưu đường dẫn ảo SAF như /tree/downloads
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

        // 2. Đường dẫn mặc định theo OS
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

            // Kiểm tra xem có phải là đường dẫn ảo SAF (Storage Access Framework) không
            bool isVirtualSaf = decoded.StartsWith("/tree/", StringComparison.OrdinalIgnoreCase) ||
                                decoded.StartsWith("tree/", StringComparison.OrdinalIgnoreCase) ||
                                decoded.StartsWith("content:", StringComparison.OrdinalIgnoreCase) ||
                                (!decoded.StartsWith("/storage/", StringComparison.OrdinalIgnoreCase) &&
                                 !decoded.StartsWith("/sdcard/", StringComparison.OrdinalIgnoreCase) &&
                                 !decoded.StartsWith("/data/", StringComparison.OrdinalIgnoreCase));

            if (isVirtualSaf)
            {
                string searchStr = (decoded + " " + (folderName ?? "")).ToLowerInvariant();

                if (searchStr.Contains("download"))
                {
                    return "/storage/emulated/0/Download/ComicDownloads";
                }
                if (searchStr.Contains("document"))
                {
                    return "/storage/emulated/0/Documents/ComicDownloads";
                }
                if (searchStr.Contains("picture"))
                {
                    return "/storage/emulated/0/Pictures/ComicDownloads";
                }

                // Nếu có định dạng SAF colon như "primary:MyFolder"
                int colonIdx = decoded.LastIndexOf(':');
                if (colonIdx >= 0 && colonIdx < decoded.Length - 1)
                {
                    string sub = decoded.Substring(colonIdx + 1).Trim().Trim('/');
                    if (!string.IsNullOrWhiteSpace(sub) && 
                        !string.Equals(sub, "primary", StringComparison.OrdinalIgnoreCase) && 
                        !string.Equals(sub, "0", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"/storage/emulated/0/{sub}";
                    }
                }

                if (!string.IsNullOrWhiteSpace(folderName) && 
                    !string.Equals(folderName, "primary", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(folderName, "0", StringComparison.OrdinalIgnoreCase))
                {
                    return $"/storage/emulated/0/Download/{folderName}";
                }

                return GetDefaultAndroidDownloadPath();
            }

            // Loại bỏ khoảng trắng hoặc path rỗng ở root dẫn đến thư mục (blank)
            if (path == "/storage/emulated/0" || path == "/storage/emulated/0/" || path == "/sdcard" || path == "/sdcard/")
            {
                return "/storage/emulated/0/Download/ComicDownloads";
            }
        }

        return path;
    }

    public static string GetDefaultAndroidDownloadPath()
    {
        // 1. Thư mục công khai Download
        string pub = "/storage/emulated/0/Download/ComicDownloads";
        if (CanWriteToDirectory(pub)) return pub;

        // 2. Thư mục App-Specific External (Đảm bảo 100% quyền ghi trên Android 11-14 không cần quyền đặc biệt)
        string appExt = GetAppSpecificExternalPath();
        if (CanWriteToDirectory(appExt)) return appExt;

        // 3. Fallback Documents
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

        // 1. Nếu candidateRoot ghi được tốt
        if (CanWriteToDirectory(candidateRoot))
        {
            return candidateRoot;
        }

        // 2. Thử Download công khai
        string pubDownload = "/storage/emulated/0/Download/ComicDownloads";
        if (CanWriteToDirectory(pubDownload))
        {
            SetDownloadRoot(pubDownload);
            return pubDownload;
        }

        // 3. Tự động chuyển sang App-Specific External Files (100% quyền ghi không bị Access Denied)
        string appStorage = GetAppSpecificExternalPath();
        if (CanWriteToDirectory(appStorage))
        {
            SetDownloadRoot(appStorage);
            LogEmitted?.Invoke("WARN", $"[Bộ nhớ Android] Thư mục ngoài bị chặn Access Denied. Đã tự động kích hoạt thư mục an toàn:\n{appStorage}\nToàn bộ ảnh sẽ được tải đầy đủ vào đây!");
            return appStorage;
        }

        // 4. Fallback MyDocuments
        string docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ComicDownloads");
        try { if (!Directory.Exists(docs)) Directory.CreateDirectory(docs); } catch {}
        SetDownloadRoot(docs);
        return docs;
    }

    public async Task StartDownloadAsync(IEnumerable<ComicBookItem> items, string mode, CancellationToken externalCt = default)
    {
        if (_isDownloading) return;

        _isDownloading = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _cts.Token;
        _lastSpeedCheckTime = DateTime.UtcNow;
        _totalBytesDownloadedInWindow = 0;

        LogEmitted?.Invoke("INFO", $"Bắt đầu tải danh sách truyện (Thư mục: {mode}) tới: {DownloadRoot}");

        try
        {
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                if (!item.IsChecked) continue;

                await DownloadComicBookAsync(item, mode, ct).ConfigureAwait(false);
            }

            LogEmitted?.Invoke("SUCCESS", "Hoàn tất toàn bộ tác vụ tải trong hàng chờ.");
        }
        catch (OperationCanceledException)
        {
            LogEmitted?.Invoke("WARN", "Tiến trình tải đã được người dùng dừng lại.");
        }
        catch (Exception ex)
        {
            LogEmitted?.Invoke("ERROR", $"Tiến trình tải gặp lỗi: {ex.Message}");
        }
        finally
        {
            _isDownloading = false;
            CurrentSpeedText = "0.0 KB/s";
            ProgressUpdated?.Invoke();
        }
    }

    public void Stop()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            LogEmitted?.Invoke("WARN", "Đang yêu cầu dừng tất cả tác vụ tải...");
        }
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
                // Kích hoạt sự kiện để UI copy clipboard và mở native viewer
                AndroidOpenFolderRequested?.Invoke(path);
            }
        }
        catch (Exception ex)
        {
            LogEmitted?.Invoke("WARN", $"Không thể mở thư mục '{path}': {ex.Message}");
        }
    }

    private async Task DownloadComicBookAsync(ComicBookItem book, string mode, CancellationToken ct)
    {
        book.Status = "Downloading";
        book.StatusMessage = "Đang khởi tạo thư mục và nạp danh sách chương...";
        ProgressUpdated?.Invoke();

        string effectiveRoot = EnsureWritableDownloadRoot(DownloadRoot);
        string safeBookName = MakeSafeFilename(book.Title);
        string bookDir = Path.Combine(effectiveRoot, safeBookName);
        book.LocalDirectory = bookDir;

        try
        {
            if (!Directory.Exists(bookDir))
            {
                Directory.CreateDirectory(bookDir);
            }

            // If chapters not extracted yet, try extraction
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
                if (string.IsNullOrWhiteSpace(book.StatusMessage) || book.StatusMessage.Contains("0 chương") || book.StatusMessage.Contains("Extracting"))
                {
                    book.StatusMessage = "Không có chương nào để tải (Hoặc lỗi kết nối/bị nhà mạng chặn TLS). Vui lòng thử bật 1.1.1.1/WARP.";
                }
                LogEmitted?.Invoke("ERROR", $"Truyện '{book.Title}' không thể tải vì 0 chương: {book.StatusMessage}");
                ProgressUpdated?.Invoke();
                return;
            }

            int totalChapters = Math.Max(1, book.TotalChapters);
            int completedChapters = 0;

            for (int chIdx = 0; chIdx < book.Chapters.Count; chIdx++)
            {
                if (ct.IsCancellationRequested)
                {
                    book.Status = "Stopped";
                    book.StatusMessage = "Đã dừng bởi người dùng";
                    ProgressUpdated?.Invoke();
                    return;
                }

                var chapter = book.Chapters[chIdx];
                string chapterDirName = MakeSafeFilename(string.IsNullOrWhiteSpace(chapter.Title) ? $"Chapter {chIdx + 1}" : chapter.Title);
                string chapterDir = Path.Combine(bookDir, chapterDirName);
                if (!Directory.Exists(chapterDir)) Directory.CreateDirectory(chapterDir);

                book.StatusMessage = $"Đang trích xuất ảnh {chapter.Title}...";
                ProgressUpdated?.Invoke();

                // 1. Scrape real image URLs for chapter
                var imageUrls = await _scraperService.ExtractChapterImageUrlsAsync(chapter.Url, book.Domain, ct).ConfigureAwait(false);
                chapter.ImageUrls = imageUrls;
                chapter.TotalPages = imageUrls.Count;

                string manifestFile = Path.Combine(chapterDir, ".manifest.json");
                var manifest = LoadManifest(manifestFile);

                if (imageUrls.Count > 0)
                {
                    // Tải song song 4 ảnh cùng lúc với SemaphoreSlim giúp tốc độ tăng gấp 4-5 lần
                    using var throttler = new SemaphoreSlim(4, 4);
                    int downloadedCount = 0;

                    var downloadTasks = imageUrls.Select(async (pageUrl, pIdx) =>
                    {
                        if (ct.IsCancellationRequested) return;

                        await throttler.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            if (ct.IsCancellationRequested) return;

                            string pageFileName = $"{(pIdx + 1):D3}.jpg";
                            string pageFilePath = Path.Combine(chapterDir, pageFileName);

                            // Resume check: if page exists and recorded in manifest, skip
                            if (File.Exists(pageFilePath) && new FileInfo(pageFilePath).Length > 1024 && manifest.ContainsKey(pageFileName))
                            {
                                int currentDone = Interlocked.Increment(ref downloadedCount);
                                chapter.DownloadedPages = currentDone;
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
                            }

                            book.StatusMessage = $"Đang tải {chapter.Title}: {downloadedCount}/{imageUrls.Count} trang";
                            CalculateSpeed();
                            ProgressUpdated?.Invoke();
                        }
                        finally
                        {
                            throttler.Release();
                        }
                    });

                    await Task.WhenAll(downloadTasks).ConfigureAwait(false);
                }
                else
                {
                    // Fallback for text/novel chapter: save placeholder text file
                    string txtPath = Path.Combine(chapterDir, "chapter_info.txt");
                    if (!File.Exists(txtPath))
                    {
                        await File.WriteAllTextAsync(txtPath, $"Title: {chapter.Title}\nURL: {chapter.Url}\nDate: {DateTime.Now}", ct).ConfigureAwait(false);
                    }
                }

                SaveManifest(manifestFile, manifest);

                completedChapters++;
                book.DownloadedChapters = completedChapters;
                book.ProgressPercentage = Math.Round((double)completedChapters / totalChapters * 100, 1);
                chapter.Status = "Completed";
                ProgressUpdated?.Invoke();
            }

            book.Status = "Completed";
            book.StatusMessage = $"Đã tải xong toàn bộ {completedChapters} chương";
            LogEmitted?.Invoke("SUCCESS", $"Đã hoàn tất truyện: '{book.Title}' ({completedChapters} chaps) -> {bookDir}");
        }
        catch (OperationCanceledException)
        {
            book.Status = "Stopped";
            book.StatusMessage = "Tác vụ tải đã dừng";
        }
        catch (Exception ex)
        {
            book.Status = "Error";
            book.StatusMessage = ex.Message;
            LogEmitted?.Invoke("ERROR", $"Lỗi tải '{book.Title}': {ex.Message}");
        }
        finally
        {
            ProgressUpdated?.Invoke();
        }
    }

    private async Task<bool> DownloadImageWithRetryAsync(string imageUrl, string destinationPath, string refererUrl, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (ct.IsCancellationRequested) return false;

            byte[]? data = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, imageUrl);

                string effectiveReferer = refererUrl;
                if (imageUrl.Contains("imggo.net", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveReferer = "https://daomeoden.net/";
                }
                else if (imageUrl.Contains("pubtranxzyzz", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveReferer = "https://sayhentai.cx/";
                }
                else if (imageUrl.Contains("hentaicdn.com", StringComparison.OrdinalIgnoreCase) || imageUrl.Contains("hentai.direct", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveReferer = "https://hentai2read.com/";
                }
                else if (imageUrl.Contains("vi-hentai", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveReferer = "https://vi-hentai.pro/";
                }

                if (!string.IsNullOrEmpty(effectiveReferer))
                {
                    req.Headers.Add("Referer", effectiveReferer);
                }

                using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    await Task.Delay(300 * attempt, ct).ConfigureAwait(false);
                    continue;
                }

                data = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (data.Length > 0)
                {
                    await File.WriteAllBytesAsync(destinationPath, data, ct).ConfigureAwait(false);
                    Interlocked.Add(ref _totalBytesDownloadedInWindow, data.Length);
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (UnauthorizedAccessException uex)
            {
                LogEmitted?.Invoke("ERROR", $"[Lỗi quyền bộ nhớ] Từ chối truy cập ghi tệp '{Path.GetFileName(destinationPath)}': {uex.Message}");

                if (OperatingSystem.IsAndroid() && data != null && data.Length > 0)
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
                    catch {}
                }
                return false;
            }
            catch (Exception ex)
            {
                LogEmitted?.Invoke("WARN", $"Lỗi tải/ghi ảnh '{Path.GetFileName(destinationPath)}' (thử lần {attempt}/3): {ex.Message}");
                await Task.Delay(150 * attempt, ct).ConfigureAwait(false);
            }
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
            double speedBps = bytes / elapsed;
            _lastSpeedCheckTime = now;

            if (speedBps > 1024 * 1024)
            {
                CurrentSpeedText = $"{(speedBps / (1024 * 1024)):F1} MB/s";
            }
            else
            {
                CurrentSpeedText = $"{(speedBps / 1024):F1} KB/s";
            }
        }
    }

    private Dictionary<string, string> LoadManifest(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
        }
        catch {}
        return new();
    }

    private void SaveManifest(string path, Dictionary<string, string> manifest)
    {
        try
        {
            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch {}
    }

    private static readonly char[] IncompatibleChars = new[] { ':', '*', '?', '"', '<', '>', '|', '\\', '/', '\0' };

    public static string MakeSafeFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return "Comic";
        foreach (char c in IncompatibleChars)
        {
            filename = filename.Replace(c, '_');
        }
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            filename = filename.Replace(c, '_');
        }
        foreach (char c in Path.GetInvalidPathChars())
        {
            filename = filename.Replace(c, '_');
        }
        return filename.Trim().Trim('.', ' ', '_');
    }
}
