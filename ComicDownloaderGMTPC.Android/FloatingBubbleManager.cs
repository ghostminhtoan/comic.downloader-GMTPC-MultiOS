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
/// - Tự động thụt 60% vào mép viền (Auto-docking / Half-hide) sau 3.5 giây nhàn rỗi.
/// - Độ mờ linh hoạt (Dynamic Opacity): mờ 45% khi nhàn rỗi để không che màn hình, 100% khi chạm hoặc có tác vụ mới.
/// - Kéo thả tự do và hít viền thông minh (Snap to nearest screen edge).
/// - Chạm nhanh để mở lại ứng dụng tức thì.
/// - Tích hợp đồng bộ trạng thái với Android Overlay và Android Bubble API.
/// </summary>
public static class FloatingBubbleManager
{
    private static IWindowManager? _windowManager;
    private static View? _bubbleView;
    private static WindowManagerLayoutParams? _layoutParams;
    private static TextView? _badgeTextView;
    private static ImageView? _iconImageView;

    private static readonly Handler _mainHandler = new(Looper.MainLooper!);
    private static Action? _idleDockRunnable;
    private static bool _isDocked;
    private static bool _isRightSide = true;
    private static int _bubbleSizePx;

    public static bool IsShowing => _bubbleView != null && _bubbleView.IsAttachedToWindow;

