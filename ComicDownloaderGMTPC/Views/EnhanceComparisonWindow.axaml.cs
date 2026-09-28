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

public partial class EnhanceComparisonWindow : Window
{
    public EnhanceComparisonWindow()
    {
        InitializeComponent();

        // 1. Dual View (Song song Before / After): Pan & 2-finger Pinch-to-Zoom
        SetupPanAndZoomGesture(BeforeScrollViewer, AfterScrollViewer);

        // 2. Split View (Rèm trượt): Kéo rèm trượt trực tiếp trên ảnh + Pan/Zoom 2 ngón/chuột phải
        SetupSplitCurtainInteractive(SplitScrollViewer, ComparisonSplitPanel);

        // 3. Single View (Ảnh đơn): Pan & Zoom tự do
        SetupPanAndZoomGesture(SingleScrollViewer);

        // Tự động nhận diện Landscape (width > height) vs Portrait (width < height)
        SizeChanged += (s, e) =>
        {
            if (DataContext is MainViewModel vm && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                vm.IsPortraitMode = e.NewSize.Width < e.NewSize.Height;
            }
        };
    }

    /// <summary>
    /// Bộ điều khiển tương tác Rèm Trượt (Split View):
    /// - Chuột trái / 1 ngón chạm: Kéo rèm chia Before / After tự do 120 FPS trực tiếp trên ảnh.
    /// - Chuột phải / chuột giữa / 2 ngón chạm: Pan góc nhìn & Pinch-to-zoom khi phóng to ảnh.
    /// - Cuộn chuột: Phóng to / Thu nhỏ.
    /// </summary>
    private void SetupSplitCurtainInteractive(ScrollViewer? viewer, Panel? splitPanel)
    {
        if (viewer == null || splitPanel == null) return;

        bool isDraggingCurtain = false;
        bool isPanning = false;
        bool isPinching = false;
        Point panStartPoint = default;
        Vector panStartOffset = default;
        Point initialPinchMidPoint = default;
        Vector initialPinchOffset = default;
        double initialPinchDistance = 0;
        double initialZoom = 1.0;

        var activePointers = new Dictionary<long, Point>();

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

        void UpdateSplitPosition(Point localPos)
        {
            if (DataContext is not MainViewModel vm) return;

            double w = splitPanel.Bounds.Width > 0 ? splitPanel.Bounds.Width : vm.EnhanceImagePixelWidth;
            double h = splitPanel.Bounds.Height > 0 ? splitPanel.Bounds.Height : vm.EnhanceImagePixelHeight;
            if (w <= 0 || h <= 0) return;

            double ratio = vm.IsSplitVerticalOrientation
                ? (localPos.Y / h)
                : (localPos.X / w);

            vm.EnhanceSplitRatio = Math.Clamp(ratio, 0.01, 0.99);
        }

        // Tương tác kéo rèm trực tiếp trên Panel ảnh
        splitPanel.AddHandler(InputElement.PointerPressedEvent, (s, ev) =>
        {
            if (DataContext is MainViewModel vm && vm.IsSplitView)
            {
                var pt = ev.GetCurrentPoint(splitPanel);
                if (pt.Properties.IsLeftButtonPressed || ev.Pointer.Type == PointerType.Touch)
                {
                    isDraggingCurtain = true;
                    UpdateSplitPosition(pt.Position);
                    ev.Pointer.Capture(splitPanel);
                    ev.Handled = true;
                }
            }
        }, RoutingStrategies.Tunnel);

        splitPanel.AddHandler(InputElement.PointerMovedEvent, (s, ev) =>
        {
            if (isDraggingCurtain && DataContext is MainViewModel vm && vm.IsSplitView)
            {
                var pt = ev.GetCurrentPoint(splitPanel);
                UpdateSplitPosition(pt.Position);
                ev.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        splitPanel.AddHandler(InputElement.PointerReleasedEvent, (s, ev) =>
        {
            if (isDraggingCurtain)
            {
                isDraggingCurtain = false;
                ev.Pointer.Capture(null);
                ev.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        splitPanel.AddHandler(InputElement.PointerCaptureLostEvent, (s, ev) =>
        {
            isDraggingCurtain = false;
        }, RoutingStrategies.Tunnel);

        // Tương tác Pan & Zoom khi dùng chuột phải, chuột giữa hoặc 2 ngón tay trên ScrollViewer
        viewer.AddHandler(InputElement.PointerPressedEvent, (s, ev) =>
        {
            if (IsScrollBarElement(ev.Source, viewer)) return;
            if (isDraggingCurtain) return;

            var pt = ev.GetCurrentPoint(viewer);
            if (pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed || ev.Pointer.Type == PointerType.Touch)
            {
                if (ev.Pointer.Type == PointerType.Mouse) activePointers.Clear();
                activePointers[ev.Pointer.Id] = pt.Position;

                if (activePointers.Count == 1 && (pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed))
                {
                    isPanning = true;
                    isPinching = false;
                    panStartPoint = pt.Position;
                    panStartOffset = viewer.Offset;
                    if (ev.Pointer.Type == PointerType.Mouse) ev.Pointer.Capture(viewer);
                    ev.Handled = true;
                }
                else if (activePointers.Count >= 2 && DataContext is MainViewModel vm)
                {
                    isPanning = true;
                    isPinching = true;
                    isDraggingCurtain = false;
                    var pts = activePointers.Values.Take(2).ToArray();
                    initialPinchDistance = Math.Max(10.0, GetDistance(pts[0], pts[1]));
                    initialPinchMidPoint = new Point((pts[0].X + pts[1].X) / 2.0, (pts[0].Y + pts[1].Y) / 2.0);
                    initialPinchOffset = viewer.Offset;
                    initialZoom = vm.EnhancePreviewZoom;
                    try { ev.Pointer.Capture(null); } catch { }
                    ev.Handled = true;
                }
            }
        }, RoutingStrategies.Tunnel);

        viewer.AddHandler(InputElement.PointerMovedEvent, (s, ev) =>
        {
            if (IsScrollBarElement(ev.Source, viewer)) return;
            if (isDraggingCurtain) return;

            if (activePointers.ContainsKey(ev.Pointer.Id))
            {
                var curPos = ev.GetCurrentPoint(viewer).Position;
                activePointers[ev.Pointer.Id] = curPos;

                if (activePointers.Count >= 2 && DataContext is MainViewModel vm)
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
                        initialPinchOffset = viewer.Offset;
                        initialZoom = vm.EnhancePreviewZoom;
                    }

                    if (initialPinchDistance > 5)
                    {
                        double scaleFactor = curDistance / initialPinchDistance;
                        double targetZoom = Math.Clamp(Math.Round(initialZoom * scaleFactor, 2), 0.25, 5.0);
                        if (Math.Abs(targetZoom - vm.EnhancePreviewZoom) >= 0.01)
                        {
                            vm.EnhancePreviewZoom = targetZoom;
                        }
                    }

                    var midDelta = initialPinchMidPoint - curMidPoint;
                    double maxOffsetX = Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width);
                    double maxOffsetY = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
                    double newX = Math.Clamp(initialPinchOffset.X + midDelta.X, 0, maxOffsetX);
                    double newY = Math.Clamp(initialPinchOffset.Y + midDelta.Y, 0, maxOffsetY);
                    viewer.Offset = new Vector(newX, newY);
                    ev.Handled = true;
                }
                else if (isPanning && !isPinching)
                {
                    var delta = panStartPoint - curPos;
                    double maxOffsetX = Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width);
                    double maxOffsetY = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
                    double newX = Math.Clamp(panStartOffset.X + delta.X, 0, maxOffsetX);
                    double newY = Math.Clamp(panStartOffset.Y + delta.Y, 0, maxOffsetY);
                    viewer.Offset = new Vector(newX, newY);
                    ev.Handled = true;
                }
            }
        }, RoutingStrategies.Tunnel);

        viewer.AddHandler(InputElement.PointerReleasedEvent, (s, ev) =>
        {
            activePointers.Remove(ev.Pointer.Id);
            if (activePointers.Count == 0)
            {
                isPanning = false;
                isPinching = false;
                try { ev.Pointer.Capture(null); } catch { }
            }
        }, RoutingStrategies.Tunnel);

        viewer.AddHandler(InputElement.PointerCaptureLostEvent, (s, ev) =>
        {
            activePointers.Remove(ev.Pointer.Id);
            if (activePointers.Count == 0)
            {
                isPanning = false;
                isPinching = false;
            }
        }, RoutingStrategies.Tunnel);

        viewer.AddHandler(InputElement.PointerWheelChangedEvent, (s, ev) =>
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
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Bộ điều khiển Pan (kéo rê góc nhìn) và Zoom cho Dual View & Single View:
    /// - Chuột trái / 1 ngón chạm / chuột phải / chuột giữa: Kéo rê (Pan) góc nhìn bức ảnh mượt mà 2 chiều.
    /// - 2 ngón tay chạm (Pinch-to-zoom): Phóng to / Thu nhỏ & Pan đồng thời.
    /// - Cuộn chuột: Phóng to / Thu nhỏ.
    /// </summary>
    private void SetupPanAndZoomGesture(params ScrollViewer?[] viewers)
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

        var activePointers = new Dictionary<long, Point>();

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
            if (p.Properties.IsLeftButtonPressed || p.Properties.IsMiddleButtonPressed || p.Properties.IsRightButtonPressed || ev.Pointer.Type == PointerType.Touch)
            {
                if (ev.Pointer.Type == PointerType.Mouse) activePointers.Clear();
                activePointers[ev.Pointer.Id] = p.Position;

                if (activePointers.Count == 1)
                {
                    isDragging = true;
                    isPinching = false;
                    dragStartPoint = p.Position;
                    dragStartOffset = currentViewer.Offset;

                    if (ev.Pointer.Type == PointerType.Mouse)
                    {
                        ev.Pointer.Capture(currentViewer);
                    }
                    ev.Handled = true;
                }
                else if (activePointers.Count >= 2 && DataContext is MainViewModel vm)
                {
                    isDragging = true;
                    isPinching = true;
                    var pts = activePointers.Values.Take(2).ToArray();
                    initialPinchDistance = Math.Max(10.0, GetDistance(pts[0], pts[1]));
                    initialPinchMidPoint = new Point((pts[0].X + pts[1].X) / 2.0, (pts[0].Y + pts[1].Y) / 2.0);
                    initialPinchOffset = currentViewer.Offset;
                    initialZoom = vm.EnhancePreviewZoom;

                    try { ev.Pointer.Capture(null); } catch { }
                    ev.Handled = true;
                }
            }
        }

        void OnMoved(object? sender, PointerEventArgs ev)
        {
            var currentViewer = sender as ScrollViewer ?? validViewers[0];
            if (IsScrollBarElement(ev.Source, currentViewer)) return;

            if (activePointers.ContainsKey(ev.Pointer.Id))
            {
                var curPos = ev.GetCurrentPoint(currentViewer).Position;
                activePointers[ev.Pointer.Id] = curPos;

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
                        initialPinchOffset = currentViewer.Offset;
                        initialZoom = vm.EnhancePreviewZoom;
                    }

                    if (initialPinchDistance > 5)
                    {
                        double scaleFactor = curDistance / initialPinchDistance;
                        double targetZoom = Math.Clamp(Math.Round(initialZoom * scaleFactor, 2), 0.25, 5.0);
                        if (Math.Abs(targetZoom - vm.EnhancePreviewZoom) >= 0.01)
                        {
                            vm.EnhancePreviewZoom = targetZoom;
                        }
                    }

                    var midDelta = initialPinchMidPoint - curMidPoint;
                    double maxOffsetX = Math.Max(0, currentViewer.Extent.Width - currentViewer.Viewport.Width);
                    double maxOffsetY = Math.Max(0, currentViewer.Extent.Height - currentViewer.Viewport.Height);
                    double newX = Math.Clamp(initialPinchOffset.X + midDelta.X, 0, maxOffsetX);
                    double newY = Math.Clamp(initialPinchOffset.Y + midDelta.Y, 0, maxOffsetY);
                    var newOffset = new Vector(newX, newY);

                    isSyncing = true;
                    foreach (var v in validViewers) v.Offset = newOffset;
                    isSyncing = false;

                    ev.Handled = true;
                }
                else if (isDragging && !isPinching)
                {
                    var delta = dragStartPoint - curPos;
                    double maxOffsetX = Math.Max(0, currentViewer.Extent.Width - currentViewer.Viewport.Width);
                    double maxOffsetY = Math.Max(0, currentViewer.Extent.Height - currentViewer.Viewport.Height);
                    double newX = Math.Clamp(dragStartOffset.X + delta.X, 0, maxOffsetX);
                    double newY = Math.Clamp(dragStartOffset.Y + delta.Y, 0, maxOffsetY);
                    var newOffset = new Vector(newX, newY);

                    isSyncing = true;
                    foreach (var v in validViewers) v.Offset = newOffset;
                    isSyncing = false;

                    ev.Handled = true;
                }
            }
        }

        void OnReleased(object? sender, PointerReleasedEventArgs ev)
        {
            activePointers.Remove(ev.Pointer.Id);
            var currentViewer = sender as ScrollViewer ?? validViewers[0];

            if (activePointers.Count == 1)
            {
                isPinching = false;
                isDragging = true;
                dragStartPoint = activePointers.Values.First();
                dragStartOffset = currentViewer.Offset;
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
