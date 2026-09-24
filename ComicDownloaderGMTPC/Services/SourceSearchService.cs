using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ComicDownloaderGMTPC.Models;

namespace ComicDownloaderGMTPC.Services;

public class SourceSearchService
{
    private static readonly Lazy<SourceSearchService> _instance = new(() => new SourceSearchService());
    public static SourceSearchService Instance => _instance.Value;

    private readonly HttpClient _httpClient;

    public SourceSearchService()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };

        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "vi,en-US;q=0.9,en;q=0.8");
    }

    public async Task<List<SourceSearchResultItem>> SearchAsync(string keyword, IEnumerable<string> domains, CancellationToken ct = default)
    {
        var results = new List<SourceSearchResultItem>();
        if (string.IsNullOrWhiteSpace(keyword)) return results;

        keyword = keyword.Trim();

        foreach (var domain in domains)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var domainResults = await SearchDomainAsync(keyword, domain, ct).ConfigureAwait(false);
                results.AddRange(domainResults);
            }
            catch
            {
                // Soft fail per domain, continue searching other sources
            }
        }

        return results;
    }

    private async Task<List<SourceSearchResultItem>> SearchDomainAsync(string keyword, string domain, CancellationToken ct)
    {
        string normDomain = domain.ToLowerInvariant().Trim();

        if (normDomain.Contains("mangadex"))
        {
            return await SearchMangaDexAsync(keyword, ct).ConfigureAwait(false);
        }
        else if (normDomain.Contains("truyenqq"))
        {
            return await SearchTruyenqqAsync(keyword, ct).ConfigureAwait(false);
        }
        else if (normDomain.Contains("nettruyen"))
        {
            return await SearchNettruyenAsync(keyword, ct).ConfigureAwait(false);
        }
        else if (normDomain.Contains("hako") || normDomain.Contains("docln"))
        {
            return await SearchHakoAsync(keyword, ct).ConfigureAwait(false);
        }

        return await SearchGenericHtmlAsync(keyword, normDomain, ct).ConfigureAwait(false);
    }

    private async Task<List<SourceSearchResultItem>> SearchMangaDexAsync(string keyword, CancellationToken ct)
    {
        var list = new List<SourceSearchResultItem>();
        string query = Uri.EscapeDataString(keyword);
        string apiUrl = $"https://api.mangadex.org/manga?title={query}&limit=12&includes[]=cover_art";

        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return list;

        string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in dataArr.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idProp)) continue;
            string mangaId = idProp.GetString() ?? string.Empty;

            string title = "MangaDex Comic";
            if (item.TryGetProperty("attributes", out var attr))
            {
                if (attr.TryGetProperty("title", out var titleObj))
                {
                    foreach (var prop in titleObj.EnumerateObject())
                    {
                        title = prop.Value.GetString() ?? title;
                        break;
                    }
                }
            }

            string coverFile = string.Empty;
            if (item.TryGetProperty("relationships", out var relArr))
            {
                foreach (var rel in relArr.EnumerateArray())
                {
                    if (rel.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "cover_art")
                    {
                        if (rel.TryGetProperty("attributes", out var cAttr) && cAttr.TryGetProperty("fileName", out var fnProp))
                        {
                            coverFile = fnProp.GetString() ?? string.Empty;
                        }
                    }
                }
            }

            string coverUrl = !string.IsNullOrEmpty(coverFile)
                ? $"https://uploads.mangadex.org/covers/{mangaId}/{coverFile}.256.jpg"
                : string.Empty;

            list.Add(new SourceSearchResultItem
            {
                Title = title,
                Url = $"https://mangadex.org/title/{mangaId}",
                Domain = "mangadex.org",
                CoverUrl = coverUrl,
                LatestChapter = "API MangaDex"
            });
        }

        return list;
    }

    private async Task<List<SourceSearchResultItem>> SearchTruyenqqAsync(string keyword, CancellationToken ct)
    {
        var list = new List<SourceSearchResultItem>();
        string query = Uri.EscapeDataString(keyword);
        string searchUrl = $"https://truyenqqto.com/tim-kiem.html?q={query}";

        using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return list;

        string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var matches = Regex.Matches(html, @"<li[^>]*>.*?<div[^>]*class=[""']book_avatar[""'][^>]*>.*?<a[^>]*href=[""']([^""']+)[""'][^>]*title=[""']([^""']+)[""'].*?<img[^>]*src=[""']([^""']+)[""']", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match m in matches)
        {
            string bookUrl = m.Groups[1].Value.Trim();
            string bookTitle = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim());
            string coverUrl = m.Groups[3].Value.Trim();

            list.Add(new SourceSearchResultItem
            {
                Title = bookTitle,
                Url = bookUrl,
                Domain = "truyenqq",
                CoverUrl = coverUrl,
                LatestChapter = "TruyenQQ"
            });
        }

        return list;
    }

    private async Task<List<SourceSearchResultItem>> SearchNettruyenAsync(string keyword, CancellationToken ct)
    {
        var list = new List<SourceSearchResultItem>();
        string query = Uri.EscapeDataString(keyword);
        string searchUrl = $"https://nettruyen.tech/tim-truyen?keyword={query}";

        using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return list;

        string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var matches = Regex.Matches(html, @"<div[^>]*class=[""']item[""'][^>]*>.*?<a[^>]*class=[""']jtip[""'][^>]*href=[""']([^""']+)[""'][^>]*>(.*?)</a>.*?<img[^>]*src=[""']([^""']+)[""']", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match m in matches)
        {
            string bookUrl = m.Groups[1].Value.Trim();
            string bookTitle = Regex.Replace(m.Groups[2].Value, @"<[^>]+>", "").Trim();
            string coverUrl = m.Groups[3].Value.Trim();

            list.Add(new SourceSearchResultItem
            {
                Title = System.Net.WebUtility.HtmlDecode(bookTitle),
                Url = bookUrl,
                Domain = "nettruyen.tech",
                CoverUrl = coverUrl,
                LatestChapter = "NetTruyen"
            });
        }

        return list;
    }

    private async Task<List<SourceSearchResultItem>> SearchHakoAsync(string keyword, CancellationToken ct)
    {
        var list = new List<SourceSearchResultItem>();
        string query = Uri.EscapeDataString(keyword);
        string searchUrl = $"https://ln.hako.vn/tim-kiem?keywords={query}";

        using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return list;

        string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var matches = Regex.Matches(html, @"<div[^>]*class=[""']thumb-item-flow[^""']*[""'][^>]*>.*?<a[^>]*href=[""']([^""']+)[""'][^>]*title=[""']([^""']+)[""']", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match m in matches)
        {
            string bookUrl = m.Groups[1].Value.Trim();
            if (!bookUrl.StartsWith("http")) bookUrl = "https://ln.hako.vn" + bookUrl;
            string bookTitle = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim());

            list.Add(new SourceSearchResultItem
            {
                Title = bookTitle,
                Url = bookUrl,
                Domain = "hako.vn",
                LatestChapter = "Light Novel"
            });
        }

        return list;
    }

    private async Task<List<SourceSearchResultItem>> SearchGenericHtmlAsync(string keyword, string domain, CancellationToken ct)
    {
        // Fallback generator if direct HTML search fails or domain requires custom token
        await Task.Delay(10, ct);
        return new List<SourceSearchResultItem>();
    }

    public string BuildExternalSearchUrl(string query, string domain, bool isBing = false)
    {
        string encoded = Uri.EscapeDataString($"site:{domain} {query}");
        return isBing ? $"https://www.bing.com/search?q={encoded}" : $"https://www.google.com/search?q={encoded}";
    }
}
