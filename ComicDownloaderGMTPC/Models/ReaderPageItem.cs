using System;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicDownloaderGMTPC.Models;

public partial class ReaderPageItem : ObservableObject
{
    public int Index { get; set; }

    public int DisplayPageNumber => Index + 1;

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Đường dẫn file ảnh trên đĩa hoặc đường dẫn tới file nén .cbz/.zip/.pdf.
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

    private int _aspectWidth = 900;
    private int _aspectHeight = 1350;

    public int AspectWidth => _aspectWidth;
    public int AspectHeight => _aspectHeight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayHeight))]
    private double _displayWidth = 900;

    /// <summary>
    /// Chiều cao cố định theo đúng tỉ lệ khung hình thực của trang truyện,
    /// giữ nguyên 100% trước khi tải, sau khi tải và sau khi giải phóng RAM để chống nhảy thanh cuộn.
    /// </summary>
    public double DisplayHeight =>
        _aspectWidth > 0 && _aspectHeight > 0
            ? Math.Max(180, Math.Round(DisplayWidth * _aspectHeight / _aspectWidth))
            : Math.Round(DisplayWidth * 1.5);

    public void SetKnownDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (_aspectWidth == width && _aspectHeight == height) return;
        _aspectWidth = width;
        _aspectHeight = height;
        OnPropertyChanged(nameof(AspectWidth));
        OnPropertyChanged(nameof(AspectHeight));
        OnPropertyChanged(nameof(DisplayHeight));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isLoaded;

    public bool HasImage => Image != null;
}


