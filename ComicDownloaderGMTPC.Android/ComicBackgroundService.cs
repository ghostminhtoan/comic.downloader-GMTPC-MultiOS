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

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent == null) return StartCommandResult.NotSticky;

        string action = intent.Action ?? "";
        if (action == ActionStop)
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
            return StartCommandResult.NotSticky;
        }

        string title = intent.GetStringExtra(ExtraTitle) ?? "Comic-GMTPC";
        string text = intent.GetStringExtra(ExtraText) ?? "Đang xử lý tác vụ nền...";
        int progress = intent.GetIntExtra(ExtraProgress, 0);

        var notification = BuildNotification(title, text, progress);

        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
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

    private Notification BuildNotification(string title, string text, int progress)
    {
        var openIntent = new Intent(this, typeof(MainActivity));
        openIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var pendingIntent = PendingIntent.GetActivity(
            this,
            0,
            openIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new NotificationCompat.Builder(this, ChannelId);
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
            var intent = new Intent(context, typeof(ComicBackgroundService));
            intent.SetAction(ActionStartOrUpdate);
            intent.PutExtra(ExtraTitle, title);
            intent.PutExtra(ExtraText, text);
            intent.PutExtra(ExtraProgress, progress);

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StartService(intent);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi khởi chạy BackgroundService: {ex.Message}");
        }
    }

    public static void Stop(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(ComicBackgroundService));
            intent.SetAction(ActionStop);
            context.StartService(intent);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi dừng BackgroundService: {ex.Message}");
        }
    }
}
