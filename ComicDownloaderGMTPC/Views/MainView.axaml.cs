using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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

        // Tự động nhận diện Landscape (width > height) vs Portrait (width < height)
        SizeChanged += (s, e) =>
        {
            if (DataContext is MainViewModel vm && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                vm.IsPortraitMode = e.NewSize.Width < e.NewSize.Height;
            }
        };

        // Bắt phím Escape hoặc phím Back (trên Android) để thoát toàn màn hình đối chiếu
        AddHandler(KeyDownEvent, (s, e) =>
        {
            if (DataContext is MainViewModel vm && vm.IsEnhanceFullscreenVisible)
            {
                if (e.Key == Key.Escape || e.Key == Key.Back)
                {
                    vm.IsEnhanceFullscreenVisible = false;
                    e.Handled = true;
                }
            }
        }, RoutingStrategies.Tunnel);
    }

    private bool _isSyncScrollSetup = false;

    private void OnMainViewLoaded(object? sender, RoutedEventArgs e)
    {
        if (!_isSyncScrollSetup)
        {
            _isSyncScrollSetup = true;

            // Dual View (Song song Before / After): Pan & ComicScreen Zoom locked-step 100%
            SetupComicScreenPanAndZoom(this.FindControl<ScrollViewer>("LiveBeforeScrollViewer"), this.FindControl<ScrollViewer>("LiveAfterScrollViewer"));
            SetupComicScreenPanAndZoom(this.FindControl<ScrollViewer>("ModalBeforeScrollViewer"), this.FindControl<ScrollViewer>("ModalAfterScrollViewer"));
        }

        // Khi bất kỳ ô nhập liệu nào (TextBox, NumericUpDown, ComboBox) nhận focus trên Android / Desktop:
        // Tự động cuộn khung nhìn để không bao giờ bị bàn phím ảo che khuất ô cần nhập
        AddHandler(InputElement.GotFocusEvent, (s, ev) =>
        {
            if (ev.Source is Control ctrl && (ctrl is TextBox || ctrl is NumericUpDown || ctrl is ComboBox || ctrl.Parent is NumericUpDown))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        ctrl.BringIntoView();
                    }
                    catch { }
                }, Avalonia.Threading.DispatcherPriority.Background);
            }
        }, RoutingStrategies.Bubble);
    }

    /// <summary>
    /// Bộ điều khiển Cử chỉ Pan & Zoom phong cách ComicScreen (InstSoft) cho Android & Windows:
    /// 1. Double-Tap & Drag 1 ngón: Nhấn đúp 1 ngón tay và GIỮ để zoom mượt mà liên tục (Kéo lên phóng to, Kéo xuống thu nhỏ, neo tâm zoom chuẩn xác không giật nhảy).
    /// 2. Quick Double-Tap: Nhấn đúp nhanh thả ra ngay để toggle tức thì giữa 2.0x và Zoom Fit tại điểm chạm.
    /// 3. Vuốt 1 ngón / Chuột trái: Pan rê ảnh mượt mà 1:1 theo ngón tay khi đang phóng to.
    /// 4. 2 ngón tay (Pinch-to-zoom): Phóng to / thu nhỏ & Pan đồng thời bằng 2 ngón.
    /// 5. Cuộn bánh xe chuột (Mouse Wheel): Zoom in / Zoom out mượt mà.
    /// </summary>
    private void SetupComicScreenPanAndZoom(params ScrollViewer?[] viewers)
    {
        var validViewers = viewers.Where(v => v != null).Cast<ScrollViewer>().ToList();
        if (validViewers.Count == 0) return;

        bool isSyncing = false;
        bool isPanning = false;
        bool isPinching = false;
        bool isDoubleTapHolding = false;
        bool hasDoubleTapMoved = false;

        // Double-Tap detection
        DateTime lastTapTime = DateTime.MinValue;
        Point lastTapPos = default;

        // Double-Tap Drag Zoom state
        Point doubleTapStartPos = default;
        Vector doubleTapStartOffset = default;
        double doubleTapStartZoom = 1.0;

        // Pan state
        Point panStartPoint = default;
        Vector panStartOffset = default;

        // Pinch state
        Point initialPinchMidPoint = default;
        Vector initialPinchOffset = default;
        double initialPinchDistance = 0;
        double initialPinchZoom = 1.0;

        var activePointers = new Dictionary<long, Point>();

        // Đồng bộ cuộn giữa các ScrollViewer trong nhóm (Dual View: Before & After)
        if (validViewers.Count > 1)
        {
            foreach (var v in validViewers)
            {
                v.ScrollChanged += (s, ev) =>
                {
                    if (isSyncing) return;
                    isSyncing = true;
                    var offset = v.Offset;
                    foreach (var other in validViewers)
                    {
                        if (other != v) other.Offset = offset;
                    }
                    isSyncing = false;
                };
            }
        }

        static bool IsScrollBarElement(object? source, Visual? container)
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

        static double GetDistance(Point p1, Point p2)
        {
            double dx = p1.X - p2.X;
            double dy = p1.Y - p2.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        void OnPressed(object? sender, PointerPressedEventArgs ev)
        {
            var currentViewer = sender as ScrollViewer ?? validViewers[0];
            if (IsScrollBarElement(ev.Source, currentViewer)) return;

            var p = ev.GetCurrentPoint(currentViewer);
            if (!p.Properties.IsLeftButtonPressed && !p.Properties.IsMiddleButtonPressed && !p.Properties.IsRightButtonPressed && ev.Pointer.Type != PointerType.Touch)
                return;

            if (ev.Pointer.Type == PointerType.Mouse) activePointers.Clear();
            activePointers[ev.Pointer.Id] = p.Position;

            var now = DateTime.UtcNow;
            double timeSinceLastTap = (now - lastTapTime).TotalMilliseconds;
            double distFromLastTap = GetDistance(p.Position, lastTapPos);

            // Kiểm tra cử chỉ Double-Tap của 1 ngón hoặc Chuột trái
            if (activePointers.Count == 1 && (p.Properties.IsLeftButtonPressed || ev.Pointer.Type == PointerType.Touch) && timeSinceLastTap <= 350 && distFromLastTap <= 40)
            {
                if (DataContext is MainViewModel vm)
                {
                    isDoubleTapHolding = true;
                    hasDoubleTapMoved = false;
                    isPanning = false;
                    isPinching = false;
                    doubleTapStartPos = p.Position;
                    doubleTapStartOffset = currentViewer.Offset;
                    doubleTapStartZoom = vm.EnhancePreviewZoom;

                    lastTapTime = DateTime.MinValue; // Tiêu thụ lượt double-tap
                    if (ev.Pointer.Type == PointerType.Mouse) ev.Pointer.Capture(currentViewer);
                    ev.Handled = true;
                    return;
                }
            }

            // Ghi nhận tap lần này để phát hiện double-tap ở lần kế tiếp
            lastTapTime = now;
            lastTapPos = p.Position;

            if (activePointers.Count == 1)
            {
                isPanning = true;
                isPinching = false;
                isDoubleTapHolding = false;
                panStartPoint = p.Position;
                panStartOffset = currentViewer.Offset;
                if (ev.Pointer.Type == PointerType.Mouse) ev.Pointer.Capture(currentViewer);
                ev.Handled = true;
            }
            else if (activePointers.Count >= 2 && DataContext is MainViewModel vm)
            {
                // Pinch-to-zoom 2 ngón tay
                isPanning = true;
                isPinching = true;
                isDoubleTapHolding = false;
                var pts = activePointers.Values.Take(2).ToArray();
                initialPinchDistance = Math.Max(10.0, GetDistance(pts[0], pts[1]));
                initialPinchMidPoint = new Point((pts[0].X + pts[1].X) / 2.0, (pts[0].Y + pts[1].Y) / 2.0);
                initialPinchOffset = currentViewer.Offset;
                initialPinchZoom = vm.EnhancePreviewZoom;
                try { ev.Pointer.Capture(null); } catch { }
                ev.Handled = true;
            }
        }

        void OnMoved(object? sender, PointerEventArgs ev)
        {
            var currentViewer = sender as ScrollViewer ?? validViewers[0];
            if (IsScrollBarElement(ev.Source, currentViewer)) return;
            if (!activePointers.ContainsKey(ev.Pointer.Id)) return;

            var curPos = ev.GetCurrentPoint(currentViewer).Position;
            activePointers[ev.Pointer.Id] = curPos;

            if (DataContext is not MainViewModel vm) return;

            // 1. CHẾ ĐỘ COMICSCREEN DOUBLE-TAP & DRAG ZOOM (1 NGÓN TAY GIỮ VÀ RÊ)
            if (isDoubleTapHolding && activePointers.Count == 1)
            {
                double deltaY = doubleTapStartPos.Y - curPos.Y; // Kéo lên là phóng to, kéo xuống là thu nhỏ
                double totalMove = GetDistance(curPos, doubleTapStartPos);
                if (totalMove > 6) hasDoubleTapMoved = true;

                // Sử dụng hàm mũ mượt mà liên tục (Exponential Scale), không bị giật nhảy số
                double scaleFactor = Math.Pow(1.006, deltaY);
                double targetZoom = Math.Clamp(Math.Round(doubleTapStartZoom * scaleFactor, 2), 0.25, 5.0);

                if (Math.Abs(targetZoom - vm.EnhancePreviewZoom) >= 0.005)
                {
                    vm.EnhancePreviewZoom = targetZoom;

                    // Căn chỉnh Offset để tâm điểm double-tap giữ nguyên vị trí trực quan trên màn hình
                    double contentPointX = (doubleTapStartOffset.X + doubleTapStartPos.X) / Math.Max(0.01, doubleTapStartZoom);
                    double contentPointY = (doubleTapStartOffset.Y + doubleTapStartPos.Y) / Math.Max(0.01, doubleTapStartZoom);

                    double contentW = vm.EnhanceImagePixelWidth * targetZoom;
                    double contentH = vm.EnhanceImagePixelHeight * targetZoom;
                    double maxOffsetX = Math.Max(0, contentW - currentViewer.Viewport.Width);
                    double maxOffsetY = Math.Max(0, contentH - currentViewer.Viewport.Height);

                    double newOffsetX = Math.Clamp(contentPointX * targetZoom - doubleTapStartPos.X, 0, maxOffsetX);
                    double newOffsetY = Math.Clamp(contentPointY * targetZoom - doubleTapStartPos.Y, 0, maxOffsetY);

                    var newOffset = new Vector(newOffsetX, newOffsetY);
                    isSyncing = true;
                    foreach (var v in validViewers) v.Offset = newOffset;
                    isSyncing = false;
                }
                ev.Handled = true;
                return;
            }

            // 2. CHẾ ĐỘ PINCH-TO-ZOOM 2 NGÓN & PAN
            if (activePointers.Count >= 2)
            {
                var pts = activePointers.Values.Take(2).ToArray();
                double curDistance = GetDistance(pts[0], pts[1]);
                var curMidPoint = new Point((pts[0].X + pts[1].X) / 2.0, (pts[0].Y + pts[1].Y) / 2.0);

                if (!isPinching)
                {
                    isPanning = true;
                    isPinching = true;
                    initialPinchDistance = Math.Max(10.0, curDistance);
                    initialPinchMidPoint = curMidPoint;
                    initialPinchOffset = currentViewer.Offset;
                    initialPinchZoom = vm.EnhancePreviewZoom;
                }

                if (initialPinchDistance > 5)
                {
                    double scaleFactor = curDistance / initialPinchDistance;
                    double targetZoom = Math.Clamp(Math.Round(initialPinchZoom * scaleFactor, 2), 0.25, 5.0);
                    if (Math.Abs(targetZoom - vm.EnhancePreviewZoom) >= 0.01)
                    {
                        vm.EnhancePreviewZoom = targetZoom;
                    }
                }

                var midDelta = initialPinchMidPoint - curMidPoint;
                double contentW = vm.EnhanceImagePixelWidth * vm.EnhancePreviewZoom;
                double contentH = vm.EnhanceImagePixelHeight * vm.EnhancePreviewZoom;
                double maxOffsetX = Math.Max(0, contentW - currentViewer.Viewport.Width);
                double maxOffsetY = Math.Max(0, contentH - currentViewer.Viewport.Height);
                double newX = Math.Clamp(initialPinchOffset.X + midDelta.X, 0, maxOffsetX);
                double newY = Math.Clamp(initialPinchOffset.Y + midDelta.Y, 0, maxOffsetY);
                var newOffset = new Vector(newX, newY);

                isSyncing = true;
                foreach (var v in validViewers) v.Offset = newOffset;
                isSyncing = false;

                ev.Handled = true;
                return;
            }

            // 3. CHẾ ĐỘ PAN 1 NGÓN / CHUỘT
            if (isPanning && !isPinching)
            {
                var delta = panStartPoint - curPos;
                double contentW = vm.EnhanceImagePixelWidth * vm.EnhancePreviewZoom;
                double contentH = vm.EnhanceImagePixelHeight * vm.EnhancePreviewZoom;
                double maxOffsetX = Math.Max(0, contentW - currentViewer.Viewport.Width);
                double maxOffsetY = Math.Max(0, contentH - currentViewer.Viewport.Height);
                double newX = Math.Clamp(panStartOffset.X + delta.X, 0, maxOffsetX);
                double newY = Math.Clamp(panStartOffset.Y + delta.Y, 0, maxOffsetY);
                var newOffset = new Vector(newX, newY);

                isSyncing = true;
                foreach (var v in validViewers) v.Offset = newOffset;
                isSyncing = false;

                ev.Handled = true;
            }
        }

        void OnReleased(object? sender, PointerReleasedEventArgs ev)
        {
            activePointers.Remove(ev.Pointer.Id);
            var currentViewer = sender as ScrollViewer ?? validViewers[0];

            if (isDoubleTapHolding)
            {
                // Nếu là Quick Double-Tap (không kéo di chuyển) -> Toggle nhanh giữa 2.0x và Zoom Fit
                if (!hasDoubleTapMoved && DataContext is MainViewModel vm)
                {
                    if (vm.EnhancePreviewZoom <= 1.05)
                    {
                        double targetZoom = 2.0;
                        vm.EnhancePreviewZoom = targetZoom;

                        double contentPointX = (doubleTapStartOffset.X + doubleTapStartPos.X) / Math.Max(0.01, doubleTapStartZoom);
                        double contentPointY = (doubleTapStartOffset.Y + doubleTapStartPos.Y) / Math.Max(0.01, doubleTapStartZoom);

                        double contentW = vm.EnhanceImagePixelWidth * targetZoom;
                        double contentH = vm.EnhanceImagePixelHeight * targetZoom;
                        double maxOffsetX = Math.Max(0, contentW - currentViewer.Viewport.Width);
                        double maxOffsetY = Math.Max(0, contentH - currentViewer.Viewport.Height);

                        double newOffsetX = Math.Clamp(contentPointX * targetZoom - doubleTapStartPos.X, 0, maxOffsetX);
                        double newOffsetY = Math.Clamp(contentPointY * targetZoom - doubleTapStartPos.Y, 0, maxOffsetY);

                        var newOffset = new Vector(newOffsetX, newOffsetY);
                        isSyncing = true;
                        foreach (var v in validViewers) v.Offset = newOffset;
                        isSyncing = false;
                    }
                    else
                    {
                        vm.ZoomFitPreview();
                    }
                }

                isDoubleTapHolding = false;
                hasDoubleTapMoved = false;
                try { ev.Pointer.Capture(null); } catch { }
                ev.Handled = true;
                return;
            }

            if (activePointers.Count == 1)
            {
                isPinching = false;
                isPanning = true;
                panStartPoint = activePointers.Values.First();
                panStartOffset = currentViewer.Offset;
            }
            else if (activePointers.Count == 0)
            {
                isPanning = false;
                isPinching = false;
                try { ev.Pointer.Capture(null); } catch { }
            }
        }

        void OnCaptureLost(object? sender, PointerCaptureLostEventArgs ev)
        {
            activePointers.Remove(ev.Pointer.Id);
            if (activePointers.Count == 0)
            {
                isPanning = false;
                isPinching = false;
                isDoubleTapHolding = false;
                hasDoubleTapMoved = false;
            }
        }

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

        foreach (var v in validViewers)
        {
            v.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
            v.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
            v.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
            v.AddHandler(PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Tunnel);
            v.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        }

        SetupAutoScrollTimer();
    }

    private Avalonia.Threading.DispatcherTimer? _autoScrollTimer;

    private void SetupAutoScrollTimer()
    {
        _autoScrollTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _autoScrollTimer.Tick += (s, e) =>
        {
            try
            {
                if (DataContext is MainViewModel vm && vm.IsAutoScrollToActive)
                {
                    ScrollToLowestDownloadingItem(vm);
                }
            }
            catch { }
        };
        _autoScrollTimer.Start();
    }

    private void ScrollToLowestDownloadingItem(MainViewModel vm)
    {
        var queueScrollViewer = this.FindControl<ScrollViewer>("QueueScrollViewer");
        if (queueScrollViewer == null || vm.ComicBooks == null || vm.ComicBooks.Count == 0) return;

        int lowestIndex = -1;
        for (int i = vm.ComicBooks.Count - 1; i >= 0; i--)
        {
            var b = vm.ComicBooks[i];
            if (b != null && (string.Equals(b.Status, "Downloading", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(b.Status, "Đang tải", StringComparison.OrdinalIgnoreCase)))
            {
                lowestIndex = i;
                break;
            }
        }

        if (lowestIndex >= 0)
        {
            double totalHeight = queueScrollViewer.Extent.Height;
            if (totalHeight <= 0) return;

            double ratio = (double)lowestIndex / Math.Max(1, vm.ComicBooks.Count);
            double targetY = Math.Max(0, (totalHeight * ratio) - 40);

            queueScrollViewer.Offset = new Vector(queueScrollViewer.Offset.X, targetY);
        }
    }

    private void OnQueueItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control ctrl && ctrl.DataContext is ComicDownloaderGMTPC.Models.ComicBookItem item && item.IsParallelSplitParent)
        {
            var src = e.Source as Control;
            while (src != null && src != ctrl)
            {
                if (src is Button || src is CheckBox || src is TextBox || src is NumericUpDown)
                {
                    return;
                }
                src = src.Parent as Control;
            }

            if (DataContext is MainViewModel vm)
            {
                vm.ToggleSplitCollapse(item);
            }
        }
    }
}