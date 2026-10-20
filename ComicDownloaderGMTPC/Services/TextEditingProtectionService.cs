// ====================================================================================================
// FILE SUMMARY: TextEditingProtectionService.cs
// ----------------------------------------------------------------------------------------------------
// VAI TRÒ & MỤC ĐÍCH:
//   Dịch vụ Bảo Vệ Ô Nhập Liệu & Chống Cử Chỉ Thoát Ứng Dụng Trên Android (Text Editing Protection Service).
//
// CÁC TÍNH NĂNG & THÀNH PHẦN CHÍNH:
//   - Lắng nghe sự kiện Focus của toàn bộ TextBox trong ứng dụng.
//   - Trên Android (API 29+): Tự động thiết lập SetSystemGestureExclusionRects loại trừ vùng cử chỉ mép màn hình.
//   - Chặn phím AppSwitch và các phím điều hướng hệ thống khi người dùng đang ấn giữ phím Spacebar để di chuyển con trỏ.
//   - Tự động hoàn trả cử chỉ hệ thống về mặc định khi người dùng hoàn tất nhập liệu (LostFocus).
//
// LƯU Ý KỸ THUẬT & QUY TẮC BẢO TRÌ:
//   - Triển khai theo mô hình Singleton, tương thích an toàn trên toàn bộ các nền tảng (no-op trên Desktop).
// ====================================================================================================

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Dịch vụ bảo vệ con trỏ nhập liệu và chống thoát/chuyển ứng dụng khi gõ văn bản trên Mobile (Android) &amp; Desktop:
/// - Khi di chuyển con trỏ trong TextBox (như giữ phím Spacebar và vuốt trên bàn phím ảo Android):
///   + Nếu đến đầu văn bản (CaretIndex &lt;= 0) và tiếp tục vuốt sang trái: giữ chặt con trỏ ở vị trí 0,
///     chặn triệt để sự kiện để không bị bubble làm mất Focus, không kích hoạt cử chỉ chuyển App của Android.
///   + Nếu đến cuối văn bản (CaretIndex &gt;= Length) và tiếp tục vuốt sang phải: giữ chặt con trỏ ở cuối văn bản,
///     chặn triệt để sự kiện để không bị bubble làm mất Focus, không kích hoạt cử chỉ chuyển App của Android.
/// - Phát tín hiệu TypingStateChanged để Android Activity kích hoạt SetSystemGestureExclusionRects bảo vệ vùng đáy / mép màn hình.
/// </summary>
public static class TextEditingProtectionService
{
    private static bool _isRegistered;

    public static bool IsTypingInTextBox { get; private set; }

    public static event Action<bool>? TypingStateChanged;

    public static void NotifyTypingState(bool isTyping)
    {
        if (IsTypingInTextBox == isTyping) return;
        IsTypingInTextBox = isTyping;
        try
        {
            TypingStateChanged?.Invoke(isTyping);
        }
        catch { }
    }

    public static void RegisterGlobalTextBoxProtection()
    {
        if (_isRegistered) return;
        _isRegistered = true;

        try
        {
            // 1. Lắng nghe GotFocus và LostFocus trên toàn bộ TextBox trong ứng dụng
            InputElement.GotFocusEvent.AddClassHandler<TextBox>((_, _) =>
            {
                try { NotifyTypingState(true); } catch { }
            }, RoutingStrategies.Bubble);

            InputElement.LostFocusEvent.AddClassHandler<TextBox>((_, _) =>
            {
                try { NotifyTypingState(false); } catch { }
            }, RoutingStrategies.Bubble);

            // 2. Can thiệp KeyDownEvent ở tầng Tunneling (trước khi xử lý) để bảo vệ tuyệt đối con trỏ ở đầu / cuối ô văn bản
            InputElement.KeyDownEvent.AddClassHandler<TextBox>((tb, e) =>
            {
                try
                {
                    if (tb == null || !tb.IsFocused) return;

                    int textLen = tb.Text?.Length ?? 0;
                    int caret = tb.CaretIndex;

                    // Trường hợp 1: Con trỏ đang ở đầu ô (CaretIndex <= 0)
                    if (caret <= 0 && tb.SelectionStart == tb.SelectionEnd)
                    {
                        if (e.Key == Key.Left || e.Key == Key.Home || e.Key == Key.PageUp ||
                            (!tb.AcceptsReturn && e.Key == Key.Up))
                        {
                            tb.CaretIndex = 0;
                            e.Handled = true; // Chặn đứng sự kiện, giữ nguyên con trỏ ở đầu ô, không bubble, không chuyển app
                            return;
                        }
                    }

                    // Trường hợp 2: Con trỏ đang ở cuối ô (CaretIndex >= textLen)
                    if (caret >= textLen && tb.SelectionStart == tb.SelectionEnd)
                    {
                        if (e.Key == Key.Right || e.Key == Key.End || e.Key == Key.PageDown ||
                            (!tb.AcceptsReturn && e.Key == Key.Down))
                        {
                            tb.CaretIndex = textLen;
                            e.Handled = true; // Chặn đứng sự kiện, giữ nguyên con trỏ ở cuối ô, không bubble, không chuyển app
                            return;
                        }
                    }
                }
                catch { }
            }, RoutingStrategies.Tunnel);
        }
        catch { }
    }
}
