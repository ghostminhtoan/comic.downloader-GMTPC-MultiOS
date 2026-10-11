using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicDownloaderGMTPC.Models;

public enum ImageFitMode
{
    FitWidth = 0,    // Khớp chiều ngang (Fit Width)
    FitHeight = 1,   // Khớp chiều dọc (Fit Height)
    FitScreen = 2,   // Khớp trọn vẹn màn hình (Fit Screen / Best Fit)
    StretchFull = 3, // Kéo giãn tràn màn hình (Stretch Full)
    Original = 4     // Kích thước gốc 1:1 (Original)
}

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
    private double _displayWidth = 900;

    [ObservableProperty]
    private double _displayHeight = 1350;

    [ObservableProperty]
    private Stretch _imageStretch = Stretch.Uniform;

    [ObservableProperty]
    private Thickness _pageMargin = new(0);

    public void RecalculateDimensions(ImageFitMode fitMode, double viewportWidth, double viewportHeight, double zoomLevel)
    {
        double effectiveZoom = Math.Max(0.2, zoomLevel);
        double vw = Math.Max(240, viewportWidth);
        double vh = Math.Max(240, viewportHeight);
        int aw = _aspectWidth > 0 ? _aspectWidth : 900;
        int ah = _aspectHeight > 0 ? _aspectHeight : 1350;

        switch (fitMode)
        {
            case ImageFitMode.FitWidth:
                DisplayWidth = Math.Round(vw * effectiveZoom);
                DisplayHeight = Math.Max(180, Math.Round(DisplayWidth * ah / aw));
                ImageStretch = Stretch.Uniform;
                break;

            case ImageFitMode.FitHeight:
                DisplayHeight = Math.Round(vh * effectiveZoom);
                DisplayWidth = Math.Max(120, Math.Round(DisplayHeight * aw / ah));
                ImageStretch = Stretch.Uniform;
                break;

            case ImageFitMode.FitScreen:
                double scaleW = vw / aw;
                double scaleH = vh / ah;
                double scale = Math.Min(scaleW, scaleH) * effectiveZoom;
                DisplayWidth = Math.Round(aw * scale);
                DisplayHeight = Math.Round(ah * scale);
                ImageStretch = Stretch.Uniform;
                break;

            case ImageFitMode.StretchFull:
                DisplayWidth = Math.Round(vw * effectiveZoom);
                DisplayHeight = Math.Round(vh * effectiveZoom);
                ImageStretch = Stretch.Fill;
                break;

            case ImageFitMode.Original:
                DisplayWidth = Math.Round(aw * effectiveZoom);
                DisplayHeight = Math.Round(ah * effectiveZoom);
                ImageStretch = Stretch.Uniform;
                break;
        }
    }

    public void SetKnownDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (_aspectWidth == width && _aspectHeight == height) return;
        _aspectWidth = width;
        _aspectHeight = height;
        OnPropertyChanged(nameof(AspectWidth));
        OnPropertyChanged(nameof(AspectHeight));
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
