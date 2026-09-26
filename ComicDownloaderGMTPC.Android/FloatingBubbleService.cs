using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AndroidX.Core.App;

namespace ComicDownloaderGMTPC.Android;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public class FloatingBubbleService : Service, View.IOnTouchListener
{
    public const string BubbleChannelId = "comic_gmtpc_bubble_channel";
    public const int BubbleNotificationId = 1003;

    private IWindowManager? _windowManager;
    private View? _floatingBubbleView;
    private WindowManagerLayoutParams? _params;
    private float _initialX;
    private float _initialY;
    private float _initialTouchX;
    private float _initialTouchY;
    private int _screenWidth = 1080;
    private int _screenHeight = 1920;
    private bool _isViewAdded = false;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        EnsureForegroundNotification();

        if (OperatingSystem.IsAndroidVersionAtLeast(23) && !Settings.CanDrawOverlays(this))
        {
            StopSelf();
            return;
        }

        CreateOrShowCyanFloatingBubble();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        EnsureForegroundNotification();

        if (OperatingSystem.IsAndroidVersionAtLeast(23) && !Settings.CanDrawOverlays(this))
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        CreateOrShowCyanFloatingBubble();
        return StartCommandResult.Sticky;
    }

    private void EnsureForegroundNotification()
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var nm = (NotificationManager?)GetSystemService(NotificationService);
                if (nm != null)
                {
                    var channel = new NotificationChannel(
                        BubbleChannelId,
                        "Bóng nổi Comic GMTPC (Cyan)",
                        NotificationImportance.Low)
                    {
                        Description = "Duy trì quả bóng nổi màu Cyan trên màn hình thiết bị",
                        LockscreenVisibility = NotificationVisibility.Public
                    };
                    nm.CreateNotificationChannel(channel);
                }

                var openIntent = new Intent(this, typeof(MainActivity));
                openIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
                var pendingIntent = PendingIntent.GetActivity(
                    this,
                    0,
                    openIntent,
                    PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

                var notifBuilder = new NotificationCompat.Builder(this, BubbleChannelId);
                notifBuilder.SetContentTitle("Comic GMTPC (Bóng nổi Cyan)");
                notifBuilder.SetContentText("Chạm vào bóng Cyan trên màn hình để mở ứng dụng");
                notifBuilder.SetSmallIcon(Resource.Drawable.Icon);
                notifBuilder.SetOngoing(true);
                notifBuilder.SetPriority(NotificationCompat.PriorityLow);
                if (pendingIntent != null)
                {
                    notifBuilder.SetContentIntent(pendingIntent);
                }

                var notif = notifBuilder.Build();
                if (notif != null)
                {
                    if (OperatingSystem.IsAndroidVersionAtLeast(34))
                    {
                        StartForeground(BubbleNotificationId, notif, ForegroundService.TypeDataSync);
                    }
                    else
                    {
                        StartForeground(BubbleNotificationId, notif);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi EnsureForegroundNotification: {ex.Message}");
        }
    }

    private void CreateOrShowCyanFloatingBubble()
    {
        var looper = Looper.MainLooper;
        if (looper == null) return;

        new Handler(looper).Post(() =>
        {
            try
            {
                _windowManager = GetSystemService(WindowService) as IWindowManager;
                if (_windowManager == null) return;

                var displayMetrics = Resources?.DisplayMetrics;
                float density = displayMetrics?.Density ?? 2.0f;
                _screenWidth = displayMetrics?.WidthPixels ?? 1080;
                _screenHeight = displayMetrics?.HeightPixels ?? 1920;
                if (_screenWidth <= 0) _screenWidth = 1080;
                if (_screenHeight <= 0) _screenHeight = 1920;

                int bubbleSizePx = (int)(64 * density);
                int initialCenterX = (_screenWidth - bubbleSizePx) / 2;
                int initialCenterY = (_screenHeight - bubbleSizePx) / 2;

                if (_floatingBubbleView != null && _isViewAdded)
                {
                    _floatingBubbleView.Visibility = ViewStates.Visible;
                    _floatingBubbleView.Alpha = 1.0f;
                    if (_params != null)
                    {
                        _params.X = initialCenterX;
                        _params.Y = initialCenterY;
                        _windowManager.UpdateViewLayout(_floatingBubbleView, _params);
                    }
                    return;
                }

                // 1. Container hình tròn phong cách DU Recorder màu CYAN (#00E5FF / #0891B2) nổi bật 100% Opaque
                var bubbleRoot = new FrameLayout(this);
                var rootBackground = new global::Android.Graphics.Drawables.GradientDrawable(
                    global::Android.Graphics.Drawables.GradientDrawable.Orientation.TlBr,
                    new int[] { Color.ParseColor("#00E5FF"), Color.ParseColor("#0891B2") });
                rootBackground.SetShape(global::Android.Graphics.Drawables.ShapeType.Oval);
                rootBackground.SetStroke((int)(3.5f * density), Color.ParseColor("#FFFFFF")); // Viền trắng phát sáng
                bubbleRoot.Background = rootBackground;
                bubbleRoot.Elevation = 20f * density;
                bubbleRoot.Alpha = 1.0f; // 100% OPAQUE, KHÔNG HỀ TRONG SUỐT

                // 2. Nội dung bên trong: Biểu tượng truyện tranh và nhãn GMTPC màu đen tương phản rõ nét
                var innerLayout = new LinearLayout(this)
                {
                    Orientation = Orientation.Vertical
                };
                innerLayout.SetGravity(GravityFlags.Center);

                var iconText = new TextView(this)
                {
                    Text = "📖",
                    TextSize = 22,
                    Gravity = GravityFlags.Center
                };
                innerLayout.AddView(iconText);

                var labelText = new TextView(this)
                {
                    Text = "GMTPC",
                    TextSize = 8,
                    Gravity = GravityFlags.Center,
                    Typeface = Typeface.DefaultBold
                };
                labelText.SetTextColor(Color.ParseColor("#000000")); // Màu đen đậm nét trên nền Cyan
                innerLayout.AddView(labelText);

                var centerParams = new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent,
                    ViewGroup.LayoutParams.WrapContent)
                {
                    Gravity = GravityFlags.Center
                };
                bubbleRoot.AddView(innerLayout, centerParams);

                // 3. Mini Active Badge góc dưới (chấm cam phát sáng báo đang chạy ngầm)
                int badgeSizePx = (int)(18 * density);
                var badgeView = new TextView(this)
                {
                    Text = "⚡",
                    TextSize = 9,
                    Gravity = GravityFlags.Center
                };
                var badgeBg = new global::Android.Graphics.Drawables.GradientDrawable();
                badgeBg.SetShape(global::Android.Graphics.Drawables.ShapeType.Oval);
                badgeBg.SetColor(Color.ParseColor("#FF5722"));
                badgeBg.SetStroke((int)(2f * density), Color.White);
                badgeView.Background = badgeBg;
                badgeView.SetTextColor(Color.White);

                var badgeParams = new FrameLayout.LayoutParams(badgeSizePx, badgeSizePx)
                {
                    Gravity = GravityFlags.Bottom | GravityFlags.End,
                    RightMargin = (int)(2 * density),
                    BottomMargin = (int)(2 * density)
                };
                bubbleRoot.AddView(badgeView, badgeParams);

                _floatingBubbleView = bubbleRoot;
                _floatingBubbleView.SetOnTouchListener(this);

                var layoutType = OperatingSystem.IsAndroidVersionAtLeast(26)
                    ? WindowManagerTypes.ApplicationOverlay
                    : WindowManagerTypes.Phone;

                _params = new WindowManagerLayoutParams(
                    bubbleSizePx,
                    bubbleSizePx,
                    layoutType,
                    WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits | WindowManagerFlags.HardwareAccelerated,
                    Format.Translucent)
                {
                    Gravity = GravityFlags.Top | GravityFlags.Start,
                    X = initialCenterX,
                    Y = initialCenterY
                };

                if (!_isViewAdded)
                {
                    _windowManager.AddView(_floatingBubbleView, _params);
                    _isViewAdded = true;
                    global::Android.Util.Log.Info("ComicGMTPC", $"Đã kích hoạt quả bóng nổi Cyan tại tâm màn hình ({initialCenterX}, {initialCenterY}) thành công!");
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi tạo Cyan Floating Bubble: {ex.Message}");
            }
        });
    }

    public bool OnTouch(View? v, MotionEvent? e)
    {
        if (e == null || _params == null || _windowManager == null || _floatingBubbleView == null) return false;

        switch (e.Action)
        {
            case MotionEventActions.Down:
                _initialX = _params.X;
                _initialY = _params.Y;
                _initialTouchX = e.RawX;
                _initialTouchY = e.RawY;
                return true;

            case MotionEventActions.Move:
                _params.X = (int)(_initialX + (e.RawX - _initialTouchX));
                _params.Y = (int)(_initialY + (e.RawY - _initialTouchY));
                try
                {
                    _windowManager.UpdateViewLayout(_floatingBubbleView, _params);
                }
                catch { }
                return true;

            case MotionEventActions.Up:
                float diffX = Math.Abs(e.RawX - _initialTouchX);
                float diffY = Math.Abs(e.RawY - _initialTouchY);
                if (diffX < 12 && diffY < 12)
                {
                    // Chạm vào bóng -> Mở lại MainActivity ngay lập tức
                    OpenMainActivity();
                }
                else
                {
                    // Tự động hít/bám vào mép trái hoặc mép phải màn hình
                    SnapToScreenEdge();
                }
                return true;
        }
        return false;
    }

    private void SnapToScreenEdge()
    {
        if (_params == null || _windowManager == null || _floatingBubbleView == null) return;
        try
        {
            float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
            int margin = (int)(4 * density);
            int bubbleWidth = _params.Width > 0 ? _params.Width : (int)(64 * density);

            int midScreen = _screenWidth / 2;
            if (_params.X + bubbleWidth / 2 < midScreen)
            {
                _params.X = margin; // Bám sát mép trái
            }
            else
            {
                _params.X = _screenWidth - bubbleWidth - margin; // Bám sát mép phải
            }
            _windowManager.UpdateViewLayout(_floatingBubbleView, _params);
        }
        catch { }
    }

    private void OpenMainActivity()
    {
        try
        {
            var intent = new Intent(this, typeof(MainActivity));
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ReorderToFront);
            StartActivity(intent);
            Services.BackgroundExecutionService.Instance.SetBubbleMode(false);
            StopSelf();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi mở MainActivity từ Cyan Bubble: {ex.Message}");
        }
    }

    public override void OnDestroy()
    {
        try
        {
            if (_floatingBubbleView != null && _windowManager != null && _isViewAdded)
            {
                _windowManager.RemoveView(_floatingBubbleView);
                _floatingBubbleView = null;
                _isViewAdded = false;
            }
        }
        catch { }
        base.OnDestroy();
    }

    public static void Show(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(FloatingBubbleService));
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
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi Show FloatingBubbleService: {ex.Message}");
            try
            {
                var intent = new Intent(context, typeof(FloatingBubbleService));
                context.StartService(intent);
            }
            catch { }
        }
    }

    public static void Hide(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(FloatingBubbleService));
            context.StopService(intent);
        }
        catch { }
    }
}
