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

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();

        if (OperatingSystem.IsAndroidVersionAtLeast(23) && !Settings.CanDrawOverlays(this))
        {
            StopSelf();
            return;
        }

        CreateFloatingBubble();
    }

    private void CreateFloatingBubble()
    {
        try
        {
            _windowManager = GetSystemService(WindowService) as IWindowManager;
            if (_windowManager == null) return;

            var bubbleLayout = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical,
                Background = CreateBubbleBackground()
            };
            bubbleLayout.SetPadding(16, 16, 16, 16);

            var iconView = new ImageView(this);
            iconView.SetImageResource(Resource.Drawable.Icon);
            var iconParams = new LinearLayout.LayoutParams(96, 96)
            {
                Gravity = GravityFlags.Center
            };
            bubbleLayout.AddView(iconView, iconParams);

            var badgeText = new TextView(this)
            {
                Text = "GMTPC",
                TextSize = 10,
                Typeface = Typeface.DefaultBold
            };
            badgeText.SetTextColor(Color.ParseColor("#38BDF8"));
            badgeText.Gravity = GravityFlags.Center;
            bubbleLayout.AddView(badgeText);

            _floatingBubbleView = bubbleLayout;
            _floatingBubbleView.SetOnTouchListener(this);

            var layoutType = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? WindowManagerTypes.ApplicationOverlay
                : WindowManagerTypes.Phone;

            _params = new WindowManagerLayoutParams(
                WindowManagerLayoutParams.WrapContent,
                WindowManagerLayoutParams.WrapContent,
                layoutType,
                WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits,
                Format.Translucent)
            {
                Gravity = GravityFlags.Top | GravityFlags.Start,
                X = 20,
                Y = 200
            };

            _windowManager.AddView(_floatingBubbleView, _params);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi tạo Floating Bubble: {ex.Message}");
        }
    }

    private global::Android.Graphics.Drawables.GradientDrawable CreateBubbleBackground()
    {
        var shape = new global::Android.Graphics.Drawables.GradientDrawable();
        shape.SetShape(global::Android.Graphics.Drawables.ShapeType.Rectangle);
        shape.SetCornerRadius(28);
        shape.SetColor(Color.ParseColor("#EE0F172A"));
        shape.SetStroke(3, Color.ParseColor("#8B5CF6"));
        return shape;
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
                if (diffX < 10 && diffY < 10)
                {
                    // Click vào bong bóng -> Mở lại MainActivity
                    OpenMainActivity();
                }
                return true;
        }
        return false;
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
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi mở MainActivity từ Bubble: {ex.Message}");
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
