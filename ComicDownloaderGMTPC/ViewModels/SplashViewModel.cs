using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class SplashViewModel : ViewModelBase
{
    [ObservableProperty]
    private int _progressPercentage = 0;

    [ObservableProperty]
    private double _sweepAngle = 0.0;

    [ObservableProperty]
    private string _progressPercentText = "0%";

    [ObservableProperty]
    private string _statusMessage = "Đang khởi tạo hệ thống...";

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
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ProgressPercentage = percent;
            if (!string.IsNullOrEmpty(message))
            {
                StatusMessage = message;
            }
        });
    }
}

