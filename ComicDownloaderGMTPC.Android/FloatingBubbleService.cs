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
    private bool _isViewAdded = false;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        if (OperatingSystem.IsAndroidVersionAtLeast(23) && !Settings.CanDrawOverlays(this))
        {
            StopSelf();
            return;
        }

        CreateOrShowMessengerFloatingBubble();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(23) && !Settings.CanDrawOverlays(this))
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        CreateOrShowMessengerFloatingBubble();
        return StartCommandResult.Sticky;
    }

    private void CreateOrShowMessengerFloatingBubble()
    {
        var looper = Looper.MainLooper;
        if (looper == null) return;

        new Handler(looper).Post(() =>
        {
            try
            {
                _windowManager = GetSystemService(WindowService) as IWindowManager;
                if (_windowManager == null) return;

                float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
                _screenWidth = Resources?.DisplayMetrics?.WidthPixels ?? 1080;
                if (_screenWidth <= 0) _screenWidth = 1080;

                int bubbleSizePx = (int)(64 * density);

                if (_floatingBubbleView != null && _isViewAdded)
                {
                    _floatingBubbleView.Visibility = ViewStates.Visible;
                    return;
                }

                // 1. Root Container hình tròn (Messenger Floating Chat Head)
                var bubbleRoot = new FrameLayout(this);
                var rootBackground = new global::Android.Graphics.Drawables.GradientDrawable(
                    global::Android.Graphics.Drawables.GradientDrawable.Orientation.TlBr,
                    new int[] { Color.ParseColor("#7C3AED"), Color.ParseColor("#4338CA") });
                rootBackground.SetShape(global::Android.Graphics.Drawables.ShapeType.Oval);
                rootBackground.SetStroke((int)(3f * density), Color.ParseColor("#C4B5FD"));
                bubbleRoot.Background = rootBackground;
                bubbleRoot.Elevation = 16f * density;

                // 2. Nội dung bên trong bong bóng: Biểu tượng truyện tranh & text GMTPC
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
                    Text = "COMIC",
                    TextSize = 8,
                    Gravity = GravityFlags.Center,
                    Typeface = Typeface.DefaultBold
                };
                labelText.SetTextColor(Color.ParseColor("#E0E7FF"));
                innerLayout.AddView(labelText);

                var centerParams = new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent,
                    ViewGroup.LayoutParams.WrapContent)
                {
                    Gravity = GravityFlags.Center
                };
                bubbleRoot.AddView(innerLayout, centerParams);

                // 3. Mini Active Badge góc dưới bên phải (chấm xanh lá phát sáng báo hiệu đang chạy ngầm)
                int badgeSizePx = (int)(18 * density);
                var badgeView = new TextView(this)
                {
                    Text = "⚡",
                    TextSize = 9,
                    Gravity = GravityFlags.Center
                };
                var badgeBg = new global::Android.Graphics.Drawables.GradientDrawable();
                badgeBg.SetShape(global::Android.Graphics.Drawables.ShapeType.Oval);
                badgeBg.SetColor(Color.ParseColor("#10B981"));
                badgeBg.SetStroke((int)(2f * density), Color.ParseColor("#1E1B4B"));
                badgeView.Background = badgeBg;
                badgeView.SetTextColor(Color.White);

                var badgeParams = new FrameLayout.LayoutParams(badgeSizePx, badgeSizePx)
                {
                    Gravity = GravityFlags.Bottom | GravityFlags.End,
                    RightMargin = (int)(3 * density),
                    BottomMargin = (int)(3 * density)
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
                    WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal | WindowManagerFlags.HardwareAccelerated,
                    Format.Translucent)
                {
                    Gravity = GravityFlags.Top | GravityFlags.Start,
                    X = _screenWidth - bubbleSizePx - (int)(12 * density),
                    Y = (int)(220 * density)
                };

                if (!_isViewAdded)
                {
                    _windowManager.AddView(_floatingBubbleView, _params);
                    _isViewAdded = true;
                    global::Android.Util.Log.Info("ComicGMTPC", "Đã kích hoạt Floating Bubble Messenger trên Android thành công!");
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi tạo Messenger Floating Bubble: {ex.Message}");
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
                if (diffX < 15 && diffY < 15)
                {
                    // Chạm vào bong bóng -> Mở lại MainActivity ngay lập tức
                    OpenMainActivity();
                }
                else
                {
                    // Tự động hít/bám vào mép trái hoặc mép phải màn hình (Messenger Snap)
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
            int margin = (int)(10 * density);
            int bubbleWidth = _params.Width > 0 ? _params.Width : (int)(64 * density);

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
        catch { }
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
                context.StartService(intent);
            }
            else
            {
                context.StartService(intent);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi Show FloatingBubbleService: {ex.Message}");
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
