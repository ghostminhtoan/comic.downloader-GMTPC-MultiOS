using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ComicDownloaderGMTPC.Models;
using SkiaSharp;

namespace ComicDownloaderGMTPC.Services;

public sealed class ThumbnailPreviewService
{
    private static readonly Lazy<ThumbnailPreviewService> _lazy = new(() => new ThumbnailPreviewService());
    public static ThumbnailPreviewService Instance => _lazy.Value;

    private readonly HttpClient _httpClient;
    private readonly CookieContainer _cookieContainer = new();
    private readonly SemaphoreSlim _generalSemaphore = new(OperatingSystem.IsAndroid() ? 6 : 16, OperatingSystem.IsAndroid() ? 6 : 16);
    private readonly SemaphoreSlim _rateSensitiveSemaphore = new(OperatingSystem.IsAndroid() ? 2 : 3, OperatingSystem.IsAndroid() ? 2 : 3);
    private readonly SemaphoreSlim _priorityScrollSemaphore = new(OperatingSystem.IsAndroid() ? 3 : 4, OperatingSystem.IsAndroid() ? 3 : 4);
    private readonly ConcurrentDictionary<string, string> _candidateUrlCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _failedUrlCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _inFlightItemTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _lastScrollRetryTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cacheRoot;
    private static readonly Random _jitter = new();
    private static readonly object _bitmapMemoryLock = new();
    private static ComicBookItem? _activeHighResItem;

    public int MaxCpuThreads { get; set; } = OperatingSystem.IsAndroid()
        ? 3
        : Math.Min(4, CpuTopologyHelper.GetMaxLogicalProcessorCount());

    public void SetMaxCpuThreads(int threads)
    {
        int maxAllowed = OperatingSystem.IsAndroid() ? 6 : CpuTopologyHelper.GetMaxLogicalProcessorCount();
        MaxCpuThreads = Math.Clamp(threads, 1, maxAllowed);
    }

    private ThumbnailPreviewService()
    {
        try
        {
            var ehUri = new Uri("https://e-hentai.org");
            _cookieContainer.Add(ehUri, new Cookie("nw", "1", "/", ".e-hentai.org"));
            _cookieContainer.Add(ehUri, new Cookie("nw", "always", "/", ".e-hentai.org"));
            _cookieContainer.Add(ehUri, new Cookie("sl", "dm_1", "/", ".e-hentai.org"));

            var exUri = new Uri("https://exhentai.org");
            _cookieContainer.Add(exUri, new Cookie("nw", "1", "/", ".exhentai.org"));
            _cookieContainer.Add(exUri, new Cookie("nw", "always", "/", ".exhentai.org"));
            _cookieContainer.Add(exUri, new Cookie("sl", "dm_1", "/", ".exhentai.org"));
        }
        catch { }

        var handler = DoHResolver.CreateBypassHandler(_cookieContainer);

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");

        _cacheRoot = ComicDiskCacheService.Instance.PreviewCacheDirectory;
        try
        {
            if (!Directory.Exists(_cacheRoot))
            {
                Directory.CreateDirectory(_cacheRoot);
            }
        }
        catch { }
    }

    public bool SupportsPreview(ComicBookItem? item)
    {
        if (item == null) return false;
        if (!string.IsNullOrWhiteSpace(item.CoverUrl)) return true;
        if (item.HasThumbnailFile) return true;

        string url = item.Url?.Trim() ?? string.Empty;
        string domain = item.Domain?.Trim() ?? string.Empty;

        return IsSupportedDomain(url, domain);
    }

    public static bool IsSupportedDomain(string url, string domain)
    {
        string combined = $"{url} {domain}".ToLowerInvariant();

        return combined.Contains("hentaiforce.net/view/") ||
               combined.Contains("truyenqq") ||
               combined.Contains("dilib.vn") ||
               combined.Contains("thuviensach.vn") ||
               combined.Contains("nettruyen") ||
               combined.Contains("mangadex.org") ||
               combined.Contains("nhentai.") ||
               combined.Contains("e-hentai.org") ||
               combined.Contains("exhentai.org") ||
               combined.Contains("hitomi.la") ||
               combined.Contains("sayhentai") ||
               combined.Contains("damconuong") ||
               combined.Contains("daomeoden") ||
               combined.Contains("vi-hentai");
    }

