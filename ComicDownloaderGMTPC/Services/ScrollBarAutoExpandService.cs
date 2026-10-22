using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
/// thao tác cuộn nào (lăn chuột, vuốt chạm ngón tay Android, kéo Thumb, click track, phím bấm).
/// Hỗ trợ mượt mà trên cả Windows, Linux và Android.
/// </summary>
public static class ScrollBarAutoExpandService
{
    private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> _trackedScrollViewers = new();

    private class ScrollState
    {
        public CancellationTokenSource? Cts;
        public ScrollBar? VerticalScrollBar;
        public bool IsPointerOver = false;
        public bool IsThumbDragging = false;
    }

    /// <summary>
    /// Đăng ký theo dõi một ScrollViewer để kích hoạt phóng to thanh cuộn dọc 3 giây khi cuộn.
    /// </summary>
    public static void RegisterScrollViewer(ScrollViewer? sv)
    {
        if (sv == null) return;

        // Tránh đăng ký trùng lặp trên cùng một ScrollViewer
        if (_trackedScrollViewers.TryGetValue(sv, out _)) return;

        var state = _trackedScrollViewers.GetOrCreateValue(sv);

        // 1. Sự kiện ScrollChanged: Bắn ra khi scroll bằng BẤT KỲ thao tác nào (mouse wheel, touch, thumb drag, keyboard, code)
        sv.ScrollChanged += (s, e) =>
        {
            if (Math.Abs(e.OffsetDelta.Y) > 0.001 || Math.Abs(e.OffsetDelta.X) > 0.001)
            {
                ActivateExpand(sv, state);
            }
        };

        // 2. Sự kiện PointerWheelChanged (lăn bánh xe chuột)
        sv.AddHandler(InputElement.PointerWheelChangedEvent, (s, e) =>
        {
            ActivateExpand(sv, state);
        }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel);

        // 3. Khi ScrollViewer được Loaded vào Visual Tree
        sv.Loaded += (s, e) =>
        {
            FindAndHookScrollBar(sv, state);
        };

        // Tìm ngay nếu control đã nằm trong visual tree
        FindAndHookScrollBar(sv, state);
    }

    private static void FindAndHookScrollBar(ScrollViewer sv, ScrollState state)
    {
        if (state.VerticalScrollBar != null) return;

        try
        {
            var bar = sv.GetVisualDescendants()
                .OfType<ScrollBar>()
                .FirstOrDefault(sb => sb.Orientation == Orientation.Vertical);

            if (bar != null)
            {
                state.VerticalScrollBar = bar;

                bar.PointerEntered += (s, e) =>
                {
                    state.IsPointerOver = true;
                    ActivateExpand(sv, state);
                };

                bar.PointerExited += (s, e) =>
                {
                    state.IsPointerOver = false;
                    if (!state.IsThumbDragging)
                    {
                        ScheduleCollapse(sv, state);
                    }
                };

                bar.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
                {
                    state.IsThumbDragging = true;
                    ActivateExpand(sv, state);
                }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel);

                bar.AddHandler(InputElement.PointerReleasedEvent, (s, e) =>
                {
                    state.IsThumbDragging = false;
                    if (!state.IsPointerOver)
                    {
                        ScheduleCollapse(sv, state);
                    }
                }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel);
            }
        }
        catch { }
    }

    private static void ActivateExpand(ScrollViewer sv, ScrollState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (state.VerticalScrollBar == null)
            {
                FindAndHookScrollBar(sv, state);
            }

            sv.Classes.Set("scroll-active", true);
            if (state.VerticalScrollBar != null)
            {
                state.VerticalScrollBar.Classes.Set("scroll-active", true);
            }

            // Nếu người dùng không rê chuột vào hoặc không nắm kéo Thumb, đếm lùi 3 giây để thu nhỏ lại
            if (!state.IsPointerOver && !state.IsThumbDragging)
            {
                ScheduleCollapse(sv, state);
            }
        });
    }

    private static void ScheduleCollapse(ScrollViewer sv, ScrollState state)
    {
        state.Cts?.Cancel();
        state.Cts?.Dispose();
        state.Cts = new CancellationTokenSource();
        var token = state.Cts.Token;

        Task.Delay(3000, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!state.IsPointerOver && !state.IsThumbDragging)
                {
                    sv.Classes.Set("scroll-active", false);
                    if (state.VerticalScrollBar != null)
                    {
                        state.VerticalScrollBar.Classes.Set("scroll-active", false);
                    }
                }
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Duyệt Visual Tree và tự động đăng ký toàn bộ các ScrollViewer bên trong một container
    /// </summary>
    public static void RegisterAllScrollViewers(Visual? container)
    {
        if (container == null) return;
        try
        {
            var viewers = container.GetVisualDescendants().OfType<ScrollViewer>().ToList();
            foreach (var sv in viewers)
            {
                RegisterScrollViewer(sv);
            }
        }
        catch { }
    }
}

