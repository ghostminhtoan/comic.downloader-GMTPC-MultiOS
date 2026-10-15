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
    public static MainViewModel SharedMainViewModel { get; } = new MainViewModel();

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
            if (desktop.Args != null && desktop.Args.Any(a => string.Equals(a, "--test-vertical-doubletap", StringComparison.OrdinalIgnoreCase)))
            {
                base.OnFrameworkInitializationCompleted();
                _ = RunVerticalDoubleTapTestAsync(desktop);
                return;
            }

            desktop.ShutdownRequested += (_, _) =>
            {
                try { SharedMainViewModel.FlushAutoSaveImmediately(); } catch { }
            };
            desktop.Exit += (_, _) =>
            {
                try { SharedMainViewModel.FlushAutoSaveImmediately(); } catch { }
            };
            desktop.MainWindow = new MainWindow
            {
                DataContext = SharedMainViewModel
            };

            // Khởi tạo và quét trạng thái liên kết tệp hệ thống trên Desktop
            SharedMainViewModel.InitFileAssociation();

            // Mở tệp trực tiếp nếu được kích hoạt qua dòng lệnh (double-click hoặc context menu)
            if (desktop.Args != null && desktop.Args.Length > 0)
            {
                string? candidate = desktop.Args.FirstOrDefault(a =>
                    !a.StartsWith("--") &&
                    !string.IsNullOrWhiteSpace(a));

                if (!string.IsNullOrEmpty(candidate))
                {
                    string cleanPath = candidate.Trim('\"', '\'');
                    if (File.Exists(cleanPath) && IsReadableComicOrMediaFile(cleanPath))
                    {
                        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                        {
                            await Task.Delay(400);
                            SharedMainViewModel.SelectedRootTabIndex = 4;
                            await SharedMainViewModel.LibraryVm.OpenStandaloneFileDirectlyAsync(cleanPath);
                        });
                    }
                }
            }
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

    private static bool IsReadableComicOrMediaFile(string path)
    {
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return false;
        return LibraryScannerService.ValidImageExtensions.Contains(ext)
            || LibraryScannerService.ValidArchiveExtensions.Contains(ext)
            || LibraryScannerService.ValidMobiExtensions.Contains(ext)
            || string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase);
    }
}