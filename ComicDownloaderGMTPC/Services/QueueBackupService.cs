using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ComicDownloaderGMTPC.Models;

namespace ComicDownloaderGMTPC.Services;

public class QueueBackupService
{
    private static readonly Lazy<QueueBackupService> _instance = new(() => new QueueBackupService());
    public static QueueBackupService Instance => _instance.Value;

    private System.Threading.Timer? _debounceTimer;
    private readonly object _timerLock = new();
    private List<ComicBookItem> _pendingItemsToSave = new();

    public string GetDefaultBackupFilePath()
    {
        try
        {
            if (OperatingSystem.IsAndroid())
            {
                string publicDownload = "/storage/emulated/0/Download/ComicDownloaderGMTPC";
                if (Directory.Exists("/storage/emulated/0/Download"))
                {
                    if (!Directory.Exists(publicDownload)) Directory.CreateDirectory(publicDownload);
                    return Path.Combine(publicDownload, "saved_queue.md");
                }
                string appDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(appDir, "saved_queue.md");
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string portableDir = Path.Combine(baseDir, ".portable");
            if (!Directory.Exists(portableDir))
            {
                Directory.CreateDirectory(portableDir);
            }
            return Path.Combine(portableDir, "saved_queue.md");
        }
        catch
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "saved_queue.md");
        }
    }

    /// <summary>
    /// Kích hoạt lưu trạng thái ngầm có debounce (3 giây) - Gom hàng chục sự kiện thành 1 lần ghi duy nhất chống nghẽn đĩa.
    /// </summary>
    public void TriggerDebouncedAutoSave(IEnumerable<ComicBookItem> items)
    {
        lock (_timerLock)
        {
            // Snapshot danh sách hiện tại
            _pendingItemsToSave = items.Where(b => !b.IsParallelSplitChild).ToList();

            _debounceTimer?.Dispose();
            _debounceTimer = new System.Threading.Timer(async _ =>
            {
                List<ComicBookItem> toSave;
                lock (_timerLock)
                {
                    toSave = new List<ComicBookItem>(_pendingItemsToSave);
                }

                if (toSave.Count > 0)
                {
                    try
                    {
                        await SaveQueueToMarkdownAsync(toSave).ConfigureAwait(false);
                    }
                    catch { }
                }
            }, null, 3000, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Xuất danh sách Queue và toàn bộ trạng thái Chapter/Trang ảnh ra file Markdown (.md).
    /// </summary>
    public async Task<string> SaveQueueToMarkdownAsync(IEnumerable<ComicBookItem> items, string? customPath = null)
    {
        string targetPath = string.IsNullOrWhiteSpace(customPath) ? GetDefaultBackupFilePath() : customPath;
        string dir = Path.GetDirectoryName(targetPath) ?? string.Empty;
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var sb = new StringBuilder();
        sb.AppendLine("# COMIC DOWNLOADER GMTPC - BACKUP QUEUE");
        sb.AppendLine($"> Thời gian sao lưu: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | Hệ điều hành: {Environment.OSVersion.Platform}");
        sb.AppendLine();
        sb.AppendLine("| STT | Tên Truyện | Nguồn / URL | Trạng Thái | Tiến Độ | Chương Đang Tải | Trang Đang Tải | Chọn Chap | Subfolder | Thư Mục Lưu |");
        sb.AppendLine("| :---: | :--- | :--- | :---: | :---: | :--- | :---: | :--- | :--- | :--- |");

        int index = 1;
        foreach (var item in items)
        {
            if (item == null || item.IsParallelSplitChild) continue;

            string safeTitle = EscapeMarkdown(item.Title);
            string safeUrl = item.Url.Replace("|", "\\|");
            string status = item.Status ?? "Waiting";
            string progress = $"{item.ProgressPercentage:F1}%";
            string currentChapter = EscapeMarkdown(!string.IsNullOrWhiteSpace(item.StatusMessage) ? item.StatusMessage : "-");
            string currentPage = EscapeMarkdown(!string.IsNullOrWhiteSpace(item.DetailProgressText) ? item.DetailProgressText : "-");
            string chapterSelect = EscapeMarkdown(item.ChapterSelectionText ?? "");
            string subfolder = EscapeMarkdown(item.Subfolder ?? "");
            string localDir = EscapeMarkdown(item.LocalDirectory ?? "");

            sb.AppendLine($"| {index} | {safeTitle} | {safeUrl} | {status} | {progress} | {currentChapter} | {currentPage} | {chapterSelect} | {subfolder} | {localDir} |");
            index++;
        }

        string tempPath = targetPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, sb.ToString(), Encoding.UTF8).ConfigureAwait(false);

        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }
        File.Move(tempPath, targetPath);

        return targetPath;
    }

    /// <summary>
    /// Đọc và phục hồi danh sách Queue từ file Markdown (.md).
    /// </summary>
    public async Task<List<ComicBookItem>> LoadQueueFromMarkdownAsync(string? customPath = null)
    {
        string targetPath = string.IsNullOrWhiteSpace(customPath) ? GetDefaultBackupFilePath() : customPath;
        var result = new List<ComicBookItem>();

        if (!File.Exists(targetPath)) return result;

        string[] lines = await File.ReadAllLinesAsync(targetPath, Encoding.UTF8).ConfigureAwait(false);
        var tableRowRegex10 = new Regex(@"^\|\s*(\d+)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|$", RegexOptions.Compiled);
        var tableRowRegex9 = new Regex(@"^\|\s*(\d+)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|$", RegexOptions.Compiled);

        int idx = 1;
        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("|") || trimmed.Contains("---") || trimmed.StartsWith("| STT"))
            {
                continue;
            }

            var match10 = tableRowRegex10.Match(trimmed);
            if (match10.Success)
            {
                string title = UnescapeMarkdown(match10.Groups[2].Value.Trim());
                string url = match10.Groups[3].Value.Trim().Replace("\\|", "|");
                string status = match10.Groups[4].Value.Trim();
                string progressStr = match10.Groups[5].Value.Trim().Replace("%", "");
                string currentChapter = UnescapeMarkdown(match10.Groups[6].Value.Trim());
                string currentPage = UnescapeMarkdown(match10.Groups[7].Value.Trim());
                string chapterSelect = UnescapeMarkdown(match10.Groups[8].Value.Trim());
                string subfolder = UnescapeMarkdown(match10.Groups[9].Value.Trim());
                string localDir = UnescapeMarkdown(match10.Groups[10].Value.Trim());

                if (string.IsNullOrWhiteSpace(url)) continue;

                double.TryParse(progressStr, out double progressVal);

                var item = new ComicBookItem
                {
                    Index = idx++,
                    Title = string.IsNullOrWhiteSpace(title) ? "Truyện phục hồi" : title,
                    Url = url,
                    Domain = DomainRoutingService.DetectDomain(url),
                    Status = status == "Completed" ? "Completed" : "Waiting",
                    StatusMessage = currentChapter != "-" ? currentChapter : "Sẵn sàng",
                    DetailProgressText = currentPage != "-" ? currentPage : "0%",
                    ProgressPercentage = progressVal,
                    ChapterSelectionText = chapterSelect,
                    Subfolder = subfolder,
                    LocalDirectory = localDir,
                    IsChecked = status != "Completed"
                };

                result.Add(item);
                continue;
            }

            var match9 = tableRowRegex9.Match(trimmed);
            if (match9.Success)
            {
                string title = UnescapeMarkdown(match9.Groups[2].Value.Trim());
                string url = match9.Groups[3].Value.Trim().Replace("\\|", "|");
                string status = match9.Groups[4].Value.Trim();
                string progressStr = match9.Groups[5].Value.Trim().Replace("%", "");
                string currentChapter = UnescapeMarkdown(match9.Groups[6].Value.Trim());
                string currentPage = UnescapeMarkdown(match9.Groups[7].Value.Trim());
                string chapterSelect = UnescapeMarkdown(match9.Groups[8].Value.Trim());
                string localDir = UnescapeMarkdown(match9.Groups[9].Value.Trim());

                if (string.IsNullOrWhiteSpace(url)) continue;

                double.TryParse(progressStr, out double progressVal);

                var item = new ComicBookItem
                {
                    Index = idx++,
                    Title = string.IsNullOrWhiteSpace(title) ? "Truyện phục hồi" : title,
                    Url = url,
                    Domain = DomainRoutingService.DetectDomain(url),
                    Status = status == "Completed" ? "Completed" : "Waiting",
                    StatusMessage = currentChapter != "-" ? currentChapter : "Sẵn sàng",
                    DetailProgressText = currentPage != "-" ? currentPage : "0%",
                    ProgressPercentage = progressVal,
                    ChapterSelectionText = chapterSelect,
                    Subfolder = string.Empty,
                    LocalDirectory = localDir,
                    IsChecked = status != "Completed"
                };

                result.Add(item);
            }
        }

        return result;
    }


    private static string EscapeMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static string UnescapeMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Replace("\\|", "|").Trim();
    }
}

