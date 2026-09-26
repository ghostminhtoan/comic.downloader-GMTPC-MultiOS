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
    private string _status = "Waiting"; // Waiting, Downloading, Completed, Error, Stopped, Paused

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private string _progressPercentageText = "0.0%";

    [ObservableProperty]
    private string _detailProgressText = "Sẵn sàng";

    [ObservableProperty]
    private string _chapterSelectionText = string.Empty; // Cú pháp chọn chapter: ví dụ 1-10; 12.5; Oneshot

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private string _coverUrl = string.Empty;

    [ObservableProperty]
    private string _missingChapters = string.Empty;

    [ObservableProperty]
    private string _latestChapter = string.Empty;

    [ObservableProperty]
    private string _preferredLanguage = "vi";

    [ObservableProperty]
    private bool _isDuplicate;

    [ObservableProperty]
    private string _localDirectory = string.Empty;

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
}
