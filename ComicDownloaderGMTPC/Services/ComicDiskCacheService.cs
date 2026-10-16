using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Dịch vụ quản lý Bộ nhớ đệm Đa Tầng tập trung (Multi-Tier L2 Disk/Flash Cache & Life-cycle Manager).
/// Quản lý toàn bộ Cache trang truyện (.zip, .cbz, .rar, .cbr, .pdf, .mobi) và ảnh bìa (Thumbnails)
/// chuẩn 100% kiến trúc Comic Screen, hỗ trợ hạn mức Quota (Set thumbnails storage),
/// thời hạn lưu trữ (Cache retention period), và dọn dẹp phân tầng độc lập (Granular Cleanup).
/// </summary>
public sealed class ComicDiskCacheService
{
    private static readonly Lazy<ComicDiskCacheService> _lazy = new(() => new ComicDiskCacheService());
    public static ComicDiskCacheService Instance => _lazy.Value;

    private readonly object _settingsLock = new();
    private readonly string _cacheRootDir;
    private readonly string _thumbnailsDir;
    private readonly string _archivePagesDir;
    private readonly string _settingsFilePath;

    public string ThumbnailsDirectory => _thumbnailsDir;
    public string ArchivePagesDirectory => _archivePagesDir;

    public sealed class CacheSettingsData
    {
        public int ThumbnailQuotaMb { get; set; } = 1000; // Mặc định 1.0 GB
        public int ZipCacheQuotaMb { get; set; } = 1000;  // Mặc định 1.0 GB
        public int RetentionDays { get; set; } = 4;        // Mặc định 4 ngày
    }

    private CacheSettingsData _settings = new();

    public int ThumbnailQuotaMb
    {
        get { lock (_settingsLock) return _settings.ThumbnailQuotaMb; }
        set
        {
            lock (_settingsLock)
            {
                _settings.ThumbnailQuotaMb = Math.Clamp(value, 100, 10000);
                SaveSettings();
            }
            _ = Task.Run(PruneThumbnailsIfOverQuota);
        }
    }

    public int ZipCacheQuotaMb
    {
        get { lock (_settingsLock) return _settings.ZipCacheQuotaMb; }
        set
        {
            lock (_settingsLock)
            {
                _settings.ZipCacheQuotaMb = Math.Clamp(value, 100, 200000);
                SaveSettings();
            }
            _ = Task.Run(PruneArchivePagesIfOverQuota);
        }
    }

    public int RetentionDays
    {
        get { lock (_settingsLock) return _settings.RetentionDays; }
        set
        {
            lock (_settingsLock)
            {
                _settings.RetentionDays = Math.Clamp(value, 0, 365); // 0 = Không bao giờ hết hạn
                SaveSettings();
            }
            _ = Task.Run(PruneExpiredArchiveCache);
        }
    }

    private ComicDiskCacheService()
    {
        string baseConfigDir = ComicScraperService.GetConfigDirectory();
        _cacheRootDir = Path.Combine(baseConfigDir, ".cache");
        _thumbnailsDir = Path.Combine(_cacheRootDir, "thumbnails");
        _archivePagesDir = Path.Combine(_cacheRootDir, "archive_pages");
        _settingsFilePath = Path.Combine(baseConfigDir, "cache_settings.json");

        EnsureDirectories();
        LoadSettings();

        // Tự động dọn dẹp cache hết hạn và kiểm tra hạn mức khi khởi động
        _ = Task.Run(() =>
        {
            PruneExpiredArchiveCache();
            PruneThumbnailsIfOverQuota();
            PruneArchivePagesIfOverQuota();
        });
    }

    private void EnsureDirectories()
    {
        try
        {
            if (!Directory.Exists(_thumbnailsDir)) Directory.CreateDirectory(_thumbnailsDir);
            if (!Directory.Exists(_archivePagesDir)) Directory.CreateDirectory(_archivePagesDir);
        }
        catch { }
    }

