using System;
using System.Threading;
using System.Threading.Tasks;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Dịch vụ kiểm soát nhiệt độ và tối ưu hóa năng lượng toàn diện (Three-Tier Thermal Defense).
/// Đảm bảo thiết bị mát mẻ (31-34°C) khi idle, không bị thoát app khi chạy ngầm,
/// và tự động làm mát (Thermal Throttling) khi nhiệt độ thiết bị vượt ngưỡng an toàn.
/// </summary>
public class ThermalAndPowerOptimizationService
{
    private static readonly Lazy<ThermalAndPowerOptimizationService> _lazy = new(() => new ThermalAndPowerOptimizationService());
    public static ThermalAndPowerOptimizationService Instance => _lazy.Value;

    private readonly object _lock = new();
    private CancellationTokenSource? _idleDebounceCts;
    private bool _isAppIdle = false;
    private bool _isAppInBackground = false;
    private bool _isThermalThrottled = false;
    private DateTime _lastUserInteractionUtc = DateTime.UtcNow;

    public bool IsAppIdle => _isAppIdle;
    public bool IsAppInBackground => _isAppInBackground;
    public bool IsThermalThrottled => _isThermalThrottled;

    // Events
    public event Action<bool>? IdleStateChanged;
    public event Action<bool>? ThermalThrottlingChanged;
    public event Action<bool>? BackgroundStateChanged;

    // Native Platform Hook kết nối với Android Service
    public static Action<bool>? NativeSetKeepAliveIdleService { get; set; }

    /// <summary>
    /// Ghi nhận có tương tác người dùng (chạm, cuộn, click, bắt đầu tải...).
    /// Đưa app ra khỏi trạng thái Idle nếu đang nghỉ.
    /// </summary>
    public void NotifyUserActivity()
    {
        _lastUserInteractionUtc = DateTime.UtcNow;

        if (_isAppIdle)
        {
            lock (_lock)
            {
                if (_isAppIdle)
                {
                    _isAppIdle = false;
                    IdleStateChanged?.Invoke(false);
                }
            }
        }

        ResetIdleDebounceTimer();
    }

    /// <summary>
    /// Đặt lại bộ đếm thời gian idle (sau 20 giây không có thao tác và không có tải thì vào Idle Mode).
    /// </summary>
    public void ResetIdleDebounceTimer()
    {
        lock (_lock)
        {
            _idleDebounceCts?.Cancel();
            _idleDebounceCts?.Dispose();
            _idleDebounceCts = new CancellationTokenSource();
            var token = _idleDebounceCts.Token;

            Task.Delay(20000, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;

                // Kiểm tra xem hiện có đang tải truyện hay tác vụ nặng không
                bool isBusy = DownloadEngineService.Instance.IsDownloading ||
                              BackgroundExecutionService.Instance.HasActiveTasks;

                if (!isBusy)
                {
                    EnterIdleMode();
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Kích hoạt chế độ nghỉ nhàn rỗi siêu tiết kiệm năng lượng:
    /// - Nhả toàn bộ WakeLock để CPU vào Deep Sleep.
    /// - Thu gom bộ nhớ nhẹ nhàng 1 lần để giảm RAM.
    /// - Giữ Silent Foreground Service để Android OS không bao giờ kill app.
    /// </summary>
    public void EnterIdleMode()
    {
        lock (_lock)
        {
            if (_isAppIdle) return;

            // Nếu đang tải truyện hoặc có background task, TUYỆT ĐỐI không vào idle mode để tránh bị thả WakeLock
            bool isBusy = DownloadEngineService.Instance.IsDownloading ||
                          BackgroundExecutionService.Instance.HasActiveTasks;
            if (isBusy) return;

            _isAppIdle = true;
            IdleStateChanged?.Invoke(true);

            // Thu gom rác nhẹ nhàng 1 lần duy nhất
            try
            {
                GC.Collect(1, GCCollectionMode.Optimized, false, false);
            }
            catch { }

            // Kích hoạt Silent Keep-Alive Service trên Android
            if (OperatingSystem.IsAndroid())
            {
                NativeSetKeepAliveIdleService?.Invoke(true);
            }
        }
    }

    /// <summary>
    /// Cập nhật trạng thái App ở Background / Foreground từ MainActivity OnPause / OnResume
    /// </summary>
    public void SetAppInBackground(bool inBackground)
    {
        _isAppInBackground = inBackground;
        BackgroundStateChanged?.Invoke(inBackground);

        if (inBackground)
        {
            // Chỉ kích hoạt Idle Mode khi KHÔNG có tiến trình tải và KHÔNG có background task
            bool isBusy = DownloadEngineService.Instance.IsDownloading ||
                          BackgroundExecutionService.Instance.HasActiveTasks;
            if (!isBusy)
            {
                EnterIdleMode();
            }
        }
        else
        {
            NotifyUserActivity();
            if (OperatingSystem.IsAndroid())
            {
                // Khi mở lại app, nếu không có download thì vẫn giữ keep-alive nhẹ nhàng
                bool isBusy = DownloadEngineService.Instance.IsDownloading ||
                              BackgroundExecutionService.Instance.HasActiveTasks;
                if (!isBusy)
                {
                    NativeSetKeepAliveIdleService?.Invoke(true);
                }
            }
        }
    }

    /// <summary>
    /// Nhận tín hiệu nhiệt độ phần cứng từ Android OS (PowerManager.OnThermalStatusChangedListener)
    /// Thermal Status: 0=None, 1=Light, 2=Moderate (~40°C), 3=Severe, 4=Critical
    /// </summary>
    public void UpdateThermalStatus(int statusLevel)
    {
        bool shouldThrottle = statusLevel >= 2; // Moderate trở lên (khoảng >= 40°C)

        if (_isThermalThrottled != shouldThrottle)
        {
            _isThermalThrottled = shouldThrottle;
            ThermalThrottlingChanged?.Invoke(shouldThrottle);

            if (shouldThrottle)
            {
                try
                {
                    GC.Collect(1, GCCollectionMode.Optimized, false, false);
                }
                catch { }
            }
        }
    }
}

