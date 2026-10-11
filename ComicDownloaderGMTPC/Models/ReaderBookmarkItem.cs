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
}

