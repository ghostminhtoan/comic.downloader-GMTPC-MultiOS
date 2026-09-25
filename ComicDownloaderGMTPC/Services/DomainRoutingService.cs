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
            if (host.Contains("damconuong")) return "damconuong.shop";
            if (host.Contains("vi-hentai") || host.Contains("vihentai")) return "vi-hentai";
            if (host.Contains("sayhentai") || host.Contains("truyengg")) return "sayhentai";
            if (host.Contains("hentaiforce")) return "hentaiforce";
            if (host.Contains("hentai2read")) return "hentai2read";
            if (host.Contains("hentaiera")) return "hentaiera";
            if (host.Contains("e-hentai") || host.Contains("exhentai")) return "e-hentai.org";

            return host;
        }
        catch
        {
            return "unknown";
        }
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
