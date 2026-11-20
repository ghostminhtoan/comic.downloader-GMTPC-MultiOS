using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.Views;

/// <summary>
/// Control vẽ vòng tròn tiến trình Circular / Radial Progress Ring:
/// - Vẽ đường ray nền mờ 360 độ (Track).
/// - Vẽ cung tròn tiến trình (DrawArc) theo góc quét % (0 đến 360 độ) xuất phát từ đỉnh 12h.
/// - Tự động đổi màu viền và nét vẽ theo từng loại tác vụ.
/// </summary>
public class CircularProgressRingControl : Control
{
    private double _progress = 0;
    private IBrush _progressBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
    private readonly Pen _trackPen = new(new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)), 3.0);
    private bool _isActive = false;

    public void UpdateProgress(double pct, IBrush brush, bool isActive)
    {
        _progress = Math.Clamp(pct, 0, 100);
        _progressBrush = brush;
        _isActive = isActive;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double strokeWidth = 3.5;
        double radius = (Math.Min(Bounds.Width, Bounds.Height) - strokeWidth - 6) / 2.0;
        if (radius <= 0) return;

        Point center = new(Bounds.Width / 2.0, Bounds.Height / 2.0);

        if (_isActive && _progress > 0)
        {
            // 1. Vẽ đường ray nền mờ 360 độ
            context.DrawEllipse(null, _trackPen, center, radius, radius);

            // 2. Vẽ cung tiến trình theo chiều kim đồng hồ từ đỉnh -90 độ
            double sweepAngle = (_progress / 100.0) * 360.0;
            if (sweepAngle >= 359.9)
            {
                var fullPen = new Pen(_progressBrush, strokeWidth, lineCap: PenLineCap.Round);
                context.DrawEllipse(null, fullPen, center, radius, radius);
            }
            else
            {
                double startAngleRad = -Math.PI / 2.0;
                double endAngleRad = startAngleRad + (sweepAngle * Math.PI / 180.0);

                Point startPoint = new(center.X + radius * Math.Cos(startAngleRad), center.Y + radius * Math.Sin(startAngleRad));
                Point endPoint = new(center.X + radius * Math.Cos(endAngleRad), center.Y + radius * Math.Sin(endAngleRad));
                bool isLargeArc = sweepAngle > 180.0;

                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(startPoint, false);
                    ctx.ArcTo(endPoint, new Size(radius, radius), 0, isLargeArc, SweepDirection.Clockwise);
                }

                var arcPen = new Pen(_progressBrush, strokeWidth, lineCap: PenLineCap.Round);
                context.DrawGeometry(null, arcPen, geometry);
            }
        }
    }
}

/// <summary>
/// Cửa sổ Bong bóng nổi (Floating Bubble Window) chạy ngầm trên Windows & Linux Desktop:
/// - Kích thước tròn 76x76, nền trong suốt, luôn nổi trên cùng (Topmost = true).
/// - Tự do kéo thả chuột ở bất kỳ đâu trên màn hình desktop (cả ở giữa lẫn mép viền).
/// - Tự động hít nhẹ vào mép viền khi kéo sát mép màn hình.
/// - Tự động thụt 55% vào mép và làm mờ 45% sau 3.5 giây nhàn rỗi (Auto-dock & Fade).
/// - Khôi phục 100% độ rõ và thụt ra ngoài khi chuột rê vào hoặc chạm.
/// - Nhấn nhanh để khôi phục và kích hoạt cửa sổ chính MainWindow.
/// - Nhấn giữ 3 giây hoặc Click chuột phải để hiển thị menu tùy chọn (Tắt bong bóng / Tắt app).
/// - Vòng tiến trình tròn Circular Progress Bar đổi màu linh hoạt theo tác vụ.
/// - Con số % động: ở nửa bên phải màn hình thì Badge ở góc dưới trái, ở nửa bên trái thì Badge ở góc dưới phải.
/// </summary>
public partial class FloatingBubbleWindow : Window
{
    private readonly Window? _mainWindow;
    private readonly CircularProgressRingControl _ringControl = new();

    private readonly DispatcherTimer _idleTimer = new();
    private readonly DispatcherTimer _shortPressTimer = new();
    private readonly DispatcherTimer _longPressTimer = new();
    private readonly Stopwatch _pressStopwatch = new();
    private MiniHudWindow? _miniHudWindow;

