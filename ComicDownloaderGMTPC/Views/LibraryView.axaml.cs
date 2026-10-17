using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.ViewModels;

namespace ComicDownloaderGMTPC.Views;

public partial class LibraryView : UserControl
{
    private bool _hasScannedOnce;
    private long _pointerPressedTick;
    private bool _isLongPressTriggered;

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

    private void OnDirectoryItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pointerPressedTick = Environment.TickCount64;
        _isLongPressTriggered = false;
    }

    private void OnDirectoryItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        long elapsed = Environment.TickCount64 - _pointerPressedTick;
        if (elapsed >= 350)
        {
            _isLongPressTriggered = true;
        }
    }

    private void OnDirectoryItemTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border border) return;
        if (_isLongPressTriggered || (border.ContextMenu != null && border.ContextMenu.IsOpen))
        {
            _isLongPressTriggered = false;
            e.Handled = true;
            return;
        }

        if (border.DataContext is DirectoryItemModel item && DataContext is LibraryViewModel vm)
        {
            _ = vm.SelectDirectoryItemAsync(item);
        }
    }

    private void OnDirectoryItemMenuButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Visual visual)
        {
            var border = visual.FindAncestorOfType<Border>();
            if (border?.ContextMenu != null)
            {
                border.ContextMenu.Open(border);
                e.Handled = true;
            }
        }
    }
}
