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

    // Quản lý cử chỉ DirectoryItem
    private Point _dirPointerStartPos;
    private long _dirPointerPressedTick;
    private bool _dirIsLongPressTriggered;
    private bool _dirIsSwipeOrScroll;

    // Quản lý cử chỉ HistoryItem
    private Point _historyPointerStartPos;
    private long _historyPointerPressedTick;
    private bool _historyIsLongPressTriggered;
    private bool _historyIsSwipeOrScroll;

    private const double TouchSlopThreshold = 12.0; // Vượt quá 12px được xác định chắc chắn là cử chỉ vuốt/cuộn

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

    // --- DirectoryItem Gestures ---
    private void OnDirectoryItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dirPointerStartPos = e.GetPosition(this);
        _dirPointerPressedTick = Environment.TickCount64;
        _dirIsLongPressTriggered = false;
        _dirIsSwipeOrScroll = false;
    }

    private void OnDirectoryItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dirIsSwipeOrScroll) return;
        var cur = e.GetPosition(this);
        double dx = cur.X - _dirPointerStartPos.X;
        double dy = cur.Y - _dirPointerStartPos.Y;
        if (Math.Sqrt(dx * dx + dy * dy) > TouchSlopThreshold)
        {
            _dirIsSwipeOrScroll = true;
        }
    }

    private void OnDirectoryItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        long elapsed = Environment.TickCount64 - _dirPointerPressedTick;
        if (elapsed >= 400)
        {
            _dirIsLongPressTriggered = true;
        }
    }

    private void OnDirectoryItemTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border border) return;

        // Nếu bấm vào các Button con (ví dụ nút menu ⋮), không tự động mở thư mục/file
        if (e.Source is Button || (e.Source as Visual)?.FindAncestorOfType<Button>() != null) return;

        if (_dirIsSwipeOrScroll || _dirIsLongPressTriggered || (border.ContextMenu != null && border.ContextMenu.IsOpen))
        {
            _dirIsLongPressTriggered = false;
            _dirIsSwipeOrScroll = false;
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

    // --- HistoryItem Gestures: Single Click/Tap để mở truyện trên Windows, Android, Linux & Swipe Guard ---
    private void OnHistoryItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _historyPointerStartPos = e.GetPosition(this);
        _historyPointerPressedTick = Environment.TickCount64;
        _historyIsLongPressTriggered = false;
        _historyIsSwipeOrScroll = false;
    }

    private void OnHistoryItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_historyIsSwipeOrScroll) return;
        var cur = e.GetPosition(this);
        double dx = cur.X - _historyPointerStartPos.X;
        double dy = cur.Y - _historyPointerStartPos.Y;
        if (Math.Sqrt(dx * dx + dy * dy) > TouchSlopThreshold)
        {
            _historyIsSwipeOrScroll = true;
        }
    }

    private void OnHistoryItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        long elapsed = Environment.TickCount64 - _historyPointerPressedTick;
        if (elapsed >= 400)
        {
            _historyIsLongPressTriggered = true;
        }
    }

    private void OnHistoryItemTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border border) return;

        // Nếu người dùng bấm vào các nút điều khiển con (ví dụ nút ▶ Đọc hoặc nút ⋮ menu), để nút đó tự xử lý
        if (e.Source is Button || (e.Source as Visual)?.FindAncestorOfType<Button>() != null) return;

        // Trên Android: Nếu ngón tay vuốt/lướt danh sách (cuộn) hoặc nhấn giữ ContextMenu -> Tuyệt đối không mở truyện!
        if (_historyIsSwipeOrScroll || _historyIsLongPressTriggered || (border.ContextMenu != null && border.ContextMenu.IsOpen))
        {
            _historyIsLongPressTriggered = false;
            _historyIsSwipeOrScroll = false;
            e.Handled = true;
            return;
        }

        // Single Click (Windows, Linux) / Single Tap (Android) vào truyện -> Mở truyện đọc ngay!
        if (border.DataContext is HistoryItemModel item && DataContext is LibraryViewModel vm)
        {
            _ = vm.OpenHistoryItemAsync(item);
        }
    }
}
