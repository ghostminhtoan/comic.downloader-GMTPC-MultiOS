using System;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.Android;

[Service(Exported = false)]
public class ComicFloatingChatHeadService : Service, View.IOnTouchListener
{
    public const string ActionShow = "ACTION_SHOW";
    public const string ActionHide = "ACTION_HIDE";

    private IWindowManager? _windowManager;
    private View? _floatingWidgetView;
    private WindowManagerLayoutParams? _params;
    private TextView? _txtCount;
    private int _addedLinksCount = 0;
    private string _lastReadClipText = string.Empty;

    private int _initialX;
    private int _initialY;
    private float _initialTouchX;
    private float _initialTouchY;
    private long _touchStartTime;

    public static bool IsRunning { get; private set; }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        _windowManager = (IWindowManager?)GetSystemService(WindowService);
        BuildFloatingWidget();
        IsRunning = true;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        string action = intent?.Action ?? ActionShow;
        if (action == ActionHide)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        return StartCommandResult.Sticky;
    }

    private void BuildFloatingWidget()
    {
        try
        {
            if (_windowManager == null) return;

            var overlayType = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? WindowManagerTypes.ApplicationOverlay
                : WindowManagerTypes.Phone;

            _params = new WindowManagerLayoutParams(
                WindowManagerLayoutParams.WrapContent,
                WindowManagerLayoutParams.WrapContent,
                overlayType,
                WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits,
                Format.Translucent)
            {
                Gravity = GravityFlags.Top | GravityFlags.Start,
                X = 20,
                Y = 200
            };

            // Tạo layout Bong bóng nổi
            var rootLayout = new FrameLayout(this);
            rootLayout.SetPadding(8, 8, 8, 8);

            // Nút hình tròn chính
            var circleButton = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical
            };
            circleButton.SetGravity(GravityFlags.Center);
            circleButton.SetPadding(12, 12, 12, 12);

            var shapeDrawable = new GradientDrawable();
            shapeDrawable.SetShape(ShapeType.Oval);
            shapeDrawable.SetColors(new int[] { Color.ParseColor("#4F46E5"), Color.ParseColor("#7C3AED") });
            shapeDrawable.SetStroke(3, Color.ParseColor("#38BDF8"));
            circleButton.Background = shapeDrawable;

            var iconText = new TextView(this)
            {
                Text = "📥",
                TextSize = 20,
                Gravity = GravityFlags.Center
            };
            circleButton.AddView(iconText);

            var btnSubText = new TextView(this)
            {
                Text = "DÁN",
                TextSize = 8,
                Typeface = Typeface.DefaultBold,
                Gravity = GravityFlags.Center
            };
            btnSubText.SetTextColor(Color.ParseColor("#F8FAFC"));
            circleButton.AddView(btnSubText);

            var circleParams = new FrameLayout.LayoutParams(dpToPx(56), dpToPx(56))
            {
                Gravity = GravityFlags.Center
            };
            rootLayout.AddView(circleButton, circleParams);

            // Badge đếm số link đã nạp
            _txtCount = new TextView(this)
            {
                Text = "0",
                TextSize = 9,
                Typeface = Typeface.DefaultBold,
                Gravity = GravityFlags.Center,
                Visibility = ViewStates.Gone
            };
            _txtCount.SetTextColor(Color.White);

            var badgeDrawable = new GradientDrawable();
            badgeDrawable.SetShape(ShapeType.Oval);
            badgeDrawable.SetColor(Color.ParseColor("#EF4444"));
            badgeDrawable.SetStroke(2, Color.White);
            _txtCount.Background = badgeDrawable;

            var badgeParams = new FrameLayout.LayoutParams(dpToPx(20), dpToPx(20))
            {
                Gravity = GravityFlags.Top | GravityFlags.End
            };
            rootLayout.AddView(_txtCount, badgeParams);

            _floatingWidgetView = rootLayout;
            _floatingWidgetView.SetOnTouchListener(this);

            _windowManager.AddView(_floatingWidgetView, _params);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC_ChatHead", $"Lỗi tạo FloatingWidget: {ex.Message}");
        }
    }

    private int dpToPx(int dp)
    {
        float density = Resources?.DisplayMetrics?.Density ?? 1.0f;
        return (int)(dp * density + 0.5f);
    }

    public bool OnTouch(View? v, MotionEvent? e)
    {
        if (e == null || _params == null || _windowManager == null || _floatingWidgetView == null) return false;

        switch (e.Action)
        {
            case MotionEventActions.Down:
                _initialX = _params.X;
                _initialY = _params.Y;
                _initialTouchX = e.RawX;
                _initialTouchY = e.RawY;
                _touchStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return true;

            case MotionEventActions.Move:
                _params.X = _initialX + (int)(e.RawX - _initialTouchX);
                _params.Y = _initialY + (int)(e.RawY - _initialTouchY);
                try
                {
                    _windowManager.UpdateViewLayout(_floatingWidgetView, _params);
                }
                catch { }
                return true;

            case MotionEventActions.Up:
                long touchDuration = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _touchStartTime;
                float diffX = Math.Abs(e.RawX - _initialTouchX);
                float diffY = Math.Abs(e.RawY - _initialTouchY);

                // Nếu là một cú Chạm (Click) nhẹ (< 250ms và không di chuyển nhiều):
                if (touchDuration < 300 && diffX < 15 && diffY < 15)
                {
                    OnFloatingWidgetClicked();
                }
                return true;
        }

        return false;
    }

    private void OnFloatingWidgetClicked()
    {
        try
        {
            var cm = (ClipboardManager?)GetSystemService(ClipboardService);
            if (cm != null && cm.HasPrimaryClip)
            {
                var clip = cm.PrimaryClip;
                if (clip != null && clip.ItemCount > 0)
                {
                    var text = clip.GetItemAt(0)?.CoerceToText(this)?.ToString();
                    if (!string.IsNullOrWhiteSpace(text) &&
                        (text.Contains("http://", StringComparison.OrdinalIgnoreCase) || text.Contains("https://", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (text != _lastReadClipText)
                        {
                            _lastReadClipText = text;
                            _addedLinksCount++;
                            if (_txtCount != null)
                            {
                                _txtCount.Text = _addedLinksCount.ToString();
                                _txtCount.Visibility = ViewStates.Visible;
                            }

                            BackgroundExecutionService.NativeOnClipboardTextChanged?.Invoke(text);
                            Toast.MakeText(this, $"📥 [Tự dán] Đã nạp link #{_addedLinksCount} vào Queue!", ToastLength.Short)?.Show();
                        }
                        else
                        {
                            Toast.MakeText(this, "ℹ️ Link này đã được nạp vào Queue trước đó.", ToastLength.Short)?.Show();
                        }
                        return;
                    }
                }
            }

            Toast.MakeText(this, "⚠️ Clipboard hiện không chứa liên kết (http/https).", ToastLength.Short)?.Show();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC_ChatHead", $"Lỗi đọc Clipboard từ FloatingWidget: {ex.Message}");
        }
    }

    public static void Start(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(ComicFloatingChatHeadService));
            intent.SetAction(ActionShow);
            context.StartService(intent);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC_ChatHead", $"Lỗi Start FloatingChatHead: {ex.Message}");
        }
    }

    public static void Stop(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(ComicFloatingChatHeadService));
            intent.SetAction(ActionHide);
            context.StartService(intent);
        }
        catch { }
    }

    public override void OnDestroy()
    {
        IsRunning = false;
        try
        {
            if (_windowManager != null && _floatingWidgetView != null)
            {
                _windowManager.RemoveView(_floatingWidgetView);
                _floatingWidgetView = null;
            }
        }
        catch { }
        base.OnDestroy();
    }
}

