using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicDownloaderGMTPC.Models;

public partial class LibraryComicItem : ObservableObject
{
    public string Title { get; set; } = string.Empty;

    public string RootFolderPath { get; set; } = string.Empty;

    public string CategoryName { get; set; } = string.Empty;

    /// <summary>
    /// Đánh dấu bộ truyện được nạp từ Lịch sử đọc toàn cục (nằm ngoài thư mục thư viện đang chọn).
    /// </summary>
    public bool IsExternalHistoryItem { get; set; }

    public int LastReadOrder { get; set; }

    public string SourceLocationLabel { get; set; } = string.Empty;

    public bool HasSourceLocationLabel => !string.IsNullOrWhiteSpace(SourceLocationLabel);

    public string SourceBadgeBackground => IsExternalHistoryItem ? "#4C1D95" : "#1E293B";

    public string SourceBadgeForeground => IsExternalHistoryItem ? "#DDD6FE" : "#94A3B8";

    public string? CoverFilePath { get; set; }

    public string? CoverArchivePath { get; set; }

    public string? CoverArchiveEntry { get; set; }

    public string? CoverPdfPath { get; set; }

    public string? CoverMobiPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    private Bitmap? _cover;

    public bool HasCover => Cover != null;

    public ObservableCollection<LibraryChapterItem> Chapters { get; set; } = new();

    public int TotalChapters => Chapters.Count;

    public int TotalPages => Chapters.Count > 0 ? Chapters.Sum(c => c.TotalPages) : 0;

    public string ChapterCountText => TotalPages > 0
        ? (Chapters.Count <= 1 ? $"{TotalPages} trang" : $"{Chapters.Count} chương ({TotalPages} trang)")
        : $"{Chapters.Count} chương";

    [ObservableProperty]
    private string _readStatusText = "Chưa đọc";

    [ObservableProperty]
    private string _readStatusBackground = "#334155";

    [ObservableProperty]
    private double _overallProgressPercent;

    public bool HasAnyReadProgress => Chapters.Any(c => c.LastReadPage > 0);

    public bool IsAllChaptersRead => Chapters.Count > 0 && Chapters.All(c => c.IsCompletedRead);

