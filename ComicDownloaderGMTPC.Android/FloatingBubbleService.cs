using System;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;

namespace ComicDownloaderGMTPC.Android;

[Service(Exported = false)]
public class FloatingBubbleService : Service, View.IOnTouchListener
{
    private IWindowManager? _windowManager;
    private View? _floatingBubbleView;
    private WindowManagerLayoutParams? _params;
    private float _initialX;
    private float _initialY;
    private float _initialTouchX;
    private float _initialTouchY;
    private int _screenWidth = 1080;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();

        if (OperatingSystem.IsAndroidVersionAtLeast(23) && !Settings.CanDrawOverlays(this))
        {
            StopSelf();
            return;
        }

        CreateMessengerFloatingBubble();
    }

    private void CreateMessengerFloatingBubble()
    {
        try
        {
            _windowManager = GetSystemService(WindowService) as IWindowManager;
            if (_windowManager == null) return;

            float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
            _screenWidth = Resources?.DisplayMetrics?.WidthPixels ?? 1080;
            int bubbleSizePx = (int)(60 * density);
            int iconSizePx = (int)(38 * density);

            // 1. Root Container tròn (Messenger Chat Head Circular Frame)
            var bubbleRoot = new FrameLayout(this);
            var rootBackground = new global::Android.Graphics.Drawables.GradientDrawable();
            rootBackground.SetShape(global::Android.Graphics.Drawables.ShapeType.Oval);
            rootBackground.SetColor(Color.ParseColor("#FF111827"));
            rootBackground.SetStroke((int)(2.5f * density), Color.ParseColor("#FF8B5CF6"));
            bubbleRoot.Background = rootBackground;

            // 2. Icon app căn giữa bên trong bong bóng tròn
            var iconView = new ImageView(this);
            iconView.SetImageResource(Resource.Drawable.Icon);
            var iconParams = new FrameLayout.LayoutParams(iconSizePx, iconSizePx)
            {
                Gravity = GravityFlags.Center
            };
            bubbleRoot.AddView(iconView, iconParams);

            // 3. Mini Active Badge góc dưới bên phải (chấm xanh lá phát sáng báo hiệu đang chạy ngầm)
            int badgeSizePx = (int)(16 * density);
            var badgeView = new TextView(this)
            {
                Text = "⚡",
                TextSize = 8,
                Gravity = GravityFlags.Center
            };
            var badgeBg = new global::Android.Graphics.Drawables.GradientDrawable();
            badgeBg.SetShape(global::Android.Graphics.Drawables.ShapeType.Oval);
            badgeBg.SetColor(Color.ParseColor("#FF10B981"));
            badgeBg.SetStroke((int)(1.5f * density), Color.ParseColor("#FF111827"));
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
                WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits,
                Format.Translucent)
            {
                Gravity = GravityFlags.Top | GravityFlags.Start,
                X = _screenWidth - bubbleSizePx - (int)(12 * density),
                Y = (int)(180 * density)
            };

            _windowManager.AddView(_floatingBubbleView, _params);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi tạo Messenger Floating Bubble: {ex.Message}");
        }
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
                _windowManager.UpdateViewLayout(_floatingBubbleView, _params);
                return true;

            case MotionEventActions.Up:
                float diffX = Math.Abs(e.RawX - _initialTouchX);
                float diffY = Math.Abs(e.RawY - _initialTouchY);
                if (diffX < 15 && diffY < 15)
                {
                    // Click vào bong bóng -> Mở lại MainActivity ngay lập tức
                    OpenMainActivity();
                }
                else
                {
                    // Tự động hít/bám vào mép trái hoặc mép phải màn hình (Messenger Edge Snap)
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
            int margin = (int)(8 * density);
            int bubbleWidth = _params.Width > 0 ? _params.Width : (int)(60 * density);

            int midScreen = _screenWidth / 2;
            if (_params.X + bubbleWidth / 2 < midScreen)
            {
                _params.X = margin; // Bám mép trái
            }
            else
            {
                _params.X = _screenWidth - bubbleWidth - margin; // Bám mép phải
            }
            _windowManager.UpdateViewLayout(_floatingBubbleView, _params);
        }
        catch {}
    }

    private void OpenMainActivity()
    {
        try
        {
            var intent = new Intent(this, typeof(MainActivity));
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ReorderToFront);
            StartActivity(intent);
            StopSelf();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi mở MainActivity từ Messenger Bubble: {ex.Message}");
        }
    }

    public override void OnDestroy()
    {
        try
        {
            if (_floatingBubbleView != null && _windowManager != null)
            {
                _windowManager.RemoveView(_floatingBubbleView);
                _floatingBubbleView = null;
            }
        }
        catch {}
        base.OnDestroy();
    }

    public static void Show(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(FloatingBubbleService));
            context.StartService(intent);
        }
        catch {}
    }

    public static void Hide(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(FloatingBubbleService));
            context.StopService(intent);
        }
        catch {}
    }
}
