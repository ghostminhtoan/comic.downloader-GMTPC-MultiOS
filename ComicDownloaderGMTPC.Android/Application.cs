using System;
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace ComicDownloaderGMTPC.Android
{
    public static class ApplicationCrashLogger
    {
        public static Exception? StartupException { get; set; }

        public static void WriteCrashLog(string source, string? details)
        {
            try
            {
                string log = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] [{source}]\n{details}\n\n";

                // 1. Ghi vào Context.GetExternalFilesDir(null) - /storage/emulated/0/Android/data/com.CompanyName.ComicDownloaderGMTPC/files/crash_log.txt
                try
                {
                    var ctx = global::Android.App.Application.Context;
                    var extFilesDir = ctx?.GetExternalFilesDir(null)?.AbsolutePath;
                    if (!string.IsNullOrEmpty(extFilesDir))
                    {
                        if (!Directory.Exists(extFilesDir)) Directory.CreateDirectory(extFilesDir);
                        var appPath = Path.Combine(extFilesDir, "crash_log.txt");
                        File.AppendAllText(appPath, log);
                    }
                }
                catch { }

                // 2. Ghi vào Environment.GetFolderPath(SpecialFolder.Personal) - Internal storage của app, 100% không cần quyền
                try
                {
                    var internalDir = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                    if (!string.IsNullOrEmpty(internalDir))
                    {
                        if (!Directory.Exists(internalDir)) Directory.CreateDirectory(internalDir);
                        var internalPath = Path.Combine(internalDir, "crash_log.txt");
                        File.AppendAllText(internalPath, log);
                    }
                }
                catch { }

                // 3. Ghi vào thư mục công cộng nếu đã có quyền lưu trữ
                string primaryPath = "/storage/emulated/0/Download/autosaveComicDownloaderGMTPC/crash_log.txt";
                string fallbackPath = "/storage/emulated/0/Download/ComicGMTPC_Crash.txt";
                try
                {
                    string dir = Path.GetDirectoryName(primaryPath)!;
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(primaryPath, log);
                    return;
                }
                catch { }
                try
                {
                    File.AppendAllText(fallbackPath, log);
                }
                catch { }
            }
            catch { }
        }
    }

    [Application]
#pragma warning disable CS0618
    [global::Android.Runtime.Preserve(AllMembers = true)]
#pragma warning restore CS0618
    public class MainApplication : AvaloniaAndroidApplication<App>
    {
        public MainApplication(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            return base.CustomizeAppBuilder(builder)
                .WithInterFont();
        }
    }
}
