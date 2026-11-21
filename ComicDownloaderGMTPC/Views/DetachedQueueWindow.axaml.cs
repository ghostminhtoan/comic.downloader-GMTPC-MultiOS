using System;
using Avalonia.Controls;
using ComicDownloaderGMTPC.ViewModels;

namespace ComicDownloaderGMTPC.Views;

public partial class DetachedQueueWindow : Window
{
    public DetachedQueueWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is MainViewModel vm)
        {
            vm.IsExternalQueueWindowOpen = false;
        }
    }
}
