using System;

using System.Collections.Generic;

using System.IO;

using System.Linq;

using System.Threading;

using System.Threading.Tasks;

using Avalonia;

using Avalonia.Controls;

using Avalonia.Controls.Primitives;

using Avalonia.Input;

using Avalonia.Interactivity;

using Avalonia.VisualTree;



using Avalonia.LogicalTree;



using ComicDownloaderGMTPC.Services;

using Avalonia.Media;

using ComicDownloaderGMTPC.Models;

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

        LanguageService.Instance.LanguageChanged += OnLanguageServiceChanged;



        // Tự động nhận diện Landscape (width > height) vs Portrait (width < height)

        SizeChanged += (s, e) =>

        {

            if (DataContext is MainViewModel vm && e.NewSize.Width > 0 && e.NewSize.Height > 0)

            {

                vm.IsPortraitMode = e.NewSize.Width < e.NewSize.Height;

            }

        };



        // Bắt phím Escape hoặc phím Back (trên Android) để thoát toàn màn hình đối chiếu hoặc đóng Popup Preview

        AddHandler(KeyDownEvent, (s, e) =>

        {

            if (DataContext is MainViewModel vm)

            {

                if (vm.IsHoverPreviewVisible && (e.Key == Key.Escape || e.Key == Key.Back))

                {

                    vm.HideHoverPreview();

                    e.Handled = true;

                    return;

                }

                if (vm.IsEnhanceFullscreenVisible && (e.Key == Key.Escape || e.Key == Key.Back))

                {

                    vm.IsEnhanceFullscreenVisible = false;

                    e.Handled = true;

                    return;

                }



                // Phím tắt cho Dual Picture Sort

                try

                {

                    if (e.Key == Key.Z && (e.KeyModifiers & KeyModifiers.Control) != 0 && vm.CanUndoSort)

                    {

                        vm.UndoLastSortCommand.Execute(null);

                        e.Handled = true;

                        return;

                    }

                    if (e.Key == Key.Enter || e.Key == Key.Right)

                    {

                        if (vm.DualSortSourceItems.Any(x => x.IsSelected))

                        {

                            vm.SortSelectedForwardCommand.Execute(null);

                            e.Handled = true;

                            return;

                        }

                    }

                    if (e.Key == Key.Left)

                    {

                        if (vm.DualSortSortedItems.Any(x => x.IsSelected))

                        {

                            vm.SortSelectedBackwardCommand.Execute(null);

                            e.Handled = true;

                            return;

                        }

                    }

                }

                catch { }

            }

        }, RoutingStrategies.Tunnel);



        DataContextChanged += (s, e) =>

        {

            if (_subscribedVm != null)

            {

                UnregisterVmEvents(_subscribedVm);

                _subscribedVm = null;

            }

            if (DataContext is MainViewModel newVm)

            {

                _subscribedVm = newVm;

                RegisterVmEvents(_subscribedVm);

            }

        };

    }



    private MainViewModel? _subscribedVm;

    private bool _isSyncScrollSetup = false;



    private void RegisterVmEvents(MainViewModel vm)

    {

        vm.AutoScrollRequested -= OnVmAutoScrollRequested;

        vm.AutoScrollRequested += OnVmAutoScrollRequested;



        vm.ScrollToTopRequested -= OnVmScrollToTopRequested;

        vm.ScrollToTopRequested += OnVmScrollToTopRequested;



        vm.ScrollToBottomRequested -= OnVmScrollToBottomRequested;

        vm.ScrollToBottomRequested += OnVmScrollToBottomRequested;



        vm.VisibleThumbnailScanRequested -= OnVmVisibleThumbnailScanRequested;

        vm.VisibleThumbnailScanRequested += OnVmVisibleThumbnailScanRequested;

    }



    private void UnregisterVmEvents(MainViewModel vm)

    {

        vm.AutoScrollRequested -= OnVmAutoScrollRequested;

        vm.ScrollToTopRequested -= OnVmScrollToTopRequested;

        vm.ScrollToBottomRequested -= OnVmScrollToBottomRequested;

        vm.VisibleThumbnailScanRequested -= OnVmVisibleThumbnailScanRequested;

    }



    private void OnVmVisibleThumbnailScanRequested()

    {

        ScheduleVisibleThumbnailScan(delayMs: 60);

    }



    private void OnVmScrollToTopRequested()

    {

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>

        {

            var scrollViewer = this.FindControl<ScrollViewer>("MainRootScrollViewer");

            if (scrollViewer != null)

            {

                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, 0);

            }

        }, Avalonia.Threading.DispatcherPriority.Background);

    }



    private void OnVmScrollToBottomRequested()

    {

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>

        {

            var scrollViewer = this.FindControl<ScrollViewer>("MainRootScrollViewer");

            if (scrollViewer != null)

            {

                double maxScroll = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);

                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, maxScroll);

            }

        }, Avalonia.Threading.DispatcherPriority.Background);

    }



    private void OnMainViewLoaded(object? sender, RoutedEventArgs e)

    {

        if (DataContext is MainViewModel vm)

        {

            RegisterVmEvents(vm);
            ScrollBarAutoExpandService.RegisterAllScrollViewers(this);

        }



        if (!_isSyncScrollSetup)

        {

            _isSyncScrollSetup = true;

            // Tự động lắng nghe toàn bộ sự kiện cuộn toàn cục (Lăn chuột Windows/Linux, cảm ứng Android, ScrollViewer)
            ScrollBarAutoExpandService.AttachGlobalScrollListeners(this);
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
            {
                topLevel.AddHandler(InputElement.PointerWheelChangedEvent, (s, ev) =>
                {
                    ScrollBarAutoExpandService.TriggerGlobalScrollExpand(this);
                }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            }



            // Dual View (Song song Before / After): Pan & ComicScreen Zoom locked-step 100%

            SetupComicScreenPanAndZoom(this.FindControl<ScrollViewer>("LiveBeforeScrollViewer"), this.FindControl<ScrollViewer>("LiveAfterScrollViewer"));

            SetupComicScreenPanAndZoom(this.FindControl<ScrollViewer>("ModalBeforeScrollViewer"), this.FindControl<ScrollViewer>("ModalAfterScrollViewer"));



            // Tự động nhận diện thao tác cuộn (Scroll) trên mọi danh sách (QueueScrollViewer, ThumbnailScrollViewer, MainRootScrollViewer)

            // để nạp ngay thumbnail cho các truyện đang hiển thị trên màn hình mà chưa có ảnh bìa

            AddHandler(ScrollViewer.ScrollChangedEvent, OnAnyScrollViewerScrollChanged, RoutingStrategies.Bubble);

        }



        ScheduleVisibleThumbnailScan(delayMs: 120);



        ApplyLanguage(LanguageService.Instance.CurrentLanguage);

        AddHandler(TabControl.SelectionChangedEvent, (s, ev) =>

        {

            Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyLanguage(LanguageService.Instance.CurrentLanguage), Avalonia.Threading.DispatcherPriority.Background);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ScrollBarAutoExpandService.RegisterAllScrollViewers(this), Avalonia.Threading.DispatcherPriority.Background);

        }, RoutingStrategies.Bubble);



        SetupFloatingBarDraggable();

        SetupDualSortBatchFloatingDraggable();



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



    private bool _isFloatingBarDragging = false;

    private Point _floatingDragStartPointer;

    private double _floatingStartTransformX = 0;

    private double _floatingStartTransformY = 0;



    /// <summary>

    /// Kéo thả di chuyển tự do cụm phím nổi (Floating Quick Control Bar) quanh màn hình

    /// để không bao giờ bị che khuất nút Đọc truyện hay các nút bấm khác bên dưới.

    /// </summary>

    private bool _isFloatingBarCollapsed = false;



    private void SetupFloatingBarDraggable()

    {

        var gripBar = this.FindControl<Border>("FloatingGripBar");

        var border = this.FindControl<Border>("FloatingQuickControlBorder");

        var actionPanel = this.FindControl<StackPanel>("FloatingActionPanel");

        var scrollPanel = this.FindControl<StackPanel>("FloatingScrollPanel");

        if (border == null) return;



        var transform = border.RenderTransform as TranslateTransform;

        if (transform == null)

        {

            transform = new TranslateTransform();

            border.RenderTransform = transform;

        }



        bool hasMoved = false;



        void OnPointerPressed(object? sender, PointerPressedEventArgs e)

        {

            if (e.Source is Button || (e.Source is Visual v && v.FindAncestorOfType<Button>() != null))

                return;



            var prop = e.GetCurrentPoint(this).Properties;

            if (prop.IsLeftButtonPressed)

            {

                _isFloatingBarDragging = true;

                hasMoved = false;

                _floatingDragStartPointer = e.GetPosition(this);

                _floatingStartTransformX = transform.X;

                _floatingStartTransformY = transform.Y;

                e.Pointer.Capture(sender as IInputElement ?? border);

                e.Handled = true;

            }

        }



        void OnPointerMoved(object? sender, PointerEventArgs e)

        {

            if (!_isFloatingBarDragging) return;



            var currentPoint = e.GetPosition(this);

            double deltaX = currentPoint.X - _floatingDragStartPointer.X;

            double deltaY = currentPoint.Y - _floatingDragStartPointer.Y;



            if (Math.Abs(deltaX) > 6 || Math.Abs(deltaY) > 6)

            {

                hasMoved = true;

            }



            double minX = -Math.Max(0, Bounds.Width - 70);

            double maxX = 20;

            double minY = -Math.Max(0, Bounds.Height - 70);

            double maxY = 20;



            transform.X = Math.Clamp(_floatingStartTransformX + deltaX, minX, maxX);

            transform.Y = Math.Clamp(_floatingStartTransformY + deltaY, minY, maxY);

            e.Handled = true;

        }



        void OnPointerReleased(object? sender, PointerReleasedEventArgs e)

        {

            if (_isFloatingBarDragging)

            {

                _isFloatingBarDragging = false;

                e.Pointer.Capture(null);

                e.Handled = true;



                // Single Tap detection (không di chuyển hoặc di chuyển < 6px): Toggle thu nhỏ / mở rộng

                if (!hasMoved)

                {

                    _isFloatingBarCollapsed = !_isFloatingBarCollapsed;

                    if (actionPanel != null) actionPanel.IsVisible = !_isFloatingBarCollapsed;

                    if (scrollPanel != null) scrollPanel.IsVisible = true; // Luôn hiển thị nút Top / Bot theo yêu cầu người dùng

                    border.Padding = _isFloatingBarCollapsed ? new Thickness(3, 4) : new Thickness(4, 5);

                    border.CornerRadius = new CornerRadius(16);

                }

            }

        }



        void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)

        {

            _isFloatingBarDragging = false;

        }



        if (gripBar != null)

        {

            gripBar.PointerPressed += OnPointerPressed;

            gripBar.PointerMoved += OnPointerMoved;

            gripBar.PointerReleased += OnPointerReleased;

            gripBar.PointerCaptureLost += OnPointerCaptureLost;

        }



        border.PointerPressed += OnPointerPressed;

        border.PointerMoved += OnPointerMoved;

        border.PointerReleased += OnPointerReleased;

        border.PointerCaptureLost += OnPointerCaptureLost;

    }



    private bool _isDualSortBatchDragging = false;

    private Point _dualSortBatchDragStartPointer;

    private double _dualSortBatchStartTransformX = 0;

    private double _dualSortBatchStartTransformY = 0;

    private bool _isDualSortBatchCollapsed = false;



    /// <summary>

    /// Kéo thả di chuyển tự do Floating Batch Toolbar trong tab Dual Picture Sort

    /// </summary>

    private void SetupDualSortBatchFloatingDraggable()

    {

        var gripBar = this.FindControl<Border>("DualSortBatchGripBar");

        var border = this.FindControl<Border>("DualSortFloatingBatchToolbar");

        var essentialPanel = this.FindControl<StackPanel>("DualSortBatchEssentialPanel");

        var advancedPanel = this.FindControl<StackPanel>("DualSortBatchAdvancedPanel");

        var headerGrid = this.FindControl<Grid>("DualSortBatchHeaderGrid");

        var closeBtn = this.FindControl<Button>("DualSortBatchCloseBtn");

        var gripText = this.FindControl<TextBlock>("DualSortBatchGripText");

        if (border == null) return;



        var transform = border.RenderTransform as TranslateTransform;

        if (transform == null)

        {

            transform = new TranslateTransform();

            border.RenderTransform = transform;

        }



        bool hasMoved = false;



        void OnPointerPressed(object? sender, PointerPressedEventArgs e)

        {

            if (e.Source is Button || (e.Source is Visual v && v.FindAncestorOfType<Button>() != null) || e.Source is CheckBox || (e.Source is Visual v2 && v2.FindAncestorOfType<CheckBox>() != null))

                return;



            var prop = e.GetCurrentPoint(this).Properties;

            if (prop.IsLeftButtonPressed)

            {

                _isDualSortBatchDragging = true;

                hasMoved = false;

                _dualSortBatchDragStartPointer = e.GetPosition(this);

                _dualSortBatchStartTransformX = transform.X;

                _dualSortBatchStartTransformY = transform.Y;

                e.Pointer.Capture(sender as IInputElement ?? border);

                e.Handled = true;

            }

        }



        void OnPointerMoved(object? sender, PointerEventArgs e)

        {

            if (!_isDualSortBatchDragging) return;



            var currentPoint = e.GetPosition(this);

            double deltaX = currentPoint.X - _dualSortBatchDragStartPointer.X;

            double deltaY = currentPoint.Y - _dualSortBatchDragStartPointer.Y;



            if (Math.Abs(deltaX) > 6 || Math.Abs(deltaY) > 6)

            {

                hasMoved = true;

            }



            double minX = -Math.Max(0, Bounds.Width - 95);

            double maxX = 20;

            double minY = -Math.Max(0, Bounds.Height - 120);

            double maxY = 20;



            transform.X = Math.Clamp(_dualSortBatchStartTransformX + deltaX, minX, maxX);

            transform.Y = Math.Clamp(_dualSortBatchStartTransformY + deltaY, minY, maxY);

            e.Handled = true;

        }



        void OnPointerReleased(object? sender, PointerReleasedEventArgs e)

        {

            if (_isDualSortBatchDragging)

            {

                _isDualSortBatchDragging = false;

                e.Pointer.Capture(null);

                e.Handled = true;



                // Single Tap detection (không di chuyển hoặc di chuyển < 6px): Toggle thu nhỏ / mở rộng

                if (!hasMoved)

                {

                    _isDualSortBatchCollapsed = !_isDualSortBatchCollapsed;

                    if (advancedPanel != null) advancedPanel.IsVisible = !_isDualSortBatchCollapsed;

                    if (essentialPanel != null) essentialPanel.IsVisible = true; // Luôn hiển thị Prev/Next Folder, Start/Stop Part, Preview theo yêu cầu

                    if (closeBtn != null) closeBtn.IsVisible = true;

                    if (gripText != null) gripText.Text = _isDualSortBatchCollapsed ? "⋮⋮ BATCH ⋮⋮" : "⋮⋮ BATCH TOOL ⋮⋮";

                    border.Padding = _isDualSortBatchCollapsed ? new Thickness(3, 4) : new Thickness(4, 5);

                    border.CornerRadius = new CornerRadius(16);

                }

            }

        }



        void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)

        {

            _isDualSortBatchDragging = false;

        }



        if (gripBar != null)

        {

            gripBar.PointerPressed += OnPointerPressed;

            gripBar.PointerMoved += OnPointerMoved;

            gripBar.PointerReleased += OnPointerReleased;

            gripBar.PointerCaptureLost += OnPointerCaptureLost;

        }



        border.PointerPressed += OnPointerPressed;

        border.PointerMoved += OnPointerMoved;

        border.PointerReleased += OnPointerReleased;

        border.PointerCaptureLost += OnPointerCaptureLost;

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

                    PerformAutoScrollToLowestDownloadingItem();

                }

            }

            catch { }

        };

        _autoScrollTimer.Start();

    }



    private void OnVmAutoScrollRequested()

    {

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>

        {

            PerformAutoScrollToLowestDownloadingItem();

        }, Avalonia.Threading.DispatcherPriority.Background);

    }



    private void OnAutoScrollCheckBoxClicked(object? sender, RoutedEventArgs e)

    {

        if (DataContext is MainViewModel vm && vm.IsAutoScrollToActive)

        {

            // Cuộn tức thì khi người dùng vừa bấm bật CheckBox (chuẩn 100% giống WPF)

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>

            {

                PerformAutoScrollToLowestDownloadingItem();

            }, Avalonia.Threading.DispatcherPriority.Background);



            _autoScrollTimer?.Stop();

            _autoScrollTimer?.Start();

        }

    }



    private void OnBubbleCheckBoxClicked(object? sender, RoutedEventArgs e)

    {

        if (sender is CheckBox cb)

        {

            bool isChecked = cb.IsChecked == true;

            if (DataContext is MainViewModel vm)

            {

                vm.IsBubbleMode = isChecked;

            }

            ComicDownloaderGMTPC.Services.BackgroundExecutionService.Instance.SetBubbleMode(isChecked);

        }

    }



    private void PerformAutoScrollToLowestDownloadingItem()

    {

        try

        {

            if (DataContext is not MainViewModel vm || vm.ComicBooks == null || vm.ComicBooks.Count == 0) return;



            var queueItemsControl = this.FindControl<ItemsControl>("QueueItemsControl");

            var queueScrollViewer = this.FindControl<ScrollViewer>("QueueScrollViewer")

                                 ?? queueItemsControl?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();



            ComicBookItem? lowestItem = null;

            int lowestIndex = -1;



            // Duyệt từ cuối danh sách lên đầu để luôn bắt chính xác truyện ở hàng thấp nhất đang tải (chuẩn WPF)

            for (int i = vm.ComicBooks.Count - 1; i >= 0; i--)

            {

                var b = vm.ComicBooks[i];

                if (b != null && IsComicBookDownloading(b))

                {

                    lowestItem = b;

                    lowestIndex = i;

                    break;

                }

            }



            if (lowestItem == null || lowestIndex < 0) return;



            // Tìm Container Control tương ứng trong Visual Tree

            Control? targetControl = null;

            if (queueItemsControl != null)

            {

                targetControl = queueItemsControl.ContainerFromIndex(lowestIndex) as Control

                             ?? queueItemsControl.ContainerFromItem(lowestItem) as Control;

            }



            if (targetControl != null && queueScrollViewer != null)

            {

                var transform = targetControl.TransformToVisual(queueScrollViewer);

                if (transform.HasValue)

                {

                    var pt = transform.Value.Transform(new Point(0, 0));

                    double itemHeight = Math.Max(28, targetControl.Bounds.Height);

                    double viewportHeight = queueScrollViewer.Viewport.Height;



                    // Nếu item nằm ở phía dưới ngoài tầm nhìn (bị che khuất hoặc vượt đáy)

                    if (pt.Y + itemHeight > viewportHeight)

                    {

                        double diff = (pt.Y + itemHeight) - viewportHeight + 14;

                        double targetY = queueScrollViewer.Offset.Y + diff;

                        double maxScroll = Math.Max(0, queueScrollViewer.Extent.Height - viewportHeight);

                        queueScrollViewer.Offset = new Vector(queueScrollViewer.Offset.X, Math.Clamp(targetY, 0, maxScroll));

                    }

                    // Nếu item nằm ở phía trên ngoài tầm nhìn

                    else if (pt.Y < 0)

                    {

                        double targetY = Math.Max(0, queueScrollViewer.Offset.Y + pt.Y - 14);

                        queueScrollViewer.Offset = new Vector(queueScrollViewer.Offset.X, targetY);

                    }

                }

                else

                {

                    targetControl.BringIntoView();

                }

            }

            else if (queueItemsControl != null)

            {

                queueItemsControl.ScrollIntoView(lowestIndex);

            }

        }

        catch { }

    }



    private static bool IsComicBookDownloading(ComicBookItem item)

    {

        if (item == null) return false;



        string status = item.Status ?? string.Empty;

        if (status.Contains("Downloading", StringComparison.OrdinalIgnoreCase) ||

            status.Contains("Đang tải", StringComparison.OrdinalIgnoreCase))

        {

            return true;

        }



        // Nếu là truyện cha đã tách chương và đang thu gọn: kiểm tra xem có task con nào đang tải không

        if (item.IsParallelSplitParent && item.IsParallelSplitCollapsed && item.ParallelSplitChildren != null)

        {

            if (item.ParallelSplitChildren.Any(c => IsComicBookDownloading(c)))

            {

                return true;

            }

        }



        // Kiểm tra StatusMessage và DetailProgressText khi đang trong tiến trình tải

        string msg = item.StatusMessage ?? string.Empty;

        string detail = item.DetailProgressText ?? string.Empty;

        bool hasActiveProgress = msg.Contains("Đang tải", StringComparison.OrdinalIgnoreCase) ||

                                 msg.Contains("Downloading", StringComparison.OrdinalIgnoreCase) ||

                                 msg.Contains("Đang trích xuất", StringComparison.OrdinalIgnoreCase) ||

                                 detail.Contains("Đang tải", StringComparison.OrdinalIgnoreCase);



        bool isTerminated = status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||

                            status.Equals("Hoàn tất", StringComparison.OrdinalIgnoreCase) ||

                            status.Equals("Stopped", StringComparison.OrdinalIgnoreCase) ||

                            status.Equals("Đã dừng", StringComparison.OrdinalIgnoreCase) ||

                            status.Equals("Paused", StringComparison.OrdinalIgnoreCase) ||

                            status.Equals("Tạm dừng", StringComparison.OrdinalIgnoreCase) ||

                            status.Equals("Error", StringComparison.OrdinalIgnoreCase) ||

                            status.Equals("Lỗi", StringComparison.OrdinalIgnoreCase) ||

                            status.Equals("Waiting", StringComparison.OrdinalIgnoreCase) ||

                            status.Equals("Chờ tải", StringComparison.OrdinalIgnoreCase);



        if (hasActiveProgress && !isTerminated)

        {

            return true;

        }



        return false;

    }



    private System.Threading.CancellationTokenSource? _queueTouchHoldCts;

    private Point _queueTouchStartPos;

    private bool _queueTouchMoved;

    private bool _queueTouchLongPressTriggered;

    private int _isThumbnailScanScheduled;



    private static bool IsInteractiveChildControl(object? source, Control container)

    {

        var src = source as Control;

        while (src != null && src != container)

        {

            if (src is Button || src is CheckBox || src is TextBox || src is NumericUpDown || src is MenuItem)

            {

                return true;

            }

            src = src.Parent as Control;

        }

        return false;

    }



    private bool ShouldKeepHoverPreviewAlive(Control? sourceControl, PointerEventArgs e)

    {

        if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid())

        {

            return true;

        }



        if (sourceControl != null && sourceControl.Bounds.Width > 0 && sourceControl.Bounds.Height > 0)

        {

            var pt = e.GetPosition(sourceControl);

            if (pt.X >= 0 && pt.Y >= 0 && pt.X <= sourceControl.Bounds.Width && pt.Y <= sourceControl.Bounds.Height)

            {

                return true;

            }

        }



        var popup = this.FindControl<Border>("HoverPreviewPopupBorder");

        if (popup != null && popup.IsVisible)

        {

            if (popup.IsPointerOver)

            {

                return true;

            }

            if (popup.Bounds.Width > 0 && popup.Bounds.Height > 0)

            {

                var popPt = e.GetPosition(popup);

                if (popPt.X >= -12 && popPt.Y >= -12 && popPt.X <= popup.Bounds.Width + 12 && popPt.Y <= popup.Bounds.Height + 12)

                {

                    return true;

                }

            }

        }



        return false;

    }



    private void OnAnyScrollViewerScrollChanged(object? sender, ScrollChangedEventArgs e)

    {

        if (sender is ScrollViewer sv)

        {

            string? name = sv.Name;

            if (name == "LiveBeforeScrollViewer" || name == "LiveAfterScrollViewer" ||

                name == "ModalBeforeScrollViewer" || name == "ModalAfterScrollViewer")

            {

                return;

            }

        }



        ScheduleVisibleThumbnailScan(delayMs: 100);

    }



    private void ScheduleVisibleThumbnailScan(int delayMs = 100)

    {

        if (System.Threading.Interlocked.CompareExchange(ref _isThumbnailScanScheduled, 1, 0) != 0)

        {

            return;

        }



        System.Threading.Tasks.Task.Run(async () =>

        {

            try

            {

                if (delayMs > 0)

                {

                    await System.Threading.Tasks.Task.Delay(delayMs).ConfigureAwait(false);

                }

            }

            catch { }

            finally

            {

                System.Threading.Interlocked.Exchange(ref _isThumbnailScanScheduled, 0);

            }



            Avalonia.Threading.Dispatcher.UIThread.Post(ScanAndLoadVisibleThumbnails, Avalonia.Threading.DispatcherPriority.Background);

        });

    }



    private void ScanAndLoadVisibleThumbnails()

    {

        if (DataContext is not MainViewModel vm)

        {

            return;

        }



        if (!vm.IsThumbnailMode && !vm.IsThumbnailColumnVisible && !vm.IsPopupPreview)

        {

            return;

        }



        Control? activeItemsControl = vm.IsThumbnailMode

            ? this.FindControl<ItemsControl>("ThumbnailItemsControl")

            : this.FindControl<ItemsControl>("QueueItemsControl");



        if (activeItemsControl == null || !activeItemsControl.IsVisible)

        {

            return;

        }



        double viewH = this.Bounds.Height > 0 ? this.Bounds.Height : 900;

        var seenItems = new HashSet<ComicBookItem>();

        var missingVisibleItems = new List<ComicBookItem>();



        foreach (var visual in activeItemsControl.GetVisualDescendants())

        {

            if (visual is Control ctrl && ctrl.IsVisible && ctrl.DataContext is ComicBookItem item)

            {

                if (!seenItems.Add(item))

                {

                    continue;

                }



                if (item.ThumbnailBitmap != null && item.HasThumbnailFile)

                {

                    continue;

                }



                if (item.IsThumbnailLoading)

                {

                    continue;

                }



                var pos = ctrl.TranslatePoint(new Point(0, 0), this);

                if (pos.HasValue)

                {

                    double y = pos.Value.Y;

                    double h = ctrl.Bounds.Height > 0 ? ctrl.Bounds.Height : 100;

                    // Kiểm tra phần tử nằm trong vùng hiển thị của màn hình (mở rộng biên 300px trên/dưới để tự động nạp khi cuộn tới)

                    if (y + h >= -300 && y <= viewH + 350)

                    {

                        missingVisibleItems.Add(item);

                    }

                }

            }

        }



        foreach (var item in missingVisibleItems)

        {

            _ = ComicDownloaderGMTPC.Services.ThumbnailPreviewService.Instance.EnsureVisibleItemThumbnailAsync(item);

        }

    }



    private void CheckAndLoadSingleControlThumbnail(Control? control)

    {

        if (control?.DataContext is ComicBookItem item && DataContext is MainViewModel vm)

        {

            if (vm.IsThumbnailMode || vm.IsThumbnailColumnVisible || vm.IsPopupPreview)

            {

                if (item.ThumbnailBitmap == null && !item.IsThumbnailLoading)

                {

                    _ = ComicDownloaderGMTPC.Services.ThumbnailPreviewService.Instance.EnsureVisibleItemThumbnailAsync(item);

                }

            }

        }

    }



    private void OnQueueItemPointerPressed(object? sender, PointerPressedEventArgs e)

    {

        if (sender is not Control ctrl || ctrl.DataContext is not ComicDownloaderGMTPC.Models.ComicBookItem item)

        {

            return;

        }



        // Bỏ qua nếu người dùng tương tác trực tiếp vào các nút con (Button, CheckBox, TextBox, ...)

        if (IsInteractiveChildControl(e.Source, ctrl))

        {

            return;

        }



        var point = e.GetCurrentPoint(ctrl);



        // Click chuột phải trên Desktop (Windows / Linux / macOS)

        if (point.Properties.IsRightButtonPressed)

        {

            if (ctrl.ContextMenu != null)

            {

                ctrl.ContextMenu.Open(ctrl);

                e.Handled = true;

            }

            return;

        }



        // Chạm cảm ứng trên Android / Màn hình Touch:

        // - Nhấn giữ > 450ms để mở Context Menu

        // - Chạm nhanh (Tap < 450ms) để mở Popup Preview (hoạt động cho cả List view và List + thumbnail column)

        if (point.Pointer.Type == PointerType.Touch)

        {

            _queueTouchHoldCts?.Cancel();

            _queueTouchHoldCts = new System.Threading.CancellationTokenSource();

            var token = _queueTouchHoldCts.Token;

            _queueTouchStartPos = point.Position;

            _queueTouchMoved = false;

            _queueTouchLongPressTriggered = false;



            System.Threading.Tasks.Task.Run(async () =>

            {

                try

                {

                    await System.Threading.Tasks.Task.Delay(450, token);

                    if (!token.IsCancellationRequested && !_queueTouchMoved)

                    {

                        _queueTouchLongPressTriggered = true;

                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>

                        {

                            if (DataContext is MainViewModel touchVm)

                            {

                                touchVm.HideHoverPreview();

                            }

                            if (ctrl.ContextMenu != null)

                            {

                                ctrl.ContextMenu.Open(ctrl);

                            }

                        });

                    }

                }

                catch (System.OperationCanceledException) { }

            }, token);

            return;

        }



        // Chuột trái trên Desktop (hoặc thiết bị gửi PointerType.Mouse):

        if (DataContext is MainViewModel vm)

        {

            if (vm.IsPopupPreview)

            {

                vm.ShowHoverPreview(item);

                UpdateHoverPreviewPosition(e);

            }

            if (item.IsParallelSplitParent)

            {

                vm.ToggleSplitCollapse(item);

            }

        }

    }



    private CancellationTokenSource? _queueHoverDelayCts;

    private ComicBookItem? _pendingQueueHoverItem;

    private Point _lastQueueHoverPoint;



    private void TriggerDelayedQueueHoverPreview(ComicBookItem item, PointerEventArgs e)

    {

        if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid()) return;

        if (DataContext is not MainViewModel vm || !vm.IsPopupPreview) return;



        if (vm.IsHoverPreviewVisible && vm.ActiveHoverItem == item)

        {

            UpdateHoverPreviewPosition(e);

            return;

        }



        var pos = e.GetCurrentPoint(this).Position;

        _lastQueueHoverPoint = pos;

        _pendingQueueHoverItem = item;



        _queueHoverDelayCts?.Cancel();

        _queueHoverDelayCts?.Dispose();

        var cts = new CancellationTokenSource();

        _queueHoverDelayCts = cts;



        Task.Delay(500, cts.Token).ContinueWith(t =>

        {

            if (t.IsCanceled) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>

            {

                if (cts.IsCancellationRequested) return;

                if (DataContext is MainViewModel currentVm && currentVm.IsPopupPreview && _pendingQueueHoverItem == item)

                {

                    currentVm.ShowHoverPreview(item);

                    UpdateHoverPreviewPositionManual(pos);

                }

            });

        }, TaskContinuationOptions.OnlyOnRanToCompletion);

    }



    private void CancelQueueHoverPreview()

    {

        _queueHoverDelayCts?.Cancel();

        _queueHoverDelayCts?.Dispose();

        _queueHoverDelayCts = null;

        _pendingQueueHoverItem = null;

    }



    private void OnQueueItemPointerEntered(object? sender, PointerEventArgs e)

    {

        if (e.Pointer.Type == PointerType.Touch) return;

        if (sender is Border border)

        {

            border.BorderBrush = Avalonia.Media.Brush.Parse("#00E5FF");

        }

        if (sender is Control control && control.DataContext is ComicBookItem item)

        {

            TriggerDelayedQueueHoverPreview(item, e);

        }

    }



    private void OnQueueItemPointerExited(object? sender, PointerEventArgs e)

    {

        if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid()) return;

        CancelQueueHoverPreview();

        if (sender is Border border)

        {

            border.BorderBrush = Avalonia.Media.Brush.Parse("#1E293B");

        }

        if (ShouldKeepHoverPreviewAlive(sender as Control, e))

        {

            return;

        }

        if (DataContext is MainViewModel vm)

        {

            vm.HideHoverPreview();

        }

    }



    private void OnQueueItemPointerMoved(object? sender, PointerEventArgs e)

    {

        if (_queueTouchHoldCts != null && sender is Visual visual)

        {

            var point = e.GetCurrentPoint(visual);

            double dx = point.Position.X - _queueTouchStartPos.X;

            double dy = point.Position.Y - _queueTouchStartPos.Y;

            if (Math.Sqrt(dx * dx + dy * dy) > 15) // Người dùng đang cuộn danh sách (swipe/scroll)

            {

                _queueTouchMoved = true;

                _queueTouchHoldCts.Cancel();

            }

        }



        if (e.Pointer.Type == PointerType.Touch) return;



        if (sender is Control control && control.DataContext is ComicBookItem item && DataContext is MainViewModel vm)

        {

            if (vm.IsPopupPreview)

            {

                if (vm.IsHoverPreviewVisible && vm.ActiveHoverItem == item)

                {

                    UpdateHoverPreviewPosition(e);

                }

                else

                {

                    var pos = e.GetCurrentPoint(this).Position;

                    double dx = pos.X - _lastQueueHoverPoint.X;

                    double dy = pos.Y - _lastQueueHoverPoint.Y;

                    if (Math.Sqrt(dx * dx + dy * dy) > 15 || _pendingQueueHoverItem != item)

                    {

                        TriggerDelayedQueueHoverPreview(item, e);

                    }

                }

            }

        }

    }



    private void OnQueueItemPointerReleased(object? sender, PointerReleasedEventArgs e)

    {

        _queueTouchHoldCts?.Cancel();



        if (e.Pointer.Type == PointerType.Touch && !_queueTouchMoved && !_queueTouchLongPressTriggered)

        {

            if (sender is Control ctrl && ctrl.DataContext is ComicBookItem item && !IsInteractiveChildControl(e.Source, ctrl))

            {

                if (DataContext is MainViewModel vm)

                {

                    if (vm.IsPopupPreview)

                    {

                        vm.ShowHoverPreview(item);

                        UpdateHoverPreviewPosition(e);

                    }

                    else if (item.IsParallelSplitParent)

                    {

                        vm.ToggleSplitCollapse(item);

                    }

                }

            }

        }

    }



    private void OnQueueItemPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)

    {

        _queueTouchHoldCts?.Cancel();

    }



    private void OnRenameDragOver(object? sender, DragEventArgs e)

    {

        e.DragEffects = DragDropEffects.Copy;

        e.Handled = true;

    }



    private void OnRenameDrop(object? sender, DragEventArgs e)

    {

        try

        {

            if (e.DataTransfer != null)

            {

                var paths = new List<string>();

                foreach (var item in e.DataTransfer.Items)

                {

                    try

                    {

                        dynamic dItem = item;

                        string p = string.Empty;

                        try { p = dItem.Path?.LocalPath ?? dItem.Path?.ToString() ?? dItem.ToString() ?? string.Empty; } catch { }

                        if (!string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)))

                        {

                            paths.Add(p);

                        }

                    }

                    catch { }

                }



                if (paths.Count > 0 && DataContext is MainViewModel vm)

                {

                    vm.AddDroppedPathsToRename(paths);

                }

            }

        }

        catch { }

    }



    private void OnThumbnailElementAttached(object? sender, VisualTreeAttachmentEventArgs e)

    {

        CheckAndLoadSingleControlThumbnail(sender as Control);

        ScheduleVisibleThumbnailScan(delayMs: 80);

    }



    private void OnThumbnailElementDataContextChanged(object? sender, EventArgs e)

    {

        CheckAndLoadSingleControlThumbnail(sender as Control);

        ScheduleVisibleThumbnailScan(delayMs: 80);

    }



    private void OnQueueThumbnailPointerPressed(object? sender, PointerPressedEventArgs e)

    {

        if (sender is Control control && control.DataContext is ComicBookItem item && DataContext is MainViewModel vm)

        {

            var point = e.GetCurrentPoint(control);

            if (point.Properties.IsRightButtonPressed)

            {

                return;

            }

            if (vm.IsPopupPreview)

            {

                vm.ShowHoverPreview(item);

                UpdateHoverPreviewPosition(e);

                e.Handled = true;

            }

        }

    }



    private void OnThumbnailItemPointerEntered(object? sender, PointerEventArgs e)

    {

        if (e.Pointer.Type == PointerType.Touch) return;

        if (sender is Border border)

        {

            border.BorderBrush = Avalonia.Media.Brush.Parse("#FF2A85");

            border.BorderThickness = new Thickness(1.5);

        }

        if (sender is Control control && control.DataContext is ComicBookItem item)

        {

            TriggerDelayedQueueHoverPreview(item, e);

        }

    }



    private void OnThumbnailItemPointerMoved(object? sender, PointerEventArgs e)

    {

        if (e.Pointer.Type == PointerType.Touch) return;

        if (sender is Control control && control.DataContext is ComicBookItem item && DataContext is MainViewModel vm)

        {

            if (vm.IsPopupPreview)

            {

                if (vm.IsHoverPreviewVisible && vm.ActiveHoverItem == item)

                {

                    UpdateHoverPreviewPosition(e);

                }

                else

                {

                    var pos = e.GetCurrentPoint(this).Position;

                    double dx = pos.X - _lastQueueHoverPoint.X;

                    double dy = pos.Y - _lastQueueHoverPoint.Y;

                    if (Math.Sqrt(dx * dx + dy * dy) > 15 || _pendingQueueHoverItem != item)

                    {

                        TriggerDelayedQueueHoverPreview(item, e);

                    }

                }

            }

        }

    }



    private void OnThumbnailItemPointerExited(object? sender, PointerEventArgs e)

    {

        if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid()) return;

        CancelQueueHoverPreview();

        if (sender is Border border)

        {

            border.BorderBrush = Avalonia.Media.Brush.Parse("#00E5FF");

            border.BorderThickness = new Thickness(1);

        }

        if (ShouldKeepHoverPreviewAlive(sender as Control, e))

        {

            return;

        }

        if (DataContext is MainViewModel vm)

        {

            vm.HideHoverPreview();

        }

    }



    private void OnPopupPreviewPointerExited(object? sender, PointerEventArgs e)

    {

        if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid()) return;

        if (DataContext is MainViewModel vm)

        {

            vm.HideHoverPreview();

        }

    }



    private void OnThumbnailItemPointerPressed(object? sender, PointerPressedEventArgs e)

    {

        if (sender is Control control && control.DataContext is ComicBookItem item)

        {

            // Toggle selection hoặc hiển thị popup khi click

            var point = e.GetCurrentPoint(control);

            if (point.Properties.IsRightButtonPressed)

            {

                // Chuột phải: mở ContextMenu tự nhiên

                return;

            }



            if (IsInteractiveChildControl(e.Source, control))

            {

                return;

            }



            if (DataContext is MainViewModel vm)

            {

                // Nếu click vào thẻ, chọn truyện này

                item.IsChecked = !item.IsChecked;

                if (vm.IsPopupPreview)

                {

                    vm.ShowHoverPreview(item);

                    UpdateHoverPreviewPosition(e);

                }

            }

        }

    }



    private void UpdateHoverPreviewPosition(PointerEventArgs e)

    {

        var popup = this.FindControl<Border>("HoverPreviewPopupBorder");

        if (popup == null)

        {

            return;

        }



        double viewW = this.Bounds.Width > 0 ? this.Bounds.Width : 1200;

        double viewH = this.Bounds.Height > 0 ? this.Bounds.Height : 850;

        double popupW = Math.Min(340, Math.Max(260, viewW - 24));

        popup.Width = popupW;

        double popupH = popup.Bounds.Height > 0 ? popup.Bounds.Height : 480;



        if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid())

        {

            double touchLeft = Math.Max(12, (viewW - popupW) / 2.0);

            var touchPos = e.GetPosition(this);

            double touchTop = Math.Clamp(touchPos.Y - 120, 16, Math.Max(16, viewH - popupH - 24));

            popup.Margin = new Thickness(touchLeft, touchTop, 0, 0);

            return;

        }



        UpdateHoverPreviewPositionManual(e.GetPosition(this));

    }



    private void UpdateHoverPreviewPositionManual(Point pos)

    {

        var popup = this.FindControl<Border>("HoverPreviewPopupBorder");

        if (popup == null) return;



        double viewW = this.Bounds.Width > 0 ? this.Bounds.Width : 1200;

        double viewH = this.Bounds.Height > 0 ? this.Bounds.Height : 850;

        double popupW = Math.Min(340, Math.Max(260, viewW - 24));

        popup.Width = popupW;

        double popupH = popup.Bounds.Height > 0 ? popup.Bounds.Height : 480;



        if (OperatingSystem.IsAndroid())

        {

            double touchLeft = Math.Max(12, (viewW - popupW) / 2.0);

            double touchTop = Math.Clamp(pos.Y - 120, 16, Math.Max(16, viewH - popupH - 24));

            popup.Margin = new Thickness(touchLeft, touchTop, 0, 0);

            return;

        }



        double left = pos.X + 24;

        if (left + popupW + 16 > viewW)

        {

            left = Math.Max(12, pos.X - popupW - 24);

        }



        double top = pos.Y - 40;

        if (top + popupH + 16 > viewH)

        {

            top = Math.Max(12, viewH - popupH - 16);

        }

        if (top < 12)

        {

            top = 12;

        }



        popup.Margin = new Thickness(left, top, 0, 0);

    }



    private void OnClosePreviewOverlayClicked(object? sender, RoutedEventArgs e)

    {

        if (DataContext is MainViewModel vm)

        {

            vm.HideHoverPreview();

        }

    }



    // =========================================================================

    // DUAL PICTURE SORT: HOVER 0.5S (DESKTOP) & TOUCH DRAG / SINGLE TAP (ANDROID)

    // =========================================================================

    private CancellationTokenSource? _dualSortHoverDelayCts;

    private PictureSortItem? _pendingDualSortHoverItem;

    private Point _lastDualSortHoverPoint;

    private Point _dualSortTouchStartPos;

    private bool _dualSortTouchIsDragging;

    private PictureSortItem? _touchedDualSortItem;

    private DateTime _dualSortLastTapTime = DateTime.MinValue;

    private PictureSortItem? _dualSortLastTappedItem;

    private CancellationTokenSource? _dualSortTapPreviewDelayCts;



    private void TriggerDelayedDualSortHoverPreview(PictureSortItem item, PointerEventArgs e)

    {

        if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid()) return;

        if (DataContext is not MainViewModel vm) return;



        if (vm.IsDualSortPreviewVisible && vm.DualSortPreviewItem == item)

        {

            return;

        }



        var pos = e.GetCurrentPoint(this).Position;

        _lastDualSortHoverPoint = pos;

        _pendingDualSortHoverItem = item;



        _dualSortHoverDelayCts?.Cancel();

        _dualSortHoverDelayCts?.Dispose();

        var cts = new CancellationTokenSource();

        _dualSortHoverDelayCts = cts;



        Task.Delay(500, cts.Token).ContinueWith(t =>

        {

            if (t.IsCanceled) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>

            {

                if (cts.IsCancellationRequested) return;

                if (DataContext is MainViewModel currentVm && _pendingDualSortHoverItem == item)

                {

                    currentVm.ShowDualSortPreview(item);

                    UpdateDualSortPopupPosition(pos);

                }

            });

        }, TaskContinuationOptions.OnlyOnRanToCompletion);

    }



    private void CancelDualSortHoverPreview()

    {

        _dualSortHoverDelayCts?.Cancel();

        _dualSortHoverDelayCts?.Dispose();

        _dualSortHoverDelayCts = null;

        _pendingDualSortHoverItem = null;



        _dualSortTapPreviewDelayCts?.Cancel();

        _dualSortTapPreviewDelayCts?.Dispose();

        _dualSortTapPreviewDelayCts = null;

    }



    private void UpdateDualSortPopupPosition(Point pos)

    {

        var popup = this.FindControl<Border>("DualSortPreviewPopupBorder");

        if (popup == null) return;



        double viewW = this.Bounds.Width > 0 ? this.Bounds.Width : 1200;

        double viewH = this.Bounds.Height > 0 ? this.Bounds.Height : 850;

        double popupW = Math.Min(380, Math.Max(280, viewW - 24));

        popup.Width = popupW;

        double popupH = popup.Bounds.Height > 0 ? popup.Bounds.Height : 480;



        if (OperatingSystem.IsAndroid())

        {

            double touchLeft = Math.Max(12, (viewW - popupW) / 2.0);

            double touchTop = Math.Clamp(pos.Y - 120, 16, Math.Max(16, viewH - popupH - 24));

            popup.Margin = new Thickness(touchLeft, touchTop, 0, 0);

            return;

        }



        double left = pos.X + 24;

        if (left + popupW + 16 > viewW)

        {

            left = Math.Max(12, pos.X - popupW - 24);

        }



        double top = pos.Y - 40;

        if (top + popupH + 16 > viewH)

        {

            top = Math.Max(12, viewH - popupH - 16);

        }

        if (top < 12)

        {

            top = 12;

        }



        popup.Margin = new Thickness(left, top, 0, 0);

    }



    private void OnDualSortPreviewPopupPointerExited(object? sender, PointerEventArgs e)

    {

        if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid()) return;

        if (DataContext is MainViewModel vm)

        {

            vm.CloseDualSortPreview();

        }

    }



    // --- Xử lý sự kiện cho Source Items ---

    private void OnDualSortSourceItemPointerEntered(object? sender, PointerEventArgs e)

    {

        if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid()) return;

        if (sender is Control control && control.DataContext is PictureSortItem item)

        {

            TriggerDelayedDualSortHoverPreview(item, e);

        }

    }



    private void OnDualSortSourceItemPointerMoved(object? sender, PointerEventArgs e)

    {

        if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid())

        {

            var currentPos = e.GetCurrentPoint(this).Position;

            double dx = currentPos.X - _dualSortTouchStartPos.X;

            double dy = currentPos.Y - _dualSortTouchStartPos.Y;

            if (Math.Sqrt(dx * dx + dy * dy) > 12)

            {

                // Đang cuộn danh sách (scroll) hoặc vuốt ngón tay: Tuyệt đối không show ảnh!

                _dualSortTouchIsDragging = true;

            }

            return;

        }



        if (sender is Control control && control.DataContext is PictureSortItem item)

        {

            var pos = e.GetCurrentPoint(this).Position;

            double dx = pos.X - _lastDualSortHoverPoint.X;

            double dy = pos.Y - _lastDualSortHoverPoint.Y;

            if (Math.Sqrt(dx * dx + dy * dy) > 15 || _pendingDualSortHoverItem != item)

            {

                TriggerDelayedDualSortHoverPreview(item, e);

            }

        }

    }



    private void OnDualSortSourceItemPointerExited(object? sender, PointerEventArgs e)

    {

        if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid()) return;

        CancelDualSortHoverPreview();

        if (DataContext is MainViewModel vm && vm.IsDualSortPreviewVisible)

        {

            var popup = this.FindControl<Border>("DualSortPreviewPopupBorder");

            if (popup != null)

            {

                var posInPopup = e.GetPosition(popup);

                if (posInPopup.X >= 0 && posInPopup.X <= popup.Bounds.Width &&

                    posInPopup.Y >= 0 && posInPopup.Y <= popup.Bounds.Height)

                {

                    return;

                }

            }

            vm.CloseDualSortPreview();

        }

    }



    private void OnDualSortSourceItemPointerPressed(object? sender, PointerPressedEventArgs e)

    {

        try

        {

            if (sender is Control control && control.DataContext is PictureSortItem item && DataContext is MainViewModel vm)

            {

                if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid())

                {

                    _dualSortTouchStartPos = e.GetCurrentPoint(this).Position;

                    _dualSortTouchIsDragging = false;

                    _touchedDualSortItem = item;

                }

                else

                {

                    if (e.ClickCount == 2)

                    {

                        vm.SortItemForward(item);

                    }

                    else

                    {

                        vm.OnSourceItemClicked(item);

                    }

                }

            }

        }

        catch { }

    }



    private void OnDualSortSourceItemPointerReleased(object? sender, PointerReleasedEventArgs e)

    {

        try

        {

            if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid())

            {

                if (!_dualSortTouchIsDragging && sender is Control control && control.DataContext is PictureSortItem item && _touchedDualSortItem == item)

                {

                    if (DataContext is MainViewModel vm)

                    {

                        if (vm.IsBatchModeActive)

                        {

                            vm.HandleBatchItemClick(item, isSource: true);

                            _dualSortTouchIsDragging = false;

                            _touchedDualSortItem = null;

                            return;

                        }



                        if (vm.IsOneClickFastSort)

                        {

                            _dualSortTapPreviewDelayCts?.Cancel();

                            _dualSortTapPreviewDelayCts?.Dispose();

                            _dualSortTapPreviewDelayCts = null;

                            _dualSortLastTapTime = DateTime.MinValue;

                            _dualSortLastTappedItem = null;

                            vm.SortItemForward(item);

                        }

                        else

                        {

                            var now = DateTime.UtcNow;

                            double elapsed = (now - _dualSortLastTapTime).TotalMilliseconds;



                            if (elapsed < 500 && _dualSortLastTappedItem == item)

                            {

                                // DOUBLE TAP TRÊN ANDROID: Hủy ngay delay preview và chuyển ảnh sang thư mục lọc!

                                _dualSortTapPreviewDelayCts?.Cancel();

                                _dualSortTapPreviewDelayCts?.Dispose();

                                _dualSortTapPreviewDelayCts = null;

                                _dualSortLastTapTime = DateTime.MinValue;

                                _dualSortLastTappedItem = null;



                                vm.CloseDualSortPreview();

                                vm.SortItemForward(item);

                            }

                            else

                            {

                                // SINGLE TAP TRÊN ANDROID: Lưu thời gian tap, chọn item, và hoãn 500ms kiểm tra không có double tap mới preview

                                _dualSortLastTapTime = now;

                                _dualSortLastTappedItem = item;

                                var tapPos = _dualSortTouchStartPos;



                                vm.SelectDualSortItem(item);



                                _dualSortTapPreviewDelayCts?.Cancel();

                                _dualSortTapPreviewDelayCts?.Dispose();

                                var cts = new CancellationTokenSource();

                                _dualSortTapPreviewDelayCts = cts;



                                Task.Delay(500, cts.Token).ContinueWith(t =>

                                {

                                    if (t.IsCanceled) return;

                                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>

                                    {

                                        if (cts.IsCancellationRequested) return;

                                        if (DataContext is MainViewModel currentVm && currentVm.IsDualSortPreviewEnabled && _dualSortLastTappedItem == item)

                                        {

                                            currentVm.ShowDualSortPreview(item);

                                            UpdateDualSortPopupPosition(tapPos);

                                        }

                                    });

                                }, TaskContinuationOptions.OnlyOnRanToCompletion);

                            }

                        }

                    }

                }

                _dualSortTouchIsDragging = false;

                _touchedDualSortItem = null;

            }

        }

        catch { }

    }



    // --- Xử lý sự kiện cho Sorted Items ---

    private void OnDualSortSortedItemPointerEntered(object? sender, PointerEventArgs e)

    {

        if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid()) return;

        if (sender is Control control && control.DataContext is PictureSortItem item)

        {

            TriggerDelayedDualSortHoverPreview(item, e);

        }

    }



    private void OnDualSortSortedItemPointerMoved(object? sender, PointerEventArgs e)

    {

        if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid())

        {

            var currentPos = e.GetCurrentPoint(this).Position;

            double dx = currentPos.X - _dualSortTouchStartPos.X;

            double dy = currentPos.Y - _dualSortTouchStartPos.Y;

            if (Math.Sqrt(dx * dx + dy * dy) > 12)

            {

                _dualSortTouchIsDragging = true;

            }

            return;

        }



        if (sender is Control control && control.DataContext is PictureSortItem item)

        {

            var pos = e.GetCurrentPoint(this).Position;

            double dx = pos.X - _lastDualSortHoverPoint.X;

            double dy = pos.Y - _lastDualSortHoverPoint.Y;

            if (Math.Sqrt(dx * dx + dy * dy) > 15 || _pendingDualSortHoverItem != item)

            {

                TriggerDelayedDualSortHoverPreview(item, e);

            }

        }

    }



    private void OnDualSortSortedItemPointerExited(object? sender, PointerEventArgs e)

    {

        if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid()) return;

        CancelDualSortHoverPreview();

        if (DataContext is MainViewModel vm && vm.IsDualSortPreviewVisible)

        {

            var popup = this.FindControl<Border>("DualSortPreviewPopupBorder");

            if (popup != null)

            {

                var posInPopup = e.GetPosition(popup);

                if (posInPopup.X >= 0 && posInPopup.X <= popup.Bounds.Width &&

                    posInPopup.Y >= 0 && posInPopup.Y <= popup.Bounds.Height)

                {

                    return;

                }

            }

            vm.CloseDualSortPreview();

        }

    }



    private void OnDualSortSortedItemPointerPressed(object? sender, PointerPressedEventArgs e)

    {

        try

        {

            if (sender is Control control && control.DataContext is PictureSortItem item && DataContext is MainViewModel vm)

            {

                if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid())

                {

                    _dualSortTouchStartPos = e.GetCurrentPoint(this).Position;

                    _dualSortTouchIsDragging = false;

                    _touchedDualSortItem = item;

                }

                else

                {

                    if (e.ClickCount == 2)

                    {

                        vm.SortItemBackward(item);

                    }

                    else

                    {

                        vm.OnSortedItemClicked(item);

                    }

                }

            }

        }

        catch { }

    }



    private void OnDualSortSortedItemPointerReleased(object? sender, PointerReleasedEventArgs e)

    {

        try

        {

            if (e.Pointer.Type == PointerType.Touch || OperatingSystem.IsAndroid())

            {

                if (!_dualSortTouchIsDragging && sender is Control control && control.DataContext is PictureSortItem item && _touchedDualSortItem == item)

                {

                    if (DataContext is MainViewModel vm)

                    {

                        if (vm.IsBatchModeActive)

                        {

                            vm.HandleBatchItemClick(item, isSource: false);

                            _dualSortTouchIsDragging = false;

                            _touchedDualSortItem = null;

                            return;

                        }



                        if (vm.IsOneClickFastSort)

                        {

                            _dualSortTapPreviewDelayCts?.Cancel();

                            _dualSortTapPreviewDelayCts?.Dispose();

                            _dualSortTapPreviewDelayCts = null;

                            _dualSortLastTapTime = DateTime.MinValue;

                            _dualSortLastTappedItem = null;

                            vm.SortItemBackward(item);

                        }

                        else

                        {

                            var now = DateTime.UtcNow;

                            double elapsed = (now - _dualSortLastTapTime).TotalMilliseconds;



                            if (elapsed < 500 && _dualSortLastTappedItem == item)

                            {

                                // DOUBLE TAP TRÊN ANDROID: Hủy ngay delay preview và chuyển ảnh ngược lại thư mục gốc!

                                _dualSortTapPreviewDelayCts?.Cancel();

                                _dualSortTapPreviewDelayCts?.Dispose();

                                _dualSortTapPreviewDelayCts = null;

                                _dualSortLastTapTime = DateTime.MinValue;

                                _dualSortLastTappedItem = null;



                                vm.CloseDualSortPreview();

                                vm.SortItemBackward(item);

                            }

                            else

                            {

                                // SINGLE TAP TRÊN ANDROID: Lưu thời gian tap, chọn item, và hoãn 500ms kiểm tra không có double tap mới preview

                                _dualSortLastTapTime = now;

                                _dualSortLastTappedItem = item;

                                var tapPos = _dualSortTouchStartPos;



                                vm.SelectDualSortItem(item);



                                _dualSortTapPreviewDelayCts?.Cancel();

                                _dualSortTapPreviewDelayCts?.Dispose();

                                var cts = new CancellationTokenSource();

                                _dualSortTapPreviewDelayCts = cts;



                                Task.Delay(500, cts.Token).ContinueWith(t =>

                                {

                                    if (t.IsCanceled) return;

                                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>

                                    {

                                        if (cts.IsCancellationRequested) return;

                                        if (DataContext is MainViewModel currentVm && currentVm.IsDualSortPreviewEnabled && _dualSortLastTappedItem == item)

                                        {

                                            currentVm.ShowDualSortPreview(item);

                                            UpdateDualSortPopupPosition(tapPos);

                                        }

                                    });

                                }, TaskContinuationOptions.OnlyOnRanToCompletion);

                            }

                        }

                    }

                }

                _dualSortTouchIsDragging = false;

                _touchedDualSortItem = null;

            }

        }

        catch { }

    }

    

    // =========================================================================

    // DYNAMIC UI LOCALIZATION (CHUYỂN ĐỔI NGÔN NGỮ ĐỘNG TOÀN DIỆN VI / EN)

    // =========================================================================

    private readonly Dictionary<object, string> _originalTextMap = new();



    private void OnLanguageServiceChanged()

    {

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>

        {

            ApplyLanguage(LanguageService.Instance.CurrentLanguage);

        });

    }



    public void ApplyLanguage(string lang)

    {

        try

        {

            bool toVi = string.Equals(lang, "VI", StringComparison.OrdinalIgnoreCase);

            var visited = new HashSet<object>();

            TranslateLogicalAndVisual(this, toVi, visited);

        }

        catch { }

    }



    private static bool IsPropertyBound(AvaloniaObject obj, AvaloniaProperty prop)

    {

        try

        {

            // Trong Avalonia, nếu control thuộc về Template của ItemsControl (danh sách động) thì bỏ qua

            if (obj is StyledElement se && se.TemplatedParent != null)

            {

                return true;

            }

            return false;

        }

        catch

        {

            return false;

        }

    }



    private void TranslateLogicalAndVisual(object? element, bool toVi, HashSet<object> visited)

    {

        if (element == null || !visited.Add(element)) return;



        // Bỏ qua toàn bộ ComboBox và các element bên trong ComboBox, TextBox, NumericUpDown

        // để không ghi đè LocalValue làm đóng băng Text hiển thị của SelectedItem trong ComboBox

        if (element is ComboBox || element is TextBox || element is NumericUpDown) return;

        if (element is Visual visualElement && visualElement.FindAncestorOfType<ComboBox>() != null) return;



        TranslateElement(element, toVi);



        if (element is ILogical logical)

        {

            foreach (var child in logical.LogicalChildren)

            {

                TranslateLogicalAndVisual(child, toVi, visited);

            }

        }



        if (element is Visual visual)

        {

            foreach (var child in visual.GetVisualChildren())

            {

                TranslateLogicalAndVisual(child, toVi, visited);

            }

        }



        if (element is TabControl tabControl && tabControl.Items != null)

        {

            foreach (var item in tabControl.Items)

            {

                TranslateLogicalAndVisual(item, toVi, visited);

            }

        }



        if (element is Control ctrl)

        {

            if (ctrl.ContextMenu != null)

            {

                TranslateLogicalAndVisual(ctrl.ContextMenu, toVi, visited);

            }

            if (ctrl is Button btn && btn.Flyout is MenuFlyout flyout)

            {

                foreach (var item in flyout.Items)

                {

                    TranslateLogicalAndVisual(item, toVi, visited);

                }

            }

        }

    }



    private void TranslateElement(object element, bool toVi)

    {

        if (element is TabItem tabItem)

        {

            if (tabItem.Header is string hStr && !string.IsNullOrWhiteSpace(hStr))

            {

                if (!IsPropertyBound(tabItem, TabItem.HeaderProperty))

                {

                    if (!_originalTextMap.TryGetValue(tabItem, out var orig))

                    {

                        orig = hStr;

                        _originalTextMap[tabItem] = orig;

                    }

                    tabItem.Header = LanguageService.Instance.Translate(orig, toVi);

                }

            }

        }

        else if (element is Button btn)

        {

            if (btn.Content is string bStr && !string.IsNullOrWhiteSpace(bStr))

            {

                if (!IsPropertyBound(btn, ContentControl.ContentProperty))

                {

                    if (!_originalTextMap.TryGetValue(btn, out var orig))

                    {

                        orig = bStr;

                        _originalTextMap[btn] = orig;

                    }

                    btn.Content = LanguageService.Instance.Translate(orig, toVi);

                }

            }

        }

        else if (element is CheckBox cb)

        {

            if (cb.Content is string cStr && !string.IsNullOrWhiteSpace(cStr))

            {

                if (!IsPropertyBound(cb, ContentControl.ContentProperty))

                {

                    if (!_originalTextMap.TryGetValue(cb, out var orig))

                    {

                        orig = cStr;

                        _originalTextMap[cb] = orig;

                    }

                    cb.Content = LanguageService.Instance.Translate(orig, toVi);

                }

            }

        }

        else if (element is MenuItem mi)

        {

            if (mi.Header is string mStr && !string.IsNullOrWhiteSpace(mStr))

            {

                if (!IsPropertyBound(mi, MenuItem.HeaderProperty))

                {

                    if (!_originalTextMap.TryGetValue(mi, out var orig))

                    {

                        orig = mStr;

                        _originalTextMap[mi] = orig;

                    }

                    mi.Header = LanguageService.Instance.Translate(orig, toVi);

                }

            }

        }

        else if (element is TextBlock tb)

        {

            if (!IsPropertyBound(tb, TextBlock.TextProperty) && !string.IsNullOrWhiteSpace(tb.Text))

            {

                if (tb.Text.Length > 1 && tb.Text != "−" && tb.Text != "+")

                {

                    if (!_originalTextMap.TryGetValue(tb, out var orig))

                    {

                        orig = tb.Text;

                        _originalTextMap[tb] = orig;

                    }

                    tb.Text = LanguageService.Instance.Translate(orig, toVi);

                }

            }

        }

        else if (element is HeaderedContentControl hcc)

        {

            if (hcc.Header is string hcStr && !string.IsNullOrWhiteSpace(hcStr))

            {

                if (!IsPropertyBound(hcc, HeaderedContentControl.HeaderProperty))

                {

                    if (!_originalTextMap.TryGetValue(hcc, out var orig))

                    {

                        orig = hcStr;

                        _originalTextMap[hcc] = orig;

                    }

                    hcc.Header = LanguageService.Instance.Translate(orig, toVi);

                }

            }

        }



        if (element is Control ctrl)

        {

            var tip = ToolTip.GetTip(ctrl);

            if (tip is string tipStr && !string.IsNullOrWhiteSpace(tipStr) && !tipStr.StartsWith("{Binding"))

            {

                string tipKey = "tip_" + ctrl.GetHashCode();

                if (!_originalTextMap.TryGetValue(tipKey, out var origTip))

                {

                    origTip = tipStr;

                    _originalTextMap[tipKey] = origTip;

                }

                ToolTip.SetTip(ctrl, LanguageService.Instance.Translate(origTip, toVi));

            }

        }

    }

}

