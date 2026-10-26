using System;
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace ComicDownloaderGMTPC.Android
{
    [Application]
    public class Application : AvaloniaAndroidApplication<App>
    {
        protected Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        public override void OnCreate()
        {
            base.OnCreate();
            SetupGlobalCrashProtection();
        }

        private static void SetupGlobalCrashProtection()
        {
            AndroidEnvironment.UnhandledExceptionRaiser += (sender, args) =>
            {
                try
                {
                    global::Android.Util.Log.Error("ComicGMTPC_Crash", $"[AndroidEnvironment Unhandled] {args.Exception}");
                    WriteCrashLog("AndroidEnvironment", args.Exception?.ToString());
                    args.Handled = true;
                }
                catch { }
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                try
                {
                    var ex = args.ExceptionObject as Exception;
                    global::Android.Util.Log.Error("ComicGMTPC_Crash", $"[AppDomain Unhandled] {ex?.Message}\n{ex?.StackTrace}");
                    WriteCrashLog("AppDomain", ex?.ToString());
                }
                catch { }
            };

            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                try
                {
                    global::Android.Util.Log.Warn("ComicGMTPC_Task", $"[UnobservedTask] {args.Exception?.Message}");
                    args.SetObserved();
                }
                catch { }
            };
        }

        private static void WriteCrashLog(string source, string? details)
        {
            try
            {
                string log = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] [{source}]\n{details}\n\n";
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

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            return base.CustomizeAppBuilder(builder)
                .WithInterFont();
        }
    }
}
