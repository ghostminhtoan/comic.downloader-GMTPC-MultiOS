using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Avalonia;
using Avalonia.Android;

namespace ComicDownloaderGMTPC.Android;

[Activity(
    Label = "ComicDownloaderGMTPC",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    SupportsPictureInPicture = true,
    ResizeableActivity = true,
    AllowEmbedded = true,
    Exported = true,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.Density | ConfigChanges.FontScale)]
public class MainActivity : AvaloniaMainActivity
{
    private const int StoragePermissionRequestCode = 1001;
    public static MainActivity? CurrentInstance { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // 1. Bảo vệ toàn diện chống crash unhandled ngay từ dòng đầu tiên của lifecycle
        AndroidEnvironment.UnhandledExceptionRaiser += (sender, args) =>
        {
            try
            {
                global::Android.Util.Log.Error("ComicGMTPC_Crash", $"[AndroidEnvironment Unhandled] {args.Exception}");
                ApplicationCrashLogger.WriteCrashLog("MainActivity.AndroidEnvironment", args.Exception?.ToString());
                args.Handled = true;
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            try
            {
                var ex = args.ExceptionObject as Exception;
                global::Android.Util.Log.Error("ComicGMTPC_Crash", $"[AppDomain Unhandled] {ex?.Message}\n{ex?.StackTrace}");
                ApplicationCrashLogger.WriteCrashLog("MainActivity.AppDomain", ex?.ToString());
            }
            catch { }
        };
        TaskScheduler.UnobservedTaskException += (sender, args) =>
        {
            try
            {
                global::Android.Util.Log.Warn("ComicGMTPC_Task", $"[UnobservedTask] {args.Exception?.Message}");
                args.SetObserved();
            }
            catch { }
        };

        if (ApplicationCrashLogger.StartupException != null)
        {
            LaunchCrashReportActivity(ApplicationCrashLogger.StartupException, "Application.Startup");
            try { base.OnCreate(savedInstanceState); } catch { }
            Finish();
            return;
        }

        CurrentInstance = this;
        try
        {
            AndroidStorageHelper.WakeUpStorage(this);
        }
        catch (Exception ex)
        {
            ApplicationCrashLogger.WriteCrashLog("MainActivity.WakeUpStorage", ex.ToString());
        }

        try
        {
            // Cung cấp danh sách đường dẫn Root của toàn bộ thẻ nhớ ngoài (SD Card)
            Services.LibraryScannerService.AndroidGetExternalStoragePaths = () =>
                AndroidStorageHelper.GetExternalStoragePaths(CurrentInstance?.ApplicationContext ?? CurrentInstance);
        }
        catch { }

        try
        {
            base.OnCreate(savedInstanceState);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ComicGMTPC_Crash", $"[MainActivity.base.OnCreate Crash] {ex}");
            ApplicationCrashLogger.WriteCrashLog("MainActivity.base.OnCreate", ex.ToString());
            LaunchCrashReportActivity(ex, "MainActivity.base.OnCreate");
            Finish();
            return;
        }

        Window?.SetSoftInputMode(SoftInput.AdjustResize);

        // Đăng ký bridge chạy ngầm, làm mát và bảo vệ thiết bị khi idle
        Services.ThermalAndPowerOptimizationService.NativeSetKeepAliveIdleService = (enable) =>
        {
            var ctx = ApplicationContext ?? this;
            if (enable)
            {
                ComicBackgroundService.StartOrUpdateIdleKeepAlive(ctx);
            }
        };

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            try
            {
                var pm = (PowerManager?)GetSystemService(PowerService);
                pm?.AddThermalStatusListener(new ThermalStatusListener(status =>
                {
                    Services.ThermalAndPowerOptimizationService.Instance.UpdateThermalStatus((int)status);
                }));
            }
            catch { }
        }

        // Đăng ký bridge chạy ngầm và bong bóng
        Services.BackgroundExecutionService.NativeStartOrUpdateForegroundNotification = (taskId, title, text, progress, isDownloading, isPaused, act1Text, act2Text) =>
        {
            var ctx = ApplicationContext ?? this;
            ComicBackgroundService.StartOrUpdate(ctx, taskId, title, text, progress, isDownloading, isPaused, act1Text, act2Text);
        };
        Services.BackgroundExecutionService.NativeStopForegroundNotification = () =>
        {
            var ctx = ApplicationContext ?? this;
            ComicBackgroundService.Stop(ctx);
        };
        Services.BackgroundExecutionService.NativeRequestEnterBubbleMode = () =>
        {
            RunOnUiThread(() =>
            {
                var ctx = ApplicationContext ?? this;
                ComicBackgroundService.StartOrUpdate(ctx, "bubble_task", "Comic Downloader GMTPC", "Bong bóng chạy ngầm đang hoạt động...", 0, false, false, "⏹️ Dừng", "📱 Mở ứng dụng");
                FloatingBubbleManager.ShowBubble(this);
            });
        };
        Services.BackgroundExecutionService.NativeRequestExitBubbleMode = () =>
        {
            RunOnUiThread(() =>
            {
                FloatingBubbleManager.HideBubble();
            });
        };
        Services.BackgroundExecutionService.NativeIsBubbleOrPipSupported = () => true;

        Services.BackgroundExecutionService.NativeMinimizeOrHide = () =>
        {
            RunOnUiThread(() =>
            {
                MoveTaskToBack(true);
            });
        };

        Services.BackgroundExecutionService.NativeForceExit = () =>
        {
            RunOnUiThread(() =>
            {
                FloatingBubbleManager.HideBubble();
                ComicBackgroundService.Stop(this);
                FinishAffinity();
                global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
                Java.Lang.JavaSystem.Exit(0);
            });
        };

        Services.BackgroundExecutionService.NativeOpenBrowserRequested = (url) =>
        {
            RunOnUiThread(() =>
            {
                try
                {
                    var uri = global::Android.Net.Uri.Parse(url);
                    var intent = new Intent(Intent.ActionView, uri);
                    intent.AddFlags(ActivityFlags.NewTask);
                    StartActivity(intent);
                }
                catch { }
            });
        };

        Services.DownloadEngineService.OpenStorageSettingsRequested += OnOpenStorageSettingsRequested;
        Services.DownloadEngineService.AndroidOpenFolderRequested += OnOpenFolderRequested;
        Services.DownloadEngineService.ResetAndroidFolderOpenerRequested += OnResetAndroidFolderOpener;
        Services.AppUpdateService.InstallApkRequested += OnInstallApkRequested;
        Services.ComicReaderService.AndroidLaunchSeparateReaderTask = LaunchComicReaderTask;

        // Đăng ký Native Android Audio Player cho âm thanh thông báo (Startup / DownloadFinish / Error)
        Services.SoundNotificationService.AndroidPlaySoundHandler = (type, filePath) =>
        {
            RunOnUiThread(() =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        var player = new global::Android.Media.MediaPlayer();
                        player.SetDataSource(filePath);
                        player.Prepare();
                        player.Start();
                        player.Completion += (s, e) =>
                        {
                            try
                            {
                                player.Release();
                                player.Dispose();
                            }
                            catch { }
                        };
                        return;
                    }

                    // Fallback qua ToneGenerator nếu chưa tải xong file WAV
                    var toneType = type switch
                    {
                        Services.SoundNotificationType.Startup => global::Android.Media.Tone.PropBeep,
                        Services.SoundNotificationType.DownloadFinish => global::Android.Media.Tone.PropAck,
                        _ => global::Android.Media.Tone.PropNack
                    };
                    using var toneGen = new global::Android.Media.ToneGenerator(global::Android.Media.Stream.Music, 100);
                    toneGen.StartTone(toneType, 250);
                }
                catch { }
            });
        };

        // Trì hoãn việc xin cấp quyền sau khi khung nhìn chính đã được render xong frame đầu tiên
        // để không làm gián đoạn vòng đời OnCreate và tránh gây xung đột màn hình chờ
        RunOnUiThread(() =>
        {
            try
            {
                Window?.DecorView?.Post(() =>
                {
                    try
                    {
                        RequestAppStoragePermissions();
                        App.TryGetSharedMainViewModel()?.LibraryVm?.LoadStorageItems();
                    }
                    catch { }
                });
            }
            catch
            {
                RequestAppStoragePermissions();
            }
        });

        // Bảo vệ cử chỉ hệ thống và vùng điều hướng đáy khi đang gõ trong TextBox
        Services.TextEditingProtectionService.TypingStateChanged += OnTypingStateChanged;

        // Xử lý mở tệp truyện nếu app được kích hoạt từ ứng dụng ngoài qua Intent
        HandleIncomingFileIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        HandleIncomingFileIntent(intent);
    }

    /// <summary>
    /// Xử lý Intent khi ứng dụng được kích hoạt từ File Manager hoặc nút Chia Sẻ:
    /// Chuyển tiếp ngay lập tức sang ComicReaderActivity để tải trực tiếp trình đọc,
    /// loại bỏ hoàn toàn 2 lần load screen và không làm tải chậm app chính.
    /// </summary>
    private void HandleIncomingFileIntent(Intent? intent)
    {
        if (intent == null) return;
        string? action = intent.Action;
        if (action != Intent.ActionView && action != Intent.ActionSend) return;

        try
        {
            var readerIntent = new Intent(this, typeof(ComicReaderActivity));
            readerIntent.SetAction(intent.Action);
            if (intent.Data != null) readerIntent.SetDataAndType(intent.Data, intent.Type);
            if (intent.Extras != null) readerIntent.PutExtras(intent.Extras);
            readerIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
            StartActivity(readerIntent);
            Finish();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ComicGMTPC", $"Lỗi forward file intent sang ComicReaderActivity: {ex.Message}");
        }
    }

    private void LaunchComicReaderTask(ViewModels.ComicReaderViewModel readerVm)
    {
        RunOnUiThread(() =>
        {
            try
            {
                var intent = new Intent(this, typeof(ComicReaderActivity));
                intent.SetData(global::Android.Net.Uri.Parse("gmtpc://reader/session"));
                intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.NewDocument | ActivityFlags.MultipleTask);

                if (!string.IsNullOrWhiteSpace(readerVm.ParentComic?.RootFolderPath))
                {
                    intent.PutExtra(ComicReaderActivity.ExtraComicRootPath, readerVm.ParentComic.RootFolderPath);
                }
                if (!string.IsNullOrWhiteSpace(readerVm.CurrentChapter?.LocalPath))
                {
                    intent.PutExtra(ComicReaderActivity.ExtraChapterLocalPath, readerVm.CurrentChapter.LocalPath);
                }
                if (!string.IsNullOrWhiteSpace(readerVm.ComicTitle))
                {
                    intent.PutExtra(ComicReaderActivity.ExtraComicTitle, readerVm.ComicTitle);
                }
                if (!string.IsNullOrWhiteSpace(readerVm.ChapterTitle))
                {
                    intent.PutExtra(ComicReaderActivity.ExtraChapterTitle, readerVm.ChapterTitle);
                }

                StartActivity(intent);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("ComicGMTPC", $"Lỗi mở cửa sổ đọc truyện độc lập: {ex.Message}");
            }
        });
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == FloatingBubbleManager.RequestOverlayPermissionCode)
        {
            if (FloatingBubbleManager.CanDrawOverlays(this))
            {
                FloatingBubbleManager.PromoteToSystemOverlay(this);
                try
                {
                    Toast.MakeText(this, "🎉 Đã kích hoạt Bong bóng nổi trên toàn hệ thống thành công!", ToastLength.Short)?.Show();
                }
                catch { }
            }
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        CurrentInstance = this;
        AndroidStorageHelper.WakeUpStorage(this);
        Services.ThermalAndPowerOptimizationService.Instance.SetAppInBackground(false);

        // Tự động kích hoạt / nâng cấp Bong bóng nổi nếu người dùng vừa cấp quyền từ màn hình Cài đặt hệ thống
        try
        {
            if (Services.BackgroundExecutionService.Instance.IsBubbleMode)
            {
                if (FloatingBubbleManager.CanDrawOverlays(this))
                {
                    FloatingBubbleManager.PromoteToSystemOverlay(this);
                }
                else if (!FloatingBubbleManager.IsShowing)
                {
                    FloatingBubbleManager.ShowBubble(this);
                }
            }
        }
        catch { }

        // Tự động làm mới danh sách Thẻ SD & Storage khi người dùng quay lại từ màn hình Cài đặt cấp quyền
        try
        {
            App.TryGetSharedMainViewModel()?.LibraryVm?.LoadStorageItems();
        }
        catch { }
    }

    protected override void OnPause()
    {
        try
        {
            base.OnPause();
            Services.ThermalAndPowerOptimizationService.Instance.SetAppInBackground(true);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi OnPause: {ex.Message}");
        }
    }

    protected override void OnStop()
    {
        try
        {
            base.OnStop();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi OnStop: {ex.Message}");
        }
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        try
        {
            base.OnWindowFocusChanged(hasFocus);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi OnWindowFocusChanged: {ex.Message}");
        }
    }

    protected override void OnDestroy()
    {
        Services.SoundNotificationService.AndroidPlaySoundHandler = null;
        Services.BackgroundExecutionService.NativeStartOrUpdateForegroundNotification = null;
        Services.BackgroundExecutionService.NativeStopForegroundNotification = null;
        Services.BackgroundExecutionService.NativeRequestEnterBubbleMode = null;
        Services.BackgroundExecutionService.NativeRequestExitBubbleMode = null;
        Services.BackgroundExecutionService.NativeMinimizeOrHide = null;
        Services.BackgroundExecutionService.NativeForceExit = null;
        Services.ComicReaderService.AndroidLaunchSeparateReaderTask = null;

        Services.DownloadEngineService.OpenStorageSettingsRequested -= OnOpenStorageSettingsRequested;
        Services.DownloadEngineService.AndroidOpenFolderRequested -= OnOpenFolderRequested;
        Services.DownloadEngineService.ResetAndroidFolderOpenerRequested -= OnResetAndroidFolderOpener;
        Services.AppUpdateService.InstallApkRequested -= OnInstallApkRequested;
        Services.TextEditingProtectionService.TypingStateChanged -= OnTypingStateChanged;
        if (CurrentInstance == this) CurrentInstance = null;
        base.OnDestroy();
    }

    private void OnTypingStateChanged(bool isTyping)
    {
        try
        {
            RunOnUiThread(() =>
            {
                try
                {
                    UpdateSystemGestureExclusion(isTyping);
                }
                catch { }
            });
        }
        catch { }
    }

    /// <summary>
    /// Bảo vệ vùng bàn phím và thanh điều hướng đáy:
    /// Khi đang nhập văn bản trong TextBox, loại trừ cử chỉ hệ thống (Back gesture, Quick switch app gesture)
    /// ở hai mép bên và đáy màn hình, đảm bảo khi người dùng giữ phím space vuốt vượt quá đầu/cuối
    /// thì con trỏ vẫn đứng yên ở đầu/cuối và KHÔNG BAO GIỜ bị nhảy qua ứng dụng khác.
    /// </summary>
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
                    int edgeWidth = (int)((Resources?.DisplayMetrics?.Density ?? 2.5f) * 120); // 120dp
                    int bottomBarHeight = (int)((Resources?.DisplayMetrics?.Density ?? 2.5f) * 75); // 75dp thanh điều hướng đáy

                    var rects = new List<global::Android.Graphics.Rect>
                    {
                        // 1. Mép trái vùng bàn phím (ngăn cử chỉ Back khi vuốt sang trái)
                        new global::Android.Graphics.Rect(0, keyboardTop, edgeWidth, height),
                        // 2. Mép phải vùng bàn phím (ngăn cử chỉ Back khi vuốt sang phải)
                        new global::Android.Graphics.Rect(Math.Max(0, width - edgeWidth), keyboardTop, width, height),
                        // 3. Mép đáy màn hình (ngăn cử chỉ vuốt chuyển ứng dụng trên thanh navigation bar)
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
        try
        {
            if (Services.TextEditingProtectionService.IsTypingInTextBox && e != null)
            {
                // Chặn tuyệt đối phím chuyển đổi ứng dụng (AppSwitch) khi đang gõ văn bản
                if (e.KeyCode == Keycode.AppSwitch)
                {
                    return true;
                }

                // Chặn các phím điều hướng hướng ra ngoài khi đang gõ để không làm mất Focus hay trigger launcher
                if (e.KeyCode == Keycode.DpadLeft || e.KeyCode == Keycode.DpadRight ||
                    e.KeyCode == Keycode.DpadUp || e.KeyCode == Keycode.DpadDown ||
                    e.KeyCode == Keycode.Tab)
                {
                    base.DispatchKeyEvent(e);
                    // Luôn tiêu thụ để hệ điều hành Android không can thiệp chuyển app
                    return true;
                }
            }
        }
        catch { }

        return base.DispatchKeyEvent(e);
    }

    public override void OnBackPressed()
    {
        // Khi người dùng bấm phím Back: Mở popup confirm 3 option (Tắt / Chạy ngầm / Thoát hoàn toàn)
        RunOnUiThread(() =>
        {
            var vm = App.SharedMainViewModel;
            if (vm != null)
            {
                if (vm.IsExitConfirmOpen)
                {
                    vm.IsExitConfirmOpen = false;
                }
                else
                {
                    vm.IsExitConfirmOpen = true;
                }
            }
        });
    }

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (keyCode == Keycode.Back && e?.Action == KeyEventActions.Down)
        {
            OnBackPressed();
            return true;
        }

        if ((keyCode == Keycode.VolumeUp || keyCode == Keycode.VolumeDown) && e?.Action == KeyEventActions.Down)
        {
            if (ComicDownloaderGMTPC.ViewModels.ComicReaderViewModel.ActiveInstance != null &&
                ComicDownloaderGMTPC.ViewModels.ComicReaderViewModel.IsVolumeKeyTurnEnabled)
            {
                bool isNext = (keyCode == Keycode.VolumeDown);
                ComicDownloaderGMTPC.ViewModels.ComicReaderViewModel.TriggerVolumeKeyTurn(isNext);
                return true;
            }
        }

        return base.OnKeyDown(keyCode, e);
    }

    protected override void OnUserLeaveHint()
    {
        base.OnUserLeaveHint();
        // Khi người dùng chuyển app hoặc về Home bình thường: Không tự động hiện PiP to đùng
        // Tiến trình vẫn chạy ngầm liên tục nhờ Foreground Service ComicBackgroundService
    }



    private void OnOpenStorageSettingsRequested()
    {
        RequestManageAllFilesPermission(this);
    }

    public static void RequestManageAllFilesPermission(Activity? activity)
    {
        if (activity == null) return;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                if (!global::Android.OS.Environment.IsExternalStorageManager)
                {
                    try
                    {
                        var uri = global::Android.Net.Uri.FromParts("package", activity.PackageName, null);
                        var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission, uri);
                        activity.StartActivity(intent);
                    }
                    catch
                    {
                        var intent = new Intent(Settings.ActionManageAllFilesAccessPermission);
                        activity.StartActivity(intent);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi mở cài đặt quyền: {ex.Message}");
        }
    }

    private void RequestAppStoragePermissions()
    {
        try
        {
            var permissionsToRequest = new List<string>();

            if (CheckSelfPermission(Manifest.Permission.ReadExternalStorage) != Permission.Granted)
            {
                permissionsToRequest.Add(Manifest.Permission.ReadExternalStorage);
            }

            if (CheckSelfPermission(Manifest.Permission.WriteExternalStorage) != Permission.Granted)
            {
                permissionsToRequest.Add(Manifest.Permission.WriteExternalStorage);
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                if (CheckSelfPermission(Manifest.Permission.ReadMediaImages) != Permission.Granted)
                {
                    permissionsToRequest.Add(Manifest.Permission.ReadMediaImages);
                }
                if (CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
                {
                    permissionsToRequest.Add(Manifest.Permission.PostNotifications);
                }
            }

            if (permissionsToRequest.Count > 0)
            {
                RequestPermissions(permissionsToRequest.ToArray(), StoragePermissionRequestCode);
            }
            else if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                RequestManageAllFilesPermission(this);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi xin cấp quyền lưu trữ: {ex.Message}");
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == StoragePermissionRequestCode)
        {
            try
            {
                App.TryGetSharedMainViewModel()?.LibraryVm?.LoadStorageItems();
            }
            catch { }

            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                RequestManageAllFilesPermission(this);
            }
        }
    }

    private void OnInstallApkRequested(string apkPath)
    {
        RunOnUiThread(() =>
        {
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                {
                    if (PackageManager != null && !PackageManager.CanRequestPackageInstalls())
                    {
                        try
                        {
                            var uri = global::Android.Net.Uri.FromParts("package", PackageName, null);
                            var permIntent = new Intent(Settings.ActionManageUnknownAppSources, uri);
                            permIntent.AddFlags(ActivityFlags.NewTask);
                            StartActivity(permIntent);
                        }
                        catch
                        {
                            var permIntent = new Intent(Settings.ActionManageUnknownAppSources);
                            permIntent.AddFlags(ActivityFlags.NewTask);
                            StartActivity(permIntent);
                        }
                    }
                }

                var apkFile = new Java.IO.File(apkPath);
                if (!apkFile.Exists())
                {
                    global::Android.Util.Log.Error("ComicGMTPC", $"File APK không tồn tại: {apkPath}");
                    return;
                }

                var contentUri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                    this,
                    PackageName + ".fileprovider",
                    apkFile);

                var installIntent = new Intent(Intent.ActionView);
                installIntent.SetDataAndType(contentUri, "application/vnd.android.package-archive");
                installIntent.AddFlags(ActivityFlags.NewTask);
                installIntent.AddFlags(ActivityFlags.GrantReadUriPermission);
                StartActivity(installIntent);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("ComicGMTPC", $"Lỗi cài đặt APK: {ex.Message}");
            }
        });
    }

    private void OnResetAndroidFolderOpener()
    {
        RunOnUiThread(() =>
        {
            try
            {
                var prefs = GetSharedPreferences("ComicDownloaderPrefs", FileCreationMode.Private);
                prefs?.Edit()?.Remove("DefaultFolderOpener")?.Apply();
                Toast.MakeText(this, "✓ Đã đặt lại ứng dụng mở thư mục mặc định. Lần sau bấm 'Mở' sẽ hiển thị bảng chọn.", ToastLength.Long)?.Show();
            }
            catch { }
        });
    }

    private void CopyPathToClipboard(string folderPath)
    {
        try
        {
            var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
            if (clipboard != null)
            {
                var clip = ClipData.NewPlainText("Folder Path", folderPath);
                clipboard.PrimaryClip = clip;
            }
        }
        catch { }
    }

    private bool IsPackageInstalled(string packageName)
    {
        try
        {
            PackageManager?.GetPackageInfo(packageName, PackageInfoFlags.Activities);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool ExecuteOpenFolder(string folderPath, string openerKey)
    {
        var dir = new Java.IO.File(folderPath);
        if (!dir.Exists())
        {
            try { dir.Mkdirs(); } catch { }
        }
        var fileUri = global::Android.Net.Uri.FromFile(dir);

        try
        {
            var policy = new StrictMode.VmPolicy.Builder().Build();
            StrictMode.SetVmPolicy(policy);
        }
        catch { }

        switch (openerKey)
        {
            case "es":
                try
                {
                    var fmIntent = new Intent(Intent.ActionView);
                    fmIntent.SetPackage("com.estrongs.android.pop");
                    fmIntent.SetDataAndType(fileUri, "resource/folder");
                    fmIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
                    StartActivity(fmIntent);
                    return true;
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi mở ES: {ex.Message}");
                }
                break;

            case "zarchiver":
                try
                {
                    var fmIntent = new Intent(Intent.ActionView);
                    fmIntent.SetPackage("ru.zdevs.zarchiver");
                    fmIntent.SetDataAndType(fileUri, "resource/folder");
                    fmIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
                    StartActivity(fmIntent);
                    return true;
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi mở ZArchiver: {ex.Message}");
                }
                break;

            case "system":
                try
                {
                    string relPath = folderPath.Replace("/storage/emulated/0/", "").Trim('/');
                    string docId = "primary:" + relPath;
                    var docUri = DocumentsContract.BuildDocumentUri("com.android.externalstorage.documents", docId);
                    var docIntent = new Intent(Intent.ActionView);
                    docIntent.SetDataAndType(docUri, DocumentsContract.Document.MimeTypeDir);
                    docIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
                    StartActivity(docIntent);
                    return true;
                }
                catch
                {
                    try
                    {
                        var rootUri = DocumentsContract.BuildRootUri("com.android.externalstorage.documents", "primary");
                        var docIntent = new Intent(Intent.ActionView);
                        docIntent.SetDataAndType(rootUri, DocumentsContract.Document.MimeTypeDir);
                        docIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
                        StartActivity(docIntent);
                        return true;
                    }
                    catch { }
                }
                break;

            case "other":
            default:
                try
                {
                    var genericIntent = new Intent(Intent.ActionView);
                    genericIntent.SetDataAndType(fileUri, "resource/folder");
                    genericIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
                    StartActivity(genericIntent);
                    return true;
                }
                catch { }
                break;
        }

        return false;
    }

    private void OnOpenFolderRequested(string folderPath)
    {
        RunOnUiThread(() =>
        {
            try
            {
                CopyPathToClipboard(folderPath);

                var prefs = GetSharedPreferences("ComicDownloaderPrefs", FileCreationMode.Private);
                string defaultOpener = prefs?.GetString("DefaultFolderOpener", "") ?? "";

                if (!string.IsNullOrEmpty(defaultOpener))
                {
                    if (ExecuteOpenFolder(folderPath, defaultOpener))
                    {
                        return;
                    }
                    // Nếu app mặc định không còn hoạt động, xóa cache để hiển thị lại bảng chọn
                    prefs?.Edit()?.Remove("DefaultFolderOpener")?.Apply();
                }

                // Danh sách các tùy chọn mở thư mục hợp lệ (Chỉ hiển thị 1 mục cho mỗi app thật)
                var optionsList = new List<(string Key, string Label)>();
                optionsList.Add(("system", "📁 Trình duyệt tệp hệ thống (Files / DocumentsUI)"));

                if (IsPackageInstalled("com.estrongs.android.pop"))
                {
                    optionsList.Add(("es", "🌐 ES Duyệt Tập Tin (ES File Explorer)"));
                }
                if (IsPackageInstalled("ru.zdevs.zarchiver"))
                {
                    optionsList.Add(("zarchiver", "📦 ZArchiver"));
                }
                optionsList.Add(("other", "📂 Chọn ứng dụng quản lý file khác..."));

                int selectedIndex = 0;
                var labels = optionsList.Select(x => x.Label).ToArray();

                var builder = new global::Android.App.AlertDialog.Builder(this);
                builder.SetTitle("📂 Mở Thư Mục Bằng Ứng Dụng");

                // Thêm CheckBox "Đặt làm mặc định"
                var container = new LinearLayout(this)
                {
                    Orientation = Orientation.Vertical
                };
                container.SetPadding(40, 15, 40, 10);

                var chkDefault = new CheckBox(this)
                {
                    Text = "Đặt làm mặc định (Luôn mở bằng ứng dụng này)",
                    Checked = true
                };
                chkDefault.SetTextColor(global::Android.Graphics.Color.ParseColor("#38BDF8"));
                container.AddView(chkDefault);

                builder.SetSingleChoiceItems(labels, selectedIndex, (s, e) =>
                {
                    selectedIndex = e.Which;
                });

                builder.SetView(container);

                builder.SetPositiveButton("MỞ", (s, e) =>
                {
                    if (selectedIndex >= 0 && selectedIndex < optionsList.Count)
                    {
                        string chosenKey = optionsList[selectedIndex].Key;
                        if (chkDefault.Checked)
                        {
                            prefs?.Edit()?.PutString("DefaultFolderOpener", chosenKey)?.Apply();
                            Toast.MakeText(this, "✓ Đã đặt làm mặc định. Bạn có thể thay đổi trong Cài Đặt.", ToastLength.Short)?.Show();
                        }
                        ExecuteOpenFolder(folderPath, chosenKey);
                    }
                });

                builder.SetNegativeButton("HỦY", (s, e) => { });

                builder.SetNeutralButton("SAO CHÉP ĐƯỜNG DẪN", (s, e) =>
                {
                    CopyPathToClipboard(folderPath);
                    Toast.MakeText(this, $"Đã sao chép: {folderPath}", ToastLength.Short)?.Show();
                });

                var dialog = builder.Create();
                dialog?.Show();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("ComicGMTPC", $"Lỗi mở thư mục trên Android: {ex.Message}");
                try
                {
                    Toast.MakeText(this, $"Đã sao chép đường dẫn: {folderPath}", ToastLength.Short)?.Show();
                }
                catch { }
            }
        });
    }

    private void LaunchCrashReportActivity(Exception ex, string source = "MainActivity")
    {
        try
        {
            var intent = new Intent(this, typeof(CrashReportActivity));
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
            intent.PutExtra(CrashReportActivity.ExtraCrashSource, source);
            intent.PutExtra(CrashReportActivity.ExtraCrashDetails, ex.ToString());
            StartActivity(intent);
        }
        catch (Exception launchEx)
        {
            global::Android.Util.Log.Error("ComicGMTPC_Crash", $"LaunchCrashReportActivity failed: {launchEx}");
        }
    }

    public override void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        try
        {
            FloatingBubbleManager.HandleOrientationOrSizeChanged(this, newConfig);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC_Bubble", $"Lỗi MainActivity OnConfigurationChanged: {ex.Message}");
        }
    }
}
#if ANDROID
[global::Android.Runtime.Register("com/companyname/comicdownloadergmtpc/ThermalStatusListener")]
public class ThermalStatusListener : Java.Lang.Object, PowerManager.IOnThermalStatusChangedListener
{
    private readonly Action<ThermalStatus> _callback;
    public ThermalStatusListener(Action<ThermalStatus> callback) => _callback = callback;
    public void OnThermalStatusChanged(ThermalStatus status) => _callback?.Invoke(status);
}
#endif

