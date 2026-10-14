using System;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicDownloaderGMTPC.Models;

/// <summary>
/// Model đại diện cho một ảnh trong công cụ Dual Picture Sort.
/// </summary>
public partial class PictureSortItem : ObservableObject
{
    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private long _fileSizeBytes;

    [ObservableProperty]
    private string _fileSizeText = string.Empty;

    [ObservableProperty]
    private int _width;

    [ObservableProperty]
    private int _height;

    [ObservableProperty]
    private string _resolutionText = string.Empty;

    [ObservableProperty]
    private DateTime _lastModified;

    [ObservableProperty]
    private string _lastModifiedText = string.Empty;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ItemBgBrush));
        OnPropertyChanged(nameof(ItemBorderBrush));
    }

    public string ItemBgBrush => IsSelected ? "#1E293B" : "#0B1120";
    public string ItemBorderBrush => IsSelected ? "#0284C7" : "#1E293B";

    [ObservableProperty]
    private bool _isLoading;

    public PictureSortItem()
    {
    }

    public PictureSortItem(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        try
        {
            var fi = new FileInfo(filePath);
            if (fi.Exists)
            {
                FileSizeBytes = fi.Length;
                FileSizeText = FormatFileSize(fi.Length);
                LastModified = fi.LastWriteTime;
                LastModifiedText = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            }
        }
        catch
        {
            FileSizeText = "0 KB";
            LastModifiedText = string.Empty;
        }
    }

    public void UpdateDimensions(int width, int height)
    {
        Width = width;
        Height = height;
        ResolutionText = (width > 0 && height > 0) ? $"{width}×{height}" : string.Empty;
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }
}

