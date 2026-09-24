using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicDownloaderGMTPC.Models;

public partial class SourceSearchResultItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _domain = string.Empty;

    [ObservableProperty]
    private string _coverUrl = string.Empty;

    [ObservableProperty]
    private string _latestChapter = string.Empty;

    [ObservableProperty]
    private string _author = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;
}
