using System;
using System.Collections.Generic;
using System.Net.Http;
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

    private string ExtractTitle(string html, string url)
    {
        // 1. Try OpenGraph title: <meta property="og:title" content="..." />
        var ogMatch = Regex.Match(html, @"<meta[^>]*property=[""']og:title[""'][^>]*content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (ogMatch.Success && !string.IsNullOrWhiteSpace(ogMatch.Groups[1].Value))
        {
            return CleanTitle(ogMatch.Groups[1].Value);
        }

        // 2. Try <h1> tag
        var h1Match = Regex.Match(html, @"<h1[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (h1Match.Success && !string.IsNullOrWhiteSpace(h1Match.Groups[1].Value))
        {
            string raw = Regex.Replace(h1Match.Groups[1].Value, @"<[^>]+>", "").Trim();
            if (!string.IsNullOrWhiteSpace(raw)) return CleanTitle(raw);
        }

        // 3. Try <title>
        var titleMatch = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (titleMatch.Success && !string.IsNullOrWhiteSpace(titleMatch.Groups[1].Value))
        {
            return CleanTitle(titleMatch.Groups[1].Value);
        }

        return ExtractFallbackTitleFromUrl(url);
    }

    private string ExtractCoverUrl(string html, string baseUrl)
    {
        // 1. Try og:image
        var ogImage = Regex.Match(html, @"<meta[^>]*property=[""']og:image[""'][^>]*content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (ogImage.Success && !string.IsNullOrWhiteSpace(ogImage.Groups[1].Value))
        {
            return MakeAbsoluteUrl(ogImage.Groups[1].Value, baseUrl);
        }

        // 2. Try book_avatar or col-image or detail-info img
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

        // Generic chapter link extractor matching common manga patterns: /chap(ter)/, -chap-, /chuong/
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

        // If no chapters matched generic pattern, generate a fallback single chapter
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
        title = Regex.Replace(title, @"\s*[-|–—].*$", "").Trim(); // Remove " - NetTruyen", " | TruyenQQ" suffixes
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
