using System;
using Android.Animation;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Provider;
using Android.Util;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.Android;

/// <summary>
/// Trình quản lý Bong bóng nổi chạy ngầm (Floating Bubble) trên Android:
/// - Kiến trúc kép Dual-Mode:
///   1. In-App Floating Bubble: Gắn trực tiếp vào ViewGroup của Activity (0 cần quyền, hiện ngay 100%).
///   2. System Overlay Bubble: Toàn hệ thống, hiển thị trên mọi ứng dụng và màn hình chính khi được cấp quyền.
/// - Tự động thụt 60% vào mép viền (Auto-docking / Half-hide) sau 3.5 giây nhàn rỗi.
/// - Độ mờ linh hoạt (Dynamic Opacity): mờ 45% khi nhàn rỗi, 100% khi chạm hoặc có tiến trình mới.
/// - Kéo thả tự do và hít viền thông minh (Snap to screen edge).
/// - Chạm nhanh để mở lại ứng dụng tức thì.
/// - Mở trực tiếp trang Cài đặt cấp quyền 'Hiển thị trên ứng dụng khác' chuẩn xác mọi dòng máy.
/// </summary>
public static class FloatingBubbleManager
{
    private static IWindowManager? _windowManager;
    private static View? _bubbleView;
    private static WindowManagerLayoutParams? _systemLayoutParams;
    private static FrameLayout.LayoutParams? _inAppLayoutParams;
    private static ViewGroup? _inAppContainer;
    private static TextView? _badgeTextView;
    private static ImageView? _iconImageView;

    private static readonly Handler _mainHandler = new(Looper.MainLooper!);
    private static Action? _idleDockRunnable;
    private static bool _isDocked;
    private static bool _isRightSide = true;
    private static int _bubbleSizePx;
    private static bool _isSystemOverlay;
    private static bool _isShowing;

    public static bool IsShowing => _isShowing && _bubbleView != null;
    public const int RequestOverlayPermissionCode = 1099;
    public static bool IsSystemOverlayActive => _isShowing && _isSystemOverlay;

