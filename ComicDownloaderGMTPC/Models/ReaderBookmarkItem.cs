using System;

namespace ComicDownloaderGMTPC.Models;

public sealed class ReaderBookmarkItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ComicRootPath { get; set; } = string.Empty;

    public string ComicTitle { get; set; } = string.Empty;

    public string ChapterLocalPath { get; set; } = string.Empty;

    public string ChapterProgressKey { get; set; } = string.Empty;

    public int ChapterIndex { get; set; } = 1;

    public string ChapterTitle { get; set; } = string.Empty;

    /// <summary>
    /// Chỉ số trang 0-based trong chương.
    /// </summary>
    public int PageIndex { get; set; }

    public int TotalPages { get; set; }

    public string Memo { get; set; } = string.Empty;

    public long CreatedAtUnixSeconds { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public int DisplayPageNumber => PageIndex + 1;

    public string LeftDisplayText
    {
        get
        {
            string baseText = string.IsNullOrWhiteSpace(ChapterTitle)
                ? $"{ComicTitle} {DisplayPageNumber:D3}"
                : $"{ComicTitle} - {ChapterTitle} ({DisplayPageNumber:D3})";

            return string.IsNullOrWhiteSpace(Memo)
                ? baseText
                : $"{baseText}  [{Memo}]";
        }
    }

    public string RightDisplayText =>
        TotalPages > 0 ? $"{DisplayPageNumber} / {TotalPages}" : $"{DisplayPageNumber}";

    /// <summary>
    /// Tên bộ truyện rõ ràng để hiển thị trên Bookmark Item.
    /// </summary>
    public string DisplayComicTitle => !string.IsNullOrWhiteSpace(ComicTitle) ? ComicTitle : (!string.IsNullOrWhiteSpace(ChapterTitle) ? ChapterTitle : "Bộ truyện");

    /// <summary>
    /// Kiểm tra xem tên truyện có khác biệt so với tên chương hay không.
    /// </summary>
    public bool HasSeparateComicTitle => !string.IsNullOrWhiteSpace(ComicTitle) &&
                                         !string.IsNullOrWhiteSpace(ChapterTitle) &&
                                         !string.Equals(ComicTitle.Trim(), ChapterTitle.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tên chương đầy đủ (Full Chapter Title) không bị cắt xén.
    /// </summary>
    public string FullChapterTitle => !string.IsNullOrWhiteSpace(ChapterTitle) ? ChapterTitle : (!string.IsNullOrWhiteSpace(ComicTitle) ? ComicTitle : "Chương không tên");

    /// <summary>
    /// Số trang đầy đủ chi tiết (ví dụ: Trang 12 / 30).
    /// </summary>
    public string FullPageText => TotalPages > 0 ? $"Trang {DisplayPageNumber} / {TotalPages}" : $"Trang {DisplayPageNumber}";

    /// <summary>
    /// Tỷ lệ phần trăm đã đọc trong chương (ví dụ: 40%).
    /// </summary>
    public string PercentText => TotalPages > 0 ? $"{(DisplayPageNumber * 100 / TotalPages)}%" : string.Empty;

    /// <summary>
    /// Ngày giờ tạo bookmark định dạng dễ nhìn.
    /// </summary>
    public string CreatedDateText => DateTimeOffset.FromUnixTimeSeconds(CreatedAtUnixSeconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>
    /// Có ghi chú memo hay không.
    /// </summary>
    public bool HasMemo => !string.IsNullOrWhiteSpace(Memo);
}

