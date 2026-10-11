using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.ViewModels;

namespace ComicDownloaderGMTPC.Views;

public partial class ComicReaderWindow : Window
{
    private ComicReaderViewModel? _subscribedVm;
    private Point _pointerPressPoint;
    private long _pointerPressTick;
    private bool _isPointerTapCandidate;
    private bool _isProgrammaticScroll;

    public ComicReaderWindow()
    {
        InitializeComponent();

        // Tự động mở ở chế độ FullScreen theo yêu cầu
        WindowState = WindowState.FullScreen;

        DataContextChanged += OnReaderDataContextChanged;

        // Lắng nghe sự kiện cuộn trên ListBox để cập nhật trang đang đọc và lưu tiến độ
        AddHandler(ScrollViewer.ScrollChangedEvent, OnReaderScrollChanged, RoutingStrategies.Bubble);

        // Lắng nghe chạm / click trên vùng đọc truyện để bật/tắt Overlay kiểu ComicScreen hoặc lật trang
        var viewport = this.FindControl<Panel>("ReaderViewportPanel");
        if (viewport != null)
        {
            viewport.AddHandler(PointerPressedEvent, OnViewportPointerPressed, RoutingStrategies.Tunnel);
            viewport.AddHandler(PointerReleasedEvent, OnViewportPointerReleased, RoutingStrategies.Tunnel);
            viewport.AddHandler(PointerWheelChangedEvent, OnViewportPointerWheelChanged, RoutingStrategies.Tunnel);
        }

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnReaderDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.ScrollToPageRequested -= OnVmScrollToPageRequested;
            _subscribedVm.FullScreenChangedRequested -= OnVmFullScreenChangedRequested;
            _subscribedVm.CloseRequested -= OnVmCloseRequested;
            _subscribedVm = null;
        }

