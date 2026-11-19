using System;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Dịch vụ điều khiển xoay màn hình và chế độ Immersive FullScreen độc lập cho Android.
/// </summary>
public static class AndroidScreenControlService
{
    /// <summary>
    /// Điều khiển hướng màn hình trên Android:
    /// 0 = Unspecified / Sensor tự động
    /// 1 = SensorLandscape (Khóa xoay ngang)
    /// 2 = SensorPortrait (Khóa xoay dọc)
    /// </summary>
    public static Action<int>? SetOrientationMode { get; set; }

    /// <summary>
    /// Bật/Tắt chế độ Toàn màn hình Immersive Sticky (ẩn/hiện Status Bar và Navigation Bar) trên Android.
    /// </summary>
    public static Action<bool>? SetImmersiveFullScreen { get; set; }
}
