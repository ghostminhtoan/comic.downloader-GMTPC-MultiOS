using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ComicDownloaderGMTPC.Services;

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
    /// Tên entry bên trong file .cbz/.zip/.rar/.cbr (rỗng nếu là file ảnh rời trên đĩa).
    /// </summary>
    public string ArchiveEntryFullName { get; set; } = string.Empty;

    public bool IsArchiveEntry => !string.IsNullOrEmpty(ArchiveEntryFullName);

    public bool IsRarEntry { get; set; }

    public bool IsPdfEntry { get; set; }

    public int PdfPageIndex { get; set; }

    public bool IsMobiEntry { get; set; }

    public int MobiPageIndex { get; set; }

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

    private ImageFitMode _lastFitMode = ImageFitMode.FitWidth;
    private double _lastVw = 0;
    private double _lastVh = 0;
    private double _lastZoom = 1.0;

    public void RecalculateDimensions(ImageFitMode fitMode, double viewportWidth, double viewportHeight, double zoomLevel)
    {
        _lastFitMode = fitMode;
        _lastVw = viewportWidth;
        _lastVh = viewportHeight;
        _lastZoom = zoomLevel;

        double effectiveZoom = Math.Max(0.2, zoomLevel);
        double vw = Math.Max(240, viewportWidth);
        double vh = Math.Max(240, viewportHeight);
        int aw = _aspectWidth > 0 ? _aspectWidth : 900;
        int ah = _aspectHeight > 0 ? _aspectHeight : 1350;

        switch (fitMode)
        {
            case ImageFitMode.FitWidth:
                DisplayWidth = Math.Round(vw * effectiveZoom);
                DisplayHeight = Math.Max(1, Math.Round(DisplayWidth * ah / aw));
                ImageStretch = Stretch.Fill;
                break;

            case ImageFitMode.FitHeight:
                DisplayHeight = Math.Round(vh * effectiveZoom);
                DisplayWidth = Math.Max(1, Math.Round(DisplayHeight * aw / ah));
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
        bool changed = _aspectWidth != width || _aspectHeight != height;
        _aspectWidth = width;
        _aspectHeight = height;
        if (changed)
        {
            OnPropertyChanged(nameof(AspectWidth));
            OnPropertyChanged(nameof(AspectHeight));
            if (_lastVw > 0 && _lastVh > 0)
            {
                RecalculateDimensions(_lastFitMode, _lastVw, _lastVh, _lastZoom);
            }
        }
    }

    public void SetKnownDimensionsFast(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        _aspectWidth = width;
        _aspectHeight = height;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private bool _isAnimated;

    public AnimatedPagePlayer? AnimationPlayer { get; set; }

    public void SetAnimationSpeed(double speed)
    {
        if (AnimationPlayer != null)
        {
            AnimationPlayer.Speed = speed;
        }
    }

    public bool HasImage => Image != null;

    /// <summary>
    /// Giải phóng toàn bộ bộ nhớ và dừng phát hoạt ảnh (nếu có) khi trang cuộn ra xa khỏi tầm nhìn.
    /// </summary>
    public void Unload()
    {
        try
        {
            AnimationPlayer?.Dispose();
        }
        catch { }
        AnimationPlayer = null;

        var old = Image;
        Image = null;
        IsLoaded = false;
        IsLoading = false;
        try
        {
            old?.Dispose();
        }
        catch { }
    }
}