    private void LoadSettings()
    {
        lock (_settingsLock)
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var loaded = JsonSerializer.Deserialize<CacheSettingsData>(json);
                    if (loaded != null)
                    {
                        _settings = loaded;
                    }
                }
            }
            catch { }
        }
    }

    private void SaveSettings()
    {
        lock (_settingsLock)
        {
            try
            {
                string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);
            }
            catch { }
        }
    }

    // =========================================================================
    // 1. TẦNG L2 DISK CACHE: TRANG TRUYỆN (.ZIP, .CBZ, .RAR, .CBR, .PDF, .MOBI)
    // =========================================================================

    public static string BuildPageCacheKey(string sourcePath, string subKey)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return string.Empty;
        string cleanSource = sourcePath.Trim().Replace('\\', '/');
        string cleanSub = (subKey ?? string.Empty).Trim().Replace('\\', '/');

        long fileLen = 0;
        long mtime = 0;
        try
        {
            if (File.Exists(sourcePath))
            {
                var fi = new FileInfo(sourcePath);
                fileLen = fi.Length;
                mtime = fi.LastWriteTimeUtc.Ticks;
            }
        }
        catch { }

        string raw = $"{cleanSource}|{cleanSub}|{fileLen}|{mtime}";
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public byte[]? TryGetCachedPageBytes(string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey)) return null;
        string targetFile = Path.Combine(_archivePagesDir, cacheKey + ".pagecache");

        try
        {
            if (File.Exists(targetFile))
            {
                // Cập nhật LastAccessTime để phục vụ cơ chế LRU và Retention Period
                File.SetLastAccessTimeUtc(targetFile, DateTime.UtcNow);
                return File.ReadAllBytes(targetFile);
            }
        }
        catch { }

        return null;
    }

    public void SaveCachedPageBytes(string cacheKey, byte[] data)
    {
        if (string.IsNullOrWhiteSpace(cacheKey) || data == null || data.Length == 0) return;
        string targetFile = Path.Combine(_archivePagesDir, cacheKey + ".pagecache");

        _ = Task.Run(() =>
        {
            try
            {
                EnsureDirectories();
                string tmpFile = targetFile + ".tmp";
                File.WriteAllBytes(tmpFile, data);
                File.Move(tmpFile, targetFile, overwrite: true);
                File.SetLastAccessTimeUtc(targetFile, DateTime.UtcNow);
                PruneArchivePagesIfOverQuota();
            }
            catch { }
        });
    }

    // =========================================================================
    // 2. TẦNG THUMBNAIL DISK CACHE (SET THUMBNAILS STORAGE)
    // =========================================================================

    public static string BuildThumbnailCacheKey(string sourcePath, int targetWidth)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return string.Empty;
        string cleanSource = sourcePath.Trim().Replace('\\', '/');
        long mtime = 0;
        long fileLen = 0;
        try
        {
            if (File.Exists(sourcePath))
            {
                var fi = new FileInfo(sourcePath);
                fileLen = fi.Length;
                mtime = fi.LastWriteTimeUtc.Ticks;
            }
            else if (Directory.Exists(sourcePath))
            {
                mtime = Directory.GetLastWriteTimeUtc(sourcePath).Ticks;
            }
        }
        catch { }

        string raw = $"thumb|{cleanSource}|{targetWidth}|{fileLen}|{mtime}";
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public Bitmap? TryGetCachedThumbnail(string sourcePath, int targetWidth)
    {
        string key = BuildThumbnailCacheKey(sourcePath, targetWidth);
        if (string.IsNullOrWhiteSpace(key)) return null;

        string targetFile = Path.Combine(_thumbnailsDir, key + ".thumb");
        try
        {
            if (File.Exists(targetFile))
            {
                File.SetLastAccessTimeUtc(targetFile, DateTime.UtcNow);
                using var fs = new FileStream(targetFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                return new Bitmap(fs);
            }
        }
        catch { }

        return null;
    }

    public void SaveCachedThumbnail(string sourcePath, int targetWidth, byte[] imageBytes)
    {
        if (imageBytes == null || imageBytes.Length == 0) return;
        string key = BuildThumbnailCacheKey(sourcePath, targetWidth);
        if (string.IsNullOrWhiteSpace(key)) return;

        string targetFile = Path.Combine(_thumbnailsDir, key + ".thumb");
        _ = Task.Run(() =>
        {
            try
            {
                EnsureDirectories();
                string tmp = targetFile + ".tmp";
                File.WriteAllBytes(tmp, imageBytes);
                File.Move(tmp, targetFile, overwrite: true);
                File.SetLastAccessTimeUtc(targetFile, DateTime.UtcNow);

                PruneThumbnailsIfOverQuota();
            }
            catch { }
        });
    }

    // =========================================================================
    // 3. THỐNG KÊ DUNG LƯỢNG VÀ FORMAT HIỂN THỊ CHUẨN COMIC SCREEN
    // =========================================================================

    public long GetThumbnailsSizeBytes()
    {
        return CalculateDirectorySizeSafe(_thumbnailsDir);
    }

    public long GetArchiveCacheSizeBytes()
    {
        return CalculateDirectorySizeSafe(_archivePagesDir);
    }

    public long GetTotalCacheSizeBytes()
    {
        return GetThumbnailsSizeBytes() + GetArchiveCacheSizeBytes();
    }

    public string GetThumbnailsStorageSummaryText()
    {
        long bytes = GetThumbnailsSizeBytes();
        string usedText = FormatBytes(bytes);
        double quotaGb = ThumbnailQuotaMb / 1000.0;
        string quotaText = quotaGb >= 1.0 ? $"{quotaGb:0.0} GB" : $"{ThumbnailQuotaMb} MB";
        return $"Thumbnails {usedText} / {quotaText}";
    }

    public string GetZipCacheStorageSummaryText()
    {
        long bytes = GetArchiveCacheSizeBytes();
        string usedText = FormatBytes(bytes);
        double quotaGb = ZipCacheQuotaMb / 1000.0;
        string quotaText = quotaGb >= 1.0 ? $"{quotaGb:0.0} GB" : $"{ZipCacheQuotaMb} MB";
        return $"Zip cache {usedText} / {quotaText}";
    }

    public string GetArchiveCacheSummaryText()
    {
        long bytes = GetArchiveCacheSizeBytes();
        return FormatBytes(bytes);
    }

    public string GetRetentionPeriodSummaryText()
    {
        int days = RetentionDays;
        if (days <= 0) return "Vĩnh viễn (Never expire)";
        return $"{days} days";
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double b = bytes;
        int i = 0;
        while (b >= 1024 && i < units.Length - 1)
        {
            b /= 1024;
            i++;
        }
        return $"{b:0.#} {units[i]}";
    }

    private static long CalculateDirectorySizeSafe(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        try
        {
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true
            };
            foreach (var f in Directory.EnumerateFiles(dir, "*", enumOptions))
            {
                try
                {
                    total += new FileInfo(f).Length;
                }
                catch { }
            }
        }
        catch { }
        return total;
    }

    // =========================================================================
    // 4. CẮT TỈA TỰ ĐỘNG (AUTO PRUNING: QUOTA & RETENTION DAYS)
    // =========================================================================

    public void PruneExpiredArchiveCache()
    {
        int days = RetentionDays;
        if (days <= 0 || !Directory.Exists(_archivePagesDir)) return;

        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(days);
            var enumOptions = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
            foreach (var file in Directory.EnumerateFiles(_archivePagesDir, "*.pagecache", enumOptions))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.LastAccessTimeUtc < cutoff)
                    {
                        fi.Delete();
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    public void PruneThumbnailsIfOverQuota()
    {
        if (!Directory.Exists(_thumbnailsDir)) return;
        long maxBytes = (long)ThumbnailQuotaMb * 1024L * 1024L;

        try
        {
            var enumOptions = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
            var files = Directory.EnumerateFiles(_thumbnailsDir, "*.thumb", enumOptions)
                .Select(f =>
                {
                    try { return new FileInfo(f); } catch { return null; }
                })
                .Where(fi => fi != null && fi.Exists)
                .OrderBy(fi => fi!.LastAccessTimeUtc) // Cũ nhất trước
                .ToList();

            long currentTotal = files.Sum(f => f!.Length);
            long targetBytes = (long)(maxBytes * 0.85); // Xả về 85% hạn mức

            if (currentTotal > maxBytes)
            {
                foreach (var fi in files)
                {
                    if (currentTotal <= targetBytes) break;
                    try
                    {
                        long len = fi!.Length;
                        fi.Delete();
                        currentTotal -= len;
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    public void PruneArchivePagesIfOverQuota()
    {
        if (!Directory.Exists(_archivePagesDir)) return;
        long maxBytes = (long)ZipCacheQuotaMb * 1024L * 1024L;

        try
        {
            var enumOptions = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
            var files = Directory.EnumerateFiles(_archivePagesDir, "*.pagecache", enumOptions)
                .Select(f =>
                {
                    try { return new FileInfo(f); } catch { return null; }
                })
                .Where(fi => fi != null && fi.Exists)
                .OrderBy(fi => fi!.LastAccessTimeUtc) // Cũ nhất trước (LRU)
                .ToList();

            long currentTotal = files.Sum(f => f!.Length);
            long targetBytes = (long)(maxBytes * 0.85); // Xả về 85% hạn mức

            if (currentTotal > maxBytes)
            {
                foreach (var fi in files)
                {
                    if (currentTotal <= targetBytes) break;
                    try
                    {
                        long len = fi!.Length;
                        fi.Delete();
                        currentTotal -= len;
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    // =========================================================================
    // 5. CÁC NÚT DỌN DẸP PHÂN TẦNG ĐỘC LẬP (GRANULAR CLEAR COMMANDS)
    // =========================================================================

    public void ClearThumbnails()
    {
        try
        {
            if (Directory.Exists(_thumbnailsDir))
            {
                foreach (var f in Directory.EnumerateFiles(_thumbnailsDir, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    public void ClearArchiveCache()
    {
        try
        {
            if (Directory.Exists(_archivePagesDir))
            {
                foreach (var f in Directory.EnumerateFiles(_archivePagesDir, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    public void ClearResumeCache()
    {
        ReadProgressStore.Instance.ClearAllProgress();
    }

    public void ClearBookmarks()
    {
        ReadProgressStore.Instance.ClearAllBookmarks();
    }

    public void ClearHistory()
    {
        ReadProgressStore.Instance.ClearAllHistory();
    }

    public void ClearAllCache()
    {
        ClearThumbnails();
        ClearArchiveCache();
        ClearResumeCache();
        ClearBookmarks();
        ClearHistory();
    }
}

