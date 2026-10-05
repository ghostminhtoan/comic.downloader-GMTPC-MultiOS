using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class MainViewModel
{
    // ==========================================
    // DOMAIN TAG URL INPUTS
    // ==========================================
    [ObservableProperty]
    private string _domainTruyenqqTagUrl = "https://truyenqqko.com/the-loai/action-26";

    [ObservableProperty]
    private string _domainNettruyenTagUrl = "https://nettruyenviet10.com/tim-truyen/adventure";

    [ObservableProperty]
    private string _domainMangadexTagUrl = "https://mangadex.org/tag/391b0423-d847-456f-b01e-d6cf80f62ff8/action";

    [ObservableProperty]
    private string _domainLoppyTagUrl = "https://loppytoonn.com/the-loai/lang-man";

    [ObservableProperty]
    private string _domainThuviensachTagUrl = "https://thuviensach.vn/truyen-tranh/shounen/";

    [ObservableProperty]
    private string _domainDaomeodenTagUrl = "https://daomeoden.net/the-loai/romance.html";

    [ObservableProperty]
    private string _domainVihentaiTagUrl = "https://vi-hentai.pro/the-loai/khong-che";

    [ObservableProperty]
    private string _domainDamconuongTagUrl = "https://damconuong.shop/the-loai/elf";

    [ObservableProperty]
    private string _domainDamconuongRedirectDomain = string.Empty;

    partial void OnDomainDamconuongRedirectDomainChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            string redirectBase = NormalizeDamconuongRedirectInput(value);
            if (!string.IsNullOrWhiteSpace(redirectBase))
            {
                DomainDamconuongTagUrl = ApplyDamconuongRedirectDomain(DomainDamconuongTagUrl);
            }
        }
    }

    [ObservableProperty]
    private string _domainSayhentaiTagUrl = "https://sayhentai.cx/genre/romance";

    [ObservableProperty]
    private string _domainHentai2readTagUrl = "https://hentai2read.com/";

    [ObservableProperty]
    private string _domainHitomiTagUrl = "https://hitomi.la/type/manga-all.html";

    [ObservableProperty]
    private string _domainHentaiforceTagUrl = "https://hentaiforce.net/";

    [ObservableProperty]
    private string _domainEhentaiTagUrl = "https://e-hentai.org/";

    // ==========================================
    // DOMAIN ANALYZE & BATCH SCRAPING STATE
    // ==========================================
    [ObservableProperty]
    private int _domainTotalPages = 1;

    [ObservableProperty]
    private int _domainPageFrom = 1;

    [ObservableProperty]
    private int _domainPageTo = 1;

    [ObservableProperty]
    private bool _isDomainAnalyzing = false;

    [ObservableProperty]
    private bool _isDomainBatchScraping = false;

    [ObservableProperty]
    private string _domainAnalyzeStatusText = "Sẵn sàng phân tích.";

    // HISTORY & BOOKMARKS
    [ObservableProperty]
    private ObservableCollection<string> _domainHistoryList = new();

    [ObservableProperty]
    private ObservableCollection<string> _domainBookmarksList = new();

    private CancellationTokenSource? _domainScrapeCts;

    [RelayCommand]
    public void OpenDomainHome(string domain)
    {
        string homeUrl = GetDomainHomeUrl(domain);
        SetDomainTagUrl(domain, homeUrl);
        AddLog("INFO", $"[Home] Đã mở trang chủ: {homeUrl}");
    }

    [RelayCommand]
    public void RestoreDefaultTag(string domain)
    {
        string defaultUrl = GetDomainDefaultTagUrl(domain);
        SetDomainTagUrl(domain, defaultUrl);
        AddLog("INFO", $"[Restore Tag] Đã khôi phục URL mặc định cho {domain}: {defaultUrl}");
    }

    [RelayCommand]
    public void OpenDomainHistory(string domain)
    {
        string currentUrl = GetDomainTagUrl(domain);
        if (!string.IsNullOrWhiteSpace(currentUrl) && !DomainHistoryList.Contains(currentUrl))
        {
            DomainHistoryList.Insert(0, currentUrl);
        }
        AddLog("INFO", $"[Lịch sử] Danh sách lịch sử gồm {DomainHistoryList.Count} liên kết đã duyệt.");
    }

    [RelayCommand]
    public void OpenDomainBookmarks(string domain)
    {
        string currentUrl = GetDomainTagUrl(domain);
        if (!string.IsNullOrWhiteSpace(currentUrl))
        {
            if (!DomainBookmarksList.Contains(currentUrl))
            {
                DomainBookmarksList.Insert(0, currentUrl);
                AddLog("SUCCESS", $"[Bookmarks] Đã thêm vào danh sách yêu thích: {currentUrl}");
            }
            else
            {
                AddLog("INFO", $"[Bookmarks] Đã có trong danh sách yêu thích ({DomainBookmarksList.Count} mục).");
            }
        }
    }

    [RelayCommand]
    public async Task AnalyzeDomainTagAsync(string domain)
    {
        if (domain.Contains("thuviensach") || domain.Contains("dilib"))
        {
            DomainAnalyzeStatusText = "⚠️ Server đang lỗi, hiện tại chưa tải được";
            AddLog("WARN", "[thuviensach.vn] Server đang lỗi, hiện tại chưa tải được");
            return;
        }

        if (domain.Contains("mangadex"))
        {
            DomainAnalyzeStatusText = "⚠️ Website không hỗ trợ analyze";
            AddLog("WARN", "[MangaDex] Website không hỗ trợ analyze");
            return;
        }

        string rawInput = GetDomainTagUrl(domain);
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            AddLog("WARN", "[Analyze] Vui lòng nhập ít nhất một URL hợp lệ!");
            return;
        }

        var candidateUrls = ExtractCandidateUrls(rawInput);
        if (candidateUrls.Count == 0)
        {
            AddLog("WARN", "[Analyze] Không tìm thấy URL hợp lệ trong nội dung nhập!");
            return;
        }

        IsDomainAnalyzing = true;

        if (domain.Contains("damconuong"))
        {
            await EnsureDamconuongRedirectDomainAsync().ConfigureAwait(false);
        }

        try
        {
            // Trường hợp 1: Nhập 1 URL đơn lẻ
            if (candidateUrls.Count == 1)
            {
                string singleUrl = candidateUrls[0];
                DomainAnalyzeStatusText = $"Đang phân tích {domain}...";
                AddLog("INFO", $"[Analyze] Bắt đầu phân tích URL: {singleUrl}");

                // Kiểm tra nếu là link bộ truyện trực tiếp (Direct Comic Book)
                if (!ComicScraperService.IsCategoryOrTagUrl(singleUrl, domain))
                {
                    AddLog("INFO", $"[Analyze] Phát hiện link bộ truyện trực tiếp, đang trích xuất dữ liệu: {singleUrl}");
                    var book = await _scraperService.ScrapeBookAsync(singleUrl, ComicBooks.Count + 1);
                    if (!ComicBooks.Any(b => b.Url.Equals(book.Url, StringComparison.OrdinalIgnoreCase)))
                    {
                        ComicBooks.Add(book);
                        UpdateStats();
                    }
                    DomainAnalyzeStatusText = $"✅ Đã phân tích & nạp truyện: {book.Title} ({book.TotalChapters} chaps)";
                    AddLog("SUCCESS", $"[Analyze] Đã nạp thành công bộ truyện vào Queue: {book.Title} ({book.TotalChapters} chaps)");
                    if (!DomainHistoryList.Contains(singleUrl)) DomainHistoryList.Insert(0, singleUrl);
                    SelectedRootTabIndex = 1;
                    return;
                }

                // Nếu là link Tag / Category / Thể loại
                var result = await _scraperService.AnalyzeTagUrlAsync(singleUrl);
                if (result.IsSuccess)
                {
                    DomainTotalPages = result.TotalPages;
                    DomainPageFrom = 1;
                    DomainPageTo = Math.Min(5, result.TotalPages);
                    DomainAnalyzeStatusText = $"✅ Phân tích xong: {result.TotalPages} trang ({result.TagTitle})";
                    AddLog("SUCCESS", $"[Analyze] Phân tích thành công {domain}: {result.TotalPages} trang ({result.TagTitle})");

                    if (!DomainHistoryList.Contains(singleUrl)) DomainHistoryList.Insert(0, singleUrl);
                }
                else
                {
                    DomainAnalyzeStatusText = "⚠️ " + result.StatusMessage;
                    AddLog("WARN", $"[Analyze] {result.StatusMessage}");
                }
            }
            // Trường hợp 2: BATCH ANALYZE HÀNG LOẠT TOÀN BỘ DOMAIN (nhiều URL cùng lúc)
            else
            {
                int totalUrls = candidateUrls.Count;
                DomainAnalyzeStatusText = $"Đang Batch Analyze {totalUrls} liên kết...";
                AddLog("INFO", $"[Batch Analyze] Bắt đầu phân tích hàng loạt {totalUrls} liên kết cho toàn bộ domain...");

                int directBookAdded = 0;
                int tagsAnalyzed = 0;
                int maxPagesFound = 1;

                for (int i = 0; i < totalUrls; i++)
                {
                    string targetUrl = candidateUrls[i];
                    string detectedDomain = DomainRoutingService.DetectDomain(targetUrl);
                    DomainAnalyzeStatusText = $"[{i + 1}/{totalUrls}] Đang phân tích ({detectedDomain}): {targetUrl}";
                    AddLog("INFO", $"[Batch Analyze] [{i + 1}/{totalUrls}] ({detectedDomain}) {targetUrl}");

                    try
                    {
                        if (!ComicScraperService.IsCategoryOrTagUrl(targetUrl, detectedDomain))
                        {
                            var book = await _scraperService.ScrapeBookAsync(targetUrl, ComicBooks.Count + 1);
                            if (!ComicBooks.Any(b => b.Url.Equals(book.Url, StringComparison.OrdinalIgnoreCase)))
                            {
                                ComicBooks.Add(book);
                                directBookAdded++;
                            }
                            AddLog("SUCCESS", $"[Batch Analyze] Đã nạp truyện [{directBookAdded}]: {book.Title} ({book.TotalChapters} chaps)");
                        }
                        else
                        {
                            var tagRes = await _scraperService.AnalyzeTagUrlAsync(targetUrl);
                            if (tagRes.IsSuccess)
                            {
                                tagsAnalyzed++;
                                if (tagRes.TotalPages > maxPagesFound) maxPagesFound = tagRes.TotalPages;
                                AddLog("SUCCESS", $"[Batch Analyze] Phân tích danh mục: {tagRes.TagTitle} - {tagRes.TotalPages} trang");
                            }
                        }

                        if (!DomainHistoryList.Contains(targetUrl)) DomainHistoryList.Insert(0, targetUrl);
                    }
                    catch (Exception exItem)
                    {
                        AddLog("ERROR", $"[Batch Analyze Error] Link {targetUrl}: {exItem.Message}");
                    }
                }

                DomainTotalPages = maxPagesFound;
                DomainPageFrom = 1;
                DomainPageTo = Math.Min(5, maxPagesFound);
                UpdateStats();

                DomainAnalyzeStatusText = $"✅ Hoàn tất Batch Analyze: {directBookAdded} truyện nạp queue, {tagsAnalyzed} danh mục ({maxPagesFound} trang).";
                AddLog("SUCCESS", $"[Batch Analyze] Hoàn tất phân tích hàng loạt {totalUrls} liên kết! (Đã nạp {directBookAdded} truyện, {tagsAnalyzed} danh mục).");

                if (directBookAdded > 0)
                {
                    SelectedRootTabIndex = 1;
                }
            }
        }
        catch (Exception ex)
        {
            DomainAnalyzeStatusText = "Lỗi: " + ex.Message;
            AddLog("ERROR", $"[Analyze Error] {ex.Message}");
        }
        finally
        {
            IsDomainAnalyzing = false;
        }
    }

    [RelayCommand]
    public async Task ScrapeDomainBatchPagesAsync(string domain)
    {
        // CÀO MỚI (CRAWL): Xóa queue cũ, cào mới từ đầu
        await ExecuteDomainBatchScrapeAsync(domain, clearExisting: true);
    }

    [RelayCommand]
    public async Task CrawlMoreDomainBatchPagesAsync(string domain)
    {
        // CÀO THÊM (CRAWL MORE): Giữ nguyên queue hiện có, cào thêm tiếp vào danh sách
        await ExecuteDomainBatchScrapeAsync(domain, clearExisting: false);
    }

    private async Task ExecuteDomainBatchScrapeAsync(string domain, bool clearExisting)
    {
        if (domain.Contains("thuviensach") || domain.Contains("dilib"))
        {
            DomainAnalyzeStatusText = "⚠️ Server đang lỗi, hiện tại chưa tải được";
            AddLog("WARN", "[thuviensach.vn] Server đang lỗi, hiện tại chưa tải được");
            return;
        }

        if (domain.Contains("mangadex"))
        {
            DomainAnalyzeStatusText = "⚠️ Website không hỗ trợ analyze";
            AddLog("WARN", "[MangaDex] Website không hỗ trợ analyze");
            return;
        }

        string rawInput = GetDomainTagUrl(domain);
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            AddLog("WARN", "[Cào hàng loạt] Vui lòng nhập URL tag/thể loại hợp lệ!");
            return;
        }

        var tagUrls = ExtractCandidateUrls(rawInput);
        if (tagUrls.Count == 0)
        {
            AddLog("WARN", "[Cào hàng loạt] Không tìm thấy URL hợp lệ để cào!");
            return;
        }

        if (DomainPageFrom < 1 || DomainPageTo < DomainPageFrom)
        {
            AddLog("WARN", "[Cào hàng loạt] Dải trang không hợp lệ (Từ trang <= Đến trang)!");
            return;
        }

        IsDomainBatchScraping = true;
        _domainScrapeCts = new CancellationTokenSource();

        if (clearExisting)
        {
            ComicBooks.Clear();
            ScanResults.Clear();
            UpdateStats();
            AddLog("INFO", "[Cào mới (Crawl)] Đã xóa sạch danh sách truyện cũ trong Queue để cào mới từ đầu.");
        }
        else
        {
            AddLog("INFO", $"[Cào thêm (Crawl More)] Giữ nguyên {ComicBooks.Count} truyện hiện tại, cào nối tiếp vào Queue.");
        }

        AddLog("INFO", $"[Cào hàng loạt] Bắt đầu cào từ trang {DomainPageFrom} đến {DomainPageTo} ({tagUrls.Count} danh mục của {domain})...");

        try
        {
            int totalAdded = 0;
            int totalScraped = 0;

            for (int tagIdx = 0; tagIdx < tagUrls.Count; tagIdx++)
            {
                if (_domainScrapeCts.Token.IsCancellationRequested) break;
                string currentTagUrl = tagUrls[tagIdx];
                string targetDomain = DomainRoutingService.DetectDomain(currentTagUrl);
                if (string.IsNullOrWhiteSpace(targetDomain)) targetDomain = domain;

                AddLog("INFO", $"[Cào hàng loạt] [{tagIdx + 1}/{tagUrls.Count}] Đang cào ({targetDomain}): {currentTagUrl} (Trang {DomainPageFrom} - {DomainPageTo})...");

                var scrapedItems = await _scraperService.ScrapeBatchComicsFromTagPagesAsync(
                    currentTagUrl,
                    DomainPageFrom,
                    DomainPageTo,
                    targetDomain,
                    _domainScrapeCts.Token);

                totalScraped += scrapedItems.Count;
                int addedThisTag = 0;

                foreach (var item in scrapedItems)
                {
                    if (!ComicBooks.Any(b => b.Url.Equals(item.Url, StringComparison.OrdinalIgnoreCase)))
                    {
                        item.Index = ComicBooks.Count + 1;
                        ComicBooks.Add(item);
                        addedThisTag++;
                        totalAdded++;
                    }
                }

                UpdateStats();
                AddLog("SUCCESS", $"[Cào hàng loạt] Tag [{tagIdx + 1}/{tagUrls.Count}]: Cào được {scrapedItems.Count} truyện (+{addedThisTag} truyện mới vào Queue).");
            }

            AddLog("SUCCESS", $"[Cào hàng loạt] Hoàn tất cào tổng cộng {totalScraped} truyện ({totalAdded} truyện mới đã được thêm vào Queue).");
            
            // Chuyển sang Tab Download Queue để người dùng xem ngay
            SelectedRootTabIndex = 1;
        }
        catch (OperationCanceledException)
        {
            AddLog("WARN", "[Cào hàng loạt] Tiến trình đã dừng theo yêu cầu.");
        }
        catch (Exception ex)
        {
            AddLog("ERROR", $"[Cào hàng loạt Error] {ex.Message}");
        }
        finally
        {
            IsDomainBatchScraping = false;
        }
    }

    [RelayCommand]
    public void StopDomainBatchScrape()
    {
        if (_domainScrapeCts != null && !_domainScrapeCts.IsCancellationRequested)
        {
            _domainScrapeCts.Cancel();
            AddLog("WARN", "[Cào hàng loạt] Đang gửi yêu cầu dừng...");
        }
    }

    private static List<string> ExtractCandidateUrls(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return new List<string>();
        var tokens = rawText.Split(new[] { '\r', '\n', '\t', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(t => t.Trim())
                            .Where(t => !string.IsNullOrWhiteSpace(t) && !t.StartsWith("#"))
                            .Distinct()
                            .ToList();
        var urls = new List<string>();
        foreach (var t in tokens)
        {
            if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                urls.Add(t);
            }
            else if (t.Contains(".") && !t.Contains(" ") && t.Length >= 5)
            {
                urls.Add("https://" + t);
            }
        }
        return urls;
    }

    private string GetDomainTagUrl(string domain)
    {
        domain = domain.ToLowerInvariant();
        if (domain.Contains("truyenqq")) return DomainTruyenqqTagUrl;
        if (domain.Contains("nettruyen")) return DomainNettruyenTagUrl;
        if (domain.Contains("mangadex")) return DomainMangadexTagUrl;
        if (domain.Contains("loppy")) return DomainLoppyTagUrl;
        if (domain.Contains("thuviensach") || domain.Contains("dilib")) return DomainThuviensachTagUrl;
        if (domain.Contains("daomeoden")) return DomainDaomeodenTagUrl;
        if (domain.Contains("vi-hentai") || domain.Contains("vihentai")) return DomainVihentaiTagUrl;
        if (domain.Contains("damconuong")) return DomainDamconuongTagUrl;
        if (domain.Contains("sayhentai")) return DomainSayhentaiTagUrl;
        if (domain.Contains("hentai2read")) return DomainHentai2readTagUrl;
        if (domain.Contains("hitomi")) return DomainHitomiTagUrl;
        if (domain.Contains("hentaiforce")) return DomainHentaiforceTagUrl;
        if (domain.Contains("e-hentai") || domain.Contains("exhentai")) return DomainEhentaiTagUrl;
        return UrlInput;
    }

    private void SetDomainTagUrl(string domain, string value)
    {
        domain = domain.ToLowerInvariant();
        if (domain.Contains("truyenqq")) DomainTruyenqqTagUrl = value;
        else if (domain.Contains("nettruyen")) DomainNettruyenTagUrl = value;
        else if (domain.Contains("mangadex")) DomainMangadexTagUrl = value;
        else if (domain.Contains("loppy")) DomainLoppyTagUrl = value;
        else if (domain.Contains("thuviensach") || domain.Contains("dilib")) DomainThuviensachTagUrl = value;
        else if (domain.Contains("daomeoden")) DomainDaomeodenTagUrl = value;
        else if (domain.Contains("vi-hentai") || domain.Contains("vihentai")) DomainVihentaiTagUrl = value;
        else if (domain.Contains("damconuong")) DomainDamconuongTagUrl = value;
        else if (domain.Contains("sayhentai")) DomainSayhentaiTagUrl = value;
        else if (domain.Contains("hentai2read")) DomainHentai2readTagUrl = value;
        else if (domain.Contains("hitomi")) DomainHitomiTagUrl = value;
        else if (domain.Contains("hentaiforce")) DomainHentaiforceTagUrl = value;
        else if (domain.Contains("e-hentai") || domain.Contains("exhentai")) DomainEhentaiTagUrl = value;
        UrlInput = value;
    }

    private static string GetDomainHomeUrl(string domain)
    {
        domain = domain.ToLowerInvariant();
        if (domain.Contains("truyenqq")) return "https://truyenqqko.com/";
        if (domain.Contains("nettruyen")) return "https://nettruyenviet10.com/";
        if (domain.Contains("mangadex")) return "https://mangadex.org/";
        if (domain.Contains("loppy")) return "https://loppytoonn.com/";
        if (domain.Contains("thuviensach") || domain.Contains("dilib")) return "https://thuviensach.vn/";
        if (domain.Contains("daomeoden")) return "https://daomeoden.net/";
        if (domain.Contains("vi-hentai") || domain.Contains("vihentai")) return "https://vi-hentai.pro/";
        if (domain.Contains("damconuong")) return "https://damconuong.shop/";
        if (domain.Contains("sayhentai")) return "https://sayhentai.cx/";
        if (domain.Contains("hentai2read")) return "https://hentai2read.com/";
        if (domain.Contains("hitomi")) return "https://hitomi.la/";
        if (domain.Contains("hentaiforce")) return "https://hentaiforce.net/";
        if (domain.Contains("e-hentai") || domain.Contains("exhentai")) return "https://e-hentai.org/";
        return "https://google.com";
    }

    private static string GetDomainDefaultTagUrl(string domain)
    {
        domain = domain.ToLowerInvariant();
        if (domain.Contains("truyenqq")) return "https://truyenqqko.com/the-loai/action-26";
        if (domain.Contains("nettruyen")) return "https://nettruyenviet10.com/tim-truyen/adventure";
        if (domain.Contains("mangadex")) return "https://mangadex.org/tag/391b0423-d847-456f-b01e-d6cf80f62ff8/action";
        if (domain.Contains("loppy")) return "https://loppytoonn.com/the-loai/lang-man";
        if (domain.Contains("thuviensach") || domain.Contains("dilib")) return "https://thuviensach.vn/truyen-tranh/shounen/";
        if (domain.Contains("daomeoden")) return "https://daomeoden.net/the-loai/romance.html";
        if (domain.Contains("vi-hentai") || domain.Contains("vihentai")) return "https://vi-hentai.pro/the-loai/khong-che";
        if (domain.Contains("damconuong")) return "https://damconuong.shop/the-loai/elf";
        if (domain.Contains("sayhentai")) return "https://sayhentai.cx/genre/romance";
        if (domain.Contains("hentai2read")) return "https://hentai2read.com/";
        if (domain.Contains("hitomi")) return "https://hitomi.la/type/manga-all.html";
        if (domain.Contains("hentaiforce")) return "https://hentaiforce.net/";
        if (domain.Contains("e-hentai") || domain.Contains("exhentai")) return "https://e-hentai.org/";
        return "";
    }

    // ==========================================
    // DAMCONUONG.SHOP AUTOMATIC REDIRECT DOMAIN PROBE
    // ==========================================
    private int _damconuongRedirectProbeStarted = 0;

    public void CheckAndAutoExtractDamconuongRedirectDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.Contains("damconuong", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("mbpro", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            string candidate = url.Trim();
            if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "https://" + candidate;
            }
            var uri = new Uri(candidate);
            string host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host.Substring(4);

            string detectedBase = $"{uri.Scheme}://{host}";
            if (string.IsNullOrWhiteSpace(DomainDamconuongRedirectDomain) ||
                (!host.Equals("damconuong.shop", StringComparison.OrdinalIgnoreCase) &&
                 !DomainDamconuongRedirectDomain.Contains(host, StringComparison.OrdinalIgnoreCase)))
            {
                DomainDamconuongRedirectDomain = detectedBase;
                DomainDamconuongTagUrl = ApplyDamconuongRedirectDomain(DomainDamconuongTagUrl);
                AddLog("INFO", $"[damconuong.shop] Đã tự động ghi nhận redirect domain từ liên kết: {detectedBase}");
            }
        }
        catch {}
    }

    public string ApplyDamconuongRedirectDomain(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        string candidate = url.Trim();
        bool isDamconuong = candidate.Contains("damconuong", StringComparison.OrdinalIgnoreCase) ||
                            candidate.Contains("mbpro.vip", StringComparison.OrdinalIgnoreCase);
        if (!isDamconuong) return url;

        string redirectBaseUrl = NormalizeDamconuongRedirectInput(DomainDamconuongRedirectDomain);
        if (string.IsNullOrWhiteSpace(redirectBaseUrl)) return url;

        if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = "https://" + candidate;
        }

        try
        {
            var sourceUri = new Uri(candidate);
            var redirectUri = new Uri(redirectBaseUrl);
            var builder = new UriBuilder(sourceUri)
            {
                Scheme = redirectUri.Scheme,
                Host = redirectUri.Host,
                Port = redirectUri.IsDefaultPort ? -1 : redirectUri.Port
            };
            return builder.Uri.ToString().TrimEnd('/');
        }
        catch
        {
            return url;
        }
    }

    public static string NormalizeDamconuongRedirectInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        string val = input.Trim().TrimEnd('/');
        if (!val.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !val.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            val = "https://" + val;
        }
        try
        {
            var u = new Uri(val);
            return $"{u.Scheme}://{u.Host}";
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task EnsureDamconuongRedirectDomainAsync()
    {
        string currentRedirect = NormalizeDamconuongRedirectInput(DomainDamconuongRedirectDomain);
        if (!string.IsNullOrWhiteSpace(currentRedirect) &&
            !currentRedirect.Contains("damconuong.shop", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Interlocked.Exchange(ref _damconuongRedirectProbeStarted, 1) != 0)
        {
            return;
        }

        try
        {
            await RefreshDamconuongRedirectDomainAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _damconuongRedirectProbeStarted, 0);
        }
    }

    public async Task RefreshDamconuongRedirectDomainAsync()
    {
        string[] candidateProbeUrls = new[]
        {
            "https://damconuong.shop/the-loai/elf",
            "https://damconuong.pet/the-loai/elf",
            "https://damconuong.store/the-loai/elf",
            "https://mbpro.vip/the-loai/elf"
        };

        foreach (var probeUrl in candidateProbeUrls)
        {
            try
            {
                using var handler = new System.Net.Http.HttpClientHandler { AllowAutoRedirect = true };
                using var client = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                using var res = await client.GetAsync(probeUrl).ConfigureAwait(false);
                var finalUri = res.RequestMessage?.RequestUri;
                if (res.IsSuccessStatusCode || (int)res.StatusCode < 400)
                {
                    string targetHost = finalUri?.Host ?? new Uri(probeUrl).Host;
                    if (!string.IsNullOrWhiteSpace(targetHost))
                    {
                        string resolvedBaseUrl = $"https://{targetHost}";
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            DomainDamconuongRedirectDomain = resolvedBaseUrl;
                            DomainDamconuongTagUrl = ApplyDamconuongRedirectDomain("https://damconuong.shop/the-loai/elf");
                            AddLog("SUCCESS", $"[damconuong.shop redirect] Đã tự động phát hiện domain redirect: {resolvedBaseUrl}");
                        });
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog("INFO", $"[damconuong.shop redirect probe] Probe {probeUrl}: {ex.Message}");
            }
        }
    }
}
