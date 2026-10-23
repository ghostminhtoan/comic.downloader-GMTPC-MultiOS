using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Dịch vụ tối ưu Vertical ScrollBar đa nền tảng (Windows, Linux, Android):
/// 1. Bỏ triệt để lỗi bấm lệch tự cuộn xuống bottom của RepeatButton (nhảy ngay đến vị trí click).
/// 2. Bấm giữ ở vị trí bất kỳ trên thanh cuộn (Hold & Drag) tự neo con trượt và di chuyển mượt mà theo tay/chuột.
/// 3. Giữ nguyên 100% cơ chế kéo thả native tự nhiên của Thumb (không can thiệp Tunnel).
/// 4. Tự động nở rộng núm trượt bán nguyệt khi cuộn và tự mờ dần sau 2.8 giây.
/// 5. Miễn nhiễm và bảo vệ an toàn tuyệt đối cho Reader / ComicReader.
/// </summary>
public static class ScrollBarAutoExpandService
{
    private static readonly HashSet<ScrollBar> _registeredBars = new();
    private static readonly HashSet<ScrollBar> _activeExpandedBars = new();
    private static readonly HashSet<ScrollBar> _hoveredBars = new();
    private static CancellationTokenSource? _globalCollapseCts;
    private static readonly object _syncLock = new();

