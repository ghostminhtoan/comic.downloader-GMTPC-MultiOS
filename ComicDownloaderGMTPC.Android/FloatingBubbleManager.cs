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
/// - Cử chỉ thông minh (Smart Gestures):
///   + Chạm 1 lần (Single-Tap): Mở ứng dụng ngay tức thì.
///   + Chạm 2 lần (Double-Tap): Nạp link từ Clipboard vào Queue tải ngầm kèm hiệu ứng Cyber Pulse nảy sóng neon.
///   + Nhấn giữ 500ms: Hiện thẻ Mini HUD Glassmorphism xem tốc độ tải, % tiến trình và 2 nút Tạm dừng/Bỏ qua.
///   + Nhấn giữ 3000ms: Mở menu tùy chọn chuyên sâu (kích thước, độ mờ, đóng).
///   + Kéo thả xuống đáy màn hình: Thùng rác đỏ 64dp xuất hiện, hút dính dưới 85dp để tắt bong bóng chuẩn Facebook Messenger.
/// - Tự động thụt vào mép viền (Auto-docking) theo tỷ lệ tùy biến sau 3.5 giây nhàn rỗi.
/// - Độ mờ linh hoạt (Dynamic Opacity) tùy biến từ 30% đến 75%.
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
    private static TextView? _toggleCloseView;
    private static View? _closeSatelliteView;
    private static WindowManagerLayoutParams? _closeSatelliteLayoutParams;
    private static FrameLayout.LayoutParams? _inAppCloseSatelliteLp;
    private static int _satelliteSizePx;
    private static int _satelliteSpacingPx;

    // Trash Drop Target (Thùng rác đáy màn hình chuẩn Facebook Messenger)
    private static View? _trashTargetView;
    private static WindowManagerLayoutParams? _trashLayoutParams;
    private static FrameLayout.LayoutParams? _inAppTrashLp;
    private static bool _isInTrashTarget = false;

    // Thẻ Mini HUD Tốc Độ & Tiến Trình (Glassmorphism Mini Card)
    private static View? _miniHudView;
    private static WindowManagerLayoutParams? _miniHudLayoutParams;
    private static FrameLayout.LayoutParams? _inAppMiniHudLp;
    private static TextView? _miniHudTitleText;
    private static TextView? _miniHudStatusText;
    private static TextView? _miniHudSpeedText;
    private static ProgressBar? _miniHudProgressBar;
    private static TextView? _miniHudPlayPauseBtn;
    private static TextView? _miniHudSkipBtn;
    private static Java.Lang.Runnable? _autoHideHudRunnable;

    private static readonly Handler _mainHandler = new(Looper.MainLooper!);

    static FloatingBubbleManager()
    {
        try
        {
            InstanceConfigService.Instance.BubbleStyleChanged += () =>
            {
                RefreshBubbleStyle();
            };
        }
        catch { }
    }
    
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

            _bubbleSizePx = DpToPx(context, InstanceConfigService.Instance.BubbleSizeDp);
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

            // 1. Thùng rác đáy màn hình (Trash Drop Target)
            int trashSize = DpToPx(context, 64);
            _trashTargetView = CreateTrashTargetView(context);
            _trashLayoutParams = new WindowManagerLayoutParams(
                trashSize,
                trashSize,
                windowType,
                flags,
                Format.Translucent)
            {
                Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
                Y = DpToPx(context, 36)
            };
            _windowManager.AddView(_trashTargetView, _trashLayoutParams);

            // 2. Thẻ Mini HUD Tốc độ & Tiến trình (Glassmorphism)
            int hudWidth = DpToPx(context, 230);
            _miniHudView = CreateMiniHudView(context);
            _miniHudLayoutParams = new WindowManagerLayoutParams(
                hudWidth,
                WindowManagerLayoutParams.WrapContent,
                windowType,
                flags,
                Format.Translucent)
            {
                Gravity = GravityFlags.Top | GravityFlags.Left
            };
            _windowManager.AddView(_miniHudView, _miniHudLayoutParams);

            // 3. Nút vệ tinh (✕) tắt app nếu người dùng bật
            _satelliteSizePx = DpToPx(context, 36);
            _satelliteSpacingPx = DpToPx(context, 8);
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

            // Đăng ký nhận tiến trình để cập nhật badge và Mini HUD
            BackgroundExecutionService.Instance.TaskProgressChanged -= OnTaskProgressChanged;
            BackgroundExecutionService.Instance.TaskProgressChanged += OnTaskProgressChanged;

            StartIdleTimer();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ComicGMTPC_Bubble", $"Lỗi tạo System Overlay Bubble: {ex.Message}");
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

            _bubbleSizePx = DpToPx(activity, InstanceConfigService.Instance.BubbleSizeDp);
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

            // 1. Thùng rác đáy màn hình
            int trashSize = DpToPx(activity, 64);
            _trashTargetView = CreateTrashTargetView(activity);
            _inAppTrashLp = new FrameLayout.LayoutParams(trashSize, trashSize)
            {
                Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
                BottomMargin = DpToPx(activity, 36)
            };
            rootContainer.AddView(_trashTargetView, _inAppTrashLp);
            _trashTargetView.BringToFront();

            // 2. Thẻ Mini HUD
            int hudWidth = DpToPx(activity, 230);
            _miniHudView = CreateMiniHudView(activity);
            _inAppMiniHudLp = new FrameLayout.LayoutParams(hudWidth, FrameLayout.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.Top | GravityFlags.Left
            };
            rootContainer.AddView(_miniHudView, _inAppMiniHudLp);
            _miniHudView.BringToFront();

            // 3. Nút vệ tinh (✕)
            _satelliteSizePx = DpToPx(activity, 36);
            _satelliteSpacingPx = DpToPx(activity, 8);
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

        // Nút Toggle Bật/Tắt dấu (✕) tắt app ở góc trên
        _toggleCloseView = new TextView(context);
        _toggleCloseView.SetTextSize(ComplexUnitType.Dip, 8.5f);
        _toggleCloseView.Gravity = GravityFlags.Center;
        _toggleCloseView.SetTypeface(null, TypefaceStyle.Bold);

        UpdateToggleCloseVisual();

        int toggleSize = DpToPx(context, 18);
        int toggleMargin = DpToPx(context, 3);
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

    /// <summary>
    /// Xử lý cập nhật vị trí neo của Bong bóng khi xoay màn hình (Portrait sang Landscape)
    /// hoặc khi kích thước hiển thị thay đổi: tự động neo chặt vào mép trái/phải tương ứng.
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

                int dockOffset = (int)(_bubbleSizePx * InstanceConfigService.Instance.BubbleDockOffsetRatio);
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
        var context = _toggleCloseView?.Context;
        if (context == null) return;

        int toggleSize = DpToPx(context, 18);
        int toggleMargin = DpToPx(context, 3);

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
        string msg = newState ? "🛑 Đã bật nút (✕) đóng/tắt cạnh bong bóng" : "⚪ Đã ẩn nút (✕) đóng/tắt";
        try { Toast.MakeText(context, msg, ToastLength.Short)?.Show(); } catch { }
    }

    /// <summary>
    /// Đảo vị trí nút vệ tinh trực quan (Smart Satellite Alignment):
    /// Khi Bubble ở nửa phải: nút vệ tinh xòe sang bên trái (hướng vào lòng màn hình).
    /// Khi Bubble ở nửa trái: nút vệ tinh xòe sang bên phải (hướng vào lòng màn hình).
    /// </summary>
    public static void SyncSatellitePosition()
    {
        bool hasClose = InstanceConfigService.Instance.IsBubbleCloseButtonEnabled;

        if (_closeSatelliteView != null)
        {
            _closeSatelliteView.Visibility = hasClose ? ViewStates.Visible : ViewStates.Gone;
        }

        if (!hasClose)
        {
            return;
        }

        if (_isSystemOverlay && _systemLayoutParams != null && _windowManager != null)
        {
            int satX = _isRightSide
                ? (_systemLayoutParams.X - _satelliteSizePx - _satelliteSpacingPx)
                : (_systemLayoutParams.X + _bubbleSizePx + _satelliteSpacingPx);
            int satY = _systemLayoutParams.Y + (_bubbleSizePx - _satelliteSizePx) / 2;

            if (_closeSatelliteView != null && _closeSatelliteLayoutParams != null)
            {
                _closeSatelliteLayoutParams.X = satX;
                _closeSatelliteLayoutParams.Y = satY;
                try { _windowManager.UpdateViewLayout(_closeSatelliteView, _closeSatelliteLayoutParams); } catch { }
            }
        }
        else if (!_isSystemOverlay && _inAppLayoutParams != null)
        {
            int satLeft = _isRightSide
                ? (_inAppLayoutParams.LeftMargin - _satelliteSizePx - _satelliteSpacingPx)
                : (_inAppLayoutParams.LeftMargin + _bubbleSizePx + _satelliteSpacingPx);
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
                        TriggerPulseEffect();
                        return true;
                    }
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Kích hoạt hiệu ứng sóng xung kích Cyber Pulse Feedback:
    /// Bubble phóng to nhẹ 1.22x và nảy đàn hồi về 1.0x kèm rung Haptic xác nhận.
    /// Dùng khi có link nạp vào Queue thành công (Double-tap, Process Text, Share intent).
    /// </summary>
    public static void TriggerPulseEffect()
    {
        _mainHandler.Post(() =>
        {
            try
            {
                if (_bubbleView == null) return;
                WakeUpAndRestore();
                try { _bubbleView.PerformHapticFeedback(FeedbackConstants.KeyboardTap); } catch { }

                _bubbleView.Animate()
                    ?.ScaleX(1.22f)
                    ?.ScaleY(1.22f)
                    ?.SetDuration(120)
                    ?.SetInterpolator(new DecelerateInterpolator())
                    ?.WithEndAction(new Java.Lang.Runnable(() =>
                    {
                        _bubbleView?.Animate()
                            ?.ScaleX(1.0f)
                            ?.ScaleY(1.0f)
                            ?.SetDuration(160)
                            ?.SetInterpolator(new OvershootInterpolator(1.2f))
                            ?.Start();
                    }))
                    ?.Start();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("ComicGMTPC_Bubble", $"Lỗi TriggerPulseEffect: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Cử chỉ Double-Tap: Tự động nạp link từ clipboard vào queue ngầm.
    /// </summary>
    public static void TriggerDoubleTapQuickAdd(Context context)
    {
        TriggerPulseEffect();

        // Bước 1: Thử Fast Path đọc clipboard trực tiếp nếu đang có quyền focus
        if (TryDirectClipboardQuickAdd(context))
        {
            return;
        }

        // Bước 2: Kích hoạt ShareLinkActivity để nhận Window Focus đọc clipboard trên Android 10+
        ShareLinkActivity.LaunchQuickAddFromClipboard(context);
    }

    // --- Trash Drop Target (Thùng rác đáy màn hình chuẩn Facebook Messenger) ---
    private static View CreateTrashTargetView(Context context)
    {
        var container = new FrameLayout(context);
        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Oval);
        bg.SetColor(Color.Argb(180, 185, 28, 28)); // #B91C1C bán trong suốt
        bg.SetStroke(DpToPx(context, 2f), Color.Rgb(239, 68, 68)); // #EF4444
        container.Background = bg;

        if (OperatingSystem.IsAndroidVersionAtLeast(21))
        {
            container.Elevation = DpToPx(context, 16);
        }

        var text = new TextView(context);
        text.Text = "✕";
        text.SetTextSize(ComplexUnitType.Dip, 22f);
        text.SetTextColor(Color.White);
        text.SetTypeface(null, TypefaceStyle.Bold);
        text.Gravity = GravityFlags.Center;

        var lp = new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MatchParent, FrameLayout.LayoutParams.MatchParent)
        {
            Gravity = GravityFlags.Center
        };
        container.AddView(text, lp);

        container.Visibility = ViewStates.Gone;
        return container;
    }

    public static void ShowTrashTarget()
    {
        _mainHandler.Post(() =>
        {
            try
            {
                if (_trashTargetView == null) return;
                _trashTargetView.Visibility = ViewStates.Visible;
                _trashTargetView.ScaleX = 1.0f;
                _trashTargetView.ScaleY = 1.0f;
                _trashTargetView.Alpha = 0f;
                _trashTargetView.Animate()
                    ?.Alpha(1.0f)
                    ?.SetDuration(180)
                    ?.Start();
            }
            catch { }
        });
    }

    public static void HideTrashTarget()
    {
        _mainHandler.Post(() =>
        {
            try
            {
                if (_trashTargetView != null && _trashTargetView.Visibility == ViewStates.Visible)
                {
                    _trashTargetView.Animate()
                        ?.Alpha(0f)
                        ?.SetDuration(180)
                        ?.WithEndAction(new Java.Lang.Runnable(() =>
                        {
                            if (_trashTargetView != null)
                            {
                                _trashTargetView.Visibility = ViewStates.Gone;
                            }
                        }))
                        ?.Start();
                }
            }
            catch { }
        });
    }

    public static void SetTrashTargetHighlighted(bool highlighted)
    {
        _mainHandler.Post(() =>
        {
            try
            {
                if (_trashTargetView == null) return;
                var ctx = _trashTargetView.Context;
                if (ctx == null) return;
                var bg = _trashTargetView.Background as GradientDrawable;
                if (highlighted)
                {
                    _trashTargetView.Animate()
                        ?.ScaleX(1.3f)
                        ?.ScaleY(1.3f)
                        ?.SetDuration(120)
                        ?.Start();
                    bg?.SetColor(Color.Rgb(220, 38, 38)); // #DC2626 đỏ tươi rực sáng
                    bg?.SetStroke(DpToPx(ctx, 3f), Color.Rgb(254, 202, 202));
                }
                else
                {
                    _trashTargetView.Animate()
                        ?.ScaleX(1.0f)
                        ?.ScaleY(1.0f)
                        ?.SetDuration(120)
                        ?.Start();
                    bg?.SetColor(Color.Argb(180, 185, 28, 28)); // #B91C1C
                    bg?.SetStroke(DpToPx(ctx, 2f), Color.Rgb(239, 68, 68));
                }
            }
            catch { }
        });
    }

    // --- Thẻ Mini HUD Tốc Độ & Tiến Trình (Glassmorphism Mini Card) ---
    private static View CreateMiniHudView(Context context)
    {
        var card = new LinearLayout(context)
        {
            Orientation = Orientation.Vertical
        };
        int padH = DpToPx(context, 12);
        int padV = DpToPx(context, 10);
        card.SetPadding(padH, padV, padH, padV);

        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Rectangle);
        bg.SetCornerRadius(DpToPx(context, 14));
        bg.SetColor(Color.Argb(235, 15, 23, 42)); // Slate #0F172A Glassmorphism
        bg.SetStroke(DpToPx(context, 1.5f), Color.Argb(180, 0, 229, 255)); // Neon Cyan
        card.Background = bg;

        if (OperatingSystem.IsAndroidVersionAtLeast(21))
        {
            card.Elevation = DpToPx(context, 16);
        }

        // 1. Header: Tiêu đề + Tốc độ
        var headerRow = new LinearLayout(context)
        {
            Orientation = Orientation.Horizontal
        };
        var headerLp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        headerRow.LayoutParameters = headerLp;

        _miniHudTitleText = new TextView(context)
        {
            Text = "Comic Downloader GMTPC",
            TextSize = 11.5f,
            Ellipsize = global::Android.Text.TextUtils.TruncateAt.End
        };
        _miniHudTitleText.SetSingleLine(true);
        _miniHudTitleText.SetTextColor(Color.White);
        _miniHudTitleText.SetTypeface(null, TypefaceStyle.Bold);
        var titleLp = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WrapContent, 1f);
        headerRow.AddView(_miniHudTitleText, titleLp);

        _miniHudSpeedText = new TextView(context)
        {
            Text = "⚡ 0.0 KB/s",
            TextSize = 9.5f
        };
        _miniHudSpeedText.SetSingleLine(true);
        _miniHudSpeedText.SetTextColor(Color.Rgb(0, 229, 255)); // Cyan
        _miniHudSpeedText.SetTypeface(null, TypefaceStyle.Bold);
        var speedLp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WrapContent, LinearLayout.LayoutParams.WrapContent);
        speedLp.LeftMargin = DpToPx(context, 6);
        headerRow.AddView(_miniHudSpeedText, speedLp);

        card.AddView(headerRow);

        // 2. Status / Chap / Chi tiết
        _miniHudStatusText = new TextView(context)
        {
            Text = "Đang ở chế độ chờ...",
            TextSize = 9.5f,
            Ellipsize = global::Android.Text.TextUtils.TruncateAt.End
        };
        _miniHudStatusText.SetSingleLine(true);
        _miniHudStatusText.SetTextColor(Color.Rgb(148, 163, 184)); // Slate 400
        var statusLp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        statusLp.TopMargin = DpToPx(context, 3);
        card.AddView(_miniHudStatusText, statusLp);

        // 3. ProgressBar ngang
        _miniHudProgressBar = new ProgressBar(context, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal)
        {
            Max = 100,
            Progress = 0,
            Indeterminate = false
        };
        var pbLp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, DpToPx(context, 4));
        pbLp.TopMargin = DpToPx(context, 6);
        pbLp.BottomMargin = DpToPx(context, 6);
        card.AddView(_miniHudProgressBar, pbLp);

        // 4. Các nút mini thao tác nhanh: Tạm dừng / Bỏ qua chap
        var btnRow = new LinearLayout(context)
        {
            Orientation = Orientation.Horizontal
        };
        var btnRowLp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        btnRow.LayoutParameters = btnRowLp;

        _miniHudPlayPauseBtn = new TextView(context)
        {
            Text = "⏸️ Tạm dừng",
            TextSize = 9.5f,
            Gravity = GravityFlags.Center
        };
        _miniHudPlayPauseBtn.SetTextColor(Color.White);
        _miniHudPlayPauseBtn.SetTypeface(null, TypefaceStyle.Bold);
        var btnBg1 = new GradientDrawable();
        btnBg1.SetShape(ShapeType.Rectangle);
        btnBg1.SetCornerRadius(DpToPx(context, 6));
        btnBg1.SetColor(Color.Rgb(51, 65, 85)); // Slate 700
        _miniHudPlayPauseBtn.Background = btnBg1;
        _miniHudPlayPauseBtn.SetPadding(DpToPx(context, 8), DpToPx(context, 4), DpToPx(context, 8), DpToPx(context, 4));
        var btn1Lp = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WrapContent, 1f);
        btn1Lp.RightMargin = DpToPx(context, 6);
        _miniHudPlayPauseBtn.SetOnClickListener(new QuickAddClickListener(() =>
        {
            try
            {
                BackgroundExecutionService.NativeNotificationTogglePauseRequested?.Invoke();
                UpdateMiniHudContent();
            }
            catch { }
        }));
        btnRow.AddView(_miniHudPlayPauseBtn, btn1Lp);

        _miniHudSkipBtn = new TextView(context)
        {
            Text = "⏭️ Bỏ qua",
            TextSize = 9.5f,
            Gravity = GravityFlags.Center
        };
        _miniHudSkipBtn.SetTextColor(Color.White);
        _miniHudSkipBtn.SetTypeface(null, TypefaceStyle.Bold);
        var btnBg2 = new GradientDrawable();
        btnBg2.SetShape(ShapeType.Rectangle);
        btnBg2.SetCornerRadius(DpToPx(context, 6));
        btnBg2.SetColor(Color.Rgb(51, 65, 85)); // Slate 700
        _miniHudSkipBtn.Background = btnBg2;
        _miniHudSkipBtn.SetPadding(DpToPx(context, 8), DpToPx(context, 4), DpToPx(context, 8), DpToPx(context, 4));
        var btn2Lp = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WrapContent, 1f);
        _miniHudSkipBtn.SetOnClickListener(new QuickAddClickListener(() =>
        {
            try
            {
                BackgroundExecutionService.NativeNotificationContextualAction1Requested?.Invoke();
                UpdateMiniHudContent();
            }
            catch { }
        }));
        btnRow.AddView(_miniHudSkipBtn, btn2Lp);

        card.AddView(btnRow);

        // Chạm vào card để mở ứng dụng
        card.SetOnClickListener(new QuickAddClickListener(() =>
        {
            OpenMainActivity(context);
            HideMiniHud();
        }));

        card.Visibility = ViewStates.Gone;
        return card;
    }

    public static void ShowMiniHud(Context context)
    {
        _mainHandler.Post(() =>
        {
            try
            {
                if (_miniHudView == null) return;
                CancelAutoHideMiniHud();
                UpdateMiniHudContent();

                int screenWidth = GetScreenWidth(context);
                int screenHeight = GetScreenHeight(context);
                int hudWidth = DpToPx(context, 230);
                int gap = DpToPx(context, 10);

                if (_isSystemOverlay && _systemLayoutParams != null && _windowManager != null && _miniHudLayoutParams != null)
                {
                    int hudX = _isRightSide
                        ? (_systemLayoutParams.X - hudWidth - gap)
                        : (_systemLayoutParams.X + _bubbleSizePx + gap);
                    int minY = DpToPx(context, 40);
                    int maxY = screenHeight - DpToPx(context, 140);
                    int hudY = Math.Clamp(_systemLayoutParams.Y, minY, Math.Max(minY, maxY));

                    _miniHudLayoutParams.X = hudX;
                    _miniHudLayoutParams.Y = hudY;
                    try { _windowManager.UpdateViewLayout(_miniHudView, _miniHudLayoutParams); } catch { }
                }
                else if (!_isSystemOverlay && _inAppLayoutParams != null && _inAppMiniHudLp != null)
                {
                    int hudLeft = _isRightSide
                        ? (_inAppLayoutParams.LeftMargin - hudWidth - gap)
                        : (_inAppLayoutParams.LeftMargin + _bubbleSizePx + gap);
                    int minY = DpToPx(context, 40);
                    int maxY = screenHeight - DpToPx(context, 140);
                    int hudTop = Math.Clamp(_inAppLayoutParams.TopMargin, minY, Math.Max(minY, maxY));

                    _inAppMiniHudLp.LeftMargin = hudLeft;
                    _inAppMiniHudLp.TopMargin = hudTop;
                    _miniHudView.LayoutParameters = _inAppMiniHudLp;
                    _miniHudView.RequestLayout();
                }

                _miniHudView.Visibility = ViewStates.Visible;
                _miniHudView.Alpha = 0f;
                _miniHudView.ScaleX = 0.85f;
                _miniHudView.ScaleY = 0.85f;
                _miniHudView.Animate()
                    ?.Alpha(1.0f)
                    ?.ScaleX(1.0f)
                    ?.ScaleY(1.0f)
                    ?.SetDuration(200)
                    ?.SetInterpolator(new DecelerateInterpolator())
                    ?.Start();

                StartAutoHideMiniHud();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("ComicGMTPC_Bubble", $"Lỗi ShowMiniHud: {ex.Message}");
            }
        });
    }

    public static void HideMiniHud()
    {
        _mainHandler.Post(() =>
        {
            try
            {
                CancelAutoHideMiniHud();
                if (_miniHudView != null && _miniHudView.Visibility == ViewStates.Visible)
                {
                    _miniHudView.Animate()
                        ?.Alpha(0f)
                        ?.ScaleX(0.85f)
                        ?.ScaleY(0.85f)
                        ?.SetDuration(180)
                        ?.WithEndAction(new Java.Lang.Runnable(() =>
                        {
                            if (_miniHudView != null)
                            {
                                _miniHudView.Visibility = ViewStates.Gone;
                            }
                        }))
                        ?.Start();
                }
            }
            catch { }
        });
    }

    private static void StartAutoHideMiniHud(long delayMs = 3500)
    {
        CancelAutoHideMiniHud();
        _autoHideHudRunnable = new Java.Lang.Runnable(() => HideMiniHud());
        _mainHandler.PostDelayed(_autoHideHudRunnable, delayMs);
    }

    private static void CancelAutoHideMiniHud()
    {
        if (_autoHideHudRunnable != null)
        {
            _mainHandler.RemoveCallbacks(_autoHideHudRunnable);
            _autoHideHudRunnable = null;
        }
    }

    public static void UpdateMiniHudContent()
    {
        if (_miniHudView == null) return;
        _mainHandler.Post(() =>
        {
            try
            {
                var primary = BackgroundExecutionService.Instance.GetPrimaryTask();
                bool isDownloading = DownloadEngineService.Instance.IsDownloading;
                bool isPaused = DownloadEngineService.Instance.IsPaused;
                string speed = DownloadEngineService.Instance.CurrentSpeedText;

                if (primary != null)
                {
                    _miniHudTitleText?.SetText(string.IsNullOrWhiteSpace(primary.Title) ? "Comic Downloader GMTPC" : primary.Title, TextView.BufferType.Normal);
                    int pct = (int)Math.Round(Math.Clamp(primary.ProgressPercentage, 0, 100));
                    string detail = string.IsNullOrWhiteSpace(primary.Detail) ? $"{pct}%" : $"{primary.Detail} ({pct}%)";
                    _miniHudStatusText?.SetText(detail, TextView.BufferType.Normal);
                    _miniHudSpeedText?.SetText(isDownloading ? $"⚡ {speed}" : "🟢 Đang xử lý", TextView.BufferType.Normal);
                    if (_miniHudProgressBar != null)
                    {
                        _miniHudProgressBar.Progress = pct;
                    }
                }
                else
                {
                    _miniHudTitleText?.SetText("Comic Downloader GMTPC", TextView.BufferType.Normal);
                    _miniHudStatusText?.SetText("Đang ở chế độ chờ", TextView.BufferType.Normal);
                    _miniHudSpeedText?.SetText("⚪ Nhàn rỗi", TextView.BufferType.Normal);
                    if (_miniHudProgressBar != null)
                    {
                        _miniHudProgressBar.Progress = 0;
                    }
                }

                if (_miniHudPlayPauseBtn != null)
                {
                    _miniHudPlayPauseBtn.Text = isPaused ? "▶️ Tiếp tục" : "⏸️ Tạm dừng";
                }
            }
            catch { }
        });
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

            ShowBubbleCloseOptionsDialog(context);
        }));

        return satButton;
    }

    /// <summary>
    /// Hiển thị hộp thoại khi chạm nút (✕) vệ tinh, cung cấp 2 tùy chọn rõ ràng cho người dùng:
    /// 1. Tắt bubble (chỉ ẩn bong bóng nổi, app vẫn tiếp tục chạy ngầm tải truyện)
    /// 2. Tắt hẳn app (Force Stop toàn diện)
    /// </summary>
    public static void ShowBubbleCloseOptionsDialog(Context context)
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

                bool isVi = LanguageService.Instance.IsVietnamese;
                builder.SetTitle(isVi ? "Tùy chọn đóng" : "Close options");

                string[] options = new string[]
                {
                    isVi ? "🚫 Tắt bubble" : "🚫 Close bubble",
                    isVi ? "🛑 Tắt hẳn app" : "🛑 Exit app completely"
                };

                builder.SetItems(options, (sender, args) =>
                {
                    int selected = args.Which;
                    if (selected == 0)
                    {
                        HideBubble();
                        try
                        {
                            BackgroundExecutionService.Instance.SetBubbleMode(false);
                        }
                        catch { }

                        try
                        {
                            string msg = isVi ? "Đã tắt bong bóng" : "Bubble closed";
                            Toast.MakeText(dialogCtx, msg, ToastLength.Short)?.Show();
                        }
                        catch { }
                    }
                    else if (selected == 1)
                    {
                        ForceStopApp(dialogCtx);
                    }
                });

                builder.SetNegativeButton(isVi ? "Hủy" : "Cancel", (sender, args) => { });

                ShowDialogSafely(builder, context);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("ComicGMTPC_Bubble", $"Lỗi hiển thị dialog tùy chọn đóng bubble: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Buộc dừng và đóng hoàn toàn ứng dụng (Force Stop), dọn dẹp Bubble, Foreground Service và tắt tiến trình.
    /// </summary>
    public static void ForceStopApp(Context context)
    {
        try
        {
            bool isVi = LanguageService.Instance.IsVietnamese;
            string msg = isVi ? "🛑 Đang tắt hoàn toàn ứng dụng..." : "🛑 Exiting app completely...";
            Toast.MakeText(context, msg, ToastLength.Short)?.Show();
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

        float idleAlpha = InstanceConfigService.Instance.BubbleIdleAlpha;
        float dockRatio = InstanceConfigService.Instance.BubbleDockOffsetRatio;

        if (_closeSatelliteView != null && InstanceConfigService.Instance.IsBubbleCloseButtonEnabled)
        {
            _closeSatelliteView.Animate()
                ?.Alpha(idleAlpha)
                ?.SetDuration(300)
                ?.Start();
        }

        var context = _bubbleView.Context;
        if (context == null) return;
        int screenWidth = GetScreenWidth(context);

        // Nếu Bubble ở sát mép viền màn hình: thụt vào mép theo tỷ lệ cấu hình
        if (_isAtEdge)
        {
            int dockOffset = (int)(_bubbleSizePx * dockRatio);

            if (_isSystemOverlay && _systemLayoutParams != null && _windowManager != null)
            {
                int targetX = _isRightSide ? (screenWidth - _bubbleSizePx + dockOffset) : (-dockOffset);
                AnimateSystemX(_systemLayoutParams.X, targetX, 280, () =>
                {
                    _bubbleView?.Animate()
                        ?.Alpha(idleAlpha)
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
                    ?.Alpha(idleAlpha)
                    ?.SetDuration(280)
                    ?.SetInterpolator(new DecelerateInterpolator())
                    ?.Start();
                if (_closeSatelliteView != null && InstanceConfigService.Instance.IsBubbleCloseButtonEnabled)
                {
                    _closeSatelliteView.Animate()
                        ?.TranslationX(targetTransX)
                        ?.Alpha(idleAlpha)
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
                ?.Alpha(idleAlpha)
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
                                float idleAlpha = InstanceConfigService.Instance.BubbleIdleAlpha;
                                _bubbleView.Animate()?.Alpha(idleAlpha)?.SetDuration(300)?.Start();
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

                if (_miniHudView != null && _miniHudView.Visibility == ViewStates.Visible)
                {
                    UpdateMiniHudContent();
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

                if (_trashTargetView != null)
                {
                    if (_isSystemOverlay && _windowManager != null)
                    {
                        try { _windowManager.RemoveView(_trashTargetView); } catch { }
                    }
                    else if (!_isSystemOverlay && _inAppContainer != null)
                    {
                        try { _inAppContainer.RemoveView(_trashTargetView); } catch { }
                    }
                    _trashTargetView = null;
                    _trashLayoutParams = null;
                    _inAppTrashLp = null;
                    _isInTrashTarget = false;
                }

                if (_miniHudView != null)
                {
                    if (_isSystemOverlay && _windowManager != null)
                    {
                        try { _windowManager.RemoveView(_miniHudView); } catch { }
                    }
                    else if (!_isSystemOverlay && _inAppContainer != null)
                    {
                        try { _inAppContainer.RemoveView(_miniHudView); } catch { }
                    }
                    _miniHudView = null;
                    _miniHudLayoutParams = null;
                    _inAppMiniHudLp = null;
                    _miniHudTitleText = null;
                    _miniHudStatusText = null;
                    _miniHudSpeedText = null;
                    _miniHudProgressBar = null;
                    _miniHudPlayPauseBtn = null;
                    _miniHudSkipBtn = null;
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
                _toggleCloseView = null;
                _isDocked = false;
                _isShowing = false;
            }
        });
    }

    /// <summary>
    /// Hiển thị menu tùy chọn khi ấn giữ Bong bóng trong 3 giây:
    /// - Xem Mini HUD
    /// - Đổi kích cỡ (Nhỏ 48dp, Vừa 60dp, Lớn 72dp)
    /// - Đổi độ mờ nhàn rỗi (30%, 45%, 60%, 75%)
    /// - Ẩn/Hiện nút (✕) tắt app
    /// - Tắt bong bóng
    /// - Tắt app hoàn toàn
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

                bool isCloseEnabled = InstanceConfigService.Instance.IsBubbleCloseButtonEnabled;
                string currentSize = InstanceConfigService.Instance.BubbleSize switch
                {
                    "Small" => "Nhỏ (48dp)",
                    "Large" => "Lớn (72dp)",
                    _ => "Vừa (60dp)"
                };
                int currentAlphaPct = (int)Math.Round(InstanceConfigService.Instance.BubbleIdleAlpha * 100);

                string[] options = new string[]
                {
                    "📊 Xem Mini HUD (Tiến trình & Tốc độ)",
                    $"📏 Đổi kích cỡ [{currentSize}]",
                    $"👁️ Đổi độ mờ nhàn rỗi [{currentAlphaPct}%]",
                    isCloseEnabled ? "⚪ Ẩn nút (✕) tắt cạnh bong bóng" : "🛑 Hiện nút (✕) tắt cạnh bong bóng",
                    "🚫 Tắt bong bóng",
                    "🛑 Tắt app hoàn toàn"
                };

                builder.SetItems(options, (sender, args) =>
                {
                    int selected = args.Which;
                    if (selected == 0)
                    {
                        ShowMiniHud(dialogCtx);
                    }
                    else if (selected == 1)
                    {
                        ShowBubbleSizeDialog(dialogCtx);
                    }
                    else if (selected == 2)
                    {
                        ShowBubbleAlphaDialog(dialogCtx);
                    }
                    else if (selected == 3)
                    {
                        ToggleCloseState(dialogCtx);
                    }
                    else if (selected == 4)
                    {
                        HideBubble();
                        try
                        {
                            Toast.MakeText(dialogCtx, "Đã tắt bong bóng", ToastLength.Short)?.Show();
                        }
                        catch { }
                    }
                    else if (selected == 5)
                    {
                        ExitAppCompletely(dialogCtx);
                    }
                });

                builder.SetNegativeButton("Hủy", (sender, args) => { });

                ShowDialogSafely(builder, context);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("ComicGMTPC_Bubble", $"Lỗi hiển thị dialog tùy chọn bubble: {ex.Message}");
            }
        });
    }

    private static void ShowBubbleSizeDialog(Context context)
    {
        var builder = new AlertDialog.Builder(context);
        builder.SetTitle("📏 Chọn kích cỡ Bong bóng");
        string[] sizes = new[] { "Nhỏ (48dp)", "Vừa (60dp - Mặc định)", "Lớn (72dp)" };
        int checkedItem = InstanceConfigService.Instance.BubbleSize switch
        {
            "Small" => 0,
            "Large" => 2,
            _ => 1
        };
        builder.SetSingleChoiceItems(sizes, checkedItem, (s, a) =>
        {
            string newSize = a.Which switch
            {
                0 => "Small",
                2 => "Large",
                _ => "Medium"
            };
            InstanceConfigService.Instance.BubbleSize = newSize;
            (s as global::Android.Content.IDialogInterface)?.Dismiss();
            Toast.MakeText(context, $"📏 Đã đổi kích cỡ sang: {sizes[a.Which]}", ToastLength.Short)?.Show();
        });
        builder.SetNegativeButton("Hủy", (s, a) => { });
        ShowDialogSafely(builder, context);
    }

    private static void ShowBubbleAlphaDialog(Context context)
    {
        var builder = new AlertDialog.Builder(context);
        builder.SetTitle("👁️ Độ mờ khi nhàn rỗi");
        string[] alphas = new[] { "Mờ nhiều (30%)", "Chuẩn (45% - Mặc định)", "Rõ nét (60%)", "Rất rõ (75%)" };
        float currentAlpha = InstanceConfigService.Instance.BubbleIdleAlpha;
        int checkedItem = currentAlpha switch
        {
            <= 0.35f => 0,
            <= 0.50f => 1,
            <= 0.65f => 2,
            _ => 3
        };
        builder.SetSingleChoiceItems(alphas, checkedItem, (s, a) =>
        {
            float newAlpha = a.Which switch
            {
                0 => 0.30f,
                2 => 0.60f,
                3 => 0.75f,
                _ => 0.45f
            };
            InstanceConfigService.Instance.BubbleIdleAlpha = newAlpha;
            (s as global::Android.Content.IDialogInterface)?.Dismiss();
            Toast.MakeText(context, $"👁️ Đã chỉnh độ mờ nhàn rỗi: {alphas[a.Which]}", ToastLength.Short)?.Show();
        });
        builder.SetNegativeButton("Hủy", (s, a) => { });
        ShowDialogSafely(builder, context);
    }

    private static void ShowDialogSafely(AlertDialog.Builder builder, Context context)
    {
        var dialog = builder.Create();
        if (dialog?.Window != null)
        {
            var activity = context as Activity ?? MainActivity.CurrentInstance;
            bool isActivityValid = activity != null && !activity.IsFinishing && !activity.IsDestroyed;
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

    public static void RefreshBubbleStyle(Context? context = null)
    {
        _mainHandler.Post(() =>
        {
            try
            {
                if (_bubbleView == null || !_isShowing) return;
                var ctx = context ?? _bubbleView.Context ?? MainActivity.CurrentInstance?.ApplicationContext;
                if (ctx == null) return;

                _bubbleSizePx = DpToPx(ctx, InstanceConfigService.Instance.BubbleSizeDp);

                if (_isSystemOverlay && _systemLayoutParams != null && _windowManager != null)
                {
                    _systemLayoutParams.Width = _bubbleSizePx;
                    _systemLayoutParams.Height = _bubbleSizePx;
                    _windowManager.UpdateViewLayout(_bubbleView, _systemLayoutParams);
                }
                else if (!_isSystemOverlay && _inAppLayoutParams != null && _bubbleView != null)
                {
                    _inAppLayoutParams.Width = _bubbleSizePx;
                    _inAppLayoutParams.Height = _bubbleSizePx;
                    _bubbleView.LayoutParameters = _inAppLayoutParams;
                    _bubbleView.RequestLayout();
                }

                HandleOrientationOrSizeChanged(ctx, null);
                SyncSatellitePosition();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("ComicGMTPC_Bubble", $"Lỗi RefreshBubbleStyle: {ex.Message}");
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
        private bool _hudTriggered;
        private long _lastTapTime;
        private Java.Lang.Runnable? _singleTapRunnable;
        private Java.Lang.Runnable? _miniHudRunnable;
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
            _hudTriggered = false;
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
                    _hudTriggered = false;
                    _onTouchStart?.Invoke();

                    if (_miniHudRunnable != null) _mainHandler.RemoveCallbacks(_miniHudRunnable);
                    if (_longPressRunnable != null) _mainHandler.RemoveCallbacks(_longPressRunnable);

                    // 1. Timer hiển thị Mini HUD ở mốc 500ms
                    _miniHudRunnable = new Java.Lang.Runnable(() =>
                    {
                        if (!_isDragging)
                        {
                            _hudTriggered = true;
                            try { v?.PerformHapticFeedback(FeedbackConstants.ContextClick); } catch { }
                            ShowMiniHud(_context);
                        }
                    });
                    _mainHandler.PostDelayed(_miniHudRunnable, 500);

                    // 2. Timer hiển thị Options Dialog ở mốc 3000ms
                    _longPressRunnable = new Java.Lang.Runnable(() =>
                    {
                        if (!_isDragging)
                        {
                            _isLongPressed = true;
                            HideMiniHud();
                            try { v?.PerformHapticFeedback(FeedbackConstants.LongPress); } catch { }
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
                        if (_miniHudRunnable != null) { _mainHandler.RemoveCallbacks(_miniHudRunnable); _miniHudRunnable = null; }
                        if (_longPressRunnable != null) { _mainHandler.RemoveCallbacks(_longPressRunnable); _longPressRunnable = null; }
                        if (_singleTapRunnable != null) { _mainHandler.RemoveCallbacks(_singleTapRunnable); _singleTapRunnable = null; }
                        _lastTapTime = 0;
                        HideMiniHud();
                        ShowTrashTarget();
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

                        // Kiểm tra khoảng cách tới tâm Thùng rác đáy màn hình
                        int screenWidth = GetScreenWidth(_context);
                        int screenHeight = GetScreenHeight(_context);
                        float bubbleCenterX = _lp.X + _bubbleSize / 2f;
                        float bubbleCenterY = _lp.Y + _bubbleSize / 2f;
                        float trashCenterX = screenWidth / 2f;
                        float trashCenterY = screenHeight - DpToPx(_context, 36) - (DpToPx(_context, 64) / 2f);

                        float dist = (float)Math.Sqrt(Math.Pow(bubbleCenterX - trashCenterX, 2) + Math.Pow(bubbleCenterY - trashCenterY, 2));
                        bool inTrash = dist < DpToPx(_context, 85);

                        if (inTrash && !_isInTrashTarget)
                        {
                            _isInTrashTarget = true;
                            SetTrashTargetHighlighted(true);
                            try { v?.PerformHapticFeedback(FeedbackConstants.LongPress); } catch { }
                        }
                        else if (!inTrash && _isInTrashTarget)
                        {
                            _isInTrashTarget = false;
                            SetTrashTargetHighlighted(false);
                        }
                    }
                    return true;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    if (_miniHudRunnable != null)
                    {
                        _mainHandler.RemoveCallbacks(_miniHudRunnable);
                        _miniHudRunnable = null;
                    }
                    if (_longPressRunnable != null)
                    {
                        _mainHandler.RemoveCallbacks(_longPressRunnable);
                        _longPressRunnable = null;
                    }

                    HideTrashTarget();

                    // TH 1: Thả vào Thùng rác -> Tắt bong bóng
                    if (_isInTrashTarget)
                    {
                        _isInTrashTarget = false;
                        HideBubble();
                        try { BackgroundExecutionService.Instance.SetBubbleMode(false); } catch { }
                        try { Toast.MakeText(_context, "🚫 Đã tắt bong bóng", ToastLength.Short)?.Show(); } catch { }
                        try { v?.PerformHapticFeedback(FeedbackConstants.LongPress); } catch { }
                        return true;
                    }

                    // TH 2: Giữ lâu 3s -> Menu Options đã hiển thị
                    if (_isLongPressed)
                    {
                        _isLongPressed = false;
                        return true;
                    }

                    // TH 3: Nhấn giữ 500ms xem Mini HUD -> Thả tay ra thì giữ nguyên Mini HUD xem tiếp
                    if (_hudTriggered)
                    {
                        _hudTriggered = false;
                        return true;
                    }

                    // TH 4: Chạm nhanh (Single-Tap mở app, Double-Tap nạp link)
                    if (!_isDragging)
                    {
                        long now = SystemClock.UptimeMillis();
                        long timeDiff = now - _lastTapTime;

                        if (timeDiff < 320)
                        {
                            // DOUBLE-TAP: Hủy mở app, nạp link ngay lập tức!
                            if (_singleTapRunnable != null)
                            {
                                _mainHandler.RemoveCallbacks(_singleTapRunnable);
                                _singleTapRunnable = null;
                            }
                            _lastTapTime = 0;
                            TriggerDoubleTapQuickAdd(_context);
                        }
                        else
                        {
                            // Chờ 320ms để nhận diện xem có chạm lần 2 không. Nếu không -> SINGLE-TAP mở MainActivity
                            _lastTapTime = now;
                            if (_singleTapRunnable != null)
                            {
                                _mainHandler.RemoveCallbacks(_singleTapRunnable);
                            }
                            _singleTapRunnable = new Java.Lang.Runnable(() =>
                            {
                                _singleTapRunnable = null;
                                _onTap?.Invoke();
                            });
                            _mainHandler.PostDelayed(_singleTapRunnable, 320);
                        }
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
        private bool _hudTriggered;
        private long _lastTapTime;
        private Java.Lang.Runnable? _singleTapRunnable;
        private Java.Lang.Runnable? _miniHudRunnable;
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
            _hudTriggered = false;
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
                    _hudTriggered = false;
                    _onTouchStart?.Invoke();

                    if (_miniHudRunnable != null) _mainHandler.RemoveCallbacks(_miniHudRunnable);
                    if (_longPressRunnable != null) _mainHandler.RemoveCallbacks(_longPressRunnable);

                    // 1. Timer hiển thị Mini HUD ở mốc 500ms
                    _miniHudRunnable = new Java.Lang.Runnable(() =>
                    {
                        if (!_isDragging)
                        {
                            _hudTriggered = true;
                            try { v?.PerformHapticFeedback(FeedbackConstants.ContextClick); } catch { }
                            ShowMiniHud(_activity);
                        }
                    });
                    _mainHandler.PostDelayed(_miniHudRunnable, 500);

                    // 2. Timer hiển thị Options Dialog ở mốc 3000ms
                    _longPressRunnable = new Java.Lang.Runnable(() =>
                    {
                        if (!_isDragging)
                        {
                            _isLongPressed = true;
                            HideMiniHud();
                            try { v?.PerformHapticFeedback(FeedbackConstants.LongPress); } catch { }
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
                        if (_miniHudRunnable != null) { _mainHandler.RemoveCallbacks(_miniHudRunnable); _miniHudRunnable = null; }
                        if (_longPressRunnable != null) { _mainHandler.RemoveCallbacks(_longPressRunnable); _longPressRunnable = null; }
                        if (_singleTapRunnable != null) { _mainHandler.RemoveCallbacks(_singleTapRunnable); _singleTapRunnable = null; }
                        _lastTapTime = 0;
                        HideMiniHud();
                        ShowTrashTarget();
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

                        // Kiểm tra khoảng cách tới tâm Thùng rác đáy màn hình
                        int screenWidth = GetScreenWidth(_activity);
                        int screenHeight = GetScreenHeight(_activity);
                        float bubbleCenterX = _lp.LeftMargin + _bubbleSize / 2f;
                        float bubbleCenterY = _lp.TopMargin + _bubbleSize / 2f;
                        float trashCenterX = screenWidth / 2f;
                        float trashCenterY = screenHeight - DpToPx(_activity, 36) - (DpToPx(_activity, 64) / 2f);

                        float dist = (float)Math.Sqrt(Math.Pow(bubbleCenterX - trashCenterX, 2) + Math.Pow(bubbleCenterY - trashCenterY, 2));
                        bool inTrash = dist < DpToPx(_activity, 85);

                        if (inTrash && !_isInTrashTarget)
                        {
                            _isInTrashTarget = true;
                            SetTrashTargetHighlighted(true);
                            try { v?.PerformHapticFeedback(FeedbackConstants.LongPress); } catch { }
                        }
                        else if (!inTrash && _isInTrashTarget)
                        {
                            _isInTrashTarget = false;
                            SetTrashTargetHighlighted(false);
                        }
                    }
                    return true;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    if (_miniHudRunnable != null)
                    {
                        _mainHandler.RemoveCallbacks(_miniHudRunnable);
                        _miniHudRunnable = null;
                    }
                    if (_longPressRunnable != null)
                    {
                        _mainHandler.RemoveCallbacks(_longPressRunnable);
                        _longPressRunnable = null;
                    }

                    HideTrashTarget();

                    // TH 1: Thả vào Thùng rác -> Tắt bong bóng
                    if (_isInTrashTarget)
                    {
                        _isInTrashTarget = false;
                        HideBubble();
                        try { BackgroundExecutionService.Instance.SetBubbleMode(false); } catch { }
                        try { Toast.MakeText(_activity, "🚫 Đã tắt bong bóng", ToastLength.Short)?.Show(); } catch { }
                        try { v?.PerformHapticFeedback(FeedbackConstants.LongPress); } catch { }
                        return true;
                    }

                    // TH 2: Giữ lâu 3s -> Menu Options đã hiển thị
                    if (_isLongPressed)
                    {
                        _isLongPressed = false;
                        return true;
                    }

                    // TH 3: Nhấn giữ 500ms xem Mini HUD -> Thả tay ra thì giữ nguyên Mini HUD xem tiếp
                    if (_hudTriggered)
                    {
                        _hudTriggered = false;
                        return true;
                    }

                    // TH 4: Chạm nhanh (Single-Tap mở app, Double-Tap nạp link)
                    if (!_isDragging)
                    {
                        long now = SystemClock.UptimeMillis();
                        long timeDiff = now - _lastTapTime;

                        if (timeDiff < 320)
                        {
                            // DOUBLE-TAP: Hủy mở app, nạp link ngay lập tức!
                            if (_singleTapRunnable != null)
                            {
                                _mainHandler.RemoveCallbacks(_singleTapRunnable);
                                _singleTapRunnable = null;
                            }
                            _lastTapTime = 0;
                            TriggerDoubleTapQuickAdd(_activity);
                        }
                        else
                        {
                            // Chờ 320ms để nhận diện xem có chạm lần 2 không. Nếu không -> SINGLE-TAP mở MainActivity
                            _lastTapTime = now;
                            if (_singleTapRunnable != null)
                            {
                                _mainHandler.RemoveCallbacks(_singleTapRunnable);
                            }
                            _singleTapRunnable = new Java.Lang.Runnable(() =>
                            {
                                _singleTapRunnable = null;
                                _onTap?.Invoke();
                            });
                            _mainHandler.PostDelayed(_singleTapRunnable, 320);
                        }
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
