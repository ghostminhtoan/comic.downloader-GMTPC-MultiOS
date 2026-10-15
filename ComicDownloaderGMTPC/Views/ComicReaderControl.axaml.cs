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
    // Theo dõi con trỏ đa điểm chạm (Multi-touch Tracking) cho Pinch-to-zoom chuẩn ComicScreen / MX Player
    private readonly System.Collections.Generic.Dictionary<int, Point> _activePointers = new();
    private readonly System.Collections.Generic.Dictionary<int, long> _pointerLastSeenTicks = new();
    private bool _isPinching;
    private double _pinchStartDistance;
    private double _pinchStartZoom;
    private Vector _pinchStartOffset;
    private Point _pinchStartPivot;
    private Point _pinchPivotInLb;
    private long _lastPinchEndTick;
    private int _pinchTargetPageIndex = -1;
    private double _pinchTargetNormX = 0.5;
    private double _pinchTargetNormY = 0.5;
    public int PinchTargetPageIndex => _pinchTargetPageIndex;
    public double PinchTargetNormX => _pinchTargetNormX;
    public double PinchTargetNormY => _pinchTargetNormY;
    private double _pinchTargetOffsetX;
    private double _pinchTargetOffsetY;

    /// <summary>
    /// Tìm chính xác trang mà điểm chạm (ngón tay) đang nằm lên trong ListBox chế độ cuộn dọc.
    /// Giúp app nhận diện được ngón tay đang ở trang nào để focus Pinch & Zoom ở riêng trang đó!
    /// </summary>
    public int FindPageIndexAtPoint(Point ptInLb, out double pageTopInLb, out double pageHeight)
    {
        pageTopInLb = 0;
        pageHeight = Bounds.Height > 0 ? Bounds.Height : 600.0;
        if (DataContext is not ComicReaderViewModel vm) return -1;

        var lb = this.FindControl<ListBox>("ReaderPagesListBox");
        if (lb == null) return vm.CurrentPageIndex;

        // 1. Quét tìm container ListBoxItem bao phủ đúng tọa độ Y của điểm chạm
        foreach (var child in lb.GetVisualDescendants())
        {
            if (child is ListBoxItem container && container.DataContext is ReaderPageItem page)
            {
                var pos = container.TranslatePoint(new Point(0, 0), lb);
                if (pos.HasValue)
                {
                    double top = pos.Value.Y;
                    double bottom = top + container.Bounds.Height;
                    if (ptInLb.Y >= top && ptInLb.Y <= bottom)
                    {
                        pageTopInLb = top;
                        pageHeight = Math.Max(1.0, container.Bounds.Height);
                        return page.Index;
                    }
                }
            }
        }

        // 2. Nếu điểm chạm nằm ở khoảng lề, chọn trang có tâm gần nhất với điểm chạm
        double minDist = double.MaxValue;
        int bestIndex = -1;
        foreach (var child in lb.GetVisualDescendants())
        {
            if (child is ListBoxItem container && container.DataContext is ReaderPageItem page)
            {
                var pos = container.TranslatePoint(new Point(0, 0), lb);
                if (pos.HasValue)
                {
                    double mid = pos.Value.Y + container.Bounds.Height * 0.5;
                    double dist = Math.Abs(mid - ptInLb.Y);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        bestIndex = page.Index;
                        pageTopInLb = pos.Value.Y;
                        pageHeight = Math.Max(1.0, container.Bounds.Height);
                    }
                }
            }
        }

        if (bestIndex >= 0) return bestIndex;
        return Math.Clamp(vm.CurrentPageIndex, 0, Math.Max(0, vm.Pages.Count - 1));
    }

    private void StartPinchGesture(Point p1, Point p2)
    {
        StopKineticScroll();
        if (DataContext is not ComicReaderViewModel vm) return;

        double dist = Distance(p1, p2);
        _pinchStartDistance = Math.Max(10.0, dist);
        _pinchStartZoom = vm.ZoomLevel;

        var sv = vm.IsScrollMode ? GetReaderScrollViewer() : GetFlipScrollViewer();
        _pinchStartOffset = sv?.Offset ?? Vector.Zero;
        _pinchTargetOffsetX = _pinchStartOffset.X;
        _pinchTargetOffsetY = _pinchStartOffset.Y;

        Point currentPivot = new Point((p1.X + p2.X) * 0.5, (p1.Y + p2.Y) * 0.5);
        _pinchStartPivot = currentPivot;

        Point inLb = currentPivot;
        var lb = this.FindControl<ListBox>("ReaderPagesListBox");
        if (lb != null)
        {
            var pt = this.TranslatePoint(currentPivot, lb);
            if (pt.HasValue) inLb = pt.Value;
        }
        _pinchPivotInLb = inLb;

        double pageTopInLb = 0;
        double pageHeight = 0;

        // ĐỌC CHÍNH XÁC NGÓN TAY ĐANG Ở TRANG NÀO VÀ TỌA ĐỘ TƯƠNG ĐỐI TRONG TRANG ĐÓ:
        if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
        {
            _pinchTargetPageIndex = FindPageIndexAtPoint(_pinchPivotInLb, out pageTopInLb, out pageHeight);
            if (pageHeight > 1.0)
            {
                _pinchTargetNormY = Math.Clamp((_pinchPivotInLb.Y - pageTopInLb) / pageHeight, 0.0, 1.0);
            }
            else
            {
                _pinchTargetNormY = 0.5;
            }

            double pageWidth = lb?.Bounds.Width ?? Bounds.Width;
            double pageLeftInLb = 0;
            if (lb != null)
            {
                foreach (var child in lb.GetVisualDescendants())
                {
                    if (child is ListBoxItem container && container.DataContext is ReaderPageItem page && page.Index == _pinchTargetPageIndex)
                    {
                        var pos = container.TranslatePoint(new Point(0, 0), lb);
                        if (pos.HasValue)
                        {
                            pageLeftInLb = pos.Value.X;
                            pageWidth = Math.Max(1.0, container.Bounds.Width);
                        }
                        break;
                    }
                }
            }
            _pinchTargetNormX = Math.Clamp((_pinchPivotInLb.X - pageLeftInLb) / Math.Max(1.0, pageWidth), 0.0, 1.0);
        }
        else if (vm.IsScrollMode && vm.IsHorizontalScrollMode)
        {
            _pinchTargetPageIndex = vm.CurrentPageIndex;
            _pinchTargetNormX = 0.5;
            _pinchTargetNormY = 0.5;
        }
        else
        {
            _pinchTargetPageIndex = vm.CurrentPageIndex;
            _pinchTargetNormX = 0.5;
            _pinchTargetNormY = 0.5;
        }

        // Khóa cứng trang đang đọc vào chính trang mà ngón tay đang chạm lên:
        if (_pinchTargetPageIndex >= 0 && _pinchTargetPageIndex < vm.Pages.Count)
        {
            vm.CurrentPageIndex = _pinchTargetPageIndex;
        }

        Console.WriteLine($"[DEBUG PINCH START] TargetPage={_pinchTargetPageIndex}, PageTop={pageTopInLb}, PageH={pageHeight}, NormY={_pinchTargetNormY:0.000}, NormX={_pinchTargetNormX:0.000}, PivotInLb={_pinchPivotInLb}");
        _isPinching = true;
        _isPanHolding = false;
        _isPanDragging = false;
        _isPointerTapCandidate = false;
        _singleTapTimer.Stop();
        _lastTapTick = 0;
        _suppressScrollChangedUntilTick = Environment.TickCount64 + 1000;
    }

    private void UpdatePinchGesture(Point p1, Point p2)
    {
        if (DataContext is not ComicReaderViewModel vm || !_isPinching) return;

        double currentDist = Distance(p1, p2);
        if (_pinchStartDistance <= 5.0 || currentDist <= 5.0) return;

        double scale = currentDist / _pinchStartDistance;
        double newZoom = Math.Clamp(Math.Round(_pinchStartZoom * scale, 3), 0.4, 4.0);

        // Cập nhật mức Zoom (kích hoạt ApplyFitModeLayout tính lại DisplayWidth / DisplayHeight cho mọi trang)
        vm.ZoomLevel = newZoom;

        Point currentPivot = new Point((p1.X + p2.X) * 0.5, (p1.Y + p2.Y) * 0.5);
        Point currentPivotInLb = currentPivot;
        var lb = this.FindControl<ListBox>("ReaderPagesListBox");
        if (lb != null)
        {
            var pt = this.TranslatePoint(currentPivot, lb);
            if (pt.HasValue) currentPivotInLb = pt.Value;
        }

        if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
        {
            var sv = GetReaderScrollViewer();
            if (sv != null && _pinchTargetPageIndex >= 0 && _pinchTargetPageIndex < vm.Pages.Count)
            {
                // 1. Tọa độ Document Y tuyệt đối của đỉnh trang mục tiêu ở mức zoom mới
                double targetPageDocY = 0;
                for (int i = 0; i < _pinchTargetPageIndex; i++)
                {
                    var p = vm.Pages[i];
                    targetPageDocY += p.DisplayHeight + p.PageMargin.Top + p.PageMargin.Bottom;
                }

                var targetPage = vm.Pages[_pinchTargetPageIndex];
                double targetPageHeight = targetPage.DisplayHeight;

                // 2. Tọa độ điểm neo bên trong trang mục tiêu
                double anchorPointInDocY = targetPageDocY + (_pinchTargetNormY * targetPageHeight);

                // 3. Offset Y mới để điểm neo trên trang luôn nằm chính xác dưới vị trí ngón tay (currentPivotInLb.Y)
                double newOffsetY = anchorPointInDocY - currentPivotInLb.Y;

                // 4. Tính toán Extent thực tế của toàn bộ truyện ở mức zoom mới (tránh bị kẹp vào Extent cũ chưa kịp cập nhật)
                double totalDocHeight = 0;
                for (int i = 0; i < vm.Pages.Count; i++)
                {
                    var p = vm.Pages[i];
                    totalDocHeight += p.DisplayHeight + p.PageMargin.Top + p.PageMargin.Bottom;
                }
                double maxOffsetY = Math.Max(0, totalDocHeight - sv.Viewport.Height);
                newOffsetY = Math.Clamp(newOffsetY, 0, maxOffsetY);

                // 5. Tính toán Offset X
                double targetPageWidth = targetPage.DisplayWidth;
                double newOffsetX = 0;
                if (targetPageWidth > sv.Viewport.Width)
                {
                    double anchorPointInDocX = _pinchTargetNormX * targetPageWidth;
                    newOffsetX = anchorPointInDocX - currentPivotInLb.X;
                    double maxOffsetX = Math.Max(0, targetPageWidth - sv.Viewport.Width);
                    newOffsetX = Math.Clamp(newOffsetX, 0, maxOffsetX);
                }

                _pinchTargetOffsetX = newOffsetX;
                _pinchTargetOffsetY = newOffsetY;

                _isProgrammaticScroll = true;
                sv.Offset = new Vector(newOffsetX, newOffsetY);
                _isProgrammaticScroll = false;

                Dispatcher.UIThread.Post(() =>
                {
                    if (_isPinching && vm.IsScrollMode && !vm.IsHorizontalScrollMode)
                    {
                        var curSv = GetReaderScrollViewer();
                        if (curSv != null)
                        {
                            _isProgrammaticScroll = true;
                            curSv.Offset = new Vector(_pinchTargetOffsetX, _pinchTargetOffsetY);
                            _isProgrammaticScroll = false;
                        }
                    }
                }, DispatcherPriority.Render);

                Console.WriteLine($"[DEBUG PINCH STEP] Zoom={newZoom:0.00}, DocY={targetPageDocY:0.0}, PageH={targetPageHeight:0.0}, AnchorDocY={anchorPointInDocY:0.0}, OffsetY={newOffsetY:0.0}, PivotLbY={currentPivotInLb.Y:0.0}, ScreenAnchor={anchorPointInDocY - newOffsetY:0.0}");
            }

            // Khóa cứng trang đọc hiện tại, chống tua trang trong suốt cử chỉ Pinch
            if (_pinchTargetPageIndex >= 0 && _pinchTargetPageIndex < vm.Pages.Count)
            {
                vm.CurrentPageIndex = _pinchTargetPageIndex;
            }
            _suppressScrollChangedUntilTick = Environment.TickCount64 + 1000;
        }
        else if (vm.IsScrollMode && vm.IsHorizontalScrollMode)
        {
            var sv = GetReaderScrollViewer();
            if (sv != null && _pinchTargetPageIndex >= 0 && _pinchTargetPageIndex < vm.Pages.Count)
            {
                double targetPageDocX = 0;
                for (int i = 0; i < _pinchTargetPageIndex; i++)
                {
                    var p = vm.Pages[i];
                    targetPageDocX += p.DisplayWidth + p.PageMargin.Left + p.PageMargin.Right;
                }

                var targetPage = vm.Pages[_pinchTargetPageIndex];
                double targetPageWidth = targetPage.DisplayWidth;
                double anchorPointInDocX = targetPageDocX + (_pinchTargetNormX * targetPageWidth);
                double newOffsetX = anchorPointInDocX - currentPivotInLb.X;

                double totalDocWidth = 0;
                for (int i = 0; i < vm.Pages.Count; i++)
                {
                    var p = vm.Pages[i];
                    totalDocWidth += p.DisplayWidth + p.PageMargin.Left + p.PageMargin.Right;
                }
                double maxOffsetX = Math.Max(0, totalDocWidth - sv.Viewport.Width);
                newOffsetX = Math.Clamp(newOffsetX, 0, maxOffsetX);

                _isProgrammaticScroll = true;
                sv.Offset = new Vector(newOffsetX, sv.Offset.Y);
                _isProgrammaticScroll = false;
            }

            if (_pinchTargetPageIndex >= 0 && _pinchTargetPageIndex < vm.Pages.Count)
            {
                vm.CurrentPageIndex = _pinchTargetPageIndex;
            }
            _suppressScrollChangedUntilTick = Environment.TickCount64 + 1000;
        }
        else
        {
            var flipSv = GetFlipScrollViewer();
            if (flipSv != null)
            {
                double ratio = (_pinchStartZoom > 0.05) ? (newZoom / _pinchStartZoom) : 1.0;
                double newTargetX = (_pinchStartOffset.X + currentPivot.X) * ratio - currentPivot.X;
                double newTargetY = (_pinchStartOffset.Y + currentPivot.Y) * ratio - currentPivot.Y;
                double maxX = Math.Max(0, flipSv.Extent.Width - flipSv.Viewport.Width);
                double maxY = Math.Max(0, flipSv.Extent.Height - flipSv.Viewport.Height);
                flipSv.Offset = new Vector(Math.Clamp(newTargetX, 0, maxX), Math.Clamp(newTargetY, 0, maxY));
            }
        }
    }

    public void PerformPinchZoomStep(Point p1, Point p2)
    {
        if (!_isPinching)
        {
            StartPinchGesture(p1, p2);
        }
        else
        {
            UpdatePinchGesture(p1, p2);
        }
    }

    public bool IsPinching => _isPinching;

    public void EndPinchZoom()
    {
        _isPinching = false;
        _lastPinchEndTick = Environment.TickCount64;
        _suppressScrollChangedUntilTick = Environment.TickCount64 + 400;
        _activePointers.Clear();
        _pointerLastSeenTicks.Clear();
        _isPanHolding = false;
        _isPanDragging = false;
        _isPointerTapCandidate = false;
        if (DataContext is ComicReaderViewModel vm && _pinchTargetPageIndex >= 0 && vm.Pages.Count > _pinchTargetPageIndex)
        {
            vm.CurrentPageIndex = _pinchTargetPageIndex;
        }
    }

    /// <summary>
    /// Thực hiện thao tác Hold and Drag cuộn trang mượt mà từ startPoint tới endPoint.
    /// Trả về true nếu cuộn thành công và hoàn toàn không bị kích hoạt nhầm sang Pinch Zoom.
    /// </summary>
    public bool PerformHoldAndDrag(Point startPoint, Point endPoint, int stepCount = 5)
    {
        if (DataContext is not ComicReaderViewModel vm) return false;

        // Bắt đầu Drag: đảm bảo sạch sẽ
        _activePointers.Clear();
        _pointerLastSeenTicks.Clear();
        _isPinching = false;

        int pointerId = 999;
        long now = Environment.TickCount64;
        _activePointers[pointerId] = startPoint;
        _pointerLastSeenTicks[pointerId] = now;

        _pointerPressPoint = startPoint;
        _pointerPressTick = now;
        _panStartPoint = startPoint;
        _panLastPoint = startPoint;
        _panStartTick = now;
        _lastPanMoveTick = now;
        _isPanHolding = true;
        _isPanDragging = true;
        _isPointerTapCandidate = false;

        for (int i = 1; i <= stepCount; i++)
        {
            double t = (double)i / stepCount;
            Point curPt = new Point(
                startPoint.X + (endPoint.X - startPoint.X) * t,
                startPoint.Y + (endPoint.Y - startPoint.Y) * t
            );

            double deltaX = curPt.X - _panLastPoint.X;
            double deltaY = curPt.Y - _panLastPoint.Y;

            _panLastPoint = curPt;
            _activePointers[pointerId] = curPt;
            _pointerLastSeenTicks[pointerId] = Environment.TickCount64;

            if (_isPinching)
            {
                // Nếu bị kích hoạt pinch trong lúc drag 1 ngón tay -> Lỗi!
                return false;
            }

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
                }
            }
        }

        // Kết thúc Drag (Release)
        _isPanHolding = false;
        _isPanDragging = false;
        _activePointers.Clear();
        _pointerLastSeenTicks.Clear();

        return !_isPinching;
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

        if (_isPinching)
        {
            if (DataContext is ComicReaderViewModel vmP && _pinchTargetPageIndex >= 0 && vmP.Pages.Count > _pinchTargetPageIndex)
            {
                vmP.CurrentPageIndex = _pinchTargetPageIndex;
            }
            if (!_isProgrammaticScroll && DataContext is ComicReaderViewModel vmScroll && vmScroll.IsScrollMode && !vmScroll.IsHorizontalScrollMode)
            {
                var curSv = GetReaderScrollViewer();
                if (curSv != null && Math.Abs(curSv.Offset.Y - _pinchTargetOffsetY) > 0.5)
                {
                    _isProgrammaticScroll = true;
                    curSv.Offset = new Vector(_pinchTargetOffsetX, _pinchTargetOffsetY);
                    _isProgrammaticScroll = false;
                }
            }
            return;
        }

        if (_isProgrammaticScroll || _isUserDraggingScrubber || _isDoubleTapZooming ||
            now < _suppressScrollChangedUntilTick ||
            (_lastPinchEndTick > 0 && pinchElapsed >= 0 && pinchElapsed < 800))
        {
            if (DataContext is ComicReaderViewModel vmP && _pinchTargetPageIndex >= 0 && vmP.Pages.Count > _pinchTargetPageIndex)
            {
                vmP.CurrentPageIndex = _pinchTargetPageIndex;
            }
            return;
        }
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

        long pinchElapsed = now - _lastPinchEndTick;
        // Bỏ qua nảy chạm ngón tay thứ hai trong vòng 100ms sau khi pinch kết thúc
        if (_lastPinchEndTick > 0 && pinchElapsed >= 0 && pinchElapsed < 100)
        {
            _activePointers.Clear();
            _pointerLastSeenTicks.Clear();
            _isPointerTapCandidate = false;
            _isPanHolding = false;
            _isPanDragging = false;
            return;
        }

        // Dọn sạch con trỏ mồ côi (Ghost Pointers) khi KHÔNG trong cử chỉ pinch (> 150ms)
        if (!_isPinching && _pointerLastSeenTicks.Count > 0)
        {
            List<int>? staleKeys = null;
            foreach (var kvp in _pointerLastSeenTicks)
            {
                if (now - kvp.Value > 150)
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
            StartPinchGesture(points[0], points[1]);
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

        long pinchElapsed = moveNow - _lastPinchEndTick;
        // Bỏ qua cử chỉ di chuyển mồ côi trong vòng 100ms sau khi pinch kết thúc
        if (_lastPinchEndTick > 0 && pinchElapsed >= 0 && pinchElapsed < 100)
        {
            _isPanHolding = false;
            _isPanDragging = false;
            _isPointerTapCandidate = false;
            e.Handled = true;
            return;
        }

        // QUY TẮC CỐT LÕI: Chỉ cập nhật con trỏ nếu con trỏ này ĐÃ ĐƯỢC ghi nhận trong Pressed!
        // Tuyệt đối không tự tiện thêm con trỏ ma vào _activePointers!
        if (!_activePointers.ContainsKey(e.Pointer.Id))
        {
            if (!_isPinching && !_isPanHolding)
            {
                return;
            }
            _activePointers[e.Pointer.Id] = currentPoint;
        }
        else
        {
            _activePointers[e.Pointer.Id] = currentPoint;
        }
        _pointerLastSeenTicks[e.Pointer.Id] = moveNow;

        // Dọn sạch con trỏ mồ côi nếu không di chuyển trong 200ms
        if (!_isPinching && _pointerLastSeenTicks.Count > 1)
        {
            List<int>? staleKeys = null;
            foreach (var kvp in _pointerLastSeenTicks)
            {
                if (kvp.Key != e.Pointer.Id && moveNow - kvp.Value > 200)
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
            PerformPinchZoomStep(points[0], points[1]);
            e.Handled = true;
            return;
        }

        if (_isPinching)
        {
            // Trong khi đang pinch: CHẶN ĐỨNG HOÀN TOÀN không cho scroll theo ngón tay lên xuống!
            e.Handled = true;
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
            EndPinchZoom();
            _isPanHolding = false;
            _isPanDragging = false;
            _isPointerTapCandidate = false;
            _activePointers.Clear();
            _pointerLastSeenTicks.Clear();
            if (DataContext is ComicReaderViewModel vmRel && _pinchTargetPageIndex >= 0 && vmRel.Pages.Count > _pinchTargetPageIndex)
            {
                vmRel.CurrentPageIndex = _pinchTargetPageIndex;
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
        // Bỏ qua cú nhả tay nếu vừa kết thúc pinch 2 ngón tay trong vòng 100ms để chống nảy chạm vô ý khi nhấc ngón tay thứ 2
        if (_lastPinchEndTick > 0 && pinchElapsed >= 0 && pinchElapsed < 100)
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

        // Trên Android: không xử lý bộ phím tắt Honeyview/Bandiview theo yêu cầu
        if (OperatingSystem.IsAndroid())
        {
            if (e.Key == Key.Back || e.Key == Key.Escape)
            {
                if (vm.HandleBackNavigation())
                {
                    e.Handled = true;
                    return;
                }
                if (vm.IsFullScreen) vm.ToggleFullScreen();
                else vm.CloseReader();
                e.Handled = true;
            }
            return;
        }

        // =========================================================================
        // BỘ PHÍM TẮT ĐỌC TRUYỆN CHUẨN HONEYVIEW & BANDIVIEW (WINDOWS & LINUX)
        // =========================================================================

        // 1. Tổ hợp phím Chuyển chương: Ctrl/Shift + Left/Right hoặc [ / ]
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
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

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.D0 || e.Key == Key.NumPad0)
            {
                vm.ResetZoom();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F)
            {
                vm.ToggleChapterDrawer();
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            // Phím [ / ]: Chuyển chương trước / sau (Honeyview / Bandiview)
            case Key.OemOpenBrackets:
                vm.PrevChapter();
                e.Handled = true;
                break;

            case Key.OemCloseBrackets:
                vm.NextChapter();
                e.Handled = true;
                break;

            // Phím Escape: Thoát Popup/Drawer -> Thoát Fullscreen -> Đóng Reader
            case Key.Escape:
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

            // Phím Backspace: Lùi trang / Cuộn lên (Honeyview)
            case Key.Back:
                if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
                    vm.PageButtonAreaPrev();
                else
                    vm.PrevPage();
                e.Handled = true;
                break;

            // Phím F11 / Enter / F: Bật/Tắt Toàn màn hình (Honeyview / Bandiview)
            case Key.F:
            case Key.F11:
            case Key.Enter:
                vm.ToggleFullScreen();
                e.Handled = true;
                break;

            // Phím Tab: Ẩn/Hiện thanh công cụ & thông tin Overlay (Honeyview)
            case Key.Tab:
                vm.ToggleOverlay();
                e.Handled = true;
                break;

            // Phím M: Chuyển đổi chế độ Cuộn (Vertical Webtoon) / Lật trang (Flip)
            case Key.M:
                vm.ToggleMode();
                e.Handled = true;
                break;

            // Phím R: Đổi hướng đọc Phải sang Trái (RTL) / Trái sang Phải (LTR)
            case Key.R:
                if (vm.IsFlipMode)
                {
                    vm.ToggleRtl();
                    e.Handled = true;
                }
                break;

            // Phím P: Bật/Tắt chế độ 2 trang song song (Double Page)
            case Key.P:
                if (vm.IsFlipMode)
                {
                    vm.ToggleDoublePage();
                    e.Handled = true;
                }
                break;

            // Phím Home / End: Về trang đầu tiên / Đến trang cuối cùng (Bandiview / Honeyview)
            case Key.Home:
                vm.ScrubToPage(0);
                e.Handled = true;
                break;

            case Key.End:
                if (vm.Pages.Count > 0)
                {
                    vm.ScrubToPage(vm.Pages.Count - 1);
                }
                e.Handled = true;
                break;

            // Phím Left / A / PageUp / Up: Trang trước hoặc cuộn lên 1 phân đoạn trang
            case Key.Left:
            case Key.A:
            case Key.PageUp:
            case Key.Up:
                if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
                {
                    vm.PageButtonAreaPrev();
                }
                else
                {
                    vm.PrevPage();
                }
                e.Handled = true;
                break;

            // Phím Right / D / PageDown / Down: Trang sau hoặc cuộn xuống 1 phân đoạn trang
            case Key.Right:
            case Key.D:
            case Key.PageDown:
            case Key.Down:
                if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
                {
                    vm.PageButtonAreaNext();
                }
                else
                {
                    vm.NextPage();
                }
                e.Handled = true;
                break;

            // Phím Space: Tiến trang sau (hoặc Shift+Space lùi trang trước)
            case Key.Space:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
                        vm.PageButtonAreaPrev();
                    else
                        vm.PrevPage();
                }
                else
                {
                    if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
                        vm.PageButtonAreaNext();
                    else
                        vm.NextPage();
                }
                e.Handled = true;
                break;

            // Phím Thu Phóng (Zoom): +, -, 0
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
            case Key.Z:
                vm.ResetZoom();
                e.Handled = true;
                break;

            // Phím Tỉ lệ xem (Fit Modes) theo Honeyview / Bandiview (1=Width, 2=Height, 3=Screen, 9=Stretch)
            case Key.D1:
            case Key.NumPad1:
            case Key.W:
                vm.SetFitMode("FitWidth");
                e.Handled = true;
                break;

            case Key.D2:
            case Key.NumPad2:
            case Key.H:
                vm.SetFitMode("FitHeight");
                e.Handled = true;
                break;

            case Key.D3:
            case Key.NumPad3:
            case Key.B:
                vm.SetFitMode("FitScreen");
                e.Handled = true;
                break;

            case Key.D9:
            case Key.NumPad9:
            case Key.S:
                vm.SetFitMode("StretchFull");
                e.Handled = true;
                break;

            // Phím V: Tự động cuộn (Auto Scroll)
            case Key.V:
                vm.ToggleAutoScroll();
                e.Handled = true;
                break;

            // Phím C: Mở danh sách chương (Chapter Drawer)
            case Key.C:
                vm.ToggleChapterDrawer();
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
