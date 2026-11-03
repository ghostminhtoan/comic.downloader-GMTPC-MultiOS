using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ComicDownloaderGMTPC.Models;

namespace ComicDownloaderGMTPC.Services;

public class QueueMarkdownPayload
{
    public string SavedAt { get; set; } = string.Empty;
    public int? ConcurrentComicDownloads { get; set; }
    public int? ImageDownloadThreads { get; set; }
    public int? LinkCpuThreads { get; set; }
    public bool? AutoProcessImages { get; set; }
    public bool? AddOrderPrefixToTitle { get; set; }
    public int? OrderPrefixStartIndex { get; set; }
    public string? EnhanceOutputFormat { get; set; }
    public float? EnhanceContrast { get; set; }
    public float? EnhanceBrightness { get; set; }
    public float? EnhanceSaturation { get; set; }
    public float? EnhanceSharpness { get; set; }
    public int? EnhanceNoiseReduce { get; set; }
    public int? EnhanceQuality { get; set; }
    public List<ComicBookItemSnapshot> Items { get; set; } = new();
}

public class QueueRestoreResult
{
    public List<ComicBookItem> Items { get; set; } = new();
    public string LoadedFromPath { get; set; } = string.Empty;
    public int? ConcurrentComicDownloads { get; set; }
    public int? ImageDownloadThreads { get; set; }
    public int? LinkCpuThreads { get; set; }
    public bool? AutoProcessImages { get; set; }
    public bool? AddOrderPrefixToTitle { get; set; }
    public int? OrderPrefixStartIndex { get; set; }
    public string? EnhanceOutputFormat { get; set; }
    public float? EnhanceContrast { get; set; }
    public float? EnhanceBrightness { get; set; }
    public float? EnhanceSaturation { get; set; }
    public float? EnhanceSharpness { get; set; }
    public int? EnhanceNoiseReduce { get; set; }
    public int? EnhanceQuality { get; set; }
    public bool HasValidStructure { get; set; }
}

