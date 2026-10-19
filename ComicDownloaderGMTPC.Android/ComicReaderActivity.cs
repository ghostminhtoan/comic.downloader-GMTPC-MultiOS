using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using Avalonia.Android;
using ComicDownloaderGMTPC.Services;
using ComicDownloaderGMTPC.ViewModels;
using ComicDownloaderGMTPC.Views;

namespace ComicDownloaderGMTPC.Android;

/// <summary>
/// Activity đọc truyện FullScreen hoàn toàn độc lập (Multiple Tasks / New Task Launch Mode).
/// Khi mở trên Android, Activity này chạy trên một Task riêng biệt (TaskAffinity riêng),
/// giúp hiển thị tách thành 2 thẻ (card) ứng dụng độc lập khi bấm nút Đa nhiệm (Recents).
/// </summary>
[Activity(
    Name = "com.CompanyName.ComicDownloaderGMTPC.ComicReaderActivity",
    Label = "ComicDownloaderGMTPC",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = false,
    TaskAffinity = "com.CompanyName.ComicDownloaderGMTPC.reader",
    LaunchMode = LaunchMode.SingleTask,
    DocumentLaunchMode = DocumentLaunchMode.IntoExisting,
    AutoRemoveFromRecents = false,
    SupportsPictureInPicture = true,
    ResizeableActivity = true,
    Exported = true,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.Density | ConfigChanges.FontScale)]
// === CÁC INTENT FILTER CHO PHÉP MỞ TRỰC TIẾP TRÌNH ĐỌC VỚI TỆP TRUYỆN & HÌNH ẢNH (ZERO DOUBLE LOAD SCREEN) ===
// 1. MIME Types: Sách & Tệp nén truyện
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "application/pdf")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "application/zip")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "application/x-zip-compressed")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "application/x-zip")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "application/x-rar-compressed")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "application/rar")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "application/x-rar")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "application/vnd.rar")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "application/x-cbz")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "application/vnd.comicbook+zip")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "application/x-cbr")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "application/vnd.comicbook-rar")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "application/x-mobipocket-ebook")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "application/vnd.amazon.mobi8-ebook")]
// 2. MIME Types: Hình ảnh (.gif, .webp, .jpg, .png, .bmp)
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "image/gif")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "image/webp")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "image/jpeg")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "image/jpg")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "image/png")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "image/bmp")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "image/x-ms-bmp")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataMimeType = "image/*")]
// 3. Scheme file:// cho toàn bộ định dạng Sách & Ảnh
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.pdf")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.PDF")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.zip")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.ZIP")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.rar")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.RAR")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.cbz")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.CBZ")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.cbr")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.CBR")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.mobi")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.MOBI")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.gif")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.GIF")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.webp")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.WEBP")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.jpg")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.JPG")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.jpeg")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.JPEG")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.png")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.PNG")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.bmp")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "file", DataHost = "*", DataPathPattern = ".*\\.BMP")]
// 4. Scheme content:// cho toàn bộ định dạng Sách & Ảnh
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.pdf")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.PDF")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.zip")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.ZIP")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.rar")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.RAR")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.cbz")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.CBZ")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.cbr")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.CBR")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.mobi")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.MOBI")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.gif")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.GIF")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.webp")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.WEBP")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.jpg")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.JPG")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.jpeg")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.JPEG")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.png")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.PNG")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.bmp")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataHost = "*", DataPathPattern = ".*\\.BMP")]
// 5. Generic application/octet-stream
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.pdf")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.zip")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.rar")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.cbz")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.cbr")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.mobi")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.gif")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.webp")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.jpg")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.jpeg")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.png")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.bmp")]
// 6. ActionSend (Chia Sẻ tệp sang app)
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/pdf")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/zip")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/x-zip-compressed")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/x-rar-compressed")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/rar")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/x-cbz")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/x-cbr")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/x-mobipocket-ebook")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "image/gif")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "image/webp")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "image/jpeg")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "image/jpg")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "image/png")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "image/bmp")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "image/*")]
public class ComicReaderActivity : AvaloniaActivity
{
    public const string ExtraComicRootPath = "comic_root_path";
    public const string ExtraChapterLocalPath = "chapter_local_path";
    public const string ExtraComicTitle = "comic_title";
    public const string ExtraChapterTitle = "chapter_title";

