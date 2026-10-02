using System;

namespace ComicDownloaderGMTPC.Services;

public static class DomainRoutingService
{
    public static string DetectDomain(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "unknown";

        try
        {
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            var uri = new Uri(url);
            string host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host.Substring(4);

            if (host.Contains("truyenqq")) return "truyenqq";
            if (host.Contains("mangadex")) return "mangadex.org";
            if (host.Contains("loppytoonn")) return "loppytoonn.com";
            if (host.Contains("nettruyenviet10")) return "nettruyenviet10.com";
            if (host.Contains("nettruyen")) return "nettruyen";
            if (host.Contains("thuviensach") || host.Contains("dilib")) return "thuviensach.vn";
            if (host.Contains("hako") || host.Contains("docln")) return "hako.vn";
            if (host.Contains("daomeoden")) return "daomeoden";
            if (host.Contains("damconuong") || host.Contains("mbpro")) return "damconuong.shop";
            if (host.Contains("vi-hentai") || host.Contains("vihentai")) return "vi-hentai";
            if (host.Contains("sayhentai") || host.Contains("truyengg")) return "sayhentai";
            if (host.Contains("hentaiforce")) return "hentaiforce";
            if (host.Contains("hentai2read")) return "hentai2read";
            if (host.Contains("hentaiera")) return "hentaiera";
            if (host.Contains("e-hentai") || host.Contains("exhentai")) return "e-hentai.org";
            if (host.Contains("hitomi")) return "hitomi.la";

            return host;
        }
        catch
        {
            return "unknown";
        }
    }

    public static string GetServerFolderName(string? domain, string? url)
    {
        string key = (domain ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(url))
        {
            key = DetectDomain(url).ToLowerInvariant();
        }

        if (key.Contains("truyenqq")) return "truyenqq";
        if (key.Contains("nettruyenviet10")) return "nettruyenviet10";
        if (key.Contains("nettruyen")) return "nettruyen";
        if (key.Contains("loppytoonn") || key.Contains("loppy")) return "loppytoonn";
        if (key.Contains("daomeoden")) return "daomeoden";
        if (key.Contains("damconuong") || key.Contains("mbpro")) return "damconuong";
        if (key.Contains("mangadex")) return "mangadex";
        if (key.Contains("sayhentai") || key.Contains("truyengg")) return "sayhentai";
        if (key.Contains("vi-hentai") || key.Contains("vihentai")) return "vi-hentai";
        if (key.Contains("hentaiforce")) return "hentaiforce";
        if (key.Contains("hentai2read")) return "hentai2read";
        if (key.Contains("hentaiera")) return "hentaiera";
        if (key.Contains("hitomi")) return "hitomi";
        if (key.Contains("e-hentai") || key.Contains("exhentai")) return "e-hentai";
        if (key.Contains("thuviensach") || key.Contains("dilib")) return "thuviensach";
        if (key.Contains("hako") || key.Contains("docln")) return "hako";

        // Fallback: Lấy subdomain/domain không có extension nếu có
        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                var uri = new Uri(NormalizeUrl(url));
                string host = uri.Host.ToLowerInvariant();
                if (host.StartsWith("www.")) host = host.Substring(4);
                string[] parts = host.Split('.');
                if (parts.Length >= 2) return parts[0];
                return host;
            }
            catch {}
        }

        return !string.IsNullOrWhiteSpace(key) && key != "unknown" ? key : "others";
    }

    public static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        url = url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }
        return url;
    }
}

