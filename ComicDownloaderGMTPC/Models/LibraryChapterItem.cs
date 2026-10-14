using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicDownloaderGMTPC.Models;

public sealed class PdfBookmarkItem
{
    public string Title { get; set; } = string.Empty;
    public int PageIndex { get; set; }
    public int DisplayPageNumber => PageIndex + 1;
    public string DisplayLabel => $"{Title} (Tr.{DisplayPageNumber})";
    public string DisplayText => DisplayLabel;
}

public partial class LibraryChapterItem : ObservableObject
{
    public int Index { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Đường dẫn tới thư mục chương hoặc đường dẫn tới file .cbz/.zip/.pdf.
    /// </summary>
    public string LocalPath { get; set; } = string.Empty;

    /// <summary>
    /// Khóa định danh lưu tiến độ đọc riêng cho chương (hỗ trợ nhiều chương con nằm chung 1 file .pdf hoặc .zip/.cbz).
    /// </summary>
    public string ProgressKey { get; set; } = string.Empty;

    public string EffectiveProgressKey => string.IsNullOrWhiteSpace(ProgressKey) ? LocalPath : ProgressKey;

    public bool IsCbz { get; set; }

    public bool IsRar { get; set; }

    public bool IsPdf { get; set; }

    public bool IsMobi { get; set; }

    /// <summary>
    /// Phạm vi trang (0-based, inclusive) của chương này bên trong file PDF. PdfEndPageIndex = -1 nghĩa là toàn bộ file.
    /// </summary>
    public int PdfStartPageIndex { get; set; } = 0;

    public int PdfEndPageIndex { get; set; } = -1;

    public int MobiStartPageIndex { get; set; } = 0;

    public int MobiEndPageIndex { get; set; } = -1;

    /// <summary>
    /// Danh sách các entry ảnh thuộc riêng chương này bên trong file .zip/.cbz/.rar/.cbr (nếu file nén chứa nhiều chương con).
    /// </summary>
    public List<string>? ArchiveEntryNames { get; set; }

    public List<PdfBookmarkItem> PdfBookmarks { get; set; } = new();

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

    public string TypeBadgeText => IsPdf ? "PDF" : (IsMobi ? "MOBI" : (IsRar ? "CBR" : (IsCbz ? "CBZ" : "FOLDER")));

    public string TypeBadgeBackground => IsPdf ? "#E11D48" : (IsMobi ? "#D97706" : (IsRar ? "#2563EB" : (IsCbz ? "#7C3AED" : "#0284C7")));

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
                if (IsPdf && PdfBookmarks.Count > 0)
                {
                    return TotalPages > 0
                        ? $"Chưa đọc ({TotalPages} trang • {PdfBookmarks.Count} mục lục)"
                        : $"Chưa đọc ({PdfBookmarks.Count} mục lục)";
                }
                return TotalPages > 0 ? $"Chưa đọc ({TotalPages} trang)" : "Chưa đọc";
            }

            if (TotalPages > 0 && LastReadPage >= TotalPages)
            {
                return $"✓ Đã đọc xong ({TotalPages}/{TotalPages} trang)";
            }

            if (IsPdf && PdfBookmarks.Count > 0)
            {
                var activeBm = PdfBookmarks.LastOrDefault(b => b.DisplayPageNumber <= LastReadPage);
                if (activeBm != null && !string.IsNullOrWhiteSpace(activeBm.Title))
                {
                    return TotalPages > 0
                        ? $"Đang đọc: Trang {LastReadPage}/{TotalPages} ({ProgressPercent:0}% • {activeBm.Title})"
                        : $"Đang đọc: Trang {LastReadPage} ({activeBm.Title})";
                }
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