    public static bool CanDrawOverlays(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            return Settings.CanDrawOverlays(context);
        }
        return true;
    }

    public static void RequestOverlayPermission(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(23) && !Settings.CanDrawOverlays(context))
        {
            try
            {
                var intent = new Intent(
                    Settings.ActionManageOverlayPermission,
                    global::Android.Net.Uri.Parse("package:" + context.PackageName));
                intent.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(intent);
                Toast.MakeText(context, "Vui lòng bật quyền 'Hiển thị trên ứng dụng khác' để sử dụng Bong bóng nổi", ToastLength.Long)?.Show();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("ComicGMTPC_Bubble", $"Lỗi mở cài đặt overlay: {ex.Message}");
            }
        }
    }

    public static void ShowBubble(Context context)
    {
        var appContext = context.ApplicationContext ?? context;

        if (OperatingSystem.IsAndroidVersionAtLeast(23) && !Settings.CanDrawOverlays(appContext))
        {
            RequestOverlayPermission(context);
            return;
        }

        if (IsShowing)
        {
            WakeUpAndRestore();
            return;
        }

        try
        {
            _windowManager = appContext.GetSystemService(Context.WindowService) as IWindowManager;
            if (_windowManager == null) return;

            _bubbleSizePx = DpToPx(appContext, 58);
            int screenWidth = GetScreenWidth(appContext);
            int screenHeight = GetScreenHeight(appContext);

            var windowType = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? WindowManagerTypes.ApplicationOverlay
                : WindowManagerTypes.Phone;

            var flags = WindowManagerFlags.NotFocusable |
                        WindowManagerFlags.LayoutNoLimits |
                        WindowManagerFlags.HardwareAccelerated;

            _layoutParams = new WindowManagerLayoutParams(
                _bubbleSizePx,
                _bubbleSizePx,
                windowType,
                flags,
                Format.Translucent)
            {
                Gravity = GravityFlags.Top | GravityFlags.Left,
                X = screenWidth - _bubbleSizePx,
                Y = screenHeight / 3
            };

            _isRightSide = true;
            _isDocked = false;

            _bubbleView = CreateBubbleView(appContext);

            var touchListener = new BubbleTouchListener(
                appContext,
                _windowManager,
                _layoutParams,
                _bubbleSizePx,
                onTouchStart: () =>
                {
                    CancelIdleTimer();
                    WakeUpAndRestore();
                },
                onTap: () =>
                {
                    OpenMainActivity(appContext);
                },
                onDragEnd: (isRightSide) =>
                {
                    _isRightSide = isRightSide;
                    StartIdleTimer();
                });

            _bubbleView.SetOnTouchListener(touchListener);

            _windowManager.AddView(_bubbleView, _layoutParams);

            // Đăng ký nhận tiến trình để cập nhật badge và đánh thức độ mờ nhẹ
            BackgroundExecutionService.Instance.TaskProgressChanged -= OnTaskProgressChanged;
            BackgroundExecutionService.Instance.TaskProgressChanged += OnTaskProgressChanged;

            StartIdleTimer();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ComicGMTPC_Bubble", $"Lỗi tạo Bong bóng nổi: {ex.Message}");
        }
    }

    private static View CreateBubbleView(Context context)
    {
        var container = new FrameLayout(context);

        // Nền tròn công nghệ với viền Gradient tím neon cao cấp
        var bgDrawable = new GradientDrawable();
        bgDrawable.SetShape(ShapeType.Oval);
        bgDrawable.SetColors(new int[] {
            (int)new Color(0x1E, 0x1B, 0x4B), // #1E1B4B
            (int)new Color(0x0F, 0x17, 0x2A)  // #0F172A
        });
        bgDrawable.SetStroke(DpToPx(context, 2), new Color(0x8B, 0x5C, 0xF6)); // #8B5CF6
        container.Background = bgDrawable;

        if (OperatingSystem.IsAndroidVersionAtLeast(21))
        {
            container.Elevation = DpToPx(context, 6);
        }

        // Icon ứng dụng ở trung tâm
        _iconImageView = new ImageView(context);
        _iconImageView.SetImageResource(Resource.Drawable.Icon);
        _iconImageView.SetScaleType(ImageView.ScaleType.FitCenter);
        var iconLp = new FrameLayout.LayoutParams(DpToPx(context, 34), DpToPx(context, 34))
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
        if (_bubbleView == null || !_bubbleView.IsAttachedToWindow || _layoutParams == null || _windowManager == null)
            return;

        if (_isDocked) return;
        _isDocked = true;

        var context = _bubbleView.Context;
        if (context == null) return;
        int screenWidth = GetScreenWidth(context);

        // Thụt 60% vào mép màn hình, chừa 40% làm tay cầm chỉ thị
        int dockOffset = (int)(_bubbleSizePx * 0.60f);
        int targetX = _isRightSide ? (screenWidth - _bubbleSizePx + dockOffset) : (-dockOffset);

        // Animation mượt mà trượt thụt vào mép
        AnimateX(_layoutParams.X, targetX, 280, () =>
        {
            // Giảm độ mờ linh hoạt xuống 0.45f để không che khuất màn hình phía dưới
            _bubbleView.Animate()
                ?.Alpha(0.45f)
                ?.SetDuration(300)
                ?.SetInterpolator(new DecelerateInterpolator())
                ?.Start();
        });
    }

    public static void WakeUpAndRestore()
    {
        if (_bubbleView == null || !_bubbleView.IsAttachedToWindow || _layoutParams == null || _windowManager == null)
            return;

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
            int targetX = _isRightSide ? (screenWidth - _bubbleSizePx) : 0;
            AnimateX(_layoutParams.X, targetX, 200, () =>
            {
                StartIdleTimer();
            });
        }
        else
        {
            StartIdleTimer();
        }
    }

    private static void AnimateX(int startX, int endX, long duration, Action? onEnd = null)
    {
        if (_bubbleView == null || !_bubbleView.IsAttachedToWindow || _layoutParams == null || _windowManager == null)
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
                    if (_bubbleView != null && _bubbleView.IsAttachedToWindow && _layoutParams != null)
                    {
                        _layoutParams.X = (int)e.Animation.AnimatedValue!;
                        _windowManager?.UpdateViewLayout(_bubbleView, _layoutParams);
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
            _layoutParams.X = endX;
            try { _windowManager.UpdateViewLayout(_bubbleView, _layoutParams); } catch { }
            onEnd?.Invoke();
        }
    }

    private static void OnTaskProgressChanged(BackgroundTaskInfo task)
    {
        _mainHandler.Post(() =>
        {
            try
            {
                if (_badgeTextView == null || _bubbleView == null || !_bubbleView.IsAttachedToWindow) return;

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
                            if (_isDocked && _bubbleView != null && _bubbleView.IsAttachedToWindow)
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
                if (_bubbleView != null && _windowManager != null)
                {
                    if (_bubbleView.IsAttachedToWindow)
                    {
                        _windowManager.RemoveView(_bubbleView);
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
                _layoutParams = null;
                _badgeTextView = null;
                _iconImageView = null;
                _isDocked = false;
            }
        });
    }

    private class BubbleTouchListener : Java.Lang.Object, View.IOnTouchListener
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

        public BubbleTouchListener(
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
                            if (v.IsAttachedToWindow)
                            {
                                _wm.UpdateViewLayout(v, _lp);
                            }
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
                    if (_bubbleView != null && _bubbleView.IsAttachedToWindow)
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
                    if (_bubbleView != null && _bubbleView.IsAttachedToWindow)
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

    private static void OpenMainActivity(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(MainActivity));
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
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var wm = (IWindowManager?)context.GetSystemService(Context.WindowService);
            var metrics = wm?.CurrentWindowMetrics;
            if (metrics != null)
            {
                return metrics.Bounds.Width();
            }
        }
        return context.Resources?.DisplayMetrics?.WidthPixels ?? 1080;
    }

    public static int GetScreenHeight(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var wm = (IWindowManager?)context.GetSystemService(Context.WindowService);
            var metrics = wm?.CurrentWindowMetrics;
            if (metrics != null)
            {
                return metrics.Bounds.Height();
            }
        }
        return context.Resources?.DisplayMetrics?.HeightPixels ?? 1920;
    }

    public static int DpToPx(Context context, float dp)
    {
        float density = context.Resources?.DisplayMetrics?.Density ?? 1.0f;
        return (int)Math.Ceiling(dp * density);
    }
}

