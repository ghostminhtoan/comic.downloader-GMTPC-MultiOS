using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class MainViewModel
{
    // =========================================================================
    // QUẢN LÝ BỘ NHỚ ĐỆM CHO TAB DOWNLOAD (THUMBNAIL STORAGE & ZIP FILE CACHE)
    // =========================================================================

    [ObservableProperty]
    private bool _isDownloadCacheModalOpen = false;

    [ObservableProperty]
    private string _thumbnailStorageSummaryText = "Thumbnails 0 B / 1.0 GB";

    [ObservableProperty]
    private string _zipCacheStorageSummaryText = "Zip cache 0 B / 1.0 GB";

    [ObservableProperty]
    private string _archiveCacheSummaryText = "0 B";

    [ObservableProperty]
    private string _retentionPeriodSummaryText = "4 days";

    [ObservableProperty]
    private string _totalCacheSummaryText = "0 B";

    [ObservableProperty]
    private bool _isThumbnailQuotaDialogOpen = false;

    [ObservableProperty]
    private int _selectedThumbnailQuotaMb = 1000;

    [ObservableProperty]
    private bool _isZipQuotaDialogOpen = false;

    [ObservableProperty]
    private int _selectedZipQuotaMb = 1000;

    [ObservableProperty]
    private bool _isRetentionDialogOpen = false;

    [ObservableProperty]
    private int _selectedRetentionDays = 4;

    public void RefreshDownloadCacheSummary()
    {
        ThumbnailStorageSummaryText = ComicDiskCacheService.Instance.GetThumbnailsStorageSummaryText();
        ZipCacheStorageSummaryText = ComicDiskCacheService.Instance.GetZipCacheStorageSummaryText();
        ArchiveCacheSummaryText = ComicDiskCacheService.Instance.GetArchiveCacheSummaryText();
        RetentionPeriodSummaryText = ComicDiskCacheService.Instance.GetRetentionPeriodSummaryText();
        TotalCacheSummaryText = ComicDiskCacheService.FormatBytes(ComicDiskCacheService.Instance.GetTotalCacheSizeBytes());
        SelectedThumbnailQuotaMb = ComicDiskCacheService.Instance.ThumbnailQuotaMb;
        SelectedZipQuotaMb = ComicDiskCacheService.Instance.ZipCacheQuotaMb;
        SelectedRetentionDays = ComicDiskCacheService.Instance.RetentionDays;
    }

    [RelayCommand]
    public void OpenDownloadCacheModal()
    {
        RefreshDownloadCacheSummary();
        IsDownloadCacheModalOpen = true;
        OnPropertyChanged(nameof(IsFloatingQuickControlVisible));
    }

    [RelayCommand]
    public void CloseDownloadCacheModal()
    {
        IsDownloadCacheModalOpen = false;
        IsThumbnailQuotaDialogOpen = false;
        IsZipQuotaDialogOpen = false;
        IsRetentionDialogOpen = false;
        OnPropertyChanged(nameof(IsFloatingQuickControlVisible));
    }

    [RelayCommand]
    public void OpenThumbnailQuotaDialog()
    {
        SelectedThumbnailQuotaMb = ComicDiskCacheService.Instance.ThumbnailQuotaMb;
        IsThumbnailQuotaDialogOpen = true;
    }

    [RelayCommand]
    public void CloseThumbnailQuotaDialog()
    {
        IsThumbnailQuotaDialogOpen = false;
    }

    [RelayCommand]
    public void SelectThumbnailQuota(object? quotaObj)
    {
        if (quotaObj != null && int.TryParse(quotaObj.ToString(), out int mb))
        {
            SelectedThumbnailQuotaMb = mb;
        }
    }

    [RelayCommand]
    public void ConfirmThumbnailQuota()
    {
        ComicDiskCacheService.Instance.ThumbnailQuotaMb = SelectedThumbnailQuotaMb;
        IsThumbnailQuotaDialogOpen = false;
        RefreshDownloadCacheSummary();
        AddLog("INFO", $"✓ Đã lưu hạn mức bộ nhớ ảnh bìa: {SelectedThumbnailQuotaMb} MB");
    }

    [RelayCommand]
    public void OpenZipQuotaDialog()
    {
        SelectedZipQuotaMb = ComicDiskCacheService.Instance.ZipCacheQuotaMb;
        IsZipQuotaDialogOpen = true;
    }

    [RelayCommand]
    public void CloseZipQuotaDialog()
    {
        IsZipQuotaDialogOpen = false;
    }

    [RelayCommand]
    public void SelectZipQuota(object? quotaObj)
    {
        if (quotaObj != null && int.TryParse(quotaObj.ToString(), out int mb))
        {
            SelectedZipQuotaMb = mb;
        }
    }

    [RelayCommand]
    public void ConfirmZipQuota()
    {
        ComicDiskCacheService.Instance.ZipCacheQuotaMb = SelectedZipQuotaMb;
        IsZipQuotaDialogOpen = false;
        RefreshDownloadCacheSummary();
        AddLog("INFO", $"✓ Đã lưu dung lượng zip cache: {SelectedZipQuotaMb} MB");
    }

    [RelayCommand]
    public void OpenRetentionDialog()
    {
        SelectedRetentionDays = ComicDiskCacheService.Instance.RetentionDays;
        IsRetentionDialogOpen = true;
    }

    [RelayCommand]
    public void CloseRetentionDialog()
    {
        IsRetentionDialogOpen = false;
    }

    [RelayCommand]
    public void SelectRetentionDays(object? daysObj)
    {
        if (daysObj != null && int.TryParse(daysObj.ToString(), out int days))
        {
            SelectedRetentionDays = days;
        }
    }

    [RelayCommand]
    public void ConfirmRetentionDays()
    {
        ComicDiskCacheService.Instance.RetentionDays = SelectedRetentionDays;
        IsRetentionDialogOpen = false;
        RefreshDownloadCacheSummary();
        string text = SelectedRetentionDays <= 0 ? "Vĩnh viễn (Không bao giờ xóa)" : $"{SelectedRetentionDays} ngày";
        AddLog("INFO", $"✓ Đã lưu thời hạn lưu trữ tệp nén: {text}");
    }

    [RelayCommand]
    public void ClearThumbnails()
    {
        ComicDiskCacheService.Instance.ClearThumbnails();
        RefreshDownloadCacheSummary();
        AddLog("INFO", "✓ Đã dọn dẹp sạch bộ nhớ đệm ảnh bìa (Thumbnails)");
    }

    [RelayCommand]
    public void ClearArchiveCache()
    {
        ComicDiskCacheService.Instance.ClearArchiveCache();
        RefreshDownloadCacheSummary();
        AddLog("INFO", "✓ Đã dọn dẹp bộ nhớ đệm tệp nén & tài liệu (.zip, .cbz, .rar, .cbr, .pdf, .mobi)");
    }

    /// <summary>
    /// Điều khiển hiển thị thanh công cụ nổi FAB. Tự động ẩn khi có Modal mở để chống che khuất UI.
    /// </summary>
    public bool IsFloatingQuickControlVisible => !IsDownloadCacheModalOpen && (LibraryVm == null || !LibraryVm.IsCacheDataModalOpen);
}

