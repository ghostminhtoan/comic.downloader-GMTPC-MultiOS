using System;
using System.Collections.Generic;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Avalonia;
using Avalonia.Android;

namespace ComicDownloaderGMTPC.Android;

[Activity(
    Label = "ComicDownloaderGMTPC.Android",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private const int StoragePermissionRequestCode = 1001;
    public static MainActivity? CurrentInstance { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        CurrentInstance = this;

        Services.DownloadEngineService.OpenStorageSettingsRequested += OnOpenStorageSettingsRequested;
        Services.AppUpdateService.InstallApkRequested += OnInstallApkRequested;
        RequestAppStoragePermissions();
    }

    protected override void OnDestroy()
    {
        Services.DownloadEngineService.OpenStorageSettingsRequested -= OnOpenStorageSettingsRequested;
        Services.AppUpdateService.InstallApkRequested -= OnInstallApkRequested;
        if (CurrentInstance == this) CurrentInstance = null;
        base.OnDestroy();
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
}
