using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.Services;
using ComicDownloaderGMTPC.ViewModels;

namespace ComicDownloaderGMTPC.Views;

public partial class ComicReaderControl : UserControl
{
    private ComicReaderViewModel? _subscribedVm;
    private Point _pointerPressPoint;
    private long _pointerPressTick;
    private bool _isPointerTapCandidate;
    private bool _isProgrammaticScroll;
    private bool _isUserDraggingScrubber;
    private bool _isDoubleTapZooming;
    private int _scrollRequestVersion;
    private long _lastUserScrollInputTick;
    private long _suppressScrollChangedUntilTick;

    // Trạng thái Hold & Drag để Scroll (Pan-Drag Engine mượt mà mọi vùng)
    private Point _panStartPoint;
    private Point _panLastPoint;
    private long _panStartTick;
    private long _lastPanMoveTick;
    private bool _isPanHolding;
    private bool _isPanDragging;
    private double _panVelocityX;
    private double _panVelocityY;
    private double _kineticVelocityX;
    private double _kineticVelocityY;
    private readonly DispatcherTimer _kineticTimer;
    private readonly System.Diagnostics.Stopwatch _kineticStopwatch = new();


    // Trạng thái kéo vượt biên để chuyển chương liền mạch (Continuous Chapters Mode)
    private double _cumulativeOverscrollY = 0;
    private double _cumulativeOverscrollX = 0;
    private long _lastAutoChapterTransitionTick = 0;

    private ScrollViewer? _cachedReaderScrollViewer;
    private ScrollViewer? _cachedFlipScrollViewer;

    // Trạng thái vuốt ngang (Swipe) trên thanh công cụ Button 1-5 ↔ Button 6-10
    private Point _toolbarSwipeStartPoint;
    private bool _isToolbarSwipeTracking;
    private bool _toolbarSwipeTriggered;

    // Trạng thái kéo trực tiếp khung cắt lề Margin Crop (Button 4)
    private Point _cropDragLastPoint;
    private bool _isCropDragging;
    private double _cropStartXRatio;
    private double _cropStartYRatio;

    // Trạng thái gõ đúp (Double-Tap Smart Zoom) và Single-Tap delay
    private Point _lastTapPoint;
    private long _lastTapTick;
    private readonly DispatcherTimer _singleTapTimer;

    // Theo dõi con trỏ đa điểm chạm (Multi-touch Tracking) cho Pinch-to-zoom chuẩn ComicScreen / MX Player
    // Theo dõi con trỏ đa điểm chạm (Multi-touch Tracking) cho Pinch-to-zoom chuẩn ComicScreen / MX Player
    private readonly System.Collections.Generic.Dictionary<int, Point> _activePointers = new();
    private readonly System.Collections.Generic.Dictionary<int, long> _pointerLastSeenTicks = new();
    private bool _isPinching;
    private double _pinchStartDistance;
    private double _pinchStartZoom;
    private Vector _pinchStartOffset;
    private Point _pinchStartPivot;
    private Point _pinchPivotInLb;
    private long _lastPinchEndTick;
    private long _pinchCooldownUntilTick;
    private int _pinchTargetPageIndex = -1;
    private double _pinchTargetNormX = 0.5;
    private double _pinchTargetNormY = 0.5;
    public int PinchTargetPageIndex => _pinchTargetPageIndex;
    public double PinchTargetNormX => _pinchTargetNormX;
    public double PinchTargetNormY => _pinchTargetNormY;
    public bool IsPanDragging => _isPanDragging;
    public bool IsPanHolding => _isPanHolding;
    public int ActivePointersCount => _activePointers.Count;
    private double _pinchTargetOffsetX;
    private double _pinchTargetOffsetY;

    /// <summary>
    /// Tìm chính xác trang mà điểm chạm (ngón tay) đang nằm lên trong bất kỳ chế độ đọc nào (Cuộn dọc, Cuộn ngang, Lật trang).
    /// Giúp app nhận diện được ngón tay đang ở trang nào để focus Pinch & Zoom ở riêng trang đó và khóa cứng chống tua trang!
    /// </summary>
    public int FindPageIndexAtPoint(Point ptInLb, out double pageTopInLb, out double pageHeight)
    {
        return FindPageIndexAtPoint(ptInLb, out pageTopInLb, out pageHeight, out _, out _);
    }

    public int FindPageIndexAtPoint(Point ptInLb, out double pageTopInLb, out double pageHeight, out double pageLeftInLb, out double pageWidth)
    {
        pageTopInLb = 0;
        pageHeight = Bounds.Height > 0 ? Bounds.Height : 600.0;
        pageLeftInLb = 0;
        pageWidth = Bounds.Width > 0 ? Bounds.Width : 400.0;
        if (DataContext is not ComicReaderViewModel vm || vm.Pages.Count == 0) return -1;

        if (vm.IsScrollMode)
        {
            var lb = this.FindControl<ListBox>("ReaderPagesListBox");
            var sv = GetReaderScrollViewer();

            if (!vm.IsHorizontalScrollMode)
            {
                // A. CUỘN DỌC (VERTICAL SCROLL - LTR / RTL):
                if (lb != null)
                {
                    foreach (var child in lb.GetVisualDescendants())
                    {
                        if (child is ListBoxItem container && container.DataContext is ReaderPageItem page)
                        {
                            var pos = container.TranslatePoint(new Point(0, 0), lb);
                            if (pos.HasValue)
                            {
                                double top = pos.Value.Y;
                                double bottom = top + container.Bounds.Height;
                                if (ptInLb.Y >= top && ptInLb.Y <= bottom)
                                {
                                    pageTopInLb = top;
                                    pageHeight = Math.Max(1.0, container.Bounds.Height);
                                    pageLeftInLb = pos.Value.X;
                                    pageWidth = Math.Max(1.0, container.Bounds.Width);
                                    return page.Index;
                                }
                            }
                        }
                    }

                    // Nếu chạm mép lề giữa các trang: tìm trang có tâm Y gần nhất
                    double minDist = double.MaxValue;
                    int bestIndex = -1;
                    foreach (var child in lb.GetVisualDescendants())
                    {
                        if (child is ListBoxItem container && container.DataContext is ReaderPageItem page)
                        {
                            var pos = container.TranslatePoint(new Point(0, 0), lb);
                            if (pos.HasValue)
                            {
                                double mid = pos.Value.Y + container.Bounds.Height * 0.5;
                                double dist = Math.Abs(mid - ptInLb.Y);
                                if (dist < minDist)
                                {
                                    minDist = dist;
                                    bestIndex = page.Index;
                                    pageTopInLb = pos.Value.Y;
                                    pageHeight = Math.Max(1.0, container.Bounds.Height);
                                    pageLeftInLb = pos.Value.X;
                                    pageWidth = Math.Max(1.0, container.Bounds.Width);
                                }
                            }
                        }
                    }
                    if (bestIndex >= 0) return bestIndex;
                }

                // Fallback tính toán Document Y từ sv.Offset:
                if (sv != null)
                {
                    double docY = sv.Offset.Y + ptInLb.Y;
                    double accY = 0;
                    for (int i = 0; i < vm.Pages.Count; i++)
                    {
                        var p = vm.Pages[i];
                        double totalItemH = p.DisplayHeight + p.PageMargin.Top + p.PageMargin.Bottom;
                        if (docY >= accY && docY <= accY + totalItemH)
                        {
                            pageTopInLb = accY + p.PageMargin.Top - sv.Offset.Y;
                            pageHeight = Math.Max(1.0, p.DisplayHeight);
                            pageLeftInLb = p.PageMargin.Left;
                            pageWidth = Math.Max(1.0, p.DisplayWidth);
                            return p.Index;
                        }
                        accY += totalItemH;
                    }
                }
            }
            else
            {
                // B. CUỘN NGANG (HORIZONTAL SCROLL - LTR / RTL):
                if (lb != null)
                {
                    foreach (var child in lb.GetVisualDescendants())
                    {
                        if (child is ListBoxItem container && container.DataContext is ReaderPageItem page)
                        {
                            var pos = container.TranslatePoint(new Point(0, 0), lb);
                            if (pos.HasValue)
                            {
                                double left = pos.Value.X;
                                double right = left + container.Bounds.Width;
                                if (ptInLb.X >= left && ptInLb.X <= right)
                                {
                                    pageLeftInLb = left;
                                    pageWidth = Math.Max(1.0, container.Bounds.Width);
                                    pageTopInLb = pos.Value.Y;
                                    pageHeight = Math.Max(1.0, container.Bounds.Height);
                                    return page.Index;
                                }
                            }
                        }
                    }

                    // Nếu chạm mép lề giữa các trang: tìm trang có tâm X gần nhất
                    double minDist = double.MaxValue;
                    int bestIndex = -1;
                    foreach (var child in lb.GetVisualDescendants())
                    {
                        if (child is ListBoxItem container && container.DataContext is ReaderPageItem page)
                        {
                            var pos = container.TranslatePoint(new Point(0, 0), lb);
                            if (pos.HasValue)
                            {
                                double mid = pos.Value.X + container.Bounds.Width * 0.5;
                                double dist = Math.Abs(mid - ptInLb.X);
                                if (dist < minDist)
                                {
                                    minDist = dist;
                                    bestIndex = page.Index;
                                    pageLeftInLb = pos.Value.X;
                                    pageWidth = Math.Max(1.0, container.Bounds.Width);
                                    pageTopInLb = pos.Value.Y;
                                    pageHeight = Math.Max(1.0, container.Bounds.Height);
                                }
                            }
                        }
                    }
                    if (bestIndex >= 0) return bestIndex;
                }

                // Fallback tính toán Document X từ sv.Offset:
                if (sv != null)
                {
                    if (!vm.IsRightToLeft)
                    {
                        double docX = sv.Offset.X + ptInLb.X;
                        double accX = 0;
                        for (int i = 0; i < vm.Pages.Count; i++)
                        {
                            var p = vm.Pages[i];
                            double totalItemW = p.DisplayWidth + p.PageMargin.Left + p.PageMargin.Right;
                            if (docX >= accX && docX <= accX + totalItemW)
                            {
                                pageLeftInLb = accX + p.PageMargin.Left - sv.Offset.X;
                                pageWidth = Math.Max(1.0, p.DisplayWidth);
                                pageTopInLb = p.PageMargin.Top;
                                pageHeight = Math.Max(1.0, p.DisplayHeight);
                                return p.Index;
                            }
                            accX += totalItemW;
                        }
                    }
                    else
                    {
                        double maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
                        double docFromRight = (maxX - sv.Offset.X) + (sv.Viewport.Width - ptInLb.X);
                        double accX = 0;
                        for (int i = 0; i < vm.Pages.Count; i++)
                        {
                            var p = vm.Pages[i];
                            double totalItemW = p.DisplayWidth + p.PageMargin.Left + p.PageMargin.Right;
                            if (docFromRight >= accX && docFromRight <= accX + totalItemW)
                            {
                                pageLeftInLb = ptInLb.X - (docFromRight - accX);
                                pageWidth = Math.Max(1.0, p.DisplayWidth);
                                pageTopInLb = p.PageMargin.Top;
                                pageHeight = Math.Max(1.0, p.DisplayHeight);
                                return p.Index;
                            }
                            accX += totalItemW;
                        }
                    }
                }
            }
        }
        else if (vm.IsFlipMode)
        {
            // C. LẬT TRANG (FLIP MODE - 1 TRANG HOẶC 2 TRANG):
            if (vm.HasSecondaryPage)
            {
                double midX = Bounds.Width > 0 ? Bounds.Width * 0.5 : 400.0;
                if (ptInLb.X < midX)
                {
                    var pLeft = vm.LeftFlipPage ?? (vm.Pages.Count > vm.CurrentPageIndex ? vm.Pages[vm.CurrentPageIndex] : null);
                    if (pLeft != null)
                    {
                        pageLeftInLb = 0;
                        pageWidth = midX;
                        pageTopInLb = 0;
                        pageHeight = Bounds.Height;
                        return pLeft.Index;
                    }
                }
                else
                {
                    var pRight = vm.RightFlipPage ?? (vm.Pages.Count > vm.CurrentPageIndex ? vm.Pages[vm.CurrentPageIndex] : null);
                    if (pRight != null)
                    {
                        pageLeftInLb = midX;
                        pageWidth = midX;
                        pageTopInLb = 0;
                        pageHeight = Bounds.Height;
                        return pRight.Index;
                    }
                }
            }
            else
            {
                var p = vm.LeftFlipPage ?? (vm.Pages.Count > vm.CurrentPageIndex ? vm.Pages[vm.CurrentPageIndex] : null);
                if (p != null)
                {
                    pageLeftInLb = 0;
                    pageWidth = Bounds.Width;
                    pageTopInLb = 0;
                    pageHeight = Bounds.Height;
                    return p.Index;
                }
            }
        }

        return Math.Clamp(vm.CurrentPageIndex, 0, Math.Max(0, vm.Pages.Count - 1));
    }

