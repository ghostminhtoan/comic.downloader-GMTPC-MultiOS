using Avalonia;
using Avalonia.Controls;
using ComicDownloaderGMTPC.ViewModels;

namespace ComicDownloaderGMTPC.Views;

public partial class LibraryView : UserControl
{
    private bool _hasScannedOnce;

    public LibraryView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnLibraryAttachedToVisualTree;
    }

    private void OnLibraryAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (!_hasScannedOnce && DataContext is LibraryViewModel vm && vm.AllComics.Count == 0 && !vm.IsScanning)
        {
            _hasScannedOnce = true;
            _ = vm.ScanLibraryAsync();
        }
    }
}