    private static bool IsPlaceholderImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        string lower = url.Trim().ToLowerInvariant();
        return lower.StartsWith("data:") ||
               lower.Contains("blank.gif") ||
               lower.Contains("mr.gif") ||
               lower.Contains("no_image") ||
               lower.Contains("no-image") ||
               lower.Contains("/g/t.png") ||
               lower.Contains("/g/d.png") ||
               lower.Contains("spacer.gif");
    }

    private static async Task RunOnUiAsync(Action action)
    {
        try
        {
            if (Avalonia.Application.Current == null || Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(action);
            }
        }
        catch
        {
            try { action(); } catch { }
        }
    }

    public async Task EnsureVisibleItemThumbnailAsync(ComicBookItem? item, CancellationToken ct = default)
    {
        if (item == null || ct.IsCancellationRequested) return;
        if (item.ThumbnailBitmap != null && item.HasThumbnailFile) return;

        string itemKey = !string.IsNullOrWhiteSpace(item.Url) ? item.Url.Trim() : item.Title?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(itemKey))
        {
            long now = Environment.TickCount64;
            if (_lastScrollRetryTick.TryGetValue(itemKey, out long lastTick) && now - lastTick < 2500 && item.IsThumbnailLoading)
            {
                return;
            }
            _lastScrollRetryTick[itemKey] = now;
        }

        // Xóa trạng thái lỗi tạm thời trước đó để đảm bảo khi người dùng cuộn tới mục thiếu thumbnail thì luôn tự động tải lại được
        if (!string.IsNullOrWhiteSpace(item.CoverUrl))
        {
            _failedUrlCache.TryRemove(item.CoverUrl, out _);
        }
        if (!string.IsNullOrWhiteSpace(item.Url))
        {
            if (_candidateUrlCache.TryGetValue(item.Url, out string? cachedUrl) && !string.IsNullOrWhiteSpace(cachedUrl))
            {
                _failedUrlCache.TryRemove(cachedUrl, out _);
            }
        }

        await EnsureThumbnailCacheAsync(item, ct, isPriority: true).ConfigureAwait(false);
    }

    public async Task EnsureHighResPreviewAsync(ComicBookItem? item, CancellationToken ct = default)
    {
        if (item == null || ct.IsCancellationRequested) return;
        if (item.HighResPreviewBitmap != null && !string.IsNullOrWhiteSpace(item.PreviewTagsText)) return;

        // Xóa cache lỗi tạm thời để khi người dùng mở Popup Preview ở bất kỳ chế độ nào (List view, List + thumbnail column, Thumbnail view) cũng nạp được ảnh
        if (!string.IsNullOrWhiteSpace(item.CoverUrl))
        {
            _failedUrlCache.TryRemove(item.CoverUrl, out _);
        }
        if (!string.IsNullOrWhiteSpace(item.Url) && _candidateUrlCache.TryGetValue(item.Url, out string? cachedUrl) && !string.IsNullOrWhiteSpace(cachedUrl))
        {
            _failedUrlCache.TryRemove(cachedUrl, out _);
        }

        await EnsureThumbnailCacheAsync(item, ct, isPriority: true).ConfigureAwait(false);

        string cacheDir = GetItemCacheDirectory(item);
        string originalFile = Path.Combine(cacheDir, "cover.jpg");
        string tagsFile = Path.Combine(cacheDir, "cover.tags.txt");

        if (item.HighResPreviewBitmap == null && File.Exists(originalFile) && new FileInfo(originalFile).Length > 0)
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(originalFile, ct).ConfigureAwait(false);
                if (bytes.Length > 0)
                {
                    await RunOnUiAsync(() =>
                    {
                        try
                        {
                            item.OriginalCoverLocalPath = originalFile;
                            if (Avalonia.Application.Current != null)
                            {
                                lock (_bitmapMemoryLock)
                                {
                                    if (_activeHighResItem != null && !ReferenceEquals(_activeHighResItem, item))
                                    {
                                        _activeHighResItem.HighResPreviewBitmap = null;
                                    }
                                    _activeHighResItem = item;
                                }

                                using var ms = new MemoryStream(bytes);
                                try
                                {
                                    item.HighResPreviewBitmap = Bitmap.DecodeToWidth(ms, 340);
                                }
                                catch
                                {
                                    ms.Position = 0;
                                    item.HighResPreviewBitmap = new Bitmap(ms);
                                }
                            }
                        }
                        catch { }
                    });
                }
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(item.PreviewTagsText))
        {
            if (File.Exists(tagsFile))
            {
                try
                {
                    string cachedTags = (await File.ReadAllTextAsync(tagsFile, ct).ConfigureAwait(false)).Trim();
                    if (!string.IsNullOrWhiteSpace(cachedTags))
                    {
                        await RunOnUiAsync(() => item.PreviewTagsText = cachedTags);
                        return;
                    }
                }
                catch { }
            }

            string link = item.Url?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(link) && !link.Contains("hitomi.la") && !link.Contains("mangadex.org"))
            {
                try
                {
                    string html = await FetchStringAsync(link, ct).ConfigureAwait(false);
                    string extractedTags = ComicScraperService.ExtractPreviewTags(html, link);
                    if (!string.IsNullOrWhiteSpace(extractedTags))
                    {
                        try { await File.WriteAllTextAsync(tagsFile, extractedTags, ct).ConfigureAwait(false); } catch { }
                        await RunOnUiAsync(() => item.PreviewTagsText = extractedTags);
                    }
                }
                catch { }
            }
        }
    }

    public async Task EnsureThumbnailCacheAsync(ComicBookItem item, CancellationToken ct = default, bool isPriority = false)
    {
        if (item == null || ct.IsCancellationRequested) return;

        string cacheDir = GetItemCacheDirectory(item);
        string originalFile = Path.Combine(cacheDir, "cover.jpg");
        string thumbFile = Path.Combine(cacheDir, "cover.thumb.jpg");
        string tagsFile = Path.Combine(cacheDir, "cover.tags.txt");

        // Đồng bộ tags vào/từ file cache nếu có
        if (!string.IsNullOrWhiteSpace(item.PreviewTagsText) && !File.Exists(tagsFile))
        {
            try { await File.WriteAllTextAsync(tagsFile, item.PreviewTagsText, ct).ConfigureAwait(false); } catch { }
        }
        else if (string.IsNullOrWhiteSpace(item.PreviewTagsText) && File.Exists(tagsFile))
        {
            try
            {
                string cachedTags = (await File.ReadAllTextAsync(tagsFile, ct).ConfigureAwait(false)).Trim();
                if (!string.IsNullOrWhiteSpace(cachedTags))
                {
                    await RunOnUiAsync(() => item.PreviewTagsText = cachedTags);
                }
            }
            catch { }
        }

        // Nếu đã có Bitmap nạp vào UI thì bỏ qua
        if (item.ThumbnailBitmap != null && item.HasThumbnailFile) return;

        // Kiểm tra xem đã có sẵn file cache trên ổ đĩa chưa (nạp tức thì không cần chờ Semaphore mạng)
        if (File.Exists(thumbFile) && new FileInfo(thumbFile).Length > 0)
        {
            await LoadThumbnailBitmapToItemAsync(item, thumbFile, File.Exists(originalFile) ? originalFile : thumbFile).ConfigureAwait(false);
            if (item.ThumbnailBitmap != null) return;
        }

        if (File.Exists(originalFile) && new FileInfo(originalFile).Length > 0)
        {
            GenerateThumbImageFromOriginal(originalFile, thumbFile);
            string fileToLoad = File.Exists(thumbFile) ? thumbFile : originalFile;
            await LoadThumbnailBitmapToItemAsync(item, fileToLoad, originalFile).ConfigureAwait(false);
            if (item.ThumbnailBitmap != null) return;
        }

        string itemKey = !string.IsNullOrWhiteSpace(item.Url) ? item.Url.Trim() : $"{item.Index}_{item.Title}";
        if (_inFlightItemTasks.TryGetValue(itemKey, out var existingTask))
        {
            try { await existingTask.ConfigureAwait(false); } catch { }
            return;
        }

        var downloadTask = DownloadAndCacheItemThumbnailCoreAsync(item, originalFile, thumbFile, isPriority, ct);
        if (_inFlightItemTasks.TryAdd(itemKey, downloadTask))
        {
            try
            {
                await downloadTask.ConfigureAwait(false);
            }
            finally
            {
                _inFlightItemTasks.TryRemove(itemKey, out _);
            }
        }
        else if (_inFlightItemTasks.TryGetValue(itemKey, out var racedTask))
        {
            try { await racedTask.ConfigureAwait(false); } catch { }
        }
    }

    private async Task DownloadAndCacheItemThumbnailCoreAsync(ComicBookItem item, string originalFile, string thumbFile, bool isPriority, CancellationToken ct)
    {
        bool isRateSensitive = IsRateSensitive(item.Url);
        SemaphoreSlim semaphore = isPriority
            ? _priorityScrollSemaphore
            : (isRateSensitive ? _rateSensitiveSemaphore : _generalSemaphore);

        if (isRateSensitive && !isPriority)
        {
            int jitterMs;
            lock (_jitter) { jitterMs = _jitter.Next(120, 320); }
            await Task.Delay(jitterMs, ct).ConfigureAwait(false);
        }

        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RunOnUiAsync(() => item.IsThumbnailLoading = true);

            List<string> candidateUrls = await ResolveCandidateUrlsAsync(item, ct, forceFreshResolve: false).ConfigureAwait(false);
            if (candidateUrls.Count == 0)
            {
                candidateUrls = await ResolveCandidateUrlsAsync(item, ct, forceFreshResolve: true).ConfigureAwait(false);
            }
            if (candidateUrls.Count == 0) return;

            bool success = await TryDownloadCandidateListAsync(item, candidateUrls, originalFile, thumbFile, ct).ConfigureAwait(false);
            if (!success && !ct.IsCancellationRequested)
            {
                // Fallback: nếu URL ứng viên ban đầu lỗi, ép phân tích lại từ trang gốc / API
                var freshUrls = await ResolveCandidateUrlsAsync(item, ct, forceFreshResolve: true).ConfigureAwait(false);
                if (freshUrls.Count > 0)
                {
                    await TryDownloadCandidateListAsync(item, freshUrls, originalFile, thumbFile, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            await RunOnUiAsync(() => item.IsThumbnailLoading = false);
            semaphore.Release();
        }
    }

    private async Task<bool> TryDownloadCandidateListAsync(ComicBookItem item, List<string> candidateUrls, string originalFile, string thumbFile, CancellationToken ct)
    {
        foreach (string candidateUrl in candidateUrls)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(candidateUrl) || IsPlaceholderImageUrl(candidateUrl) || _failedUrlCache.ContainsKey(candidateUrl)) continue;

            bool downloaded = await DownloadImageFileAsync(candidateUrl, originalFile, item.Url, ct).ConfigureAwait(false);
            if (downloaded && File.Exists(originalFile) && new FileInfo(originalFile).Length > 0)
            {
                GenerateThumbImageFromOriginal(originalFile, thumbFile);
                string targetThumb = File.Exists(thumbFile) ? thumbFile : originalFile;
                await LoadThumbnailBitmapToItemAsync(item, targetThumb, originalFile).ConfigureAwait(false);
                if (item.ThumbnailBitmap != null)
                {
                    return true;
                }
            }

            _failedUrlCache.TryAdd(candidateUrl, 1);
        }

        return false;
    }

    public async Task PrefetchBatchAsync(IEnumerable<ComicBookItem> items, CancellationToken ct = default)
    {
        if (items == null) return;
        var validItems = items.Where(SupportsPreview).Distinct().ToList();
        if (validItems.Count == 0) return;

        // Ưu tiên nạp trước các truyện chưa có ThumbnailBitmap trên giao diện; chạy tuần tự theo nhóm nhỏ để nhường băng thông cho thao tác cuộn (Scroll) và Popup Preview
        var queue = new ConcurrentQueue<ComicBookItem>(validItems.OrderBy(x => x.ThumbnailBitmap != null ? 1 : 0));
        bool isReaderActive = ComicReaderService.Instance.IsReaderActive;
        int workerCount = isReaderActive
            ? 1
            : Math.Clamp(MaxCpuThreads, 1, 2);
        var workers = new List<Task>(workerCount);

        for (int w = 0; w < workerCount; w++)
        {
            workers.Add(Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && queue.TryDequeue(out var nextItem))
                {
                    if (ComicReaderService.Instance.IsReaderActive)
                    {
                        // Thong thả nghỉ 250ms giữa các item khi đang đọc truyện để không tranh chấp CPU và làm nóng máy
                        await Task.Delay(250, ct).ConfigureAwait(false);
                    }
                    if (nextItem.ThumbnailBitmap != null && nextItem.HasThumbnailFile) continue;
                    await EnsureThumbnailCacheAsync(nextItem, ct, isPriority: false).ConfigureAwait(false);
                    await Task.Delay(40, ct).ConfigureAwait(false);
                }
            }, ct));
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    public async Task RescanItemAsync(ComicBookItem item, CancellationToken ct = default)
    {
        if (item == null) return;

        if (!string.IsNullOrWhiteSpace(item.CoverUrl))
        {
            _failedUrlCache.TryRemove(item.CoverUrl, out _);
        }
        if (!string.IsNullOrWhiteSpace(item.Url))
        {
            if (_candidateUrlCache.TryRemove(item.Url, out string? oldCached) && !string.IsNullOrWhiteSpace(oldCached))
            {
                _failedUrlCache.TryRemove(oldCached, out _);
            }
        }

        string cacheDir = GetItemCacheDirectory(item);
        string originalFile = Path.Combine(cacheDir, "cover.jpg");
        string thumbFile = Path.Combine(cacheDir, "cover.thumb.jpg");
        string tempOriginalFile = Path.Combine(cacheDir, "cover.rescan.jpg");

        await RunOnUiAsync(() => item.IsThumbnailLoading = true);
        try
        {
            // 1. Luôn tìm kiếm bộ URL ứng viên mới nhất (bao gồm cả CoverUrl sẵn có và quét lại từ API/HTML)
            var candidateUrls = await ResolveCandidateUrlsAsync(item, ct, forceFreshResolve: true).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(item.CoverUrl) && !IsPlaceholderImageUrl(item.CoverUrl) && !candidateUrls.Contains(item.CoverUrl, StringComparer.OrdinalIgnoreCase))
            {
                candidateUrls.Insert(0, item.CoverUrl);
            }

            bool downloadedFresh = false;
            foreach (string candidateUrl in candidateUrls)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(candidateUrl) || IsPlaceholderImageUrl(candidateUrl)) continue;

                _failedUrlCache.TryRemove(candidateUrl, out _);
                if (await DownloadImageFileAsync(candidateUrl, tempOriginalFile, item.Url, ct).ConfigureAwait(false))
                {
                    if (File.Exists(tempOriginalFile) && new FileInfo(tempOriginalFile).Length > 0)
                    {
                        try
                        {
                            File.Copy(tempOriginalFile, originalFile, overwrite: true);
                            File.Delete(tempOriginalFile);
                        }
                        catch { }

                        GenerateThumbImageFromOriginal(originalFile, thumbFile);
                        string targetThumb = File.Exists(thumbFile) ? thumbFile : originalFile;
                        await LoadThumbnailBitmapToItemAsync(item, targetThumb, originalFile).ConfigureAwait(false);
                        if (item.ThumbnailBitmap != null)
                        {
                            downloadedFresh = true;
                            break;
                        }
                    }
                }
            }

            // 2. Nếu mạng lỗi tạm thời nhưng trên đĩa đã có sẵn file cache hợp lệ thì khôi phục lại từ đĩa (tránh mất ảnh cũ)
            if (!downloadedFresh)
            {
                if (File.Exists(thumbFile) && new FileInfo(thumbFile).Length > 0)
                {
                    await LoadThumbnailBitmapToItemAsync(item, thumbFile, File.Exists(originalFile) ? originalFile : thumbFile).ConfigureAwait(false);
                }
                else if (File.Exists(originalFile) && new FileInfo(originalFile).Length > 0)
                {
                    GenerateThumbImageFromOriginal(originalFile, thumbFile);
                    string targetThumb = File.Exists(thumbFile) ? thumbFile : originalFile;
                    await LoadThumbnailBitmapToItemAsync(item, targetThumb, originalFile).ConfigureAwait(false);
                }
            }
        }
        catch { }
        finally
        {
            try { if (File.Exists(tempOriginalFile)) File.Delete(tempOriginalFile); } catch { }
            await RunOnUiAsync(() => item.IsThumbnailLoading = false);
        }
    }

    private static async Task LoadThumbnailBitmapToItemAsync(ComicBookItem item, string filePath, string? originalPath = null)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            byte[] bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            if (bytes.Length == 0) return;

            await RunOnUiAsync(() =>
            {
                try
                {
                    item.ThumbnailLocalPath = filePath;
                    if (!string.IsNullOrWhiteSpace(originalPath))
                    {
                        item.OriginalCoverLocalPath = originalPath;
                    }
                    if (Avalonia.Application.Current != null)
                    {
                        // Không thu hồi (evict) ThumbnailBitmap của các truyện trong hàng đợi vì ảnh đã được thu nhỏ 140px (~100KB/ảnh),
                        // giúp tránh hiện tượng tự nhiên mất thumbnail khi danh sách có > 35 truyện hoặc khi quét lại thumbnail.
                        using var ms = new MemoryStream(bytes);
                        try
                        {
                            item.ThumbnailBitmap = Bitmap.DecodeToWidth(ms, 140);
                        }
                        catch
                        {
                            ms.Position = 0;
                            item.ThumbnailBitmap = new Bitmap(ms);
                        }
                    }
                }
                catch { }
            });
        }
        catch { }
    }

    private static void GenerateThumbImageFromOriginal(string originalFile, string thumbFile)
    {
        try
        {
            if (!File.Exists(originalFile)) return;
            byte[] origBytes = File.ReadAllBytes(originalFile);
            if (origBytes.Length == 0) return;

            using var codec = SKCodec.Create(new MemoryStream(origBytes));
            if (codec == null) return;

            using var originalBitmap = SKBitmap.Decode(codec);
            if (originalBitmap == null) return;

            int targetWidth = 220;
            if (originalBitmap.Width <= targetWidth)
            {
                File.Copy(originalFile, thumbFile, overwrite: true);
                return;
            }

            int targetHeight = (int)Math.Round((double)originalBitmap.Height * targetWidth / originalBitmap.Width);
            targetHeight = Math.Max(10, targetHeight);

            var imageInfo = new SKImageInfo(targetWidth, targetHeight, originalBitmap.ColorType, originalBitmap.AlphaType);
            using var resizedBitmap = originalBitmap.Resize(imageInfo, SKSamplingOptions.Default);
            if (resizedBitmap == null) return;

            using var image = SKImage.FromBitmap(resizedBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);

            using var fs = new FileStream(thumbFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            data.SaveTo(fs);
        }
        catch { }
    }

    private async Task<bool> DownloadImageFileAsync(string imageUrl, string destinationPath, string? refererUrl, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            if (imageUrl.Contains("ehgt.org", StringComparison.OrdinalIgnoreCase) ||
                imageUrl.Contains("e-hentai.org", StringComparison.OrdinalIgnoreCase) ||
                imageUrl.Contains("exhentai.org", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Referrer = new Uri("https://e-hentai.org/");
                request.Headers.TryAddWithoutValidation("Cookie", "nw=1; nw=always; sl=dm_1");
            }
            else if (!string.IsNullOrWhiteSpace(refererUrl) && Uri.TryCreate(refererUrl, UriKind.Absolute, out var refUri))
            {
                request.Headers.Referrer = refUri;
            }
            else if (imageUrl.Contains("thuviensach.vn", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Referrer = new Uri("https://thuviensach.vn/");
            }
            else if (imageUrl.Contains("gold-usergeneratedcontent.net", StringComparison.OrdinalIgnoreCase) || imageUrl.Contains("hitomi.la", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Referrer = new Uri("https://hitomi.la/");
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes == null || bytes.Length < 64) return false;

            // Xác thực dữ liệu tải về thực sự là ảnh hợp lệ (tránh trang HTML Cloudflare / 404 giả 200)
            using var verifyStream = new MemoryStream(bytes);
            using var codec = SKCodec.Create(verifyStream);
            if (codec == null) return false;

            string? dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllBytesAsync(destinationPath, bytes, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<List<string>> ResolveCandidateUrlsAsync(ComicBookItem item, CancellationToken ct, bool forceFreshResolve = false)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? u, string basePage)
        {
            if (string.IsNullOrWhiteSpace(u)) return;
            string clean = WebUtility.HtmlDecode(u).Trim();
            if (IsPlaceholderImageUrl(clean)) return;
            string abs = MakeAbsoluteUrl(clean, basePage);
            if (!string.IsNullOrWhiteSpace(abs) && !IsPlaceholderImageUrl(abs) && seen.Add(abs))
            {
                urls.Add(abs);
            }
        }

        string link = item.Url?.Trim() ?? string.Empty;

        // 1. Nếu item đã có CoverUrl sẵn từ scraper (và chưa từng lỗi)
        if (!forceFreshResolve && !string.IsNullOrWhiteSpace(item.CoverUrl) && !IsPlaceholderImageUrl(item.CoverUrl) && !_failedUrlCache.ContainsKey(item.CoverUrl))
        {
            AddCandidate(item.CoverUrl, link);
        }

        if (string.IsNullOrWhiteSpace(link)) return urls;

        if (!forceFreshResolve && _candidateUrlCache.TryGetValue(link, out string? cached) && !string.IsNullOrWhiteSpace(cached) && !_failedUrlCache.ContainsKey(cached))
        {
            AddCandidate(cached, link);
        }

        // Nếu không ép quét mới và đã có ứng viên từ CoverUrl/Cache thì thử tải ngay trước
        if (!forceFreshResolve && urls.Count > 0)
        {
            return urls;
        }

        try
        {
            // TruyenQQ: Thu thập tất cả ứng viên từ book_avatar + og:image và bóc tách Tags
            if (link.Contains("truyenqq", StringComparison.OrdinalIgnoreCase))
            {
                string html = await FetchStringAsync(link, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(html))
                {
                    CacheExtractedTagsIfNeeded(item, html, link);

                    var avatarBlock = Regex.Match(html, @"<div[^>]*class=[""'][^""']*\bbook_avatar\b[^""']*[""'][^>]*>(?<content>[\s\S]*?)</div>", RegexOptions.IgnoreCase);
                    string scope = avatarBlock.Success ? avatarBlock.Groups["content"].Value : html;

                    foreach (Match m in Regex.Matches(scope, @"(?:src|data-original|data-src|data-fb)=[""'](?<url>[^""']+?\.(?:jpe?g|png|gif|webp|bmp)(?:\?[^""']*)?)[""']", RegexOptions.IgnoreCase))
                    {
                        string raw = m.Groups["url"].Value;
                        if (!IsPlaceholderImageUrl(raw))
                        {
                            AddCandidate(raw, link);
                        }
                    }

                    string og = ExtractRegexGroup(html, @"<meta[^>]*property=[""']og:image[""'][^>]*content=[""'](?<url>[^""']+)[""']");
                    if (!string.IsNullOrWhiteSpace(og))
                    {
                        AddCandidate(og, link);
                    }

                    foreach (Match m in Regex.Matches(scope, @"data-ni=[""'](?<url>[^""']+)[""']", RegexOptions.IgnoreCase))
                    {
                        AddCandidate(m.Groups["url"].Value, link);
                    }

                    if (urls.Count > 0) _candidateUrlCache[link] = urls[0];
                }
            }
            // NetTruyen
            else if (link.Contains("nettruyen", StringComparison.OrdinalIgnoreCase))
            {
                string html = await FetchStringAsync(link, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(html))
                {
                    CacheExtractedTagsIfNeeded(item, html, link);
                    string nettruyenCover = ExtractRegexGroup(html, @"<div[^>]*class=[""'][^""']*\bcol-image\b[^""']*[""'][^>]*>[\s\S]*?<img[^>]+(?:data-original|data-src|src)=[""'](?<url>[^""']+)[""']");
                    AddCandidate(nettruyenCover, link);
                    string og = ExtractRegexGroup(html, @"<meta[^>]*property=[""']og:image[""'][^>]*content=[""'](?<url>[^""']+)[""']");
                    AddCandidate(og, link);
                    if (urls.Count > 0) _candidateUrlCache[link] = urls[0];
                }
            }
            // E-Hentai / ExHentai: Ưu tiên gọi Official GData API để lấy chính xác ảnh bìa thumb và tags mà không bị chặn HTML
            else if (link.Contains("e-hentai.org/g/", StringComparison.OrdinalIgnoreCase) || link.Contains("exhentai.org/g/", StringComparison.OrdinalIgnoreCase))
            {
                var gidMatch = Regex.Match(link, @"/g/(?<gid>\d+)/(?<token>[a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
                if (gidMatch.Success && long.TryParse(gidMatch.Groups["gid"].Value, out long gid))
                {
                    string token = gidMatch.Groups["token"].Value;
                    try
                    {
                        var apiPayload = new
                        {
                            method = "gdata",
                            gidlist = new object[] { new object[] { gid, token } },
                            @namespace = 1
                        };
                        using var apiReq = new HttpRequestMessage(HttpMethod.Post, "https://api.e-hentai.org/api.php")
                        {
                            Content = new StringContent(JsonSerializer.Serialize(apiPayload), Encoding.UTF8, "application/json")
                        };
                        using var apiRes = await _httpClient.SendAsync(apiReq, ct).ConfigureAwait(false);
                        if (apiRes.IsSuccessStatusCode)
                        {
                            string json = await apiRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("gmetadata", out var gmeta) && gmeta.GetArrayLength() > 0)
                            {
                                var first = gmeta[0];
                                if (first.TryGetProperty("thumb", out var thumbProp))
                                {
                                    string apiThumb = thumbProp.GetString() ?? string.Empty;
                                    if (!string.IsNullOrWhiteSpace(apiThumb))
                                    {
                                        AddCandidate(apiThumb, link);
                                        await RunOnUiAsync(() => item.CoverUrl = apiThumb);
                                    }
                                }
                                if (first.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
                                {
                                    var tagList = new List<string>();
                                    foreach (var t in tagsProp.EnumerateArray())
                                    {
                                        string? tv = t.GetString();
                                        if (!string.IsNullOrWhiteSpace(tv)) tagList.Add(tv);
                                        if (tagList.Count >= 18) break;
                                    }
                                    if (tagList.Count > 0)
                                    {
                                        string formattedTags = "Tags: " + string.Join(", ", tagList);
                                        try
                                        {
                                            string cacheDir = GetItemCacheDirectory(item);
                                            File.WriteAllText(Path.Combine(cacheDir, "cover.tags.txt"), formattedTags);
                                        }
                                        catch { }
                                        await RunOnUiAsync(() => item.PreviewTagsText = formattedTags);
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (urls.Count == 0)
                {
                    string html = await FetchStringAsync(link, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(html))
                    {
                        CacheExtractedTagsIfNeeded(item, html, link);
                        string eCover = ExtractRegexGroup(html, @"<div[^>]*id=[""'](?:gcover|gd1)[""'][^>]*>[\s\S]*?(?:src=[""'](?<url>https?://[^""']+)[""']|url\((['""]?)(?<url>https?://[^'"")]+)\1\))");
                        if (string.IsNullOrWhiteSpace(eCover))
                        {
                            eCover = ExtractRegexGroup(html, @"init~(?<url>https?://[^~""]+?\.(?:jpe?g|png|webp))");
                        }
                        AddCandidate(eCover, link);
                    }
                }

                if (urls.Count > 0) _candidateUrlCache[link] = urls[0];
            }
            // Hentaiforce
            else if (link.Contains("hentaiforce.net/view/", StringComparison.OrdinalIgnoreCase))
            {
                string html = await FetchStringAsync(link, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(html))
                {
                    CacheExtractedTagsIfNeeded(item, html, link);
                    string hfCover = ExtractRegexGroup(html, @"<div[^>]*id=[""']gallery-main-cover[""'][^>]*>[\s\S]*?<img[^>]+(?:src|data-src)=[""'](?<url>[^""']+)[""']");
                    if (string.IsNullOrWhiteSpace(hfCover))
                    {
                        hfCover = ExtractRegexGroup(html, @"<div[^>]*class=[""'][^""']*\bcover\b[^""']*[""'][^>]*>[\s\S]*?<img[^>]*src=[""'](?<url>[^""']+)[""']");
                    }
                    AddCandidate(hfCover, link);
                    if (urls.Count > 0) _candidateUrlCache[link] = urls[0];
                }
            }
            // Dilib / Thuviensach
            else if (link.Contains("dilib.vn", StringComparison.OrdinalIgnoreCase) || link.Contains("thuviensach.vn", StringComparison.OrdinalIgnoreCase))
            {
                string html = await FetchStringAsync(link, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(html))
                {
                    CacheExtractedTagsIfNeeded(item, html, link);
                    string dilibCover = ExtractRegexGroup(html, @"<div[^>]*class=[""'][^""']*\bbook-info\b[^""']*[""'][\s\S]*?<img[^>]*src=[""'](?<url>[^""']+)[""']");
                    AddCandidate(dilibCover, link);
                    if (urls.Count > 0) _candidateUrlCache[link] = urls[0];
                }
            }
            // Hitomi.la
            else if (link.Contains("hitomi.la", StringComparison.OrdinalIgnoreCase))
            {
                string galleryId = HitomiResolverService.ExtractGalleryId(link);
                if (!string.IsNullOrEmpty(galleryId))
                {
                    string jsUrl = $"https://ltn.gold-usergeneratedcontent.net/galleries/{galleryId}.js";
                    string jsContent = await FetchStringAsync(jsUrl, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(jsContent))
                    {
                        string json = jsContent.Replace("var galleryinfo = ", "").Trim().TrimEnd(';');
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
                        {
                            var firstFile = files[0];
                            string hash = firstFile.GetProperty("hash").GetString() ?? "";
                            string name = firstFile.GetProperty("name").GetString() ?? "";
                            if (!string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(name))
                            {
                                string thumbUrl = HitomiResolverService.ResolveImageUrl(hash, name, isThumbnail: true);
                                if (!string.IsNullOrEmpty(thumbUrl))
                                {
                                    AddCandidate(thumbUrl, link);
                                    _candidateUrlCache[link] = thumbUrl;
                                }
                            }
                        }
                    }
                }
            }
            // Các domain khác (SayHentai, Damconuong, Daomeoden, Vi-Hentai, Loppytoon)
            else
            {
                string html = await FetchStringAsync(link, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(html))
                {
                    CacheExtractedTagsIfNeeded(item, html, link);
                    string og = ExtractRegexGroup(html, @"<meta[^>]*property=[""']og:image[""'][^>]*content=[""'](?<url>[^""']+)[""']");
                    AddCandidate(og, link);
                    if (urls.Count > 0) _candidateUrlCache[link] = urls[0];
                }
            }
        }
        catch { }

        return urls;
    }

    private void CacheExtractedTagsIfNeeded(ComicBookItem item, string html, string link)
    {
        if (string.IsNullOrWhiteSpace(html)) return;
        string extractedTags = ComicScraperService.ExtractPreviewTags(html, link);
        if (!string.IsNullOrWhiteSpace(extractedTags))
        {
            try
            {
                string cacheDir = GetItemCacheDirectory(item);
                string tagsFile = Path.Combine(cacheDir, "cover.tags.txt");
                File.WriteAllText(tagsFile, extractedTags);
            }
            catch { }

            _ = RunOnUiAsync(() =>
            {
                if (string.IsNullOrWhiteSpace(item.PreviewTagsText))
                {
                    item.PreviewTagsText = extractedTags;
                }
            });
        }
    }

    private async Task<string> FetchStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (url.Contains("e-hentai.org", StringComparison.OrdinalIgnoreCase) || url.Contains("exhentai.org", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation("Referer", "https://e-hentai.org/");
                request.Headers.TryAddWithoutValidation("Cookie", "nw=1; nw=always; sl=dm_1");
            }
            else if (url.Contains("hentaiforce.net", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation("Referer", "https://hentaiforce.net/");
            }
            else if (url.Contains("hitomi.la", StringComparison.OrdinalIgnoreCase) || url.Contains("gold-usergeneratedcontent.net", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation("Referer", "https://hitomi.la/");
            }

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
        }
        catch { }
        return string.Empty;
    }

    private string GetItemCacheDirectory(ComicBookItem item)
    {
        string domain = item.Domain?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(item.Url))
        {
            try { domain = new Uri(item.Url).Host; } catch { }
        }
        if (string.IsNullOrWhiteSpace(domain)) domain = "unknown";

        string key = !string.IsNullOrWhiteSpace(item.Url) ? item.Url : item.Title;
        string hash = ComputeMd5Hash(key);

        string safeDomain = SanitizeFileName(domain);
        string folder = Path.Combine(_cacheRoot, safeDomain, hash);
        try
        {
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
        }
        catch { }
        return folder;
    }

    private static string ComputeMd5Hash(string input)
    {
        using var md5 = MD5.Create();
        byte[] inputBytes = Encoding.UTF8.GetBytes(input ?? string.Empty);
        byte[] hashBytes = md5.ComputeHash(inputBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "default";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        return sb.ToString().Trim();
    }

    private static string ExtractRegexGroup(string input, string pattern)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["url"].Value.Trim() : string.Empty;
    }

    private static string MakeAbsoluteUrl(string relative, string baseUri)
    {
        if (string.IsNullOrWhiteSpace(relative)) return string.Empty;
        if (Uri.TryCreate(relative, UriKind.Absolute, out var abs)) return abs.ToString();
        if (Uri.TryCreate(new Uri(baseUri), relative, out var combined)) return combined.ToString();
        return relative;
    }

    private static bool IsRateSensitive(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string lower = url.ToLowerInvariant();
        return lower.Contains("nhentai.") || lower.Contains("truyenqq");
    }
}

