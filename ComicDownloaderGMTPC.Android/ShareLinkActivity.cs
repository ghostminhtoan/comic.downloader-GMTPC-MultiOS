using System;
using System.Collections.Generic;
using System.Text;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.Content.PM;
using AndroidX.Core.Graphics.Drawable;

namespace ComicDownloaderGMTPC.Android;

/// <summary>
/// Quản lý Direct Share Shortcut (Sharing Shortcuts API) để ứng dụng luôn hiển thị ngay
/// ở hàng chia sẻ trực tiếp trên cùng của Android Sharesheet mà không cần bấm vào "Khác".
/// </summary>
public static class ShareLinkShortcutHelper
{
    public const string DirectShareShortcutId = "direct_share_comic_link";
    public const string ShareTargetCategory = "com.CompanyName.ComicDownloaderGMTPC.category.SHARE_LINK";
    public const string DirectSharePersonKey = "comic_downloader_gmtpc_share_target";

    public static void PublishDirectShareShortcut(Context? context)
    {
        if (context == null || !OperatingSystem.IsAndroidVersionAtLeast(25))
        {
            return;
        }

        try
        {
            var icon = IconCompat.CreateWithResource(context, Resource.Drawable.Icon);

            var personBuilder = new global::AndroidX.Core.App.Person.Builder();
            personBuilder.SetName("ComicDownloader");
            personBuilder.SetKey(DirectSharePersonKey);
            personBuilder.SetIcon(icon);
            personBuilder.SetImportant(true);
            var person = personBuilder.Build();

            var shortcutIntent = new Intent(context, typeof(ShareLinkActivity));
            shortcutIntent.SetAction(Intent.ActionSend);
            shortcutIntent.SetType("text/plain");

            var shortcutBuilder = new ShortcutInfoCompat.Builder(context, DirectShareShortcutId);
            shortcutBuilder.SetShortLabel("ComicDownloader");
            shortcutBuilder.SetLongLabel("ComicDownloaderGMTPC");
            shortcutBuilder.SetIcon(icon);
            shortcutBuilder.SetIntent(shortcutIntent);
            shortcutBuilder.SetCategories(new[] { ShareTargetCategory });
            shortcutBuilder.SetLongLived(true);
            shortcutBuilder.SetRank(0);
            shortcutBuilder.SetPerson(person);
            shortcutBuilder.SetExcludedFromSurfaces(ShortcutInfoCompat.SurfaceLauncher);
            var shortcut = shortcutBuilder.Build();

            if (shortcut != null)
            {
                ShortcutManagerCompat.PushDynamicShortcut(context, shortcut);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC_Share", $"Không thể đăng ký Direct Share shortcut: {ex.Message}");
        }
    }

    public static void ReportDirectShareUsed(Context? context)
    {
        if (context == null || !OperatingSystem.IsAndroidVersionAtLeast(25))
        {
            return;
        }

        try
        {
            ShortcutManagerCompat.ReportShortcutUsed(context, DirectShareShortcutId);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC_Share", $"Không thể ghi nhận sử dụng Direct Share shortcut: {ex.Message}");
        }
    }
}

/// <summary>
/// Activity trung gian siêu nhẹ nhận Intent chia sẻ liên kết (ACTION_SEND / ACTION_SEND_MULTIPLE)
/// từ trình duyệt và các ứng dụng khác, rồi chuyển thẳng vào MainActivity để phân tích link truyện.
/// </summary>
[Activity(
    Name = "com.CompanyName.ComicDownloaderGMTPC.ShareLinkActivity",
    Label = "ComicDownloaderGMTPC",
    Icon = "@drawable/icon",
    Theme = "@android:style/Theme.Translucent.NoTitleBar",
    NoHistory = true,
    ExcludeFromRecents = true,
    Exported = true)]
[IntentFilter(
    new[] { Intent.ActionSend },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataMimeType = "text/plain")]
[IntentFilter(
    new[] { Intent.ActionSend },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataMimeType = "text/uri-list")]
[IntentFilter(
    new[] { Intent.ActionSendMultiple },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "text/plain")]
public class ShareLinkActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        try
        {
            string? sharedText = ExtractSharedTextFromIntent(this, Intent);

            ShareLinkShortcutHelper.ReportDirectShareUsed(ApplicationContext ?? this);
            ShareLinkShortcutHelper.PublishDirectShareShortcut(ApplicationContext ?? this);

            var mainIntent = new Intent(this, typeof(MainActivity));
            mainIntent.SetAction(MainActivity.ActionReceiveSharedLink);
            if (!string.IsNullOrWhiteSpace(sharedText))
            {
                mainIntent.PutExtra(MainActivity.ExtraSharedLinkText, sharedText);
            }
            mainIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            StartActivity(mainIntent);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ComicGMTPC_Share", $"Lỗi xử lý ShareLinkActivity: {ex}");
        }
        finally
        {
            Finish();
        }
    }

    public static string? ExtractSharedTextFromIntent(Context context, Intent? intent)
    {
        if (intent == null)
        {
            return null;
        }

        var parts = new List<string>();

        if (string.Equals(intent.Action, Intent.ActionSendMultiple, StringComparison.Ordinal))
        {
            var multiList = intent.GetCharSequenceArrayListExtra(Intent.ExtraText);
            if (multiList != null)
            {
                foreach (var item in multiList)
                {
                    string? s = item?.ToString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        parts.Add(s.Trim());
                    }
                }
            }
        }

        string? singleExtraText = intent.GetCharSequenceExtra(Intent.ExtraText)?.ToString();
        if (!string.IsNullOrWhiteSpace(singleExtraText))
        {
            parts.Add(singleExtraText.Trim());
        }

        if (intent.ClipData != null && intent.ClipData.ItemCount > 0)
        {
            for (int i = 0; i < intent.ClipData.ItemCount; i++)
            {
                var clipItem = intent.ClipData.GetItemAt(i);
                if (clipItem == null) continue;

                string? clipText = clipItem.Text ?? clipItem.Uri?.ToString() ?? clipItem.CoerceToText(context)?.ToString();
                if (!string.IsNullOrWhiteSpace(clipText))
                {
                    parts.Add(clipText.Trim());
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(intent.DataString))
        {
            parts.Add(intent.DataString.Trim());
        }

        if (parts.Count == 0)
        {
            string? subject = intent.GetCharSequenceExtra(Intent.ExtraSubject)?.ToString();
            if (!string.IsNullOrWhiteSpace(subject))
            {
                parts.Add(subject.Trim());
            }
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (unique.Add(p))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(p);
            }
        }

        return sb.ToString();
    }
}
