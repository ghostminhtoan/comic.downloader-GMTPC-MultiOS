using System;
using System.Collections.Generic;
using System.IO;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.OS;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.Android;

/// <summary>
/// Dịch vụ trích xuất ảnh bìa (Thumbnail) và chuỗi khung hình (Video Motion Frames) chuyên dụng cho Android.
/// Tận dụng trực tiếp MediaMetadataRetriever native của hệ điều hành Android (0MB tăng thêm cho APK),
/// hỗ trợ giải mã toàn diện các định dạng video (.mp4, .webm, .mkv, .ts) cả đường dẫn file thực lẫn Content URI.
/// Sử dụng trực tiếp FileDescriptor kernel nhằm vượt qua 100% rào cản Scoped Storage / SELinux trên Android 10+.
/// </summary>
public static class AndroidVideoThumbnailService
{
    public static void Register()
    {
        VideoThumbnailService.ExternalNativeThumbnailProvider = ExtractThumbnail;
        VideoThumbnailService.ExternalNativeFramesProvider = ExtractFrames;
    }

    public static byte[]? ExtractThumbnail(string videoPath, int maxWidth, int maxHeight)
    {
        if (string.IsNullOrWhiteSpace(videoPath)) return null;

        MediaMetadataRetriever? retriever = null;
        IDisposable? streamHandle = null;
        try
        {
            retriever = new MediaMetadataRetriever();
            streamHandle = SetDataSourceSafe(retriever, videoPath);

            long durationMs = TryGetDurationMs(retriever);
            long durationUs = durationMs > 0 ? durationMs * 1000 : 0;

            global::Android.Graphics.Bitmap? bmp = TryGetAnyFrame(retriever, durationUs, maxWidth, maxHeight);

            if (bmp != null)
            {
                using (bmp)
                using (var ms = new MemoryStream())
                {
                    bmp.Compress(global::Android.Graphics.Bitmap.CompressFormat.Jpeg!, 85, ms);
                    return ms.ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC_Thumb", $"[AndroidVideo] ExtractThumbnail error for '{videoPath}': {ex.Message}");
        }
        finally
        {
            try { streamHandle?.Dispose(); } catch { }
            try { retriever?.Release(); } catch { }
            try { retriever?.Dispose(); } catch { }
        }

        return null;
    }

    public static List<byte[]> ExtractFrames(string videoPath, int maxFrames, int maxWidth, int maxHeight)
    {
        var list = new List<byte[]>();
        if (string.IsNullOrWhiteSpace(videoPath)) return list;

        MediaMetadataRetriever? retriever = null;
        IDisposable? streamHandle = null;
        try
        {
            retriever = new MediaMetadataRetriever();
            streamHandle = SetDataSourceSafe(retriever, videoPath);

            long durationMs = TryGetDurationMs(retriever);
            int count = Math.Max(2, Math.Min(16, maxFrames));
            long durationUs = durationMs > 0 ? durationMs * 1000 : 5_000_000; // 5 giây mặc định nếu không đọc được duration
            long stepUs = Math.Max(100_000, durationUs / count);

            for (int i = 0; i < count; i++)
            {
                long timeUs = i * stepUs;
                global::Android.Graphics.Bitmap? bmp = null;

                // 1. Thử với Option.Closest (Giải mã chính xác P-frame/B-frame cho video ngắn)
                try
                {
                    bmp = retriever.GetFrameAtTime(timeUs, Option.Closest);
                }
                catch { }

                // 2. Thử với Option.ClosestSync
                if (bmp == null)
                {
                    try
                    {
                        bmp = retriever.GetFrameAtTime(timeUs, Option.ClosestSync);
                    }
                    catch { }
                }

                // 3. Fallback GetFrameAtTime không tham số
                if (bmp == null && i == 0)
                {
                    try
                    {
                        bmp = retriever.FrameAtTime;
                    }
                    catch { }
                }

                if (bmp != null)
                {
                    bmp = ScaleBitmapIfNeeded(bmp, maxWidth, maxHeight);
                    using (bmp)
                    using (var ms = new MemoryStream())
                    {
                        bmp.Compress(global::Android.Graphics.Bitmap.CompressFormat.Jpeg!, 80, ms);
                        byte[] bytes = ms.ToArray();
                        if (bytes.Length > 0)
                        {
                            list.Add(bytes);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC_Thumb", $"[AndroidVideo] ExtractFrames error for '{videoPath}': {ex.Message}");
        }
        finally
        {
            try { streamHandle?.Dispose(); } catch { }
            try { retriever?.Release(); } catch { }
            try { retriever?.Dispose(); } catch { }
        }

        return list;
    }

    private static global::Android.Graphics.Bitmap? TryGetAnyFrame(MediaMetadataRetriever retriever, long durationUs, int maxWidth, int maxHeight)
    {
        global::Android.Graphics.Bitmap? bmp = null;

        // Chiến lược 1: FrameAtTime mặc định của hệ điều hành
        try
        {
            bmp = retriever.FrameAtTime;
        }
        catch { }

        // Chiến lược 2: Frame tại thời điểm 0 với Option.Closest
        if (bmp == null)
        {
            try
            {
                bmp = retriever.GetFrameAtTime(0, Option.Closest);
            }
            catch { }
        }

        // Chiến lược 3: Frame tại 0.5s với Option.ClosestSync (thường có keyframe đầu tiên)
        if (bmp == null)
        {
            try
            {
                bmp = retriever.GetFrameAtTime(500_000, Option.ClosestSync);
            }
            catch { }
        }

        // Chiến lược 4: Frame tại 1.0s
        if (bmp == null)
        {
            try
            {
                bmp = retriever.GetFrameAtTime(1_000_000, Option.ClosestSync);
            }
            catch { }
        }

        // Chiến lược 5: Frame tại 0.1s với Option.Closest
        if (bmp == null)
        {
            try
            {
                bmp = retriever.GetFrameAtTime(100_000, Option.Closest);
            }
            catch { }
        }

        // Chiến lược 6: Frame theo tỉ lệ % duration nếu video dài
        if (bmp == null && durationUs > 0)
        {
            try
            {
                bmp = retriever.GetFrameAtTime(durationUs / 10, Option.Closest);
            }
            catch { }
        }

        if (bmp == null && durationUs > 0)
        {
            try
            {
                bmp = retriever.GetFrameAtTime(durationUs / 4, Option.Closest);
            }
            catch { }
        }

        if (bmp != null)
        {
            bmp = ScaleBitmapIfNeeded(bmp, maxWidth, maxHeight);
        }

        return bmp;
    }

    private static global::Android.Graphics.Bitmap ScaleBitmapIfNeeded(global::Android.Graphics.Bitmap orig, int maxWidth, int maxHeight)
    {
        if (maxWidth <= 0 || maxHeight <= 0) return orig;
        if (orig.Width <= maxWidth && orig.Height <= maxHeight) return orig;

        try
        {
            float ratio = Math.Min((float)maxWidth / orig.Width, (float)maxHeight / orig.Height);
            int targetW = Math.Max(1, (int)Math.Round(orig.Width * ratio));
            int targetH = Math.Max(1, (int)Math.Round(orig.Height * ratio));

            var scaled = global::Android.Graphics.Bitmap.CreateScaledBitmap(orig, targetW, targetH, true);
            if (scaled != null && scaled != orig)
            {
                orig.Recycle();
                orig.Dispose();
                return scaled;
            }
        }
        catch { }

        return orig;
    }

    private static long TryGetDurationMs(MediaMetadataRetriever retriever)
    {
        try
        {
            string? durStr = retriever.ExtractMetadata(MetadataKey.Duration);
            if (!string.IsNullOrEmpty(durStr) && long.TryParse(durStr, out long d))
            {
                return d;
            }
        }
        catch { }
        return 0;
    }

    private static IDisposable? SetDataSourceSafe(MediaMetadataRetriever retriever, string videoPath)
    {
        Context ctx = MainActivity.CurrentInstance ?? global::Android.App.Application.Context;

        // 1. Xử lý Content URI (SAF Storage)
        if (videoPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = global::Android.Net.Uri.Parse(videoPath);
            if (uri != null)
            {
                try
                {
                    var pfd = ctx.ContentResolver?.OpenFileDescriptor(uri, "r");
                    if (pfd != null && pfd.FileDescriptor != null && pfd.FileDescriptor.Valid())
                    {
                        retriever.SetDataSource(pfd.FileDescriptor);
                        return pfd;
                    }
                }
                catch { }

                try
                {
                    retriever.SetDataSource(ctx, uri);
                    return null;
                }
                catch { }
            }
        }

        // 2. Mở qua FileDescriptor từ Java.IO.FileInputStream (Giải quyết triệt để lỗi 0x80000000 trên Android 10+)
        try
        {
            var jFile = new Java.IO.File(videoPath);
            if (jFile.Exists() && jFile.CanRead())
            {
                var fis = new Java.IO.FileInputStream(jFile);
                if (fis.FD != null && fis.FD.Valid())
                {
                    long length = jFile.Length();
                    if (length > 0)
                    {
                        retriever.SetDataSource(fis.FD, 0, length);
                    }
                    else
                    {
                        retriever.SetDataSource(fis.FD);
                    }
                    return fis;
                }
            }
        }
        catch { }

        // 3. Mở qua Android.Net.Uri.FromFile
        try
        {
            var fileUri = global::Android.Net.Uri.FromFile(new Java.IO.File(videoPath));
            if (fileUri != null)
            {
                retriever.SetDataSource(ctx, fileUri);
                return null;
            }
        }
        catch { }

        // 4. Fallback đường dẫn chuỗi
        retriever.SetDataSource(videoPath);
        return null;
    }
}
