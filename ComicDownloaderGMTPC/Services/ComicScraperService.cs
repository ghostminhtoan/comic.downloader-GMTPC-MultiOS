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
            string baseDomain = DomainRoutingService.NormalizeUrl(chapterUrl);
            req.Headers.Add("Referer", baseDomain);
            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return images;

            string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // 1. Phân lập vùng đọc ảnh (Reading Scope) theo các khối page-chapter hoặc chapter_content
            string searchScope = html;
            var pageBlocks = Regex.Matches(
                html,
                @"<div[^>]+id=[""']page_\d+[""'][^>]*class=[""'][^""']*page-chapter[^""']*[""'][^>]*>.*?(?=<div[^>]+id=[""']page_\d+[""']|$)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (pageBlocks.Count > 0)
            {
                searchScope = string.Join("\n", pageBlocks.Cast<Match>().Select(m => m.Value));
            }
            else
            {
                var contentMatch = Regex.Match(html, @"<(?:div|section|article)[^>]*(?:chapter_content|story-see-content|reading-detail|chapter-img|reading)[^>]*>.*?</(?:div|section|article)>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (contentMatch.Success)
                {
                    searchScope = contentMatch.Value;
                }
            }

            // 2. Quét tất cả các thẻ <img> và <source> trong vùng đọc
            var imgTags = Regex.Matches(searchScope, @"<(?:img|source)\s+[^>]*>", RegexOptions.IgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in imgTags)
            {
                string tag = m.Value;
                string? imgUrl = null;

                // Ưu tiên data-original -> data-cdn -> data-lazy-src -> data-src -> src
                var dataOriginal = Regex.Match(tag, @"data-original=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (dataOriginal.Success)
                {
                    imgUrl = dataOriginal.Groups[1].Value;
                }
                else
                {
                    var dataCdn = Regex.Match(tag, @"data-cdn=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                    if (dataCdn.Success)
                    {
                        imgUrl = dataCdn.Groups[1].Value;
                    }
                    else
                    {
                        var dataLazy = Regex.Match(tag, @"data-lazy-src=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                        if (dataLazy.Success)
                        {
                            imgUrl = dataLazy.Groups[1].Value;
                        }
                        else
                        {
                            var dataSrc = Regex.Match(tag, @"data-src=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                            if (dataSrc.Success)
                            {
                                imgUrl = dataSrc.Groups[1].Value;
                            }
                            else
                            {
                                var src = Regex.Match(tag, @"src=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                                if (src.Success)
                                {
                                    imgUrl = src.Groups[1].Value;
                                }
                            }
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(imgUrl)) continue;
                imgUrl = imgUrl.Trim();

                // Lọc bỏ ảnh hệ thống/rác
                if (imgUrl.StartsWith("data:") ||
                    imgUrl.Contains("logo") ||
                    imgUrl.Contains("banner") ||
                    imgUrl.Contains("icon") ||
                    imgUrl.Contains("avatar") ||
                    imgUrl.Contains("loading") ||
                    imgUrl.Contains("no_image") ||
                    imgUrl.Contains("facebook.com"))
                {
                    continue;
                }

                // Giữ nguyên query string (theo workflow.md)
                string fullUrl = MakeAbsoluteUrl(imgUrl, chapterUrl);
                if (seen.Add(fullUrl))
                {
                    images.Add(fullUrl);
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
        // 1. Thẻ meta itemprop="name" (Chuẩn nhất ở các web manga như TruyenQQ)
        var itempropMeta = Regex.Match(html, @"<meta[^>]*itemprop=[""']name[""'][^>]*content=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (itempropMeta.Success && !string.IsNullOrWhiteSpace(itempropMeta.Groups[1].Value))
        {
            return CleanTitle(itempropMeta.Groups[1].Value);
        }

        // 2. Thẻ h1 itemprop="name" hoặc h1 với class tiêu đề
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

        // 1. Nhận diện vị trí bắt đầu của danh sách chương thật (bỏ qua toàn bộ header/menu chứa nút 'Đọc từ đầu')
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
                // Cắt bớt phần bình luận/quảng cáo phía sau nếu có
                int commentIdx = searchScope.IndexOf("id=\"comment", StringComparison.OrdinalIgnoreCase);
                if (commentIdx < 0) commentIdx = searchScope.IndexOf("class=\"comment", StringComparison.OrdinalIgnoreCase);
                if (commentIdx > 0)
                {
                    searchScope = searchScope.Substring(0, commentIdx);
                }
                break;
            }
        }

        // 2. Quét thẻ <a> trong scope
        var matches = Regex.Matches(searchScope, @"<a[^>]*href=[""']([^""']*(?:chap|chuong|chapter)[^""']*)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Regex lọc bỏ triệt để các nút điều hướng
        var navBypassRegex = new Regex(@"^(?:đọc\s*(?:từ\s*đầu|mới\s*nhất|tiếp)|doc\s*(?:tu\s*dau|moi\s*nhat|tiep)|read\s*(?:first|latest|continue)|theo\s*dõi|thích|like|subscribe|xem\s*thêm|mục\s*lục)$", RegexOptions.IgnoreCase);

        foreach (Match m in matches)
        {
            string href = m.Groups[1].Value.Trim();
            string text = Regex.Replace(m.Groups[2].Value, @"<[^>]+>", "").Trim();

            if (string.IsNullOrWhiteSpace(text) || href.StartsWith("#") || href.StartsWith("javascript:") || href.Contains("void(0)"))
            {
                continue;
            }

            // BẮT BUỘC BYPASS nút "Đọc từ đầu", "Đọc mới nhất", "Theo dõi", v.v.
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

        // 3. BẮT BUỘC sắp xếp tăng dần theo ChapterNumber (từ chương nhỏ nhất đến chương lớn nhất: 1 -> 39)
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
        // Loại bỏ cụm SEO "chương mới nhất \d+..." hoặc "chap mới nhất \d+..."
        title = Regex.Replace(title, @"\s*(?:chương|chap|chapter)\s*(?:mới\s*nhất)?\s*\d+.*$", "", RegexOptions.IgnoreCase).Trim();
        // Loại bỏ thương hiệu đuôi: - TruyenQQ, | TruyenQQ, - NetTruyen, v.v.
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
        if (Uri.TryCreate(new Uri(baseUrl), relativeOrAbsolute, out var result))
        {
            return result.AbsoluteUri;
        }
        return relativeOrAbsolute;
    }
}
