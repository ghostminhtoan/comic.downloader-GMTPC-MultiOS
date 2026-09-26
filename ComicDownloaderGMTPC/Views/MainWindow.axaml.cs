using Avalonia.Controls;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.Views;

public partial class MainWindow : Window
{
    private double _savedWidth = 1100;
    private double _savedHeight = 750;
    private bool _inBubbleMode = false;

    public MainWindow()
    {
        InitializeComponent();
        BackgroundExecutionService.Instance.BubbleModeChanged += OnBubbleModeChanged;

        BackgroundExecutionService.NativeMinimizeOrHide = () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                WindowState = WindowState.Minimized;
            });
        };

        BackgroundExecutionService.NativeForceExit = () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Close();
                System.Environment.Exit(0);
            });
        };
    }

    private void OnBubbleModeChanged(bool enabled)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (enabled && !_inBubbleMode)
            {
                _savedWidth = Width;
                _savedHeight = Height;
                _inBubbleMode = true;

                MinWidth = 300;
                MinHeight = 180;
                Width = 380;
                Height = 240;
                Topmost = true;
                CanResize = false;
            }
            else if (!enabled && _inBubbleMode)
            {
                _inBubbleMode = false;
                MinWidth = 850;
                MinHeight = 550;
                Width = _savedWidth > 850 ? _savedWidth : 1100;
                Height = _savedHeight > 550 ? _savedHeight : 750;
                Topmost = false;
                CanResize = true;
            }
        });
    }

    protected override void OnClosed(System.EventArgs e)
    {
        BackgroundExecutionService.Instance.BubbleModeChanged -= OnBubbleModeChanged;
        base.OnClosed(e);
    }
}