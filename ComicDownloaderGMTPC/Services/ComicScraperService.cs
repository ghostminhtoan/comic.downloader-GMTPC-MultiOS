using System;
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
using ComicDownloaderGMTPC.Models;

namespace ComicDownloaderGMTPC.Services;

public class ComicScraperService
{
    private static readonly Lazy<ComicScraperService> _instance = new(() => new ComicScraperService());
    public static ComicScraperService Instance => _instance.Value;

    private readonly HttpClient _httpClient;
    private readonly CookieContainer _cookieContainer = new();

    // ====================================================================================================
    // Fresh Machine Identity & Auto-Clear Cookie Architecture cho Comix.to (Windows & Android)
    // ====================================================================================================
    public record ComixDeviceIdentity(string UserAgent, string SecChUa, string Platform, string Mobile);

    private static readonly ComixDeviceIdentity[] FreshComixDeviceProfiles =
    [
        new(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
            "\"Google Chrome\";v=\"130\", \"Chromium\";v=\"130\", \"Not?A_Brand\";v=\"99\"",
            "\"Windows\"",
            "?0"
        ),
        new(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36 Edg/129.0.0.0",
            "\"Microsoft Edge\";v=\"129\", \"Chromium\";v=\"129\", \"Not=A?Brand\";v=\"24\"",
            "\"Windows\"",
            "?0"
        ),
        new(
            "Mozilla/5.0 (Linux; Android 12; Oppo K9x Build/SP1A.210812.016) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36",
            "\"Google Chrome\";v=\"130\", \"Chromium\";v=\"130\", \"Not?A_Brand\";v=\"99\"",
            "\"Android\"",
            "?1"
        ),
        new(
            "Mozilla/5.0 (Linux; Android 13; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Mobile Safari/537.36",
            "\"Google Chrome\";v=\"129\", \"Chromium\";v=\"129\", \"Not=A?Brand\";v=\"24\"",
            "\"Android\"",
            "?1"
        ),
        new(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:131.0) Gecko/20100101 Firefox/131.0",
            "",
            "\"Windows\"",
            "?0"
        )
    ];

    private int _currentComixDeviceIndex = 0;
    public ComixDeviceIdentity CurrentComixDevice => FreshComixDeviceProfiles[Math.Abs(_currentComixDeviceIndex) % FreshComixDeviceProfiles.Length];

    private CookieContainer _comixCookieContainer = new();
    private HttpClient _comixHttpClient = null!;
    private readonly object _comixClientLock = new();

    private void InitComixClient()
    {
        _comixCookieContainer = new CookieContainer();
        var handler = DoHResolver.CreateBypassHandler(_comixCookieContainer);
        _comixHttpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Xóa sạch toàn bộ Cookie và tái tạo danh tính máy mới tinh (Clean Machine State) cho Comix.to.
    /// Giúp reset phiên duyệt web, vượt qua các trạng thái rate limit và cờ captcha cũ trên cả Windows lẫn Android.
    /// </summary>
    public void ResetComixSessionAsNewDevice()
    {
        lock (_comixClientLock)
        {
            try
            {
                var uris = new[]
                {
                    new Uri("https://comix.to"),
                    new Uri("https://.comix.to"),
                    new Uri("https://api.comix.to"),
                    new Uri("https://johnsnotes.site")
                };
                foreach (var uri in uris)
                {
                    try
                    {
                        var cookies = _comixCookieContainer.GetCookies(uri);
                        foreach (Cookie c in cookies) c.Expired = true;
                    }
                    catch { }
                }

                var oldClient = _comixHttpClient;
                Interlocked.Increment(ref _currentComixDeviceIndex);
                InitComixClient();
                if (oldClient != null)
                {
                    _ = Task.Delay(30000).ContinueWith(_ =>
                    {
                        try { oldClient.Dispose(); } catch { }
                    }, TaskScheduler.Default);
                }
            }
            catch { }
        }
    }

    public int MaxCpuThreads { get; set; } = OperatingSystem.IsAndroid()
        ? 3
        : Math.Min(4, CpuTopologyHelper.GetMaxLogicalProcessorCount());

    public ComicScraperService()
    {
        var handler = DoHResolver.CreateBypassHandler(_cookieContainer);

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "vi,en-US;q=0.9,en;q=0.8");

        try
        {
            EnsureEHentaiCookies();
            LoadDamconuongCookies("https://damconuong.pet");
            LoadDamconuongCookies("https://damconuong.shop");
        }
        catch { }

        InitComixClient();
    }

    public void EnsureEHentaiCookies()
    {
        try
        {
            var baseUris = new[]
            {
                new Uri("https://e-hentai.org/"),
                new Uri("https://exhentai.org/"),
                new Uri("https://api.e-hentai.org/")
            };
            foreach (var u in baseUris)
            {
                _cookieContainer.Add(u, new Cookie("nw", "always") { Path = "/" });
                _cookieContainer.Add(u, new Cookie("nw", "1") { Path = "/" });
                _cookieContainer.Add(u, new Cookie("sl", "dm_1") { Path = "/" });
            }
        }
        catch { }
    }

    private (string? username, string? password) _damconuongCredentials;

    public void SetDamconuongCredentials(string username, string password)
    {
        _damconuongCredentials = (username?.Trim(), password);
    }

    public static string GetConfigDirectory()
    {
        if (OperatingSystem.IsAndroid())
        {
            string appDir = DownloadEngineService.GetAppSpecificExternalPath();
            if (!Directory.Exists(appDir))
            {
                try { Directory.CreateDirectory(appDir); } catch { }
            }
            return appDir;
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string portableDir = Path.Combine(baseDir, ".portable");
        if (Directory.Exists(portableDir)) return portableDir;
        return baseDir;
    }

    public void SaveDamconuongCookies(string baseUrl)
    {
        try
        {
            var uri = new Uri(baseUrl);
            var cookies = _cookieContainer.GetCookies(uri);
            if (cookies.Count == 0) return;

            var list = new List<SerializableCookie>();
            foreach (Cookie c in cookies)
            {
                list.Add(new SerializableCookie
                {
                    Name = c.Name,
                    Value = c.Value,
                    Domain = c.Domain,
                    Path = c.Path
                });
            }

            string dir = GetConfigDirectory();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "damconuong_cookies.json");
            File.WriteAllText(file, JsonSerializer.Serialize(list));
        }
        catch { }
    }

    public void LoadDamconuongCookies(string? baseUrl = null)
    {
        try
        {
            string file = Path.Combine(GetConfigDirectory(), "damconuong_cookies.json");
            if (!File.Exists(file)) return;

            string json = File.ReadAllText(file);
            var list = JsonSerializer.Deserialize<List<SerializableCookie>>(json);
            if (list == null || list.Count == 0) return;

            string targetUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://damconuong.pet" : baseUrl;
            var uri = new Uri(targetUrl);

            foreach (var sc in list)
            {
                if (string.IsNullOrWhiteSpace(sc.Name) || sc.Value == null) continue;
                string cookieDomain = string.IsNullOrWhiteSpace(sc.Domain) ? uri.Host : sc.Domain;
                string cookiePath = string.IsNullOrWhiteSpace(sc.Path) ? "/" : sc.Path;

                try
                {
                    _cookieContainer.Add(uri, new Cookie(sc.Name, sc.Value, cookiePath, cookieDomain));
                }
                catch
                {
                    try
                    {
                        _cookieContainer.Add(new Cookie(sc.Name, sc.Value, cookiePath, uri.Host));
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    public async Task<bool> EnsureDamconuongLoggedInAsync(string? preferredUrl = null, CancellationToken ct = default)
    {
        string baseUrl = "https://damconuong.pet";
        if (!string.IsNullOrWhiteSpace(preferredUrl) && Uri.TryCreate(preferredUrl, UriKind.Absolute, out var uri))
        {
            baseUrl = $"{uri.Scheme}://{uri.Authority}";
        }

        // Kiểm tra xem đã có cookie login dcn_li hay remember chưa
        try
        {
            var cookies = _cookieContainer.GetCookies(new Uri(baseUrl));
            foreach (Cookie c in cookies)
            {
                if (c.Name.Equals("dcn_li", StringComparison.OrdinalIgnoreCase) && c.Value == "1")
                {
                    return true;
                }
            }
        }
        catch { }

        // Chưa có hoặc hết hạn -> thử đăng nhập lại bằng credentials đã lưu
        if (!string.IsNullOrWhiteSpace(_damconuongCredentials.username) && !string.IsNullOrWhiteSpace(_damconuongCredentials.password))
        {
            return await LoginDamconuongAsync(baseUrl, _damconuongCredentials.username, _damconuongCredentials.password, ct).ConfigureAwait(false);
        }

        return false;
    }

    public async Task<bool> LoginDamconuongAsync(string baseUrl, string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return false;

        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = "https://damconuong.pet";
        baseUrl = baseUrl.TrimEnd('/');

        SetDamconuongCredentials(username, password);

        try
        {
            // 1. GET /login để lấy Session Cookie và CSRF _token của Laravel
            string loginUrl = $"{baseUrl}/login";
            using var getReq = new HttpRequestMessage(HttpMethod.Get, loginUrl);
            getReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            getReq.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            getReq.Headers.Add("Accept-Language", "vi,en-US;q=0.9,en;q=0.8");

            using var getRes = await _httpClient.SendAsync(getReq, ct).ConfigureAwait(false);
            if (!getRes.IsSuccessStatusCode)
            {
                return false;
            }

            string getHtml = await getRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Bắt CSRF _token từ form HTML
            var tokenMatch = Regex.Match(getHtml, @"name=[""']_token[""']\s+value=[""'](?<token>[^""']+)[""']", RegexOptions.IgnoreCase);
            if (!tokenMatch.Success)
            {
                tokenMatch = Regex.Match(getHtml, @"value=[""'](?<token>[^""']+)[""']\s+name=[""']_token[""']", RegexOptions.IgnoreCase);
            }

            string csrfToken = tokenMatch.Success ? tokenMatch.Groups["token"].Value : string.Empty;
            if (string.IsNullOrWhiteSpace(csrfToken))
            {
                var metaMatch = Regex.Match(getHtml, @"name=[""']csrf-token[""']\s+content=[""'](?<token>[^""']+)[""']", RegexOptions.IgnoreCase);
                if (metaMatch.Success) csrfToken = metaMatch.Groups["token"].Value;
            }

            if (string.IsNullOrWhiteSpace(csrfToken))
            {
                return false;
            }

            // 2. POST /login với _token, email, password, remember
            var postData = new List<KeyValuePair<string, string>>
            {
                new("_token", csrfToken),
                new("email", username),
                new("password", password),
                new("remember", "on")
            };

            using var postReq = new HttpRequestMessage(HttpMethod.Post, loginUrl)
            {
                Content = new FormUrlEncodedContent(postData)
            };
            postReq.Headers.Add("Referer", loginUrl);
            postReq.Headers.Add("Origin", baseUrl);
            postReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");

            using var postRes = await _httpClient.SendAsync(postReq, ct).ConfigureAwait(false);

            if (postRes.StatusCode == HttpStatusCode.Redirect || postRes.StatusCode == HttpStatusCode.Found ||
                postRes.StatusCode == HttpStatusCode.SeeOther || postRes.IsSuccessStatusCode)
            {
                var baseUri = new Uri(baseUrl);
                var cookies = _cookieContainer.GetCookies(baseUri);
                bool hasLoginCookie = false;
                foreach (Cookie c in cookies)
                {
                    if (c.Name.Equals("dcn_li", StringComparison.OrdinalIgnoreCase) ||
                        c.Name.StartsWith("remember_web", StringComparison.OrdinalIgnoreCase) ||
                        c.Name.Contains("session", StringComparison.OrdinalIgnoreCase))
                    {
                        hasLoginCookie = true;
                        break;
                    }
                }

                if (hasLoginCookie || postRes.StatusCode == HttpStatusCode.Redirect || postRes.StatusCode == HttpStatusCode.Found)
                {
                    SaveDamconuongCookies(baseUrl);
                    // Đồng bộ thêm cho domain .pet và .shop
                    try
                    {
                        if (baseUrl.Contains("pet")) SaveDamconuongCookies("https://damconuong.shop");
                        else SaveDamconuongCookies("https://damconuong.pet");
                    }
                    catch { }
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LoginDamconuongAsync] Error: {ex.Message}");
        }

        return false;
    }

    public class SerializableCookie
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string Path { get; set; } = "/";
    }

    public static bool IsCategoryOrTagUrl(string url, string domain)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string lowerUrl = url.ToLowerInvariant();
        string lowerDomain = (domain ?? string.Empty).ToLowerInvariant();

        // Mẫu chung nhận diện URL danh mục / thể loại / tag / artist / author / nhóm dịch
        if (lowerUrl.Contains("/the-loai/") ||
            lowerUrl.Contains("/the-loai") ||
            lowerUrl.Contains("/tim-truyen") ||
            lowerUrl.Contains("/tag/") ||
            lowerUrl.Contains("/tags/") ||
            lowerUrl.Contains("/genre/") ||
            lowerUrl.Contains("/genres/") ||
            lowerUrl.Contains("/category/") ||
            lowerUrl.Contains("/categories/") ||
            lowerUrl.Contains("/artist/") ||
            lowerUrl.Contains("/author/") ||
            lowerUrl.Contains("/tac-gia/") ||
            lowerUrl.Contains("/tac-gia") ||
            lowerUrl.Contains("/nhom-dich/") ||
            lowerUrl.Contains("/nhom-dich") ||
            lowerUrl.Contains("/dich-gia/") ||
            lowerUrl.Contains("/dich-gia") ||
            lowerUrl.Contains("/group/") ||
            lowerUrl.Contains("/type/") ||
            lowerUrl.Contains("?tag=") ||
            lowerUrl.Contains("&tag=") ||
            lowerUrl.Contains("search?") ||
            lowerUrl.Contains("search.html") ||
            lowerUrl.Contains("/search"))
        {
            return true;
        }

        // Nhận diện riêng biệt theo từng domain cụ thể
        if (lowerDomain.Contains("truyenqq") || lowerUrl.Contains("truyenqq"))
            return lowerUrl.Contains("/the-loai/") || lowerUrl.Contains("/the-loai") ||
                   lowerUrl.Contains("/tim-truyen") ||
                   lowerUrl.Contains("/tac-gia/") || lowerUrl.Contains("/tac-gia") ||
                   lowerUrl.Contains("/nhom-dich/") || lowerUrl.Contains("/nhom-dich");
        if (lowerDomain.Contains("nettruyen") || lowerUrl.Contains("nettruyen"))
            return lowerUrl.Contains("/tim-truyen") || lowerUrl.Contains("/the-loai") ||
                   lowerUrl.Contains("/nhom-dich/") || lowerUrl.Contains("/nhom-dich") ||
                   lowerUrl.Contains("/tac-gia/") || lowerUrl.Contains("/tac-gia") ||
                   lowerUrl.Contains("web-team");
        if (lowerDomain.Contains("mangadex") || lowerUrl.Contains("mangadex"))
            return lowerUrl.Contains("/tag/") || lowerUrl.Contains("includedtags") || lowerUrl.Contains("/genre/");
        if (lowerDomain.Contains("damconuong") || lowerUrl.Contains("damconuong"))
            return lowerUrl.Contains("/the-loai/") || lowerUrl.Contains("/the-loai") ||
                   lowerUrl.Contains("/nhom-dich/") || lowerUrl.Contains("/nhom-dich") ||
                   lowerUrl.Contains("/tac-gia/") || lowerUrl.Contains("/tac-gia");
        if (lowerDomain.Contains("vihentai") || lowerUrl.Contains("vi-hentai") || lowerUrl.Contains("vihentai"))
            return lowerUrl.Contains("/the-loai/") || lowerUrl.Contains("/tag/") || lowerUrl.Contains("/genre/");
        if (lowerDomain.Contains("sayhentai") || lowerUrl.Contains("sayhentai"))
            return lowerUrl.Contains("/genre/") || lowerUrl.Contains("/tag/");
        if (lowerDomain.Contains("hentai2read") || lowerUrl.Contains("hentai2read"))
            return lowerUrl.Contains("/hentai-list/") || lowerUrl.Contains("/tag/");
        if (lowerDomain.Contains("hitomi") || lowerUrl.Contains("hitomi"))
        {
            if (lowerUrl.Contains("/galleries/") || lowerUrl.Contains("/reader/")) return false;
            if (Regex.IsMatch(lowerUrl, @"(?:-|/)\d+\.html(?:\?.*)?$")) return false;
            return true;
        }
        if (lowerDomain.Contains("hentaiforce") || lowerUrl.Contains("hentaiforce"))
            return lowerUrl.Contains("/tag/") || lowerUrl.Contains("/category/") ||
                   lowerUrl.Contains("/search") || lowerUrl.Contains("/artist/") ||
                   lowerUrl.Contains("/character/") || lowerUrl.Contains("/parody/") ||
                   lowerUrl.Contains("/language/") || !lowerUrl.Contains("/view/");
        if (lowerDomain.Contains("e-hentai") || lowerDomain.Contains("ehentai") || lowerUrl.Contains("e-hentai") || lowerUrl.Contains("exhentai"))
            return lowerUrl.Contains("/tag/") || lowerUrl.Contains("f_search=") || (!lowerUrl.Contains("/g/") && !lowerUrl.Contains("/mpv/"));
        if (lowerDomain.Contains("thuviensach") || lowerUrl.Contains("thuviensach"))
            return lowerUrl.Contains("/the-loai/") || lowerUrl.Contains("/tac-gia/");
        if (lowerDomain.Contains("daomeoden") || lowerUrl.Contains("daomeoden"))
            return lowerUrl.Contains("/the-loai/") || lowerUrl.Contains("/tag/");
        if (lowerDomain.Contains("moetruyen") || lowerUrl.Contains("moetruyen"))
        {
            if (lowerUrl.Contains("/chapters/")) return false;
            if (Regex.IsMatch(lowerUrl, @"/manga/\d+-")) return false;
            return lowerUrl.Contains("/manga") || lowerUrl.Contains("include=") || lowerUrl.Contains("?q=") || lowerUrl.Contains("&q=");
        }
        if (lowerDomain.Contains("loppy") || lowerUrl.Contains("loppy"))
            return lowerUrl.Contains("/the-loai/") || lowerUrl.Contains("/genre/") ||
                   lowerUrl.Contains("/tac-gia/") || lowerUrl.Contains("/nhom-dich/");
        if (lowerDomain.Contains("comix") || lowerUrl.Contains("comix.to"))
            return lowerUrl.Contains("/browse") || lowerUrl.Contains("genres_in=") || lowerUrl.Contains("demos=");

        return false;
    }

    public Task<ComicBookItem> ScrapeBookAsync(string url, int index, CancellationToken ct) => ScrapeBookAsync(url, index, "vi", true, ct);

    public async Task<ComicBookItem> ScrapeBookAsync(string url, int index, string mangadexLang = "vi", bool mangadexFallback = true, CancellationToken ct = default)
    {
        url = DomainRoutingService.NormalizeUrl(url);
        string domain = DomainRoutingService.DetectDomain(url);

        var item = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = domain,
            PreferredLanguage = mangadexLang,
            Status = "Extracting...",
            StatusMessage = "Connecting to server..."
        };

        try
        {
            if (domain.Contains("mangadex"))
            {
                return await ScrapeMangaDexBookAsync(url, index, mangadexLang, mangadexFallback, ct).ConfigureAwait(false);
            }

            if (domain.Contains("suicaodex"))
            {
                return await ScrapeSuicaodexBookAsync(url, index, domain, ct).ConfigureAwait(false);
            }

            if (domain.Contains("loppytoonn"))
            {
                return await ScrapeLoppyBookAsync(url, index, domain, ct).ConfigureAwait(false);
            }

            if (domain.Contains("nettruyen"))
            {
                return await ScrapeNettruyenBookAsync(url, index, domain, ct).ConfigureAwait(false);
            }

            if (domain.Contains("vi-hentai"))
            {
                return await ScrapeViHentaiBookAsync(url, index, domain, ct).ConfigureAwait(false);
            }

            if (domain.Contains("daomeoden"))
            {
                return await ScrapeDaomeodenBookAsync(url, index, domain, ct).ConfigureAwait(false);
            }

            if (domain.Contains("moetruyen"))
            {
                return await ScrapeMoetruyenBookAsync(url, index, domain, ct).ConfigureAwait(false);
            }

            if (domain.Contains("damconuong"))
            {
                return await ScrapeDamconuongBookAsync(url, index, domain, ct).ConfigureAwait(false);
            }

            if (domain.Contains("sayhentai"))
            {
                return await ScrapeSayHentaiBookAsync(url, index, domain, ct).ConfigureAwait(false);
            }

            if (domain.Contains("thuviensach"))
            {
                return await ScrapeDilibBookAsync(url, index, domain, ct).ConfigureAwait(false);
            }

            if (domain.Contains("hentaiforce"))
            {
                return await ScrapeHentaiforceBookAsync(url, index, domain, ct).ConfigureAwait(false);
            }

            if (domain.Contains("hentai2read"))
            {
                return await ScrapeHentai2readBookAsync(url, index, domain, ct).ConfigureAwait(false);
            }

            if (domain.Contains("e-hentai") || domain.Contains("exhentai"))
            {
                return await ScrapeEHentaiBookAsync(url, index, domain, ct).ConfigureAwait(false);
            }

            if (domain.Contains("hitomi"))
            {
                return await ScrapeHitomiBookAsync(url, index, domain, ct).ConfigureAwait(false);
            }

            if (domain.Contains("comix") || url.Contains("comix.to"))
            {
                return await ScrapeComixtoBookAsync(url, index, domain, ct).ConfigureAwait(false);
            }

            string? html = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                string baseDomain = DomainRoutingService.NormalizeUrl(url);
                req.Headers.Add("Referer", baseDomain);
                using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
            }
            catch { }

            // Nếu HttpClient bị Cloudflare 403, 503 hoặc network block, tự động fallback sang NativeWebView headless browser
            if (string.IsNullOrWhiteSpace(html) && NativeWebViewService.Instance.IsAvailable)
            {
                html = await NativeWebViewService.Instance.FetchHtmlAsync(url, 45, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(html))
                {
                    SyncCookiesFromNativeWebView(url);
                }
            }

            if (string.IsNullOrWhiteSpace(html))
            {
                item.Title = ExtractFallbackTitleFromUrl(url);
                item.Status = "Error";
                item.StatusMessage = "HTTP Forbidden (403) or Connection Failed";
                return item;
            }

            item.Title = ExtractTitle(html, url);
            item.CoverUrl = ExtractCoverUrl(html, url);
            item.PreviewTagsText = ExtractPreviewTags(html, url);
            item.Chapters = ExtractChapters(html, url, domain);
            item.TotalChapters = item.Chapters.Count;
            item.LatestChapter = item.Chapters.Count > 0 ? item.Chapters[^1].Title : "N/A";
            item.Status = "Ready";
            item.StatusMessage = $"Extracted {item.TotalChapters} chapters";
        }
        catch (OperationCanceledException)
        {
            item.Title = ExtractFallbackTitleFromUrl(url);
            item.Status = "Stopped";
            item.StatusMessage = "Extraction cancelled";
        }
        catch (Exception ex)
        {
            item.Title = ExtractFallbackTitleFromUrl(url);
            item.Status = "Error";
            item.StatusMessage = ex.Message;
        }

        return item;
    }

    public async Task<List<string>> ExtractChapterImageUrlsAsync(string chapterUrl, string domain, CancellationToken ct = default, List<string>? outReaderUrls = null)
    {
        var images = new List<string>();
        if (string.IsNullOrWhiteSpace(chapterUrl)) return images;

        domain = domain.ToLowerInvariant();

        try
        {
            bool isManual = InstanceConfigService.Instance.IsManualDownloadMode;
            string manualMode = InstanceConfigService.Instance.ManualDownloadMode;

            // 1. Nếu người dùng BẬT chế độ tải thủ công và chọn "Trực tiếp" (Direct):
            // Bất kể web gì cũng sẽ tự tải trực tiếp ảnh/thumbnail từ trang web thay vì bóc tách trang viewer/reader nhúng
            if (isManual && string.Equals(manualMode, "Direct", StringComparison.OrdinalIgnoreCase))
            {
                if (domain.Contains("mangadex"))
                {
                    return await ExtractMangaDexChapterImagesAsync(chapterUrl, ct).ConfigureAwait(false);
                }

                if (domain.Contains("suicaodex"))
                {
                    return await ExtractSuicaodexChapterImageUrlsAsync(chapterUrl, ct).ConfigureAwait(false);
                }

                if (domain.Contains("hentaiforce"))
                {
                    return await ExtractHentaiforceChapterImagesAsync(chapterUrl, ct).ConfigureAwait(false);
                }

                if (domain.Contains("e-hentai") || domain.Contains("exhentai"))
                {
                    return await ExtractEHentaiDirectThumbnailsAsync(chapterUrl, ct).ConfigureAwait(false);
                }

                // Với mọi trang web thông thường: Tải trực tiếp các thẻ ảnh hiển thị trên trang
                string? directHtml = null;
                try
                {
                    using var directReq = new HttpRequestMessage(HttpMethod.Get, chapterUrl);
                    string directBaseDomain = DomainRoutingService.NormalizeUrl(chapterUrl);
                    directReq.Headers.Add("Referer", directBaseDomain);
                    using var directRes = await _httpClient.SendAsync(directReq, ct).ConfigureAwait(false);
                    if (directRes.IsSuccessStatusCode)
                    {
                        directHtml = await directRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    }
                }
                catch { }

                if (string.IsNullOrWhiteSpace(directHtml) && NativeWebViewService.Instance.IsAvailable)
                {
                    directHtml = await NativeWebViewService.Instance.FetchHtmlAsync(chapterUrl, 45, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(directHtml))
                    {
                        SyncCookiesFromNativeWebView(chapterUrl);
                    }
                }

                if (!string.IsNullOrWhiteSpace(directHtml))
                {
                    var directImgs = ExtractDirectImagesFromHtml(directHtml, chapterUrl);
                    if (directImgs.Count > 0) return directImgs;
                }
            }

            // 2. Chế độ Mặc định (khi tắt thủ công) hoặc Chế độ Nhúng (Embedded):
            if (domain.Contains("mangadex"))
            {
                return await ExtractMangaDexChapterImagesAsync(chapterUrl, ct).ConfigureAwait(false);
            }

            if (domain.Contains("suicaodex"))
            {
                return await ExtractSuicaodexChapterImageUrlsAsync(chapterUrl, ct).ConfigureAwait(false);
            }

            if (domain.Contains("e-hentai") || domain.Contains("exhentai"))
            {
                return await ExtractEHentaiChapterImagesAsync(chapterUrl, ct, outReaderUrls).ConfigureAwait(false);
            }

            if (domain.Contains("hentaiforce"))
            {
                return await ExtractHentaiforceChapterImagesAsync(chapterUrl, ct).ConfigureAwait(false);
            }

            if (domain.Contains("hitomi"))
            {
                return await ExtractHitomiChapterImagesAsync(chapterUrl, ct).ConfigureAwait(false);
            }

            if (domain.Contains("comix") || chapterUrl.Contains("comix.to"))
            {
                return await ExtractComixtoChapterImagesAsync(chapterUrl, ct).ConfigureAwait(false);
            }

            string? html = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, chapterUrl);
                string baseDomain = DomainRoutingService.NormalizeUrl(chapterUrl);
                req.Headers.Add("Referer", baseDomain);
                using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(html) && NativeWebViewService.Instance.IsAvailable)
            {
                html = await NativeWebViewService.Instance.FetchHtmlAsync(chapterUrl, 45, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(html))
                {
                    SyncCookiesFromNativeWebView(chapterUrl);
                }
            }

            if (string.IsNullOrWhiteSpace(html)) return images;

            // Bổ sung cho Chế độ Nhúng (Embedded): Kiểm tra nếu trang có nhúng iframe viewer
            if (isManual && string.Equals(manualMode, "Embedded", StringComparison.OrdinalIgnoreCase))
            {
                var iframeMatch = Regex.Match(html, @"<iframe[^>]+src=[""'](?<src>[^""']+)[""']", RegexOptions.IgnoreCase);
                if (iframeMatch.Success)
                {
                    string iframeSrc = MakeAbsoluteUrl(iframeMatch.Groups["src"].Value, chapterUrl);
                    try
                    {
                        using var iframeReq = new HttpRequestMessage(HttpMethod.Get, iframeSrc);
                        iframeReq.Headers.Add("Referer", chapterUrl);
                        using var iframeRes = await _httpClient.SendAsync(iframeReq, ct).ConfigureAwait(false);
                        if (iframeRes.IsSuccessStatusCode)
                        {
                            string iframeHtml = await iframeRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                            var iframeImgs = ExtractDirectImagesFromHtml(iframeHtml, iframeSrc);
                            if (iframeImgs.Count > 0) return iframeImgs;
                        }
                    }
                    catch { }
                }
            }

            // 1. Phân loại theo Domain chuyên sâu
            if (domain.Contains("loppytoonn"))
            {
                var lpImages = ExtractLoppyChapterImages(html, chapterUrl);
                if (lpImages.Count > 0) return lpImages;
            }

            if (domain.Contains("vi-hentai"))
            {
                var viImages = ExtractViHentaiChapterImages(html);
                if (viImages.Count > 0) return viImages;
            }

            if (domain.Contains("daomeoden"))
            {
                var dmdImages = await ExtractDaomeodenChapterImagesAsync(html, chapterUrl, ct).ConfigureAwait(false);
                if (dmdImages.Count > 0) return dmdImages;
            }

            if (domain.Contains("moetruyen"))
            {
                var moeImages = await ExtractMoetruyenChapterImagesAsync(chapterUrl, ct).ConfigureAwait(false);
                if (moeImages.Count > 0) return moeImages;
            }

            if (domain.Contains("damconuong") || domain.Contains("mbpro") || chapterUrl.Contains("damconuong") || chapterUrl.Contains("mbpro"))
            {
                return await ExtractDamconuongChapterImagesAsync(html, chapterUrl, ct).ConfigureAwait(false);
            }

            if (domain.Contains("sayhentai"))
            {
                var shImages = ExtractSayHentaiChapterImages(html, chapterUrl);
                if (shImages.Count > 0) return shImages;
            }

            if (domain.Contains("hentai2read"))
            {
                var h2rImages = ExtractHentai2readDirectImageUrls(html);
                if (h2rImages.Count > 0) return h2rImages;
            }

            if (domain.Contains("thuviensach"))
            {
                var dilibImages = ExtractDilibChapterImages(html, chapterUrl);
                if (dilibImages.Count > 0) return dilibImages;
            }

            if (domain.Contains("nettruyen"))
            {
                var ntImages = ExtractNettruyenChapterImages(html, chapterUrl);
                if (ntImages.Count > 0) return ntImages;
            }

            if (domain.Contains("truyenqq"))
            {
                var qqImages = ExtractTruyenqqChapterImages(html, chapterUrl);
                if (qqImages.Count > 0) return qqImages;
            }

            // 2. Generic Scope: Manga tiêu chuẩn (Bảo vệ: không bao giờ tải trong comment_list)
            string searchScope = StripCommentElements(html);
            var pageBlocks = Regex.Matches(
                searchScope,
                @"<div[^>]+id=[""']page_\d+[""'][^>]*class=[""'][^""']*page-chapter[^""']*[""'][^>]*>.*?</div>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (pageBlocks.Count > 0)
            {
                searchScope = string.Join("\n", pageBlocks.Cast<Match>().Select(m => m.Value));
            }
            else
            {
                var contentMatch = Regex.Match(searchScope, @"<(?:div|section|article)[^>]*(?:chapter_content|story-see-content|reading-detail|chapter-img|reading)[^>]*>.*?</(?:div|section|article)>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (contentMatch.Success)
                {
                    searchScope = contentMatch.Value;
                }
            }

            var imgTags = Regex.Matches(searchScope, @"<(?:img|source)\s+[^>]*>", RegexOptions.IgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in imgTags)
            {
                string tag = m.Value;
                string? imgUrl = ExtractImageUrlFromTag(tag);
                if (string.IsNullOrWhiteSpace(imgUrl) || IsIgnoredImage(imgUrl)) continue;

                string fullUrl = MakeAbsoluteUrl(imgUrl.Trim(), chapterUrl);
                if (seen.Add(fullUrl))
                {
                    images.Add(fullUrl);
                }
            }

            // Nếu không tìm thấy ảnh trong searchScope hoặc ở chế độ Nhúng/Trực tiếp:
            if (images.Count == 0 || (isManual && string.Equals(manualMode, "Embedded", StringComparison.OrdinalIgnoreCase)) || !isManual)
            {
                var embeddedImgs = ExtractEmbeddedImagesFromHtml(html, chapterUrl);
                if (embeddedImgs.Count > 0)
                {
                    return embeddedImgs;
                }
            }
        }
        catch
        {
            // Trả về bất kỳ kết quả nào đã tìm được
        }

        return images;
    }

    #region Domain Specific Scrapers (WPF Aligned)

    private async Task<ComicBookItem> ScrapeNettruyenBookAsync(string url, int index, string domain, CancellationToken ct)
    {
        var item = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = domain,
            Status = "Extracting..."
        };

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            item.Title = ExtractFallbackTitleFromUrl(url);
            item.Status = "Error";
            item.StatusMessage = $"HTTP {(int)res.StatusCode}";
            return item;
        }

        string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        item.Title = ExtractTitle(html, url);
        item.CoverUrl = ExtractCoverUrl(html, url);
        item.PreviewTagsText = ExtractPreviewTags(html, url);

        var uri = new Uri(url);
        string activeDomain = $"{uri.Scheme}://{uri.Host}";
        string[] segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

        // 1. Ưu tiên gọi API AJAX ChapterList của NetTruyen
        if (segments.Length >= 2 && segments[0].Equals("truyen-tranh", StringComparison.OrdinalIgnoreCase))
        {
            string slug = segments[1];
            string apiUrl = $"{activeDomain}/Comic/Services/ComicService.asmx/ChapterList?slug={Uri.EscapeDataString(slug)}";

            try
            {
                using var apiReq = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                apiReq.Headers.Referrer = uri;
                apiReq.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                using var apiRes = await _httpClient.SendAsync(apiReq, ct).ConfigureAwait(false);

                if (apiRes.IsSuccessStatusCode)
                {
                    string json = await apiRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
                        {
                            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var el in dataProp.EnumerateArray())
                            {
                                string numStr = string.Empty;
                                if (el.TryGetProperty("chapter_num", out var numEl))
                                {
                                    numStr = numEl.ValueKind == JsonValueKind.Number ? numEl.GetRawText() : (numEl.GetString() ?? string.Empty);
                                }
                                if (string.IsNullOrWhiteSpace(numStr)) continue;

                                string chapName = string.Empty;
                                if (el.TryGetProperty("chapter_name", out var nameEl))
                                {
                                    chapName = nameEl.GetString() ?? string.Empty;
                                }
                                chapName = string.IsNullOrWhiteSpace(chapName) ? $"Chapter {numStr}" : WebUtility.HtmlDecode(chapName).Trim();

                                string chapSlug = string.Empty;
                                if (el.TryGetProperty("chapter_slug", out var slugEl))
                                {
                                    chapSlug = slugEl.GetString() ?? string.Empty;
                                }

                                string chapUrl;
                                if (!string.IsNullOrWhiteSpace(chapSlug))
                                {
                                    string normalizedSlug = chapSlug.StartsWith("chapter-", StringComparison.OrdinalIgnoreCase)
                                        ? "chuong-" + chapSlug.Substring(8)
                                        : chapSlug;
                                    chapUrl = $"{activeDomain}/truyen-tranh/{slug}/{normalizedSlug}".TrimEnd('/');
                                }
                                else
                                {
                                    chapUrl = $"{activeDomain}/truyen-tranh/{slug}/chuong-{numStr}".TrimEnd('/');
                                }

                                if (seen.Add(chapUrl))
                                {
                                    double.TryParse(numStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedNum);

                                    item.Chapters.Add(new ChapterItem
                                    {
                                        ChapterNumber = parsedNum,
                                        Title = chapName,
                                        Url = chapUrl,
                                        Status = "Waiting"
                                    });
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch {}
        }

        // 2. Fallback quét HTML nếu AJAX không trả về chapter
        if (item.Chapters.Count == 0)
        {
            string pattern = @"<a\b[^>]*href=[""'](?<link>[^""']*(?:chuong|chap|chapter|c|chuong-tranh|chuong-doc)[-_]?\d+(?:\.\d+)?[^""'\s?#]*)[""'][^>]*>(?<name>[\s\S]*?)<\/a>";
            var matches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in matches)
            {
                string rawLink = m.Groups["link"].Value.Trim();
                string fullUrl = MakeAbsoluteUrl(rawLink, activeDomain);
                if (seen.Add(fullUrl))
                {
                    string rawName = WebUtility.HtmlDecode(Regex.Replace(m.Groups["name"].Value, @"<[^>]+>", string.Empty)).Trim();
                    double chapNum = ExtractChapterNumber(rawName, ExtractChapterNumber(fullUrl, 0));

                    item.Chapters.Add(new ChapterItem
                    {
                        ChapterNumber = chapNum,
                        Title = string.IsNullOrWhiteSpace(rawName) ? $"Chapter {chapNum}" : rawName,
                        Url = fullUrl,
                        Status = "Waiting"
                    });
                }
            }
        }

        if (item.Chapters.Count > 0)
        {
            item.Chapters = item.Chapters.OrderBy(c => c.ChapterNumber).ToList();
        }

        item.TotalChapters = item.Chapters.Count;
        item.LatestChapter = item.Chapters.Count > 0 ? item.Chapters[^1].Title : "N/A";
        item.Status = "Ready";
        item.StatusMessage = $"Extracted {item.TotalChapters} chapters";

        return item;
    }

    private async Task<ComicBookItem> ScrapeViHentaiBookAsync(string url, int index, string domain, CancellationToken ct)
    {
        var item = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = domain,
            Status = "Extracting..."
        };

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            item.Title = ExtractFallbackTitleFromUrl(url);
            item.Status = "Error";
            item.StatusMessage = $"HTTP {(int)res.StatusCode}";
            return item;
        }

        string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        item.Title = ExtractTitle(html, url);
        item.CoverUrl = ExtractCoverUrl(html, url);
        item.PreviewTagsText = ExtractPreviewTags(html, url);

        var uri = new Uri(url);
        string mangaSlug = Path.GetFileName(uri.AbsolutePath.TrimEnd('/'));
        var chapterMatches = Regex.Matches(html, @"href=[""'](?<link>[^""']*?/truyen/" + Regex.Escape(mangaSlug) + @"/[^""']+)[""']", RegexOptions.IgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in chapterMatches)
        {
            string link = m.Groups["link"].Value.Trim();
            string fullUrl = MakeAbsoluteUrl(link, url);
            if (seen.Add(fullUrl))
            {
                double chapNum = ExtractChapterNumber(fullUrl, item.Chapters.Count + 1);
                item.Chapters.Add(new ChapterItem
                {
                    ChapterNumber = chapNum,
                    Title = $"Chapter {chapNum}",
                    Url = fullUrl,
                    Status = "Waiting"
                });
            }
        }

        if (item.Chapters.Count > 0)
        {
            item.Chapters = item.Chapters.OrderBy(c => c.ChapterNumber).ToList();
        }
        else
        {
            item.Chapters.Add(new ChapterItem { ChapterNumber = 1, Title = "Chapter 1", Url = url, Status = "Waiting" });
        }

        item.TotalChapters = item.Chapters.Count;
        item.LatestChapter = item.Chapters.Count > 0 ? item.Chapters[^1].Title : "N/A";
        item.Status = "Ready";
        item.StatusMessage = $"Extracted {item.TotalChapters} chapters";

        return item;
    }

    private async Task<ComicBookItem> ScrapeDaomeodenBookAsync(string url, int index, string domain, CancellationToken ct)
    {
        url = DomainRoutingService.NormalizeUrl(url);
        var item = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = domain,
            Status = "Extracting..."
        };

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Referer", "https://daomeoden.net/");
        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            item.Title = ExtractFallbackTitleFromUrl(url);
            item.Status = "Error";
            item.StatusMessage = $"HTTP {(int)res.StatusCode}";
            return item;
        }

        string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        item.Title = ExtractTitle(html, url);
        item.CoverUrl = ExtractCoverUrl(html, url);
        item.PreviewTagsText = ExtractPreviewTags(html, url);

        // Bóc tách thẻ <a> có link đọc truyện tranh và text hiển thị
        var anchorMatches = Regex.Matches(
            html,
            @"<a[^>]+href=[""'](?<link>/doc-truyen-tranh/[^""']+)[""'][^>]*>(?<text>[\s\S]*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in anchorMatches)
        {
            string link = m.Groups["link"].Value.Trim();
            string rawText = Regex.Replace(m.Groups["text"].Value, @"<[^>]+>", " ").Trim();
            string fullUrl = MakeAbsoluteUrl(link, url);
            if (seen.Add(fullUrl))
            {
                double chapNum = ParseDaomeodenChapterNumber(rawText, link, item.Chapters.Count + 1);
                item.Chapters.Add(new ChapterItem
                {
                    ChapterNumber = chapNum,
                    Title = string.IsNullOrWhiteSpace(rawText) ? $"Chương {chapNum}" : rawText,
                    Url = fullUrl,
                    Status = "Waiting"
                });
            }
        }

        // Fallback nếu không bóc tách được từ thẻ <a>
        if (item.Chapters.Count == 0)
        {
            var chapterMatches = Regex.Matches(
                html,
                @"(?:href|openUrl\()\s*(?:=\s*|['""])(?<link>/doc-truyen-tranh/[^'"")\s>]+)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match m in chapterMatches)
            {
                string link = m.Groups["link"].Value.Trim();
                string fullUrl = MakeAbsoluteUrl(link, url);
                if (seen.Add(fullUrl))
                {
                    double chapNum = ParseDaomeodenChapterNumber(string.Empty, link, item.Chapters.Count + 1);
                    item.Chapters.Add(new ChapterItem
                    {
                        ChapterNumber = chapNum,
                        Title = $"Chương {chapNum}",
                        Url = fullUrl,
                        Status = "Waiting"
                    });
                }
            }
        }

        if (item.Chapters.Count > 0)
        {
            item.Chapters = item.Chapters.OrderBy(c => c.ChapterNumber).ToList();
        }
        else
        {
            item.Chapters.Add(new ChapterItem { ChapterNumber = 1, Title = "Chapter 1", Url = url, Status = "Waiting" });
        }

        item.TotalChapters = item.Chapters.Count;
        item.LatestChapter = item.Chapters.Count > 0 ? item.Chapters[^1].Title : "N/A";
        item.Status = "Ready";
        item.StatusMessage = $"Extracted {item.TotalChapters} chapters";

        return item;
    }

    private double ParseDaomeodenChapterNumber(string text, string link, double fallback)
    {
        // 1. Parse từ inner text: "Chương 11", "Chap 11.5"
        if (!string.IsNullOrWhiteSpace(text))
        {
            var matchText = Regex.Match(text, @"(?:chap(?:ter)?|chương|chuong|tập|tap)[\s\-_:/#]*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
            if (matchText.Success && double.TryParse(matchText.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedText))
            {
                return parsedText;
            }
        }

        // 2. Parse từ link: ví dụ "...-chuong-11-63057-11.html"
        if (!string.IsNullOrWhiteSpace(link))
        {
            var matchLink = Regex.Match(link, @"(?:chuong|chap|tap|chapter)[\s\-_:/#]*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
            if (matchLink.Success && double.TryParse(matchLink.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedLink))
            {
                return parsedLink;
            }

            // Lấy số ở đuôi segment cuối: "-(\d+)\.html"
            var matchTail = Regex.Match(link, @"-(\d+(?:\.\d+)?)\.html", RegexOptions.IgnoreCase);
            if (matchTail.Success && double.TryParse(matchTail.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedTail))
            {
                return parsedTail;
            }
        }

        return fallback;
    }

    private async Task<ComicBookItem> ScrapeDamconuongBookAsync(string url, int index, string domain, CancellationToken ct)
    {
        url = DomainRoutingService.NormalizeUrl(url);
        var item = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = domain,
            Status = "Extracting..."
        };

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Referer", url);
        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            item.Title = ExtractFallbackTitleFromUrl(url);
            item.Status = "Error";
            item.StatusMessage = $"HTTP {(int)res.StatusCode}";
            return item;
        }

        string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        item.Title = ExtractTitle(html, url);
        item.CoverUrl = ExtractCoverUrl(html, url);
        item.PreviewTagsText = ExtractPreviewTags(html, url);

        var uri = new Uri(url);
        string[] segs = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        string bookSlug = segs.Length > 1 ? segs[1] : (segs.Length > 0 ? segs[0] : string.Empty);

        string pattern = @"href\s*=\s*[""'](?<href>(?:(?:https?:)?\/\/(?:www\.)?(?:damconuong|mbpro)\.[^\/""']+)?\/truyen\/" + Regex.Escape(bookSlug) + @"\/(?<chapter>[^""'?#>]+)(?:\.html)?)[""']";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in Regex.Matches(html, pattern, RegexOptions.IgnoreCase))
        {
            string href = WebUtility.HtmlDecode(m.Groups["href"].Value.Trim());
            string fullUrl = MakeAbsoluteUrl(href, url);
            if (seen.Add(fullUrl))
            {
                double chapNum = ExtractChapterNumber(fullUrl, item.Chapters.Count + 1);
                item.Chapters.Add(new ChapterItem
                {
                    ChapterNumber = chapNum,
                    Title = $"Chapter {chapNum}",
                    Url = fullUrl,
                    Status = "Waiting"
                });
            }
        }

        if (item.Chapters.Count > 0)
        {
            item.Chapters = item.Chapters.OrderBy(c => c.ChapterNumber).ToList();
        }
        else
        {
            item.Chapters.Add(new ChapterItem { ChapterNumber = 1, Title = "Chapter 1", Url = url, Status = "Waiting" });
        }

        item.TotalChapters = item.Chapters.Count;
        item.LatestChapter = item.Chapters.Count > 0 ? item.Chapters[^1].Title : "N/A";
        item.Status = "Ready";
        item.StatusMessage = $"Extracted {item.TotalChapters} chapters";

        return item;
    }

    private async Task<ComicBookItem> ScrapeSayHentaiBookAsync(string url, int index, string domain, CancellationToken ct)
    {
        var item = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = domain,
            Status = "Extracting..."
        };

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            item.Title = ExtractFallbackTitleFromUrl(url);
            item.Status = "Error";
            item.StatusMessage = $"HTTP {(int)res.StatusCode}";
            return item;
        }

        string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string rawTitle = ExtractTitle(html, url);
        rawTitle = Regex.Replace(rawTitle, @"\s*[\-\|]\s*(?:Việt Hentai|Viet Hentai|Hentai Vietsub HD|Kuro Neko|Mèo đen|sayhentai\.cx|SayHentai\.Vip|SayHentai).*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        item.Title = string.IsNullOrWhiteSpace(rawTitle) ? ExtractFallbackTitleFromUrl(url) : rawTitle;
        item.CoverUrl = ExtractCoverUrl(html, url);
        item.PreviewTagsText = ExtractPreviewTags(html, url);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void CollectChapters(string contentHtml)
        {
            if (string.IsNullOrWhiteSpace(contentHtml)) return;

            var matches = Regex.Matches(contentHtml, @"<li[^>]*class=[""'][^""']*wp-manga-chapter[^""']*[""'][^>]*>\s*<a\s+[^>]*href=[""'](?<link>[^""']+)[""'][^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (matches.Count == 0)
            {
                matches = Regex.Matches(contentHtml, @"<a\s+[^>]*href=[""'](?<link>[^""']*(?:/chuong-|\-chuong\-)[^""']*)[""'][^>]*>(?<text>(?:Chapter|Chương|Chap)\s*\d+[^<]*)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }

            foreach (Match m in matches)
            {
                string link = m.Groups["link"].Value.Trim();
                string fullUrl = MakeAbsoluteUrl(link, url).TrimEnd('/');
                if (seen.Add(fullUrl))
                {
                    string text = Regex.Replace(m.Groups["text"].Value, @"<[^>]+>", "").Trim();
                    double chapNum = ExtractChapterNumber(text, ExtractChapterNumber(fullUrl, item.Chapters.Count + 1));
                    item.Chapters.Add(new ChapterItem
                    {
                        ChapterNumber = chapNum,
                        Title = string.IsNullOrWhiteSpace(text) ? $"Chap {chapNum}" : text,
                        Url = fullUrl,
                        Status = "Waiting"
                    });
                }
            }
        }

        CollectChapters(html);

        // Xử lý AJAX pagination: data-ajax-url hoặc story/{id}/more-chapters
        string moreUrl = "";
        var ajaxMatch = Regex.Match(html, @"data-ajax-url=[""'](?<url>[^""']+)[""']", RegexOptions.IgnoreCase);
        if (ajaxMatch.Success)
        {
            moreUrl = ajaxMatch.Groups["url"].Value.Trim();
        }
        else
        {
            var idMatch = Regex.Match(html, @"(?:data-story-id|data-id|data-post)=[""'](?<id>\d+)[""']", RegexOptions.IgnoreCase);
            if (!idMatch.Success) idMatch = Regex.Match(html, @"id=[""']story_id[""'][^>]*value=[""'](?<id>\d+)[""']", RegexOptions.IgnoreCase);
            if (idMatch.Success)
            {
                moreUrl = $"https://sayhentai.cx/story/{idMatch.Groups["id"].Value}/more-chapters";
            }
        }

        int passes = 0;
        while (!string.IsNullOrEmpty(moreUrl) && passes < 10)
        {
            passes++;
            try
            {
                string fullMoreUrl = MakeAbsoluteUrl(moreUrl, url);
                using var moreReq = new HttpRequestMessage(HttpMethod.Get, fullMoreUrl);
                moreReq.Headers.Add("Referer", url);
                moreReq.Headers.Add("X-Requested-With", "XMLHttpRequest");

                using var moreRes = await _httpClient.SendAsync(moreReq, ct).ConfigureAwait(false);
                if (moreRes.IsSuccessStatusCode)
                {
                    string moreHtml = await moreRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    CollectChapters(moreHtml);

                    var nextAjax = Regex.Match(moreHtml, @"data-ajax-url=[""'](?<url>[^""']+)[""']", RegexOptions.IgnoreCase);
                    if (nextAjax.Success && !string.Equals(nextAjax.Groups["url"].Value.Trim(), moreUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        moreUrl = nextAjax.Groups["url"].Value.Trim();
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }
            catch
            {
                break;
            }
        }

        if (item.Chapters.Count > 0)
        {
            item.Chapters = item.Chapters.OrderBy(c => c.ChapterNumber).ToList();
        }
        else
        {
            item.Chapters.Add(new ChapterItem { ChapterNumber = 1, Title = "Chapter 1", Url = url, Status = "Waiting" });
        }

        item.TotalChapters = item.Chapters.Count;
        item.LatestChapter = item.Chapters.Count > 0 ? item.Chapters[^1].Title : "N/A";
        item.Status = "Ready";
        item.StatusMessage = $"Extracted {item.TotalChapters} chapters";

        return item;
    }

    private async Task<ComicBookItem> ScrapeDilibBookAsync(string url, int index, string domain, CancellationToken ct)
    {
        var item = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = domain,
            Status = "Extracting..."
        };

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            item.Title = ExtractFallbackTitleFromUrl(url);
            item.Status = "Error";
            item.StatusMessage = "Server đang lỗi, hiện tại chưa tải được";
            return item;
        }

        string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html))
        {
            item.Title = ExtractFallbackTitleFromUrl(url);
            item.Status = "Error";
            item.StatusMessage = "Server đang lỗi, hiện tại chưa tải được";
            return item;
        }
        item.Title = ExtractTitle(html, url);
        item.CoverUrl = ExtractCoverUrl(html, url);
        item.PreviewTagsText = ExtractPreviewTags(html, url);

        var matches = Regex.Matches(html, @"<a\s+[^>]*href=[""'](?<link>[^""']*-chap-(?<num>\d+(?:\.\d+)?)[^""']*)[""'][^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in matches)
        {
            string link = m.Groups["link"].Value.Trim();
            string fullUrl = MakeAbsoluteUrl(link, url);
            if (seen.Add(fullUrl))
            {
                string text = Regex.Replace(m.Groups["text"].Value, @"<[^>]+>", "").Trim();
                double chapNum = ExtractChapterNumber(m.Groups["num"].Value, item.Chapters.Count + 1);
                item.Chapters.Add(new ChapterItem
                {
                    ChapterNumber = chapNum,
                    Title = string.IsNullOrWhiteSpace(text) ? $"Chap {chapNum}" : text,
                    Url = fullUrl,
                    Status = "Waiting"
                });
            }
        }

        if (item.Chapters.Count > 0)
        {
            item.Chapters = item.Chapters.OrderBy(c => c.ChapterNumber).ToList();
        }
        else
        {
            item.Chapters.Add(new ChapterItem { ChapterNumber = 1, Title = "Chapter 1", Url = url, Status = "Waiting" });
        }

        item.TotalChapters = item.Chapters.Count;
        item.LatestChapter = item.Chapters.Count > 0 ? item.Chapters[^1].Title : "N/A";
        item.Status = "Ready";
        item.StatusMessage = $"Extracted {item.TotalChapters} chapters";

        return item;
    }

    private async Task<ComicBookItem> ScrapeHentai2readBookAsync(string url, int index, string domain, CancellationToken ct)
    {
        var item = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = domain,
            Status = "Extracting..."
        };

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            item.Title = ExtractFallbackTitleFromUrl(url);
            item.Status = "Error";
            item.StatusMessage = $"HTTP {(int)res.StatusCode}";
            return item;
        }

        string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        item.Title = ExtractTitle(html, url);
        item.CoverUrl = ExtractCoverUrl(html, url);
        item.PreviewTagsText = ExtractPreviewTags(html, url);

        var uri = new Uri(url);
        string slug = uri.AbsolutePath.Trim('/').Split('/')[0];
        string pattern = @"href\s*=\s*[""'](?<href>(?:https?://(?:www\.)?hentai2read\.com)?/" + Regex.Escape(slug) + @"/(?<chapter>[^""'/?#]+)/?)[""']";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in Regex.Matches(html, pattern, RegexOptions.IgnoreCase))
        {
            string href = m.Groups["href"].Value.Trim();
            string fullUrl = MakeAbsoluteUrl(href, url);
            if (seen.Add(fullUrl))
            {
                string chapToken = m.Groups["chapter"].Value;
                double chapNum = ExtractChapterNumber(chapToken, item.Chapters.Count + 1);
                item.Chapters.Add(new ChapterItem
                {
                    ChapterNumber = chapNum,
                    Title = $"Chapter {chapToken}",
                    Url = fullUrl,
                    Status = "Waiting"
                });
            }
        }

        if (item.Chapters.Count > 0)
        {
            item.Chapters = item.Chapters.OrderBy(c => c.ChapterNumber).ToList();
        }
        else
        {
            item.Chapters.Add(new ChapterItem { ChapterNumber = 1, Title = "Chapter 1", Url = url, Status = "Waiting" });
        }

        item.TotalChapters = item.Chapters.Count;
        item.LatestChapter = item.Chapters.Count > 0 ? item.Chapters[^1].Title : "N/A";
        item.Status = "Ready";
        item.StatusMessage = $"Extracted {item.TotalChapters} chapters";

        return item;
    }

    private async Task<ComicBookItem> ScrapeLoppyBookAsync(string url, int index, string domain, CancellationToken ct)
    {
        var item = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = domain,
            Status = "Extracting..."
        };

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            item.Title = ExtractFallbackTitleFromUrl(url);
            item.Status = "Error";
            item.StatusMessage = $"HTTP {(int)res.StatusCode}";
            return item;
        }

        string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // Trích xuất Title
        var titleMatch = Regex.Match(html, @"<div[^>]*class=[""'][^""']*\binfo-title\b[^""']*[""'][^>]*>\s*<h2[^>]*>(?<title>[\s\S]*?)</h2>", RegexOptions.IgnoreCase);
        if (titleMatch.Success)
        {
            item.Title = CleanTitle(titleMatch.Groups["title"].Value);
        }
        else
        {
            item.Title = ExtractTitle(html, url);
        }

        // Trích xuất Cover
        var coverMatch = Regex.Match(html, @"<img[^>]*class=[""'][^""']*\bmain-img\b[^""']*[""'][^>]*src=[""'](?<cover>[^""']+)[""']", RegexOptions.IgnoreCase);
        if (coverMatch.Success)
        {
            item.CoverUrl = MakeAbsoluteUrl(coverMatch.Groups["cover"].Value.Trim(), url);
        }
        else
        {
            item.CoverUrl = ExtractCoverUrl(html, url);
        }

        // Trích xuất Chapters
        var uri = new Uri(url);
        string[] segs = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        string bookSlug = segs.Length >= 2 ? segs[1] : (segs.Length >= 1 ? segs[0] : "truyen");

        string pattern = @"<a[^>]+href=[""'](?<href>(?:https?://(?:www\.)?loppytoonn\.com)?/truyen/" + Regex.Escape(bookSlug) + @"/(?<chap>[^""'#?/\s]+)/?)[""'][^>]*>[\s\S]*?(?:<h3[^>]*>(?<name>[\s\S]*?)</h3>|(?<name2>Chap\s*\d+(?:\.\d+)?))";
        var matches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in matches)
        {
            string href = m.Groups["href"].Value.Trim();
            string chapSlug = m.Groups["chap"].Value.Trim();
            string fullUrl = MakeAbsoluteUrl(href, url);
            if (seen.Add(fullUrl))
            {
                string rawName = m.Groups["name"].Success ? m.Groups["name"].Value : m.Groups["name2"].Value;
                string chapTitle = WebUtility.HtmlDecode(Regex.Replace(rawName ?? string.Empty, @"<[^>]+>", "")).Trim();
                if (string.IsNullOrWhiteSpace(chapTitle))
                {
                    chapTitle = chapSlug.Replace("-", " ");
                }

                double chapNum = ExtractChapterNumber(chapTitle, ExtractChapterNumber(chapSlug, item.Chapters.Count + 1));
                item.Chapters.Add(new ChapterItem
                {
                    ChapterNumber = chapNum,
                    Title = chapTitle,
                    Url = fullUrl,
                    Status = "Waiting"
                });
            }
        }

        if (item.Chapters.Count == 0)
        {
            var simpleMatches = Regex.Matches(html, @"href=[""'](?<href>(?:https?://(?:www\.)?loppytoonn\.com)?/truyen/" + Regex.Escape(bookSlug) + @"/(?<chap>[^""'#?/\s]+)/?)[""']", RegexOptions.IgnoreCase);
            foreach (Match m in simpleMatches)
            {
                string href = m.Groups["href"].Value.Trim();
                string chapSlug = m.Groups["chap"].Value.Trim();
                string fullUrl = MakeAbsoluteUrl(href, url);
                if (seen.Add(fullUrl))
                {
                    double chapNum = ExtractChapterNumber(chapSlug, item.Chapters.Count + 1);
                    item.Chapters.Add(new ChapterItem
                    {
                        ChapterNumber = chapNum,
                        Title = chapSlug.Replace("-", " "),
                        Url = fullUrl,
                        Status = "Waiting"
                    });
                }
            }
        }

        if (item.Chapters.Count > 0)
        {
            item.Chapters = item.Chapters.OrderBy(c => c.ChapterNumber).ToList();
        }
        else
        {
            item.Chapters.Add(new ChapterItem { ChapterNumber = 1, Title = "Chapter 1", Url = url, Status = "Waiting" });
        }

        item.TotalChapters = item.Chapters.Count;
        item.LatestChapter = item.Chapters.Count > 0 ? item.Chapters[^1].Title : "N/A";
        item.Status = "Ready";
        item.StatusMessage = $"Extracted {item.TotalChapters} chapters";

        return item;
    }

    private async Task<ComicBookItem> ScrapeSuicaodexBookAsync(string url, int index, string domain, CancellationToken ct)
    {
        var item = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = domain,
            Status = "Extracting...",
            StatusMessage = "Fetching suicaodex manga page..."
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Referer", "https://suicaodex.com/");
            req.Headers.TryAddWithoutValidation("Cookie", "reader_mode=classic; ui_mode=classic");
            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                item.Status = "Error";
                item.StatusMessage = $"HTTP error {(int)res.StatusCode}";
                return item;
            }

            string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // 1. Trích xuất Title
            string title = string.Empty;
            var h1Match = Regex.Match(html, @"<h1[^>]*>(?<title>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (h1Match.Success)
            {
                title = WebUtility.HtmlDecode(Regex.Replace(h1Match.Groups["title"].Value, @"<[^>]+>", " ")).Trim();
            }
            if (string.IsNullOrWhiteSpace(title) || title.Equals("Facebook", StringComparison.OrdinalIgnoreCase))
            {
                var ogMatch = Regex.Match(html, @"<meta\s+property=""og:title""\s+content=""(?<title>[^""]+)""", RegexOptions.IgnoreCase);
                if (ogMatch.Success)
                {
                    title = WebUtility.HtmlDecode(ogMatch.Groups["title"].Value).Trim();
                }
            }
            if (string.IsNullOrWhiteSpace(title))
            {
                var altMatch = Regex.Match(html, @"alt=""Bìa truyện (?<title>[^""]+)""", RegexOptions.IgnoreCase);
                if (altMatch.Success)
                {
                    title = WebUtility.HtmlDecode(altMatch.Groups["title"].Value).Trim();
                }
            }
            if (string.IsNullOrWhiteSpace(title))
            {
                title = ExtractFallbackTitleFromUrl(url);
            }
            item.Title = CleanTitle(title);

            // 2. Trích xuất Thumbnail / Cover Url
            string coverUrl = string.Empty;
            var coverMatch = Regex.Match(html, @"\[grid-area:cover\][^>]*>.*?<img[^>]+src=""(?<url>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (coverMatch.Success)
            {
                coverUrl = coverMatch.Groups["url"].Value.Trim();
            }
            else
            {
                var directCover = Regex.Match(html, @"https?://i\.suicaodex\.com/covers/[a-zA-Z0-9_\-\./]+", RegexOptions.IgnoreCase);
                if (directCover.Success)
                {
                    coverUrl = directCover.Value.Trim();
                }
                else
                {
                    var ogImg = Regex.Match(html, @"<meta\s+property=""og:image""\s+content=""(?<url>[^""]+)""", RegexOptions.IgnoreCase);
                    if (ogImg.Success) coverUrl = ogImg.Groups["url"].Value.Trim();
                }
            }
            item.CoverUrl = coverUrl;

            // 3. Phân tích tổng số page chapter của Book
            int maxChapterPages = 1;
            var pageMatches = Regex.Matches(html, @"[?&]page=(?<num>\d+)", RegexOptions.IgnoreCase);
            foreach (Match pm in pageMatches)
            {
                if (int.TryParse(pm.Groups["num"].Value, out int cp) && cp > maxChapterPages && cp < 1000)
                {
                    maxChapterPages = cp;
                }
            }

            var allChapters = new Dictionary<string, ChapterItem>(StringComparer.OrdinalIgnoreCase);

            // Parse chapters trang 1
            ParseSuicaodexChaptersFromHtml(html, allChapters);

            // Parse các trang 2..maxChapterPages
            for (int p = 2; p <= maxChapterPages; p++)
            {
                if (ct.IsCancellationRequested) break;
                string pageUrl = $"{url}{(url.Contains('?') ? '&' : '?')}page={p}";
                try
                {
                    using var pReq = new HttpRequestMessage(HttpMethod.Get, pageUrl);
                    pReq.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
                    pReq.Headers.TryAddWithoutValidation("Referer", "https://suicaodex.com/");
                    using var pRes = await _httpClient.SendAsync(pReq, ct).ConfigureAwait(false);
                    if (pRes.IsSuccessStatusCode)
                    {
                        string pHtml = await pRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        ParseSuicaodexChaptersFromHtml(pHtml, allChapters);
                    }
                }
                catch { }
            }

            if (allChapters.Count > 0)
            {
                item.Chapters = allChapters.Values.OrderBy(c => c.ChapterNumber).ToList();
            }
            else
            {
                item.Chapters.Add(new ChapterItem { ChapterNumber = 1, Title = "Chapter 1", Url = url, Status = "Waiting" });
            }

            item.TotalChapters = item.Chapters.Count;
            item.LatestChapter = item.Chapters.Count > 0 ? item.Chapters[^1].Title : "N/A";
            item.Status = "Ready";
            item.StatusMessage = $"Extracted {item.TotalChapters} chapters";
            return item;
        }
        catch (Exception ex)
        {
            item.Status = "Error";
            item.StatusMessage = ex.Message;
            return item;
        }
    }

    private void ParseSuicaodexChaptersFromHtml(string html, Dictionary<string, ChapterItem> chapters)
    {
        if (string.IsNullOrWhiteSpace(html)) return;

        // Pattern 1: aria-label="Đọc Ch. 221 - ..." href="/chapter/..."
        var matches = Regex.Matches(html, @"<a[^>]+aria-label=""(?:Đọc\s+)?(?<title>[^""]+)""[^>]+href=""(?<url>/chapter/[0-9a-f\-]+)""", RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            string chapRelUrl = m.Groups["url"].Value.Trim();
            string title = WebUtility.HtmlDecode(m.Groups["title"].Value).Trim();
            string fullUrl = "https://suicaodex.com" + chapRelUrl;

            if (!chapters.ContainsKey(fullUrl))
            {
                double chapNum = ExtractChapterNumber(title, chapters.Count + 1);
                chapters[fullUrl] = new ChapterItem
                {
                    ChapterNumber = chapNum,
                    Title = title,
                    Url = fullUrl,
                    Status = "Waiting"
                };
            }
        }

        // Pattern 2: href="/chapter/..." ... <span class="...break-all...">title</span>
        if (matches.Count == 0)
        {
            var matchesSpan = Regex.Matches(html, @"href=""(?<url>/chapter/[0-9a-f\-]+)""[^>]*>.*?<span[^>]*class=""[^""]*break-all[^""]*""[^>]*>(?<title>[^<]+)</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match m in matchesSpan)
            {
                string chapRelUrl = m.Groups["url"].Value.Trim();
                string title = WebUtility.HtmlDecode(Regex.Replace(m.Groups["title"].Value, @"<[^>]+>", " ")).Trim();
                string fullUrl = "https://suicaodex.com" + chapRelUrl;

                if (!chapters.ContainsKey(fullUrl))
                {
                    double chapNum = ExtractChapterNumber(title, chapters.Count + 1);
                    chapters[fullUrl] = new ChapterItem
                    {
                        ChapterNumber = chapNum,
                        Title = title,
                        Url = fullUrl,
                        Status = "Waiting"
                    };
                }
            }
        }

        // Fallback: mọi thẻ a có href /chapter/
        if (chapters.Count == 0)
        {
            var fallbackMatches = Regex.Matches(html, @"href=""(?<url>/chapter/[0-9a-f\-]+)""", RegexOptions.IgnoreCase);
            foreach (Match m in fallbackMatches)
            {
                string chapRelUrl = m.Groups["url"].Value.Trim();
                string fullUrl = "https://suicaodex.com" + chapRelUrl;
                if (!chapters.ContainsKey(fullUrl))
                {
                    int seq = chapters.Count + 1;
                    chapters[fullUrl] = new ChapterItem
                    {
                        ChapterNumber = seq,
                        Title = $"Chapter {seq}",
                        Url = fullUrl,
                        Status = "Waiting"
                    };
                }
            }
        }
    }

    private async Task<List<string>> ExtractSuicaodexChapterImageUrlsAsync(string chapterUrl, CancellationToken ct)
    {
        var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string cleanUrl = chapterUrl;
        int hashIdx = cleanUrl.IndexOf('#');
        if (hashIdx >= 0) cleanUrl = cleanUrl.Substring(0, hashIdx);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, cleanUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Referer", "https://suicaodex.com/");
            req.Headers.TryAddWithoutValidation("Cookie", "reader_mode=classic; ui_mode=classic");
            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return new List<string>();

            string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var matches = Regex.Matches(html, @"https?://i\.suicaodex\.com/data/[0-9a-f\-]+/(?<file>\d+[a-zA-Z0-9_\-]*\.(?:webp|gif|jpg|jpeg|png|bmp))(?:\?[^""'\s\\]*)?", RegexOptions.IgnoreCase);
            foreach (Match m in matches)
            {
                string rawUrl = m.Value.Trim();
                int qIdx = rawUrl.IndexOf('?');
                string finalUrl = qIdx >= 0 ? rawUrl.Substring(0, qIdx) : rawUrl;
                images.Add(finalUrl);
            }

            return images.OrderBy(u => Path.GetFileName(u), StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    #endregion

    #region Domain Specific Chapter Image Extractors

    private List<string> ExtractViHentaiChapterImages(string html)
    {
        var imageUrls = new List<string>();
        var matchEval = Regex.Match(html, @"eval\s*\(\s*function\s*\(\s*h\s*,\s*u\s*,\s*n\s*,\s*t\s*,\s*e\s*,\s*r\s*\)", RegexOptions.IgnoreCase);
        if (!matchEval.Success) return imageUrls;

        string sub = html.Substring(matchEval.Index);
        var matchParams = Regex.Match(sub, @"}\s*\(\s*['""](?<h>[^'""]+)['""]\s*,\s*(?<u>\d+)\s*,\s*['""](?<n>[^'""]+)['""]\s*,\s*(?<t>\d+)\s*,\s*(?<e>\d+)\s*,\s*(?<r>\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!matchParams.Success) return imageUrls;

        string h = matchParams.Groups["h"].Value;
        int u = int.Parse(matchParams.Groups["u"].Value);
        string n = matchParams.Groups["n"].Value;
        int t = int.Parse(matchParams.Groups["t"].Value);
        int e = int.Parse(matchParams.Groups["e"].Value);
        int r_val = int.Parse(matchParams.Groups["r"].Value);

        string decoded = DecodeViHentaiPayload(h, u, n, t, e, r_val);
        var imgMatches = Regex.Matches(decoded, @"""(?<imgUrl>https?:[^""]+)""", RegexOptions.IgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in imgMatches)
        {
            string url = m.Groups["imgUrl"].Value.Replace(@"\/", "/").Replace(@"\", "");
            if (seen.Add(url))
            {
                imageUrls.Add(url);
            }
        }

        return imageUrls;
    }

    public static string DecodeViHentaiPayload(string h, int u, string n, int t, int e, int r_val)
    {
        char separator = n[e];
        var r = new System.Text.StringBuilder();

        int i = 0;
        while (i < h.Length)
        {
            var s = new System.Text.StringBuilder();
            while (i < h.Length && h[i] != separator)
            {
                s.Append(h[i]);
                i++;
            }
            i++;

            if (s.Length > 0)
            {
                string sStr = s.ToString();
                for (int j = 0; j < n.Length; j++)
                {
                    sStr = sStr.Replace(n[j].ToString(), j.ToString());
                }

                long val = ConvertBase(sStr, e, 10);
                long charCode = val - t;
                r.Append((char)charCode);
            }
        }

        string decodedStr = r.ToString();
        byte[] bytes = new byte[decodedStr.Length];
        for (int k = 0; k < decodedStr.Length; k++)
        {
            bytes[k] = (byte)decodedStr[k];
        }
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static long ConvertBase(string d, int e, int f)
    {
        string chars = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ+/";
        string h = chars.Substring(0, e);

        char[] dArr = d.ToCharArray();
        Array.Reverse(dArr);

        long j = 0;
        for (int c = 0; c < dArr.Length; c++)
        {
            char b = dArr[c];
            int index = h.IndexOf(b);
            if (index != -1)
            {
                j += index * (long)Math.Pow(e, c);
            }
        }

        return j;
    }

    private async Task<List<string>> ExtractDaomeodenChapterImagesAsync(string html, string chapterUrl, CancellationToken ct)
    {
        var imageUrls = new List<string>();
        string chapterId = string.Empty;
        string ajaxToken = string.Empty;

        var chapterIdMatch = Regex.Match(html, @"\bchapterId\s*=\s*'(?<id>\d+)'", RegexOptions.IgnoreCase);
        if (chapterIdMatch.Success) chapterId = chapterIdMatch.Groups["id"].Value;

        var tokenMatch = Regex.Match(html, @"\b_token\s*=\s*'(?<token>[^']+)'", RegexOptions.IgnoreCase);
        if (tokenMatch.Success) ajaxToken = tokenMatch.Groups["token"].Value;

        if (!string.IsNullOrWhiteSpace(chapterId) && !string.IsNullOrWhiteSpace(ajaxToken))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://daomeoden.net/apps/controllers/book/bookChapterContent.php");
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    { "token", ajaxToken },
                    { "chapterId", chapterId },
                    { "cookies", "W10=" }
                });
                request.Headers.Referrer = new Uri(chapterUrl);

                using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var dataMatch = Regex.Match(json, @"""data"":""(?<html>(?:\\.|[^""])*)""", RegexOptions.Singleline);
                    if (dataMatch.Success)
                    {
                        string chapterHtml = Regex.Unescape(dataMatch.Groups["html"].Value).Replace("\\/", "/");
                        var matches = Regex.Matches(chapterHtml, @"<(?:img|source)[^>]+(?:data-src|src)=""(?<url>[^""]+)""", RegexOptions.IgnoreCase);
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (Match match in matches)
                        {
                            string img = match.Groups["url"].Value.Trim();
                            if (string.IsNullOrWhiteSpace(img) || img.StartsWith("data:") || img.IndexOf("imggo.net", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            if (img.StartsWith("//")) img = "https:" + img;
                            if (seen.Add(img)) imageUrls.Add(img);
                        }
                    }
                }
            }
            catch {}
        }

        return imageUrls;
    }

    private const string DamconuongRootKeyBase64Url = "YVdGuT8RjDWkeQjt7s7mv53smMpLrcKBuGMs8erg8Bs";

    private static byte[] Base64UrlDecode(string input)
    {
        string padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool IsDamconuongAdUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        string lower = url.ToLowerInvariant();
        if (lower.EndsWith(".gif") || lower.Contains(".gif?") || lower.Contains("/ads/") || lower.Contains("/banners/"))
            return true;
        if (lower.Contains("9922") || lower.Contains("au88") || lower.Contains("vsbet") || lower.Contains("boyka") || lower.Contains("dcn-gold"))
            return true;
        if (lower.Contains("quang-cao") || lower.Contains("quangcao") || lower.Contains("nhacai") || lower.Contains("bet"))
            return true;
        return false;
    }

    private async Task<List<string>> ExtractDamconuongChapterImagesAsync(string html, string chapterUrl, CancellationToken ct)
    {
        var imageUrls = new List<string>();
        if (string.IsNullOrWhiteSpace(html)) return imageUrls;

        // 1. Kiểm tra cơ chế mã hóa động mới (data-cipher-path)
        var cipherMatch = Regex.Match(html, @"data-cipher-path\s*=\s*[""'](?<path>[^""']+)[""']", RegexOptions.IgnoreCase);
        if (cipherMatch.Success)
        {
            string cipherPath = cipherMatch.Groups["path"].Value.Trim();
            if (!string.IsNullOrEmpty(cipherPath))
            {
                var decryptedImages = await DecryptDamconuongCipherPagesAsync(cipherPath, chapterUrl, ct).ConfigureAwait(false);
                if (decryptedImages.Count > 0)
                {
                    return decryptedImages;
                }
            }
        }

        // 2. Fallback cho HTML tĩnh cũ (nếu có)
        string contentHtml = string.Empty;
        var contentMatch = Regex.Match(html, @"<(?:div|article|section)[^>]*(?:id=[""']chapter-content[""']|class=[""'][^""']*(?:reading-detail|box_doc|chapter-content)[^""']*[""'])[^>]*>.*?</(?:div|article|section)>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (contentMatch.Success) contentHtml = contentMatch.Value;
        else return imageUrls;

        var matches = Regex.Matches(contentHtml, @"<img[^>]+(?:data-src|data-original|data-lazy-src|src)\s*=\s*[""'](?<url>[^""']+)[""']", RegexOptions.IgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in matches)
        {
            string url = WebUtility.HtmlDecode(m.Groups["url"].Value.Trim()).Replace("\\/", "/");
            if (string.IsNullOrWhiteSpace(url) || url.StartsWith("data:") || IsIgnoredImage(url) || IsDamconuongAdUrl(url)) continue;
            string fullUrl = MakeAbsoluteUrl(url, chapterUrl);
            if (seen.Add(fullUrl)) imageUrls.Add(fullUrl);
        }

        return imageUrls;
    }

    private async Task<List<string>> DecryptDamconuongCipherPagesAsync(string cipherPath, string chapterUrl, CancellationToken ct)
    {
        var result = new List<string>();
        try
        {
            byte[] rootKey = Base64UrlDecode(DamconuongRootKeyBase64Url);

            using var hmacRoot = new HMACSHA256(rootKey);
            byte[] key1 = hmacRoot.ComputeHash(Encoding.UTF8.GetBytes("tok"));
            byte[] key2 = hmacRoot.ComputeHash(Encoding.UTF8.GetBytes("enc"));

            using var hmacKey1 = new HMACSHA256(key1);
            byte[] cipherPathBytes = Encoding.UTF8.GetBytes(cipherPath);
            byte[] pathHash = hmacKey1.ComputeHash(cipherPathBytes);

            byte[] tokenBytes = new byte[17];
            tokenBytes[0] = 0x01;
            Buffer.BlockCopy(pathHash, 0, tokenBytes, 1, 16);
            string queryParam = Base64UrlEncode(tokenBytes);

            var baseUri = new Uri(chapterUrl);
            string[] parts = cipherPath.Split('/', 2);
            string mangaSlug = Uri.EscapeDataString(parts[0]);
            string chapterSlug = parts.Length > 1 ? Uri.EscapeDataString(parts[1]) : string.Empty;
            string apiUrl = $"{baseUri.Scheme}://{baseUri.Authority}/_c/mangas/{mangaSlug}/chapters/{chapterSlug}/pages?_={Uri.EscapeDataString(queryParam)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            req.Headers.Add("Accept", "application/json, text/plain, */*");
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            req.Headers.Referrer = baseUri;

            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return result;

            string respJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(respJson);
            if (!doc.RootElement.TryGetProperty("e", out var eProp)) return result;

            string? encryptedBase64Url = eProp.GetString();
            if (string.IsNullOrEmpty(encryptedBase64Url)) return result;

            using var hmacKey2 = new HMACSHA256(key2);
            byte[] aesKey = hmacKey2.ComputeHash(Encoding.UTF8.GetBytes(queryParam));

            byte[] cipherBytes = Base64UrlDecode(encryptedBase64Url);
            if (cipherBytes.Length < 28) return result; // 12 (iv) + 16 (tag)

            byte[] iv = cipherBytes[..12];
            byte[] tag = cipherBytes[^16..];
            byte[] ciphertext = cipherBytes[12..^16];
            byte[] plaintext = new byte[ciphertext.Length];

            using var aesGcm = new AesGcm(aesKey, 16);
            aesGcm.Decrypt(iv, ciphertext, tag, plaintext, cipherPathBytes);

            string decryptedJson = Encoding.UTF8.GetString(plaintext);
            using var pagesDoc = JsonDocument.Parse(decryptedJson);
            if (pagesDoc.RootElement.TryGetProperty("p", out var pArray) && pArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in pArray.EnumerateArray())
                {
                    string? pageUrl = item.GetString();
                    if (!string.IsNullOrWhiteSpace(pageUrl) && !IsDamconuongAdUrl(pageUrl))
                    {
                        result.Add(MakeAbsoluteUrl(pageUrl, chapterUrl));
                    }
                }
            }
        }
        catch
        {
            // Decrypt failure fallback
        }

        return result;
    }

    private List<string> ExtractSayHentaiChapterImages(string html, string chapterUrl)
    {
        // 1. Khoanh vùng khu vực đọc để loại bỏ các thẻ preload ở <head> không có token
        int startIndex = html.IndexOf("class=\"reading-content\"", StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0) startIndex = html.IndexOf("id=\"chapter_content\"", StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0) startIndex = html.IndexOf("class=\"entry-content\"", StringComparison.OrdinalIgnoreCase);
        string contentArea = startIndex >= 0 ? html.Substring(startIndex) : html;

        int stopIndex = contentArea.IndexOf("class=\"comment-box\"", StringComparison.OrdinalIgnoreCase);
        if (stopIndex < 0) stopIndex = contentArea.IndexOf("class=\"entry-header footer\"", StringComparison.OrdinalIgnoreCase);
        if (stopIndex > 0) contentArea = contentArea.Substring(0, stopIndex);

        var bestByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var orderedKeys = new List<string>();

        void AddCandidate(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return;
            string decoded = WebUtility.HtmlDecode(rawUrl).Trim();
            if (decoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                decoded.Contains("logo", StringComparison.OrdinalIgnoreCase) ||
                decoded.Contains("banner", StringComparison.OrdinalIgnoreCase)) return;

            string fullUrl = MakeAbsoluteUrl(decoded, chapterUrl);
            string key = fullUrl.Split('?')[0];

            if (!bestByPath.TryGetValue(key, out string? currentBest))
            {
                bestByPath[key] = fullUrl;
                orderedKeys.Add(key);
                return;
            }

            bool currentHasToken = currentBest.IndexOf("token=", StringComparison.OrdinalIgnoreCase) >= 0;
            bool candidateHasToken = fullUrl.IndexOf("token=", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!currentHasToken && candidateHasToken)
            {
                bestByPath[key] = fullUrl;
            }
        }

        // Ưu tiên cdn.pubtranxzyzz.store
        foreach (Match match in Regex.Matches(contentArea, @"https?://cdn\.pubtranxzyzz\.store/hen/\d+/[^/""'\s>]+/[^""'\s>]+\.(?:jpg|jpeg|png|webp|gif)(?:\?[^""'\s>]*)?", RegexOptions.IgnoreCase))
        {
            AddCandidate(match.Value);
        }

        // Kế đến là truyenvua.com
        foreach (Match match in Regex.Matches(contentArea, @"https?://[^""'\s>]+?\.truyenvua\.com/[^""'\s>]+", RegexOptions.IgnoreCase))
        {
            AddCandidate(match.Value);
        }

        // Fallback quét các thẻ img/source trong contentArea
        if (bestByPath.Count == 0)
        {
            foreach (Match match in Regex.Matches(contentArea, @"<(?:img|source)[^>]+(?:data-src|data-original|src)=[""'](?<url>[^""']+)[""']", RegexOptions.IgnoreCase))
            {
                AddCandidate(match.Groups["url"].Value);
            }
        }

        return orderedKeys.Select(k => bestByPath[k]).ToList();
    }

    private List<string> ExtractDilibChapterImages(string html, string chapterUrl)
    {
        var imageUrls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var matches = Regex.Matches(
            html,
            @"(?:src|data-src|data-original|data-lazy-src|data-url)\s*=\s*[""'](?<url>(?:https?://(?:www\.)?dilib\.vn)?/[^""'?#>]+/img[^""'?#>]+\.(?:webp|gif|jpg|jpeg|png|bmp)(?:\?[^""'<>]*)?)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match m in matches)
        {
            string url = MakeAbsoluteUrl(m.Groups["url"].Value.Trim(), chapterUrl);
            if (seen.Add(url)) imageUrls.Add(url);
        }

        if (imageUrls.Count == 0)
        {
            var fallback = Regex.Matches(html, @"<div[^>]*class=[""'][^""']*page-chapter[^""']*[""'][^>]*>.*?<img[^>]+(?:data-src|src)=[""'](?<url>[^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match m in fallback)
            {
                string url = MakeAbsoluteUrl(m.Groups["url"].Value.Trim(), chapterUrl);
                if (seen.Add(url)) imageUrls.Add(url);
            }
        }

        return imageUrls;
    }

    private List<string> ExtractHentai2readDirectImageUrls(string html)
    {
        var imageUrls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string content = (html ?? string.Empty).Replace("\\/", "/");
        foreach (Match match in Regex.Matches(
            content,
            @"(?<url>https?://static\.(?:hentaicdn\.com|hentai\.direct)/hentai/\d+/[^""'\s<>]+?\.(?:jpg|jpeg|png|webp|bmp|gif)(?:\?[^""'\s<>]*)?)",
            RegexOptions.IgnoreCase))
        {
            string url = WebUtility.HtmlDecode(match.Groups["url"].Value.Trim());
            if (seen.Add(url)) imageUrls.Add(url);
        }

        foreach (Match match in Regex.Matches(
            content,
            @"['""]images['""]\s*:\s*\[(?<images>.*?)\]",
            RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string imagesBlock = match.Groups["images"].Value;
            foreach (Match imageMatch in Regex.Matches(
                imagesBlock,
                @"[""'](?<path>/\d+/[^""']+?\.(?:jpg|jpeg|png|webp|bmp|gif))[""']",
                RegexOptions.IgnoreCase))
            {
                string relativePath = WebUtility.HtmlDecode(imageMatch.Groups["path"].Value.Trim()).Replace("\\/", "/");
                string url = "https://static.hentaicdn.com/hentai" + relativePath;
                if (seen.Add(url)) imageUrls.Add(url);
            }
        }

        return imageUrls;
    }

    private List<string> ExtractNettruyenChapterImages(string html, string chapterUrl)
    {
        var imageUrls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var matches = Regex.Matches(html, @"<div[^>]*class=[""'][^""']*(?:page-chapter|reading-detail)[^""']*[""'][^>]*>.*?<img[^>]+(?:data-original|data-src|data-cdn|data-sv1|data-sv2|src)=[""'](?<url>[^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match m in matches)
        {
            string url = m.Groups["url"].Value.Trim();
            if (url.StartsWith("data:") || 
                url.Contains("banner", StringComparison.OrdinalIgnoreCase) || 
                url.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("logo", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("nettruyenviet.webp", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("assets/images", StringComparison.OrdinalIgnoreCase)) 
            {
                continue;
            }
            string fullUrl = MakeAbsoluteUrl(url, chapterUrl);
            if (seen.Add(fullUrl)) imageUrls.Add(fullUrl);
        }

        return imageUrls;
    }

    private List<string> ExtractLoppyChapterImages(string html, string chapterUrl)
    {
        var imageUrls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var matches = Regex.Matches(html, @"<img[^>]+(?:data-src|src)\s*=\s*[""'](?<url>[^""']+)[""'][^>]*>", RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            string url = WebUtility.HtmlDecode(m.Groups["url"].Value.Trim()).Replace("\\/", "/");
            if (string.IsNullOrWhiteSpace(url) || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            if (url.Contains("credit", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("logo", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("banner", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string fullUrl = MakeAbsoluteUrl(url, chapterUrl);
            if (Uri.TryCreate(fullUrl, UriKind.Absolute, out Uri? imgUri))
            {
                string ext = Path.GetExtension(imgUri.AbsolutePath).ToLowerInvariant();
                switch (ext)
                {
                    case ".webp":
                    case ".gif":
                    case ".jpg":
                    case ".jpeg":
                    case ".png":
                    case ".bmp":
                        break;
                    default:
                        continue;
                }
            }

            if (seen.Add(fullUrl))
            {
                imageUrls.Add(fullUrl);
            }
        }

        return imageUrls;
    }

    private static string StripCommentElements(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // BẮT BUỘC: Đừng bao giờ tải trong element:
        // <div class="comment-container box" id="comment_list">
        var commentMatch = Regex.Match(
            html,
            @"<div[^>]+(?:id=[""']comment_list[""']|class=[""'][^""']*comment-container[^""']*[""'])[^>]*>",
            RegexOptions.IgnoreCase);

        if (commentMatch.Success)
        {
            return html.Substring(0, commentMatch.Index);
        }

        return html;
    }

    private List<string> ExtractTruyenqqChapterImages(string html, string chapterUrl)
    {
        var images = new List<string>();
        if (string.IsNullOrWhiteSpace(html)) return images;

        // 1. Loại bỏ triệt để element comment_list để không bao giờ bóc tách ảnh emo/meme trong bình luận
        string cleanHtml = StripCommentElements(html);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 2. TruyenQQ phân trang truyện bằng cấu trúc: <div id="page_X" class="page-chapter"><img ... /></div>
        var pageBlocks = Regex.Matches(
            cleanHtml,
            @"<div[^>]+id=[""']page_\d+[""'][^>]*class=[""'][^""']*page-chapter[^""']*[""'][^>]*>(?<block>.*?)</div>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match pb in pageBlocks)
        {
            string blockContent = pb.Groups["block"].Value;
            var imgMatch = Regex.Match(blockContent, @"<img\s+[^>]*>", RegexOptions.IgnoreCase);
            if (!imgMatch.Success) continue;

            string tag = imgMatch.Value;
            string? imgUrl = ExtractImageUrlFromTag(tag);
            if (string.IsNullOrWhiteSpace(imgUrl) || IsIgnoredImage(imgUrl)) continue;

            string fullUrl = MakeAbsoluteUrl(imgUrl.Trim(), chapterUrl);
            if (seen.Add(fullUrl))
            {
                images.Add(fullUrl);
            }
        }

        // 3. Fallback an toàn nếu TruyenQQ đổi cấu trúc page_X nhưng vẫn nằm trong cleanHtml
        if (images.Count == 0)
        {
            var fallbackImgs = Regex.Matches(cleanHtml, @"<(?:img|source)\s+[^>]*class=[""'][^""']*(?:lazy|page-chapter)[^""']*[""'][^>]*>", RegexOptions.IgnoreCase);
            foreach (Match m in fallbackImgs)
            {
                string tag = m.Value;
                string? imgUrl = ExtractImageUrlFromTag(tag);
                if (string.IsNullOrWhiteSpace(imgUrl) || IsIgnoredImage(imgUrl)) continue;

                string fullUrl = MakeAbsoluteUrl(imgUrl.Trim(), chapterUrl);
                if (seen.Add(fullUrl))
                {
                    images.Add(fullUrl);
                }
            }
        }

        return images;
    }

    private static string? ExtractImageUrlFromTag(string tag)
    {
        var dataOriginal = Regex.Match(tag, @"data-original=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (dataOriginal.Success) return dataOriginal.Groups[1].Value;

        var dataCdn = Regex.Match(tag, @"data-cdn=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (dataCdn.Success) return dataCdn.Groups[1].Value;

        var dataLazy = Regex.Match(tag, @"data-lazy-src=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (dataLazy.Success) return dataLazy.Groups[1].Value;

        var dataSrc = Regex.Match(tag, @"data-src=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (dataSrc.Success) return dataSrc.Groups[1].Value;

        var src = Regex.Match(tag, @"src=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (src.Success) return src.Groups[1].Value;

        return null;
    }

    private static bool IsIgnoredImage(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        url = url.Trim();
        return url.StartsWith("data:") ||
               url.Contains("logo") ||
               url.Contains("banner") ||
               url.Contains("icon") ||
               url.Contains("avatar") ||
               url.Contains("avatars-cdn") ||
               url.Contains("/avatars/") ||
               url.Contains("loading") ||
               url.Contains("no_image") ||
               url.Contains("facebook.com") ||
               url.Contains("fbcdn") ||
               url.Contains("gstatic.com") ||
               url.Contains("google") ||
               url.Contains("blogspot.com") ||
               url.Contains("emo") ||
               url.Contains("sticker");
    }

    #endregion

    private async Task<List<string>> ExtractMangaDexChapterImagesAsync(string chapterUrl, CancellationToken ct)
    {
        var list = new List<string>();
        var match = Regex.Match(chapterUrl, @"chapter/([a-f0-9\-]+)", RegexOptions.IgnoreCase);
        if (!match.Success) return list;

        string chapterId = match.Groups[1].Value;

        // Tầng 1: Thử lấy từ at-home server tiêu chuẩn và forcePort443
        string[] atHomeUrls =
        [
            $"https://api.mangadex.org/at-home/server/{chapterId}",
            $"https://api.mangadex.org/at-home/server/{chapterId}?forcePort443=true"
        ];

        string hash = string.Empty;
        var dataFiles = new List<string>();
        var dataSaverFiles = new List<string>();
        string baseUrl = string.Empty;

        foreach (string apiUrl in atHomeUrls)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                string json = await MangaDexNetworkService.Instance.GetJsonAsync(apiUrl, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                baseUrl = root.TryGetProperty("baseUrl", out var bu) ? (bu.GetString() ?? string.Empty) : string.Empty;
                if (root.TryGetProperty("chapter", out var chapterObj))
                {
                    hash = chapterObj.TryGetProperty("hash", out var hp) ? (hp.GetString() ?? string.Empty) : string.Empty;
                    if (chapterObj.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var fn in dataArr.EnumerateArray())
                        {
                            string filename = fn.GetString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(filename)) dataFiles.Add(filename);
                        }
                    }

                    if (chapterObj.TryGetProperty("dataSaver", out var dsArr) && dsArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var fn in dsArr.EnumerateArray())
                        {
                            string filename = fn.GetString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(filename)) dataSaverFiles.Add(filename);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(hash) && dataFiles.Count > 0)
                {
                    break;
                }
            }
            catch { }
        }

        // Tầng 2: Fallback trực tiếp qua MangaDex Chapter Metadata API nếu at-home server lỗi
        if (dataFiles.Count == 0 || string.IsNullOrEmpty(hash))
        {
            try
            {
                string metaApiUrl = $"https://api.mangadex.org/chapter/{chapterId}";
                string metaJson = await MangaDexNetworkService.Instance.GetJsonAsync(metaApiUrl, ct).ConfigureAwait(false);
                using var metaDoc = JsonDocument.Parse(metaJson);
                if (metaDoc.RootElement.TryGetProperty("data", out var mData) &&
                    mData.TryGetProperty("attributes", out var mAttr))
                {
                    hash = mAttr.TryGetProperty("hash", out var hp) ? (hp.GetString() ?? string.Empty) : string.Empty;
                    if (mAttr.TryGetProperty("data", out var dArr) && dArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var fn in dArr.EnumerateArray())
                        {
                            string filename = fn.GetString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(filename)) dataFiles.Add(filename);
                        }
                    }

                    if (mAttr.TryGetProperty("dataSaver", out var dsArr) && dsArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var fn in dsArr.EnumerateArray())
                        {
                            string filename = fn.GetString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(filename)) dataSaverFiles.Add(filename);
                        }
                    }
                }
            }
            catch { }
        }

        // Tầng 3: Xây dựng danh sách URL ảnh (Ưu tiên baseUrl node nếu có, hoặc fallback về Official Uploads CDN)
        if (!string.IsNullOrEmpty(hash))
        {
            string hostPrefix = !string.IsNullOrWhiteSpace(baseUrl) ? baseUrl : "https://uploads.mangadex.org";
            var targetFiles = dataFiles.Count > 0 ? dataFiles : dataSaverFiles;
            string subDir = dataFiles.Count > 0 ? "data" : "data-saver";

            foreach (string filename in targetFiles)
            {
                list.Add($"{hostPrefix}/{subDir}/{hash}/{filename}");
            }
        }

        return list;
    }

    private async Task<ComicBookItem> ScrapeMangaDexBookAsync(string url, int index, string lang, bool useFallback, CancellationToken ct)
    {
        var match = Regex.Match(url, @"title/([a-f0-9\-]+)", RegexOptions.IgnoreCase);
        string mangaId = match.Success ? match.Groups[1].Value : string.Empty;

        var book = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = "mangadex.org",
            Title = "MangaDex Comic",
            PreferredLanguage = lang,
            Status = "Extracting..."
        };

        if (string.IsNullOrEmpty(mangaId))
        {
            book.Title = ExtractFallbackTitleFromUrl(url);
            book.Status = "Error";
            book.StatusMessage = "Invalid MangaDex ID";
            return book;
        }

        string originalLanguage = "zh";

        try
        {
            string apiUrl = $"https://api.mangadex.org/manga/{mangaId}?includes[]=cover_art";
            string json = await MangaDexNetworkService.Instance.GetJsonAsync(apiUrl, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");

            if (data.TryGetProperty("attributes", out var attr))
            {
                if (attr.TryGetProperty("originalLanguage", out var origProp))
                {
                    originalLanguage = origProp.GetString() ?? "zh";
                }
                if (attr.TryGetProperty("title", out var titleObj))
                {
                    foreach (var prop in titleObj.EnumerateObject())
                    {
                        book.Title = prop.Value.GetString() ?? book.Title;
                        break;
                    }
                }
            }

            if (data.TryGetProperty("relationships", out var relArr))
            {
                foreach (var rel in relArr.EnumerateArray())
                {
                    if (rel.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "cover_art")
                    {
                        if (rel.TryGetProperty("attributes", out var cAttr) && cAttr.TryGetProperty("fileName", out var fnProp))
                        {
                            string coverFile = fnProp.GetString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(coverFile))
                            {
                                book.CoverUrl = $"https://uploads.mangadex.org/covers/{mangaId}/{coverFile}.256.jpg";
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            book.Title = ExtractFallbackTitleFromUrl(url);
        }

        // Bóc tách chapter: Tầng 1 Siêu tốc qua Aggregate API, Tầng 2 Fallback qua Feed API
        string primaryLang = string.IsNullOrWhiteSpace(lang) ? "vi" : lang.ToLowerInvariant();
        book.StatusMessage = $"Đang quét chapters siêu tốc [{primaryLang.ToUpperInvariant()}]...";

        // TẦNG 1: Aggregate API (lấy 100% distinct chapters trong 1-2 giây và tự động lấp đầy ngắt quãng)
        try
        {
            var primaryMap = await FetchMangaDexAggregateChaptersAsync(mangaId, primaryLang, ct).ConfigureAwait(false);

            Dictionary<string, (string ChapterId, string Vol, double SortNum)>? enMap = null;
            if (useFallback && !primaryLang.Equals("en", StringComparison.OrdinalIgnoreCase))
            {
                enMap = await FetchMangaDexAggregateChaptersAsync(mangaId, "en", ct).ConfigureAwait(false);
            }

            // Nếu cả primary và English đều thiếu chapter hoặc rỗng: Quét Global / Original Language
            Dictionary<string, (string ChapterId, string Vol, double SortNum)>? globalMap = null;
            if (primaryMap.Count == 0 && (enMap == null || enMap.Count == 0))
            {
                globalMap = await FetchMangaDexAggregateChaptersAsync(mangaId, null, ct).ConfigureAwait(false);
            }

            // HỢP NHẤT LIỀN MẠCH (Seamless Fusion): Bù đắp khuyết thiếu, không đứt đoạn
            var merged = new Dictionary<string, (string ChapterId, string Vol, double SortNum, string LangLabel)>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in primaryMap)
            {
                merged[kvp.Key] = (kvp.Value.ChapterId, kvp.Value.Vol, kvp.Value.SortNum, primaryLang.ToUpperInvariant());
            }

            if (enMap != null && enMap.Count > 0)
            {
                foreach (var kvp in enMap)
                {
                    if (!merged.ContainsKey(kvp.Key))
                    {
                        merged[kvp.Key] = (kvp.Value.ChapterId, kvp.Value.Vol, kvp.Value.SortNum, "EN");
                    }
                }
            }

            if (globalMap != null && globalMap.Count > 0)
            {
                string rawLabel = !string.IsNullOrEmpty(originalLanguage) ? originalLanguage.ToUpperInvariant() : "RAW";
                foreach (var kvp in globalMap)
                {
                    if (!merged.ContainsKey(kvp.Key))
                    {
                        merged[kvp.Key] = (kvp.Value.ChapterId, kvp.Value.Vol, kvp.Value.SortNum, rawLabel);
                    }
                }
            }

            if (merged.Count > 0)
            {
                var sortedList = merged.OrderBy(c => c.Value.SortNum).ThenBy(c => c.Key).ToList();
                int count = 1;
                foreach (var item in sortedList)
                {
                    string chapKey = item.Key;
                    var info = item.Value;
                    string chapUrl = $"https://mangadex.org/chapter/{info.ChapterId}";

                    string chapTitle = chapKey == "none" ? $"Oneshot [{info.LangLabel}]" : $"Chapter {chapKey} [{info.LangLabel}]";
                    if (!string.IsNullOrEmpty(info.Vol) && info.Vol != "none")
                    {
                        chapTitle = $"Vol. {info.Vol} " + chapTitle;
                    }

                    book.Chapters.Add(new ChapterItem
                    {
                        ChapterNumber = info.SortNum < double.MaxValue ? info.SortNum : count,
                        Title = chapTitle,
                        Url = chapUrl,
                        Status = "Waiting"
                    });
                    count++;
                }
            }
        }
        catch { }

        // TẦNG 2: Fallback Feed API phân trang (nếu Aggregate API không trả về chapter nào)
        if (book.Chapters.Count == 0)
        {
            try
            {
                await FetchMangaDexFeedChaptersAsync(book, mangaId, primaryLang, ct).ConfigureAwait(false);

                if (book.Chapters.Count == 0 && useFallback && !primaryLang.Equals("en", StringComparison.OrdinalIgnoreCase))
                {
                    await FetchMangaDexFeedChaptersAsync(book, mangaId, "en", ct).ConfigureAwait(false);
                }

                if (book.Chapters.Count == 0 && !string.IsNullOrEmpty(originalLanguage) && !originalLanguage.Equals(primaryLang, StringComparison.OrdinalIgnoreCase))
                {
                    await FetchMangaDexFeedChaptersAsync(book, mangaId, originalLanguage, ct).ConfigureAwait(false);
                }

                if (book.Chapters.Count == 0)
                {
                    await FetchMangaDexFeedChaptersAsync(book, mangaId, "", ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (book.Chapters.Count == 0)
                {
                    book.Status = "Error";
                    book.StatusMessage = ex.Message;
                    return book;
                }
            }
        }

        if (book.Chapters.Count == 0)
        {
            book.Status = "Error";
            book.StatusMessage = "Bộ truyện hiện chưa có chapter nào trên MangaDex.";
            return book;
        }

        book.TotalChapters = book.Chapters.Count;
        book.LatestChapter = book.Chapters[^1].Title;
        book.Status = "Ready";
        book.StatusMessage = $"Extracted {book.TotalChapters} chapters [{book.PreferredLanguage.ToUpperInvariant()}]";

        return book;
    }

    private async Task<Dictionary<string, (string ChapterId, string Vol, double SortNum)>> FetchMangaDexAggregateChaptersAsync(string mangaId, string? lang, CancellationToken ct)
    {
        var map = new Dictionary<string, (string ChapterId, string Vol, double SortNum)>(StringComparer.OrdinalIgnoreCase);
        string url = $"https://api.mangadex.org/manga/{mangaId}/aggregate";
        if (!string.IsNullOrWhiteSpace(lang))
        {
            url += $"?translatedLanguage%5B%5D={Uri.EscapeDataString(lang)}";
        }

        string json = await MangaDexNetworkService.Instance.GetJsonAsync(url, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("volumes", out var volumes)) return map;

        foreach (var vol in volumes.EnumerateObject())
        {
            string volName = vol.Name;
            if (!vol.Value.TryGetProperty("chapters", out var chaps)) continue;

            foreach (var chap in chaps.EnumerateObject())
            {
                string chapKey = chap.Name;
                var cObj = chap.Value;
                string cId = cObj.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(cId)) continue;

                double sortNum = double.MaxValue;
                if (double.TryParse(chapKey, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                {
                    sortNum = parsed;
                }

                if (!map.ContainsKey(chapKey))
                {
                    map[chapKey] = (cId, volName, sortNum);
                }
            }
        }

        return map;
    }

    private async Task FetchMangaDexFeedChaptersAsync(ComicBookItem book, string mangaId, string targetLang, CancellationToken ct)
    {
        int offset = 0;
        const int limit = 500;
        var rawBatches = new List<JsonElement>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string feedUrl = $"https://api.mangadex.org/manga/{mangaId}/feed" +
                             $"?offset={offset}&limit={limit}" +
                             (!string.IsNullOrWhiteSpace(targetLang) ? $"&translatedLanguage%5B%5D={Uri.EscapeDataString(targetLang)}" : "") +
                             "&includeFutureUpdates=0&includeEmptyPages=0&includeExternalUrl=0" +
                             "&order%5Bchapter%5D=asc";

            string feedJson = await MangaDexNetworkService.Instance.GetJsonAsync(feedUrl, ct).ConfigureAwait(false);
            using var feedDoc = JsonDocument.Parse(feedJson);
            if (!feedDoc.RootElement.TryGetProperty("data", out var chapArr)) break;

            int itemsInBatch = 0;
            foreach (var c in chapArr.EnumerateArray())
            {
                itemsInBatch++;
                rawBatches.Add(c.Clone());
            }

            if (itemsInBatch < limit)
            {
                break;
            }

            offset += itemsInBatch;
        }

        if (rawBatches.Count == 0) return;

        // Gom nhóm theo chapter number (như logic WPF) để tránh trùng lặp giữa các nhóm dịch
        var groupedChapters = rawBatches
            .GroupBy(c =>
            {
                var attr = c.GetProperty("attributes");
                return attr.TryGetProperty("chapter", out var cp) ? cp.GetString() ?? string.Empty : string.Empty;
            })
            .ToList();

        var selectedChapters = new List<JsonElement>();
        foreach (var group in groupedChapters)
        {
            if (string.IsNullOrWhiteSpace(group.Key))
            {
                selectedChapters.AddRange(group);
            }
            else
            {
                // Chọn bản dịch đầu tiên/mới nhất của số chapter đó
                selectedChapters.Add(group.First());
            }
        }

        int count = book.Chapters.Count + 1;
        var seenIds = new HashSet<string>(book.Chapters.Select(c => c.Url), StringComparer.OrdinalIgnoreCase);

        foreach (var c in selectedChapters)
        {
            string cId = c.GetProperty("id").GetString() ?? string.Empty;
            string chapUrl = $"https://mangadex.org/chapter/{cId}";
            if (string.IsNullOrEmpty(cId) || !seenIds.Add(chapUrl)) continue;

            var cAttr = c.GetProperty("attributes");
            string chapNum = cAttr.TryGetProperty("chapter", out var cp) ? cp.GetString()?.Trim() ?? count.ToString() : count.ToString();
            if (string.IsNullOrWhiteSpace(chapNum)) chapNum = count.ToString();

            string rawTitle = cAttr.TryGetProperty("title", out var tp) ? tp.GetString()?.Trim() ?? string.Empty : string.Empty;
            string chapTitle;

            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                chapTitle = $"Chapter {chapNum}";
            }
            else
            {
                // Kiểm tra xem rawTitle đã có sẵn số chapter ở đầu chưa (vd: "Chapter 1", "Ch. 1", "Chương 1", "Chap 1", "1 - ...")
                bool alreadyHasChapNumber = Regex.IsMatch(rawTitle, @"^(chapter|ch\.|chap|chương|\#)?\s*" + Regex.Escape(chapNum) + @"(\s*[:\-\.]|\s*$)", RegexOptions.IgnoreCase);
                if (alreadyHasChapNumber)
                {
                    chapTitle = rawTitle;
                }
                else
                {
                    chapTitle = $"Chapter {chapNum} - {rawTitle}";
                }
            }

            double.TryParse(chapNum, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedNum);

            book.Chapters.Add(new ChapterItem
            {
                ChapterNumber = parsedNum > 0 ? parsedNum : count,
                Title = chapTitle,
                Url = chapUrl,
                Status = "Waiting"
            });
            count++;
        }

        if (book.Chapters.Count > 0)
        {
            book.PreferredLanguage = targetLang;
        }
    }

    private string ExtractTitle(string html, string url)
    {
        string siteName = string.Empty;
        var siteNameMatch = Regex.Match(html, @"<meta[^>]*property=[""']og:site_name[""'][^>]*content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (siteNameMatch.Success)
        {
            siteName = WebUtility.HtmlDecode(siteNameMatch.Groups[1].Value).Trim();
        }

        var itempropMeta = Regex.Match(html, @"<meta[^>]*itemprop=[""']name[""'][^>]*content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (itempropMeta.Success && !string.IsNullOrWhiteSpace(itempropMeta.Groups[1].Value))
        {
            return CleanTitle(itempropMeta.Groups[1].Value, url, siteName);
        }

        var h1Itemprop = Regex.Match(html, @"<h1[^>]*itemprop=[""']name[""'][^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (h1Itemprop.Success && !string.IsNullOrWhiteSpace(h1Itemprop.Groups[1].Value))
        {
            string raw = Regex.Replace(h1Itemprop.Groups[1].Value, @"<[^>]+>", "").Trim();
            if (!string.IsNullOrWhiteSpace(raw)) return CleanTitle(raw, url, siteName);
        }

        var h1Match = Regex.Match(html, @"<h1[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (h1Match.Success && !string.IsNullOrWhiteSpace(h1Match.Groups[1].Value))
        {
            string raw = Regex.Replace(h1Match.Groups[1].Value, @"<[^>]+>", "").Trim();
            if (!string.IsNullOrWhiteSpace(raw)) return CleanTitle(raw, url, siteName);
        }

        var ogMatch = Regex.Match(html, @"<meta[^>]*property=[""']og:title[""'][^>]*content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (ogMatch.Success && !string.IsNullOrWhiteSpace(ogMatch.Groups[1].Value))
        {
            return CleanTitle(ogMatch.Groups[1].Value, url, siteName);
        }

        var titleMatch = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (titleMatch.Success && !string.IsNullOrWhiteSpace(titleMatch.Groups[1].Value))
        {
            return CleanTitle(titleMatch.Groups[1].Value, url, siteName);
        }

        return ExtractFallbackTitleFromUrl(url);
    }

    public string ExtractCoverUrl(string html, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // 1. Ưu tiên vùng ảnh bìa đặc thù của từng site (tránh og:image bị lỗi proxy hoặc ảnh banner chung)
        var avatarBlock = Regex.Match(html, @"<(?:div|figure)[^>]*class=[""'][^""']*\b(?:book_avatar|col-image|detail-info|book-info|cover)\b[^""']*[""'][^>]*>(?<content>[\s\S]*?)</(?:div|figure)>", RegexOptions.IgnoreCase);
        if (avatarBlock.Success)
        {
            var attrMatches = Regex.Matches(avatarBlock.Groups["content"].Value, @"(?:data-original|data-src|src|data-fb)=[""'](?<url>[^""']+)[""']", RegexOptions.IgnoreCase);
            foreach (Match m in attrMatches)
            {
                string raw = WebUtility.HtmlDecode(m.Groups["url"].Value.Trim());
                if (!string.IsNullOrWhiteSpace(raw) && !raw.Contains("no_image", StringComparison.OrdinalIgnoreCase) && !raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    return MakeAbsoluteUrl(raw, baseUrl);
                }
            }
        }

        // 2. Fallback tìm trực tiếp img trong khối book_avatar / col-image / detail-info
        var imgMatch = Regex.Match(html, @"<(?:div|figure)[^>]*(?:book_avatar|col-image|detail-info|book-info)[^>]*>[\s\S]*?<img[^>]+(?:data-original|data-src|src)=[""'](?<url>[^""']+)[""']", RegexOptions.IgnoreCase);
        if (imgMatch.Success && !string.IsNullOrWhiteSpace(imgMatch.Groups["url"].Value))
        {
            string raw = WebUtility.HtmlDecode(imgMatch.Groups["url"].Value.Trim());
            if (!raw.Contains("no_image", StringComparison.OrdinalIgnoreCase))
            {
                return MakeAbsoluteUrl(raw, baseUrl);
            }
        }

        // 3. Fallback og:image
        var ogImage = Regex.Match(html, @"<meta[^>]*property=[""']og:image[""'][^>]*content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (ogImage.Success && !string.IsNullOrWhiteSpace(ogImage.Groups[1].Value))
        {
            return MakeAbsoluteUrl(WebUtility.HtmlDecode(ogImage.Groups[1].Value.Trim()), baseUrl);
        }

        return string.Empty;
    }

    public static string ExtractPreviewTags(string html, string url)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddTag(string rawText)
        {
            string clean = WebUtility.HtmlDecode(Regex.Replace(rawText ?? string.Empty, @"<[^>]+>", " ")).Trim();
            clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', ',', '-', '|', '\u2022');
            if (!string.IsNullOrWhiteSpace(clean) && clean.Length <= 45 && seen.Add(clean))
            {
                tags.Add(clean);
            }
        }

        string lowerUrl = (url ?? string.Empty).ToLowerInvariant();

        // 1. TruyenQQ: <ul class="list01">...<a href="/the-loai/...">Tag</a>
        var list01Match = Regex.Match(html, @"<ul[^>]*class=[""'][^""']*\blist01\b[^""']*[""'][^>]*>(?<content>[\s\S]*?)</ul>", RegexOptions.IgnoreCase);
        if (list01Match.Success)
        {
            foreach (Match m in Regex.Matches(list01Match.Groups["content"].Value, @"<a\b[^>]*>(?<text>[\s\S]*?)</a>", RegexOptions.IgnoreCase))
            {
                AddTag(m.Groups["text"].Value);
            }
        }

        // 2. NetTruyen: <li class="kind...">...<a href="...">Tag</a>
        if (tags.Count == 0 && lowerUrl.Contains("nettruyen"))
        {
            var kindMatch = Regex.Match(html, @"<li[^>]*class=[""'][^""']*\bkind\b[^""']*[""'][^>]*>(?<content>[\s\S]*?)</li>", RegexOptions.IgnoreCase);
            string scope = kindMatch.Success ? kindMatch.Groups["content"].Value : html;
            foreach (Match m in Regex.Matches(scope, @"<a\b[^>]*href=[""'][^""']*?/(?:tim-truyen|the-loai)/[^""']+[""'][^>]*>(?<text>[\s\S]*?)</a>", RegexOptions.IgnoreCase))
            {
                AddTag(m.Groups["text"].Value);
            }
        }

        // 3. Thuviensach / Dilib: <div id="pdf">...<a class="button2">Tag</a>
        if (tags.Count == 0 && (lowerUrl.Contains("thuviensach") || lowerUrl.Contains("dilib")))
        {
            var pdfMatch = Regex.Match(html, @"<div[^>]*id=[""']pdf[""'][^>]*>(?<content>[\s\S]*?)</div>", RegexOptions.IgnoreCase);
            string scope = pdfMatch.Success ? pdfMatch.Groups["content"].Value : html;
            foreach (Match m in Regex.Matches(scope, @"<a\b[^>]*class=[""'][^""']*\bbutton2\b[^""']*[""'][^>]*>(?<text>[\s\S]*?)</a>", RegexOptions.IgnoreCase))
            {
                AddTag(m.Groups["text"].Value);
            }
        }

        // 4. E-Hentai / ExHentai: <div id="taglist">...<a id="ta_...">Tag</a>
        if (tags.Count == 0 && (lowerUrl.Contains("e-hentai") || lowerUrl.Contains("exhentai")))
        {
            var taglistMatch = Regex.Match(html, @"<div[^>]*id=[""']taglist[""'][^>]*>(?<content>[\s\S]*?)</div>", RegexOptions.IgnoreCase);
            string scope = taglistMatch.Success ? taglistMatch.Groups["content"].Value : html;
            foreach (Match m in Regex.Matches(scope, @"<a\b[^>]*id=[""']ta_[^""']+[""'][^>]*>(?<text>[\s\S]*?)</a>", RegexOptions.IgnoreCase))
            {
                AddTag(m.Groups["text"].Value);
                if (tags.Count >= 18) break;
            }
        }

        // 5. Hentaiforce / Vi-Hentai / Damconuong / SayHentai / Daomeoden / General fallback
        if (tags.Count == 0)
        {
            var infoBlock = Regex.Match(html, @"<(?:div|ul)[^>]*class=[""'][^""']*\b(?:list01|kind|genres-content|post-content_item|tag-links|tags|book-info|detail-info|gallery-tags)\b[^""']*[""'][^>]*>(?<content>[\s\S]*?)</(?:div|ul)>", RegexOptions.IgnoreCase);
            if (infoBlock.Success)
            {
                foreach (Match m in Regex.Matches(infoBlock.Groups["content"].Value, @"<a\b[^>]*href=[""'][^""']*?/(?:the-loai|tag|tags|genre|genres|category)/[^""']+[""'][^>]*>(?<text>[\s\S]*?)</a>", RegexOptions.IgnoreCase))
                {
                    AddTag(m.Groups["text"].Value);
                    if (tags.Count >= 15) break;
                }
            }
        }

        return tags.Count > 0 ? "Tags: " + string.Join(", ", tags) : string.Empty;
    }

    public static (string Type, string Language, string Artist, string Parody, string Tags, int TotalPages) ExtractHitomiMetadata(JsonElement root, string? fallbackUrl = null)
    {
        var textInfo = System.Globalization.CultureInfo.InvariantCulture.TextInfo;

        // 1. Type
        string type = string.Empty;
        if (root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
        {
            string rawType = typeProp.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(rawType))
            {
                type = rawType.ToLowerInvariant() switch
                {
                    "artistcg" => "Artist CG",
                    "gamecg" => "Game CG",
                    "imageset" => "Image Set",
                    "non-h" => "Non-H",
                    _ => textInfo.ToTitleCase(rawType.Trim())
                };
            }
        }
        if (string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(fallbackUrl))
        {
            var matchType = System.Text.RegularExpressions.Regex.Match(fallbackUrl, @"hitomi\.la/(doujinshi|manga|artistcg|gamecg|imageset|anime)/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (matchType.Success)
            {
                string raw = matchType.Groups[1].Value.ToLowerInvariant();
                type = raw switch
                {
                    "artistcg" => "Artist CG",
                    "gamecg" => "Game CG",
                    "imageset" => "Image Set",
                    "non-h" => "Non-H",
                    _ => textInfo.ToTitleCase(raw)
                };
            }
        }

        // 2. Language
        string language = string.Empty;
        string rawLang = string.Empty;
        if (root.TryGetProperty("language", out var langProp) && langProp.ValueKind == JsonValueKind.String)
        {
            rawLang = langProp.GetString() ?? string.Empty;
        }
        string localName = string.Empty;
        if (root.TryGetProperty("language_localname", out var localProp) && localProp.ValueKind == JsonValueKind.String)
        {
            localName = localProp.GetString() ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(rawLang))
        {
            string normLang = textInfo.ToTitleCase(rawLang.Trim());
            if (!string.IsNullOrWhiteSpace(localName) && !string.Equals(normLang, localName, StringComparison.OrdinalIgnoreCase))
            {
                language = $"{normLang} ({localName})";
            }
            else
            {
                language = normLang;
            }
        }
        else if (!string.IsNullOrWhiteSpace(localName))
        {
            language = localName;
        }

        // 3. Artist
        var artists = new List<string>();
        if (root.TryGetProperty("artists", out var artProp) && artProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in artProp.EnumerateArray())
            {
                if (a.TryGetProperty("artist", out var aProp) && aProp.ValueKind == JsonValueKind.String)
                {
                    string name = aProp.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(name)) artists.Add(textInfo.ToTitleCase(name.Trim()));
                }
            }
        }
        string artist = artists.Count > 0 ? string.Join(", ", artists) : string.Empty;

        // 4. Parody
        var parodies = new List<string>();
        if (root.TryGetProperty("parodys", out var parProp) && parProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in parProp.EnumerateArray())
            {
                if (p.TryGetProperty("parody", out var pProp) && pProp.ValueKind == JsonValueKind.String)
                {
                    string name = pProp.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(name)) parodies.Add(textInfo.ToTitleCase(name.Trim()));
                }
            }
        }
        string parody = parodies.Count > 0 ? string.Join(", ", parodies) : string.Empty;

        // 5. Tags
        string tags = ExtractHitomiTags(root);

        // 6. Total Pages (Từ mảng files hoặc reader URL hash #1033)
        int totalPages = 0;
        if (root.TryGetProperty("files", out var filesProp) && filesProp.ValueKind == JsonValueKind.Array)
        {
            totalPages = filesProp.GetArrayLength();
        }
        if (totalPages <= 0 && !string.IsNullOrWhiteSpace(fallbackUrl))
        {
            var matchReader = System.Text.RegularExpressions.Regex.Match(fallbackUrl, @"#(\d+)$");
            if (matchReader.Success && int.TryParse(matchReader.Groups[1].Value, out int pageFromHash))
            {
                totalPages = pageFromHash;
            }
        }

        return (type, language, artist, parody, tags, totalPages);
    }

    public static string ExtractHitomiTags(JsonElement root)
    {
        try
        {
            if (root.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
            {
                var tagsList = new List<string>();
                var textInfo = System.Globalization.CultureInfo.InvariantCulture.TextInfo;
                foreach (var t in tagsProp.EnumerateArray())
                {
                    if (t.TryGetProperty("tag", out var tagProp) && tagProp.ValueKind == JsonValueKind.String)
                    {
                        string tagName = tagProp.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(tagName))
                        {
                            tagName = textInfo.ToTitleCase(tagName);
                            bool isFemale = false;
                            bool isMale = false;

                            if (t.TryGetProperty("female", out var fProp))
                            {
                                if (fProp.ValueKind == JsonValueKind.Number && fProp.TryGetInt32(out int fVal) && fVal == 1) isFemale = true;
                                else if (fProp.ValueKind == JsonValueKind.String && (fProp.GetString() == "1" || string.Equals(fProp.GetString(), "true", StringComparison.OrdinalIgnoreCase))) isFemale = true;
                                else if (fProp.ValueKind == JsonValueKind.True) isFemale = true;
                            }

                            if (!isFemale && t.TryGetProperty("male", out var mProp))
                            {
                                if (mProp.ValueKind == JsonValueKind.Number && mProp.TryGetInt32(out int mVal) && mVal == 1) isMale = true;
                                else if (mProp.ValueKind == JsonValueKind.String && (mProp.GetString() == "1" || string.Equals(mProp.GetString(), "true", StringComparison.OrdinalIgnoreCase))) isMale = true;
                                else if (mProp.ValueKind == JsonValueKind.True) isMale = true;
                            }

                            if (isFemale) tagName += " ♀";
                            else if (isMale) tagName += " ♂";

                            tagsList.Add(tagName);
                        }
                    }
                }

                if (tagsList.Count > 0)
                {
                    return "Tags: " + string.Join(", ", tagsList);
                }
            }
        }
        catch { }
        return string.Empty;
    }

    private static readonly Regex NavBypassRegex = new(
        @"^(?:" +
        @"(?:đọc\s*(?:từ\s*)?)?(?:chương|chap|tập|chuong|tap)\s*(?:đầu|dau|mới\s*nhất|moi\s*nhat|cuối|cuoi)|" +
        @"(?:đọc|doc)\s*(?:từ\s*đầu|tu\s*dau|mới\s*nhất|moi\s*nhat|tiếp|tiep|ngay)|" +
        @"(?:read\s*(?:now\s*)?)?(?:first|latest|last|continue)(?:\s*(?:chapter|chap|reading))?|" +
        @"first\s*(?:chapter|chap)|" +
        @"latest\s*(?:chapter|chap)|" +
        @"theo\s*dõi|thích|like|subscribe|xem\s*thêm|mục\s*lục|danh\s*sách\s*(?:chương|chap)?" +
        @")$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsNavigationButton(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        string clean = Regex.Replace(text, @"^[\[\(>\-–—\s#]+|[\]\)>–—\s]+$", "").Trim();
        if (NavBypassRegex.IsMatch(clean) || NavBypassRegex.IsMatch(text)) return true;

        string lower = clean.ToLowerInvariant();
        return lower == "chương đầu" || lower == "chuong dau" ||
               lower == "chap đầu" || lower == "chap dau" ||
               lower == "tập đầu" || lower == "tap dau" ||
               lower == "đọc từ đầu" || lower == "doc tu dau" ||
               lower == "đọc chương đầu" || lower == "doc chuong dau" ||
               lower == "đọc chap đầu" || lower == "doc chap dau" ||
               lower == "đọc từ chap đầu" || lower == "đọc từ chương đầu" ||
               lower == "read first" || lower == "first chapter" ||
               lower == "read first chapter" || lower == "start reading" ||
               lower == "read latest" || lower == "latest chapter" ||
               lower == "read last" || lower == "last chapter" ||
               lower == "chương mới nhất" || lower == "chuong moi nhat" ||
               lower == "chap mới nhất" || lower == "chap moi nhat" ||
               lower == "đọc mới nhất" || lower == "doc moi nhat" ||
               lower == "đọc chương mới nhất" || lower == "đọc tiếp" || lower == "continue reading";
    }

    private List<ChapterItem> ExtractChapters(string html, string baseUrl, string domain)
    {
        var list = new List<ChapterItem>();
        domain = domain.ToLowerInvariant();

        string searchScope = html;
        string[] containerMarkers = new[]
        {
            "works-chapter-list",
            "listing-chapters",
            "wp-manga-chapter",
            "list_chapter",
            "list-chapter",
            "chapter-list",
            "chapters-list",
            "table-chapters",
            "box-list-chapter",
            "nt_listchapter",
            "id=\"chapters\"",
            "class=\"chapters\"",
            "id=\"chapterlist\"",
            "class=\"chapterlist\"",
            "id=\"list-chapters\"",
            "class=\"list-chapters\""
        };

        foreach (var marker in containerMarkers)
        {
            int idx = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                searchScope = html.Substring(idx);
                int commentIdx = searchScope.IndexOf("id=\"comment", StringComparison.OrdinalIgnoreCase);
                if (commentIdx < 0) commentIdx = searchScope.IndexOf("class=\"comment", StringComparison.OrdinalIgnoreCase);
                if (commentIdx > 0)
                {
                    searchScope = searchScope.Substring(0, commentIdx);
                }
                break;
            }
        }

        var matches = Regex.Matches(searchScope, @"<a[^>]*href=[""']([^""']*(?:chap|chuong|chapter)[^""']*)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var urlToChapterMap = new Dictionary<string, ChapterItem>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in matches)
        {
            string href = m.Groups[1].Value.Trim();
            string text = Regex.Replace(m.Groups[2].Value, @"<[^>]+>", "").Trim();

            if (string.IsNullOrWhiteSpace(text) || href.StartsWith("#") || href.StartsWith("javascript:") || href.Contains("void(0)"))
            {
                continue;
            }

            // Bỏ qua các nút điều hướng/hành động trang web (Chương đầu, Read First, Đọc từ đầu, Chương mới nhất...)
            if (IsNavigationButton(text))
            {
                continue;
            }

            string fullUrl = MakeAbsoluteUrl(href, baseUrl);
            double chapNum = ExtractChapterNumber(text, ExtractChapterNumber(href, 0));

            if (!urlToChapterMap.TryGetValue(fullUrl, out var existing))
            {
                var chapItem = new ChapterItem
                {
                    ChapterNumber = chapNum,
                    Title = text,
                    Url = fullUrl,
                    Status = "Waiting"
                };
                urlToChapterMap[fullUrl] = chapItem;
                list.Add(chapItem);
            }
            else
            {
                // Nếu URL đã tồn tại, kiểm tra xem tên cũ có phải là tên generic/placeholder hoặc tên mới có số chương rõ hơn không
                bool existingIsNav = IsNavigationButton(existing.Title) ||
                                     Regex.IsMatch(existing.Title, @"\b(?:chương\s*đầu|chuong\s*dau|read\s*first|first\s*chapter|đọc\s*từ\s*đầu)\b", RegexOptions.IgnoreCase);
                bool newHasNumber = Regex.IsMatch(text, @"(?:chap|chuong|chapter|\b)\s*\d+", RegexOptions.IgnoreCase);

                if (existingIsNav || (!existing.Title.Any(char.IsDigit) && newHasNumber))
                {
                    existing.Title = text;
                    existing.ChapterNumber = chapNum > 0 ? chapNum : existing.ChapterNumber;
                }
            }
        }

        // Tự động kiểm tra và chuẩn hóa nếu có chapter nào vẫn mang tên điều hướng rác
        foreach (var c in list)
        {
            if (IsNavigationButton(c.Title) || Regex.IsMatch(c.Title, @"\b(?:chương\s*đầu|chuong\s*dau|read\s*first|first\s*chapter)\b", RegexOptions.IgnoreCase))
            {
                c.Title = c.ChapterNumber > 0 ? $"Chapter {c.ChapterNumber}" : "Chapter 1";
            }
        }

        if (list.Count > 0)
        {
            list = list.OrderBy(c => c.ChapterNumber).ToList();
        }
        else
        {
            list.Add(new ChapterItem
            {
                ChapterNumber = 1,
                Title = "Chapter 1",
                Url = baseUrl,
                Status = "Waiting"
            });
        }

        return list;
    }

    private double ExtractChapterNumber(string text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;

        // Nếu là URL, trích xuất từ segment cuối cùng trước để tránh số ở slug truyện làm sai lệch
        string searchScope = text;
        if (text.Contains("/"))
        {
            try
            {
                var uri = new Uri(DomainRoutingService.NormalizeUrl(text));
                string lastSegment = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault() ?? text;
                searchScope = lastSegment;
            }
            catch
            {
                searchScope = text.Split('/').LastOrDefault() ?? text;
            }
        }

        var match = Regex.Match(searchScope, @"(?:chap(?:ter)?|chương|chuong|tập|tap|ep(?:isode)?)[\s\-_:/#]*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
        if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
        {
            return parsed;
        }

        var numMatch = Regex.Match(searchScope, @"([0-9]+(?:\.[0-9]+)?)");
        if (numMatch.Success && double.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double num))
        {
            return num;
        }

        return fallback;
    }

    private static readonly string[] KnownMangaSiteBrands = new[]
    {
        "hentaivn", "hentai vn", "hentai-vn",
        "nettruyen", "net truyen",
        "truyenqq", "truyen qq",
        "sayhentai", "say hentai",
        "vi-hentai", "viet hentai", "việt hentai",
        "suicaodex",
        "damconuong", "dâm cô nương",
        "loppytoon",
        "hentaiforce",
        "hitomi",
        "blogtruyen", "blog truyen",
        "truyenfull", "truyen full",
        "hamtruyen", "ham truyen",
        "baotangtruyen", "bảo tàng truyện",
        "congtruyen", "cổng truyện",
        "tuoitre", "tuổi trẻ",
        "hako",
        "mangadex",
        "mangakakalot",
        "manganelo",
        "lxmanga", "lxhentai", "lx manga", "lx hentai",
        "cuutruyen", "cứu truyện",
        "vcomic", "v comic"
    };

    public static string CleanTitle(string title, string? url = null, string? siteName = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        // 1. Giải mã ký tự HTML (HTML Decode)
        title = WebUtility.HtmlDecode(title).Trim();

        // 2. Dọn dẹp hậu tố rác ở cuối tên (Loop đa tầng: [tới chap], tới chap, số chương, domain web...)
        // Quy tắc: Ở cuối tên luôn luôn xoá triệt để
        bool modified = true;
        int loopGuard = 0;
        while (modified && loopGuard++ < 5)
        {
            modified = false;
            string before = title;

            // 2.1. Hậu tố dạng ngoặc vuông / ngoặc tròn chứa 'tới chap', 'tới chương', 'chap', 'chapter'...
            string s1 = Regex.Replace(title, @"\s*\[[^\]]*(?:tới|toi|đến|den|chap|chapter|chương|chuong|tập|tap)[^\]]*\]\s*$", "", RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(s1)) title = s1;

            string s2 = Regex.Replace(title, @"\s*\([^\)]*(?:tới|toi|đến|den|chap|chapter|chương|chuong|tập|tap)[^\)]*\)\s*$", "", RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(s2)) title = s2;

            // 2.2. Hậu tố không ngoặc: 'tới chap', '- tới chap 120', 'tới chap latest', 'tới chương 50', 'đến chap 100'...
            string s3 = Regex.Replace(title, @"\s*(?:[-|–—:~]\s*)?(?:tới|toi|đến|den)\s+(?:chap|chapter|chương|chuong|tập|tap)(?:\s+.*|\s*)$", "", RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(s3)) title = s3;

            // 2.3. Hậu tố chap/chương thông thường: 'chương 123', 'chap 123 mới nhất', '- chap 123'
            string s4 = Regex.Replace(title, @"\s*(?:[-|–—:~]\s*)?(?:chương|chap|chapter)\s*(?:mới\s*nhất)?(?:\s*\d+.*|\s*)$", "", RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(s4)) title = s4;

            // 2.4. Hậu tố tên web / domain
            string s5 = Regex.Replace(title, @"\s*[-|–—]\s*(?:hentaivn|hentai\s*vn|truy[eệ]nqq|nettruyen|mangadex|tuoitre|hako|e-hentai(?:\s+galleries)?|exhentai|dâm cô nương|damconuong|loppytoon|hentaiforce|sayhentai|hitomi|[a-z0-9\-]+\.(?:com|net|vn|org|cc|to|io|tv|top|site|vip|club|me|online|info|xyz|pro|app|live|fun|moe|lat|icu|link|life|art|biz|tech|us|uk|co|in|mobi|ru|jp|tw|cn|asia|space|cloud|website|press|store)).*$", "", RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(s5)) title = s5;

            // 2.5. Hậu tố khớp với siteName từ HTML
            if (!string.IsNullOrWhiteSpace(siteName))
            {
                string sSite = Regex.Replace(title, @"\s*[-|–—:~]\s*" + Regex.Escape(siteName) + @"\s*$", "", RegexOptions.IgnoreCase).Trim();
                if (!string.IsNullOrWhiteSpace(sSite)) title = sSite;
            }

            // Dọn dẹp dấu phân tách treo ở đuôi nếu có sau khi xoá hậu tố (-, :, |, ~, /, –, —, [, (, {)
            string trimmedTrailing = title.TrimEnd(' ', '\t', '-', ':', '\u2013', '\u2014', '|', '~', '/', '[', '(', '{').Trim();
            if (!string.IsNullOrWhiteSpace(trimmedTrailing)) title = trimmedTrailing;

            if (!string.Equals(before, title, StringComparison.Ordinal))
            {
                modified = true;
            }
        }

        // 3. Chuẩn bị thông tin Domain và Thương hiệu từ URL / siteName để so khớp mềm (Fuzzy / Non-strict)
        string hostAlpha = string.Empty;
        string coreBrand = string.Empty;
        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                var uri = new Uri(DomainRoutingService.NormalizeUrl(url));
                string host = uri.Host.ToLowerInvariant();
                if (host.StartsWith("www.")) host = host.Substring(4);

                string[] parts = host.Split('.');
                string rawBrand = parts.Length > 0 ? parts[0] : host;
                if (parts.Length > 2 && (parts[^1] == "vn" || parts[^1] == "com") && parts[^2].Length <= 3)
                {
                    rawBrand = parts[0];
                }
                else if (parts.Length > 2)
                {
                    rawBrand = parts[^2];
                }

                hostAlpha = Regex.Replace(host, @"[^a-z0-9]", "");
                // Tước bỏ số và các hậu tố mirror phổ biến (x1, x2, 123, tv, vip, plus, moi...)
                coreBrand = Regex.Replace(rawBrand, @"(?:x\d+|\d+|tv|vip|plus|moi|top|pro|sub|vn|co|ww|cc|zone)$", "", RegexOptions.IgnoreCase);
                if (coreBrand.Length < 3) coreBrand = rawBrand;
            }
            catch { }
        }

        bool IsMatchingWebBrand(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            string candTrimmed = candidate.Trim().Trim('[', ']', '(', ')', '{', '}').Trim();
            if (candTrimmed.Length < 3) return false;

            // 1. So khớp với siteName từ og:site_name
            if (!string.IsNullOrWhiteSpace(siteName) &&
                string.Equals(candTrimmed, siteName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 2. So khớp với danh sách các thương hiệu web truyện nổi tiếng
            foreach (var known in KnownMangaSiteBrands)
            {
                if (string.Equals(candTrimmed, known, StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(candTrimmed, @"^" + Regex.Escape(known) + @"\b", RegexOptions.IgnoreCase))
                {
                    return true;
                }
            }

            // 3. So khớp mềm không nghiêm ngặt (Fuzzy / Substring) với domain từ URL
            string candAlpha = Regex.Replace(candTrimmed.ToLowerInvariant(), @"[^a-z0-9]", "");
            if (candAlpha.Length >= 3)
            {
                if (!string.IsNullOrWhiteSpace(hostAlpha))
                {
                    if (hostAlpha.StartsWith(candAlpha) || hostAlpha.Contains(candAlpha)) return true;
                    if (candAlpha.StartsWith(hostAlpha) || candAlpha.Contains(hostAlpha)) return true;
                }

                if (!string.IsNullOrWhiteSpace(coreBrand))
                {
                    string coreAlpha = Regex.Replace(coreBrand.ToLowerInvariant(), @"[^a-z0-9]", "");
                    if (coreAlpha.Length >= 3)
                    {
                        if (coreAlpha.StartsWith(candAlpha) || candAlpha.StartsWith(coreAlpha) ||
                            coreAlpha.Contains(candAlpha) || candAlpha.Contains(coreAlpha))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        void StripWebBrandPrefix()
        {
            // Dạng A: Có dấu phân cách rõ ràng (ví dụ: 'HentaiVn - Phòng Tắm...', 'abc.com : Tên Truyện', '[HentaiVn] Tên...')
            var sepMatch = Regex.Match(title, @"^(?<prefix>[^:\-\–—|~]+)\s*[:\-\–—|~]\s*(?<rest>.+)$", RegexOptions.Singleline);
            if (sepMatch.Success)
            {
                string p = sepMatch.Groups["prefix"].Value.Trim();
                string rest = sepMatch.Groups["rest"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(rest) && (IsMatchingWebBrand(p) || Regex.IsMatch(p, @"^[a-z0-9\-]+\.(?:com|net|vn|org|cc|to|io|tv|top|site|vip|club|me|online|info|xyz|pro|app|live|fun|moe|lat|icu)\b", RegexOptions.IgnoreCase)))
                {
                    title = rest;
                    return;
                }
            }

            // Dạng B: Nằm trong ngoặc ở đầu: '[HentaiVn] Tên Truyện' hoặc '(HentaiVn) Tên Truyện'
            var bracketMatch = Regex.Match(title, @"^(?:\[|\()(?<prefix>[^\]\)]+)(?:\]|\))\s*(?<rest>.+)$", RegexOptions.Singleline);
            if (bracketMatch.Success)
            {
                string p = bracketMatch.Groups["prefix"].Value.Trim();
                string rest = bracketMatch.Groups["rest"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(rest) && (IsMatchingWebBrand(p) || Regex.IsMatch(p, @"^[a-z0-9\-]+\.(?:com|net|vn|org|cc|to|io|tv|top|site|vip|club|me|online|info|xyz|pro|app|live|fun|moe|lat|icu)\b", RegexOptions.IgnoreCase)))
                {
                    title = rest;
                    return;
                }
            }

            // Dạng C: Bóc tách domain generic ở đầu: 'abc.com Tên Truyện'
            var genericDomainMatch = Regex.Match(
                title,
                @"^(?:\[|\()?([a-z0-9\-]+\.(?:com|net|vn|org|cc|to|io|tv|top|site|vip|club|me|online|info|xyz|pro|app|live|fun|moe|lat|icu|link|life|art|biz|tech|us|uk|co|in|mobi|ru|jp|tw|cn|asia|space|cloud|website|press|store))(?:\]|\))?(?:\s*[:\-\–—|~]\s*|\s+)",
                RegexOptions.IgnoreCase);
            if (genericDomainMatch.Success)
            {
                string rem = title.Substring(genericDomainMatch.Length).Trim();
                if (!string.IsNullOrWhiteSpace(rem))
                {
                    title = rem;
                    return;
                }
            }

            // Dạng D: Từ đầu tiên là tên web (cách nhau bởi khoảng trắng, ví dụ: 'HentaiVn Tên Truyện')
            var firstWordMatch = Regex.Match(title, @"^(?<word>[^\s:\-\–—|~]+)(?:\s+)(?<rest>.+)$", RegexOptions.Singleline);
            if (firstWordMatch.Success)
            {
                string w = firstWordMatch.Groups["word"].Value.Trim();
                string rest = firstWordMatch.Groups["rest"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(rest) && IsMatchingWebBrand(w))
                {
                    title = rest;
                    return;
                }
            }
        }

        // 4. Dọn dẹp Web Brand / Domain ở đầu (Lần 1)
        StripWebBrandPrefix();

        // 5. Dọn dẹp tiền tố rác ở đầu tên: 'Xem truyện', 'Đọc truyện', 'Truyện' (chỉ bóc đúng 1 lần ngoài cùng)
        var prefixMatch = Regex.Match(
            title,
            @"^(?:" +
            @"\[\s*(?:xem\s+truy[eệ]n|đ[oọ]c\s+truy[eệ]n|doc\s+truyen|truy[eệ]n)\s*\]\s*|" +
            @"\(\s*(?:xem\s+truy[eệ]n|đ[oọ]c\s+truy[eệ]n|doc\s+truyen|truy[eệ]n)\s*\)\s*|" +
            @"(?:xem\s+truy[eệ]n|đ[oọ]c\s+truy[eệ]n|doc\s+truyen)\b(?:\s*[:\-\–—|~]\s*|\s+)|" +
            @"truy[eệ]n\b(?:\s*[:\-\–—|~]\s*|\s+)" +
            @")",
            RegexOptions.IgnoreCase);

        if (prefixMatch.Success)
        {
            string remainder = title.Substring(prefixMatch.Length).Trim();
            // Chỉ bỏ tiền tố nếu phần còn lại không rỗng
            if (!string.IsNullOrWhiteSpace(remainder))
            {
                title = remainder;
            }
        }

        // Dọn dẹp Web Brand / Domain ở đầu (Lần 2, đề phòng trường hợp 'Xem truyện HentaiVn - ...')
        StripWebBrandPrefix();

        // Dọn dẹp dấu phân tách treo ở đầu nếu còn sót
        title = title.TrimStart(' ', '\t', '-', ':', '\u2013', '\u2014', '|', '~', '/', ']').Trim();

        return title;
    }

    private string ExtractFallbackTitleFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            string segment = uri.AbsolutePath.Trim('/');
            int lastSlash = segment.LastIndexOf('/');
            if (lastSlash >= 0) segment = segment.Substring(lastSlash + 1);
            segment = Regex.Replace(segment, @"\.(html|php|aspx|htm)$", "");
            segment = segment.Replace("-", " ").Replace("_", " ");
            if (string.IsNullOrWhiteSpace(segment) && !string.IsNullOrWhiteSpace(uri.Query))
            {
                var matchSearch = Regex.Match(uri.Query, @"[?&]f_search=([^&]+)", RegexOptions.IgnoreCase);
                if (matchSearch.Success)
                {
                    return "Search: " + Uri.UnescapeDataString(matchSearch.Groups[1].Value.Replace('+', ' '));
                }
            }
            if (string.IsNullOrWhiteSpace(segment)) return "E-Hentai Tag";
            return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(segment);
        }
        catch
        {
            return "Comic " + DateTime.Now.ToString("HHmmss");
        }
    }

    private string MakeAbsoluteUrl(string relativeOrAbsolute, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolute)) return string.Empty;
        relativeOrAbsolute = relativeOrAbsolute.Trim();

        if (relativeOrAbsolute.StartsWith("//"))
        {
            return "https:" + relativeOrAbsolute;
        }

        if (Uri.TryCreate(new Uri(baseUrl), relativeOrAbsolute, out var result))
        {
            return result.AbsoluteUri;
        }
        return relativeOrAbsolute;
    }

    public static string? ExtractEHentaiNextUrl(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        // 1. Ưu tiên biến Javascript var nexturl = "..."; (chuẩn xác nhất trên E-Hentai)
        var jsNextMatch = Regex.Match(html, @"(?:var\s+nexturl\s*=\s*[""'](?<next>[^""']*)[""']|nexturl\s*:\s*[""'](?<next>[^""']*)[""'])", RegexOptions.IgnoreCase);
        if (jsNextMatch.Success)
        {
            string raw = jsNextMatch.Groups["next"].Value.Trim();
            if (string.IsNullOrWhiteSpace(raw)) return null; // Trang cuối cùng: var nexturl="";
            return WebUtility.HtmlDecode(raw);
        }

        // 2. Tìm thẻ a có id dnext hoặc unext chứa href
        var nextMatch = Regex.Match(html, @"<a[^>]*?(?:id=[""'](?:dnext|unext)[""'][^>]*?href=[""'](?<next>[^""']+)[""']|href=[""'](?<next>[^""']+)[""'][^>]*?id=[""'](?:dnext|unext)[""'])", RegexOptions.IgnoreCase);
        if (nextMatch.Success)
        {
            string raw = nextMatch.Groups["next"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(raw) && !raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                return WebUtility.HtmlDecode(raw);
            }
        }

        // 3. Fallback table pagination (table.ptt) nếu có
        var pttNextMatch = Regex.Match(html, @"<td[^>]*class=[""']ptdd[""'][^>]*>\s*<a[^>]*href=[""'](?<next>[^""']+)[""'][^>]*>&gt;<\/a>", RegexOptions.IgnoreCase);
        if (pttNextMatch.Success)
        {
            string raw = pttNextMatch.Groups["next"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(raw)) return WebUtility.HtmlDecode(raw);
        }

        return null;
    }

    public async Task<TagAnalysisResult> AnalyzeTagUrlAsync(string url, CancellationToken ct = default)
    {
        var result = new TagAnalysisResult
        {
            BaseUrl = url,
            Domain = DomainRoutingService.DetectDomain(url),
            TotalPages = 1,
            IsSuccess = false
        };

        if (string.IsNullOrWhiteSpace(url))
        {
            result.StatusMessage = "URL không hợp lệ";
            return result;
        }

        try
        {
            string html = string.Empty;
            bool isComixDomain = result.Domain.Contains("comix") || url.Contains("comix.to");
            if (isComixDomain)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
                using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                if (string.IsNullOrWhiteSpace(html))
                {
                    result.StatusMessage = "Không thể tải trang comix.to";
                    return result;
                }

                // Trích xuất số trang từ <div class="list-count mono"><strong>403</strong> items</div>
                int comixMaxPage = 1;
                var comixCountMatch = Regex.Match(html, @"class=[""']list-count mono[""']>\s*<strong>(?<count>[0-9,]+)</strong>\s*items", RegexOptions.IgnoreCase);
                if (comixCountMatch.Success && int.TryParse(comixCountMatch.Groups["count"].Value.Replace(",", ""), out int comixTotalItems) && comixTotalItems > 0)
                {
                    comixMaxPage = (int)Math.Ceiling(comixTotalItems / 28.0);
                }
                else
                {
                    var pagerButtons = Regex.Matches(html, @"class=[""'][^""']*npager__num[^""']*[""'][^>]*>(?<num>\d+)</button>", RegexOptions.IgnoreCase);
                    foreach (Match pb in pagerButtons)
                    {
                        if (int.TryParse(pb.Groups["num"].Value, out int pNum) && pNum > comixMaxPage)
                        {
                            comixMaxPage = pNum;
                        }
                    }
                }

                var comixTitleM = Regex.Match(html, @"<title[^>]*>(?<title>[^<]+)</title>", RegexOptions.IgnoreCase);
                if (comixTitleM.Success)
                {
                    string rawTitle = comixTitleM.Groups["title"].Value;
                    rawTitle = Regex.Replace(rawTitle, @"\s*[-·|]\s*Comix.*$", "", RegexOptions.IgnoreCase);
                    result.TagTitle = CleanTitle(rawTitle);
                }
                if (string.IsNullOrWhiteSpace(result.TagTitle))
                {
                    result.TagTitle = ExtractFallbackTitleFromUrl(url);
                }

                result.TotalPages = Math.Max(1, comixMaxPage);
                result.IsSuccess = true;
                result.StatusMessage = $"Phân tích thành công: {result.TotalPages:N0} trang ({result.TagTitle})";
                return result;
            }

            bool isMoetruyenDomain = result.Domain.Contains("moetruyen") || url.Contains("moetruyen");
            if (isMoetruyenDomain)
            {
                using var moeReq = new HttpRequestMessage(HttpMethod.Get, url);
                moeReq.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
                moeReq.Headers.TryAddWithoutValidation("Referer", "https://moetruyen.net/");
                using var moeRes = await _httpClient.SendAsync(moeReq, ct).ConfigureAwait(false);
                if (!moeRes.IsSuccessStatusCode)
                {
                    result.StatusMessage = $"Lỗi máy chủ HTTP {(int)moeRes.StatusCode}";
                    return result;
                }
                html = await moeRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                // Bóc tách tổng số trang qua data-manga-catalog-page="(\d+)"
                int moeMaxP = 1;
                var catMatches = Regex.Matches(html, @"data-manga-catalog-page=[""'](?<p>\d+)[""']", RegexOptions.IgnoreCase);
                foreach (Match cm in catMatches)
                {
                    if (int.TryParse(cm.Groups["p"].Value, out int p) && p > moeMaxP) moeMaxP = p;
                }
                var pageParamMatches = Regex.Matches(html, @"[?&]page=(?<p>\d+)", RegexOptions.IgnoreCase);
                foreach (Match pm in pageParamMatches)
                {
                    if (int.TryParse(pm.Groups["p"].Value, out int p) && p > moeMaxP) moeMaxP = p;
                }

                // Tiêu đề tag/tìm kiếm
                string rawMoeTitle = string.Empty;
                var qMatch = Regex.Match(url, @"[?&]q=([^&]+)");
                if (qMatch.Success)
                {
                    rawMoeTitle = "Tìm kiếm: " + Uri.UnescapeDataString(qMatch.Groups[1].Value.Replace("+", " "));
                }
                else
                {
                    var incMatch = Regex.Match(url, @"[?&]include=([^&]+)");
                    if (incMatch.Success)
                    {
                        rawMoeTitle = "Thể loại: " + Uri.UnescapeDataString(incMatch.Groups[1].Value.Replace("+", " "));
                    }
                    else
                    {
                        var tMatch = Regex.Match(html, @"<title[^>]*>(?<t>[^<]+)</title>", RegexOptions.IgnoreCase);
                        if (tMatch.Success)
                        {
                            rawMoeTitle = CleanTitle(Regex.Replace(tMatch.Groups["t"].Value, @"\s*[-|·]\s*Mòe Truyện.*$", "", RegexOptions.IgnoreCase));
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(rawMoeTitle)) rawMoeTitle = ExtractFallbackTitleFromUrl(url);

                result.TagTitle = rawMoeTitle;
                result.TotalPages = Math.Max(1, moeMaxP);
                result.IsSuccess = true;
                result.StatusMessage = $"Phân tích thành công: {result.TotalPages:N0} trang ({result.TagTitle})";
                return result;
            }

            bool isEHentaiDomain = result.Domain.Contains("e-hentai") || result.Domain.Contains("ehentai") || result.Domain.Contains("exhentai") || url.Contains("e-hentai") || url.Contains("exhentai");

            if (isEHentaiDomain)
            {
                html = await FetchEHentaiHtmlAsync(url, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(html))
                {
                    result.StatusMessage = "Không thể tải nội dung trang E-Hentai (kiểm tra kết nối hoặc chặn IP)";
                    return result;
                }
            }
            else
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (result.Domain.Contains("hentaiforce") || url.Contains("hentaiforce"))
                {
                    req.Headers.TryAddWithoutValidation("Referer", "https://hentaiforce.net/");
                }
                else if (result.Domain.Contains("hitomi") || url.Contains("hitomi"))
                {
                    req.Headers.TryAddWithoutValidation("Referer", "https://hitomi.la/");
                }
                using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    result.StatusMessage = $"Lỗi máy chủ HTTP {(int)res.StatusCode}";
                    return result;
                }
                html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }

            // Trích xuất tiêu đề Tag / Thể loại
            var titleMatch = Regex.Match(html, @"<title[^>]*>(?<title>[^<]+)</title>", RegexOptions.IgnoreCase);
            if (titleMatch.Success)
            {
                string rawT = titleMatch.Groups["title"].Value;
                rawT = Regex.Replace(rawT, @"\s*-\s*E-Hentai Galleries\s*$", "", RegexOptions.IgnoreCase);
                rawT = Regex.Replace(rawT, @"\s*[-·|]\s*SuicaoDex.*$", "", RegexOptions.IgnoreCase);
                result.TagTitle = CleanTitle(rawT);
            }
            if (string.IsNullOrWhiteSpace(result.TagTitle))
            {
                result.TagTitle = ExtractFallbackTitleFromUrl(url);
            }

            int maxPage = 1;

            bool isHitomiDomain = result.Domain.Contains("hitomi") || url.Contains("hitomi");
            if (isHitomiDomain)
            {
                if (HitomiResolverService.IsHitomiSearchUrl(url))
                {
                    result.TagTitle = HitomiResolverService.ExtractSearchTitle(url);
                    var searchGalleryIds = await HitomiResolverService.ResolveHitomiSearchGalleryIdsAsync(url, _httpClient, ct).ConfigureAwait(false);
                    if (searchGalleryIds.Count > 0)
                    {
                        int calculatedPages = (int)Math.Ceiling(searchGalleryIds.Count / 25.0);
                        result.TotalPages = Math.Max(1, calculatedPages);
                        result.IsSuccess = true;
                        result.StatusMessage = $"Phân tích thành công: {result.TotalPages:N0} trang ({searchGalleryIds.Count:N0} truyện) ({result.TagTitle})";
                        return result;
                    }
                    else
                    {
                        result.TotalPages = 0;
                        result.IsSuccess = false;
                        result.StatusMessage = $"Không tìm thấy truyện nào khớp với từ khóa tìm kiếm: {result.TagTitle}";
                        return result;
                    }
                }

                string hTitle = ExtractFallbackTitleFromUrl(url);
                if (titleMatch.Success)
                {
                    string rawHitomi = titleMatch.Groups["title"].Value.Trim();
                    rawHitomi = Regex.Replace(rawHitomi, @"\s*\|\s*Hitomi(?:\.la)?\s*$", "", RegexOptions.IgnoreCase).Trim(' ', '|', '-');
                    if (!string.IsNullOrWhiteSpace(rawHitomi))
                    {
                        hTitle = rawHitomi;
                    }
                }
                result.TagTitle = hTitle;

                string nozomiUrl = GetHitomiNozomiUrl(url);
                if (!string.IsNullOrWhiteSpace(nozomiUrl))
                {
                    try
                    {
                        using var nozomiReq = new HttpRequestMessage(HttpMethod.Head, nozomiUrl);
                        nozomiReq.Headers.TryAddWithoutValidation("Referer", "https://hitomi.la/");
                        using var nozomiRes = await _httpClient.SendAsync(nozomiReq, ct).ConfigureAwait(false);
                        if (nozomiRes.IsSuccessStatusCode && nozomiRes.Content.Headers.ContentLength.HasValue)
                        {
                            long totalBytes = nozomiRes.Content.Headers.ContentLength.Value;
                            int totalGalleries = (int)(totalBytes / 4);
                            if (totalGalleries > 0)
                            {
                                int calculatedPages = (int)Math.Ceiling(totalGalleries / 25.0);
                                maxPage = Math.Max(maxPage, calculatedPages);
                                result.TotalPages = maxPage;
                                result.IsSuccess = true;
                                result.StatusMessage = $"Phân tích thành công: {result.TotalPages:N0} trang ({totalGalleries:N0} truyện) ({result.TagTitle})";
                                return result;
                            }
                        }
                    }
                    catch { }
                }
            }

            // 0. Phân tích kết quả tìm kiếm/danh mục đặc thù E-Hentai (Keyset pagination qua nexturl)
            if (isEHentaiDomain)
            {
                string? firstNextUrl = ExtractEHentaiNextUrl(html);

                // Nếu không có nexturl ngay từ trang 1 -> Toàn bộ danh mục chỉ có duy nhất 1 trang!
                if (string.IsNullOrWhiteSpace(firstNextUrl))
                {
                    result.TotalPages = 1;
                    result.IsSuccess = true;
                    result.StatusMessage = $"Phân tích thành công: 1 trang ({result.TagTitle})";
                    return result;
                }

                // Đọc số lượng kết quả ước lượng từ thẻ searchtext (ví dụ: "Found about 290 results" hoặc "Found about 5,000 results")
                int rawCount = 0;
                var ehCountMatch = Regex.Match(html, @"(?:Found\s+(?:about\s+)?|Showing\s+\d+\s*-\s*\d+\s+of\s+)([\d,]+)(?:\+)?\s+results", RegexOptions.IgnoreCase);
                if (ehCountMatch.Success)
                {
                    string numStr = ehCountMatch.Groups[1].Value.Replace(",", "");
                    int.TryParse(numStr, out rawCount);
                }

                int estimatedPages = rawCount > 0 ? (int)Math.Ceiling(rawCount / 25.0) : 2;

                // TH1: Dò tìm trang nhanh (Fast Page Probe) khi số lượng trang ước lượng nhỏ (<= 15 trang)
                // Giúp xác định chính xác 100% số trang thực tế sau khi đã lọc bỏ các gallery bị loại trừ (excluded)
                // Ví dụ: Link 1 báo "Found about 290 results" (~12 trang thô), sau 5 bước probe sẽ phát hiện chính xác 6 trang!
                if (estimatedPages <= 15)
                {
                    int confirmedPages = 1;
                    string? probeUrl = firstNextUrl;

                    while (!string.IsNullOrWhiteSpace(probeUrl) && confirmedPages < 25 && !ct.IsCancellationRequested)
                    {
                        confirmedPages++;
                        try
                        {
                            string probeHtml = await FetchEHentaiHtmlAsync(probeUrl, ct).ConfigureAwait(false);
                            if (string.IsNullOrWhiteSpace(probeHtml)) break;
                            probeUrl = ExtractEHentaiNextUrl(probeHtml);
                        }
                        catch
                        {
                            break;
                        }
                    }

                    result.TotalPages = confirmedPages;
                    result.IsSuccess = true;
                    result.StatusMessage = $"Phân tích thành công: {confirmedPages} trang ({result.TagTitle})";
                    return result;
                }
                else
                {
                    // TH2: Số lượng trang lớn (> 15 trang)
                    // Kiểm tra xem query có chứa toán tử trừ (-) loại trừ từ khóa không (ví dụ: -"furry$", -"goblin$")
                    // Do Search Engine E-Hentai đếm thô trước khi trừ, số gallery thực tế chỉ chiếm khoảng 20% - 25% (như 5000 kết quả -> 43 trang)
                    bool hasNegativeFilter = url.Contains("-%22") || url.Contains("-\"") || url.Contains("+-") || url.Contains(" -");

                    int adjustedPages = estimatedPages;
                    if (hasNegativeFilter)
                    {
                        adjustedPages = Math.Max(2, (int)Math.Round(estimatedPages * 0.215));
                    }

                    result.TotalPages = adjustedPages;
                    result.IsSuccess = true;
                    result.StatusMessage = rawCount > 0
                        ? $"Phân tích thành công: ~{adjustedPages} trang (~{rawCount:N0} kết quả ước tính) ({result.TagTitle})"
                        : $"Phân tích thành công: ~{adjustedPages} trang ({result.TagTitle})";
                    return result;
                }
            }

            // 1. Phân tích vùng phân trang đặc thù (page_redirect, pagination, paging)
            var paginationBlockMatch = Regex.Match(html, @"<(?:div|ul|nav)[^>]*class=[""'][^""']*(?:page_redirect|pagination|paging|page-navigation)[^""']*[""'][^>]*>([\s\S]*?)<\/(?:div|ul|nav)>", RegexOptions.IgnoreCase);
            string targetPaginationHtml = paginationBlockMatch.Success ? paginationBlockMatch.Value : html;

            // 2. Phân tích các liên kết phân trang pagination (href="/trang-X", href="/page-X", etc.)
            var pageMatches = Regex.Matches(targetPaginationHtml, @"(?:page[=/_-]|trang[=/_-]|\/page\/|\/trang-)(\d+)", RegexOptions.IgnoreCase);
            foreach (Match m in pageMatches)
            {
                if (int.TryParse(m.Groups[1].Value, out int p) && p > maxPage && p < 100000)
                {
                    maxPage = p;
                }
            }

            // 2.1 Phân tích các liên kết phân trang dạng path số (ví dụ hentaiforce: /category/doujin/2)
            var slashPageMatches = Regex.Matches(targetPaginationHtml, @"href=[""'][^""']*?/(\d+)(?:[?#/""']|$)", RegexOptions.IgnoreCase);
            foreach (Match m in slashPageMatches)
            {
                if (int.TryParse(m.Groups[1].Value, out int p) && p > maxPage && p < 100000)
                {
                    maxPage = p;
                }
            }

            // 3. Phân tích text trong các thẻ trang (thẻ a, span, li, p chứa số trang)
            var numMatches = Regex.Matches(targetPaginationHtml, @"<(?:a|span|li|p)[^>]*>(\d+)</(?:a|span|li|p)>", RegexOptions.IgnoreCase);
            foreach (Match m in numMatches)
            {
                if (int.TryParse(m.Groups[1].Value, out int p) && p > maxPage && p < 100000)
                {
                    maxPage = p;
                }
            }

            // 4. Fallback quét toàn bộ HTML nếu chưa tìm được trang trong pagination container
            if (maxPage <= 1 && paginationBlockMatch.Success)
            {
                var fallbackMatches = Regex.Matches(html, @"(?:page[=/_-]|trang[=/_-]|\/page\/|\/trang-)(\d+)", RegexOptions.IgnoreCase);
                foreach (Match m in fallbackMatches)
                {
                    if (int.TryParse(m.Groups[1].Value, out int p) && p > maxPage && p < 100000)
                    {
                        maxPage = p;
                    }
                }
            }

            result.TotalPages = Math.Max(1, maxPage);
            result.IsSuccess = true;
            result.StatusMessage = $"Phân tích thành công: {result.TotalPages} trang ({result.TagTitle})";
        }
        catch (Exception ex)
        {
            result.StatusMessage = $"Lỗi phân tích: {ex.Message}";
        }

        return result;
    }

    public async Task<List<ComicBookItem>> ScrapeBatchComicsFromTagPagesAsync(string tagUrl, int pageFrom, int pageTo, string domain, CancellationToken ct = default, Action<int, int, int, string>? onProgress = null)
    {
        var list = new List<ComicBookItem>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        tagUrl = tagUrl.Trim();
        pageFrom = Math.Max(1, pageFrom);
        pageTo = Math.Max(pageFrom, pageTo);

        bool isEHentai = domain.Contains("e-hentai") || domain.Contains("ehentai") || domain.Contains("exhentai") || tagUrl.Contains("e-hentai") || tagUrl.Contains("exhentai");

        if (isEHentai)
        {
            string currentUrl = tagUrl;
            for (int page = 1; page <= pageTo; page++)
            {
                if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(currentUrl)) break;

                try
                {
                    string html = await FetchEHentaiHtmlAsync(currentUrl, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(html)) break;

                    if (page >= pageFrom)
                    {
                        var comicLinks = ExtractComicLinksFromTagPage(html, currentUrl, domain);
                        string latestTitle = string.Empty;
                        foreach (var (cUrl, cTitle, cCover) in comicLinks)
                        {
                            if (seenUrls.Add(cUrl))
                            {
                                latestTitle = cTitle;
                                list.Add(new ComicBookItem
                                {
                                    Index = list.Count + 1,
                                    Url = cUrl,
                                    Title = cTitle,
                                    CoverUrl = cCover,
                                    Domain = DomainRoutingService.DetectDomain(cUrl),
                                    Status = "Waiting",
                                    StatusMessage = "Sẵn sàng tải"
                                });
                            }
                        }
                        onProgress?.Invoke(page - pageFrom + 1, pageTo - pageFrom + 1, list.Count, latestTitle);
                    }

                    // Tìm URL trang kế tiếp qua ExtractEHentaiNextUrl chuẩn
                    string? nextUrl = ExtractEHentaiNextUrl(html);

                    if (!string.IsNullOrWhiteSpace(nextUrl) && !string.Equals(nextUrl, currentUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        currentUrl = MakeAbsoluteUrl(nextUrl, currentUrl);
                    }
                    else
                    {
                        break;
                    }
                }
                catch
                {
                    break;
                }
            }

            return list;
        }

        bool isHitomi = domain.Contains("hitomi") || tagUrl.Contains("hitomi");
        if (isHitomi)
        {
            bool isHitomiSearch = HitomiResolverService.IsHitomiSearchUrl(tagUrl);
            List<int>? searchGalleryIds = null;
            if (isHitomiSearch)
            {
                searchGalleryIds = await HitomiResolverService.ResolveHitomiSearchGalleryIdsAsync(tagUrl, _httpClient, ct).ConfigureAwait(false);
            }

            string nozomiUrl = isHitomiSearch ? string.Empty : GetHitomiNozomiUrl(tagUrl);
            if (!string.IsNullOrWhiteSpace(nozomiUrl) || (isHitomiSearch && searchGalleryIds != null && searchGalleryIds.Count > 0))
            {
                int totalHitomiPages = pageTo - pageFrom + 1;
                int totalExpectedGalleries = (isHitomiSearch && searchGalleryIds != null)
                    ? Math.Min(totalHitomiPages * 25, Math.Max(0, searchGalleryIds.Count - (pageFrom - 1) * 25))
                    : (totalHitomiPages * 25);
                if (totalExpectedGalleries <= 0) totalExpectedGalleries = 25;
                int completedHitomiGalleries = 0;

                int hitomiConcurrency = Math.Clamp(MaxCpuThreads, 1, OperatingSystem.IsAndroid() ? 6 : 16);
                using var hitomiSem = new SemaphoreSlim(hitomiConcurrency, hitomiConcurrency);
                await HitomiResolverService.RefreshGGAsync(_httpClient, ct).ConfigureAwait(false);
                for (int page = pageFrom; page <= pageTo; page++)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var pageGalleryIds = new List<int>();

                        if (isHitomiSearch && searchGalleryIds != null)
                        {
                            int startIdx = (page - 1) * 25;
                            int takeCount = Math.Min(25, searchGalleryIds.Count - startIdx);
                            if (startIdx >= 0 && startIdx < searchGalleryIds.Count && takeCount > 0)
                            {
                                for (int i = 0; i < takeCount; i++)
                                {
                                    int gId = searchGalleryIds[startIdx + i];
                                    string cUrl = $"https://hitomi.la/galleries/{gId}.html";
                                    if (seenUrls.Add(cUrl))
                                    {
                                        pageGalleryIds.Add(gId);
                                    }
                                }
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(nozomiUrl))
                        {
                            long startByte = (long)(page - 1) * 25 * 4;
                            long endByte = startByte + (25 * 4) - 1;

                            using var req = new HttpRequestMessage(HttpMethod.Get, nozomiUrl);
                            req.Headers.TryAddWithoutValidation("Referer", "https://hitomi.la/");
                            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startByte, endByte);
                            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                            if (!res.IsSuccessStatusCode) continue;

                            byte[] bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                            for (int i = 0; i + 3 < bytes.Length; i += 4)
                            {
                                int gId = (bytes[i] << 24) | (bytes[i + 1] << 16) | (bytes[i + 2] << 8) | bytes[i + 3];
                                string cUrl = $"https://hitomi.la/galleries/{gId}.html";
                                if (seenUrls.Add(cUrl))
                                {
                                    pageGalleryIds.Add(gId);
                                }
                            }
                        }

                        var fetchedItems = new ComicBookItem?[pageGalleryIds.Count];
                        var galleryTasks = pageGalleryIds.Select(async (gId, idx) =>
                        {
                            await hitomiSem.WaitAsync(ct).ConfigureAwait(false);
                            try
                            {
                                string cUrl = $"https://hitomi.la/galleries/{gId}.html";
                                string cTitle = $"Hitomi Gallery {gId}";
                                string cCover = string.Empty;
                                string cTags = string.Empty;
                                string cType = string.Empty;
                                string cLang = string.Empty;
                                string cArtist = string.Empty;
                                string cParody = string.Empty;
                                int cPages = 0;

                                try
                                {
                                    using var doc = await HitomiResolverService.FetchGalleryInfoDocAsync(gId.ToString(), _httpClient, ct).ConfigureAwait(false);
                                    if (doc != null)
                                    {
                                        var root = doc.RootElement;
                                        var meta = ExtractHitomiMetadata(root, cUrl);
                                        cType = meta.Type;
                                        cLang = meta.Language;
                                        cArtist = meta.Artist;
                                        cParody = meta.Parody;
                                        cTags = meta.Tags;
                                        cPages = meta.TotalPages;
                                        if (root.TryGetProperty("title", out var tProp) && tProp.ValueKind == JsonValueKind.String)
                                        {
                                            cTitle = tProp.GetString() ?? cTitle;
                                        }
                                        if (root.TryGetProperty("files", out var fProp) && fProp.ValueKind == JsonValueKind.Array && fProp.GetArrayLength() > 0)
                                        {
                                            var firstF = fProp[0];
                                            string fHash = firstF.TryGetProperty("hash", out var hP) ? (hP.GetString() ?? "") : "";
                                            string fName = firstF.TryGetProperty("name", out var nP) ? (nP.GetString() ?? "") : "";
                                            if (!string.IsNullOrWhiteSpace(fHash))
                                            {
                                                cCover = HitomiResolverService.ResolveImageUrl(fHash, fName, isThumbnail: true);
                                            }
                                        }
                                    }
                                }
                                catch { }

                                fetchedItems[idx] = new ComicBookItem
                                {
                                    Url = cUrl,
                                    Title = CleanTitle(cTitle),
                                    CoverUrl = cCover,
                                    PreviewType = cType,
                                    PreviewLanguage = cLang,
                                    PreviewArtist = cArtist,
                                    PreviewParody = cParody,
                                    PreviewTagsText = cTags,
                                    TotalChapters = 1,
                                    TotalPages = cPages,
                                    LatestChapter = cPages > 0 ? $"{cPages} Pages" : "Gallery",
                                    Domain = "hitomi.la",
                                    Status = "Waiting",
                                    StatusMessage = cPages > 0 ? $"{cPages} Pages" : "Sẵn sàng tải"
                                };
                            }
                            finally
                            {
                                int doneCount = Interlocked.Increment(ref completedHitomiGalleries);
                                string reportTitle = fetchedItems[idx]?.Title ?? $"Hitomi Gallery {gId}";
                                onProgress?.Invoke(doneCount, totalExpectedGalleries, doneCount, reportTitle);
                                hitomiSem.Release();
                            }
                        });

                        await Task.WhenAll(galleryTasks).ConfigureAwait(false);
                        foreach (var item in fetchedItems)
                        {
                            if (item != null)
                            {
                                item.Index = list.Count + 1;
                                list.Add(item);
                            }
                        }
                    }
                    catch { }
                }
                return list;
            }
        }

        int totalPagesToFetch = pageTo - pageFrom + 1;
        int pageConcurrency = Math.Clamp(MaxCpuThreads, 1, OperatingSystem.IsAndroid() ? 6 : 16);
        var pageResults = new List<(string Url, string Title, string Cover)>?[totalPagesToFetch];
        using var pageSem = new SemaphoreSlim(pageConcurrency, pageConcurrency);
        int completedPagesCount = 0;

        var pageTasks = Enumerable.Range(0, totalPagesToFetch).Select(async offset =>
        {
            int page = pageFrom + offset;
            if (ct.IsCancellationRequested) return;

            await pageSem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string pageUrl = BuildPagedTagUrl(tagUrl, page, domain);
                string? html = null;
                using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
                if (domain.Contains("comix"))
                {
                    req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
                }
                else if (domain.Contains("hentaiforce"))
                {
                    req.Headers.TryAddWithoutValidation("Referer", "https://hentaiforce.net/");
                }
                else if (domain.Contains("hitomi"))
                {
                    req.Headers.TryAddWithoutValidation("Referer", "https://hitomi.la/");
                }
                using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }

                if (!string.IsNullOrWhiteSpace(html))
                {
                    var extracted = ExtractComicLinksFromTagPage(html, pageUrl, domain);
                    pageResults[offset] = extracted;
                    int done = Interlocked.Increment(ref completedPagesCount);
                    string lastTitle = extracted.LastOrDefault().Title ?? "";
                    onProgress?.Invoke(done, totalPagesToFetch, done * Math.Max(1, extracted.Count), lastTitle);
                }
                else
                {
                    int done = Interlocked.Increment(ref completedPagesCount);
                    onProgress?.Invoke(done, totalPagesToFetch, 0, "");
                }
            }
            catch
            {
                int done = Interlocked.Increment(ref completedPagesCount);
                onProgress?.Invoke(done, totalPagesToFetch, 0, "");
            }
            finally
            {
                pageSem.Release();
            }
        });

        await Task.WhenAll(pageTasks).ConfigureAwait(false);

        for (int offset = 0; offset < totalPagesToFetch; offset++)
        {
            var comicLinks = pageResults[offset];
            if (comicLinks == null) continue;

            foreach (var (cUrl, cTitle, cCover) in comicLinks)
            {
                if (seenUrls.Add(cUrl))
                {
                    list.Add(new ComicBookItem
                    {
                        Index = list.Count + 1,
                        Url = cUrl,
                        Title = cTitle,
                        CoverUrl = cCover,
                        Domain = DomainRoutingService.DetectDomain(cUrl),
                        Status = "Waiting",
                        StatusMessage = "Sẵn sàng tải"
                    });
                }
            }
        }

        return list;
    }

    private static string GetHitomiNozomiUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            string path = uri.AbsolutePath.TrimStart('/');
            if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                string nozomiPath = path.Substring(0, path.Length - 5) + ".nozomi";
                return $"https://ltn.gold-usergeneratedcontent.net/{nozomiPath}";
            }
        }
        catch { }
        return string.Empty;
    }

    private string BuildPagedTagUrl(string baseUrl, int page, string domain)
    {
        if (page <= 1) return baseUrl;

        domain = domain.ToLowerInvariant();
        if (domain.Contains("e-hentai") || domain.Contains("ehentai") || domain.Contains("exhentai"))
        {
            char sep = baseUrl.Contains('?') ? '&' : '?';
            return $"{baseUrl}{sep}page={page - 1}";
        }

        if (domain.Contains("suicaodex"))
        {
            return $"{baseUrl}{(baseUrl.Contains('?') ? '&' : '?')}page={page}";
        }

        if (baseUrl.Contains("?") || baseUrl.Contains("page=") || baseUrl.Contains("&page="))
        {
            if (Regex.IsMatch(baseUrl, @"[?&]page=\d+"))
            {
                return Regex.Replace(baseUrl, @"([?&]page=)\d+", $"${{1}}{page}");
            }
            return $"{baseUrl}&page={page}";
        }

        if (domain.Contains("truyenqq"))
        {
            string clean = Regex.Replace(baseUrl.TrimEnd('/'), @"/(?:page|trang)-\d+(?:\.html)?$", "", RegexOptions.IgnoreCase);
            if (clean.Contains("/tac-gia"))
            {
                return $"{clean}/page-{page}";
            }
            return $"{clean}/trang-{page}";
        }

        if (domain.Contains("thuviensach") || domain.Contains("dilib"))
        {
            return $"{baseUrl.TrimEnd('/')}/trang-{page}/";
        }

        if (domain.Contains("daomeoden"))
        {
            return $"{baseUrl.TrimEnd('/')}/page-{page}.html";
        }

        if (domain.Contains("moetruyen"))
        {
            if (page <= 1 && !baseUrl.Contains("page=")) return baseUrl;
            if (Regex.IsMatch(baseUrl, @"([?&]page=)\d+"))
            {
                return Regex.Replace(baseUrl, @"([?&]page=)\d+", $"${{1}}{page}");
            }
            char sep = baseUrl.Contains('?') ? '&' : '?';
            return $"{baseUrl}{sep}page={page}";
        }

        if (domain.Contains("sayhentai") || domain.Contains("hentai2read"))
        {
            return $"{baseUrl.TrimEnd('/')}/page/{page}/";
        }

        if (domain.Contains("hentaiforce"))
        {
            if (baseUrl.Contains("?"))
            {
                string cleanQuery = Regex.Replace(baseUrl, @"([?&])page=\d+&?", "$1").TrimEnd('?', '&');
                char sep = cleanQuery.Contains("?") ? '&' : '?';
                return $"{cleanQuery}{sep}page={page}";
            }
            string clean = Regex.Replace(baseUrl.TrimEnd('/'), @"/\d+$", "");
            return $"{clean}/{page}";
        }

        return $"{baseUrl}?page={page}";
    }

    private List<(string Url, string Title, string Cover)> ExtractComicLinksFromTagPage(string html, string pageUrl, string domain)
    {
        var result = new List<(string Url, string Title, string Cover)>();
        domain = domain.ToLowerInvariant();

        // Bóc tách chuyên biệt cho comix.to (thẻ <div class="lrow">)
        if (domain.Contains("comix"))
        {
            var cardMatches = Regex.Matches(html, @"<div\s+class=[""']lrow[""'][\s\S]*?(?=<div\s+class=[""']lrow[""']|<footer|<\/body>|$)", RegexOptions.IgnoreCase);
            foreach (Match cm in cardMatches)
            {
                string cardHtml = cm.Value;
                var linkM = Regex.Match(cardHtml, @"href=[""'](?<url>/title/[^""']+)[""']", RegexOptions.IgnoreCase);
                if (!linkM.Success) continue;

                string cUrl = linkM.Groups["url"].Value;
                if (!cUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    cUrl = "https://comix.to" + cUrl;
                }

                string cTitle = string.Empty;
                var titleM = Regex.Match(cardHtml, @"<h3\s+class=[""']lrow__title[""']>(?<title>[^<]+)</h3>", RegexOptions.IgnoreCase);
                if (titleM.Success)
                {
                    cTitle = CleanTitle(titleM.Groups["title"].Value);
                }
                else
                {
                    var ariaM = Regex.Match(cardHtml, @"aria-label=[""'](?<title>[^""']+)[""']", RegexOptions.IgnoreCase);
                    if (ariaM.Success) cTitle = CleanTitle(ariaM.Groups["title"].Value);
                }

                string cCover = string.Empty;
                var coverM = Regex.Match(cardHtml, @"<img[^>]+src=[""'](?<src>[^""']+)[""']", RegexOptions.IgnoreCase);
                if (coverM.Success)
                {
                    cCover = coverM.Groups["src"].Value;
                }

                if (!string.IsNullOrWhiteSpace(cUrl) && !string.IsNullOrWhiteSpace(cTitle))
                {
                    result.Add((cUrl, cTitle, cCover));
                }
            }
            return result;
        }

        // 0. Bóc tách chuyên biệt cho E-Hentai (bảng Compact/Minimal/Extended hàng <tr> hoặc lưới Thumbnail <div class="gl1t">)
        if (domain.Contains("e-hentai") || domain.Contains("ehentai") || domain.Contains("exhentai"))
        {
            var blocks = new List<string>();

            // 0.1 Thử bóc tách theo khối div Thumbnail/Extended (gl1t / gl1e)
            var divBlocks = Regex.Matches(html, @"<div\s+class=[""']gl1[te][^""']*[""'][\s\S]*?(?=<div\s+class=[""']gl1[te][^""']*[""']|<div\s+class=[""']searchnav[""']|<div\s+id=[""']rangebar[""']|<\/body>|$)", RegexOptions.IgnoreCase);
            if (divBlocks.Count > 0)
            {
                foreach (Match db in divBlocks) blocks.Add(db.Value);
            }
            else
            {
                // 0.2 Fallback bảng Minimal/Compact (các thẻ <tr>)
                var trBlocks = Regex.Matches(html, @"<tr[^>]*>[\s\S]*?<\/tr>", RegexOptions.IgnoreCase);
                foreach (Match tb in trBlocks) blocks.Add(tb.Value);
            }

            foreach (string block in blocks)
            {
                var gMatch = Regex.Match(block, @"href=[""'](?<link>(?:https?://[^""']+)?/(?:g|mpv)/\d+/[a-zA-Z0-9]+/?)[^""']*[""']", RegexOptions.IgnoreCase);
                if (!gMatch.Success) continue;

                string gLink = MakeAbsoluteUrl(gMatch.Groups["link"].Value, pageUrl);

                string gTitle = string.Empty;
                var glinkMatch = Regex.Match(block, @"<div\s+class=[""'][^""']*glink[^""']*[""'][^>]*>(?<t>[^<]+)<\/div>", RegexOptions.IgnoreCase);
                if (glinkMatch.Success)
                {
                    gTitle = WebUtility.HtmlDecode(glinkMatch.Groups["t"].Value.Trim());
                }

                string gCover = string.Empty;
                foreach (Match imgMatch in Regex.Matches(block, @"<img\s+[^>]*?>", RegexOptions.IgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(gTitle))
                    {
                        var altMatch = Regex.Match(imgMatch.Value, @"(?:alt|title)=[""'](?<t>[^""']+)[""']", RegexOptions.IgnoreCase);
                        if (altMatch.Success && !string.IsNullOrWhiteSpace(altMatch.Groups["t"].Value))
                        {
                            gTitle = WebUtility.HtmlDecode(altMatch.Groups["t"].Value.Trim());
                        }
                    }

                    string? img = ExtractImageUrlFromTag(imgMatch.Value);
                    if (!string.IsNullOrWhiteSpace(img) &&
                        !IsIgnoredImage(img) &&
                        !img.Contains("blank.gif", StringComparison.OrdinalIgnoreCase) &&
                        !img.Contains("mr.gif", StringComparison.OrdinalIgnoreCase) &&
                        !img.Contains("/g/t.png", StringComparison.OrdinalIgnoreCase) &&
                        !img.Contains("/g/d.png", StringComparison.OrdinalIgnoreCase))
                    {
                        gCover = MakeAbsoluteUrl(img, pageUrl);
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(gTitle))
                {
                    gTitle = ExtractFallbackTitleFromUrl(gLink);
                }

                if (!result.Any(x => x.Url.Equals(gLink, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add((gLink, CleanTitle(gTitle), gCover));
                }
            }

            if (result.Count > 0)
            {
                return result;
            }
        }

        // 1. Regex tìm tất cả các thẻ chứa link truyện và ảnh bìa
        var itemMatches = Regex.Matches(html, @"<a\s+[^>]*?href=[""'](?<link>[^""']+)[""'][^>]*>(?<inner>[\s\S]*?)<\/a>", RegexOptions.IgnoreCase);

        foreach (Match m in itemMatches)
        {
            string link = m.Groups["link"].Value.Trim();
            string inner = m.Groups["inner"].Value;

            if (link.StartsWith("#") || link.StartsWith("javascript:") || link.Contains("/the-loai/") || link.Contains("/genre/"))
            {
                continue;
            }

            // Lọc đúng link truyện theo domain
            bool isComicLink = false;
            if (domain.Contains("truyenqq") && link.Contains("/truyen-tranh/") && !link.Contains("-chap-")) isComicLink = true;
            else if (domain.Contains("nettruyen") && link.Contains("/truyen-tranh/") && !link.Contains("/chap-") && !link.Contains("/chuong-")) isComicLink = true;
            else if (domain.Contains("thuviensach") && (link.EndsWith(".html") || link.Contains("/truyen-tranh/"))) isComicLink = true;
            else if (domain.Contains("loppytoonn") && link.Contains("/truyen/") && link.TrimEnd('/').Split('/').Length <= 5) isComicLink = true;
            else if (domain.Contains("daomeoden") && link.Contains("/truyen-tranh/")) isComicLink = true;
            else if (domain.Contains("moetruyen") && Regex.IsMatch(link, @"/manga/\d+-") && !link.Contains("/chapters/")) isComicLink = true;
            else if (domain.Contains("vi-hentai") && link.Contains("/truyen/")) isComicLink = true;
            else if (domain.Contains("damconuong") && link.Contains("/truyen/") && !link.Contains("/chapter-") && !link.Contains("/chap-")) isComicLink = true;
            else if (domain.Contains("sayhentai") && (link.Contains("/story/") || link.Contains("/truyen/"))) isComicLink = true;
            else if (domain.Contains("hentai2read") && link.Contains("hentai2read.com/") && link.TrimEnd('/').Split('/').Length <= 5) isComicLink = true;
            else if (domain.Contains("hentaiforce") && link.Contains("/view/")) isComicLink = true;
            else if (domain.Contains("hitomi") && (link.Contains("/galleries/") || link.Contains("/reader/") || link.Contains("/manga/") || link.Contains("/doujinshi/") || link.Contains("/cg/") || link.Contains("/gamecg/"))) isComicLink = true;
            else if ((domain.Contains("e-hentai") || domain.Contains("ehentai") || domain.Contains("exhentai")) && (link.Contains("/g/") || link.Contains("/mpv/"))) isComicLink = true;
            else if (domain.Contains("suicaodex") && link.Contains("/manga/")) isComicLink = true;

            if (isComicLink)
            {
                string fullUrl = MakeAbsoluteUrl(link, pageUrl);
                
                string title = string.Empty;
                var titleAttr = Regex.Match(m.Value, @"title=[""'](?<t>[^""']+)[""']", RegexOptions.IgnoreCase);
                if (titleAttr.Success)
                {
                    title = WebUtility.HtmlDecode(titleAttr.Groups["t"].Value.Trim());
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    var cleanInner = Regex.Replace(inner, @"<[^>]+>", " ").Trim();
                    if (!string.IsNullOrWhiteSpace(cleanInner) && cleanInner.Length >= 2)
                    {
                        title = WebUtility.HtmlDecode(cleanInner);
                    }
                }

                // Hỗ trợ bóc tách tên tiếng Việt có dấu từ thẻ img (alt="..." hoặc data-title="...") cho Damconuong và các web dùng thẻ ảnh
                if (string.IsNullOrWhiteSpace(title))
                {
                    var altAttr = Regex.Match(inner, @"alt=[""'](?<t>[^""']+)[""']", RegexOptions.IgnoreCase);
                    if (altAttr.Success && !string.IsNullOrWhiteSpace(altAttr.Groups["t"].Value) && altAttr.Groups["t"].Value.Trim().Length >= 2)
                    {
                        title = WebUtility.HtmlDecode(altAttr.Groups["t"].Value.Trim());
                    }
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    title = ExtractFallbackTitleFromUrl(fullUrl);
                }

                string coverUrl = string.Empty;
                foreach (Match imgMatch in Regex.Matches(inner, @"<img\s+[^>]*?>", RegexOptions.IgnoreCase))
                {
                    string? img = ExtractImageUrlFromTag(imgMatch.Value);
                    if (!string.IsNullOrWhiteSpace(img) &&
                        !IsIgnoredImage(img) &&
                        !img.Contains("blank.gif", StringComparison.OrdinalIgnoreCase) &&
                        !img.Contains("mr.gif", StringComparison.OrdinalIgnoreCase))
                    {
                        coverUrl = MakeAbsoluteUrl(img, pageUrl);
                        break;
                    }
                }

                int existingIdx = result.FindIndex(x => x.Url.Equals(fullUrl, StringComparison.OrdinalIgnoreCase));
                if (existingIdx >= 0)
                {
                    var existing = result[existingIdx];
                    string newClean = CleanTitle(title);
                    if (!string.IsNullOrWhiteSpace(newClean) && (string.IsNullOrWhiteSpace(existing.Title) || existing.Title == ExtractFallbackTitleFromUrl(fullUrl) || newClean.Length > existing.Title.Length))
                    {
                        result[existingIdx] = (fullUrl, newClean, string.IsNullOrWhiteSpace(existing.Cover) ? coverUrl : existing.Cover);
                    }
                    else if (string.IsNullOrWhiteSpace(existing.Cover) && !string.IsNullOrWhiteSpace(coverUrl))
                    {
                        result[existingIdx] = (fullUrl, existing.Title, coverUrl);
                    }
                }
                else if (title.Length >= 2)
                {
                    result.Add((fullUrl, CleanTitle(title), coverUrl));
                }
            }
        }

        return result;
    }

    private async Task<string> FetchEHentaiHtmlAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        url = DomainRoutingService.NormalizeUrl(url);

        try
        {
            EnsureEHentaiCookies();
            string referer = url.Contains("exhentai.org", StringComparison.OrdinalIgnoreCase)
                ? "https://exhentai.org/"
                : "https://e-hentai.org/";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", referer);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            req.Headers.TryAddWithoutValidation("Cookie", "nw=1; nw=always; sl=dm_1");
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return string.Empty;

            string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(html) && (html.Contains("Content Warning") || html.Contains("Offensive For Everyone")))
            {
                string sep = url.Contains('?') ? "&" : "?";
                string bypassUrl = url + sep + "nw=always";
                using var bypassReq = new HttpRequestMessage(HttpMethod.Get, bypassUrl);
                bypassReq.Headers.TryAddWithoutValidation("Referer", referer);
                bypassReq.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                bypassReq.Headers.TryAddWithoutValidation("Cookie", "nw=1; nw=always; sl=dm_1");
                bypassReq.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
                using var bypassRes = await _httpClient.SendAsync(bypassReq, ct).ConfigureAwait(false);
                if (bypassRes.IsSuccessStatusCode)
                {
                    string bypassHtml = await bypassRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(bypassHtml) && !bypassHtml.Contains("Content Warning"))
                    {
                        return bypassHtml;
                    }
                }
            }
            return html;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<ComicBookItem> ScrapeEHentaiBookAsync(string url, int index, string domain, CancellationToken ct)
    {
        var item = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = domain,
            Status = "Extracting...",
            StatusMessage = "Connecting to e-hentai.org..."
        };

        // 1. Phân tích GID và Token từ URL (vd: /g/4219377/399f951d2d)
        var gidMatch = Regex.Match(url, @"/g/(?<gid>\d+)/(?<token>[a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
        string title = string.Empty;
        string coverUrl = string.Empty;
        int totalPages = 0;

        if (gidMatch.Success && long.TryParse(gidMatch.Groups["gid"].Value, out long gid))
        {
            string token = gidMatch.Groups["token"].Value;
            try
            {
                // Ưu tiên Official GData API: siêu tốc và không lo bị vướng HTML layout
                var apiPayload = new
                {
                    method = "gdata",
                    gidlist = new object[] { new object[] { gid, token } },
                    @namespace = 1
                };

                using var apiReq = new HttpRequestMessage(HttpMethod.Post, "https://api.e-hentai.org/api.php");
                apiReq.Content = new StringContent(JsonSerializer.Serialize(apiPayload), System.Text.Encoding.UTF8, "application/json");
                using var apiRes = await _httpClient.SendAsync(apiReq, ct).ConfigureAwait(false);
                if (apiRes.IsSuccessStatusCode)
                {
                    string json = await apiRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("gmetadata", out var gmeta) && gmeta.GetArrayLength() > 0)
                    {
                        var first = gmeta[0];
                        if (first.TryGetProperty("title", out var tProp)) title = tProp.GetString() ?? "";
                        if (string.IsNullOrWhiteSpace(title) && first.TryGetProperty("title_jpn", out var tjProp)) title = tjProp.GetString() ?? "";
                        if (first.TryGetProperty("thumb", out var thumbProp)) coverUrl = thumbProp.GetString() ?? "";
                        if (first.TryGetProperty("filecount", out var fcProp))
                        {
                            if (int.TryParse(fcProp.GetString(), out int fc)) totalPages = fc;
                        }
                    }
                }
            }
            catch {}
        }

        // 2. Fallback sang cào HTML nếu API không trả về
        if (string.IsNullOrWhiteSpace(title) || totalPages == 0)
        {
            string html = await FetchEHentaiHtmlAsync(url, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(html))
            {
                if (string.IsNullOrWhiteSpace(title))
                {
                    var gnMatch = Regex.Match(html, @"<h1[^>]*id=""gn""[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (gnMatch.Success) title = WebUtility.HtmlDecode(Regex.Replace(gnMatch.Groups[1].Value, @"<[^>]+>", "")).Trim();
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        var gjMatch = Regex.Match(html, @"<h1[^>]*id=""gj""[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                        if (gjMatch.Success) title = WebUtility.HtmlDecode(Regex.Replace(gjMatch.Groups[1].Value, @"<[^>]+>", "")).Trim();
                    }
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        var tMatch = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                        if (tMatch.Success) title = WebUtility.HtmlDecode(Regex.Replace(tMatch.Groups[1].Value, @"\s*-\s*E-Hentai Galleries\s*$", "", RegexOptions.IgnoreCase)).Trim();
                    }
                }

                if (string.IsNullOrWhiteSpace(coverUrl))
                {
                    var bgMatch = Regex.Match(html, @"id=""gd1""[^>]*>.*?url\((['""]?)(?<url>https?://[^'"")]+)\1\)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (bgMatch.Success) coverUrl = bgMatch.Groups["url"].Value;
                    else
                    {
                        var imgMatch = Regex.Match(html, @"id=""gd1""[^>]*>.*?<img[^>]+src=['""](?<url>https?://[^'""]+)['""]", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                        if (imgMatch.Success) coverUrl = imgMatch.Groups["url"].Value;
                    }
                }

                if (totalPages == 0)
                {
                    var countMatch = Regex.Match(html, @"Showing\s+[\d,]+\s*-\s*[\d,]+\s+of\s+([\d,]+)\s+images", RegexOptions.IgnoreCase);
                    if (!countMatch.Success) countMatch = Regex.Match(html, @"<td[^>]*class=""gdt2""[^>]*>(\d+)\s+pages</td>", RegexOptions.IgnoreCase);
                    if (countMatch.Success && int.TryParse(countMatch.Groups[1].Value.Replace(",", ""), out int parsed)) totalPages = parsed;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = ExtractFallbackTitleFromUrl(url);
        }

        item.Title = CleanTitle(title);
        item.CoverUrl = coverUrl;
        item.Chapters.Clear();
        item.Chapters.Add(new ChapterItem
        {
            ChapterNumber = 1,
            Title = "Full Gallery",
            Url = url,
            TotalPages = totalPages,
            Status = "Waiting"
        });

        item.TotalChapters = 1;
        item.LatestChapter = totalPages > 0 ? $"{totalPages} Pages" : "Gallery";
        item.Status = "Ready";
        item.StatusMessage = totalPages > 0 ? $"Extracted {totalPages} pages" : "Ready";

        return item;
    }

    private async Task<List<string>> ExtractEHentaiChapterImagesAsync(string galleryUrl, CancellationToken ct, List<string>? outReaderUrls = null)
    {
        var imageUrls = new List<string>();
        galleryUrl = DomainRoutingService.NormalizeUrl(galleryUrl);

        string firstHtml = await FetchEHentaiHtmlAsync(galleryUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(firstHtml)) return imageUrls;

        // Bóc tách tổng số trang ảnh
        int totalImages = 0;
        var countMatch = Regex.Match(firstHtml, @"Showing\s+[\d,]+\s*-\s*[\d,]+\s+of\s+([\d,]+)\s+images", RegexOptions.IgnoreCase);
        if (!countMatch.Success) countMatch = Regex.Match(firstHtml, @"<td[^>]*class=""gdt2""[^>]*>(\d+)\s+pages</td>", RegexOptions.IgnoreCase);
        if (countMatch.Success && int.TryParse(countMatch.Groups[1].Value.Replace(",", ""), out int parsedCount))
        {
            totalImages = parsedCount;
        }

        var readerUrlByPage = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();

        void HarvestReaderUrls(string htmlContent)
        {
            if (string.IsNullOrWhiteSpace(htmlContent)) return;
            var matches = Regex.Matches(htmlContent, @"<a[^>]+href=['""](?<url>(?:https?://(?:e-hentai|exhentai)\.org)?/s/[a-zA-Z0-9]+/(?<gid>\d+)-(?<page>\d+))['""][^>]*>", RegexOptions.IgnoreCase);
            foreach (Match m in matches)
            {
                string rUrl = m.Groups["url"].Value;
                if (!rUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    rUrl = "https://e-hentai.org" + (rUrl.StartsWith("/") ? "" : "/") + rUrl;
                }
                if (int.TryParse(m.Groups["page"].Value, out int pNum))
                {
                    readerUrlByPage[pNum] = rUrl;
                }
            }
        }

        HarvestReaderUrls(firstHtml);

        int imagesPerPage = readerUrlByPage.Count > 0 ? readerUrlByPage.Count : 20;
        int maxPIndex = 0;
        var pttMatches = Regex.Matches(firstHtml, @"[?&]p=(\d+)", RegexOptions.IgnoreCase);
        foreach (Match m in pttMatches)
        {
            if (int.TryParse(m.Groups[1].Value, out int pVal))
            {
                if (pVal > maxPIndex) maxPIndex = pVal;
            }
        }

        if (totalImages > 0)
        {
            int calculatedMaxP = (int)Math.Ceiling((double)totalImages / imagesPerPage) - 1;
            maxPIndex = Math.Max(maxPIndex, Math.Max(0, calculatedMaxP));
        }

        string baseGalleryUrl = Regex.Replace(galleryUrl, @"([?&])p=\d+(&|$)", "$1", RegexOptions.IgnoreCase).TrimEnd('&', '?');
        string sep = baseGalleryUrl.Contains('?') ? "&" : "?";

        // Thu thập các trang danh mục còn lại song song
        if (maxPIndex >= 1)
        {
            using var pageSem = new SemaphoreSlim(4, 4);
            var pageTasks = new List<Task>();
            for (int p = 1; p <= maxPIndex; p++)
            {
                int pNum = p;
                pageTasks.Add(Task.Run(async () =>
                {
                    await pageSem.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        string pUrl = $"{baseGalleryUrl}{sep}p={pNum}";
                        for (int attempt = 1; attempt <= 3; attempt++)
                        {
                            ct.ThrowIfCancellationRequested();
                            string pHtml = await FetchEHentaiHtmlAsync(pUrl, ct).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(pHtml))
                            {
                                HarvestReaderUrls(pHtml);
                                break;
                            }
                            await Task.Delay(200 * attempt, ct).ConfigureAwait(false);
                        }
                    }
                    catch {}
                    finally
                    {
                        pageSem.Release();
                    }
                }, ct));
            }
            await Task.WhenAll(pageTasks).ConfigureAwait(false);
        }

        int finalTotalPages = totalImages > 0 ? totalImages : (readerUrlByPage.Count > 0 ? readerUrlByPage.Keys.Max() : 1);
        var directImageByPage = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();

        // Resolve direct image URLs từ các trang reader
        using var resolveSem = new SemaphoreSlim(6, 6);
        var resolveTasks = new List<Task>();

        for (int p = 1; p <= finalTotalPages; p++)
        {
            int pageNum = p;
            resolveTasks.Add(Task.Run(async () =>
            {
                await resolveSem.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (!readerUrlByPage.TryGetValue(pageNum, out string? readerUrl) || string.IsNullOrWhiteSpace(readerUrl))
                    {
                        return;
                    }

                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        try
                        {
                            string readerHtml = await FetchEHentaiHtmlAsync(readerUrl, ct).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(readerHtml))
                            {
                                string? directImgUrl = ExtractEHentaiDirectImageUrl(readerHtml);
                                string? nlParam = ExtractEHentaiNlParam(readerHtml);

                                if (string.IsNullOrWhiteSpace(directImgUrl) && !string.IsNullOrWhiteSpace(nlParam))
                                {
                                    string sepChar = readerUrl.Contains('?') ? "&" : "?";
                                    string fallbackUrl = $"{readerUrl}{sepChar}nl={Uri.EscapeDataString(nlParam)}";
                                    string fbHtml = await FetchEHentaiHtmlAsync(fallbackUrl, ct).ConfigureAwait(false);
                                    directImgUrl = ExtractEHentaiDirectImageUrl(fbHtml);
                                }

                                if (!string.IsNullOrWhiteSpace(directImgUrl))
                                {
                                    directImageByPage[pageNum] = WebUtility.HtmlDecode(directImgUrl);
                                    break;
                                }
                            }
                        }
                        catch
                        {
                            if (attempt >= 3) break;
                            await Task.Delay(250 * attempt, ct).ConfigureAwait(false);
                        }
                    }
                }
                catch {}
                finally
                {
                    resolveSem.Release();
                }
            }, ct));
        }

        await Task.WhenAll(resolveTasks).ConfigureAwait(false);

        // Gom ảnh theo đúng thứ tự 1..N và lưu danh sách ReaderUrls đồng bộ
        for (int p = 1; p <= finalTotalPages; p++)
        {
            if (directImageByPage.TryGetValue(p, out string? imgUrl) && !string.IsNullOrWhiteSpace(imgUrl))
            {
                imageUrls.Add(imgUrl);
                if (outReaderUrls != null && readerUrlByPage.TryGetValue(p, out string? rUrl))
                {
                    outReaderUrls.Add(rUrl);
                }
            }
            else if (readerUrlByPage.TryGetValue(p, out string? fallbackReaderUrl))
            {
                // Dự phòng: nếu lúc resolve ban đầu bị miss, lưu link reader để lúc tải tự động resolve lại
                imageUrls.Add(fallbackReaderUrl);
                if (outReaderUrls != null)
                {
                    outReaderUrls.Add(fallbackReaderUrl);
                }
            }
        }

        return imageUrls;
    }

    /// <summary>
    /// Bóc tách URL ảnh mới tinh từ trang reader E-Hentai thông qua tham số nl (failover sang Node H@H khác khi node cũ trả về 15 bytes placeholder hoặc chết)
    /// </summary>
    public async Task<string?> ResolveFreshEHentaiImageUrlAsync(string readerUrl, string? failedDirectUrl = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(readerUrl)) return null;

        try
        {
            readerUrl = DomainRoutingService.NormalizeUrl(readerUrl);
            string readerHtml = await FetchEHentaiHtmlAsync(readerUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(readerHtml)) return null;

            string? currentImgUrl = ExtractEHentaiDirectImageUrl(readerHtml);
            string? nlParam = ExtractEHentaiNlParam(readerHtml);

            // Nếu currentImgUrl khác failedDirectUrl và hợp lệ -> Trả về ngay
            if (!string.IsNullOrWhiteSpace(currentImgUrl) && (failedDirectUrl == null || !string.Equals(currentImgUrl, failedDirectUrl, StringComparison.OrdinalIgnoreCase)))
            {
                return WebUtility.HtmlDecode(currentImgUrl);
            }

            // Nếu URL bị trùng hoặc có mã nl -> Gửi request với ?nl=xxx để server E-Hentai cấp Node Hath mới
            if (!string.IsNullOrWhiteSpace(nlParam))
            {
                string sepChar = readerUrl.Contains('?') ? "&" : "?";
                string fallbackUrl = $"{readerUrl}{sepChar}nl={Uri.EscapeDataString(nlParam)}";
                string fbHtml = await FetchEHentaiHtmlAsync(fallbackUrl, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(fbHtml))
                {
                    string? freshImgUrl = ExtractEHentaiDirectImageUrl(fbHtml);
                    if (!string.IsNullOrWhiteSpace(freshImgUrl))
                    {
                        return WebUtility.HtmlDecode(freshImgUrl);
                    }
                }
            }
        }
        catch {}

        return null;
    }

    private static string? ExtractEHentaiDirectImageUrl(string readerHtml)
    {
        if (string.IsNullOrWhiteSpace(readerHtml)) return null;

        // 1. Thẻ <img id="img" src="..."> hoặc <img src="..." id="img">
        var imgTagMatch = Regex.Match(readerHtml, @"<img[^>]+id=['""]?img['""]?[^>]*>", RegexOptions.IgnoreCase);
        if (imgTagMatch.Success)
        {
            var srcMatch = Regex.Match(imgTagMatch.Value, @"src=['""](?<url>[^'""]+?)['""]", RegexOptions.IgnoreCase);
            if (srcMatch.Success) return srcMatch.Groups["url"].Value;
        }

        var reverseImgMatch = Regex.Match(readerHtml, @"<img[^>]+src=['""](?<url>[^'""]+?)['""][^>]+id=['""]?img['""]?", RegexOptions.IgnoreCase);
        if (reverseImgMatch.Success)
        {
            return reverseImgMatch.Groups["url"].Value;
        }

        // 2. Fallback hath.network / ehgt.org
        var hathMatch = Regex.Match(readerHtml, @"<img[^>]+src=['""](?<url>https?://[^'""]*?\.(?:hath\.network|ehgt\.org)[^'""]*)['""]", RegexOptions.IgnoreCase);
        if (hathMatch.Success) return hathMatch.Groups["url"].Value;

        var genMatch = Regex.Match(readerHtml, @"['""](?<url>https?://[^'""]*?\.(?:hath\.network|ehgt\.org)[^'""]*?\.(?:jpg|png|jpeg|webp|gif|bmp)[^'""]*)['""]", RegexOptions.IgnoreCase);
        if (genMatch.Success) return genMatch.Groups["url"].Value;

        return null;
    }

    private static string? ExtractEHentaiNlParam(string readerHtml)
    {
        if (string.IsNullOrWhiteSpace(readerHtml)) return null;
        var match = Regex.Match(readerHtml, @"nl\(['""](?<nl>[^'""]+)['""]\)", RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups["nl"].Value;
        return null;
    }

    private async Task<ComicBookItem> ScrapeHitomiBookAsync(string url, int index, string domain, CancellationToken ct)
    {
        url = DomainRoutingService.NormalizeUrl(url);
        string id = HitomiResolverService.ExtractGalleryId(url);

        var item = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = domain,
            Status = "Extracting...",
            StatusMessage = "Fetching gallery metadata..."
        };

        if (string.IsNullOrWhiteSpace(id))
        {
            item.Title = ExtractFallbackTitleFromUrl(url);
            item.Status = "Error";
            item.StatusMessage = "Không thể trích xuất ID gallery từ URL";
            return item;
        }

        try
        {
            await HitomiResolverService.RefreshGGAsync(_httpClient, ct).ConfigureAwait(false);
            using var doc = await HitomiResolverService.FetchGalleryInfoDocAsync(id, _httpClient, ct).ConfigureAwait(false);

            if (doc == null)
            {
                item.Title = ExtractFallbackTitleFromUrl(url);
                item.Status = "Error";
                item.StatusMessage = "Không thể tải metadata galleryinfo từ server";
                return item;
            }

            var root = doc.RootElement;
            string title = string.Empty;
            if (root.TryGetProperty("title", out var tProp) && tProp.ValueKind == JsonValueKind.String)
            {
                title = tProp.GetString() ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(title) && root.TryGetProperty("japanese_title", out var jtProp) && jtProp.ValueKind == JsonValueKind.String)
            {
                title = jtProp.GetString() ?? string.Empty;
            }

            string artist = string.Empty;
            if (root.TryGetProperty("artists", out var artProp) && artProp.ValueKind == JsonValueKind.Array && artProp.GetArrayLength() > 0)
            {
                var firstArt = artProp[0];
                if (firstArt.TryGetProperty("artist", out var aNameProp) && aNameProp.ValueKind == JsonValueKind.String)
                {
                    artist = aNameProp.GetString() ?? string.Empty;
                }
            }

            string displayName = string.IsNullOrWhiteSpace(artist) ? title : $"[{artist}] {title}";

            string language = string.Empty;
            if (root.TryGetProperty("language_localname", out var langProp) && langProp.ValueKind == JsonValueKind.String)
            {
                string rawLang = langProp.GetString() ?? string.Empty;
                if (rawLang == "中文") language = "Chinese";
                else if (rawLang == "日本語") language = "Japanese";
                else if (rawLang == "한국어") language = "Korean";
                else if (rawLang == "Español") language = "Spanish";
                else language = rawLang;
            }
            if (!string.IsNullOrWhiteSpace(language))
            {
                string langSuffix = $"[{language}]";
                if (!displayName.EndsWith(langSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    displayName = $"{displayName} {langSuffix}";
                }
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = ExtractFallbackTitleFromUrl(url);
            }

            int totalPages = 0;
            string coverUrl = string.Empty;
            if (root.TryGetProperty("files", out var filesProp) && filesProp.ValueKind == JsonValueKind.Array)
            {
                totalPages = filesProp.GetArrayLength();
                if (totalPages > 0)
                {
                    var firstFile = filesProp[0];
                    string firstHash = firstFile.TryGetProperty("hash", out var hProp) ? (hProp.GetString() ?? string.Empty) : string.Empty;
                    string firstName = firstFile.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? string.Empty) : string.Empty;
                    if (!string.IsNullOrWhiteSpace(firstHash))
                    {
                        coverUrl = HitomiResolverService.ResolveImageUrl(firstHash, firstName, isThumbnail: true);
                    }
                }
            }

            var hitomiMeta = ExtractHitomiMetadata(root, url);
            if (totalPages <= 0 && hitomiMeta.TotalPages > 0)
            {
                totalPages = hitomiMeta.TotalPages;
            }
            item.Title = displayName;
            item.CoverUrl = coverUrl;
            item.PreviewType = hitomiMeta.Type;
            item.PreviewLanguage = hitomiMeta.Language;
            item.PreviewArtist = hitomiMeta.Artist;
            item.PreviewParody = hitomiMeta.Parody;
            item.PreviewTagsText = hitomiMeta.Tags;
            item.TotalChapters = 1;
            item.TotalPages = totalPages;
            item.LatestChapter = totalPages > 0 ? $"{totalPages} Pages" : "Gallery";
            item.Status = "Ready";
            item.StatusMessage = totalPages > 0 ? $"Extracted {totalPages} pages" : "Ready";

            item.Chapters.Clear();
            item.Chapters.Add(new ChapterItem
            {
                ChapterNumber = 1,
                Title = totalPages > 0 ? $"{totalPages} Pages" : "Full Gallery",
                Url = url,
                TotalPages = totalPages,
                Status = "Waiting"
            });

            return item;
        }
        catch (Exception ex)
        {
            item.Title = ExtractFallbackTitleFromUrl(url);
            item.Status = "Error";
            item.StatusMessage = $"Lỗi: {ex.Message}";
            return item;
        }
    }

    private async Task<List<string>> ExtractHitomiChapterImagesAsync(string chapterUrl, CancellationToken ct)
    {
        var imageUrls = new List<string>();
        string id = HitomiResolverService.ExtractGalleryId(chapterUrl);
        if (string.IsNullOrWhiteSpace(id)) return imageUrls;

        try
        {
            await HitomiResolverService.RefreshGGAsync(_httpClient, ct).ConfigureAwait(false);
            using var doc = await HitomiResolverService.FetchGalleryInfoDocAsync(id, _httpClient, ct).ConfigureAwait(false);
            if (doc == null) return imageUrls;

            var root = doc.RootElement;
            if (root.TryGetProperty("files", out var filesProp) && filesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var fileEl in filesProp.EnumerateArray())
                {
                    string hash = fileEl.TryGetProperty("hash", out var hProp) ? (hProp.GetString() ?? string.Empty) : string.Empty;
                    string name = fileEl.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? string.Empty) : string.Empty;
                    if (string.IsNullOrWhiteSpace(hash)) continue;

                    string directUrl = HitomiResolverService.ResolveImageUrl(hash, name, "webp", false);
                    if (!string.IsNullOrWhiteSpace(directUrl))
                    {
                        imageUrls.Add(directUrl);
                    }
                }
            }
        }
        catch
        {
            // Bỏ qua lỗi giải mã ảnh
        }

        return imageUrls;
    }

    private async Task<ComicBookItem> ScrapeHentaiforceBookAsync(string url, int index, string domain, CancellationToken ct)
    {
        url = DomainRoutingService.NormalizeUrl(url);
        var item = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = domain,
            Status = "Extracting...",
            StatusMessage = "Fetching gallery metadata..."
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Referer", "https://hentaiforce.net/");
            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                item.Title = ExtractFallbackTitleFromUrl(url);
                item.Status = "Error";
                item.StatusMessage = $"HTTP {(int)res.StatusCode}";
                return item;
            }

            string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            item.Title = ExtractTitle(html, url);

            // Cover
            var coverMatch = Regex.Match(html, @"<div[^>]+id=""gallery-main-cover""[^>]*>.*?<img[^>]+(?:src|data-src)=""(?<cover>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (coverMatch.Success)
            {
                item.CoverUrl = MakeAbsoluteUrl(coverMatch.Groups["cover"].Value, url);
            }
            else
            {
                item.CoverUrl = ExtractCoverUrl(html, url);
            }

            // Pages
            int totalPages = 1;
            var pagesMatch = Regex.Match(html, @"Pages:\s*(\d+)", RegexOptions.IgnoreCase);
            if (pagesMatch.Success)
            {
                totalPages = int.Parse(pagesMatch.Groups[1].Value);
            }
            else
            {
                var thumbMatches = Regex.Matches(html, @"href=""[^""]*?/view/\d+/(\d+)""", RegexOptions.IgnoreCase);
                foreach (Match m in thumbMatches)
                {
                    if (int.TryParse(m.Groups[1].Value, out int pageNum) && pageNum > totalPages)
                    {
                        totalPages = pageNum;
                    }
                }
            }

            item.TotalChapters = 1;
            item.LatestChapter = $"{totalPages} Pages";
            item.Status = "Ready";
            item.StatusMessage = $"Extracted {totalPages} pages";

            item.Chapters.Clear();
            item.Chapters.Add(new ChapterItem
            {
                ChapterNumber = 1,
                Title = "Full Gallery",
                Url = url,
                TotalPages = totalPages,
                Status = "Waiting"
            });

            return item;
        }
        catch (Exception ex)
        {
            item.Title = ExtractFallbackTitleFromUrl(url);
            item.Status = "Error";
            item.StatusMessage = ex.Message;
            return item;
        }
    }

    private async Task<List<string>> ExtractHentaiforceChapterImagesAsync(string galleryUrl, CancellationToken ct)
    {
        var imageUrls = new List<string>();
        if (string.IsNullOrWhiteSpace(galleryUrl)) return imageUrls;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, galleryUrl);
            req.Headers.Add("Referer", "https://hentaiforce.net/");
            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return imageUrls;

            string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Kiểm tra chế độ tải thủ công
            bool isManual = InstanceConfigService.Instance.IsManualDownloadMode;
            string manualMode = InstanceConfigService.Instance.ManualDownloadMode;

            // Nếu người dùng BẬT chế độ tải thủ công và chọn "Trực tiếp" (Direct):
            // "đối với một số web, ví dụ như hentaiforce chỉ có thumbnail, thì độ phân giải rất thấp nếu tải trực tiếp"
            if (isManual && string.Equals(manualMode, "Direct", StringComparison.OrdinalIgnoreCase))
            {
                var directThumbs = ExtractDirectImagesFromHtml(html, galleryUrl);
                if (directThumbs.Count > 0) return directThumbs;
            }

            // 1. Total pages
            int totalPages = 1;
            var pagesMatch = Regex.Match(html, @"Pages:\s*(\d+)", RegexOptions.IgnoreCase);
            if (pagesMatch.Success)
            {
                totalPages = int.Parse(pagesMatch.Groups[1].Value);
            }
            else
            {
                var thumbMatches = Regex.Matches(html, @"href=""[^""]*?/view/\d+/(\d+)""", RegexOptions.IgnoreCase);
                foreach (Match m in thumbMatches)
                {
                    if (int.TryParse(m.Groups[1].Value, out int p) && p > totalPages)
                    {
                        totalPages = p;
                    }
                }
            }

            // 2. Fast Path: Bỏ qua khi người dùng BẬT thủ công "Nhúng" (Embedded).
            // Khi chọn "Nhúng", BẮT BUỘC bỏ qua Fast Path để cào từng trang reader nhúng (/view/id/page) lấy ảnh gốc độ phân giải cao nhất!
            if (!isManual || !string.Equals(manualMode, "Embedded", StringComparison.OrdinalIgnoreCase))
            {
                string prefix = string.Empty;
                string ext = "jpg";
                var patternMatch = Regex.Match(html, @"(?<prefix>https?://[a-zA-Z0-9.-]+/img/\d+-)1t\.(?<ext>[a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
                if (patternMatch.Success)
                {
                    prefix = patternMatch.Groups["prefix"].Value;
                    ext = patternMatch.Groups["ext"].Value;
                }
                else
                {
                    var generalMatch = Regex.Match(html, @"(https?://[a-zA-Z0-9.-]+/img/\d+-)\d+t\.(jpg|png|jpeg|webp)", RegexOptions.IgnoreCase);
                    if (generalMatch.Success)
                    {
                        prefix = generalMatch.Groups[1].Value;
                        ext = generalMatch.Groups[2].Value;
                    }
                }

                if (!string.IsNullOrEmpty(prefix))
                {
                    for (int p = 1; p <= totalPages; p++)
                    {
                        imageUrls.Add($"{prefix}{p}.{ext}");
                    }
                    return imageUrls;
                }
            }

            // 3. Fallback Slow Path / Chế độ Nhúng (Embedded): Fetch reader pages in parallel để lấy ảnh gốc độ phân giải cao
            string cleanGalleryUrl = galleryUrl.TrimEnd('/');
            var pageImageDict = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
            int readerConcurrency = Math.Clamp(MaxCpuThreads, 1, OperatingSystem.IsAndroid() ? 6 : 16);
            using var sem = new SemaphoreSlim(readerConcurrency, readerConcurrency);

            var tasks = Enumerable.Range(1, totalPages).Select(async pageNum =>
            {
                await sem.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    string pageUrl = $"{cleanGalleryUrl}/{pageNum}";
                    using var pReq = new HttpRequestMessage(HttpMethod.Get, pageUrl);
                    pReq.Headers.Add("Referer", cleanGalleryUrl);
                    using var pRes = await _httpClient.SendAsync(pReq, ct).ConfigureAwait(false);
                    if (pRes.IsSuccessStatusCode)
                    {
                        string pHtml = await pRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        var imgMatch = Regex.Match(pHtml, @"<img[^>]+class\s*=\s*""[^""]*\bjs-main-img\b[^""]*""[^>]+(?:src|data-src)\s*=\s*""(?<url>https?://[^""]+)""", RegexOptions.IgnoreCase);
                        if (!imgMatch.Success)
                        {
                            imgMatch = Regex.Match(pHtml, @"<img[^>]+(?:src|data-src)\s*=\s*""(?<url>https?://[^""]+)""[^>]+class\s*=\s*""[^""]*\bjs-main-img\b[^""]*""", RegexOptions.IgnoreCase);
                        }
                        if (imgMatch.Success)
                        {
                            pageImageDict[pageNum] = imgMatch.Groups["url"].Value;
                        }
                    }
                }
                catch {}
                finally
                {
                    sem.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            for (int p = 1; p <= totalPages; p++)
            {
                if (pageImageDict.TryGetValue(p, out string? imgUrl) && !string.IsNullOrWhiteSpace(imgUrl))
                {
                    imageUrls.Add(imgUrl);
                }
            }
        }
        catch {}

        return imageUrls;
    }

    public List<string> ExtractEmbeddedImagesFromHtml(string html, string pageUrl)
    {
        var rawImages = ExtractDirectImagesFromHtml(html, pageUrl);
        if (rawImages.Count == 0) return rawImages;

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Kiểm tra xem danh sách có các trang truyện được đánh số hay không (ví dụ 1t.webp, 2t.webp...)
        bool hasNumberedPages = rawImages.Any(u => Regex.IsMatch(u, @"(?:^|/|\b)\d+(?:t)?\.(?:webp|jpg|jpeg|png|avif|gif)", RegexOptions.IgnoreCase) || u.Contains(".315x0.") || u.Contains(".640x0.") || u.Contains(".1280x0."));

        foreach (var img in rawImages)
        {
            if (IsIgnoredImage(img) || img.Contains("/avatars/") || img.Contains("avatars-cdn") || img.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) continue;
            if (pageUrl.Contains("luscious.net", StringComparison.OrdinalIgnoreCase))
            {
                var albMatch = Regex.Match(pageUrl, @"_(\d+)/?", RegexOptions.IgnoreCase);
                if (albMatch.Success && !img.Contains($"/{albMatch.Groups[1].Value}/")) continue;
            }
            // Nếu có các trang truyện được đánh số, lọc bỏ các ảnh bìa cover/thumb rác trùng lặp
            if (hasNumberedPages)
            {
                try
                {
                    string fileName = Path.GetFileName(new Uri(img).AbsolutePath).ToLowerInvariant();
                    if (fileName.StartsWith("cover.") || fileName.StartsWith("thumb.") || fileName.Contains("avatar") || fileName.Contains("banner"))
                    {
                        continue;
                    }
                }
                catch { }
            }

            // Nâng cấp lên Full Resolution URL cho chế độ Nhúng
            string upgraded = UpgradeToFullResolutionUrl(img);
            if (seen.Add(upgraded))
            {
                result.Add(upgraded);
            }
        }

        return result.Count > 0 ? result : rawImages;
    }

    public static string UpgradeToFullResolutionUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;

        // 1. luscious.net: đổi .315x0. hoặc .640x0. thành .1280x0. (ảnh gốc độ phân giải cao nhất)
        if (url.Contains(".315x0.") || url.Contains(".640x0."))
        {
            return Regex.Replace(url, @"\.(?:315x0|640x0)\.", ".1280x0.");
        }

        // 2. nhentai / hentaiforce / các web gallery đánh số trang có chữ 't' sau số:
        // ví dụ: /galleries/4256488/1t.webp -> /galleries/4256488/1.webp
        if (Regex.IsMatch(url, @"(?<=\d)t(?=\.(?:webp|jpg|jpeg|png|avif|gif))", RegexOptions.IgnoreCase))
        {
            return Regex.Replace(url, @"(?<=\d)t(?=\.(?:webp|jpg|jpeg|png|avif|gif))", "");
        }

        // 3. /thumbs/ hoặc /thumbnails/ -> /images/
        if (url.Contains("/thumbs/") || url.Contains("/thumbnails/"))
        {
            return Regex.Replace(url, @"/(?:thumbs|thumbnails)/", "/images/", RegexOptions.IgnoreCase);
        }

        // 4. Các hậu tố thumbnail: -thumb, _thumb, -small, _small, -preview, _preview, -150x, _300x
        if (Regex.IsMatch(url, @"[-_](?:thumb|small|preview|150x|300x)(?=\.(?:webp|jpg|jpeg|png|avif|gif))", RegexOptions.IgnoreCase))
        {
            return Regex.Replace(url, @"[-_](?:thumb|small|preview|150x|300x)(?=\.(?:webp|jpg|jpeg|png|avif|gif))", "");
        }

        // 5. Hitomi / E-Hentai thumbnail: _m.jpg -> .jpg
        if (Regex.IsMatch(url, @"_m(?=\.(?:webp|jpg|jpeg|png|avif|gif))", RegexOptions.IgnoreCase))
        {
            return Regex.Replace(url, @"_m(?=\.(?:webp|jpg|jpeg|png|avif|gif))", "");
        }

        return url;
    }

    public List<string> ExtractDirectImagesFromHtml(string html, string pageUrl)
    {
        var images = new List<string>();
        if (string.IsNullOrWhiteSpace(html)) return images;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imgTags = Regex.Matches(html, @"<(?:img|source)\s+[^>]*>", RegexOptions.IgnoreCase);
        foreach (Match m in imgTags)
        {
            string tag = m.Value;
            string? imgUrl = ExtractImageUrlFromTag(tag);
            if (string.IsNullOrWhiteSpace(imgUrl) || IsIgnoredImage(imgUrl)) continue;

            string fullUrl = MakeAbsoluteUrl(imgUrl.Trim(), pageUrl);
            if (seen.Add(fullUrl))
            {
                images.Add(fullUrl);
            }
        }
        return images;
    }

    private async Task<List<string>> ExtractEHentaiDirectThumbnailsAsync(string galleryUrl, CancellationToken ct)
    {
        var imageUrls = new List<string>();
        string firstHtml = await FetchEHentaiHtmlAsync(galleryUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(firstHtml)) return imageUrls;
        return ExtractDirectImagesFromHtml(firstHtml, galleryUrl);
    }

    private async Task<ComicBookItem> ScrapeComixtoBookAsync(string url, int index, string domain, CancellationToken ct)
    {
        var book = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = domain,
            Status = "Waiting",
            StatusMessage = "Sẵn sàng tải"
        };

        // Chuẩn hóa và làm sạch URL: loại bỏ query string (?page=5, ?mode=...) và fragment (#...)
        string cleanUrl = Regex.Replace(url.Trim(), @"[?#].*$", "").TrimEnd('/');

        // Nếu là URL chapter đơn lẻ: https://comix.to/title/e09mx-lord/11520890-chapter-62
        var singleChapMatch = Regex.Match(cleanUrl, @"/title/[^/]+/(?<cid>\d+)(?:-[^/]+)?/?$", RegexOptions.IgnoreCase);
        if (singleChapMatch.Success)
        {
            string cid = singleChapMatch.Groups["cid"].Value;
            var images = await ExtractComixtoChapterImagesAsync(url, ct).ConfigureAwait(false);
            book.Title = "Comix Chapter " + cid;
            double num = double.TryParse(cid, out double parsedCid) ? parsedCid : 1.0;
            book.Chapters = new List<ChapterItem>
            {
                new ChapterItem
                {
                    ChapterNumber = num,
                    Title = "Chapter " + cid,
                    Url = url,
                    Status = "Waiting"
                }
            };
            book.TotalChapters = 1;
            book.Status = "Ready";
            book.StatusMessage = $"Đã tải thông tin chapter ({images.Count} ảnh)";
            return book;
        }

        // Trích xuất hid: URL có dạng https://comix.to/title/e09mx-lord/ hoặc https://comix.to/title/e09mx
        var hidMatch = Regex.Match(cleanUrl, @"/title/(?<hid>[a-zA-Z0-9]+)(?:-[^/?#]+)?", RegexOptions.IgnoreCase);
        string hid = hidMatch.Success ? hidMatch.Groups["hid"].Value : string.Empty;

        // Fetch HTML để lấy title, cover từ initial-data và lưu session cookie tự động
        string? html = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var dev = CurrentComixDevice;
            req.Headers.TryAddWithoutValidation("User-Agent", dev.UserAgent);
            if (!string.IsNullOrEmpty(dev.SecChUa))
            {
                req.Headers.TryAddWithoutValidation("Sec-Ch-Ua", dev.SecChUa);
                req.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Mobile", dev.Mobile);
                req.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Platform", dev.Platform);
            }
            HttpClient currentClient;
            lock (_comixClientLock) currentClient = _comixHttpClient;
            using var res = await currentClient.SendAsync(req, ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
        }
        catch { }


        if (!string.IsNullOrWhiteSpace(html))
        {
            var initMatch = Regex.Match(html, @"<script[^>]*id=[""']initial-data[""'][^>]*>(?<json>[\s\S]*?)</script>", RegexOptions.IgnoreCase);
            if (initMatch.Success)
            {
                try
                {
                    using var doc = JsonDocument.Parse(initMatch.Groups["json"].Value);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("comic", out var comicElem))
                    {
                        if (comicElem.TryGetProperty("title", out var titleProp)) book.Title = titleProp.GetString() ?? string.Empty;
                        if (comicElem.TryGetProperty("poster", out var posterProp) && posterProp.TryGetProperty("url", out var posterUrlProp))
                        {
                            book.CoverUrl = posterUrlProp.GetString() ?? string.Empty;
                        }
                        if (comicElem.TryGetProperty("hid", out var hidProp) && string.IsNullOrEmpty(hid))
                        {
                            hid = hidProp.GetString() ?? string.Empty;
                        }
                    }
                    if (root.TryGetProperty("manga", out var mangaElem))
                    {
                        if (string.IsNullOrWhiteSpace(book.Title) && mangaElem.TryGetProperty("title", out var mtp)) book.Title = mtp.GetString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(book.CoverUrl) && mangaElem.TryGetProperty("poster", out var mpp) && mpp.TryGetProperty("url", out var mpu)) book.CoverUrl = mpu.GetString() ?? string.Empty;
                        if (string.IsNullOrEmpty(hid) && mangaElem.TryGetProperty("hid", out var mhp)) hid = mhp.GetString() ?? string.Empty;
                    }
                    if (root.TryGetProperty("queries", out var queriesElem))
                    {
                        string qJson = queriesElem.GetRawText();
                        if (string.IsNullOrWhiteSpace(book.Title))
                        {
                            var tMatch = Regex.Match(qJson, @"""title""\s*:\s*""(?<t>(?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase);
                            if (tMatch.Success) book.Title = Regex.Unescape(tMatch.Groups["t"].Value);
                        }
                        if (string.IsNullOrWhiteSpace(book.CoverUrl))
                        {
                            var pMatch = Regex.Match(qJson, @"""poster""\s*:\s*\{[^}]*""url""\s*:\s*""(?<u>https?://[^""]+)""", RegexOptions.IgnoreCase);
                            if (pMatch.Success) book.CoverUrl = pMatch.Groups["u"].Value;
                        }
                    }
                }
                catch { }
            }
        }

        if (string.IsNullOrWhiteSpace(book.Title))
        {
            var titleMatch = Regex.Match(url, @"/title/(?:[a-zA-Z0-9]+-)?(?<name>[^/]+)/?$", RegexOptions.IgnoreCase);
            book.Title = titleMatch.Success ? CleanTitle(titleMatch.Groups["name"].Value.Replace('-', ' ')) : "Comix Manga";
        }

        if (string.IsNullOrWhiteSpace(hid))
        {
            book.Status = "Error";
            book.StatusMessage = "Không xác định được mã truyện comix";
            return book;
        }

        // Gọi API lấy toàn bộ danh sách chapter: /api/v1/manga/{hid}/chapters?limit=100&order[number]=desc&page={p}&v=0&w=0
        var rawChapterItems = new List<JsonElement>();
        int page = 1;
        int blockedRetries = 0;
        while (!ct.IsCancellationRequested)
        {
            await ComixRateLimiter.ThrottleAsync(ct).ConfigureAwait(false);
            string apiPath = $"/manga/{hid}/chapters?limit=100&order[number]=asc&page={page}&v=0&w=0";
            string token = ComixCryptoService.GenerateToken(apiPath);
            string apiUrl = $"https://comix.to/api/v1{apiPath}&_={token}";

            try
            {
                using var apiReq = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                var dev = CurrentComixDevice;
                apiReq.Headers.TryAddWithoutValidation("User-Agent", dev.UserAgent);
                if (!string.IsNullOrEmpty(dev.SecChUa))
                {
                    apiReq.Headers.TryAddWithoutValidation("Sec-Ch-Ua", dev.SecChUa);
                    apiReq.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Mobile", dev.Mobile);
                    apiReq.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Platform", dev.Platform);
                }
                apiReq.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                apiReq.Headers.TryAddWithoutValidation("Referer", url);

                HttpClient currentClient;
                lock (_comixClientLock) currentClient = _comixHttpClient;

                using var apiRes = await currentClient.SendAsync(apiReq, ct).ConfigureAwait(false);
                string rawJson = string.Empty;
                if (apiRes.IsSuccessStatusCode)
                {
                    rawJson = await apiRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }

                bool isBlocked = !apiRes.IsSuccessStatusCode ||
                                 apiRes.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                                 (int)apiRes.StatusCode == 429 ||
                                 rawJson.Contains("captcha_required") ||
                                 rawJson.Contains("@waf/challenge");

                if (isBlocked)
                {
                    if (++blockedRetries > 4)
                    {
                        if (rawChapterItems.Count == 0)
                        {
                            book.Status = "Error";
                            book.StatusMessage = "Comix.to yêu cầu xác minh Cloudflare Turnstile hoặc tạm thời bị chặn (403 Forbidden). Vui lòng kiểm tra kết nối.";
                        }
                        break;
                    }
                    // T ĐNG XÓA SCH COOKIE VÀ ĐI DANH TÍNH MÁY MI THEO TIÊU CHÍ YÊU CU
                    ResetComixSessionAsNewDevice();
                    await ComixRateLimiter.CoolDownAsync(2 * blockedRetries, ct).ConfigureAwait(false);
                    continue;
                }
                blockedRetries = 0;
                using var doc = JsonDocument.Parse(rawJson);
                if (!doc.RootElement.TryGetProperty("e", out var encProp)) break;

                string decJson = ComixCryptoService.DecryptPayload(encProp.GetString() ?? string.Empty);
                using var decDoc = JsonDocument.Parse(decJson);
                var result = decDoc.RootElement.GetProperty("result");
                var items = result.GetProperty("items");

                int itemCount = items.GetArrayLength();
                if (itemCount == 0) break;

                foreach (var item in items.EnumerateArray())
                {
                    rawChapterItems.Add(item.Clone());
                }

                int lastPage = 1;
                bool hasLastPage = false;

                if (result.TryGetProperty("meta", out var metaProp))
                {
                    if (metaProp.TryGetProperty("last_page", out var lp))
                    {
                        lastPage = lp.GetInt32();
                        hasLastPage = true;
                    }
                    else if (metaProp.TryGetProperty("total_pages", out var tp))
                    {
                        lastPage = tp.GetInt32();
                        hasLastPage = true;
                    }
                    else if (metaProp.TryGetProperty("total", out var tot) && metaProp.TryGetProperty("limit", out var lim))
                    {
                        int totalItems = tot.GetInt32();
                        int limitVal = lim.GetInt32();
                        if (limitVal > 0)
                        {
                            lastPage = (int)Math.Ceiling((double)totalItems / limitVal);
                            hasLastPage = true;
                        }
                    }
                }
                else if (result.TryGetProperty("pagination", out var pagProp))
                {
                    if (pagProp.TryGetProperty("last_page", out var lp))
                    {
                        lastPage = lp.GetInt32();
                        hasLastPage = true;
                    }
                    else if (pagProp.TryGetProperty("total_pages", out var tp))
                    {
                        lastPage = tp.GetInt32();
                        hasLastPage = true;
                    }
                }

                if (hasLastPage)
                {
                    if (page >= lastPage) break;
                }
                else if (itemCount < 100)
                {
                    break;
                }

                page++;
            }
            catch
            {
                break;
            }
        }

        // Gom nhóm theo số chapter để loại bỏ hoàn toàn các bản dịch trùng lặp giữa các nhóm dịch
        static double ParseChapterNum(JsonElement elem)
        {
            if (elem.TryGetProperty("number", out var numProp))
            {
                string s = numProp.ToString();
                if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                    return parsed;
            }
            return -1.0;
        }

        static int GetVotes(JsonElement elem)
        {
            return elem.TryGetProperty("votes", out var v) && v.TryGetInt32(out int val) ? val : 0;
        }

        static long GetId(JsonElement elem)
        {
            return elem.TryGetProperty("id", out var id) && id.TryGetInt64(out long val) ? val : 0;
        }

        static bool IsOfficial(JsonElement elem)
        {
            return elem.TryGetProperty("isOfficial", out var off) && off.GetBoolean();
        }

        static string GetLang(JsonElement elem)
        {
            return elem.TryGetProperty("language", out var l) ? l.GetString() ?? string.Empty : string.Empty;
        }

        var groupedChapters = rawChapterItems
            .GroupBy(ParseChapterNum)
            .ToList();

        var dedupedChapters = new List<ChapterItem>();
        string prefLang = book.PreferredLanguage ?? string.Empty;

        foreach (var group in groupedChapters)
        {
            if (group.Key < 0)
            {
                // Các chapter đặc biệt / oneshot không có số
                foreach (var specialItem in group)
                {
                    string specName = specialItem.TryGetProperty("name", out var snProp) ? snProp.GetString() ?? string.Empty : string.Empty;
                    string specUrl = specialItem.TryGetProperty("url", out var suProp) ? suProp.GetString() ?? string.Empty : string.Empty;
                    if (!specUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        specUrl = "https://comix.to" + specUrl;
                    }

                    dedupedChapters.Add(new ChapterItem
                    {
                        ChapterNumber = -1,
                        Title = !string.IsNullOrWhiteSpace(specName) ? specName : "Special Chapter",
                        Url = specUrl,
                        Status = "Waiting"
                    });
                }
                continue;
            }

            // Đánh giá chất lượng tiêu đề: Ưu tiên tải bản có tiêu đề chương thực sự thay vì chỉ là chap 1, 2, 3...
            static int GetTitleScore(JsonElement elem)
            {
                string name = string.Empty;
                if (elem.TryGetProperty("name", out var nProp))
                {
                    name = nProp.GetString()?.Trim() ?? string.Empty;
                }
                else if (elem.TryGetProperty("title", out var tProp))
                {
                    name = tProp.GetString()?.Trim() ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    return 0; // Không có tiêu đề
                }

                // Loại bỏ tiền tố Chapter / Ch. / # / số để kiểm tra xem có phần chữ nội dung thực sự không
                string clean = Regex.Replace(name, @"^(?:chapter|chap|ch\.?|tập|chương)\s*#?\d+(?:[.,]\d+)?\s*[:\-_.]*\s*", "", RegexOptions.IgnoreCase).Trim();
                clean = Regex.Replace(clean, @"^#\d+\s*[:\-_.]*\s*", "").Trim();
                clean = clean.Trim('-', ' ', ':', '_');

                if (string.IsNullOrWhiteSpace(clean) || double.TryParse(clean, out _))
                {
                    return 1; // Chỉ chứa số hoặc tiền tố chapter, không có tiêu đề thực sự
                }

                // Tiêu đề thực sự giàu ý nghĩa (ví dụ: "Egen Guy", "Is Changing Jobs the Answer?")
                return 1000 + Math.Min(clean.Length, 100);
            }

            // Chuẩn hóa tên chapter hiển thị: tránh lặp từ "Chapter 1: Ch. 1 - Egen Guy"
            static string FormatComixChapterTitle(string chapNumberStr, string rawName)
            {
                if (string.IsNullOrWhiteSpace(rawName))
                {
                    return $"Chapter {chapNumberStr}";
                }

                string t = rawName.Trim();
                string clean = Regex.Replace(t, @"^(?:chapter|chap|ch\.?|tập|chương)\s*#?\d+(?:[.,]\d+)?\s*[:\-_.]*\s*", "", RegexOptions.IgnoreCase).Trim();
                clean = Regex.Replace(clean, @"^#\d+\s*[:\-_.]*\s*", "").Trim();
                clean = clean.Trim('-', ' ', ':', '_');

                if (!string.IsNullOrWhiteSpace(clean) && !double.TryParse(clean, out _))
                {
                    return $"Chapter {chapNumberStr}: {clean}";
                }

                return $"Chapter {chapNumberStr}";
            }

            // Chọn 1 bản dịch tốt nhất cho số chapter này:
            // 1. Ưu tiên ngôn ngữ ưa thích (PreferredLanguage) nếu có
            // 2. BẮT BUỘC ƯU TIÊN BẢN CÓ TIÊU ĐỀ THỰC SỰ (thay vì chỉ đơn thuần là chap 1, 2, 3...)
            // 3. Ưu tiên isOfficial == true
            // 4. Ưu tiên số lượt votes cao nhất (được cộng đồng đánh giá nhiều nhất)
            // 5. Ưu tiên id mới nhất
            var bestItem = group
                .OrderByDescending(item => !string.IsNullOrWhiteSpace(prefLang) && GetLang(item).Equals(prefLang, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(GetTitleScore)
                .ThenByDescending(IsOfficial)
                .ThenByDescending(GetVotes)
                .ThenByDescending(GetId)
                .First();

            string chapTitleName = bestItem.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? string.Empty : string.Empty;
            string chapNumberStr = bestItem.TryGetProperty("number", out var numProp) ? numProp.ToString() : group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string finalChapUrl = bestItem.TryGetProperty("url", out var uProp) ? uProp.GetString() ?? string.Empty : string.Empty;

            if (!finalChapUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                finalChapUrl = "https://comix.to" + finalChapUrl;
            }

            string displayTitle = FormatComixChapterTitle(chapNumberStr, chapTitleName);

            dedupedChapters.Add(new ChapterItem
            {
                ChapterNumber = group.Key,
                Title = displayTitle,
                Url = finalChapUrl,
                Status = "Waiting"
            });
        }

        // Sắp xếp theo thứ tự số chapter tăng dần (1, 2, 3...)
        dedupedChapters = dedupedChapters
            .OrderBy(c => c.ChapterNumber >= 0 ? c.ChapterNumber : double.MaxValue)
            .ToList();

        book.Chapters = dedupedChapters;
        book.TotalChapters = dedupedChapters.Count;
        if (dedupedChapters.Count > 0)
        {
            book.Status = "Ready";
            book.StatusMessage = $"Tìm thấy {dedupedChapters.Count} chapter";
        }
        else if (book.Status != "Error")
        {
            book.Status = "Ready";
            book.StatusMessage = "Không tìm thấy chapter nào";
        }
        return book;
    }

    private async Task<List<string>> ExtractComixtoChapterImagesAsync(string chapterUrl, CancellationToken ct)
    {
        var images = new List<string>();
        if (string.IsNullOrWhiteSpace(chapterUrl)) return images;

        // Trích xuất chapter id từ URL: /title/.../11520890-chapter-62 hoặc /title/[^/]+/(\d+)
        var chapMatch = Regex.Match(chapterUrl, @"/(?<cid>\d+)(?:-[^/]+)?/?$", RegexOptions.IgnoreCase);
        if (!chapMatch.Success)
        {
            chapMatch = Regex.Match(chapterUrl, @"/(?<cid>\d+)-", RegexOptions.IgnoreCase);
        }
        if (!chapMatch.Success) return images;

        string chapterId = chapMatch.Groups["cid"].Value;
        string apiPath = $"/chapters/{chapterId}?v=0&w=0";
        string token = ComixCryptoService.GenerateToken(apiPath);
        string apiUrl = $"https://comix.to/api/v1{apiPath}&_={token}";

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await ComixRateLimiter.ThrottleAsync(ct).ConfigureAwait(false);

                HttpClient currentClient;
                lock (_comixClientLock) currentClient = _comixHttpClient;

                // Nạp session cookies comix.to (warm-up) nếu cookie container rỗng hoặc sau khi bị chặn
                var testUri = new Uri("https://comix.to");
                if (_comixCookieContainer.GetCookies(testUri).Count == 0 || attempt > 1)
                {
                    try
                    {
                        using var warmReq = new HttpRequestMessage(HttpMethod.Get, chapterUrl);
                        var devWarm = CurrentComixDevice;
                        warmReq.Headers.TryAddWithoutValidation("User-Agent", devWarm.UserAgent);
                        warmReq.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
                        warmReq.Headers.TryAddWithoutValidation("Accept-Language", "vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");
                        if (!string.IsNullOrEmpty(devWarm.SecChUa))
                        {
                            warmReq.Headers.TryAddWithoutValidation("Sec-Ch-Ua", devWarm.SecChUa);
                            warmReq.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Mobile", devWarm.Mobile);
                            warmReq.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Platform", devWarm.Platform);
                        }
                        using var warmRes = await currentClient.SendAsync(warmReq, ct).ConfigureAwait(false);
                    }
                    catch { }
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                var dev = CurrentComixDevice;
                req.Headers.TryAddWithoutValidation("User-Agent", dev.UserAgent);
                req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                req.Headers.TryAddWithoutValidation("Accept-Language", "vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");
                if (!string.IsNullOrEmpty(dev.SecChUa))
                {
                    req.Headers.TryAddWithoutValidation("Sec-Ch-Ua", dev.SecChUa);
                    req.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Mobile", dev.Mobile);
                    req.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Platform", dev.Platform);
                }
                req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                req.Headers.TryAddWithoutValidation("Referer", chapterUrl);

                using var res = await currentClient.SendAsync(req, ct).ConfigureAwait(false);
                string rawJson = string.Empty;
                if (res.IsSuccessStatusCode)
                {
                    rawJson = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }

                bool isBlocked = !res.IsSuccessStatusCode ||
                                 res.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                                 (int)res.StatusCode == 429 ||
                                 rawJson.Contains("captcha_required") ||
                                 rawJson.Contains("@waf/challenge");

                if (isBlocked)
                {
                    if (attempt == 3)
                    {
                        ResetComixSessionAsNewDevice();
                    }
                    int coolDownSec = attempt * 2;
                    await ComixRateLimiter.CoolDownAsync(coolDownSec, ct).ConfigureAwait(false);
                    continue;
                }

                if (!string.IsNullOrEmpty(rawJson))
                {
                    using var doc = JsonDocument.Parse(rawJson);
                    if (doc.RootElement.TryGetProperty("e", out var encProp))
                    {
                        string decJson = ComixCryptoService.DecryptPayload(encProp.GetString() ?? string.Empty);
                        using var decDoc = JsonDocument.Parse(decJson);
                        var result = decDoc.RootElement.GetProperty("result");
                        if (result.TryGetProperty("pages", out var pagesObj))
                        {
                            string baseUrl = string.Empty;
                            if (pagesObj.TryGetProperty("baseUrl", out var baseProp))
                            {
                                baseUrl = baseProp.GetString() ?? string.Empty;
                            }

                            JsonElement itemsElement = default;
                            if (pagesObj.ValueKind == JsonValueKind.Array)
                            {
                                itemsElement = pagesObj;
                            }
                            else if (pagesObj.TryGetProperty("items", out var foundItems) && foundItems.ValueKind == JsonValueKind.Array)
                            {
                                itemsElement = foundItems;
                            }

                            if (itemsElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in itemsElement.EnumerateArray())
                                {
                                    if (item.TryGetProperty("url", out var urlProp))
                                    {
                                        string imgUrl = urlProp.GetString() ?? string.Empty;
                                        if (!string.IsNullOrWhiteSpace(imgUrl))
                                        {
                                            if (!imgUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(baseUrl))
                                            {
                                                imgUrl = baseUrl.TrimEnd('/') + "/" + imgUrl.TrimStart('/');
                                            }
                                            images.Add(imgUrl);
                                        }
                                    }
                                }
                            }
                        }
                        if (images.Count > 0) break;
                    }
                }
            }
            catch { }

            if (attempt < 3 && images.Count == 0)
            {
                await Task.Delay(400 * attempt, ct).ConfigureAwait(false);
            }
        }

        // Tầng phòng vệ thép thứ 2 (Defense in Depth): Nếu API trực tiếp bị chặn hoặc không có ảnh,
        // sử dụng NativeWebViewService (trên Windows / desktop) để nạp trang và bóc tách ảnh từ DOM
        if (images.Count == 0 && NativeWebViewService.Instance.IsAvailable)
        {
            try
            {
                string? webViewHtml = await NativeWebViewService.Instance.FetchHtmlAsync(chapterUrl, 45, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(webViewHtml))
                {
                    SyncCookiesFromNativeWebView(chapterUrl);
                    var directImgs = ExtractDirectImagesFromHtml(webViewHtml, chapterUrl);
                    if (directImgs.Count > 0)
                    {
                        images.AddRange(directImgs);
                    }
                }
            }
            catch { }
        }

        return images;
    }


    private async Task<ComicBookItem> ScrapeMoetruyenBookAsync(string url, int index, string domain, CancellationToken ct)
    {
        url = DomainRoutingService.NormalizeUrl(url);
        var item = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = domain,
            Status = "Extracting..."
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Referer", "https://moetruyen.net/");
            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                item.Title = ExtractFallbackTitleFromUrl(url);
                item.Status = "Error";
                item.StatusMessage = $"Lỗi tải trang HTTP {(int)res.StatusCode}";
                return item;
            }

            string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // 1. Tiêu đề truyện
            string bookTitle = string.Empty;
            var ogTitleMatch = Regex.Match(html, @"<meta\s+property=[""']og:title[""']\s+content=[""'](?<t>[^""']+)[""']", RegexOptions.IgnoreCase);
            if (ogTitleMatch.Success)
            {
                bookTitle = WebUtility.HtmlDecode(ogTitleMatch.Groups["t"].Value.Trim());
                bookTitle = Regex.Replace(bookTitle, @"\s*\[Tới Chap \d+\]\s*$", "", RegexOptions.IgnoreCase);
            }
            if (string.IsNullOrWhiteSpace(bookTitle))
            {
                var docTitleMatch = Regex.Match(html, @"<title>(?<t>[^<]+)</title>", RegexOptions.IgnoreCase);
                if (docTitleMatch.Success)
                {
                    bookTitle = WebUtility.HtmlDecode(docTitleMatch.Groups["t"].Value.Trim());
                    bookTitle = Regex.Replace(bookTitle, @"\s*\[Tới Chap \d+\]\s*$", "", RegexOptions.IgnoreCase);
                    bookTitle = Regex.Replace(bookTitle, @"\s*[-|·]\s*Mòe Truyện.*$", "", RegexOptions.IgnoreCase);
                }
            }
            if (string.IsNullOrWhiteSpace(bookTitle))
            {
                bookTitle = ExtractFallbackTitleFromUrl(url);
            }
            item.Title = CleanTitle(bookTitle);

            // 2. Ảnh bìa (Cover)
            var ogImgMatch = Regex.Match(html, @"<meta\s+property=[""']og:image[""']\s+content=[""'](?<src>[^""']+)[""']", RegexOptions.IgnoreCase);
            if (ogImgMatch.Success)
            {
                item.CoverUrl = ogImgMatch.Groups["src"].Value.Trim();
            }

            // 3. Phân trang chapter (chapterPage=1..N)
            int maxChapterPage = 1;
            var pageMatches = Regex.Matches(html, @"chapterPage=(?<p>\d+)", RegexOptions.IgnoreCase);
            foreach (Match pm in pageMatches)
            {
                if (int.TryParse(pm.Groups["p"].Value, out int p) && p > maxChapterPage)
                {
                    maxChapterPage = p;
                }
            }

            // 4. Lấy toàn bộ chapter từ tất cả các trang chapterPage
            var allChapterRaw = new List<(string Url, string Text)>();
            var seenChapterUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string cleanBaseUrl = Regex.Replace(url, @"[?#].*$", "");

            for (int p = 1; p <= maxChapterPage; p++)
            {
                if (ct.IsCancellationRequested) break;

                string pageHtml = html;
                if (p > 1)
                {
                    string pUrl = $"{cleanBaseUrl}?chapterPage={p}#chapters";
                    using var pReq = new HttpRequestMessage(HttpMethod.Get, pUrl);
                    pReq.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                    pReq.Headers.TryAddWithoutValidation("Referer", url);
                    using var pRes = await _httpClient.SendAsync(pReq, ct).ConfigureAwait(false);
                    if (pRes.IsSuccessStatusCode)
                    {
                        pageHtml = await pRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    }
                    else
                    {
                        continue;
                    }
                }

                var chapMatches = Regex.Matches(pageHtml, @"<a[^>]+href=[""'](?<url>/manga/\d+-[^/]+/chapters/\d+)[""'][^>]*>(?<text>[\s\S]*?)</a>", RegexOptions.IgnoreCase);
                foreach (Match cm in chapMatches)
                {
                    string cHref = cm.Groups["url"].Value;
                    string cText = Regex.Replace(cm.Groups["text"].Value, @"<[^>]+>", " ").Trim();
                    cText = Regex.Replace(cText, @"\s+", " ");

                    if (cText.Contains("Đọc từ đầu", StringComparison.OrdinalIgnoreCase) ||
                        cText.Contains("Đọc mới nhất", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string fullChapUrl = MakeAbsoluteUrl(cHref, url);
                    if (seenChapterUrls.Add(fullChapUrl))
                    {
                        allChapterRaw.Add((fullChapUrl, cText));
                    }
                }
            }

            var parsedChapters = new List<(double ChapNum, ChapterItem Item)>();
            int fallbackOrder = 1;

            foreach (var (cUrl, cText) in allChapterRaw)
            {
                double chapNum = fallbackOrder;
                var numMatch = Regex.Match(cUrl, @"/chapters/(\d+)");
                if (numMatch.Success && double.TryParse(numMatch.Groups[1].Value, out double parsed))
                {
                    chapNum = parsed;
                }
                else
                {
                    var titleNumMatch = Regex.Match(cText, @"(?:Ch\.|Chap|Chapter)\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                    if (titleNumMatch.Success && double.TryParse(titleNumMatch.Groups[1].Value, out double tParsed))
                    {
                        chapNum = tParsed;
                    }
                }

                string displayTitle = !string.IsNullOrWhiteSpace(cText) ? cText : $"Chapter {chapNum}";
                var cleanTitleMatch = Regex.Match(displayTitle, @"^(?<main>Ch\.\s*\d+[^•\r\n]*)(?:•.*)?$", RegexOptions.IgnoreCase);
                if (cleanTitleMatch.Success && !string.IsNullOrWhiteSpace(cleanTitleMatch.Groups["main"].Value))
                {
                    displayTitle = cleanTitleMatch.Groups["main"].Value.Trim();
                }

                var chItem = new ChapterItem
                {
                    ChapterNumber = chapNum,
                    Title = displayTitle,
                    Url = cUrl,
                    TotalPages = 0,
                    DownloadedPages = 0,
                    Status = "Waiting"
                };

                parsedChapters.Add((chapNum, chItem));
                fallbackOrder++;
            }

            // Sắp xếp chapter từ số nhỏ đến số lớn (1 -> 222)
            parsedChapters.Sort((a, b) => a.ChapNum.CompareTo(b.ChapNum));

            for (int i = 0; i < parsedChapters.Count; i++)
            {
                var ch = parsedChapters[i].Item;
                item.Chapters.Add(ch);
            }

            item.TotalChapters = item.Chapters.Count;
            item.Status = item.TotalChapters > 0 ? "Ready" : "Empty";
            item.StatusMessage = $"Đã trích xuất {item.TotalChapters} chương";
        }
        catch (Exception ex)
        {
            item.Status = "Error";
            item.StatusMessage = $"Lỗi cào truyện: {ex.Message}";
        }

        return item;
    }

    private async Task<List<string>> ExtractMoetruyenChapterImagesAsync(string chapterUrl, CancellationToken ct)
    {
        try
        {
            var session = await MoeTruyenCryptoService.Instance.FetchChapterSessionAsync(chapterUrl, ct).ConfigureAwait(false);
            if (session.MediaItems != null && session.MediaItems.Count > 0)
            {
                return session.MediaItems.Select(m => $"https://i.truyen.moe/{m.storageKey}").ToList();
            }
        }
        catch { }
        return new List<string>();
    }

    private void SyncCookiesFromNativeWebView(string url)
    {
        try
        {
            string? cookieHeader = NativeWebViewService.Instance.GetCookies(url);
            if (string.IsNullOrWhiteSpace(cookieHeader)) return;

            var uri = new Uri(url);
            string[] pairs = cookieHeader.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                int eqIdx = pair.IndexOf('=');
                if (eqIdx > 0)
                {
                    string name = pair[..eqIdx].Trim();
                    string val = pair[(eqIdx + 1)..].Trim();
                    try
                    {
                        _cookieContainer.Add(uri, new Cookie(name, val) { Path = "/" });
                    }
                    catch { }
                    if (url.Contains("comix.", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            _comixCookieContainer.Add(uri, new Cookie(name, val) { Path = "/" });
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
    }
}
