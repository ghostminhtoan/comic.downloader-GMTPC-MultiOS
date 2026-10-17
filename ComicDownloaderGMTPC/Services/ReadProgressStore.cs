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

    public const string AutoSaveReaderFileName = "autosave_reader.md";
    public const string SavedReaderFileName = "saved_reader_bookmarks.md";
    public const string ReaderStateBeginMarker = "<!-- COMIC_READER_STATE_BEGIN -->";
    public const string ReaderStateEndMarker = "<!-- COMIC_READER_STATE_END -->";

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
    private const long EpochOffsetSeconds = 1700000000L;

    private readonly ConcurrentDictionary<string, int> _progressMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ReaderBookmarkItem> _bookmarks = new();
    private readonly object _bookmarkLock = new();
    private readonly object _saveLock = new();
    private readonly Timer _debounceTimer;
    private volatile bool _isDirty;

    private ReadProgressStore()
    {
        _debounceTimer = new Timer(_ => FlushImmediately(), null, Timeout.Infinite, Timeout.Infinite);
        LoadFromDisk();
        LoadBookmarksFromDisk();
    }

    private static string GetStoreFilePath()
    {
        string dir = ComicScraperService.GetConfigDirectory();
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
        return Path.Combine(dir, "read_progress.json");
    }

    private static string GetBookmarksFilePath()
    {
        string dir = ComicScraperService.GetConfigDirectory();
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
        return Path.Combine(dir, "reader_bookmarks.json");
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

    /// <summary>
    /// Tạo danh sách khóa nhận diện linh hoạt (Portable Identity Keys) dựa trên tên file / tên bộ truyện + tên chương.
    /// Giúp giữ nguyên 100% lịch sử đọc kể cả khi người dùng di chuyển truyện sang thư mục hoặc ổ đĩa khác.
    /// </summary>
    private static List<string> BuildPortableIdentityKeys(string rawKey)
    {
        var keys = new List<string>(2);
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
            // Khóa cho chương con bên trong PDF/ZIP hoặc #last_active_chapter
            keys.Add($"{PortableIdPrefix}leaf|{leafName}{suffix}");
            if (!string.IsNullOrWhiteSpace(parentName))
            {
                keys.Add($"{PortableIdPrefix}pair|{parentName}/{leafName}{suffix}");
            }
        }
        else
        {
            // Khóa cho thư mục chương hoặc file lẻ (.cbz/.zip/.pdf)
            if (!string.IsNullOrWhiteSpace(parentName))
            {
                keys.Add($"{PortableIdPrefix}pair|{parentName}/{leafName}");
            }

            string ext = Path.GetExtension(leafName);
            if (!string.IsNullOrEmpty(ext))
            {
                keys.Add($"{PortableIdPrefix}file|{leafName}");
            }
        }

        return keys;
    }

    /// <summary>
    /// Trả về số trang đang đọc (1-based: 0 = chưa đọc, 1..N = trang đang đọc).
    /// Tự động đối chiếu cả đường dẫn tuyệt đối lẫn khóa định danh linh hoạt (khi đổi thư mục).
    /// </summary>
    public int GetLastReadPage(string chapterKey)
    {
        string key = NormalizeKey(chapterKey);
        if (string.IsNullOrEmpty(key)) return 0;

        if (_progressMap.TryGetValue(key, out int page) && page > 0)
        {
            return page;
        }

        foreach (string portableKey in BuildPortableIdentityKeys(key))
        {
            if (_progressMap.TryGetValue(portableKey, out int portablePage) && portablePage > 0)
            {
                return portablePage;
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

        return 0;
    }

    /// <summary>
    /// Ghi nhận một bộ truyện (thư mục truyện hoặc file .pdf/.zip/.cbz) vào danh mục Lịch sử Đọc toàn cục
    /// để hiển thị xuyên suốt bất kể người dùng đang chọn thư mục thư viện nào.
    /// </summary>
    public void RegisterReadComic(string comicRootOrFilePath, bool touchTimestamp = false)
    {
        if (string.IsNullOrWhiteSpace(comicRootOrFilePath)) return;
        string cleanPath = NormalizeKey(comicRootOrFilePath);
        int sep = cleanPath.IndexOf("::", StringComparison.Ordinal);
        if (sep > 0) cleanPath = cleanPath.Substring(0, sep);
        if (string.IsNullOrWhiteSpace(cleanPath)) return;

        string histKey = HistoryPrefix + cleanPath;
        if (touchTimestamp || !_progressMap.ContainsKey(histKey))
        {
            _progressMap[histKey] = GetCurrentOrderStamp();
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

        return entries
            .OrderByDescending(e => e.Order)
            .Select(e => e.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        MarkDirtyAndScheduleFlush();
    }

    public int GetChapterTotalPages(string chapterKey)
    {
        if (string.IsNullOrWhiteSpace(chapterKey)) return 0;
        string key = NormalizeKey(chapterKey) + "::#total_pages";
        if (_progressMap.TryGetValue(key, out int total) && total > 0) return total;
        return 0;
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

    private void SaveBookmarksToDisk()
    {
        try
        {
            string filePath = GetBookmarksFilePath();
            string tmpPath = filePath + ".tmp";
            string json = JsonSerializer.Serialize(_bookmarks, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, filePath, overwrite: true);
        }
        catch { }
    }

    private void LoadBookmarksFromDisk()
    {
        try
        {
            string filePath = GetBookmarksFilePath();
            if (!File.Exists(filePath)) return;
            string json = File.ReadAllText(filePath);
            var list = JsonSerializer.Deserialize<List<ReaderBookmarkItem>>(json);
            if (list != null)
            {
                lock (_bookmarkLock)
                {
                    _bookmarks.Clear();
                    _bookmarks.AddRange(list);
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
                var snapshot = new Dictionary<string, int>(_progressMap, StringComparer.OrdinalIgnoreCase);
                string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, filePath, overwrite: true);
            }
            catch { }
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            string filePath = GetStoreFilePath();
            if (!File.Exists(filePath)) return;

            string json = File.ReadAllText(filePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (dict == null) return;

            foreach (var kv in dict)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value > 0)
                {
                    string normKey = NormalizeKey(kv.Key);
                    _progressMap[normKey] = kv.Value;
                }
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
                    string lastChapText = mh.Groups[3].Value.Trim();
                    string lastPageText = mh.Groups[4].Value.Trim();

                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        RegisterReadComic(path, touchTimestamp: true);
                        result.RestoredHistory++;

                        var mPage = Regex.Match(lastPageText, @"\d+");
                        if (mPage.Success && int.TryParse(mPage.Value, out int pageNum) && pageNum > 0)
                        {
                            SaveProgress(path, pageNum - 1);
                        }

                        var mChap = Regex.Match(lastChapText, @"\d+");
                        if (mChap.Success && int.TryParse(mChap.Value, out int chapNum) && chapNum > 0)
                        {
                            SaveLastActiveChapterIndex(path, chapNum);
                        }
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


