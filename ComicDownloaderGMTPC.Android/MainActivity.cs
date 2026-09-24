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

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestAppStoragePermissions();
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
                if (!global::Android.OS.Environment.IsExternalStorageManager)
                {
                    try
                    {
                        var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission);
                        intent.SetData(global::Android.Net.Uri.Parse($"package:{PackageName}"));
                        StartActivity(intent);
                    }
                    catch
                    {
                        var intent = new Intent(Settings.ActionManageAllFilesAccessPermission);
                        StartActivity(intent);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi xin cấp quyền lưu trữ: {ex.Message}");
        }
    }
}
