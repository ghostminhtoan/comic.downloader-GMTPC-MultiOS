using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using ComicDownloaderGMTPC.ViewModels;

namespace ComicDownloaderGMTPC.Views;

public partial class DetachedQueueWindow : Window
{
    private MainViewModel? _subscribedVm;
    private bool _isFloatingBarDragging = false;
    private Point _floatingDragStartPointer;
    private double _floatingStartTransformX = 0;
    private double _floatingStartTransformY = 0;
    private bool _isFloatingBarCollapsed = false;

    public DetachedQueueWindow()
    {
        InitializeComponent();
        AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, DetachedQueueWindow_PointerWheelChanged, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, DetachedQueueWindow_KeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        DataContextChanged += OnDataContextChanged;
        SizeChanged += (s, e) =>
        {
            if (DataContext is MainViewModel vm && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                vm.IsPortraitMode = e.NewSize.Width < e.NewSize.Height;
            }
        };

        Loaded += (s, e) =>
        {
            SetupFloatingBarDraggable();
            if (DataContext is MainViewModel vm && Bounds.Width > 0 && Bounds.Height > 0)
            {
                vm.IsPortraitMode = Bounds.Width < Bounds.Height;
            }
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.ScrollToTopRequested -= OnScrollToTopRequested;
            _subscribedVm.ScrollToBottomRequested -= OnScrollToBottomRequested;
            _subscribedVm = null;
        }

        if (DataContext is MainViewModel vm)
        {
            _subscribedVm = vm;
            vm.ScrollToTopRequested += OnScrollToTopRequested;
            vm.ScrollToBottomRequested += OnScrollToBottomRequested;
        }
    }

    private void OnScrollToTopRequested()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var sv1 = this.FindControl<ScrollViewer>("DetachedSingleColumnScrollViewer");
            var sv2 = this.FindControl<ScrollViewer>("DetachedGridScrollViewer");
            var sv3 = this.FindControl<ScrollViewer>("DetachedThumbnailScrollViewer");
            sv1?.ScrollToHome();
            sv2?.ScrollToHome();
            sv3?.ScrollToHome();
        });
    }

    private void OnScrollToBottomRequested()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var sv1 = this.FindControl<ScrollViewer>("DetachedSingleColumnScrollViewer");
            var sv2 = this.FindControl<ScrollViewer>("DetachedGridScrollViewer");
            var sv3 = this.FindControl<ScrollViewer>("DetachedThumbnailScrollViewer");
            sv1?.ScrollToEnd();
            sv2?.ScrollToEnd();
            sv3?.ScrollToEnd();
        });
    }

    private void SetupFloatingBarDraggable()
    {
        var gripBar = this.FindControl<Border>("FloatingGripBar");
        var border = this.FindControl<Border>("FloatingQuickControlBorder");
        var actionPanel = this.FindControl<StackPanel>("FloatingActionPanel");
        var scrollPanel = this.FindControl<StackPanel>("FloatingScrollPanel");
        if (border == null) return;

        var transform = border.RenderTransform as TranslateTransform;
        if (transform == null)
        {
            transform = new TranslateTransform();
            border.RenderTransform = transform;
        }

        bool hasMoved = false;

        void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is Button || e.Source is CheckBox || (e.Source is Visual v && (v.FindAncestorOfType<Button>() != null || v.FindAncestorOfType<CheckBox>() != null)))
                return;

            var prop = e.GetCurrentPoint(this).Properties;
            if (prop.IsLeftButtonPressed)
            {
                _isFloatingBarDragging = true;
                hasMoved = false;
                _floatingDragStartPointer = e.GetPosition(this);
                _floatingStartTransformX = transform.X;
                _floatingStartTransformY = transform.Y;
                e.Pointer.Capture(sender as IInputElement ?? border);
                e.Handled = true;
            }
        }

        void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isFloatingBarDragging) return;

            var currentPoint = e.GetPosition(this);
            double deltaX = currentPoint.X - _floatingDragStartPointer.X;
            double deltaY = currentPoint.Y - _floatingDragStartPointer.Y;

            if (Math.Abs(deltaX) > 6 || Math.Abs(deltaY) > 6)
            {
                hasMoved = true;
            }

            double minX = -20;
            double maxX = Math.Max(0, Bounds.Width - 70);
            double minY = -Math.Max(0, Bounds.Height - 70);
            double maxY = 20;

            transform.X = Math.Clamp(_floatingStartTransformX + deltaX, minX, maxX);
            transform.Y = Math.Clamp(_floatingStartTransformY + deltaY, minY, maxY);
            e.Handled = true;
        }

        void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_isFloatingBarDragging)
            {
                _isFloatingBarDragging = false;
                e.Pointer.Capture(null);
                e.Handled = true;

                if (!hasMoved)
                {
                    _isFloatingBarCollapsed = !_isFloatingBarCollapsed;
                    if (actionPanel != null) actionPanel.IsVisible = !_isFloatingBarCollapsed;
                    if (scrollPanel != null) scrollPanel.IsVisible = true;
                    border.Padding = _isFloatingBarCollapsed ? new Thickness(3, 4) : new Thickness(4, 5);
                    border.CornerRadius = new CornerRadius(16);
                }
            }
        }

        void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            _isFloatingBarDragging = false;
        }

        if (gripBar != null)
        {
            gripBar.PointerPressed += OnPointerPressed;
            gripBar.PointerMoved += OnPointerMoved;
            gripBar.PointerReleased += OnPointerReleased;
            gripBar.PointerCaptureLost += OnPointerCaptureLost;
        }

        border.PointerPressed += OnPointerPressed;
        border.PointerMoved += OnPointerMoved;
        border.PointerReleased += OnPointerReleased;
        border.PointerCaptureLost += OnPointerCaptureLost;
    }

    private void DetachedQueueWindow_PointerWheelChanged(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & Avalonia.Input.KeyModifiers.Control) != 0)
        {
            if (DataContext is MainViewModel vm)
            {
                if (e.Delta.Y > 0)
                {
                    vm.ZoomInCommand.Execute(null);
                }
                else if (e.Delta.Y < 0)
                {
                    vm.ZoomOutCommand.Execute(null);
                }
                e.Handled = true;
            }
        }
    }

    private void DetachedQueueWindow_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if ((e.KeyModifiers & Avalonia.Input.KeyModifiers.Control) != 0)
        {
            if (DataContext is MainViewModel vm)
            {
                if (e.Key == Avalonia.Input.Key.OemPlus || e.Key == Avalonia.Input.Key.Add)
                {
                    vm.ZoomInCommand.Execute(null);
                    e.Handled = true;
                }
                else if (e.Key == Avalonia.Input.Key.OemMinus || e.Key == Avalonia.Input.Key.Subtract)
                {
                    vm.ZoomOutCommand.Execute(null);
                    e.Handled = true;
                }
                else if (e.Key == Avalonia.Input.Key.D0 || e.Key == Avalonia.Input.Key.NumPad0)
                {
                    vm.ZoomResetCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_subscribedVm != null)
        {
            _subscribedVm.ScrollToTopRequested -= OnScrollToTopRequested;
            _subscribedVm.ScrollToBottomRequested -= OnScrollToBottomRequested;
            _subscribedVm = null;
        }
        if (DataContext is MainViewModel vm)
        {
            vm.IsExternalQueueWindowOpen = false;
        }
    }
}
