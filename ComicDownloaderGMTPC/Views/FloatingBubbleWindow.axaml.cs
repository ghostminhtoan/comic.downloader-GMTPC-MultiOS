using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    private readonly DispatcherTimer _longPressTimer = new();
    private readonly Stopwatch _pressStopwatch = new();

    private bool _isPointerDown = false;
    private bool _isDragging = false;
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

        // Cấu hình bộ đếm nhấn giữ 3 giây
        _longPressTimer.Interval = TimeSpan.FromMilliseconds(3000);
        _longPressTimer.Tick += (_, _) =>
        {
            _longPressTimer.Stop();
            if (_isPointerDown && !_isDragging)
            {
                _isPointerDown = false;
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
        StartIdleTimer();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _idleTimer.Stop();
        _longPressTimer.Stop();
        BackgroundExecutionService.Instance.TaskProgressChanged -= OnTaskProgressChanged;
        BackgroundExecutionService.Instance.AnyTaskRunningChanged -= OnAnyTaskRunningChanged;
    }

    #region Mouse Dragging & Gesture Interactions

    private void OnBubblePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pointProps = e.GetCurrentPoint(this).Properties;

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
            _pointerStartScreenPoint = this.PointToScreen(e.GetPosition(this));
            _windowStartPosition = Position;

            _pressStopwatch.Restart();
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
            _longPressTimer.Stop(); // Hủy đếm nhấn giữ 3 giây
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
        _longPressTimer.Stop();

        if (_isPointerDown)
        {
            _isPointerDown = false;

            if (_isDragging)
            {
                _isDragging = false;
                HandleDragCompleted();
            }
            else
            {
                // Nhấn chuột đơn thuần (Single Click) -> Mở lại cửa sổ chính
                RestoreMainWindow();
            }
            e.Handled = true;
        }
    }

    private void OnBubblePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isPointerDown = false;
        _isDragging = false;
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
            Opacity = 0.45;

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
        if (_mainWindow != null)
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }
    }

    private void ShowBubbleContextMenu()
    {
        WakeUpAndRestore();

        var menu = new ContextMenu();

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

        menu.Items.Add(itemCloseBubble);
        menu.Items.Add(new Separator());
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

                    BadgeText.Text = $"{task.ProgressPercentage}%";
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
            BadgeText.Text = $"{primary.ProgressPercentage}%";
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
