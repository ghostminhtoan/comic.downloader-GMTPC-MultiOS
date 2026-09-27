using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ComicDownloaderGMTPC.ViewModels;

namespace ComicDownloaderGMTPC.Views;

public partial class EnhanceComparisonWindow : Window
{
    private bool _isSyncingScroll = false;
    private bool _isDragging = false;
    private Point _dragStartPoint;
    private Vector _dragStartOffset;

    public EnhanceComparisonWindow()
    {
        InitializeComponent();

        // Đăng ký sự kiện cuộn đồng bộ
        if (BeforeScrollViewer != null)
        {
            BeforeScrollViewer.ScrollChanged += OnBeforeScrollChanged;
            BeforeScrollViewer.AddHandler(PointerPressedEvent, OnImagePointerPressed, RoutingStrategies.Tunnel);
            BeforeScrollViewer.AddHandler(PointerMovedEvent, OnImagePointerMoved, RoutingStrategies.Tunnel);
            BeforeScrollViewer.AddHandler(PointerReleasedEvent, OnImagePointerReleased, RoutingStrategies.Tunnel);
            BeforeScrollViewer.AddHandler(PointerCaptureLostEvent, (s, e) => _isDragging = false, RoutingStrategies.Tunnel);
        }

        if (AfterScrollViewer != null)
        {
            AfterScrollViewer.ScrollChanged += OnAfterScrollChanged;
            AfterScrollViewer.AddHandler(PointerPressedEvent, OnImagePointerPressed, RoutingStrategies.Tunnel);
            AfterScrollViewer.AddHandler(PointerMovedEvent, OnImagePointerMoved, RoutingStrategies.Tunnel);
            AfterScrollViewer.AddHandler(PointerReleasedEvent, OnImagePointerReleased, RoutingStrategies.Tunnel);
            AfterScrollViewer.AddHandler(PointerCaptureLostEvent, (s, e) => _isDragging = false, RoutingStrategies.Tunnel);
        }

        if (SingleScrollViewer != null)
        {
            SingleScrollViewer.AddHandler(PointerPressedEvent, OnSinglePointerPressed, RoutingStrategies.Tunnel);
            SingleScrollViewer.AddHandler(PointerReleasedEvent, OnSinglePointerReleased, RoutingStrategies.Tunnel);
        }

        // Tự động nhận diện Landscape (width > height) vs Portrait (width < height)
        SizeChanged += (s, e) =>
        {
            if (DataContext is MainViewModel vm && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                vm.IsPortraitMode = e.NewSize.Width < e.NewSize.Height;
            }
        };
    }

    private void OnBeforeScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isSyncingScroll || BeforeScrollViewer == null || AfterScrollViewer == null) return;
        _isSyncingScroll = true;
        AfterScrollViewer.Offset = BeforeScrollViewer.Offset;
        _isSyncingScroll = false;
    }

    private void OnAfterScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isSyncingScroll || BeforeScrollViewer == null || AfterScrollViewer == null) return;
        _isSyncingScroll = true;
        BeforeScrollViewer.Offset = AfterScrollViewer.Offset;
        _isSyncingScroll = false;
    }

    private static bool IsScrollBarElement(object? source, Visual? container)
    {
        if (source is Visual v)
        {
            Visual? cur = v;
            while (cur != null && cur != container)
            {
                if (cur is ScrollBar) return true;
                cur = cur.GetVisualParent();
            }
        }
        return false;
    }

    private void OnImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsScrollBarElement(e.Source, sender as Visual)) return;

        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed || point.Properties.IsMiddleButtonPressed)
        {
            _isDragging = true;
            _dragStartPoint = point.Position;
            _dragStartOffset = BeforeScrollViewer?.Offset ?? default;
            e.Pointer.Capture(sender as IInputElement);
            e.Handled = true;
        }
    }

    private void OnImagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (IsScrollBarElement(e.Source, sender as Visual)) return;

        if (_isDragging && BeforeScrollViewer != null && AfterScrollViewer != null)
        {
            var currentPoint = e.GetCurrentPoint(this).Position;
            var delta = _dragStartPoint - currentPoint;
            var newOffset = new Vector(
                Math.Max(0, _dragStartOffset.X + delta.X),
                Math.Max(0, _dragStartOffset.Y + delta.Y));

            _isSyncingScroll = true;
            BeforeScrollViewer.Offset = newOffset;
            AfterScrollViewer.Offset = newOffset;
            _isSyncingScroll = false;
            e.Handled = true;
        }
    }

    private void OnImagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
        }
    }

    private void OnSinglePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.IsSingleView)
        {
            vm.IsSingleShowingBefore = true;
        }
    }

    private void OnSinglePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.IsSingleView)
        {
            vm.IsSingleShowingBefore = false;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (DataContext is MainViewModel vm)
        {
            if (e.Delta.Y > 0)
            {
                vm.ZoomInPreview();
                e.Handled = true;
            }
            else if (e.Delta.Y < 0)
            {
                vm.ZoomOutPreview();
                e.Handled = true;
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is MainViewModel vm)
        {
            switch (e.Key)
            {
                case Key.Left:
                case Key.A:
                    vm.PreviousImage();
                    e.Handled = true;
                    break;
                case Key.Right:
                case Key.D:
                    vm.NextImage();
                    e.Handled = true;
                    break;
                case Key.F:
                    vm.ZoomFitPreview();
                    e.Handled = true;
                    break;
                case Key.D1:
                case Key.NumPad1:
                    vm.ResetZoomPreview();
                    e.Handled = true;
                    break;
                case Key.OemPlus:
                case Key.Add:
                    vm.ZoomInPreview();
                    e.Handled = true;
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    vm.ZoomOutPreview();
                    e.Handled = true;
                    break;
                case Key.Space:
                    if (vm.IsSingleView)
                    {
                        vm.IsSingleShowingBefore = true;
                        e.Handled = true;
                    }
                    break;
            }
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (DataContext is MainViewModel vm && e.Key == Key.Space)
        {
            vm.IsSingleShowingBefore = false;
            e.Handled = true;
        }
    }
}
