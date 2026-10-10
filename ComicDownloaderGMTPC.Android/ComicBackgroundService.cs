using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.Android;

[Service(ForegroundServiceType = ForegroundService.TypeDataSync, Exported = false)]
public class ComicBackgroundService : Service
{
    public const string ChannelId = "comic_gmtpc_bg_channel";
    public const int NotificationId = 1002;
    public const string ActionStartOrUpdate = "ACTION_START_OR_UPDATE";
    public const string ActionStop = "ACTION_STOP";
    public const string ActionToggleDownload = "ACTION_TOGGLE_DOWNLOAD";
    public const string ActionTogglePause = "ACTION_TOGGLE_PAUSE";
    public const string ActionContextual1 = "ACTION_CONTEXTUAL_1";
    public const string ActionContextual2 = "ACTION_CONTEXTUAL_2";
    public const string ExtraTaskId = "EXTRA_TASK_ID";
    public const string ExtraTitle = "EXTRA_TITLE";
    public const string ExtraText = "EXTRA_TEXT";
    public const string ExtraProgress = "EXTRA_PROGRESS";
    public const string ExtraIsDownloading = "EXTRA_IS_DOWNLOADING";
    public const string ExtraIsPaused = "EXTRA_IS_PAUSED";
    public const string ExtraAction1Text = "EXTRA_ACTION_1_TEXT";
    public const string ExtraAction2Text = "EXTRA_ACTION_2_TEXT";

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

        if (action == ActionToggleDownload || action == ActionContextual1)
        {
            try
            {
                if (action == ActionToggleDownload)
                {
                    BackgroundExecutionService.NativeNotificationToggleDownloadRequested?.Invoke();
                }
                else
                {
                    BackgroundExecutionService.NativeNotificationContextualAction1Requested?.Invoke();
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi kích hoạt Action 1 từ Notification: {ex.Message}");
            }
            return StartCommandResult.Sticky;
        }

        if (action == ActionTogglePause || action == ActionContextual2)
        {
            try
            {
                if (action == ActionTogglePause)
                {
                    BackgroundExecutionService.NativeNotificationTogglePauseRequested?.Invoke();
                }
                else
                {
                    BackgroundExecutionService.NativeNotificationContextualAction2Requested?.Invoke();
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi kích hoạt Action 2 từ Notification: {ex.Message}");
            }
            return StartCommandResult.Sticky;
        }

        string taskId = intent.GetStringExtra(ExtraTaskId) ?? "download_queue";
        string title = intent.GetStringExtra(ExtraTitle) ?? "Comic-GMTPC";
        string text = intent.GetStringExtra(ExtraText) ?? "Đang xử lý tác vụ nền...";
        int progress = intent.GetIntExtra(ExtraProgress, 0);
        bool isDownloading = intent.GetBooleanExtra(ExtraIsDownloading, true);
        bool isPaused = intent.GetBooleanExtra(ExtraIsPaused, false);
        string action1Text = intent.GetStringExtra(ExtraAction1Text) ?? (isDownloading ? "⏹️ Dừng tải" : "⬇️ Tải");
        string action2Text = intent.GetStringExtra(ExtraAction2Text) ?? (isPaused ? "▶️ Tiếp tục" : "⏸️ Tạm dừng");

        var notification = BuildNotification(this, taskId, title, text, progress, isDownloading, isPaused, action1Text, action2Text);

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

    public static Notification BuildNotification(
        Context context,
        string taskId,
        string title,
        string text,
        int progress,
        bool isDownloading = true,
        bool isPaused = false,
        string? action1Text = null,
        string? action2Text = null)
    {
        var openIntent = new Intent(context, typeof(MainActivity));
        openIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var pendingIntent = PendingIntent.GetActivity(
            context,
            0,
            openIntent,
            PendingIntentFlags.UpdateCurrent | (OperatingSystem.IsAndroidVersionAtLeast(23) ? PendingIntentFlags.Immutable : 0));

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

        // Action 1: Dựa theo ngữ cảnh (Tải/Dừng, Dừng xử lý ảnh, Dừng đóng gói, Dừng cắt ảnh...)
        var act1Intent = new Intent(context, typeof(ComicBackgroundService));
        act1Intent.SetAction(ActionContextual1);
        var act1Pending = PendingIntent.GetService(
            context,
            1,
            act1Intent,
            PendingIntentFlags.UpdateCurrent | (OperatingSystem.IsAndroidVersionAtLeast(23) ? PendingIntentFlags.Immutable : 0));

        if (act1Pending != null)
        {
            string btnText1 = !string.IsNullOrEmpty(action1Text)
                ? action1Text
                : (isDownloading ? "⏹️ Dừng tải" : "⬇️ Tải");
            builder.AddAction(Resource.Drawable.Icon, btnText1, act1Pending);
        }

        // Action 2: Dựa theo ngữ cảnh (Tạm dừng/Tiếp tục hoặc Mở ứng dụng)
        if (taskId == "download_queue" || string.IsNullOrEmpty(action2Text) || action2Text.Contains("tạm dừng", StringComparison.OrdinalIgnoreCase) || action2Text.Contains("tiếp tục", StringComparison.OrdinalIgnoreCase))
        {
            var act2Intent = new Intent(context, typeof(ComicBackgroundService));
            act2Intent.SetAction(ActionContextual2);
            var act2Pending = PendingIntent.GetService(
                context,
                2,
                act2Intent,
                PendingIntentFlags.UpdateCurrent | (OperatingSystem.IsAndroidVersionAtLeast(23) ? PendingIntentFlags.Immutable : 0));

            if (act2Pending != null)
            {
                string btnText2 = !string.IsNullOrEmpty(action2Text)
                    ? action2Text
                    : (isPaused ? "▶️ Tiếp tục" : "⏸️ Tạm dừng");
                builder.AddAction(Resource.Drawable.Icon, btnText2, act2Pending);
            }
        }
        else if (pendingIntent != null)
        {
            builder.AddAction(Resource.Drawable.Icon, action2Text ?? "📱 Mở ứng dụng", pendingIntent);
        }

        return builder.Build()!;
    }

    public static void StartOrUpdate(
        Context context,
        string taskId,
        string title,
        string text,
        int progress,
        bool isDownloading = true,
        bool isPaused = false,
        string? action1Text = null,
        string? action2Text = null)
    {
        try
        {
            var appContext = context.ApplicationContext ?? context;

            // Nếu Service đã chạy: Cập nhật Notification trực tiếp qua NotificationManager!
            if (IsRunning)
            {
                try
                {
                    var notification = BuildNotification(appContext, taskId, title, text, progress, isDownloading, isPaused, action1Text, action2Text);
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
                    var notification = BuildNotification(appContext, taskId, title, text, progress, isDownloading, isPaused, action1Text, action2Text);
                    NotificationManagerCompat.From(appContext)?.Notify(NotificationId, notification);
                    return;
                }

                var intent = new Intent(appContext, typeof(ComicBackgroundService));
                intent.SetAction(ActionStartOrUpdate);
                intent.PutExtra(ExtraTaskId, taskId);
                intent.PutExtra(ExtraTitle, title);
                intent.PutExtra(ExtraText, text);
                intent.PutExtra(ExtraProgress, progress);
                intent.PutExtra(ExtraIsDownloading, isDownloading);
                intent.PutExtra(ExtraIsPaused, isPaused);
                intent.PutExtra(ExtraAction1Text, action1Text);
                intent.PutExtra(ExtraAction2Text, action2Text);

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
