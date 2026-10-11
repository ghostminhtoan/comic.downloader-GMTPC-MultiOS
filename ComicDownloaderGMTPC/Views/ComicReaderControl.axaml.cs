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

public partial class ComicReaderControl : UserControl
{
    private ComicReaderViewModel? _subscribedVm;
    private Point _pointerPressPoint;
    private long _pointerPressTick;
    private bool _isPointerTapCandidate;
    private bool _isProgrammaticScroll;
    private bool _isUserDraggingScrubber;
    private int _scrollRequestVersion;
    private long _lastUserScrollInputTick;

    // Trạng thái vuốt ngang (Swipe) trên thanh công cụ Button 1-5 ↔ Button 6-10
    private Point _toolbarSwipeStartPoint;
    private bool _isToolbarSwipeTracking;
    private bool _toolbarSwipeTriggered;

    // Trạng thái kéo trực tiếp khung cắt lề Margin Crop (Button 4)
    private Point _cropDragLastPoint;
    private bool _isCropDragging;
    private double _cropStartXRatio;
    private double _cropStartYRatio;

    public ComicReaderControl()
    {
        InitializeComponent();

        DataContextChanged += OnReaderDataContextChanged;

        // Lắng nghe sự kiện cuộn trên ListBox để cập nhật trang đang đọc và lưu tiến độ
        AddHandler(ScrollViewer.ScrollChangedEvent, OnReaderScrollChanged, RoutingStrategies.Bubble);

        // Lắng nghe chạm / click trên vùng đọc truyện để bật/tắt Overlay kiểu ComicScreen hoặc lật trang
        var viewport = this.FindControl<Panel>("ReaderViewportPanel");
        if (viewport != null)
        {
            viewport.AddHandler(PointerPressedEvent, OnViewportPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            viewport.AddHandler(PointerReleasedEvent, OnViewportPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            viewport.AddHandler(PointerWheelChangedEvent, OnViewportPointerWheelChanged, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        // Lắng nghe vuốt ngang (Swipe) trên thanh công cụ 10 nút (Button 1-5 ↔ Button 6-10)
        var toolbar = this.FindControl<Border>("SwipeableToolbarBorder");
        if (toolbar != null)
        {
            toolbar.AddHandler(PointerPressedEvent, OnToolbarPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            toolbar.AddHandler(PointerMovedEvent, OnToolbarPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            toolbar.AddHandler(PointerReleasedEvent, OnToolbarPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        // Lắng nghe kéo trực tiếp trên khung cắt lề Margin Crop (Button 4)
        var cropPanel = this.FindControl<Panel>("MarginCropInteractivePanel");
        if (cropPanel != null)
        {
            cropPanel.AddHandler(PointerPressedEvent, OnCropPanelPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            cropPanel.AddHandler(PointerMovedEvent, OnCropPanelPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            cropPanel.AddHandler(PointerReleasedEvent, OnCropPanelPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        // Lắng nghe trực tiếp thao tác kéo thanh trượt trang của người dùng (tránh vòng lặp TwoWay với ScrollViewer)
        var scrubber = this.FindControl<Slider>("PageScrubberSlider");
        if (scrubber != null)
        {
            scrubber.AddHandler(PointerPressedEvent, OnScrubberPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            scrubber.AddHandler(PointerReleasedEvent, OnScrubberPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            scrubber.AddHandler(PointerCaptureLostEvent, OnScrubberPointerCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
            scrubber.PropertyChanged += OnScrubberPropertyChanged;
        }

        AddHandler(KeyDownEvent, OnControlKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && DataContext is ComicReaderViewModel vm)
        {
            var b = Bounds;
            if (b.Width > 0 && b.Height > 0)
            {
                vm.UpdateViewportDimensions(b.Width, b.Height);
            }
        }
    }

    // =========================================================================
    // XỬ LÝ VUỐT NGANG (SWIPE) TRÊN THANH CÔNG CỤ BUTTON 1-5 ↔ BUTTON 6-10
    // =========================================================================

    private void OnToolbarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _toolbarSwipeStartPoint = e.GetPosition(this);
        _isToolbarSwipeTracking = true;
        _toolbarSwipeTriggered = false;
    }

    private void OnToolbarPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isToolbarSwipeTracking || _toolbarSwipeTriggered) return;

        Point current = e.GetPosition(this);
        double dx = current.X - _toolbarSwipeStartPoint.X;
        double dy = current.Y - _toolbarSwipeStartPoint.Y;

        if (Math.Abs(dx) >= 26.0 && Math.Abs(dx) > Math.Abs(dy) * 1.15)
        {
            _toolbarSwipeTriggered = true;
            if (DataContext is ComicReaderViewModel vm)
            {
                if (dx < 0)
                {
                    // Vuốt sang trái: sang trang 6-10 (hoặc đảo trang nếu đã ở 6-10)
                    vm.SetToolbarPage(!vm.IsToolbarPageTwo ? true : false);
                }
                else
                {
                    // Vuốt sang phải: về trang 1-5 (hoặc đảo trang nếu đã ở 1-5)
                    vm.SetToolbarPage(vm.IsToolbarPageTwo ? false : true);
                }
            }
            e.Handled = true;
        }
    }

    private void OnToolbarPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isToolbarSwipeTracking) return;
        _isToolbarSwipeTracking = false;

        if (_toolbarSwipeTriggered)
        {
            _toolbarSwipeTriggered = false;
            e.Handled = true;
            return;
        }

        Point release = e.GetPosition(this);
        double dx = release.X - _toolbarSwipeStartPoint.X;
        double dy = release.Y - _toolbarSwipeStartPoint.Y;

        if (Math.Abs(dx) >= 24.0 && Math.Abs(dx) > Math.Abs(dy) * 1.15)
        {
            if (DataContext is ComicReaderViewModel vm)
            {
                vm.ToggleToolbarPage();
            }
            e.Handled = true;
        }
    }

    // =========================================================================
    // XỬ LÝ KÉO KHUNG CẮT LỀ MARGIN CROP (BUTTON 4)
    // =========================================================================

    private void OnCropPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control panel) return;
        _cropDragLastPoint = e.GetPosition(panel);
        double w = Math.Max(1.0, panel.Bounds.Width);
        double h = Math.Max(1.0, panel.Bounds.Height);
        _cropStartXRatio = _cropDragLastPoint.X / w;
        _cropStartYRatio = _cropDragLastPoint.Y / h;
        _isCropDragging = true;
    }

    private void OnCropPanelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isCropDragging || sender is not Control panel || DataContext is not ComicReaderViewModel vm) return;

        Point pt = e.GetPosition(panel);
        double w = Math.Max(100.0, panel.Bounds.Width);
        double h = Math.Max(100.0, panel.Bounds.Height);

        double dxPct = (pt.X - _cropDragLastPoint.X) / w * 100.0;
        double dyPct = (pt.Y - _cropDragLastPoint.Y) / h * 100.0;
        _cropDragLastPoint = pt;

        double dLeft = _cropStartXRatio < 0.5 ? dxPct : 0;
        double dRight = _cropStartXRatio >= 0.5 ? -dxPct : 0;
        double dTop = _cropStartYRatio < 0.5 ? dyPct : 0;
        double dBottom = _cropStartYRatio >= 0.5 ? -dyPct : 0;

        vm.AdjustPendingCropByDrag(dLeft, dTop, dRight, dBottom);
    }

    private void OnCropPanelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isCropDragging = false;
    }

    // =========================================================================
    // XỬ LÝ THANH TRƯỢT TIẾN ĐỘ TRANG (PAGE SCRUBBER SLIDER)
    // =========================================================================

    private void OnScrubberPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isUserDraggingScrubber = true;
    }

    private void OnScrubberPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isUserDraggingScrubber) return;
        _isUserDraggingScrubber = false;
        if (sender is Slider slider && DataContext is ComicReaderViewModel vm)
        {
            vm.ScrubToPage((int)Math.Round(slider.Value));
        }
    }

    private void OnScrubberPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isUserDraggingScrubber) return;
        _isUserDraggingScrubber = false;
        if (sender is Slider slider && DataContext is ComicReaderViewModel vm)
        {
            vm.ScrubToPage((int)Math.Round(slider.Value));
        }
    }

