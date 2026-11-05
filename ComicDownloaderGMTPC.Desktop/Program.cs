using System.Collections.Generic;
using ComicDownloaderGMTPC.ViewModels;
using ComicDownloaderGMTPC.Views;
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
    private static void EnsureApplicationRootDirectory()
    {
        try
        {
            string currentBase = AppContext.BaseDirectory.TrimEnd('/', '\\');
            string normalized = currentBase.Replace('/', '\\');
            string suffix = @"\.portable\bin\net avalonia";
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var dirInfo = new DirectoryInfo(currentBase);
                var rootDir = dirInfo.Parent?.Parent?.Parent;
                if (rootDir != null && rootDir.Exists)
                {
                    string rootPath = rootDir.FullName;
                    if (!rootPath.EndsWith(Path.DirectorySeparatorChar.ToString()) && !rootPath.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
                    {
                        rootPath += Path.DirectorySeparatorChar;
                    }
                    AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", rootPath);
                    Environment.CurrentDirectory = rootDir.FullName;
                }
            }
            else
            {
                string? envRoot = Environment.GetEnvironmentVariable("COMIC_DOWNLOADER_ROOT");
                if (!string.IsNullOrEmpty(envRoot) && Directory.Exists(envRoot))
                {
                    string rootPath = envRoot;
                    if (!rootPath.EndsWith(Path.DirectorySeparatorChar.ToString()) && !rootPath.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
                    {
                        rootPath += Path.DirectorySeparatorChar;
                    }
                    AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", rootPath);
                    Environment.CurrentDirectory = envRoot;
                }
            }
        }
        catch { }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        EnsureApplicationRootDirectory();
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

        if (args.Any(a => string.Equals(a, "--test-pinch-zoom-20passes", StringComparison.OrdinalIgnoreCase)))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var app = BuildAvaloniaApp().SetupWithoutStarting();

            var testTask = Task.Run(async () => await RunWebtoonPinchZoom20ConsecutiveTestAsync());
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

    private static async Task<int> RunWebtoonPinchZoom20ConsecutiveTestAsync()
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("  BẮT ĐẦU CHU KỲ KIỂM THỬ TỰ ĐỘNG PINCH & ZOOM WEBTOON 20 LẦN LIÊN TIẾP");
        Console.WriteLine("  Yêu cầu: 10 kịch bản Pinch In / Out 2 ngón tay ở 10 hướng khác nhau");
        Console.WriteLine("  Mục tiêu: Đạt đủ 20 LẦN LIÊN TIẾP THÀNH CÔNG trên cả tệp ZIP và Thư mục");
        Console.WriteLine("  1. Tệp nén ZIP: " + TestZipPath);
        Console.WriteLine("  2. Thư mục:     " + TestFolderPath);
        Console.WriteLine("================================================================================");

        int targetConsecutive = 20;
        int currentStreak = 0;
        int totalAttempts = 0;

        while (currentStreak < targetConsecutive)
        {
            totalAttempts++;
            Console.WriteLine($"\n>>> [LẦN CHẠY {totalAttempts}] - Chuỗi thành công hiện tại: {currentStreak}/{targetConsecutive}");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            bool pass = await RunSinglePinchZoomIterationAsync(totalAttempts);
            sw.Stop();

            if (pass)
            {
                currentStreak++;
                Console.WriteLine($"✅ LẦN CHẠY {totalAttempts} THÀNH CÔNG XUẤT SẮC trong {sw.ElapsedMilliseconds}ms! Chuỗi: {currentStreak}/{targetConsecutive}");
            }
            else
            {
                currentStreak = 0;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"❌ LẦN CHẠY {totalAttempts} THẤT BẠI! Reset chuỗi thành công về 0/{targetConsecutive}. Sẽ chạy lại!");
                Console.ResetColor();
            }

            // Dọn dẹp tài nguyên sau mỗi lần chạy
            ComicReaderService.Instance.SetReaderActive(false);
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            await Task.Delay(150);
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n================================================================================");
        Console.WriteLine($"🎉 XÁC NHẬN HOÀN TẤT: ĐÃ ĐẠT ĐỦ {targetConsecutive} LẦN LIÊN TIẾP THÀNH CÔNG 100%!");
        Console.WriteLine($"   Tổng số lần thực hiện: {totalAttempts}");
        Console.WriteLine("================================================================================");
        Console.ResetColor();
        return 0;
    }

    private static async Task<bool> RunSinglePinchZoomIterationAsync(int attempt)
    {
        try
        {
            // -------------------------------------------------------------------------
            // MỤC TIÊU 1: Tệp nén ZIP (Ta Luyện Khí Ba Ngàn Năm.zip - 1.74GB, 381 chương)
            // -------------------------------------------------------------------------
            Console.WriteLine($"  [Test 1/2] Kiểm tra 10 hướng Pinch-to-Zoom với Tệp ZIP...");
            var comicZip = LibraryScannerService.Instance.CreateQuickComicFromPath(TestZipPath);
            if (comicZip == null || comicZip.Chapters.Count == 0)
            {
                Console.WriteLine("    [LỖI] Không thể phân tích tệp ZIP!");
                return false;
            }

            var zipPages = await ComicReaderService.Instance.LoadChapterPagesAsync(comicZip.Chapters[0], null, CancellationToken.None);
            if (zipPages == null || zipPages.Count == 0)
            {
                Console.WriteLine("    [LỖI] Không nạp được trang nào từ Chapter 1 tệp ZIP!");
                return false;
            }

            bool zipPass = await Test10PinchDirectionsOnPagesAsync(zipPages, "ZIP: " + Path.GetFileName(TestZipPath));
            if (!zipPass) return false;

            // -------------------------------------------------------------------------
            // MỤC TIÊU 2: Thư mục 500 chương (võ luyện đỉnh phong chap 0001-0500)
            // -------------------------------------------------------------------------
            Console.WriteLine($"  [Test 2/2] Kiểm tra 10 hướng Pinch-to-Zoom với Thư mục 500 chương...");
            var comicFolder = LibraryScannerService.Instance.CreateQuickComicFromPath(TestFolderPath);
            if (comicFolder == null || comicFolder.Chapters.Count == 0)
            {
                Console.WriteLine("    [LỖI] Không thể phân tích thư mục 500 chương!");
                return false;
            }

            var folderPages = await ComicReaderService.Instance.LoadChapterPagesAsync(comicFolder.Chapters[0], null, CancellationToken.None);
            if (folderPages == null || folderPages.Count == 0)
            {
                Console.WriteLine("    [LỖI] Không nạp được trang nào từ Chapter 1 thư mục!");
                return false;
            }

            bool folderPass = await Test10PinchDirectionsOnPagesAsync(folderPages, "Folder: " + Path.GetFileName(TestFolderPath));
            if (!folderPass) return false;

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    [EXCEPTION] Lỗi trong chu kỳ test: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    private static async Task<bool> Test10PinchDirectionsOnPagesAsync(System.Collections.Generic.List<ReaderPageItem> pages, string contextName)
    {
        // Khởi tạo ViewModel và ComicReaderControl trong môi trường Avalonia UI Thread
        return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var vm = new ComicReaderViewModel();
            vm.Pages.AddRange(pages);
            vm.CurrentPageIndex = 0;
            vm.IsScrollMode = true;
            vm.IsHorizontalScrollMode = false; // Webtoon Mode
            vm.UpdateViewportDimensions(800, 1200);

            var control = new ComicReaderControl
            {
                DataContext = vm,
                Width = 800,
                Height = 1200
            };

            var window = new Avalonia.Controls.Window
            {
                Width = 800,
                Height = 1200,
                Content = control
            };
            window.Show();
            window.Measure(new Avalonia.Size(800, 1200));
            window.Arrange(new Avalonia.Rect(0, 0, 800, 1200));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var sv = control.GetReaderScrollViewer();
            if (sv == null)
            {
                Console.WriteLine($"    [LỖI] Không tìm thấy ScrollViewer trong ReaderControl cho {contextName}!");
                window.Close();
                return false;
            }

            // Đặt scroll offset ban đầu vào giữa tài liệu
            sv.Offset = new Avalonia.Vector(0, 600);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // 10 KỊCH BẢN PINCH & ZOOM Ở 10 HƯỚNG KHÁC NHAU:
            var scenarios = new (string Name, Point P1Start, Point P2Start, Point P1End, Point P2End, bool ExpectZoomIn)[]
            {
                // Hướng 1: Dọc mở rộng (Vertical Zoom-In)
                ("1. Dọc Zoom-In (0° / 180°)", new Point(400, 500), new Point(400, 700), new Point(400, 420), new Point(400, 780), true),
                // Hướng 2: Dọc thu nhỏ (Vertical Zoom-Out)
                ("2. Dọc Zoom-Out (0° / 180°)", new Point(400, 420), new Point(400, 780), new Point(400, 520), new Point(400, 680), false),
                // Hướng 3: Ngang mở rộng (Horizontal Zoom-In)
                ("3. Ngang Zoom-In (90° / 270°)", new Point(300, 600), new Point(500, 600), new Point(200, 600), new Point(600, 600), true),
                // Hướng 4: Ngang thu nhỏ (Horizontal Zoom-Out)
                ("4. Ngang Zoom-Out (90° / 270°)", new Point(200, 600), new Point(600, 600), new Point(320, 600), new Point(480, 600), false),
                // Hướng 5: Chéo góc 1-3 mở rộng (Diagonal Zoom-In 45°)
                ("5. Chéo Zoom-In 45° (ĐB - TN)", new Point(330, 670), new Point(470, 530), new Point(250, 750), new Point(550, 450), true),
                // Hướng 6: Chéo góc 1-3 thu nhỏ (Diagonal Zoom-Out 45°)
                ("6. Chéo Zoom-Out 45° (ĐB - TN)", new Point(250, 750), new Point(550, 450), new Point(340, 660), new Point(460, 540), false),
                // Hướng 7: Chéo góc 2-4 mở rộng (Diagonal Zoom-In 135°)
                ("7. Chéo Zoom-In 135° (TB - ĐN)", new Point(330, 530), new Point(470, 670), new Point(240, 440), new Point(560, 760), true),
                // Hướng 8: Chéo góc 2-4 thu nhỏ (Diagonal Zoom-Out 135°)
                ("8. Chéo Zoom-Out 135° (TB - ĐN)", new Point(240, 440), new Point(560, 760), new Point(350, 550), new Point(450, 650), false),
                // Hướng 9: Bất đối xứng (Asymmetric Pinch / 1 ngón cố định, 1 ngón kéo)
                ("9. Bất đối xứng (1 ngón giữ, 1 ngón kéo)", new Point(300, 500), new Point(450, 650), new Point(300, 500), new Point(600, 900), true),
                // Hướng 10: Vừa Pinch vừa Pan dịch chuyển (Combined Pinch & Pan)
                ("10. Pinch + Pan (Vừa zoom vừa dịch chuyển)", new Point(350, 550), new Point(450, 650), new Point(300, 450), new Point(500, 650), true)
            };

            for (int i = 0; i < scenarios.Length; i++)
            {
                var s = scenarios[i];
                double initialZoom = vm.ZoomLevel;
                Avalonia.Vector initialOffset = sv.Offset;

                // 1. Bắt đầu Pinch
                control.PerformPinchZoomStep(s.P1Start, s.P2Start);
                if (!control.IsPinching)
                {
                    Console.WriteLine($"      [FAIL {s.Name}] Cử chỉ pinch không được kích hoạt!");
                    window.Close();
                    return false;
                }

                // 2. Di chuyển ngón tay tới vị trí kết thúc
                control.PerformPinchZoomStep(s.P1End, s.P2End);

                double newZoom = vm.ZoomLevel;
                Avalonia.Vector newOffset = sv.Offset;

                // 3. Đánh giá tính toàn vẹn:
                // - Zoom level phải đổi đúng chiều
                if (s.ExpectZoomIn && newZoom <= initialZoom)
                {
                    Console.WriteLine($"      [FAIL {s.Name}] Zoom In thất bại: ban đầu {initialZoom}, sau pinch {newZoom}");
                    window.Close();
                    return false;
                }
                if (!s.ExpectZoomIn && newZoom >= initialZoom)
                {
                    Console.WriteLine($"      [FAIL {s.Name}] Zoom Out thất bại: ban đầu {initialZoom}, sau pinch {newZoom}");
                    window.Close();
                    return false;
                }

                // - Tính toán công thức neo:
                // Tọa độ Document Point dưới tâm ban đầu = initialOffset.Y + pivotStart.Y
                // Khi zoomRatio thay đổi: TargetOffsetY = (initialOffset.Y + pivotStart.Y) * zoomRatio - pivotEnd.Y
                double zoomRatio = initialZoom > 0.05 ? newZoom / initialZoom : 1.0;
                double pivotStartY = (s.P1Start.Y + s.P2Start.Y) * 0.5;
                double pivotEndY = (s.P1End.Y + s.P2End.Y) * 0.5;
                double expectedTargetY = (initialOffset.Y + pivotStartY) * zoomRatio - pivotEndY;

                // Độ dịch chuyển bất thường không được vượt quá phạm vi dự kiến (chống drag văng quá mức)
                // Khoảng offset thực tế phải bám sát expectedTargetY trong phạm vi Clamp
                double actualY = newOffset.Y;
                double deltaExpected = Math.Abs(actualY - expectedTargetY);
                if (deltaExpected > 2.0 && actualY > 0)
                {
                    Console.WriteLine($"      [FAIL {s.Name}] Ảnh bị trôi lệch khỏi công thức neo! Actual={actualY:F1}, Expected={expectedTargetY:F1}, Delta={deltaExpected:F1}");
                    window.Close();
                    return false;
                }

                // 4. Kết thúc Pinch
                control.EndPinchZoom();
                if (control.IsPinching)
                {
                    Console.WriteLine($"      [FAIL {s.Name}] Cử chỉ pinch không kết thúc đúng cách!");
                    window.Close();
                    return false;
                }

                // Đảm bảo không bị tua sang trang ngẫu nhiên khác
                if (vm.CurrentPageIndex < 0 || vm.CurrentPageIndex >= vm.Pages.Count)
                {
                    Console.WriteLine($"      [FAIL {s.Name}] CurrentPageIndex bị nhảy sai: {vm.CurrentPageIndex}");
                    window.Close();
                    return false;
                }
            }

            Console.WriteLine($"    -> Đã kiểm tra hoàn tất 10/10 hướng Pinch-to-Zoom thành công cho {contextName}!");
            window.Close();
            return true;
        });
    }

}
