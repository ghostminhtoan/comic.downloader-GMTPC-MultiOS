using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;

namespace ComicDownloaderGMTPC.Android;

[Service(ForegroundServiceType = ForegroundService.TypeDataSync, Exported = false)]
public class ComicBackgroundService : Service
{
    public const string ChannelId = "comic_gmtpc_bg_channel";
    public const int NotificationId = 1002;
    public const string ActionStartOrUpdate = "ACTION_START_OR_UPDATE";
    public const string ActionStop = "ACTION_STOP";
    public const string ExtraTitle = "EXTRA_TITLE";
    public const string ExtraText = "EXTRA_TEXT";
    public const string ExtraProgress = "EXTRA_PROGRESS";

    private PowerManager.WakeLock? _wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();
        AcquireWakeLock();
    }

    public static bool IsRunning { get; private set; }
    private static readonly object _serviceLock = new();

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent == null) return StartCommandResult.NotSticky;

        string action = intent.Action ?? "";
        if (action == ActionStop)
        {
            IsRunning = false;
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(24))
                {
                    StopForeground(StopForegroundFlags.Remove);
                }
                else
                {
                    StopForeground(true);
                }
                StopSelf();
                ReleaseWakeLock();
            }
            catch { }
            return StartCommandResult.NotSticky;
        }

        string title = intent.GetStringExtra(ExtraTitle) ?? "Comic-GMTPC";
        string text = intent.GetStringExtra(ExtraText) ?? "Đang xử lý tác vụ nền...";
        int progress = intent.GetIntExtra(ExtraProgress, 0);

        var notification = BuildNotification(this, title, text, progress);

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(34))
            {
                StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }
            IsRunning = true;
        }
        catch (Java.Lang.Throwable jex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi Java StartForeground: {jex.Message}");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi C# StartForeground: {ex.Message}");
        }

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        IsRunning = false;
        ReleaseWakeLock();
        base.OnDestroy();
    }

    private void AcquireWakeLock()
    {
        try
        {
            if (_wakeLock == null || !_wakeLock.IsHeld)
            {
                var pm = (PowerManager?)GetSystemService(PowerService);
                if (pm != null)
                {
                    _wakeLock = pm.NewWakeLock(WakeLockFlags.Partial, "ComicGMTPC::BackgroundWakeLock");
                    _wakeLock?.Acquire();
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi kích hoạt WakeLock: {ex.Message}");
        }
    }

    private void ReleaseWakeLock()
    {
        try
        {
            if (_wakeLock != null && _wakeLock.IsHeld)
            {
                _wakeLock.Release();
                _wakeLock = null;
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi giải phóng WakeLock: {ex.Message}");
        }
    }

    private void CreateNotificationChannel()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            var channel = new NotificationChannel(
                ChannelId,
                "Comic GMTPC Tiến Trình Chạy Ngầm",
                NotificationImportance.High)
            {
                Description = "Thông báo tiến trình tải truyện, xử lý ảnh và đóng gói file chạy ngầm liên tục",
                LockscreenVisibility = NotificationVisibility.Public
            };
            channel.SetShowBadge(false);

            var nm = (NotificationManager?)GetSystemService(NotificationService);
            nm?.CreateNotificationChannel(channel);
        }
    }

    public static Notification BuildNotification(Context context, string title, string text, int progress)
    {
        var openIntent = new Intent(context, typeof(MainActivity));
        openIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var pendingIntent = PendingIntent.GetActivity(
            context,
            0,
            openIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new NotificationCompat.Builder(context, ChannelId);
        builder.SetContentTitle(title);
        builder.SetContentText(text);
        builder.SetSmallIcon(Resource.Drawable.Icon);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetPriority(NotificationCompat.PriorityHigh);
        builder.SetCategory(NotificationCompat.CategoryProgress);
        if (pendingIntent != null)
        {
            builder.SetContentIntent(pendingIntent);
        }

        if (progress >= 0 && progress <= 100)
        {
            builder.SetProgress(100, progress, false);
        }
        else
        {
            builder.SetProgress(0, 0, true);
        }

        return builder.Build()!;
    }

    public static void StartOrUpdate(Context context, string title, string text, int progress)
    {
        try
        {
            var appContext = context.ApplicationContext ?? context;

            // Nếu Service đã chạy: CHỈ cập nhật Notification trực tiếp qua NotificationManager!
            // Tuyệt đối không gọi StartForegroundService() khi app ở background (tránh ForegroundServiceStartNotAllowedException).
            if (IsRunning)
            {
                try
                {
                    var notification = BuildNotification(appContext, title, text, progress);
                    var nm = NotificationManagerCompat.From(appContext);
                    nm?.Notify(NotificationId, notification);
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi cập nhật Notification: {ex.Message}");
                }
                return;
            }

            // Nếu Service chưa chạy: Khởi động Service ở Foreground
            lock (_serviceLock)
            {
                if (IsRunning)
                {
                    var notification = BuildNotification(appContext, title, text, progress);
                    NotificationManagerCompat.From(appContext)?.Notify(NotificationId, notification);
                    return;
                }

                var intent = new Intent(appContext, typeof(ComicBackgroundService));
                intent.SetAction(ActionStartOrUpdate);
                intent.PutExtra(ExtraTitle, title);
                intent.PutExtra(ExtraText, text);
                intent.PutExtra(ExtraProgress, progress);

                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                {
                    appContext.StartForegroundService(intent);
                }
                else
                {
                    appContext.StartService(intent);
                }
            }
        }
        catch (Java.Lang.Throwable jex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi Java khởi chạy BackgroundService: {jex.Message}");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi C# khởi chạy BackgroundService: {ex.Message}");
        }
    }

    public static void Stop(Context context)
    {
        IsRunning = false;
        try
        {
            var appContext = context.ApplicationContext ?? context;
            var intent = new Intent(appContext, typeof(ComicBackgroundService));
            intent.SetAction(ActionStop);
            appContext.StartService(intent);

            var nm = NotificationManagerCompat.From(appContext);
            nm?.Cancel(NotificationId);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi dừng BackgroundService: {ex.Message}");
        }
    }
}
