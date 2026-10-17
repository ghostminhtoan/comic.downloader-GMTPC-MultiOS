using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
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
    Label = "📖 Đọc Truyện - GMTPC",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = false,
    TaskAffinity = "com.CompanyName.ComicDownloaderGMTPC.reader",
    LaunchMode = LaunchMode.SingleTask,
    DocumentLaunchMode = DocumentLaunchMode.IntoExisting,
    AutoRemoveFromRecents = false,
    SupportsPictureInPicture = true,
    ResizeableActivity = true,
    Exported = false,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.Density | ConfigChanges.FontScale)]
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
        LibraryScannerService.AndroidGetExternalStoragePaths ??= () => AndroidStorageHelper.GetExternalStoragePaths(this);

        ComicReaderService.AndroidSetOrientationMode = ApplyOrientationMode;
        ComicReaderService.AndroidApplyDisplaySettings = ApplyDisplaySettings;
        ComicReaderViewModel.AndroidOverlayVisibilityChanged += OnAndroidOverlayVisibilityChanged;

        BackRequested += OnActivityBackRequested;

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

        if (activeVm == null && intent != null)
        {
            // Khôi phục phiên đọc nếu người dùng bấm vào thẻ Đa nhiệm (Recents) sau khi tiến trình nền bị Android thu hồi
            string? comicRoot = intent.GetStringExtra(ExtraComicRootPath);
            string? chapterPath = intent.GetStringExtra(ExtraChapterLocalPath);

            if (!string.IsNullOrWhiteSpace(comicRoot))
            {
                activeVm = new ComicReaderViewModel();
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
                return;
            }
        }

        AttachViewModel(activeVm);
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

    private void CloseReaderTask()
    {
        if (_isClosingTask) return;
        _isClosingTask = true;

        RunOnUiThread(() =>
        {
            try
            {
                var vm = _subscribedVm;
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

    protected override void OnDestroy()
    {
        BackRequested -= OnActivityBackRequested;
        ComicReaderViewModel.AndroidOverlayVisibilityChanged -= OnAndroidOverlayVisibilityChanged;

        if (!_isClosingTask && _subscribedVm != null)
        {
            try
            {
                var vm = _subscribedVm;
                AttachViewModel(null);
                vm.Cleanup();
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

