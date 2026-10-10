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

    public async Task EnsureThumbnailCacheAsync(ComicBookItem item, CancellationToken ct = default)
    {
        if (item == null || ct.IsCancellationRequested) return;

        // Nếu đã có Bitmap nạp vào UI thì bỏ qua
        if (item.ThumbnailBitmap != null && item.HasThumbnailFile) return;

        string cacheDir = GetItemCacheDirectory(item);
        string originalFile = Path.Combine(cacheDir, "cover.jpg");
        string thumbFile = Path.Combine(cacheDir, "cover.thumb.jpg");

        // Kiểm tra xem đã có sẵn file cache trên ổ đĩa chưa
        if (File.Exists(thumbFile) && new FileInfo(thumbFile).Length > 0)
        {
            await LoadThumbnailBitmapToItemAsync(item, thumbFile);
            return;
        }

        if (File.Exists(originalFile) && new FileInfo(originalFile).Length > 0)
        {
            GenerateThumbImageFromOriginal(originalFile, thumbFile);
            string fileToLoad = File.Exists(thumbFile) ? thumbFile : originalFile;
            await LoadThumbnailBitmapToItemAsync(item, fileToLoad);
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
            lock (_jitter) { jitterMs = _jitter.Next(200, 600); }
            await Task.Delay(jitterMs, ct).ConfigureAwait(false);
        }

        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => item.IsThumbnailLoading = true);

            foreach (string candidateUrl in candidateUrls)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(candidateUrl) || _failedUrlCache.ContainsKey(candidateUrl)) continue;

                bool downloaded = await DownloadImageFileAsync(candidateUrl, originalFile, item.Url, ct).ConfigureAwait(false);
                if (downloaded && File.Exists(originalFile) && new FileInfo(originalFile).Length > 0)
                {
                    GenerateThumbImageFromOriginal(originalFile, thumbFile);
                    string targetThumb = File.Exists(thumbFile) ? thumbFile : originalFile;
                    await LoadThumbnailBitmapToItemAsync(item, targetThumb);
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
            await Dispatcher.UIThread.InvokeAsync(() => item.IsThumbnailLoading = false);
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

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            item.ThumbnailBitmap = null;
            item.ThumbnailLocalPath = null;
        });

        await EnsureThumbnailCacheAsync(item, ct);
    }

    private static async Task LoadThumbnailBitmapToItemAsync(ComicBookItem item, string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            byte[] bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            if (bytes.Length == 0) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    using var ms = new MemoryStream(bytes);
                    var bmp = new Bitmap(ms);
                    item.ThumbnailBitmap = bmp;
                    item.ThumbnailLocalPath = filePath;
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
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 82);

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

            string? dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await stream.CopyToAsync(fileStream, 81920, ct).ConfigureAwait(false);

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

        // 1. Nếu item đã có CoverUrl sẵn từ scraper
        if (!string.IsNullOrWhiteSpace(item.CoverUrl))
        {
            urls.Add(item.CoverUrl);
            return urls;
        }

        string link = item.Url?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(link)) return urls;

        if (_candidateUrlCache.TryGetValue(link, out string? cached) && !string.IsNullOrWhiteSpace(cached))
        {
            urls.Add(cached);
            return urls;
        }

        try
        {
            // TruyenQQ
            if (link.Contains("truyenqq"))
            {
                string html = await FetchStringAsync(link, ct).ConfigureAwait(false);
                string truyenqqCover = ExtractRegexGroup(html, @"<div[^>]*class=[""'][^""']*\bbook_avatar\b[^""']*[""'][^>]*>\s*<img[^>]*src=[""'](?<url>[^""']+)[""']");
                if (!string.IsNullOrWhiteSpace(truyenqqCover))
                {
                    string abs = MakeAbsoluteUrl(truyenqqCover, link);
                    urls.Add(abs);
                    _candidateUrlCache[link] = abs;
                    return urls;
                }
            }
            // NetTruyen
            else if (link.Contains("nettruyen"))
            {
                string html = await FetchStringAsync(link, ct).ConfigureAwait(false);
                string nettruyenCover = ExtractRegexGroup(html, @"<div[^>]*class=[""'][^""']*\bcol-image\b[^""']*[""'][^>]*>\s*<img[^>]*src=[""'](?<url>[^""']+)[""']");
                if (string.IsNullOrWhiteSpace(nettruyenCover))
                {
                    nettruyenCover = ExtractRegexGroup(html, @"<div[^>]*class=[""'][^""']*\bcol-image\b[^""']*[""'][^>]*>\s*<img[^>]*data-src=[""'](?<url>[^""']+)[""']");
                }
                if (!string.IsNullOrWhiteSpace(nettruyenCover))
                {
                    string abs = MakeAbsoluteUrl(nettruyenCover, link);
                    urls.Add(abs);
                    _candidateUrlCache[link] = abs;
                    return urls;
                }
            }
            // E-Hentai / ExHentai
            else if (link.Contains("e-hentai.org/g/") || link.Contains("exhentai.org/g/"))
            {
                string html = await FetchStringAsync(link, ct).ConfigureAwait(false);
                string eCover = ExtractRegexGroup(html, @"<div[^>]*id=[""']gcover[""'][^>]*>[\s\S]*?<img[^>]*src=[""'](?<url>[^""']+)[""']");
                if (string.IsNullOrWhiteSpace(eCover))
                {
                    eCover = ExtractRegexGroup(html, @"init~(?<url>https?://[^~""]+?\.(?:jpe?g|png|webp))");
                }
                if (!string.IsNullOrWhiteSpace(eCover))
                {
                    urls.Add(eCover);
                    _candidateUrlCache[link] = eCover;
                    return urls;
                }
            }
            // Hentaiforce
            else if (link.Contains("hentaiforce.net/view/"))
            {
                string html = await FetchStringAsync(link, ct).ConfigureAwait(false);
                string hfCover = ExtractRegexGroup(html, @"<div[^>]*class=[""'][^""']*\bcover\b[^""']*[""'][^>]*>\s*<img[^>]*src=[""'](?<url>[^""']+)[""']");
                if (!string.IsNullOrWhiteSpace(hfCover))
                {
                    string abs = MakeAbsoluteUrl(hfCover, link);
                    urls.Add(abs);
                    _candidateUrlCache[link] = abs;
                    return urls;
                }
            }
            // Dilib / Thuviensach
            else if (link.Contains("dilib.vn") || link.Contains("thuviensach.vn"))
            {
                string html = await FetchStringAsync(link, ct).ConfigureAwait(false);
                string dilibCover = ExtractRegexGroup(html, @"<div[^>]*class=[""'][^""']*\bbook-info\b[^""']*[""'][\s\S]*?<img[^>]*src=[""'](?<url>[^""']+)[""']");
                if (!string.IsNullOrWhiteSpace(dilibCover))
                {
                    string abs = MakeAbsoluteUrl(dilibCover, link);
                    urls.Add(abs);
                    _candidateUrlCache[link] = abs;
                    return urls;
                }
            }
            // Hitomi.la
            else if (link.Contains("hitomi.la"))
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
                                    urls.Add(thumbUrl);
                                    _candidateUrlCache[link] = thumbUrl;
                                    return urls;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return urls;
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

