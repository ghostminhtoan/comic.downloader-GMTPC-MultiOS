// ====================================================================================================
// FILE SUMMARY: TextEditingProtectionService.cs
// ----------------------------------------------------------------------------------------------------
// VAI TRÒ & MỤC ĐÍCH:
//   Dịch vụ Bảo Vệ Ô Nhập Liệu & Tối Ưu Bàn Phím Ảo, Menu Ngữ Cảnh Trên Mobile (Android) & Desktop:
//   - Tự động gắn menu ngữ cảnh chuẩn hoá (Cắt, Sao chép, Dán, và CHỌN TẤT CẢ) cho 100% TextBox trong app.
//   - Khắc phục triệt để lỗi không hiện bàn phím ảo ở lần chọn đầu tiên trên Android (bằng cách ép hiển thị
//     ngay khi chạm vào ô kể cả khi ô đã có focus trước đó).
//   - Bảo vệ con trỏ ở đầu/cuối ô khi vuốt spacebar, tránh kích hoạt nhầm cử chỉ thoát app của Android.
// ====================================================================================================

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Dịch vụ quản lý ô nhập liệu (TextBox), bàn phím ảo và menu ngữ cảnh toàn hệ thống:
/// 1. Bất kỳ ô nhập ký tự nào khi chọn / highlight đều có đầy đủ menu ngữ cảnh gồm: Cắt, Sao chép, Dán, và "🔘 Chọn tất cả".
/// 2. Bấm vào bất kỳ ô nhập ký tự nào luôn luôn kích hoạt bàn phím ảo ngay ở lần chạm đầu tiên,
///    ngay cả khi ô đó đã có focus từ trước (không cần phải chọn ô khác rồi chọn lại).
/// 3. Bảo vệ con trỏ nhập liệu chống cử chỉ back/thoát app của Android khi vuốt spacebar.
/// </summary>
public static class TextEditingProtectionService
{
    private static bool _isRegistered;

    public static bool IsTypingInTextBox { get; private set; }

    public static event Action<bool>? TypingStateChanged;

    /// <summary>
    /// Delegate gọi native platform (Android Activity / WindowInsetsController / InputMethodManager)
    /// để cưỡng chế hiển thị bàn phím ảo ngay ở lần chạm đầu tiên.
    /// </summary>
    public static Action? RequestShowSoftKeyboard { get; set; }

    /// <summary>
    /// Delegate gọi native platform để ẩn bàn phím ảo khi hoàn tất nhập liệu hoặc mất focus.
    /// </summary>
    public static Action? RequestHideSoftKeyboard { get; set; }

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

