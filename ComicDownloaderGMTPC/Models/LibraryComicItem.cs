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

    public string? CoverFilePath { get; set; }

    public string? CoverArchivePath { get; set; }

    public string? CoverArchiveEntry { get; set; }

    public string? CoverPdfPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    private Bitmap? _cover;

    public bool HasCover => Cover != null;

    public ObservableCollection<LibraryChapterItem> Chapters { get; set; } = new();

    public int TotalChapters => Chapters.Count;

    public string ChapterCountText => $"{Chapters.Count} chương";

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
        if (savedActiveIdx > 0)
        {
            var savedCh = Chapters.FirstOrDefault(c => c.Index == savedActiveIdx);
            if (savedCh != null)
            {
                return savedCh;
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
                    return savedCh;
                }
            }
        }

        return Chapters.LastOrDefault(c => c.LastReadPage > 0 && !c.IsCompletedRead)
               ?? (Chapters.Any(c => c.LastReadPage > 0) ? Chapters.FirstOrDefault(c => !c.IsCompletedRead) : null)
               ?? Chapters.LastOrDefault(c => c.LastReadPage > 0)
               ?? Chapters[0];
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

        OnPropertyChanged(nameof(HasAnyReadProgress));
        OnPropertyChanged(nameof(IsAllChaptersRead));
        OnPropertyChanged(nameof(TotalChapters));
        OnPropertyChanged(nameof(ChapterCountText));
    }
}


