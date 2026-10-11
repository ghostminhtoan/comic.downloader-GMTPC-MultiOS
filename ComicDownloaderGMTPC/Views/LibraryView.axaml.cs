using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.ViewModels;

namespace ComicDownloaderGMTPC.Views;

public partial class LibraryView : UserControl
{
    private Point _inlinePressPoint;
    private long _inlinePressTick;
    private bool _hasScannedOnce;
    private bool _isInlineProgrammaticScroll;
    private LibraryViewModel? _subscribedLibVm;
    private ComicReaderViewModel? _subscribedInlineReaderVm;

    public LibraryView()
    {
        InitializeComponent();

        AttachedToVisualTree += OnLibraryAttachedToVisualTree;
        DataContextChanged += OnLibraryDataContextChanged;

        AddHandler(ScrollViewer.ScrollChangedEvent, OnInlineReaderScrollChanged, RoutingStrategies.Bubble);

        var inlineViewport = this.FindControl<Panel>("InlineReaderViewportPanel");
        if (inlineViewport != null)
        {
            inlineViewport.AddHandler(PointerPressedEvent, OnInlineViewportPressed, RoutingStrategies.Tunnel);
            inlineViewport.AddHandler(PointerReleasedEvent, OnInlineViewportReleased, RoutingStrategies.Tunnel);
        }
    }

    private void OnLibraryDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedLibVm != null)
        {
            _subscribedLibVm.PropertyChanged -= OnLibVmPropertyChanged;
            _subscribedLibVm = null;
        }

        if (DataContext is LibraryViewModel vm)
        {
            _subscribedLibVm = vm;
            vm.PropertyChanged += OnLibVmPropertyChanged;
            AttachInlineReaderVm(vm.InlineReaderVm);
        }
        else
        {
            AttachInlineReaderVm(null);
        }
    }

    private void OnLibVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.InlineReaderVm) && DataContext is LibraryViewModel vm)
        {
            AttachInlineReaderVm(vm.InlineReaderVm);
        }
    }

    private void AttachInlineReaderVm(ComicReaderViewModel? readerVm)
    {
        if (_subscribedInlineReaderVm != null)
        {
            _subscribedInlineReaderVm.ScrollToPageRequested -= OnInlineVmScrollToPageRequested;
            _subscribedInlineReaderVm = null;
        }

        if (readerVm != null)
        {
            _subscribedInlineReaderVm = readerVm;
            readerVm.ScrollToPageRequested += OnInlineVmScrollToPageRequested;
        }
    }

    private void OnInlineVmScrollToPageRequested(int pageIndex)
    {
        var readerVm = _subscribedInlineReaderVm;
        if (readerVm == null || pageIndex < 0 || pageIndex >= readerVm.Pages.Count) return;

        var listBox = this.FindControl<ListBox>("InlineReaderPagesListBox");
        if (listBox == null) return;

        _isInlineProgrammaticScroll = true;
        try
        {
            listBox.ScrollIntoView(readerVm.Pages[pageIndex]);
        }
        catch { }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (pageIndex >= 0 && pageIndex < readerVm.Pages.Count)
                {
                    listBox.ScrollIntoView(readerVm.Pages[pageIndex]);
                }
            }
            catch { }
            finally
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => _isInlineProgrammaticScroll = false,
                    Avalonia.Threading.DispatcherPriority.Background);
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnInlineReaderScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isInlineProgrammaticScroll) return;
        var readerVm = _subscribedInlineReaderVm;
        if (readerVm == null || !readerVm.IsScrollMode || readerVm.Pages.Count == 0) return;

        var listBox = this.FindControl<ListBox>("InlineReaderPagesListBox");
        if (listBox == null) return;

        double viewportCenterY = listBox.Bounds.Height * 0.45;
        int bestPageIndex = -1;
        double bestDist = double.MaxValue;

        foreach (var child in listBox.GetVisualDescendants())
        {
            if (child is ListBoxItem container && container.DataContext is ReaderPageItem page)
            {
                var pos = container.TranslatePoint(new Point(0, 0), listBox);
                if (pos.HasValue)
                {
                    double top = pos.Value.Y;
                    double bottom = top + container.Bounds.Height;
                    if (top <= viewportCenterY && bottom >= viewportCenterY)
                    {
                        bestPageIndex = page.Index;
                        break;
                    }

                    double mid = (top + bottom) * 0.5;
                    double dist = Math.Abs(mid - viewportCenterY);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestPageIndex = page.Index;
                    }
                }
            }
        }

        if (bestPageIndex >= 0)
        {
            readerVm.UpdatePageIndexFromScroll(bestPageIndex);
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

