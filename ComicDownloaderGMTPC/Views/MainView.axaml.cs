using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ComicDownloaderGMTPC.ViewModels;

namespace ComicDownloaderGMTPC.Views;

public partial class MainView : UserControl
{
    public static MainView? Instance { get; private set; }

    public MainView()
    {
        Instance = this;
        InitializeComponent();

        Loaded += OnMainViewLoaded;
    }

    private void OnMainViewLoaded(object? sender, RoutedEventArgs e)
    {
        // Đồng bộ Pan & Zoom cho Live Preview trong Tab Xử Lý Ảnh
        SetupSyncScroll(this.FindControl<ScrollViewer>("LiveBeforeScrollViewer"), this.FindControl<ScrollViewer>("LiveAfterScrollViewer"));

        // Đồng bộ Pan & Zoom cho Modal Đối Chiếu Toàn Màn Hình
        SetupSyncScroll(this.FindControl<ScrollViewer>("ModalBeforeScrollViewer"), this.FindControl<ScrollViewer>("ModalAfterScrollViewer"));
    }

    private void SetupSyncScroll(ScrollViewer? before, ScrollViewer? after)
    {
        if (before == null || after == null) return;

        bool isSyncing = false;
        bool isDragging = false;
        Point dragStart = default;
        Vector startOffset = default;

        before.ScrollChanged += (s, ev) =>
        {
            if (isSyncing) return;
            isSyncing = true;
            after.Offset = before.Offset;
            isSyncing = false;
        };

        after.ScrollChanged += (s, ev) =>
        {
            if (isSyncing) return;
            isSyncing = true;
            before.Offset = after.Offset;
            isSyncing = false;
        };

        void OnPressed(object? sender, PointerPressedEventArgs ev)
        {
            var p = ev.GetCurrentPoint(this);
            if (p.Properties.IsLeftButtonPressed || p.Properties.IsMiddleButtonPressed)
            {
                isDragging = true;
                dragStart = p.Position;
                startOffset = before.Offset;
                ev.Pointer.Capture(sender as IInputElement);
            }
        }

        void OnMoved(object? sender, PointerEventArgs ev)
        {
            if (isDragging)
            {
                var cur = ev.GetCurrentPoint(this).Position;
                var delta = dragStart - cur;
                var newOffset = new Vector(Math.Max(0, startOffset.X + delta.X), Math.Max(0, startOffset.Y + delta.Y));
                isSyncing = true;
                before.Offset = newOffset;
                after.Offset = newOffset;
                isSyncing = false;
            }
        }

        void OnReleased(object? sender, PointerReleasedEventArgs ev)
        {
            if (isDragging)
            {
                isDragging = false;
                ev.Pointer.Capture(null);
            }
        }

        before.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        before.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        before.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);

        after.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        after.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        after.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);

        // Đồng bộ phóng to / thu nhỏ bằng bánh xe cuộn chuột trực tiếp trên khung ảnh
        void OnWheel(object? sender, PointerWheelEventArgs ev)
        {
            if (DataContext is MainViewModel vm)
            {
                if (ev.Delta.Y > 0)
                {
                    vm.ZoomInPreview();
                    ev.Handled = true;
                }
                else if (ev.Delta.Y < 0)
                {
                    vm.ZoomOutPreview();
                    ev.Handled = true;
                }
            }
        }

        before.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        after.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }
}