    public LibraryChapterItem? GetBestResumeChapter()
    {
        if (Chapters.Count == 0) return null;

        int savedActiveIdx = Services.ReadProgressStore.Instance.GetLastActiveChapterIndex(RootFolderPath);
        if (savedActiveIdx <= 0 && !string.IsNullOrWhiteSpace(Title))
        {
            savedActiveIdx = Services.ReadProgressStore.Instance.GetLastActiveChapterIndex(Title);
        }

        if (savedActiveIdx > 0)
        {
            var savedCh = Chapters.FirstOrDefault(c => c.Index == savedActiveIdx);
            if (savedCh != null)
            {
                // Nếu savedCh là mục 'Toàn bộ' nhưng chưa từng đọc thật sự (trang <= 1)
                // và có folder con phía sau, ưu tiên mở folder đầu tiên
                bool isAllPages = IsAllPagesChapter(savedCh);
                if (!isAllPages || savedCh.LastReadPage > 1)
                {
                    return savedCh;
                }
            }
        }

        string? firstLocalPath = Chapters[0].LocalPath;
        if (!string.IsNullOrWhiteSpace(firstLocalPath) &&
            !string.Equals(firstLocalPath, RootFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            int savedFileIdx = Services.ReadProgressStore.Instance.GetLastActiveChapterIndex(firstLocalPath);
            if (savedFileIdx > 0)
            {
                var savedCh = Chapters.FirstOrDefault(c => c.Index == savedFileIdx);
                if (savedCh != null)
                {
                    bool isAllPages = IsAllPagesChapter(savedCh);
                    if (!isAllPages || savedCh.LastReadPage > 1)
                    {
                        return savedCh;
                    }
                }
            }
        }

        // Tra cứu trực tiếp từ resume.md (CrossDeviceResumeEntry) dùng chung
        // Đảm bảo thiết bị mới dù chưa từng mở truyện này trong lịch sử vẫn tự động mở chính xác chương đã xem!
        var resumeEntry = Services.ReadProgressStore.Instance.FindResumeEntryForComic(Title, RootFolderPath);
        if (resumeEntry != null)
        {
            LibraryChapterItem? matchCh = null;

            // 1. So khớp theo số chương (ChapterNumber)
            if (resumeEntry.ChapterNumber >= 0)
            {
                matchCh = Chapters.FirstOrDefault(c =>
                    Math.Abs(Services.ReadProgressStore.ExtractChapterNumber(c.Title) - resumeEntry.ChapterNumber) < 0.001);
            }

            // 2. So khớp theo tên chương (ChapterTitle)
            if (matchCh == null && !string.IsNullOrWhiteSpace(resumeEntry.ChapterTitle))
            {
                string normTarget = Services.ReadProgressStore.NormalizeChapterName(resumeEntry.ChapterTitle);
                matchCh = Chapters.FirstOrDefault(c =>
                    string.Equals(Services.ReadProgressStore.NormalizeChapterName(c.Title), normTarget, StringComparison.OrdinalIgnoreCase) ||
                    c.Title.Contains(resumeEntry.ChapterTitle, StringComparison.OrdinalIgnoreCase));
            }

            // 3. So khớp theo ChapterSuffix (dành cho file zip/pdf đa chương)
            if (matchCh == null && !string.IsNullOrWhiteSpace(resumeEntry.ChapterSuffix))
            {
                matchCh = Chapters.FirstOrDefault(c =>
                    (!string.IsNullOrEmpty(c.ProgressKey) && c.ProgressKey.EndsWith(resumeEntry.ChapterSuffix, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(c.LocalPath) && c.LocalPath.EndsWith(resumeEntry.ChapterSuffix, StringComparison.OrdinalIgnoreCase)));
            }

            // 4. So khớp theo chỉ số chương (ChapterIndex)
            if (matchCh == null && resumeEntry.ChapterIndex > 0)
            {
                matchCh = Chapters.FirstOrDefault(c => c.Index == resumeEntry.ChapterIndex);
            }

            if (matchCh != null)
            {
                if (resumeEntry.LastReadPage > 0 && matchCh.LastReadPage <= 0)
                {
                    matchCh.LastReadPage = resumeEntry.LastReadPage;
                }
                Services.ReadProgressStore.Instance.SaveLastActiveChapterIndex(RootFolderPath, matchCh.Index);
                if (!string.IsNullOrWhiteSpace(Title))
                {
                    Services.ReadProgressStore.Instance.SaveLastActiveChapterForTitle(Title, matchCh.Index);
                }
                if (matchCh.LastReadPage > 0)
                {
                    Services.ReadProgressStore.Instance.SaveProgress(matchCh.EffectiveProgressKey, Math.Max(0, matchCh.LastReadPage - 1));
                }
                return matchCh;
            }
        }

        // Ưu tiên chương đang đọc dở (LastReadPage > 0 và chưa hoàn thành)
        var inProgress = Chapters.LastOrDefault(c => c.LastReadPage > 0 && !c.IsCompletedRead);
        if (inProgress != null)
        {
            return inProgress;
        }

        // Nếu các chương đã đọc đều đã hoàn thành 100%, ưu tiên mở chương kế tiếp sau chương đã đọc cuối cùng;
        // nếu đã là chương cuối cùng của bộ truyện, mở lại chính chương đó
        var lastReadChapter = Chapters.LastOrDefault(c => c.LastReadPage > 0);
        if (lastReadChapter != null)
        {
            int lastIdx = Chapters.IndexOf(lastReadChapter);
            if (lastReadChapter.IsCompletedRead && lastIdx >= 0 && lastIdx + 1 < Chapters.Count)
            {
                return Chapters[lastIdx + 1];
            }
            return lastReadChapter;
        }

        // Mở lần đầu: nếu có mục 'Toàn bộ truyện' ở đầu và có các folder chương con thực sự phía sau,
        // ưu tiên mở folder chương đầu tiên trước thay vì mở toàn bộ!
        if (Chapters.Count > 1 && IsAllPagesChapter(Chapters[0]))
        {
            return Chapters[1];
        }

        return Chapters[0];
    }

    private static bool IsAllPagesChapter(LibraryChapterItem ch)
    {
        if (ch == null) return false;
        return (ch.Title != null && ch.Title.StartsWith("Toàn bộ", StringComparison.OrdinalIgnoreCase)) ||
               (ch.ProgressKey != null && ch.ProgressKey.Contains("_all_pages", StringComparison.OrdinalIgnoreCase));
    }

    public void RefreshReadStatus()
    {
        if (Chapters.Count == 0)
        {
            ReadStatusText = "0 chương";
            ReadStatusBackground = "#334155";
            OverallProgressPercent = 0;
            return;
        }

        int completedChapters = Chapters.Count(c => c.IsCompletedRead);
        var inProgressChapter = GetBestResumeChapter();
        if (inProgressChapter != null && inProgressChapter.LastReadPage <= 0 && !Chapters.Any(c => c.LastReadPage > 0))
        {
            inProgressChapter = null;
        }

        if (completedChapters >= Chapters.Count)
        {
            ReadStatusText = $"✓ Đã đọc hết ({Chapters.Count} chương)";
            ReadStatusBackground = "#059669";
            OverallProgressPercent = 100.0;
        }
        else if (inProgressChapter != null)
        {
            ReadStatusText = $"Đang đọc: {inProgressChapter.Title} (Tr.{Math.Max(1, inProgressChapter.LastReadPage)})";
            ReadStatusBackground = "#D97706";
            double sumRatio = Chapters.Sum(c => c.ProgressPercent / 100.0);
            OverallProgressPercent = Math.Clamp(sumRatio / Chapters.Count * 100.0, 2.0, 99.0);
        }
        else
        {
            ReadStatusText = "Mới / Chưa đọc";
            ReadStatusBackground = "#334155";
            OverallProgressPercent = 0;
        }

        if (HasAnyReadProgress && !string.IsNullOrWhiteSpace(RootFolderPath))
        {
            Services.ReadProgressStore.Instance.RegisterReadComic(RootFolderPath, touchTimestamp: false);
            LastReadOrder = Math.Max(LastReadOrder, Services.ReadProgressStore.Instance.GetComicLastReadOrder(RootFolderPath));
        }

        OnPropertyChanged(nameof(HasAnyReadProgress));
        OnPropertyChanged(nameof(IsAllChaptersRead));
        OnPropertyChanged(nameof(TotalChapters));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(ChapterCountText));
    }
}


