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
                            .Where(sb => sb.Orientation == Orientation.Vertical && sb.IsVisible && !IsInsideReader(sb))
                            .ToList();
                        _lastTreeScanUtc = now;
                    }
                    else
                    {
                        verticalBars = _activeExpandedBars.Where(sb => sb.IsVisible && !IsInsideReader(sb)).ToList();
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

    private static void HookScrollBarPointerEvents(ScrollBar bar)
    {
        if (!_hookedBars.Add(bar)) return;

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
                _hoveredBars.Remove(bar);
            }
            ScheduleDelayedCollapse();
        };

        bar.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            lock (_syncLock)
            {
                _hoveredBars.Add(bar);
            }
        }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel);

        bar.AddHandler(InputElement.PointerReleasedEvent, (s, e) =>
        {
            lock (_syncLock)
            {
                if (!bar.IsPointerOver)
                {
                    _hoveredBars.Remove(bar);
                }
            }
            ScheduleDelayedCollapse();
        }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel);
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

        // 1. Bắt thao tác lăn chuột trên Windows & Linux (kể cả khi nội dung không thể cuộn thêm)
        rootControl.AddHandler(InputElement.PointerWheelChangedEvent, (s, e) =>
        {
            TriggerGlobalScrollExpand(rootControl);
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        // 2. Bắt sự kiện cuộn từ mọi ScrollViewer trong ứng dụng
        rootControl.AddHandler(ScrollViewer.ScrollChangedEvent, (s, e) =>
        {
            if (Math.Abs(e.OffsetDelta.Y) > 0.001 || Math.Abs(e.OffsetDelta.X) > 0.001)
            {
                TriggerGlobalScrollExpand(rootControl);
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        // 3. Bắt cử chỉ vuốt chạm cảm ứng (Touch swipe trên Android)
        Point? touchStart = null;
        bool isTouchMoving = false;

        rootControl.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            var pt = e.GetCurrentPoint(rootControl);
            if (pt.Properties.IsLeftButtonPressed)
            {
                touchStart = pt.Position;
                isTouchMoving = true;
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        rootControl.AddHandler(InputElement.PointerMovedEvent, (s, e) =>
        {
            if (isTouchMoving && touchStart.HasValue)
            {
                var cur = e.GetCurrentPoint(rootControl).Position;
                if (Math.Abs(cur.Y - touchStart.Value.Y) > 6)
                {
                    TriggerGlobalScrollExpand(rootControl);
                }
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        rootControl.AddHandler(InputElement.PointerReleasedEvent, (s, e) =>
        {
            isTouchMoving = false;
            touchStart = null;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
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
