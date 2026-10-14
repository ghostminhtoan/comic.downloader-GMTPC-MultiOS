using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
    private bool _isDoubleTapZooming;
    private int _scrollRequestVersion;
    private long _lastUserScrollInputTick;
    private long _suppressScrollChangedUntilTick;

    // Trạng thái Hold & Drag để Scroll (Pan-Drag Engine mượt mà mọi vùng)
    private Point _panStartPoint;
    private Point _panLastPoint;
    private long _panStartTick;
    private long _lastPanMoveTick;
    private bool _isPanHolding;
    private bool _isPanDragging;
    private double _panVelocityX;
    private double _panVelocityY;
    private double _kineticVelocityX;
    private double _kineticVelocityY;
    private readonly DispatcherTimer _kineticTimer;

    // Trạng thái kéo vượt biên để chuyển chương liền mạch (Continuous Chapters Mode)
    private double _cumulativeOverscrollY = 0;
    private double _cumulativeOverscrollX = 0;
    private long _lastAutoChapterTransitionTick = 0;

    private ScrollViewer? _cachedReaderScrollViewer;
    private ScrollViewer? _cachedFlipScrollViewer;

    // Trạng thái vuốt ngang (Swipe) trên thanh công cụ Button 1-5 ↔ Button 6-10
    private Point _toolbarSwipeStartPoint;
    private bool _isToolbarSwipeTracking;
    private bool _toolbarSwipeTriggered;

    // Trạng thái kéo trực tiếp khung cắt lề Margin Crop (Button 4)
    private Point _cropDragLastPoint;
    private bool _isCropDragging;
    private double _cropStartXRatio;
    private double _cropStartYRatio;

    // Trạng thái gõ đúp (Double-Tap Smart Zoom) và Single-Tap delay
    private Point _lastTapPoint;
    private long _lastTapTick;
    private readonly DispatcherTimer _singleTapTimer;

    // Theo dõi con trỏ đa điểm chạm (Multi-touch Tracking) cho Pinch-to-zoom chuẩn ComicScreen / MX Player
    private readonly System.Collections.Generic.Dictionary<int, Point> _activePointers = new();
    private readonly System.Collections.Generic.Dictionary<int, long> _pointerLastSeenTicks = new();
    private bool _isPinching;
    private double _pinchStartDistance;
    private double _pinchLastDistance;
    private double _pinchStartZoom;
    private Point _pinchPivot;
    private long _lastPinchEndTick;
    private int _pinchStartPageIndex = -1;
    private double _pinchFractionY = 0.5;
    private double _pinchPageHeight = 600.0;

    private void InitVerticalPinchAnchor(Point pivot)
    {
        _pinchFractionY = 0.5;
        _pinchPageHeight = Bounds.Height > 0 ? Bounds.Height : 600.0;

        if (DataContext is not ComicReaderViewModel vm || !vm.IsScrollMode || vm.IsHorizontalScrollMode)
            return;

        var lb = this.FindControl<ListBox>("ReaderPagesListBox");
        if (lb == null) return;

        foreach (var child in lb.GetVisualDescendants())
        {
            if (child is ListBoxItem container && container.DataContext is ReaderPageItem page)
            {
                var pos = container.TranslatePoint(new Point(0, 0), lb);
                if (pos.HasValue)
                {
                    double top = pos.Value.Y;
                    double bottom = top + container.Bounds.Height;
                    if (pivot.Y >= top && pivot.Y <= bottom)
                    {
                        _pinchStartPageIndex = page.Index;
                        _pinchPageHeight = Math.Max(100.0, container.Bounds.Height);
                        _pinchFractionY = Math.Clamp((pivot.Y - top) / _pinchPageHeight, 0.0, 1.0);
                        return;
                    }
                }
            }
        }
    }

    private static double Distance(Point p1, Point p2)
    {
        double dx = p1.X - p2.X;
        double dy = p1.Y - p2.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public ComicReaderControl()
    {
        InitializeComponent();

        _kineticTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _kineticTimer.Tick += OnKineticTimerTick;

        _singleTapTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _singleTapTimer.Tick += (s, e) =>
        {
            _singleTapTimer.Stop();
            if (DataContext is ComicReaderViewModel vm)
            {
                Dispatcher.UIThread.Post(() => vm.ToggleOverlay());
            }
        };

        DataContextChanged += OnReaderDataContextChanged;

        // Lắng nghe sự kiện cuộn trên ListBox để cập nhật trang đang đọc và lưu tiến độ
        AddHandler(ScrollViewer.ScrollChangedEvent, OnReaderScrollChanged, RoutingStrategies.Bubble);

        var readerListBox = this.FindControl<ListBox>("ReaderPagesListBox");
        if (readerListBox != null)
        {
            readerListBox.SelectionChanged += (s, e) =>
            {
                if (readerListBox.SelectedItem != null)
                {
                    readerListBox.SelectedItem = null;
                }
            };
        }

        // Lắng nghe chạm / click / kéo trên vùng đọc truyện để Hold & Drag Scroll hoặc bật/tắt Overlay kiểu ComicScreen
        var viewport = this.FindControl<Panel>("ReaderViewportPanel");
        if (viewport != null)
        {
            viewport.AddHandler(PointerPressedEvent, OnViewportPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            viewport.AddHandler(PointerMovedEvent, OnViewportPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            viewport.AddHandler(PointerReleasedEvent, OnViewportPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            viewport.AddHandler(PointerCaptureLostEvent, OnViewportPointerCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
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

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopKineticScroll();
    }

    public ScrollViewer? GetReaderScrollViewer()
    {
        if (_cachedReaderScrollViewer != null)
            return _cachedReaderScrollViewer;
        var listBox = this.FindControl<ListBox>("ReaderPagesListBox");
        _cachedReaderScrollViewer = listBox?.FindDescendantOfType<ScrollViewer>();
        return _cachedReaderScrollViewer;
    }

    private ScrollViewer? GetFlipScrollViewer()
    {
        if (_cachedFlipScrollViewer != null)
            return _cachedFlipScrollViewer;
        _cachedFlipScrollViewer = this.FindControl<ScrollViewer>("FlipModeScrollViewer");
        return _cachedFlipScrollViewer;
    }

    private void StopKineticScroll()
    {
        _kineticTimer.Stop();
        _kineticVelocityX = 0;
        _kineticVelocityY = 0;
    }

    private void OnKineticTimerTick(object? sender, EventArgs e)
    {
        if (DataContext is not ComicReaderViewModel vm)
        {
            StopKineticScroll();
            return;
        }

        // Ma sát hãm đà quán tính mượt mà
        _kineticVelocityX *= 0.915;
        _kineticVelocityY *= 0.915;

        if (Math.Abs(_kineticVelocityX) < 0.025 && Math.Abs(_kineticVelocityY) < 0.025)
        {
            StopKineticScroll();
            return;
        }

        double stepX = _kineticVelocityX * 16.0;
        double stepY = _kineticVelocityY * 16.0;

        if (vm.IsScrollMode)
        {
            var scrollViewer = GetReaderScrollViewer();
            if (scrollViewer != null)
            {
                double newX = scrollViewer.Offset.X - stepX;
                double newY = scrollViewer.Offset.Y - stepY;

                double maxX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
                double maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);

                if (newX <= 0 || newX >= maxX) _kineticVelocityX = 0;
                if (newY <= 0 || newY >= maxY) _kineticVelocityY = 0;

                scrollViewer.Offset = new Vector(Math.Clamp(newX, 0, maxX), Math.Clamp(newY, 0, maxY));
            }
            else
            {
                StopKineticScroll();
            }
        }
        else if (vm.IsFlipMode && vm.ZoomLevel > 1.02)
        {
            var flipScrollViewer = GetFlipScrollViewer();
            if (flipScrollViewer != null)
            {
                double newX = flipScrollViewer.Offset.X - stepX;
                double newY = flipScrollViewer.Offset.Y - stepY;

                double maxX = Math.Max(0, flipScrollViewer.Extent.Width - flipScrollViewer.Viewport.Width);
                double maxY = Math.Max(0, flipScrollViewer.Extent.Height - flipScrollViewer.Viewport.Height);

                if (newX <= 0 || newX >= maxX) _kineticVelocityX = 0;
                if (newY <= 0 || newY >= maxY) _kineticVelocityY = 0;

                flipScrollViewer.Offset = new Vector(Math.Clamp(newX, 0, maxX), Math.Clamp(newY, 0, maxY));
            }
            else
            {
                StopKineticScroll();
            }
        }
        else
        {
            StopKineticScroll();
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
                    // Vuốt sang trái: tiến sang trang sau (1-5 -> 6-10 -> 11)
                    vm.NextToolbarPage();
                }
                else
                {
                    // Vuốt sang phải: lùi về trang trước (11 -> 6-10 -> 1-5)
                    vm.PrevToolbarPage();
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
        StopKineticScroll();
        _isUserDraggingScrubber = true;
        if (sender is Slider slider && DataContext is ComicReaderViewModel vm)
        {
            vm.UpdateScrubberHover((int)Math.Round(slider.Value), true);
        }
    }

    private void OnScrubberPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isUserDraggingScrubber) return;
        _isUserDraggingScrubber = false;
        if (sender is Slider slider && DataContext is ComicReaderViewModel vm)
        {
            vm.ScrubToPage((int)Math.Round(slider.Value));
            vm.UpdateScrubberHover(0, false);
        }
    }

    private void OnScrubberPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isUserDraggingScrubber) return;
        _isUserDraggingScrubber = false;
        if (sender is Slider slider && DataContext is ComicReaderViewModel vm)
        {
            vm.ScrubToPage((int)Math.Round(slider.Value));
            vm.UpdateScrubberHover(0, false);
        }
    }

    private void OnScrubberPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_isUserDraggingScrubber &&
            e.Property == RangeBase.ValueProperty &&
            sender is Slider slider &&
            DataContext is ComicReaderViewModel vm)
        {
            int targetPage = (int)Math.Round(slider.Value);
            vm.UpdateScrubberHover(targetPage, true);
            vm.ScrubToPage(targetPage);
        }
    }

    private void OnReaderDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.ScrollToPageRequested -= OnVmScrollToPageRequested;
            _subscribedVm.AutoScrollStepRequested -= OnVmAutoScrollStepRequested;
            _subscribedVm.PageButtonAreaScrollRequested -= OnVmPageButtonAreaScrollRequested;
            _subscribedVm = null;
        }

        if (DataContext is ComicReaderViewModel vm)
        {
            _subscribedVm = vm;
            vm.ScrollToPageRequested += OnVmScrollToPageRequested;
            vm.AutoScrollStepRequested += OnVmAutoScrollStepRequested;
            vm.PageButtonAreaScrollRequested += OnVmPageButtonAreaScrollRequested;
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

    private void OnPageButtonAreaPrevClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ComicReaderViewModel vm)
        {
            vm.PageButtonAreaPrev();
            e.Handled = true;
        }
    }

    private void OnPageButtonAreaNextClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ComicReaderViewModel vm)
        {
            vm.PageButtonAreaNext();
            e.Handled = true;
        }
    }

    private void OnBoundaryNextChapterClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ComicReaderViewModel vm && vm.CanGoNextChapter)
        {
            vm.NextChapter();
            e.Handled = true;
        }
    }

    private void OnBoundaryPrevChapterClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ComicReaderViewModel vm && vm.CanGoPrevChapter)
        {
            vm.PrevChapter();
            e.Handled = true;
        }
    }

    private void OnVmPageButtonAreaScrollRequested(double fraction, bool animated)
    {
        StopKineticScroll();
        if (DataContext is not ComicReaderViewModel vm) return;
        if (!vm.IsScrollMode) return;

        var scrollViewer = GetReaderScrollViewer();
        if (scrollViewer == null) return;

        if (vm.IsHorizontalScrollMode)
        {
            double dir = vm.IsRightToLeft ? -1.0 : 1.0;
            double vpWidth = scrollViewer.Viewport.Width > 0 ? scrollViewer.Viewport.Width : Bounds.Width;
            if (vpWidth <= 0) vpWidth = 800.0;
            double step = vpWidth * fraction * dir;
            double maxX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
            double targetX = scrollViewer.Offset.X + step;

            if (fraction > 0)
            {
                // Nút Tiến (+): Chuyển chương nếu đã ở cuối, hoặc neo tới cuối trang
                bool atEnd = vm.IsRightToLeft
                    ? (scrollViewer.Offset.X <= 4 || vm.CurrentPageIndex >= vm.Pages.Count - 1 || vm.IsScrollAtEnd)
                    : (scrollViewer.Offset.X >= maxX - 4 || vm.CurrentPageIndex >= vm.Pages.Count - 1 || vm.IsScrollAtEnd);

                if (atEnd)
                {
                    if (vm.CanGoNextChapter)
                    {
                        vm.NextChapter();
                        return;
                    }
                }
                else
                {
                    bool reachingEnd = vm.IsRightToLeft ? targetX <= 4 : targetX >= maxX - 4;
                    if (reachingEnd)
                    {
                        double finalX = vm.IsRightToLeft ? 0 : maxX;
                        scrollViewer.Offset = new Vector(finalX, scrollViewer.Offset.Y);
                        vm.IsScrollAtEnd = true;
                        vm.IsScrollAtStart = false;
                        vm.UpdatePageIndexFromScroll(vm.Pages.Count - 1);
                        return;
                    }
                }
            }
            else if (fraction < 0)
            {
                // Nút Lùi (-): Chuyển chương nếu đã ở đầu, hoặc neo về đầu trang
                bool atStart = vm.IsRightToLeft
                    ? (scrollViewer.Offset.X >= maxX - 4 || vm.CurrentPageIndex <= 0 || vm.IsScrollAtStart)
                    : (scrollViewer.Offset.X <= 4 || vm.CurrentPageIndex <= 0 || vm.IsScrollAtStart);

                if (atStart)
                {
                    if (vm.CanGoPrevChapter)
                    {
                        vm.PrevChapter();
                        return;
                    }
                }
                else
                {
                    bool reachingStart = vm.IsRightToLeft ? targetX >= maxX - 4 : targetX <= 4;
                    if (reachingStart)
                    {
                        double finalX = vm.IsRightToLeft ? maxX : 0;
                        scrollViewer.Offset = new Vector(finalX, scrollViewer.Offset.Y);
                        vm.IsScrollAtStart = true;
                        vm.IsScrollAtEnd = false;
                        vm.UpdatePageIndexFromScroll(0);
                        return;
                    }
                }
            }

            // Xử lý chuyển chương khi cuộn chạm mép ở chế độ Đọc liền mạch
            if (vm.IsContinuousChaptersMode)
            {
                bool atEnd = vm.IsRightToLeft ? targetX <= -10 : targetX >= maxX + 10;
                bool atStart = vm.IsRightToLeft ? targetX >= maxX + 10 : targetX <= -10;
                if (atEnd && vm.CanGoNextChapter)
                {
                    vm.NextChapter();
                    return;
                }
                if (atStart && vm.CanGoPrevChapter)
                {
                    vm.PrevChapter();
                    return;
                }
            }

            scrollViewer.Offset = new Vector(Math.Clamp(targetX, 0, maxX), scrollViewer.Offset.Y);
        }
        else
        {
            double vpHeight = scrollViewer.Viewport.Height > 0 ? scrollViewer.Viewport.Height : Bounds.Height;
            if (vpHeight <= 0) vpHeight = 600.0;
            double step = vpHeight * fraction;
            double maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            double targetY = scrollViewer.Offset.Y + step;

            if (fraction > 0)
            {
                // Nút Tiến (+): Chuyển chương nếu đã ở đáy hoặc đang hiện nút chuyển chap tiếp theo
                if (scrollViewer.Offset.Y >= maxY - 4 || vm.CurrentPageIndex >= vm.Pages.Count - 1 || vm.IsScrollAtEnd)
                {
                    if (vm.CanGoNextChapter)
                    {
                        vm.NextChapter();
                        return;
                    }
                }
                else if (targetY >= maxY - 4)
                {
                    scrollViewer.Offset = new Vector(scrollViewer.Offset.X, maxY);
                    vm.IsScrollAtEnd = true;
                    vm.IsScrollAtStart = false;
                    vm.UpdatePageIndexFromScroll(vm.Pages.Count - 1);
                    return;
                }
            }
            else if (fraction < 0)
            {
                // Nút Lùi (-): Chuyển chương nếu đã ở đỉnh hoặc đang hiện nút chuyển chap trước
                if (scrollViewer.Offset.Y <= 4 || vm.CurrentPageIndex <= 0 || vm.IsScrollAtStart)
                {
                    if (vm.CanGoPrevChapter)
                    {
                        vm.PrevChapter();
                        return;
                    }
                }
                else if (targetY <= 4)
                {
                    scrollViewer.Offset = new Vector(scrollViewer.Offset.X, 0);
                    vm.IsScrollAtStart = true;
                    vm.IsScrollAtEnd = false;
                    vm.UpdatePageIndexFromScroll(0);
                    return;
                }
            }

            // Xử lý chuyển chương khi cuộn chạm mép ở chế độ Đọc liền mạch
            if (vm.IsContinuousChaptersMode)
            {
                if (targetY >= maxY + 10 && vm.CanGoNextChapter)
                {
                    vm.NextChapter();
                    return;
                }
                if (targetY <= -10 && vm.CanGoPrevChapter)
                {
                    vm.PrevChapter();
                    return;
                }
            }

            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, Math.Clamp(targetY, 0, maxY));
        }
    }

    private void OnVmAutoScrollStepRequested(double delta)
    {
        StopKineticScroll();
        var scrollViewer = GetReaderScrollViewer();
        if (scrollViewer != null)
        {
            if (DataContext is ComicReaderViewModel vm && vm.IsHorizontalScrollMode)
            {
                double dir = vm.IsRightToLeft ? -1.0 : 1.0;
                scrollViewer.Offset = new Vector(scrollViewer.Offset.X + delta * dir, scrollViewer.Offset.Y);
            }
            else
            {
                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, scrollViewer.Offset.Y + delta);
            }
        }
    }

    private void OnVmScrollToPageRequested(int pageIndex)
    {
        StopKineticScroll();
        if (DataContext is not ComicReaderViewModel vm) return;
        if (pageIndex < 0 || pageIndex >= vm.Pages.Count) return;

        var listBox = this.FindControl<ListBox>("ReaderPagesListBox");
        if (listBox == null) return;

        int reqVersion = ++_scrollRequestVersion;
        var targetItem = vm.Pages[pageIndex];
        _isProgrammaticScroll = true;

        if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
        {
            double targetY = 0;
            for (int i = 0; i < pageIndex && i < vm.Pages.Count; i++)
            {
                var p = vm.Pages[i];
                targetY += p.DisplayHeight + p.PageMargin.Top + p.PageMargin.Bottom;
            }
            var sv = GetReaderScrollViewer();
            if (sv != null)
            {
                double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                sv.Offset = new Vector(sv.Offset.X, Math.Clamp(targetY, 0, maxY));
            }
        }

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

            if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
            {
                double targetY = 0;
                for (int i = 0; i < pageIndex && i < vm.Pages.Count; i++)
                {
                    var p = vm.Pages[i];
                    targetY += p.DisplayHeight + p.PageMargin.Top + p.PageMargin.Bottom;
                }
                var sv = GetReaderScrollViewer();
                if (sv != null)
                {
                    double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                    sv.Offset = new Vector(sv.Offset.X, Math.Clamp(targetY, 0, maxY));
                }
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
        long now = Environment.TickCount64;
        long pinchElapsed = now - _lastPinchEndTick;
        if (_isProgrammaticScroll || _isUserDraggingScrubber || _isDoubleTapZooming || _isPinching ||
            now < _suppressScrollChangedUntilTick ||
            (_lastPinchEndTick > 0 && pinchElapsed >= 0 && pinchElapsed < 400)) return;
        if (DataContext is not ComicReaderViewModel vm || !vm.IsScrollMode || vm.Pages.Count == 0) return;

        var listBox = this.FindControl<ListBox>("ReaderPagesListBox");
        if (listBox == null) return;

        var scrollViewer = GetReaderScrollViewer();
        if (scrollViewer != null)
        {
            if (vm.IsHorizontalScrollMode)
            {
                double maxX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
                bool isAtEnd = vm.IsRightToLeft ? scrollViewer.Offset.X <= 6 : scrollViewer.Offset.X >= maxX - 6;
                bool isAtStart = vm.IsRightToLeft ? scrollViewer.Offset.X >= maxX - 6 : scrollViewer.Offset.X <= 6;

                if (isAtStart)
                {
                    vm.IsScrollAtStart = true;
                    vm.IsScrollAtEnd = false;
                    vm.UpdatePageIndexFromScroll(0);
                    return;
                }
                else if (isAtEnd && maxX > 0)
                {
                    vm.IsScrollAtStart = false;
                    vm.IsScrollAtEnd = true;
                    vm.UpdatePageIndexFromScroll(vm.Pages.Count - 1);
                    return;
                }
                else
                {
                    vm.IsScrollAtStart = false;
                    vm.IsScrollAtEnd = false;
                }
            }
            else
            {
                double maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                if (scrollViewer.Offset.Y <= 6)
                {
                    vm.IsScrollAtStart = true;
                    vm.IsScrollAtEnd = false;
                    vm.UpdatePageIndexFromScroll(0);
                    return;
                }
                else if (scrollViewer.Offset.Y >= maxY - 6 && maxY > 0)
                {
                    vm.IsScrollAtStart = false;
                    vm.IsScrollAtEnd = true;
                    vm.UpdatePageIndexFromScroll(vm.Pages.Count - 1);
                    return;
                }
                else
                {
                    vm.IsScrollAtStart = false;
                    vm.IsScrollAtEnd = false;
                }
            }
        }

        if (vm.IsHorizontalScrollMode)
        {
            double viewportCenterX = listBox.Bounds.Width * 0.5;
            int bestPageIndexX = -1;
            double bestDistX = double.MaxValue;

            foreach (var child in listBox.GetVisualDescendants())
            {
                if (child is ListBoxItem container && container.DataContext is ReaderPageItem page)
                {
                    var pos = container.TranslatePoint(new Point(0, 0), listBox);
                    if (pos.HasValue)
                    {
                        double left = pos.Value.X;
                        double right = left + container.Bounds.Width;
                        if (left <= viewportCenterX && right >= viewportCenterX)
                        {
                            bestPageIndexX = page.Index;
                            break;
                        }

                        double mid = (left + right) * 0.5;
                        double dist = Math.Abs(mid - viewportCenterX);
                        if (dist < bestDistX)
                        {
                            bestDistX = dist;
                            bestPageIndexX = page.Index;
                        }
                    }
                }
            }

            if (bestPageIndexX >= 0)
            {
                vm.UpdatePageIndexFromScroll(bestPageIndexX);
            }
            return;
        }

        // Chế độ cuộn dọc (Webtoon / Vertical Scroll)
        // Áp dụng cơ chế Hysteresis Sticky Anchor để triệt để chống nhảy/tua trang khi Zoom & Cuộn
        double vh = listBox.Bounds.Height;
        if (vh <= 10) return;

        // 1. Kiểm tra xem trang hiện tại (CurrentPageIndex) có còn hiển thị trên viewport không
        if (vm.CurrentPageIndex >= 0 && vm.CurrentPageIndex < vm.Pages.Count)
        {
            foreach (var child in listBox.GetVisualDescendants())
            {
                if (child is ListBoxItem container && container.DataContext is ReaderPageItem page && page.Index == vm.CurrentPageIndex)
                {
                    var pos = container.TranslatePoint(new Point(0, 0), listBox);
                    if (pos.HasValue)
                    {
                        double top = pos.Value.Y;
                        double bottom = top + container.Bounds.Height;
                        double visibleTop = Math.Max(0, top);
                        double visibleBottom = Math.Min(vh, bottom);
                        double visibleHeight = Math.Max(0, visibleBottom - visibleTop);

                        // Miễn là trang hiện tại còn hiển thị ít nhất 25% chiều cao trên màn hình
                        // HOẶC bao phủ vùng giữa màn hình (từ 40% đến 60% vh):
                        // -> GIỮ NGUYÊN CurrentPageIndex, TUYỆT ĐỐI KHÔNG TUA TRANG!
                        if (visibleHeight >= vh * 0.25 || (top <= vh * 0.5 && bottom >= vh * 0.5))
                        {
                            return;
                        }
                    }
                    break;
                }
            }
        }

        // 2. Nếu trang hiện tại đã cuộn khỏi màn hình, tìm trang mới chiếm diện tích hiển thị lớn nhất (Maximum Overlap)
        int bestPageIndex = -1;
        double maxVisibleHeight = -1;

        foreach (var child in listBox.GetVisualDescendants())
        {
            if (child is ListBoxItem container && container.DataContext is ReaderPageItem page)
            {
                var pos = container.TranslatePoint(new Point(0, 0), listBox);
                if (pos.HasValue)
                {
                    double top = pos.Value.Y;
                    double bottom = top + container.Bounds.Height;
                    double visibleTop = Math.Max(0, top);
                    double visibleBottom = Math.Min(vh, bottom);
                    double visibleHeight = Math.Max(0, visibleBottom - visibleTop);

                    // Tự động kích hoạt nạp ảnh tức thì cho bất kỳ trang nào lọt vào viewport mà chưa nạp
                    if (visibleHeight > 10 && !page.IsLoaded && !page.IsLoading)
                    {
                        _ = vm.EnsureSinglePageLoadedAsync(page);
                    }

                    if (visibleHeight > maxVisibleHeight)
                    {
                        maxVisibleHeight = visibleHeight;
                        bestPageIndex = page.Index;
                    }
                }
            }
        }

        if (bestPageIndex >= 0 && bestPageIndex != vm.CurrentPageIndex && maxVisibleHeight >= vh * 0.20)
        {
            vm.UpdatePageIndexFromScroll(bestPageIndex);
        }
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        StopKineticScroll();

        if (IsScrollBarOrInteractiveControl(e.Source as Visual))
        {
            _lastUserScrollInputTick = Environment.TickCount64;
            _isPointerTapCandidate = false;
            _isPanHolding = false;
            _isPanDragging = false;
            return;
        }

        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed && e.Pointer.Type != PointerType.Touch && e.Pointer.Type != PointerType.Pen)
        {
            _isPointerTapCandidate = false;
            _isPanHolding = false;
            _isPanDragging = false;
            return;
        }

        Point pos = e.GetPosition(this);
        long now = Environment.TickCount64;

        // Dọn sạch triệt để con trỏ mồ côi (Ghost Pointers) nếu không hoạt động trong vòng > 250ms
        if (_pointerLastSeenTicks.Count > 0)
        {
            List<int>? staleKeys = null;
            foreach (var kvp in _pointerLastSeenTicks)
            {
                if (now - kvp.Value > 250)
                {
                    staleKeys ??= new List<int>();
                    staleKeys.Add(kvp.Key);
                }
            }
            if (staleKeys != null)
            {
                foreach (var k in staleKeys)
                {
                    _activePointers.Remove(k);
                    _pointerLastSeenTicks.Remove(k);
                }
            }
        }

        if (_activePointers.Count == 0)
        {
            _isPinching = false;
            _isPanHolding = false;
            _isPanDragging = false;
        }

        _activePointers[e.Pointer.Id] = pos;
        _pointerLastSeenTicks[e.Pointer.Id] = now;

        // Cử chỉ 2 ngón tay Pinch-to-zoom: Kích hoạt ngay khi có từ 2 con trỏ trở lên trên màn hình
        if (_activePointers.Count >= 2)
        {
            var points = _activePointers.Values.Take(2).ToArray();
            _singleTapTimer.Stop();
            _lastTapTick = 0;
            _isPointerTapCandidate = false;
            _isPanHolding = false;
            _isPanDragging = false;
            StopKineticScroll();

            double dist = Distance(points[0], points[1]);
            _pinchStartDistance = Math.Max(10.0, dist);
            _pinchLastDistance = _pinchStartDistance;
            _pinchStartZoom = (DataContext is ComicReaderViewModel vmp) ? vmp.ZoomLevel : 1.0;
            _pinchPivot = new Point((points[0].X + points[1].X) * 0.5, (points[0].Y + points[1].Y) * 0.5);
            _pinchStartPageIndex = (DataContext is ComicReaderViewModel vmPg) ? vmPg.CurrentPageIndex : -1;
            _isPinching = true;
            InitVerticalPinchAnchor(_pinchPivot);
            return;
        }

        _pointerPressPoint = pos;
        _pointerPressTick = now;
        _isPointerTapCandidate = true;

        _panStartPoint = pos;
        _panLastPoint = pos;
        _panStartTick = now;
        _lastPanMoveTick = now;
        _isPanHolding = true;
        _isPanDragging = false;
        _panVelocityX = 0;
        _panVelocityY = 0;
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        Point currentPoint = e.GetPosition(this);
        long moveNow = Environment.TickCount64;
        _activePointers[e.Pointer.Id] = currentPoint;
        _pointerLastSeenTicks[e.Pointer.Id] = moveNow;

        // Dọn sạch con trỏ mồ côi (Ghost Pointers) từ các lần chạm trước (> 250ms) chống tự động kích hoạt Pinch Zoom
        if (_pointerLastSeenTicks.Count > 1)
        {
            List<int>? staleKeys = null;
            foreach (var kvp in _pointerLastSeenTicks)
            {
                if (kvp.Key != e.Pointer.Id && moveNow - kvp.Value > 250)
                {
                    staleKeys ??= new List<int>();
                    staleKeys.Add(kvp.Key);
                }
            }
            if (staleKeys != null)
            {
                foreach (var k in staleKeys)
                {
                    _activePointers.Remove(k);
                    _pointerLastSeenTicks.Remove(k);
                }
            }
        }

        if (DataContext is not ComicReaderViewModel vm) return;
        if (vm.IsMarginEditorOpen) return;

        // Xử lý cử chỉ 2 ngón tay Pinch-to-zoom mượt mà chuẩn ComicScreen và MX Player:
        // Hỗ trợ thu phóng liên tục 2 chiều (Zoom In và Zoom Out) liền mạch trong cùng một lần nhấn cho đến khi buông cả 2 ngón tay
        if (_activePointers.Count >= 2)
        {
            var points = _activePointers.Values.Take(2).ToArray();
            double currentDist = Distance(points[0], points[1]);
            Point currentPivot = new Point((points[0].X + points[1].X) * 0.5, (points[0].Y + points[1].Y) * 0.5);

            if (!_isPinching)
            {
                _isPinching = true;
                _pinchStartDistance = Math.Max(10.0, currentDist);
                _pinchLastDistance = _pinchStartDistance;
                _pinchStartZoom = vm.ZoomLevel;
                _pinchPivot = currentPivot;
                _pinchStartPageIndex = vm.CurrentPageIndex;
                _isPanHolding = false;
                _isPanDragging = false;
                _isPointerTapCandidate = false;
                _singleTapTimer.Stop();
                InitVerticalPinchAnchor(currentPivot);
            }

            if (_pinchLastDistance > 5.0 && currentDist > 5.0)
            {
                double scaleFactor = currentDist / _pinchLastDistance;
                double panDist = Distance(currentPivot, _pinchPivot);

                // Cho phép thu phóng mượt mà ở mọi vi sai di chuyển của cả 2 chiều phóng to và thu nhỏ
                if (Math.Abs(scaleFactor - 1.0) > 0.001 || panDist > 1.0)
                {
                    double oldZoom = vm.ZoomLevel;
                    double newZoom = Math.Clamp(oldZoom * scaleFactor, 0.4, 4.0);
                    newZoom = Math.Round(newZoom, 3);

                    bool zoomChanged = Math.Abs(newZoom - oldZoom) >= 0.001;
                    bool pivotMoved = panDist > 1.0;

                    if (zoomChanged || pivotMoved)
                    {
                        var scrollViewer = vm.IsScrollMode ? GetReaderScrollViewer() : GetFlipScrollViewer();
                        Vector? oldOffset = scrollViewer?.Offset;

                        if (zoomChanged)
                        {
                            vm.ZoomLevel = newZoom;
                        }

                        // Neo tâm thu phóng (Pivot Zoom Alignment) để điểm ảnh dưới ngón tay không bị trôi lệch
                        if (scrollViewer != null && oldOffset.HasValue)
                        {
                            double ratio = zoomChanged ? (newZoom / oldZoom) : 1.0;
                            double maxX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
                            double maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);

                            double panDx = currentPivot.X - _pinchPivot.X;
                            double panDy = currentPivot.Y - _pinchPivot.Y;

                            double newOffsetX = (oldOffset.Value.X + currentPivot.X) * ratio - currentPivot.X - panDx;
                            double newOffsetY;

                            if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
                            {
                                // CHẾ ĐỘ CUỘN DỌC (VERTICAL SCROLL):
                                // Tuyệt đối KHÔNG nhân dồn Offset.Y của tất cả các trang trước!
                                // Bù trừ vi sai Y neo theo chiều cao trang đang đọc để triệt tiêu hiện tượng nhảy/tua trang
                                double pageH = _pinchPageHeight > 0 ? _pinchPageHeight : (Bounds.Height > 0 ? Bounds.Height : 600.0);
                                double pinchDeltaY = _pinchFractionY * pageH * (ratio - 1.0);
                                newOffsetY = oldOffset.Value.Y + pinchDeltaY - panDy;
                                if (zoomChanged)
                                {
                                    _pinchPageHeight = pageH * ratio; // Cập nhật chiều cao ước lượng cho frame tiếp theo
                                }
                            }
                            else
                            {
                                newOffsetY = (oldOffset.Value.Y + currentPivot.Y) * ratio - currentPivot.Y - panDy;
                            }

                            scrollViewer.Offset = new Vector(Math.Clamp(newOffsetX, 0, maxX), Math.Clamp(newOffsetY, 0, maxY));
                        }

                        if (zoomChanged)
                        {
                            _pinchLastDistance = currentDist;
                        }
                        if (pivotMoved)
                        {
                            _pinchPivot = currentPivot;
                        }
                    }
                }
            }

            e.Handled = true;
            return;
        }

        if (_isPinching)
        {
            // Vừa kết thúc pinch, bỏ qua cử chỉ pan đơn để chống giật màn hình
            return;
        }

        if (!_isPanHolding) return;
        double totalDx = currentPoint.X - _panStartPoint.X;
        double totalDy = currentPoint.Y - _panStartPoint.Y;
        double totalDist = Math.Sqrt(totalDx * totalDx + totalDy * totalDy);

        bool isZoomedFlip = vm.IsFlipMode && vm.ZoomLevel > 1.02;
        bool canPanScroll = vm.IsScrollMode || isZoomedFlip;

        if (!canPanScroll)
        {
            // Ở chế độ Flip mode zoom 1.0: không pan ScrollViewer, để cử chỉ Swipe lật trang xử lý khi nhả tay
            return;
        }

        // Ngưỡng kéo (Touch Slop): Phân biệt rõ ràng giữa chạm ngón tay (Tap) và kéo rê (Pan)
        // Trên màn hình cảm ứng Android DPI cao, vi rung lắc da ngón tay đạt tới 20px, do đó ngưỡng là 28.0px
        double slopThreshold = (e.Pointer.Type == PointerType.Mouse) ? 8.0 : 28.0;
        if (!_isPanDragging)
        {
            if (totalDist >= slopThreshold)
            {
                _isPanDragging = true;
                _isPointerTapCandidate = false; // Đã kéo quá ngưỡng slop thì mới hủy Tap
                _lastTapTick = 0;
                _lastTapPoint = new Point(-9999, -9999);
                _singleTapTimer.Stop();

                // QUY TẮC WORKFLOW MỤC 15.14: TUYỆT ĐỐI KHÔNG CAPTURE TOUCH TRÊN ANDROID!
                if (e.Pointer.Type == PointerType.Mouse)
                {
                    var viewport = sender as IInputElement ?? this;
                    e.Pointer.Capture(viewport);
                }

                // Tự động ẩn thanh overlay nếu đang hiện khi người dùng bắt đầu cuộn trang
                if (vm.IsOverlayVisible && !vm.HasActivePopup)
                {
                    vm.IsOverlayVisible = false;
                }
            }
            else
            {
                return;
            }
        }

        double deltaX = currentPoint.X - _panLastPoint.X;
        double deltaY = currentPoint.Y - _panLastPoint.Y;

        long now = Environment.TickCount64;
        double dt = Math.Max(1.0, now - _lastPanMoveTick);
        _lastPanMoveTick = now;

        // Vận tốc tức thời (px/ms)
        double instantVx = deltaX / dt;
        double instantVy = deltaY / dt;
        _panVelocityX = _panVelocityX * 0.35 + instantVx * 0.65;
        _panVelocityY = _panVelocityY * 0.35 + instantVy * 0.65;

        _panLastPoint = currentPoint;
        _lastUserScrollInputTick = now;

        if (vm.IsScrollMode)
        {
            var scrollViewer = GetReaderScrollViewer();
            if (scrollViewer != null)
            {
                double newX = scrollViewer.Offset.X - deltaX;
                double newY = scrollViewer.Offset.Y - deltaY;

                double maxX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
                double maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);

                scrollViewer.Offset = new Vector(Math.Clamp(newX, 0, maxX), Math.Clamp(newY, 0, maxY));
                CheckContinuousOverscroll(vm, scrollViewer, deltaX, deltaY, maxX, maxY);
            }
        }
        else if (isZoomedFlip)
        {
            var flipScrollViewer = GetFlipScrollViewer();
            if (flipScrollViewer != null)
            {
                double newX = flipScrollViewer.Offset.X - deltaX;
                double newY = flipScrollViewer.Offset.Y - deltaY;

                double maxX = Math.Max(0, flipScrollViewer.Extent.Width - flipScrollViewer.Viewport.Width);
                double maxY = Math.Max(0, flipScrollViewer.Extent.Height - flipScrollViewer.Viewport.Height);

                flipScrollViewer.Offset = new Vector(Math.Clamp(newX, 0, maxX), Math.Clamp(newY, 0, maxY));
            }
        }

        e.Handled = true;
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse)
        {
            e.Pointer.Capture(null);
        }

        _activePointers.Remove(e.Pointer.Id);
        _pointerLastSeenTicks.Remove(e.Pointer.Id);

        // Khi người dùng nhấc tay kết thúc cử chỉ Pinch Zoom:
        if (_isPinching)
        {
            _isPinching = false;
            _lastPinchEndTick = Environment.TickCount64;
            _activePointers.Clear(); // Giải phóng 100% con trỏ đa điểm, chống kẹt ID trên Android!
            _pointerLastSeenTicks.Clear();
            _isPanHolding = false;
            _isPanDragging = false;
            _isPointerTapCandidate = false;
            if (DataContext is ComicReaderViewModel vmRel && _pinchStartPageIndex >= 0 && vmRel.Pages.Count > _pinchStartPageIndex)
            {
                vmRel.CurrentPageIndex = _pinchStartPageIndex;
            }
            e.Handled = true;
            return;
        }

        // Nếu chỉ còn 0 hoặc 1 con trỏ, dọn sạch 100% để chống kẹt con trỏ mồ côi của ngón thứ hai
        if (_activePointers.Count <= 1)
        {
            _activePointers.Clear();
            _pointerLastSeenTicks.Clear();
            _isPinching = false;
        }

        long now = Environment.TickCount64;
        long pinchElapsed = now - _lastPinchEndTick;
        // Bỏ qua cú nhả tay nếu vừa kết thúc pinch 2 ngón tay trong vòng 400ms để chống nảy chạm vô ý khi nhấc ngón tay thứ 2
        if (_lastPinchEndTick > 0 && pinchElapsed >= 0 && pinchElapsed < 400)
        {
            _isPanHolding = false;
            _isPanDragging = false;
            _isPointerTapCandidate = false;
            e.Handled = true;
            return;
        }

        _cumulativeOverscrollX = 0;
        _cumulativeOverscrollY = 0;

        bool wasDragging = _isPanDragging;
        _isPanHolding = false;
        _isPanDragging = false;

        if (IsScrollBarOrInteractiveControl(e.Source as Visual))
        {
            _isPointerTapCandidate = false;
            return;
        }

        if (DataContext is not ComicReaderViewModel vm)
        {
            _isPointerTapCandidate = false;
            return;
        }

        Point releasePoint = e.GetPosition(this);
        long elapsedMs = now - _pointerPressTick;
        double dx = releasePoint.X - _pointerPressPoint.X;
        double dy = releasePoint.Y - _pointerPressPoint.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        // Đánh giá chuẩn xác cú Tap (Single-Tap) trên thiết bị di động Android:
        double maxTapDist = (e.Pointer.Type == PointerType.Mouse) ? 15.0 : 30.0;
        bool isLegitTap = distance <= maxTapDist && elapsedMs <= 500;

        if (wasDragging)
        {
            _isPointerTapCandidate = false;
            _lastTapTick = 0;
            _lastTapPoint = new Point(-9999, -9999);
            _singleTapTimer.Stop();
            e.Handled = true;

            // Kích hoạt Quán tính cuộn mượt mà (Kinetic Momentum)
            long releaseNow = Environment.TickCount64;
            long timeSinceLastMove = releaseNow - _lastPanMoveTick;

            if (timeSinceLastMove <= 90)
            {
                const double MaxV = 3.0; // px/ms
                _panVelocityX = Math.Clamp(_panVelocityX, -MaxV, MaxV);
                _panVelocityY = Math.Clamp(_panVelocityY, -MaxV, MaxV);

                if (Math.Abs(_panVelocityX) > 0.08 || Math.Abs(_panVelocityY) > 0.08)
                {
                    _kineticVelocityX = _panVelocityX;
                    _kineticVelocityY = _panVelocityY;
                    _kineticTimer.Start();
                }
            }
            return;
        }

        if (!isLegitTap && !_isPointerTapCandidate) return;
        _isPointerTapCandidate = false;

        long tapNow = Environment.TickCount64;
        double tapDx = releasePoint.X - _lastTapPoint.X;
        double tapDy = releasePoint.Y - _lastTapPoint.Y;
        double tapDist = Math.Sqrt(tapDx * tapDx + tapDy * tapDy);

        if (tapNow - _lastTapTick <= 500 && tapDist < 50.0)
        {
            // Chạm đúp (Double-Tap) nhanh trong vòng 500ms -> HỦY timer single-tap ngay lập tức!
            _singleTapTimer.Stop();
            _lastTapTick = 0;

            PerformDoubleTapZoom(releasePoint);
            return;
        }

        // Chạm đơn (Single-Tap): Ghi nhận vị trí và bắt đầu đếm lùi 500ms (0.5 giây).
        // Chỉ khi hết 500ms mà không có cú chạm đúp nào thì mới ẩn / hiện thanh công cụ!
        _lastTapTick = tapNow;
        _lastTapPoint = releasePoint;
        _singleTapTimer.Stop();
        _singleTapTimer.Start();
    }

    /// <summary>
    /// Thực hiện thao tác Chạm đúp (Double-Tap) Zoom In / Out mượt mà, neo điểm tựa chính xác tuyệt đối,
    /// loại bỏ hoàn toàn lỗi lệch trang / tua trang trong cả chế độ Cuộn dọc (Vertical Scroll) và Lật trang (Flip Mode).
    /// </summary>
    public void PerformDoubleTapZoom(Point releasePoint)
    {
        if (DataContext is not ComicReaderViewModel vm) return;

        int savedPageIndex = vm.CurrentPageIndex;
        double oldZoom = vm.ZoomLevel;
        var scrollViewer = vm.IsScrollMode ? GetReaderScrollViewer() : GetFlipScrollViewer();
        Vector oldOffset = scrollViewer?.Offset ?? Vector.Zero;

        var lb = this.FindControl<ListBox>("ReaderPagesListBox");
        Point touchInLb = releasePoint;
        if (lb != null)
        {
            var ptInLb = this.TranslatePoint(releasePoint, lb);
            if (ptInLb.HasValue) touchInLb = ptInLb.Value;
        }

        _isDoubleTapZooming = true;
        _isProgrammaticScroll = true;
        _suppressScrollChangedUntilTick = Environment.TickCount64 + 400;

        // Kích hoạt Double-Tap Zoom in / out trên mọi vùng (true = không phụ thuộc vùng ảnh)
        vm.ExecuteDoubleTapZoom(true);

        double newZoom = vm.ZoomLevel;
        double ratio = (oldZoom > 0.05) ? (newZoom / oldZoom) : 1.0;

        if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
        {
            // Nguyên lý Bất biến Điểm chạm Nội dung (Content Anchor Invariance):
            // Tọa độ Y tuyệt đối trên toàn bộ dải trang trước khi zoom
            double contentYBefore = oldOffset.Y + touchInLb.Y;
            // Tọa độ Y tuyệt đối trên dải trang sau khi zoom
            double contentYAfter = contentYBefore * ratio;
            // Vị trí cuộn Offset mới để điểm chạm tiếp tục nằm chính xác dưới ngón tay người dùng
            double targetOffsetY = Math.Max(0, contentYAfter - touchInLb.Y);

            // Tương tự cho trục X khi phóng to ngang
            double contentXBefore = oldOffset.X + touchInLb.X;
            double contentXAfter = contentXBefore * ratio;
            double targetOffsetX = Math.Max(0, contentXAfter - touchInLb.X);

            // Trang mục tiêu luôn luôn là trang hiện tại đang đọc (savedPageIndex),
            // tuyệt đối KHÔNG cho phép Double-Tap tua hoặc đổi sang trang khác!
            int targetPageIndex = savedPageIndex;

            Dispatcher.UIThread.Post(() =>
            {
                var sv = GetReaderScrollViewer();
                if (sv != null)
                {
                    double maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
                    double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                    sv.Offset = new Vector(Math.Clamp(targetOffsetX, 0, maxX), Math.Clamp(targetOffsetY, 0, maxY));
                }

                vm.CurrentPageIndex = targetPageIndex;
                _isDoubleTapZooming = false;
                _isProgrammaticScroll = false;
                _suppressScrollChangedUntilTick = Environment.TickCount64 + 250;
            }, DispatcherPriority.Render);
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                var sv = vm.IsScrollMode ? GetReaderScrollViewer() : GetFlipScrollViewer();
                if (sv != null)
                {
                    double maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
                    double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                    double targetX = (oldOffset.X + releasePoint.X) * ratio - releasePoint.X;
                    double targetY = (oldOffset.Y + releasePoint.Y) * ratio - releasePoint.Y;
                    sv.Offset = new Vector(Math.Clamp(targetX, 0, maxX), Math.Clamp(targetY, 0, maxY));
                }

                vm.CurrentPageIndex = savedPageIndex;
                _isDoubleTapZooming = false;
                _isProgrammaticScroll = false;
                _suppressScrollChangedUntilTick = Environment.TickCount64 + 250;
            }, DispatcherPriority.Render);
        }
    }

    private bool CheckIfPointIsInsideImage(Point p)
    {
        if (DataContext is not ComicReaderViewModel vm) return true;

        double vw = Math.Max(1.0, Bounds.Width);
        double vh = Math.Max(1.0, Bounds.Height);

        if (vm.IsFlipMode)
        {
            double imgW = vm.FlipDisplayWidth;
            double imgH = vm.FlipDisplayHeight;
            if (double.IsNaN(imgW) || double.IsNaN(imgH) || imgW <= 0 || imgH <= 0)
                return true;

            double left = (vw - imgW) / 2.0;
            double top = (vh - imgH) / 2.0;
            return p.X >= left && p.X <= left + imgW && p.Y >= top && p.Y <= top + imgH;
        }
        else if (vm.IsScrollMode)
        {
            return true;
        }

        return true;
    }

    private void OnViewportPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _activePointers.Clear();
        _pointerLastSeenTicks.Clear();
        _isPinching = false;
        _isPanHolding = false;
        _isPanDragging = false;
        _isPointerTapCandidate = false;
        _lastTapTick = 0;
        _lastTapPoint = new Point(-9999, -9999);
        _singleTapTimer.Stop();
    }

    private void OnViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        StopKineticScroll();
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

        // Chế độ Cuộn ngang liên tiếp: lăn chuột dọc chuyển thành cuộn ngang mượt mà
        if (vm.IsScrollMode && vm.IsHorizontalScrollMode && vm.ZoomLevel <= 1.05)
        {
            var scrollViewer = GetReaderScrollViewer();
            if (scrollViewer != null)
            {
                double step = (e.Delta.Y != 0 ? -e.Delta.Y : e.Delta.X) * 140.0;
                double dir = vm.IsRightToLeft ? -1.0 : 1.0;
                double maxX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
                double targetX = scrollViewer.Offset.X + step * dir;

                if (vm.IsContinuousChaptersMode)
                {
                    long now = Environment.TickCount64;
                    if (now - _lastAutoChapterTransitionTick > 1200)
                    {
                        bool atEnd = vm.IsRightToLeft ? targetX <= -10 : targetX >= maxX + 10;
                        bool atStart = vm.IsRightToLeft ? targetX >= maxX + 10 : targetX <= -10;
                        if (atEnd && vm.CanGoNextChapter)
                        {
                            _lastAutoChapterTransitionTick = now;
                            vm.NextChapter();
                            e.Handled = true;
                            return;
                        }
                        if (atStart && vm.CanGoPrevChapter)
                        {
                            _lastAutoChapterTransitionTick = now;
                            vm.PrevPage();
                            e.Handled = true;
                            return;
                        }
                    }
                }

                scrollViewer.Offset = new Vector(Math.Clamp(targetX, 0, maxX), scrollViewer.Offset.Y);
                e.Handled = true;
                return;
            }
        }

        // Chế độ Cuộn dọc: lăn chuột vượt biên ở chế độ đọc liền mạch
        if (vm.IsScrollMode && !vm.IsHorizontalScrollMode && vm.ZoomLevel <= 1.05 && vm.IsContinuousChaptersMode)
        {
            var scrollViewer = GetReaderScrollViewer();
            if (scrollViewer != null)
            {
                double maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                long now = Environment.TickCount64;
                if (scrollViewer.Offset.Y >= maxY - 2 && e.Delta.Y < -0.15 && vm.CanGoNextChapter && now - _lastAutoChapterTransitionTick > 1200)
                {
                    _lastAutoChapterTransitionTick = now;
                    vm.NextChapter();
                    e.Handled = true;
                    return;
                }
                if (scrollViewer.Offset.Y <= 2 && e.Delta.Y > 0.15 && vm.CanGoPrevChapter && now - _lastAutoChapterTransitionTick > 1200)
                {
                    _lastAutoChapterTransitionTick = now;
                    vm.PrevPage();
                    e.Handled = true;
                    return;
                }
            }
        }

        // Ở chế độ Flip mode (khi không giữ Ctrl): lăn chuột chuyển trang mượt mà
        if (vm.IsFlipMode && vm.ZoomLevel <= 1.05)
        {
            if (e.Delta.Y < 0) vm.NextPage();
            else if (e.Delta.Y > 0) vm.PrevPage();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Kiểm tra cử chỉ kéo vượt biên trong chế độ cuộn trang để tự động chuyển sang chương tiếp theo/chương trước
    /// khi người dùng bật tùy chọn "Đọc liền mạch" (Continuous Chapters Mode).
    /// </summary>
    private void CheckContinuousOverscroll(ComicReaderViewModel vm, ScrollViewer scrollViewer, double deltaX, double deltaY, double maxX, double maxY)
    {
        if (vm.IsLoadingChapter) return;
        long now = Environment.TickCount64;
        if (now - _lastAutoChapterTransitionTick < 1400) return;

        double threshold = vm.IsContinuousChaptersMode ? 50.0 : 75.0;

        if (vm.IsHorizontalScrollMode)
        {
            bool isAtEnd = vm.IsRightToLeft
                ? scrollViewer.Offset.X <= 4
                : scrollViewer.Offset.X >= maxX - 4;
            bool isAtStart = vm.IsRightToLeft
                ? scrollViewer.Offset.X >= maxX - 4
                : scrollViewer.Offset.X <= 4;

            double pullDelta = vm.IsRightToLeft ? deltaX : -deltaX;

            if (isAtEnd && pullDelta > 0)
            {
                vm.IsScrollAtEnd = true;
                vm.IsScrollAtStart = false;
                vm.UpdatePageIndexFromScroll(vm.Pages.Count - 1);

                _cumulativeOverscrollX += pullDelta;
                if (_cumulativeOverscrollX > threshold && vm.CanGoNextChapter)
                {
                    _cumulativeOverscrollX = 0;
                    _lastAutoChapterTransitionTick = now;
                    StopKineticScroll();
                    vm.NextChapter();
                }
            }
            else if (isAtStart && pullDelta < 0)
            {
                vm.IsScrollAtStart = true;
                vm.IsScrollAtEnd = false;
                vm.UpdatePageIndexFromScroll(0);

                _cumulativeOverscrollX += pullDelta;
                if (_cumulativeOverscrollX < -threshold && vm.CanGoPrevChapter)
                {
                    _cumulativeOverscrollX = 0;
                    _lastAutoChapterTransitionTick = now;
                    StopKineticScroll();
                    vm.PrevChapter();
                }
            }
            else
            {
                _cumulativeOverscrollX = 0;
            }
        }
        else
        {
            bool isAtBottom = scrollViewer.Offset.Y >= maxY - 4;
            bool isAtTop = scrollViewer.Offset.Y <= 4;

            if (isAtBottom && deltaY < 0)
            {
                vm.IsScrollAtEnd = true;
                vm.IsScrollAtStart = false;
                vm.UpdatePageIndexFromScroll(vm.Pages.Count - 1);

                _cumulativeOverscrollY += Math.Abs(deltaY);
                if (_cumulativeOverscrollY > threshold && vm.CanGoNextChapter)
                {
                    _cumulativeOverscrollY = 0;
                    _lastAutoChapterTransitionTick = now;
                    StopKineticScroll();
                    vm.NextChapter();
                }
            }
            else if (isAtTop && deltaY > 0)
            {
                vm.IsScrollAtStart = true;
                vm.IsScrollAtEnd = false;
                vm.UpdatePageIndexFromScroll(0);

                _cumulativeOverscrollY -= Math.Abs(deltaY);
                if (_cumulativeOverscrollY < -threshold && vm.CanGoPrevChapter)
                {
                    _cumulativeOverscrollY = 0;
                    _lastAutoChapterTransitionTick = now;
                    StopKineticScroll();
                    vm.PrevChapter();
                }
            }
            else
            {
                _cumulativeOverscrollY = 0;
            }
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
                if (vm.IsFlipMode || vm.IsHorizontalScrollMode)
                {
                    vm.TapLeftZone();
                    e.Handled = true;
                }
                break;

            case Key.Right:
            case Key.D:
                if (vm.IsFlipMode || vm.IsHorizontalScrollMode)
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

            case Key.W:
                vm.SetFitMode("FitWidth");
                e.Handled = true;
                break;

            case Key.H:
                vm.SetFitMode("FitHeight");
                e.Handled = true;
                break;

            case Key.S:
                vm.SetFitMode("StretchFull");
                e.Handled = true;
                break;

            case Key.B:
                vm.SetFitMode("FitScreen");
                e.Handled = true;
                break;

            case Key.Z:
                vm.OpenZoomPopup();
                e.Handled = true;
                break;

            case Key.V:
                vm.ToggleAutoScroll();
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
