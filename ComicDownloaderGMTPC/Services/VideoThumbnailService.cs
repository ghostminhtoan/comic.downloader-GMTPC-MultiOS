using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Dịch vụ trích xuất frame thumbnail và tạo ảnh xem trước (Preview) cho các định dạng video (.webm, .mp4, .mkv, .ts)
/// Tối ưu hiệu năng cao đa nền tảng:
/// 1. Trên Windows: Sử dụng native COM IShellItemImageFactory trích xuất trực tiếp poster frame siêu tốc, 0 phụ thuộc ngoài.
/// 2. Caching đa tầng: Lưu bộ đệm ảnh thumbnail JPEG (.tmp/preview-cache/videos/) để truy xuất 0ms cho các lần xem tiếp theo.
/// 3. Fallback thông minh: Tự động tạo poster card chuyên nghiệp bằng SkiaSharp nếu video lỗi hoặc chạy trên môi trường không có codec Shell.
/// </summary>
public static class VideoThumbnailService
{
    private static readonly HashSet<string> _videoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webm", ".mp4", ".mkv", ".ts", ".m2ts", ".avi", ".mov", ".flv", ".wmv"
    };

    public static Func<string, int, int, byte[]?>? ExternalNativeThumbnailProvider { get; set; }
    public static Func<string, int, int, int, List<byte[]>>? ExternalNativeFramesProvider { get; set; }
    public static Func<string, long, int, int, byte[]?>? ExternalNativeFrameAtTimeProvider { get; set; }

    public static bool IsValidVideoPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith("content://", StringComparison.OrdinalIgnoreCase)) return true;
        return File.Exists(path);
    }

    public static long GetVideoDurationMs(string videoPath)
    {
        return VideoMetadataParser.GetVideoDurationMs(videoPath);
    }

    public static Bitmap? GetFrameAtTime(string videoPath, long timeMs, int maxWidth, int maxHeight)
    {
        if (!IsValidVideoPath(videoPath)) return null;

        // 1. Android Native MediaMetadataRetriever
        if (ExternalNativeFrameAtTimeProvider != null)
        {
            try
            {
                byte[]? b = ExternalNativeFrameAtTimeProvider(videoPath, timeMs, maxWidth, maxHeight);
                if (b != null && b.Length > 0)
                {
                    return new Bitmap(new MemoryStream(b));
                }
            }
            catch { }
        }

        // 2. Windows Native Windows Media Foundation (0ms, 0 external dependency)
        if (OperatingSystem.IsWindows())
        {
            try
            {
                byte[]? b = WindowsMediaFoundationExtractor.ExtractFrameAtTime(videoPath, timeMs, maxWidth, maxHeight);
                if (b != null && b.Length > 0)
                {
                    return new Bitmap(new MemoryStream(b));
                }
            }
            catch { }
        }

        // 3. Linux qua FFmpeg
        if (OperatingSystem.IsLinux() || (!OperatingSystem.IsWindows() && !OperatingSystem.IsAndroid()))
        {
            try
            {
                byte[]? b = ExtractFfmpegFrameAtTime(videoPath, timeMs, maxWidth, maxHeight);
                if (b != null && b.Length > 0)
                {
                    return new Bitmap(new MemoryStream(b));
                }
            }
            catch { }
        }

        return null;
    }

    public static bool IsVideoFile(string? pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return false;
        try
        {
            int queryIdx = pathOrUrl.IndexOf('?');
            string clean = queryIdx >= 0 ? pathOrUrl.Substring(0, queryIdx) : pathOrUrl;
            string ext = Path.GetExtension(clean);
            return !string.IsNullOrEmpty(ext) && _videoExtensions.Contains(ext);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsVideoData(byte[]? bytes)
    {
        if (bytes == null || bytes.Length < 16) return false;

        // 1. EBML Header (WebM / MKV): 0x1A, 0x45, 0xDF, 0xA3
        if (bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3)
            return true;

        // 2. MP4 / MOV: Box 'ftyp' tại offset 4..7
        if (bytes.Length >= 12 &&
            bytes[4] == (byte)'f' && bytes[5] == (byte)'t' && bytes[6] == (byte)'y' && bytes[7] == (byte)'p')
            return true;

        // 3. MPEG Transport Stream (TS): 0x47 sync byte
        if (bytes[0] == 0x47 && bytes.Length >= 188 && bytes[188] == 0x47)
            return true;

        return false;
    }

    public static Bitmap? GetVideoThumbnail(string videoPath, int maxWidth, int maxHeight, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            byte[]? jpegBytes = GetVideoThumbnailBytes(videoPath, maxWidth, maxHeight, out width, out height);
            if (jpegBytes != null && jpegBytes.Length > 0)
            {
                using var ms = new MemoryStream(jpegBytes);
                return new Bitmap(ms);
            }
        }
        catch { }

        return null;
    }

    public static byte[]? GetVideoThumbnailBytes(string videoPath, int maxWidth, int maxHeight, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!IsValidVideoPath(videoPath)) return null;

        string cacheFile = GetCacheFilePath(videoPath, maxWidth, maxHeight);
        if (File.Exists(cacheFile) && new FileInfo(cacheFile).Length > 0)
        {
            try
            {
                byte[] cachedBytes = File.ReadAllBytes(cacheFile);
                if (cachedBytes.Length > 0)
                {
                    using var codec = SKCodec.Create(new MemoryStream(cachedBytes));
                    if (codec != null)
                    {
                        width = codec.Info.Width;
                        height = codec.Info.Height;
                        return cachedBytes;
                    }
                }
            }
            catch { }
        }

        byte[]? resultBytes = null;

        // 1. Thử trích xuất native poster frame qua External Provider (Android MediaMetadataRetriever...)
        if (ExternalNativeThumbnailProvider != null)
        {
            try
            {
                resultBytes = ExternalNativeThumbnailProvider(videoPath, maxWidth, maxHeight);
                if (resultBytes != null && resultBytes.Length > 0)
                {
                    using var codec = SKCodec.Create(new MemoryStream(resultBytes));
                    if (codec != null)
                    {
                        width = codec.Info.Width;
                        height = codec.Info.Height;
                    }
                }
            }
            catch { }
        }

        // 2. Thử trích xuất native poster frame trên Windows Shell COM
        if ((resultBytes == null || resultBytes.Length == 0) && OperatingSystem.IsWindows())
        {
            try
            {
                resultBytes = ExtractWindowsShellThumbnailBytes(videoPath, maxWidth, maxHeight, out width, out height);
            }
            catch { }
        }

        bool isRealFrame = resultBytes != null && resultBytes.Length > 0;

        // 3. Nếu không trích xuất được frame, tự động sinh Poster Card nghệ thuật bằng SkiaSharp
        if (!isRealFrame)
        {
            resultBytes = CreatePosterCardBytes(videoPath, maxWidth, maxHeight, out width, out height);
        }

        // 4. Lưu vào bộ đệm cache trên ổ đĩa: Chỉ lưu khi trích xuất được frame thật
        // Tuyệt đối không cache Poster Card giả để tránh làm kẹt video ở khung đen vĩnh viễn
        if (isRealFrame && resultBytes != null && resultBytes.Length > 0)
        {
            try
            {
                string? dir = Path.GetDirectoryName(cacheFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllBytes(cacheFile, resultBytes);
            }
            catch { }
        }

        return resultBytes;
    }

    public static byte[]? ExtractThumbnailBytesFromVideoBuffer(byte[] videoBytes, string ext, int maxWidth, int maxHeight, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (videoBytes == null || videoBytes.Length == 0) return null;

        string tempDir = Path.Combine(AppContext.BaseDirectory, ".tmp", "video_thumb_temp");
        try
        {
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            string safeExt = string.IsNullOrWhiteSpace(ext) ? ".mp4" : (ext.StartsWith('.') ? ext : "." + ext);
            string tempFile = Path.Combine(tempDir, $"{Guid.NewGuid():N}{safeExt}");
            File.WriteAllBytes(tempFile, videoBytes);

            try
            {
                return GetVideoThumbnailBytes(tempFile, maxWidth, maxHeight, out width, out height);
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
        catch
        {
            return CreatePosterCardFromInfo("Video", videoBytes.Length, ext, maxWidth, maxHeight, out width, out height);
        }
    }

    public static async Task<List<Bitmap>> GetOrExtractVideoFramesAsync(string videoPath, int maxFrames = 14, int maxWidth = 480, int maxHeight = 480, System.Threading.CancellationToken ct = default)
    {
        var result = new List<Bitmap>();
        if (!IsValidVideoPath(videoPath)) return result;

        long len = 0;
        long time = 0;
        try
        {
            var fi = new FileInfo(videoPath);
            if (fi.Exists)
            {
                len = fi.Length;
                time = fi.LastWriteTimeUtc.Ticks;
            }
        }
        catch { }

        string rawKey = $"{videoPath}_{len}_{time}_v2";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        string hashStr = Convert.ToHexString(hash).Substring(0, 24).ToLowerInvariant();

        string framesDir = Path.Combine(AppContext.BaseDirectory, ".tmp", "preview-cache", "video-frames", $"{hashStr}_{maxWidth}x{maxHeight}");

        // 1. Kiểm tra cache frame trên đĩa
        if (Directory.Exists(framesDir))
        {
            try
            {
                var cachedFiles = Directory.GetFiles(framesDir, "frame_*.jpg");
                Array.Sort(cachedFiles, NaturalSortComparer.Instance);
                if (cachedFiles.Length >= 2)
                {
                    foreach (var f in cachedFiles)
                    {
                        if (ct.IsCancellationRequested) break;
                        byte[] fb = await File.ReadAllBytesAsync(f, ct).ConfigureAwait(false);
                        if (fb.Length > 0)
                        {
                            result.Add(new Bitmap(new MemoryStream(fb)));
                        }
                    }
                    if (result.Count >= 2) return result;
                }
            }
            catch { }
        }

        // 2. Trích xuất frame mới
        var frameBytesList = new List<byte[]>();

        // 2a. Nếu có External Provider (Android MediaMetadataRetriever frames extraction)
        if (ExternalNativeFramesProvider != null)
        {
            try
            {
                frameBytesList = ExternalNativeFramesProvider(videoPath, maxFrames, maxWidth, maxHeight);
            }
            catch { }
        }

        // 2b. Nếu trên Windows: Sử dụng native Windows Media Foundation (0 phụ thuộc ngoài, trích xuất full duration)
        if (frameBytesList.Count < 2 && OperatingSystem.IsWindows())
        {
            try
            {
                long durMs = GetVideoDurationMs(videoPath);
                frameBytesList = WindowsMediaFoundationExtractor.ExtractFrames(videoPath, maxFrames, maxWidth, maxHeight, durMs);
            }
            catch { }
        }

        // 2c. Nếu trên Linux hoặc môi trường có ffmpeg
        if (frameBytesList.Count < 2 && (OperatingSystem.IsLinux() || (!OperatingSystem.IsWindows() && !OperatingSystem.IsAndroid())))
        {
            try
            {
                frameBytesList = ExtractFfmpegFrames(videoPath, maxFrames, maxWidth, maxHeight);
            }
            catch { }
        }

        // 2d. Nếu chưa có frame: Thử trích xuất native poster và sinh Cinematic Motion Frames
        if (frameBytesList.Count < 2)
        {
            byte[]? posterBytes = GetVideoThumbnailBytes(videoPath, maxWidth, maxHeight, out _, out _);
            if (posterBytes != null && posterBytes.Length > 0)
            {
                frameBytesList = GenerateCinematicMotionFrames(posterBytes, maxFrames, maxWidth, maxHeight);
            }
        }

        if (frameBytesList.Count == 0) return result;

        // 3. Lưu vào cache trên đĩa để nạp tức thì 0ms ở các lần sau
        try
        {
            if (!Directory.Exists(framesDir)) Directory.CreateDirectory(framesDir);
            for (int i = 0; i < frameBytesList.Count; i++)
            {
                string framePath = Path.Combine(framesDir, $"frame_{i:D2}.jpg");
                await File.WriteAllBytesAsync(framePath, frameBytesList[i], ct).ConfigureAwait(false);
            }
        }
        catch { }

        // 4. Tạo Avalonia Bitmap danh sách
        foreach (var b in frameBytesList)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                result.Add(new Bitmap(new MemoryStream(b)));
            }
            catch { }
        }

        return result;
    }

    public static async Task<List<Bitmap>> GetOrExtractVideoFramesFromBufferAsync(byte[] videoBytes, string ext, int maxFrames = 14, int maxWidth = 480, int maxHeight = 480, System.Threading.CancellationToken ct = default)
    {
        var result = new List<Bitmap>();
        if (videoBytes == null || videoBytes.Length == 0) return result;

        string tempDir = Path.Combine(AppContext.BaseDirectory, ".tmp", "video_thumb_temp");
        try
        {
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            string safeExt = string.IsNullOrWhiteSpace(ext) ? ".webm" : (ext.StartsWith('.') ? ext : "." + ext);
            string tempFile = Path.Combine(tempDir, $"{Guid.NewGuid():N}{safeExt}");
            await File.WriteAllBytesAsync(tempFile, videoBytes, ct).ConfigureAwait(false);

            try
            {
                return await GetOrExtractVideoFramesAsync(tempFile, maxFrames, maxWidth, maxHeight, ct).ConfigureAwait(false);
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
        catch
        {
            return result;
        }
    }

    public static List<byte[]> GenerateCinematicMotionFrames(byte[] posterBytes, int frameCount = 14, int maxWidth = 480, int maxHeight = 480)
    {
        var list = new List<byte[]>();
        if (posterBytes == null || posterBytes.Length == 0) return list;

        try
        {
            using var codec = SKCodec.Create(new MemoryStream(posterBytes));
            if (codec == null) return list;

            using var origBitmap = SKBitmap.Decode(codec);
            if (origBitmap == null || origBitmap.Width <= 0 || origBitmap.Height <= 0) return list;

            int origW = origBitmap.Width;
            int origH = origBitmap.Height;
            int targetW = Math.Min(maxWidth, origW);
            int targetH = (int)Math.Round((double)origH * targetW / origW);
            targetH = Math.Max(10, Math.Min(maxHeight, targetH));

            var targetInfo = new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);

            for (int i = 0; i < frameCount; i++)
            {
                double progress = (double)i / Math.Max(1, frameCount - 1);
                // Hiệu ứng Ken Burns mượt mà: Zoom 100% -> 109%, pan nhẹ sang ngang
                double zoom = 1.0 + 0.09 * Math.Sin(progress * Math.PI);
                double visibleW = origW / zoom;
                double visibleH = origH / zoom;
                double offsetX = (origW - visibleW) * (0.2 + 0.6 * progress);
                double offsetY = (origH - visibleH) * (0.3 + 0.4 * progress);

                var srcRect = new SKRect((float)offsetX, (float)offsetY, (float)(offsetX + visibleW), (float)(offsetY + visibleH));
                var destRect = new SKRect(0, 0, targetW, targetH);

                using var surface = SKSurface.Create(targetInfo);
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Black);
                using (var bmpPaint = new SKPaint { IsAntialias = true })
                {
                    canvas.DrawBitmap(origBitmap, srcRect, destRect, bmpPaint);
                }

                // Vẽ vạch timeline video chạy mượt ở đáy khung ảnh preview
                using (var progressPaint = new SKPaint
                {
                    Color = new SKColor(0, 229, 255, 200), // Cyan neon video playback bar
                    Style = SKPaintStyle.Fill,
                    IsAntialias = true
                })
                {
                    float barW = (float)(targetW * progress);
                    canvas.DrawRect(0, targetH - 3, barW, 3, progressPaint);
                }

                using var snap = surface.Snapshot();
                using var encoded = snap.Encode(SKEncodedImageFormat.Jpeg, 88);
                list.Add(encoded.ToArray());
            }
        }
        catch { }

        return list;
    }

    private static List<byte[]> ExtractFfmpegFrames(string videoPath, int maxFrames, int maxWidth, int maxHeight)
    {
        var list = new List<byte[]>();
        try
        {
            long durationMs = VideoMetadataParser.GetVideoDurationMs(videoPath);
            int count = Math.Max(8, Math.Min(24, maxFrames));
            double durSec = durationMs > 0 ? durationMs / 1000.0 : 15.0;
            double stepSec = Math.Max(0.5, (durSec * 0.95) / count);

            string tempDir = Path.Combine(AppContext.BaseDirectory, ".tmp", "ffmpeg_frames_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    double seekSec = i * stepSec;
                    string outPath = Path.Combine(tempDir, $"f_{i:D3}.jpg");
                    var psi = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = $"-y -ss {seekSec:F2} -i \"{videoPath}\" -vframes 1 -vf \"scale='min({maxWidth},iw)':-1\" -q:v 3 \"{outPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        p.WaitForExit(1500);
                        if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                        {
                            byte[] b = File.ReadAllBytes(outPath);
                            if (b.Length > 0) list.Add(b);
                        }
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        catch { }
        return list;
    }

    private static byte[]? ExtractFfmpegFrameAtTime(string videoPath, long timeMs, int maxWidth, int maxHeight)
    {
        try
        {
            double seekSec = Math.Max(0, timeMs / 1000.0);
            string tempDir = Path.Combine(AppContext.BaseDirectory, ".tmp", "ffmpeg_exact_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string outPath = Path.Combine(tempDir, "exact.jpg");
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-y -ss {seekSec:F2} -i \"{videoPath}\" -vframes 1 -vf \"scale='min({maxWidth},iw)':-1\" -q:v 2 \"{outPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    p.WaitForExit(2000);
                    if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                    {
                        return File.ReadAllBytes(outPath);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        catch { }
        return null;
    }

    private static string GetCacheFilePath(string videoPath, int maxWidth, int maxHeight)
    {
        long len = 0;
        long time = 0;
        try
        {
            var fi = new FileInfo(videoPath);
            if (fi.Exists)
            {
                len = fi.Length;
                time = fi.LastWriteTimeUtc.Ticks;
            }
        }
        catch { }

        string rawKey = $"{videoPath}_{len}_{time}_v2";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        string hashStr = Convert.ToHexString(hash).Substring(0, 24).ToLowerInvariant();

        string cacheDir = Path.Combine(AppContext.BaseDirectory, ".tmp", "preview-cache", "videos");
        return Path.Combine(cacheDir, $"{hashStr}_{maxWidth}x{maxHeight}.jpg");
    }

    #region Windows Shell COM Interop
    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [Flags]
    private enum SIIGBF
    {
        SIIGBF_RESIZETOFIT = 0x00,
        SIIGBF_BIGGERSIZEOK = 0x01,
        SIIGBF_MEMORYONLY = 0x02,
        SIIGBF_ICONONLY = 0x04,
        SIIGBF_THUMBNAILONLY = 0x08,
        SIIGBF_INCACHEONLY = 0x10,
        SIIGBF_CROPTOSQUARE = 0x20,
        SIIGBF_WIDETHUMBNAILS = 0x40,
        SIIGBF_ICONBACKGROUND = 0x80,
        SIIGBF_SCALEUP = 0x100
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory ppv);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, [Out] byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    private static byte[]? ExtractWindowsShellThumbnailBytes(string videoPath, int maxWidth, int maxHeight, out int outW, out int outH)
    {
        outW = 0;
        outH = 0;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            Guid uuid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            SHCreateItemFromParsingName(videoPath, IntPtr.Zero, uuid, out IShellItemImageFactory factory);
            if (factory == null) return null;

            int targetDimW = Math.Max(64, maxWidth);
            int targetDimH = Math.Max(64, maxHeight);

            int hr = factory.GetImage(new SIZE(targetDimW, targetDimH), SIIGBF.SIIGBF_RESIZETOFIT, out hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return null;

            BITMAP bm = new();
            if (GetObject(hbm, Marshal.SizeOf(typeof(BITMAP)), ref bm) == 0) return null;

            int w = bm.bmWidth;
            int h = bm.bmHeight;
            if (w <= 0 || h <= 0) return null;

            outW = w;
            outH = h;

            IntPtr hdc = GetDC(IntPtr.Zero);
            try
            {
                BITMAPINFOHEADER bih = new();
                bih.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bih.biWidth = w;
                bih.biHeight = -h; // top-down
                bih.biPlanes = 1;
                bih.biBitCount = 32;
                bih.biCompression = 0; // BI_RGB

                byte[] pixels = new byte[w * h * 4];
                int lines = GetDIBits(hdc, hbm, 0, (uint)h, pixels, ref bih, 0);
                if (lines <= 0) return null;

                // Sử dụng SkiaSharp encode sang ảnh JPEG chất lượng 90
                var imageInfo = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var skBitmap = new SKBitmap();
                GCHandle pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    skBitmap.InstallPixels(imageInfo, pinned.AddrOfPinnedObject());
                    using var image = SKImage.FromBitmap(skBitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
                    return data.ToArray();
                }
                finally
                {
                    pinned.Free();
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
        }
    }
    #endregion

    #region Poster Card Generator (SkiaSharp)
    private static byte[] CreatePosterCardBytes(string videoPath, int maxWidth, int maxHeight, out int width, out int height)
    {
        string fileName = Path.GetFileName(videoPath);
        long fileSize = 0;
        try { fileSize = new FileInfo(videoPath).Length; } catch { }
        string ext = Path.GetExtension(videoPath).ToLowerInvariant();

        return CreatePosterCardFromInfo(fileName, fileSize, ext, maxWidth, maxHeight, out width, out height);
    }

    public static byte[] CreatePosterCardFromInfo(string fileName, long fileSize, string ext, int maxWidth, int maxHeight, out int width, out int height)
    {
        width = Math.Max(480, Math.Min(640, maxWidth));
        height = (int)Math.Round(width * 9.0 / 16.0); // Chuẩn tỉ lệ video 16:9

        var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(imageInfo);
        var canvas = surface.Canvas;

        // 1. Nền gradient dark modern
        using (var paintBg = new SKPaint())
        {
            paintBg.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, height),
                new[] { new SKColor(11, 16, 26), new SKColor(23, 33, 53) },
                null,
                SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, width, height, paintBg);
        }

        // 2. Viền khung tinh tế
        using (var borderPaint = new SKPaint
        {
            Color = new SKColor(51, 65, 85),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true
        })
        {
            canvas.DrawRoundRect(1, 1, width - 2, height - 2, 8, 8, borderPaint);
        }

        float centerX = width / 2f;
        float centerY = height / 2f - 18f;

        // 3. Vòng tròn biểu tượng Play Video
        using (var circlePaint = new SKPaint
        {
            Color = new SKColor(56, 189, 248, 40),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        })
        {
            canvas.DrawCircle(centerX, centerY, 38f, circlePaint);
        }
        using (var circleBorderPaint = new SKPaint
        {
            Color = new SKColor(56, 189, 248),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f,
            IsAntialias = true
        })
        {
            canvas.DrawCircle(centerX, centerY, 38f, circleBorderPaint);
        }

        // 4. Nút Play Triangle
        using (var playPaint = new SKPaint
        {
            Color = new SKColor(56, 189, 248),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        })
        {
            using var playPath = new SKPath();
            float pSize = 18f;
            playPath.MoveTo(centerX - pSize * 0.5f, centerY - pSize);
            playPath.LineTo(centerX + pSize, centerY);
            playPath.LineTo(centerX - pSize * 0.5f, centerY + pSize);
            playPath.Close();
            canvas.DrawPath(playPath, playPaint);
        }

        // 5. Badge định dạng Video (🎬 WEBM / 🎬 MP4 / 🎬 MKV / 🎬 TS)
        string badgeText = "🎬 " + ext.TrimStart('.').ToUpperInvariant();
        using (var badgeBgPaint = new SKPaint
        {
            Color = new SKColor(124, 58, 237),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        })
        using (var font = new SKFont(SKTypeface.Default, 13f))
        using (var textPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        })
        {
            float badgeW = 90f;
            float badgeH = 24f;
            float badgeX = centerX - badgeW / 2f;
            float badgeY = centerY + 52f;
            canvas.DrawRoundRect(badgeX, badgeY, badgeW, badgeH, 4, 4, badgeBgPaint);
            canvas.DrawText(badgeText, centerX, badgeY + 17f, SKTextAlign.Center, font, textPaint);
        }

        // 6. Tên file & Dung lượng
        using (var fontName = new SKFont(SKTypeface.Default, 12f))
        using (var namePaint = new SKPaint
        {
            Color = new SKColor(241, 245, 249),
            IsAntialias = true
        })
        {
            string displayTitle = fileName;
            if (displayTitle.Length > 45) displayTitle = displayTitle.Substring(0, 42) + "...";
            canvas.DrawText(displayTitle, centerX, height - 32f, SKTextAlign.Center, fontName, namePaint);
        }

        if (fileSize > 0)
        {
            string sizeStr = FormatFileSize(fileSize);
            using var fontSize = new SKFont(SKTypeface.Default, 10.5f);
            using var sizePaint = new SKPaint
            {
                Color = new SKColor(148, 163, 184),
                IsAntialias = true
            };
            canvas.DrawText(sizeStr, centerX, height - 14f, SKTextAlign.Center, fontSize, sizePaint);
        }

        using var imageFinal = surface.Snapshot();
        using var finalData = imageFinal.Encode(SKEncodedImageFormat.Jpeg, 90);
        return finalData.ToArray();
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }
    #endregion
}
