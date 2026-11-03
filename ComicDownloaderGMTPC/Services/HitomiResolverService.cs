using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ComicDownloaderGMTPC.Services;

public static class HitomiResolverService
{
    private static readonly SemaphoreSlim _ggLock = new(1, 1);
    private static int _mDefault = 0;
    private static readonly ConcurrentDictionary<int, int> _mMap = new();
    private static string _b = string.Empty;
    private static long _lastRetrieval = 0;

    public static string ExtractGalleryId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        string target = url.Trim();
        int hashIdx = target.IndexOf('#');
        if (hashIdx >= 0) target = target.Substring(0, hashIdx);
        int qIdx = target.IndexOf('?');
        if (qIdx >= 0) target = target.Substring(0, qIdx);

        var match = Regex.Match(target, @"(?:-|/|^)(\d+)(?:\.html)?$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        var anyDigits = Regex.Match(target, @"\b(\d{5,8})\b");
        if (anyDigits.Success)
        {
            return anyDigits.Groups[1].Value;
        }

        return string.Empty;
    }

    public static async Task RefreshGGAsync(HttpClient httpClient, CancellationToken ct = default)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_lastRetrieval > 0 && _lastRetrieval + 60000 >= now && !string.IsNullOrEmpty(_b))
        {
            return;
        }

        await _ggLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_lastRetrieval > 0 && _lastRetrieval + 60000 >= now && !string.IsNullOrEmpty(_b))
            {
                return;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://ltn.gold-usergeneratedcontent.net/gg.js");
            req.Headers.Add("Referer", "https://hitomi.la/");
            using var res = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return;

            string body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) return;

            var reDefault = Regex.Match(body, @"var\s+o\s*=\s*(\d+)");
            if (reDefault.Success && int.TryParse(reDefault.Groups[1].Value, out int defVal))
            {
                _mDefault = defVal;
            }

            var reB = Regex.Match(body, @"b:\s*'([^']+)'");
            if (reB.Success)
            {
                _b = reB.Groups[1].Value;
            }

            // Trich xuat gia tri o trong switch case (Vi du: o = 0; break; hoac o = 1; break;)
            int switchCaseVal = 0;
            var reSwitchVal = Regex.Match(body, @"switch\s*\([^)]*\)\s*\{[\s\S]*?o\s*=\s*(\d+);");
            if (reSwitchVal.Success && int.TryParse(reSwitchVal.Groups[1].Value, out int sVal))
            {
                switchCaseVal = sVal;
            }

            var reCaseMatches = Regex.Matches(body, @"case\s+(\d+):");
            _mMap.Clear();
            foreach (Match m in reCaseMatches)
            {
                if (int.TryParse(m.Groups[1].Value, out int caseVal))
                {
                    _mMap[caseVal] = switchCaseVal;
                }
            }

            _lastRetrieval = now;
        }
        catch
        {
            // Bỏ qua lỗi kết nối mạng tạm thời, giữ nguyên state trước đó
        }
        finally
        {
            _ggLock.Release();
        }
    }

    public static int GetM(int g)
    {
        if (_mMap.TryGetValue(g, out int val)) return val;
        return _mDefault;
    }

    public static string GetS(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return string.Empty;
        var match = Regex.Match(hash, @"(..)(.)$");
        if (match.Success)
        {
            string hex = match.Groups[2].Value + match.Groups[1].Value;
            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int num))
            {
                return num.ToString();
            }
        }
        return string.Empty;
    }

    public static string GetSubdomain(string url, string? baseDomain, string? dir)
    {
        string retval = string.Empty;
        if (string.IsNullOrEmpty(baseDomain))
        {
            if (dir == "webp") retval = "w";
            else if (dir == "avif") retval = "a";
        }

        var match = Regex.Match(url, @"/[0-9a-f]{61}([0-9a-f]{2})([0-9a-f])", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            string hex = match.Groups[2].Value + match.Groups[1].Value;
            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int g))
            {
                int m = GetM(g);
                if (!string.IsNullOrEmpty(baseDomain))
                {
                    return ((char)(97 + m)) + baseDomain;
                }
                return retval + (1 + m);
            }
        }

        return retval;
    }

    public static string ResolveImageUrl(string hash, string name, string dir = "webp", bool isThumbnail = false)
    {
        if (string.IsNullOrWhiteSpace(hash)) return string.Empty;

        if (isThumbnail)
        {
            string realPath = Regex.Replace(hash, @"^.*(..)(.)$", "$2/$1/" + hash);
            string rawUrl = $"https://a.gold-usergeneratedcontent.net/webpbigtn/{realPath}.webp";
            string sub = GetSubdomain(rawUrl, "tn", null);
            return rawUrl.Replace("//a.gold-usergeneratedcontent.net/", $"//{sub}.gold-usergeneratedcontent.net/");
        }

        string ext = dir == "webp" ? "webp" : (dir == "avif" ? "avif" : (Path.GetExtension(name)?.TrimStart('.') ?? "webp"));
        string b = _b;
        string s = GetS(hash);
        string fullUrl = $"https://a.gold-usergeneratedcontent.net/{b}{s}/{hash}.{ext}";
        string fullSub = GetSubdomain(fullUrl, null, dir);
        return fullUrl.Replace("//a.gold-usergeneratedcontent.net/", $"//{fullSub}.gold-usergeneratedcontent.net/");
    }

    public static async Task<JsonDocument?> FetchGalleryInfoDocAsync(string galleryId, HttpClient httpClient, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(galleryId)) return null;
        string apiUrl = $"https://ltn.gold-usergeneratedcontent.net/galleries/{galleryId}.js";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            req.Headers.Add("Referer", "https://hitomi.la/");
            using var res = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            string jsContent = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(jsContent)) return null;

            string json = jsContent.Replace("var galleryinfo = ", "").Trim();
            if (json.EndsWith(';'))
            {
                json = json.Substring(0, json.Length - 1).Trim();
            }

            return JsonDocument.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    // ====================================================================================================
    // HITOMI.LA SEARCH & NOZOMI MULTI-TAG QUERY RESOLUTION (Cross-Platform Windows/Linux/Android)
    // ====================================================================================================
    private static readonly ConcurrentDictionary<string, (List<int> Ids, DateTime Expiry)> _searchCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsHitomiSearchUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string lower = url.ToLowerInvariant();
        return lower.Contains("hitomi") && (lower.Contains("/search.html") || lower.Contains("/search?") || lower.Contains("search.html?"));
    }

    public static string ExtractSearchQuery(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        int qIdx = url.IndexOf('?');
        if (qIdx < 0 || qIdx + 1 >= url.Length) return string.Empty;
        string rawQuery = url.Substring(qIdx + 1);
        int hashIdx = rawQuery.IndexOf('#');
        if (hashIdx >= 0) rawQuery = rawQuery.Substring(0, hashIdx);
        return System.Net.WebUtility.UrlDecode(rawQuery).Trim();
    }

    public static string ExtractSearchTitle(string url)
    {
        string q = ExtractSearchQuery(url);
        if (!string.IsNullOrWhiteSpace(q)) return q;
        return "Hitomi Search";
    }

    public static List<string> ParseSearchTerms(string query)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return list;

        var parts = query.Split(new[] { ' ', '+' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            string t = System.Net.WebUtility.UrlDecode(p.Trim());
            if (!string.IsNullOrWhiteSpace(t))
            {
                list.Add(t);
            }
        }
        return list;
    }

    public static (string NozomiUrl, bool IsNegative) MapTermToNozomiUrl(string rawTerm, string language = "all")
    {
        if (string.IsNullOrWhiteSpace(rawTerm)) return (string.Empty, false);
        string term = rawTerm.Trim();
        bool isNegative = false;
        if (term.StartsWith("-"))
        {
            isNegative = true;
            term = term.Substring(1).Trim();
        }

        if (string.IsNullOrWhiteSpace(term)) return (string.Empty, isNegative);

        int colonIdx = term.IndexOf(':');
        if (colonIdx > 0)
        {
            string ns = term.Substring(0, colonIdx).Trim().ToLowerInvariant();
            string val = term.Substring(colonIdx + 1).Trim();
            string safeVal = val.Replace(' ', '_');

            if (ns == "female" || ns == "male")
            {
                return ($"https://ltn.gold-usergeneratedcontent.net/tag/{ns}:{safeVal}-{language}.nozomi", isNegative);
            }
            if (ns == "tag")
            {
                return ($"https://ltn.gold-usergeneratedcontent.net/tag/{safeVal}-{language}.nozomi", isNegative);
            }
            if (ns == "artist")
            {
                return ($"https://ltn.gold-usergeneratedcontent.net/artist/{safeVal}-{language}.nozomi", isNegative);
            }
            if (ns == "series")
            {
                return ($"https://ltn.gold-usergeneratedcontent.net/series/{safeVal}-{language}.nozomi", isNegative);
            }
            if (ns == "character")
            {
                return ($"https://ltn.gold-usergeneratedcontent.net/character/{safeVal}-{language}.nozomi", isNegative);
            }
            if (ns == "group")
            {
                return ($"https://ltn.gold-usergeneratedcontent.net/group/{safeVal}-{language}.nozomi", isNegative);
            }
            if (ns == "type")
            {
                return ($"https://ltn.gold-usergeneratedcontent.net/type/{safeVal}-{language}.nozomi", isNegative);
            }
            if (ns == "language")
            {
                return ($"https://ltn.gold-usergeneratedcontent.net/index-{safeVal.ToLowerInvariant()}.nozomi", isNegative);
            }
            return ($"https://ltn.gold-usergeneratedcontent.net/tag/{term.Replace(' ', '_')}-{language}.nozomi", isNegative);
        }

        return ($"https://ltn.gold-usergeneratedcontent.net/tag/{term.Replace(' ', '_')}-{language}.nozomi", isNegative);
    }

    public static async Task<List<int>> FetchNozomiGalleryIdsAsync(string nozomiUrl, HttpClient httpClient, CancellationToken ct = default)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(nozomiUrl)) return list;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, nozomiUrl);
            req.Headers.TryAddWithoutValidation("Referer", "https://hitomi.la/");
            using var res = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return list;

            byte[] bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            list.Capacity = bytes.Length / 4;
            for (int i = 0; i + 3 < bytes.Length; i += 4)
            {
                int gId = (bytes[i] << 24) | (bytes[i + 1] << 16) | (bytes[i + 2] << 8) | bytes[i + 3];
                list.Add(gId);
            }
        }
        catch
        {
            // Trả về rỗng an toàn khi timeout hoặc không tìm thấy tag
        }
        return list;
    }

    public static async Task<List<int>> ResolveHitomiSearchGalleryIdsAsync(string searchUrl, HttpClient httpClient, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(searchUrl)) return new List<int>();

        if (_searchCache.TryGetValue(searchUrl, out var cached) && cached.Expiry > DateTime.UtcNow)
        {
            return cached.Ids;
        }

        string query = ExtractSearchQuery(searchUrl);
        var terms = ParseSearchTerms(query);

        if (terms.Count == 0)
        {
            var allIds = await FetchNozomiGalleryIdsAsync("https://ltn.gold-usergeneratedcontent.net/index-all.nozomi", httpClient, ct).ConfigureAwait(false);
            _searchCache[searchUrl] = (allIds, DateTime.UtcNow.AddMinutes(15));
            return allIds;
        }

        var positiveTasks = new List<Task<List<int>>>();
        var negativeTasks = new List<Task<List<int>>>();

        foreach (var term in terms)
        {
            var (nozomiUrl, isNegative) = MapTermToNozomiUrl(term, "all");
            if (string.IsNullOrWhiteSpace(nozomiUrl)) continue;

            if (isNegative)
            {
                negativeTasks.Add(FetchNozomiGalleryIdsAsync(nozomiUrl, httpClient, ct));
            }
            else
            {
                positiveTasks.Add(FetchNozomiGalleryIdsAsync(nozomiUrl, httpClient, ct));
            }
        }

        if (positiveTasks.Count == 0 && negativeTasks.Count == 0)
        {
            return new List<int>();
        }

        await Task.WhenAll(positiveTasks.Concat(negativeTasks)).ConfigureAwait(false);

        List<int>? result = null;

        // Phép giao (Intersection) của các tập positive
        foreach (var pTask in positiveTasks)
        {
            var ids = await pTask.ConfigureAwait(false);
            if (result == null)
            {
                result = new List<int>(ids);
            }
            else
            {
                var set = new HashSet<int>(ids);
                result.RemoveAll(id => !set.Contains(id));
            }
        }

        // Nếu chỉ có negative tags mà không có positive tags -> lấy index-all trừ đi negative
        if (result == null)
        {
            var allIds = await FetchNozomiGalleryIdsAsync("https://ltn.gold-usergeneratedcontent.net/index-all.nozomi", httpClient, ct).ConfigureAwait(false);
            result = new List<int>(allIds);
        }

        // Phép trừ (Difference) của các tập negative
        foreach (var nTask in negativeTasks)
        {
            var negIds = await nTask.ConfigureAwait(false);
            if (negIds.Count > 0)
            {
                var negSet = new HashSet<int>(negIds);
                result.RemoveAll(id => negSet.Contains(id));
            }
        }

        // Sắp xếp giảm dần theo ID (gallery mới nhất đứng trước)
        result.Sort((a, b) => b.CompareTo(a));

        _searchCache[searchUrl] = (result, DateTime.UtcNow.AddMinutes(15));
        return result;
    }
}
