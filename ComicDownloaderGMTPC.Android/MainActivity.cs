using System;
using System.Collections.Generic;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Views;
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
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.Density | ConfigChanges.FontScale)]
public class MainActivity : AvaloniaMainActivity
{
    private const int StoragePermissionRequestCode = 1001;
    public static MainActivity? CurrentInstance { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        CurrentInstance = this;

        // Đăng ký bridge chạy ngầm và bong bóng
        Services.BackgroundExecutionService.NativeStartOrUpdateForegroundNotification = (title, text, progress) =>
        {
            ComicBackgroundService.StartOrUpdate(this, title, text, progress);
        };
        Services.BackgroundExecutionService.NativeStopForegroundNotification = () =>
        {
            ComicBackgroundService.Stop(this);
        };
        Services.BackgroundExecutionService.NativeRequestEnterBubbleMode = () =>
        {
            EnterBubbleOrPipMode();
        };
        Services.BackgroundExecutionService.NativeRequestExitBubbleMode = () =>
        {
            FloatingBubbleService.Hide(this);
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
                ComicBackgroundService.Stop(this);
                FloatingBubbleService.Hide(this);
                FinishAffinity();
                global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
                Java.Lang.JavaSystem.Exit(0);
            });
        };

        Services.DownloadEngineService.OpenStorageSettingsRequested += OnOpenStorageSettingsRequested;
        Services.DownloadEngineService.AndroidOpenFolderRequested += OnOpenFolderRequested;
        Services.AppUpdateService.InstallApkRequested += OnInstallApkRequested;
        RequestAppStoragePermissions();
    }

    protected override void OnDestroy()
    {
        Services.BackgroundExecutionService.NativeStartOrUpdateForegroundNotification = null;
        Services.BackgroundExecutionService.NativeStopForegroundNotification = null;
        Services.BackgroundExecutionService.NativeRequestEnterBubbleMode = null;
        Services.BackgroundExecutionService.NativeRequestExitBubbleMode = null;
        Services.BackgroundExecutionService.NativeMinimizeOrHide = null;
        Services.BackgroundExecutionService.NativeForceExit = null;

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

    [System.Runtime.Versioning.SupportedOSPlatform("android26.0")]
    public override void OnPictureInPictureModeChanged(bool isInPictureInPictureMode, global::Android.Content.Res.Configuration? newConfig)
    {
        base.OnPictureInPictureModeChanged(isInPictureInPictureMode, newConfig);
        Services.BackgroundExecutionService.Instance.SetBubbleMode(isInPictureInPictureMode);
    }

    public void EnterBubbleOrPipMode()
    {
        RunOnUiThread(() =>
        {
            try
            {
                // Ưu tiên kích hoạt Bong bóng nổi tròn phong cách Messenger Chat Head
                if (OperatingSystem.IsAndroidVersionAtLeast(23) && Settings.CanDrawOverlays(this))
                {
                    FloatingBubbleService.Show(this);
                    MoveTaskToBack(true);
                }
                else if (OperatingSystem.IsAndroidVersionAtLeast(23) && !Settings.CanDrawOverlays(this))
                {
                    try
                    {
                        var intent = new Intent(Settings.ActionManageOverlayPermission, global::Android.Net.Uri.Parse("package:" + PackageName));
                        intent.AddFlags(ActivityFlags.NewTask);
                        StartActivity(intent);
                    }
                    catch
                    {
                        var intent = new Intent(Settings.ActionManageOverlayPermission);
                        intent.AddFlags(ActivityFlags.NewTask);
                        StartActivity(intent);
                    }
                }
                else if (OperatingSystem.IsAndroidVersionAtLeast(26))
                {
                    using var pipBuilder = new PictureInPictureParams.Builder();
                    var pipParams = pipBuilder.Build();
                    if (pipParams != null)
                    {
                        EnterPictureInPictureMode(pipParams);
                    }
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi kích hoạt Bong bóng: {ex.Message}");
            }
        });
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

                bool launched = false;

                // 1. Thử mở qua DocumentsUI / ExternalStorageProvider (Chuẩn nhất trên Android 8+)
                try
                {
                    string relPath = folderPath.Replace("/storage/emulated/0/", "").Trim('/');
                    string docId = "primary:" + relPath;
                    var uri = DocumentsContract.BuildDocumentUri("com.android.externalstorage.documents", docId);
                    var intent = new Intent(Intent.ActionView);
                    intent.SetDataAndType(uri, DocumentsContract.Document.MimeTypeDir);
                    intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
                    StartActivity(intent);
                    launched = true;
                }
                catch { }

                // 2. Thử mở qua FileProvider
                if (!launched)
                {
                    try
                    {
                        var contentUri = AndroidX.Core.Content.FileProvider.GetUriForFile(this, PackageName + ".fileprovider", dir);
                        var intent = new Intent(Intent.ActionView);
                        intent.SetDataAndType(contentUri, DocumentsContract.Document.MimeTypeDir);
                        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
                        StartActivity(intent);
                        launched = true;
                    }
                    catch { }
                }

                // 3. Thử mở qua resource/folder MIME
                if (!launched)
                {
                    try
                    {
                        var uri = global::Android.Net.Uri.FromFile(dir);
                        var intent = new Intent(Intent.ActionView);
                        intent.SetDataAndType(uri, "resource/folder");
                        intent.AddFlags(ActivityFlags.NewTask);
                        StartActivity(intent);
                        launched = true;
                    }
                    catch { }
                }

                // 4. Fallback với ACTION_VIEW thông thường
                if (!launched)
                {
                    try
                    {
                        var intent = new Intent(Intent.ActionView);
                        intent.SetDataAndType(global::Android.Net.Uri.Parse(folderPath), "*/*");
                        intent.AddFlags(ActivityFlags.NewTask);
                        StartActivity(intent);
                        launched = true;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("ComicGMTPC", $"Lỗi mở thư mục trên Android: {ex.Message}");
            }
        });
    }
}
