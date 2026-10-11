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
        SizeChanged += OnLibrarySizeChanged;
    }

    private void OnLibrarySizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm)
        {
            vm.IsCompactWidth = e.NewSize.Width < 780;
        }
    }

    private void OnLibraryAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is LibraryViewModel vm)
        {
            if (Bounds.Width > 0)
            {
                vm.IsCompactWidth = Bounds.Width < 780;
            }
            if (!_hasScannedOnce && vm.AllComics.Count == 0 && !vm.IsScanning)
            {
                _hasScannedOnce = true;
                _ = vm.ScanLibraryAsync();
            }
        }
    }
}
