using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace ComicDownloaderGMTPC.Services;

public sealed class ReadProgressStore
{
    private static readonly Lazy<ReadProgressStore> _lazy = new(() => new ReadProgressStore());
    public static ReadProgressStore Instance => _lazy.Value;

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

    public static string NormalizeKey(string chapterKey)
    {
        if (string.IsNullOrWhiteSpace(chapterKey)) return string.Empty;
        string trimmed = chapterKey.Trim();

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
    /// Trả về số trang đang đọc (1-based: 0 = chưa đọc, 1..N = trang đang đọc).
    /// </summary>
    public int GetLastReadPage(string chapterKey)
    {
        string key = NormalizeKey(chapterKey);
        if (string.IsNullOrEmpty(key)) return 0;
        return _progressMap.TryGetValue(key, out int page) && page > 0 ? page : 0;
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
        _isDirty = true;

        try
        {
            _debounceTimer.Change(500, Timeout.Infinite);
        }
        catch { }
    }

    /// <summary>
    /// Lưu chỉ số chương (1-based Chapter.Index) đang đọc gần nhất của 1 bộ truyện / file PDF / file ZIP.
    /// </summary>
    public void SaveLastActiveChapterIndex(string comicOrFilePath, int chapterIndex1Based)
    {
        if (string.IsNullOrWhiteSpace(comicOrFilePath) || chapterIndex1Based <= 0) return;
        string key = NormalizeKey(comicOrFilePath) + "::#last_active_chapter";
        _progressMap[key] = chapterIndex1Based;
        _isDirty = true;

        try
        {
            _debounceTimer.Change(500, Timeout.Infinite);
        }
        catch { }
    }

    public int GetLastActiveChapterIndex(string comicOrFilePath)
    {
        if (string.IsNullOrWhiteSpace(comicOrFilePath)) return 0;
        string key = NormalizeKey(comicOrFilePath) + "::#last_active_chapter";
        return _progressMap.TryGetValue(key, out int idx) && idx > 0 ? idx : 0;
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
                    _progressMap[NormalizeKey(kv.Key)] = kv.Value;
                }
            }
        }
        catch { }
    }
}