    private bool _isPointerDown = false;
    private bool _isDragging = false;
    private bool _isShortPressTriggered = false;
    private bool _isLongPressTriggered = false;
    private PixelPoint _pointerStartScreenPoint;
    private PixelPoint _windowStartPosition;

    private bool _isRightSide = true;
    private bool _isAtEdge = false;
    private bool _isDocked = false;

    public FloatingBubbleWindow() : this(null)
    {
    }

    public FloatingBubbleWindow(Window? mainWindow)
    {
        InitializeComponent();
        _mainWindow = mainWindow;

        RingHost.Content = _ringControl;

        // Cấu hình bộ đếm nhàn rỗi 3.5 giây
        _idleTimer.Interval = TimeSpan.FromMilliseconds(3500);
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            PerformAutoDockAndFade();
        };

        // Cấu hình bộ đếm nhấn giữ ngắn 500ms để hiện Mini HUD
        _shortPressTimer.Interval = TimeSpan.FromMilliseconds(500);
        _shortPressTimer.Tick += (_, _) =>
        {
            _shortPressTimer.Stop();
            if (_isPointerDown && !_isDragging)
            {
                _isShortPressTriggered = true;
                ShowMiniHud();
            }
        };

        // Cấu hình bộ đếm nhấn giữ 3 giây để hiện Menu Tùy chọn đầy đủ
        _longPressTimer.Interval = TimeSpan.FromMilliseconds(3000);
        _longPressTimer.Tick += (_, _) =>
        {
            _longPressTimer.Stop();
            if (_isPointerDown && !_isDragging)
            {
                _isLongPressTriggered = true;
                _isPointerDown = false;
                HideMiniHud();
                ShowBubbleContextMenu();
            }
        };

        // Bắt sự kiện chuột dạng Tunnel để không bị cản trở bởi các visual con
        AddHandler(PointerPressedEvent, OnBubblePointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnBubblePointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnBubblePointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, OnBubblePointerCaptureLost, RoutingStrategies.Tunnel);
        AddHandler(PointerEnteredEvent, OnBubblePointerEntered, RoutingStrategies.Tunnel);
        AddHandler(PointerExitedEvent, OnBubblePointerExited, RoutingStrategies.Tunnel);

        Opened += OnWindowOpened;
        Closed += OnWindowClosed;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        // Định vị vị trí ban đầu: góc trên bên phải màn hình
        var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
        if (screen != null)
        {
            var workArea = screen.WorkingArea;
            int initialX = workArea.X + workArea.Width - (int)Width - 25;
            int initialY = workArea.Y + 120;
            Position = new PixelPoint(initialX, initialY);
            _isRightSide = true;
            _isAtEdge = false;
            UpdateBadgePosition(true);
        }

        // Đăng ký nhận cập nhật tiến trình chạy ngầm
        BackgroundExecutionService.Instance.TaskProgressChanged -= OnTaskProgressChanged;
        BackgroundExecutionService.Instance.TaskProgressChanged += OnTaskProgressChanged;
        BackgroundExecutionService.Instance.AnyTaskRunningChanged -= OnAnyTaskRunningChanged;
        BackgroundExecutionService.Instance.AnyTaskRunningChanged += OnAnyTaskRunningChanged;

