using System;
using Avalonia.Controls;
using ComicDownloaderGMTPC.ViewModels;

namespace ComicDownloaderGMTPC.Views;

public partial class DetachedQueueWindow : Window
{
    public DetachedQueueWindow()
    {
        InitializeComponent();
        AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, DetachedQueueWindow_PointerWheelChanged, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, DetachedQueueWindow_KeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
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
        if (DataContext is MainViewModel vm)
        {
            vm.IsExternalQueueWindowOpen = false;
        }
    }
}