    private void StartPinchGesture(Point p1, Point p2)
    {
        StopKineticScroll();
        if (DataContext is not ComicReaderViewModel vm) return;

        double dist = Distance(p1, p2);
        _pinchStartDistance = Math.Max(10.0, dist);
        _pinchStartZoom = vm.ZoomLevel;

        var sv = vm.IsScrollMode ? GetReaderScrollViewer() : GetFlipScrollViewer();
        _pinchStartOffset = sv?.Offset ?? Vector.Zero;
        _pinchTargetOffsetX = _pinchStartOffset.X;
        _pinchTargetOffsetY = _pinchStartOffset.Y;

        Point currentPivot = new Point((p1.X + p2.X) * 0.5, (p1.Y + p2.Y) * 0.5);
        _pinchStartPivot = currentPivot;

        Point inLb = currentPivot;
        var lb = this.FindControl<ListBox>("ReaderPagesListBox");
        if (lb != null)
        {
            var pt = this.TranslatePoint(currentPivot, lb);
            if (pt.HasValue) inLb = pt.Value;
        }
        _pinchPivotInLb = inLb;

        double pageTopInLb = 0;
        double pageHeight = 0;
        double pageLeftInLb = 0;
        double pageWidth = 0;

        // BẮT CHÍNH XÁC VỊ TRÍ NGÓN TAY ĐẦU TIÊN HOẶC CẢ 2 NGÓN TAY ĐANG CHẠM TRANG NÀO:
        Point pressPtInLb = _pointerPressPoint;
        if (lb != null)
        {
            var tr = this.TranslatePoint(_pointerPressPoint, lb);
            if (tr.HasValue) pressPtInLb = tr.Value;
        }

        int targetFromPivot = FindPageIndexAtPoint(_pinchPivotInLb, out double pTopPivot, out double pHPivot, out double pLeftPivot, out double pWPivot);
        int targetFromPress = FindPageIndexAtPoint(pressPtInLb, out double pTopPress, out double pHPress, out double pLeftPress, out double pWPress);

        if (targetFromPivot >= 0)
        {
            _pinchTargetPageIndex = targetFromPivot;
            pageTopInLb = pTopPivot;
            pageHeight = pHPivot;
            pageLeftInLb = pLeftPivot;
            pageWidth = pWPivot;
        }
        else if (targetFromPress >= 0)
        {
            _pinchTargetPageIndex = targetFromPress;
            pageTopInLb = pTopPress;
            pageHeight = pHPress;
            pageLeftInLb = pLeftPress;
            pageWidth = pWPress;
        }
        else
        {
            _pinchTargetPageIndex = Math.Clamp(vm.CurrentPageIndex, 0, Math.Max(0, vm.Pages.Count - 1));
        }

        if (pageHeight > 1.0)
        {
            _pinchTargetNormY = Math.Clamp((_pinchPivotInLb.Y - pageTopInLb) / pageHeight, 0.0, 1.0);
        }
        else
        {
            _pinchTargetNormY = 0.5;
        }

        if (pageWidth > 1.0)
        {
            _pinchTargetNormX = Math.Clamp((_pinchPivotInLb.X - pageLeftInLb) / pageWidth, 0.0, 1.0);
        }
        else
        {
            _pinchTargetNormX = 0.5;
        }

        // Khóa cứng trang đang đọc vào chính trang mà ngón tay đang chạm lên:
        if (_pinchTargetPageIndex >= 0 && _pinchTargetPageIndex < vm.Pages.Count)
        {
            vm.CurrentPageIndex = _pinchTargetPageIndex;
        }

        _isPinching = true;
        _isPanHolding = false;
        _isPanDragging = false;
        _isPointerTapCandidate = false;
        _singleTapTimer.Stop();
        _lastTapTick = 0;
        _suppressScrollChangedUntilTick = Environment.TickCount64 + 1000;
    }

    private void UpdatePinchGesture(Point p1, Point p2)
    {
        if (DataContext is not ComicReaderViewModel vm || !_isPinching) return;

        double currentDist = Distance(p1, p2);
        if (_pinchStartDistance <= 5.0 || currentDist <= 5.0) return;

        // Áp dụng hệ số làm mượt Damped Pinch Scale (hàm mũ 0.45):
        // Giảm tốc độ phóng to / thu nhỏ quá mức, mang lại cảm giác zoom đầm chắc, kiểm soát vi điểm giống ComicScreen
        double rawScale = currentDist / _pinchStartDistance;
        double dampedScale = Math.Pow(rawScale, 0.45);
        double newZoom = Math.Clamp(Math.Round(_pinchStartZoom * dampedScale, 3), 0.4, 4.0);

        // Cập nhật mức Zoom (kích hoạt ApplyFitModeLayout tính lại DisplayWidth / DisplayHeight cho mọi trang)
        vm.ZoomLevel = newZoom;

        // KHÓA HOÀN TOÀN THAO TÁC DRAG KHI 2 NGÓN TAY ĐANG THAO TÁC PINCH:
        // Cố định điểm neo theo _pinchPivotInLb / _pinchStartPivot ban đầu lúc bắt đầu chạm,
        // tuyệt đối KHÔNG cho phép rê/trượt ảnh theo chuyển động tay khi cả 2 ngón đang pinch!
        if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
        {
            var sv = GetReaderScrollViewer();
            if (sv != null && _pinchTargetPageIndex >= 0 && _pinchTargetPageIndex < vm.Pages.Count)
            {
                // 1. Tọa độ Document Y tuyệt đối của đỉnh trang mục tiêu ở mức zoom mới
                double targetPageDocY = 0;
                for (int i = 0; i < _pinchTargetPageIndex; i++)
                {
                    var p = vm.Pages[i];
                    targetPageDocY += p.DisplayHeight + p.PageMargin.Top + p.PageMargin.Bottom;
                }

                var targetPage = vm.Pages[_pinchTargetPageIndex];
                double targetPageHeight = targetPage.DisplayHeight;

                // 2. Tọa độ điểm neo bên trong trang mục tiêu
                double anchorPointInDocY = targetPageDocY + (_pinchTargetNormY * targetPageHeight);

                // 3. Offset Y mới để điểm neo trên trang luôn nằm chính xác tại vị trí pivot ban đầu (_pinchPivotInLb.Y)
                double newOffsetY = anchorPointInDocY - _pinchPivotInLb.Y;

                // 4. Tính toán Extent thực tế của toàn bộ truyện ở mức zoom mới (tránh bị kẹp vào Extent cũ chưa kịp cập nhật)
                double totalDocHeight = 0;
                for (int i = 0; i < vm.Pages.Count; i++)
                {
                    var p = vm.Pages[i];
                    totalDocHeight += p.DisplayHeight + p.PageMargin.Top + p.PageMargin.Bottom;
                }
                double maxOffsetY = Math.Max(0, totalDocHeight - sv.Viewport.Height);
                newOffsetY = Math.Clamp(newOffsetY, 0, maxOffsetY);

                // 5. Tính toán Offset X cố định theo điểm chạm ban đầu _pinchPivotInLb.X
                double targetPageWidth = targetPage.DisplayWidth;
                double newOffsetX = 0;
                if (targetPageWidth > sv.Viewport.Width)
                {
                    double anchorPointInDocX = _pinchTargetNormX * targetPageWidth;
                    newOffsetX = anchorPointInDocX - _pinchPivotInLb.X;
                    double maxOffsetX = Math.Max(0, targetPageWidth - sv.Viewport.Width);
                    newOffsetX = Math.Clamp(newOffsetX, 0, maxOffsetX);
                }

                _pinchTargetOffsetX = newOffsetX;
                _pinchTargetOffsetY = newOffsetY;

                _isProgrammaticScroll = true;
                sv.Offset = new Vector(newOffsetX, newOffsetY);
                _isProgrammaticScroll = false;
            }

            // Khóa cứng trang đọc hiện tại, chống tua trang trong suốt cử chỉ Pinch
            if (_pinchTargetPageIndex >= 0 && _pinchTargetPageIndex < vm.Pages.Count)
            {
                vm.CurrentPageIndex = _pinchTargetPageIndex;
            }
            _suppressScrollChangedUntilTick = Environment.TickCount64 + 1000;
        }
        else if (vm.IsScrollMode && vm.IsHorizontalScrollMode)
        {
            var sv = GetReaderScrollViewer();
            if (sv != null && _pinchTargetPageIndex >= 0 && _pinchTargetPageIndex < vm.Pages.Count)
            {
                double accX = 0;
                for (int i = 0; i < _pinchTargetPageIndex; i++)
                {
                    var p = vm.Pages[i];
                    accX += p.DisplayWidth + p.PageMargin.Left + p.PageMargin.Right;
                }

                var targetPage = vm.Pages[_pinchTargetPageIndex];
                double targetPageWidth = targetPage.DisplayWidth;
                double totalDocWidth = 0;
                for (int i = 0; i < vm.Pages.Count; i++)
                {
                    var p = vm.Pages[i];
                    totalDocWidth += p.DisplayWidth + p.PageMargin.Left + p.PageMargin.Right;
                }
                double maxX = Math.Max(0, totalDocWidth - sv.Viewport.Width);

                double newOffsetX = 0;
                if (!vm.IsRightToLeft)
                {
                    double anchorPointInDocX = accX + (_pinchTargetNormX * targetPageWidth);
                    newOffsetX = anchorPointInDocX - _pinchPivotInLb.X;
                }
                else
                {
                    double anchorPointInDocX = (maxX - accX) + ((1.0 - _pinchTargetNormX) * targetPageWidth);
                    newOffsetX = anchorPointInDocX - _pinchPivotInLb.X;
                }
                newOffsetX = Math.Clamp(newOffsetX, 0, maxX);

                _pinchTargetOffsetX = newOffsetX;
                _isProgrammaticScroll = true;
                sv.Offset = new Vector(newOffsetX, sv.Offset.Y);
                _isProgrammaticScroll = false;
            }

            if (_pinchTargetPageIndex >= 0 && _pinchTargetPageIndex < vm.Pages.Count)
            {
                vm.CurrentPageIndex = _pinchTargetPageIndex;
            }
            _suppressScrollChangedUntilTick = Environment.TickCount64 + 1000;
        }
        else
        {
            var flipSv = GetFlipScrollViewer();
            if (flipSv != null)
            {
                double ratio = (_pinchStartZoom > 0.05) ? (newZoom / _pinchStartZoom) : 1.0;
                // Neo cố định theo _pinchStartPivot ban đầu, không cho rê theo ngón tay khi 2 ngón đang pinch:
                double newTargetX = (_pinchStartOffset.X + _pinchStartPivot.X) * ratio - _pinchStartPivot.X;
                double newTargetY = (_pinchStartOffset.Y + _pinchStartPivot.Y) * ratio - _pinchStartPivot.Y;
                double maxX = Math.Max(0, flipSv.Extent.Width - flipSv.Viewport.Width);
                double maxY = Math.Max(0, flipSv.Extent.Height - flipSv.Viewport.Height);
                flipSv.Offset = new Vector(Math.Clamp(newTargetX, 0, maxX), Math.Clamp(newTargetY, 0, maxY));
            }

            if (_pinchTargetPageIndex >= 0 && _pinchTargetPageIndex < vm.Pages.Count)
            {
                vm.CurrentPageIndex = _pinchTargetPageIndex;
            }
            _suppressScrollChangedUntilTick = Environment.TickCount64 + 1000;
        }
    }

    public void PerformPinchZoomStep(Point p1, Point p2)
    {
        if (!_isPinching)
        {
            StartPinchGesture(p1, p2);
        }
        else
        {
            UpdatePinchGesture(p1, p2);
        }
    }

    public bool IsPinching => _isPinching;

    public void EndPinchZoom()
    {
        _isPinching = false;
        _lastPinchEndTick = Environment.TickCount64;
        _pinchCooldownUntilTick = Environment.TickCount64 + 400;
        _suppressScrollChangedUntilTick = Environment.TickCount64 + 500;
        _activePointers.Clear();
        _pointerLastSeenTicks.Clear();
        _isPanHolding = false;
        _isPanDragging = false;
        _isPointerTapCandidate = false;
        StopKineticScroll();
        if (DataContext is ComicReaderViewModel vm && _pinchTargetPageIndex >= 0 && vm.Pages.Count > _pinchTargetPageIndex)
        {
            vm.CurrentPageIndex = _pinchTargetPageIndex;
        }
    }

    /// <summary>
    /// Thu phóng có neo vị trí đọc (Zoom with Anchor):
    /// Giữ nguyên trang và tâm màn hình hiện tại khi zoom bằng phím tắt Ctrl +/- hoặc phím tắt tùy chỉnh,
    /// loại bỏ hoàn toàn hiện tượng vừa zoom vừa bị tua trang lùi/tới.
    /// </summary>
    public void ZoomWithAnchor(bool zoomIn)
    {
        if (DataContext is not ComicReaderViewModel vm || vm.Pages.Count == 0) return;

        double oldZoom = vm.ZoomLevel;
        double newZoom = Math.Clamp(Math.Round(zoomIn ? oldZoom + 0.15 : oldZoom - 0.15, 2), 0.4, 3.0);
        if (Math.Abs(newZoom - oldZoom) < 0.005) return;

        int savedPageIndex = Math.Clamp(vm.CurrentPageIndex, 0, vm.Pages.Count - 1);
        double ratio = newZoom / Math.Max(0.1, oldZoom);

        _isProgrammaticScroll = true;
        _suppressScrollChangedUntilTick = Environment.TickCount64 + 400;

        if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
        {
            var sv = GetReaderScrollViewer();
            if (sv != null)
            {
                double anchorViewportY = sv.Viewport.Height > 0 ? sv.Viewport.Height * 0.5 : 300.0;
                double currentDocY = sv.Offset.Y + anchorViewportY;
                double targetDocY = currentDocY * ratio;
                double targetOffsetY = targetDocY - anchorViewportY;

                vm.ZoomLevel = newZoom;

                double totalDocHeight = 0;
                for (int i = 0; i < vm.Pages.Count; i++)
                {
                    var p = vm.Pages[i];
                    totalDocHeight += p.DisplayHeight + p.PageMargin.Top + p.PageMargin.Bottom;
                }
                double maxOffsetY = Math.Max(0, totalDocHeight - sv.Viewport.Height);
                sv.Offset = new Vector(sv.Offset.X, Math.Clamp(targetOffsetY, 0, maxOffsetY));
            }
            else
            {
                vm.ZoomLevel = newZoom;
            }
        }
        else if (vm.IsScrollMode && vm.IsHorizontalScrollMode)
        {
            var sv = GetReaderScrollViewer();
            if (sv != null)
            {
                double anchorViewportX = sv.Viewport.Width > 0 ? sv.Viewport.Width * 0.5 : 400.0;
                double currentDocX = sv.Offset.X + anchorViewportX;
                double targetDocX = currentDocX * ratio;
                double targetOffsetX = targetDocX - anchorViewportX;

                vm.ZoomLevel = newZoom;

                double totalDocWidth = 0;
                for (int i = 0; i < vm.Pages.Count; i++)
                {
                    var p = vm.Pages[i];
                    totalDocWidth += p.DisplayWidth + p.PageMargin.Left + p.PageMargin.Right;
                }
                double maxOffsetX = Math.Max(0, totalDocWidth - sv.Viewport.Width);
                sv.Offset = new Vector(Math.Clamp(targetOffsetX, 0, maxOffsetX), sv.Offset.Y);
            }
            else
            {
                vm.ZoomLevel = newZoom;
            }
        }
        else
        {
            var flipSv = GetFlipScrollViewer();
            if (flipSv != null)
            {
                double anchorX = flipSv.Viewport.Width * 0.5;
                double anchorY = flipSv.Viewport.Height * 0.5;
                double targetX = (flipSv.Offset.X + anchorX) * ratio - anchorX;
                double targetY = (flipSv.Offset.Y + anchorY) * ratio - anchorY;

                vm.ZoomLevel = newZoom;

                double maxX = Math.Max(0, flipSv.Extent.Width - flipSv.Viewport.Width);
                double maxY = Math.Max(0, flipSv.Extent.Height - flipSv.Viewport.Height);
                flipSv.Offset = new Vector(Math.Clamp(targetX, 0, maxX), Math.Clamp(targetY, 0, maxY));
            }
            else
            {
                vm.ZoomLevel = newZoom;
            }
        }

        if (savedPageIndex >= 0 && savedPageIndex < vm.Pages.Count)
        {
            vm.CurrentPageIndex = savedPageIndex;
        }
        _isProgrammaticScroll = false;
    }