    public static bool CanDrawOverlays(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            try
            {
                var appContext = context.ApplicationContext ?? context;
                return Settings.CanDrawOverlays(appContext);
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    public static void OpenOverlaySettings(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            var activity = context as Activity ?? MainActivity.CurrentInstance;
            bool opened = false;

            // 1. Chuẩn Android: ACTION_MANAGE_OVERLAY_PERMISSION với package URI
            try
            {
                var uri = global::Android.Net.Uri.Parse("package:" + context.PackageName);
                var intent = new Intent(Settings.ActionManageOverlayPermission, uri);
                if (activity != null)
                {
                    activity.StartActivityForResult(intent, RequestOverlayPermissionCode);
                }
                else
                {
                    intent.AddFlags(ActivityFlags.NewTask);
                    context.StartActivity(intent);
                }
                opened = true;
            }
            catch { }

            // 2. Chuẩn Android: ACTION_MANAGE_OVERLAY_PERMISSION không có URI (cho các ROM chặn package URI)
            if (!opened)
            {
                try
                {
                    var intent = new Intent(Settings.ActionManageOverlayPermission);
                    if (activity != null)
                    {
                        activity.StartActivityForResult(intent, RequestOverlayPermissionCode);
                    }
                    else
                    {
                        intent.AddFlags(ActivityFlags.NewTask);
                        context.StartActivity(intent);
                    }
                    opened = true;
                }
                catch { }
            }

            // 3. Fallback: Trang Cài đặt ứng dụng
            if (!opened)
            {
                try
                {
                    var intent = new Intent(Settings.ActionApplicationDetailsSettings);
                    intent.SetData(global::Android.Net.Uri.Parse("package:" + context.PackageName));
                    if (activity != null)
                    {
                        activity.StartActivityForResult(intent, RequestOverlayPermissionCode);
                    }
                    else
                    {
                        intent.AddFlags(ActivityFlags.NewTask);
                        context.StartActivity(intent);
                    }
                    opened = true;
                }
                catch { }
            }
        }
    }

    public static void ShowBubble(Context context)
    {
        var activity = context as Activity ?? MainActivity.CurrentInstance;

        if (IsShowing)
        {
            WakeUpAndRestore();
            return;
        }

        bool hasOverlayPermission = CanDrawOverlays(context);

        if (hasOverlayPermission)
        {
            ShowSystemOverlayBubble(context);
        }
        else
        {
            // 1. Hiển thị Bong bóng trong ứng dụng (In-App) NGAY LẬP TỨC để người dùng thấy bong bóng hoạt động
            if (activity != null)
            {
                ShowInAppBubble(activity);
            }

            // 2. Mở trực tiếp trang cấp quyền Overlay để bong bóng bay ra ngoài màn hình chính
            try
            {
                Toast.MakeText(context, "👉 Vui lòng bật 'Hiển thị trên ứng dụng khác' để bong bóng xuất hiện ngoài màn hình chính!", ToastLength.Long)?.Show();
            }
            catch { }

            OpenOverlaySettings(context);
        }
    }

    public static void PromoteToSystemOverlay(Context context)
    {
        if (CanDrawOverlays(context))
        {
            if (_isShowing && !_isSystemOverlay)
            {
                // Đang hiển thị In-App, gỡ In-App và nâng cấp lên System Overlay
                HideBubble();
                ShowSystemOverlayBubble(context);
            }
            else if (!_isShowing)
            {
                ShowSystemOverlayBubble(context);
            }
        }
    }

    public static void ShowSystemOverlayBubble(Context context)
    {
        try
        {
            var appContext = context.ApplicationContext ?? context;
            _windowManager = (context as Activity)?.WindowManager ??
                             (IWindowManager?)context.GetSystemService(Context.WindowService) ??
                             (IWindowManager?)appContext.GetSystemService(Context.WindowService);

            if (_windowManager == null) return;

            _bubbleSizePx = DpToPx(context, 60);
            int screenWidth = GetScreenWidth(context);
            int screenHeight = GetScreenHeight(context);

            var windowType = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? WindowManagerTypes.ApplicationOverlay
                : WindowManagerTypes.Phone;

            var flags = WindowManagerFlags.NotFocusable |
                        WindowManagerFlags.NotTouchModal |
                        WindowManagerFlags.HardwareAccelerated;

            _systemLayoutParams = new WindowManagerLayoutParams(
                _bubbleSizePx,
                _bubbleSizePx,
                windowType,
                flags,
                Format.Translucent)
            {
                Gravity = GravityFlags.Top | GravityFlags.Left,
                X = screenWidth - _bubbleSizePx,
                Y = Math.Max(DpToPx(context, 100), screenHeight / 3)
            };

            _isRightSide = true;
            _isDocked = false;
            _isSystemOverlay = true;

            _bubbleView = CreateBubbleView(context);

            var touchListener = new SystemBubbleTouchListener(
                context,
                _windowManager,
                _systemLayoutParams,
                _bubbleSizePx,
                onTouchStart: () =>
                {
                    CancelIdleTimer();
                    WakeUpAndRestore();
                },
                onTap: () =>
                {
                    OpenMainActivity(context);
                },
                onDragEnd: (isRightSide) =>
                {
                    _isRightSide = isRightSide;
                    StartIdleTimer();
                });

            _bubbleView.SetOnTouchListener(touchListener);

            _windowManager.AddView(_bubbleView, _systemLayoutParams);
            _isShowing = true;

            // Đăng ký nhận tiến trình để cập nhật badge và đánh thức độ mờ nhẹ
            BackgroundExecutionService.Instance.TaskProgressChanged -= OnTaskProgressChanged;
            BackgroundExecutionService.Instance.TaskProgressChanged += OnTaskProgressChanged;

            StartIdleTimer();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ComicGMTPC_Bubble", $"Lỗi tạo System Overlay Bubble: {ex.Message}");
            // Fallback hiển thị In-App nếu tạo System Overlay lỗi
            if (context is Activity act)
            {
                ShowInAppBubble(act);
            }
        }
    }

    public static void ShowInAppBubble(Activity activity)
    {
        try
        {
            var rootContainer = activity.FindViewById<ViewGroup>(global::Android.Resource.Id.Content) ??
                                (activity.Window?.DecorView as ViewGroup);
            if (rootContainer == null) return;

            _bubbleSizePx = DpToPx(activity, 60);
            int screenWidth = GetScreenWidth(activity);
            int screenHeight = GetScreenHeight(activity);

            _inAppContainer = rootContainer;
            _isRightSide = true;
            _isDocked = false;
            _isSystemOverlay = false;

            _inAppLayoutParams = new FrameLayout.LayoutParams(_bubbleSizePx, _bubbleSizePx)
            {
                Gravity = GravityFlags.Top | GravityFlags.Left,
                LeftMargin = screenWidth - _bubbleSizePx,
                TopMargin = Math.Max(DpToPx(activity, 100), screenHeight / 3)
            };

            _bubbleView = CreateBubbleView(activity);

            var touchListener = new InAppBubbleTouchListener(
                activity,
                _inAppLayoutParams,
                _bubbleSizePx,
                onTouchStart: () =>
                {
                    CancelIdleTimer();
                    WakeUpAndRestore();
                },
                onTap: () =>
                {
                    WakeUpAndRestore();
                    Toast.MakeText(activity, "🫧 Comic Downloader GMTPC đang hoạt động", ToastLength.Short)?.Show();
                },
                onDragEnd: (isRightSide) =>
                {
                    _isRightSide = isRightSide;
                    StartIdleTimer();
                });

            _bubbleView.SetOnTouchListener(touchListener);
            if (_bubbleView.Parent != null)
            {
                try { ((ViewGroup)_bubbleView.Parent).RemoveView(_bubbleView); } catch { }
            }
            rootContainer.AddView(_bubbleView, _inAppLayoutParams);
            _bubbleView.BringToFront();
            if (OperatingSystem.IsAndroidVersionAtLeast(21))
            {
                _bubbleView.Elevation = DpToPx(activity, 25);
                _bubbleView.TranslationZ = DpToPx(activity, 15);
            }
            rootContainer.RequestLayout();
            rootContainer.Invalidate();
            _isShowing = true;

            BackgroundExecutionService.Instance.TaskProgressChanged -= OnTaskProgressChanged;
            BackgroundExecutionService.Instance.TaskProgressChanged += OnTaskProgressChanged;

            StartIdleTimer();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ComicGMTPC_Bubble", $"Lỗi tạo In-App Bubble: {ex.Message}");
        }
    }

    private static View CreateBubbleView(Context context)
    {
        var container = new FrameLayout(context);

        // Nền tròn công nghệ với viền Gradient tím neon cao cấp
        var bgDrawable = new GradientDrawable();
        bgDrawable.SetShape(ShapeType.Oval);
        bgDrawable.SetColors(new int[] {
            (int)new Color(0x4F, 0x46, 0xE5), // #4F46E5 Indigo
            (int)new Color(0x0F, 0x17, 0x2A)  // #0F172A Dark Slate
        });
        bgDrawable.SetStroke(DpToPx(context, 2.5f), new Color(0xC0, 0x84, 0xFC)); // #C084FC Bright Purple
        container.Background = bgDrawable;

        if (OperatingSystem.IsAndroidVersionAtLeast(21))
        {
            container.Elevation = DpToPx(context, 10);
        }

        // Icon ứng dụng ở trung tâm
        _iconImageView = new ImageView(context);
        try
        {
            _iconImageView.SetImageResource(Resource.Drawable.Icon);
        }
        catch { }
        _iconImageView.SetScaleType(ImageView.ScaleType.FitCenter);
        var iconLp = new FrameLayout.LayoutParams(DpToPx(context, 36), DpToPx(context, 36))
        {
            Gravity = GravityFlags.Center
        };
        container.AddView(_iconImageView, iconLp);

        // Badge trạng thái / tiến trình ở góc dưới
        _badgeTextView = new TextView(context);
        _badgeTextView.SetTextColor(Color.White);
        _badgeTextView.SetTextSize(ComplexUnitType.Dip, 9);
        _badgeTextView.Gravity = GravityFlags.Center;
        _badgeTextView.SetTypeface(null, TypefaceStyle.Bold);

        var badgeBg = new GradientDrawable();
        badgeBg.SetShape(ShapeType.Oval);
        badgeBg.SetColor((int)new Color(0x10, 0xB9, 0x81)); // Green #10B981
        _badgeTextView.Background = badgeBg;

        int badgeSize = DpToPx(context, 16);
        var badgeLp = new FrameLayout.LayoutParams(badgeSize, badgeSize)
        {
            Gravity = GravityFlags.Bottom | GravityFlags.Right,
            RightMargin = DpToPx(context, 3),
            BottomMargin = DpToPx(context, 3)
        };
        _badgeTextView.Visibility = ViewStates.Gone; // Chỉ hiện khi có tiến trình
        container.AddView(_badgeTextView, badgeLp);

        return container;
    }

    private static void StartIdleTimer()
    {
        CancelIdleTimer();
        _idleDockRunnable = () =>
        {
            PerformAutoDockAndFade();
        };
        _mainHandler.PostDelayed(_idleDockRunnable, 3500); // 3.5 giây nhàn rỗi
    }

    private static void CancelIdleTimer()
    {
        if (_idleDockRunnable != null)
        {
            _mainHandler.RemoveCallbacks(_idleDockRunnable);
            _idleDockRunnable = null;
        }
    }

    private static void PerformAutoDockAndFade()
    {
        if (_bubbleView == null || _isDocked) return;
        _isDocked = true;

        var context = _bubbleView.Context;
        if (context == null) return;
        int screenWidth = GetScreenWidth(context);

        // Thụt 60% vào mép màn hình, chừa 40% làm tay cầm chỉ thị
        int dockOffset = (int)(_bubbleSizePx * 0.60f);

        if (_isSystemOverlay && _systemLayoutParams != null && _windowManager != null)
        {
            int targetX = _isRightSide ? (screenWidth - _bubbleSizePx + dockOffset) : (-dockOffset);
            AnimateSystemX(_systemLayoutParams.X, targetX, 280, () =>
            {
                _bubbleView?.Animate()
                    ?.Alpha(0.45f)
                    ?.SetDuration(300)
                    ?.SetInterpolator(new DecelerateInterpolator())
                    ?.Start();
            });
        }
        else if (!_isSystemOverlay && _inAppLayoutParams != null)
        {
            int targetLeft = _isRightSide ? (screenWidth - _bubbleSizePx + dockOffset) : (-dockOffset);
            AnimateInAppX(_inAppLayoutParams.LeftMargin, targetLeft, 280, () =>
            {
                _bubbleView?.Animate()
                    ?.Alpha(0.45f)
                    ?.SetDuration(300)
                    ?.SetInterpolator(new DecelerateInterpolator())
                    ?.Start();
            });
        }
    }

    public static void WakeUpAndRestore()
    {
        if (_bubbleView == null) return;

        // Khôi phục độ mờ 100%
        _bubbleView.Animate()
            ?.Alpha(1.0f)
            ?.SetDuration(150)
            ?.Start();

        // Nếu đang thụt vào mép, trượt bung ra toàn bộ
        if (_isDocked)
        {
            _isDocked = false;
            var context = _bubbleView.Context;
            if (context == null) return;
            int screenWidth = GetScreenWidth(context);

            if (_isSystemOverlay && _systemLayoutParams != null && _windowManager != null)
            {
                int targetX = _isRightSide ? (screenWidth - _bubbleSizePx) : 0;
                AnimateSystemX(_systemLayoutParams.X, targetX, 200, () =>
                {
                    StartIdleTimer();
                });
            }
            else if (!_isSystemOverlay && _inAppLayoutParams != null)
            {
                int targetLeft = _isRightSide ? (screenWidth - _bubbleSizePx) : 0;
                AnimateInAppX(_inAppLayoutParams.LeftMargin, targetLeft, 200, () =>
                {
                    StartIdleTimer();
                });
            }
        }
        else
        {
            StartIdleTimer();
        }
    }

    private static void AnimateSystemX(int startX, int endX, long duration, Action? onEnd = null)
    {
        if (_bubbleView == null || _systemLayoutParams == null || _windowManager == null)
        {
            onEnd?.Invoke();
            return;
        }

        var anim = ValueAnimator.OfInt(startX, endX);
        anim?.SetDuration(duration);
        anim?.SetInterpolator(new DecelerateInterpolator());
        if (anim != null)
        {
            anim.Update += (s, e) =>
            {
                try
                {
                    if (_bubbleView != null && _systemLayoutParams != null && _windowManager != null)
                    {
                        _systemLayoutParams.X = (int)e.Animation.AnimatedValue!;
                        _windowManager.UpdateViewLayout(_bubbleView, _systemLayoutParams);
                    }
                }
                catch { }
            };
            anim.AnimationEnd += (s, e) =>
            {
                onEnd?.Invoke();
            };
            anim.Start();
        }
        else
        {
            _systemLayoutParams.X = endX;
            try { _windowManager.UpdateViewLayout(_bubbleView, _systemLayoutParams); } catch { }
            onEnd?.Invoke();
        }
    }

    private static void AnimateInAppX(int startLeft, int endLeft, long duration, Action? onEnd = null)
    {
        if (_bubbleView == null || _inAppLayoutParams == null)
        {
            onEnd?.Invoke();
            return;
        }

        var anim = ValueAnimator.OfInt(startLeft, endLeft);
        anim?.SetDuration(duration);
        anim?.SetInterpolator(new DecelerateInterpolator());
        if (anim != null)
        {
            anim.Update += (s, e) =>
            {
                try
                {
                    if (_bubbleView != null && _inAppLayoutParams != null)
                    {
                        _inAppLayoutParams.LeftMargin = (int)e.Animation.AnimatedValue!;
                        _bubbleView.LayoutParameters = _inAppLayoutParams;
                    }
                }
                catch { }
            };
            anim.AnimationEnd += (s, e) =>
            {
                onEnd?.Invoke();
            };
            anim.Start();
        }
        else
        {
            _inAppLayoutParams.LeftMargin = endLeft;
            try { _bubbleView.LayoutParameters = _inAppLayoutParams; } catch { }
            onEnd?.Invoke();
        }
    }

    private static void OnTaskProgressChanged(BackgroundTaskInfo task)
    {
        _mainHandler.Post(() =>
        {
            try
            {
                if (_badgeTextView == null || _bubbleView == null) return;

                if (task.IsRunning)
                {
                    _badgeTextView.Visibility = ViewStates.Visible;
                    _badgeTextView.Text = task.ProgressPercentage > 0 ? $"{task.ProgressPercentage:0}" : "●";

                    // Khi có tiến trình đang chạy, tạm thời tăng độ rõ lên 0.85f trong 2.5 giây rồi mới mờ lại nếu đang docked
                    if (_isDocked)
                    {
                        _bubbleView.Animate()?.Alpha(0.85f)?.SetDuration(200)?.Start();
                        CancelIdleTimer();
                        _mainHandler.PostDelayed(() =>
                        {
                            if (_isDocked && _bubbleView != null)
                            {
                                _bubbleView.Animate()?.Alpha(0.45f)?.SetDuration(300)?.Start();
                            }
                        }, 2500);
                    }
                }
                else
                {
                    var primary = BackgroundExecutionService.Instance.GetPrimaryTask();
                    if (primary != null)
                    {
                        _badgeTextView.Visibility = ViewStates.Visible;
                        _badgeTextView.Text = $"{primary.ProgressPercentage:0}";
                    }
                    else
                    {
                        _badgeTextView.Visibility = ViewStates.Gone;
                    }
                }
            }
            catch { }
        });
    }

    public static void HideBubble()
    {
        CancelIdleTimer();
        BackgroundExecutionService.Instance.TaskProgressChanged -= OnTaskProgressChanged;

        _mainHandler.Post(() =>
        {
            try
            {
                if (_bubbleView != null)
                {
                    if (_isSystemOverlay && _windowManager != null)
                    {
                        _windowManager.RemoveView(_bubbleView);
                    }
                    else if (!_isSystemOverlay && _inAppContainer != null)
                    {
                        _inAppContainer.RemoveView(_bubbleView);
                    }
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("ComicGMTPC_Bubble", $"Lỗi ẩn bubble: {ex.Message}");
            }
            finally
            {
                _bubbleView = null;
                _systemLayoutParams = null;
                _inAppLayoutParams = null;
                _inAppContainer = null;
                _badgeTextView = null;
                _iconImageView = null;
                _isDocked = false;
                _isShowing = false;
            }
        });
    }

    private class SystemBubbleTouchListener : Java.Lang.Object, View.IOnTouchListener
    {
        private readonly Context _context;
        private readonly IWindowManager _wm;
        private readonly WindowManagerLayoutParams _lp;
        private readonly int _bubbleSize;
        private readonly Action _onTouchStart;
        private readonly Action _onTap;
        private readonly Action<bool> _onDragEnd;

        private float _startX, _startY;
        private float _touchX, _touchY;
        private bool _isDragging;
        private readonly int _touchSlop;

        public SystemBubbleTouchListener(
            Context context,
            IWindowManager wm,
            WindowManagerLayoutParams lp,
            int bubbleSize,
            Action onTouchStart,
            Action onTap,
            Action<bool> onDragEnd)
        {
            _context = context;
            _wm = wm;
            _lp = lp;
            _bubbleSize = bubbleSize;
            _onTouchStart = onTouchStart;
            _onTap = onTap;
            _onDragEnd = onDragEnd;
            _touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 16;
        }

        public bool OnTouch(View? v, MotionEvent? e)
        {
            if (e == null || v == null) return false;

            switch (e.Action)
            {
                case MotionEventActions.Down:
                    _startX = _lp.X;
                    _startY = _lp.Y;
                    _touchX = e.RawX;
                    _touchY = e.RawY;
                    _isDragging = false;
                    _onTouchStart?.Invoke();
                    return true;

                case MotionEventActions.Move:
                    float dx = e.RawX - _touchX;
                    float dy = e.RawY - _touchY;
                    if (!_isDragging && (Math.Abs(dx) > _touchSlop || Math.Abs(dy) > _touchSlop))
                    {
                        _isDragging = true;
                    }
                    if (_isDragging)
                    {
                        _lp.X = (int)(_startX + dx);
                        _lp.Y = (int)(_startY + dy);
                        try
                        {
                            _wm.UpdateViewLayout(v, _lp);
                        }
                        catch { }
                    }
                    return true;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    if (!_isDragging)
                    {
                        _onTap?.Invoke();
                    }
                    else
                    {
                        int screenWidth = GetScreenWidth(_context);
                        int screenHeight = GetScreenHeight(_context);
                        bool isRightSide = (_lp.X + _bubbleSize / 2) > (screenWidth / 2);
                        int snapX = isRightSide ? (screenWidth - _bubbleSize) : 0;

                        int minY = DpToPx(_context, 40);
                        int maxY = screenHeight - _bubbleSize - DpToPx(_context, 40);
                        int snapY = Math.Clamp(_lp.Y, minY, Math.Max(minY, maxY));

                        AnimateSnap(_lp.X, snapX, _lp.Y, snapY, () =>
                        {
                            _onDragEnd?.Invoke(isRightSide);
                        });
                    }
                    return true;
            }

            return false;
        }

        private void AnimateSnap(int startX, int endX, int startY, int endY, Action? onEnd)
        {
            var animX = ValueAnimator.OfInt(startX, endX);
            var animY = ValueAnimator.OfInt(startY, endY);

            animX?.SetDuration(220);
            animY?.SetDuration(220);
            var interpolator = new DecelerateInterpolator();
            animX?.SetInterpolator(interpolator);
            animY?.SetInterpolator(interpolator);

            animX!.Update += (s, e) =>
            {
                try
                {
                    _lp.X = (int)e.Animation.AnimatedValue!;
                    if (_bubbleView != null)
                    {
                        _wm.UpdateViewLayout(_bubbleView, _lp);
                    }
                }
                catch { }
            };

            animY!.Update += (s, e) =>
            {
                try
                {
                    _lp.Y = (int)e.Animation.AnimatedValue!;
                    if (_bubbleView != null)
                    {
                        _wm.UpdateViewLayout(_bubbleView, _lp);
                    }
                }
                catch { }
            };

            animX.AnimationEnd += (s, e) =>
            {
                onEnd?.Invoke();
            };

            animX.Start();
            animY.Start();
        }
    }

    private class InAppBubbleTouchListener : Java.Lang.Object, View.IOnTouchListener
    {
        private readonly Activity _activity;
        private readonly FrameLayout.LayoutParams _lp;
        private readonly int _bubbleSize;
        private readonly Action _onTouchStart;
        private readonly Action _onTap;
        private readonly Action<bool> _onDragEnd;

        private float _startLeft, _startTop;
        private float _touchX, _touchY;
        private bool _isDragging;
        private readonly int _touchSlop;

        public InAppBubbleTouchListener(
            Activity activity,
            FrameLayout.LayoutParams lp,
            int bubbleSize,
            Action onTouchStart,
            Action onTap,
            Action<bool> onDragEnd)
        {
            _activity = activity;
            _lp = lp;
            _bubbleSize = bubbleSize;
            _onTouchStart = onTouchStart;
            _onTap = onTap;
            _onDragEnd = onDragEnd;
            _touchSlop = ViewConfiguration.Get(activity)?.ScaledTouchSlop ?? 16;
        }

        public bool OnTouch(View? v, MotionEvent? e)
        {
            if (e == null || v == null) return false;

            switch (e.Action)
            {
                case MotionEventActions.Down:
                    _startLeft = _lp.LeftMargin;
                    _startTop = _lp.TopMargin;
                    _touchX = e.RawX;
                    _touchY = e.RawY;
                    _isDragging = false;
                    _onTouchStart?.Invoke();
                    return true;

                case MotionEventActions.Move:
                    float dx = e.RawX - _touchX;
                    float dy = e.RawY - _touchY;
                    if (!_isDragging && (Math.Abs(dx) > _touchSlop || Math.Abs(dy) > _touchSlop))
                    {
                        _isDragging = true;
                    }
                    if (_isDragging)
                    {
                        _lp.LeftMargin = (int)(_startLeft + dx);
                        _lp.TopMargin = (int)(_startTop + dy);
                        try
                        {
                            v.LayoutParameters = _lp;
                        }
                        catch { }
                    }
                    return true;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    if (!_isDragging)
                    {
                        _onTap?.Invoke();
                    }
                    else
                    {
                        int screenWidth = GetScreenWidth(_activity);
                        int screenHeight = GetScreenHeight(_activity);
                        bool isRightSide = (_lp.LeftMargin + _bubbleSize / 2) > (screenWidth / 2);
                        int snapLeft = isRightSide ? (screenWidth - _bubbleSize) : 0;

                        int minY = DpToPx(_activity, 40);
                        int maxY = screenHeight - _bubbleSize - DpToPx(_activity, 40);
                        int snapTop = Math.Clamp(_lp.TopMargin, minY, Math.Max(minY, maxY));

                        AnimateInAppSnap(_lp.LeftMargin, snapLeft, _lp.TopMargin, snapTop, () =>
                        {
                            _onDragEnd?.Invoke(isRightSide);
                        });
                    }
                    return true;
            }

            return false;
        }

        private void AnimateInAppSnap(int startLeft, int endLeft, int startTop, int endTop, Action? onEnd)
        {
            var animX = ValueAnimator.OfInt(startLeft, endLeft);
            var animY = ValueAnimator.OfInt(startTop, endTop);

            animX?.SetDuration(220);
            animY?.SetDuration(220);
            var interpolator = new DecelerateInterpolator();
            animX?.SetInterpolator(interpolator);
            animY?.SetInterpolator(interpolator);

            animX!.Update += (s, e) =>
            {
                try
                {
                    _lp.LeftMargin = (int)e.Animation.AnimatedValue!;
                    if (_bubbleView != null)
                    {
                        _bubbleView.LayoutParameters = _lp;
                    }
                }
                catch { }
            };

            animY!.Update += (s, e) =>
            {
                try
                {
                    _lp.TopMargin = (int)e.Animation.AnimatedValue!;
                    if (_bubbleView != null)
                    {
                        _bubbleView.LayoutParameters = _lp;
                    }
                }
                catch { }
            };

            animX.AnimationEnd += (s, e) =>
            {
                onEnd?.Invoke();
            };

            animX.Start();
            animY.Start();
        }
    }

    private static void OpenMainActivity(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(MainActivity));
            intent.SetAction(Intent.ActionMain);
            intent.AddCategory(Intent.CategoryLauncher);
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ReorderToFront);
            context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC_Bubble", $"Không thể mở MainActivity: {ex.Message}");
        }
    }

    public static int GetScreenWidth(Context context)
    {
        try
        {
            if (context is Activity act && OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var metrics = act.WindowManager?.CurrentWindowMetrics;
                if (metrics != null)
                {
                    return metrics.Bounds.Width();
                }
            }
        }
        catch { }

        try
        {
            var dm = context.Resources?.DisplayMetrics;
            if (dm != null && dm.WidthPixels > 0)
            {
                return dm.WidthPixels;
            }
        }
        catch { }

        return 1080;
    }

    public static int GetScreenHeight(Context context)
    {
        try
        {
            if (context is Activity act && OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var metrics = act.WindowManager?.CurrentWindowMetrics;
                if (metrics != null)
                {
                    return metrics.Bounds.Height();
                }
            }
        }
        catch { }

        try
        {
            var dm = context.Resources?.DisplayMetrics;
            if (dm != null && dm.HeightPixels > 0)
            {
                return dm.HeightPixels;
            }
        }
        catch { }

        return 1920;
    }

    public static int DpToPx(Context context, float dp)
    {
        float density = context.Resources?.DisplayMetrics?.Density ?? 1.0f;
        return (int)Math.Ceiling(dp * density);
    }
}
