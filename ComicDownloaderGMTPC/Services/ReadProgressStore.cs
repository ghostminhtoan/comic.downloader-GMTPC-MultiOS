// ====================================================================================================
// FILE SUMMARY: ReadProgressStore.cs
// ----------------------------------------------------------------------------------------------------
// VAI TRÒ & MỤC ĐÍCH:
//   Dịch vụ Lưu Trữ & Quản Lý Tiến Độ Đọc Dở, Dấu Trang & Lịch Sử (Reading Progress Store).
//
// CÁC TÍNH NĂNG & THÀNH PHẦN CHÍNH:
//   - Quản lý tệp tiến độ resume.md chuẩn Markdown và chỉ mục JSON tốc độ cao.
//   - Ghi nhận chính xác vị trí chapter và số trang vừa đọc của từng bộ truyện.
//   - Hỗ trợ phục hồi tiến độ chéo thiết bị (Cross-Device Resume) giữa Windows, Linux và Android.
//   - Cung cấp API truy vấn tiến độ nhanh phục vụ hiển thị trên giao diện Thư viện.
//
// LƯU Ý KỸ THUẬT & QUY TẮC BẢO TRÌ:
//   - Sử dụng khóa đồng bộ thread-safe khi ghi tệp tiến độ để tránh xung đột khi nhiều luồng cùng ghi.
// ====================================================================================================

using System;
using System.Collections.Concurrent;
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

public sealed class ReaderBackupPayload
{
    public string SavedAt { get; set; } = string.Empty;
    public List<ReaderBookmarkItem> Bookmarks { get; set; } = new();
    public Dictionary<string, int> ProgressMap { get; set; } = new();
}

public sealed class BookmarksBackupPayload
{
    public string SavedAt { get; set; } = string.Empty;
    public List<ReaderBookmarkItem> Bookmarks { get; set; } = new();
}

public sealed class HistoryItemSnapshot
{
    public string Title { get; set; } = string.Empty;
    public int OrderStamp { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
}

public sealed class HistoryBackupPayload
{
    public string SavedAt { get; set; } = string.Empty;
    public List<HistoryItemSnapshot> History { get; set; } = new();
}

public sealed class CrossDeviceResumeEntry
{
    public string ComicTitle { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string ChapterTitle { get; set; } = string.Empty;
    public double ChapterNumber { get; set; } = -1;
    public int ChapterIndex { get; set; } = 1;
    public string ChapterSuffix { get; set; } = string.Empty;
    public int LastReadPage { get; set; } = 1;

    [JsonIgnore]
    public int TotalPages { get; set; } = 0;

    public long UpdatedAtUnixSeconds { get; set; }
    public string DeviceName { get; set; } = string.Empty;
}

public sealed class UnifiedHistoryItemInfo
{
    public string Path { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ChapterOrSubTitle { get; set; } = string.Empty;
    public int LastReadPage { get; set; }
    public int TotalPages { get; set; }
    public int OrderStamp { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public bool HasLocalPath { get; set; }
}

public sealed class ComicResumePayload
{
    public string SavedAt { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int SchemaVersion { get; set; } = 3;
    public List<CrossDeviceResumeEntry> Entries { get; set; } = new();
}

public sealed class ResumeRestoreResult
{
    public int RestoredProgressEntries { get; set; }
    public int RestoredCrossDeviceComics { get; set; }
    public bool HasValidStructure { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class ReaderRestoreResult
{
    public int RestoredBookmarks { get; set; }
    public int RestoredHistory { get; set; }
    public int RestoredProgressEntries { get; set; }
    public bool HasValidStructure { get; set; }
    public string LoadedFromPath { get; set; } = string.Empty;
}

public sealed class ReadProgressStore
{
    private static readonly Lazy<ReadProgressStore> _lazy = new(() => new ReadProgressStore());
    public static ReadProgressStore Instance => _lazy.Value;

    public const string BookmarksFileName = "bookmarks.md";
    public const string HistoryFileName = "history.md";
    public const string ResumeFileName = "resume.md";
    public const string AutoSaveReaderFileName = "bookmarks.md";
    public const string SavedReaderFileName = "bookmarks.md";

    public const string BookmarksStateBeginMarker = "<!-- COMIC_BOOKMARKS_STATE_BEGIN -->";
    public const string BookmarksStateEndMarker = "<!-- COMIC_BOOKMARKS_STATE_END -->";
    public const string HistoryStateBeginMarker = "<!-- COMIC_HISTORY_STATE_BEGIN -->";
    public const string HistoryStateEndMarker = "<!-- COMIC_HISTORY_STATE_END -->";
    public const string ReaderStateBeginMarker = "<!-- COMIC_READER_STATE_BEGIN -->";
    public const string ReaderStateEndMarker = "<!-- COMIC_READER_STATE_END -->";
    public const string ResumeStateBeginMarker = "<!-- COMIC_RESUME_STATE_BEGIN -->";
    public const string ResumeStateEndMarker = "<!-- COMIC_RESUME_STATE_END -->";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private const string HistoryPrefix = "@history|";
    private const string FolderHistoryPrefix = "@folder|";
    private const string LastRootPrefix = "@last_root|";
    private const string PinnedFolderPrefix = "@pinned|";
    private const string PortableIdPrefix = "@id|";
    private const string LocalPathPrefix = "@localpath|";
    public const string LocalPathsFileName = "device_local_paths.json";
    private const long EpochOffsetSeconds = 1700000000L;

    private readonly ConcurrentDictionary<string, int> _progressMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _deviceLocalPathRegistry = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CrossDeviceResumeEntry> _crossDeviceEntries = new();
    private readonly object _crossDeviceLock = new();
    private readonly object _localPathLock = new();
    private readonly List<ReaderBookmarkItem> _bookmarks = new();
    private readonly object _bookmarkLock = new();
    private readonly object _saveLock = new();
    private readonly Timer _debounceTimer;
    private volatile bool _isDirty;
    private volatile bool _isLocalPathDirty;

    /// <summary>
    /// Nguồn cấp bổ sung các thư mục Storage từ bên ngoài (ví dụ các thư mục đang nạp trong LibraryViewModel).
    /// </summary>
    public static Func<IEnumerable<string>>? AdditionalStorageRootsProvider { get; set; }

    private ReadProgressStore()
    {
        _debounceTimer = new Timer(_ => FlushImmediately(), null, Timeout.Infinite, Timeout.Infinite);
        MigrateLegacyStorageFiles();
        LoadFromDisk();
        LoadBookmarksFromDisk();
        LoadHistoryFromDisk();
        LoadDeviceLocalPathsFromDisk();
    }

    public static string GetStoreFilePath()
    {
        string dir = QueueBackupService.GetAutoSaveDirectory();
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
        return Path.Combine(dir, ResumeFileName);
    }

    public static string GetBookmarksFilePath()
    {
        string dir = QueueBackupService.GetAutoSaveDirectory();
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
        return Path.Combine(dir, BookmarksFileName);
    }

    public static string GetHistoryFilePath()
    {
        string dir = QueueBackupService.GetAutoSaveDirectory();
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
        return Path.Combine(dir, HistoryFileName);
    }

    /// <summary>
    /// Đường dẫn tệp lưu trữ ánh xạ cục bộ (Comic Title -> Đường dẫn thực tế trên thiết bị này).
    /// Tệp này LƯU TRỮ ĐỘC LẬP TRÊN THIẾT BỊ HIỆN TẠI, TUYỆT ĐỐI KHÔNG BỊ GOOGLE DRIVE GHI ĐÈ.
    /// </summary>
    public static string GetDeviceLocalPathsFilePath()
    {
        string dir = QueueBackupService.GetAutoSaveDirectory();
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
        return Path.Combine(dir, LocalPathsFileName);
    }

    /// <summary>
    /// Đăng ký và bảo vệ đường dẫn file/thư mục cục bộ thực tế của bộ truyện trên thiết bị hiện tại.
    /// Giúp Reader và History luôn luôn ưu tiên mở file cục bộ, bất kể có đồng bộ Google Drive hay không.
    /// </summary>
    public void RegisterLocalComicPath(string? comicTitle, string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath)) return;
        string cleanPath = NormalizeKey(localPath).TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(cleanPath)) return;

        bool isArchive = LibraryScannerService.IsArchiveFile(cleanPath) || LibraryScannerService.IsPdfFile(cleanPath) || LibraryScannerService.IsMobiFile(cleanPath);
        string fileName = Path.GetFileName(cleanPath);
        string fileBase = isArchive ? Path.GetFileNameWithoutExtension(cleanPath) : fileName;

        void AddKey(string? rawKey)
        {
            if (string.IsNullOrWhiteSpace(rawKey)) return;
            string k = NormalizeKey(rawKey).Trim();
            if (k.Length > 0)
            {
                _deviceLocalPathRegistry[k] = cleanPath;
                string norm = NormalizeChapterName(k);
                if (!string.IsNullOrEmpty(norm) && !string.Equals(norm, k, StringComparison.OrdinalIgnoreCase))
                {
                    _deviceLocalPathRegistry[norm] = cleanPath;
                }
            }
        }

        AddKey(comicTitle);
        if (!string.IsNullOrWhiteSpace(comicTitle))
        {
            AddKey(Path.GetFileNameWithoutExtension(comicTitle));
        }
        AddKey(fileName);
        AddKey(fileBase);

        _isLocalPathDirty = true;
        SaveDeviceLocalPathsToDisk();

        // Ghi nhận vào lịch sử đọc local
        RegisterReadComic(cleanPath, touchTimestamp: false);
    }

    private void SaveDeviceLocalPathsToDisk()
    {
        if (!_isLocalPathDirty) return;
        lock (_localPathLock)
        {
            if (!_isLocalPathDirty) return;
            _isLocalPathDirty = false;
            try
            {
                string path = GetDeviceLocalPathsFilePath();
                string tmp = path + ".tmp";
                var dict = new Dictionary<string, string>(_deviceLocalPathRegistry, StringComparer.OrdinalIgnoreCase);
                string json = JsonSerializer.Serialize(dict, _jsonOptions);
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, path, overwrite: true);
            }
            catch { }
        }
    }

    private void LoadDeviceLocalPathsFromDisk()
    {
        try
        {
            string path = GetDeviceLocalPathsFilePath();
            if (!File.Exists(path)) return;
            string json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return;
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, _jsonOptions);
            if (dict != null)
            {
                foreach (var kv in dict)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    {
                        _deviceLocalPathRegistry[kv.Key] = kv.Value;
                    }
                }
            }
        }
        catch { }
    }

    private static int GetCurrentOrderStamp()
    {
        long rel = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - EpochOffsetSeconds;
        return (int)Math.Clamp(rel, 1L, int.MaxValue - 1L);
    }

    public static string NormalizeKey(string chapterKey)
    {
        if (string.IsNullOrWhiteSpace(chapterKey)) return string.Empty;
        string trimmed = chapterKey.Trim();

        if (trimmed.StartsWith("@", StringComparison.Ordinal))
        {
            return trimmed;
        }

        int sepIdx = trimmed.IndexOf("::", StringComparison.Ordinal);
        if (sepIdx > 0)
        {
            string basePath = trimmed.Substring(0, sepIdx);
            string suffix = trimmed.Substring(sepIdx);
            try
            {
                return Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + suffix;
            }
            catch
            {
                return basePath.TrimEnd('\\', '/') + suffix;
            }
        }

        try
        {
            return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return trimmed.TrimEnd('\\', '/');
        }
    }

    public static double ExtractChapterNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return -1;
        // Bắt các pattern như: chap 12, chapter 12.5, ch 05, hồi 10, tập 3, c12, v.v.
        var m = Regex.Match(text, @"(?:chap(?:ter)?|ch|hồi|tập|tâp|ep|part|bài|quyển)?\s*(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase);
        if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
        {
            return val;
        }
        var m2 = Regex.Match(text, @"\b(\d+(?:[.,]\d+)?)\b");
        if (m2.Success && double.TryParse(m2.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double val2))
        {
            return val2;
        }
        return -1;
    }

    public static string NormalizeChapterName(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string clean = text.Trim().ToLowerInvariant();
        clean = Regex.Replace(clean, @"[_\-\s]+", " ");
        double num = ExtractChapterNumber(clean);
        if (num >= 0)
        {
            return $"ch_{num.ToString(CultureInfo.InvariantCulture)}";
        }
        return clean;
    }

    public static string GetSafeRelativePath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return string.Empty;
        string clean = NormalizeKey(fullPath);
        int sep = clean.IndexOf("::", StringComparison.Ordinal);
        string suffix = sep > 0 ? clean.Substring(sep) : string.Empty;
        string baseP = sep > 0 ? clean.Substring(0, sep) : clean;
        string leaf = Path.GetFileName(baseP.TrimEnd('\\', '/'));
        string? parent = Path.GetDirectoryName(baseP.TrimEnd('\\', '/'));
        string parentLeaf = !string.IsNullOrWhiteSpace(parent) ? Path.GetFileName(parent.TrimEnd('\\', '/')) : string.Empty;
        if (!string.IsNullOrWhiteSpace(parentLeaf) && !string.IsNullOrWhiteSpace(leaf))
        {
            return $"{parentLeaf}/{leaf}{suffix}";
        }
        return $"{leaf}{suffix}";
    }

