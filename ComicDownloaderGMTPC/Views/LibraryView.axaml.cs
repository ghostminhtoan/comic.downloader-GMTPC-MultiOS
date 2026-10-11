using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.ViewModels;

namespace ComicDownloaderGMTPC.Views;

public partial class LibraryView : UserControl
{
    private Point _inlinePressPoint;
    private long _inlinePressTick;
    private bool _hasScannedOnce;

    public LibraryView()
    {
        InitializeComponent();

        AttachedToVisualTree += OnLibraryAttachedToVisualTree;

        var inlineViewport = this.FindControl<Panel>("InlineReaderViewportPanel");
        if (inlineViewport != null)
        {
            inlineViewport.AddHandler(PointerPressedEvent, OnInlineViewportPressed, RoutingStrategies.Tunnel);
            inlineViewport.AddHandler(PointerReleasedEvent, OnInlineViewportReleased, RoutingStrategies.Tunnel);
        }
    }

    private void OnLibraryAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (!_hasScannedOnce && DataContext is LibraryViewModel vm && vm.AllComics.Count == 0 && !vm.IsScanning)
        {
            _hasScannedOnce = true;
            _ = vm.ScanLibraryAsync();
        }
    }

    private void OnInlinePageContainerAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control ctrl &&
            ctrl.DataContext is ReaderPageItem page &&
            DataContext is LibraryViewModel libVm &&
            libVm.InlineReaderVm != null)
        {
            _ = libVm.InlineReaderVm.EnsureSinglePageLoadedAsync(page);
        }
    }

    private void OnInlineViewportPressed(object? sender, PointerPressedEventArgs e)
    {
        _inlinePressPoint = e.GetPosition(this);
        _inlinePressTick = Environment.TickCount64;
    }

    private void OnInlineViewportReleased(object? sender, PointerReleasedEventArgs e)
    {
        long elapsed = Environment.TickCount64 - _inlinePressTick;
        Point release = e.GetPosition(this);
        double dx = release.X - _inlinePressPoint.X;
        double dy = release.Y - _inlinePressPoint.Y;
        if (Math.Sqrt(dx * dx + dy * dy) > 14.0 || elapsed > 450) return;

        if (DataContext is LibraryViewModel libVm && libVm.InlineReaderVm is ComicReaderViewModel readerVm)
        {
            double w = Math.Max(1.0, Bounds.Width);
            double ratio = release.X / w;
            if (readerVm.IsScrollMode)
            {
                if (ratio >= 0.15 && ratio <= 0.85) readerVm.ToggleOverlay();
            }
            else
            {
                if (ratio < 0.32) readerVm.TapLeftZone();
                else if (ratio > 0.68) readerVm.TapRightZone();
                else readerVm.ToggleOverlay();
            }
        }
    }
}