    public static ComicReaderActivity? CurrentInstance { get; private set; }

    private ComicReaderControl? _readerControl;
    private ComicReaderViewModel? _subscribedVm;
    private bool _isClosingTask;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        CurrentInstance = this;

        AndroidStorageHelper.WakeUpStorage(this);
        AndroidStorageHelper.RequestAppStoragePermissions(this);
        LibraryScannerService.AndroidGetExternalStoragePaths ??= () => AndroidStorageHelper.GetExternalStoragePaths(this);

        ComicReaderService.AndroidSetOrientationMode = ApplyOrientationMode;
        ComicReaderService.AndroidApplyDisplaySettings = ApplyDisplaySettings;
        BackRequested += OnActivityBackRequested;
        ComicReaderViewModel.AndroidOverlayVisibilityChanged += OnAndroidOverlayVisibilityChanged;
        Services.TextEditingProtectionService.TypingStateChanged += OnTypingStateChanged;

        _readerControl = new ComicReaderControl();
        Content = _readerControl;

        BindOrRestoreReaderSession(Intent);
        ApplyImmersiveFullScreen(_subscribedVm?.IsFullScreen ?? true);
        if (_subscribedVm != null)
        {
            ApplyOrientationMode(_subscribedVm.OrientationLockMode);
            ApplyDisplaySettings(_subscribedVm.IsAutoSystemBrightness, _subscribedVm.SystemBrightnessPercent, _subscribedVm.IsKeepScreenOn);
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        BindOrRestoreReaderSession(intent);
        ApplyImmersiveFullScreen(_subscribedVm?.IsFullScreen ?? true);
    }

    protected override void OnResume()
    {
        base.OnResume();
        CurrentInstance = this;
        AndroidStorageHelper.WakeUpStorage(this);
        ComicReaderService.AndroidSetOrientationMode = ApplyOrientationMode;
        ComicReaderService.AndroidApplyDisplaySettings = ApplyDisplaySettings;

        if (ComicReaderService.Instance.ActiveExternalReaderViewModel != null &&
            !ReferenceEquals(_subscribedVm, ComicReaderService.Instance.ActiveExternalReaderViewModel))
        {
            BindOrRestoreReaderSession(Intent);
        }
        ApplyImmersiveFullScreen(_subscribedVm?.IsFullScreen ?? true);
        if (_subscribedVm != null)
        {
            ApplyOrientationMode(_subscribedVm.OrientationLockMode);
            ApplyDisplaySettings(_subscribedVm.IsAutoSystemBrightness, _subscribedVm.SystemBrightnessPercent, _subscribedVm.IsKeepScreenOn);
        }
    }

    private void ApplyOrientationMode(int mode)
    {
        RunOnUiThread(() =>
        {
            try
            {
                RequestedOrientation = mode switch
                {
                    1 => ScreenOrientation.SensorLandscape,
                    2 => ScreenOrientation.SensorPortrait,
                    _ => ScreenOrientation.Unspecified
                };
            }
            catch { }
        });
    }

    private void ApplyDisplaySettings(bool autoBrightness, double brightnessPercent, bool keepScreenOn)
    {
        RunOnUiThread(() =>
        {
            try
            {
                var window = Window;
                if (window == null) return;

                if (keepScreenOn)
                {
                    window.AddFlags(WindowManagerFlags.KeepScreenOn);
                }
                else
                {
                    window.ClearFlags(WindowManagerFlags.KeepScreenOn);
                }

                var lp = window.Attributes;
                if (lp != null)
                {
                    lp.ScreenBrightness = autoBrightness
                        ? WindowManagerLayoutParams.BrightnessOverrideNone
                        : (float)Math.Clamp(brightnessPercent / 100.0, 0.02, 1.0);
                    window.Attributes = lp;
                }
            }
            catch { }
        });
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        try
        {
            base.OnWindowFocusChanged(hasFocus);
            if (hasFocus)
            {
                ApplyImmersiveFullScreen(_subscribedVm?.IsFullScreen ?? true);
            }
        }
        catch { }
    }