        if (DataContext is ComicReaderViewModel vm)
        {
            _subscribedVm = vm;
            vm.ScrollToPageRequested += OnVmScrollToPageRequested;
            vm.FullScreenChangedRequested += OnVmFullScreenChangedRequested;
            vm.CloseRequested += OnVmCloseRequested;
        }
    }

    private void OnVmScrollToPageRequested(int pageIndex)
    {
        if (DataContext is not ComicReaderViewModel vm) return;
        if (pageIndex < 0 || pageIndex >= vm.Pages.Count) return;

        var listBox = this.FindControl<ListBox>("ReaderPagesListBox");
        if (listBox == null) return;

        _isProgrammaticScroll = true;
        try
        {
            listBox.ScrollIntoView(vm.Pages[pageIndex]);
        }
        catch { }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _isProgrammaticScroll = false, Avalonia.Threading.DispatcherPriority.Background);
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

    private void OnPageContainerAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control ctrl &&
            ctrl.DataContext is ReaderPageItem pageItem &&
            DataContext is ComicReaderViewModel vm)
        {
            _ = vm.EnsureSinglePageLoadedAsync(pageItem);
        }
    }

    private void OnReaderScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isProgrammaticScroll) return;
        if (DataContext is not ComicReaderViewModel vm || !vm.IsScrollMode || vm.Pages.Count == 0) return;

        var listBox = this.FindControl<ListBox>("ReaderPagesListBox");
        if (listBox == null) return;

        double viewportCenterY = listBox.Bounds.Height * 0.45;
        int bestPageIndex = -1;
        double bestDist = double.MaxValue;

        foreach (var child in listBox.GetVisualDescendants())
        {
            if (child is ListBoxItem container && container.DataContext is ReaderPageItem page)
            {
                var pos = container.TranslatePoint(new Point(0, 0), listBox);
                if (pos.HasValue)
                {
                    double top = pos.Value.Y;
                    double bottom = top + container.Bounds.Height;
                    if (top <= viewportCenterY && bottom >= viewportCenterY)
                    {
                        bestPageIndex = page.Index;
                        break;
                    }

                    double mid = (top + bottom) * 0.5;
                    double dist = Math.Abs(mid - viewportCenterY);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestPageIndex = page.Index;
                    }
                }
            }
        }

        if (bestPageIndex >= 0)
        {
            vm.UpdatePageIndexFromScroll(bestPageIndex);
        }
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsScrollBarOrInteractiveControl(e.Source as Visual))
        {
            _isPointerTapCandidate = false;
            return;
        }

        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed && e.Pointer.Type != PointerType.Touch)
        {
            _isPointerTapCandidate = false;
            return;
        }

        _pointerPressPoint = e.GetPosition(this);
        _pointerPressTick = Environment.TickCount64;
        _isPointerTapCandidate = true;
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPointerTapCandidate) return;
        _isPointerTapCandidate = false;

        if (IsScrollBarOrInteractiveControl(e.Source as Visual)) return;

        Point releasePoint = e.GetPosition(this);
        long elapsedMs = Environment.TickCount64 - _pointerPressTick;
        double dx = releasePoint.X - _pointerPressPoint.X;
        double dy = releasePoint.Y - _pointerPressPoint.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        // Chỉ tính là Tap khi người dùng chạm nhanh và không kéo cuộn trang
        if (distance > 14.0 || elapsedMs > 450) return;

        if (DataContext is not ComicReaderViewModel vm) return;

        double width = Math.Max(1.0, Bounds.Width);
        double xRatio = releasePoint.X / width;

        if (vm.IsScrollMode)
        {
            // Ở chế độ Scroll dọc: chạm vào vùng giữa màn hình để bật/tắt tùy chọn ComicScreen
            if (xRatio >= 0.15 && xRatio <= 0.85)
            {
                vm.ToggleOverlay();
            }
        }
        else
        {
            // Ở chế độ Lật trang: 1/3 Trái = lật trái, 1/3 Giữa = bật/tắt tùy chọn, 1/3 Phải = lật phải
            if (xRatio < 0.32)
            {
                vm.TapLeftZone();
            }
            else if (xRatio > 0.68)
            {
                vm.TapRightZone();
            }
            else
            {
                vm.ToggleOverlay();
            }
        }
    }

    private void OnViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not ComicReaderViewModel vm) return;

        // Giữ Ctrl + lăn chuột = Phóng to / Thu nhỏ
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Delta.Y > 0) vm.ZoomIn();
            else if (e.Delta.Y < 0) vm.ZoomOut();
            e.Handled = true;
            return;
        }

        // Ở chế độ Flip mode (khi không giữ Ctrl): lăn chuột chuyển trang mượt mà
        if (vm.IsFlipMode && vm.ZoomLevel <= 1.05)
        {
            if (e.Delta.Y < 0) vm.NextPage();
            else if (e.Delta.Y > 0) vm.PrevPage();
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ComicReaderViewModel vm) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.Right)
            {
                vm.NextChapter();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Left)
            {
                vm.PrevChapter();
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Key.Escape:
            case Key.Back:
                if (vm.IsOverlayVisible)
                {
                    vm.IsOverlayVisible = false;
                }
                else if (WindowState == WindowState.FullScreen)
                {
                    vm.ToggleFullScreen();
                }
                else
                {
                    vm.CloseReader();
                }
                e.Handled = true;
                break;

            case Key.F:
            case Key.F11:
                vm.ToggleFullScreen();
                e.Handled = true;
                break;

            case Key.M:
                vm.ToggleMode();
                e.Handled = true;
                break;

            case Key.R:
                if (vm.IsFlipMode)
                {
                    vm.ToggleRtl();
                    e.Handled = true;
                }
                break;

            case Key.P:
                if (vm.IsFlipMode)
                {
                    vm.ToggleDoublePage();
                    e.Handled = true;
                }
                break;

            case Key.Left:
            case Key.A:
                if (vm.IsFlipMode)
                {
                    vm.TapLeftZone();
                    e.Handled = true;
                }
                break;

            case Key.Right:
            case Key.D:
                if (vm.IsFlipMode)
                {
                    vm.TapRightZone();
                    e.Handled = true;
                }
                break;

            case Key.Space:
                if (vm.IsFlipMode)
                {
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                        vm.PrevPage();
                    else
                        vm.NextPage();
                    e.Handled = true;
                }
                break;

            case Key.OemPlus:
            case Key.Add:
                vm.ZoomIn();
                e.Handled = true;
                break;

            case Key.OemMinus:
            case Key.Subtract:
                vm.ZoomOut();
                e.Handled = true;
                break;

            case Key.D0:
            case Key.NumPad0:
                vm.ResetZoom();
                e.Handled = true;
                break;
        }
    }

    private static bool IsScrollBarOrInteractiveControl(Visual? source)
    {
        var current = source;
        while (current != null)
        {
            if (current is ScrollBar ||
                current is Thumb ||
                current is Track ||
                current is RepeatButton ||
                current is Button ||
                current is Slider ||
                current is ComboBox ||
                current is ComboBoxItem)
            {
                return true;
            }
            current = current.GetVisualParent();
        }
        return false;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is ComicReaderViewModel vm)
        {
            vm.Cleanup();
        }
        base.OnClosed(e);
    }
}

