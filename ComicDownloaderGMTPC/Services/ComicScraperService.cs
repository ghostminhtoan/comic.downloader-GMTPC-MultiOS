using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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

    public ComicScraperService()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "vi,en-US;q=0.9,en;q=0.8");
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

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

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

    public async Task<List<string>> ExtractChapterImageUrlsAsync(string chapterUrl, string domain, CancellationToken ct = default)
    {
        var images = new List<string>();
        if (string.IsNullOrWhiteSpace(chapterUrl)) return images;

        domain = domain.ToLowerInvariant();

        try
        {
            if (domain.Contains("mangadex"))
            {
                return await ExtractMangaDexChapterImagesAsync(chapterUrl, ct).ConfigureAwait(false);
            }

            if (domain.Contains("e-hentai") || domain.Contains("exhentai"))
            {
                return await ExtractEHentaiChapterImagesAsync(chapterUrl, ct).ConfigureAwait(false);
            }

            if (domain.Contains("hentaiforce"))
            {
                return await ExtractHentaiforceChapterImagesAsync(chapterUrl, ct).ConfigureAwait(false);
            }

            if (domain.Contains("hitomi"))
            {
                return await ExtractHitomiChapterImagesAsync(chapterUrl, ct).ConfigureAwait(false);
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, chapterUrl);
            string baseDomain = DomainRoutingService.NormalizeUrl(chapterUrl);
            req.Headers.Add("Referer", baseDomain);
            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return images;

            string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

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

            if (domain.Contains("damconuong"))
            {
                var dcnImages = ExtractDamconuongChapterImages(html, chapterUrl);
                if (dcnImages.Count > 0) return dcnImages;
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
                    var chapterMatches = Regex.Matches(json, @"\{(?<obj>[^{}]*""chapter_num""[^{}]*)\}", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (Match m in chapterMatches)
                    {
                        string obj = m.Groups["obj"].Value;
                        var numMatch = Regex.Match(obj, @"""chapter_num""\s*:\s*""?(?<num>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                        if (!numMatch.Success) continue;

                        string numStr = numMatch.Groups["num"].Value;
                        string chapUrl = $"{activeDomain}/truyen-tranh/{slug}/chuong-{numStr}".TrimEnd('/');
                        if (seen.Add(chapUrl))
                        {
                            var nameMatch = Regex.Match(obj, @"""chapter_name""\s*:\s*""(?<name>(?:\\.|[^""\\])*)""", RegexOptions.IgnoreCase);
                            string chapName = nameMatch.Success
                                ? WebUtility.HtmlDecode(Regex.Unescape(nameMatch.Groups["name"].Value)).Trim()
                                : $"Chapter {numStr}";

                            double.TryParse(numStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedNum);

                            item.Chapters.Add(new ChapterItem
                            {
                                ChapterNumber = parsedNum,
                                Title = string.IsNullOrWhiteSpace(chapName) ? $"Chapter {numStr}" : chapName,
                                Url = chapUrl,
                                Status = "Waiting"
                            });
                        }
                    }
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

        var uri = new Uri(url);
        string[] segs = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        string bookSlug = segs.Length > 1 ? segs[1] : (segs.Length > 0 ? segs[0] : string.Empty);

        string pattern = @"href\s*=\s*[""'](?<href>(?:(?:https?:)?\/\/(?:www\.)?damconuong\.[^\/""']+)?\/truyen\/" + Regex.Escape(bookSlug) + @"\/(?<chapter>[^""'?#>]+)(?:\.html)?)[""']";
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
            item.StatusMessage = $"HTTP {(int)res.StatusCode}";
            return item;
        }

        string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        item.Title = ExtractTitle(html, url);
        item.CoverUrl = ExtractCoverUrl(html, url);

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
            item.Title = WebUtility.HtmlDecode(titleMatch.Groups["title"].Value).Trim();
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

    private List<string> ExtractDamconuongChapterImages(string html, string chapterUrl)
    {
        var imageUrls = new List<string>();
        if (string.IsNullOrWhiteSpace(html)) return imageUrls;

        string contentHtml = string.Empty;

        var contentMatch = Regex.Match(html, @"<(?:div|article|section)[^>]*(?:id=[""']chapter-content[""']|class=[""'][^""']*(?:reading-detail|box_doc|chapter-content)[^""']*[""'])[^>]*>.*?</(?:div|article|section)>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (contentMatch.Success) contentHtml = contentMatch.Value;
        else contentHtml = html;

        var matches = Regex.Matches(contentHtml, @"<img[^>]+(?:data-src|data-original|data-lazy-src|src)\s*=\s*[""'](?<url>[^""']+)[""']", RegexOptions.IgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in matches)
        {
            string url = WebUtility.HtmlDecode(m.Groups["url"].Value.Trim()).Replace("\\/", "/");
            if (string.IsNullOrWhiteSpace(url) || url.StartsWith("data:") || IsIgnoredImage(url)) continue;
            string fullUrl = MakeAbsoluteUrl(url, chapterUrl);
            if (seen.Add(fullUrl)) imageUrls.Add(fullUrl);
        }

        return imageUrls;
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

        try
        {
            string apiUrl = $"https://api.mangadex.org/manga/{mangaId}?includes[]=cover_art";
            string json = await MangaDexNetworkService.Instance.GetJsonAsync(apiUrl, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");

            if (data.TryGetProperty("attributes", out var attr) && attr.TryGetProperty("title", out var titleObj))
            {
                foreach (var prop in titleObj.EnumerateObject())
                {
                    book.Title = prop.Value.GetString() ?? book.Title;
                    break;
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

        // Bóc tách chapter feed theo ngôn ngữ đã chọn (có phân trang offset và fallback)
        try
        {
            string primaryLang = string.IsNullOrWhiteSpace(lang) ? "vi" : lang.ToLowerInvariant();
            await FetchMangaDexFeedChaptersAsync(book, mangaId, primaryLang, ct).ConfigureAwait(false);

            if (book.Chapters.Count == 0 && useFallback)
            {
                string fallbackLang = primaryLang == "vi" ? "en" : "vi";
                await FetchMangaDexFeedChaptersAsync(book, mangaId, fallbackLang, ct).ConfigureAwait(false);
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

        if (book.Chapters.Count == 0)
        {
            book.Status = "Error";
            book.StatusMessage = $"Không tìm thấy chapter ngôn ngữ [{book.PreferredLanguage.ToUpperInvariant()}]. Vui lòng thử đổi ngôn ngữ.";
            return book;
        }

        book.TotalChapters = book.Chapters.Count;
        book.LatestChapter = book.Chapters[^1].Title;
        book.Status = "Ready";
        book.StatusMessage = $"Extracted {book.TotalChapters} chapters [{book.PreferredLanguage.ToUpperInvariant()}]";

        return book;
    }

    private async Task FetchMangaDexFeedChaptersAsync(ComicBookItem book, string mangaId, string targetLang, CancellationToken ct)
    {
        int offset = 0;
        const int limit = 100;
        var rawBatches = new List<JsonElement>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string feedUrl = $"https://api.mangadex.org/manga/{mangaId}/feed" +
                             $"?offset={offset}&limit={limit}" +
                             $"&translatedLanguage%5B%5D={Uri.EscapeDataString(targetLang)}" +
                             "&includes%5B%5D=scanlation_group" +
                             "&includeFutureUpdates=0&includeEmptyPages=0&includeExternalUrl=0" +
                             "&order%5Bvolume%5D=asc&order%5Bchapter%5D=asc&order%5BreadableAt%5D=asc";

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
        var itempropMeta = Regex.Match(html, @"<meta[^>]*itemprop=[""']name[""'][^>]*content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (itempropMeta.Success && !string.IsNullOrWhiteSpace(itempropMeta.Groups[1].Value))
        {
            return CleanTitle(itempropMeta.Groups[1].Value);
        }

        var h1Itemprop = Regex.Match(html, @"<h1[^>]*itemprop=[""']name[""'][^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (h1Itemprop.Success && !string.IsNullOrWhiteSpace(h1Itemprop.Groups[1].Value))
        {
            string raw = Regex.Replace(h1Itemprop.Groups[1].Value, @"<[^>]+>", "").Trim();
            if (!string.IsNullOrWhiteSpace(raw)) return CleanTitle(raw);
        }

        var h1Match = Regex.Match(html, @"<h1[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (h1Match.Success && !string.IsNullOrWhiteSpace(h1Match.Groups[1].Value))
        {
            string raw = Regex.Replace(h1Match.Groups[1].Value, @"<[^>]+>", "").Trim();
            if (!string.IsNullOrWhiteSpace(raw)) return CleanTitle(raw);
        }

        var ogMatch = Regex.Match(html, @"<meta[^>]*property=[""']og:title[""'][^>]*content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (ogMatch.Success && !string.IsNullOrWhiteSpace(ogMatch.Groups[1].Value))
        {
            return CleanTitle(ogMatch.Groups[1].Value);
        }

        var titleMatch = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (titleMatch.Success && !string.IsNullOrWhiteSpace(titleMatch.Groups[1].Value))
        {
            return CleanTitle(titleMatch.Groups[1].Value);
        }

        return ExtractFallbackTitleFromUrl(url);
    }

    private string ExtractCoverUrl(string html, string baseUrl)
    {
        var ogImage = Regex.Match(html, @"<meta[^>]*property=[""']og:image[""'][^>]*content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (ogImage.Success && !string.IsNullOrWhiteSpace(ogImage.Groups[1].Value))
        {
            return MakeAbsoluteUrl(ogImage.Groups[1].Value, baseUrl);
        }

        var imgMatch = Regex.Match(html, @"<(?:div|figure)[^>]*(?:book_avatar|col-image|detail-info)[^>]*>.*?<img[^>]*src=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (imgMatch.Success && !string.IsNullOrWhiteSpace(imgMatch.Groups[1].Value))
        {
            return MakeAbsoluteUrl(imgMatch.Groups[1].Value, baseUrl);
        }

        return string.Empty;
    }

    private List<ChapterItem> ExtractChapters(string html, string baseUrl, string domain)
    {
        var list = new List<ChapterItem>();
        domain = domain.ToLowerInvariant();

        string searchScope = html;
        string[] containerMarkers = new[]
        {
            "works-chapter-list",
            "list_chapter",
            "chapter-list",
            "list-chapter",
            "table-chapters",
            "box-list-chapter",
            "nt_listchapter"
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
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var navBypassRegex = new Regex(@"^(?:đọc\s*(?:từ\s*đầu|mới\s*nhất|tiếp)|doc\s*(?:tu\s*dau|moi\s*nhat|tiep)|read\s*(?:first|latest|continue)|theo\s*dõi|thích|like|subscribe|xem\s*thêm|mục\s*lục)$", RegexOptions.IgnoreCase);

        foreach (Match m in matches)
        {
            string href = m.Groups[1].Value.Trim();
            string text = Regex.Replace(m.Groups[2].Value, @"<[^>]+>", "").Trim();

            if (string.IsNullOrWhiteSpace(text) || href.StartsWith("#") || href.StartsWith("javascript:") || href.Contains("void(0)"))
            {
                continue;
            }

            if (navBypassRegex.IsMatch(text))
            {
                continue;
            }

            string fullUrl = MakeAbsoluteUrl(href, baseUrl);
            if (seenUrls.Add(fullUrl))
            {
                double chapNum = ExtractChapterNumber(text, ExtractChapterNumber(href, 0));
                list.Add(new ChapterItem
                {
                    ChapterNumber = chapNum,
                    Title = text,
                    Url = fullUrl,
                    Status = "Waiting"
                });
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

    private string CleanTitle(string title)
    {
        title = WebUtility.HtmlDecode(title);
        title = Regex.Replace(title, @"\s*(?:chương|chap|chapter)\s*(?:mới\s*nhất)?\s*\d+.*$", "", RegexOptions.IgnoreCase).Trim();
        title = Regex.Replace(title, @"\s*[-|–—]\s*(?:truy[eệ]nqq|nettruyen|mangadex|tuoitre|hako).*$", "", RegexOptions.IgnoreCase).Trim();
        title = Regex.Replace(title, @"\s*[-|–—].*$", "").Trim();
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
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                result.StatusMessage = $"Lỗi máy chủ HTTP {(int)res.StatusCode}";
                return result;
            }

            string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Trích xuất tiêu đề Tag / Thể loại
            var titleMatch = Regex.Match(html, @"<title[^>]*>(?<title>[^<]+)</title>", RegexOptions.IgnoreCase);
            if (titleMatch.Success)
            {
                result.TagTitle = CleanTitle(titleMatch.Groups["title"].Value);
            }

            int maxPage = 1;

            // 1. Phân tích các liên kết phân trang pagination
            var pageMatches = Regex.Matches(html, @"(?:page[=/_-]|trang[=/_-]|\/page\/|\/trang-)(\d+)", RegexOptions.IgnoreCase);
            foreach (Match m in pageMatches)
            {
                if (int.TryParse(m.Groups[1].Value, out int p) && p > maxPage && p < 10000)
                {
                    maxPage = p;
                }
            }

            // 2. Phân tích text trong các nút phân trang
            var numMatches = Regex.Matches(html, @"<(?:a|span|li)[^>]*class=[""'][^""']*(?:page|pagination|paging)[^""']*[""'][^>]*>(\d+)</(?:a|span|li)>", RegexOptions.IgnoreCase);
            foreach (Match m in numMatches)
            {
                if (int.TryParse(m.Groups[1].Value, out int p) && p > maxPage && p < 10000)
                {
                    maxPage = p;
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

    public async Task<List<ComicBookItem>> ScrapeBatchComicsFromTagPagesAsync(string tagUrl, int pageFrom, int pageTo, string domain, CancellationToken ct = default)
    {
        var list = new List<ComicBookItem>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        tagUrl = tagUrl.Trim();
        pageFrom = Math.Max(1, pageFrom);
        pageTo = Math.Max(pageFrom, pageTo);

        for (int page = pageFrom; page <= pageTo; page++)
        {
            if (ct.IsCancellationRequested) break;

            string pageUrl = BuildPagedTagUrl(tagUrl, page, domain);

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
                using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) continue;

                string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                // Trích xuất các liên kết truyện từ trang danh mục
                var comicLinks = ExtractComicLinksFromTagPage(html, pageUrl, domain);

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
            catch {}
        }

        return list;
    }

    private string BuildPagedTagUrl(string baseUrl, int page, string domain)
    {
        if (page <= 1) return baseUrl;

        domain = domain.ToLowerInvariant();
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
            return $"{baseUrl.TrimEnd('/')}/trang-{page}.html";
        }

        if (domain.Contains("thuviensach") || domain.Contains("dilib"))
        {
            return $"{baseUrl.TrimEnd('/')}/trang-{page}/";
        }

        if (domain.Contains("daomeoden"))
        {
            return $"{baseUrl.TrimEnd('/')}/page-{page}.html";
        }

        if (domain.Contains("sayhentai") || domain.Contains("hentai2read"))
        {
            return $"{baseUrl.TrimEnd('/')}/page/{page}/";
        }

        return $"{baseUrl}?page={page}";
    }

    private List<(string Url, string Title, string Cover)> ExtractComicLinksFromTagPage(string html, string pageUrl, string domain)
    {
        var result = new List<(string Url, string Title, string Cover)>();
        domain = domain.ToLowerInvariant();

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
            else if (domain.Contains("nettruyen") && link.Contains("/truyen-tranh/") && !link.Contains("/chap-")) isComicLink = true;
            else if (domain.Contains("thuviensach") && (link.EndsWith(".html") || link.Contains("/truyen-tranh/"))) isComicLink = true;
            else if (domain.Contains("loppytoonn") && link.Contains("/truyen/") && link.Split('/').Length <= 5) isComicLink = true;
            else if (domain.Contains("daomeoden") && link.Contains("/truyen-tranh/")) isComicLink = true;
            else if (domain.Contains("vi-hentai") && link.Contains("/truyen/")) isComicLink = true;
            else if (domain.Contains("damconuong") && link.Contains("/truyen/")) isComicLink = true;
            else if (domain.Contains("sayhentai") && (link.Contains("/story/") || link.Contains("/truyen/"))) isComicLink = true;
            else if (domain.Contains("hentai2read") && link.Contains("hentai2read.com/") && link.Split('/').Length <= 5) isComicLink = true;

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

                if (string.IsNullOrWhiteSpace(title))
                {
                    title = ExtractFallbackTitleFromUrl(fullUrl);
                }

                string coverUrl = string.Empty;
                var imgMatch = Regex.Match(inner, @"<img\s+[^>]*?>", RegexOptions.IgnoreCase);
                if (imgMatch.Success)
                {
                    string? img = ExtractImageUrlFromTag(imgMatch.Value);
                    if (!string.IsNullOrWhiteSpace(img))
                    {
                        coverUrl = MakeAbsoluteUrl(img, pageUrl);
                    }
                }

                if (title.Length >= 2 && !result.Any(x => x.Url.Equals(fullUrl, StringComparison.OrdinalIgnoreCase)))
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
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Cookie", "nw=1");
            using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return string.Empty;

            string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(html) && (html.Contains("Content Warning") || html.Contains("Offensive For Everyone")))
            {
                string sep = url.Contains('?') ? "&" : "?";
                string bypassUrl = url + sep + "nw=always";
                using var bypassReq = new HttpRequestMessage(HttpMethod.Get, bypassUrl);
                bypassReq.Headers.Add("Cookie", "nw=1; nw=always");
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

        item.Title = title;
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

    private async Task<List<string>> ExtractEHentaiChapterImagesAsync(string galleryUrl, CancellationToken ct)
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

        // Gom ảnh theo đúng thứ tự 1..N
        for (int p = 1; p <= finalTotalPages; p++)
        {
            if (directImageByPage.TryGetValue(p, out string? imgUrl) && !string.IsNullOrWhiteSpace(imgUrl))
            {
                imageUrls.Add(imgUrl);
            }
        }

        return imageUrls;
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

            item.Title = displayName;
            item.CoverUrl = coverUrl;
            item.TotalChapters = 1;
            item.LatestChapter = totalPages > 0 ? $"{totalPages} Pages" : "Gallery";
            item.Status = "Ready";
            item.StatusMessage = totalPages > 0 ? $"Extracted {totalPages} pages" : "Ready";

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

            // 2. Fast Path: Identify prefix & extension from thumbnail URL (bỏ 't' để lấy ảnh gốc)
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

            // 3. Fallback Slow Path: Fetch reader pages in parallel
            string cleanGalleryUrl = galleryUrl.TrimEnd('/');
            var pageImageDict = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
            using var sem = new SemaphoreSlim(8, 8);

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
}