    private void OnScrubberPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_isUserDraggingScrubber &&
            e.Property == RangeBase.ValueProperty &&
            sender is Slider slider &&
            DataContext is ComicReaderViewModel vm)
        {
            vm.ScrubToPage((int)Math.Round(slider.Value));
        }
    }

    private void OnReaderDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.ScrollToPageRequested -= OnVmScrollToPageRequested;
            _subscribedVm = null;
        }

        if (DataContext is ComicReaderViewModel vm)
        {
            _subscribedVm = vm;
            vm.ScrollToPageRequested += OnVmScrollToPageRequested;
            if (Bounds.Width > 0 && Bounds.Height > 0)
            {
                vm.UpdateViewportDimensions(Bounds.Width, Bounds.Height);
            }
            if (vm.Pages.Count > 0 && vm.CurrentPageIndex >= 0)
            {
                OnVmScrollToPageRequested(vm.CurrentPageIndex);
            }
        }
    }

    private void OnVmScrollToPageRequested(int pageIndex)
    {
        if (DataContext is not ComicReaderViewModel vm) return;
        if (pageIndex < 0 || pageIndex >= vm.Pages.Count) return;

        var listBox = this.FindControl<ListBox>("ReaderPagesListBox");
        if (listBox == null) return;

        int reqVersion = ++_scrollRequestVersion;
        var targetItem = vm.Pages[pageIndex];
        _isProgrammaticScroll = true;
        try
        {
            listBox.ScrollIntoView(targetItem);
        }
        catch { }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (reqVersion != _scrollRequestVersion) return;
            if (!_isUserDraggingScrubber && Environment.TickCount64 - _lastUserScrollInputTick < 220)
            {
                _isProgrammaticScroll = false;
                return;
            }

            try
            {
                if (pageIndex >= 0 && pageIndex < vm.Pages.Count)
                {
                    listBox.ScrollIntoView(vm.Pages[pageIndex]);
                }
            }
            catch { }
            finally
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (reqVersion == _scrollRequestVersion)
                        {
                            _isProgrammaticScroll = false;
                        }
                    },
                    Avalonia.Threading.DispatcherPriority.Background);
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
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
        if (_isProgrammaticScroll || _isUserDraggingScrubber) return;
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
            _lastUserScrollInputTick = Environment.TickCount64;
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

        // Nếu ở chế độ लật trang (Flip Mode) và người dùng vuốt ngang (Swipe) trên trang truyện -> Lật trang!
        if (DataContext is ComicReaderViewModel swipeVm && swipeVm.IsFlipMode && !swipeVm.IsMarginEditorOpen)
        {
            if (Math.Abs(dx) >= 42.0 && Math.Abs(dx) > Math.Abs(dy) * 1.3 && elapsedMs < 650)
            {
                if (dx < 0)
                {
                    // Vuốt từ phải sang trái
                    swipeVm.TapRightZone();
                }
                else
                {
                    // Vuốt từ trái sang phải
                    swipeVm.TapLeftZone();
                }
                return;
            }
        }

        // Chỉ tính là Tap khi người dùng chạm nhanh và không kéo cuộn trang
        if (distance > 14.0 || elapsedMs > 450) return;

        if (DataContext is not ComicReaderViewModel vm) return;

        double width = Math.Max(1.0, Bounds.Width);
        double xRatio = releasePoint.X / width;

        if (vm.IsScrollMode)
        {
            // Ở chế độ Scroll dọc: chạm vào vùng giữa màn hình để bật/tắt tùy chọn ComicScreen
            if (xRatio >= 0.12 && xRatio <= 0.88)
            {
                vm.ToggleOverlay();
            }
        }
        else
        {
            // Ở chế độ Lật trang: 1/3 Trái = lật trái, 1/3 Giữa = bật/tắt tùy chọn ComicScreen, 1/3 Phải = lật phải
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

        _lastUserScrollInputTick = Environment.TickCount64;

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

    private void OnControlKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ComicReaderViewModel vm) return;

        // Nếu đang gõ Memo trong hộp thoại Add Bookmark thì chỉ bắt phím Esc / Enter
        if (e.Source is TextBox)
        {
            if (e.Key == Key.Escape)
            {
                vm.CancelAddBookmark();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                vm.ConfirmAddBookmark();
                e.Handled = true;
            }
            return;
        }

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
                if (vm.HandleBackNavigation())
                {
                    e.Handled = true;
                    break;
                }
                if (vm.IsFullScreen)
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
                current is CheckBox ||
                current is TextBox ||
                current is ComboBox ||
                current is ComboBoxItem)
            {
                return true;
            }
            current = current.GetVisualParent();
        }
        return false;
    }
}
