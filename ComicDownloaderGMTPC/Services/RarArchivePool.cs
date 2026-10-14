using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using ComicDownloaderGMTPC.Models;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SkiaSharp;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Hồ lưu trữ và tái sử dụng RarArchive tập trung (Zero-Allocation RAR/CBR Handle Pool).
/// Cho phép đọc các trang ảnh bên trong tệp .rar/.cbr với tốc độ cao, hỗ trợ cả RAR4 và RAR5.
/// </summary>
public sealed class RarArchivePool : IDisposable
{
    private static readonly Lazy<RarArchivePool> _lazy = new(() => new RarArchivePool());
    public static RarArchivePool Instance => _lazy.Value;

    private sealed class PooledRarEntry : IDisposable
    {
        public string FilePath { get; }
        public DateTime LastWriteTimeUtc { get; }
        public FileStream Stream { get; }
        public RarArchive Archive { get; }
        public Dictionary<string, RarArchiveEntry> EntriesMap { get; }
        public object Lock { get; } = new();
        public long LastAccessTick { get; set; }

        public PooledRarEntry(string filePath, DateTime lastWriteTimeUtc, FileStream stream, RarArchive archive, Dictionary<string, RarArchiveEntry> entriesMap)
        {
            FilePath = filePath;
            LastWriteTimeUtc = lastWriteTimeUtc;
            Stream = stream;
            Archive = archive;
            EntriesMap = entriesMap;
            LastAccessTick = Environment.TickCount64;
        }

        public void Dispose()
        {
            try { Archive.Dispose(); } catch { }
            try { Stream.Dispose(); } catch { }
        }
    }

    private readonly ConcurrentDictionary<string, PooledRarEntry> _pool = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cleanupLock = new();
    private const int MaxOpenArchives = 8;

    private RarArchivePool() { }

