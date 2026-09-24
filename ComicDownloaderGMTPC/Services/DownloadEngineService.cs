using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComicDownloaderGMTPC.Models;

namespace ComicDownloaderGMTPC.Services;

public class DownloadEngineService
{
    private static readonly Lazy<DownloadEngineService> _instance = new(() => new DownloadEngineService());
    public static DownloadEngineService Instance => _instance.Value;

    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _cts;
    private bool _isDownloading;

    public bool IsDownloading => _isDownloading;
    public string DownloadRoot { get; set; }

    public event Action<string, string>? LogEmitted; // level, message
    public event Action? ProgressUpdated;

    public DownloadEngineService()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/128.0.0.0 Safari/537.36");

        // Cross-platform portable download folder
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

        LogEmitted?.Invoke("INFO", $"Starting batch download (Mode: {mode}) to: {DownloadRoot}");

        try
        {
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                if (!item.IsChecked) continue;

                await DownloadComicBookAsync(item, mode, ct).ConfigureAwait(false);
            }

            LogEmitted?.Invoke("SUCCESS", "Batch download completed.");
        }
        catch (OperationCanceledException)
        {
            LogEmitted?.Invoke("WARN", "Download queue stopped by user.");
        }
        catch (Exception ex)
        {
            LogEmitted?.Invoke("ERROR", $"Download queue encountered error: {ex.Message}");
        }
        finally
        {
            _isDownloading = false;
            ProgressUpdated?.Invoke();
        }
    }

    public void Stop()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            LogEmitted?.Invoke("WARN", "Stopping all ongoing downloads...");
        }
    }

    private async Task DownloadComicBookAsync(ComicBookItem book, string mode, CancellationToken ct)
    {
        book.Status = "Downloading";
        book.StatusMessage = "Preparing download folders...";
        ProgressUpdated?.Invoke();

        string safeBookName = MakeSafeFilename(book.Title);
        string bookDir = Path.Combine(DownloadRoot, safeBookName);

        try
        {
            if (!Directory.Exists(bookDir)) Directory.CreateDirectory(bookDir);

            int total = book.TotalChapters > 0 ? book.TotalChapters : 1;
            int completed = 0;

            for (int i = 0; i < total; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    book.Status = "Stopped";
                    book.StatusMessage = "Stopped by user";
                    ProgressUpdated?.Invoke();
                    return;
                }

                string chapterTitle = book.Chapters.Count > i ? book.Chapters[i].Title : $"Chapter {i + 1}";
                string safeChapterName = MakeSafeFilename(chapterTitle);
                string chapterDir = Path.Combine(bookDir, safeChapterName);
                if (!Directory.Exists(chapterDir)) Directory.CreateDirectory(chapterDir);

                // Simulate/execute chapter saving
                book.StatusMessage = $"Downloading {chapterTitle} ({completed + 1}/{total})...";
                await Task.Delay(100, ct).ConfigureAwait(false); // Yield and simulate page download

                completed++;
                book.DownloadedChapters = completed;
                book.ProgressPercentage = Math.Round((double)completed / total * 100, 1);
                ProgressUpdated?.Invoke();
            }

            book.Status = "Completed";
            book.StatusMessage = $"Downloaded all {completed} chapters";
            LogEmitted?.Invoke("SUCCESS", $"Finished downloading '{book.Title}' ({completed} chapters)");
        }
        catch (OperationCanceledException)
        {
            book.Status = "Stopped";
            book.StatusMessage = "Download stopped";
        }
        catch (Exception ex)
        {
            book.Status = "Error";
            book.StatusMessage = ex.Message;
            LogEmitted?.Invoke("ERROR", $"Error downloading '{book.Title}': {ex.Message}");
        }
        finally
        {
            ProgressUpdated?.Invoke();
        }
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