public class ComicBookItemSnapshot
{
    public int Index { get; set; }
    public int OriginalIndex { get; set; }
    public bool IsChecked { get; set; } = true;
    public string Title { get; set; } = string.Empty;
    public string RawTitle { get; set; } = string.Empty;
    public bool HasOrderPrefixApplied { get; set; }
    public int AppliedOrderNumber { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public int TotalChapters { get; set; }
    public int DownloadedChapters { get; set; }
    public int TotalPages { get; set; }
    public int DownloadedPages { get; set; }
    public string Status { get; set; } = "Waiting";
    public string StatusMessage { get; set; } = string.Empty;
    public double ProgressPercentage { get; set; }
    public string DetailProgressText { get; set; } = "Sẵn sàng";
    public string ChapterSelectionText { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
    public string? ThumbnailLocalPath { get; set; }
    public string? OriginalCoverLocalPath { get; set; }
    public string PreviewTagsText { get; set; } = string.Empty;
    public string PreviewLanguage { get; set; } = string.Empty;
    public string PreviewType { get; set; } = string.Empty;
    public string PreviewArtist { get; set; } = string.Empty;
    public string PreviewParody { get; set; } = string.Empty;
    public string MissingChapters { get; set; } = string.Empty;
    public string LatestChapter { get; set; } = string.Empty;
    public string PreferredLanguage { get; set; } = "vi";
    public bool IsDuplicate { get; set; }
    public string LocalDirectory { get; set; } = string.Empty;
    public string Subfolder { get; set; } = string.Empty;
    public bool IsSubfolderLocked { get; set; }
    public bool IsParallelSplitParent { get; set; }
    public bool IsParallelSplitChild { get; set; }
    public bool IsParallelSplitCollapsed { get; set; }
    public string ParallelSplitParentUrl { get; set; } = string.Empty;
    public List<ComicBookItemSnapshot> ParallelSplitChildren { get; set; } = new();
    public List<ChapterItemSnapshot> Chapters { get; set; } = new();
}

public class ChapterItemSnapshot
{
    public double ChapterNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Status { get; set; } = "Waiting";
    public int TotalPages { get; set; }
    public int DownloadedPages { get; set; }
}

public class QueueBackupService
{
    private static readonly Lazy<QueueBackupService> _instance = new(() => new QueueBackupService());
    public static QueueBackupService Instance => _instance.Value;

    public const string AutoSaveFileName = "autosave.md";
    public const string AutoSaveMirrorFileName = "autosave mirror.md";
    public const string AutoSaveMirrorUnderscoreFileName = "autosave_mirror.md";
    public const string LegacyBackupFileName = "saved_queue.md";

    private const string QueueStateBeginMarker = "<!-- COMIC_QUEUE_STATE_BEGIN -->";
    private const string QueueStateEndMarker = "<!-- COMIC_QUEUE_STATE_END -->";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private System.Threading.Timer? _debounceTimer;
    private readonly object _timerLock = new();
    private readonly object _writeLock = new();
    private List<ComicBookItemSnapshot> _pendingSnapshotsToSave = new();
    private bool _hasPendingSnapshot;

    public int CurrentConcurrentComicDownloads { get; set; } = 2;
    public int CurrentImageDownloadThreads { get; set; } = 3;
    public int CurrentLinkCpuThreads { get; set; } = OperatingSystem.IsAndroid()
        ? 3
        : Math.Min(4, CpuTopologyHelper.GetMaxLogicalProcessorCount());
    public bool CurrentIsAutoProcessImages { get; set; } = false;
    public bool CurrentIsAddOrderPrefix { get; set; } = false;
    public int CurrentOrderPrefixStartIndex { get; set; } = 1;
    public string CurrentEnhanceOutputFormat { get; set; } = "original";
    public float CurrentEnhanceContrast { get; set; } = 0f;
    public float CurrentEnhanceBrightness { get; set; } = 0f;
    public float CurrentEnhanceSaturation { get; set; } = 100f;
    public float CurrentEnhanceSharpness { get; set; } = 0f;
    public int CurrentEnhanceNoiseReduce { get; set; } = 0;
    public int CurrentEnhanceQuality { get; set; } = 90;

    private static readonly object _migrationLock = new object();
    private static bool _migrationExecuted = false;

    /// <summary>
    /// Di chuyển toàn bộ dữ liệu từ thư mục cũ ComicDownloaderGMTPC sang autosaveComicDownloaderGMTPC trên Android
    /// Đảm bảo không mất mát dữ liệu trước khi app đọc file autosave, bookmarks, history...
    /// </summary>
    public static void EnsureAutoSaveMigration()
    {
        if (_migrationExecuted) return;
        lock (_migrationLock)
        {
            if (_migrationExecuted) return;
            try
            {
                if (OperatingSystem.IsAndroid())
                {
                    string oldDir = "/storage/emulated/0/Download/ComicDownloaderGMTPC";
                    string newDir = "/storage/emulated/0/Download/autosaveComicDownloaderGMTPC";

                    if (Directory.Exists(oldDir))
                    {
                        if (!Directory.Exists(newDir))
                        {
                            Directory.CreateDirectory(newDir);
                        }

                        // 1. Di chuyển toàn bộ file (.md, .json, .txt, ...)
                        var files = Directory.GetFiles(oldDir);
                        foreach (var file in files)
                        {
                            try
                            {
                                string fileName = Path.GetFileName(file);
                                string destPath = Path.Combine(newDir, fileName);

                                if (File.Exists(destPath))
                                {
                                    var srcInfo = new FileInfo(file);
                                    var dstInfo = new FileInfo(destPath);
                                    // Ưu tiên file có dung lượng lớn hơn hoặc thời gian sửa đổi mới hơn
                                    if (srcInfo.Length > dstInfo.Length || srcInfo.LastWriteTimeUtc > dstInfo.LastWriteTimeUtc)
                                    {
                                        File.Copy(file, destPath, overwrite: true);
                                    }
                                    File.Delete(file);
                                }
                                else
                                {
                                    File.Move(file, destPath);
                                }
                            }
                            catch { }
                        }

                        // 2. Di chuyển các thư mục con nếu có
                        var subDirs = Directory.GetDirectories(oldDir);
                        foreach (var subDir in subDirs)
                        {
                            try
                            {
                                string dirName = Path.GetFileName(subDir);
                                string destSubDir = Path.Combine(newDir, dirName);
                                if (!Directory.Exists(destSubDir))
                                {
                                    Directory.Move(subDir, destSubDir);
                                }
                            }
                            catch { }
                        }

                        // 3. Xóa thư mục cũ sau khi đã dọn sạch để người dùng không nhầm lẫn với ComicDownloads
                        try
                        {
                            if (!Directory.EnumerateFileSystemEntries(oldDir).Any())
                            {
                                Directory.Delete(oldDir, recursive: false);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            finally
            {
                _migrationExecuted = true;
            }
        }
    }

    public static string GetAutoSaveDirectory()
    {
        try
        {
            if (OperatingSystem.IsAndroid())
            {
                string publicDownload = "/storage/emulated/0/Download/autosaveComicDownloaderGMTPC";
                try
                {
                    if (DownloadEngineService.CanWriteToDirectory(publicDownload))
                    {
                        EnsureAutoSaveMigration();
                        return publicDownload;
                    }
                }
                catch { }

                // Fallback an toàn tuyệt đối 100% không cần bất kỳ quyền nào (Zero-Permission App Storage)
                try
                {
                    string safeDir = DownloadEngineService.GetAppSpecificExternalPath();
                    string autoSaveSafe = Path.Combine(safeDir, "autosave");
                    if (!Directory.Exists(autoSaveSafe)) Directory.CreateDirectory(autoSaveSafe);
                    return autoSaveSafe;
                }
                catch { }

                try
                {
                    string appDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string androidAutoSave = Path.Combine(appDir, "autosave");
                    if (!Directory.Exists(androidAutoSave)) Directory.CreateDirectory(androidAutoSave);
                    return androidAutoSave;
                }
                catch { }
            }

            string portableDir = GetPortableDirectory();
            string autoSaveDir = Path.Combine(portableDir, "autosave");
            if (!Directory.Exists(autoSaveDir))
            {
                Directory.CreateDirectory(autoSaveDir);
            }

            // Tự động di chuyển các file autosave cũ từ baseDir/autosave sang .portable/autosave nếu có
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string oldAutoSaveDir = Path.Combine(baseDir, "autosave");
                if (Directory.Exists(oldAutoSaveDir) && !string.Equals(oldAutoSaveDir, autoSaveDir, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var file in Directory.GetFiles(oldAutoSaveDir))
                    {
                        string dest = Path.Combine(autoSaveDir, Path.GetFileName(file));
                        if (!File.Exists(dest))
                        {
                            File.Move(file, dest);
                        }
                    }
                    if (!Directory.EnumerateFileSystemEntries(oldAutoSaveDir).Any())
                    {
                        Directory.Delete(oldAutoSaveDir, false);
                    }
                }
            }
            catch { }

            return autoSaveDir;
        }
        catch
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }

    public static string GetPortableDirectory()
    {
        try
        {
            if (OperatingSystem.IsAndroid())
            {
                return GetAutoSaveDirectory();
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string portableDir = Path.Combine(baseDir, ".portable");
            if (!Directory.Exists(portableDir))
            {
                Directory.CreateDirectory(portableDir);
            }
            return portableDir;
        }
        catch
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }

    public string GetDefaultBackupFilePath()
    {
        return Path.Combine(GetAutoSaveDirectory(), AutoSaveFileName);
    }

    public string GetMirrorBackupFilePath()
    {
        return Path.Combine(GetAutoSaveDirectory(), AutoSaveMirrorFileName);
    }

    /// <summary>
    /// Kích hoạt lưu trạng thái ngầm có debounce (mặc định 500ms) ra autosave.md và autosave mirror.md.
    /// </summary>
    public void TriggerDebouncedAutoSave(IEnumerable<ComicBookItem> items, int delayMs = 500)
    {
        if (items == null) return;

        List<ComicBookItemSnapshot> snapshots;
        try
        {
            snapshots = CaptureRootSnapshots(items);
        }
        catch
        {
            return;
        }

        lock (_timerLock)
        {
            _pendingSnapshotsToSave = snapshots;
            _hasPendingSnapshot = true;

            if (_debounceTimer == null)
            {
                _debounceTimer = new System.Threading.Timer(_ => FlushPendingAutoSave(), null, Math.Max(50, delayMs), Timeout.Infinite);
            }
            else
            {
                _debounceTimer.Change(Math.Max(50, delayMs), Timeout.Infinite);
            }
        }
    }

    /// <summary>
    /// Ghi ngay lập tức trạng thái hiện tại ra autosave.md và autosave mirror.md (dùng khi đóng app / thoát / dừng tải).
    /// </summary>
    public void FlushAutoSaveImmediately(IEnumerable<ComicBookItem>? items = null)
    {
        try
        {
            List<ComicBookItemSnapshot>? snapshots = null;
            if (items != null)
            {
                snapshots = CaptureRootSnapshots(items);
            }
            else
            {
                lock (_timerLock)
                {
                    if (_hasPendingSnapshot)
                    {
                        snapshots = new List<ComicBookItemSnapshot>(_pendingSnapshotsToSave);
                        _hasPendingSnapshot = false;
                    }
                }
            }

            if (snapshots != null)
            {
                WriteAutoSaveSnapshotsSync(snapshots);
            }
        }
        catch
        {
            // An toàn khi thoát ứng dụng
        }
    }

    private void FlushPendingAutoSave()
    {
        List<ComicBookItemSnapshot>? toSave = null;
        lock (_timerLock)
        {
            if (_hasPendingSnapshot)
            {
                toSave = new List<ComicBookItemSnapshot>(_pendingSnapshotsToSave);
                _hasPendingSnapshot = false;
            }
        }

        if (toSave != null)
        {
            try
            {
                WriteAutoSaveSnapshotsSync(toSave);
            }
            catch { }
        }
    }

    public Task SaveAutoBackupSnapshotAsync(IEnumerable<ComicBookItem> items)
    {
        var snapshots = CaptureRootSnapshots(items);
        return Task.Run(() => WriteAutoSaveSnapshotsSync(snapshots));
    }

    private void WriteAutoSaveSnapshotsSync(List<ComicBookItemSnapshot> snapshots)
    {
        string content = BuildQueueMarkdownFromSnapshots(snapshots);
        lock (_writeLock)
        {
            string autoSaveDir = GetAutoSaveDirectory();
            WritePrimaryAndMirrorFilesSync(autoSaveDir, content);

            // Chỉ lưu duy nhất vào thư mục autosave chuẩn (.portable\autosave)
            if (!OperatingSystem.IsAndroid())
            {
                try
                {
                    string portableDir = GetPortableDirectory();
                    if (!string.Equals(autoSaveDir, portableDir, StringComparison.OrdinalIgnoreCase))
                    {
                        string legacyAutoSave = Path.Combine(portableDir, AutoSaveFileName);
                        string legacyMirror = Path.Combine(portableDir, AutoSaveMirrorFileName);
                        if (File.Exists(legacyAutoSave)) File.Delete(legacyAutoSave);
                        if (File.Exists(legacyMirror)) File.Delete(legacyMirror);
                    }
                }
                catch { }
            }
        }
    }

    private static void WritePrimaryAndMirrorFilesSync(string directory, string content)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string primaryPath = Path.Combine(directory, AutoSaveFileName);
            string mirrorSpacePath = Path.Combine(directory, AutoSaveMirrorFileName);
            string mirrorUnderscorePath = Path.Combine(directory, AutoSaveMirrorUnderscoreFileName);

            // Nếu file chính đang tồn tại và hợp lệ, sao lưu sang mirror trước khi ghi đè file chính (chống mất dữ liệu khi sập nguồn/crash giữa chừng)
            if (File.Exists(primaryPath))
            {
                try
                {
                    var fi = new FileInfo(primaryPath);
                    if (fi.Length > 64)
                    {
                        File.Copy(primaryPath, mirrorSpacePath, overwrite: true);
                        File.Copy(primaryPath, mirrorUnderscorePath, overwrite: true);
                    }
                }
                catch { }
            }

            WriteFileAtomicallySync(primaryPath, content);
            WriteFileAtomicallySync(mirrorSpacePath, content);
            WriteFileAtomicallySync(mirrorUnderscorePath, content);
        }
        catch { }
    }

    private static void WriteFileAtomicallySync(string targetPath, string content)
    {
        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = targetPath + ".tmp";
        File.WriteAllText(tempPath, content, Encoding.UTF8);
        File.Move(tempPath, targetPath, overwrite: true);
    }

    /// <summary>
    /// Xuất danh sách Queue và toàn bộ trạng thái Chapter/Trang ảnh ra chuỗi Markdown (.md) kèm khối JSON Full State.
    /// </summary>
    public string BuildQueueMarkdownContent(IEnumerable<ComicBookItem> items)
    {
        var snapshots = CaptureRootSnapshots(items);
        return BuildQueueMarkdownFromSnapshots(snapshots);
    }

    private static List<ComicBookItemSnapshot> CaptureRootSnapshots(IEnumerable<ComicBookItem> items)
    {
        var list = items?.Where(b => b != null).ToList() ?? new List<ComicBookItem>();
        var result = new List<ComicBookItemSnapshot>();
        int idx = 1;

        foreach (var item in list)
        {
            if (item.IsParallelSplitChild) continue;
            result.Add(CreateSnapshot(item, idx++));
        }

        return result;
    }

    private static ComicBookItemSnapshot CreateSnapshot(ComicBookItem item, int index)
    {
        var snap = new ComicBookItemSnapshot
        {
            Index = index,
            OriginalIndex = item.OriginalIndex > 0 ? item.OriginalIndex : index,
            IsChecked = item.IsChecked,
            Title = item.Title ?? string.Empty,
            RawTitle = item.RawTitle ?? string.Empty,
            HasOrderPrefixApplied = item.HasOrderPrefixApplied,
            AppliedOrderNumber = item.AppliedOrderNumber,
            Url = item.Url ?? string.Empty,
            Domain = item.Domain ?? string.Empty,
            TotalChapters = item.TotalChapters,
            DownloadedChapters = item.DownloadedChapters,
            TotalPages = item.TotalPages,
            DownloadedPages = item.DownloadedPages,
            Status = item.Status ?? "Waiting",
            StatusMessage = item.StatusMessage ?? string.Empty,
            ProgressPercentage = Math.Round(item.ProgressPercentage, 1),
            DetailProgressText = item.DetailProgressText ?? "Sẵn sàng",
            ChapterSelectionText = item.ChapterSelectionText ?? string.Empty,
            CoverUrl = item.CoverUrl ?? string.Empty,
            ThumbnailLocalPath = item.ThumbnailLocalPath,
            OriginalCoverLocalPath = item.OriginalCoverLocalPath,
            PreviewTagsText = item.PreviewTagsText ?? string.Empty,
            PreviewLanguage = item.PreviewLanguage ?? string.Empty,
            PreviewType = item.PreviewType ?? string.Empty,
            PreviewArtist = item.PreviewArtist ?? string.Empty,
            PreviewParody = item.PreviewParody ?? string.Empty,
            MissingChapters = item.MissingChapters ?? string.Empty,
            LatestChapter = item.LatestChapter ?? string.Empty,
            PreferredLanguage = item.PreferredLanguage ?? "vi",
            IsDuplicate = item.IsDuplicate,
            LocalDirectory = item.LocalDirectory ?? string.Empty,
            Subfolder = item.Subfolder ?? string.Empty,
            IsSubfolderLocked = item.IsSubfolderLocked,
            IsParallelSplitParent = item.IsParallelSplitParent,
            IsParallelSplitChild = item.IsParallelSplitChild,
            IsParallelSplitCollapsed = item.IsParallelSplitCollapsed,
            ParallelSplitParentUrl = item.ParallelSplitParentUrl ?? string.Empty
        };

        if (item.ParallelSplitChildren != null && item.ParallelSplitChildren.Count > 0)
        {
            int childIdx = 1;
            foreach (var child in item.ParallelSplitChildren.Where(c => c != null))
            {
                snap.ParallelSplitChildren.Add(CreateSnapshot(child, childIdx++));
            }
        }

        if (item.Chapters != null && item.Chapters.Count > 0)
        {
            foreach (var ch in item.Chapters)
            {
                if (ch == null) continue;
                snap.Chapters.Add(new ChapterItemSnapshot
                {
                    ChapterNumber = ch.ChapterNumber,
                    Title = ch.Title ?? string.Empty,
                    Url = ch.Url ?? string.Empty,
                    Status = ch.Status ?? "Waiting",
                    TotalPages = ch.TotalPages,
                    DownloadedPages = ch.DownloadedPages
                });
            }
        }

        return snap;
    }

    private string BuildQueueMarkdownFromSnapshots(List<ComicBookItemSnapshot> snapshots)
    {
        int concurrentComics = Math.Clamp(CurrentConcurrentComicDownloads, 1, 16);
        int imageConnections = Math.Clamp(CurrentImageDownloadThreads, 1, 16);
        int maxAllowedCpu = OperatingSystem.IsAndroid() ? 6 : CpuTopologyHelper.GetMaxLogicalProcessorCount();
        int linkCpuThreads = Math.Clamp(CurrentLinkCpuThreads, 1, maxAllowedCpu);

        var sb = new StringBuilder();
        sb.AppendLine("# COMIC DOWNLOADER GMTPC - BACKUP QUEUE");
        sb.AppendLine($"> Thời gian sao lưu: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | Hệ điều hành: {Environment.OSVersion.Platform}");
        string autoProcText = CurrentIsAutoProcessImages ? "BẬT" : "TẮT";
        string prefixText = CurrentIsAddOrderPrefix ? $"BẬT (từ {CurrentOrderPrefixStartIndex})" : "TẮT";
        sb.AppendLine($"> Cấu hình tải: Số truyện cùng lúc: {concurrentComics} | Connection tải ảnh: {imageConnections} | Số luồng CPU: {linkCpuThreads} | Xử lý ảnh: {autoProcText} | Đánh STT: {prefixText}");
        sb.AppendLine();
        sb.AppendLine("| STT | Tên Truyện | Nguồn / URL | Trạng Thái | Tiến Độ | Chương Đang Tải | Trang Đang Tải | Chọn Chap | Subfolder | Thư Mục Lưu |");
        sb.AppendLine("| :---: | :--- | :--- | :---: | :---: | :--- | :--- | :--- | :--- | :--- |");

        int index = 1;
        foreach (var item in snapshots)
        {
            if (item == null || item.IsParallelSplitChild) continue;

            string safeTitle = EscapeMarkdown(item.Title);
            string safeUrl = (item.Url ?? string.Empty).Replace("|", "\\|");
            string status = item.Status ?? "Waiting";
            string progress = string.Format(CultureInfo.InvariantCulture, "{0:F1}%", item.ProgressPercentage);
            string currentChapter = EscapeMarkdown(!string.IsNullOrWhiteSpace(item.StatusMessage) ? item.StatusMessage : "-");
            string currentPage = EscapeMarkdown(!string.IsNullOrWhiteSpace(item.DetailProgressText) ? item.DetailProgressText : "-");
            string chapterSelect = EscapeMarkdown(item.ChapterSelectionText ?? "");
            string rawSub = item.Subfolder ?? "";
            string subfolderText = item.IsSubfolderLocked ? $"🔒 {rawSub}".Trim() : rawSub;
            string subfolder = EscapeMarkdown(subfolderText);
            string localDir = EscapeMarkdown(item.LocalDirectory ?? "");

            sb.AppendLine($"| {index} | {safeTitle} | {safeUrl} | {status} | {progress} | {currentChapter} | {currentPage} | {chapterSelect} | {subfolder} | {localDir} |");
            index++;
        }

        var payload = new QueueMarkdownPayload
        {
            SavedAt = DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
            ConcurrentComicDownloads = concurrentComics,
            ImageDownloadThreads = imageConnections,
            LinkCpuThreads = linkCpuThreads,
            AutoProcessImages = CurrentIsAutoProcessImages,
            AddOrderPrefixToTitle = CurrentIsAddOrderPrefix,
            OrderPrefixStartIndex = CurrentOrderPrefixStartIndex,
            EnhanceOutputFormat = CurrentEnhanceOutputFormat,
            EnhanceContrast = CurrentEnhanceContrast,
            EnhanceBrightness = CurrentEnhanceBrightness,
            EnhanceSaturation = CurrentEnhanceSaturation,
            EnhanceSharpness = CurrentEnhanceSharpness,
            EnhanceNoiseReduce = CurrentEnhanceNoiseReduce,
            EnhanceQuality = CurrentEnhanceQuality,
            Items = snapshots
        };

        string json = JsonSerializer.Serialize(payload, _jsonOptions);
        sb.AppendLine();
        sb.AppendLine("## Full Queue State Snapshot");
        sb.AppendLine(QueueStateBeginMarker);
        sb.AppendLine("```json");
        sb.AppendLine(json);
        sb.AppendLine("```");
        sb.AppendLine(QueueStateEndMarker);

        return sb.ToString();
    }

    /// <summary>
    /// Xuất danh sách Queue và toàn bộ trạng thái Chapter/Trang ảnh ra file Markdown (.md).
    /// </summary>
    public async Task<string> SaveQueueToMarkdownAsync(IEnumerable<ComicBookItem> items, string? customPath = null)
    {
        string targetPath = string.IsNullOrWhiteSpace(customPath) ? GetDefaultBackupFilePath() : customPath;
        string content = BuildQueueMarkdownContent(items);

        await Task.Run(() =>
        {
            lock (_writeLock)
            {
                WriteFileAtomicallySync(targetPath, content);
            }
        }).ConfigureAwait(false);

        return targetPath;
    }

    /// <summary>
    /// Tự động đọc và phục hồi danh sách Queue + Tiến độ + Cấu hình tải (Số truyện cùng lúc, Số connection, Số luồng CPU) từ autosave.md hoặc autosave mirror.md.
    /// </summary>
    public async Task<QueueRestoreResult> LoadAutoSaveQueueResultAsync()
    {
        return await Task.Run(() =>
        {
            var candidatePaths = GetAutoSaveCandidatePaths();

            QueueRestoreResult? bestEmptyResult = null;
            foreach (var path in candidatePaths)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    string content = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    var parsedResult = ParseQueueWithMetadataFromMarkdownContent(content);
                    parsedResult.LoadedFromPath = path;

                    if (parsedResult.Items.Count > 0)
                    {
                        if (bestEmptyResult != null)
                        {
                            parsedResult.ConcurrentComicDownloads ??= bestEmptyResult.ConcurrentComicDownloads;
                            parsedResult.ImageDownloadThreads ??= bestEmptyResult.ImageDownloadThreads;
                            parsedResult.LinkCpuThreads ??= bestEmptyResult.LinkCpuThreads;
                        }
                        return parsedResult;
                    }
                    if (parsedResult.HasValidStructure && bestEmptyResult == null)
                    {
                        bestEmptyResult = parsedResult;
                    }
                }
                catch
                {
                    // Nếu file chính lỗi/corrupt do crash, tự động chuyển sang đọc file mirror tiếp theo
                }
            }

            return bestEmptyResult ?? new QueueRestoreResult();
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Tự động đọc và phục hồi danh sách Queue + Tiến độ từ autosave.md hoặc file dự phòng autosave mirror.md khi mở app / sau khi crash.
    /// </summary>
    public async Task<(List<ComicBookItem> Items, string LoadedFromPath)> LoadAutoSaveQueueWithSourceAsync()
    {
        var result = await LoadAutoSaveQueueResultAsync().ConfigureAwait(false);
        return (result.Items, result.LoadedFromPath);
    }

    private static List<string> GetAutoSaveCandidatePaths()
    {
        var dirs = new List<string>();
        string autoDir = GetAutoSaveDirectory();
        if (!string.IsNullOrWhiteSpace(autoDir)) dirs.Add(autoDir);

        if (!OperatingSystem.IsAndroid())
        {
            string portableDir = GetPortableDirectory();
            if (!string.IsNullOrWhiteSpace(portableDir) && !dirs.Contains(portableDir, StringComparer.OrdinalIgnoreCase))
            {
                dirs.Add(portableDir);
            }
        }

        string[] fileNames =
        {
            AutoSaveFileName,
            AutoSaveMirrorFileName,
            AutoSaveMirrorUnderscoreFileName,
            "autosave miiror.md",
            "autosave.mirror.md",
            LegacyBackupFileName
        };

        var allCandidates = new List<string>();
        foreach (var dir in dirs)
        {
            foreach (var name in fileNames)
            {
                allCandidates.Add(Path.Combine(dir, name));
            }
        }

        // Ưu tiên nhóm autosave mới nhất theo thời gian ghi thực tế trên đĩa, giữ thứ tự primary -> mirror
        return allCandidates
            .Where(File.Exists)
            .OrderByDescending(p =>
            {
                string fileName = Path.GetFileName(p);
                bool isLegacy = string.Equals(fileName, LegacyBackupFileName, StringComparison.OrdinalIgnoreCase);
                return isLegacy ? 0 : 1;
            })
            .ThenByDescending(p =>
            {
                try { return File.GetLastWriteTimeUtc(p).Ticks / TimeSpan.FromSeconds(2).Ticks; }
                catch { return 0L; }
            })
            .ToList();
    }

    /// <summary>
    /// Đọc và phục hồi danh sách Queue kèm cấu hình tải từ file Markdown (.md).
    /// </summary>
    public async Task<QueueRestoreResult> LoadQueueResultFromMarkdownAsync(string? customPath = null)
    {
        if (string.IsNullOrWhiteSpace(customPath))
        {
            return await LoadAutoSaveQueueResultAsync().ConfigureAwait(false);
        }

        if (!File.Exists(customPath)) return new QueueRestoreResult { LoadedFromPath = customPath };
        string content = await File.ReadAllTextAsync(customPath, Encoding.UTF8).ConfigureAwait(false);
        var result = ParseQueueWithMetadataFromMarkdownContent(content);
        result.LoadedFromPath = customPath;
        return result;
    }

    /// <summary>
    /// Đọc và phục hồi danh sách Queue từ file Markdown (.md).
    /// </summary>
    public async Task<List<ComicBookItem>> LoadQueueFromMarkdownAsync(string? customPath = null)
    {
        var res = await LoadQueueResultFromMarkdownAsync(customPath).ConfigureAwait(false);
        return res.Items;
    }

    public List<ComicBookItem> ParseQueueFromMarkdownContent(string content)
    {
        return ParseQueueFromMarkdownContent(content, out _);
    }

    public List<ComicBookItem> ParseQueueFromMarkdownContent(string content, out bool hadValidStructure)
    {
        var res = ParseQueueWithMetadataFromMarkdownContent(content);
        hadValidStructure = res.HasValidStructure;
        return res.Items;
    }

    private static readonly Regex _headerConcurrentRegex = new(@"Số\s+truyện\s+cùng\s+lúc\s*:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _headerConnectionRegex = new(@"Connection\s+tải\s+ảnh\s*:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _headerCpuThreadsRegex = new(@"Số\s+luồng\s+CPU\s*:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public QueueRestoreResult ParseQueueWithMetadataFromMarkdownContent(string content, string? loadedFromPath = null)
    {
        var restoreResult = new QueueRestoreResult { LoadedFromPath = loadedFromPath ?? string.Empty };
        if (string.IsNullOrWhiteSpace(content)) return restoreResult;

        // Đọc cấu hình tải từ dòng header Markdown (dự phòng khi thiếu JSON hoặc chỉnh sửa trực tiếp trên Markdown)
        var mConc = _headerConcurrentRegex.Match(content);
        if (mConc.Success && int.TryParse(mConc.Groups[1].Value, out int parsedConc))
        {
            restoreResult.ConcurrentComicDownloads = parsedConc;
            restoreResult.HasValidStructure = true;
        }

        var mConn = _headerConnectionRegex.Match(content);
        if (mConn.Success && int.TryParse(mConn.Groups[1].Value, out int parsedConn))
        {
            restoreResult.ImageDownloadThreads = parsedConn;
            restoreResult.HasValidStructure = true;
        }

        var mCpu = _headerCpuThreadsRegex.Match(content);
        if (mCpu.Success && int.TryParse(mCpu.Groups[1].Value, out int parsedCpu))
        {
            restoreResult.LinkCpuThreads = parsedCpu;
            restoreResult.HasValidStructure = true;
        }

        var mAutoProc = System.Text.RegularExpressions.Regex.Match(content, @"Xử lý ảnh:\s*(BẬT|TẮT|ON|OFF)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (mAutoProc.Success)
        {
            string val = mAutoProc.Groups[1].Value.ToUpperInvariant();
            restoreResult.AutoProcessImages = val == "BẬT" || val == "ON";
            restoreResult.HasValidStructure = true;
        }

        var mPrefix = System.Text.RegularExpressions.Regex.Match(content, @"Đánh STT:\s*(?:BẬT\s*(?:\(từ\s*(\d+)\))?|TẮT|ON|OFF)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (mPrefix.Success)
        {
            string raw = mPrefix.Value.ToUpperInvariant();
            restoreResult.AddOrderPrefixToTitle = raw.Contains("BẬT") || raw.Contains("ON");
            if (mPrefix.Groups[1].Success && int.TryParse(mPrefix.Groups[1].Value, out int sttFrom))
            {
                restoreResult.OrderPrefixStartIndex = sttFrom;
            }
            restoreResult.HasValidStructure = true;
        }

        // 1. Ưu tiên đọc từ khối JSON Full Queue State Snapshot
        var payload = TryParseJsonPayload(content);
        if (payload != null)
        {
            restoreResult.HasValidStructure = true;
            if (payload.ConcurrentComicDownloads.HasValue && payload.ConcurrentComicDownloads.Value > 0)
            {
                restoreResult.ConcurrentComicDownloads = payload.ConcurrentComicDownloads.Value;
            }
            if (payload.ImageDownloadThreads.HasValue && payload.ImageDownloadThreads.Value > 0)
            {
                restoreResult.ImageDownloadThreads = payload.ImageDownloadThreads.Value;
            }
            if (payload.LinkCpuThreads.HasValue && payload.LinkCpuThreads.Value > 0)
            {
                restoreResult.LinkCpuThreads = payload.LinkCpuThreads.Value;
            }
            if (payload.AutoProcessImages.HasValue)
            {
                restoreResult.AutoProcessImages = payload.AutoProcessImages.Value;
            }
            if (payload.AddOrderPrefixToTitle.HasValue)
            {
                restoreResult.AddOrderPrefixToTitle = payload.AddOrderPrefixToTitle.Value;
            }
            if (payload.OrderPrefixStartIndex.HasValue && payload.OrderPrefixStartIndex.Value > 0)
            {
                restoreResult.OrderPrefixStartIndex = payload.OrderPrefixStartIndex.Value;
            }
            if (!string.IsNullOrWhiteSpace(payload.EnhanceOutputFormat))
            {
                restoreResult.EnhanceOutputFormat = payload.EnhanceOutputFormat;
            }
            if (payload.EnhanceContrast.HasValue) restoreResult.EnhanceContrast = payload.EnhanceContrast.Value;
            if (payload.EnhanceBrightness.HasValue) restoreResult.EnhanceBrightness = payload.EnhanceBrightness.Value;
            if (payload.EnhanceSaturation.HasValue) restoreResult.EnhanceSaturation = payload.EnhanceSaturation.Value;
            if (payload.EnhanceSharpness.HasValue) restoreResult.EnhanceSharpness = payload.EnhanceSharpness.Value;
            if (payload.EnhanceNoiseReduce.HasValue) restoreResult.EnhanceNoiseReduce = payload.EnhanceNoiseReduce.Value;
            if (payload.EnhanceQuality.HasValue) restoreResult.EnhanceQuality = payload.EnhanceQuality.Value;

            if (payload.Items != null)
            {
                int idx = 1;
                foreach (var snap in payload.Items)
                {
                    if (snap == null || string.IsNullOrWhiteSpace(snap.Url)) continue;
                    var item = RestoreItemFromSnapshot(snap, idx++);
                    restoreResult.Items.Add(item);

                    if (item.HasParallelSplitChildren && !item.IsParallelSplitCollapsed)
                    {
                        foreach (var child in item.ParallelSplitChildren)
                        {
                            child.Index = idx++;
                            restoreResult.Items.Add(child);
                        }
                    }
                }
                return restoreResult;
            }
        }

        // 2. Fallback đọc từ bảng Markdown (hỗ trợ cả định dạng 10 cột, 9 cột và bảng WPF 4-5 cột)
        string[] lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var tableRowRegex10 = new Regex(@"^\|\s*(\d+)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|$", RegexOptions.Compiled);
        var tableRowRegex9 = new Regex(@"^\|\s*(\d+)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|$", RegexOptions.Compiled);

        int rowIdx = 1;
        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("# COMIC DOWNLOADER GMTPC", StringComparison.OrdinalIgnoreCase))
            {
                restoreResult.HasValidStructure = true;
            }

            if (!trimmed.StartsWith("|") || trimmed.Contains("---") || trimmed.StartsWith("| STT", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("| No.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match10 = tableRowRegex10.Match(trimmed);
            if (match10.Success)
            {
                restoreResult.HasValidStructure = true;
                string title = UnescapeMarkdown(match10.Groups[2].Value.Trim());
                string url = match10.Groups[3].Value.Trim().Replace("\\|", "|");
                string status = match10.Groups[4].Value.Trim();
                string progressStr = match10.Groups[5].Value.Trim().Replace("%", "").Replace(",", ".");
                string currentChapter = UnescapeMarkdown(match10.Groups[6].Value.Trim());
                string currentPage = UnescapeMarkdown(match10.Groups[7].Value.Trim());
                string chapterSelect = UnescapeMarkdown(match10.Groups[8].Value.Trim());
                string subfolderRaw = UnescapeMarkdown(match10.Groups[9].Value.Trim());
                string localDir = UnescapeMarkdown(match10.Groups[10].Value.Trim());

                if (string.IsNullOrWhiteSpace(url)) continue;

                double.TryParse(progressStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double progressVal);
                ParseSubfolderLock(subfolderRaw, out string cleanSubfolder, out bool isLocked);

                var item = CreateItemFromTableRow(rowIdx++, title, url, status, progressVal, currentChapter, currentPage, chapterSelect, cleanSubfolder, isLocked, localDir);
                restoreResult.Items.Add(item);
                continue;
            }

            var match9 = tableRowRegex9.Match(trimmed);
            if (match9.Success)
            {
                restoreResult.HasValidStructure = true;
                string title = UnescapeMarkdown(match9.Groups[2].Value.Trim());
                string url = match9.Groups[3].Value.Trim().Replace("\\|", "|");
                string status = match9.Groups[4].Value.Trim();
                string progressStr = match9.Groups[5].Value.Trim().Replace("%", "").Replace(",", ".");
                string currentChapter = UnescapeMarkdown(match9.Groups[6].Value.Trim());
                string currentPage = UnescapeMarkdown(match9.Groups[7].Value.Trim());
                string chapterSelect = UnescapeMarkdown(match9.Groups[8].Value.Trim());
                string localDir = UnescapeMarkdown(match9.Groups[9].Value.Trim());

                if (string.IsNullOrWhiteSpace(url)) continue;

                double.TryParse(progressStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double progressVal);
                var item = CreateItemFromTableRow(rowIdx++, title, url, status, progressVal, currentChapter, currentPage, chapterSelect, string.Empty, false, localDir);
                restoreResult.Items.Add(item);
            }
        }

        return restoreResult;
    }

    private static QueueMarkdownPayload? TryParseJsonPayload(string content)
    {
        int beginIdx = content.IndexOf(QueueStateBeginMarker, StringComparison.Ordinal);
        if (beginIdx < 0) return null;

        int endIdx = content.IndexOf(QueueStateEndMarker, beginIdx, StringComparison.Ordinal);
        if (endIdx < 0) return null;

        int fenceStart = content.IndexOf("```json", beginIdx, StringComparison.OrdinalIgnoreCase);
        if (fenceStart < 0 || fenceStart > endIdx) return null;

        int lineEnd = content.IndexOf('\n', fenceStart);
        if (lineEnd < 0 || lineEnd > endIdx) return null;

        int jsonStart = lineEnd + 1;
        int fenceEnd = content.IndexOf("```", jsonStart, StringComparison.Ordinal);
        if (fenceEnd < 0 || fenceEnd > endIdx) return null;

        string json = content.Substring(jsonStart, fenceEnd - jsonStart).Trim();
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<QueueMarkdownPayload>(json, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static ComicBookItem RestoreItemFromSnapshot(ComicBookItemSnapshot snap, int index)
    {
        bool isCompleted = string.Equals(snap.Status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(snap.Status, "Hoàn tất", StringComparison.OrdinalIgnoreCase) ||
                           snap.ProgressPercentage >= 100.0;

        string restoredStatus;
        if (isCompleted)
        {
            restoredStatus = "Completed";
        }
        else if (string.Equals(snap.Status, "Error", StringComparison.OrdinalIgnoreCase))
        {
            restoredStatus = "Error";
        }
        else if (string.Equals(snap.Status, "Stopped", StringComparison.OrdinalIgnoreCase))
        {
            restoredStatus = "Stopped";
        }
        else if (string.Equals(snap.Status, "Paused", StringComparison.OrdinalIgnoreCase))
        {
            restoredStatus = "Paused";
        }
        else
        {
            // Nếu trước khi tắt app / crash đang ở trạng thái Downloading hoặc Waiting thì chuyển về Waiting để sẵn sàng tải tiếp
            restoredStatus = "Waiting";
        }

        double restoredProgress = isCompleted ? 100.0 : Math.Clamp(snap.ProgressPercentage, 0.0, 100.0);
        string restoredDetail = !string.IsNullOrWhiteSpace(snap.DetailProgressText)
            ? snap.DetailProgressText
            : (restoredProgress > 0 ? $"{restoredProgress:0.0}%" : "Sẵn sàng");

        string restoredStatusMsg = snap.StatusMessage ?? string.Empty;
        if (string.Equals(snap.Status, "Downloading", StringComparison.OrdinalIgnoreCase) && restoredProgress > 0 && !isCompleted)
        {
            restoredStatusMsg = $"Chờ tải tiếp ({restoredDetail})";
        }
        else if (string.IsNullOrWhiteSpace(restoredStatusMsg))
        {
            restoredStatusMsg = isCompleted ? "Đã tải xong toàn bộ" : "Sẵn sàng";
        }

        var item = new ComicBookItem
        {
            Index = index,
            OriginalIndex = snap.OriginalIndex > 0 ? snap.OriginalIndex : index,
            IsChecked = snap.IsChecked,
            Title = string.IsNullOrWhiteSpace(snap.Title) ? "Truyện phục hồi" : snap.Title,
            RawTitle = snap.RawTitle ?? string.Empty,
            HasOrderPrefixApplied = snap.HasOrderPrefixApplied,
            AppliedOrderNumber = snap.AppliedOrderNumber,
            Url = snap.Url,
            Domain = !string.IsNullOrWhiteSpace(snap.Domain) ? snap.Domain : DomainRoutingService.DetectDomain(snap.Url),
            TotalChapters = snap.TotalChapters,
            DownloadedChapters = snap.DownloadedChapters,
            TotalPages = snap.TotalPages,
            DownloadedPages = snap.DownloadedPages,
            Status = restoredStatus,
            StatusMessage = restoredStatusMsg,
            ProgressPercentage = restoredProgress,
            DetailProgressText = restoredDetail,
            ChapterSelectionText = snap.ChapterSelectionText ?? string.Empty,
            CoverUrl = snap.CoverUrl ?? string.Empty,
            ThumbnailLocalPath = snap.ThumbnailLocalPath,
            OriginalCoverLocalPath = snap.OriginalCoverLocalPath,
            PreviewTagsText = snap.PreviewTagsText ?? string.Empty,
            PreviewLanguage = snap.PreviewLanguage ?? string.Empty,
            PreviewType = snap.PreviewType ?? string.Empty,
            PreviewArtist = snap.PreviewArtist ?? string.Empty,
            PreviewParody = snap.PreviewParody ?? string.Empty,
            MissingChapters = snap.MissingChapters ?? string.Empty,
            LatestChapter = snap.LatestChapter ?? string.Empty,
            PreferredLanguage = !string.IsNullOrWhiteSpace(snap.PreferredLanguage) ? snap.PreferredLanguage : "vi",
            IsDuplicate = snap.IsDuplicate,
            LocalDirectory = snap.LocalDirectory ?? string.Empty,
            Subfolder = snap.Subfolder ?? string.Empty,
            IsSubfolderLocked = snap.IsSubfolderLocked,
            IsParallelSplitParent = snap.IsParallelSplitParent,
            IsParallelSplitChild = snap.IsParallelSplitChild,
            IsParallelSplitCollapsed = snap.IsParallelSplitCollapsed,
            ParallelSplitParentUrl = snap.ParallelSplitParentUrl ?? string.Empty
        };

        if (snap.Chapters != null && snap.Chapters.Count > 0)
        {
            foreach (var chSnap in snap.Chapters)
            {
                if (chSnap == null) continue;
                item.Chapters.Add(new ChapterItem
                {
                    ChapterNumber = chSnap.ChapterNumber,
                    Title = chSnap.Title ?? string.Empty,
                    Url = chSnap.Url ?? string.Empty,
                    Status = chSnap.Status ?? "Waiting",
                    TotalPages = chSnap.TotalPages,
                    DownloadedPages = chSnap.DownloadedPages
                });
            }
        }

        if (snap.ParallelSplitChildren != null && snap.ParallelSplitChildren.Count > 0)
        {
            int childIdx = 1;
            foreach (var childSnap in snap.ParallelSplitChildren)
            {
                if (childSnap == null) continue;
                var childItem = RestoreItemFromSnapshot(childSnap, childIdx++);
                childItem.IsParallelSplitChild = true;
                childItem.IsParallelSplitParent = false;
                if (string.IsNullOrWhiteSpace(childItem.ParallelSplitParentUrl))
                {
                    childItem.ParallelSplitParentUrl = item.Url;
                }
                item.ParallelSplitChildren.Add(childItem);
            }
            item.IsParallelSplitParent = true;
            item.NotifySplitHierarchyChanged();
        }

        return item;
    }

    private static void ParseSubfolderLock(string subfolderRaw, out string cleanSubfolder, out bool isLocked)
    {
        isLocked = false;
        cleanSubfolder = subfolderRaw ?? string.Empty;
        if (cleanSubfolder.StartsWith("🔒"))
        {
            isLocked = true;
            cleanSubfolder = cleanSubfolder.Substring("🔒".Length).Trim();
        }
        else if (cleanSubfolder.StartsWith("[locked]", StringComparison.OrdinalIgnoreCase))
        {
            isLocked = true;
            cleanSubfolder = cleanSubfolder.Substring("[locked]".Length).Trim();
        }
    }

    private static readonly Regex _chapterPageDetailRegex = new(
        @"Chương\s+(\d+)\s*/\s*(\d+)(?:\s*\(\s*Trang\s+(\d+)\s*/\s*(\d+)\s*\))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static ComicBookItem CreateItemFromTableRow(
        int index,
        string title,
        string url,
        string status,
        double progressVal,
        string currentChapter,
        string currentPage,
        string chapterSelect,
        string cleanSubfolder,
        bool isLocked,
        string localDir)
    {
        bool isCompleted = string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(status, "Hoàn tất", StringComparison.OrdinalIgnoreCase) ||
                           progressVal >= 100.0;

        int dlChaps = 0, totalChaps = 0, dlPages = 0, totalPages = 0;
        var detailMatch = _chapterPageDetailRegex.Match(currentPage ?? string.Empty);
        if (detailMatch.Success)
        {
            int.TryParse(detailMatch.Groups[1].Value, out dlChaps);
            int.TryParse(detailMatch.Groups[2].Value, out totalChaps);
            if (detailMatch.Groups[3].Success) int.TryParse(detailMatch.Groups[3].Value, out dlPages);
            if (detailMatch.Groups[4].Success) int.TryParse(detailMatch.Groups[4].Value, out totalPages);
        }

        return new ComicBookItem
        {
            Index = index,
            Title = string.IsNullOrWhiteSpace(title) ? "Truyện phục hồi" : title,
            Url = url,
            Domain = DomainRoutingService.DetectDomain(url),
            Status = isCompleted ? "Completed" : (status == "Error" || status == "Stopped" || status == "Paused" ? status : "Waiting"),
            StatusMessage = currentChapter != "-" && !string.IsNullOrWhiteSpace(currentChapter) ? currentChapter : "Sẵn sàng",
            DetailProgressText = currentPage != "-" && !string.IsNullOrWhiteSpace(currentPage) ? currentPage : (progressVal > 0 ? $"{progressVal:0.0}%" : "Sẵn sàng"),
            ProgressPercentage = isCompleted ? 100.0 : Math.Clamp(progressVal, 0.0, 100.0),
            DownloadedChapters = dlChaps,
            TotalChapters = totalChaps,
            DownloadedPages = dlPages,
            TotalPages = totalPages,
            ChapterSelectionText = chapterSelect,
            Subfolder = cleanSubfolder,
            IsSubfolderLocked = isLocked,
            LocalDirectory = localDir,
            IsChecked = !isCompleted
        };
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


