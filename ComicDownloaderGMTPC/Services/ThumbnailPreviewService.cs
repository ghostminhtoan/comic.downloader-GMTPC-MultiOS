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
    private readonly SemaphoreSlim _generalSemaphore = new(4, 4);
    private readonly SemaphoreSlim _rateSensitiveSemaphore = new(2, 2);
    private readonly ConcurrentDictionary<string, string> _candidateUrlCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _failedUrlCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cacheRoot;
    private static readonly Random _jitter = new();

    private ThumbnailPreviewService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 8
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");

        _cacheRoot = Path.Combine(AppContext.BaseDirectory, ".tmp", "preview-cache");
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

    public async Task EnsureHighResPreviewAsync(ComicBookItem? item, CancellationToken ct = default)
    {
        if (item == null || ct.IsCancellationRequested) return;
        if (item.HighResPreviewBitmap != null && !string.IsNullOrWhiteSpace(item.PreviewTagsText)) return;

        await EnsureThumbnailCacheAsync(item, ct).ConfigureAwait(false);

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
                                using var ms = new MemoryStream(bytes);
                                item.HighResPreviewBitmap = new Bitmap(ms);
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

    public async Task EnsureThumbnailCacheAsync(ComicBookItem item, CancellationToken ct = default)
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

        // Kiểm tra xem đã có sẵn file cache trên ổ đĩa chưa
        if (File.Exists(thumbFile) && new FileInfo(thumbFile).Length > 0)
        {
            await LoadThumbnailBitmapToItemAsync(item, thumbFile, File.Exists(originalFile) ? originalFile : thumbFile);
            return;
        }

        if (File.Exists(originalFile) && new FileInfo(originalFile).Length > 0)
        {
            GenerateThumbImageFromOriginal(originalFile, thumbFile);
            string fileToLoad = File.Exists(thumbFile) ? thumbFile : originalFile;
            await LoadThumbnailBitmapToItemAsync(item, fileToLoad, originalFile);
            return;
        }

        // Cần tải từ mạng
        List<string> candidateUrls = await ResolveCandidateUrlsAsync(item, ct).ConfigureAwait(false);
        if (candidateUrls.Count == 0) return;

        bool isRateSensitive = IsRateSensitive(item.Url);
        SemaphoreSlim semaphore = isRateSensitive ? _rateSensitiveSemaphore : _generalSemaphore;

        if (isRateSensitive)
        {
            int jitterMs;
            lock (_jitter) { jitterMs = _jitter.Next(120, 350); }
            await Task.Delay(jitterMs, ct).ConfigureAwait(false);
        }

        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RunOnUiAsync(() => item.IsThumbnailLoading = true);

            foreach (string candidateUrl in candidateUrls)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(candidateUrl) || _failedUrlCache.ContainsKey(candidateUrl)) continue;

                bool downloaded = await DownloadImageFileAsync(candidateUrl, originalFile, item.Url, ct).ConfigureAwait(false);
                if (downloaded && File.Exists(originalFile) && new FileInfo(originalFile).Length > 0)
                {
                    GenerateThumbImageFromOriginal(originalFile, thumbFile);
                    string targetThumb = File.Exists(thumbFile) ? thumbFile : originalFile;
                    await LoadThumbnailBitmapToItemAsync(item, targetThumb, originalFile);
                    break;
                }
                else
                {
                    _failedUrlCache.TryAdd(candidateUrl, 1);
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

    public async Task PrefetchBatchAsync(IEnumerable<ComicBookItem> items, CancellationToken ct = default)
    {
        if (items == null) return;
        var validItems = items.Where(SupportsPreview).Distinct().ToList();
        if (validItems.Count == 0) return;

        // Ưu tiên các item chưa có ThumbnailFile tải trước
        var sorted = validItems.OrderBy(x => x.HasThumbnailFile ? 1 : 0).ToList();

        var tasks = new List<Task>();
        foreach (var item in sorted)
        {
            if (ct.IsCancellationRequested) break;
            tasks.Add(EnsureThumbnailCacheAsync(item, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task RescanItemAsync(ComicBookItem item, CancellationToken ct = default)
    {
        if (item == null) return;

        try
        {
            string cacheDir = GetItemCacheDirectory(item);
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, recursive: true);
            }
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(item.CoverUrl))
        {
            _failedUrlCache.TryRemove(item.CoverUrl, out _);
        }
        if (!string.IsNullOrWhiteSpace(item.Url))
        {
            _candidateUrlCache.TryRemove(item.Url, out _);
        }

        await RunOnUiAsync(() =>
        {
            item.ThumbnailBitmap = null;
            item.HighResPreviewBitmap = null;
            item.ThumbnailLocalPath = null;
            item.OriginalCoverLocalPath = null;
        });

        await EnsureThumbnailCacheAsync(item, ct);
    }

    private static async Task LoadThumbnailBitmapToItemAsync(ComicBookItem item, string filePath, string? originalPath = null)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            byte[] bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            if (bytes.Length == 0) return;

            byte[]? origBytes = null;
            if (!string.IsNullOrWhiteSpace(originalPath) && File.Exists(originalPath))
            {
                if (string.Equals(filePath, originalPath, StringComparison.OrdinalIgnoreCase))
                {
                    origBytes = bytes;
                }
                else
                {
                    try { origBytes = await File.ReadAllBytesAsync(originalPath).ConfigureAwait(false); } catch { }
                }
            }

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
                        using var ms = new MemoryStream(bytes);
                        item.ThumbnailBitmap = new Bitmap(ms);
                        if (origBytes != null && origBytes.Length > 0)
                        {
                            using var origMs = new MemoryStream(origBytes);
                            item.HighResPreviewBitmap = new Bitmap(origMs);
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
            if (!string.IsNullOrWhiteSpace(refererUrl))
            {
                request.Headers.Referrer = new Uri(refererUrl);
            }
            else if (imageUrl.Contains("thuviensach.vn"))
            {
                request.Headers.Referrer = new Uri("https://thuviensach.vn/");
            }
            else if (imageUrl.Contains("gold-usergeneratedcontent.net") || imageUrl.Contains("hitomi.la"))
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

    private async Task<List<string>> ResolveCandidateUrlsAsync(ComicBookItem item, CancellationToken ct)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? u, string basePage)
        {
            if (string.IsNullOrWhiteSpace(u)) return;
            string clean = WebUtility.HtmlDecode(u).Trim();
            if (string.IsNullOrWhiteSpace(clean) || clean.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
            string abs = MakeAbsoluteUrl(clean, basePage);
            if (!string.IsNullOrWhiteSpace(abs) && seen.Add(abs))
            {
                urls.Add(abs);
            }
        }

        string link = item.Url?.Trim() ?? string.Empty;

        // 1. Nếu item đã có CoverUrl sẵn từ scraper (và chưa từng lỗi)
        if (!string.IsNullOrWhiteSpace(item.CoverUrl) && !_failedUrlCache.ContainsKey(item.CoverUrl))
        {
            AddCandidate(item.CoverUrl, link);
        }

        if (string.IsNullOrWhiteSpace(link)) return urls;

        if (_candidateUrlCache.TryGetValue(link, out string? cached) && !string.IsNullOrWhiteSpace(cached) && !_failedUrlCache.ContainsKey(cached))
        {
            AddCandidate(cached, link);
        }

        // Nếu đã có CoverUrl và đã có PreviewTagsText thì dùng luôn trước, trừ khi CoverUrl nằm trong failed cache
        if (urls.Count > 0 && !string.IsNullOrWhiteSpace(item.PreviewTagsText))
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
                        if (!raw.Contains("no_image", StringComparison.OrdinalIgnoreCase))
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
            // E-Hentai / ExHentai
            else if (link.Contains("e-hentai.org/g/", StringComparison.OrdinalIgnoreCase) || link.Contains("exhentai.org/g/", StringComparison.OrdinalIgnoreCase))
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
                    if (urls.Count > 0) _candidateUrlCache[link] = urls[0];
                }
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
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }
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

