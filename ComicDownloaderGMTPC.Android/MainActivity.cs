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
// === CÁC INTENT FILTER CHO PHÉP MỞ APP VỚI FILE .PDF, .ZIP, .RAR, .CBR, .CBZ, .MOBI (CHỌN ỨNG DỤNG ĐỌC) ===
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
// Bắt theo Scheme file:// và extension
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
// Bắt theo Scheme content:// và extension
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
// Bắt theo generic application/octet-stream
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.pdf")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.zip")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.rar")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.cbz")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.cbr")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/octet-stream", DataHost = "*", DataPathPattern = ".*\\.mobi")]
// Bắt khi người dùng bấm Chia Sẻ (ActionSend)
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/pdf")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/zip")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/x-zip-compressed")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/x-rar-compressed")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/rar")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/x-cbz")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/x-cbr")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/x-mobipocket-ebook")]
public class MainActivity : AvaloniaMainActivity
{
    private const int StoragePermissionRequestCode = 1001;
    public static MainActivity? CurrentInstance { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        CurrentInstance = this;
        AndroidStorageHelper.WakeUpStorage(this);

        // Cung cấp danh sách đường dẫn Root của toàn bộ thẻ nhớ ngoài (SD Card)
        Services.LibraryScannerService.AndroidGetExternalStoragePaths = () =>
            AndroidStorageHelper.GetExternalStoragePaths(CurrentInstance?.ApplicationContext ?? CurrentInstance);

        base.OnCreate(savedInstanceState);
        Window?.SetSoftInputMode(SoftInput.AdjustResize);

        // Bảo vệ toàn diện chống crash unhandled khi chạy ngầm hoặc thay đổi focus/cử chỉ screenshot
        AndroidEnvironment.UnhandledExceptionRaiser += (sender, args) =>
        {
            try
            {
                global::Android.Util.Log.Error("ComicGMTPC_Crash", $"[AndroidEnvironment Unhandled] {args.Exception}");
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
            var ctx = ApplicationContext ?? this;
            ComicBackgroundService.StartOrUpdate(ctx, "bubble_task", "Comic Downloader GMTPC", "Ứng dụng đang chạy ngầm...", 0, false, false, "⏹️ Dừng", "📱 Mở ứng dụng");
            MoveTaskToBack(true);
        };
        Services.BackgroundExecutionService.NativeRequestExitBubbleMode = null;
        Services.BackgroundExecutionService.NativeIsBubbleOrPipSupported = () => false;

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

        RequestAppStoragePermissions();

        // Tự động làm mới danh mục Storage sau khi khởi động xong để bảo đảm hiện Thẻ SD
        RunOnUiThread(() =>
        {
            try
            {
                App.SharedMainViewModel?.LibraryVm?.LoadStorageItems();
            }
            catch { }
        });

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
    /// Xử lý Intent khi ứng dụng được kích hoạt từ File Manager hoặc nút Chia Sẻ
    /// để mở trực tiếp các tệp truyện: .pdf, .zip, .rar, .cbr, .cbz, .mobi, v.v.
    /// </summary>
    private void HandleIncomingFileIntent(Intent? intent)
    {
        if (intent == null) return;
        string? action = intent.Action;
        if (action != Intent.ActionView && action != Intent.ActionSend) return;

        global::Android.Net.Uri? uri = intent.Data;
#pragma warning disable CA1422
        if (uri == null && action == Intent.ActionSend)
        {
            uri = intent.GetParcelableExtra(Intent.ExtraStream) as global::Android.Net.Uri;
        }
#pragma warning restore CA1422

        if (uri == null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                string? resolvedFilePath = await ResolveUriToFilePathAsync(uri);
                if (string.IsNullOrWhiteSpace(resolvedFilePath) || !File.Exists(resolvedFilePath))
                {
                    RunOnUiThread(() =>
                    {
                        Toast.MakeText(this, "Không thể đọc tệp truyện được yêu cầu mở.", ToastLength.Short)?.Show();
                    });
                    return;
                }

                // Đảm bảo môi trường Avalonia & ViewModel đã khởi tạo xong (tối đa 5 giây khi Cold Start)
                for (int i = 0; i < 25; i++)
                {
                    if (App.SharedMainViewModel?.LibraryVm != null) break;
                    await Task.Delay(200);
                }

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try
                    {
                        if (App.SharedMainViewModel?.LibraryVm != null)
                        {
                            await App.SharedMainViewModel.LibraryVm.OpenStandaloneFileDirectlyAsync(resolvedFilePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        global::Android.Util.Log.Error("ComicGMTPC", $"Lỗi nạp truyện vào Reader: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("ComicGMTPC", $"Lỗi xử lý file intent: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Phân giải Intent Uri (file:// hoặc content://) thành đường dẫn tệp thực tế trên Android
    /// Hỗ trợ cả Zero-Copy cho Documents Storage (Bộ nhớ máy / Thẻ SD) và Stream Copy cho ContentProvider bên thứ 3.
    /// </summary>
    private async Task<string?> ResolveUriToFilePathAsync(global::Android.Net.Uri uri)
    {
        if (uri == null) return null;

        // 1. Nếu là scheme file:// trực tiếp
        if (string.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
        {
            string? rawPath = uri.Path;
            if (!string.IsNullOrEmpty(rawPath) && File.Exists(rawPath))
            {
                return rawPath;
            }
        }

        // 2. Lấy tên tệp gốc từ OpenableColumns.DisplayName
        string fileName = Path.GetFileName(uri.Path ?? "") ?? "comic_document";
        try
        {
            using var cursor = ContentResolver?.Query(uri, null, null, null, null);
            if (cursor != null && cursor.MoveToFirst())
            {
                int nameIdx = cursor.GetColumnIndex(global::Android.Provider.IOpenableColumns.DisplayName);
                if (nameIdx >= 0)
                {
                    string? name = cursor.GetString(nameIdx);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        fileName = name;
                    }
                }
            }
        }
        catch { }

        // 3. Phân giải nhanh Zero-Copy cho DocumentsContract (bộ nhớ trong primary: hoặc thẻ SD)
        try
        {
            string uriStr = uri.ToString() ?? "";
            if (uriStr.Contains("com.android.externalstorage.documents"))
            {
                string? docId = global::Android.Provider.DocumentsContract.GetDocumentId(uri);
                if (!string.IsNullOrEmpty(docId))
                {
                    if (docId.StartsWith("primary:", StringComparison.OrdinalIgnoreCase))
                    {
                        string sub = docId.Substring("primary:".Length);
                        string realPath = Path.Combine("/storage/emulated/0", sub.TrimStart('/', '\\'));
                        if (File.Exists(realPath)) return realPath;
                    }
                    else
                    {
                        int colonIdx = docId.IndexOf(':');
                        if (colonIdx > 0)
                        {
                            var sdRoots = AndroidStorageHelper.GetExternalStoragePaths(this);
                            string rel = docId.Substring(colonIdx + 1).TrimStart('/', '\\');
                            foreach (var sd in sdRoots)
                            {
                                string candidate = Path.Combine(sd, rel);
                                if (File.Exists(candidate)) return candidate;
                            }
                        }
                    }
                }
            }
        }
        catch { }

        // 4. Nếu uri.Path là tệp tồn tại
        if (!string.IsNullOrEmpty(uri.Path) && File.Exists(uri.Path))
        {
            return uri.Path;
        }

        // 5. Fallback hoàn hảo: Sao chép luồng dữ liệu từ ContentResolver sang thư mục tạm của app
        try
        {
            string cacheDir = Path.Combine(CacheDir?.AbsolutePath ?? Path.GetTempPath(), "opened_comics");
            if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

            string destPath = Path.Combine(cacheDir, fileName);

            using var inStream = ContentResolver?.OpenInputStream(uri);
            if (inStream != null)
            {
                using var outStream = File.Create(destPath);
                await inStream.CopyToAsync(outStream);
                return destPath;
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ComicGMTPC", $"Lỗi sao chép stream từ URI ngoài: {ex.Message}");
        }

        return null;
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

    protected override void OnResume()
    {
        base.OnResume();
        CurrentInstance = this;
        AndroidStorageHelper.WakeUpStorage(this);

        // Tự động làm mới danh sách Thẻ SD & Storage khi người dùng quay lại từ màn hình Cài đặt cấp quyền
        try
        {
            App.SharedMainViewModel?.LibraryVm?.LoadStorageItems();
        }
        catch { }
    }

    protected override void OnPause()
    {
        try
        {
            base.OnPause();
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
        Services.AppUpdateService.InstallApkRequested -= OnInstallApkRequested;
        if (CurrentInstance == this) CurrentInstance = null;
        base.OnDestroy();
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

            // Với Android 11+ (API 30+), xin quyền quản lý toàn bộ tệp (All files access)
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                RequestManageAllFilesPermission(this);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi xin cấp quyền lưu trữ: {ex.Message}");
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

    private void OnOpenFolderRequested(string folderPath)
    {
        RunOnUiThread(() =>
        {
            try
            {
                var dir = new Java.IO.File(folderPath);
                if (!dir.Exists())
                {
                    dir.Mkdirs();
                }

                // 1. Bypass StrictMode cho file:// URI giữa các app
                try
                {
                    var policy = new StrictMode.VmPolicy.Builder().Build();
                    StrictMode.SetVmPolicy(policy);
                }
                catch { }

                var fileUri = global::Android.Net.Uri.FromFile(dir);

                // Danh sách package ứng dụng đọc truyện tranh và quản lý file phổ biến
                string[] comicReaderPackages = new[]
                {
                    "com.viewer.comicscreen",
                    "com.viewer.comicscreen.lite",
                    "com.viewer.comicscreen.free",
                    "com.rookiestudio.perfectviewer",
                    "com.kuro.kuroreader",
                    "com.foobnix.pdf.reader",
                    "com.github.axet.bookreader"
                };

                string[] fileManagerPackages = new[]
                {
                    "com.estrongs.android.pop",
                    "com.mixplorer",
                    "ru.zdevs.zarchiver",
                    "pl.solidexplorer2",
                    "com.lonelycatgames.Xplore",
                    "com.google.android.apps.nbu.files",
                    "com.mi.android.globalFileExplorer",
                    "com.coloros.filemanager",
                    "com.sec.android.app.myfiles",
                    "com.alphainventor.filemanager"
                };

                var targetedIntents = new List<IParcelable>();
                var pm = PackageManager;

                // 2. Kiểm tra các app đọc truyện tranh (Comic Screen, Perfect Viewer...)
                if (pm != null)
                {
                    foreach (var pkg in comicReaderPackages)
                    {
                        try
                        {
                            var launchIntent = pm.GetLaunchIntentForPackage(pkg);
                            if (launchIntent != null)
                            {
                                var comicIntent = new Intent(Intent.ActionView);
                                comicIntent.SetPackage(pkg);
                                comicIntent.SetDataAndType(fileUri, "*/*");
                                comicIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
                                targetedIntents.Add(comicIntent);
                            }
                        }
                        catch { }
                    }

                    // 3. Kiểm tra các app quản lý file (ES File Explorer...)
                    foreach (var pkg in fileManagerPackages)
                    {
                        try
                        {
                            var launchIntent = pm.GetLaunchIntentForPackage(pkg);
                            if (launchIntent != null)
                            {
                                var fmIntent = new Intent(Intent.ActionView);
                                fmIntent.SetPackage(pkg);
                                fmIntent.SetDataAndType(fileUri, "resource/folder");
                                fmIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
                                targetedIntents.Add(fmIntent);
                            }
                        }
                        catch { }
                    }
                }

                // 4. Intent cơ sở với file://
                var primaryIntent = new Intent(Intent.ActionView);
                primaryIntent.SetDataAndType(fileUri, "resource/folder");
                primaryIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);

                // 5. Intent DocumentsUI / Files hệ thống
                try
                {
                    string relPath = folderPath.Replace("/storage/emulated/0/", "").Trim('/');
                    string docId = "primary:" + relPath;
                    var docUri = DocumentsContract.BuildDocumentUri("com.android.externalstorage.documents", docId);
                    var docIntent = new Intent(Intent.ActionView);
                    docIntent.SetDataAndType(docUri, DocumentsContract.Document.MimeTypeDir);
                    docIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
                    targetedIntents.Add(docIntent);
                }
                catch { }

                // 6. Tạo Chooser để người dùng có thể chọn Comic Screen, ES File Explorer hoặc Files
                var chooserIntent = Intent.CreateChooser(primaryIntent, "📂 Mở Thư Mục Bằng Ứng Dụng");
                if (chooserIntent != null)
                {
                    chooserIntent.AddFlags(ActivityFlags.NewTask);
                    if (targetedIntents.Count > 0)
                    {
                        chooserIntent.PutExtra(Intent.ExtraInitialIntents, targetedIntents.ToArray());
                    }
                    StartActivity(chooserIntent);
                }
                else
                {
                    StartActivity(primaryIntent);
                }
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
}
