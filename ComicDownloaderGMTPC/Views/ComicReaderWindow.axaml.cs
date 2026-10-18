using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ComicDownloaderGMTPC.ViewModels;

namespace ComicDownloaderGMTPC.Views;

public partial class ComicReaderWindow : Window
{
    private ComicReaderViewModel? _subscribedVm;

    public ComicReaderWindow()
    {
        InitializeComponent();

        // Tự động mở ở chế độ FullScreen theo yêu cầu
        WindowState = WindowState.FullScreen;

        // Bắt phím ngay lập tức khi mở Full Screen (không cần click chuột vào giữa màn hình)
        Focusable = true;
        AddHandler(KeyDownEvent, OnWindowKeyDownTunnel, RoutingStrategies.Tunnel);
        Loaded += (_, _) => Dispatcher.UIThread.Post(() => InnerReaderControl?.Focus(), DispatcherPriority.Input);
        Activated += (_, _) => Dispatcher.UIThread.Post(() => InnerReaderControl?.Focus(), DispatcherPriority.Input);

        DataContextChanged += OnReaderDataContextChanged;
    }

    private void OnWindowKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (InnerReaderControl != null && !e.Handled)
        {
            InnerReaderControl.HandleKeyDownFromHost(e);
        }
    }

    private void OnReaderDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.FullScreenChangedRequested -= OnVmFullScreenChangedRequested;
            _subscribedVm.CloseRequested -= OnVmCloseRequested;
            _subscribedVm = null;
        }

        if (DataContext is ComicReaderViewModel vm)
        {
            _subscribedVm = vm;
            vm.FullScreenChangedRequested += OnVmFullScreenChangedRequested;
            vm.CloseRequested += OnVmCloseRequested;
        }
    }

    private void OnVmFullScreenChangedRequested(bool fullScreen)
    {
        WindowState = fullScreen ? WindowState.FullScreen : WindowState.Maximized;
    }

    private void OnVmCloseRequested()
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.FullScreenChangedRequested -= OnVmFullScreenChangedRequested;
            _subscribedVm.CloseRequested -= OnVmCloseRequested;
            _subscribedVm = null;
        }

        if (DataContext is ComicReaderViewModel vm)
        {
            vm.SaveCurrentReadingSession();
            vm.Cleanup();
        }
        base.OnClosed(e);
    }
}
