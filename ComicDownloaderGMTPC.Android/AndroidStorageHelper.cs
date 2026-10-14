using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Android.Content;
using Android.OS;
using Android.OS.Storage;

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
}