    private void BindOrRestoreReaderSession(Intent? intent)
    {
        var activeVm = ComicReaderService.Instance.ActiveExternalReaderViewModel;

        // 1. Nếu mở từ bên trong app (đã gán ViewModel sẵn)
        if (activeVm != null)
        {
            AttachViewModel(activeVm);
            return;
        }

        if (intent != null)
        {
            // 2. Mở trực tiếp từ bên ngoài (ActionView / ActionSend): Tải thẳng Reader ngay từ lần đầu!
            string? action = intent.Action;
            if (action == Intent.ActionView || action == Intent.ActionSend)
            {
                OpenIncomingFileDirectly(intent);
                return;
            }

            // 3. Khôi phục phiên đọc nếu người dùng bấm vào thẻ Đa nhiệm (Recents) sau khi tiến trình nền bị Android thu hồi
            string? comicRoot = intent.GetStringExtra(ExtraComicRootPath);
            string? chapterPath = intent.GetStringExtra(ExtraChapterLocalPath);

            if (!string.IsNullOrWhiteSpace(comicRoot))
            {
                RestoreReaderSessionFromExtras(comicRoot, chapterPath);
                return;
            }
        }

        AttachViewModel(null);
    }

    /// <summary>
    /// Mở trực tiếp tệp truyện hoặc hình ảnh khi người dùng bấm vào tệp từ File Manager hoặc nút Chia Sẻ.
    /// Tải thẳng Reader mà không phải đi qua MainActivity, loại bỏ hoàn toàn 2 lần load screen.
    /// </summary>
    private void OpenIncomingFileDirectly(Intent intent)
    {
        string? action = intent.Action;
        global::Android.Net.Uri? uri = intent.Data;
#pragma warning disable CA1422
        if (uri == null && action == Intent.ActionSend)
        {
            uri = intent.GetParcelableExtra(Intent.ExtraStream) as global::Android.Net.Uri;
        }
#pragma warning restore CA1422

        if (uri == null)
        {
            Toast.MakeText(this, "Không tìm thấy dữ liệu tệp được yêu cầu mở.", ToastLength.Short)?.Show();
            return;
        }

        var activeVm = new ComicReaderViewModel();
        ComicReaderService.Instance.ActiveExternalReaderViewModel = activeVm;
        AttachViewModel(activeVm);
        activeVm.IsLoadingChapter = true;
        activeVm.ChapterLoadingStatusText = "⏳ Đang mở tệp...";

        _ = Task.Run(async () =>
        {
            try
            {
                AndroidStorageHelper.WakeUpStorage(this);
                string? resolvedFilePath = await AndroidStorageHelper.ResolveUriToFilePathAsync(this, uri);

                if (string.IsNullOrWhiteSpace(resolvedFilePath) || !File.Exists(resolvedFilePath))
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        activeVm.IsLoadingChapter = false;
                        activeVm.ShowTransientToast("Không thể đọc tệp truyện hoặc hình ảnh.");
                        Toast.MakeText(this, "Không thể đọc tệp truyện hoặc hình ảnh.", ToastLength.Short)?.Show();
                    });
                    return;
                }

                var comic = LibraryScannerService.Instance.CreateQuickComicFromPath(resolvedFilePath);
                if ((comic == null || comic.Chapters.Count == 0) && resolvedFilePath.Contains("/storage/"))
                {
                    AndroidStorageHelper.WakeUpStorage(this);
                    await Task.Delay(150);
                    comic = LibraryScannerService.Instance.CreateQuickComicFromPath(resolvedFilePath);
                }

                if (comic != null && comic.Chapters.Count > 0)
                {
                    ReadProgressStore.Instance.RegisterReadComic(comic.RootFolderPath, touchTimestamp: true);
                    var targetChap = comic.GetBestResumeChapter() ?? comic.Chapters[0];

                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        try
                        {
                            await activeVm.InitializeAsync(comic, targetChap);
                            UpdateRecentsTaskCardTitle(activeVm);

                            var sharedMainVm = App.TryGetSharedMainViewModel();
                            if (sharedMainVm?.LibraryVm != null)
                            {
                                var libVm = sharedMainVm.LibraryVm;
                                var existing = libVm.AllComics.FirstOrDefault(c =>
                                    string.Equals(c.RootFolderPath, comic.RootFolderPath, StringComparison.OrdinalIgnoreCase));
                                if (existing == null)
                                {
                                    libVm.AllComics.Insert(0, comic);
                                    _ = LibraryScannerService.Instance.LoadCoversAsync(new[] { comic }, System.Threading.CancellationToken.None);
                                }
                                libVm.ApplyFilter();
                            }
                        }
                        catch (Exception ex)
                        {
                            global::Android.Util.Log.Error("ComicGMTPC", $"Lỗi nạp truyện vào Reader: {ex.Message}");
                        }
                    });
                }
                else
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        activeVm.IsLoadingChapter = false;
                        activeVm.ShowTransientToast("Định dạng tệp không được hỗ trợ hoặc không có trang nào.");
                        Toast.MakeText(this, "Định dạng tệp không được hỗ trợ hoặc không có trang nào.", ToastLength.Short)?.Show();
                    });
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("ComicGMTPC", $"Lỗi xử lý file intent: {ex.Message}");
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    activeVm.IsLoadingChapter = false;
                    activeVm.ShowTransientToast($"Lỗi mở tệp: {ex.Message}");
                });
            }
        });
    }

    private void RestoreReaderSessionFromExtras(string comicRoot, string? chapterPath)
    {
        var activeVm = new ComicReaderViewModel();
        ComicReaderService.Instance.ActiveExternalReaderViewModel = activeVm;
        AttachViewModel(activeVm);

        _ = Task.Run(async () =>
        {
            try
            {
                var comic = LibraryScannerService.Instance.CreateQuickComicFromPath(comicRoot);
                if ((comic == null || comic.Chapters.Count == 0) && comicRoot.Contains("/storage/"))
                {
                    AndroidStorageHelper.WakeUpStorage(CurrentInstance?.ApplicationContext ?? CurrentInstance);
                    await Task.Delay(150);
                    comic = LibraryScannerService.Instance.CreateQuickComicFromPath(comicRoot);
                }

                if (comic != null && comic.Chapters.Count > 0)
                {
                    var targetChap = (!string.IsNullOrWhiteSpace(chapterPath)
                        ? comic.Chapters.FirstOrDefault(c => string.Equals(c.LocalPath, chapterPath, StringComparison.OrdinalIgnoreCase))
                        : null)
                        ?? comic.GetBestResumeChapter()
                        ?? comic.Chapters[0];

                    await activeVm.InitializeAsync(comic, targetChap);
                }
                else
                {
                    activeVm.ShowTransientToast("Không thể nạp tệp truyện từ thẻ nhớ. Vui lòng mở lại từ Thư viện.");
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("ComicGMTPC", $"Lỗi khôi phục phiên đọc: {ex.Message}");
            }
        });
    }

    private void AttachViewModel(ComicReaderViewModel? vm)
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.FullScreenChangedRequested -= OnVmFullScreenChangedRequested;
            _subscribedVm.CloseRequested -= OnVmCloseRequested;
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm = null;
        }

        _subscribedVm = vm;

        if (_readerControl != null)
        {
            _readerControl.DataContext = vm;
        }

        if (vm != null)
        {
            vm.FullScreenChangedRequested += OnVmFullScreenChangedRequested;
            vm.CloseRequested += OnVmCloseRequested;
            vm.PropertyChanged += OnVmPropertyChanged;
            UpdateRecentsTaskCardTitle(vm);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ComicReaderViewModel vm &&
            (e.PropertyName == nameof(ComicReaderViewModel.ComicTitle) ||
             e.PropertyName == nameof(ComicReaderViewModel.ChapterTitle)))
        {
            RunOnUiThread(() => UpdateRecentsTaskCardTitle(vm));
        }
    }

    private void UpdateRecentsTaskCardTitle(ComicReaderViewModel vm)
    {
        try
        {
            string comicTitle = string.IsNullOrWhiteSpace(vm.ComicTitle) ? "Đọc Truyện" : vm.ComicTitle;
            string chapTitle = string.IsNullOrWhiteSpace(vm.ChapterTitle) ? string.Empty : $" • {vm.ChapterTitle}";
            string cardLabel = $"📖 {comicTitle}{chapTitle}";
            Title = cardLabel;

            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                var taskDesc = new ActivityManager.TaskDescription.Builder()
                    .SetLabel(cardLabel)
                    .Build();
                SetTaskDescription(taskDesc);
            }
            else
            {
#pragma warning disable CA1422
                SetTaskDescription(new ActivityManager.TaskDescription(cardLabel));
#pragma warning restore CA1422
            }
        }
        catch { }
    }

    private void OnVmFullScreenChangedRequested(bool fullScreen)
    {
        RunOnUiThread(() => ApplyImmersiveFullScreen(fullScreen));
    }

