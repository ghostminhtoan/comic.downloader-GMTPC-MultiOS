using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicDownloaderGMTPC.Models;

public partial class ComicBookItem : ObservableObject
{
    [ObservableProperty]
    private int _index;

    [ObservableProperty]
    private int _originalIndex;

    [ObservableProperty]
    private bool _isChecked = true;

    [ObservableProperty]
    private bool _isSelectedInGrid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueRowBackground))]
    [NotifyPropertyChangedFor(nameof(QueueRowBorderBrush))]
    [NotifyPropertyChangedFor(nameof(QueueRowBorderThickness))]
    [NotifyPropertyChangedFor(nameof(ThumbnailCardBackground))]
    [NotifyPropertyChangedFor(nameof(ThumbnailCardBorderBrush))]
    [NotifyPropertyChangedFor(nameof(ThumbnailCardBorderThickness))]
    private bool _isHighlighted = false;

    public string QueueRowBackground => IsHighlighted ? "#1E3A5F" : "#131B2E";
    public string QueueRowBorderBrush => IsHighlighted ? "#00E5FF" : "#1E293B";
    public Avalonia.Thickness QueueRowBorderThickness => IsHighlighted ? new Avalonia.Thickness(3.5, 1, 1, 1) : new Avalonia.Thickness(0, 0, 0, 1);

    public string ThumbnailCardBackground => IsHighlighted ? "#1A2E4C" : "#091018";
    public string ThumbnailCardBorderBrush => IsHighlighted ? "#F59E0B" : "#1E293B";
    public Avalonia.Thickness ThumbnailCardBorderThickness => IsHighlighted ? new Avalonia.Thickness(2.5) : new Avalonia.Thickness(1);

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _rawTitle = string.Empty;

    [ObservableProperty]
    private bool _hasOrderPrefixApplied = false;

    [ObservableProperty]
    private int _appliedOrderNumber = 0;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _domain = string.Empty;

    [ObservableProperty]
    private int _totalChapters;

    [ObservableProperty]
    private int _downloadedChapters;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestChapterDisplay))]
    private int _totalPages;

    [ObservableProperty]
    private int _downloadedPages;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressBarBrush))]
    [NotifyPropertyChangedFor(nameof(DisplayStatusVi))]
    private string _status = "Waiting"; // Waiting, Downloading, Completed, Error, Stopped, Paused, Moving

    public string DisplayStatusVi => Status switch
    {
        "Waiting" => "Chờ tải",
        "Ready" => "Chờ tải",
        "Extracting..." => "Đang trích xuất...",
        "Downloading" => "Đang tải",
        "Completed" => "Hoàn tất",
        "Hoàn tất" => "Hoàn tất",
        "Error" => "Lỗi",
        "Stopped" => "Đã dừng",
        "Paused" => "Tạm dừng",
        "Moving" => "Đang di chuyển",
        _ => string.IsNullOrWhiteSpace(Status) ? "Chờ tải" : Status
    };

    public string ProgressBarBrush => Status switch
    {
        "Moving" => "#F59E0B",
        "Completed" => "#10B981",
        "Hoàn tất" => "#10B981",
        "Error" => "#EF4444",
        "Stopped" => "#6B7280",
        "Paused" => "#EAB308",
        _ => "#0284C7"
    };

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private string _progressPercentageText = "0%";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThumbnailProcessDisplay))]
    private string _detailProgressText = "Sẵn sàng";

    public string ThumbnailProcessDisplay =>
        string.IsNullOrWhiteSpace(DetailProgressText) || DetailProgressText == "Sẵn sàng"
            ? "-"
            : DetailProgressText;

    [ObservableProperty]
    private string _chapterSelectionText = string.Empty; // Cú pháp chọn chapter: ví dụ 1-10; 12.5; Oneshot

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private string _coverUrl = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnailFile))]
    private string? _thumbnailLocalPath;

    [ObservableProperty]
    private string? _originalCoverLocalPath;

    public bool HasThumbnailFile => !string.IsNullOrWhiteSpace(ThumbnailLocalPath) && System.IO.File.Exists(ThumbnailLocalPath);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectivePopupBitmap))]
    private Avalonia.Media.Imaging.Bitmap? _thumbnailBitmap;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectivePopupBitmap))]
    private Avalonia.Media.Imaging.Bitmap? _highResPreviewBitmap;

    public Avalonia.Media.Imaging.Bitmap? EffectivePopupBitmap => HighResPreviewBitmap ?? ThumbnailBitmap;

    [ObservableProperty]
    private bool _isThumbnailLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewTags))]
    private string _previewTagsText = string.Empty;

    public bool HasPreviewTags => !string.IsNullOrWhiteSpace(PreviewTagsText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewLanguage))]
    [NotifyPropertyChangedFor(nameof(HasPreviewTypeOrLanguage))]
    [NotifyPropertyChangedFor(nameof(HasBothPreviewTypeAndLanguage))]
    private string _previewLanguage = string.Empty;

    public bool HasPreviewLanguage => !string.IsNullOrWhiteSpace(PreviewLanguage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewType))]
    [NotifyPropertyChangedFor(nameof(HasPreviewTypeOrLanguage))]
    [NotifyPropertyChangedFor(nameof(HasBothPreviewTypeAndLanguage))]
    private string _previewType = string.Empty;

    public bool HasPreviewType => !string.IsNullOrWhiteSpace(PreviewType);

    public bool HasPreviewTypeOrLanguage => HasPreviewType || HasPreviewLanguage;
    public bool HasBothPreviewTypeAndLanguage => HasPreviewType && HasPreviewLanguage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewArtist))]
    private string _previewArtist = string.Empty;

    public bool HasPreviewArtist => !string.IsNullOrWhiteSpace(PreviewArtist);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewParody))]
    private string _previewParody = string.Empty;

    public bool HasPreviewParody => !string.IsNullOrWhiteSpace(PreviewParody);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MissingChapterDisplay))]
    [NotifyPropertyChangedFor(nameof(HasMissingChapterIssue))]
    [NotifyPropertyChangedFor(nameof(MissingChapterBrush))]
    private string _missingChapters = string.Empty;

    public bool HasMissingChapterIssue =>
        !string.IsNullOrWhiteSpace(MissingChapters) &&
        !MissingChapters.Contains("Đủ", StringComparison.OrdinalIgnoreCase) &&
        !MissingChapters.Contains("Không thiếu", StringComparison.OrdinalIgnoreCase) &&
        !MissingChapters.Equals("Complete", StringComparison.OrdinalIgnoreCase);

    public string MissingChapterDisplay =>
        HasMissingChapterIssue ? MissingChapters : "đủ chapter";

    public string MissingChapterBrush =>
        HasMissingChapterIssue ? "#FF2A85" : "#00E5FF";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestChapterDisplay))]
    private string _latestChapter = string.Empty;

    public string LatestChapterDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(LatestChapter) && !LatestChapter.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                return LatestChapter;
            if (TotalPages > 0)
                return $"{TotalPages} Pages";
            if (Chapters != null && Chapters.Count > 0 && !string.IsNullOrWhiteSpace(Chapters[^1].Title))
                return Chapters[^1].Title;
            if (TotalChapters > 1)
                return $"Chương {TotalChapters}";
            if (TotalChapters == 1)
                return TotalPages > 0 ? $"{TotalPages} Pages" : "Chương 1";
            return "N/A";
        }
    }

    [ObservableProperty]
    private string _preferredLanguage = "vi";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleBrush))]
    private bool _isDuplicate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleBrush))]
    private bool _isCensorshipColorDuplicate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleBrush))]
    private bool _isNumberedVariantDuplicate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleBrush))]
    private bool _isCensorshipUncensoredVariant;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleBrush))]
    private bool _isCensorshipFullColorVariant;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleBrush))]
    private bool _isImageDuplicate;

    [ObservableProperty]
    private ulong _coverDHash;

    [ObservableProperty]
    private string? _imageDuplicateGroupId;

    /// <summary>
    /// Màu sắc tiêu đề truyện chuẩn Color Rules Cyberpunk từ WPF
    /// </summary>
    public string TitleBrush
    {
        get
        {
            if (IsNumberedVariantDuplicate && IsCensorshipUncensoredVariant) return "#A3E635"; // Lime (#FF9DFF00)
            if (IsNumberedVariantDuplicate && IsCensorshipFullColorVariant) return "#38BDF8"; // Bright Blue (#00CCFF)
            if (IsCensorshipUncensoredVariant && IsCensorshipFullColorVariant) return "#F43F5E"; // Pink (#FF0055)
            if (IsCensorshipUncensoredVariant) return "#FFE600"; // Yellow (#FFE600)
            if (IsCensorshipFullColorVariant) return "#E879F9"; // Light Purple (#FFD78B)
            if (IsNumberedVariantDuplicate) return "#2DD4BF"; // Mint (#33FFCC)
            if (IsImageDuplicate) return "#FB7185"; // Rose/Coral cho truyện trùng ảnh bìa
            if (IsDuplicate || IsCensorshipColorDuplicate) return "#00E5FF"; // Cyan (#00F0FF)
            return "#FDE047"; // Vàng mặc định nguyên bản
        }
    }

    public void NotifyTitleBrushChanged()
    {
        OnPropertyChanged(nameof(TitleBrush));
    }

    [ObservableProperty]
    private bool _isFilteredOut = false;

    [ObservableProperty]
    private string _localDirectory = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplaySubfolder))]
    private string _subfolder = string.Empty;

    public string DisplaySubfolder => string.IsNullOrWhiteSpace(Subfolder) ? "[default]" : Subfolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubfolderLockIcon))]
    [NotifyPropertyChangedFor(nameof(SubfolderLockTooltip))]
    private bool _isSubfolderLocked = false;

    public string SubfolderLockIcon => IsSubfolderLocked ? "🔒" : "🔓";
    public string SubfolderLockTooltip => IsSubfolderLocked ? "Đang khóa Subfolder (Click để mở khóa)" : "Subfolder đang mở (Click để khóa)";

    // Phân cấp Tree Folder cho tính năng Tự động tách chương (Auto Split Chapters)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasParallelSplitChildren))]
    [NotifyPropertyChangedFor(nameof(ParallelSplitToggleText))]
    private bool _isParallelSplitParent = false;

    [ObservableProperty]
    private bool _isParallelSplitChild = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ParallelSplitToggleText))]
    private bool _isParallelSplitCollapsed = false;

    [ObservableProperty]
    private string _parallelSplitParentUrl = string.Empty;

    public List<ComicBookItem> ParallelSplitChildren { get; set; } = new();

    public bool HasParallelSplitChildren => IsParallelSplitParent && ParallelSplitChildren != null && ParallelSplitChildren.Count > 0;

    public string ParallelSplitToggleText =>
        !HasParallelSplitChildren ? string.Empty :
        (IsParallelSplitCollapsed ? $"[+] Xem thêm {ParallelSplitChildren.Count} phần" : "[-] Thu gọn");

    public void NotifySplitHierarchyChanged()
    {
        OnPropertyChanged(nameof(HasParallelSplitChildren));
        OnPropertyChanged(nameof(ParallelSplitToggleText));
    }

    public List<ChapterItem> Chapters { get; set; } = new();

    partial void OnProgressPercentageChanged(double value)
    {
        ProgressPercentageText = $"{Math.Max(0, Math.Min(100, value)):0.0}%";
    }

    private long _lastUiProgressTick;

    public void UpdateProgress(int currentChapter, int totalChapters, int currentPage, int totalPages)
    {
        UpdateProgressThrottled(currentChapter, totalChapters, currentPage, totalPages, null, force: true);
    }

    public void UpdateProgressThrottled(int currentChapter, int totalChapters, int currentPage, int totalPages, string? statusMsg = null, bool force = false)
    {
        int safeTotalChapters = Math.Max(1, totalChapters);
        double chPercent = (double)currentChapter / safeTotalChapters * 100.0;
        double roundedPercent = Math.Round(chPercent, 1);

        long now = Environment.TickCount64;
        long minIntervalMs = OperatingSystem.IsAndroid() ? 450 : 180;
        if (!force && now - _lastUiProgressTick < minIntervalMs)
        {
            return;
        }

        _lastUiProgressTick = now;

        void ApplyOnUi()
        {
            DownloadedChapters = currentChapter;
            TotalChapters = safeTotalChapters;
            DownloadedPages = currentPage;
            TotalPages = totalPages;
            ProgressPercentage = roundedPercent;

            if (totalPages > 0)
            {
                DetailProgressText = $"Chương {currentChapter}/{safeTotalChapters} (Trang {currentPage}/{totalPages}) • {roundedPercent:0.0}%";
            }
            else
            {
                DetailProgressText = $"Chương {currentChapter}/{safeTotalChapters} • {roundedPercent:0.0}%";
            }

            if (statusMsg != null)
            {
                StatusMessage = statusMsg;
            }
        }

        if (Avalonia.Application.Current == null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            ApplyOnUi();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyOnUi);
        }
    }

    #region Fast Search Index Cache
    private string? _normalizedSearchIndex;
    private string? _normalizedTitle;
    private string? _normalizedUrl;
    private string? _normalizedTags;

    public void InvalidateSearchIndex()
    {
        _normalizedSearchIndex = null;
        _normalizedTitle = null;
        _normalizedUrl = null;
        _normalizedTags = null;
    }

    public string GetNormalizedSearchIndex()
    {
        if (_normalizedSearchIndex == null)
        {
            BuildSearchIndex();
        }
        return _normalizedSearchIndex!;
    }

    public string GetNormalizedTitle()
    {
        if (_normalizedTitle == null) BuildSearchIndex();
        return _normalizedTitle!;
    }

    public string GetNormalizedUrl()
    {
        if (_normalizedUrl == null) BuildSearchIndex();
        return _normalizedUrl!;
    }

    public string GetNormalizedTags()
    {
        if (_normalizedTags == null) BuildSearchIndex();
        return _normalizedTags!;
    }

    private void BuildSearchIndex()
    {
        _normalizedTitle = RemoveDiacriticsFast(Title).ToLowerInvariant();
        _normalizedUrl = (Url ?? string.Empty).ToLowerInvariant();
        _normalizedTags = RemoveDiacriticsFast(PreviewTagsText).ToLowerInvariant();
        string pLang = RemoveDiacriticsFast(PreviewLanguage).ToLowerInvariant();
        string pType = RemoveDiacriticsFast(PreviewType).ToLowerInvariant();
        string pArt = RemoveDiacriticsFast(PreviewArtist).ToLowerInvariant();
        string pPar = RemoveDiacriticsFast(PreviewParody).ToLowerInvariant();
        string missing = RemoveDiacriticsFast(MissingChapters).ToLowerInvariant();
        string status = RemoveDiacriticsFast(Status).ToLowerInvariant();
        string progress = RemoveDiacriticsFast(DetailProgressText).ToLowerInvariant();
        string chapSel = RemoveDiacriticsFast(ChapterSelectionText).ToLowerInvariant();

        _normalizedSearchIndex = $"{_normalizedTitle} {_normalizedUrl} {_normalizedTags} {pLang} {pType} {pArt} {pPar} {missing} {status} {progress} {chapSel}";
    }

    public static string RemoveDiacriticsFast(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        bool hasNonAscii = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] >= 128)
            {
                hasNonAscii = true;
                break;
            }
        }
        if (!hasNonAscii) return text;

        var normalizedString = text.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(normalizedString.Length);

        for (int i = 0; i < normalizedString.Length; i++)
        {
            char c = normalizedString[i];
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                if (c == 'đ') sb.Append('d');
                else if (c == 'Đ') sb.Append('D');
                else sb.Append(c);
            }
        }

        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    partial void OnTitleChanged(string value)
    {
        if (!HasOrderPrefixApplied && !string.IsNullOrEmpty(value))
        {
            RawTitle = value;
        }
        InvalidateSearchIndex();
    }
    partial void OnUrlChanged(string value) => InvalidateSearchIndex();
    partial void OnPreviewTagsTextChanged(string value) => InvalidateSearchIndex();
    partial void OnPreviewLanguageChanged(string value) => InvalidateSearchIndex();
    partial void OnPreviewTypeChanged(string value) => InvalidateSearchIndex();
    partial void OnPreviewArtistChanged(string value) => InvalidateSearchIndex();
    partial void OnPreviewParodyChanged(string value) => InvalidateSearchIndex();
    partial void OnMissingChaptersChanged(string value) => InvalidateSearchIndex();
    partial void OnStatusChanged(string value) => InvalidateSearchIndex();
    partial void OnDetailProgressTextChanged(string value) => InvalidateSearchIndex();
    partial void OnChapterSelectionTextChanged(string value) => InvalidateSearchIndex();
    #endregion

    public void RestoreDownloadProgress()
    {
        if (TotalChapters > 0)
        {
            double chPercent = (double)DownloadedChapters / TotalChapters * 100.0;
            ProgressPercentage = Math.Round(Math.Clamp(chPercent, 0, 100), 1);
            if (TotalPages > 0)
            {
                DetailProgressText = $"Chương {DownloadedChapters}/{TotalChapters} (Trang {DownloadedPages}/{TotalPages}) • {ProgressPercentage:0.0}%";
            }
            else
            {
                DetailProgressText = DownloadedChapters > 0 ? $"Chương {DownloadedChapters}/{TotalChapters} • {ProgressPercentage:0.0}%" : "Sẵn sàng";
            }
        }
        else
        {
            ProgressPercentage = 0;
            DetailProgressText = "Sẵn sàng";
        }
    }
}
