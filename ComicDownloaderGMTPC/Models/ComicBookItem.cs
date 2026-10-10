using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicDownloaderGMTPC.Models;

public partial class ComicBookItem : ObservableObject
{
    [ObservableProperty]
    private int _index;

    [ObservableProperty]
    private bool _isChecked = true;

    [ObservableProperty]
    private bool _isSelectedInGrid;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _domain = string.Empty;

    [ObservableProperty]
    private int _totalChapters;

    [ObservableProperty]
    private int _downloadedChapters;

    [ObservableProperty]
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
            if (Chapters != null && Chapters.Count > 0 && !string.IsNullOrWhiteSpace(Chapters[^1].Title))
                return Chapters[^1].Title;
            if (TotalChapters > 0)
                return $"Chương {TotalChapters}";
            return "N/A";
        }
    }

    [ObservableProperty]
    private string _preferredLanguage = "vi";

    [ObservableProperty]
    private bool _isDuplicate;

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

    public void UpdateProgress(int currentChapter, int totalChapters, int currentPage, int totalPages)
    {
        DownloadedChapters = currentChapter;
        TotalChapters = Math.Max(1, totalChapters);
        DownloadedPages = currentPage;
        TotalPages = totalPages;

        double chPercent = (double)currentChapter / TotalChapters * 100.0;
        ProgressPercentage = Math.Round(chPercent, 1);

        if (totalPages > 0)
        {
            DetailProgressText = $"Chương {currentChapter}/{TotalChapters} (Trang {currentPage}/{totalPages}) • {ProgressPercentage:0.0}%";
        }
        else
        {
            DetailProgressText = $"Chương {currentChapter}/{TotalChapters} • {ProgressPercentage:0.0}%";
        }
    }

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