#pragma warning disable CA1422, CS0618
    private void ApplyImmersiveFullScreen(bool fullScreen)
    {
        try
        {
            var window = Window;
            if (window == null) return;

            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                window.SetDecorFitsSystemWindows(!fullScreen);
                var controller = window.InsetsController;
                if (controller != null)
                {
                    if (fullScreen)
                    {
                        controller.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
                        controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                    }
                    else
                    {
                        controller.Show(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
                    }
                }
            }
            else
            {
                var decorView = window.DecorView;
                if (decorView != null)
                {
                    if (fullScreen)
                    {
                        decorView.SystemUiVisibility = (StatusBarVisibility)(
                            SystemUiFlags.ImmersiveSticky |
                            SystemUiFlags.Fullscreen |
                            SystemUiFlags.HideNavigation |
                            SystemUiFlags.LayoutStable |
                            SystemUiFlags.LayoutFullscreen |
                            SystemUiFlags.LayoutHideNavigation);
                    }
                    else
                    {
                        decorView.SystemUiVisibility = StatusBarVisibility.Visible;
                    }
                }
            }
        }
        catch { }
    }
#pragma warning restore CA1422, CS0618

    private void OnVmCloseRequested()
    {
        CloseReaderTask();
    }

    private void OnActivityBackRequested(object? sender, AndroidBackRequestedEventArgs e)
    {
        if (_subscribedVm != null && _subscribedVm.HandleBackNavigation())
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        CloseReaderTask();
    }

