using System;
using System.Collections.Generic;
using System.Linq;
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
            AutomaticDecompression = System.Net.DecompressionMethods.All,
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

    public async Task<ComicBookItem> ScrapeBookAsync(string url, int index, CancellationToken ct = default)
    {
        url = DomainRoutingService.NormalizeUrl(url);
        string domain = DomainRoutingService.DetectDomain(url);

        var item = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = domain,
            Status = "Extracting...",
            StatusMessage = "Connecting to server..."
        };

        try
        {
            if (domain.Contains("mangadex"))
            {
                return await ScrapeMangaDexBookAsync(url, index, ct).ConfigureAwait(false);
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

            using var req = new HttpRequestMessage(HttpMethod.Get, chapterUrl);
            req.Headers.Add("Referer", DomainRoutingService.NormalizeUrl(chapterUrl));
            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return images;

            string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // 1. Try reading containers
            var matches = Regex.Matches(html, @"<(?:div|section|article)[^>]*(?:chapter_content|story-see-content|reading-detail|page-chapter|chapter-img|reading)[^>]*>.*?</(?:div|section|article)>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

            string searchScope = matches.Count > 0
                ? string.Join("\n", matches.Select(m => m.Value))
                : html;

            // 2. Extract image tags inside reading scope
            var imgMatches = Regex.Matches(searchScope, @"<img[^>]+(?:data-src|data-original|data-cdn|src)=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in imgMatches)
            {
                string src = m.Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(src) || src.StartsWith("data:") || src.Contains("logo") || src.Contains("banner") || src.Contains("icon"))
                {
                    continue;
                }

                string full = MakeAbsoluteUrl(src, chapterUrl);
                if (seen.Add(full))
                {
                    images.Add(full);
                }
            }
        }
        catch
        {
            // Return whatever found or empty
        }

        return images;
    }

    private async Task<List<string>> ExtractMangaDexChapterImagesAsync(string chapterUrl, CancellationToken ct)
    {
        var list = new List<string>();
        var match = Regex.Match(chapterUrl, @"chapter/([a-f0-9\-]+)", RegexOptions.IgnoreCase);
        if (!match.Success) return list;

        string chapterId = match.Groups[1].Value;
        string apiUrl = $"https://api.mangadex.org/at-home/server/{chapterId}";

        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return list;

        string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string baseUrl = root.GetProperty("baseUrl").GetString() ?? string.Empty;
        var chapterObj = root.GetProperty("chapter");
        string hash = chapterObj.GetProperty("hash").GetString() ?? string.Empty;

        if (chapterObj.TryGetProperty("data", out var dataArr))
        {
            foreach (var fn in dataArr.EnumerateArray())
            {
                string filename = fn.GetString() ?? string.Empty;
                if (!string.IsNullOrEmpty(filename))
                {
                    list.Add($"{baseUrl}/data/{hash}/{filename}");
                }
            }
        }

        return list;
    }

    private async Task<ComicBookItem> ScrapeMangaDexBookAsync(string url, int index, CancellationToken ct)
    {
        var match = Regex.Match(url, @"title/([a-f0-9\-]+)", RegexOptions.IgnoreCase);
        string mangaId = match.Success ? match.Groups[1].Value : string.Empty;

        var book = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = "mangadex.org",
            Title = "MangaDex Comic",
            Status = "Extracting..."
        };

        if (string.IsNullOrEmpty(mangaId))
        {
            book.Title = ExtractFallbackTitleFromUrl(url);
            book.Status = "Error";
            book.StatusMessage = "Invalid MangaDex ID";
            return book;
        }

        string apiUrl = $"https://api.mangadex.org/manga/{mangaId}?includes[]=cover_art";
        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (res.IsSuccessStatusCode)
        {
            string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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

        // Fetch feed/chapters
        string feedUrl = $"https://api.mangadex.org/manga/{mangaId}/feed?translatedLanguage[]=vi&translatedLanguage[]=en&order[chapter]=asc&limit=100";
        using var feedReq = new HttpRequestMessage(HttpMethod.Get, feedUrl);
        using var feedRes = await _httpClient.SendAsync(feedReq, ct).ConfigureAwait(false);
        if (feedRes.IsSuccessStatusCode)
        {
            string feedJson = await feedRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var feedDoc = JsonDocument.Parse(feedJson);
            if (feedDoc.RootElement.TryGetProperty("data", out var chapArr))
            {
                int count = 1;
                foreach (var c in chapArr.EnumerateArray())
                {
                    string cId = c.GetProperty("id").GetString() ?? string.Empty;
                    var cAttr = c.GetProperty("attributes");
                    string chapNum = cAttr.TryGetProperty("chapter", out var cp) ? cp.GetString() ?? count.ToString() : count.ToString();
                    string chapTitle = cAttr.TryGetProperty("title", out var tp) ? tp.GetString() ?? $"Chapter {chapNum}" : $"Chapter {chapNum}";

                    double.TryParse(chapNum, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedNum);

                    book.Chapters.Add(new ChapterItem
                    {
                        ChapterNumber = parsedNum > 0 ? parsedNum : count,
                        Title = string.IsNullOrWhiteSpace(chapTitle) ? $"Chapter {chapNum}" : chapTitle,
                        Url = $"https://mangadex.org/chapter/{cId}",
                        Status = "Waiting"
                    });
                    count++;
                }
            }
        }

        book.TotalChapters = book.Chapters.Count;
        book.LatestChapter = book.Chapters.Count > 0 ? book.Chapters[^1].Title : "N/A";
        book.Status = "Ready";
        book.StatusMessage = $"Extracted {book.TotalChapters} chapters";

        return book;
    }

    private string ExtractTitle(string html, string url)
    {
        var ogMatch = Regex.Match(html, @"<meta[^>]*property=[""']og:title[""'][^>]*content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (ogMatch.Success && !string.IsNullOrWhiteSpace(ogMatch.Groups[1].Value))
        {
            return CleanTitle(ogMatch.Groups[1].Value);
        }

        var h1Match = Regex.Match(html, @"<h1[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (h1Match.Success && !string.IsNullOrWhiteSpace(h1Match.Groups[1].Value))
        {
            string raw = Regex.Replace(h1Match.Groups[1].Value, @"<[^>]+>", "").Trim();
            if (!string.IsNullOrWhiteSpace(raw)) return CleanTitle(raw);
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
        var matches = Regex.Matches(html, @"<a[^>]*href=[""']([^""']*(?:chap|chuong|chapter)[^""']*)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int counter = 1;
        foreach (Match m in matches)
        {
            string href = m.Groups[1].Value.Trim();
            string text = Regex.Replace(m.Groups[2].Value, @"<[^>]+>", "").Trim();

            if (string.IsNullOrWhiteSpace(text) || href.StartsWith("#") || href.StartsWith("javascript:"))
            {
                continue;
            }

            string fullUrl = MakeAbsoluteUrl(href, baseUrl);
            if (seenUrls.Add(fullUrl))
            {
                double chapNum = ExtractChapterNumber(text, counter);
                list.Add(new ChapterItem
                {
                    ChapterNumber = chapNum,
                    Title = text,
                    Url = fullUrl,
                    Status = "Waiting"
                });
                counter++;
            }
        }

        if (list.Count == 0)
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
        var match = Regex.Match(text, @"(?:chap(?:ter)?|chương|tập)\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
        if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
        {
            return parsed;
        }

        var numMatch = Regex.Match(text, @"([0-9]+(?:\.[0-9]+)?)");
        if (numMatch.Success && double.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double num))
        {
            return num;
        }

        return fallback;
    }

    private string CleanTitle(string title)
    {
        title = System.Net.WebUtility.HtmlDecode(title);
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
        if (Uri.TryCreate(new Uri(baseUrl), relativeOrAbsolute, out var result))
        {
            return result.AbsoluteUri;
        }
        return relativeOrAbsolute;
    }
}
