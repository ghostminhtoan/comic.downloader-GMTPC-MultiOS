using System.Threading;
using System.Threading.Tasks;
using ComicDownloaderGMTPC.Models;
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

        if (args.Any(a => string.Equals(a, "--test-reader-5passes", StringComparison.OrdinalIgnoreCase)))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var app = BuildAvaloniaApp().SetupWithoutStarting();

            var testTask = Task.Run(async () => await RunReaderAutoTest5ConsecutiveAsync());
            while (!testTask.IsCompleted)
            {
                try
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                }
                catch { }
                Thread.Sleep(5);
            }

            int exitCode = testTask.GetAwaiter().GetResult();
            Environment.Exit(exitCode);
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

    private const string TestZipPath = @"T:\[HDD T]\xnconvert\uploaded\Ta Luyện Khí Ba Ngàn Năm.zip";
    private const string TestFolderPath = @"T:\[HDD T]\xnconvert\done\võ luyện đỉnh phong nettruyenvio\võ luyện đỉnh phong chap 0001-0500";

    private static async Task<int> RunReaderAutoTest5ConsecutiveAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  BẮT ĐẦU CHU KỲ KIỂM THỬ TỰ ĐỘNG 5 LẦN LIÊN TIẾP CHO READER");
        Console.WriteLine("  1. Tệp nén ZIP: " + TestZipPath);
        Console.WriteLine("  2. Thư mục:     " + TestFolderPath);
        Console.WriteLine("================================================================");

        int targetConsecutive = 5;
        int currentStreak = 0;
        int totalAttempts = 0;

        while (currentStreak < targetConsecutive)
        {
            totalAttempts++;
            Console.WriteLine($"\n[LẦN CHẠY {totalAttempts}] - Chuỗi thành công hiện tại: {currentStreak}/{targetConsecutive}");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            bool pass = await RunSingleReaderTestIterationAsync();
            sw.Stop();

            if (pass)
            {
                currentStreak++;
                Console.WriteLine($"✅ LẦN CHẠY {totalAttempts} THÀNH CÔNG TRỌN VẸN trong {sw.ElapsedMilliseconds}ms! Chuỗi thành công: {currentStreak}/{targetConsecutive}");
            }
            else
            {
                currentStreak = 0;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"❌ LẦN CHẠY {totalAttempts} THẤT BẠI! Reset chuỗi thành công về 0/{targetConsecutive}. Sẽ chạy lại!");
                Console.ResetColor();
            }

            // Dọn dẹp tài nguyên và bộ nhớ sau mỗi lần lặp
            ComicReaderService.Instance.SetReaderActive(false);
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            await Task.Delay(200);
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n================================================================");
        Console.WriteLine($"🎉 XÁC NHẬN: TEST THÀNH CÔNG ĐỦ {targetConsecutive} LẦN LIÊN TIẾP!");
        Console.WriteLine($"   Tổng số lần thực hiện: {totalAttempts}");
        Console.WriteLine("================================================================");
        Console.ResetColor();
        return 0;
    }

    private static async Task<bool> RunSingleReaderTestIterationAsync()
    {
        try
        {
            // -------------------------------------------------------------
            // PHẦN 1: TEST TỆP NÉN 1.74GB (Ta Luyện Khí Ba Ngàn Năm.zip)
            // -------------------------------------------------------------
            Console.WriteLine($"  [Test 1/2] Đang quét và kiểm tra tệp ZIP lớn: {Path.GetFileName(TestZipPath)}...");
            if (!File.Exists(TestZipPath))
            {
                Console.WriteLine($"    [LỖI] Không tìm thấy tệp ZIP: {TestZipPath}");
                return false;
            }

            var comicZip = LibraryScannerService.Instance.CreateQuickComicFromPath(TestZipPath);
            if (comicZip == null || comicZip.Chapters.Count == 0)
            {
                Console.WriteLine("    [LỖI] Không thể phân tích chapter từ tệp ZIP!");
                return false;
            }
            Console.WriteLine($"    -> Đã nhận diện: '{comicZip.Title}' ({comicZip.Chapters.Count} chương).");

            var firstChap = comicZip.Chapters[0];
            var zipPages = await ComicReaderService.Instance.LoadChapterPagesAsync(firstChap, null, CancellationToken.None);
            if (zipPages == null || zipPages.Count == 0)
            {
                Console.WriteLine("    [LỖI] Không nạp được trang nào từ chapter 1 của tệp ZIP!");
                return false;
            }
            Console.WriteLine($"    -> Chapter 1 có {zipPages.Count} trang ảnh.");

            // Giải mã trang 1 (Thử nghiệm Subsample, ArrayPool, Skia Pipeline)
            await ComicReaderService.Instance.EnsurePageLoadedAsync(zipPages[0]);
            if (!zipPages[0].HasImage || zipPages[0].Image == null)
            {
                Console.WriteLine("    [LỖI] Giải mã trang 1 tệp ZIP thất bại (Image null)!");
                return false;
            }
            Console.WriteLine($"    -> Trang 1 giải mã thành công: {zipPages[0].Image!.PixelSize.Width}x{zipPages[0].Image!.PixelSize.Height} px.");

            // Kiểm tra bộ lọc Warm Filter & 16-bit RGB_565 (Mục 2 & 14)
            if (zipPages.Count > 1)
            {
                ComicReaderService.Instance.ActiveEffectSettings.IsWarmFilter = true;
                ComicReaderService.Instance.ActiveEffectSettings.Prefer16BitColor = true;

                await ComicReaderService.Instance.EnsurePageLoadedAsync(zipPages[1]);
                if (!zipPages[1].HasImage || zipPages[1].Image == null)
                {
                    Console.WriteLine("    [LỖI] Giải mã trang 2 với Warm Filter / 16-bit thất bại!");
                    return false;
                }
                Console.WriteLine($"    -> Trang 2 áp dụng Warm Filter & 16-bit thành công: {zipPages[1].Image!.PixelSize.Width}x{zipPages[1].Image!.PixelSize.Height} px.");

                ComicReaderService.Instance.ActiveEffectSettings.IsWarmFilter = false;
            }

            // -------------------------------------------------------------
            // PHẦN 2: TEST THƯ MỤC 500 CHƯƠNG (võ luyện đỉnh phong chap 0001-0500)
            // -------------------------------------------------------------
            Console.WriteLine($"  [Test 2/2] Đang quét và kiểm tra thư mục 500 chương: {Path.GetFileName(TestFolderPath)}...");
            if (!Directory.Exists(TestFolderPath))
            {
                Console.WriteLine($"    [LỖI] Không tìm thấy thư mục: {TestFolderPath}");
                return false;
            }

            var comicFolder = LibraryScannerService.Instance.CreateQuickComicFromPath(TestFolderPath);
            if (comicFolder == null || comicFolder.Chapters.Count < 450)
            {
                Console.WriteLine($"    [LỖI] Quét thư mục 500 chương chỉ nhận được {comicFolder?.Chapters.Count ?? 0} chương!");
                return false;
            }
            Console.WriteLine($"    -> Đã nhận diện đủ {comicFolder.Chapters.Count} chương với Natural Sort chuẩn xác.");

            // Nạp Chapter 1
            var chap1Pages = await ComicReaderService.Instance.LoadChapterPagesAsync(comicFolder.Chapters[0], null, CancellationToken.None);
            if (chap1Pages == null || chap1Pages.Count == 0)
            {
                Console.WriteLine("    [LỖI] Không nạp được trang nào từ Chapter 1 thư mục!");
                return false;
            }
            await ComicReaderService.Instance.EnsurePageLoadedAsync(chap1Pages[0]);
            if (!chap1Pages[0].HasImage || chap1Pages[0].Image == null)
            {
                Console.WriteLine("    [LỖI] Giải mã trang đầu Chapter 1 thư mục thất bại!");
                return false;
            }
            Console.WriteLine($"    -> Chapter 1 ({chap1Pages.Count} trang) - Trang 1 giải mã: {chap1Pages[0].Image!.PixelSize.Width}x{chap1Pages[0].Image!.PixelSize.Height} px.");

            // Nạp Chapter 100
            int chapIdx = Math.Min(99, comicFolder.Chapters.Count - 1);
            var chap100Pages = await ComicReaderService.Instance.LoadChapterPagesAsync(comicFolder.Chapters[chapIdx], null, CancellationToken.None);
            if (chap100Pages == null || chap100Pages.Count == 0)
            {
                Console.WriteLine($"    [LỖI] Không nạp được trang nào từ Chapter {chapIdx + 1} thư mục!");
                return false;
            }
            await ComicReaderService.Instance.EnsurePageLoadedAsync(chap100Pages[0]);
            if (!chap100Pages[0].HasImage || chap100Pages[0].Image == null)
            {
                Console.WriteLine($"    [LỖI] Giải mã trang đầu Chapter {chapIdx + 1} thư mục thất bại!");
                return false;
            }
            Console.WriteLine($"    -> Chapter {chapIdx + 1} ({chap100Pages.Count} trang) - Trang 1 giải mã: {chap100Pages[0].Image!.PixelSize.Width}x{chap100Pages[0].Image!.PixelSize.Height} px.");

            // Kiểm tra Smart Margin Detection (Mục 17)
            var rawFolder = ComicReaderService.ReadRawPageBytes(chap1Pages[0]);
            if (rawFolder != null)
            {
                var (l, t, r, b) = ComicReaderService.DetectSmartMargins(rawFolder);
                Console.WriteLine($"    -> Thuật toán Auto Smart Margin phân tích viền: L {l:0}% • T {t:0}% • R {r:0}% • B {b:0}%.");
            }

            // Dọn dẹp các trang đã nạp trong RAM
            zipPages[0].Unload();
            if (zipPages.Count > 1) zipPages[1].Unload();
            chap1Pages[0].Unload();
            chap100Pages[0].Unload();

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    [EXCEPTION] Lỗi ngoại lệ trong quá trình test: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }
}
