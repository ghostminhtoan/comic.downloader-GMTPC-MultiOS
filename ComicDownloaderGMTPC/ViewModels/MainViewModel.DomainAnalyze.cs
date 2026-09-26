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
    private string _domainSayhentaiTagUrl = "https://sayhentai.cx/genre/romance";

    [ObservableProperty]
    private string _domainHentai2readTagUrl = "https://hentai2read.com/";

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
        string url = GetDomainTagUrl(domain);
        if (string.IsNullOrWhiteSpace(url))
        {
            AddLog("WARN", "[Analyze] Vui lòng nhập URL hợp lệ!");
            return;
        }

        IsDomainAnalyzing = true;
        DomainAnalyzeStatusText = $"Đang phân tích {domain}...";
        AddLog("INFO", $"[Analyze] Bắt đầu phân tích URL: {url}");

        try
        {
            var result = await _scraperService.AnalyzeTagUrlAsync(url);
            if (result.IsSuccess)
            {
                DomainTotalPages = result.TotalPages;
                DomainPageFrom = 1;
                DomainPageTo = Math.Min(5, result.TotalPages);
                DomainAnalyzeStatusText = $"✅ Phân tích xong: {result.TotalPages} trang ({result.TagTitle})";
                AddLog("SUCCESS", $"[Analyze] Phân tích thành công {domain}: {result.TotalPages} trang ({result.TagTitle})");

                // Thêm vào history
                if (!DomainHistoryList.Contains(url)) DomainHistoryList.Insert(0, url);
            }
            else
            {
                DomainAnalyzeStatusText = "⚠️ " + result.StatusMessage;
                AddLog("WARN", $"[Analyze] {result.StatusMessage}");
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
        string url = GetDomainTagUrl(domain);
        if (string.IsNullOrWhiteSpace(url))
        {
            AddLog("WARN", "[Cào hàng loạt] Vui lòng nhập URL tag/thể loại hợp lệ!");
            return;
        }

        if (DomainPageFrom < 1 || DomainPageTo < DomainPageFrom)
        {
            AddLog("WARN", "[Cào hàng loạt] Dải trang không hợp lệ (Từ trang <= Đến trang)!");
            return;
        }

        IsDomainBatchScraping = true;
        _domainScrapeCts = new CancellationTokenSource();
        AddLog("INFO", $"[Cào hàng loạt] Bắt đầu cào từ trang {DomainPageFrom} đến {DomainPageTo} của {domain}...");

        try
        {
            var scrapedItems = await _scraperService.ScrapeBatchComicsFromTagPagesAsync(
                url,
                DomainPageFrom,
                DomainPageTo,
                domain,
                _domainScrapeCts.Token);

            int addedCount = 0;
            foreach (var item in scrapedItems)
            {
                if (!ComicBooks.Any(b => b.Url.Equals(item.Url, StringComparison.OrdinalIgnoreCase)))
                {
                    item.Index = ComicBooks.Count + 1;
                    ComicBooks.Add(item);
                    addedCount++;
                }
            }

            UpdateStats();
            AddLog("SUCCESS", $"[Cào hàng loạt] Hoàn tất cào {scrapedItems.Count} truyện (Đã thêm {addedCount} truyện mới vào Queue).");
            
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
        return "";
    }
}
