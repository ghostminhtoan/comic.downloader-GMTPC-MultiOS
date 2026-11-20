using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.Views;

public partial class MiniHudWindow : Window
{
    private readonly Action? _restoreMainWindowAction;
    private readonly DispatcherTimer _autoHideTimer = new();

    public MiniHudWindow() : this(null)
    {
    }

    public MiniHudWindow(Action? restoreMainWindowAction)
    {
        InitializeComponent();
        _restoreMainWindowAction = restoreMainWindowAction;

        _autoHideTimer.Interval = TimeSpan.FromMilliseconds(3500);
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            Hide();
        };

        Opened += OnWindowOpened;
        Closed += OnWindowClosed;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        BackgroundExecutionService.Instance.TaskProgressChanged -= OnTaskProgressChanged;
        BackgroundExecutionService.Instance.TaskProgressChanged += OnTaskProgressChanged;
        BackgroundExecutionService.Instance.AnyTaskRunningChanged -= OnAnyTaskRunningChanged;
        BackgroundExecutionService.Instance.AnyTaskRunningChanged += OnAnyTaskRunningChanged;
        UpdateContent();
        RestartAutoHideTimer();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _autoHideTimer.Stop();
        BackgroundExecutionService.Instance.TaskProgressChanged -= OnTaskProgressChanged;
        BackgroundExecutionService.Instance.AnyTaskRunningChanged -= OnAnyTaskRunningChanged;
    }

    private void OnTaskProgressChanged(BackgroundTaskInfo task)
    {
        Dispatcher.UIThread.Post(UpdateContent);
    }

    private void OnAnyTaskRunningChanged(bool anyRunning)
    {
        Dispatcher.UIThread.Post(UpdateContent);
    }

    public void RestartAutoHideTimer()
    {
        _autoHideTimer.Stop();
        _autoHideTimer.Start();
    }

    public void CancelAutoHideTimer()
    {
        _autoHideTimer.Stop();
    }

    public void UpdateContent()
    {
        try
        {
            var primary = BackgroundExecutionService.Instance.GetPrimaryTask();
            bool isDownloading = DownloadEngineService.Instance.IsDownloading;
            bool isPaused = DownloadEngineService.Instance.IsPaused;
            string speed = DownloadEngineService.Instance.CurrentSpeedText;

            if (primary != null)
            {
                TitleText.Text = string.IsNullOrWhiteSpace(primary.Title) ? "Comic Downloader GMTPC" : primary.Title;
                int pct = (int)Math.Round(Math.Clamp(primary.ProgressPercentage, 0, 100));
                string detail = string.IsNullOrWhiteSpace(primary.Detail) ? $"{pct}%" : $"{primary.Detail} ({pct}%)";
                StatusText.Text = detail;
                SpeedText.Text = isDownloading ? $"⚡ {speed}" : "🟢 Đang xử lý";
                ProgressBarCtrl.Value = pct;
            }
            else
            {
                TitleText.Text = "Comic Downloader GMTPC";
                StatusText.Text = "Đang ở chế độ chờ";
                SpeedText.Text = "⚪ Nhàn rỗi";
                ProgressBarCtrl.Value = 0;
            }

            // Nút 1: Tải / Dừng
            BtnStartStop.Content = isDownloading ? "⏹️ Dừng" : "▶️ Tải";
            BtnStartStop.Foreground = isDownloading
                ? new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71))
                : new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));

            // Nút 2: Tạm dừng / Tiếp tục
            BtnPlayPause.Content = isPaused ? "▶️ Tiếp tục" : "⏸️ Tạm dừng";
            BtnPlayPause.Foreground = isPaused
                ? new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15))
                : new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            BtnPlayPause.IsEnabled = isDownloading;
            BtnPlayPause.Opacity = isDownloading ? 1.0 : 0.5;
        }
        catch { }
    }

    private void OnStartStopClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        RestartAutoHideTimer();
        try
        {
            BackgroundExecutionService.NativeNotificationToggleDownloadRequested?.Invoke();
            UpdateContent();
        }
        catch { }
    }

    private void OnPlayPauseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        RestartAutoHideTimer();
        try
        {
            BackgroundExecutionService.NativeNotificationTogglePauseRequested?.Invoke();
            UpdateContent();
        }
        catch { }
    }

    private void OnSkipClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        RestartAutoHideTimer();
        try
        {
            BackgroundExecutionService.NativeNotificationContextualAction1Requested?.Invoke();
            UpdateContent();
        }
        catch { }
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button || (e.Source is Visual visual && visual.FindAncestorOfType<Button>() != null))
        {
            return;
        }

        Hide();
        _restoreMainWindowAction?.Invoke();
    }

    private void OnCardPointerEntered(object? sender, PointerEventArgs e)
    {
        CancelAutoHideTimer();
    }

    private void OnCardPointerExited(object? sender, PointerEventArgs e)
    {
        RestartAutoHideTimer();
    }
}
