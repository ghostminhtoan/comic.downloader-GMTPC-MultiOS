using System;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace ComicDownloaderGMTPC.Android;

[Activity(
    Label = "Comic Downloader GMTPC - Báo Lỗi Khởi Động",
    Theme = "@style/MyTheme.NoActionBar",
    Exported = false,
    ConfigurationChanges = global::Android.Content.PM.ConfigChanges.Orientation | global::Android.Content.PM.ConfigChanges.ScreenSize)]
public class CrashReportActivity : Activity
{
    public const string ExtraCrashDetails = "extra_crash_details";
    public const string ExtraCrashSource = "extra_crash_source";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        try
        {
            string errorDetails = Intent?.GetStringExtra(ExtraCrashDetails) ?? "Không có chi tiết lỗi.";
            string errorSource = Intent?.GetStringExtra(ExtraCrashSource) ?? "Khởi động";

            var scrollView = new ScrollView(this)
            {
                LayoutParameters = new ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent)
            };
            scrollView.SetBackgroundColor(global::Android.Graphics.Color.Rgb(15, 23, 42)); // Nền Dark sang trọng
            scrollView.SetPadding(40, 60, 40, 60);

            var layout = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical,
                LayoutParameters = new LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MatchParent,
                    LinearLayout.LayoutParams.WrapContent)
            };

            var titleView = new TextView(this)
            {
                Text = "⚠️ COMIC DOWNLOADER GMTPC",
                TextSize = 20,
                Typeface = global::Android.Graphics.Typeface.DefaultBold
            };
            titleView.SetTextColor(global::Android.Graphics.Color.Rgb(248, 113, 113));
            titleView.SetPadding(0, 0, 0, 10);
            layout.AddView(titleView);

            var subtitleView = new TextView(this)
            {
                Text = $"BÁO CÁO LỖI KHỞI ĐỘNG [{errorSource}]",
                TextSize = 14,
                Typeface = global::Android.Graphics.Typeface.DefaultBold
            };
            subtitleView.SetTextColor(global::Android.Graphics.Color.Rgb(251, 191, 36));
            subtitleView.SetPadding(0, 0, 0, 20);
            layout.AddView(subtitleView);

            var descView = new TextView(this)
            {
                Text = "Ứng dụng gặp sự cố khi khởi tạo. Toàn bộ chi tiết lỗi đã được ghi tự động vào tệp crash_log.txt.\nBạn có thể sao chép thông tin bên dưới để gửi cho đội ngũ phát triển:",
                TextSize = 13
            };
            descView.SetTextColor(global::Android.Graphics.Color.Rgb(203, 213, 225));
            descView.SetPadding(0, 0, 0, 30);
            layout.AddView(descView);

            var copyBtn = new Button(this)
            {
                Text = "📋 SAO CHÉP TOÀN BỘ LỖI VÀO CLIPBOARD"
            };
            copyBtn.SetBackgroundColor(global::Android.Graphics.Color.Rgb(2, 132, 199));
            copyBtn.SetTextColor(global::Android.Graphics.Color.White);
            copyBtn.Click += (_, _) =>
            {
                try
                {
                    var cm = (ClipboardManager?)GetSystemService(ClipboardService);
                    if (cm != null)
                    {
                        var clip = ClipData.NewPlainText("ComicGMTPC_Crash", errorDetails);
                        cm.PrimaryClip = clip;
                        Toast.MakeText(this, "Đã sao chép chi tiết lỗi vào Clipboard!", ToastLength.Short)?.Show();
                    }
                }
                catch { }
            };
            layout.AddView(copyBtn);

            var restartBtn = new Button(this)
            {
                Text = "🔄 THỬ KHỞI ĐỘNG LẠI ỨNG DỤNG"
            };
            restartBtn.SetBackgroundColor(global::Android.Graphics.Color.Rgb(16, 185, 129));
            restartBtn.SetTextColor(global::Android.Graphics.Color.White);
            restartBtn.Click += (_, _) =>
            {
                try
                {
                    var intent = PackageManager?.GetLaunchIntentForPackage(PackageName ?? "");
                    if (intent != null)
                    {
                        intent.AddFlags(ActivityFlags.ClearTop | ActivityFlags.NewTask);
                        StartActivity(intent);
                        Finish();
                    }
                }
                catch { }
            };
            var btnParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent,
                LinearLayout.LayoutParams.WrapContent);
            btnParams.SetMargins(0, 20, 0, 30);
            restartBtn.LayoutParameters = btnParams;
            layout.AddView(restartBtn);

            var errorDetailView = new TextView(this)
            {
                Text = errorDetails,
                TextSize = 11,
                Typeface = global::Android.Graphics.Typeface.Monospace
            };
            errorDetailView.SetTextColor(global::Android.Graphics.Color.Rgb(254, 202, 202));
            errorDetailView.SetBackgroundColor(global::Android.Graphics.Color.Rgb(30, 41, 59));
            errorDetailView.SetPadding(20, 20, 20, 20);
            layout.AddView(errorDetailView);

            scrollView.AddView(layout);
            SetContentView(scrollView);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ComicGMTPC_Crash", $"CrashReportActivity error: {ex}");
        }
    }
}
