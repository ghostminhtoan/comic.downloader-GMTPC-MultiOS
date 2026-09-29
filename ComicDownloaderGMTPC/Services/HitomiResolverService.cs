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

            var reCaseMatches = Regex.Matches(body, @"case\s+(\d+):");
            _mMap.Clear();
            foreach (Match m in reCaseMatches)
            {
                if (int.TryParse(m.Groups[1].Value, out int caseVal))
                {
                    _mMap[caseVal] = 1;
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
}
