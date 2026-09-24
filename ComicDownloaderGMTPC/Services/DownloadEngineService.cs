using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        DownloadRoot = Path.Combine(appDir, "Downloads");
        try
        {
            if (!Directory.Exists(DownloadRoot)) Directory.CreateDirectory(DownloadRoot);
        }
        catch
        {
            DownloadRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ComicDownloads");
        }
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

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = true });
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

        string safeBookName = MakeSafeFilename(book.Title);
        string bookDir = Path.Combine(DownloadRoot, safeBookName);
        book.LocalDirectory = bookDir;

        try
        {
            if (!Directory.Exists(bookDir)) Directory.CreateDirectory(bookDir);

            // If chapters not extracted yet, try extraction
            if (book.Chapters.Count == 0)
            {
                var refreshed = await _scraperService.ScrapeBookAsync(book.Url, book.Index, ct).ConfigureAwait(false);
                book.Chapters = refreshed.Chapters;
                book.TotalChapters = refreshed.TotalChapters;
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
                    for (int pIdx = 0; pIdx < imageUrls.Count; pIdx++)
                    {
                        if (ct.IsCancellationRequested) break;

                        string pageUrl = imageUrls[pIdx];
                        string pageFileName = $"{(pIdx + 1):D3}.jpg";
                        string pageFilePath = Path.Combine(chapterDir, pageFileName);

                        // Resume check: if page exists and recorded in manifest, skip
                        if (File.Exists(pageFilePath) && new FileInfo(pageFilePath).Length > 1024 && manifest.ContainsKey(pageFileName))
                        {
                            chapter.DownloadedPages = pIdx + 1;
                            continue;
                        }

                        bool downloaded = await DownloadImageWithRetryAsync(pageUrl, pageFilePath, chapter.Url, ct).ConfigureAwait(false);
                        if (downloaded)
                        {
                            manifest[pageFileName] = pageUrl;
                            chapter.DownloadedPages = pIdx + 1;
                        }

                        book.StatusMessage = $"Đang tải {chapter.Title}: trang {pIdx + 1}/{imageUrls.Count}";
                        CalculateSpeed();
                        ProgressUpdated?.Invoke();
                    }
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

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, imageUrl);
                if (!string.IsNullOrEmpty(refererUrl))
                {
                    req.Headers.Add("Referer", refererUrl);
                }

                using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    await Task.Delay(300 * attempt, ct).ConfigureAwait(false);
                    continue;
                }

                byte[] data = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
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
            catch
            {
                await Task.Delay(400 * attempt, ct).ConfigureAwait(false);
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

    private string MakeSafeFilename(string filename)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            filename = filename.Replace(c, '_');
        }
        return filename.Trim();
    }
}
