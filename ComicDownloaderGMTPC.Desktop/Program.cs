using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.VisualTree;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.Desktop;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--test-scroll-drag", StringComparison.OrdinalIgnoreCase)))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("=== STARTING DETAILED SCROLL DRAG TEST ===");
            var app = BuildAvaloniaApp().SetupWithoutStarting();
            
            var window = new Avalonia.Controls.Window { Width = 800, Height = 600 };
            var scrollViewer = new Avalonia.Controls.ScrollViewer
            {
                Width = 400,
                Height = 400,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            };

            var stack = new Avalonia.Controls.StackPanel { Spacing = 10 };
            for (int i = 0; i < 50; i++)
            {
                stack.Children.Add(new Avalonia.Controls.TextBlock { Text = $"Item {i}", Height = 40 });
            }
            scrollViewer.Content = stack;
            window.Content = scrollViewer;

            ScrollBarAutoExpandService.AttachGlobalScrollListeners(window);
            ScrollBarAutoExpandService.RegisterAllScrollViewers(window);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            window.Show();

            // Force layout
            window.Measure(new Avalonia.Size(800, 600));
            window.Arrange(new Avalonia.Rect(0, 0, 800, 600));

            var scrollBar = window.GetVisualDescendants()
                .OfType<Avalonia.Controls.Primitives.ScrollBar>()
                .FirstOrDefault(sb => sb.Orientation == Avalonia.Layout.Orientation.Vertical);

            Console.WriteLine($"ScrollBar found: {scrollBar != null}");
            if (scrollBar != null)
            {
                ScrollBarAutoExpandService.HookScrollBar(scrollBar);
                // Trigger expand so scrollbar is in expanded 36px state
                ScrollBarAutoExpandService.TriggerGlobalScrollExpand(window);
                
                // Force layout again after expand
                window.Measure(new Avalonia.Size(800, 600));
                window.Arrange(new Avalonia.Rect(0, 0, 800, 600));

                Console.WriteLine($"After Expand: ScrollBar.Bounds={scrollBar.Bounds}, Classes='{string.Join(",", scrollBar.Classes)}'");
                var track = scrollBar.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Track>().FirstOrDefault();
                Console.WriteLine($"Track: ViewportSize={track?.ViewportSize}, Bounds={track?.Bounds}");
                
                var thumb = track?.Thumb;
                Console.WriteLine($"Thumb: Bounds={thumb?.Bounds}, DesiredSize={thumb?.DesiredSize}");

                // Scenario A: Touch Drag on Thumb with PointerType.Touch
                Console.WriteLine("\n--- Scenario A: Touch drag directly on Thumb (PointerType.Touch) ---");
                if (thumb != null)
                {
                    var touchPointer = new Avalonia.Input.Pointer(10, Avalonia.Input.PointerType.Touch, true);
                    var thumbCenterLocal = new Avalonia.Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2);
                    var thumbCenterOnWindow = thumb.TranslatePoint(thumbCenterLocal, window) ?? thumbCenterLocal;
                    Console.WriteLine($"Thumb Center on Window: {thumbCenterOnWindow}");

                    var pressArgs = new Avalonia.Input.PointerPressedEventArgs(
                        thumb,
                        touchPointer,
                        window,
                        thumbCenterOnWindow,
                        0,
                        new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonPressed),
                        Avalonia.Input.KeyModifiers.None);
                    thumb.RaiseEvent(pressArgs);
                    Console.WriteLine($"Press Handled={pressArgs.Handled}, Offset={scrollViewer.Offset.Y}");

                    for (int step = 1; step <= 3; step++)
                    {
                        var movePoint = new Avalonia.Point(thumbCenterOnWindow.X, thumbCenterOnWindow.Y + step * 30);
                        var moveArgs = new Avalonia.Input.PointerEventArgs(
                            Avalonia.Input.InputElement.PointerMovedEvent,
                            thumb,
                            touchPointer,
                            window,
                            movePoint,
                            0,
                            new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonPressed),
                            Avalonia.Input.KeyModifiers.None);
                        thumb.RaiseEvent(moveArgs);
                        Console.WriteLine($"Move Step {step} (+{step * 30}px): Move Handled={moveArgs.Handled}, ScrollViewer.Offset={scrollViewer.Offset.Y}");
                    }
                }

                // Scenario B: Touch Drag on ScrollBar/Track (outside Thumb, e.g. at Y = 250)
                Console.WriteLine("\n--- Scenario B: Touch drag on ScrollBar Track (at Y = 250, outside Thumb) ---");
                {
                    var touchPointer2 = new Avalonia.Input.Pointer(11, Avalonia.Input.PointerType.Touch, true);
                    var trackPoint = new Avalonia.Point(scrollBar.Bounds.Width / 2, 250);

                    var pressArgs2 = new Avalonia.Input.PointerPressedEventArgs(
                        scrollBar,
                        touchPointer2,
                        window,
                        trackPoint,
                        0,
                        new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonPressed),
                        Avalonia.Input.KeyModifiers.None);
                    scrollBar.RaiseEvent(pressArgs2);
                    Console.WriteLine($"Track Press Handled={pressArgs2.Handled}, Offset={scrollViewer.Offset.Y}");

                    for (int step = 1; step <= 3; step++)
                    {
                        var moveArgs2 = new Avalonia.Input.PointerEventArgs(
                            Avalonia.Input.InputElement.PointerMovedEvent,
                            scrollBar,
                            touchPointer2,
                            window,
                            new Avalonia.Point(trackPoint.X, trackPoint.Y + step * 30),
                            0,
                            new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonPressed),
                            Avalonia.Input.KeyModifiers.None);
                        scrollBar.RaiseEvent(moveArgs2);
                        Console.WriteLine($"Track Move Step {step} (+{step * 30}px): Handled={moveArgs2.Handled}, Offset={scrollViewer.Offset.Y}");
                    }
                }
            }
            Console.WriteLine("=== END OF SCROLL DRAG TEST ===");
            return;
        }

        if (args.Any(a => string.Equals(a, "--test-truyenqq", StringComparison.OrdinalIgnoreCase)))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var sb = new System.Text.StringBuilder();
            var scraper = new ComicScraperService();
            string[] testUrls =
            {
                "https://truyenqqko.com/truyen-tranh/gijoe-2024-25044",
                "https://truyenqqko.com/truyen-tranh/ca-vang-shibuya-25047",
                "https://truyenqqko.com/truyen-tranh/hinowa-chinh-phat-25046"
            };
            for (int i = 0; i < testUrls.Length; i++)
            {
                var item = scraper.ScrapeBookAsync(testUrls[i], i + 1, "vi", true).GetAwaiter().GetResult();
                item.LatestChapter = item.Chapters.Count > 0 ? item.Chapters[item.Chapters.Count - 1].Title : "-";
                item.MissingChapters = $"Đủ ({item.TotalChapters} chap)";
                ThumbnailPreviewService.Instance.RescanItemAsync(item).GetAwaiter().GetResult();
                ThumbnailPreviewService.Instance.EnsureHighResPreviewAsync(item).GetAwaiter().GetResult();
                sb.AppendLine($"[TEST {i + 1}] Title='{item.Title}' | Chaps={item.TotalChapters} | Latest='{item.LatestChapterDisplay}' | Missing='{item.MissingChapterDisplay}' | Status='{item.DisplayStatusVi}'");
                sb.AppendLine($"  CoverUrl='{item.CoverUrl}'");
                sb.AppendLine($"  Tags='{item.PreviewTagsText}'");
                sb.AppendLine($"  ThumbFile='{item.ThumbnailLocalPath}' (Exists={File.Exists(item.ThumbnailLocalPath)}, Size={(File.Exists(item.ThumbnailLocalPath) ? new FileInfo(item.ThumbnailLocalPath!).Length : 0)})");
                sb.AppendLine($"  OrigFile='{item.OriginalCoverLocalPath}' (Exists={File.Exists(item.OriginalCoverLocalPath)}, Size={(File.Exists(item.OriginalCoverLocalPath) ? new FileInfo(item.OriginalCoverLocalPath!).Length : 0)})");
            }
            string logPath = Path.Combine(AppContext.BaseDirectory, ".tmp", "test-truyenqq.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, sb.ToString(), System.Text.Encoding.UTF8);
            Console.WriteLine(sb.ToString());
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