    /// <summary>
    /// Tạo danh sách khóa nhận diện linh hoạt (Portable Identity Keys) dựa trên tên file / tên bộ truyện + tên chương.
    /// Giúp giữ nguyên 100% lịch sử đọc kể cả khi người dùng di chuyển truyện sang thư mục hoặc ổ đĩa khác.
    /// </summary>
    private static List<string> BuildPortableIdentityKeys(string rawKey)
    {
        var keys = new List<string>(6);
        if (string.IsNullOrWhiteSpace(rawKey) || rawKey.StartsWith("@", StringComparison.Ordinal))
        {
            return keys;
        }

        string normalized = NormalizeKey(rawKey);
        string basePath = normalized;
        string suffix = string.Empty;

        int sepIdx = normalized.IndexOf("::", StringComparison.Ordinal);
        if (sepIdx > 0)
        {
            basePath = normalized.Substring(0, sepIdx);
            suffix = normalized.Substring(sepIdx);
        }

        string cleanBase = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leafName = Path.GetFileName(cleanBase);
        if (string.IsNullOrWhiteSpace(leafName)) return keys;

        string? parentDir = null;
        try { parentDir = Path.GetDirectoryName(cleanBase); } catch { }
        string parentName = !string.IsNullOrWhiteSpace(parentDir)
            ? Path.GetFileName(parentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : string.Empty;

        if (!string.IsNullOrEmpty(suffix))
        {
            // Khóa cho chương con bên trong PDF/ZIP hoặc #last_active_chapter hoặc #total_pages
            keys.Add($"{PortableIdPrefix}leaf|{leafName}{suffix}");
            if (!string.IsNullOrWhiteSpace(parentName))
            {
                keys.Add($"{PortableIdPrefix}pair|{parentName}/{leafName}{suffix}");
            }

            double sufNum = ExtractChapterNumber(suffix);
            if (sufNum >= 0)
            {
                keys.Add($"{PortableIdPrefix}num|{leafName}/chap_{sufNum.ToString(CultureInfo.InvariantCulture)}");
                keys.Add($"{PortableIdPrefix}num|{leafName}/chap_{((int)sufNum).ToString(CultureInfo.InvariantCulture)}");
            }
        }
        else
        {
            // Khóa cho thư mục chương hoặc file lẻ (.cbz/.zip/.pdf)
            if (!string.IsNullOrWhiteSpace(parentName))
            {
                keys.Add($"{PortableIdPrefix}pair|{parentName}/{leafName}");
                keys.Add($"{PortableIdPrefix}norm|{parentName}/{NormalizeChapterName(leafName)}");

                double chNum = ExtractChapterNumber(leafName);
                if (chNum >= 0)
                {
                    keys.Add($"{PortableIdPrefix}num|{parentName}/chap_{chNum.ToString(CultureInfo.InvariantCulture)}");
                    keys.Add($"{PortableIdPrefix}num|{parentName}/chap_{((int)chNum).ToString(CultureInfo.InvariantCulture)}");
                }
            }

            string ext = Path.GetExtension(leafName);
            if (!string.IsNullOrEmpty(ext))
            {
                keys.Add($"{PortableIdPrefix}file|{leafName}");
                string noExt = Path.GetFileNameWithoutExtension(leafName);
                if (!string.IsNullOrWhiteSpace(noExt) && !string.Equals(noExt, leafName, StringComparison.OrdinalIgnoreCase))
                {
                    keys.Add($"{PortableIdPrefix}file|{noExt}");
                }
            }
            else
            {
                keys.Add($"{PortableIdPrefix}leaf|{leafName}");
            }
        }

        return keys;
    }

    /// <summary>
    /// Trả về số trang đang đọc (1-based: 0 = chưa đọc, 1..N = trang đang đọc).
    /// Tự động đối chiếu cả đường dẫn tuyệt đối lẫn khóa định danh linh hoạt (khi đổi thư mục hoặc cross-device).
    /// </summary>
    public int GetLastReadPage(string chapterKey)
    {
        string key = NormalizeKey(chapterKey);
        if (string.IsNullOrEmpty(key)) return 0;

        // 1. Đối chiếu trực tiếp theo đường dẫn tuyệt đối
        if (_progressMap.TryGetValue(key, out int page) && page > 0)
        {
            return page;
        }

        // 2. Đối chiếu qua các khóa nhận diện linh hoạt (Portable Identity Keys)
        foreach (string portableKey in BuildPortableIdentityKeys(key))
        {
            if (_progressMap.TryGetValue(portableKey, out int portablePage) && portablePage > 0)
            {
                return portablePage;
            }
        }

        // 3. Đối chiếu thông minh theo số chương hoặc tên chương chuẩn hóa
        int sepIdx = key.IndexOf("::", StringComparison.Ordinal);
        string basePath = sepIdx > 0 ? key.Substring(0, sepIdx) : key;
        string cleanBase = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaf = Path.GetFileName(cleanBase);
        string? parentDir = null;
        try { parentDir = Path.GetDirectoryName(cleanBase); } catch { }
        string parentName = !string.IsNullOrWhiteSpace(parentDir)
            ? Path.GetFileName(parentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(parentName) && !string.IsNullOrWhiteSpace(leaf))
        {
            double chNum = ExtractChapterNumber(leaf);
            if (chNum >= 0)
            {
                string numKey1 = $"{PortableIdPrefix}num|{parentName}/chap_{chNum.ToString(CultureInfo.InvariantCulture)}";
                if (_progressMap.TryGetValue(numKey1, out int p1) && p1 > 0) return p1;
                string numKey2 = $"{PortableIdPrefix}num|{parentName}/chap_{((int)chNum).ToString(CultureInfo.InvariantCulture)}";
                if (_progressMap.TryGetValue(numKey2, out int p2) && p2 > 0) return p2;
            }

            string normKey = $"{PortableIdPrefix}norm|{parentName}/{NormalizeChapterName(leaf)}";
            if (_progressMap.TryGetValue(normKey, out int pNorm) && pNorm > 0) return pNorm;
        }

        // 4. Đối chiếu thông minh từ resume.md (CrossDeviceResumeEntry) nếu chưa có lịch sử cục bộ
        if (!string.IsNullOrWhiteSpace(parentName) || !string.IsNullOrWhiteSpace(leaf))
        {
            var resumeEntry = FindResumeEntryForComic(parentName, basePath);
            if (resumeEntry != null && resumeEntry.LastReadPage > 0)
            {
                double chNum = ExtractChapterNumber(leaf);
                bool isMatchingChapter = false;
                if (chNum >= 0 && resumeEntry.ChapterNumber >= 0 && Math.Abs(chNum - resumeEntry.ChapterNumber) < 0.001)
                {
                    isMatchingChapter = true;
                }
                else if (!string.IsNullOrWhiteSpace(resumeEntry.ChapterTitle) &&
                         string.Equals(NormalizeChapterName(leaf), NormalizeChapterName(resumeEntry.ChapterTitle), StringComparison.OrdinalIgnoreCase))
                {
                    isMatchingChapter = true;
                }
                else if (resumeEntry.ChapterIndex > 0 && chNum == resumeEntry.ChapterIndex)
                {
                    isMatchingChapter = true;
                }

                if (isMatchingChapter)
                {
                    return resumeEntry.LastReadPage;
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Trả về chỉ số trang (0-based index) để nhảy tới khi mở chương.
    /// </summary>
    public int GetLastPageIndex(string chapterKey, int totalPages)
    {
        int page1Based = GetLastReadPage(chapterKey);
        if (page1Based <= 1) return 0;
        if (totalPages > 0)
        {
            return Math.Clamp(page1Based - 1, 0, totalPages - 1);
        }
        return Math.Max(0, page1Based - 1);
    }

    /// <summary>
    /// Lưu trang đang đọc (0-based pageIndex -> lưu thành 1-based page number) với cơ chế debounce 500ms.
    /// </summary>
    public void SaveProgress(string chapterKey, int pageIndex)
    {
        string key = NormalizeKey(chapterKey);
        if (string.IsNullOrEmpty(key)) return;

        int page1Based = Math.Max(1, pageIndex + 1);
        _progressMap[key] = page1Based;

        foreach (string portableKey in BuildPortableIdentityKeys(key))
        {
            _progressMap[portableKey] = page1Based;
        }

        RegisterComicFromChapterKeyInternal(key, touchTimestamp: true);
        MarkDirtyAndScheduleFlush();
    }

    /// <summary>
    /// Lưu chỉ số chương (1-based Chapter.Index) đang đọc gần nhất của 1 bộ truyện / file PDF / file ZIP.
    /// </summary>
    public void SaveLastActiveChapterIndex(string comicOrFilePath, int chapterIndex1Based)
    {
        if (string.IsNullOrWhiteSpace(comicOrFilePath) || chapterIndex1Based <= 0) return;
        string normalizedBase = NormalizeKey(comicOrFilePath);
        string key = normalizedBase + "::#last_active_chapter";
        _progressMap[key] = chapterIndex1Based;

        foreach (string portableKey in BuildPortableIdentityKeys(key))
        {
            _progressMap[portableKey] = chapterIndex1Based;
        }

        RegisterReadComic(normalizedBase, touchTimestamp: true);
        MarkDirtyAndScheduleFlush();
    }

    public int GetLastActiveChapterIndex(string comicOrFilePath)
    {
        if (string.IsNullOrWhiteSpace(comicOrFilePath)) return 0;
        string key = NormalizeKey(comicOrFilePath) + "::#last_active_chapter";
        if (_progressMap.TryGetValue(key, out int idx) && idx > 0)
        {
            return idx;
        }

        foreach (string portableKey in BuildPortableIdentityKeys(key))
        {
            if (_progressMap.TryGetValue(portableKey, out int portableIdx) && portableIdx > 0)
            {
                return portableIdx;
            }
        }

        string leaf = Path.GetFileName(comicOrFilePath.TrimEnd('\\', '/'));
        if (!string.IsNullOrWhiteSpace(leaf))
        {
            string leafKey = $"{PortableIdPrefix}leaf|{leaf}::#last_active_chapter";
            if (_progressMap.TryGetValue(leafKey, out int lIdx) && lIdx > 0) return lIdx;

            string normKey = $"{PortableIdPrefix}norm|{NormalizeChapterName(leaf)}::#last_active_chapter";
            if (_progressMap.TryGetValue(normKey, out int nIdx) && nIdx > 0) return nIdx;

            // Fallback: tra cứu từ resume.md (CrossDeviceResumeEntry)
            var resumeEntry = FindResumeEntryForComic(leaf, comicOrFilePath);
            if (resumeEntry != null && resumeEntry.ChapterIndex > 0)
            {
                return resumeEntry.ChapterIndex;
            }
        }

        // Tra cứu trực tiếp theo comicOrFilePath như tên truyện (Title) trong resume.md
        var directResume = FindResumeEntryForComic(comicOrFilePath);
        if (directResume != null && directResume.ChapterIndex > 0)
        {
            return directResume.ChapterIndex;
        }

        return 0;
    }

    /// <summary>
    /// Lưu chỉ số chương đang đọc gần nhất theo Tên truyện (Title), đảm bảo nhận diện chính xác
    /// kể cả khi mở truyện từ các đường dẫn khác nhau hoặc khi ParentComic null.
    /// </summary>
    public void SaveLastActiveChapterForTitle(string comicTitle, int chapterIndex1Based)
    {
        if (string.IsNullOrWhiteSpace(comicTitle) || chapterIndex1Based <= 0) return;
        string cleanTitle = comicTitle.Trim();
        string key = $"{PortableIdPrefix}leaf|{cleanTitle}::#last_active_chapter";
        _progressMap[key] = chapterIndex1Based;

        string normTitle = NormalizeChapterName(cleanTitle);
        if (!string.IsNullOrEmpty(normTitle))
        {
            _progressMap[$"{PortableIdPrefix}norm|{normTitle}::#last_active_chapter"] = chapterIndex1Based;
        }

        MarkDirtyAndScheduleFlush();
    }

    /// <summary>
    /// Ghi nhận phiên đọc hiện tại vào danh mục đồng bộ dùng chung (_crossDeviceEntries) cho resume.md.
    /// Đảm bảo tự động nhớ chính xác chương và trang vừa đọc trên toàn bộ thiết bị ngay khi thoát Reader.
    /// </summary>
    public void RecordSessionResume(
        string? comicTitle,
        string? comicRootOrFilePath,
        string? chapterTitle,
        int chapterIndex,
        double chapterNumber,
        int pageIndex,
        int totalPages,
        string? currentDeviceName = null)
    {
        string title = !string.IsNullOrWhiteSpace(comicTitle) ? comicTitle.Trim() : string.Empty;
        string path = !string.IsNullOrWhiteSpace(comicRootOrFilePath) ? NormalizeKey(comicRootOrFilePath) : string.Empty;

        // Quy tắc tối thượng: Chỉ có file ảnh mới được phép gộp thành parent folder!
        // File nén (archive), PDF, MOBI khi mở và khi tắt reader tuyệt đối KHÔNG ĐƯỢC PHÉP gộp với parent folder!
        bool isArchive = !string.IsNullOrEmpty(path) && (LibraryScannerService.IsArchiveFile(path) || LibraryScannerService.IsPdfFile(path) || LibraryScannerService.IsMobiFile(path));

        if (!isArchive && !string.IsNullOrEmpty(path) && (Directory.Exists(path) || File.Exists(path)))
        {
            if (LibraryScannerService.TryGetParentComicRoot(path, out string parentRoot, out string chName))
            {
                if (!LibraryScannerService.IsCommonSystemOrUserDirectory(parentRoot))
                {
                    path = NormalizeKey(parentRoot);
                    title = Path.GetFileName(parentRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (string.IsNullOrWhiteSpace(chapterTitle) || string.Equals(chapterTitle, title, StringComparison.OrdinalIgnoreCase))
                    {
                        chapterTitle = chName;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(path))
        {
            title = isArchive
                ? Path.GetFileNameWithoutExtension(path)
                : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        if (string.IsNullOrEmpty(title)) return;

        int page1Based = Math.Max(1, pageIndex + 1);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string devName = !string.IsNullOrWhiteSpace(currentDeviceName)
            ? currentDeviceName
            : (OperatingSystem.IsAndroid() ? "Android Device" : (OperatingSystem.IsWindows() ? "Windows PC" : "Linux PC"));

        lock (_crossDeviceLock)
        {
            var existing = _crossDeviceEntries.FirstOrDefault(e =>
                string.Equals(e.ComicTitle, title, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeChapterName(e.ComicTitle), NormalizeChapterName(title), StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                if (chapterIndex > 0) existing.ChapterIndex = chapterIndex;
                if (!string.IsNullOrWhiteSpace(chapterTitle)) existing.ChapterTitle = chapterTitle;
                if (chapterNumber >= 0) existing.ChapterNumber = chapterNumber;
                existing.LastReadPage = page1Based;
                if (totalPages > 0) existing.TotalPages = totalPages;
                existing.UpdatedAtUnixSeconds = now;
                existing.DeviceName = devName;
                if (!string.IsNullOrEmpty(path))
                {
                    existing.RelativePath = GetSafeRelativePath(path);
                }
            }
            else
            {
                _crossDeviceEntries.Add(new CrossDeviceResumeEntry
                {
                    ComicTitle = title,
                    RelativePath = !string.IsNullOrEmpty(path) ? GetSafeRelativePath(path) : title,
                    ChapterTitle = chapterTitle ?? (chapterIndex > 0 ? $"Chương {chapterIndex}" : string.Empty),
                    ChapterNumber = chapterNumber >= 0 ? chapterNumber : (chapterIndex > 0 ? chapterIndex : -1),
                    ChapterIndex = chapterIndex > 0 ? chapterIndex : 1,
                    ChapterSuffix = string.Empty,
                    LastReadPage = page1Based,
                    TotalPages = totalPages,
                    UpdatedAtUnixSeconds = now,
                    DeviceName = devName
                });
            }
        }

        if (chapterIndex > 0)
        {
            _progressMap[$"{PortableIdPrefix}leaf|{title}::#last_active_chapter"] = chapterIndex;
            string normTitle = NormalizeChapterName(title);
            if (!string.IsNullOrEmpty(normTitle))
            {
                _progressMap[$"{PortableIdPrefix}norm|{normTitle}::#last_active_chapter"] = chapterIndex;
            }
        }

        if (!string.IsNullOrEmpty(path))
        {
            RegisterReadComic(path, touchTimestamp: true);
            if (chapterIndex > 0)
            {
                _progressMap[path + "::#last_active_chapter"] = chapterIndex;
                foreach (string pk in BuildPortableIdentityKeys(path + "::#last_active_chapter"))
                {
                    _progressMap[pk] = chapterIndex;
                }
            }
        }

        MarkDirtyAndScheduleFlush();
    }

    /// <summary>
    /// Ghi nhận một bộ truyện (thư mục truyện hoặc file .pdf/.zip/.cbz) vào danh mục Lịch sử Đọc toàn cục
    /// để hiển thị xuyên suốt bất kể người dùng đang chọn thư mục thư viện nào.
    /// Tự động quy về Thư mục cha tổng nếu đường dẫn truyền vào là thư mục/tệp chương con.
    /// </summary>
    public void RegisterReadComic(string comicRootOrFilePath, bool touchTimestamp = false)
    {
        if (string.IsNullOrWhiteSpace(comicRootOrFilePath)) return;
        string cleanPath = NormalizeKey(comicRootOrFilePath);
        int sep = cleanPath.IndexOf("::", StringComparison.Ordinal);
        if (sep > 0) cleanPath = cleanPath.Substring(0, sep);
        if (string.IsNullOrWhiteSpace(cleanPath)) return;

        // Quy tắc tối thượng: File nén là truyện độc lập, tuyệt đối không gộp vào parent folder!
        bool isArchive = LibraryScannerService.IsArchiveFile(cleanPath) || LibraryScannerService.IsPdfFile(cleanPath) || LibraryScannerService.IsMobiFile(cleanPath);

        if (!isArchive && (Directory.Exists(cleanPath) || File.Exists(cleanPath)))
        {
            if (LibraryScannerService.TryGetParentComicRoot(cleanPath, out string parentRoot, out _))
            {
                cleanPath = NormalizeKey(parentRoot);
            }
        }

        string histKey = HistoryPrefix + cleanPath;
        if (touchTimestamp || !_progressMap.ContainsKey(histKey))
        {
            _progressMap[histKey] = GetCurrentOrderStamp();

            // Tự động loại bỏ các file/thư mục chương con lẻ thuộc về cleanPath để không bị rác history (nhưng bảo lưu file nén độc lập)
            string prefixSep1 = cleanPath + Path.DirectorySeparatorChar;
            string prefixSep2 = cleanPath + "/";
            foreach (var key in _progressMap.Keys)
            {
                if (key.StartsWith(HistoryPrefix, StringComparison.OrdinalIgnoreCase) && !string.Equals(key, histKey, StringComparison.OrdinalIgnoreCase))
                {
                    string sub = key.Substring(HistoryPrefix.Length);
                    if ((sub.StartsWith(prefixSep1, StringComparison.OrdinalIgnoreCase) || sub.StartsWith(prefixSep2, StringComparison.OrdinalIgnoreCase))
                        && !LibraryScannerService.IsArchiveFile(sub))
                    {
                        _progressMap.TryRemove(key, out _);
                    }
                }
            }

            MarkDirtyAndScheduleFlush();
        }
    }

    public int GetComicLastReadOrder(string comicRootOrFilePath)
    {
        if (string.IsNullOrWhiteSpace(comicRootOrFilePath)) return 0;
        string cleanPath = NormalizeKey(comicRootOrFilePath);
        if (_progressMap.TryGetValue(HistoryPrefix + cleanPath, out int order) && order > 0)
        {
            return order;
        }

        string leafName = Path.GetFileName(cleanPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(leafName))
        {
            foreach (var kv in _progressMap)
            {
                if (kv.Key.StartsWith(HistoryPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string candidatePath = kv.Key.Substring(HistoryPrefix.Length);
                    string candidateLeaf = Path.GetFileName(candidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (string.Equals(leafName, candidateLeaf, StringComparison.OrdinalIgnoreCase))
                    {
                        return kv.Value;
                    }
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Trả về danh sách toàn bộ đường dẫn truyện (Folder hoặc File .pdf/.zip/.cbz) từng được đọc,
    /// sắp xếp theo thời gian đọc gần nhất giảm dần.
    /// </summary>
    public List<string> GetKnownReadComicPaths()
    {
        var entries = new List<(string Path, int Order)>();
        foreach (var kv in _progressMap)
        {
            if (kv.Key.StartsWith(HistoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string path = kv.Key.Substring(HistoryPrefix.Length).Trim();
                if (!string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
                {
                    entries.Add((path, kv.Value));
                }
            }
        }

        return entries
            .OrderByDescending(e => e.Order)
            .Select(e => e.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Trả về danh sách toàn bộ đường dẫn truyện trong Lịch sử Đọc (tùy chọn có lọc tồn tại file hay không).
    /// Tự động loại bỏ các thư mục chương con trùng lặp nếu đã có thư mục truyện cha.
    /// </summary>
    public List<string> GetAllHistoryPaths(bool checkPathExists = false)
    {
        var entries = new List<(string Path, int Order)>();
        foreach (var kv in _progressMap)
        {
            if (kv.Key.StartsWith(HistoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string path = kv.Key.Substring(HistoryPrefix.Length).Trim();
                if (!string.IsNullOrWhiteSpace(path) && (!checkPathExists || File.Exists(path) || Directory.Exists(path)))
                {
                    entries.Add((path, kv.Value));
                }
            }
        }

        var sorted = entries
            .OrderByDescending(e => e.Order)
            .Select(e => e.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cleanPaths = new List<string>();
        var keysToRemove = new List<string>();

        foreach (var path in sorted)
        {
            // 1. Kiểm tra xem path có phải là subfolder của một path khác trong sorted không
            // File nén là một bộ truyện độc lập, tuyệt đối không bị coi là subfolder hay bị ẩn khi nằm trong folder cha!
            bool isSubfolder = false;
            bool isArchive = LibraryScannerService.IsArchiveFile(path) || LibraryScannerService.IsPdfFile(path) || LibraryScannerService.IsMobiFile(path);

            if (!isArchive)
            {
                foreach (var other in sorted)
                {
                    if (string.Equals(path, other, StringComparison.OrdinalIgnoreCase)) continue;
                    string cleanOther = other.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (path.StartsWith(cleanOther, StringComparison.OrdinalIgnoreCase))
                    {
                        isSubfolder = true;
                        break;
                    }
                }

                // 2. Kiểm tra nếu path là một chương con có parent comic root hợp lệ
                if (!isSubfolder && (Directory.Exists(path) || File.Exists(path)))
                {
                    if (LibraryScannerService.TryGetParentComicRoot(path, out string parentRoot, out _))
                    {
                        if (!LibraryScannerService.IsCommonSystemOrUserDirectory(parentRoot) &&
                            !string.Equals(NormalizeKey(parentRoot), NormalizeKey(path), StringComparison.OrdinalIgnoreCase))
                        {
                            // Nếu đã có truyện cha trong danh sách sorted thì ẩn chương con này đi
                            string normParent = NormalizeKey(parentRoot);
                            if (sorted.Any(s => string.Equals(NormalizeKey(s), normParent, StringComparison.OrdinalIgnoreCase)))
                            {
                                isSubfolder = true;
                            }
                        }
                    }
                }
            }

            if (!isSubfolder)
            {
                cleanPaths.Add(path);
            }
        }

        if (keysToRemove.Count > 0)
        {
            foreach (var k in keysToRemove)
            {
                _progressMap.TryRemove(k, out _);
            }
            MarkDirtyAndScheduleFlush();
        }

        return cleanPaths;
    }

    public int GetBookmarkCount()
    {
        lock (_bookmarkLock)
        {
            return _bookmarks.Count;
        }
    }

    public void RemoveHistory(string comicRootOrFilePath)
    {
        if (string.IsNullOrWhiteSpace(comicRootOrFilePath)) return;
        string cleanPath = NormalizeKey(comicRootOrFilePath);
        string histKey = HistoryPrefix + cleanPath;
        _progressMap.TryRemove(histKey, out _);

        string leafName = Path.GetFileName(cleanPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(leafName))
        {
            foreach (var key in _progressMap.Keys.Where(k => k.StartsWith(HistoryPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                string candidatePath = key.Substring(HistoryPrefix.Length);
                string candidateLeaf = Path.GetFileName(candidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.Equals(leafName, candidateLeaf, StringComparison.OrdinalIgnoreCase))
                {
                    _progressMap.TryRemove(key, out _);
                }
            }
        }
        MarkDirtyAndScheduleFlush();
    }

    /// <summary>
    /// Xóa toàn bộ tiến độ đọc dở (Resume progress) của riêng một bộ truyện hoặc tập tin cụ thể.
    /// Đưa vị trí đọc về ban đầu (0%), đồng thời bảo toàn hoặc xóa lịch sử theo nhu cầu.
    /// </summary>
    public void ClearProgressForComic(string comicRootOrFilePath)
    {
        if (string.IsNullOrWhiteSpace(comicRootOrFilePath)) return;
        string cleanPath = NormalizeKey(comicRootOrFilePath);
        string leafName = Path.GetFileName(cleanPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var keysToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Khóa trực tiếp và khóa chương cuối cùng
        keysToRemove.Add(cleanPath);
        keysToRemove.Add(cleanPath + "::#last_active_chapter");

        // Portable keys
        foreach (var pk in BuildPortableIdentityKeys(cleanPath))
        {
            keysToRemove.Add(pk);
        }
        foreach (var pk in BuildPortableIdentityKeys(cleanPath + "::#last_active_chapter"))
        {
            keysToRemove.Add(pk);
        }

        string prefixSep1 = cleanPath + Path.DirectorySeparatorChar;
        string prefixSep2 = cleanPath + "/";
        string prefixSep3 = cleanPath + "::";

        foreach (var key in _progressMap.Keys)
        {
            if (string.Equals(key, cleanPath, StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith(prefixSep1, StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith(prefixSep2, StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith(prefixSep3, StringComparison.OrdinalIgnoreCase))
            {
                keysToRemove.Add(key);
            }
            else if (!string.IsNullOrWhiteSpace(leafName) && key.StartsWith(PortableIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (key.Contains(leafName, StringComparison.OrdinalIgnoreCase))
                {
                    keysToRemove.Add(key);
                }
            }
        }

        foreach (var k in keysToRemove)
        {
            _progressMap.TryRemove(k, out _);
        }

        MarkDirtyAndScheduleFlush();
    }

    public void ClearAllHistory()
    {
        foreach (var key in _progressMap.Keys.Where(k => k.StartsWith(HistoryPrefix, StringComparison.OrdinalIgnoreCase) || k.StartsWith(FolderHistoryPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _progressMap.TryRemove(key, out _);
        }
        MarkDirtyAndScheduleFlush();
    }

    public List<string> GetPinnedFolders()
    {
        var list = new List<(string Path, int Order)>();
        foreach (var kv in _progressMap)
        {
            if (kv.Key.StartsWith(PinnedFolderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string path = kv.Key.Substring(PinnedFolderPrefix.Length).Trim();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    list.Add((path, kv.Value));
                }
            }
        }
        return list.OrderByDescending(x => x.Order).Select(x => x.Path).ToList();
    }

    public void AddPinnedFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        string clean = NormalizeKey(folderPath);
        if (string.IsNullOrWhiteSpace(clean)) return;
        _progressMap[PinnedFolderPrefix + clean] = GetCurrentOrderStamp();
        MarkDirtyAndScheduleFlush();
    }

    public void RemovePinnedFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        string clean = NormalizeKey(folderPath);
        _progressMap.TryRemove(PinnedFolderPrefix + clean, out _);
        MarkDirtyAndScheduleFlush();
    }

    public void SaveChapterTotalPages(string chapterKey, int totalPages)
    {
        if (string.IsNullOrWhiteSpace(chapterKey) || totalPages <= 0) return;
        string key = NormalizeKey(chapterKey) + "::#total_pages";
        _progressMap[key] = totalPages;

        foreach (string pk in BuildPortableIdentityKeys(key))
        {
            _progressMap[pk] = totalPages;
        }

        MarkDirtyAndScheduleFlush();
    }

    public int GetChapterTotalPages(string chapterKey)
    {
        if (string.IsNullOrWhiteSpace(chapterKey)) return 0;
        string key = NormalizeKey(chapterKey) + "::#total_pages";
        if (_progressMap.TryGetValue(key, out int total) && total > 0) return total;

        foreach (string pk in BuildPortableIdentityKeys(key))
        {
            if (_progressMap.TryGetValue(pk, out int pTotal) && pTotal > 0) return pTotal;
        }

        return 0;
    }

    /// <summary>
    /// Thu thập toàn bộ các thư mục gốc khả dụng trên thiết bị (Download, Storage roots, Thư mục ghim, Thư viện gần nhất)
    /// để tự động quét tìm và liên kết đường dẫn truyện cục bộ khi phục hồi chéo từ thiết bị khác.
    /// </summary>
    public List<string> GetCandidateStorageRoots()
    {
        var candidateRoots = new List<string>();

        void TryAdd(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string norm = NormalizeKey(path).TrimEnd('\\', '/');
                if (Directory.Exists(norm) && !candidateRoots.Contains(norm, StringComparer.OrdinalIgnoreCase))
                {
                    candidateRoots.Add(norm);
                }
            }
            catch { }
        }

        // Thêm các nguồn từ LibraryViewModel nếu đã nạp
        try
        {
            var extraRoots = AdditionalStorageRootsProvider?.Invoke();
            if (extraRoots != null)
            {
                foreach (var r in extraRoots) TryAdd(r);
            }
        }
        catch { }

        try { TryAdd(QueueBackupService.GetAutoSaveDirectory()); } catch { }
        try { TryAdd(AppDomain.CurrentDomain.BaseDirectory); } catch { }

        try
        {
            if (!OperatingSystem.IsAndroid())
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.IsReady)
                    {
                        TryAdd(d.RootDirectory.FullName);
                    }
                }
            }
        }
        catch { }

        try
        {
            foreach (var p in GetPinnedFolders())
            {
                TryAdd(p);
            }
        }
        catch { }

        try
        {
            TryAdd(GetLastLibraryRootPath());
        }
        catch { }

        try
        {
            string sysDown = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            TryAdd(sysDown);
        }
        catch { }

        if (OperatingSystem.IsAndroid())
        {
            try
            {
                var androidPaths = LibraryScannerService.AndroidGetExternalStoragePaths?.Invoke();
                if (androidPaths != null)
                {
                    foreach (var ap in androidPaths) TryAdd(ap);
                }
            }
            catch { }

            TryAdd("/storage/emulated/0/Download");
            TryAdd("/storage/emulated/0/Download/Comics");
            TryAdd("/storage/emulated/0/Download/ComicDownloader");
            TryAdd("/storage/emulated/0/Documents");
            TryAdd("/storage/emulated/0/Documents/Comics");
            TryAdd("/storage/emulated/0/Comics");
            TryAdd("/storage/emulated/0/DCIM");
            TryAdd("/storage/emulated/0/Pictures");
            TryAdd("/sdcard");
            TryAdd("/sdcard/Download");
            TryAdd("/sdcard/Comics");

            // Tự động quét tìm thẻ nhớ ngoài SD Card (/storage/XXXX-XXXX)
            try
            {
                if (Directory.Exists("/storage"))
                {
                    foreach (var sd in Directory.GetDirectories("/storage"))
                    {
                        string sdName = Path.GetFileName(sd);
                        if (!string.Equals(sdName, "emulated", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(sdName, "self", StringComparison.OrdinalIgnoreCase))
                        {
                            TryAdd(sd);
                            TryAdd(Path.Combine(sd, "Download"));
                            TryAdd(Path.Combine(sd, "Comics"));
                            TryAdd(Path.Combine(sd, "Documents"));
                        }
                    }
                }
            }
            catch { }
        }

        return candidateRoots;
    }

    /// <summary>
    /// Thử tìm đường dẫn cục bộ trên thiết bị hiện tại khớp với tên truyện (ComicTitle) hoặc tên file.
    /// Tra cứu siêu tốc đa tầng (Registry máy hiện tại -> File/Thư mục trực tiếp -> Quét sâu Storage roots).
    /// </summary>
    public string? TryResolveLocalPathForComic(string comicTitle)
    {
        if (string.IsNullOrWhiteSpace(comicTitle)) return null;
        string cleanTitle = comicTitle.Trim();

        // 1. Kiểm tra trực tiếp nếu comicTitle là một đường dẫn đầy đủ đang tồn tại
        if (File.Exists(cleanTitle) || Directory.Exists(cleanTitle))
        {
            RegisterLocalComicPath(cleanTitle, cleanTitle);
            return cleanTitle;
        }

        // 2. Tra cứu siêu tốc từ bảng đăng ký cục bộ trên thiết bị hiện tại (_deviceLocalPathRegistry)
        string titleNorm = NormalizeKey(cleanTitle);
        if (_deviceLocalPathRegistry.TryGetValue(titleNorm, out string? regPath) && !string.IsNullOrEmpty(regPath))
        {
            if (File.Exists(regPath) || Directory.Exists(regPath)) return regPath;
        }
        string titleNoExt = Path.GetFileNameWithoutExtension(cleanTitle);
        if (!string.IsNullOrEmpty(titleNoExt) && _deviceLocalPathRegistry.TryGetValue(NormalizeKey(titleNoExt), out string? regPathNoExt) && !string.IsNullOrEmpty(regPathNoExt))
        {
            if (File.Exists(regPathNoExt) || Directory.Exists(regPathNoExt)) return regPathNoExt;
        }
        string chNorm = NormalizeChapterName(cleanTitle);
        if (!string.IsNullOrEmpty(chNorm) && _deviceLocalPathRegistry.TryGetValue(chNorm, out string? regPathCh) && !string.IsNullOrEmpty(regPathCh))
        {
            if (File.Exists(regPathCh) || Directory.Exists(regPathCh)) return regPathCh;
        }

        // 3. Quét sâu đa tầng trong các Candidate Storage Roots
        var roots = GetCandidateStorageRoots();
        foreach (var root in roots)
        {
            try
            {
                // A. Kiểm tra trực tiếp thư mục hoặc file cùng tên
                string directFolder = Path.Combine(root, cleanTitle);
                if (Directory.Exists(directFolder) || File.Exists(directFolder))
                {
                    RegisterLocalComicPath(cleanTitle, directFolder);
                    return directFolder;
                }

                if (!string.IsNullOrEmpty(titleNoExt))
                {
                    string directNoExt = Path.Combine(root, titleNoExt);
                    if (Directory.Exists(directNoExt) || File.Exists(directNoExt))
                    {
                        RegisterLocalComicPath(cleanTitle, directNoExt);
                        return directNoExt;
                    }
                }

                // B. Tìm các file nén / PDF / Archive trong root
                var files = Directory.GetFiles(root);
                foreach (var f in files)
                {
                    string fName = Path.GetFileName(f);
                    string fBase = Path.GetFileNameWithoutExtension(f);
                    if (string.Equals(fName, cleanTitle, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fBase, cleanTitle, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(titleNoExt) && (string.Equals(fName, titleNoExt, StringComparison.OrdinalIgnoreCase) || string.Equals(fBase, titleNoExt, StringComparison.OrdinalIgnoreCase))))
                    {
                        RegisterLocalComicPath(cleanTitle, f);
                        return f;
                    }
                }

                // C. Tìm trong các thư mục con cấp 1 và cấp 2 (hỗ trợ truyện lồng nhau / grouped chapters)
                var subDirs = Directory.GetDirectories(root);
                foreach (var sd in subDirs)
                {
                    string sdName = Path.GetFileName(sd);
                    if (string.Equals(sdName, cleanTitle, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(titleNoExt) && string.Equals(sdName, titleNoExt, StringComparison.OrdinalIgnoreCase)))
                    {
                        RegisterLocalComicPath(cleanTitle, sd);
                        return sd;
                    }

                    string nested = Path.Combine(sd, cleanTitle);
                    if (Directory.Exists(nested) || File.Exists(nested))
                    {
                        RegisterLocalComicPath(cleanTitle, nested);
                        return nested;
                    }

                    if (!string.IsNullOrEmpty(titleNoExt))
                    {
                        string nestedNoExt = Path.Combine(sd, titleNoExt);
                        if (Directory.Exists(nestedNoExt) || File.Exists(nestedNoExt))
                        {
                            RegisterLocalComicPath(cleanTitle, nestedNoExt);
                            return nestedNoExt;
                        }
                    }

                    // Quét các file nén bên trong thư mục con cấp 1
                    try
                    {
                        var subFiles = Directory.GetFiles(sd);
                        foreach (var sf in subFiles)
                        {
                            string sfName = Path.GetFileName(sf);
                            string sfBase = Path.GetFileNameWithoutExtension(sf);
                            if (string.Equals(sfName, cleanTitle, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(sfBase, cleanTitle, StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrEmpty(titleNoExt) && (string.Equals(sfName, titleNoExt, StringComparison.OrdinalIgnoreCase) || string.Equals(sfBase, titleNoExt, StringComparison.OrdinalIgnoreCase))))
                            {
                                RegisterLocalComicPath(cleanTitle, sf);
                                return sf;
                            }
                        }

                        // Quét cấp 2 nếu có
                        var subSubDirs = Directory.GetDirectories(sd);
                        foreach (var ssd in subSubDirs)
                        {
                            string ssdName = Path.GetFileName(ssd);
                            if (string.Equals(ssdName, cleanTitle, StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrEmpty(titleNoExt) && string.Equals(ssdName, titleNoExt, StringComparison.OrdinalIgnoreCase)))
                            {
                                RegisterLocalComicPath(cleanTitle, ssd);
                                return ssd;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Tự động dò tìm và ánh xạ tiến độ từ các mục CrossDeviceResumeEntry vào các file/thư mục cục bộ có sẵn trên thiết bị hiện tại.
    /// </summary>
    public int AutoResolveAndRemapLocalPaths(IEnumerable<CrossDeviceResumeEntry> entries)
    {
        if (entries == null) return 0;
        int mappedCount = 0;

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.ComicTitle)) continue;

            string? localPath = TryResolveLocalPathForComic(entry.ComicTitle);
            if (string.IsNullOrWhiteSpace(localPath)) continue;

            string cleanLocal = NormalizeKey(localPath);
            int orderStamp = entry.UpdatedAtUnixSeconds > 0
                ? (int)Math.Clamp(entry.UpdatedAtUnixSeconds - EpochOffsetSeconds, 1L, int.MaxValue - 1L)
                : GetCurrentOrderStamp();

            RegisterReadComic(cleanLocal, touchTimestamp: false);
            _progressMap[HistoryPrefix + cleanLocal] = orderStamp;

            if (entry.ChapterIndex > 0)
            {
                _progressMap[cleanLocal + "::#last_active_chapter"] = entry.ChapterIndex;
            }
            if (entry.TotalPages > 0)
            {
                _progressMap[cleanLocal + "::#total_pages"] = entry.TotalPages;
            }

            if (File.Exists(cleanLocal))
            {
                if (entry.LastReadPage > 0)
                {
                    _progressMap[cleanLocal] = entry.LastReadPage;
                }
                if (!string.IsNullOrWhiteSpace(entry.ChapterSuffix))
                {
                    _progressMap[cleanLocal + entry.ChapterSuffix] = entry.LastReadPage;
                    if (entry.TotalPages > 0)
                    {
                        _progressMap[cleanLocal + entry.ChapterSuffix + "::#total_pages"] = entry.TotalPages;
                    }
                }
                mappedCount++;
            }
            else if (Directory.Exists(cleanLocal))
            {
                try
                {
                    var subDirs = Directory.GetDirectories(cleanLocal);
                    foreach (var sub in subDirs)
                    {
                        string subName = Path.GetFileName(sub);
                        bool isMatch = false;

                        if (!string.IsNullOrWhiteSpace(entry.ChapterTitle) &&
                            string.Equals(subName, entry.ChapterTitle, StringComparison.OrdinalIgnoreCase))
                        {
                            isMatch = true;
                        }
                        else if (entry.ChapterNumber >= 0 && ExtractChapterNumber(subName) == entry.ChapterNumber)
                        {
                            isMatch = true;
                        }

                        if (isMatch)
                        {
                            string normSub = NormalizeKey(sub);
                            if (entry.LastReadPage > 0)
                            {
                                _progressMap[normSub] = entry.LastReadPage;
                            }
                            if (entry.TotalPages > 0)
                            {
                                _progressMap[normSub + "::#total_pages"] = entry.TotalPages;
                            }
                            break;
                        }
                    }
                    mappedCount++;
                }
                catch { }
            }
        }

        if (mappedCount > 0)
        {
            MarkDirtyAndScheduleFlush();
        }

        return mappedCount;
    }

    /// <summary>
    /// Trả về danh sách Lịch Sử Đọc hợp nhất gồm cả các truyện cục bộ trên thiết bị
    /// VÀ các truyện được đồng bộ từ thiết bị khác (kể cả khi chưa tìm thấy file cục bộ).
    /// Ưu tiên tuyệt đối đọc file local nếu có tệp/thư mục tương ứng trên máy hiện tại.
    /// </summary>
    public List<UnifiedHistoryItemInfo> GetUnifiedHistoryItems()
    {
        var result = new List<UnifiedHistoryItemInfo>();
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Thêm các truyện cục bộ tồn tại trên thiết bị hiện tại
        var localPaths = GetAllHistoryPaths(checkPathExists: true);
        foreach (var path in localPaths)
        {
            string title = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string titleWithoutExt = Path.GetFileNameWithoutExtension(title);
            int lastChap = GetLastActiveChapterIndex(path);
            int lastPage = GetLastReadPage(path);
            int totalPages = GetChapterTotalPages(path);
            int order = GetComicLastReadOrder(path);
            string subTitle = lastChap > 0 ? $"Chương {lastChap}" : string.Empty;

            // Đồng bộ tiến trình đọc dở từ _crossDeviceEntries (từ resume.md) nếu có
            lock (_crossDeviceLock)
            {
                var matchEntry = _crossDeviceEntries.FirstOrDefault(e =>
                    string.Equals(e.ComicTitle, title, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.ComicTitle, titleWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeChapterName(e.ComicTitle), NormalizeChapterName(title), StringComparison.OrdinalIgnoreCase));
                if (matchEntry != null)
                {
                    if (matchEntry.LastReadPage > 0) lastPage = matchEntry.LastReadPage;
                    if (matchEntry.ChapterIndex > 0)
                    {
                        lastChap = matchEntry.ChapterIndex;
                        subTitle = !string.IsNullOrWhiteSpace(matchEntry.ChapterTitle)
                            ? matchEntry.ChapterTitle
                            : $"Chương {matchEntry.ChapterIndex}";
                    }
                    if (matchEntry.UpdatedAtUnixSeconds > 0)
                    {
                        int cloudOrder = (int)Math.Clamp(matchEntry.UpdatedAtUnixSeconds - EpochOffsetSeconds, 1L, int.MaxValue - 1L);
                        order = Math.Max(order, cloudOrder);
                    }
                }
            }

            // Nếu subTitle đang dạng thô "Chương X", thử tra cứu tên thư mục chương thực tế nếu có
            if (string.IsNullOrWhiteSpace(subTitle) || subTitle.StartsWith("Chương ", StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(path))
                {
                    try
                    {
                        var subs = Directory.GetDirectories(path).NaturalSort();
                        if (lastChap > 0 && lastChap <= subs.Count)
                        {
                            string realChapDirName = Path.GetFileName(subs[lastChap - 1]);
                            if (!string.IsNullOrWhiteSpace(realChapDirName))
                            {
                                subTitle = realChapDirName;
                            }
                        }
                    }
                    catch { }
                }
            }

            seenTitles.Add(title);
            if (!string.IsNullOrEmpty(titleWithoutExt)) seenTitles.Add(titleWithoutExt);
            string normTitle = NormalizeChapterName(title);
            if (!string.IsNullOrEmpty(normTitle)) seenTitles.Add(normTitle);

            // Tự động ghi nhớ ánh xạ local path
            RegisterLocalComicPath(title, path);

            result.Add(new UnifiedHistoryItemInfo
            {
                Path = path,
                Title = title,
                ChapterOrSubTitle = subTitle,
                LastReadPage = lastPage,
                TotalPages = totalPages,
                OrderStamp = order,
                DeviceName = string.Empty, // Truyện local luôn có DeviceName rỗng để không bị đánh dấu là Cloud
                HasLocalPath = true
            });
        }

        // 2. Bổ sung các truyện đồng bộ chéo từ thiết bị khác
        lock (_crossDeviceLock)
        {
            foreach (var entry in _crossDeviceEntries)
            {
                if (string.IsNullOrWhiteSpace(entry.ComicTitle))
                    continue;

                string entryTitle = entry.ComicTitle.Trim();
                string entryTitleNoExt = Path.GetFileNameWithoutExtension(entryTitle);
                string entryNorm = NormalizeChapterName(entryTitle);

                // Thử tìm đường dẫn cục bộ trên thiết bị hiện tại
                string? resolved = TryResolveLocalPathForComic(entryTitle);
                if (string.IsNullOrEmpty(resolved) && !string.IsNullOrEmpty(entry.RelativePath))
                {
                    resolved = TryResolveLocalPathForComic(Path.GetFileName(entry.RelativePath));
                    if (string.IsNullOrEmpty(resolved))
                    {
                        resolved = TryResolveLocalPathForComic(Path.GetFileNameWithoutExtension(entry.RelativePath));
                    }
                }

                bool hasLocal = !string.IsNullOrWhiteSpace(resolved) && (File.Exists(resolved) || Directory.Exists(resolved));

                int orderStamp = entry.UpdatedAtUnixSeconds > 0
                    ? (int)Math.Clamp(entry.UpdatedAtUnixSeconds - EpochOffsetSeconds, 1L, int.MaxValue - 1L)
                    : 0;

                string subTitle = !string.IsNullOrWhiteSpace(entry.ChapterTitle)
                    ? entry.ChapterTitle
                    : (entry.ChapterIndex > 0 ? $"Chương {entry.ChapterIndex}" : string.Empty);

                // KIỂM TRA ĐÃ CÓ TRONG DANH SÁCH LOCAL CHƯA
                bool alreadyInList = seenTitles.Contains(entryTitle) ||
                                     (!string.IsNullOrEmpty(entryTitleNoExt) && seenTitles.Contains(entryTitleNoExt)) ||
                                     (!string.IsNullOrEmpty(entryNorm) && seenTitles.Contains(entryNorm));

                if (alreadyInList)
                {
                    // Đã có trong danh sách local -> Cập nhật thông tin đọc dở nếu entry đám mây mới hơn
                    var existingLocal = result.FirstOrDefault(r =>
                        string.Equals(r.Title, entryTitle, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileNameWithoutExtension(r.Title), entryTitleNoExt, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(NormalizeChapterName(r.Title), entryNorm, StringComparison.OrdinalIgnoreCase));

                    if (existingLocal != null)
                    {
                        if (entry.LastReadPage > 0) existingLocal.LastReadPage = entry.LastReadPage;
                        if (!string.IsNullOrWhiteSpace(subTitle)) existingLocal.ChapterOrSubTitle = subTitle;
                        existingLocal.OrderStamp = Math.Max(existingLocal.OrderStamp, orderStamp);
                        // ĐẢM BẢO GIỮ NGUYÊN LOCAL PATH VÀ KHÔNG BIẾN THÀNH CLOUD ITEM
                        existingLocal.HasLocalPath = true;
                        existingLocal.DeviceName = string.Empty;
                        if (hasLocal && !string.IsNullOrEmpty(resolved) && !File.Exists(existingLocal.Path) && !Directory.Exists(existingLocal.Path))
                        {
                            existingLocal.Path = resolved;
                        }
                    }
                    continue;
                }

                // Nếu chưa có trong danh sách:
                int dynTotalPages = (hasLocal && !string.IsNullOrEmpty(resolved)) ? GetChapterTotalPages(resolved) : entry.TotalPages;

                string displayDevice = hasLocal ? string.Empty : entry.DeviceName;
                if (!hasLocal && !string.IsNullOrWhiteSpace(displayDevice))
                {
                    subTitle = string.IsNullOrWhiteSpace(subTitle)
                        ? $"Từ {displayDevice}"
                        : $"{subTitle} ({displayDevice})";
                }

                seenTitles.Add(entryTitle);
                if (!string.IsNullOrEmpty(entryTitleNoExt)) seenTitles.Add(entryTitleNoExt);
                if (!string.IsNullOrEmpty(entryNorm)) seenTitles.Add(entryNorm);

                result.Add(new UnifiedHistoryItemInfo
                {
                    Path = hasLocal ? (resolved ?? string.Empty) : string.Empty,
                    Title = entryTitle,
                    ChapterOrSubTitle = subTitle,
                    LastReadPage = entry.LastReadPage,
                    TotalPages = dynTotalPages,
                    OrderStamp = orderStamp,
                    DeviceName = displayDevice,
                    HasLocalPath = hasLocal
                });
            }
        }

        return result.OrderByDescending(r => r.OrderStamp).ToList();
    }

    /// <summary>
    /// Tra cứu thông tin tiến độ đọc dở chéo thiết bị (từ resume.md) theo tên truyện hoặc đường dẫn/tên file.
    /// Giúp thiết bị mới dù chưa từng mở truyện trong lịch sử vẫn nhận diện và mở chính xác chương + trang đã xem.
    /// </summary>
    public CrossDeviceResumeEntry? FindResumeEntryForComic(string? comicTitle, string? rootPathOrFileName = null)
    {
        lock (_crossDeviceLock)
        {
            if (_crossDeviceEntries.Count == 0) return null;

            string cleanTitle = !string.IsNullOrWhiteSpace(comicTitle) ? comicTitle.Trim() : string.Empty;
            string cleanPath = !string.IsNullOrWhiteSpace(rootPathOrFileName) ? NormalizeKey(rootPathOrFileName) : string.Empty;
            string leaf = !string.IsNullOrWhiteSpace(cleanPath)
                ? Path.GetFileName(cleanPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : string.Empty;
            string leafWithoutExt = !string.IsNullOrWhiteSpace(leaf)
                ? Path.GetFileNameWithoutExtension(leaf)
                : string.Empty;

            // 1. So khớp tuyệt đối hoặc so khớp chuẩn hóa theo ComicTitle
            if (!string.IsNullOrEmpty(cleanTitle))
            {
                var match = _crossDeviceEntries.FirstOrDefault(e =>
                    string.Equals(e.ComicTitle, cleanTitle, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeChapterName(e.ComicTitle), NormalizeChapterName(cleanTitle), StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            // 2. So khớp theo tên thư mục hoặc tên file không đuôi (leaf / leafWithoutExt)
            if (!string.IsNullOrEmpty(leafWithoutExt))
            {
                var match = _crossDeviceEntries.FirstOrDefault(e =>
                    string.Equals(e.ComicTitle, leafWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.ComicTitle, leaf, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeChapterName(e.ComicTitle), NormalizeChapterName(leafWithoutExt), StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            // 3. So khớp tương đối nếu tên truyện chứa tên thư mục hoặc ngược lại
            if (!string.IsNullOrEmpty(cleanTitle))
            {
                var match = _crossDeviceEntries.FirstOrDefault(e =>
                    e.ComicTitle.Contains(cleanTitle, StringComparison.OrdinalIgnoreCase) ||
                    cleanTitle.Contains(e.ComicTitle, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            if (!string.IsNullOrEmpty(leafWithoutExt) && leafWithoutExt.Length >= 3)
            {
                var match = _crossDeviceEntries.FirstOrDefault(e =>
                    e.ComicTitle.Contains(leafWithoutExt, StringComparison.OrdinalIgnoreCase) ||
                    leafWithoutExt.Contains(e.ComicTitle, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            return null;
        }
    }

    /// <summary>
    /// Ghép thông minh (Smart 2-Way Merge) nội dung resume.md từ Google Drive với dữ liệu hiện có trên máy.
    /// Giữ nguyên toàn bộ các bộ truyện của thiết bị khác trên Cloud, cập nhật tiến độ mới hơn theo thời gian,
    /// triệt tiêu 100% tình trạng upload ghi đè làm mất dữ liệu của thiết bị khác.
    /// </summary>
    public int MergeCloudResumeContent(string cloudContent, string currentDeviceName)
    {
        if (string.IsNullOrWhiteSpace(cloudContent)) return 0;
        int mergedCount = 0;

        int beginIdx = cloudContent.IndexOf(ResumeStateBeginMarker, StringComparison.Ordinal);
        int endIdx = beginIdx >= 0 ? cloudContent.IndexOf(ResumeStateEndMarker, beginIdx, StringComparison.Ordinal) : -1;
        if (beginIdx < 0 || endIdx <= beginIdx) return 0;

        int fenceStart = cloudContent.IndexOf("```json", beginIdx, StringComparison.OrdinalIgnoreCase);
        if (fenceStart < 0 || fenceStart >= endIdx) return 0;

        int lineEnd = cloudContent.IndexOf('\n', fenceStart);
        if (lineEnd < 0 || lineEnd >= endIdx) return 0;

        int jsonStart = lineEnd + 1;
        int fenceEnd = cloudContent.IndexOf("```", jsonStart, StringComparison.Ordinal);
        if (fenceEnd < 0 || fenceEnd > endIdx) return 0;

        string json = cloudContent.Substring(jsonStart, fenceEnd - jsonStart).Trim();
        if (string.IsNullOrWhiteSpace(json)) return 0;

        try
        {
            var cloudPayload = JsonSerializer.Deserialize<ComicResumePayload>(json, _jsonOptions);
            if (cloudPayload?.Entries == null || cloudPayload.Entries.Count == 0) return 0;

            lock (_crossDeviceLock)
            {
                foreach (var cloudEntry in cloudPayload.Entries)
                {
                    if (string.IsNullOrWhiteSpace(cloudEntry.ComicTitle)) continue;

                    int exIdx = _crossDeviceEntries.FindIndex(e =>
                        string.Equals(e.ComicTitle, cloudEntry.ComicTitle, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(NormalizeChapterName(e.ComicTitle), NormalizeChapterName(cloudEntry.ComicTitle), StringComparison.OrdinalIgnoreCase));

                    if (exIdx >= 0)
                    {
                        var localEntry = _crossDeviceEntries[exIdx];
                        // Nếu cloudEntry có thời gian cập nhật mới hơn -> cập nhật localEntry
                        if (cloudEntry.UpdatedAtUnixSeconds > localEntry.UpdatedAtUnixSeconds)
                        {
                            _crossDeviceEntries[exIdx] = cloudEntry;
                            mergedCount++;
                        }
                    }
                    else
                    {
                        // Truyện trên cloud máy này chưa có -> nạp vào danh sách
                        _crossDeviceEntries.Add(cloudEntry);
                        mergedCount++;
                    }

                    // Tự động sinh portable keys vào _progressMap
                    if (cloudEntry.ChapterIndex > 0)
                    {
                        string chKey = $"{PortableIdPrefix}leaf|{cloudEntry.ComicTitle}::#last_active_chapter";
                        _progressMap[chKey] = cloudEntry.ChapterIndex;
                    }
                    if (cloudEntry.LastReadPage > 0)
                    {
                        if (cloudEntry.ChapterNumber >= 0)
                        {
                            _progressMap[$"{PortableIdPrefix}num|{cloudEntry.ComicTitle}/chap_{cloudEntry.ChapterNumber.ToString(CultureInfo.InvariantCulture)}"] = cloudEntry.LastReadPage;
                            _progressMap[$"{PortableIdPrefix}num|{cloudEntry.ComicTitle}/chap_{((int)cloudEntry.ChapterNumber).ToString(CultureInfo.InvariantCulture)}"] = cloudEntry.LastReadPage;
                        }
                        if (!string.IsNullOrWhiteSpace(cloudEntry.ChapterTitle))
                        {
                            _progressMap[$"{PortableIdPrefix}pair|{cloudEntry.ComicTitle}/{cloudEntry.ChapterTitle}"] = cloudEntry.LastReadPage;
                            _progressMap[$"{PortableIdPrefix}norm|{cloudEntry.ComicTitle}/{NormalizeChapterName(cloudEntry.ChapterTitle)}"] = cloudEntry.LastReadPage;
                        }
                        _progressMap[$"{PortableIdPrefix}file|{cloudEntry.ComicTitle}"] = cloudEntry.LastReadPage;
                    }
                }
            }

            if (mergedCount > 0)
            {
                AutoResolveAndRemapLocalPaths(cloudPayload.Entries);
                MarkDirtyAndScheduleFlush();
            }

            return mergedCount;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReadProgressStore] MergeCloudResumeContent error: {ex.Message}");
            return 0;
        }
    }

    public void SaveLastLibraryRootPath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        string clean = NormalizeKey(folderPath);
        if (string.IsNullOrWhiteSpace(clean)) return;

        // Xóa khóa last_root cũ và lưu khóa mới
        foreach (var k in _progressMap.Keys.Where(k => k.StartsWith(LastRootPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _progressMap.TryRemove(k, out _);
        }

        int stamp = GetCurrentOrderStamp();
        _progressMap[LastRootPrefix + clean] = stamp;
        _progressMap[FolderHistoryPrefix + clean] = stamp;
        MarkDirtyAndScheduleFlush();
    }

    public string? GetLastLibraryRootPath()
    {
        foreach (var kv in _progressMap)
        {
            if (kv.Key.StartsWith(LastRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string path = kv.Key.Substring(LastRootPrefix.Length).Trim();
                if (!string.IsNullOrWhiteSpace(path) && (Directory.Exists(path) || File.Exists(path)))
                {
                    return path;
                }
            }
        }
        return null;
    }

    public List<string> GetKnownLibraryFolders()
    {
        var folders = new List<(string Path, int Order)>();
        foreach (var kv in _progressMap)
        {
            if (kv.Key.StartsWith(FolderHistoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string path = kv.Key.Substring(FolderHistoryPrefix.Length).Trim();
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    folders.Add((path, kv.Value));
                }
            }
        }

        return folders
            .OrderByDescending(f => f.Order)
            .Select(f => f.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Lấy danh sách dấu trang (Bookmark) của bộ truyện hiện tại (hoặc toàn bộ nếu không lọc).
    /// </summary>
    public List<ReaderBookmarkItem> GetBookmarks(string? comicRootOrTitle = null)
    {
        lock (_bookmarkLock)
        {
            if (string.IsNullOrWhiteSpace(comicRootOrTitle))
            {
                return _bookmarks.OrderByDescending(b => b.CreatedAtUnixSeconds).ToList();
            }

            string cleanRoot = NormalizeKey(comicRootOrTitle);
            var filtered = _bookmarks
                .Where(b =>
                    string.Equals(NormalizeKey(b.ComicRootPath), cleanRoot, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(b.ComicTitle, comicRootOrTitle, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(b => b.CreatedAtUnixSeconds)
                .ToList();

            // Chỉ trả về danh sách bookmark thuộc đúng bộ truyện được lọc, không để tàn dư truyện cũ
            return filtered;
        }
    }

    public void AddOrUpdateBookmark(ReaderBookmarkItem item)
    {
        if (item == null) return;
        lock (_bookmarkLock)
        {
            // Nếu đã có bookmark trùng chương + trùng trang thì cập nhật Memo và thời gian
            var existing = _bookmarks.FirstOrDefault(b =>
                string.Equals(b.ChapterProgressKey, item.ChapterProgressKey, StringComparison.OrdinalIgnoreCase) &&
                b.PageIndex == item.PageIndex);

            if (existing != null)
            {
                existing.Memo = item.Memo;
                existing.TotalPages = item.TotalPages;
                existing.CreatedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            else
            {
                _bookmarks.Insert(0, item);
            }

            SaveBookmarksToDisk();
        }
    }

    public void RemoveBookmark(string bookmarkId)
    {
        if (string.IsNullOrWhiteSpace(bookmarkId)) return;
        lock (_bookmarkLock)
        {
            int removed = _bookmarks.RemoveAll(b => string.Equals(b.Id, bookmarkId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveBookmarksToDisk();
            }
        }
    }

    public void ClearAllBookmarks()
    {
        lock (_bookmarkLock)
        {
            _bookmarks.Clear();
            SaveBookmarksToDisk();
        }
    }

    /// <summary>
    /// Xóa toàn bộ dấu trang (Bookmarks) thuộc về riêng một bộ truyện hoặc tập tin cụ thể.
    /// Trả về số lượng bookmark đã bị xóa.
    /// </summary>
    public int ClearBookmarksForComic(string comicRootOrFilePath, string? comicTitle = null)
    {
        if (string.IsNullOrWhiteSpace(comicRootOrFilePath) && string.IsNullOrWhiteSpace(comicTitle)) return 0;

        string cleanPath = !string.IsNullOrWhiteSpace(comicRootOrFilePath) ? NormalizeKey(comicRootOrFilePath) : string.Empty;
        string leaf = !string.IsNullOrWhiteSpace(cleanPath)
            ? Path.GetFileName(cleanPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : string.Empty;

        lock (_bookmarkLock)
        {
            int removed = _bookmarks.RemoveAll(b =>
            {
                if (!string.IsNullOrEmpty(cleanPath) &&
                    string.Equals(NormalizeKey(b.ComicRootPath), cleanPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(comicTitle) &&
                    string.Equals(b.ComicTitle, comicTitle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(leaf))
                {
                    string bLeaf = Path.GetFileName(NormalizeKey(b.ComicRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (string.Equals(bLeaf, leaf, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(b.ComicTitle, leaf, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                if (!string.IsNullOrEmpty(cleanPath) && !string.IsNullOrWhiteSpace(b.ChapterLocalPath) &&
                    (b.ChapterLocalPath.StartsWith(cleanPath, StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrEmpty(leaf) && b.ChapterLocalPath.Contains(leaf, StringComparison.OrdinalIgnoreCase))))
                {
                    return true;
                }

                return false;
            });

            if (removed > 0)
            {
                SaveBookmarksToDisk();
            }
            return removed;
        }
    }

    private void MigrateLegacyStorageFiles()
    {
        try
        {
            if (OperatingSystem.IsAndroid()) return;

            string configDir = ComicScraperService.GetConfigDirectory();
            string autoSaveDir = QueueBackupService.GetAutoSaveDirectory();
            if (string.Equals(configDir, autoSaveDir, StringComparison.OrdinalIgnoreCase)) return;

            // 1. Chuyển resume.md cũ ở root .portable sang .portable\autosave
            string legacyResume = Path.Combine(configDir, "resume.md");
            string targetResume = Path.Combine(autoSaveDir, ResumeFileName);
            if (File.Exists(legacyResume))
            {
                if (!File.Exists(targetResume))
                {
                    File.Move(legacyResume, targetResume);
                }
                else
                {
                    File.Delete(legacyResume);
                }
            }

            // 2. Chuyển read_progress.json cũ ở root .portable
            string legacyProgress = Path.Combine(configDir, "read_progress.json");
            if (File.Exists(legacyProgress))
            {
                try
                {
                    string json = File.ReadAllText(legacyProgress);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                    if (dict != null)
                    {
                        foreach (var kv in dict)
                        {
                            if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value > 0)
                            {
                                _progressMap.TryAdd(NormalizeKey(kv.Key), kv.Value);
                            }
                        }
                    }
                }
                catch { }
                try { File.Delete(legacyProgress); } catch { }
            }

            // 3. Chuyển reader_bookmarks.json cũ ở root .portable
            string legacyBookmarksJson = Path.Combine(configDir, "reader_bookmarks.json");
            string targetBookmarksMd = Path.Combine(autoSaveDir, BookmarksFileName);
            if (File.Exists(legacyBookmarksJson))
            {
                try
                {
                    string json = File.ReadAllText(legacyBookmarksJson);
                    var list = JsonSerializer.Deserialize<List<ReaderBookmarkItem>>(json);
                    if (list != null && list.Count > 0)
                    {
                        lock (_bookmarkLock)
                        {
                            foreach (var b in list)
                            {
                                if (b != null && !_bookmarks.Any(x => x.Id == b.Id))
                                {
                                    _bookmarks.Add(b);
                                }
                            }
                        }
                        if (!File.Exists(targetBookmarksMd))
                        {
                            SaveBookmarksToDisk();
                        }
                    }
                }
                catch { }
                try { File.Delete(legacyBookmarksJson); } catch { }
            }

            // 4. Dọn dẹp saved_reader_bookmarks.md và autosave_reader.md cũ ở root .portable
            string legacySavedReader = Path.Combine(configDir, "saved_reader_bookmarks.md");
            if (File.Exists(legacySavedReader))
            {
                try
                {
                    string content = File.ReadAllText(legacySavedReader);
                    ParseReaderMarkdownContent(content, mergeWithExisting: true);
                }
                catch { }
                try { File.Delete(legacySavedReader); } catch { }
            }

            string legacyAutoSaveReader = Path.Combine(configDir, "autosave_reader.md");
            if (File.Exists(legacyAutoSaveReader))
            {
                try { File.Delete(legacyAutoSaveReader); } catch { }
            }

            // 5. Dọn dẹp autosave.md và autosave mirror.md còn sót ở root .portable
            string rootAutoSave = Path.Combine(configDir, "autosave.md");
            string rootMirror = Path.Combine(configDir, "autosave mirror.md");
            string rootMirrorAlt = Path.Combine(configDir, "autosave_mirror.md");
            if (File.Exists(rootAutoSave)) try { File.Delete(rootAutoSave); } catch { }
            if (File.Exists(rootMirror)) try { File.Delete(rootMirror); } catch { }
            if (File.Exists(rootMirrorAlt)) try { File.Delete(rootMirrorAlt); } catch { }
        }
        catch { }
    }

    public void SaveBookmarksToDisk()
    {
        try
        {
            string filePath = GetBookmarksFilePath();
            string tmpPath = filePath + ".tmp";
            string mdContent = BuildBookmarksMarkdownContent();
            File.WriteAllText(tmpPath, mdContent, Encoding.UTF8);
            File.Move(tmpPath, filePath, overwrite: true);
        }
        catch { }
    }

    private void LoadBookmarksFromDisk()
    {
        try
        {
            string filePath = GetBookmarksFilePath();
            if (!File.Exists(filePath))
            {
                string legacyJson = Path.Combine(QueueBackupService.GetAutoSaveDirectory(), "reader_bookmarks.json");
                if (File.Exists(legacyJson))
                {
                    string json = File.ReadAllText(legacyJson);
                    var list = JsonSerializer.Deserialize<List<ReaderBookmarkItem>>(json);
                    if (list != null)
                    {
                        lock (_bookmarkLock)
                        {
                            _bookmarks.Clear();
                            _bookmarks.AddRange(list);
                        }
                        SaveBookmarksToDisk();
                    }
                    try { File.Delete(legacyJson); } catch { }
                }
                return;
            }

            string content = File.ReadAllText(filePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(content)) return;

            var payload = TryParseBookmarksJsonPayload(content);
            if (payload?.Bookmarks != null && payload.Bookmarks.Count > 0)
            {
                lock (_bookmarkLock)
                {
                    _bookmarks.Clear();
                    _bookmarks.AddRange(payload.Bookmarks);
                }
                return;
            }

            string trimmed = content.Trim();
            if (trimmed.StartsWith("["))
            {
                var list = JsonSerializer.Deserialize<List<ReaderBookmarkItem>>(trimmed);
                if (list != null)
                {
                    lock (_bookmarkLock)
                    {
                        _bookmarks.Clear();
                        _bookmarks.AddRange(list);
                    }
                    SaveBookmarksToDisk();
                }
            }
        }
        catch { }
    }

    public void SaveHistoryToDisk()
    {
        try
        {
            string filePath = GetHistoryFilePath();
            string tmpPath = filePath + ".tmp";
            string mdContent = BuildHistoryMarkdownContent();
            File.WriteAllText(tmpPath, mdContent, Encoding.UTF8);
            File.Move(tmpPath, filePath, overwrite: true);
        }
        catch { }
    }

    private void LoadHistoryFromDisk()
    {
        try
        {
            string filePath = GetHistoryFilePath();
            if (!File.Exists(filePath)) return;

            string content = File.ReadAllText(filePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(content)) return;

            var payload = TryParseHistoryJsonPayload(content);
            if (payload?.History != null && payload.History.Count > 0)
            {
                foreach (var h in payload.History)
                {
                    if (string.IsNullOrWhiteSpace(h.LocalPath)) continue;
                    string norm = NormalizeKey(h.LocalPath);
                    if (h.OrderStamp > 0)
                    {
                        // CHỈ GHI NHẬN LỊCH SỬ MỞ TRUYỆN, TUYỆT ĐỐI KHÔNG GHI ĐÈ TIẾN TRÌNH ĐỌC TRANG CỦA RESUME.MD
                        _progressMap[HistoryPrefix + norm] = h.OrderStamp;
                    }
                }
            }
        }
        catch { }
    }

    private void RegisterComicFromChapterKeyInternal(string normalizedChapterKey, bool touchTimestamp)
    {
        if (string.IsNullOrWhiteSpace(normalizedChapterKey) || normalizedChapterKey.StartsWith("@", StringComparison.Ordinal))
            return;

        string basePath = normalizedChapterKey;
        int sepIdx = normalizedChapterKey.IndexOf("::", StringComparison.Ordinal);
        if (sepIdx > 0)
        {
            basePath = normalizedChapterKey.Substring(0, sepIdx);
        }

        try
        {
            if (File.Exists(basePath))
            {
                RegisterReadComic(basePath, touchTimestamp);
                return;
            }

            if (Directory.Exists(basePath))
            {
                // Nếu basePath là thư mục chương (chứa ảnh trực tiếp), thư mục cha là bộ truyện
                bool hasDirectImages = Directory.EnumerateFiles(basePath).Any(LibraryScannerService.IsImageFile);
                if (hasDirectImages)
                {
                    string? parentDir = Path.GetDirectoryName(basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (!string.IsNullOrWhiteSpace(parentDir) && Directory.Exists(parentDir))
                    {
                        RegisterReadComic(parentDir, touchTimestamp);
                        return;
                    }
                }

                RegisterReadComic(basePath, touchTimestamp);
            }
        }
        catch { }
    }

    private void MarkDirtyAndScheduleFlush()
    {
        _isDirty = true;
        try
        {
            _debounceTimer.Change(500, Timeout.Infinite);
        }
        catch { }
    }

    public void FlushImmediately()
    {
        if (!_isDirty) return;

        lock (_saveLock)
        {
            if (!_isDirty) return;
            _isDirty = false;

            try
            {
                string filePath = GetStoreFilePath();
                string tmpPath = filePath + ".tmp";
                string deviceName = string.Empty;
                try
                {
                    deviceName = GoogleDriveSyncService.Instance.CurrentDeviceName;
                }
                catch { }
                if (string.IsNullOrWhiteSpace(deviceName))
                {
                    deviceName = Environment.MachineName;
                }

                string mdContent = BuildResumeMarkdownContent(deviceName);
                File.WriteAllText(tmpPath, mdContent, Encoding.UTF8);
                File.Move(tmpPath, filePath, overwrite: true);

                // Đồng thời lưu lịch sử đọc vào history.md trong .portable\autosave
                SaveHistoryToDisk();
            }
            catch { }
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            string resumeFilePath = GetStoreFilePath();
            string legacyJsonFilePath = Path.Combine(ComicScraperService.GetConfigDirectory(), "read_progress.json");

            string? activePath = null;
            if (File.Exists(resumeFilePath))
            {
                activePath = resumeFilePath;
            }
            else if (File.Exists(legacyJsonFilePath))
            {
                activePath = legacyJsonFilePath;
            }

            if (string.IsNullOrEmpty(activePath) || !File.Exists(activePath)) return;

            string content = File.ReadAllText(activePath);
            if (string.IsNullOrWhiteSpace(content)) return;

            bool loaded = false;
            // 1. Phục hồi từ tệp Markdown chứa khối JSON máy đọc (resume.md)
            if (content.Contains(ResumeStateBeginMarker, StringComparison.Ordinal))
            {
                var restoreResult = ParseResumeMarkdownContent(content, mergeWithExisting: true);
                loaded = restoreResult.HasValidStructure;
            }

            // 2. Fallback: Parse trực tiếp nếu là JSON thuần (read_progress.json cũ)
            if (!loaded)
            {
                string jsonToParse = content.Trim();
                if (jsonToParse.StartsWith("{", StringComparison.Ordinal))
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(jsonToParse);
                    if (dict != null && dict.Count > 0)
                    {
                        foreach (var kv in dict)
                        {
                            if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value > 0)
                            {
                                string normKey = NormalizeKey(kv.Key);
                                _progressMap[normKey] = kv.Value;
                            }
                        }
                        loaded = true;
                    }
                }
            }

            // 3. Nếu đọc từ file legacy json cũ mà chưa có resume.md -> lưu ngay ra resume.md
            if (loaded && !File.Exists(resumeFilePath))
            {
                _isDirty = true;
                FlushImmediately();
            }

            // Tự động đồng bộ khóa định danh linh hoạt (@id|...) và lịch sử đọc (@history|...) từ dữ liệu sẵn có
            var existingKeys = _progressMap.Keys.ToList();
            foreach (string key in existingKeys)
            {
                if (key.StartsWith("@", StringComparison.Ordinal)) continue;
                int val = _progressMap[key];

                foreach (string portableKey in BuildPortableIdentityKeys(key))
                {
                    _progressMap.TryAdd(portableKey, val);
                }

                RegisterComicFromChapterKeyInternal(key, touchTimestamp: false);
            }
        }
        catch { }
    }

    public void ClearAllProgress()
    {
        var keysToRemove = _progressMap.Keys
            .Where(k => !k.StartsWith(PinnedFolderPrefix, StringComparison.Ordinal) &&
                        !k.StartsWith(LastRootPrefix, StringComparison.Ordinal))
            .ToList();

        foreach (var k in keysToRemove)
        {
            _progressMap.TryRemove(k, out _);
        }
        FlushImmediately();
    }

    // =========================================================================
    // SAO LƯU & PHỤC HỒI DẤU TRANG & LỊCH SỬ ĐỌC RA FILE MARKDOWN (.MD)
    // =========================================================================

    public static string GetDefaultReaderBackupFilePath()
    {
        string baseDir = QueueBackupService.GetAutoSaveDirectory();
        return Path.Combine(baseDir, AutoSaveReaderFileName);
    }

    /// <summary>
    /// Xuất toàn bộ danh sách Dấu trang (Bookmarks) và Lịch sử đọc (Reading History)
    /// ra chuỗi Markdown (.md) 2 tầng (Bảng trực quan cho người đọc + Snapshot JSON cho máy đọc 100% chuẩn xác).
    /// </summary>
    public string BuildReaderMarkdownContent()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# COMIC DOWNLOADER GMTPC - BACKUP DẤU TRANG & LỊCH SỬ ĐỌC");
        sb.AppendLine($"> Thời gian sao lưu: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | Hệ điều hành: {Environment.OSVersion.Platform}");

        // 1. BẢNG DẤU TRANG (BOOKMARKS)
        List<ReaderBookmarkItem> bookmarksSnapshot;
        lock (_bookmarkLock)
        {
            bookmarksSnapshot = new List<ReaderBookmarkItem>(_bookmarks);
        }

        sb.AppendLine();
        sb.AppendLine("## 🔖 Dấu Trang Đã Lưu (Bookmarks)");
        sb.AppendLine($"> Tổng cộng: {bookmarksSnapshot.Count} dấu trang");
        sb.AppendLine();
        sb.AppendLine("| STT | Bộ Truyện | Chương | Vị Trí Trang | % | Ghi Chú | Thời Gian | Đường Dẫn |");
        sb.AppendLine("| :---: | :--- | :--- | :---: | :---: | :--- | :---: | :--- |");

        int bIdx = 1;
        foreach (var b in bookmarksSnapshot)
        {
            string safeComic = EscapeMarkdown(b.DisplayComicTitle);
            string safeChap = EscapeMarkdown(b.FullChapterTitle);
            string pageText = b.RightDisplayText;
            string pct = b.PercentText;
            string memo = EscapeMarkdown(!string.IsNullOrWhiteSpace(b.Memo) ? b.Memo : "-");
            string date = b.CreatedDateText;
            string safePath = EscapeMarkdown(b.ChapterLocalPath ?? b.ComicRootPath ?? string.Empty);
            sb.AppendLine($"| {bIdx++} | {safeComic} | {safeChap} | {pageText} | {pct} | {memo} | {date} | {safePath} |");
        }

        // 2. BẢNG LỊCH SỬ ĐỌC (READING HISTORY)
        var historyPaths = GetAllHistoryPaths(checkPathExists: false);
        sb.AppendLine();
        sb.AppendLine("## 🕒 Lịch Sử Đọc Gần Đây (Reading History)");
        sb.AppendLine($"> Tổng cộng: {historyPaths.Count} bộ truyện");
        sb.AppendLine();
        sb.AppendLine("| STT | Tên Truyện / File | Chương Đang Đọc | Trang Đang Đọc | Lần Đọc Gần Nhất | Đường Dẫn |");
        sb.AppendLine("| :---: | :--- | :---: | :---: | :---: | :--- |");

        int hIdx = 1;
        foreach (var path in historyPaths)
        {
            string title = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            int lastChap = GetLastActiveChapterIndex(path);
            int lastPage = GetLastReadPage(path);
            string chapText = lastChap > 0 ? $"Chương {lastChap}" : "-";
            string pageText = lastPage > 0 ? $"Trang {lastPage}" : "-";
            int order = GetComicLastReadOrder(path);
            string timeText = order > 0
                ? DateTimeOffset.FromUnixTimeSeconds(order + EpochOffsetSeconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "-";
            string safePath = EscapeMarkdown(path);
            sb.AppendLine($"| {hIdx++} | {EscapeMarkdown(title)} | {chapText} | {pageText} | {timeText} | {safePath} |");
        }

        // 3. KHỐI JSON FULL SNAPSHOT CHO MÁY ĐỌC (MACHINE-READABLE)
        var snapshot = new ReaderBackupPayload
        {
            SavedAt = DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
            Bookmarks = bookmarksSnapshot,
            ProgressMap = new Dictionary<string, int>(_progressMap, StringComparer.OrdinalIgnoreCase)
        };

        string json = JsonSerializer.Serialize(snapshot, _jsonOptions);

        sb.AppendLine();
        sb.AppendLine("## Full Reader State Snapshot");
        sb.AppendLine(ReaderStateBeginMarker);
        sb.AppendLine("```json");
        sb.AppendLine(json);
        sb.AppendLine("```");
        sb.AppendLine(ReaderStateEndMarker);

        return sb.ToString();
    }

    /// <summary>
    /// Xuất toàn bộ danh sách Dấu trang và Lịch sử ra file Markdown (.md).
    /// </summary>
    public async Task<string> SaveReaderToMarkdownAsync(string? customPath = null)
    {
        string targetPath = string.IsNullOrWhiteSpace(customPath) ? GetDefaultReaderBackupFilePath() : customPath;
        string content = BuildReaderMarkdownContent();

        await Task.Run(() =>
        {
            lock (_saveLock)
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
        }).ConfigureAwait(false);

        return targetPath;
    }

    /// <summary>
    /// Phục hồi Dấu trang & Lịch sử đọc từ nội dung file Markdown (.md).
    /// </summary>
    public ReaderRestoreResult ParseReaderMarkdownContent(string content, bool mergeWithExisting = true, string? loadedFromPath = null)
    {
        var result = new ReaderRestoreResult { LoadedFromPath = loadedFromPath ?? string.Empty };
        if (string.IsNullOrWhiteSpace(content)) return result;

        // 1. ƯU TIÊN ĐỌC TỪ KHỐI JSON FULL SNAPSHOT
        var payload = TryParseReaderJsonPayload(content);
        if (payload != null)
        {
            result.HasValidStructure = true;

            // Merge / restore bookmarks
            if (payload.Bookmarks != null && payload.Bookmarks.Count > 0)
            {
                lock (_bookmarkLock)
                {
                    if (!mergeWithExisting)
                    {
                        _bookmarks.Clear();
                    }

                    foreach (var item in payload.Bookmarks)
                    {
                        if (item == null) continue;
                        var existing = _bookmarks.FirstOrDefault(b =>
                            string.Equals(b.ChapterProgressKey, item.ChapterProgressKey, StringComparison.OrdinalIgnoreCase) &&
                            b.PageIndex == item.PageIndex);

                        if (existing != null)
                        {
                            if (!string.IsNullOrWhiteSpace(item.Memo)) existing.Memo = item.Memo;
                            existing.TotalPages = Math.Max(existing.TotalPages, item.TotalPages);
                            existing.CreatedAtUnixSeconds = Math.Max(existing.CreatedAtUnixSeconds, item.CreatedAtUnixSeconds);
                        }
                        else
                        {
                            _bookmarks.Add(item);
                            result.RestoredBookmarks++;
                        }
                    }
                    SaveBookmarksToDisk();
                }
            }

            // Merge / restore progressMap
            if (payload.ProgressMap != null && payload.ProgressMap.Count > 0)
            {
                if (!mergeWithExisting)
                {
                    _progressMap.Clear();
                }

                foreach (var kv in payload.ProgressMap)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value <= 0) continue;
                    string normKey = NormalizeKey(kv.Key);
                    _progressMap.AddOrUpdate(normKey, kv.Value, (_, oldVal) => Math.Max(oldVal, kv.Value));
                    result.RestoredProgressEntries++;

                    if (normKey.StartsWith(HistoryPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        result.RestoredHistory++;
                    }

                    // Tự động tái tạo portable keys
                    if (!normKey.StartsWith("@", StringComparison.Ordinal))
                    {
                        foreach (string portableKey in BuildPortableIdentityKeys(normKey))
                        {
                            _progressMap.TryAdd(portableKey, kv.Value);
                        }
                    }
                }
                FlushImmediately();
            }

            if (result.RestoredBookmarks == 0 && payload.Bookmarks != null)
            {
                result.RestoredBookmarks = payload.Bookmarks.Count;
            }
            if (result.RestoredHistory == 0 && payload.ProgressMap != null)
            {
                result.RestoredHistory = payload.ProgressMap.Keys.Count(k => k.StartsWith(HistoryPrefix, StringComparison.OrdinalIgnoreCase));
            }

            return result;
        }

        // 2. FALLBACK: ĐỌC TỪ BẢNG MARKDOWN NẾU KHÔNG CÓ KHỐI JSON
        string[] lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var bookmarkRowRegex8 = new Regex(@"^\|\s*(\d+)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|$", RegexOptions.Compiled);
        var historyRowRegex6 = new Regex(@"^\|\s*(\d+)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|\s*(.*?)\s*\|$", RegexOptions.Compiled);

        bool inBookmarkSection = false;
        bool inHistorySection = false;

        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("# COMIC DOWNLOADER GMTPC", StringComparison.OrdinalIgnoreCase))
            {
                result.HasValidStructure = true;
            }

            if (trimmed.StartsWith("## 🔖 Dấu Trang", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("## Bookmarks", StringComparison.OrdinalIgnoreCase))
            {
                inBookmarkSection = true;
                inHistorySection = false;
                result.HasValidStructure = true;
                continue;
            }

            if (trimmed.StartsWith("## 🕒 Lịch Sử", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("## Reading History", StringComparison.OrdinalIgnoreCase))
            {
                inBookmarkSection = false;
                inHistorySection = true;
                result.HasValidStructure = true;
                continue;
            }

            if (trimmed.StartsWith("##", StringComparison.Ordinal) || trimmed.StartsWith("<!--", StringComparison.Ordinal))
            {
                inBookmarkSection = false;
                inHistorySection = false;
                continue;
            }

            if (!trimmed.StartsWith("|") || trimmed.Contains("---") || trimmed.StartsWith("| STT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (inBookmarkSection)
            {
                var mb = bookmarkRowRegex8.Match(trimmed);
                if (mb.Success)
                {
                    result.HasValidStructure = true;
                    string comicTitle = UnescapeMarkdown(mb.Groups[2].Value);
                    string chapterTitle = UnescapeMarkdown(mb.Groups[3].Value);
                    string pageText = mb.Groups[4].Value.Trim();
                    string memo = UnescapeMarkdown(mb.Groups[6].Value);
                    if (memo == "-") memo = string.Empty;
                    string path = UnescapeMarkdown(mb.Groups[8].Value);

                    int pageIndex = 0;
                    int totalPages = 0;
                    if (pageText.Contains('/'))
                    {
                        var parts = pageText.Split('/');
                        if (int.TryParse(parts[0].Trim(), out int p1)) pageIndex = Math.Max(0, p1 - 1);
                        if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out int tot)) totalPages = tot;
                    }
                    else if (int.TryParse(pageText, out int p2))
                    {
                        pageIndex = Math.Max(0, p2 - 1);
                    }

                    var bm = new ReaderBookmarkItem
                    {
                        ComicTitle = comicTitle,
                        ChapterTitle = chapterTitle,
                        PageIndex = pageIndex,
                        TotalPages = totalPages,
                        Memo = memo,
                        ChapterLocalPath = path,
                        ComicRootPath = !string.IsNullOrEmpty(path) ? Path.GetDirectoryName(path) ?? string.Empty : string.Empty,
                        ChapterProgressKey = !string.IsNullOrEmpty(path) ? NormalizeKey(path) : $"{comicTitle}/{chapterTitle}",
                        CreatedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };

                    AddOrUpdateBookmark(bm);
                    result.RestoredBookmarks++;
                }
            }
            else if (inHistorySection)
            {
                var mh = historyRowRegex6.Match(trimmed);
                if (mh.Success)
                {
                    result.HasValidStructure = true;
                    string path = UnescapeMarkdown(mh.Groups[6].Value);

                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        RegisterReadComic(path, touchTimestamp: true);
                        result.RestoredHistory++;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Đọc và phục hồi Dấu trang & Lịch sử đọc từ file Markdown trên đĩa.
    /// </summary>
    public async Task<ReaderRestoreResult> LoadReaderFromMarkdownAsync(string? customPath = null, bool mergeWithExisting = true)
    {
        string targetPath = string.IsNullOrWhiteSpace(customPath) ? GetDefaultReaderBackupFilePath() : customPath;
        if (!File.Exists(targetPath)) return new ReaderRestoreResult { LoadedFromPath = targetPath };

        string content = await File.ReadAllTextAsync(targetPath, Encoding.UTF8).ConfigureAwait(false);
        return ParseReaderMarkdownContent(content, mergeWithExisting, targetPath);
    }

    private static ReaderBackupPayload? TryParseReaderJsonPayload(string content)
    {
        int beginIdx = content.IndexOf(ReaderStateBeginMarker, StringComparison.Ordinal);
        if (beginIdx < 0) return null;

        int endIdx = content.IndexOf(ReaderStateEndMarker, beginIdx, StringComparison.Ordinal);
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
            return JsonSerializer.Deserialize<ReaderBackupPayload>(json, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static BookmarksBackupPayload? TryParseBookmarksJsonPayload(string content)
    {
        int beginIdx = content.IndexOf(BookmarksStateBeginMarker, StringComparison.Ordinal);
        if (beginIdx < 0) return null;

        int endIdx = content.IndexOf(BookmarksStateEndMarker, beginIdx, StringComparison.Ordinal);
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
            return JsonSerializer.Deserialize<BookmarksBackupPayload>(json, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static HistoryBackupPayload? TryParseHistoryJsonPayload(string content)
    {
        int beginIdx = content.IndexOf(HistoryStateBeginMarker, StringComparison.Ordinal);
        if (beginIdx < 0) return null;

        int endIdx = content.IndexOf(HistoryStateEndMarker, beginIdx, StringComparison.Ordinal);
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
            return JsonSerializer.Deserialize<HistoryBackupPayload>(json, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Phục hồi Dấu trang từ nội dung tệp bookmarks.md.
    /// </summary>
    public int ParseBookmarksMarkdownContent(string content, bool mergeWithExisting = true)
    {
        if (string.IsNullOrWhiteSpace(content)) return 0;
        int restored = 0;

        var payload = TryParseBookmarksJsonPayload(content);
        if (payload?.Bookmarks != null && payload.Bookmarks.Count > 0)
        {
            lock (_bookmarkLock)
            {
                if (!mergeWithExisting)
                {
                    _bookmarks.Clear();
                }

                foreach (var item in payload.Bookmarks)
                {
                    if (item == null) continue;
                    var existing = _bookmarks.FirstOrDefault(b =>
                        string.Equals(b.ChapterProgressKey, item.ChapterProgressKey, StringComparison.OrdinalIgnoreCase) &&
                        b.PageIndex == item.PageIndex);

                    if (existing != null)
                    {
                        if (!string.IsNullOrWhiteSpace(item.Memo)) existing.Memo = item.Memo;
                        existing.TotalPages = Math.Max(existing.TotalPages, item.TotalPages);
                        existing.CreatedAtUnixSeconds = Math.Max(existing.CreatedAtUnixSeconds, item.CreatedAtUnixSeconds);
                    }
                    else
                    {
                        _bookmarks.Add(item);
                        restored++;
                    }
                }
                SaveBookmarksToDisk();
            }
            return restored > 0 ? restored : payload.Bookmarks.Count;
        }

        // Fallback: nếu tệp dùng định dạng Reader cũ (saved_reader_bookmarks.md)
        var readerPayload = TryParseReaderJsonPayload(content);
        if (readerPayload?.Bookmarks != null && readerPayload.Bookmarks.Count > 0)
        {
            lock (_bookmarkLock)
            {
                if (!mergeWithExisting)
                {
                    _bookmarks.Clear();
                }

                foreach (var item in readerPayload.Bookmarks)
                {
                    if (item == null) continue;
                    var existing = _bookmarks.FirstOrDefault(b =>
                        string.Equals(b.ChapterProgressKey, item.ChapterProgressKey, StringComparison.OrdinalIgnoreCase) &&
                        b.PageIndex == item.PageIndex);

                    if (existing != null)
                    {
                        if (!string.IsNullOrWhiteSpace(item.Memo)) existing.Memo = item.Memo;
                        existing.TotalPages = Math.Max(existing.TotalPages, item.TotalPages);
                        existing.CreatedAtUnixSeconds = Math.Max(existing.CreatedAtUnixSeconds, item.CreatedAtUnixSeconds);
                    }
                    else
                    {
                        _bookmarks.Add(item);
                        restored++;
                    }
                }
                SaveBookmarksToDisk();
            }
            return restored > 0 ? restored : readerPayload.Bookmarks.Count;
        }

        return restored;
    }

    /// <summary>
    /// Phục hồi Lịch sử đọc từ nội dung tệp history.md.
    /// CHỈ PHỤC HỒI DANH SÁCH FILE MỞ TRUYỆN, TUYỆT ĐỐI KHÔNG CAN THIỆP TIẾN TRÌNH ĐỌC DỞ CỦA RESUME.MD.
    /// </summary>
    public int ParseHistoryMarkdownContent(string content, bool mergeWithExisting = true)
    {
        if (string.IsNullOrWhiteSpace(content)) return 0;
        int restored = 0;

        var payload = TryParseHistoryJsonPayload(content);
        if (payload?.History != null && payload.History.Count > 0)
        {
            if (!mergeWithExisting)
            {
                ClearAllHistory();
            }

            foreach (var h in payload.History)
            {
                if (string.IsNullOrWhiteSpace(h.LocalPath)) continue;
                string norm = NormalizeKey(h.LocalPath);
                if (h.OrderStamp > 0)
                {
                    // CHỈ GHI NHẬN LỊCH SỬ MỞ TRUYỆN, KHÔNG GHI ĐÈ TIẾN TRÌNH ĐỌC TRANG
                    _progressMap[HistoryPrefix + norm] = h.OrderStamp;
                    restored++;
                }
            }

            SaveHistoryToDisk();
            FlushImmediately();
            return restored;
        }

        return restored;
    }

    public string BuildBookmarksMarkdownContent()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# COMIC DOWNLOADER GMTPC - DẤU TRANG ĐÃ LƯU (BOOKMARKS.MD)");
        sb.AppendLine($"> Thời gian sao lưu: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | Hệ điều hành: {Environment.OSVersion.Platform}");

        List<ReaderBookmarkItem> bookmarksSnapshot;
        lock (_bookmarkLock)
        {
            bookmarksSnapshot = new List<ReaderBookmarkItem>(_bookmarks);
        }

        sb.AppendLine();
        sb.AppendLine("## 🔖 Danh Sách Dấu Trang (Bookmarks)");
        sb.AppendLine($"> Tổng cộng: {bookmarksSnapshot.Count} dấu trang");
        sb.AppendLine();
        sb.AppendLine("| STT | Bộ Truyện | Chương | Vị Trí Trang | % | Ghi Chú | Thời Gian | Đường Dẫn |");
        sb.AppendLine("| :---: | :--- | :--- | :---: | :---: | :--- | :---: | :--- |");

        int bIdx = 1;
        foreach (var b in bookmarksSnapshot)
        {
            string safeComic = EscapeMarkdown(b.DisplayComicTitle);
            string safeChap = EscapeMarkdown(b.FullChapterTitle);
            string pageText = b.RightDisplayText;
            string pct = b.PercentText;
            string memo = EscapeMarkdown(!string.IsNullOrWhiteSpace(b.Memo) ? b.Memo : "-");
            string date = b.CreatedDateText;
            string safePath = EscapeMarkdown(b.ChapterLocalPath ?? b.ComicRootPath ?? string.Empty);
            sb.AppendLine($"| {bIdx++} | {safeComic} | {safeChap} | {pageText} | {pct} | {memo} | {date} | {safePath} |");
        }

        var payload = new BookmarksBackupPayload
        {
            SavedAt = DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
            Bookmarks = bookmarksSnapshot
        };

        string json = JsonSerializer.Serialize(payload, _jsonOptions);

        sb.AppendLine();
        sb.AppendLine("## Machine-Readable Bookmarks State");
        sb.AppendLine(BookmarksStateBeginMarker);
        sb.AppendLine("```json");
        sb.AppendLine(json);
        sb.AppendLine("```");
        sb.AppendLine(BookmarksStateEndMarker);

        return sb.ToString();
    }

    /// <summary>
    /// Xuất danh sách Lịch sử mở truyện ra file history.md.
    /// CHỈ PHỤ TRÁCH LƯU FILE MỞ TRUYỆN, TUYỆT ĐỐI KHÔNG LƯU TIẾN TRÌNH ĐỌC DỞ (để tránh xung đột với resume.md).
    /// </summary>
    public string BuildHistoryMarkdownContent()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# COMIC DOWNLOADER GMTPC - LỊCH SỬ MỞ TRUYỆN (HISTORY.MD)");
        sb.AppendLine($"> Thời gian cập nhật: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | Hệ điều hành: {Environment.OSVersion.Platform}");

        var historyPaths = GetAllHistoryPaths(checkPathExists: false);
        sb.AppendLine();
        sb.AppendLine("## 🕒 Danh Sách Lịch Sử Mở Truyện (Reading History)");
        sb.AppendLine($"> Tổng cộng: {historyPaths.Count} bộ truyện");
        sb.AppendLine();
        sb.AppendLine("| STT | Tên Truyện / File | Lần Mở Gần Nhất | Đường Dẫn Cục Bộ |");
        sb.AppendLine("| :---: | :--- | :---: | :--- |");

        var snapshots = new List<HistoryItemSnapshot>();
        int hIdx = 1;
        foreach (var path in historyPaths)
        {
            string title = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            int order = GetComicLastReadOrder(path);
            string timeText = order > 0
                ? DateTimeOffset.FromUnixTimeSeconds(order + EpochOffsetSeconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "-";
            string safePath = EscapeMarkdown(path);
            sb.AppendLine($"| {hIdx++} | {EscapeMarkdown(title)} | {timeText} | {safePath} |");

            snapshots.Add(new HistoryItemSnapshot
            {
                Title = title,
                OrderStamp = order,
                LocalPath = path
            });
        }

        var payload = new HistoryBackupPayload
        {
            SavedAt = DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
            History = snapshots
        };

        string json = JsonSerializer.Serialize(payload, _jsonOptions);

        sb.AppendLine();
        sb.AppendLine("## Machine-Readable History State");
        sb.AppendLine(HistoryStateBeginMarker);
        sb.AppendLine("```json");
        sb.AppendLine(json);
        sb.AppendLine("```");
        sb.AppendLine(HistoryStateEndMarker);

        return sb.ToString();
    }

    private static string EscapeMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();
    }

    /// <summary>
    /// Xuất tiến trình đọc truyện dở dùng chung (resume.md) cho toàn bộ các thiết bị.
    /// Chuẩn hóa theo phong cách ComicScreen: Tuyệt đối không chứa đường dẫn ổ đĩa tuyệt đối (C:\, D:\, /storage/...).
    /// </summary>
    public string BuildResumeMarkdownContent(string currentDeviceName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# COMIC DOWNLOADER GMTPC - TIẾN TRÌNH ĐỌC DÙNG CHUNG (RESUME.MD)");
        sb.AppendLine("> Tệp tin này được dùng chung cho toàn bộ các thiết bị (Windows, Android, Linux).");
        sb.AppendLine("> Nội dung được chuẩn hóa độc lập nền tảng, không phụ thuộc vào đường dẫn ổ đĩa tuyệt đối.");
        sb.AppendLine($"> Thiết bị cập nhật cuối: **{EscapeMarkdown(currentDeviceName)}** | Thời gian: `{DateTime.Now:yyyy-MM-dd HH:mm:ss}`");
        sb.AppendLine();
        sb.AppendLine("## Danh Sách Truyện & Trang Đang Đọc Dở");
        sb.AppendLine("| STT | Tên Truyện / File | Chương Đang Đọc | Trang Đang Đọc | Cập Nhật Gần Nhất | Thiết Bị |");
        sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- |");

        var allHistoryPaths = GetAllHistoryPaths(checkPathExists: false);
        var entries = new List<CrossDeviceResumeEntry>();
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Quét từ lịch sử đọc hiện tại trên thiết bị
        foreach (var path in allHistoryPaths)
        {
            string cleanPath = NormalizeKey(path);
            string comicTitle = Path.GetFileName(cleanPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(comicTitle) || seenTitles.Contains(comicTitle)) continue;
            seenTitles.Add(comicTitle);

            string bestChapterTitle = string.Empty;
            string bestChapterSuffix = string.Empty;
            double bestChapterNumber = -1;
            int bestPage = 0;
            int bestTotalPages = 0;

            int lastChapIdx = GetLastActiveChapterIndex(cleanPath);
            if (lastChapIdx <= 0 && !string.IsNullOrWhiteSpace(comicTitle))
            {
                lastChapIdx = GetLastActiveChapterIndex(comicTitle);
            }
            if (lastChapIdx <= 0)
            {
                lock (_crossDeviceLock)
                {
                    var matchEntry = _crossDeviceEntries.FirstOrDefault(e =>
                        string.Equals(e.ComicTitle, comicTitle, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(NormalizeChapterName(e.ComicTitle), NormalizeChapterName(comicTitle), StringComparison.OrdinalIgnoreCase));
                    if (matchEntry != null && matchEntry.ChapterIndex > 0)
                    {
                        lastChapIdx = matchEntry.ChapterIndex;
                        if (string.IsNullOrEmpty(bestChapterTitle) && !string.IsNullOrEmpty(matchEntry.ChapterTitle))
                        {
                            bestChapterTitle = matchEntry.ChapterTitle;
                        }
                        if (bestChapterNumber < 0 && matchEntry.ChapterNumber >= 0)
                        {
                            bestChapterNumber = matchEntry.ChapterNumber;
                        }
                    }
                }
            }
            int order = GetComicLastReadOrder(cleanPath);
            long updatedTime = order > 0 ? (order + EpochOffsetSeconds) : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            string prefixSep1 = cleanPath + Path.DirectorySeparatorChar;
            string prefixSep2 = cleanPath + "/";
            string prefixSep3 = cleanPath + "::";

            foreach (var kvp in _progressMap)
            {
                if (kvp.Value <= 0 || kvp.Key.StartsWith("@", StringComparison.Ordinal)) continue;
                if (kvp.Key.EndsWith("::#last_active_chapter", StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.EndsWith("::#total_pages", StringComparison.OrdinalIgnoreCase)) continue;

                if (string.Equals(kvp.Key, cleanPath, StringComparison.OrdinalIgnoreCase))
                {
                    bestPage = Math.Max(bestPage, kvp.Value);
                    bestTotalPages = Math.Max(bestTotalPages, GetChapterTotalPages(cleanPath));
                }
                else if (kvp.Key.StartsWith(prefixSep3, StringComparison.OrdinalIgnoreCase))
                {
                    string suffix = kvp.Key.Substring(cleanPath.Length);
                    bestChapterSuffix = suffix;
                    bestChapterNumber = ExtractChapterNumber(suffix);
                    bestPage = kvp.Value;
                    bestTotalPages = GetChapterTotalPages(kvp.Key);
                }
                else if (kvp.Key.StartsWith(prefixSep1, StringComparison.OrdinalIgnoreCase) ||
                         kvp.Key.StartsWith(prefixSep2, StringComparison.OrdinalIgnoreCase))
                {
                    string subPath = kvp.Key;
                    string subName = Path.GetFileName(subPath.TrimEnd('\\', '/'));
                    double chNum = ExtractChapterNumber(subName);

                    if (lastChapIdx > 0 && chNum == lastChapIdx)
                    {
                        bestChapterTitle = subName;
                        bestChapterNumber = chNum;
                        bestPage = kvp.Value;
                        bestTotalPages = GetChapterTotalPages(subPath);
                        break;
                    }
                    else if (kvp.Value > bestPage)
                    {
                        bestChapterTitle = subName;
                        bestChapterNumber = chNum;
                        bestPage = kvp.Value;
                        bestTotalPages = GetChapterTotalPages(subPath);
                    }
                }
            }

            lock (_crossDeviceLock)
            {
                var matchEntry = _crossDeviceEntries.FirstOrDefault(e =>
                    string.Equals(e.ComicTitle, comicTitle, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeChapterName(e.ComicTitle), NormalizeChapterName(comicTitle), StringComparison.OrdinalIgnoreCase));
                if (matchEntry != null)
                {
                    if (string.IsNullOrEmpty(bestChapterTitle) && !string.IsNullOrEmpty(matchEntry.ChapterTitle))
                    {
                        bestChapterTitle = matchEntry.ChapterTitle;
                    }
                    if (bestChapterNumber < 0 && matchEntry.ChapterNumber >= 0)
                    {
                        bestChapterNumber = matchEntry.ChapterNumber;
                    }
                    if (bestPage <= 0 && matchEntry.LastReadPage > 0)
                    {
                        bestPage = matchEntry.LastReadPage;
                    }
                    if (bestTotalPages <= 0 && matchEntry.TotalPages > 0)
                    {
                        bestTotalPages = matchEntry.TotalPages;
                    }
                    if (lastChapIdx <= 0 && matchEntry.ChapterIndex > 0)
                    {
                        lastChapIdx = matchEntry.ChapterIndex;
                    }
                }
            }

            if (bestPage <= 0)
            {
                bestPage = GetLastReadPage(cleanPath);
            }
            if (bestTotalPages <= 0)
            {
                bestTotalPages = GetChapterTotalPages(cleanPath);
            }

            entries.Add(new CrossDeviceResumeEntry
            {
                ComicTitle = comicTitle,
                RelativePath = GetSafeRelativePath(cleanPath),
                ChapterTitle = bestChapterTitle,
                ChapterNumber = bestChapterNumber,
                ChapterIndex = lastChapIdx > 0 ? lastChapIdx : 1,
                ChapterSuffix = bestChapterSuffix,
                LastReadPage = bestPage,
                TotalPages = bestTotalPages,
                UpdatedAtUnixSeconds = updatedTime,
                DeviceName = currentDeviceName
            });
        }

        // 2. Bảo toàn các mục từ thiết bị khác trong _crossDeviceEntries nếu chưa được ghi đè
        lock (_crossDeviceLock)
        {
            foreach (var oldEntry in _crossDeviceEntries)
            {
                if (string.IsNullOrWhiteSpace(oldEntry.ComicTitle) || seenTitles.Contains(oldEntry.ComicTitle))
                    continue;

                seenTitles.Add(oldEntry.ComicTitle);
                entries.Add(oldEntry);
            }
        }

        var sortedEntries = entries.OrderByDescending(e => e.UpdatedAtUnixSeconds).ToList();
        int idx = 1;
        foreach (var e in sortedEntries)
        {
            string chapText = !string.IsNullOrWhiteSpace(e.ChapterTitle)
                ? e.ChapterTitle
                : (e.ChapterIndex > 0 ? $"Chương {e.ChapterIndex}" : "-");

            // Chỉ hiển thị "Trang X", TUYỆT ĐỐI KHÔNG lưu hay hiển thị tổng số trang "/TotalPages"
            string pageText = e.LastReadPage > 0 ? $"Trang {e.LastReadPage}" : "-";

            string timeText = e.UpdatedAtUnixSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(e.UpdatedAtUnixSeconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "-";

            string devText = !string.IsNullOrWhiteSpace(e.DeviceName) ? e.DeviceName : currentDeviceName;

            sb.AppendLine($"| {idx++} | {EscapeMarkdown(e.ComicTitle)} | {chapText} | {pageText} | {timeText} | {devText} |");
        }

        // CHUẨN COMICSCREEN: TUYỆT ĐỐI KHÔNG LƯU ProgressMap CÓ PATH VÀO RESUME.MD
        var payload = new ComicResumePayload
        {
            SavedAt = DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
            DeviceName = currentDeviceName,
            SchemaVersion = 3,
            Entries = sortedEntries
        };

        string json = JsonSerializer.Serialize(payload, _jsonOptions);

        sb.AppendLine();
        sb.AppendLine("## Machine-Readable Resume State");
        sb.AppendLine(ResumeStateBeginMarker);
        sb.AppendLine("```json");
        sb.AppendLine(json);
        sb.AppendLine("```");
        sb.AppendLine(ResumeStateEndMarker);

        return sb.ToString();
    }

    /// <summary>
    /// Lưu tiến trình đọc truyện dùng chung (resume.md) ra tệp Markdown cục bộ.
    /// </summary>
    public async Task<string> SaveResumeToMarkdownAsync(string? customPath = null)
    {
        string targetPath = string.IsNullOrWhiteSpace(customPath) ? GetStoreFilePath() : customPath;
        string deviceName = string.Empty;
        try { deviceName = GoogleDriveSyncService.Instance.CurrentDeviceName; } catch { }
        if (string.IsNullOrWhiteSpace(deviceName)) deviceName = Environment.MachineName;

        string content = BuildResumeMarkdownContent(deviceName);

        await Task.Run(() =>
        {
            lock (_saveLock)
            {
                string? dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    try { Directory.CreateDirectory(dir); } catch { }
                }

                string tmpPath = targetPath + ".tmp";
                File.WriteAllText(tmpPath, content, Encoding.UTF8);
                File.Move(tmpPath, targetPath, overwrite: true);
            }
        }).ConfigureAwait(false);

        return targetPath;
    }

    /// <summary>
    /// Phục hồi tiến trình đọc truyện dùng chung (resume.md) từ tệp Markdown cục bộ.
    /// </summary>
    public async Task<ResumeRestoreResult> LoadResumeFromMarkdownAsync(string? customPath = null, bool mergeWithExisting = true)
    {
        string targetPath = string.IsNullOrWhiteSpace(customPath) ? GetStoreFilePath() : customPath;
        if (!File.Exists(targetPath)) return new ResumeRestoreResult { Message = $"Tệp {targetPath} không tồn tại." };

        string content = await File.ReadAllTextAsync(targetPath, Encoding.UTF8).ConfigureAwait(false);
        return ParseResumeMarkdownContent(content, mergeWithExisting);
    }

    /// <summary>
    /// Phục hồi và Smart Merge tiến trình đọc dở từ file resume.md trên đám mây hoặc cục bộ.
    /// Tự động sinh Portable Keys, đối chiếu chéo nền tảng và ánh xạ vào các đường dẫn cục bộ khả dụng.
    /// </summary>
    public ResumeRestoreResult ParseResumeMarkdownContent(string content, bool mergeWithExisting = true)
    {
        var res = new ResumeRestoreResult();
        if (string.IsNullOrWhiteSpace(content))
        {
            res.Message = "Nội dung tệp resume.md trống.";
            return res;
        }

        int beginIdx = content.IndexOf(ResumeStateBeginMarker, StringComparison.Ordinal);
        int endIdx = beginIdx >= 0 ? content.IndexOf(ResumeStateEndMarker, beginIdx, StringComparison.Ordinal) : -1;
        if (beginIdx >= 0 && endIdx > beginIdx)
        {
            int fenceStart = content.IndexOf("```json", beginIdx, StringComparison.OrdinalIgnoreCase);
            if (fenceStart >= 0 && fenceStart < endIdx)
            {
                int lineEnd = content.IndexOf('\n', fenceStart);
                if (lineEnd >= 0 && lineEnd < endIdx)
                {
                    int jsonStart = lineEnd + 1;
                    int fenceEnd = content.IndexOf("```", jsonStart, StringComparison.Ordinal);
                    if (fenceEnd >= 0 && fenceEnd <= endIdx)
                    {
                        string json = content.Substring(jsonStart, fenceEnd - jsonStart).Trim();
                        try
                        {
                            var payload = JsonSerializer.Deserialize<ComicResumePayload>(json, _jsonOptions);
                            if (payload != null)
                            {
                                res.HasValidStructure = true;

                                // Nạp và xử lý danh sách CrossDeviceResumeEntry độc lập nền tảng
                                if (payload.Entries != null && payload.Entries.Count > 0)
                                {
                                    lock (_crossDeviceLock)
                                    {
                                        foreach (var entry in payload.Entries)
                                        {
                                            if (string.IsNullOrWhiteSpace(entry.ComicTitle)) continue;

                                            // Cập nhật vào danh sách cache cross-device
                                            int exIdx = _crossDeviceEntries.FindIndex(e => string.Equals(e.ComicTitle, entry.ComicTitle, StringComparison.OrdinalIgnoreCase));
                                            if (exIdx >= 0)
                                            {
                                                if (entry.UpdatedAtUnixSeconds >= _crossDeviceEntries[exIdx].UpdatedAtUnixSeconds || !mergeWithExisting)
                                                {
                                                    _crossDeviceEntries[exIdx] = entry;
                                                }
                                            }
                                            else
                                            {
                                                _crossDeviceEntries.Add(entry);
                                            }

                                            // Sinh portable keys chuẩn hóa vào _progressMap
                                            if (entry.ChapterIndex > 0)
                                            {
                                                string chKey = $"{PortableIdPrefix}leaf|{entry.ComicTitle}::#last_active_chapter";
                                                _progressMap[chKey] = entry.ChapterIndex;
                                            }

                                            if (entry.LastReadPage > 0)
                                            {
                                                if (entry.ChapterNumber >= 0)
                                                {
                                                    _progressMap[$"{PortableIdPrefix}num|{entry.ComicTitle}/chap_{entry.ChapterNumber.ToString(CultureInfo.InvariantCulture)}"] = entry.LastReadPage;
                                                    _progressMap[$"{PortableIdPrefix}num|{entry.ComicTitle}/chap_{((int)entry.ChapterNumber).ToString(CultureInfo.InvariantCulture)}"] = entry.LastReadPage;
                                                }

                                                if (!string.IsNullOrWhiteSpace(entry.ChapterTitle))
                                                {
                                                    _progressMap[$"{PortableIdPrefix}pair|{entry.ComicTitle}/{entry.ChapterTitle}"] = entry.LastReadPage;
                                                    _progressMap[$"{PortableIdPrefix}norm|{entry.ComicTitle}/{NormalizeChapterName(entry.ChapterTitle)}"] = entry.LastReadPage;
                                                }

                                                if (!string.IsNullOrWhiteSpace(entry.ChapterSuffix))
                                                {
                                                    _progressMap[$"{PortableIdPrefix}leaf|{entry.ComicTitle}{entry.ChapterSuffix}"] = entry.LastReadPage;
                                                }

                                                _progressMap[$"{PortableIdPrefix}file|{entry.ComicTitle}"] = entry.LastReadPage;
                                            }
                                        }
                                    }

                                    // Tự động dò tìm và liên kết vào các thư mục / file truyện cục bộ có sẵn trên thiết bị
                                    int remapped = AutoResolveAndRemapLocalPaths(payload.Entries);
                                    res.RestoredCrossDeviceComics = payload.Entries.Count;
                                    res.RestoredProgressEntries = payload.Entries.Count;
                                    res.Message = $"Đã đồng bộ {payload.Entries.Count} bộ truyện từ [{payload.DeviceName}], tự động liên kết {remapped} thư mục/file trên thiết bị này.";
                                }
                                else
                                {
                                    res.RestoredProgressEntries = 0;
                                    res.Message = $"Đã đồng bộ resume.md từ [{payload.DeviceName}] (Danh sách rỗng).";
                                }

                                MarkDirtyAndScheduleFlush();
                                return res;
                            }
                        }
                        catch (Exception ex)
                        {
                            res.Message = $"Lỗi phân tích JSON resume: {ex.Message}";
                        }
                    }
                }
            }
        }

        res.Message = "Không tìm thấy khối dữ liệu máy đọc hợp lệ trong resume.md.";
        return res;
    }

    private static string UnescapeMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Replace("\\|", "|").Trim();
    }
}


