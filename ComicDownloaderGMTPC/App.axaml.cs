using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.Services;
using ComicDownloaderGMTPC.ViewModels;
using ComicDownloaderGMTPC.Views;
using SkiaSharp;

namespace ComicDownloaderGMTPC;

public partial class App : Application
{
    private static MainViewModel? _sharedMainViewModel;
    public static MainViewModel SharedMainViewModel => _sharedMainViewModel ??= new MainViewModel();

    private static System.Threading.Mutex? _singleInstanceMutex;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 1. Kiểm tra cờ kiểm thử tự động CLI
            if (desktop.Args != null && desktop.Args.Any(a => string.Equals(a, "--test-vertical-doubletap", StringComparison.OrdinalIgnoreCase)))
            {
                base.OnFrameworkInitializationCompleted();
                _ = RunVerticalDoubleTapTestAsync(desktop);
                return;
            }

            if (desktop.Args != null && desktop.Args.Any(a => string.Equals(a, "--test-pinch-thumuc", StringComparison.OrdinalIgnoreCase)))
            {
                base.OnFrameworkInitializationCompleted();
                _ = RunPinchThuMucTestAsync(desktop);
                return;
            }

            if (desktop.Args != null && desktop.Args.Any(a => string.Equals(a, "--test-buttobi-drag", StringComparison.OrdinalIgnoreCase)))
            {
                base.OnFrameworkInitializationCompleted();
                _ = RunButtobiDragTestAsync(desktop);
                return;
            }

            if (desktop.Args != null && desktop.Args.Any(a => string.Equals(a, "--test-dualsort-nav", StringComparison.OrdinalIgnoreCase)))
            {
                base.OnFrameworkInitializationCompleted();
                _ = RunDualSortNavTestAsync(desktop);
                return;
            }

            if (desktop.Args != null && desktop.Args.Any(a => string.Equals(a, "--test-dualsort-keep-selected", StringComparison.OrdinalIgnoreCase)))
            {
                base.OnFrameworkInitializationCompleted();
                _ = RunDualSortKeepSelectedTestAsync(desktop);
                return;
            }

            if (desktop.Args != null && desktop.Args.Any(a => string.Equals(a, "--test-inspect-media", StringComparison.OrdinalIgnoreCase)))
            {
                base.OnFrameworkInitializationCompleted();
                _ = RunInspectMediaTestAsync(desktop);
                return;
            }

            // 2. Kiểm tra nếu mở tệp trực tiếp từ hệ điều hành (Double-click file hoặc Context Menu)
            // Khởi động trực tiếp TRÌNH ĐỌC TRUYỆN ĐỘC LẬP SIÊU NHẸ (STANDALONE READER MODE),
            // TUYỆT ĐỐI KHÔNG khởi tạo MainWindow / MainViewModel (không nạp Scraper, WebView, Database)!
            if (desktop.Args != null && desktop.Args.Length > 0)
            {
                string? candidate = desktop.Args.FirstOrDefault(a =>
                    !a.StartsWith("--") &&
                    !string.IsNullOrWhiteSpace(a));

                if (!string.IsNullOrEmpty(candidate))
                {
                    string cleanPath = candidate.Trim('\"', '\'');
                    if (IsReadableComicOrMediaFileOrDirectory(cleanPath))
                    {
                        var scanner = new LibraryScannerService();
                        var quickComic = scanner.CreateQuickComicFromPath(cleanPath);
                        if (quickComic != null && quickComic.Chapters.Count > 0)
                        {
                            var targetChap = quickComic.GetBestResumeChapter() ?? quickComic.Chapters[0];
                            var readerVm = new ComicReaderViewModel();
                            var readerWin = new ComicReaderWindow
                            {
                                DataContext = readerVm
                            };
                            desktop.MainWindow = readerWin;
                            readerWin.Show();
                            _ = readerVm.InitializeAsync(quickComic, targetChap);
                            base.OnFrameworkInitializationCompleted();
                            return;
                        }
                    }
                }
            }

            // 3. Khởi động Ứng dụng chính (Comic Downloader GMTPC):
            // Kiểm tra cấu hình Đa tiến trình (Multi-instance Setting)
            if (!InstanceConfigService.Instance.IsMultiInstanceEnabled)
            {
                bool isFirst = CheckOrCreateSingleInstanceMutex();
                if (!isFirst)
                {
                    // Đã có instance app chính khác đang chạy: đưa cửa sổ cũ lên trước và thoát instance mới
                    BringExistingWindowToFront();
                    desktop.Shutdown(0);
                    return;
                }
            }

            desktop.ShutdownRequested += (_, _) =>
            {
                try { _sharedMainViewModel?.FlushAutoSaveImmediately(); } catch { }
            };
            desktop.Exit += (_, _) =>
            {
                try { _sharedMainViewModel?.FlushAutoSaveImmediately(); } catch { }
                try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            };
            desktop.MainWindow = new MainWindow
            {
                DataContext = SharedMainViewModel
            };

