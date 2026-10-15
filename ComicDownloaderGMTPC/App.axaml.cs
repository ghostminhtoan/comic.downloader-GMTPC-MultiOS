using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.Services;
using ComicDownloaderGMTPC.ViewModels;
using ComicDownloaderGMTPC.Views;

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