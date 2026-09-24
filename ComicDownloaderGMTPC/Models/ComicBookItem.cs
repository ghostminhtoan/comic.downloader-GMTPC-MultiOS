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
    private string _status = "Waiting"; // Waiting, Downloading, Completed, Error, Stopped, Paused

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private string _coverUrl = string.Empty;

    [ObservableProperty]
    private string _missingChapters = string.Empty;

    [ObservableProperty]
    private string _latestChapter = string.Empty;

    [ObservableProperty]
    private bool _isDuplicate;

    public List<ChapterItem> Chapters { get; set; } = new();
}