    public byte[]? ReadEntryBytes(string archivePath, string entryFullName)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || string.IsNullOrWhiteSpace(entryFullName) || !File.Exists(archivePath))
        {
            return null;
        }

        string normalizedPath;
        DateTime lastWriteUtc;
        try
        {
            normalizedPath = Path.GetFullPath(archivePath);
            lastWriteUtc = File.GetLastWriteTimeUtc(normalizedPath);
        }
        catch
        {
            normalizedPath = archivePath;
            lastWriteUtc = DateTime.MinValue;
        }

        PooledRarEntry? entry = GetOrOpenArchive(normalizedPath, lastWriteUtc);
        if (entry == null) return null;

        lock (entry.Lock)
        {
            entry.LastAccessTick = Environment.TickCount64;

            string cleanKey = entryFullName.Replace('\\', '/').TrimStart('/');
            if (!entry.EntriesMap.TryGetValue(cleanKey, out var rarEntry))
            {
                string leaf = Path.GetFileName(cleanKey);
                foreach (var kv in entry.EntriesMap)
                {
                    if (string.Equals(Path.GetFileName(kv.Key), leaf, StringComparison.OrdinalIgnoreCase))
                    {
                        rarEntry = kv.Value;
                        break;
                    }
                }
            }

            if (rarEntry == null) return null;

            try
            {
                using var es = rarEntry.OpenEntryStream();
                using var ms = new MemoryStream((int)Math.Max(32768, rarEntry.Size));
                es.CopyTo(ms);
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }
    }

    public List<(string FullName, string Name, long Length)> GetImageEntriesInfo(string archivePath)
    {
        var result = new List<(string FullName, string Name, long Length)>();
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath)) return result;

        string normalizedPath;
        DateTime lastWriteUtc;
        try
        {
            normalizedPath = Path.GetFullPath(archivePath);
            lastWriteUtc = File.GetLastWriteTimeUtc(normalizedPath);
        }
        catch
        {
            normalizedPath = archivePath;
            lastWriteUtc = DateTime.MinValue;
        }

        PooledRarEntry? entry = GetOrOpenArchive(normalizedPath, lastWriteUtc);
        if (entry == null) return result;

        lock (entry.Lock)
        {
            entry.LastAccessTick = Environment.TickCount64;
            foreach (var kv in entry.EntriesMap)
            {
                var re = kv.Value;
                string name = Path.GetFileName(re.Key ?? string.Empty);
                if (!string.IsNullOrEmpty(name) && LibraryScannerService.IsImageFile(name) && re.Size > 0)
                {
                    result.Add((re.Key ?? name, name, re.Size));
                }
            }
        }

        result.Sort((a, b) => NaturalSortComparer.Instance.Compare(a.FullName, b.FullName));
        return result;
    }

    public Bitmap? DecodeCoverThumbnail(string archivePath, int targetWidth)
    {
        var entries = GetImageEntriesInfo(archivePath);
        if (entries.Count == 0) return null;

        byte[]? rawBytes = ReadEntryBytes(archivePath, entries[0].FullName);
        if (rawBytes == null || rawBytes.Length == 0) return null;

        try
        {
            using var data = SKData.CreateCopy(rawBytes);
            using var codec = SKCodec.Create(data);
            if (codec == null) return null;

            int origW = codec.Info.Width;
            int origH = codec.Info.Height;
            if (origW <= 0 || origH <= 0) return null;

            int dstW = Math.Max(32, targetWidth);
            int dstH = Math.Max(32, (int)Math.Round((double)origH * dstW / origW));

            using var srcBmp = SKBitmap.Decode(codec);
            if (srcBmp == null) return null;

            var info = new SKImageInfo(dstW, dstH, srcBmp.ColorType, srcBmp.AlphaType);
            using var resizedBitmap = srcBmp.Resize(info, SKSamplingOptions.Default);
            if (resizedBitmap == null) return null;

            using var img = SKImage.FromBitmap(resizedBitmap);
            using var encoded = img.Encode(SKEncodedImageFormat.Jpeg, 85);
            using var stream = encoded.AsStream();
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private PooledRarEntry? GetOrOpenArchive(string fullPath, DateTime lastWriteUtc)
    {
        if (_pool.TryGetValue(fullPath, out var cached))
        {
            if (cached.LastWriteTimeUtc == lastWriteUtc)
            {
                return cached;
            }
            if (_pool.TryRemove(fullPath, out var old))
            {
                old.Dispose();
            }
        }

        lock (_cleanupLock)
        {
            if (_pool.TryGetValue(fullPath, out cached))
            {
                if (cached.LastWriteTimeUtc == lastWriteUtc)
                {
                    return cached;
                }
                if (_pool.TryRemove(fullPath, out var old))
                {
                    old.Dispose();
                }
            }

            if (_pool.Count >= MaxOpenArchives)
            {
                string? oldestKey = null;
                long oldestTick = long.MaxValue;
                foreach (var kv in _pool)
                {
                    if (kv.Value.LastAccessTick < oldestTick)
                    {
                        oldestTick = kv.Value.LastAccessTick;
                        oldestKey = kv.Key;
                    }
                }
                if (oldestKey != null && _pool.TryRemove(oldestKey, out var evicted))
                {
                    evicted.Dispose();
                }
            }

            try
            {
                var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                var archive = RarArchive.Open(fs);
                var map = new Dictionary<string, RarArchiveEntry>(StringComparer.OrdinalIgnoreCase);

                foreach (var ze in archive.Entries)
                {
                    if (ze.IsDirectory || string.IsNullOrEmpty(ze.Key)) continue;
                    string key = ze.Key.Replace('\\', '/').TrimStart('/');
                    map[key] = ze;
                }

                var entry = new PooledRarEntry(fullPath, lastWriteUtc, fs, archive, map);
                _pool[fullPath] = entry;
                return entry;
            }
            catch
            {
                return null;
            }
        }
    }

    public void Close(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath)) return;
        try
        {
            string norm = Path.GetFullPath(archivePath);
            if (_pool.TryRemove(norm, out var entry))
            {
                entry.Dispose();
            }
        }
        catch { }
    }

    public void Clear()
    {
        lock (_cleanupLock)
        {
            foreach (var kv in _pool)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _pool.Clear();
        }
    }

    public void Dispose()
    {
        Clear();
    }
}
