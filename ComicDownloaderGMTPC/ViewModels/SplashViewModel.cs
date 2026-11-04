using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class SplashViewModel : ViewModelBase
{
    public static SplashViewModel? ActiveInstance { get; private set; }

    [ObservableProperty]
    private int _progressPercentage = 0;

    [ObservableProperty]
    private double _sweepAngle = 0.0;

    [ObservableProperty]
    private string _progressPercentText = "0%";

    [ObservableProperty]
    private string _statusMessage = "Đang khởi tạo hệ thống...";

    public SplashViewModel()
    {
        ActiveInstance = this;
    }

    partial void OnProgressPercentageChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, 100);
        if (_progressPercentage != clamped)
        {
            _progressPercentage = clamped;
        }
        SweepAngle = (clamped / 100.0) * 360.0;
        ProgressPercentText = $"{clamped}%";
    }

    public void ReportProgress(int percent, string message)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ProgressPercentage = clamped;
            if (!string.IsNullOrEmpty(message))
            {
                StatusMessage = message;
            }
        });
    }

    public static void ReportGlobalProgress(int percent, string? message = null)
    {
        ActiveInstance?.ReportProgress(percent, message ?? string.Empty);
    }
}
