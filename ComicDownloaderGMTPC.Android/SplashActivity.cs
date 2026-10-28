using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace ComicDownloaderGMTPC.Android;

[Activity(
    Label = "ComicDownloaderGMTPC",
    Theme = "@style/MyTheme.Splash",
    Icon = "@drawable/icon",
    MainLauncher = true,
    NoHistory = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.Density | ConfigChanges.FontScale)]
public class SplashActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        try
        {
            // Nếu có crash ngay từ tầng Application.OnCreate, mở thẳng giao diện báo lỗi
            if (ApplicationCrashLogger.StartupException != null)
            {
                var crashIntent = new Intent(this, typeof(CrashReportActivity));
                crashIntent.PutExtra(CrashReportActivity.ExtraCrashSource, "Application.Startup");
                crashIntent.PutExtra(CrashReportActivity.ExtraCrashDetails, ApplicationCrashLogger.StartupException.ToString());
                crashIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
                StartActivity(crashIntent);
                Finish();
                return;
            }

            // Chuyển tiếp mượt mà sang MainActivity
            var mainIntent = new Intent(this, typeof(MainActivity));
            if (Intent?.Extras != null)
            {
                mainIntent.PutExtras(Intent.Extras);
            }
            if (Intent?.Data != null)
            {
                mainIntent.SetData(Intent.Data);
            }
            mainIntent.AddFlags(ActivityFlags.ForwardResult);
            StartActivity(mainIntent);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ComicGMTPC_Crash", $"[SplashActivity Crash] {ex}");
            ApplicationCrashLogger.WriteCrashLog("SplashActivity.OnCreate", ex.ToString());
            
            try
            {
                var crashIntent = new Intent(this, typeof(CrashReportActivity));
                crashIntent.PutExtra(CrashReportActivity.ExtraCrashSource, "SplashActivity");
                crashIntent.PutExtra(CrashReportActivity.ExtraCrashDetails, ex.ToString());
                crashIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
                StartActivity(crashIntent);
            }
            catch { }
        }
        finally
        {
            Finish();
        }
    }
}

