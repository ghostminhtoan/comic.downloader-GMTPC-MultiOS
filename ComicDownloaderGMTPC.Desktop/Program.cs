using System;
using System.IO;
using System.Linq;
using Avalonia;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
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
