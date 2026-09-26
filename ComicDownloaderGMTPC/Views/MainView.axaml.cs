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

    private void OnMainViewLoaded(object? sender, RoutedEventArgs e)
    {
        // Đồng bộ Pan & Zoom cho Live Preview trong Tab Xử Lý Ảnh
        SetupSyncScroll(this.FindControl<ScrollViewer>("LiveBeforeScrollViewer"), this.FindControl<ScrollViewer>("LiveAfterScrollViewer"));

        // Đồng bộ Pan & Zoom cho Modal Đối Chiếu Toàn Màn Hình
        SetupSyncScroll(this.FindControl<ScrollViewer>("ModalBeforeScrollViewer"), this.FindControl<ScrollViewer>("ModalAfterScrollViewer"));

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

    private void SetupSyncScroll(ScrollViewer? before, ScrollViewer? after)
    {
        if (before == null || after == null) return;

        bool isSyncing = false;
        bool isDragging = false;
        bool isPinching = false;
        Point dragStartPoint = default;
        Vector dragStartOffset = default;

        // Bảng theo dõi các con trỏ chạm đa điểm (Multi-touch Touch Tracking)
        var activePointers = new Dictionary<long, Point>();
        double initialPinchDistance = 0;
        double initialZoom = 1.0;

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

        // Kiểm tra xem nguồn sự kiện có nằm trên thanh ScrollBar (Track/Thumb/Buttons) hay không
        // Nếu là ScrollBar, tuyệt đối không can thiệp để Avalonia tự xử lý cuộn chuẩn xác
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
            // Bỏ qua nếu người dùng click/chạm vào thanh ScrollBar (chống cướp pointer làm kẹt lặp lại cuộn tuột xuống bottom)
            if (IsScrollBarElement(ev.Source, sender as Visual)) return;

            var p = ev.GetCurrentPoint(this);
            if (p.Properties.IsLeftButtonPressed || p.Properties.IsMiddleButtonPressed || ev.Pointer.Type == PointerType.Touch)
            {
                activePointers[ev.Pointer.Id] = p.Position;

                if (activePointers.Count == 1)
                {
                    // 1 ngón tay / Chuột: Chế độ Pan (kéo rê ảnh tự do 2 chiều)
                    isDragging = true;
                    isPinching = false;
                    dragStartPoint = p.Position;
                    dragStartOffset = before.Offset;
                    ev.Pointer.Capture(sender as IInputElement);
                    ev.Handled = true; // Ngăn ScrollViewer ngoài cùng cuộn trang khi chạm vào ảnh
                }
                else if (activePointers.Count >= 2 && DataContext is MainViewModel vm)
                {
                    // 2 ngón tay trên Android / Màn hình cảm ứng: Kích hoạt Pinch-to-Zoom mượt mà
                    isDragging = false;
                    isPinching = true;
                    var pts = activePointers.Values.Take(2).ToArray();
                    initialPinchDistance = Math.Max(10.0, GetDistance(pts[0], pts[1]));
                    initialZoom = vm.EnhancePreviewZoom;
                    ev.Handled = true; // Chặn cử chỉ cuộn trang ngoài cùng
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

                if (isPinching && activePointers.Count >= 2 && DataContext is MainViewModel vm)
                {
                    // Đang dùng 2 ngón tay để zoom (Pinch Gesture)
                    var pts = activePointers.Values.Take(2).ToArray();
                    double curDistance = GetDistance(pts[0], pts[1]);
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
                    ev.Handled = true; // Ngăn chặn ScrollViewer ngoài cùng cuộn dọc
                }
                else if (isDragging)
                {
                    // Đang dùng 1 ngón tay / chuột để kéo rê ảnh (Pan Drag)
                    var delta = dragStartPoint - curPos;
                    var newOffset = new Vector(
                        Math.Max(0, dragStartOffset.X + delta.X),
                        Math.Max(0, dragStartOffset.Y + delta.Y)
                    );
                    isSyncing = true;
                    before.Offset = newOffset;
                    after.Offset = newOffset;
                    isSyncing = false;
                    ev.Handled = true; // Khắc phục triệt để lỗi không pan lên xuống được trên Android
                }
            }
        }

        void OnReleased(object? sender, PointerReleasedEventArgs ev)
        {
            activePointers.Remove(ev.Pointer.Id);

            if (activePointers.Count == 1)
            {
                // Khi nhấc 1 ngón tay lên sau khi zoom, ngón còn lại chuyển mượt về chế độ Pan
                isPinching = false;
                isDragging = true;
                dragStartPoint = activePointers.Values.First();
                dragStartOffset = before.Offset;
            }
            else if (activePointers.Count == 0)
            {
                isDragging = false;
                isPinching = false;
                ev.Pointer.Capture(null);
            }
        }

        void OnCaptureLost(object? sender, PointerCaptureLostEventArgs ev)
        {
            activePointers.Clear();
            isDragging = false;
            isPinching = false;
        }

        before.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        before.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        before.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        before.AddHandler(PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Tunnel);

        after.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        after.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        after.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        after.AddHandler(PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Tunnel);

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