    /// <summary>
    /// Thực hiện thao tác Hold and Drag cuộn trang mượt mà từ startPoint tới endPoint.
    /// Trả về true nếu cuộn thành công và hoàn toàn không bị kích hoạt nhầm sang Pinch Zoom.
    /// </summary>
    public bool PerformHoldAndDrag(Point startPoint, Point endPoint, int stepCount = 5)
    {
        if (DataContext is not ComicReaderViewModel vm) return false;

        // Bắt đầu Drag: đảm bảo sạch sẽ
        _activePointers.Clear();
        _pointerLastSeenTicks.Clear();
        _isPinching = false;

        int pointerId = 999;
        long now = Environment.TickCount64;
        _activePointers[pointerId] = startPoint;
        _pointerLastSeenTicks[pointerId] = now;

        _pointerPressPoint = startPoint;
        _pointerPressTick = now;
        _panStartPoint = startPoint;
        _panLastPoint = startPoint;
        _panStartTick = now;
        _lastPanMoveTick = now;
        _isPanHolding = true;
        _isPanDragging = true;
        _isPointerTapCandidate = false;

        for (int i = 1; i <= stepCount; i++)
        {
            double t = (double)i / stepCount;
            Point curPt = new Point(
                startPoint.X + (endPoint.X - startPoint.X) * t,
                startPoint.Y + (endPoint.Y - startPoint.Y) * t
            );

            double deltaX = curPt.X - _panLastPoint.X;
            double deltaY = curPt.Y - _panLastPoint.Y;

            _panLastPoint = curPt;
            _activePointers[pointerId] = curPt;
            _pointerLastSeenTicks[pointerId] = Environment.TickCount64;

            if (_isPinching)
            {
                // Nếu bị kích hoạt pinch trong lúc drag 1 ngón tay -> Lỗi!
                return false;
            }

            if (vm.IsScrollMode)
            {
                var scrollViewer = GetReaderScrollViewer();
                if (scrollViewer != null)
                {
                    double effectiveDeltaX = (vm.IsHorizontalScrollMode && vm.IsRightToLeft) ? -deltaX : deltaX;
                    double newX = scrollViewer.Offset.X - effectiveDeltaX;
                    double newY = scrollViewer.Offset.Y - deltaY;
                    double maxX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
                    double maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                    scrollViewer.Offset = new Vector(Math.Clamp(newX, 0, maxX), Math.Clamp(newY, 0, maxY));
                }
            }
        }

        // Kết thúc Drag (Release)
        _isPanHolding = false;
        _isPanDragging = false;
        _activePointers.Clear();
        _pointerLastSeenTicks.Clear();

        return !_isPinching;
    }

