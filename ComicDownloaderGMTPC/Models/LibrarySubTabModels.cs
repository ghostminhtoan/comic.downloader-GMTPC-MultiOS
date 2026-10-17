using System;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicDownloaderGMTPC.Models;

public enum StorageItemType
{
    Recent,
    Device,
    SDCard,
    PinnedFolder
}

public partial class StorageItemModel : ObservableObject
{
    public StorageItemType Type { get; set; } = StorageItemType.Device;

    public string Title { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Subtitle => Path;

    public string IconText { get; set; } = "📁";

    public string IconColor { get; set; } = "#38BDF8";

    public bool IsPinned => Type == StorageItemType.PinnedFolder;
}

public partial class DirectoryItemModel : ObservableObject
{
    public bool IsFolder { get; set; }

    public bool IsImageFile { get; set; }

    public bool CanReadDirectly { get; set; }

    public string Name { get; set; } = string.Empty;

    public string FullPath { get; set; } = string.Empty;

    public string ItemCountOrSize { get; set; } = string.Empty;

    public string IconText { get; set; } = "📁";

    public string IconColor { get; set; } = "#A78BFA";

    public string Extension { get; set; } = string.Empty;

    public DateTime LastModified { get; set; }

    public long FileSizeBytes { get; set; }

    public int ImageIndexInFolder { get; set; }

    public bool HasItemCountOrSize => !string.IsNullOrWhiteSpace(ItemCountOrSize);

    public bool ShowDirectReadButton => CanReadDirectly && IsFolder && !IsImageFile;

    public bool IsNormalFolder => IsFolder && !ShowDirectReadButton;

    public bool IsArchiveOrPdfFile => !IsFolder && !IsImageFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    private Bitmap? _cover;

    public bool HasCover => Cover != null;

    public string LastModifiedDateText => LastModified != DateTime.MinValue ? LastModified.ToString("yyyy-MM-dd") : string.Empty;

    public string FileSizeText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadingProgress))]
    private string _readingProgressText = string.Empty;

    public bool HasReadingProgress => !string.IsNullOrWhiteSpace(ReadingProgressText);

    [ObservableProperty]
    private double _progressPercent;
}

public partial class HistoryItemModel : ObservableObject
{
    public string Path { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string ChapterOrSubTitle { get; set; } = string.Empty;

    public bool HasChapterOrSubTitle => !string.IsNullOrWhiteSpace(ChapterOrSubTitle);

    public string FileSizeText { get; set; } = string.Empty;

    public string LastReadDateText { get; set; } = string.Empty;

    public int CurrentPage { get; set; }

    public int TotalPages { get; set; }

    public string ProgressText { get; set; } = string.Empty;

    public double ProgressPercent { get; set; }

    public string ProgressBarColor { get; set; } = "#E91E63"; // Hồng rực chuẩn ComicScreen

    [ObservableProperty]
    private Bitmap? _cover;
}

public partial class BookmarkGroupModel : ObservableObject
{
    public string ComicTitle { get; set; } = string.Empty;

    public string ComicRootPath { get; set; } = string.Empty;

    public ObservableCollection<BookmarkItemDisplayModel> Bookmarks { get; set; } = new();
}

public partial class BookmarkItemDisplayModel : ObservableObject
{
    public ReaderBookmarkItem Bookmark { get; set; } = new();

    public string ChapterTitle { get; set; } = string.Empty;

    public string PageProgressText { get; set; } = string.Empty;

    public string PercentText { get; set; } = string.Empty;

    public string DateText { get; set; } = string.Empty;

    public string Memo { get; set; } = string.Empty;

    public bool HasMemo => !string.IsNullOrWhiteSpace(Memo);
}

