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
/// Dịch vụ tự động phóng to thanh cuộn dọc (Vertical ScrollBar) trong 3 giây khi có bất kỳ
/// thao tác cuộn nào (lăn chuột Windows/Linux, vuốt chạm ngón tay Android, kéo Thumb, click track, phím bấm).
/// Áp dụng cho mọi tab (trừ trình đọc truyện Reader).
/// </summary>
public static class ScrollBarAutoExpandService
{
    private static readonly HashSet<ScrollBar> _activeExpandedBars = new();
    private static readonly HashSet<ScrollBar> _hoveredBars = new();
    private static readonly HashSet<ScrollBar> _hookedBars = new();
    private static CancellationTokenSource? _globalCollapseCts;
    private static readonly object _syncLock = new();
    private static DateTime _lastTreeScanUtc = DateTime.MinValue;

    /// <summary>
    /// Kiểm tra xem một control có nằm trong trình đọc truyện ComicReader hay không.
    /// Nếu nằm trong Reader, tuyệt đối không can thiệp phóng to để tránh che khuất trang truyện.
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
    /// Kích hoạt phóng to toàn bộ ScrollBar dọc đang hiển thị trong giao diện (trừ Reader) trong 3 giây.
    /// Kể cả nội dung có cuộn hay không (ở đầu trang/cuối trang), khi có thao tác cuộn vẫn lập tức phóng to!
    /// </summary>
    public static void TriggerGlobalScrollExpand(Visual? rootVisual)
    {
        if (rootVisual == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var now = DateTime.UtcNow;
                List<ScrollBar> verticalBars;

                lock (_syncLock)
                {
                    if ((now - _lastTreeScanUtc).TotalMilliseconds > 350 || _activeExpandedBars.Count == 0)
                    {
                        verticalBars = rootVisual.GetVisualDescendants()
                            .OfType<ScrollBar>()
                            .Where(sb => sb.Orientation == Orientation.Vertical && !IsInsideReader(sb))
                            .ToList();
                        _lastTreeScanUtc = now;
                    }
                    else
                    {
                        verticalBars = _activeExpandedBars.Where(sb => !IsInsideReader(sb)).ToList();
                    }

                    if (verticalBars.Count == 0) return;

                    foreach (var bar in verticalBars)
                    {
                        bar.Classes.Set("scroll-active", true);
                        if (bar.TemplatedParent is ScrollViewer sv)
                        {
                            sv.Classes.Set("scroll-active", true);
                        }

                        _activeExpandedBars.Add(bar);
                        HookScrollBarPointerEvents(bar);
                    }

                    // Reset bộ đếm 3.0 giây
                    _globalCollapseCts?.Cancel();
                    _globalCollapseCts?.Dispose();
                    _globalCollapseCts = new CancellationTokenSource();
                    var token = _globalCollapseCts.Token;

                    Task.Delay(3000, token).ContinueWith(t =>
                    {
                        if (t.IsCanceled) return;
                        Dispatcher.UIThread.Post(() =>
                        {
                            CollapseInactiveBars();
                        });
                    }, TaskScheduler.Default);
                }
            }
            catch { }
        }, DispatcherPriority.Normal);
    }

    public static void HookScrollBar(ScrollBar bar) => HookScrollBarPointerEvents(bar);
    private static void HookScrollBarPointerEvents(ScrollBar bar)
    {
        if (!_hookedBars.Add(bar)) return;

        bool isDraggingBar = false;

        void UpdateScrollFromPointer(Point pos)
        {
            double trackHeight = bar.Bounds.Height;
            if (trackHeight <= 0) return;

            double thumbHeight = 56.0;
            double thumbHalfHeight = thumbHeight / 2.0;
            double availableHeight = Math.Max(1.0, trackHeight - thumbHeight);
            double relativeY = Math.Clamp(pos.Y - thumbHalfHeight, 0, availableHeight);
            double ratio = relativeY / availableHeight;
            double targetValue = bar.Minimum + ratio * (bar.Maximum - bar.Minimum);

            System.Diagnostics.Debug.WriteLine($"[UpdateScrollFromPointer] pos.Y={pos.Y}, relativeY={relativeY}, ratio={ratio}, targetValue={targetValue}");
            Console.WriteLine($"[UpdateScrollFromPointer] pos.Y={pos.Y:F1}, relativeY={relativeY:F1}, ratio={ratio:F3}, targetValue={targetValue:F1}");

            bar.SetCurrentValue(ScrollBar.ValueProperty, targetValue);
            var sv = bar.TemplatedParent as ScrollViewer ?? bar.FindAncestorOfType<ScrollViewer>();
            if (sv != null)
            {
                sv.SetCurrentValue(ScrollViewer.OffsetProperty, sv.Offset.WithY(targetValue));
            }
        }

        bar.PointerEntered += (s, e) =>
        {
            lock (_syncLock)
            {
                _hoveredBars.Add(bar);
            }
        };

        bar.PointerExited += (s, e) =>
        {
            lock (_syncLock)
            {
                if (!isDraggingBar)
                {
                    _hoveredBars.Remove(bar);
                    ScheduleDelayedCollapse();
                }
            }
        };

        // Khi người dùng click hoặc tap ở bất kỳ vị trí nào trên thanh cuộn (dù trúng slider hay lệch ra ngoài):
        // 1. Chặn triệt để lỗi tự cuộn về bottom của RepeatButton (bằng RoutingStrategies.Tunnel & e.Handled = true)
        // 2. Di chuyển slider ngay đến vị trí click/tap/hold đó
        // 3. Tự neo (capture) con trỏ/ngón tay để khi di chuyển lên xuống thì slider di chuyển theo
        // 4. Khi thả tay/click ra thì ngưng di chuyển
        bar.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            var prop = e.GetCurrentPoint(bar).Properties;
            if (prop.IsLeftButtonPressed || e.Pointer.Type == PointerType.Touch || e.Pointer.Type == PointerType.Pen)
            {
                var pos = e.GetPosition(bar);

                isDraggingBar = true;
                e.Pointer.Capture(bar);
                e.PreventGestureRecognition();

                lock (_syncLock)
                {
                    _hoveredBars.Add(bar);
                    bar.Classes.Set("scroll-active", true);
                    if (bar.TemplatedParent is ScrollViewer sv)
                    {
                        sv.Classes.Set("scroll-active", true);
                    }
                    _activeExpandedBars.Add(bar);
                }

                // Di chuyển slider đến vị trí hold ngay lập tức (không bị nhảy về bottom)
                UpdateScrollFromPointer(pos);
                e.Handled = true; // Chặn hành vi PageDown/cuộn về bottom mặc định của Avalonia
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        bar.AddHandler(InputElement.PointerMovedEvent, (s, e) =>
        {
            if (isDraggingBar)
            {
                UpdateScrollFromPointer(e.GetPosition(bar));
                e.Handled = true;
            }
        }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);

        void EndBarDrag(Avalonia.Interactivity.RoutedEventArgs? e = null)
        {
            if (isDraggingBar)
            {
                isDraggingBar = false;
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
            EndBarDrag(e);
        }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);

        bar.AddHandler(InputElement.PointerCaptureLostEvent, (s, e) =>
        {
            EndBarDrag(e);
        }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static void ScheduleDelayedCollapse()
    {
        lock (_syncLock)
        {
            _globalCollapseCts?.Cancel();
            _globalCollapseCts?.Dispose();
            _globalCollapseCts = new CancellationTokenSource();
            var token = _globalCollapseCts.Token;

            Task.Delay(3000, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                Dispatcher.UIThread.Post(() =>
                {
                    CollapseInactiveBars();
                });
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
                // Nếu chuột vẫn đang hover hoặc nắm kéo thumb, không thu nhỏ
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
    /// Tự động đăng ký toàn bộ sự kiện cuộn (PointerWheel, Touch swipe, ScrollChanged) lên root control.
    /// </summary>
    public static void AttachGlobalScrollListeners(Control rootControl)
    {
        if (rootControl == null) return;

        rootControl.Loaded += (s, e) =>
        {
            var bars = rootControl.GetVisualDescendants()
                .OfType<ScrollBar>()
                .Where(sb => sb.Orientation == Orientation.Vertical && !IsInsideReader(sb));
            foreach (var b in bars)
            {
                HookScrollBarPointerEvents(b);
            }
        };

        // 1. Bắt thao tác lăn chuột trên Windows & Linux (kể cả khi nội dung không thể cuộn thêm)
        rootControl.AddHandler(InputElement.PointerWheelChangedEvent, (s, e) =>
        {
            TriggerGlobalScrollExpand(rootControl);
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        // 2. Bắt sự kiện cuộn từ mọi ScrollViewer trong ứng dụng
        rootControl.AddHandler(ScrollViewer.ScrollChangedEvent, (s, e) =>
        {
            TriggerGlobalScrollExpand(rootControl);
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);



        // 4. Bắt cử chỉ vuốt chạm ngón tay ở giữa màn hình hoặc bất kỳ đâu trên Android
        Point? touchStart = null;
        bool isTouchMoving = false;

        void OnTouchDown(Point pos)
        {
            touchStart = pos;
            isTouchMoving = true;
        }

        void OnTouchMove(Point curPos)
        {
            if (isTouchMoving && touchStart.HasValue)
            {
                if (Math.Abs(curPos.Y - touchStart.Value.Y) > 3 || Math.Abs(curPos.X - touchStart.Value.X) > 3)
                {
                    TriggerGlobalScrollExpand(rootControl);
                }
            }
        }

        void OnTouchUp()
        {
            isTouchMoving = false;
            touchStart = null;
        }

        rootControl.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            OnTouchDown(e.GetPosition(rootControl));
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        rootControl.AddHandler(InputElement.PointerMovedEvent, (s, e) =>
        {
            OnTouchMove(e.GetPosition(rootControl));
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        rootControl.AddHandler(InputElement.PointerReleasedEvent, (s, e) =>
        {
            OnTouchUp();
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        rootControl.AddHandler(InputElement.PointerCaptureLostEvent, (s, e) =>
        {
            OnTouchUp();
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        Avalonia.Interactivity.Interactive? topLevel = (Interactive?)TopLevel.GetTopLevel(rootControl);
        if (topLevel != null && topLevel != rootControl)
        {
            topLevel.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
            {
                OnTouchDown(e.GetPosition(rootControl));
            }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

            topLevel.AddHandler(InputElement.PointerMovedEvent, (s, e) =>
            {
                OnTouchMove(e.GetPosition(rootControl));
            }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

            topLevel.AddHandler(InputElement.PointerReleasedEvent, (s, e) =>
            {
                OnTouchUp();
            }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);


        }
    }

    /// <summary>
    /// Duyệt Visual Tree và tự động đăng ký (giữ tương thích với code gọi cũ)
    /// </summary>
    public static void RegisterAllScrollViewers(Visual? container)
    {
        if (container == null) return;
        TriggerGlobalScrollExpand(container);
    }
}