    /// <summary>
    /// Kích hoạt hiển thị bàn phím ảo tức thì trên TextBox, kết hợp retry ở nhiều mức DispatcherPriority
    /// để đảm bảo bàn phím hiện ngay 100% ở lần chạm đầu tiên.
    /// </summary>
    public static void TriggerShowKeyboard(TextBox tb)
    {
        if (tb == null || tb.IsReadOnly || !tb.IsEnabled) return;

        // 1. Gọi ngay lập tức
        RequestShowSoftKeyboard?.Invoke();

        // 2. Retry ở Input priority (~40-60ms)
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (tb.IsFocused && !tb.IsReadOnly && tb.IsEnabled)
                {
                    RequestShowSoftKeyboard?.Invoke();
                }
            }
            catch { }
        }, DispatcherPriority.Input);

        // 3. Retry ở Background priority (~120-150ms) để bắt dính trường hợp Android vừa kết thúc MotionEvent
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (tb.IsFocused && !tb.IsReadOnly && tb.IsEnabled)
                {
                    RequestShowSoftKeyboard?.Invoke();
                }
            }
            catch { }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Gắn MenuFlyout tùy biến chuẩn hóa (Cắt, Sao chép, Dán, Chọn tất cả) cho TextBox nếu chưa có.
    /// </summary>
    public static void AttachCustomContextFlyout(TextBox tb)
    {
        if (tb == null) return;

        if (tb.ContextFlyout is MenuFlyout existingFlyout)
        {
            UpdateFlyoutItemsState(tb, existingFlyout);
            return;
        }

        var flyout = CreateCustomMenuFlyout(tb);
        tb.ContextFlyout = flyout;
    }

    private static MenuFlyout CreateCustomMenuFlyout(TextBox tb)
    {
        var flyout = new MenuFlyout();

        var cutItem = new MenuItem
        {
            Tag = "cut",
            Command = new SimpleTextBoxCommand(() => tb.Cut(), () => tb.CanCut && !tb.IsReadOnly && tb.SelectionStart != tb.SelectionEnd && tb.PasswordChar == '\0'),
            InputGesture = new KeyGesture(Key.X, KeyModifiers.Control)
        };

        var copyItem = new MenuItem
        {
            Tag = "copy",
            Command = new SimpleTextBoxCommand(() => tb.Copy(), () => tb.CanCopy && tb.SelectionStart != tb.SelectionEnd && tb.PasswordChar == '\0'),
            InputGesture = new KeyGesture(Key.C, KeyModifiers.Control)
        };

        var pasteItem = new MenuItem
        {
            Tag = "paste",
            Command = new SimpleTextBoxCommand(() => tb.Paste(), () => tb.CanPaste && !tb.IsReadOnly),
            InputGesture = new KeyGesture(Key.V, KeyModifiers.Control)
        };

        var selectAllItem = new MenuItem
        {
            Tag = "selectall",
            Command = new SimpleTextBoxCommand(() => tb.SelectAll(), () => !string.IsNullOrEmpty(tb.Text)),
            InputGesture = new KeyGesture(Key.A, KeyModifiers.Control)
        };

        flyout.Items.Add(cutItem);
        flyout.Items.Add(copyItem);
        flyout.Items.Add(pasteItem);
        flyout.Items.Add(new Separator());
        flyout.Items.Add(selectAllItem);

        flyout.Opening += (s, e) =>
        {
            UpdateFlyoutItemsState(tb, flyout);
        };

        UpdateFlyoutItemsState(tb, flyout);
        return flyout;
    }

    private static void UpdateFlyoutItemsState(TextBox tb, MenuFlyout flyout)
    {
        if (tb == null || flyout == null) return;

        bool isVi = LanguageService.Instance.IsVietnamese;
        bool hasText = !string.IsNullOrEmpty(tb.Text);
        bool hasSelection = tb.SelectionStart != tb.SelectionEnd;
        bool isPassword = tb.PasswordChar != '\0';

        foreach (var item in flyout.Items)
        {
            if (item is MenuItem mi && mi.Tag is string tag)
            {
                switch (tag)
                {
                    case "cut":
                        mi.Header = isVi ? "✂️ Cắt" : "✂️ Cut";
                        mi.IsEnabled = tb.CanCut && !tb.IsReadOnly && hasSelection && !isPassword;
                        (mi.Command as SimpleTextBoxCommand)?.RaiseCanExecuteChanged();
                        break;
                    case "copy":
                        mi.Header = isVi ? "📋 Sao chép" : "📋 Copy";
                        mi.IsEnabled = tb.CanCopy && hasSelection && !isPassword;
                        (mi.Command as SimpleTextBoxCommand)?.RaiseCanExecuteChanged();
                        break;
                    case "paste":
                        mi.Header = isVi ? "📥 Dán" : "📥 Paste";
                        mi.IsEnabled = tb.CanPaste && !tb.IsReadOnly;
                        (mi.Command as SimpleTextBoxCommand)?.RaiseCanExecuteChanged();
                        break;
                    case "selectall":
                        mi.Header = isVi ? "🔘 Chọn tất cả" : "🔘 Select All";
                        mi.IsEnabled = hasText;
                        (mi.Command as SimpleTextBoxCommand)?.RaiseCanExecuteChanged();
                        break;
                }
            }
        }
    }

    private class SimpleTextBoxCommand : System.Windows.Input.ICommand
    {
        private readonly Action _action;
        private readonly Func<bool>? _canExecute;

        public SimpleTextBoxCommand(Action action, Func<bool>? canExecute = null)
        {
            _action = action;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => _action?.Invoke();

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged()
        {
            try { CanExecuteChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    public static void RegisterGlobalTextBoxProtection()
    {
        if (_isRegistered) return;
        _isRegistered = true;

        try
        {
            // 1. Khi TextBox được tải vào cây giao diện (Loaded) hoặc mở menu ngữ cảnh (ContextRequested)
            Control.LoadedEvent.AddClassHandler<TextBox>((tb, _) =>
            {
                try { AttachCustomContextFlyout(tb); } catch { }
            }, RoutingStrategies.Direct | RoutingStrategies.Bubble);

            Control.ContextRequestedEvent.AddClassHandler<TextBox>((tb, _) =>
            {
                try
                {
                    AttachCustomContextFlyout(tb);
                    if (tb.ContextFlyout is MenuFlyout mf)
                    {
                        UpdateFlyoutItemsState(tb, mf);
                    }
                }
                catch { }
            }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

            // 2. Khi người dùng chạm ngón tay / nhấp chuột vào TextBox (PointerPressed & Tapped)
            // Kể cả khi TextBox ĐÃ CÓ FOCUS từ trước, vẫn cưỡng chế hiện bàn phím ảo ngay ở lần chạm đầu tiên!
            InputElement.PointerPressedEvent.AddClassHandler<TextBox>((tb, _) =>
            {
                try
                {
                    if (tb == null || tb.IsReadOnly || !tb.IsEnabled) return;
                    AttachCustomContextFlyout(tb);
                    if (!tb.IsFocused)
                    {
                        tb.Focus();
                    }
                    NotifyTypingState(true);
                    TriggerShowKeyboard(tb);
                }
                catch { }
            }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

            InputElement.TappedEvent.AddClassHandler<TextBox>((tb, _) =>
            {
                try
                {
                    if (tb == null || tb.IsReadOnly || !tb.IsEnabled) return;
                    AttachCustomContextFlyout(tb);
                    if (!tb.IsFocused)
                    {
                        tb.Focus();
                    }
                    NotifyTypingState(true);
                    TriggerShowKeyboard(tb);
                }
                catch { }
            }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

            // 3. Khi TextBox nhận focus (GotFocus) hoặc mất focus (LostFocus)
            InputElement.GotFocusEvent.AddClassHandler<TextBox>((tb, _) =>
            {
                try
                {
                    NotifyTypingState(true);
                    AttachCustomContextFlyout(tb);
                    if (tb != null && !tb.IsReadOnly && tb.IsEnabled)
                    {
                        TriggerShowKeyboard(tb);
                    }
                }
                catch { }
            }, RoutingStrategies.Bubble);

            InputElement.LostFocusEvent.AddClassHandler<TextBox>((tb, _) =>
            {
                try
                {
                    NotifyTypingState(false);
                }
                catch { }
            }, RoutingStrategies.Bubble);

            // 4. Can thiệp KeyDownEvent ở tầng Tunneling (trước khi xử lý) để bảo vệ tuyệt đối con trỏ ở đầu / cuối ô văn bản
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