#pragma warning disable CA1422
    public override void OnBackPressed()
    {
        if (_subscribedVm != null && _subscribedVm.HandleBackNavigation())
        {
            return;
        }

        CloseReaderTask();
    }
#pragma warning restore CA1422

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (keyCode == Keycode.Back && e?.Action == KeyEventActions.Down)
        {
            if (_subscribedVm != null && _subscribedVm.HandleBackNavigation())
            {
                return true;
            }

            CloseReaderTask();
            return true;
        }

        if (e?.Action == KeyEventActions.Down && _subscribedVm != null)
        {
            if (keyCode == Keycode.VolumeUp)
            {
                RunOnUiThread(() => _subscribedVm.PrevPage());
                return true;
            }
            if (keyCode == Keycode.VolumeDown)
            {
                RunOnUiThread(() => _subscribedVm.NextPage());
                return true;
            }
        }

        return base.OnKeyDown(keyCode, e);
    }

    public override bool OnKeyUp(Keycode keyCode, KeyEvent? e)
    {
        if (keyCode == Keycode.VolumeUp || keyCode == Keycode.VolumeDown)
        {
            return true;
        }
        return base.OnKeyUp(keyCode, e);
    }

    protected override void OnPause()
    {
        base.OnPause();
        try
        {
            _subscribedVm?.SaveCurrentReadingSession();
        }
        catch { }
    }

    private void CloseReaderTask()
    {
        if (_isClosingTask) return;
        _isClosingTask = true;

        RunOnUiThread(() =>
        {
            try
            {
                var vm = _subscribedVm;
                try { vm?.SaveCurrentReadingSession(); } catch { }
                AttachViewModel(null);
                vm?.Cleanup();

                if (ReferenceEquals(ComicReaderService.Instance.ActiveExternalReaderViewModel, vm))
                {
                    ComicReaderService.Instance.ActiveExternalReaderViewModel = null;
                }

                ComicReaderService.Instance.ActiveExternalReaderClosedCallback?.Invoke();
            }
            catch { }

            try
            {
                FinishAndRemoveTask();
            }
            catch
            {
                try { Finish(); } catch { }
            }
        });
    }

    private void OnAndroidOverlayVisibilityChanged(bool isOverlayVisible)
    {
        // QUY TẮC BẤT BIẾN KHUNG HÌNH (WINDOW/LINUX PARITY):
        // Giống như Windows và Linux, khi bật/tắt thanh công cụ (Overlay Tool),
        // Android Reader Activity DUY TRÌ 100% chế độ Immersive Sticky FullScreen cố định.
        // Tuyệt đối không thoát toàn màn hình để tránh làm co giật Viewport / Bounds,
        // triệt tiêu hoàn toàn hiện tượng lệch trang, nhảy trang hoặc co méo ảnh.
    }

    private void OnTypingStateChanged(bool isTyping)
    {
        RunOnUiThread(() =>
        {
            UpdateSystemGestureExclusion(isTyping);
        });
    }

    private void UpdateSystemGestureExclusion(bool isTyping)
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29) && Window?.DecorView != null)
            {
                if (isTyping)
                {
                    var decorView = Window.DecorView;
                    int width = decorView.Width;
                    int height = decorView.Height;
                    if (width <= 0 || height <= 0)
                    {
                        var dm = Resources?.DisplayMetrics;
                        width = dm?.WidthPixels ?? 1080;
                        height = dm?.HeightPixels ?? 2400;
                    }

                    int keyboardTop = Math.Max(0, (int)(height * 0.45));
                    int edgeWidth = (int)((Resources?.DisplayMetrics?.Density ?? 2.5f) * 120);
                    int bottomBarHeight = (int)((Resources?.DisplayMetrics?.Density ?? 2.5f) * 75);

                    var rects = new List<global::Android.Graphics.Rect>
                    {
                        new global::Android.Graphics.Rect(0, keyboardTop, edgeWidth, height),
                        new global::Android.Graphics.Rect(Math.Max(0, width - edgeWidth), keyboardTop, width, height),
                        new global::Android.Graphics.Rect(0, Math.Max(0, height - bottomBarHeight), width, height)
                    };

                    decorView.SystemGestureExclusionRects = rects;
                }
                else
                {
                    Window.DecorView.SystemGestureExclusionRects = new List<global::Android.Graphics.Rect>();
                }
            }
        }
        catch { }
    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (Services.TextEditingProtectionService.IsTypingInTextBox && e != null)
        {
            if (e.KeyCode == Keycode.AppSwitch)
            {
                return true;
            }

            if (e.KeyCode == Keycode.DpadLeft || e.KeyCode == Keycode.DpadRight ||
                e.KeyCode == Keycode.DpadUp || e.KeyCode == Keycode.DpadDown ||
                e.KeyCode == Keycode.Tab)
            {
                base.DispatchKeyEvent(e);
                return true;
            }
        }

        return base.DispatchKeyEvent(e);
    }

    protected override void OnDestroy()
    {
        BackRequested -= OnActivityBackRequested;
        ComicReaderViewModel.AndroidOverlayVisibilityChanged -= OnAndroidOverlayVisibilityChanged;
        Services.TextEditingProtectionService.TypingStateChanged -= OnTypingStateChanged;

        if (!_isClosingTask && _subscribedVm != null)
        {
            try
            {
                var vm = _subscribedVm;
                try { vm?.SaveCurrentReadingSession(); } catch { }
                AttachViewModel(null);
                vm?.Cleanup();
                if (ReferenceEquals(ComicReaderService.Instance.ActiveExternalReaderViewModel, vm))
                {
                    ComicReaderService.Instance.ActiveExternalReaderViewModel = null;
                }
                ComicReaderService.Instance.ActiveExternalReaderClosedCallback?.Invoke();
            }
            catch { }
        }

        if (CurrentInstance == this)
        {
            CurrentInstance = null;
        }

        base.OnDestroy();
    }
}

