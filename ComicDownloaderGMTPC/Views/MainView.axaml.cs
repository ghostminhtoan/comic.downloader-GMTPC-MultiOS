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

            // 1. Đồng bộ Pan & 2 ngón Pinch-to-Zoom cho Live Preview trong Tab Xử Lý Ảnh
            SetupPinchAndPanGesture(this.FindControl<ScrollViewer>("LiveBeforeScrollViewer"), this.FindControl<ScrollViewer>("LiveAfterScrollViewer"));

            // 2. Đồng bộ Pan & 2 ngón Pinch-to-Zoom cho Modal Đối Chiếu Toàn Màn Hình (Dual View)
            SetupPinchAndPanGesture(this.FindControl<ScrollViewer>("ModalBeforeScrollViewer"), this.FindControl<ScrollViewer>("ModalAfterScrollViewer"));

            // 3. Pan & 2 ngón Pinch-to-Zoom cho Split View & Single View trong Modal Toàn Màn Hình
            SetupPinchAndPanGesture(this.FindControl<ScrollViewer>("ModalSplitScrollViewer"));
            SetupPinchAndPanGesture(this.FindControl<ScrollViewer>("ModalSingleScrollViewer"));
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

    private void SetupPinchAndPanGesture(params ScrollViewer?[] viewers)
    {
        var validViewers = viewers.Where(v => v != null).Cast<ScrollViewer>().ToList();
        if (validViewers.Count == 0) return;

        bool isSyncing = false;
        bool isDragging = false;
        bool isPinching = false;
        Point dragStartPoint = default;
        Vector dragStartOffset = default;
        Point initialPinchMidPoint = default;
        Vector initialPinchOffset = default;
        double initialPinchDistance = 0;
        double initialZoom = 1.0;

        // Bảng theo dõi các con trỏ chạm đa điểm (Multi-touch Touch Tracking)
        var activePointers = new Dictionary<long, Point>();

        // Đồng bộ cuộn giữa các ScrollViewer nếu có nhiều hơn 1
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

        // Kiểm tra xem nguồn sự kiện có nằm trên thanh ScrollBar (Track/Thumb/Buttons) hay không
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
            if (IsScrollBarElement(ev.Source, sender as Visual)) return;

            var p = ev.GetCurrentPoint(this);
            if (p.Properties.IsLeftButtonPressed || p.Properties.IsMiddleButtonPressed || ev.Pointer.Type == PointerType.Touch)
            {
                if (ev.Pointer.Type == PointerType.Mouse)
                {
                    activePointers.Clear();
                }

                activePointers[ev.Pointer.Id] = p.Position;
                var firstViewer = validViewers[0];

                if (activePointers.Count == 1)
                {
                    // 1 ngón tay / Chuột: Chế độ Pan (kéo rê ảnh tự do 2 chiều)
                    isDragging = true;
                    isPinching = false;
                    dragStartPoint = p.Position;
                    dragStartOffset = firstViewer.Offset;

                    // Chỉ capture khi là chuột máy tính, KHÔNG capture trên Touch để không khóa đa chạm 2 ngón trên Android
                    if (ev.Pointer.Type == PointerType.Mouse)
                    {
                        ev.Pointer.Capture(sender as IInputElement);
                    }
                    ev.Handled = true;
                }
                else if (activePointers.Count >= 2 && DataContext is MainViewModel vm)
                {
                    // 2 ngón tay trên Android / Màn hình cảm ứng: Kích hoạt Pinch-to-Zoom & Pan 2 ngón đồng thời
                    isDragging = true;
                    isPinching = true;
                    var pts = activePointers.Values.Take(2).ToArray();
                    initialPinchDistance = Math.Max(10.0, GetDistance(pts[0], pts[1]));
                    initialPinchMidPoint = new Point((pts[0].X + pts[1].X) / 2.0, (pts[0].Y + pts[1].Y) / 2.0);
                    initialPinchOffset = firstViewer.Offset;
                    initialZoom = vm.EnhancePreviewZoom;

                    try { ev.Pointer.Capture(null); } catch { }
                    ev.Handled = true;
                }
            }
        }

        void OnMoved(object? sender, PointerEventArgs ev)
        {
            if (IsScrollBarElement(ev.Source, sender as Visual)) return;

            if (activePointers.ContainsKey(ev.Pointer.Id))
            {
                var curPos = ev.GetCurrentPoint(this).Position;
                activePointers[ev.Pointer.Id] = curPos;
                var firstViewer = validViewers[0];

                if (activePointers.Count >= 2 && DataContext is MainViewModel vm)
                {
                    var pts = activePointers.Values.Take(2).ToArray();
                    double curDistance = GetDistance(pts[0], pts[1]);
                    var curMidPoint = new Point((pts[0].X + pts[1].X) / 2.0, (pts[0].Y + pts[1].Y) / 2.0);

                    if (!isPinching)
                    {
                        isDragging = true;
                        isPinching = true;
                        initialPinchDistance = Math.Max(10.0, curDistance);
                        initialPinchMidPoint = curMidPoint;
                        initialPinchOffset = firstViewer.Offset;
                        initialZoom = vm.EnhancePreviewZoom;
                    }

                    // 1. Phóng to / Thu nhỏ (Pinch-to-Zoom) theo tỷ lệ khoảng cách 2 ngón tay
                    if (initialPinchDistance > 5)
                    {
                        double scaleFactor = curDistance / initialPinchDistance;
                        double targetZoom = Math.Clamp(initialZoom * scaleFactor, 0.25, 5.0);
                        targetZoom = Math.Round(targetZoom, 2);
                        if (Math.Abs(targetZoom - vm.EnhancePreviewZoom) >= 0.01)
                        {
                            vm.EnhancePreviewZoom = targetZoom;
                        }
                    }

                    // 2. Kéo rê (Pan) đồng thời theo độ dịch chuyển của trung điểm 2 ngón tay
                    var midDelta = initialPinchMidPoint - curMidPoint;
                    var newOffset = new Vector(
                        Math.Max(0, initialPinchOffset.X + midDelta.X),
                        Math.Max(0, initialPinchOffset.Y + midDelta.Y)
                    );

                    isSyncing = true;
                    foreach (var v in validViewers)
                    {
                        v.Offset = newOffset;
                    }
                    isSyncing = false;

                    ev.Handled = true;
                }
                else if (isDragging && !isPinching)
                {
                    var delta = dragStartPoint - curPos;
                    var newOffset = new Vector(
                        Math.Max(0, dragStartOffset.X + delta.X),
                        Math.Max(0, dragStartOffset.Y + delta.Y)
                    );

                    isSyncing = true;
                    foreach (var v in validViewers)
                    {
                        v.Offset = newOffset;
                    }
                    isSyncing = false;

                    ev.Handled = true;
                }
            }
        }

        void OnReleased(object? sender, PointerReleasedEventArgs ev)
        {
            activePointers.Remove(ev.Pointer.Id);
            var firstViewer = validViewers[0];

            if (activePointers.Count == 1)
            {
                isPinching = false;
                isDragging = true;
                dragStartPoint = activePointers.Values.First();
                dragStartOffset = firstViewer.Offset;
            }
            else if (activePointers.Count == 0)
            {
                isDragging = false;
                isPinching = false;
                try { ev.Pointer.Capture(null); } catch { }
            }
        }

        void OnCaptureLost(object? sender, PointerCaptureLostEventArgs ev)
        {
            activePointers.Remove(ev.Pointer.Id);
            if (activePointers.Count == 0)
            {
                isDragging = false;
                isPinching = false;
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
    }
}