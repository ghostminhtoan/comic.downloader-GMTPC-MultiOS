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
        try
        {
            retriever = new MediaMetadataRetriever();
            SetDataSourceSafe(retriever, videoPath);

            global::Android.Graphics.Bitmap? bmp = null;
            if (OperatingSystem.IsAndroidVersionAtLeast(27) && maxWidth > 0 && maxHeight > 0)
            {
                try
                {
                    bmp = retriever.GetScaledFrameAtTime(-1, Option.ClosestSync, maxWidth, maxHeight);
                }
                catch { }
            }

            if (bmp == null)
            {
                bmp = retriever.GetFrameAtTime(-1, Option.ClosestSync);
            }

            if (bmp == null)
            {
                bmp = retriever.FrameAtTime;
            }

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
        try
        {
            retriever = new MediaMetadataRetriever();
            SetDataSourceSafe(retriever, videoPath);

            long durationMs = 0;
            try
            {
                string? durStr = retriever.ExtractMetadata(MetadataKey.Duration);
                if (!string.IsNullOrEmpty(durStr) && long.TryParse(durStr, out long d))
                {
                    durationMs = d;
                }
            }
            catch { }

            int count = Math.Max(2, Math.Min(16, maxFrames));
            long durationUs = durationMs > 0 ? durationMs * 1000 : 10_000_000; // 10 giây nếu không đọc được duration
            long stepUs = durationUs / count;

            for (int i = 0; i < count; i++)
            {
                long timeUs = i * stepUs;
                global::Android.Graphics.Bitmap? bmp = null;

                if (OperatingSystem.IsAndroidVersionAtLeast(27) && maxWidth > 0 && maxHeight > 0)
                {
                    try
                    {
                        bmp = retriever.GetScaledFrameAtTime(timeUs, Option.ClosestSync, maxWidth, maxHeight);
                    }
                    catch { }
                }

                if (bmp == null)
                {
                    bmp = retriever.GetFrameAtTime(timeUs, Option.ClosestSync);
                }

                if (bmp != null)
                {
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
            try { retriever?.Release(); } catch { }
            try { retriever?.Dispose(); } catch { }
        }

        return list;
    }

    private static void SetDataSourceSafe(MediaMetadataRetriever retriever, string videoPath)
    {
        if (videoPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
        {
            Context ctx = MainActivity.CurrentInstance ?? global::Android.App.Application.Context;
            var uri = global::Android.Net.Uri.Parse(videoPath);
            if (uri != null)
            {
                retriever.SetDataSource(ctx, uri);
                return;
            }
        }

        retriever.SetDataSource(videoPath);
    }
}