    private static double Distance(Point p1, Point p2)
    {
        double dx = p1.X - p2.X;
        double dy = p1.Y - p2.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public ComicReaderControl()
    {
        InitializeComponent();

        _kineticTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _kineticTimer.Tick += OnKineticTimerTick;

        _singleTapTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _singleTapTimer.Tick += (s, e) =>
        {
            _singleTapTimer.Stop();
            if (DataContext is ComicReaderViewModel vm)
            {
                var sv = vm.IsScrollMode ? GetReaderScrollViewer() : GetFlipScrollViewer();
                Vector? savedOffset = sv?.Offset;
                int savedPage = vm.CurrentPageIndex;
                _suppressScrollChangedUntilTick = Environment.TickCount64 + 800;

                vm.ToggleOverlay();

                if (sv != null && savedOffset.HasValue)
                {
                    sv.Offset = savedOffset.Value;
                }
                if (savedPage >= 0 && savedPage < vm.Pages.Count)
                {
                    vm.CurrentPageIndex = savedPage;
                }
            }
        };

        DataContextChanged += OnReaderDataContextChanged;

        // Lắng nghe sự kiện cuộn trên ListBox để cập nhật trang đang đọc và lưu tiến độ
        AddHandler(ScrollViewer.ScrollChangedEvent, OnReaderScrollChanged, RoutingStrategies.Bubble);

        var readerListBox = this.FindControl<ListBox>("ReaderPagesListBox");
        if (readerListBox != null)
        {
            readerListBox.SelectionChanged += (s, e) =>
            {
                if (readerListBox.SelectedItem != null)
                {
                    readerListBox.SelectedItem = null;
                }
            };
        }

        // Lắng nghe chạm / click / kéo trên vùng đọc truyện để Hold & Drag Scroll hoặc bật/tắt Overlay kiểu ComicScreen
        var viewport = this.FindControl<Panel>("ReaderViewportPanel");
        if (viewport != null)
        {
            viewport.AddHandler(PointerPressedEvent, OnViewportPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            viewport.AddHandler(PointerMovedEvent, OnViewportPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            viewport.AddHandler(PointerReleasedEvent, OnViewportPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            viewport.AddHandler(PointerCaptureLostEvent, OnViewportPointerCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
            viewport.AddHandler(PointerWheelChangedEvent, OnViewportPointerWheelChanged, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        // Lắng nghe vuốt ngang (Swipe) trên thanh công cụ 10 nút (Button 1-5 ↔ Button 6-10)
        var toolbar = this.FindControl<Border>("SwipeableToolbarBorder");
        if (toolbar != null)
        {
            toolbar.AddHandler(PointerPressedEvent, OnToolbarPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            toolbar.AddHandler(PointerMovedEvent, OnToolbarPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            toolbar.AddHandler(PointerReleasedEvent, OnToolbarPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        // Lắng nghe kéo trực tiếp trên khung cắt lề Margin Crop (Button 4)
        var cropPanel = this.FindControl<Panel>("MarginCropInteractivePanel");
        if (cropPanel != null)
        {
            cropPanel.AddHandler(PointerPressedEvent, OnCropPanelPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            cropPanel.AddHandler(PointerMovedEvent, OnCropPanelPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            cropPanel.AddHandler(PointerReleasedEvent, OnCropPanelPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        // Lắng nghe trực tiếp thao tác kéo thanh trượt trang của người dùng (tránh vòng lặp TwoWay với ScrollViewer)
        var scrubber = this.FindControl<Slider>("PageScrubberSlider");
        if (scrubber != null)
        {
            scrubber.AddHandler(PointerPressedEvent, OnScrubberPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            scrubber.AddHandler(PointerReleasedEvent, OnScrubberPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            scrubber.AddHandler(PointerCaptureLostEvent, OnScrubberPointerCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
            scrubber.PropertyChanged += OnScrubberPropertyChanged;
        }

        AddHandler(KeyDownEvent, OnControlKeyDown, RoutingStrategies.Tunnel);

        Focusable = true;
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => this.Focus(), DispatcherPriority.Input);
        Loaded += (_, _) => Dispatcher.UIThread.Post(() => this.Focus(), DispatcherPriority.Input);
    }

    /// <summary>
    /// Cho phép ComicReaderWindow chuyển tiếp trực tiếp sự kiện phím tắt (Tunnel KeyDown) xuống UserControl
    /// ngay khi vừa mở FullScreen mà người dùng chưa cần click chuột.
    /// </summary>
    public void HandleKeyDownFromHost(KeyEventArgs e) => OnControlKeyDown(this, e);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && DataContext is ComicReaderViewModel vm)
        {
            var b = Bounds;
            if (b.Width > 0 && b.Height > 0)
            {
                int savedPage = vm.CurrentPageIndex;
                _suppressScrollChangedUntilTick = Environment.TickCount64 + 800;
                vm.UpdateViewportDimensions(b.Width, b.Height);
                if (savedPage >= 0 && savedPage < vm.Pages.Count)
                {
                    vm.CurrentPageIndex = savedPage;
                }
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopKineticScroll();
    }

    public ScrollViewer? GetReaderScrollViewer()
    {
        if (_cachedReaderScrollViewer != null)
            return _cachedReaderScrollViewer;
        var listBox = this.FindControl<ListBox>("ReaderPagesListBox");
        _cachedReaderScrollViewer = listBox?.FindDescendantOfType<ScrollViewer>();
        return _cachedReaderScrollViewer;
    }

    private ScrollViewer? GetFlipScrollViewer()
    {
        if (_cachedFlipScrollViewer != null)
            return _cachedFlipScrollViewer;
        _cachedFlipScrollViewer = this.FindControl<ScrollViewer>("FlipModeScrollViewer");
        return _cachedFlipScrollViewer;
    }

    private void StopKineticScroll()
    {
        _kineticTimer.Stop();
        _kineticStopwatch.Reset();
        _kineticVelocityX = 0;
        _kineticVelocityY = 0;
    }

    private void OnKineticTimerTick(object? sender, EventArgs e)
    {
        if (DataContext is not ComicReaderViewModel vm)
        {
            StopKineticScroll();
            return;
        }

        double dt = Math.Clamp(_kineticStopwatch.Elapsed.TotalSeconds, 0.002, 0.05);
        _kineticStopwatch.Restart();

        // Ma sát hãm đà quán tính mượt mà chuẩn 60-120 FPS
        double friction = Math.Pow(0.915, dt / 0.016);
        _kineticVelocityX *= friction;
        _kineticVelocityY *= friction;

        if (Math.Abs(_kineticVelocityX) < 0.025 && Math.Abs(_kineticVelocityY) < 0.025)
        {
            StopKineticScroll();
            return;
        }

        double stepX = _kineticVelocityX * (dt * 1000.0);
        double stepY = _kineticVelocityY * (dt * 1000.0);


        if (vm.IsScrollMode)
        {
            var scrollViewer = GetReaderScrollViewer();
            if (scrollViewer != null)
            {
                double effectiveStepX = (vm.IsHorizontalScrollMode && vm.IsRightToLeft) ? -stepX : stepX;
                double newX = scrollViewer.Offset.X - effectiveStepX;
                double newY = scrollViewer.Offset.Y - stepY;

                double maxX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
                double maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);

                if (newX <= 0 || newX >= maxX) _kineticVelocityX = 0;
                if (newY <= 0 || newY >= maxY) _kineticVelocityY = 0;

                scrollViewer.Offset = new Vector(Math.Clamp(newX, 0, maxX), Math.Clamp(newY, 0, maxY));
            }
            else
            {
                StopKineticScroll();
            }
        }
        else if (vm.IsFlipMode && vm.ZoomLevel > 1.02)
        {
            var flipScrollViewer = GetFlipScrollViewer();
            if (flipScrollViewer != null)
            {
                double newX = flipScrollViewer.Offset.X - stepX;
                double newY = flipScrollViewer.Offset.Y - stepY;

                double maxX = Math.Max(0, flipScrollViewer.Extent.Width - flipScrollViewer.Viewport.Width);
                double maxY = Math.Max(0, flipScrollViewer.Extent.Height - flipScrollViewer.Viewport.Height);

                if (newX <= 0 || newX >= maxX) _kineticVelocityX = 0;
                if (newY <= 0 || newY >= maxY) _kineticVelocityY = 0;

                flipScrollViewer.Offset = new Vector(Math.Clamp(newX, 0, maxX), Math.Clamp(newY, 0, maxY));
            }
            else
            {
                StopKineticScroll();
            }
        }
        else
        {
            StopKineticScroll();
        }
    }

    // =========================================================================
    // XỬ LÝ VUỐT NGANG (SWIPE) TRÊN THANH CÔNG CỤ BUTTON 1-5 ↔ BUTTON 6-10
    // =========================================================================

    private void OnToolbarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _toolbarSwipeStartPoint = e.GetPosition(this);
        _isToolbarSwipeTracking = true;
        _toolbarSwipeTriggered = false;
    }

    private void OnToolbarPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isToolbarSwipeTracking || _toolbarSwipeTriggered) return;

        Point current = e.GetPosition(this);
        double dx = current.X - _toolbarSwipeStartPoint.X;
        double dy = current.Y - _toolbarSwipeStartPoint.Y;

        if (Math.Abs(dx) >= 26.0 && Math.Abs(dx) > Math.Abs(dy) * 1.15)
        {
            _toolbarSwipeTriggered = true;
            if (DataContext is ComicReaderViewModel vm)
            {
                if (dx < 0)
                {
                    // Vuốt sang trái: tiến sang trang sau (1-5 -> 6-10 -> 11)
                    vm.NextToolbarPage();
                }
                else
                {
                    // Vuốt sang phải: lùi về trang trước (11 -> 6-10 -> 1-5)
                    vm.PrevToolbarPage();
                }
            }
            e.Handled = true;
        }
    }

    private void OnToolbarPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isToolbarSwipeTracking) return;
        _isToolbarSwipeTracking = false;

        if (_toolbarSwipeTriggered)
        {
            _toolbarSwipeTriggered = false;
            e.Handled = true;
            return;
        }

        Point release = e.GetPosition(this);
        double dx = release.X - _toolbarSwipeStartPoint.X;
        double dy = release.Y - _toolbarSwipeStartPoint.Y;

        if (Math.Abs(dx) >= 24.0 && Math.Abs(dx) > Math.Abs(dy) * 1.15)
        {
            if (DataContext is ComicReaderViewModel vm)
            {
                vm.ToggleToolbarPage();
            }
            e.Handled = true;
        }
    }

    // =========================================================================
    // XỬ LÝ KÉO KHUNG CẮT LỀ MARGIN CROP (BUTTON 4)
    // =========================================================================

    private void OnCropPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control panel) return;
        _cropDragLastPoint = e.GetPosition(panel);
        double w = Math.Max(1.0, panel.Bounds.Width);
        double h = Math.Max(1.0, panel.Bounds.Height);
        _cropStartXRatio = _cropDragLastPoint.X / w;
        _cropStartYRatio = _cropDragLastPoint.Y / h;
        _isCropDragging = true;
    }

    private void OnCropPanelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isCropDragging || sender is not Control panel || DataContext is not ComicReaderViewModel vm) return;

        Point pt = e.GetPosition(panel);
        double w = Math.Max(100.0, panel.Bounds.Width);
        double h = Math.Max(100.0, panel.Bounds.Height);

        double dxPct = (pt.X - _cropDragLastPoint.X) / w * 100.0;
        double dyPct = (pt.Y - _cropDragLastPoint.Y) / h * 100.0;
        _cropDragLastPoint = pt;

        double dLeft = _cropStartXRatio < 0.5 ? dxPct : 0;
        double dRight = _cropStartXRatio >= 0.5 ? -dxPct : 0;
        double dTop = _cropStartYRatio < 0.5 ? dyPct : 0;
        double dBottom = _cropStartYRatio >= 0.5 ? -dyPct : 0;

        vm.AdjustPendingCropByDrag(dLeft, dTop, dRight, dBottom);
    }

    private void OnCropPanelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isCropDragging = false;
    }

    // =========================================================================
    // XỬ LÝ THANH TRƯỢT TIẾN ĐỘ TRANG (PAGE SCRUBBER SLIDER)
    // =========================================================================

    private void OnScrubberPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        StopKineticScroll();
        _isUserDraggingScrubber = true;
        if (sender is Slider slider && DataContext is ComicReaderViewModel vm)
        {
            vm.UpdateScrubberHover((int)Math.Round(slider.Value), true);
        }
    }

    private void OnScrubberPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isUserDraggingScrubber) return;
        _isUserDraggingScrubber = false;
        if (sender is Slider slider && DataContext is ComicReaderViewModel vm)
        {
            _suppressScrollChangedUntilTick = Environment.TickCount64 + 600;
            vm.UpdateScrubberHover(0, false);
            vm.ScrubToPage((int)Math.Round(slider.Value));
        }
    }

    private void OnScrubberPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_isUserDraggingScrubber) return;
        _isUserDraggingScrubber = false;
        if (sender is Slider slider && DataContext is ComicReaderViewModel vm)
        {
            _suppressScrollChangedUntilTick = Environment.TickCount64 + 600;
            vm.UpdateScrubberHover(0, false);
            vm.ScrubToPage((int)Math.Round(slider.Value));
        }
    }

    private void OnScrubberPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_isUserDraggingScrubber &&
            e.Property == RangeBase.ValueProperty &&
            sender is Slider slider &&
            DataContext is ComicReaderViewModel vm)
        {
            int targetPage = (int)Math.Round(slider.Value);
            // Trong khi đang kéo thanh trượt: CHỈ cập nhật bong bóng số trang (Preview HUD)
            // TUYỆT ĐỐI KHÔNG cuộn dồn dập ScrollViewer để tránh rung lắc và nghẽn decode với hàng ngàn trang
            vm.UpdateScrubberHover(targetPage, true);
        }
    }

    private void OnReaderDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.ScrollToPageRequested -= OnVmScrollToPageRequested;
            _subscribedVm.AutoScrollStepRequested -= OnVmAutoScrollStepRequested;
            _subscribedVm.PageButtonAreaScrollRequested -= OnVmPageButtonAreaScrollRequested;
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm = null;
        }

        if (DataContext is ComicReaderViewModel vm)
        {
            _subscribedVm = vm;
            vm.ScrollToPageRequested += OnVmScrollToPageRequested;
            vm.AutoScrollStepRequested += OnVmAutoScrollStepRequested;
            vm.PageButtonAreaScrollRequested += OnVmPageButtonAreaScrollRequested;
            vm.PropertyChanged += OnVmPropertyChanged;
            if (Bounds.Width > 0 && Bounds.Height > 0)
            {
                vm.UpdateViewportDimensions(Bounds.Width, Bounds.Height);
            }
            if (vm.Pages.Count > 0 && vm.CurrentPageIndex >= 0)
            {
                OnVmScrollToPageRequested(vm.CurrentPageIndex);
            }
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComicReaderViewModel.IsOverlayVisible))
        {
            _suppressScrollChangedUntilTick = Environment.TickCount64 + 800;
        }
    }

    private void OnPageButtonAreaPrevClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ComicReaderViewModel vm)
        {
            vm.PageButtonAreaPrev();
            e.Handled = true;
        }
    }

    private void OnPageButtonAreaNextClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ComicReaderViewModel vm)
        {
            vm.PageButtonAreaNext();
            e.Handled = true;
        }
    }

    private void OnBoundaryNextChapterClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ComicReaderViewModel vm && vm.CanGoNextChapter)
        {
            vm.NextChapter();
            e.Handled = true;
        }
    }

    private void OnBoundaryPrevChapterClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ComicReaderViewModel vm && vm.CanGoPrevChapter)
        {
            vm.PrevChapter();
            e.Handled = true;
        }
    }

    private void OnVmPageButtonAreaScrollRequested(double fraction, bool animated)
    {
        StopKineticScroll();
        if (DataContext is not ComicReaderViewModel vm) return;
        if (!vm.IsScrollMode) return;

        var scrollViewer = GetReaderScrollViewer();
        if (scrollViewer == null) return;

        if (vm.IsHorizontalScrollMode)
        {
            double dir = vm.IsRightToLeft ? -1.0 : 1.0;
            double vpWidth = scrollViewer.Viewport.Width > 0 ? scrollViewer.Viewport.Width : Bounds.Width;
            if (vpWidth <= 0) vpWidth = 800.0;
            double step = vpWidth * fraction * dir;
            double maxX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
            double targetX = scrollViewer.Offset.X + step;

            if (fraction > 0)
            {
                // Nút Tiến (+): Chuyển chương nếu đã ở cuối, hoặc neo tới cuối trang
                bool atEnd = vm.IsRightToLeft
                    ? (scrollViewer.Offset.X <= 4 || vm.CurrentPageIndex >= vm.Pages.Count - 1 || vm.IsScrollAtEnd)
                    : (scrollViewer.Offset.X >= maxX - 4 || vm.CurrentPageIndex >= vm.Pages.Count - 1 || vm.IsScrollAtEnd);

                if (atEnd)
                {
                    if (vm.CanGoNextChapter)
                    {
                        vm.NextChapter();
                        return;
                    }
                }
                else
                {
                    bool reachingEnd = vm.IsRightToLeft ? targetX <= 4 : targetX >= maxX - 4;
                    if (reachingEnd)
                    {
                        double finalX = vm.IsRightToLeft ? 0 : maxX;
                        scrollViewer.Offset = new Vector(finalX, scrollViewer.Offset.Y);
                        vm.IsScrollAtEnd = true;
                        vm.IsScrollAtStart = false;
                        vm.UpdatePageIndexFromScroll(vm.Pages.Count - 1);
                        return;
                    }
                }
            }
            else if (fraction < 0)
            {
                // Nút Lùi (-): Chuyển chương nếu đã ở đầu, hoặc neo về đầu trang
                bool atStart = vm.IsRightToLeft
                    ? (scrollViewer.Offset.X >= maxX - 4 || vm.CurrentPageIndex <= 0 || vm.IsScrollAtStart)
                    : (scrollViewer.Offset.X <= 4 || vm.CurrentPageIndex <= 0 || vm.IsScrollAtStart);

                if (atStart)
                {
                    if (vm.CanGoPrevChapter)
                    {
                        vm.PrevChapter();
                        return;
                    }
                }
                else
                {
                    bool reachingStart = vm.IsRightToLeft ? targetX >= maxX - 4 : targetX <= 4;
                    if (reachingStart)
                    {
                        double finalX = vm.IsRightToLeft ? maxX : 0;
                        scrollViewer.Offset = new Vector(finalX, scrollViewer.Offset.Y);
                        vm.IsScrollAtStart = true;
                        vm.IsScrollAtEnd = false;
                        vm.UpdatePageIndexFromScroll(0);
                        return;
                    }
                }
            }

            // Xử lý chuyển chương khi cuộn chạm mép ở chế độ Đọc liền mạch
            if (vm.IsContinuousChaptersMode)
            {
                bool atEnd = vm.IsRightToLeft ? targetX <= -10 : targetX >= maxX + 10;
                bool atStart = vm.IsRightToLeft ? targetX >= maxX + 10 : targetX <= -10;
                if (atEnd && vm.CanGoNextChapter)
                {
                    vm.NextChapter();
                    return;
                }
                if (atStart && vm.CanGoPrevChapter)
                {
                    vm.PrevChapter();
                    return;
                }
            }

            scrollViewer.Offset = new Vector(Math.Clamp(targetX, 0, maxX), scrollViewer.Offset.Y);
        }
        else
        {
            double vpHeight = scrollViewer.Viewport.Height > 0 ? scrollViewer.Viewport.Height : Bounds.Height;
            if (vpHeight <= 0) vpHeight = 600.0;
            double step = vpHeight * fraction;
            double maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            double targetY = scrollViewer.Offset.Y + step;

            if (fraction > 0)
            {
                // Nút Tiến (+): Chuyển chương nếu đã ở đáy hoặc đang hiện nút chuyển chap tiếp theo
                if (scrollViewer.Offset.Y >= maxY - 4 || vm.CurrentPageIndex >= vm.Pages.Count - 1 || vm.IsScrollAtEnd)
                {
                    if (vm.CanGoNextChapter)
                    {
                        vm.NextChapter();
                        return;
                    }
                }
                else if (targetY >= maxY - 4)
                {
                    scrollViewer.Offset = new Vector(scrollViewer.Offset.X, maxY);
                    vm.IsScrollAtEnd = true;
                    vm.IsScrollAtStart = false;
                    vm.UpdatePageIndexFromScroll(vm.Pages.Count - 1);
                    return;
                }
            }
            else if (fraction < 0)
            {
                // Nút Lùi (-): Chuyển chương nếu đã ở đỉnh hoặc đang hiện nút chuyển chap trước
                if (scrollViewer.Offset.Y <= 4 || vm.CurrentPageIndex <= 0 || vm.IsScrollAtStart)
                {
                    if (vm.CanGoPrevChapter)
                    {
                        vm.PrevChapter();
                        return;
                    }
                }
                else if (targetY <= 4)
                {
                    scrollViewer.Offset = new Vector(scrollViewer.Offset.X, 0);
                    vm.IsScrollAtStart = true;
                    vm.IsScrollAtEnd = false;
                    vm.UpdatePageIndexFromScroll(0);
                    return;
                }
            }

            // Xử lý chuyển chương khi cuộn chạm mép ở chế độ Đọc liền mạch
            if (vm.IsContinuousChaptersMode)
            {
                if (targetY >= maxY + 10 && vm.CanGoNextChapter)
                {
                    vm.NextChapter();
                    return;
                }
                if (targetY <= -10 && vm.CanGoPrevChapter)
                {
                    vm.PrevChapter();
                    return;
                }
            }

            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, Math.Clamp(targetY, 0, maxY));
        }
    }

    private void OnVmAutoScrollStepRequested(double delta)
    {
        StopKineticScroll();
        var scrollViewer = GetReaderScrollViewer();
        if (scrollViewer != null)
        {
            if (DataContext is ComicReaderViewModel vm && vm.IsHorizontalScrollMode)
            {
                double dir = vm.IsRightToLeft ? -1.0 : 1.0;
                scrollViewer.Offset = new Vector(scrollViewer.Offset.X + delta * dir, scrollViewer.Offset.Y);
            }
            else
            {
                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, scrollViewer.Offset.Y + delta);
            }
        }
    }

    private void OnVmScrollToPageRequested(int pageIndex)
    {
        StopKineticScroll();
        if (DataContext is not ComicReaderViewModel vm) return;
        if (pageIndex < 0 || pageIndex >= vm.Pages.Count) return;

        var listBox = this.FindControl<ListBox>("ReaderPagesListBox");
        if (listBox == null) return;

        int reqVersion = ++_scrollRequestVersion;
        var targetItem = vm.Pages[pageIndex];
        _isProgrammaticScroll = true;
        // Khóa sự kiện ScrollChanged đủ lâu (1200ms trên Android) để ảo hóa VirtualizingStackPanel và bố cục ổn định tuyệt đối
        _suppressScrollChangedUntilTick = Environment.TickCount64 + (OperatingSystem.IsAndroid() ? 1200 : 600);

        var sv = GetReaderScrollViewer();
        if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
        {
            double targetY = 0;
            for (int i = 0; i < pageIndex && i < vm.Pages.Count; i++)
            {
                var p = vm.Pages[i];
                targetY += p.DisplayHeight + p.PageMargin.Top + p.PageMargin.Bottom;
            }
            if (sv != null)
            {
                double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                sv.Offset = new Vector(sv.Offset.X, Math.Clamp(targetY, 0, maxY));
            }
            try { listBox.ScrollIntoView(targetItem); } catch { }
        }
        else if (vm.IsScrollMode && vm.IsHorizontalScrollMode)
        {
            if (sv != null)
            {
                double accX = 0;
                for (int i = 0; i < pageIndex && i < vm.Pages.Count; i++)
                {
                    var p = vm.Pages[i];
                    accX += p.DisplayWidth + p.PageMargin.Left + p.PageMargin.Right;
                }
                double maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
                double targetX = vm.IsRightToLeft ? (maxX - accX) : accX;
                sv.Offset = new Vector(Math.Clamp(targetX, 0, maxX), sv.Offset.Y);
            }
            try { listBox.ScrollIntoView(targetItem); } catch { }
        }
        else if (vm.IsFlipMode)
        {
            var flipSv = GetFlipScrollViewer();
            if (flipSv != null)
            {
                flipSv.Offset = Vector.Zero;
            }
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (reqVersion != _scrollRequestVersion) return;
            if (!_isUserDraggingScrubber && Environment.TickCount64 - _lastUserScrollInputTick < 220)
            {
                _isProgrammaticScroll = false;
                return;
            }

            if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
            {
                double targetY = 0;
                for (int i = 0; i < pageIndex && i < vm.Pages.Count; i++)
                {
                    var p = vm.Pages[i];
                    targetY += p.DisplayHeight + p.PageMargin.Top + p.PageMargin.Bottom;
                }
                if (sv != null)
                {
                    double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                    sv.Offset = new Vector(sv.Offset.X, Math.Clamp(targetY, 0, maxY));
                }
            }
            else if (vm.IsScrollMode && vm.IsHorizontalScrollMode)
            {
                if (sv != null)
                {
                    double accX = 0;
                    for (int i = 0; i < pageIndex && i < vm.Pages.Count; i++)
                    {
                        var p = vm.Pages[i];
                        accX += p.DisplayWidth + p.PageMargin.Left + p.PageMargin.Right;
                    }
                    double maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
                    double targetX = vm.IsRightToLeft ? (maxX - accX) : accX;
                    sv.Offset = new Vector(Math.Clamp(targetX, 0, maxX), sv.Offset.Y);
                }
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(
                () =>
                {
                    if (reqVersion == _scrollRequestVersion)
                    {
                        _isProgrammaticScroll = false;
                    }
                },
                Avalonia.Threading.DispatcherPriority.Background);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnPageContainerAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control ctrl &&
            ctrl.DataContext is ReaderPageItem pageItem &&
            DataContext is ComicReaderViewModel vm)
        {
            _ = vm.EnsureSinglePageLoadedAsync(pageItem);
        }
    }

    private void OnReaderScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        long now = Environment.TickCount64;
        long pinchElapsed = now - _lastPinchEndTick;

        if (_isPinching || now < _pinchCooldownUntilTick ||
            (_lastPinchEndTick > 0 && pinchElapsed >= 0 && pinchElapsed < 800))
        {
            if (DataContext is ComicReaderViewModel vmP && _pinchTargetPageIndex >= 0 && vmP.Pages.Count > _pinchTargetPageIndex)
            {
                vmP.CurrentPageIndex = _pinchTargetPageIndex;
            }
            return;
        }

        if (now < _suppressScrollChangedUntilTick)
        {
            return;
        }

        if (_isProgrammaticScroll || _isUserDraggingScrubber || _isDoubleTapZooming)
        {
            return;
        }
        if (DataContext is not ComicReaderViewModel vm || !vm.IsScrollMode || vm.Pages.Count == 0) return;

        var listBox = this.FindControl<ListBox>("ReaderPagesListBox");
        if (listBox == null) return;

        var scrollViewer = GetReaderScrollViewer();
        if (scrollViewer != null)
        {
            if (vm.IsHorizontalScrollMode)
            {
                double maxX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
                bool isAtEnd = vm.IsRightToLeft ? scrollViewer.Offset.X <= 6 : scrollViewer.Offset.X >= maxX - 6;
                bool isAtStart = vm.IsRightToLeft ? scrollViewer.Offset.X >= maxX - 6 : scrollViewer.Offset.X <= 6;

                if (isAtStart && vm.CurrentPageIndex <= 1)
                {
                    vm.IsScrollAtStart = true;
                    vm.IsScrollAtEnd = false;
                    vm.UpdatePageIndexFromScroll(0);
                    return;
                }
                else if (isAtEnd && maxX > 0 && vm.CurrentPageIndex >= vm.Pages.Count - 2)
                {
                    vm.IsScrollAtStart = false;
                    vm.IsScrollAtEnd = true;
                    vm.UpdatePageIndexFromScroll(vm.Pages.Count - 1);
                    return;
                }
                else
                {
                    vm.IsScrollAtStart = false;
                    vm.IsScrollAtEnd = false;
                }
            }
            else
            {
                double maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                if (scrollViewer.Offset.Y <= 6 && vm.CurrentPageIndex <= 1)
                {
                    vm.IsScrollAtStart = true;
                    vm.IsScrollAtEnd = false;
                    vm.UpdatePageIndexFromScroll(0);
                    return;
                }
                else if (scrollViewer.Offset.Y >= maxY - 6 && maxY > 0 && vm.CurrentPageIndex >= vm.Pages.Count - 2)
                {
                    vm.IsScrollAtStart = false;
                    vm.IsScrollAtEnd = true;
                    vm.UpdatePageIndexFromScroll(vm.Pages.Count - 1);
                    return;
                }
                else
                {
                    vm.IsScrollAtStart = false;
                    vm.IsScrollAtEnd = false;
                }
            }
        }

        if (vm.Pages.Count == 0) return;

        if (vm.IsHorizontalScrollMode)
        {
            double vw = (scrollViewer != null && scrollViewer.Viewport.Width > 10) ? scrollViewer.Viewport.Width : listBox.Bounds.Width;
            if (vw <= 10) return;

            double maxX = scrollViewer != null ? Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width) : 0;
            double offsetX = scrollViewer != null ? scrollViewer.Offset.X : 0;

            // Xử lý tọa độ cuộn hiệu dụng (effectiveScrollX) tính từ Trang 0:
            // Với LTR: Trang 0 ở X = 0, cuộn tới thì Offset.X tăng dần từ 0 đến maxX.
            // Với RTL: Trang 0 ở X = maxX, cuộn tới thì Offset.X giảm dần từ maxX về 0.
            double effectiveScrollX = (vm.IsRightToLeft && maxX > 0)
                ? Math.Clamp(maxX - offsetX, 0, maxX)
                : Math.Clamp(offsetX, 0, maxX);

            double viewportLeft = effectiveScrollX;
            double viewportRight = effectiveScrollX + vw;
            double viewportMidX = effectiveScrollX + vw * 0.5;

            // 1. Kiểm tra Hysteresis Sticky Anchor cho trang hiện tại (vm.CurrentPageIndex)
            double currentAccX = 0;
            double curPageLeft = -1;
            double curPageRight = -1;

            for (int i = 0; i < vm.Pages.Count; i++)
            {
                var p = vm.Pages[i];
                double pW = Math.Max(40, p.DisplayWidth + p.PageMargin.Left + p.PageMargin.Right);
                if (i == vm.CurrentPageIndex)
                {
                    curPageLeft = currentAccX;
                    curPageRight = currentAccX + pW;
                    break;
                }
                currentAccX += pW;
            }

            if (curPageLeft >= 0)
            {
                double visibleLeft = Math.Max(curPageLeft, viewportLeft);
                double visibleRight = Math.Min(curPageRight, viewportRight);
                double visibleWidth = Math.Max(0, visibleRight - visibleLeft);

                // Miễn là trang hiện tại còn hiển thị ít nhất 25% chiều rộng trên màn hình
                // HOẶC bao phủ vùng giữa màn hình (từ tâm viewportMidX):
                // -> GIỮ NGUYÊN CurrentPageIndex, TUYỆT ĐỐI KHÔNG TUA TRANG!
                if (visibleWidth >= vw * 0.25 || (curPageLeft <= viewportMidX && curPageRight >= viewportMidX))
                {
                    return;
                }
            }

            // 2. Nếu trang hiện tại đã cuộn thực sự ra khỏi vùng đọc, tìm trang mới chiếm diện tích hiển thị lớn nhất (Maximum Overlap)
            int bestPageIndexX = -1;
            double maxVisibleWidth = -1;
            double runningX = 0;

            for (int i = 0; i < vm.Pages.Count; i++)
            {
                var page = vm.Pages[i];
                double pW = Math.Max(40, page.DisplayWidth + page.PageMargin.Left + page.PageMargin.Right);
                double left = runningX;
                double right = runningX + pW;
                runningX += pW;

                if (right < viewportLeft) continue;
                if (left > viewportRight) break;

                double visibleLeft = Math.Max(left, viewportLeft);
                double visibleRight = Math.Min(right, viewportRight);
                double visibleWidth = Math.Max(0, visibleRight - visibleLeft);

                if (visibleWidth > maxVisibleWidth)
                {
                    maxVisibleWidth = visibleWidth;
                    bestPageIndexX = i;
                }
            }

            if (bestPageIndexX >= 0 && bestPageIndexX != vm.CurrentPageIndex && maxVisibleWidth >= vw * 0.20)
            {
                vm.UpdatePageIndexFromScroll(bestPageIndexX);
            }
            return;
        }

        // Chế độ cuộn dọc (Webtoon / Vertical Scroll)
        // Thuật toán toán học O(N) thuần túy trên RAM (0.001ms), loại bỏ hoàn toàn việc duyệt Visual Tree
        // Giúp tốc độ cuộn đạt 60-120 FPS siêu mượt, đồng thời bảo tồn cơ chế Hysteresis Sticky Anchor chống tua giật
        double vh = (scrollViewer != null && scrollViewer.Viewport.Height > 10) ? scrollViewer.Viewport.Height : listBox.Bounds.Height;
        if (vh <= 10) return;

        double offsetY = scrollViewer != null ? scrollViewer.Offset.Y : 0;
        double viewportTop = offsetY;
        double viewportBottom = offsetY + vh;
        double viewportMidY = offsetY + vh * 0.5;

        // 1. Kiểm tra Hysteresis cho CurrentPageIndex
        double currentAccY = 0;
        double curPageTop = -1;
        double curPageBottom = -1;

        for (int i = 0; i < vm.Pages.Count; i++)
        {
            var p = vm.Pages[i];
            double pH = Math.Max(40, p.DisplayHeight + p.PageMargin.Top + p.PageMargin.Bottom);
            if (i == vm.CurrentPageIndex)
            {
                curPageTop = currentAccY;
                curPageBottom = currentAccY + pH;
                break;
            }
            currentAccY += pH;
        }

        if (curPageTop >= 0)
        {
            double visibleTop = Math.Max(curPageTop, viewportTop);
            double visibleBottom = Math.Min(curPageBottom, viewportBottom);
            double visibleHeight = Math.Max(0, visibleBottom - visibleTop);

            // Miễn là trang hiện tại còn hiển thị ít nhất 25% chiều cao trên màn hình
            // HOẶC bao phủ vùng giữa màn hình (từ 40% đến 60% vh):
            // -> GIỮ NGUYÊN CurrentPageIndex, TUYỆT ĐỐI KHÔNG TUA TRANG!
            if (visibleHeight >= vh * 0.25 || (curPageTop <= viewportMidY && curPageBottom >= viewportMidY))
            {
                return;
            }
        }

        // 2. Nếu trang hiện tại đã cuộn khỏi màn hình, tìm trang mới chiếm diện tích hiển thị lớn nhất (Maximum Overlap)
        int bestPageIndex = -1;
        double maxVisibleHeight = -1;
        double runningY = 0;

        for (int i = 0; i < vm.Pages.Count; i++)
        {
            var page = vm.Pages[i];
            double pH = Math.Max(40, page.DisplayHeight + page.PageMargin.Top + page.PageMargin.Bottom);
            double top = runningY;
            double bottom = runningY + pH;
            runningY += pH;

            if (bottom < viewportTop) continue;
            if (top > viewportBottom) break;

            double visibleTop = Math.Max(top, viewportTop);
            double visibleBottom = Math.Min(bottom, viewportBottom);
            double visibleHeight = Math.Max(0, visibleBottom - visibleTop);

            if (visibleHeight > maxVisibleHeight)
            {
                maxVisibleHeight = visibleHeight;
                bestPageIndex = i;
            }
        }

        if (bestPageIndex >= 0 && bestPageIndex != vm.CurrentPageIndex && maxVisibleHeight >= vh * 0.20)
        {
            vm.UpdatePageIndexFromScroll(bestPageIndex);
        }

    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        StopKineticScroll();

        if (IsScrollBarOrInteractiveControl(e.Source as Visual))
        {
            _lastUserScrollInputTick = Environment.TickCount64;
            _isPointerTapCandidate = false;
            _isPanHolding = false;
            _isPanDragging = false;
            return;
        }

        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed && e.Pointer.Type != PointerType.Touch && e.Pointer.Type != PointerType.Pen)
        {
            _isPointerTapCandidate = false;
            _isPanHolding = false;
            _isPanDragging = false;
            return;
        }

        Point pos = e.GetPosition(this);
        long now = Environment.TickCount64;

        long pinchElapsed = now - _lastPinchEndTick;
        // Bỏ qua nảy chạm ngón tay thứ hai trong vòng 400ms sau khi pinch kết thúc
        if (now < _pinchCooldownUntilTick || (_lastPinchEndTick > 0 && pinchElapsed >= 0 && pinchElapsed < 400))
        {
            _activePointers.Clear();
            _pointerLastSeenTicks.Clear();
            _isPointerTapCandidate = false;
            _isPanHolding = false;
            _isPanDragging = false;
            StopKineticScroll();
            return;
        }

        // Dọn sạch con trỏ mồ côi (Ghost Pointers) khi KHÔNG trong cử chỉ pinch (> 150ms)
        if (!_isPinching && _pointerLastSeenTicks.Count > 0)
        {
            List<int>? staleKeys = null;
            foreach (var kvp in _pointerLastSeenTicks)
            {
                if (now - kvp.Value > 150)
                {
                    staleKeys ??= new List<int>();
                    staleKeys.Add(kvp.Key);
                }
            }
            if (staleKeys != null)
            {
                foreach (var k in staleKeys)
                {
                    _activePointers.Remove(k);
                    _pointerLastSeenTicks.Remove(k);
                }
            }
        }

        if (_activePointers.Count == 0)
        {
            _isPinching = false;
            _isPanHolding = false;
            _isPanDragging = false;
        }

        _activePointers[e.Pointer.Id] = pos;
        _pointerLastSeenTicks[e.Pointer.Id] = now;

        // Cử chỉ 2 ngón tay Pinch-to-zoom: Kích hoạt ngay khi có từ 2 con trỏ trở lên trên màn hình
        if (_activePointers.Count >= 2)
        {
            _isPanHolding = false;
            _isPanDragging = false;
            _isPointerTapCandidate = false;
            StopKineticScroll();

            var points = _activePointers.Values.Take(2).ToArray();
            StartPinchGesture(points[0], points[1]);
            return;
        }

        _pointerPressPoint = pos;
        _pointerPressTick = now;
        _isPointerTapCandidate = true;

        _panStartPoint = pos;
        _panLastPoint = pos;
        _panStartTick = now;
        _lastPanMoveTick = now;
        _isPanHolding = true;
        _isPanDragging = false;
        _panVelocityX = 0;
        _panVelocityY = 0;
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        Point currentPoint = e.GetPosition(this);
        long moveNow = Environment.TickCount64;

        long pinchElapsed = moveNow - _lastPinchEndTick;
        // Bỏ qua cử chỉ di chuyển mồ côi nếu KHÔNG ĐANG DRAG LIỀN MẠCH và chưa hết cooldown
        if (!_isPanDragging && (moveNow < _pinchCooldownUntilTick || (_lastPinchEndTick > 0 && pinchElapsed >= 0 && pinchElapsed < 250 && _activePointers.Count == 0)))
        {
            _isPanHolding = false;
            _isPanDragging = false;
            _isPointerTapCandidate = false;
            StopKineticScroll();
            e.Handled = true;
            return;
        }

        // QUY TẮC CỐT LÕI: Chỉ cập nhật con trỏ nếu con trỏ này ĐÃ ĐƯỢC ghi nhận trong Pressed!
        // Tuyệt đối không tự tiện thêm con trỏ ma vào _activePointers!
        if (!_activePointers.ContainsKey(e.Pointer.Id))
        {
            if (!_isPinching && !_isPanHolding)
            {
                return;
            }
            _activePointers[e.Pointer.Id] = currentPoint;
        }
        else
        {
            _activePointers[e.Pointer.Id] = currentPoint;
        }
        _pointerLastSeenTicks[e.Pointer.Id] = moveNow;

        // Dọn sạch con trỏ mồ côi nếu không di chuyển trong 350ms
        if (!_isPinching && _pointerLastSeenTicks.Count > 1)
        {
            List<int>? staleKeys = null;
            foreach (var kvp in _pointerLastSeenTicks)
            {
                if (kvp.Key != e.Pointer.Id && moveNow - kvp.Value > 350)
                {
                    staleKeys ??= new List<int>();
                    staleKeys.Add(kvp.Key);
                }
            }
            if (staleKeys != null)
            {
                foreach (var k in staleKeys)
                {
                    _activePointers.Remove(k);
                    _pointerLastSeenTicks.Remove(k);
                }
            }
        }

        if (DataContext is not ComicReaderViewModel vm) return;
        if (vm.IsMarginEditorOpen) return;

        // Xử lý cử chỉ 2 ngón tay Pinch-to-zoom: Kích hoạt ngay khi có từ 2 con trỏ trở lên trên màn hình
        if (_activePointers.Count >= 2)
        {
            // KHÓA HOÀN TOÀN THAO TÁC DRAG KHI CÓ 2 NGÓN TAY TRÊN MÀN HÌNH:
            _isPanHolding = false;
            _isPanDragging = false;
            var points = _activePointers.Values.Take(2).ToArray();
            PerformPinchZoomStep(points[0], points[1]);
            e.Handled = true;
            return;
        }

        if (_isPinching)
        {
            // Trong khi đang pinch: CHẶN ĐỨNG HOÀN TOÀN không cho scroll theo ngón tay lên xuống!
            e.Handled = true;
            return;
        }

        // CHỈ ĐƯỢC PHÉP DRAG ẢNH KHI CHỈ CÒN 1 NGÓN TAY:
        if (!_isPanHolding) return;
        double totalDx = currentPoint.X - _panStartPoint.X;
        double totalDy = currentPoint.Y - _panStartPoint.Y;
        double totalDist = Math.Sqrt(totalDx * totalDx + totalDy * totalDy);

        bool isZoomedFlip = vm.IsFlipMode && vm.ZoomLevel > 1.02;
        bool canPanScroll = vm.IsScrollMode || isZoomedFlip;

        if (!canPanScroll)
        {
            // Ở chế độ Flip mode zoom 1.0: không pan ScrollViewer, để cử chỉ Swipe lật trang xử lý khi nhả tay
            return;
        }

        // Ngưỡng kéo (Touch Slop): Phân biệt rõ ràng giữa chạm ngón tay (Tap) và kéo rê (Pan)
        // Trên màn hình cảm ứng Android DPI cao, vi rung lắc da ngón tay đạt tới 20px, do đó ngưỡng là 28.0px
        double slopThreshold = (e.Pointer.Type == PointerType.Mouse) ? 8.0 : 28.0;
        if (!_isPanDragging)
        {
            if (totalDist >= slopThreshold)
            {
                _isPanDragging = true;
                _isPointerTapCandidate = false; // Đã kéo quá ngưỡng slop thì mới hủy Tap
                _lastTapTick = 0;
                _lastTapPoint = new Point(-9999, -9999);
                _singleTapTimer.Stop();

                // QUY TẮC WORKFLOW MỤC 15.14: TUYỆT ĐỐI KHÔNG CAPTURE TOUCH TRÊN ANDROID!
                if (e.Pointer.Type == PointerType.Mouse)
                {
                    var viewport = sender as IInputElement ?? this;
                    e.Pointer.Capture(viewport);
                }

                // Tự động ẩn thanh overlay nếu đang hiện khi người dùng bắt đầu cuộn trang
                if (vm.IsOverlayVisible && !vm.HasActivePopup)
                {
                    vm.IsOverlayVisible = false;
                }
            }
            else
            {
                return;
            }
        }

        double deltaX = currentPoint.X - _panLastPoint.X;
        double deltaY = currentPoint.Y - _panLastPoint.Y;

        long now = Environment.TickCount64;
        double dt = Math.Max(1.0, now - _lastPanMoveTick);
        _lastPanMoveTick = now;

        // Vận tốc tức thời (px/ms)
        double instantVx = deltaX / dt;
        double instantVy = deltaY / dt;
        _panVelocityX = _panVelocityX * 0.35 + instantVx * 0.65;
        _panVelocityY = _panVelocityY * 0.35 + instantVy * 0.65;

        _panLastPoint = currentPoint;
        _lastUserScrollInputTick = now;

        if (vm.IsScrollMode)
        {
            var scrollViewer = GetReaderScrollViewer();
            if (scrollViewer != null)
            {
                double effectiveDeltaX = (vm.IsHorizontalScrollMode && vm.IsRightToLeft) ? -deltaX : deltaX;
                double newX = scrollViewer.Offset.X - effectiveDeltaX;
                double newY = scrollViewer.Offset.Y - deltaY;

                double maxX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
                double maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);

                scrollViewer.Offset = new Vector(Math.Clamp(newX, 0, maxX), Math.Clamp(newY, 0, maxY));
                CheckContinuousOverscroll(vm, scrollViewer, deltaX, deltaY, maxX, maxY);
            }
        }
        else if (isZoomedFlip)
        {
            var flipScrollViewer = GetFlipScrollViewer();
            if (flipScrollViewer != null)
            {
                double newX = flipScrollViewer.Offset.X - deltaX;
                double newY = flipScrollViewer.Offset.Y - deltaY;

                double maxX = Math.Max(0, flipScrollViewer.Extent.Width - flipScrollViewer.Viewport.Width);
                double maxY = Math.Max(0, flipScrollViewer.Extent.Height - flipScrollViewer.Viewport.Height);

                flipScrollViewer.Offset = new Vector(Math.Clamp(newX, 0, maxX), Math.Clamp(newY, 0, maxY));
            }
        }

        e.Handled = true;
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse)
        {
            e.Pointer.Capture(null);
        }

        _activePointers.Remove(e.Pointer.Id);
        _pointerLastSeenTicks.Remove(e.Pointer.Id);

        // KHI NGƯỜI DÙNG NHẤC TAY TRONG KHI ĐANG PINCH ZOOM:
        if (_isPinching)
        {
            if (_activePointers.Count == 1)
            {
                // CHUYỂN TIẾP LIỀN MẠCH TỪ PINCH SANG DRAG 1 NGÓN:
                // Người dùng nhấc 1 ngón tay lên, ngón còn lại tiếp tục drag rê ảnh mượt mà ngay lập tức!
                _isPinching = false;
                _lastPinchEndTick = Environment.TickCount64;
                _pinchCooldownUntilTick = 0; // Không cooldown để ngón tay còn lại không bị đơ
                _suppressScrollChangedUntilTick = Environment.TickCount64 + 300;

                var remainingPt = _activePointers.Values.First();
                _panStartPoint = remainingPt;
                _panLastPoint = remainingPt;
                _panStartTick = Environment.TickCount64;
                _lastPanMoveTick = Environment.TickCount64;
                _isPanHolding = true;
                _isPanDragging = true; // Kích hoạt kéo rê ngay tức khắc
                _isPointerTapCandidate = false;
                _singleTapTimer.Stop();
                _panVelocityX = 0;
                _panVelocityY = 0;

                if (DataContext is ComicReaderViewModel vmRel && _pinchTargetPageIndex >= 0 && vmRel.Pages.Count > _pinchTargetPageIndex)
                {
                    vmRel.CurrentPageIndex = _pinchTargetPageIndex;
                }
                e.Handled = true;
                return;
            }
            else
            {
                // Cả 2 ngón tay đều đã nhấc lên:
                EndPinchZoom();
                _isPanHolding = false;
                _isPanDragging = false;
                _isPointerTapCandidate = false;
                _activePointers.Clear();
                _pointerLastSeenTicks.Clear();
                if (DataContext is ComicReaderViewModel vmRel && _pinchTargetPageIndex >= 0 && vmRel.Pages.Count > _pinchTargetPageIndex)
                {
                    vmRel.CurrentPageIndex = _pinchTargetPageIndex;
                }
                e.Handled = true;
                return;
            }
        }

        // Nếu không còn con trỏ nào, dọn sạch
        if (_activePointers.Count == 0)
        {
            _activePointers.Clear();
            _pointerLastSeenTicks.Clear();
        }

        long now = Environment.TickCount64;
        long pinchElapsed = now - _lastPinchEndTick;
        // Bỏ qua cú nhả tay nếu vừa kết thúc pinch 2 ngón tay trong vòng 250ms để chống nảy chạm vô ý khi nhấc ngón tay thứ 2 (trừ khi đang drag)
        if (!_isPanDragging && (now < _pinchCooldownUntilTick || (_lastPinchEndTick > 0 && pinchElapsed >= 0 && pinchElapsed < 250 && _activePointers.Count == 0)))
        {
            _activePointers.Clear();
            _pointerLastSeenTicks.Clear();
            _isPanHolding = false;
            _isPanDragging = false;
            _isPointerTapCandidate = false;
            StopKineticScroll();
            if (DataContext is ComicReaderViewModel vmRel && _pinchTargetPageIndex >= 0 && vmRel.Pages.Count > _pinchTargetPageIndex)
            {
                vmRel.CurrentPageIndex = _pinchTargetPageIndex;
            }
            e.Handled = true;
            return;
        }

        _cumulativeOverscrollX = 0;
        _cumulativeOverscrollY = 0;

        bool wasDragging = _isPanDragging;
        _isPanHolding = false;
        _isPanDragging = false;

        if (IsScrollBarOrInteractiveControl(e.Source as Visual))
        {
            _isPointerTapCandidate = false;
            return;
        }

        if (DataContext is not ComicReaderViewModel vm)
        {
            _isPointerTapCandidate = false;
            return;
        }

        Point releasePoint = e.GetPosition(this);
        long elapsedMs = now - _pointerPressTick;
        double dx = releasePoint.X - _pointerPressPoint.X;
        double dy = releasePoint.Y - _pointerPressPoint.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        // Đánh giá chuẩn xác cú Tap (Single-Tap) trên thiết bị di động Android:
        double maxTapDist = (e.Pointer.Type == PointerType.Mouse) ? 15.0 : 30.0;
        bool isLegitTap = distance <= maxTapDist && elapsedMs <= 500;

        if (wasDragging)
        {
            _isPointerTapCandidate = false;
            _lastTapTick = 0;
            _lastTapPoint = new Point(-9999, -9999);
            _singleTapTimer.Stop();
            e.Handled = true;

            // Kích hoạt Quán tính cuộn mượt mà (Kinetic Momentum)
            long releaseNow = Environment.TickCount64;
            long timeSinceLastMove = releaseNow - _lastPanMoveTick;

            if (timeSinceLastMove <= 90)
            {
                const double MaxV = 3.0; // px/ms
                _panVelocityX = Math.Clamp(_panVelocityX, -MaxV, MaxV);
                _panVelocityY = Math.Clamp(_panVelocityY, -MaxV, MaxV);

                if (Math.Abs(_panVelocityX) > 0.08 || Math.Abs(_panVelocityY) > 0.08)
                {
                    _kineticVelocityX = _panVelocityX;
                    _kineticVelocityY = _panVelocityY;
                    _kineticStopwatch.Restart();
                    _kineticTimer.Start();

                }
            }
            return;
        }

        if (!isLegitTap && !_isPointerTapCandidate) return;
        _isPointerTapCandidate = false;

        long tapNow = Environment.TickCount64;
        double tapDx = releasePoint.X - _lastTapPoint.X;
        double tapDy = releasePoint.Y - _lastTapPoint.Y;
        double tapDist = Math.Sqrt(tapDx * tapDx + tapDy * tapDy);

        if (tapNow - _lastTapTick <= 500 && tapDist < 50.0)
        {
            // Chạm đúp (Double-Tap) nhanh trong vòng 500ms -> HỦY timer single-tap ngay lập tức!
            _singleTapTimer.Stop();
            _lastTapTick = 0;

            PerformDoubleTapZoom(releasePoint);
            return;
        }

        // Chạm đơn (Single-Tap): Ghi nhận vị trí và bắt đầu đếm lùi 500ms (0.5 giây).
        // Chỉ khi hết 500ms mà không có cú chạm đúp nào thì mới ẩn / hiện thanh công cụ!
        _lastTapTick = tapNow;
        _lastTapPoint = releasePoint;
        _singleTapTimer.Stop();
        _singleTapTimer.Start();
    }

    /// <summary>
    /// Thực hiện thao tác Chạm đúp (Double-Tap) Zoom In / Out mượt mà, neo điểm tựa chính xác tuyệt đối,
    /// loại bỏ hoàn toàn lỗi lệch trang / tua trang trong cả chế độ Cuộn dọc (Vertical Scroll) và Lật trang (Flip Mode).
    /// </summary>
    public void PerformDoubleTapZoom(Point releasePoint)
    {
        if (DataContext is not ComicReaderViewModel vm) return;

        int savedPageIndex = vm.CurrentPageIndex;
        double oldZoom = vm.ZoomLevel;
        var scrollViewer = vm.IsScrollMode ? GetReaderScrollViewer() : GetFlipScrollViewer();
        Vector oldOffset = scrollViewer?.Offset ?? Vector.Zero;

        var lb = this.FindControl<ListBox>("ReaderPagesListBox");
        Point touchInLb = releasePoint;
        if (lb != null)
        {
            var ptInLb = this.TranslatePoint(releasePoint, lb);
            if (ptInLb.HasValue) touchInLb = ptInLb.Value;
        }

        _isDoubleTapZooming = true;
        _isProgrammaticScroll = true;
        _suppressScrollChangedUntilTick = Environment.TickCount64 + 400;

        // Kích hoạt Double-Tap Zoom in / out trên mọi vùng (true = không phụ thuộc vùng ảnh)
        vm.ExecuteDoubleTapZoom(true);

        double newZoom = vm.ZoomLevel;
        double ratio = (oldZoom > 0.05) ? (newZoom / oldZoom) : 1.0;

        if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
        {
            // Nguyên lý Bất biến Điểm chạm Nội dung (Content Anchor Invariance):
            // Tọa độ Y tuyệt đối trên toàn bộ dải trang trước khi zoom
            double contentYBefore = oldOffset.Y + touchInLb.Y;
            // Tọa độ Y tuyệt đối trên dải trang sau khi zoom
            double contentYAfter = contentYBefore * ratio;
            // Vị trí cuộn Offset mới để điểm chạm tiếp tục nằm chính xác dưới ngón tay người dùng
            double targetOffsetY = Math.Max(0, contentYAfter - touchInLb.Y);

            // Tương tự cho trục X khi phóng to ngang
            double contentXBefore = oldOffset.X + touchInLb.X;
            double contentXAfter = contentXBefore * ratio;
            double targetOffsetX = Math.Max(0, contentXAfter - touchInLb.X);

            // Trang mục tiêu luôn luôn là trang hiện tại đang đọc (savedPageIndex),
            // tuyệt đối KHÔNG cho phép Double-Tap tua hoặc đổi sang trang khác!
            int targetPageIndex = savedPageIndex;

            Dispatcher.UIThread.Post(() =>
            {
                var sv = GetReaderScrollViewer();
                if (sv != null)
                {
                    double maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
                    double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                    sv.Offset = new Vector(Math.Clamp(targetOffsetX, 0, maxX), Math.Clamp(targetOffsetY, 0, maxY));
                }

                vm.CurrentPageIndex = targetPageIndex;
                _isDoubleTapZooming = false;
                _isProgrammaticScroll = false;
                _suppressScrollChangedUntilTick = Environment.TickCount64 + 250;
            }, DispatcherPriority.Render);
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                var sv = vm.IsScrollMode ? GetReaderScrollViewer() : GetFlipScrollViewer();
                if (sv != null)
                {
                    double maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
                    double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                    double targetX = (oldOffset.X + releasePoint.X) * ratio - releasePoint.X;
                    double targetY = (oldOffset.Y + releasePoint.Y) * ratio - releasePoint.Y;
                    sv.Offset = new Vector(Math.Clamp(targetX, 0, maxX), Math.Clamp(targetY, 0, maxY));
                }

                vm.CurrentPageIndex = savedPageIndex;
                _isDoubleTapZooming = false;
                _isProgrammaticScroll = false;
                _suppressScrollChangedUntilTick = Environment.TickCount64 + 250;
            }, DispatcherPriority.Render);
        }
    }

    private bool CheckIfPointIsInsideImage(Point p)
    {
        if (DataContext is not ComicReaderViewModel vm) return true;

        double vw = Math.Max(1.0, Bounds.Width);
        double vh = Math.Max(1.0, Bounds.Height);

        if (vm.IsFlipMode)
        {
            double imgW = vm.FlipDisplayWidth;
            double imgH = vm.FlipDisplayHeight;
            if (double.IsNaN(imgW) || double.IsNaN(imgH) || imgW <= 0 || imgH <= 0)
                return true;

            double left = (vw - imgW) / 2.0;
            double top = (vh - imgH) / 2.0;
            return p.X >= left && p.X <= left + imgW && p.Y >= top && p.Y <= top + imgH;
        }
        else if (vm.IsScrollMode)
        {
            return true;
        }

        return true;
    }

    private void OnViewportPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _activePointers.Clear();
        _pointerLastSeenTicks.Clear();
        _isPinching = false;
        _isPanHolding = false;
        _isPanDragging = false;
        _isPointerTapCandidate = false;
        _lastTapTick = 0;
        _lastTapPoint = new Point(-9999, -9999);
        _singleTapTimer.Stop();
    }

    private void OnViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        StopKineticScroll();
        if (DataContext is not ComicReaderViewModel vm) return;

        _lastUserScrollInputTick = Environment.TickCount64;

        // Giữ Ctrl + lăn chuột = Phóng to / Thu nhỏ
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Delta.Y > 0) ZoomWithAnchor(true);
            else if (e.Delta.Y < 0) ZoomWithAnchor(false);
            e.Handled = true;
            return;
        }

        // Chế độ Cuộn ngang liên tiếp: lăn chuột dọc chuyển thành cuộn ngang mượt mà
        if (vm.IsScrollMode && vm.IsHorizontalScrollMode && vm.ZoomLevel <= 1.05)
        {
            var scrollViewer = GetReaderScrollViewer();
            if (scrollViewer != null)
            {
                // Lăn chuột xuống (e.Delta.Y < 0): step > 0 -> tiến tới (tua tới)
                // Lăn chuột lên (e.Delta.Y > 0): step < 0 -> lùi lại (tua lui)
                double step = (e.Delta.Y != 0 ? -e.Delta.Y : e.Delta.X) * 140.0;
                double maxX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
                double targetX = scrollViewer.Offset.X + step;

                if (vm.IsContinuousChaptersMode)
                {
                    long now = Environment.TickCount64;
                    if (now - _lastAutoChapterTransitionTick > 1200)
                    {
                        bool atEnd = targetX >= maxX + 10;
                        bool atStart = targetX <= -10;
                        if (atEnd && vm.CanGoNextChapter)
                        {
                            _lastAutoChapterTransitionTick = now;
                            vm.NextChapter();
                            e.Handled = true;
                            return;
                        }
                        if (atStart && vm.CanGoPrevChapter)
                        {
                            _lastAutoChapterTransitionTick = now;
                            vm.PrevPage();
                            e.Handled = true;
                            return;
                        }
                    }
                }

                scrollViewer.Offset = new Vector(Math.Clamp(targetX, 0, maxX), scrollViewer.Offset.Y);
                e.Handled = true;
                return;
            }
        }

        // Chế độ Cuộn dọc: lăn chuột vượt biên ở chế độ đọc liền mạch
        if (vm.IsScrollMode && !vm.IsHorizontalScrollMode && vm.ZoomLevel <= 1.05 && vm.IsContinuousChaptersMode)
        {
            var scrollViewer = GetReaderScrollViewer();
            if (scrollViewer != null)
            {
                double maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                long now = Environment.TickCount64;
                if (scrollViewer.Offset.Y >= maxY - 2 && e.Delta.Y < -0.15 && vm.CanGoNextChapter && now - _lastAutoChapterTransitionTick > 1200)
                {
                    _lastAutoChapterTransitionTick = now;
                    vm.NextChapter();
                    e.Handled = true;
                    return;
                }
                if (scrollViewer.Offset.Y <= 2 && e.Delta.Y > 0.15 && vm.CanGoPrevChapter && now - _lastAutoChapterTransitionTick > 1200)
                {
                    _lastAutoChapterTransitionTick = now;
                    vm.PrevPage();
                    e.Handled = true;
                    return;
                }
            }
        }

        // Ở chế độ Flip mode (khi không giữ Ctrl): lăn chuột chuyển trang mượt mà
        if (vm.IsFlipMode && vm.ZoomLevel <= 1.05)
        {
            if (e.Delta.Y < 0) vm.NextPage();
            else if (e.Delta.Y > 0) vm.PrevPage();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Kiểm tra cử chỉ kéo vượt biên trong chế độ cuộn trang để tự động chuyển sang chương tiếp theo/chương trước
    /// khi người dùng bật tùy chọn "Đọc liền mạch" (Continuous Chapters Mode).
    /// </summary>
    private void CheckContinuousOverscroll(ComicReaderViewModel vm, ScrollViewer scrollViewer, double deltaX, double deltaY, double maxX, double maxY)
    {
        if (vm.IsLoadingChapter) return;
        long now = Environment.TickCount64;
        if (now - _lastAutoChapterTransitionTick < 1400) return;

        double threshold = vm.IsContinuousChaptersMode ? 50.0 : 75.0;

        if (vm.IsHorizontalScrollMode)
        {
            bool isAtEnd = vm.IsRightToLeft
                ? scrollViewer.Offset.X <= 4
                : scrollViewer.Offset.X >= maxX - 4;
            bool isAtStart = vm.IsRightToLeft
                ? scrollViewer.Offset.X >= maxX - 4
                : scrollViewer.Offset.X <= 4;

            double pullDelta = vm.IsRightToLeft ? deltaX : -deltaX;

            if (isAtEnd && pullDelta > 0)
            {
                vm.IsScrollAtEnd = true;
                vm.IsScrollAtStart = false;
                vm.UpdatePageIndexFromScroll(vm.Pages.Count - 1);

                _cumulativeOverscrollX += pullDelta;
                if (_cumulativeOverscrollX > threshold && vm.CanGoNextChapter)
                {
                    _cumulativeOverscrollX = 0;
                    _lastAutoChapterTransitionTick = now;
                    StopKineticScroll();
                    vm.NextChapter();
                }
            }
            else if (isAtStart && pullDelta < 0)
            {
                vm.IsScrollAtStart = true;
                vm.IsScrollAtEnd = false;
                vm.UpdatePageIndexFromScroll(0);

                _cumulativeOverscrollX += pullDelta;
                if (_cumulativeOverscrollX < -threshold && vm.CanGoPrevChapter)
                {
                    _cumulativeOverscrollX = 0;
                    _lastAutoChapterTransitionTick = now;
                    StopKineticScroll();
                    vm.PrevChapter();
                }
            }
            else
            {
                _cumulativeOverscrollX = 0;
            }
        }
        else
        {
            bool isAtBottom = scrollViewer.Offset.Y >= maxY - 4;
            bool isAtTop = scrollViewer.Offset.Y <= 4;

            if (isAtBottom && deltaY < 0)
            {
                vm.IsScrollAtEnd = true;
                vm.IsScrollAtStart = false;
                vm.UpdatePageIndexFromScroll(vm.Pages.Count - 1);

                _cumulativeOverscrollY += Math.Abs(deltaY);
                if (_cumulativeOverscrollY > threshold && vm.CanGoNextChapter)
                {
                    _cumulativeOverscrollY = 0;
                    _lastAutoChapterTransitionTick = now;
                    StopKineticScroll();
                    vm.NextChapter();
                }
            }
            else if (isAtTop && deltaY > 0)
            {
                vm.IsScrollAtStart = true;
                vm.IsScrollAtEnd = false;
                vm.UpdatePageIndexFromScroll(0);

                _cumulativeOverscrollY -= Math.Abs(deltaY);
                if (_cumulativeOverscrollY < -threshold && vm.CanGoPrevChapter)
                {
                    _cumulativeOverscrollY = 0;
                    _lastAutoChapterTransitionTick = now;
                    StopKineticScroll();
                    vm.PrevChapter();
                }
            }
            else
            {
                _cumulativeOverscrollY = 0;
            }
        }
    }

    private void OnControlKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ComicReaderViewModel vm) return;

        // Nếu đang trong ô TextBox (tìm kiếm phím tắt hoặc ghi chú memo)
        if (e.Source is TextBox)
        {
            if (vm.IsSettingsPopupOpen)
            {
                if (e.Key == Key.Escape || e.Key == Key.F5)
                {
                    vm.CloseSettingsPopup();
                    e.Handled = true;
                }
                return;
            }

            if (e.Key == Key.Escape)
            {
                vm.CancelAddBookmark();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                vm.ConfirmAddBookmark();
                e.Handled = true;
            }
            return;
        }

        // Trên Android: không xử lý bộ phím tắt Honeyview/Bandiview theo yêu cầu
        if (OperatingSystem.IsAndroid())
        {
            if (e.Key == Key.Back || e.Key == Key.Escape)
            {
                if (vm.HandleBackNavigation())
                {
                    e.Handled = true;
                    return;
                }
                if (vm.IsFullScreen) vm.ToggleFullScreen();
                else vm.CloseReader();
                e.Handled = true;
            }
            return;
        }

        // =========================================================================
        // 1. CHẾ ĐỘ LẮNG NGHE GÁN PHÍM TẮT MỚI (SHORTCUT LISTENING MODE)
        // =========================================================================
        if (vm.IsListeningForShortcutKey)
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                vm.CancelAssignKey();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
                e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
                e.Key == Key.LeftShift || e.Key == Key.RightShift)
            {
                // Đang nhấn giữ phím bổ trợ, chờ phím chính
                return;
            }

            vm.AssignKeyToEditingShortcut(e.Key, e.KeyModifiers);
            e.Handled = true;
            return;
        }

        // =========================================================================
        // 2. KHI HỘP THOẠI CÀI ĐẶT (F5 SETTINGS MODAL) ĐANG MỞ
        // =========================================================================
        if (vm.IsSettingsPopupOpen)
        {
            if (e.Key == Key.Escape || e.Key == Key.F5)
            {
                vm.CloseSettingsPopup();
                e.Handled = true;
                return;
            }
            // Không thực hiện các lệnh đọc truyện khi đang mở Settings
            return;
        }

        // =========================================================================
        // 3. MỞ HỘP THOẠI CÀI ĐẶT & PHÍM TẮT BẰNG PHÍM F5 (BANDIVIEW STANDARD)
        // =========================================================================
        if (e.Key == Key.F5)
        {
            vm.OpenSettingsPopup(0);
            e.Handled = true;
            return;
        }

        // =========================================================================
        // 4. TRA CỨU & THỰC THI PHÍM TẮT TÙY BIẾN HOẶC MẶC ĐỊNH BANDIVIEW
        // =========================================================================
        string? action = ReaderShortcutService.Instance.MatchAction(e.Key, e.KeyModifiers);
        if (!string.IsNullOrEmpty(action))
        {
            ExecuteShortcutAction(action, vm);
            e.Handled = true;
            return;
        }

        // =========================================================================
        // 5. BỘ DỰ PHÒNG PHÍM TẮT CŨ (FALLBACK NẾU KHÔNG CÓ ACTION NÀO MATCH)
        // =========================================================================

        // Chuyển chương: Ctrl/Shift + Left/Right hoặc [ / ]
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (e.Key == Key.Right)
            {
                vm.NextChapter();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Left)
            {
                vm.PrevChapter();
                e.Handled = true;
                return;
            }
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Ctrl + / Ctrl -: Thu phóng có neo giữ trang (Zoom In / Zoom Out with Anchor)
            if (e.Key == Key.OemPlus || e.Key == Key.Add)
            {
                ZoomWithAnchor(true);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
            {
                ZoomWithAnchor(false);
                e.Handled = true;
                return;
            }

            // Ctrl + 6: Kiểu đọc Right to Left Scroll (Cuộn ngang liên tục Phải → Trái)
            if (e.Key == Key.D6 || e.Key == Key.NumPad6)
            {
                vm.SelectPageScrollingMode("4");
                e.Handled = true;
                return;
            }

            // Ctrl + 7: Kiểu đọc Left to Right Scroll (Cuộn ngang liên tục Trái → Phải)
            if (e.Key == Key.D7 || e.Key == Key.NumPad7)
            {
                vm.SelectPageScrollingMode("3");
                e.Handled = true;
                return;
            }

            // Ctrl + 0: Khớp trọn vẹn màn hình (best fit / FitScreen)
            if (e.Key == Key.D0 || e.Key == Key.NumPad0)
            {
                vm.SetFitMode("FitScreen");
                e.Handled = true;
                return;
            }

            // Ctrl + D: Thêm / Xóa dấu trang (Bookmark) trang hiện tại
            if (e.Key == Key.D)
            {
                vm.ToggleBookmarkCurrentPage();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F)
            {
                vm.ToggleChapterDrawer();
                e.Handled = true;
                return;
            }
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Shift + D: Mở danh sách bookmark
            if (e.Key == Key.D)
            {
                vm.OpenBookmarkListPopup();
                e.Handled = true;
                return;
            }
        }


        switch (e.Key)
        {
            // Phím [ / ]: Chuyển chương trước / sau (Honeyview / Bandiview)
            case Key.OemOpenBrackets:
                vm.PrevChapter();
                e.Handled = true;
                break;

            case Key.OemCloseBrackets:
                vm.NextChapter();
                e.Handled = true;
                break;

            // Phím Escape: Thoát Popup/Drawer -> Thoát Fullscreen -> Đóng Reader
            case Key.Escape:
                if (vm.HandleBackNavigation())
                {
                    e.Handled = true;
                    break;
                }
                if (vm.IsFullScreen)
                {
                    vm.ToggleFullScreen();
                }
                else
                {
                    vm.CloseReader();
                }
                e.Handled = true;
                break;

            // Phím Backspace: Lùi trang / Cuộn lên (Honeyview)
            case Key.Back:
                if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
                    vm.PageButtonAreaPrev();
                else
                    vm.PrevPage();
                e.Handled = true;
                break;

            // Phím F11 / Enter / F: Bật/Tắt Toàn màn hình (Honeyview / Bandiview)
            case Key.F:
            case Key.F11:
            case Key.Enter:
                vm.ToggleFullScreen();
                e.Handled = true;
                break;

            // Phím Tab: Ẩn/Hiện thanh công cụ & thông tin Overlay (Honeyview)
            case Key.Tab:
                vm.ToggleOverlay();
                e.Handled = true;
                break;

            // Phím M: Chuyển đổi chế độ Cuộn (Vertical Webtoon) / Lật trang (Flip)
            case Key.M:
                vm.ToggleMode();
                e.Handled = true;
                break;

            // Phím R: Đổi hướng đọc Phải sang Trái (RTL) / Trái sang Phải (LTR)
            case Key.R:
                if (vm.IsFlipMode)
                {
                    vm.ToggleRtl();
                    e.Handled = true;
                }
                break;

            // Phím P: Bật/Tắt chế độ 2 trang song song (Double Page)
            case Key.P:
                if (vm.IsFlipMode)
                {
                    vm.ToggleDoublePage();
                    e.Handled = true;
                }
                break;

            // Phím Home / End: Về trang đầu tiên / Đến trang cuối cùng (Bandiview / Honeyview)
            case Key.Home:
                vm.ScrubToPage(0);
                e.Handled = true;
                break;

            case Key.End:
                if (vm.Pages.Count > 0)
                {
                    vm.ScrubToPage(vm.Pages.Count - 1);
                }
                e.Handled = true;
                break;

            // Phím Left / A / PageUp / Up: Trang trước hoặc cuộn lên 1 phân đoạn trang
            case Key.Left:
            case Key.A:
            case Key.PageUp:
            case Key.Up:
                if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
                {
                    vm.PageButtonAreaPrev();
                }
                else
                {
                    vm.PrevPage();
                }
                e.Handled = true;
                break;

            // Phím Right / D / PageDown / Down: Trang sau hoặc cuộn xuống 1 phân đoạn trang
            case Key.Right:
            case Key.D:
            case Key.PageDown:
            case Key.Down:
                if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
                {
                    vm.PageButtonAreaNext();
                }
                else
                {
                    vm.NextPage();
                }
                e.Handled = true;
                break;

            // Phím Space: Tiến trang sau (hoặc Shift+Space lùi trang trước)
            case Key.Space:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
                        vm.PageButtonAreaPrev();
                    else
                        vm.PrevPage();
                }
                else
                {
                    if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
                        vm.PageButtonAreaNext();
                    else
                        vm.NextPage();
                }
                e.Handled = true;
                break;

            // Phím Thu Phóng (Zoom): +, -, 0
            case Key.OemPlus:
            case Key.Add:
                ZoomWithAnchor(true);
                e.Handled = true;
                break;

            case Key.OemMinus:
            case Key.Subtract:
                ZoomWithAnchor(false);
                e.Handled = true;
                break;

            // Phím số 0: Kích thước gốc ảnh (Original size 1:1, zoom 100%)
            case Key.D0:
            case Key.NumPad0:
                vm.SetOriginalSizeImage();
                e.Handled = true;
                break;

            case Key.Z:
                vm.ResetZoom();
                e.Handled = true;
                break;

            // Phím số 1: Chuyển đổi tuần tự các tỉ lệ vừa khung hình: Fit Width -> Fit Height -> Fit Screen -> Stretch Full
            case Key.D1:
            case Key.NumPad1:
                vm.CycleFitMode();
                e.Handled = true;
                break;

            // Phím ` / ~: Bật/Tắt HUD hiển thị chi tiết thuộc tính ảnh (Độ phân giải, dung lượng, codec, container)
            case Key.OemTilde:
                vm.ToggleImagePropertiesHud();
                e.Handled = true;
                break;


            // Phím số 2: Chuyển đổi bố cục trang (Toggle Page Layout: Off -> Single -> Dual -> Auto)
            case Key.D2:
            case Key.NumPad2:
                vm.TogglePageLayout();
                e.Handled = true;
                break;

            // Phím 8 hoặc W: Vừa chiều rộng (Fit Width)
            case Key.W:
            case Key.D8:
            case Key.NumPad8:
                vm.SetFitMode("FitWidth");
                e.Handled = true;
                break;

            // Phím 9 hoặc H: Vừa chiều cao (Fit Height)
            case Key.H:
            case Key.D9:
            case Key.NumPad9:
                vm.SetFitMode("FitHeight");
                e.Handled = true;
                break;

            case Key.D3:
            case Key.NumPad3:
            case Key.B:
                vm.SetFitMode("FitScreen");
                e.Handled = true;
                break;

            case Key.D5:
            case Key.NumPad5:
                vm.SelectPageScrollingMode("5"); // Webtoon (Cuộn dọc)
                e.Handled = true;
                break;

            case Key.D6:
            case Key.NumPad6:
                vm.SelectPageScrollingMode("1"); // Manga Right to Left (Lật trang Phải → Trái)
                e.Handled = true;
                break;

            case Key.D7:
            case Key.NumPad7:
                vm.SelectPageScrollingMode("0"); // Manga Left to Right (Lật trang Trái → Phải)
                e.Handled = true;
                break;

            case Key.S:
                vm.SetFitMode("StretchFull");
                e.Handled = true;
                break;

            // Phím V: Tự động cuộn (Auto Scroll)
            case Key.V:
                vm.ToggleAutoScroll();
                e.Handled = true;
                break;

            // Phím C: Mở danh sách chương (Chapter Drawer)
            case Key.C:
                vm.ToggleChapterDrawer();
                e.Handled = true;
                break;
        }
    }

    private void ExecuteShortcutAction(string action, ComicReaderViewModel vm)
    {
        switch (action)
        {
            case "PageNext":
                if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
                    vm.PageButtonAreaNext();
                else
                    vm.NextPage();
                break;

            case "PagePrev":
                if (vm.IsScrollMode && !vm.IsHorizontalScrollMode)
                    vm.PageButtonAreaPrev();
                else
                    vm.PrevPage();
                break;

            case "PageFirst":
                vm.ScrubToPage(0);
                break;

            case "PageLast":
                if (vm.Pages.Count > 0)
                {
                    vm.ScrubToPage(vm.Pages.Count - 1);
                }
                break;

            case "ChapterNext":
                vm.NextChapter();
                break;

            case "ChapterPrev":
                vm.PrevChapter();
                break;

            case "FullScreen":
                vm.ToggleFullScreen();
                break;

            case "ToggleOverlay":
                vm.ToggleOverlay();
                break;

            case "ZoomIn":
                ZoomWithAnchor(true);
                break;

            case "ZoomOut":
                ZoomWithAnchor(false);
                break;

            case "ResetZoom":
                vm.ResetZoom();
                break;

            case "OriginalSize":
            case "Zoom100":
                vm.SetOriginalSizeImage();
                break;

            case "TogglePageLayout":
                vm.TogglePageLayout();
                break;

            case "FitWidth":
                vm.SetFitMode("FitWidth");
                break;

            case "FitHeight":
                vm.SetFitMode("FitHeight");
                break;

            case "FitScreen":
                vm.SetFitMode("FitScreen");
                break;

            case "StretchFull":
                vm.SetFitMode("StretchFull");
                break;

            case "WebtoonMode":
                vm.SelectPageScrollingMode("5");
                break;

            case "MangaRtlMode":
                vm.SelectPageScrollingMode("1");
                break;

            case "MangaLtrMode":
                vm.SelectPageScrollingMode("0");
                break;

            case "RtlScrollMode":
                vm.SelectPageScrollingMode("4");
                break;

            case "LtrScrollMode":
                vm.SelectPageScrollingMode("3");
                break;

            case "ChapterDrawer":
                vm.ToggleChapterDrawer();
                break;

            case "AutoScroll":
                vm.ToggleAutoScroll();
                break;

            case "OpenSettings":
                vm.OpenSettingsPopup(0);
                break;

            case "CycleFitMode":
                vm.CycleFitMode();
                break;

            case "ToggleBookmark":
                vm.ToggleBookmarkCurrentPage();
                break;

            case "BookmarkList":
                vm.OpenBookmarkListPopup();
                break;

            case "ImageProperties":
                vm.ToggleImagePropertiesHud();
                break;

            case "CloseReader":
                if (vm.HandleBackNavigation()) return;
                if (vm.IsFullScreen) vm.ToggleFullScreen();
                else vm.CloseReader();
                break;
        }

    }

    private static bool IsScrollBarOrInteractiveControl(Visual? source)
    {
        var current = source;
        while (current != null)
        {
            if (current is ScrollBar ||
                current is Thumb ||
                current is Track ||
                current is RepeatButton ||
                current is Button ||
                current is Slider ||
                current is CheckBox ||
                current is TextBox ||
                current is ComboBox ||
                current is ComboBoxItem)
            {
                return true;
            }
            current = current.GetVisualParent();
        }
        return false;
    }
}
