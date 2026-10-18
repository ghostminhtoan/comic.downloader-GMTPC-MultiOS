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
using Android.OS.Storage;
using Android.Provider;

namespace ComicDownloaderGMTPC.Android;

public static class AndroidStorageHelper
{
    private static long _lastWakeUpTick = 0;

    /// <summary>
    /// Đánh thức FUSE mount namespace của toàn bộ thẻ nhớ ngoài (SD Card) và bộ nhớ ngoài ngay khi app khởi động (Cold Start).
    /// </summary>
    public static void WakeUpStorage(Context? context)
    {
        if (context == null) return;
        long now = System.Environment.TickCount64;
        if (now - _lastWakeUpTick < 1000) return; // Tránh gọi dồn dập
        _lastWakeUpTick = now;

        try
        {
            var filesDirs = context.GetExternalFilesDirs(null);
            if (filesDirs != null)
            {
                foreach (var dir in filesDirs)
                {
                    if (dir != null)
                    {
                        try { _ = dir.Exists(); } catch { }
                    }
                }
            }

            var cacheDirs = context.GetExternalCacheDirs();
            if (cacheDirs != null)
            {
                foreach (var dir in cacheDirs)
                {
                    if (dir != null)
                    {
                        try { _ = dir.Exists(); } catch { }
                    }
                }
            }

#pragma warning disable CA1422
            var mediaDirs = context.GetExternalMediaDirs();
            if (mediaDirs != null)
            {
                foreach (var dir in mediaDirs)
                {
                    if (dir != null)
                    {
                        try { _ = dir.Exists(); } catch { }
                    }
                }
            }
#pragma warning restore CA1422

            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var sm = (StorageManager?)context.GetSystemService(Context.StorageService);
                if (sm != null)
                {
                    var volumes = sm.StorageVolumes;
                    if (volumes != null)
                    {
                        foreach (var vol in volumes)
                        {
                            if (vol != null)
                            {
                                try
                                {
                                    var d = vol.Directory;
                                    if (d != null) { _ = d.Exists(); }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Trích xuất danh sách đường dẫn Root của toàn bộ thẻ nhớ SD Card gắn ngoài.
    /// </summary>
    public static List<string> GetExternalStoragePaths(Context? context)
    {
        var results = new List<string>();
        if (context == null) return results;

        try
        {
            WakeUpStorage(context);

            var filesDirs = context.GetExternalFilesDirs(null);
            if (filesDirs != null)
            {
                foreach (var dir in filesDirs)
                {
                    if (dir == null) continue;
                    string abs = dir.AbsolutePath;
                    int idx = abs.IndexOf("/Android/data", StringComparison.OrdinalIgnoreCase);
                    if (idx > 0)
                    {
                        string root = abs.Substring(0, idx);
                        if (!root.Contains("emulated", StringComparison.OrdinalIgnoreCase) &&
                            !results.Any(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase)))
                        {
                            results.Add(root);
                        }
                    }
                }
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var sm = (StorageManager?)context.GetSystemService(Context.StorageService);
                if (sm != null)
                {
                    var volumes = sm.StorageVolumes;
                    if (volumes != null)
                    {
                        foreach (var vol in volumes)
                        {
                            if (vol == null || vol.IsPrimary) continue;
                            try
                            {
                                var d = vol.Directory;
                                if (d != null && !string.IsNullOrWhiteSpace(d.AbsolutePath))
                                {
                                    string vPath = d.AbsolutePath;
                                    if (!results.Any(r => string.Equals(r, vPath, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        results.Add(vPath);
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
        }
        catch { }

        return results;
    }

    /// <summary>
    /// Phân giải Intent Uri (file:// hoặc content://) thành đường dẫn tệp thực tế trên Android
    /// Hỗ trợ cả Zero-Copy cho Documents Storage (Bộ nhớ máy / Thẻ SD) và Stream Copy cho ContentProvider bên thứ 3.
    /// </summary>
    public static async Task<string?> ResolveUriToFilePathAsync(Context context, global::Android.Net.Uri? uri)
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
            using var cursor = context.ContentResolver?.Query(uri, null, null, null, null);
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
                            var sdRoots = GetExternalStoragePaths(context);
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

        // 5. Fallback: Sao chép luồng dữ liệu từ ContentResolver sang thư mục tạm của app
        try
        {
            string cacheDir = Path.Combine(context.CacheDir?.AbsolutePath ?? Path.GetTempPath(), "opened_comics");
            if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

            string destPath = Path.Combine(cacheDir, fileName);

            using var inStream = context.ContentResolver?.OpenInputStream(uri);
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

    public static void RequestAppStoragePermissions(Activity? activity, int requestCode = 1001)
    {
        if (activity == null) return;
        try
        {
            var permissionsToRequest = new List<string>();

            if (activity.CheckSelfPermission(Manifest.Permission.ReadExternalStorage) != Permission.Granted)
            {
                permissionsToRequest.Add(Manifest.Permission.ReadExternalStorage);
            }

            if (activity.CheckSelfPermission(Manifest.Permission.WriteExternalStorage) != Permission.Granted)
            {
                permissionsToRequest.Add(Manifest.Permission.WriteExternalStorage);
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                if (activity.CheckSelfPermission(Manifest.Permission.ReadMediaImages) != Permission.Granted)
                {
                    permissionsToRequest.Add(Manifest.Permission.ReadMediaImages);
                }
                if (activity.CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
                {
                    permissionsToRequest.Add(Manifest.Permission.PostNotifications);
                }
            }

            if (permissionsToRequest.Count > 0)
            {
                activity.RequestPermissions(permissionsToRequest.ToArray(), requestCode);
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                RequestManageAllFilesPermission(activity);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC", $"Lỗi xin cấp quyền lưu trữ: {ex.Message}");
        }
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
}

