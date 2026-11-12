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
    private static CircularProgressRingView? _progressRingView;
    private static GradientDrawable? _badgeBg;
    private static TextView? _toggleQuickAddView;
    private static TextView? _toggleCloseView;
    private static View? _quickAddSatelliteView;
    private static View? _closeSatelliteView;
    private static WindowManagerLayoutParams? _satelliteLayoutParams;
    private static WindowManagerLayoutParams? _closeSatelliteLayoutParams;
    private static FrameLayout.LayoutParams? _inAppSatelliteLp;
    private static FrameLayout.LayoutParams? _inAppCloseSatelliteLp;
    private static int _satelliteSizePx;
    private static int _satelliteSpacingPx;

    private static readonly Handler _mainHandler = new(Looper.MainLooper!);
    
    /*
     Vòng tròn tiến trình Circular / Radial Progress Ring bao quanh Bubble:
     - Vẽ đường ray nền mờ (Track) 360 độ.
     - Vẽ vòng cung tiến trình (DrawArc) theo góc quét % (0 đến 360 độ).
     - Tự động đổi màu viền và nét vẽ theo từng loại tác vụ (Download, Convert/Enhancer, Packer, Splitter, Folders).
    */
    private class CircularProgressRingView : View
    {
        private readonly Paint _trackPaint = new() { AntiAlias = true };
        private readonly Paint _progressPaint = new() { AntiAlias = true };
        private readonly RectF _boundsRect = new();
        private float _strokeWidth;
        private float _progress = 0;
        private bool _isActive = false;

        public CircularProgressRingView(Context context) : base(context)
        {
            Init(context);
        }

        public CircularProgressRingView(IntPtr javaReference, global::Android.Runtime.JniHandleOwnership transfer) 
            : base(javaReference, transfer)
        {
        }

        private void Init(Context context)
        {
            _strokeWidth = DpToPx(context, 3.5f);

            _trackPaint.SetStyle(Paint.Style.Stroke);
            _trackPaint.StrokeWidth = _strokeWidth;
            _trackPaint.Color = new Color(0x35, 0xFF, 0xFF, 0xFF);

            _progressPaint.SetStyle(Paint.Style.Stroke);
            _progressPaint.StrokeWidth = _strokeWidth;
            _progressPaint.StrokeCap = Paint.Cap.Round;
            _progressPaint.Color = new Color(0x00, 0xE5, 0xFF);
        }

        public void UpdateProgress(double progressPct, string taskId, bool isRunning)
        {
            _isActive = isRunning;
            _progress = (float)Math.Clamp(progressPct, 0, 100);
            _progressPaint.Color = GetTaskColor(taskId);
            Invalidate();
        }

        protected override void OnDraw(Canvas? canvas)
        {
            if (canvas == null) return;
            base.OnDraw(canvas);

            float halfStroke = _strokeWidth / 2f + 2f;
            _boundsRect.Set(halfStroke, halfStroke, Width - halfStroke, Height - halfStroke);

            if (_isActive && _progress > 0)
            {
                canvas.DrawArc(_boundsRect, 0, 360, false, _trackPaint);
                float sweepAngle = (_progress / 100f) * 360f;
                canvas.DrawArc(_boundsRect, -90, sweepAngle, false, _progressPaint);
            }
        }
    }

    private class IdleDockRunnable : Java.Lang.Object, Java.Lang.IRunnable
    {
        public void Run()
        {
            try
            {
                PerformAutoDockAndFade();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("ComicGMTPC_Bubble", $"Lỗi PerformAutoDockAndFade: {ex.Message}");
            }
        }
    }
    private static readonly IdleDockRunnable _idleDockRunnableInstance = new();
    private static ValueAnimator? _activeSystemAnimator;

    private static bool _isDocked;
    private static bool _isRightSide = true;
    private static bool _isAtEdge = true;
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
                        WindowManagerFlags.LayoutNoLimits |
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
                onDragEnd: (isRightSide, isAtEdge) =>
                {
                    _isRightSide = isRightSide;
                    _isAtEdge = isAtEdge;
                    UpdateBadgePosition(_isRightSide);
                    StartIdleTimer();
                });

            _bubbleView.SetOnTouchListener(touchListener);

            _windowManager.AddView(_bubbleView, _systemLayoutParams);

            _quickAddSatelliteView = CreateQuickAddSatelliteView(context);
            _satelliteLayoutParams = new WindowManagerLayoutParams(
                _satelliteSizePx,
                _satelliteSizePx,
                windowType,
                flags,
                Format.Translucent)
            {
                Gravity = GravityFlags.Top | GravityFlags.Left
            };
            if (!InstanceConfigService.Instance.IsBubbleQuickAddButtonEnabled)
            {
                _quickAddSatelliteView.Visibility = ViewStates.Gone;
            }
            _windowManager.AddView(_quickAddSatelliteView, _satelliteLayoutParams);

            _closeSatelliteView = CreateCloseSatelliteView(context);
            _closeSatelliteLayoutParams = new WindowManagerLayoutParams(
                _satelliteSizePx,
                _satelliteSizePx,
                windowType,
                flags,
                Format.Translucent)
            {
                Gravity = GravityFlags.Top | GravityFlags.Left
            };
            if (!InstanceConfigService.Instance.IsBubbleCloseButtonEnabled)
            {
                _closeSatelliteView.Visibility = ViewStates.Gone;
            }
            _windowManager.AddView(_closeSatelliteView, _closeSatelliteLayoutParams);

            SyncSatellitePosition();

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
                onDragEnd: (isRightSide, isAtEdge) =>
                {
                    _isRightSide = isRightSide;
                    _isAtEdge = isAtEdge;
                    UpdateBadgePosition(_isRightSide);
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

            _quickAddSatelliteView = CreateQuickAddSatelliteView(activity);
            _inAppSatelliteLp = new FrameLayout.LayoutParams(_satelliteSizePx, _satelliteSizePx)
            {
                Gravity = GravityFlags.Top | GravityFlags.Left
            };
            if (!InstanceConfigService.Instance.IsBubbleQuickAddButtonEnabled)
            {
                _quickAddSatelliteView.Visibility = ViewStates.Gone;
            }
            rootContainer.AddView(_quickAddSatelliteView, _inAppSatelliteLp);
            _quickAddSatelliteView.BringToFront();

            _closeSatelliteView = CreateCloseSatelliteView(activity);
            _inAppCloseSatelliteLp = new FrameLayout.LayoutParams(_satelliteSizePx, _satelliteSizePx)
            {
                Gravity = GravityFlags.Top | GravityFlags.Left
            };
            if (!InstanceConfigService.Instance.IsBubbleCloseButtonEnabled)
            {
                _closeSatelliteView.Visibility = ViewStates.Gone;
            }
            rootContainer.AddView(_closeSatelliteView, _inAppCloseSatelliteLp);
            _closeSatelliteView.BringToFront();

            SyncSatellitePosition();

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

    private class BubbleContainerView : FrameLayout
    {
        public BubbleContainerView(Context context) : base(context)
        {
        }

        public BubbleContainerView(IntPtr javaReference, global::Android.Runtime.JniHandleOwnership transfer) 
            : base(javaReference, transfer)
        {
        }

        protected override void OnConfigurationChanged(global::Android.Content.Res.Configuration? newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            HandleOrientationOrSizeChanged(Context, newConfig);
        }

        protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
        {
            base.OnSizeChanged(w, h, oldw, oldh);
            if (oldw > 0 && oldh > 0 && (w != oldw || h != oldh))
            {
                HandleOrientationOrSizeChanged(Context, null);
            }
        }
    }

    private static View CreateBubbleView(Context context)
    {
        var container = new BubbleContainerView(context);

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
        // Circular Progress Ring bao quanh mép ngoài Bubble
        _progressRingView = new CircularProgressRingView(context);
        var ringLp = new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MatchParent, FrameLayout.LayoutParams.MatchParent);
        container.AddView(_progressRingView, ringLp);

        // Badge trạng thái / tiến trình ở góc dưới
        _badgeTextView = new TextView(context);
        _badgeTextView.SetTextColor(Color.White);
        _badgeTextView.SetTextSize(ComplexUnitType.Dip, 8.5f);
        _badgeTextView.Gravity = GravityFlags.Center;
        _badgeTextView.SetTypeface(null, TypefaceStyle.Bold);

        _badgeBg = new GradientDrawable();
        _badgeBg.SetShape(ShapeType.Oval);
        _badgeBg.SetColor((int)new Color(0x10, 0xB9, 0x81));
        _badgeTextView.Background = _badgeBg;

        int badgeSize = DpToPx(context, 16);
        int margin = DpToPx(context, 3);
        var initialGravity = _isRightSide ? (GravityFlags.Bottom | GravityFlags.Left) : (GravityFlags.Bottom | GravityFlags.Right);
        var badgeLp = new FrameLayout.LayoutParams(badgeSize, badgeSize)
        {
            Gravity = initialGravity,
            LeftMargin = _isRightSide ? margin : 0,
            RightMargin = _isRightSide ? 0 : margin,
            BottomMargin = margin
        };
        _badgeTextView.Visibility = ViewStates.Gone; // Chỉ hiện khi có tiến trình
        container.AddView(_badgeTextView, badgeLp);

        // Nút Toggle Bật/Tắt dấu (+) thêm link nhanh ở góc trên
        _toggleQuickAddView = new TextView(context);
        _toggleQuickAddView.SetTextSize(ComplexUnitType.Dip, 8.5f);
        _toggleQuickAddView.Gravity = GravityFlags.Center;
        _toggleQuickAddView.SetTypeface(null, TypefaceStyle.Bold);

        UpdateToggleQuickAddVisual();

        int toggleSize = DpToPx(context, 18);
        int toggleMargin = DpToPx(context, 3);
        var initialToggleGravity = _isRightSide ? (GravityFlags.Top | GravityFlags.Left) : (GravityFlags.Top | GravityFlags.Right);
        var toggleLp = new FrameLayout.LayoutParams(toggleSize, toggleSize)
        {
            Gravity = initialToggleGravity,
            LeftMargin = _isRightSide ? toggleMargin : 0,
            RightMargin = _isRightSide ? 0 : toggleMargin,
            TopMargin = toggleMargin
        };
        _toggleQuickAddView.SetOnTouchListener(new ToggleTouchListener(() =>
        {
            ToggleQuickAddState(context);
        }));
        container.AddView(_toggleQuickAddView, toggleLp);

        // Nút Toggle Bật/Tắt dấu (✕) tắt app ở góc trên đối diện
        _toggleCloseView = new TextView(context);
        _toggleCloseView.SetTextSize(ComplexUnitType.Dip, 8.5f);
        _toggleCloseView.Gravity = GravityFlags.Center;
        _toggleCloseView.SetTypeface(null, TypefaceStyle.Bold);

        UpdateToggleCloseVisual();

        var initialToggleCloseGravity = _isRightSide ? (GravityFlags.Top | GravityFlags.Right) : (GravityFlags.Top | GravityFlags.Left);
        var toggleCloseLp = new FrameLayout.LayoutParams(toggleSize, toggleSize)
        {
            Gravity = initialToggleCloseGravity,
            LeftMargin = _isRightSide ? 0 : toggleMargin,
            RightMargin = _isRightSide ? toggleMargin : 0,
            TopMargin = toggleMargin
        };
        _toggleCloseView.SetOnTouchListener(new ToggleTouchListener(() =>
        {
            ToggleCloseState(context);
        }));
        container.AddView(_toggleCloseView, toggleCloseLp);

        return container;
    }

    /*
     Điều chỉnh vị trí con số phần trăm (% badge) theo nửa màn hình Bubble đang ngự trị:
     - Khi Bubble ở nửa bên PHẢI: Badge nằm ở góc dưới bên TRÁI.
     - Khi Bubble ở nửa bên TRÁI: Badge nằm ở góc dưới bên PHẢI.
    */
    /// <summary>
    /// Xử lý cập nhật vị trí neo của Bong bóng khi xoay màn hình (Portrait sang Landscape)
    /// hoặc khi kích thước hiển thị thay đổi: tự động neo chặt vào mép trái/phải tương ứng,
    /// tuyệt đối không để Bubble bị lơ lửng ở chính giữa màn hình khi ở chế độ Landscape.
    /// </summary>
    public static void HandleOrientationOrSizeChanged(Context? context = null, global::Android.Content.Res.Configuration? newConfig = null)
    {
        _mainHandler.Post(() =>
        {
            try
            {
                if (_bubbleView == null || !_isShowing) return;

                var ctx = context ?? _bubbleView.Context ?? MainActivity.CurrentInstance?.ApplicationContext;
                if (ctx == null) return;

                int screenWidth = GetScreenWidth(ctx);
                int screenHeight = GetScreenHeight(ctx);

                int dockOffset = (int)(_bubbleSizePx * 0.60f);
                int targetX;

                if (_isRightSide)
                {
                    targetX = _isDocked ? (screenWidth - _bubbleSizePx + dockOffset) : (screenWidth - _bubbleSizePx);
                }
                else
                {
                    targetX = _isDocked ? (-dockOffset) : 0;
                }

                int minY = DpToPx(ctx, 40);
                int maxY = Math.Max(minY, screenHeight - _bubbleSizePx - DpToPx(ctx, 40));

                int currentY = _isSystemOverlay ? (_systemLayoutParams?.Y ?? 0) : (_inAppLayoutParams?.TopMargin ?? 0);
                int targetY = Math.Clamp(currentY, minY, maxY);

                if (_isSystemOverlay && _systemLayoutParams != null && _windowManager != null)
                {
                    _systemLayoutParams.X = targetX;
                    _systemLayoutParams.Y = targetY;
                    _windowManager.UpdateViewLayout(_bubbleView, _systemLayoutParams);
                }
                else if (!_isSystemOverlay && _inAppLayoutParams != null && _bubbleView != null)
                {
                    _inAppLayoutParams.LeftMargin = targetX;
                    _inAppLayoutParams.TopMargin = targetY;
                    _bubbleView.LayoutParameters = _inAppLayoutParams;
                    if (_isDocked)
                    {
                        float targetTransX = _isRightSide ? dockOffset : (-dockOffset);
                        _bubbleView.TranslationX = targetTransX;
                    }
                    else
                    {
                        _bubbleView.TranslationX = 0f;
                    }
                    _inAppContainer?.RequestLayout();
                    _bubbleView.RequestLayout();
                }

                _isAtEdge = true;
                UpdateBadgePosition(_isRightSide);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("ComicGMTPC_Bubble", $"Lỗi HandleOrientationOrSizeChanged: {ex.Message}");
            }
        });
    }

    private static void UpdateBadgePosition(bool isRightSide)
    {
        if (_badgeTextView == null) return;
        var context = _badgeTextView.Context;
        if (context == null) return;

        int badgeSize = DpToPx(context, 16);
        int margin = DpToPx(context, 3);

        var gravity = isRightSide
            ? (GravityFlags.Bottom | GravityFlags.Left)
            : (GravityFlags.Bottom | GravityFlags.Right);

        var lp = new FrameLayout.LayoutParams(badgeSize, badgeSize)
        {
            Gravity = gravity,
            LeftMargin = isRightSide ? margin : 0,
            RightMargin = isRightSide ? 0 : margin,
            BottomMargin = margin
        };

        _badgeTextView.LayoutParameters = lp;
        _badgeTextView.RequestLayout();

        UpdateTogglePosition(isRightSide);
        SyncSatellitePosition();
    }

    private static void UpdateTogglePosition(bool isRightSide)
    {
        var context = _toggleQuickAddView?.Context ?? _toggleCloseView?.Context;
        if (context == null) return;

        int toggleSize = DpToPx(context, 18);
        int toggleMargin = DpToPx(context, 3);

        if (_toggleQuickAddView != null)
        {
            var gravityQuickAdd = isRightSide
                ? (GravityFlags.Top | GravityFlags.Left)
                : (GravityFlags.Top | GravityFlags.Right);

            var lpQuickAdd = new FrameLayout.LayoutParams(toggleSize, toggleSize)
            {
                Gravity = gravityQuickAdd,
                LeftMargin = isRightSide ? toggleMargin : 0,
                RightMargin = isRightSide ? 0 : toggleMargin,
                TopMargin = toggleMargin
            };

            _toggleQuickAddView.LayoutParameters = lpQuickAdd;
            _toggleQuickAddView.RequestLayout();
        }

        if (_toggleCloseView != null)
        {
            var gravityClose = isRightSide
                ? (GravityFlags.Top | GravityFlags.Right)
                : (GravityFlags.Top | GravityFlags.Left);

            var lpClose = new FrameLayout.LayoutParams(toggleSize, toggleSize)
            {
                Gravity = gravityClose,
                LeftMargin = isRightSide ? 0 : toggleMargin,
                RightMargin = isRightSide ? toggleMargin : 0,
                TopMargin = toggleMargin
            };

            _toggleCloseView.LayoutParameters = lpClose;
            _toggleCloseView.RequestLayout();
        }
    }

    private static void UpdateToggleQuickAddVisual()
    {
        if (_toggleQuickAddView == null) return;
        bool isEnabled = InstanceConfigService.Instance.IsBubbleQuickAddButtonEnabled;

        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Oval);
        if (isEnabled)
        {
            bg.SetColor((int)new Color(0x00, 0xE5, 0xFF)); // Cyber Cyan
            _toggleQuickAddView.Text = "⚡";
            _toggleQuickAddView.SetTextColor(new Color(0x0F, 0x17, 0x2A));
        }
        else
        {
            bg.SetColor((int)new Color(0x33, 0x41, 0x55)); // Slate
            _toggleQuickAddView.Text = "○";
            _toggleQuickAddView.SetTextColor(new Color(0x94, 0xA3, 0xB8));
        }
        _toggleQuickAddView.Background = bg;
    }

    public static void ToggleQuickAddState(Context context)
    {
        bool newState = !InstanceConfigService.Instance.IsBubbleQuickAddButtonEnabled;
        InstanceConfigService.Instance.IsBubbleQuickAddButtonEnabled = newState;
        UpdateToggleQuickAddVisual();
        SyncSatellitePosition();
        string msg = newState ? "⚡ Đã bật nút (+) thêm link cạnh bong bóng" : "⚪ Đã ẩn nút (+) thêm link";
        try { Toast.MakeText(context, msg, ToastLength.Short)?.Show(); } catch { }
    }

    private static void UpdateToggleCloseVisual()
    {
        if (_toggleCloseView == null) return;
        bool isEnabled = InstanceConfigService.Instance.IsBubbleCloseButtonEnabled;

        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Oval);
        if (isEnabled)
        {
            bg.SetColor((int)new Color(0xEF, 0x44, 0x44)); // Red 500
            _toggleCloseView.Text = "✕";
            _toggleCloseView.SetTextColor(Color.White);
        }
        else
        {
            bg.SetColor((int)new Color(0x33, 0x41, 0x55)); // Slate
            _toggleCloseView.Text = "○";
            _toggleCloseView.SetTextColor(new Color(0x94, 0xA3, 0xB8));
        }
        _toggleCloseView.Background = bg;
    }

    public static void ToggleCloseState(Context context)
    {
        bool newState = !InstanceConfigService.Instance.IsBubbleCloseButtonEnabled;
        InstanceConfigService.Instance.IsBubbleCloseButtonEnabled = newState;
        UpdateToggleCloseVisual();
        SyncSatellitePosition();
        string msg = newState ? "🛑 Đã bật nút (✕) tắt app cạnh bong bóng" : "⚪ Đã ẩn nút (✕) tắt app";
        try { Toast.MakeText(context, msg, ToastLength.Short)?.Show(); } catch { }
    }

    public static void SyncSatellitePosition()
    {
        bool hasQuickAdd = InstanceConfigService.Instance.IsBubbleQuickAddButtonEnabled;
        bool hasClose = InstanceConfigService.Instance.IsBubbleCloseButtonEnabled;

        if (_quickAddSatelliteView != null)
        {
            _quickAddSatelliteView.Visibility = hasQuickAdd ? ViewStates.Visible : ViewStates.Gone;
        }

        if (_closeSatelliteView != null)
        {
            _closeSatelliteView.Visibility = hasClose ? ViewStates.Visible : ViewStates.Gone;
        }

        if (!hasQuickAdd && !hasClose)
        {
            return;
        }

        int satVerticalGap = (_bubbleView?.Context != null) ? DpToPx(_bubbleView.Context, 6) : 10;

        if (_isSystemOverlay && _systemLayoutParams != null && _windowManager != null)
        {
            int satX = _isRightSide
                ? (_systemLayoutParams.X - _satelliteSizePx - _satelliteSpacingPx)
                : (_systemLayoutParams.X + _bubbleSizePx + _satelliteSpacingPx);

            if (hasQuickAdd && hasClose)
            {
                int clusterStartY = _systemLayoutParams.Y + (_bubbleSizePx - (2 * _satelliteSizePx + satVerticalGap)) / 2;
                int satPlusY = clusterStartY;
                int satCloseY = clusterStartY + _satelliteSizePx + satVerticalGap;

                if (_quickAddSatelliteView != null && _satelliteLayoutParams != null)
                {
                    _satelliteLayoutParams.X = satX;
                    _satelliteLayoutParams.Y = satPlusY;
                    try { _windowManager.UpdateViewLayout(_quickAddSatelliteView, _satelliteLayoutParams); } catch { }
                }

                if (_closeSatelliteView != null && _closeSatelliteLayoutParams != null)
                {
                    _closeSatelliteLayoutParams.X = satX;
                    _closeSatelliteLayoutParams.Y = satCloseY;
                    try { _windowManager.UpdateViewLayout(_closeSatelliteView, _closeSatelliteLayoutParams); } catch { }
                }
            }
            else if (hasQuickAdd)
            {
                int satY = _systemLayoutParams.Y + (_bubbleSizePx - _satelliteSizePx) / 2;
                if (_quickAddSatelliteView != null && _satelliteLayoutParams != null)
                {
                    _satelliteLayoutParams.X = satX;
                    _satelliteLayoutParams.Y = satY;
                    try { _windowManager.UpdateViewLayout(_quickAddSatelliteView, _satelliteLayoutParams); } catch { }
                }
            }
            else if (hasClose)
            {
                int satY = _systemLayoutParams.Y + (_bubbleSizePx - _satelliteSizePx) / 2;
                if (_closeSatelliteView != null && _closeSatelliteLayoutParams != null)
                {
                    _closeSatelliteLayoutParams.X = satX;
                    _closeSatelliteLayoutParams.Y = satY;
                    try { _windowManager.UpdateViewLayout(_closeSatelliteView, _closeSatelliteLayoutParams); } catch { }
                }
            }
        }
        else if (!_isSystemOverlay && _inAppLayoutParams != null)
        {
            int satLeft = _isRightSide
                ? (_inAppLayoutParams.LeftMargin - _satelliteSizePx - _satelliteSpacingPx)
                : (_inAppLayoutParams.LeftMargin + _bubbleSizePx + _satelliteSpacingPx);

            if (hasQuickAdd && hasClose)
            {
                int clusterStartTop = _inAppLayoutParams.TopMargin + (_bubbleSizePx - (2 * _satelliteSizePx + satVerticalGap)) / 2;
                int satPlusTop = clusterStartTop;
                int satCloseTop = clusterStartTop + _satelliteSizePx + satVerticalGap;

                if (_quickAddSatelliteView != null && _inAppSatelliteLp != null)
                {
                    _inAppSatelliteLp.LeftMargin = satLeft;
                    _inAppSatelliteLp.TopMargin = satPlusTop;
                    _quickAddSatelliteView.LayoutParameters = _inAppSatelliteLp;
                    _quickAddSatelliteView.RequestLayout();
                }

                if (_closeSatelliteView != null && _inAppCloseSatelliteLp != null)
                {
                    _inAppCloseSatelliteLp.LeftMargin = satLeft;
                    _inAppCloseSatelliteLp.TopMargin = satCloseTop;
                    _closeSatelliteView.LayoutParameters = _inAppCloseSatelliteLp;
                    _closeSatelliteView.RequestLayout();
                }
            }
            else if (hasQuickAdd)
            {
                int satTop = _inAppLayoutParams.TopMargin + (_bubbleSizePx - _satelliteSizePx) / 2;
                if (_quickAddSatelliteView != null && _inAppSatelliteLp != null)
                {
                    _inAppSatelliteLp.LeftMargin = satLeft;
                    _inAppSatelliteLp.TopMargin = satTop;
                    _quickAddSatelliteView.LayoutParameters = _inAppSatelliteLp;
                    _quickAddSatelliteView.RequestLayout();
                }
            }
            else if (hasClose)
            {
                int satTop = _inAppLayoutParams.TopMargin + (_bubbleSizePx - _satelliteSizePx) / 2;
                if (_closeSatelliteView != null && _inAppCloseSatelliteLp != null)
                {
                    _inAppCloseSatelliteLp.LeftMargin = satLeft;
                    _inAppCloseSatelliteLp.TopMargin = satTop;
                    _closeSatelliteView.LayoutParameters = _inAppCloseSatelliteLp;
                    _closeSatelliteView.RequestLayout();
                }
            }
        }
    }

    private static bool TryDirectClipboardQuickAdd(Context context)
    {
        try
        {
            var clipboard = (ClipboardManager?)context.GetSystemService(Context.ClipboardService);
            if (clipboard != null && clipboard.HasPrimaryClip && clipboard.PrimaryClip != null && clipboard.PrimaryClip.ItemCount > 0)
            {
                var item = clipboard.PrimaryClip.GetItemAt(0);
                string? text = item?.Text ?? item?.Uri?.ToString() ?? item?.CoerceToText(context)?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(text, @"https?://[^\s<>""'()\[\]{}]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        string foundUrl = match.Value.Trim().TrimEnd('.', ',', ';', '!', '?');
                        App.EnqueueSharedLinkText(foundUrl);
                        ShareLinkShortcutHelper.ReportDirectShareUsed(context.ApplicationContext ?? context);

                        if (App.IsMainViewReady && App.TryGetSharedMainViewModel() is { } vm)
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                _ = vm.DrainPendingSharedLinksAsync();
                            });
                        }

                        Toast.MakeText(context, "📥 Đã nạp link vào hàng chờ tải!", ToastLength.Short)?.Show();
                        return true;
                    }
                }
            }
        }
        catch { }
        return false;
    }

    private static View CreateQuickAddSatelliteView(Context context)
    {
        _satelliteSizePx = DpToPx(context, 36);
        _satelliteSpacingPx = DpToPx(context, 8);

        var satButton = new FrameLayout(context);
        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Oval);
        bg.SetColors(new int[] {
            (int)new Color(0x00, 0xE5, 0xFF), // Cyber Cyan
            (int)new Color(0x02, 0x84, 0xC7)  // Sky 600
        });
        bg.SetStroke(DpToPx(context, 1.5f), new Color(0x38, 0xBD, 0xF8));
        satButton.Background = bg;

        if (OperatingSystem.IsAndroidVersionAtLeast(21))
        {
            satButton.Elevation = DpToPx(context, 12);
        }

        var plusText = new TextView(context);
        plusText.Text = "+";
        plusText.SetTextSize(ComplexUnitType.Dip, 20f);
        plusText.SetTextColor(Color.White);
        plusText.SetTypeface(null, TypefaceStyle.Bold);
        plusText.Gravity = GravityFlags.Center;

        var textLp = new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MatchParent, FrameLayout.LayoutParams.MatchParent)
        {
            Gravity = GravityFlags.Center
        };
        satButton.AddView(plusText, textLp);

        satButton.SetOnClickListener(new QuickAddClickListener(() =>
        {
            try
            {
                satButton.Animate()?.ScaleX(1.2f)?.ScaleY(1.2f)?.SetDuration(100)?.WithEndAction(new Java.Lang.Runnable(() =>
                {
                    satButton.Animate()?.ScaleX(1.0f)?.ScaleY(1.0f)?.SetDuration(100)?.Start();
                }))?.Start();
            }
            catch { }

            // Bước 1: Thử Fast Path đọc clipboard trực tiếp nếu đang có quyền focus (ví dụ In-App hoặc Android <= 9)
            if (TryDirectClipboardQuickAdd(context))
            {
                return;
            }

            // Bước 2: Kích hoạt ShareLinkActivity để nhận Window Focus đọc clipboard trên Android 10+
            ShareLinkActivity.LaunchQuickAddFromClipboard(context);
        }));

        return satButton;
    }

    private static View CreateCloseSatelliteView(Context context)
    {
        _satelliteSizePx = DpToPx(context, 36);
        _satelliteSpacingPx = DpToPx(context, 8);

        var satButton = new FrameLayout(context);
        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Oval);
        bg.SetColors(new int[] {
            (int)new Color(0xEF, 0x44, 0x44), // Red 500
            (int)new Color(0xB9, 0x1C, 0x1C)  // Red 700
        });
        bg.SetStroke(DpToPx(context, 1.5f), new Color(0xF8, 0x71, 0x71));
        satButton.Background = bg;

        if (OperatingSystem.IsAndroidVersionAtLeast(21))
        {
            satButton.Elevation = DpToPx(context, 12);
        }

        var closeText = new TextView(context);
        closeText.Text = "✕";
        closeText.SetTextSize(ComplexUnitType.Dip, 16f);
        closeText.SetTextColor(Color.White);
        closeText.SetTypeface(null, TypefaceStyle.Bold);
        closeText.Gravity = GravityFlags.Center;

        var textLp = new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MatchParent, FrameLayout.LayoutParams.MatchParent)
        {
            Gravity = GravityFlags.Center
        };
        satButton.AddView(closeText, textLp);

        satButton.SetOnClickListener(new QuickAddClickListener(() =>
        {
            try
            {
                satButton.Animate()?.ScaleX(1.2f)?.ScaleY(1.2f)?.SetDuration(100)?.WithEndAction(new Java.Lang.Runnable(() =>
                {
                    satButton.Animate()?.ScaleX(1.0f)?.ScaleY(1.0f)?.SetDuration(100)?.Start();
                }))?.Start();
            }
            catch { }

            ForceStopApp(context);
        }));

        return satButton;
    }

    /// <summary>
    /// Buộc dừng và đóng hoàn toàn ứng dụng (Force Stop), dọn dẹp Bubble, Foreground Service và tắt tiến trình.
    /// </summary>
    public static void ForceStopApp(Context context)
    {
        try
        {
            Toast.MakeText(context, "🛑 Đang tắt hoàn toàn ứng dụng...", ToastLength.Short)?.Show();
        }
        catch { }

        _mainHandler.PostDelayed(() =>
        {
            try
            {
                HideBubble();
            }
            catch { }

            try
            {
                ComicBackgroundService.Stop(context);
            }
            catch { }

            try
            {
                var act = context as Activity ?? MainActivity.CurrentInstance;
                act?.FinishAffinity();
            }
            catch { }

            try
            {
                global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
                Java.Lang.JavaSystem.Exit(0);
            }
            catch { }
        }, 150);
    }

    private static void StartIdleTimer(long delayMs = 3500)
    {
        CancelIdleTimer();
        _mainHandler.PostDelayed(_idleDockRunnableInstance, delayMs);
    }

    private static void CancelIdleTimer()
    {
        _mainHandler.RemoveCallbacks(_idleDockRunnableInstance);
    }

    private static void PerformAutoDockAndFade()
    {
        if (_bubbleView == null || _isDocked) return;
        _isDocked = true;

        if (_quickAddSatelliteView != null && InstanceConfigService.Instance.IsBubbleQuickAddButtonEnabled)
        {
            _quickAddSatelliteView.Animate()
                ?.Alpha(0.45f)
                ?.SetDuration(300)
                ?.Start();
        }
        if (_closeSatelliteView != null && InstanceConfigService.Instance.IsBubbleCloseButtonEnabled)
        {
            _closeSatelliteView.Animate()
                ?.Alpha(0.45f)
                ?.SetDuration(300)
                ?.Start();
        }

        var context = _bubbleView.Context;
        if (context == null) return;
        int screenWidth = GetScreenWidth(context);

        // Nếu Bubble ở sát mép viền màn hình: thụt 60% vào mép, chừa 40% làm tay cầm
        if (_isAtEdge)
        {
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
            else if (!_isSystemOverlay && _bubbleView != null)
            {
                float targetTransX = _isRightSide ? dockOffset : (-dockOffset);
                _bubbleView.Animate()
                    ?.TranslationX(targetTransX)
                    ?.Alpha(0.45f)
                    ?.SetDuration(280)
                    ?.SetInterpolator(new DecelerateInterpolator())
                    ?.Start();
                if (_quickAddSatelliteView != null && InstanceConfigService.Instance.IsBubbleQuickAddButtonEnabled)
                {
                    _quickAddSatelliteView.Animate()
                        ?.TranslationX(targetTransX)
                        ?.Alpha(0.45f)
                        ?.SetDuration(280)
                        ?.SetInterpolator(new DecelerateInterpolator())
                        ?.Start();
                }
                if (_closeSatelliteView != null && InstanceConfigService.Instance.IsBubbleCloseButtonEnabled)
                {
                    _closeSatelliteView.Animate()
                        ?.TranslationX(targetTransX)
                        ?.Alpha(0.45f)
                        ?.SetDuration(280)
                        ?.SetInterpolator(new DecelerateInterpolator())
                        ?.Start();
                }
            }
        }
        else
        {
            // Bubble nằm tự do ở bên trong màn hình: giữ nguyên vị trí X, Y, chỉ fade mờ nhẹ để không che tầm nhìn
            _bubbleView.Animate()
                ?.Alpha(0.45f)
                ?.SetDuration(300)
                ?.SetInterpolator(new DecelerateInterpolator())
                ?.Start();
        }
    }

    public static void WakeUpAndRestore()
    {
        if (_bubbleView == null) return;
        SyncSatellitePosition();

        // Khôi phục độ mờ 100%
        _bubbleView.Animate()
            ?.Alpha(1.0f)
            ?.SetDuration(150)
            ?.Start();

        if (_quickAddSatelliteView != null && InstanceConfigService.Instance.IsBubbleQuickAddButtonEnabled)
        {
            _quickAddSatelliteView.Animate()
                ?.Alpha(1.0f)
                ?.SetDuration(150)
                ?.Start();
        }
        if (_closeSatelliteView != null && InstanceConfigService.Instance.IsBubbleCloseButtonEnabled)
        {
            _closeSatelliteView.Animate()
                ?.Alpha(1.0f)
                ?.SetDuration(150)
                ?.Start();
        }

        // Nếu đang thụt vào mép, trượt bung ra toàn bộ
        if (_isDocked)
        {
            _isDocked = false;
            var context = _bubbleView.Context;
            if (context == null) return;
            int screenWidth = GetScreenWidth(context);

            if (_isAtEdge)
            {
                if (_isSystemOverlay && _systemLayoutParams != null && _windowManager != null)
                {
                    int targetX = _isRightSide ? (screenWidth - _bubbleSizePx) : 0;
                    AnimateSystemX(_systemLayoutParams.X, targetX, 200, () =>
                    {
                        StartIdleTimer();
                    });
                }
                else if (!_isSystemOverlay && _bubbleView != null)
                {
                    _bubbleView.Animate()
                        ?.TranslationX(0f)
                        ?.SetDuration(200)
                        ?.SetInterpolator(new DecelerateInterpolator())
                        ?.WithEndAction(new Java.Lang.Runnable(() =>
                        {
                            StartIdleTimer();
                        }))
                        ?.Start();
                    if (_quickAddSatelliteView != null && InstanceConfigService.Instance.IsBubbleQuickAddButtonEnabled)
                    {
                        _quickAddSatelliteView.Animate()
                            ?.TranslationX(0f)
                            ?.SetDuration(200)
                            ?.SetInterpolator(new DecelerateInterpolator())
                            ?.Start();
                    }
                    if (_closeSatelliteView != null && InstanceConfigService.Instance.IsBubbleCloseButtonEnabled)
                    {
                        _closeSatelliteView.Animate()
                            ?.TranslationX(0f)
                            ?.SetDuration(200)
                            ?.SetInterpolator(new DecelerateInterpolator())
                            ?.Start();
                    }
                }
            }
            else
            {
                StartIdleTimer();
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

        _activeSystemAnimator?.Cancel();

        var anim = ValueAnimator.OfInt(startX, endX);
        if (anim != null)
        {
            anim.SetDuration(duration);
            anim.SetInterpolator(new DecelerateInterpolator());
            anim.Update += (s, e) =>
            {
                try
                {
                    if (_bubbleView != null && _systemLayoutParams != null && _windowManager != null)
                    {
                        _systemLayoutParams.X = (int)e.Animation.AnimatedValue!;
                        _windowManager.UpdateViewLayout(_bubbleView, _systemLayoutParams);
                        SyncSatellitePosition();
                    }
                }
                catch { }
            };
            anim.AnimationEnd += (s, e) =>
            {
                _activeSystemAnimator = null;
                onEnd?.Invoke();
            };
            _activeSystemAnimator = anim;
            anim.Start();
        }
        else
        {
            _systemLayoutParams.X = endX;
            try { _windowManager.UpdateViewLayout(_bubbleView, _systemLayoutParams); } catch { }
            onEnd?.Invoke();
        }
    }



    private static void OnTaskProgressChanged(BackgroundTaskInfo task)
    {
        _mainHandler.Post(() =>
        {
            try
            {
                if (_bubbleView == null) return;

                if (task.IsRunning)
                {
                    if (_badgeTextView != null)
                    {
                        _badgeTextView.Visibility = ViewStates.Visible;
                        int pct = (int)Math.Round(Math.Clamp(task.ProgressPercentage, 0, 100));
                        _badgeTextView.Text = task.ProgressPercentage >= 0 ? $"{pct}" : "●";
                        int badgeColor = (int)GetTaskColor(task.Id);
                        _badgeBg?.SetColor(badgeColor);
                    }

                    _progressRingView?.UpdateProgress(task.ProgressPercentage, task.Id, true);

                    if (_isDocked)
                    {
                        _bubbleView.Animate()?.Alpha(0.90f)?.SetDuration(200)?.Start();
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
                        if (_badgeTextView != null)
                        {
                            _badgeTextView.Visibility = ViewStates.Visible;
                            int pct = (int)Math.Round(Math.Clamp(primary.ProgressPercentage, 0, 100));
                            _badgeTextView.Text = primary.ProgressPercentage >= 0 ? $"{pct}" : "●";
                            _badgeBg?.SetColor((int)GetTaskColor(primary.Id));
                        }
                        _progressRingView?.UpdateProgress(primary.ProgressPercentage, primary.Id, true);
                    }
                    else
                    {
                        if (_badgeTextView != null)
                        {
                            _badgeTextView.Visibility = ViewStates.Gone;
                        }
                        _progressRingView?.UpdateProgress(0, string.Empty, false);
                    }
                }
            }
            catch { }
        });
    }

    private static Color GetTaskColor(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return new Color(0x00, 0xE5, 0xFF);

        if (taskId.Contains("download", StringComparison.OrdinalIgnoreCase))
            return new Color(0x00, 0xE5, 0xFF); // Cyber Cyan
        if (taskId.Contains("image_enhancer", StringComparison.OrdinalIgnoreCase) || taskId.Contains("convert", StringComparison.OrdinalIgnoreCase))
            return new Color(0xC0, 0x84, 0xFC); // Purple Neon
        if (taskId.Contains("file_packer", StringComparison.OrdinalIgnoreCase))
            return new Color(0xF5, 0x9E, 0x0B); // Amber Cam
        if (taskId.Contains("image_splitter", StringComparison.OrdinalIgnoreCase))
            return new Color(0xF4, 0x3F, 0x5E); // Rose Hồng
        if (taskId.Contains("folder_tools", StringComparison.OrdinalIgnoreCase))
            return new Color(0x10, 0xB9, 0x81); // Emerald Xanh
        if (taskId.Contains("archive_merger", StringComparison.OrdinalIgnoreCase))
            return new Color(0x38, 0xBD, 0xF8); // Sky Blue

        return new Color(0x00, 0xE5, 0xFF);
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
                if (_quickAddSatelliteView != null)
                {
                    if (_isSystemOverlay && _windowManager != null)
                    {
                        try { _windowManager.RemoveView(_quickAddSatelliteView); } catch { }
                    }
                    else if (!_isSystemOverlay && _inAppContainer != null)
                    {
                        try { _inAppContainer.RemoveView(_quickAddSatelliteView); } catch { }
                    }
                    _quickAddSatelliteView = null;
                    _satelliteLayoutParams = null;
                    _inAppSatelliteLp = null;
                }
                if (_closeSatelliteView != null)
                {
                    if (_isSystemOverlay && _windowManager != null)
                    {
                        try { _windowManager.RemoveView(_closeSatelliteView); } catch { }
                    }
                    else if (!_isSystemOverlay && _inAppContainer != null)
                    {
                        try { _inAppContainer.RemoveView(_closeSatelliteView); } catch { }
                    }
                    _closeSatelliteView = null;
                    _closeSatelliteLayoutParams = null;
                    _inAppCloseSatelliteLp = null;
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
                _progressRingView = null;
                _badgeBg = null;
                _iconImageView = null;
                _toggleQuickAddView = null;
                _toggleCloseView = null;
                _isDocked = false;
                _isShowing = false;
            }
        });
    }

    /// <summary>
    /// Hiển thị menu tùy chọn khi ấn giữ Bong bóng trong 3 giây:
    /// 1. Tắt bong bóng
    /// 2. Tắt app hoàn toàn
    /// </summary>
    public static void ShowBubbleOptionsDialog(Context context)
    {
        _mainHandler.Post(() =>
        {
            try
            {
                var activity = context as Activity ?? MainActivity.CurrentInstance;
                bool isActivityValid = activity != null && !activity.IsFinishing && !activity.IsDestroyed;

                Context dialogCtx = isActivityValid
                    ? (Context)activity!
                    : new ContextThemeWrapper(context.ApplicationContext ?? context, global::Android.Resource.Style.ThemeDeviceDefaultDialogAlert);

                var builder = new AlertDialog.Builder(dialogCtx);
                builder.SetTitle("🫧 Tùy chọn Bong bóng GMTPC");

                bool isQuickAddEnabled = InstanceConfigService.Instance.IsBubbleQuickAddButtonEnabled;
                bool isCloseEnabled = InstanceConfigService.Instance.IsBubbleCloseButtonEnabled;
                string[] options = new string[]
                {
                    isQuickAddEnabled ? "⚪ Ẩn nút (+) thêm link nhanh" : "⚡ Hiện nút (+) thêm link nhanh",
                    isCloseEnabled ? "⚪ Ẩn nút (✕) tắt app" : "🛑 Hiện nút (✕) tắt app",
                    "🚫 Tắt bong bóng",
                    "🛑 Tắt app hoàn toàn"
                };

                builder.SetItems(options, (sender, args) =>
                {
                    int selected = args.Which;
                    if (selected == 0)
                    {
                        ToggleQuickAddState(dialogCtx);
                    }
                    else if (selected == 1)
                    {
                        ToggleCloseState(dialogCtx);
                    }
                    else if (selected == 2)
                    {
                        HideBubble();
                        try
                        {
                            Toast.MakeText(dialogCtx, "Đã tắt bong bóng", ToastLength.Short)?.Show();
                        }
                        catch { }
                    }
                    else if (selected == 3)
                    {
                        ExitAppCompletely(dialogCtx);
                    }
                });

                builder.SetNegativeButton("Hủy", (sender, args) => { });

                var dialog = builder.Create();
                if (dialog?.Window != null)
                {
                    if (!isActivityValid || _isSystemOverlay)
                    {
                        if (CanDrawOverlays(context))
                        {
                            var windowType = OperatingSystem.IsAndroidVersionAtLeast(26)
                                ? WindowManagerTypes.ApplicationOverlay
                                : WindowManagerTypes.Phone;
                            dialog.Window.SetType(windowType);
                        }
                    }
                    dialog.Show();
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("ComicGMTPC_Bubble", $"Lỗi hiển thị dialog tùy chọn bubble: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Đóng hoàn toàn ứng dụng, dừng Foreground Service và giải phóng tiến trình.
    /// </summary>
    public static void ExitAppCompletely(Context context)
    {
        try
        {
            HideBubble();
        }
        catch { }

        try
        {
            BackgroundExecutionService.NativeForceExit?.Invoke();
            return;
        }
        catch { }

        try
        {
            var act = context as Activity ?? MainActivity.CurrentInstance;
            if (act != null)
            {
                ComicBackgroundService.Stop(act);
                act.FinishAffinity();
            }
            else
            {
                ComicBackgroundService.Stop(context);
            }
        }
        catch { }

        try
        {
            global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
            Java.Lang.JavaSystem.Exit(0);
        }
        catch { }
    }

    private class SystemBubbleTouchListener : Java.Lang.Object, View.IOnTouchListener
    {
        private readonly Context _context;
        private readonly IWindowManager _wm;
        private readonly WindowManagerLayoutParams _lp;
        private readonly int _bubbleSize;
        private readonly Action _onTouchStart;
        private readonly Action _onTap;
        private readonly Action<bool, bool> _onDragEnd;

        private float _startX, _startY;
        private float _touchX, _touchY;
        private bool _isDragging;
        private bool _isLongPressed;
        private Java.Lang.Runnable? _longPressRunnable;
        private readonly int _touchSlop;

        public SystemBubbleTouchListener(
            Context context,
            IWindowManager wm,
            WindowManagerLayoutParams lp,
            int bubbleSize,
            Action onTouchStart,
            Action onTap,
            Action<bool, bool> onDragEnd)
        {
            _context = context;
            _wm = wm;
            _lp = lp;
            _bubbleSize = bubbleSize;
            _onTouchStart = onTouchStart;
            _onTap = onTap;
            _onDragEnd = onDragEnd;
            _touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 16;
            _isLongPressed = false;
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
                    _isLongPressed = false;
                    _onTouchStart?.Invoke();

                    if (_longPressRunnable != null)
                    {
                        _mainHandler.RemoveCallbacks(_longPressRunnable);
                    }
                    _longPressRunnable = new Java.Lang.Runnable(() =>
                    {
                        if (!_isDragging)
                        {
                            _isLongPressed = true;
                            try
                            {
                                v?.PerformHapticFeedback(FeedbackConstants.LongPress);
                            }
                            catch { }
                            ShowBubbleOptionsDialog(_context);
                        }
                    });
                    _mainHandler.PostDelayed(_longPressRunnable, 3000);
                    return true;

                case MotionEventActions.Move:
                    float dx = e.RawX - _touchX;
                    float dy = e.RawY - _touchY;
                    if (!_isDragging && (Math.Abs(dx) > _touchSlop || Math.Abs(dy) > _touchSlop))
                    {
                        _isDragging = true;
                        if (_longPressRunnable != null)
                        {
                            _mainHandler.RemoveCallbacks(_longPressRunnable);
                            _longPressRunnable = null;
                        }
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
                        SyncSatellitePosition();
                    }
                    return true;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    if (_longPressRunnable != null)
                    {
                        _mainHandler.RemoveCallbacks(_longPressRunnable);
                        _longPressRunnable = null;
                    }

                    if (_isLongPressed)
                    {
                        _isLongPressed = false;
                        return true;
                    }

                    if (!_isDragging)
                    {
                        _onTap?.Invoke();
                    }
                    else
                    {
                        int screenWidth = GetScreenWidth(_context);
                        int screenHeight = GetScreenHeight(_context);
                        bool isLandscape = screenWidth > screenHeight;

                        int minY = DpToPx(_context, 40);
                        int maxY = screenHeight - _bubbleSize - DpToPx(_context, 40);
                        int targetY = Math.Clamp(_lp.Y, minY, Math.Max(minY, maxY));

                        bool isRightSide = (_lp.X + _bubbleSize / 2) > (screenWidth / 2);
                        int targetX;
                        bool isAtEdge;

                        if (isLandscape)
                        {
                            // Khi Landscape: Luôn luôn neo chặt vào mép trái hoặc mép phải gần nhất
                            targetX = isRightSide ? (screenWidth - _bubbleSize) : 0;
                            isAtEdge = true;
                        }
                        else
                        {
                            int snapEdgeThreshold = DpToPx(_context, 20);
                            if (_lp.X <= snapEdgeThreshold || _lp.X >= (screenWidth - _bubbleSize - snapEdgeThreshold))
                            {
                                targetX = isRightSide ? (screenWidth - _bubbleSize) : 0;
                                isAtEdge = true;
                            }
                            else
                            {
                                targetX = Math.Clamp(_lp.X, 0, screenWidth - _bubbleSize);
                                isAtEdge = false;
                            }
                        }

                        AnimateSnap(_lp.X, targetX, _lp.Y, targetY, () =>
                        {
                            _onDragEnd?.Invoke(isRightSide, isAtEdge);
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
                    SyncSatellitePosition();
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
        private readonly Action<bool, bool> _onDragEnd;

        private float _startLeft, _startTop;
        private float _touchX, _touchY;
        private bool _isDragging;
        private bool _isLongPressed;
        private Java.Lang.Runnable? _longPressRunnable;
        private readonly int _touchSlop;

        public InAppBubbleTouchListener(
            Activity activity,
            FrameLayout.LayoutParams lp,
            int bubbleSize,
            Action onTouchStart,
            Action onTap,
            Action<bool, bool> onDragEnd)
        {
            _activity = activity;
            _lp = lp;
            _bubbleSize = bubbleSize;
            _onTouchStart = onTouchStart;
            _onTap = onTap;
            _onDragEnd = onDragEnd;
            _touchSlop = ViewConfiguration.Get(activity)?.ScaledTouchSlop ?? 16;
            _isLongPressed = false;
        }

        public bool OnTouch(View? v, MotionEvent? e)
        {
            if (e == null || v == null) return false;

            switch (e.Action)
            {
                case MotionEventActions.Down:
                    CancelIdleTimer();
                    if (_isDocked)
                    {
                        WakeUpAndRestore();
                    }
                    else
                    {
                        _bubbleView?.Animate()?.Alpha(1.0f)?.SetDuration(100)?.Start();
                    }
                    _startLeft = _lp.LeftMargin;
                    _startTop = _lp.TopMargin;
                    _touchX = e.RawX;
                    _touchY = e.RawY;
                    _isDragging = false;
                    _isLongPressed = false;
                    _onTouchStart?.Invoke();

                    if (_longPressRunnable != null)
                    {
                        _mainHandler.RemoveCallbacks(_longPressRunnable);
                    }
                    _longPressRunnable = new Java.Lang.Runnable(() =>
                    {
                        if (!_isDragging)
                        {
                            _isLongPressed = true;
                            try
                            {
                                v?.PerformHapticFeedback(FeedbackConstants.LongPress);
                            }
                            catch { }
                            ShowBubbleOptionsDialog(_activity);
                        }
                    });
                    _mainHandler.PostDelayed(_longPressRunnable, 3000);
                    return true;

                case MotionEventActions.Move:
                    float dx = e.RawX - _touchX;
                    float dy = e.RawY - _touchY;
                    if (!_isDragging && (Math.Abs(dx) > _touchSlop || Math.Abs(dy) > _touchSlop))
                    {
                        _isDragging = true;
                        if (_longPressRunnable != null)
                        {
                            _mainHandler.RemoveCallbacks(_longPressRunnable);
                            _longPressRunnable = null;
                        }
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
                        SyncSatellitePosition();
                    }
                    return true;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    if (_longPressRunnable != null)
                    {
                        _mainHandler.RemoveCallbacks(_longPressRunnable);
                        _longPressRunnable = null;
                    }

                    if (_isLongPressed)
                    {
                        _isLongPressed = false;
                        return true;
                    }

                    if (!_isDragging)
                    {
                        _onTap?.Invoke();
                    }
                    else
                    {
                        int screenWidth = GetScreenWidth(_activity);
                        int screenHeight = GetScreenHeight(_activity);
                        bool isLandscape = screenWidth > screenHeight;

                        int minY = DpToPx(_activity, 40);
                        int maxY = screenHeight - _bubbleSize - DpToPx(_activity, 40);
                        int targetTop = Math.Clamp(_lp.TopMargin, minY, Math.Max(minY, maxY));

                        bool isRightSide = (_lp.LeftMargin + _bubbleSize / 2) > (screenWidth / 2);
                        int targetLeft;
                        bool isAtEdge;

                        if (isLandscape)
                        {
                            // Khi Landscape: Luôn luôn neo chặt vào mép trái hoặc mép phải gần nhất
                            targetLeft = isRightSide ? (screenWidth - _bubbleSize) : 0;
                            isAtEdge = true;
                        }
                        else
                        {
                            int snapEdgeThreshold = DpToPx(_activity, 20);
                            if (_lp.LeftMargin <= snapEdgeThreshold || _lp.LeftMargin >= (screenWidth - _bubbleSize - snapEdgeThreshold))
                            {
                                targetLeft = isRightSide ? (screenWidth - _bubbleSize) : 0;
                                isAtEdge = true;
                            }
                            else
                            {
                                targetLeft = Math.Clamp(_lp.LeftMargin, 0, screenWidth - _bubbleSize);
                                isAtEdge = false;
                            }
                        }

                        AnimateInAppSnap(_lp.LeftMargin, targetLeft, _lp.TopMargin, targetTop, () =>
                        {
                            _onDragEnd?.Invoke(isRightSide, isAtEdge);
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
                    SyncSatellitePosition();
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

#pragma warning disable CA1422
    public static int GetScreenWidth(Context context)
    {
        try
        {
            var wm = (context as Activity)?.WindowManager ??
                     (IWindowManager?)context.GetSystemService(Context.WindowService) ??
                     (IWindowManager?)context.ApplicationContext?.GetSystemService(Context.WindowService);

            if (OperatingSystem.IsAndroidVersionAtLeast(30) && wm != null)
            {
                var metrics = wm.CurrentWindowMetrics;
                if (metrics != null)
                {
                    return metrics.Bounds.Width();
                }
            }

            if (wm != null)
            {
                var display = wm.DefaultDisplay;
                if (display != null)
                {
                    var p = new Point();
                    display.GetRealSize(p);
                    if (p.X > 0) return p.X;
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
            var wm = (context as Activity)?.WindowManager ??
                     (IWindowManager?)context.GetSystemService(Context.WindowService) ??
                     (IWindowManager?)context.ApplicationContext?.GetSystemService(Context.WindowService);

            if (OperatingSystem.IsAndroidVersionAtLeast(30) && wm != null)
            {
                var metrics = wm.CurrentWindowMetrics;
                if (metrics != null)
                {
                    return metrics.Bounds.Height();
                }
            }

            if (wm != null)
            {
                var display = wm.DefaultDisplay;
                if (display != null)
                {
                    var p = new Point();
                    display.GetRealSize(p);
                    if (p.Y > 0) return p.Y;
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
#pragma warning restore CA1422

    public static int DpToPx(Context context, float dp)
    {
        float density = context.Resources?.DisplayMetrics?.Density ?? 1.0f;
        return (int)Math.Ceiling(dp * density);
    }

    private class QuickAddClickListener : Java.Lang.Object, View.IOnClickListener
    {
        private readonly Action _onClick;
        public QuickAddClickListener(Action onClick) => _onClick = onClick;
        public void OnClick(View? v) => _onClick?.Invoke();
    }

    private class ToggleTouchListener : Java.Lang.Object, View.IOnTouchListener
    {
        private readonly Action _onToggle;
        public ToggleTouchListener(Action onToggle) => _onToggle = onToggle;
        public bool OnTouch(View? v, MotionEvent? e)
        {
            if (e == null) return false;
            if (e.Action == MotionEventActions.Up)
            {
                _onToggle?.Invoke();
            }
            return true;
        }
    }
}
