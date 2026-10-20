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
    public const string ChannelId = "comic_gmtpc_bg_progress_channel";
    public const string ChannelIdBubble = "comic_gmtpc_bubble_channel";
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

    private static ComicBackgroundService? _currentInstance;
    private readonly object _wakeLockSync = new();
    private PowerManager.WakeLock? _wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        _currentInstance = this;
        CreateNotificationChannel();
        // KHÔNG acquire WakeLock ở đây! WakeLock chỉ được kích hoạt khi đang tải truyện thực tế để tránh gây nóng máy.
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
            UpdateWakeLock(taskId, isDownloading, isPaused);
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
        if (_currentInstance == this)
        {
            _currentInstance = null;
        }
        base.OnDestroy();
    }

    public void UpdateWakeLock(string taskId, bool isDownloading, bool isPaused)
    {
        lock (_wakeLockSync)
        {
            bool shouldHold;
            if (taskId == "download_queue")
            {
                // Chỉ giữ WakeLock khi ĐANG TẢI THỰC TẾ và KHÔNG BỊ TẠM DỪNG
                shouldHold = isDownloading && !isPaused;
            }
            else
            {
                // Các tác vụ nền khác (xử lý ảnh, đóng gói...): chỉ giữ khi đang chạy thực tế
                shouldHold = isDownloading;
            }

            if (shouldHold)
            {
                AcquireWakeLock();
            }
            else
            {
                ReleaseWakeLock();
            }
        }
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
                    // Đặt timeout an toàn tối đa 15 phút: nếu hệ thống/tác vụ gặp sự cố, Android sẽ tự nhả WakeLock để hạ nhiệt CPU
                    _wakeLock?.Acquire(15 * 60 * 1000L);
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

    private static PendingIntent? _cachedOpenPendingIntent;
    private static PendingIntent? _cachedAct1PendingIntent;
    private static PendingIntent? _cachedAct2PendingIntent;

    private void CreateNotificationChannel()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            var channel = new NotificationChannel(
                ChannelId,
                "Comic GMTPC Tiến Trình Chạy Ngầm",
                NotificationImportance.Low)
            {
                Description = "Thông báo tiến trình tải truyện, xử lý ảnh và đóng gói file chạy ngầm liên tục",
                LockscreenVisibility = NotificationVisibility.Public
            };
            channel.SetShowBadge(false);
            channel.EnableVibration(false);
            channel.SetSound(null, null);

            var nm = (NotificationManager?)GetSystemService(NotificationService);
            if (nm != null)
            {
                nm.CreateNotificationChannel(channel);

                // Kênh thông báo chuyên biệt kích hoạt Android Bubble API (Android 11+)
                var bubbleChannel = new NotificationChannel(
                    ChannelIdBubble,
                    "Comic GMTPC Bong Bóng Nổi (Bubbles)",
                    NotificationImportance.High)
                {
                    Description = "Kênh bong bóng nổi tương tác nhanh trên Android",
                    LockscreenVisibility = NotificationVisibility.Public
                };
                bubbleChannel.SetShowBadge(true);
                bubbleChannel.EnableVibration(false);
                bubbleChannel.SetSound(null, null);
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    bubbleChannel.SetAllowBubbles(true);
                }
                nm.CreateNotificationChannel(bubbleChannel);
            }
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
        var flags = PendingIntentFlags.UpdateCurrent | (OperatingSystem.IsAndroidVersionAtLeast(23) ? PendingIntentFlags.Immutable : 0);

        if (_cachedOpenPendingIntent == null)
        {
            var openIntent = new Intent(context, typeof(MainActivity));
            openIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
            _cachedOpenPendingIntent = PendingIntent.GetActivity(context, 0, openIntent, flags);
        }

        bool isBubbleMode = BackgroundExecutionService.Instance.IsBubbleMode;
        var builder = new NotificationCompat.Builder(context, isBubbleMode ? ChannelIdBubble : ChannelId);
        builder.SetContentTitle(title);
        builder.SetContentText(text);
        builder.SetSmallIcon(Resource.Drawable.Icon);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetSilent(true);
        builder.SetPriority(NotificationCompat.PriorityLow);
        builder.SetCategory(NotificationCompat.CategoryProgress);
        if (_cachedOpenPendingIntent != null)
        {
            builder.SetContentIntent(_cachedOpenPendingIntent);
        }

        // Tích hợp Android Bubble API (Hệ thống Bong bóng Android 10+ / 11+)
        if (OperatingSystem.IsAndroidVersionAtLeast(29) && BackgroundExecutionService.Instance.IsBubbleMode)
        {
            try
            {
                var bubbleIntent = new Intent(context, typeof(MainActivity));
                bubbleIntent.SetAction(Intent.ActionView);
                var bubbleFlags = PendingIntentFlags.UpdateCurrent;
                if (OperatingSystem.IsAndroidVersionAtLeast(31))
                {
                    bubbleFlags |= PendingIntentFlags.Mutable;
                }
                var bubblePendingIntent = PendingIntent.GetActivity(context, 2002, bubbleIntent, bubbleFlags);

                if (bubblePendingIntent != null)
                {
                    var iconCompat = AndroidX.Core.Graphics.Drawable.IconCompat.CreateWithResource(context, Resource.Drawable.Icon);
                    if (iconCompat != null)
                    {
                        var bubbleBuilder = new NotificationCompat.BubbleMetadata.Builder(bubblePendingIntent, iconCompat);
                        bubbleBuilder.SetDesiredHeight(600);
                        bubbleBuilder.SetAutoExpandBubble(false);
                        bubbleBuilder.SetSuppressNotification(false);
                        var bubbleData = bubbleBuilder.Build();
                        if (bubbleData != null)
                        {
                            builder.SetBubbleMetadata(bubbleData);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("ComicGMTPC_Bubble", $"Lỗi đính kèm BubbleMetadata: {ex.Message}");
            }
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
        if (_cachedAct1PendingIntent == null)
        {
            var act1Intent = new Intent(context, typeof(ComicBackgroundService));
            act1Intent.SetAction(ActionContextual1);
            _cachedAct1PendingIntent = PendingIntent.GetService(context, 1, act1Intent, flags);
        }

        if (_cachedAct1PendingIntent != null)
        {
            string btnText1 = !string.IsNullOrEmpty(action1Text)
                ? action1Text
                : (isDownloading ? "⏹️ Dừng tải" : "⬇️ Tải");
            builder.AddAction(Resource.Drawable.Icon, btnText1, _cachedAct1PendingIntent);
        }

        // Action 2: Dựa theo ngữ cảnh (Tạm dừng/Tiếp tục hoặc Mở ứng dụng)
        if (taskId == "download_queue" || string.IsNullOrEmpty(action2Text) || action2Text.Contains("tạm dừng", StringComparison.OrdinalIgnoreCase) || action2Text.Contains("tiếp tục", StringComparison.OrdinalIgnoreCase))
        {
            if (_cachedAct2PendingIntent == null)
            {
                var act2Intent = new Intent(context, typeof(ComicBackgroundService));
                act2Intent.SetAction(ActionContextual2);
                _cachedAct2PendingIntent = PendingIntent.GetService(context, 2, act2Intent, flags);
            }

            if (_cachedAct2PendingIntent != null)
            {
                string btnText2 = !string.IsNullOrEmpty(action2Text)
                    ? action2Text
                    : (isPaused ? "▶️ Tiếp tục" : "⏸️ Tạm dừng");
                builder.AddAction(Resource.Drawable.Icon, btnText2, _cachedAct2PendingIntent);
            }
        }
        else if (_cachedOpenPendingIntent != null)
        {
            builder.AddAction(Resource.Drawable.Icon, action2Text ?? "📱 Mở ứng dụng", _cachedOpenPendingIntent);
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

            // Nếu Service đã chạy: Cập nhật Notification trực tiếp qua NotificationManager và đồng bộ WakeLock!
            if (IsRunning)
            {
                try
                {
                    var notification = BuildNotification(appContext, taskId, title, text, progress, isDownloading, isPaused, action1Text, action2Text);
                    var nm = NotificationManagerCompat.From(appContext);
                    nm?.Notify(NotificationId, notification);
                    _currentInstance?.UpdateWakeLock(taskId, isDownloading, isPaused);
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
                    _currentInstance?.UpdateWakeLock(taskId, isDownloading, isPaused);
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
            _currentInstance?.UpdateWakeLock(string.Empty, false, false);
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
