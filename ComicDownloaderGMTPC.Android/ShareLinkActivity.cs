using System;
using System.Collections.Generic;
using System.Text;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
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
    TaskAffinity = "",
    LaunchMode = LaunchMode.Multiple,
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
    public const string ActionQuickAddFromClipboard = "com.CompanyName.ComicDownloaderGMTPC.action.QUICK_ADD_CLIPBOARD";

    private bool _isClipboardHandled = false;
    private readonly Handler _handler = new(Looper.MainLooper!);
    private Java.Lang.Runnable? _timeoutFallbackRunnable;

    public static void LaunchQuickAddFromClipboard(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(ShareLinkActivity));
            intent.SetAction(ActionQuickAddFromClipboard);
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.MultipleTask | ActivityFlags.NoAnimation | ActivityFlags.ExcludeFromRecents);
            context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ComicGMTPC_Share", $"Không thể mở ShareLinkActivity QuickAdd: {ex.Message}");
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        try
        {
            if (string.Equals(Intent?.Action, ActionQuickAddFromClipboard, StringComparison.Ordinal))
            {
                DisableActivityTransitions();

                // Thiết lập View vô hình để Activity được trao Window Focus từ hệ thống Android 10+
                var dummyView = new global::Android.Views.View(this)
                {
                    Focusable = true,
                    FocusableInTouchMode = true
                };
                SetContentView(dummyView);
                dummyView.RequestFocus();

                // Lên lịch fallback dự phòng sau 350ms trong trường hợp OEM ROM hoãn OnWindowFocusChanged
                _timeoutFallbackRunnable = new Java.Lang.Runnable(() =>
                {
                    if (!_isClipboardHandled && !IsFinishing && !IsDestroyed)
                    {
                        ProcessClipboardQuickAdd();
                    }
                });
                _handler.PostDelayed(_timeoutFallbackRunnable, 350);
                return;
            }

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
            if (!string.Equals(Intent?.Action, ActionQuickAddFromClipboard, StringComparison.Ordinal))
            {
                Finish();
            }
        }
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);

        if (hasFocus && string.Equals(Intent?.Action, ActionQuickAddFromClipboard, StringComparison.Ordinal))
        {
            if (!_isClipboardHandled)
            {
                if (_timeoutFallbackRunnable != null)
                {
                    _handler.RemoveCallbacks(_timeoutFallbackRunnable);
                    _timeoutFallbackRunnable = null;
                }
                // Delay 40ms để bảo đảm WindowManagerService và ClipboardService hoàn tất cấp quyền truy cập
                _handler.PostDelayed(ProcessClipboardQuickAdd, 40);
            }
        }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_timeoutFallbackRunnable != null)
        {
            _handler.RemoveCallbacks(_timeoutFallbackRunnable);
            _timeoutFallbackRunnable = null;
        }
    }

    private void ProcessClipboardQuickAdd()
    {
        if (_isClipboardHandled) return;
        _isClipboardHandled = true;

        try
        {
            string? foundUrlOrText = ExtractBestUrlFromClipboard();

            if (!string.IsNullOrWhiteSpace(foundUrlOrText))
            {
                App.EnqueueSharedLinkText(foundUrlOrText);
                ShareLinkShortcutHelper.ReportDirectShareUsed(ApplicationContext ?? this);

                try
                {
                    if (App.IsMainViewReady && MainActivity.CurrentInstance != null)
                    {
                        MainActivity.CurrentInstance.RunOnUiThread(() =>
                        {
                            var vm = App.TryGetSharedMainViewModel();
                            if (vm != null)
                            {
                                _ = vm.DrainPendingSharedLinksAsync();
                            }
                        });
                    }
                }
                catch { }

                Toast.MakeText(this, "📥 Đã nạp link vào hàng chờ tải!", ToastLength.Short)?.Show();
            }
            else
            {
                Toast.MakeText(this, "📋 Bộ nhớ tạm không có link truyện (URL)", ToastLength.Short)?.Show();
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ComicGMTPC_Share", $"Lỗi đọc clipboard: {ex.Message}");
        }
        finally
        {
            try
            {
                MoveTaskToBack(true);
                FinishAndRemoveTask();
                DisableActivityTransitions();
            }
            catch
            {
                try { Finish(); } catch { }
            }
        }
    }

    private void DisableActivityTransitions()
    {
        try
        {
#pragma warning disable CA1422
            OverridePendingTransition(0, 0);
#pragma warning restore CA1422
        }
        catch { }
    }

    private string? ExtractBestUrlFromClipboard()
    {
        try
        {
            var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
            if (clipboard == null || !clipboard.HasPrimaryClip || clipboard.PrimaryClip == null || clipboard.PrimaryClip.ItemCount == 0)
            {
                return null;
            }

            var clipData = clipboard.PrimaryClip;
            var parts = new List<string>();

            for (int i = 0; i < clipData.ItemCount; i++)
            {
                var item = clipData.GetItemAt(i);
                if (item == null) continue;

                if (!string.IsNullOrWhiteSpace(item.Text))
                {
                    parts.Add(item.Text);
                }
                string? uriStr = item.Uri?.ToString();
                if (!string.IsNullOrWhiteSpace(uriStr))
                {
                    parts.Add(uriStr);
                }
                if (!string.IsNullOrWhiteSpace(item.HtmlText))
                {
                    parts.Add(item.HtmlText);
                }
                try
                {
                    string? coerced = item.CoerceToText(this)?.ToString();
                    if (!string.IsNullOrWhiteSpace(coerced) && !parts.Contains(coerced))
                    {
                        parts.Add(coerced);
                    }
                }
                catch { }
            }

            if (parts.Count == 0) return null;

            var urlRegex = new System.Text.RegularExpressions.Regex(@"https?://[^\s<>""'()\[\]{}]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var foundUrls = new List<string>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var part in parts)
            {
                var matches = urlRegex.Matches(part);
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    if (m.Success)
                    {
                        string url = m.Value.Trim().TrimEnd('.', ',', ';', '!', '?', ')', ']');
                        if (seenUrls.Add(url))
                        {
                            foundUrls.Add(url);
                        }
                    }
                }
            }

            if (foundUrls.Count > 0)
            {
                return string.Join(System.Environment.NewLine, foundUrls);
            }

            // Fallback nếu người dùng copy link không chứa tiền tố http/https
            foreach (var part in parts)
            {
                string trimmed = part.Trim();
                if (trimmed.Contains(".") && !trimmed.Contains(" ") &&
                    (trimmed.Contains("/manga") || trimmed.Contains("/truyen") || trimmed.Contains("cuutruyen") || trimmed.Contains("nettruyen") || trimmed.Contains("blogtruyen") || trimmed.Contains("truyenqq") || trimmed.Contains("cmanga")))
                {
                    return "https://" + trimmed.TrimStart('/', ':');
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC_Share", $"Lỗi trích xuất URL từ clipboard: {ex.Message}");
            return null;
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