        // Khởi tạo trạng thái ban đầu
        SyncCurrentTaskState();
        InstanceConfigService.Instance.BubbleQuickAddButtonEnabledChanged -= OnBubbleQuickAddButtonEnabledChanged;
        InstanceConfigService.Instance.BubbleQuickAddButtonEnabledChanged += OnBubbleQuickAddButtonEnabledChanged;
        InstanceConfigService.Instance.BubbleStyleChanged -= OnBubbleStyleChanged;
        InstanceConfigService.Instance.BubbleStyleChanged += OnBubbleStyleChanged;
        ApplyBubbleStyle();
        UpdateQuickAddVisualState();
        StartIdleTimer();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _idleTimer.Stop();
        _shortPressTimer.Stop();
        _longPressTimer.Stop();
        InstanceConfigService.Instance.BubbleQuickAddButtonEnabledChanged -= OnBubbleQuickAddButtonEnabledChanged;
        InstanceConfigService.Instance.BubbleStyleChanged -= OnBubbleStyleChanged;
        BackgroundExecutionService.Instance.TaskProgressChanged -= OnTaskProgressChanged;
        BackgroundExecutionService.Instance.AnyTaskRunningChanged -= OnAnyTaskRunningChanged;
        _miniHudWindow?.Close();
        _miniHudWindow = null;
    }

    #region Mouse Dragging & Gesture Interactions

    private static bool IsDescendantOf(Visual? visual, Visual? ancestor)
    {
        if (visual == null || ancestor == null) return false;
        var current = visual;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = current.GetVisualParent();
        }
        return false;
    }

    private void OnBubblePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pointProps = e.GetCurrentPoint(this).Properties;

        // Bỏ qua sự kiện kéo thả nếu nhấn vào nút Toggle hoặc nút QuickAdd
        if (e.Source is Visual visual && (IsDescendantOf(visual, QuickAddLeftButton) || IsDescendantOf(visual, QuickAddRightButton) || IsDescendantOf(visual, ToggleQuickAddBorder)))
        {
            return;
        }

        // Chuột phải -> Mở menu ngay lập tức
        if (pointProps.IsRightButtonPressed)
        {
            e.Handled = true;
            ShowBubbleContextMenu();
            return;
        }

        // Chuột trái -> Bắt đầu theo dõi nhấn giữ hoặc kéo thả
        if (pointProps.IsLeftButtonPressed)
        {
            _isPointerDown = true;
            _isDragging = false;
            _isShortPressTriggered = false;
            _isLongPressTriggered = false;
            _pointerStartScreenPoint = this.PointToScreen(e.GetPosition(this));
            _windowStartPosition = Position;

            _pressStopwatch.Restart();
            _shortPressTimer.Start();
            _longPressTimer.Start();

            CancelIdleTimer();
            WakeUpAndRestore();

            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    private void OnBubblePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPointerDown) return;

        var currentScreenPoint = this.PointToScreen(e.GetPosition(this));
        int dx = currentScreenPoint.X - _pointerStartScreenPoint.X;
        int dy = currentScreenPoint.Y - _pointerStartScreenPoint.Y;

        // Nếu di chuyển vượt quá ngưỡng 5px -> Chuyển sang chế độ Kéo Thả (Drag)
        if (!_isDragging && (Math.Abs(dx) > 5 || Math.Abs(dy) > 5))
        {
            _isDragging = true;
            _shortPressTimer.Stop();
            _longPressTimer.Stop();
            HideMiniHud();
        }

        if (_isDragging)
        {
            Position = new PixelPoint(_windowStartPosition.X + dx, _windowStartPosition.Y + dy);
            e.Handled = true;
        }
    }

    private void OnBubblePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        _shortPressTimer.Stop();
        _longPressTimer.Stop();

        if (_isPointerDown)
        {
            _isPointerDown = false;

            if (_isDragging)
            {
                _isDragging = false;
                HandleDragCompleted();
            }
            else if (_isLongPressTriggered)
            {
                // Đã kích hoạt menu tùy chọn 3s
            }
            else if (_isShortPressTriggered)
            {
                // Đã kích hoạt Mini HUD ở mốc 500ms -> Thả tay ra giữ nguyên Mini HUD để xem
            }
            else
            {
                // Nhấn chuột đơn thuần (Single Click ngắn < 500ms) -> Mở lại cửa sổ chính
                RestoreMainWindow();
            }
            e.Handled = true;
        }
    }

    private void OnBubblePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isPointerDown = false;
        _isDragging = false;
        _shortPressTimer.Stop();
        _longPressTimer.Stop();
        StartIdleTimer();
    }

    private void OnBubblePointerEntered(object? sender, PointerEventArgs e)
    {
        CancelIdleTimer();
        WakeUpAndRestore();
    }

    private void OnBubblePointerExited(object? sender, PointerEventArgs e)
    {
        if (!_isPointerDown)
        {
            StartIdleTimer();
        }
    }

    private void HandleDragCompleted()
    {
        var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
        if (screen != null)
        {
            var workArea = screen.WorkingArea;
            int centerX = Position.X + (int)(Width / 2);
            _isRightSide = centerX >= (workArea.X + workArea.Width / 2);
            UpdateBadgePosition(_isRightSide);

            // Kiểm tra hít mép: Nếu khoảng cách tới mép viền < 30px thì hít mép viền,
            // còn nếu người dùng để ở giữa thì GIỮ NGUYÊN (hỗ trợ tự do đặt bất kỳ đâu).
            int leftDist = Position.X - workArea.X;
            int rightDist = (workArea.X + workArea.Width) - (Position.X + (int)Width);

            if (leftDist >= 0 && leftDist < 30)
            {
                Position = new PixelPoint(workArea.X, Position.Y);
                _isAtEdge = true;
            }
            else if (rightDist >= 0 && rightDist < 30)
            {
                Position = new PixelPoint(workArea.X + workArea.Width - (int)Width, Position.Y);
                _isAtEdge = true;
            }
            else
            {
                _isAtEdge = false;
            }
        }

        _isDocked = false;
        StartIdleTimer();
    }

    #endregion

    #region Auto-dock, Fade & Context Menu

    private void StartIdleTimer()
    {
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private void CancelIdleTimer()
    {
        _idleTimer.Stop();
    }

    private void PerformAutoDockAndFade()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Opacity = InstanceConfigService.Instance.BubbleIdleAlpha;

            // Nếu đang ở sát mép màn hình: Tự động thụt 55% thân bóng vào mép
            if (_isAtEdge && !_isDocked)
            {
                var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
                if (screen != null)
                {
                    var workArea = screen.WorkingArea;
                    int dockOffset = (int)(Width * 0.55);

                    if (_isRightSide)
                    {
                        Position = new PixelPoint(workArea.X + workArea.Width - (int)Width + dockOffset, Position.Y);
                    }
                    else
                    {
                        Position = new PixelPoint(workArea.X - dockOffset, Position.Y);
                    }
                    _isDocked = true;
                }
            }
        });
    }

    private void WakeUpAndRestore()
    {
        Opacity = 1.0;
        UpdateQuickAddVisualState();

        if (_isDocked)
        {
            var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
            if (screen != null)
            {
                var workArea = screen.WorkingArea;
                if (_isRightSide)
                {
                    Position = new PixelPoint(workArea.X + workArea.Width - (int)Width, Position.Y);
                }
                else
                {
                    Position = new PixelPoint(workArea.X, Position.Y);
                }
            }
            _isDocked = false;
        }
    }

    public void RestoreMainWindow()
    {
        WakeUpAndRestore();
        HideMiniHud();
        if (_mainWindow != null)
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }
    }

    public void ShowMiniHud()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_miniHudWindow == null)
                {
                    _miniHudWindow = new MiniHudWindow(() => RestoreMainWindow());
                }

                var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
                int screenW = screen?.WorkingArea.Width ?? 1920;
                int screenH = screen?.WorkingArea.Height ?? 1080;
                int screenX = screen?.WorkingArea.X ?? 0;
                int screenY = screen?.WorkingArea.Y ?? 0;

                int hudW = (int)_miniHudWindow.Width;
                int hudH = (int)_miniHudWindow.Height;
                int gap = 10;

                int hudX = _isRightSide
                    ? (Position.X - hudW - gap)
                    : (Position.X + (int)Width + gap);

                int minY = screenY + 20;
                int maxY = Math.Max(minY, screenY + screenH - hudH - 20);
                int hudY = Math.Clamp(Position.Y, minY, maxY);

                _miniHudWindow.Position = new PixelPoint(hudX, hudY);
                _miniHudWindow.Show();
                _miniHudWindow.UpdateContent();
                _miniHudWindow.RestartAutoHideTimer();
            }
            catch { }
        });
    }

    public void HideMiniHud()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _miniHudWindow?.Hide();
            }
            catch { }
        });
    }

    private void OnBubbleStyleChanged()
    {
        Dispatcher.UIThread.Post(ApplyBubbleStyle);
    }

    private void ApplyBubbleStyle()
    {
        try
        {
            int dp = InstanceConfigService.Instance.BubbleSizeDp;
            int bodySize = dp switch
            {
                48 => 46,
                72 => 68,
                _ => 56
            };
            int ringSize = dp switch
            {
                48 => 64,
                72 => 88,
                _ => 76
            };

            BubbleBody.Width = bodySize;
            BubbleBody.Height = bodySize;
            BubbleBody.CornerRadius = new CornerRadius(bodySize / 2.0);
            RingHost.Width = ringSize;
            RingHost.Height = ringSize;

            if (_isDocked)
            {
                PerformAutoDockAndFade();
            }
        }
        catch { }
    }

    private void ShowBubbleContextMenu()
    {
        WakeUpAndRestore();

        var menu = new ContextMenu();

        // 1. Xem Mini HUD
        var itemMiniHud = new MenuItem
        {
            Header = "📊 Xem Mini HUD (Tiến trình & Tốc độ)",
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF))
        };
        itemMiniHud.Click += (_, _) => ShowMiniHud();
        menu.Items.Add(itemMiniHud);

        // 2. Đổi kích cỡ (Submenu)
        string curSizeLabel = InstanceConfigService.Instance.BubbleSize switch
        {
            "Small" => "Nhỏ (48px)",
            "Large" => "Lớn (72px)",
            _ => "Vừa (60px)"
        };
        var itemSize = new MenuItem
        {
            Header = $"📏 Đổi kích cỡ [{curSizeLabel}]",
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8))
        };
        var itemSizeSmall = new MenuItem
        {
            Header = (InstanceConfigService.Instance.BubbleSize == "Small" ? "✓ " : "   ") + "Nhỏ (48px)"
        };
        itemSizeSmall.Click += (_, _) =>
        {
            InstanceConfigService.Instance.BubbleSize = "Small";
            ApplyBubbleStyle();
        };
        var itemSizeMed = new MenuItem
        {
            Header = (InstanceConfigService.Instance.BubbleSize == "Medium" ? "✓ " : "   ") + "Vừa (60px - Mặc định)"
        };
        itemSizeMed.Click += (_, _) =>
        {
            InstanceConfigService.Instance.BubbleSize = "Medium";
            ApplyBubbleStyle();
        };
        var itemSizeLarge = new MenuItem
        {
            Header = (InstanceConfigService.Instance.BubbleSize == "Large" ? "✓ " : "   ") + "Lớn (72px)"
        };
        itemSizeLarge.Click += (_, _) =>
        {
            InstanceConfigService.Instance.BubbleSize = "Large";
            ApplyBubbleStyle();
        };
        itemSize.Items.Add(itemSizeSmall);
        itemSize.Items.Add(itemSizeMed);
        itemSize.Items.Add(itemSizeLarge);
        menu.Items.Add(itemSize);

        // 3. Đổi độ mờ nhàn rỗi (Submenu)
        int curAlphaPct = (int)Math.Round(InstanceConfigService.Instance.BubbleIdleAlpha * 100);
        var itemAlpha = new MenuItem
        {
            Header = $"👁️ Đổi độ mờ nhàn rỗi [{curAlphaPct}%]",
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x84, 0xFC))
        };
        var itemAlpha30 = new MenuItem
        {
            Header = (Math.Abs(InstanceConfigService.Instance.BubbleIdleAlpha - 0.30f) < 0.05f ? "✓ " : "   ") + "Mờ nhiều (30%)"
        };
        itemAlpha30.Click += (_, _) => InstanceConfigService.Instance.BubbleIdleAlpha = 0.30f;
        var itemAlpha45 = new MenuItem
        {
            Header = (Math.Abs(InstanceConfigService.Instance.BubbleIdleAlpha - 0.45f) < 0.05f ? "✓ " : "   ") + "Chuẩn (45% - Mặc định)"
        };
        itemAlpha45.Click += (_, _) => InstanceConfigService.Instance.BubbleIdleAlpha = 0.45f;
        var itemAlpha60 = new MenuItem
        {
            Header = (Math.Abs(InstanceConfigService.Instance.BubbleIdleAlpha - 0.60f) < 0.05f ? "✓ " : "   ") + "Rõ nét (60%)"
        };
        itemAlpha60.Click += (_, _) => InstanceConfigService.Instance.BubbleIdleAlpha = 0.60f;
        var itemAlpha75 = new MenuItem
        {
            Header = (Math.Abs(InstanceConfigService.Instance.BubbleIdleAlpha - 0.75f) < 0.05f ? "✓ " : "   ") + "Rất rõ (75%)"
        };
        itemAlpha75.Click += (_, _) => InstanceConfigService.Instance.BubbleIdleAlpha = 0.75f;
        itemAlpha.Items.Add(itemAlpha30);
        itemAlpha.Items.Add(itemAlpha45);
        itemAlpha.Items.Add(itemAlpha60);
        itemAlpha.Items.Add(itemAlpha75);
        menu.Items.Add(itemAlpha);

        // 4. Bật/Tắt nút (+) thêm link
        bool isQuickAddEnabled = InstanceConfigService.Instance.IsBubbleQuickAddButtonEnabled;
        var itemToggleQuickAdd = new MenuItem
        {
            Header = isQuickAddEnabled ? "⚪ Ẩn nút (+) thêm link" : "⚡ Hiện nút (+) thêm link",
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF))
        };
        itemToggleQuickAdd.Click += (_, _) =>
        {
            InstanceConfigService.Instance.IsBubbleQuickAddButtonEnabled = !InstanceConfigService.Instance.IsBubbleQuickAddButtonEnabled;
            UpdateQuickAddVisualState();
        };
        menu.Items.Add(itemToggleQuickAdd);

        // 5. Bật/Tắt nút (✕) tắt cạnh bong bóng
        bool isCloseEnabled = InstanceConfigService.Instance.IsBubbleCloseButtonEnabled;
        var itemToggleClose = new MenuItem
        {
            Header = isCloseEnabled ? "⚪ Ẩn nút (✕) tắt cạnh bong bóng" : "🛑 Hiện nút (✕) tắt cạnh bong bóng",
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x3F, 0x5E))
        };
        itemToggleClose.Click += (_, _) =>
        {
            InstanceConfigService.Instance.IsBubbleCloseButtonEnabled = !InstanceConfigService.Instance.IsBubbleCloseButtonEnabled;
        };
        menu.Items.Add(itemToggleClose);

        menu.Items.Add(new Separator());

        // 6. Tắt bong bóng
        var itemCloseBubble = new MenuItem
        {
            Header = "🚫 Tắt bong bóng",
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8))
        };
        itemCloseBubble.Click += (_, _) =>
        {
            BackgroundExecutionService.Instance.SetBubbleMode(false);
        };
        menu.Items.Add(itemCloseBubble);

        menu.Items.Add(new Separator());

        // 7. Tắt app hoàn toàn
        var itemExitApp = new MenuItem
        {
            Header = "🛑 Tắt app hoàn toàn",
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x3F, 0x5E))
        };
        itemExitApp.Click += (_, _) =>
        {
            BackgroundExecutionService.Instance.ForceExit();
        };
        menu.Items.Add(itemExitApp);

        menu.Open(this);
    }

    #endregion

    #region Task Progress & Dynamic Badge Positioning

    private void UpdateBadgePosition(bool isRightSide)
    {
        // Khi Bubble ở nửa bên PHẢI màn hình: Badge % đặt ở góc dưới bên TRÁI
        // Khi Bubble ở nửa bên TRÁI màn hình: Badge % đặt ở góc dưới bên PHẢI
        BadgeBorder.HorizontalAlignment = isRightSide ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right;
        if (ToggleQuickAddBorder != null)
        {
            ToggleQuickAddBorder.HorizontalAlignment = isRightSide ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right;
            ToggleQuickAddBorder.Margin = isRightSide ? new Thickness(8, 8, 0, 0) : new Thickness(0, 8, 8, 0);
        }
        UpdateQuickAddVisualState();
    }

    private void OnBubbleQuickAddButtonEnabledChanged(bool enabled)
    {
        Dispatcher.UIThread.Post(UpdateQuickAddVisualState);
    }

    private void OnToggleQuickAddPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        WakeUpAndRestore();
        InstanceConfigService.Instance.IsBubbleQuickAddButtonEnabled = !InstanceConfigService.Instance.IsBubbleQuickAddButtonEnabled;
        UpdateQuickAddVisualState();
    }

    private async void OnQuickAddButtonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        WakeUpAndRestore();

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            string? text = null;
            if (topLevel?.Clipboard != null)
            {
                text = await topLevel.Clipboard.TryGetTextAsync();
            }

            if (!string.IsNullOrWhiteSpace(text) && (text.Contains("http://", StringComparison.OrdinalIgnoreCase) || text.Contains("https://", StringComparison.OrdinalIgnoreCase)))
            {
                App.EnqueueSharedLinkText(text);
                var vm = (_mainWindow?.DataContext as ViewModels.MainViewModel) ?? App.TryGetSharedMainViewModel();
                if (vm != null)
                {
                    _ = vm.DrainPendingSharedLinksAsync();
                }

                BadgeText.Text = "OK";
                BadgeBorder.Background = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                BadgeBorder.IsVisible = true;

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.0) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    SyncCurrentTaskState();
                };
                timer.Start();
            }
            else
            {
                BadgeText.Text = "CLIP?";
                BadgeBorder.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                BadgeBorder.IsVisible = true;

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.0) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    SyncCurrentTaskState();
                };
                timer.Start();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Quick add clipboard desktop error: {ex.Message}");
        }
    }

    public void UpdateQuickAddVisualState()
    {
        bool isEnabled = InstanceConfigService.Instance.IsBubbleQuickAddButtonEnabled;

        if (ToggleQuickAddBorder != null)
        {
            ToggleQuickAddBorder.Background = isEnabled
                ? new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));
        }

        if (ToggleQuickAddText != null)
        {
            ToggleQuickAddText.Text = isEnabled ? "⚡" : "○";
            ToggleQuickAddText.Foreground = isEnabled
                ? new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A))
                : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        }

        bool shouldShow = isEnabled;
        if (QuickAddLeftButton != null)
        {
            QuickAddLeftButton.IsVisible = shouldShow && _isRightSide;
        }
        if (QuickAddRightButton != null)
        {
            QuickAddRightButton.IsVisible = shouldShow && !_isRightSide;
        }
    }

    private void OnTaskProgressChanged(BackgroundTaskInfo task)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (task.IsRunning)
                {
                    var taskBrush = GetTaskBrush(task.Id);

                    int pct = (int)Math.Round(Math.Clamp(task.ProgressPercentage, 0, 100));
                    BadgeText.Text = task.ProgressPercentage >= 0 ? $"{pct}%" : "●";
                    BadgeBorder.Background = taskBrush;
                    BadgeBorder.IsVisible = true;

                    // Chỉnh màu chữ Badge đảm bảo độ tương phản cao
                    BadgeText.Foreground = IsDarkColor(taskBrush) ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));

                    _ringControl.UpdateProgress(task.ProgressPercentage, taskBrush, true);

                    // Đánh thức nhẹ Bubble khi có tiến trình mới
                    if (_isDocked)
                    {
                        Opacity = 0.90;
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                        timer.Tick += (s, ev) =>
                        {
                            timer.Stop();
                            if (_isDocked) Opacity = 0.45;
                        };
                        timer.Start();
                    }
                }
                else
                {
                    SyncCurrentTaskState();
                }
            }
            catch { }
        });
    }

    private void OnAnyTaskRunningChanged(bool anyRunning)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!anyRunning)
            {
                BadgeBorder.IsVisible = false;
                _ringControl.UpdateProgress(0, Brushes.Cyan, false);
            }
            else
            {
                SyncCurrentTaskState();
            }
        });
    }

    private void SyncCurrentTaskState()
    {
        var primary = BackgroundExecutionService.Instance.GetPrimaryTask();
        if (primary != null && primary.IsRunning)
        {
            var brush = GetTaskBrush(primary.Id);
            int pct = (int)Math.Round(Math.Clamp(primary.ProgressPercentage, 0, 100));
            BadgeText.Text = primary.ProgressPercentage >= 0 ? $"{pct}%" : "●";
            BadgeBorder.Background = brush;
            BadgeBorder.IsVisible = true;
            BadgeText.Foreground = IsDarkColor(brush) ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
            _ringControl.UpdateProgress(primary.ProgressPercentage, brush, true);
        }
        else
        {
            BadgeBorder.IsVisible = false;
            _ringControl.UpdateProgress(0, Brushes.Cyan, false);
        }
    }

    private static IBrush GetTaskBrush(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));

        if (taskId.Contains("download", StringComparison.OrdinalIgnoreCase))
            return new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)); // Cyber Cyan
        if (taskId.Contains("image_enhancer", StringComparison.OrdinalIgnoreCase) || taskId.Contains("convert", StringComparison.OrdinalIgnoreCase))
            return new SolidColorBrush(Color.FromRgb(0xC0, 0x84, 0xFC)); // Purple Neon
        if (taskId.Contains("file_packer", StringComparison.OrdinalIgnoreCase))
            return new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // Amber Cam
        if (taskId.Contains("image_splitter", StringComparison.OrdinalIgnoreCase))
            return new SolidColorBrush(Color.FromRgb(0xF4, 0x3F, 0x5E)); // Rose Hồng
        if (taskId.Contains("folder_tools", StringComparison.OrdinalIgnoreCase))
            return new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)); // Emerald Xanh
        if (taskId.Contains("archive_merger", StringComparison.OrdinalIgnoreCase))
            return new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)); // Sky Blue

        return new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
    }

    private static bool IsDarkColor(IBrush brush)
    {
        if (brush is ISolidColorBrush scb)
        {
            var c = scb.Color;
            double luminance = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            return luminance < 140;
        }
        return false;
    }

    #endregion
}
