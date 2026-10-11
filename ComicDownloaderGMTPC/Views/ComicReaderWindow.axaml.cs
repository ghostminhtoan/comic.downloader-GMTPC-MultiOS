using System;
using Avalonia.Controls;
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

        DataContextChanged += OnReaderDataContextChanged;
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
            vm.Cleanup();
        }
        base.OnClosed(e);
    }
}
