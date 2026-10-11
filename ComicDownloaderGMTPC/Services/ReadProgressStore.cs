using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace ComicDownloaderGMTPC.Services;

public sealed class ReadProgressStore
{
    private static readonly Lazy<ReadProgressStore> _lazy = new(() => new ReadProgressStore());
    public static ReadProgressStore Instance => _lazy.Value;

    private const string HistoryPrefix = "@history|";
    private const string FolderHistoryPrefix = "@folder|";
    private const string LastRootPrefix = "@last_root|";
    private const string PortableIdPrefix = "@id|";
    private const long EpochOffsetSeconds = 1700000000L;

    private readonly ConcurrentDictionary<string, int> _progressMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _saveLock = new();
    private readonly Timer _debounceTimer;
    private volatile bool _isDirty;

    private ReadProgressStore()
    {
        _debounceTimer = new Timer(_ => FlushImmediately(), null, Timeout.Infinite, Timeout.Infinite);
        LoadFromDisk();
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
}


