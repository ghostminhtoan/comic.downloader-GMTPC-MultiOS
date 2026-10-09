using System;
using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Views.Accessibility;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.Android;

[Service(Label = "Comic Downloader GMTPC - Tự Động Dán Link", Permission = global::Android.Manifest.Permission.BindAccessibilityService, Exported = true)]
[IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
[MetaData("android.accessibilityservice", Resource = "@xml/accessibility_service_config")]
public class ComicClipboardAccessibilityService : AccessibilityService
{
    public static ComicClipboardAccessibilityService? Instance { get; private set; }
    private ClipboardManager? _clipboardManager;
    private ClipboardManager.IOnPrimaryClipChangedListener? _clipListener;
    private string _lastClipText = string.Empty;

    public static bool IsServiceRunning => Instance != null;

    public override void OnCreate()
    {
        base.OnCreate();
        Instance = this;
        SetupClipboardListener();
    }

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        Instance = this;
        SetupClipboardListener();
        global::Android.Util.Log.Info("ComicGMTPC_Access", "AccessibilityService đã kết nối và sẵn sàng theo dõi clipboard real-time.");
    }

    private void SetupClipboardListener()
    {
        try
        {
            if (_clipboardManager == null)
            {
                _clipboardManager = (ClipboardManager?)GetSystemService(ClipboardService);
            }

            if (_clipboardManager != null && _clipListener == null)
            {
                _clipListener = new ClipChangedListener(this);
                _clipboardManager.AddPrimaryClipChangedListener(_clipListener);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC_Access", $"Lỗi cài đặt ClipListener: {ex.Message}");
        }
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
        // Khi người dùng thao tác ở bất kỳ app nào (copy, chạm, chuyển màn hình...), kiểm tra clipboard nếu AutoPaste đang bật
        CheckClipboardAndNotify();
    }

    public void CheckClipboardAndNotify()
    {
        try
        {
            if (!BackgroundExecutionService.IsAutoPasteEnabled) return;

            if (_clipboardManager == null)
            {
                _clipboardManager = (ClipboardManager?)GetSystemService(ClipboardService);
            }

            if (_clipboardManager != null && _clipboardManager.HasPrimaryClip)
            {
                var clip = _clipboardManager.PrimaryClip;
                if (clip != null && clip.ItemCount > 0)
                {
                    var item = clip.GetItemAt(0);
                    string? text = item?.CoerceToText(this)?.ToString();
                    if (!string.IsNullOrWhiteSpace(text) && text != _lastClipText)
                    {
                        _lastClipText = text;
                        if (text.Contains("http://", StringComparison.OrdinalIgnoreCase) || 
                            text.Contains("https://", StringComparison.OrdinalIgnoreCase))
                        {
                            global::Android.Util.Log.Info("ComicGMTPC_Access", $"[AutoPaste Real-time] Bắt được link mới từ Accessibility: {text}");
                            BackgroundExecutionService.NativeOnClipboardTextChanged?.Invoke(text);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC_Access", $"Lỗi CheckClipboardAndNotify: {ex.Message}");
        }
    }

    public override void OnInterrupt()
    {
    }

    public override void OnDestroy()
    {
        try
        {
            if (_clipboardManager != null && _clipListener != null)
            {
                _clipboardManager.RemovePrimaryClipChangedListener(_clipListener);
                _clipListener = null;
            }
        }
        catch { }
        Instance = null;
        base.OnDestroy();
    }

    private class ClipChangedListener : Java.Lang.Object, ClipboardManager.IOnPrimaryClipChangedListener
    {
        private readonly ComicClipboardAccessibilityService _service;
        public ClipChangedListener(ComicClipboardAccessibilityService service)
        {
            _service = service;
        }

        public void OnPrimaryClipChanged()
        {
            _service.CheckClipboardAndNotify();
        }
    }
}

