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
        VideoThumbnailService.ExternalNativeFrameAtTimeProvider = ExtractFrameAtTime;
        NativeAudioPlaybackService.ExternalAndroidAudioPlayerProvider = PlayAndroidAudio;
    }

    private static INativeAudioPlayerInstance? PlayAndroidAudio(string filePath, double startSeconds)
    {
        try
        {
            var mp = new MediaPlayer();
            var ctx = global::Android.App.Application.Context;
            if (filePath.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = global::Android.Net.Uri.Parse(filePath);
                if (uri != null) mp.SetDataSource(ctx, uri);
            }
            else
            {
                mp.SetDataSource(filePath);
            }
            mp.Prepare();
            if (startSeconds > 0)
            {
                mp.SeekTo((int)(startSeconds * 1000));
            }
            mp.Start();

            return new AndroidMediaPlayerHandle(mp);
        }
        catch
        {
            return null;
        }
    }

    private sealed class AndroidMediaPlayerHandle : INativeAudioPlayerInstance
    {
        private MediaPlayer? _mp;
        public AndroidMediaPlayerHandle(MediaPlayer mp) { _mp = mp; }

        public void Pause()
        {
            try
            {
                if (_mp != null && _mp.IsPlaying)
                {
                    _mp.Pause();
                }
            }
            catch { }
        }

        public void Resume()
        {
            try
            {
                if (_mp != null && !_mp.IsPlaying)
                {
                    _mp.Start();
                }
            }
            catch { }
        }

        public void Seek(double seconds)
        {
            try
            {
                if (_mp != null)
                {
                    int msec = Math.Max(0, (int)(seconds * 1000));
                    _mp.SeekTo(msec);
                }
            }
            catch { }
        }

        public void Stop()
        {
            try
            {
                if (_mp != null)
                {
                    if (_mp.IsPlaying) _mp.Stop();
                }
            }
            catch { }
        }

        public void Dispose()
        {
            try
            {
                if (_mp != null)
                {
                    if (_mp.IsPlaying) _mp.Stop();
                    _mp.Release();
                    _mp.Dispose();
                    _mp = null;
                }
            }
            catch { }
        }
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

            long durationMs = TryGetDurationMs(retriever, videoPath);
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

            long durationMs = TryGetDurationMs(retriever, videoPath);
            int count = Math.Max(4, Math.Min(8, maxFrames));
            long durationUs = durationMs > 0 ? durationMs * 1000 : 15_000_000;
            // Phân bổ đều khung hình xuyên suốt toàn bộ thời lượng video (từ 0% đến 96%)
            long spanUs = durationUs > 0 ? (long)(durationUs * 0.96) : 10_000_000;
            long stepUs = Math.Max(100_000, spanUs / count);

            for (int i = 0; i < count; i++)
            {
                long timeUs = i * stepUs;
                global::Android.Graphics.Bitmap? bmp = null;

                // 1. Thử với Option.ClosestSync (Giải mã Keyframe siêu tốc ~10ms trên GPU)
                try
                {
                    bmp = retriever.GetFrameAtTime(timeUs, Option.ClosestSync);
                }
                catch { }

                // 2. Thử với Option.Closest nếu ClosestSync không ra
                if (bmp == null)
                {
                    try
                    {
                        bmp = retriever.GetFrameAtTime(timeUs, Option.Closest);
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

    public static byte[]? ExtractFrameAtTime(string videoPath, long timeMs, int maxWidth, int maxHeight)
    {
        if (string.IsNullOrWhiteSpace(videoPath)) return null;

        MediaMetadataRetriever? retriever = null;
        IDisposable? streamHandle = null;
        try
        {
            retriever = new MediaMetadataRetriever();
            streamHandle = SetDataSourceSafe(retriever, videoPath);

            long timeUs = Math.Max(0, timeMs * 1000);
            global::Android.Graphics.Bitmap? bmp = null;

            try
            {
                bmp = retriever.GetFrameAtTime(timeUs, Option.Closest);
            }
            catch { }

            if (bmp == null)
            {
                try
                {
                    bmp = retriever.GetFrameAtTime(timeUs, Option.ClosestSync);
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
                    return ms.ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ComicGMTPC_Thumb", $"[AndroidVideo] ExtractFrameAtTime error at {timeMs}ms: {ex.Message}");
        }
        finally
        {
            try { streamHandle?.Dispose(); } catch { }
            try { retriever?.Release(); } catch { }
            try { retriever?.Dispose(); } catch { }
        }

        return null;
    }

    private static global::Android.Graphics.Bitmap? TryGetAnyFrame(MediaMetadataRetriever retriever, long durationUs, int maxWidth, int maxHeight)
    {
        global::Android.Graphics.Bitmap? bmp = null;

        try { bmp = retriever.FrameAtTime; } catch { }
        if (bmp == null) { try { bmp = retriever.GetFrameAtTime(0, Option.Closest); } catch { } }
        if (bmp == null) { try { bmp = retriever.GetFrameAtTime(500_000, Option.ClosestSync); } catch { } }
        if (bmp == null) { try { bmp = retriever.GetFrameAtTime(1_000_000, Option.ClosestSync); } catch { } }
        if (bmp == null) { try { bmp = retriever.GetFrameAtTime(100_000, Option.Closest); } catch { } }

        if (bmp == null && durationUs > 0)
        {
            try { bmp = retriever.GetFrameAtTime(durationUs / 10, Option.Closest); } catch { }
        }
        if (bmp == null && durationUs > 0)
        {
            try { bmp = retriever.GetFrameAtTime(durationUs / 4, Option.Closest); } catch { }
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

    private static long TryGetDurationMs(MediaMetadataRetriever retriever, string videoPath)
    {
        // 1. Ưu tiên đọc trực tiếp từ Container Box (mvhd / EBML Info) chuẩn xác 100%
        long parsed = VideoMetadataParser.GetVideoDurationMs(videoPath);
        if (parsed > 0) return parsed;

        // 2. Fallback đọc từ MediaMetadataRetriever
        try
        {
            string? durStr = retriever.ExtractMetadata(MetadataKey.Duration);
            if (!string.IsNullOrEmpty(durStr) && long.TryParse(durStr, out long d) && d > 0)
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

        retriever.SetDataSource(videoPath);
        return null;
    }
}
