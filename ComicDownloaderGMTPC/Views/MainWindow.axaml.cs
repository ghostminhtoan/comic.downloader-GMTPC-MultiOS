using System;
using Avalonia.Controls;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.Views;

public partial class MainWindow : Window
{
    private FloatingBubbleWindow? _bubbleWindow;

    public MainWindow()
    {
        InitializeComponent();
        BackgroundExecutionService.Instance.BubbleModeChanged += OnBubbleModeChanged;

        AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, MainWindow_PointerWheelChanged, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, MainWindow_KeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

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
                try
                {
                    if (_bubbleWindow != null)
                    {
                        _bubbleWindow.Close();
                        _bubbleWindow = null;
                    }
                }
                catch { }

                Close();
                Environment.Exit(0);
            });
        };
    }

    private void OnBubbleModeChanged(bool enabled)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (enabled)
            {
                if (_bubbleWindow == null)
                {
                    _bubbleWindow = new FloatingBubbleWindow(this);
                    _bubbleWindow.Closed += (_, _) =>
                    {
                        _bubbleWindow = null;
                        if (BackgroundExecutionService.Instance.IsBubbleMode)
                        {
                            BackgroundExecutionService.Instance.SetBubbleMode(false);
                        }
                    };
                }

                _bubbleWindow.Show();

                // Thu nhỏ MainWindow khi bong bóng nổi kích hoạt để giải phóng không gian làm việc cho người dùng
                WindowState = WindowState.Minimized;
            }
            else
            {
                if (_bubbleWindow != null)
                {
                    var bw = _bubbleWindow;
                    _bubbleWindow = null;
                    try { bw.Close(); } catch { }
                }

                // Khôi phục hiển thị MainWindow kích thước chuẩn ban đầu
                Show();
                WindowState = WindowState.Normal;
                Activate();
            }
        });
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        try
        {
            App.SharedMainViewModel.FlushAutoSaveImmediately();
        }
        catch { }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            App.SharedMainViewModel.FlushAutoSaveImmediately();
        }
        catch { }

        try
        {
            if (_bubbleWindow != null)
            {
                _bubbleWindow.Close();
                _bubbleWindow = null;
            }
        }
        catch { }

        BackgroundExecutionService.Instance.BubbleModeChanged -= OnBubbleModeChanged;
        base.OnClosed(e);
    }

    private void MainWindow_PointerWheelChanged(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & Avalonia.Input.KeyModifiers.Control) != 0)
        {
            if (DataContext is ViewModels.MainViewModel vm)
            {
                if (e.Delta.Y > 0)
                {
                    vm.ZoomInCommand.Execute(null);
                }
                else if (e.Delta.Y < 0)
                {
                    vm.ZoomOutCommand.Execute(null);
                }
                e.Handled = true;
            }
        }
    }

    private void MainWindow_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if ((e.KeyModifiers & Avalonia.Input.KeyModifiers.Control) != 0)
        {
            if (DataContext is ViewModels.MainViewModel vm)
            {
                if (e.Key == Avalonia.Input.Key.OemPlus || e.Key == Avalonia.Input.Key.Add)
                {
                    vm.ZoomInCommand.Execute(null);
                    e.Handled = true;
                }
                else if (e.Key == Avalonia.Input.Key.OemMinus || e.Key == Avalonia.Input.Key.Subtract)
                {
                    vm.ZoomOutCommand.Execute(null);
                    e.Handled = true;
                }
                else if (e.Key == Avalonia.Input.Key.D0 || e.Key == Avalonia.Input.Key.NumPad0)
                {
                    vm.ZoomResetCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }
    }
}
