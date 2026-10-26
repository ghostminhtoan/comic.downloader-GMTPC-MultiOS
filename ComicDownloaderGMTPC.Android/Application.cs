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
        public static void WriteCrashLog(string source, string? details)
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
    }

    [Application]
    public class Application : AvaloniaAndroidApplication<App>
    {
        // Phải là public để Java Android Runtime có thể instantiate khi process khởi động
        public Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        public override void OnCreate()
        {
            SetupGlobalCrashProtection();
            try
            {
                base.OnCreate();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("ComicGMTPC_Crash", $"[Application.OnCreate Crash] {ex}");
                ApplicationCrashLogger.WriteCrashLog("Application.OnCreate", ex.ToString());
            }
        }

        private static void SetupGlobalCrashProtection()
        {
            AndroidEnvironment.UnhandledExceptionRaiser += (sender, args) =>
            {
                try
                {
                    global::Android.Util.Log.Error("ComicGMTPC_Crash", $"[AndroidEnvironment Unhandled] {args.Exception}");
                    ApplicationCrashLogger.WriteCrashLog("AndroidEnvironment", args.Exception?.ToString());
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
                    ApplicationCrashLogger.WriteCrashLog("AppDomain", ex?.ToString());
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

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            return base.CustomizeAppBuilder(builder)
                .WithInterFont();
        }
    }
}
