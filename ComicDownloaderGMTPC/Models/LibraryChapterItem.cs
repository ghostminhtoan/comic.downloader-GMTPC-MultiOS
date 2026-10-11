using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicDownloaderGMTPC.Models;

public partial class LibraryChapterItem : ObservableObject
{
    public int Index { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Đường dẫn tới thư mục chương hoặc đường dẫn tới file .cbz/.zip.
    /// </summary>
    public string LocalPath { get; set; } = string.Empty;

    public bool IsCbz { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(ProgressStatusText))]
    [NotifyPropertyChangedFor(nameof(ProgressBadgeBackground))]
    [NotifyPropertyChangedFor(nameof(ReadButtonLabel))]
    private int _totalPages;

    /// <summary>
    /// Trang cuối đang đọc (1-based: 0 = chưa đọc, 1..TotalPages = trang đang đọc).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(ProgressStatusText))]
    [NotifyPropertyChangedFor(nameof(ProgressBadgeBackground))]
    [NotifyPropertyChangedFor(nameof(ReadButtonLabel))]
    [NotifyPropertyChangedFor(nameof(HasReadProgress))]
    [NotifyPropertyChangedFor(nameof(IsCompletedRead))]
    private int _lastReadPage;

    public DateTime LastModified { get; set; }

    public string TypeBadgeText => IsCbz ? "CBZ" : "FOLDER";

    public string TypeBadgeBackground => IsCbz ? "#7C3AED" : "#0284C7";

    public bool HasReadProgress => LastReadPage > 0;

    public bool IsCompletedRead => TotalPages > 0 && LastReadPage >= TotalPages;

    public double ProgressPercent =>
        TotalPages > 0
            ? Math.Clamp((double)LastReadPage / TotalPages * 100.0, 0.0, 100.0)
            : (LastReadPage > 0 ? 50.0 : 0.0);

    public string ProgressStatusText
    {
        get
        {
            if (LastReadPage <= 0)
            {
                return TotalPages > 0 ? $"Chưa đọc ({TotalPages} trang)" : "Chưa đọc";
            }

            if (TotalPages > 0 && LastReadPage >= TotalPages)
            {
                return $"✓ Đã đọc xong ({TotalPages}/{TotalPages} trang)";
            }

            return TotalPages > 0
                ? $"Đang đọc: Trang {LastReadPage}/{TotalPages} ({ProgressPercent:0}%)"
                : $"Đang đọc: Trang {LastReadPage}";
        }
    }

    public string ProgressBadgeBackground
    {
        get
        {
            if (LastReadPage <= 0) return "#334155";
            if (TotalPages > 0 && LastReadPage >= TotalPages) return "#059669";
            return "#D97706";
        }
    }

    public string ReadButtonLabel =>
        LastReadPage > 1 && (!IsCompletedRead)
            ? $"▶ Đọc tiếp (Tr.{LastReadPage})"
            : "📖 Đọc ngay";
}

