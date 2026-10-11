using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicDownloaderGMTPC.Models;

public partial class ReaderPageItem : ObservableObject
{
    public int Index { get; set; }

    public int DisplayPageNumber => Index + 1;

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Đường dẫn file ảnh trên đĩa hoặc đường dẫn tới file nén .cbz/.zip.
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Tên entry bên trong file .cbz/.zip (rỗng nếu là file ảnh rời trên đĩa).
    /// </summary>
    public string ArchiveEntryFullName { get; set; } = string.Empty;

    public bool IsArchiveEntry => !string.IsNullOrEmpty(ArchiveEntryFullName);

    public bool IsPdfEntry { get; set; }

    public int PdfPageIndex { get; set; }

    public long FileSizeBytes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isLoaded;

    public bool HasImage => Image != null;
}