    /// <summary>
    /// Kiểm tra xem một control có nằm trong trình đọc truyện ComicReader hay không.
    /// Nếu nằm trong Reader, tuyệt đối không can thiệp.
    /// </summary>
    public static bool IsInsideReader(Visual visual)
    {
        var current = visual;
        while (current != null)
        {
            var typeName = current.GetType().Name;
            if (typeName.Contains("ComicReader", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("ReaderControl", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("ReaderWindow", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            current = current.GetVisualParent();
        }
        return false;
    }

    /// <summary>
    /// Kích hoạt hiển thị rõ nét toàn bộ ScrollBar dọc đang active (trừ Reader) trong 2.8 giây.
    /// </summary>
    public static void TriggerGlobalScrollExpand(Visual? rootVisual)
    {
        if (rootVisual == null) return;
        ThermalAndPowerOptimizationService.Instance.NotifyUserActivity();

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                lock (_syncLock)
                {
                    // Nếu chưa có bar nào đăng ký, quét nhẹ để tìm
                    if (_registeredBars.Count == 0)
                    {
                        var foundBars = rootVisual.GetVisualDescendants()
                            .OfType<ScrollBar>()
                            .Where(sb => sb.Orientation == Orientation.Vertical && !IsInsideReader(sb));
                        foreach (var b in foundBars)
                        {
                            HookScrollBar(b);
                        }
                    }

                    foreach (var bar in _registeredBars)
                    {
                        if (IsInsideReader(bar)) continue;

                        bar.Classes.Set("scroll-active", true);
                        if (bar.TemplatedParent is ScrollViewer sv)
                        {
                            sv.Classes.Set("scroll-active", true);
                        }
                        _activeExpandedBars.Add(bar);
                    }

                    ScheduleDelayedCollapse();
                }
            }
            catch { }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Đăng ký và thiết lập xử lý tương tác chuẩn cho từng ScrollBar
    /// </summary>
    public static void HookScrollBar(ScrollBar bar)
    {
        if (bar == null || IsInsideReader(bar)) return;

        lock (_syncLock)
        {
            if (!_registeredBars.Add(bar)) return;
        }

        bool isDraggingTrack = false;

        void UpdateScrollToPoint(Point pos)
        {
            double barHeight = bar.Bounds.Height;
            if (barHeight <= 0) return;

            // Tìm chiều cao thật của Thumb trong template, không hardcode
            var thumb = bar.FindDescendantOfType<Thumb>();
            double thumbHeight = thumb != null && thumb.Bounds.Height > 0 ? thumb.Bounds.Height : 36.0;

            double availableHeight = Math.Max(1.0, barHeight - thumbHeight);
            double targetThumbTop = Math.Clamp(pos.Y - (thumbHeight / 2.0), 0.0, availableHeight);
            double ratio = targetThumbTop / availableHeight;
            double targetValue = bar.Minimum + ratio * (bar.Maximum - bar.Minimum);

            bar.SetCurrentValue(ScrollBar.ValueProperty, targetValue);

            var sv = bar.TemplatedParent as ScrollViewer ?? bar.FindAncestorOfType<ScrollViewer>();
            if (sv != null)
            {
                sv.SetCurrentValue(ScrollViewer.OffsetProperty, sv.Offset.WithY(targetValue));
            }
        }

        // Hover events
        bar.PointerEntered += (s, e) =>
        {
            lock (_syncLock)
            {
                _hoveredBars.Add(bar);
                bar.Classes.Set("scroll-active", true);
            }
        };

        bar.PointerExited += (s, e) =>
        {
            lock (_syncLock)
            {
                if (!isDraggingTrack)
                {
                    _hoveredBars.Remove(bar);
                    ScheduleDelayedCollapse();
                }
            }
        };

        // BẮT SỰ KIỆN POINTER PRESSED THEO LUỒNG BUBBLE TỰ NHIÊN (KHÔNG DÙNG TUNNEL)
        bar.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            var prop = e.GetCurrentPoint(bar).Properties;
            if (!prop.IsLeftButtonPressed && e.Pointer.Type != PointerType.Touch && e.Pointer.Type != PointerType.Pen)
                return;

            // Kiểm tra xem vị trí click có trúng vào chính Thumb hay không
            var sourceVisual = e.Source as Visual;
            bool isDirectThumbHit = false;
            var cur = sourceVisual;
            while (cur != null && cur != bar)
            {
                if (cur is Thumb)
                {
                    isDirectThumbHit = true;
                    break;
                }
                cur = cur.GetVisualParent();
            }

            // NẾU CLICK VÀO CHÍNH THUMB:
            // Để Thumb tự xử lý kéo thả (DragDelta) native 100% của Avalonia! Không can thiệp!
            if (isDirectThumbHit)
            {
                lock (_syncLock)
                {
                    _hoveredBars.Add(bar);
                    bar.Classes.Set("scroll-active", true);
                    _activeExpandedBars.Add(bar);
                }
                return;
            }

            // NẾU CLICK VÀO KHOẢNG TRỐNG (TRACK / NGOÀI THUMB):
            // 1. Nhảy con trượt ngay đến vị trí click (chặn triệt để lỗi RepeatButton cuộn về bottom)
            // 2. Tự neo (capture) con trỏ để khi người dùng hold và di chuyển lên xuống thì con trượt di chuyển theo
            isDraggingTrack = true;
            e.Pointer.Capture(bar);
            e.PreventGestureRecognition();

            lock (_syncLock)
            {
                _hoveredBars.Add(bar);
                bar.Classes.Set("scroll-active", true);
                _activeExpandedBars.Add(bar);
            }

            UpdateScrollToPoint(e.GetPosition(bar));
            e.Handled = true; // Chặn RepeatButton phát lệnh PageDown về bottom
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        // KHI DI CHUYỂN CON TRỎ (POINTER MOVED)
        bar.AddHandler(InputElement.PointerMovedEvent, (s, e) =>
        {
            if (isDraggingTrack)
            {
                UpdateScrollToPoint(e.GetPosition(bar));
                e.Handled = true;
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        // KHI THẢ TAY / THẢ CHUỘT (RELEASED HOẶC CAPTURE LOST)
        void EndTrackDrag(RoutedEventArgs? e = null)
        {
            if (isDraggingTrack)
            {
                isDraggingTrack = false;
                if (e is PointerEventArgs pe)
                {
                    pe.Pointer.Capture(null);
                }
                lock (_syncLock)
                {
                    _hoveredBars.Remove(bar);
                }
                ScheduleDelayedCollapse();
                if (e != null) e.Handled = true;
            }
        }

        bar.AddHandler(InputElement.PointerReleasedEvent, (s, e) =>
        {
            EndTrackDrag(e);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        bar.AddHandler(InputElement.PointerCaptureLostEvent, (s, e) =>
        {
            EndTrackDrag(e);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        // Dọn dẹp khi ScrollBar bị gỡ khỏi Visual Tree
        bar.DetachedFromVisualTree += (s, e) =>
        {
            lock (_syncLock)
            {
                _registeredBars.Remove(bar);
                _activeExpandedBars.Remove(bar);
                _hoveredBars.Remove(bar);
            }
        };
    }

    private static void ScheduleDelayedCollapse()
    {
        lock (_syncLock)
        {
            _globalCollapseCts?.Cancel();
            _globalCollapseCts?.Dispose();
            _globalCollapseCts = new CancellationTokenSource();
            var token = _globalCollapseCts.Token;

            Task.Delay(2800, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                Dispatcher.UIThread.Post(() =>
                {
                    CollapseInactiveBars();
                }, DispatcherPriority.Background);
            }, TaskScheduler.Default);
        }
    }

    private static void CollapseInactiveBars()
    {
        lock (_syncLock)
        {
            var barsToDeactivate = new List<ScrollBar>();
            foreach (var bar in _activeExpandedBars)
            {
                // Nếu chuột vẫn đang hover hoặc thumb đang được kéo, giữ nguyên
                if (_hoveredBars.Contains(bar) || bar.IsPointerOver)
                {
                    continue;
                }

                bar.Classes.Set("scroll-active", false);
                if (bar.TemplatedParent is ScrollViewer sv)
                {
                    sv.Classes.Set("scroll-active", false);
                }
                barsToDeactivate.Add(bar);
            }

            foreach (var b in barsToDeactivate)
            {
                _activeExpandedBars.Remove(b);
            }
        }
    }

    /// <summary>
    /// Đăng ký toàn bộ ScrollBar trong visual tree của rootControl
    /// </summary>
    public static void AttachGlobalScrollListeners(Control rootControl)
    {
        if (rootControl == null) return;

        void RegisterBars()
        {
            var bars = rootControl.GetVisualDescendants()
                .OfType<ScrollBar>()
                .Where(sb => sb.Orientation == Orientation.Vertical && !IsInsideReader(sb));
            foreach (var b in bars)
            {
                HookScrollBar(b);
            }
        }

        rootControl.Loaded += (s, e) => RegisterBars();

        // Lăn chuột trên Windows/Linux
        rootControl.AddHandler(InputElement.PointerWheelChangedEvent, (s, e) =>
        {
            TriggerGlobalScrollExpand(rootControl);
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        // Cuộn nội dung từ ScrollViewer
        rootControl.AddHandler(ScrollViewer.ScrollChangedEvent, (s, e) =>
        {
            TriggerGlobalScrollExpand(rootControl);
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// Giữ tương thích với các điểm gọi cũ
    /// </summary>
    public static void RegisterAllScrollViewers(Visual? container)
    {
        if (container == null) return;
        TriggerGlobalScrollExpand(container);
    }
}