            // Khởi tạo và quét trạng thái liên kết tệp hệ thống trên Desktop
            SharedMainViewModel.InitFileAssociation();
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime singleViewFactoryApplicationLifetime)
        {
            singleViewFactoryApplicationLifetime.MainViewFactory = () => new MainView { DataContext = SharedMainViewModel };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = SharedMainViewModel
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static bool CheckOrCreateSingleInstanceMutex()
    {
        try
        {
            string appKey = "ComicDownloaderGMTPC_SingleInstance_" + Math.Abs(AppContext.BaseDirectory.ToLowerInvariant().GetHashCode()).ToString("X8");
            _singleInstanceMutex = new System.Threading.Mutex(true, appKey, out bool createdNew);
            return createdNew;
        }
        catch
        {
            return true;
        }
    }

    private static void BringExistingWindowToFront()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var procList = System.Diagnostics.Process.GetProcessesByName(System.Diagnostics.Process.GetCurrentProcess().ProcessName);
                int currentId = System.Diagnostics.Process.GetCurrentProcess().Id;
                foreach (var proc in procList)
                {
                    if (proc.Id != currentId && proc.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(proc.MainWindowHandle, 9); // SW_RESTORE
                        SetForegroundWindow(proc.MainWindowHandle);
                        break;
                    }
                }
            }
            catch { }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static async Task RunVerticalDoubleTapTestAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string zipPath = @"T:\[HDD T]\zfaststone capture\zfinish\độc thủ vu y nettruyenvio\độc thủ vu y chap 001-200.zip";
        if (!File.Exists(zipPath))
        {
            Console.WriteLine($"[TEST ERROR] File not found: {zipPath}");
            desktop.Shutdown(1);
            return;
        }

        Console.WriteLine($"[TEST START] Loading comic archive: {zipPath}");
        int idx = 1;
        var chapters = LibraryScannerService.ExpandArchiveIntoChapters(zipPath, ref idx, out string? firstEntry);
        if (chapters.Count == 0)
        {
            Console.WriteLine("[TEST ERROR] No chapters found in archive!");
            desktop.Shutdown(1);
            return;
        }

        var targetChapter = chapters[0];
        Console.WriteLine($"[TEST] Selected Chapter: '{targetChapter.Title}' with {targetChapter.TotalPages} pages.");

        var readerVm = new ComicReaderViewModel();
        readerVm.PageLayoutMode = 0; // Chế độ Cuộn dọc (Vertical Scroll / Webtoon)
        readerVm.DoubleTapZoomScale = 125.0; // Zoom mặc định 125%

        var readerControl = new ComicReaderControl
        {
            DataContext = readerVm
        };

        var window = new Window
        {
            Title = "TEST VERTICAL DOUBLE TAP - Độc Thủ Vu Y",
            Width = 480,
            Height = 854,
            Content = readerControl
        };

        desktop.MainWindow = window;
        window.Show();

        await readerVm.InitializeAsync(null, targetChapter);
        Console.WriteLine($"[TEST] Reader initialized with {readerVm.Pages.Count} pages. CurrentPageIndex = {readerVm.CurrentPageIndex}");

        // Chờ layout hoàn tất
        await Task.Delay(2000);

        // Cuộn xuống 2 trang: tức là trang hiện tại là Page Index 2
        Console.WriteLine("[TEST] Scrolling down 2 pages (to Page Index 2)...");
        readerVm.CurrentPageIndex = 2;
        readerVm.RequestScrollToPage(2);

        await Task.Delay(2500);
        Console.WriteLine($"[TEST] Stabilized at CurrentPageIndex = {readerVm.CurrentPageIndex}, ZoomLevel = {readerVm.ZoomLevel * 100:0}%");

        // Thực hiện Double-Tap liên tục 10 lần với tốc độ cao (350ms giữa các lần gõ) để chứng minh KHÔNG CÒN DELAY!
        var traceLogs = new List<string>();
        bool allPassed = true;

        for (int step = 1; step <= 10; step++)
        {
            double preZoom = readerVm.ZoomLevel;
            int preIndex = readerVm.CurrentPageIndex;

            // Chạm đúp tại vị trí tâm của vùng đọc truyện
            Point tapPoint = new Point(window.Bounds.Width / 2.0, window.Bounds.Height / 2.0);
            readerControl.PerformDoubleTapZoom(tapPoint);

            // Chờ cực ngắn 350ms (Double-Tap liên tục siêu tốc) để kiểm chứng không bị delay!
            await Task.Delay(350);

            double postZoom = readerVm.ZoomLevel;
            int postIndex = readerVm.CurrentPageIndex;
            double expectedZoom = (step % 2 == 1) ? 1.25 : 1.0;
            bool zoomMatched = Math.Abs(postZoom - expectedZoom) < 0.05;
            bool pageMatched = (postIndex == 2);
            bool pass = zoomMatched && pageMatched;
            if (!pass) allPassed = false;

            string logLine = $"[RAPID DOUBLETAP TRACE {step:D2}/10] PreZoom={preZoom * 100:0}% -> PostZoom={postZoom * 100:0}% (Expected={expectedZoom * 100:0}%) | PrePage={preIndex} -> PostPage={postIndex} (Expected=2) -> {(pass ? "PASS ✅ KHÔNG DELAY, KHÔNG LỆCH TRANG" : "FAIL ❌")}";
            Console.WriteLine(logLine);
            traceLogs.Add(logLine);
        }

        string resultSummary = allPassed
            ? "\n=========================================\n🎉 KẾT QUẢ TEST: 10/10 LẦN PASS! KHÔNG BỊ LỆCH TRANG NÀO!\n========================================="
            : "\n=========================================\n⚠️ KẾT QUẢ TEST: CÓ LẦN BỊ LỆCH TRANG!\n=========================================";
        Console.WriteLine(resultSummary);

        // Ghi log ra file
        string logDir = Path.Combine(AppContext.BaseDirectory, ".tmp");
        Directory.CreateDirectory(logDir);
        string logFile = Path.Combine(logDir, "test-vertical-doubletap.log");
        File.WriteAllLines(logFile, traceLogs.Concat(new[] { resultSummary }), System.Text.Encoding.UTF8);
        Console.WriteLine($"[TEST LOG SAVED] {logFile}");

        await Task.Delay(1000);
        desktop.Shutdown(allPassed ? 0 : 2);
    }

    private static async Task RunPinchThuMucTestAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string dirPath = @"T:\[HDD T]\zfaststone capture\Thư mục";
        if (!Directory.Exists(dirPath))
        {
            Console.WriteLine($"[TEST ERROR] Directory not found: {dirPath}");
            desktop.Shutdown(1);
            return;
        }

        Console.WriteLine($"[TEST START] Loading folder images: {dirPath}");
        var scanner = new LibraryScannerService();
        var quickComic = scanner.CreateQuickComicFromPath(dirPath);
        if (quickComic == null || quickComic.Chapters.Count == 0)
        {
            Console.WriteLine("[TEST ERROR] No chapters or images found in directory!");
            desktop.Shutdown(1);
            return;
        }

        var targetChapter = quickComic.Chapters[0];
        Console.WriteLine($"[TEST] Selected Chapter: '{targetChapter.Title}' with {targetChapter.TotalPages} pages.");

        var readerVm = new ComicReaderViewModel();
        readerVm.PageLayoutMode = 0; // Chế độ Cuộn dọc (Vertical Scroll / Webtoon)
        readerVm.ZoomLevel = 1.0;

        var readerControl = new ComicReaderControl
        {
            DataContext = readerVm
        };

        var window = new Window
        {
            Title = "TEST PINCH ZOOM - Thư mục T",
            Width = 480,
            Height = 854,
            Content = readerControl
        };

        desktop.MainWindow = window;
        window.Show();

        await readerVm.InitializeAsync(quickComic, targetChapter);
        Console.WriteLine($"[TEST] Reader initialized with {readerVm.Pages.Count} pages. CurrentPageIndex = {readerVm.CurrentPageIndex}");

        // Chờ layout và nạp ảnh hoàn tất
        await Task.Delay(2500);

        // Cuộn xuống trang 2 (Index 2)
        Console.WriteLine("[TEST] Scrolling to target Page Index 2...");
        readerVm.CurrentPageIndex = 2;
        readerVm.RequestScrollToPage(2);
        await Task.Delay(2500);

        int targetIndex = 2;
        Console.WriteLine($"[TEST] Stabilized at CurrentPageIndex = {readerVm.CurrentPageIndex}, ZoomLevel = {readerVm.ZoomLevel * 100:0}%");

        // Mô phỏng cử chỉ 2 ngón tay Pinch-to-zoom 20 lần liên tục (10 lần Zoom In + 10 lần Zoom Out)
        var traceLogs = new List<string>();
        bool allPassed = true;

        // Bắt đầu pinch ở vị trí giữa màn hình
        double centerX = window.Bounds.Width > 0 ? window.Bounds.Width / 2.0 : 240;
        double centerY = window.Bounds.Height > 0 ? window.Bounds.Height / 2.0 : 427;

        // 20 tỉ lệ khoảng cách 2 ngón tay mô phỏng (10 bước phóng to + 10 bước thu nhỏ)
        double[] distanceMultipliers = new double[]
        {
            1.06, 1.15, 1.25, 1.40, 1.60, 1.80, 2.05, 2.30, 2.60, 3.00, // 10 bước Zoom In
            2.70, 2.40, 2.10, 1.80, 1.55, 1.35, 1.20, 1.10, 0.95, 1.00  // 10 bước Zoom Out
        };

        readerControl.PerformPinchZoomStep(new Point(centerX - 100, centerY), new Point(centerX + 100, centerY));
        await Task.Delay(100);

        for (int step = 0; step < distanceMultipliers.Length; step++)
        {
            double mult = distanceMultipliers[step];
            double halfDist = 100.0 * mult;
            Point p1 = new Point(centerX - halfDist, centerY);
            Point p2 = new Point(centerX + halfDist, centerY);

            double preZoom = readerVm.ZoomLevel;
            int preIndex = readerVm.CurrentPageIndex;

            readerControl.PerformPinchZoomStep(p1, p2);
            await Task.Delay(250);

            double postZoom = readerVm.ZoomLevel;
            int postIndex = readerVm.CurrentPageIndex;

            // Kiểm tra trang mục tiêu giữ nguyên không bị tua
            bool pageMatched = (postIndex == targetIndex);

            // Kiểm tra điểm neo nội dung trên trang mục tiêu: không bị giật nhảy lên xuống
            var sv = readerControl.GetReaderScrollViewer();
            double curOffsetY = sv?.Offset.Y ?? 0;
            double targetPageDocY = 0;
            for (int i = 0; i < targetIndex && i < readerVm.Pages.Count; i++)
            {
                var p = readerVm.Pages[i];
                targetPageDocY += p.DisplayHeight + p.PageMargin.Top + p.PageMargin.Bottom;
            }
            double targetPageHeight = readerVm.Pages[targetIndex].DisplayHeight;
            double normY = readerControl.PinchTargetNormY;
            double anchorDocY = targetPageDocY + (normY * targetPageHeight);
            double anchorScreenY = anchorDocY - curOffsetY;
            double jitter = Math.Abs(anchorScreenY - centerY);
            bool jitterOk = jitter <= 5.0; // Độ lệch điểm chạm dưới ngón tay <= 5.0px (ổn định tuyệt đối)

            bool pass = pageMatched && jitterOk;
            if (!pass) allPassed = false;

            string direction = step < 10 ? "ZoomIn" : "ZoomOut";
            string logLine = $"[PINCH 20-STEPS TRACE {step + 1:D2}/20 | {direction}] Mult={mult:0.00} | Zoom={postZoom * 100:0}% | Page={postIndex} (Exp={targetIndex}) | OffsetY={curOffsetY:0.0} | Jitter={jitter:0.1}px -> {(pass ? "PASS ✅ ỔN ĐỊNH, KHÔNG GIẬT NHẢY" : "FAIL ❌ BỊ LỆCH HOẶC GIẬT NHẢY!")}";
            Console.WriteLine(logLine);
            traceLogs.Add(logLine);
        }

        // Kết thúc cử chỉ pinch zoom (buông tay)
        readerControl.EndPinchZoom();
        await Task.Delay(500);

        // Kiểm tra sau khi buông tay xem có bị tua trang không
        int finalIndex = readerVm.CurrentPageIndex;
        bool finalMatched = (finalIndex == targetIndex);
        if (!finalMatched) allPassed = false;

        string finalLog = $"[PINCH RELEASE TEST] FinalPage={finalIndex} (Expected={targetIndex}) -> {(finalMatched ? "PASS ✅ GIỮ NGUYÊN TRANG" : "FAIL ❌ BỊ TUA KHI NHẤC TAY!")}";
        Console.WriteLine(finalLog);
        traceLogs.Add(finalLog);

        string resultSummary = allPassed
            ? "\n=========================================\n🎉 KẾT QUẢ TEST PINCH 20 LẦN: 100% PASS! KHÔNG HỀ GIẬT NHẢY HAY TUA TRANG!\n========================================="
            : "\n=========================================\n⚠️ KẾT QUẢ TEST PINCH 20 LẦN: CÓ BƯỚC BỊ GIẬT NHẢY HOẶC TUA TRANG!\n=========================================";
        Console.WriteLine(resultSummary);

        // Ghi log ra file
        string logDir = Path.Combine(AppContext.BaseDirectory, ".tmp");
        Directory.CreateDirectory(logDir);
        string logFile = Path.Combine(logDir, "test-pinch-thumuc.log");
        File.WriteAllLines(logFile, traceLogs.Concat(new[] { resultSummary }), System.Text.Encoding.UTF8);
        Console.WriteLine($"[TEST LOG SAVED] {logFile}");

        await Task.Delay(1000);
        desktop.Shutdown(allPassed ? 0 : 2);
    }

    private static async Task RunButtobiDragTestAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string zipPath = @"T:\[HDD T]\zfaststone capture\buttobi itto.zip";
        if (!File.Exists(zipPath))
        {
            Console.WriteLine($"[TEST ERROR] File not found: {zipPath}");
            desktop.Shutdown(1);
            return;
        }

        Console.WriteLine($"[TEST START] Loading buttobi itto.zip: {zipPath}");
        var scanner = new LibraryScannerService();
        var quickComic = scanner.CreateQuickComicFromPath(zipPath);
        if (quickComic == null || quickComic.Chapters.Count == 0)
        {
            Console.WriteLine("[TEST ERROR] No chapters found in zip archive!");
            desktop.Shutdown(1);
            return;
        }

        // Lọc ra các chapter có ít nhất 3 trang
        var candidateChapters = quickComic.Chapters.Where(c => c.TotalPages >= 3).ToList();
        Console.WriteLine($"[TEST] Total candidate chapters with >= 3 pages: {candidateChapters.Count}");

        if (candidateChapters.Count == 0)
        {
            Console.WriteLine("[TEST ERROR] No chapter has at least 3 pages!");
            desktop.Shutdown(1);
            return;
        }

        var readerVm = new ComicReaderViewModel();
        readerVm.PageLayoutMode = 0; // Chế độ Cuộn dọc (Vertical Scroll / Webtoon)
        readerVm.ZoomLevel = 1.0;

        var readerControl = new ComicReaderControl
        {
            DataContext = readerVm
        };

        var window = new Window
        {
            Title = "TEST PINCH ZOOM & DRAG - Buttobi Itto",
            Width = 480,
            Height = 854,
            Content = readerControl
        };

        desktop.MainWindow = window;
        window.Show();

        int totalCycles = Math.Min(20, candidateChapters.Count);
        Console.WriteLine($"[TEST] Starting 20 Cycles of Pinch Zoom + Drag + DoubleTap on 20 different chapters (Page 3)...\n");

        var traceLogs = new List<string>();
        bool allPassed = true;

        for (int cycle = 0; cycle < totalCycles; cycle++)
        {
            var chapter = candidateChapters[cycle];
            Console.WriteLine($"--- [CYCLE {cycle + 1:D2}/20] Chapter: '{chapter.Title}' (Total {chapter.TotalPages} pages) ---");

            // Nạp chapter
            await readerVm.InitializeAsync(quickComic, chapter);
            await Task.Delay(1000);

            // Chuyển tới Trang thứ 3 (Page Index 2)
            readerVm.CurrentPageIndex = 2;
            readerVm.RequestScrollToPage(2);
            await Task.Delay(1200);

            if (readerVm.CurrentPageIndex != 2)
            {
                readerVm.CurrentPageIndex = 2;
            }

            double centerX = window.Bounds.Width > 0 ? window.Bounds.Width / 2.0 : 240;
            double centerY = window.Bounds.Height > 0 ? window.Bounds.Height / 2.0 : 427;

            // 1. PINCH ZOOM PHÓNG TO (từ 100% lên 160%)
            readerControl.PerformPinchZoomStep(new Point(centerX - 100, centerY), new Point(centerX + 100, centerY));
            await Task.Delay(50);
            readerControl.PerformPinchZoomStep(new Point(centerX - 160, centerY), new Point(centerX + 160, centerY));
            await Task.Delay(100);
            readerControl.EndPinchZoom();
            await Task.Delay(150);

            double zoomAfterPinch = readerVm.ZoomLevel;
            bool pinchOk = (zoomAfterPinch >= 1.4) && !readerControl.IsPinching;

            // 2. HOLD AND DRAG ĐI XUỐNG 3 LẦN (kéo ngón tay từ Y=600 lên Y=350 để trang cuộn xuống)
            bool drag1Ok = true;
            for (int d = 1; d <= 3; d++)
            {
                var sv = readerControl.GetReaderScrollViewer();
                double preY = sv?.Offset.Y ?? 0;
                bool success = readerControl.PerformHoldAndDrag(new Point(centerX, centerY + 150), new Point(centerX, centerY - 150));
                await Task.Delay(100);
                double postY = sv?.Offset.Y ?? 0;

                // Kiểm tra: tuyệt đối không bị kích hoạt pinch và zoom không bị nhảy
                if (readerControl.IsPinching || Math.Abs(readerVm.ZoomLevel - zoomAfterPinch) > 0.05 || !success)
                {
                    drag1Ok = false;
                }
            }

            // 3. DOUBLE TAP ĐỂ ZOOM OUT (về 100%)
            readerControl.PerformDoubleTapZoom(new Point(centerX, centerY));
            await Task.Delay(250);
            double zoomAfterDoubleTap = readerVm.ZoomLevel;
            bool doubleTapOk = (Math.Abs(zoomAfterDoubleTap - 1.0) < 0.05) && !readerControl.IsPinching;

            // 4. DRAG ĐI XUỐNG 3 LẦN NỮA Ở MỨC ZOOM 100%
            bool drag2Ok = true;
            for (int d = 1; d <= 3; d++)
            {
                var sv = readerControl.GetReaderScrollViewer();
                double preY = sv?.Offset.Y ?? 0;
                bool success = readerControl.PerformHoldAndDrag(new Point(centerX, centerY + 150), new Point(centerX, centerY - 150));
                await Task.Delay(100);
                double postY = sv?.Offset.Y ?? 0;

                if (readerControl.IsPinching || Math.Abs(readerVm.ZoomLevel - 1.0) > 0.05 || !success)
                {
                    drag2Ok = false;
                }
            }

            bool cyclePass = pinchOk && drag1Ok && doubleTapOk && drag2Ok;
            if (!cyclePass) allPassed = false;

            string logLine = $"[CYCLE {cycle + 1:D2}/20] Chap='{chapter.Title}' | PinchZoom={zoomAfterPinch * 100:0}% (OK={pinchOk}) | Drag1x3=(OK={drag1Ok}) | DoubleTap={zoomAfterDoubleTap * 100:0}% (OK={doubleTapOk}) | Drag2x3=(OK={drag2Ok}) -> {(cyclePass ? "PASS ✅ HOÀN TOÀN KHÔNG BỊ TỰ PINCH ZOOM KHI DRAG" : "FAIL ❌ BỊ LỖI TỰ PINCH ZOOM HOẶC SAI ZOOM!")}";
            Console.WriteLine(logLine);
            traceLogs.Add(logLine);
        }

        string resultSummary = allPassed
            ? "\n=========================================\n🎉 KẾT QUẢ TEST BUTTOBI ITTO 20 CHU KỲ: 100% PASS! KHÔNG HỀ BỊ TỰ PINCH ZOOM KHI DRAG!\n========================================="
            : "\n=========================================\n⚠️ KẾT QUẢ TEST BUTTOBI ITTO 20 CHU KỲ: CÓ CHU KỲ BỊ TỰ PINCH ZOOM HOẶC LỖI!\n=========================================";
        Console.WriteLine(resultSummary);

        // Ghi log ra file
        string logDir = Path.Combine(AppContext.BaseDirectory, ".tmp");
        Directory.CreateDirectory(logDir);
        string logFile = Path.Combine(logDir, "test-buttobi-drag.log");
        File.WriteAllLines(logFile, traceLogs.Concat(new[] { resultSummary }), System.Text.Encoding.UTF8);
        Console.WriteLine($"[TEST LOG SAVED] {logFile}");

        await Task.Delay(1000);
        desktop.Shutdown(allPassed ? 0 : 2);
    }

    private static async Task RunDualSortNavTestAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Console.WriteLine("\n=========================================");
        Console.WriteLine("🚀 BẮT ĐẦU KIỂM THỬ CLI DUAL SORT FOLDER NAVIGATION (PREV & NEXT)");
        Console.WriteLine("=========================================\n");

        string testRoot = Path.Combine(AppContext.BaseDirectory, ".tmp", "test-dualsort-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            Directory.CreateDirectory(testRoot);
            string chap1 = Path.Combine(testRoot, "Chap 01");
            string chap2 = Path.Combine(testRoot, "Chap 02");
            string chap2Sorted = Path.Combine(testRoot, "Chap 02-sorted");
            string chap3 = Path.Combine(testRoot, "Chap 03");

            Directory.CreateDirectory(chap1);
            Directory.CreateDirectory(chap2);
            Directory.CreateDirectory(chap2Sorted);
            Directory.CreateDirectory(chap3);

            File.WriteAllText(Path.Combine(chap1, "001.jpg"), "dummy1");
            File.WriteAllText(Path.Combine(chap2, "001.jpg"), "dummy2");
            File.WriteAllText(Path.Combine(chap2Sorted, "001.jpg"), "dummy2sorted");
            File.WriteAllText(Path.Combine(chap3, "001.jpg"), "dummy3");

            var vm = new ViewModels.MainViewModel();
            vm.DualSortSourcePath = chap2;
            vm.IsAutoCreateSortedFolder = true;
            await vm.LoadBothSortFoldersAsync();

            bool allPassed = true;

            // 1. Test Previous từ Chap 02 -> Chap 01
            Console.WriteLine("[TEST 1] Thử nghiệm Previous Folder từ Chap 02...");
            await vm.DualSortPreviousFolderAsync();
            bool test1Ok = string.Equals(Path.GetFullPath(vm.DualSortSourcePath).TrimEnd('\\', '/'), Path.GetFullPath(chap1).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($" -> Kết quả: {Path.GetFileName(vm.DualSortSourcePath)} (Kỳ vọng: Chap 01) => {(test1Ok ? "PASS ✅" : "FAIL ❌")}");
            if (!test1Ok) allPassed = false;

            // 2. Test Previous tiếp từ Chap 01 (ở đầu danh sách)
            Console.WriteLine("[TEST 2] Thử nghiệm Previous Folder khi đang ở đầu danh sách (Chap 01)...");
            await vm.DualSortPreviousFolderAsync();
            bool test2Ok = string.Equals(Path.GetFullPath(vm.DualSortSourcePath).TrimEnd('\\', '/'), Path.GetFullPath(chap1).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($" -> Kết quả: {Path.GetFileName(vm.DualSortSourcePath)} | Status: {vm.DualSortStatusText} => {(test2Ok ? "PASS ✅" : "FAIL ❌")}");
            if (!test2Ok) allPassed = false;

            // 3. Test Next từ Chap 01 -> Chap 02
            Console.WriteLine("[TEST 3] Thử nghiệm Next Folder từ Chap 01...");
            await vm.DualSortNextFolderAsync();
            bool test3Ok = string.Equals(Path.GetFullPath(vm.DualSortSourcePath).TrimEnd('\\', '/'), Path.GetFullPath(chap2).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($" -> Kết quả: {Path.GetFileName(vm.DualSortSourcePath)} (Kỳ vọng: Chap 02) => {(test3Ok ? "PASS ✅" : "FAIL ❌")}");
            if (!test3Ok) allPassed = false;

            // 4. Test Next từ Chap 02 -> Chap 03 (phải bỏ qua Chap 02-sorted!)
            Console.WriteLine("[TEST 4] Thử nghiệm Next Folder từ Chap 02 (Phải bỏ qua Chap 02-sorted)...");
            await vm.DualSortNextFolderAsync();
            bool test4Ok = string.Equals(Path.GetFullPath(vm.DualSortSourcePath).TrimEnd('\\', '/'), Path.GetFullPath(chap3).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($" -> Kết quả: {Path.GetFileName(vm.DualSortSourcePath)} (Kỳ vọng: Chap 03) => {(test4Ok ? "PASS ✅" : "FAIL ❌")}");
            if (!test4Ok) allPassed = false;

            // 5. Test Next tiếp từ Chap 03 (ở cuối danh sách)
            Console.WriteLine("[TEST 5] Thử nghiệm Next Folder khi đang ở cuối danh sách (Chap 03)...");
            await vm.DualSortNextFolderAsync();
            bool test5Ok = string.Equals(Path.GetFullPath(vm.DualSortSourcePath).TrimEnd('\\', '/'), Path.GetFullPath(chap3).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($" -> Kết quả: {Path.GetFileName(vm.DualSortSourcePath)} | Status: {vm.DualSortStatusText} => {(test5Ok ? "PASS ✅" : "FAIL ❌")}");
            if (!test5Ok) allPassed = false;

            Console.WriteLine("\n=========================================");
            Console.WriteLine(allPassed ? "🎉 KẾT QUẢ TEST DUAL SORT NAVIGATION: 100% PASS!" : "❌ KẾT QUẢ TEST DUAL SORT NAVIGATION: CÓ LỖI!");
            Console.WriteLine("=========================================\n");

            Environment.Exit(allPassed ? 0 : 1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ EXCEPTION TRONG TEST: {ex.Message}");
            Environment.Exit(1);
        }
        finally
        {
            try
            {
                if (Directory.Exists(testRoot))
                    Directory.Delete(testRoot, true);
            }
            catch { }
        }
    }

    private static async Task RunInspectMediaTestAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Console.WriteLine("\n=========================================");
        Console.WriteLine("🔍 BẮT ĐẦU CHẨN ĐOÁN GIẢI MÃ THUMBNAIL & ANIMATION");
        Console.WriteLine("=========================================\n");

        string folder1 = @"T:\[HDD T]\zfaststone capture\test";
        string folder2 = @"T:\[HDD T]\zmedia\bulk image downloader\downloading\hentaiforce\uncen full color\2B9S - HentaiForce";

        var folders = new[] { folder1, folder2 };

        foreach (var fld in folders)
        {
            if (!Directory.Exists(fld))
            {
                Console.WriteLine($"[SKIP] Thư mục không tồn tại: {fld}");
                continue;
            }

            Console.WriteLine($"\n📁 ĐANG QUÉT THƯ MỤC: {fld}");
            var files = Directory.GetFiles(fld);
            foreach (var file in files)
            {
                string fn = Path.GetFileName(file);
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".webp" && ext != ".gif" && ext != ".bmp")
                    continue;

                Console.WriteLine($"\n--- TỆP: {fn} ({new FileInfo(file).Length / 1024.0:0.0} KB) ---");

                // 1. Thử nghiệm SKCodec
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var codec = SKCodec.Create(stream);
                    if (codec != null)
                    {
                        Console.WriteLine($" [SKCodec] Width={codec.Info.Width}, Height={codec.Info.Height}, FrameCount={codec.FrameCount}, EncodedFormat={codec.EncodedFormat}");

                        int maxDim = 240;
                        int origW = codec.Info.Width;
                        int origH = codec.Info.Height;
                        int thumbW = origW;
                        int thumbH = origH;
                        if (origW > maxDim || origH > maxDim)
                        {
                            if (origW >= origH)
                            {
                                thumbW = maxDim;
                                thumbH = Math.Max(1, (int)((float)origH / origW * maxDim));
                            }
                            else
                            {
                                thumbH = maxDim;
                                thumbW = Math.Max(1, (int)((float)origW / origH * maxDim));
                            }
                        }

                        // Thử decode theo cách cũ: SKBitmap.Decode(codec, decodeInfo)
                        var decodeInfo = new SKImageInfo(thumbW, thumbH, SKColorType.Rgba8888, SKAlphaType.Premul);
                        using var oldSkBmp = SKBitmap.Decode(codec, decodeInfo);
                        if (oldSkBmp != null)
                        {
                            Console.WriteLine($" [CÁCH CŨ SKBitmap.Decode] THÀNH CÔNG: {oldSkBmp.Width}x{oldSkBmp.Height}");
                        }
                        else
                        {
                            Console.WriteLine($" [CÁCH CŨ SKBitmap.Decode] THẤT BẠI (NULL) ❌ (Không hỗ trợ scale sang {thumbW}x{thumbH})");
                        }
                    }
                    else
                    {
                        Console.WriteLine($" [SKCodec] KHÔNG TẠO ĐƯỢC CODEC ❌");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($" [SKCodec] LỖI: {ex.Message}");
                }

                // 2. Thử nghiệm Avalonia Bitmap.DecodeToWidth
                try
                {
                    using var stream2 = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var avaBmp = Bitmap.DecodeToWidth(stream2, 240);
                    if (avaBmp != null)
                    {
                        Console.WriteLine($" [AVALONIA DecodeToWidth] THÀNH CÔNG ✅: {avaBmp.PixelSize.Width}x{avaBmp.PixelSize.Height}");
                    }
                    else
                    {
                        Console.WriteLine($" [AVALONIA DecodeToWidth] TRẢ VỀ NULL ❌");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($" [AVALONIA DecodeToWidth] LỖI: {ex.Message}");
                }
            }
        }

        Console.WriteLine("\n=========================================");
        Console.WriteLine("🏁 HOÀN TẤT CHẨN ĐOÁN!");
        Console.WriteLine("=========================================\n");

        Environment.Exit(0);
    }

    private static async Task RunDualSortKeepSelectedTestAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Console.WriteLine("\n=========================================");
        Console.WriteLine("🧪 KIỂM THỬ: DUAL PICTURE SORT - CHỈ GIỮ ĐÃ CHỌN (KEEP SELECTED ONLY)");
        Console.WriteLine("=========================================\n");

        string testRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".tmp", "test_keep_selected_" + Guid.NewGuid().ToString("N"));
        string parentDir = Path.Combine(testRoot, "ParentFolder");
        string childDir = Path.Combine(parentDir, "ParentFolder-sorted");

        try
        {
            Directory.CreateDirectory(parentDir);
            Directory.CreateDirectory(childDir);

            // Tạo các file giả lập
            string p1 = Path.Combine(parentDir, "p1.jpg");
            string p2 = Path.Combine(parentDir, "p2.jpg");
            string p3 = Path.Combine(parentDir, "p3.jpg");
            string c1 = Path.Combine(childDir, "c1.jpg");
            string c2 = Path.Combine(childDir, "c2.jpg");

            File.WriteAllText(p1, "parent image 1");
            File.WriteAllText(p2, "parent image 2");
            File.WriteAllText(p3, "parent image 3");
            File.WriteAllText(c1, "child image 1");
            File.WriteAllText(c2, "child image 2");

            Console.WriteLine($"[1] Đã tạo thư mục test:\n  - Gốc: {parentDir} (3 file: p1, p2, p3)\n  - Lọc: {childDir} (2 file: c1, c2)");

            var vm = new MainViewModel();
            vm.DualSortSourcePath = parentDir;
            vm.DualSortSortedPath = childDir;

            await vm.LoadBothSortFoldersAsync();
            Console.WriteLine($"[2] Nạp thư mục: Gốc={vm.DualSortSourceItems.Count} ảnh, Đã lọc={vm.DualSortSortedItems.Count} ảnh.");

            // Chọn p1 (ở gốc) và c2 (ở lọc)
            var p1Item = vm.DualSortSourceItems.FirstOrDefault(x => Path.GetFileName(x.FilePath) == "p1.jpg");
            var c2Item = vm.DualSortSortedItems.FirstOrDefault(x => Path.GetFileName(x.FilePath) == "c2.jpg");

            if (p1Item == null || c2Item == null)
            {
                Console.WriteLine("❌ LỖI: Không tìm thấy p1.jpg hoặc c2.jpg trong VM!");
                Environment.Exit(1);
                return;
            }

            p1Item.IsSelected = true;
            c2Item.IsSelected = true;
            Console.WriteLine("[3] Đã chọn: p1.jpg (ở Gốc) và c2.jpg (ở Lọc).");

            Console.WriteLine("[4] Gọi KeepSelectedOnlyAsync()...");
            await vm.KeepSelectedOnlyAsync();

            // Kiểm tra kết quả
            bool childDirExists = Directory.Exists(childDir);
            var remainingFiles = Directory.GetFiles(parentDir, "*.*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).ToList();

            Console.WriteLine($"[5] Kết quả sau khi chạy:");
            Console.WriteLine($"    - Thư mục Child còn tồn tại? {childDirExists} (Kỳ vọng: False)");
            Console.WriteLine($"    - Các file còn lại trong Parent: {string.Join(", ", remainingFiles)} (Kỳ vọng: p1.jpg, c2.jpg)");

            if (!childDirExists && remainingFiles.Count == 2 && remainingFiles.Contains("p1.jpg") && remainingFiles.Contains("c2.jpg"))
            {
                Console.WriteLine("\n✅ KIỂM THỬ THÀNH CÔNG 100%: 'Chỉ giữ đã chọn' hoạt động chính xác tuyệt đối!");
                Console.WriteLine("   - File không chọn (p2, p3, c1) đã được dọn sạch.");
                Console.WriteLine("   - File được chọn ở child (c2) đã được move an toàn về parent.");
                Console.WriteLine("   - File được chọn ở parent (p1) được giữ nguyên.");
                Console.WriteLine("   - Thư mục child đã được xóa sạch sẽ.");
            }
            else
            {
                Console.WriteLine("\n❌ KIỂM THỬ THẤT BẠI: Kết quả không khớp kỳ vọng!");
                Environment.Exit(1);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ EXCEPTION: {ex.Message}\n{ex.StackTrace}");
            Environment.Exit(1);
        }
        finally
        {
            try
            {
                if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
            }
            catch { }
        }

        Environment.Exit(0);
    }

    private static bool IsReadableComicOrMediaFileOrDirectory(string path)
    {
        if (Directory.Exists(path)) return true;
        if (!File.Exists(path)) return false;
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return false;
        return LibraryScannerService.ValidImageExtensions.Contains(ext)
            || LibraryScannerService.ValidArchiveExtensions.Contains(ext)
            || LibraryScannerService.ValidMobiExtensions.Contains(ext)
            || string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase);
    }
}
