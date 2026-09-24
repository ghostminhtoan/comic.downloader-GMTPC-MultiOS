using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicDownloaderGMTPC.Models;

public partial class MissingScanItem : ObservableObject
{
    [ObservableProperty]
    private int _index;

    [ObservableProperty]
    private bool _isChecked = true;

    [ObservableProperty]
    private string _domain = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _latestChapter = string.Empty;

    [ObservableProperty]
    private string _missingIntegerChapters = string.Empty;

    [ObservableProperty]
    private string _decimalChapters = string.Empty;

    [ObservableProperty]
    private string _status = "Pending";
}
