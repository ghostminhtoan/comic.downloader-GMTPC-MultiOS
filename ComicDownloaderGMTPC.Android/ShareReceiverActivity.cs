using System;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.Android;

[Activity(
    Label = "Thêm vào Comic Downloader GMTPC",
    Icon = "@drawable/icon",
    Theme = "@android:style/Theme.Translucent.NoTitleBar",
    Exported = true)]
[IntentFilter(
    new[] { Intent.ActionSend },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "text/plain")]
public class ShareReceiverActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        try
        {
            if (Intent?.Action == Intent.ActionSend)
            {
                string? sharedText = Intent.GetStringExtra(Intent.ExtraText);
                if (string.IsNullOrWhiteSpace(sharedText) && Intent.ClipData != null && Intent.ClipData.ItemCount > 0)
                {
                    sharedText = Intent.ClipData.GetItemAt(0)?.Text;
                }

                if (!string.IsNullOrWhiteSpace(sharedText) &&
                    (sharedText.Contains("http://", StringComparison.OrdinalIgnoreCase) || 
                     sharedText.Contains("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    BackgroundExecutionService.NativeOnClipboardTextChanged?.Invoke(sharedText);
                    Toast.MakeText(this, "📥 Đã nạp link truyện vào hàng đợi tải!", ToastLength.Short)?.Show();
                }
                else
                {
                    Toast.MakeText(this, "⚠️ Không tìm thấy liên kết truyện hợp lệ trong nội dung chia sẻ.", ToastLength.Short)?.Show();
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC_Share", $"Lỗi nhận Share: {ex.Message}");
        }

        Finish();
    }
}

