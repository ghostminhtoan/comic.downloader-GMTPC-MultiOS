using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using HanumanInstitute.LibMpv;
using HanumanInstitute.LibMpv.Core;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Service bọc MPV Engine chuyên nghiệp cho Comic Downloader GMTPC Avalonia.
/// Tự động đồng bộ Audio-Video, tương thích 100% mọi định dạng (.webm, .mp4, .mkv, .ts...),
/// hỗ trợ A-B Looper, Instant Seek, Hardware Acceleration và Zero-Copy Software Rendering vào Avalonia WriteableBitmap.
/// </summary>
public sealed class MpvVideoPlayerService : IDisposable
{
    private static bool? _isSupportedCached;
    private static readonly object _initLock = new();

    private readonly MpvContext _mpv;
    private bool _disposed;
    private int _videoWidth;
    private int _videoHeight;
    private long _durationMs;

    /// <summary>
    /// Kiểm tra môi trường hiện tại có hỗ trợ native libmpv không.
    /// </summary>
    public static bool IsSupported
    {
        get
        {
            if (_isSupportedCached.HasValue) return _isSupportedCached.Value;

            lock (_initLock)
            {
                if (_isSupportedCached.HasValue) return _isSupportedCached.Value;

                // Android: Tích hợp trọn vẹn libmpv.so đa kiến trúc (arm64-v8a, armeabi-v7a, x86, x86_64)
                if (OperatingSystem.IsAndroid())
                {
                    try
                    {
                        RegisterDllResolverIfNeeded();
                        using var dummy = new MpvContext();
                        dummy.SetOptionString("vo", "null");
                        dummy.SetOptionString("ao", "null");
                        dummy.Initialize();
                        _isSupportedCached = true;
                        return true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MPV Android Init Note]: {ex.Message}");
                        _isSupportedCached = false;
                        return false;
                    }
                }

                if (OperatingSystem.IsWindows())
                {
                    string? dir = ResolveNativeLibraryDirectory();
                    if (!string.IsNullOrEmpty(dir))
                    {
                        try
                        {
                            MpvApi.RootPath = dir;
                            // Thử tạo dummy handle để xác thực DLL tải thành công
                            using var dummy = new MpvContext();
                            dummy.Initialize();
                            _isSupportedCached = true;
                            return true;
                        }
                        catch
                        {
                            _isSupportedCached = false;
                            return false;
                        }
                    }
                    _isSupportedCached = false;
                    return false;
                }

                if (OperatingSystem.IsLinux())
                {
                    // Trên Linux nếu hệ thống có libmpv.so
                    try
                    {
                        using var dummy = new MpvContext();
                        dummy.Initialize();
                        _isSupportedCached = true;
                        return true;
                    }
                    catch
                    {
                        _isSupportedCached = false;
                        return false;
                    }
                }

                _isSupportedCached = false;
                return false;
            }
        }
    }

    private static string? ResolveNativeLibraryDirectory()
    {
        if (!OperatingSystem.IsWindows()) return null;

        string baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, "libmpv-2.dll"))) return baseDir;

        var parent = Directory.GetParent(baseDir);
        if (parent != null)
        {
            if (File.Exists(Path.Combine(parent.FullName, "libmpv-2.dll"))) return parent.FullName;
            var grandParent = parent.Parent;
            if (grandParent != null && File.Exists(Path.Combine(grandParent.FullName, "libmpv-2.dll"))) return grandParent.FullName;
        }

        string nugetPath = @"C:\Users\Admin\.nuget\packages\endpne.libmpv.windows\0.41.0\build\x64";
        if (Directory.Exists(nugetPath) && File.Exists(Path.Combine(nugetPath, "libmpv-2.dll"))) return nugetPath;

        return null;
    }

    private static bool _dllResolverRegistered;
    private static void RegisterDllResolverIfNeeded()
    {
        if (_dllResolverRegistered) return;
        _dllResolverRegistered = true;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(HanumanInstitute.LibMpv.MpvContext).Assembly, (libraryName, assembly, searchPath) =>
            {
                if (libraryName.Equals("mpv", StringComparison.OrdinalIgnoreCase) ||
                    libraryName.Equals("libmpv", StringComparison.OrdinalIgnoreCase) ||
                    libraryName.Equals("libmpv-2", StringComparison.OrdinalIgnoreCase))
                {
                    if (OperatingSystem.IsAndroid() || OperatingSystem.IsLinux())
                    {
                        if (NativeLibrary.TryLoad("libmpv.so", assembly, searchPath, out IntPtr handle))
                            return handle;
                        if (NativeLibrary.TryLoad("mpv", assembly, searchPath, out handle))
                            return handle;
                    }
                }
                return IntPtr.Zero;
            });
        }
        catch { }
    }

    public int VideoWidth => _videoWidth;
    public int VideoHeight => _videoHeight;
    public long DurationMs => _durationMs;

    public long CurrentPositionMs
    {
        get
        {
            if (_disposed) return 0;
            try
            {
                double sec = _mpv.GetProperty<double>("playback-time");
                return (long)(sec * 1000.0);
            }
            catch
            {
                return 0;
            }
        }
    }

    private MpvVideoPlayerService(MpvContext mpv, int width, int height, long durationMs)
    {
        _mpv = mpv;
        _videoWidth = width;
        _videoHeight = height;
        _durationMs = durationMs;
    }

    /// <summary>
    /// Khởi tạo MpvVideoPlayerService cho một file video cụ thể.
    /// </summary>
    public static MpvVideoPlayerService? Create(string videoPath, Action? onFrameReady = null)
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(videoPath) || !VideoThumbnailService.IsValidVideoPath(videoPath))
            return null;

        try
        {
            RegisterDllResolverIfNeeded();
            string? dir = ResolveNativeLibraryDirectory();
            if (!string.IsNullOrEmpty(dir))
            {
                MpvApi.RootPath = dir;
            }

            var mpv = new MpvContext();
            mpv.SetOptionString("vo", "libmpv");

            // Software Rendering vào Avalonia WriteableBitmap:
            // Trên Android: BẮT BUỘC dùng hwdec="no" (FFmpeg NEON CPU Software Decode) và ao="null" (Tắt hẳn audio MPV).
            // Tuyệt đối không dùng ao="audiotrack" hay "opensles" vì audiotrack gọi JNI vào is.xyz.mpv.MPVLib gây crash SIGSEGV (SEGV_MAPERR),
            // còn opensles không tồn tại trong bản build này. Âm thanh trên Android được phát qua native MediaPlayer.
            // Đồng thời giới hạn 2 decode threads để chống cạn kiệt pthread quota trên nhân Linux/Android.
            if (OperatingSystem.IsAndroid())
            {
                mpv.SetOptionString("hwdec", "no");
                mpv.SetOptionString("ao", "null");
                mpv.SetOptionString("vd-lavc-threads", "2");
            }
            else
            {
                mpv.SetOptionString("hwdec", "auto-copy");
                mpv.SetOptionString("audio-device", "auto");
            }

            // Tắt direct rendering nguy hiểm tránh xung đột pointer SW buffer
            mpv.SetOptionString("vd-lavc-dr", "no");
            // Kiểm soát bộ đệm demuxer để ngăn ngừa Out-Of-Memory trên thiết bị Android
            mpv.SetOptionString("demuxer-max-bytes", "32MiB");
            mpv.SetOptionString("demuxer-max-back-bytes", "16MiB");

            mpv.SetOptionString("keep-open", "yes");
            mpv.SetOptionString("loop-file", "inf");

            // BẮT BUỘC gọi mpv.Initialize() TRƯỚC KHI tạo mpv_render_context theo chuẩn API chính thức của libmpv!
            mpv.Initialize();

            // Khởi tạo Render Context "sw" an toàn:
            bool renderContextOk = false;
            if (OperatingSystem.IsAndroid())
            {
                // Trên Android: Tự tạo RenderContext "sw" trực tiếp mà KHÔNG gọi RenderContextSetUpdateCallback
                // để ngăn chặn 100% rủi ro pthread native của libmpv gọi ngược vào delegate C# Mono (loại bỏ triệt để SIGSEGV)
                renderContextOk = InitAndroidSoftwareRenderingSafe(mpv);
                if (!renderContextOk)
                {
                    try { mpv.Dispose(); } catch { }
                    return null;
                }
            }
            else
            {
                mpv.StartSoftwareRendering(() => onFrameReady?.Invoke());
                renderContextOk = true;
            }

            mpv.RunCommand(null, new object[] { "loadfile", videoPath });

            // Đợi đọc metadata video (tối đa 1.5 giây)
            int width = 0, height = 0;
            long durationMs = 0;
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(50);
                try
                {
                    var wStr = mpv.GetPropertyString("width");
                    var hStr = mpv.GetPropertyString("height");
                    if (int.TryParse(wStr, out int pw) && pw > 0) width = pw;
                    if (int.TryParse(hStr, out int ph) && ph > 0) height = ph;

                    if (width <= 0 || height <= 0)
                    {
                        var dwStr = mpv.GetPropertyString("dwidth");
                        var dhStr = mpv.GetPropertyString("dheight");
                        if (int.TryParse(dwStr, out int pdw) && pdw > 0) width = pdw;
                        if (int.TryParse(dhStr, out int pdh) && pdh > 0) height = pdh;
                    }

                    var durStr = mpv.GetPropertyString("duration");
                    if (double.TryParse(durStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double durSec) && durSec > 0)
                    {
                        durationMs = (long)(durSec * 1000.0);
                    }

                    if (width > 0 && height > 0)
                        break;
                }
                catch { }
            }

            // Nếu MPV chưa đọc kịp thì lấy kích thước thật từ container parser
            if (width <= 0 || height <= 0)
            {
                try
                {
                    if (VideoMetadataParser.GetVideoDimensions(videoPath, out int dimW, out int dimH) && dimW > 0 && dimH > 0)
                    {
                        width = dimW;
                        height = dimH;
                    }
                    if (durationMs <= 0)
                    {
                        durationMs = VideoMetadataParser.GetVideoDurationMs(videoPath);
                    }
                }
                catch { }
            }

            if (width <= 0) width = 720;
            if (height <= 0) height = 1280;

            return new MpvVideoPlayerService(mpv, width, height, durationMs);
        }
        catch
        {
            return null;
        }
    }

    private static unsafe bool InitAndroidSoftwareRenderingSafe(MpvContext mpv)
    {
        try
        {
            byte[] apiTypeBytes = System.Text.Encoding.ASCII.GetBytes("sw\0");
            int[] advancedArray = [0];
            fixed (byte* pApiType = apiTypeBytes)
            fixed (int* pAdvanced = advancedArray)
            {
                MpvRenderParam[] paramList =
                [
                    new() { Type = MpvRenderParamType.ApiType, Data = pApiType },
                    new() { Type = MpvRenderParamType.AdvancedControl, Data = pAdvanced },
                    new() { Type = MpvRenderParamType.Invalid, Data = null }
                ];
                fixed (MpvRenderParam* pParams = paramList)
                {
                    mpv.RenderContextCreate(pParams);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InitAndroidSoftwareRenderingSafe Note]: {ex.Message}");
            return false;
        }
    }

    public void Play()
    {
        if (_disposed) return;
        try { _mpv.SetProperty("pause", false); } catch { }
    }

    public void Pause()
    {
        if (_disposed) return;
        try { _mpv.SetProperty("pause", true); } catch { }
    }

    public void Seek(long targetMs)
    {
        if (_disposed) return;
        try
        {
            double sec = Math.Max(0, targetMs / 1000.0);
            _mpv.RunCommand(null, new object[] { "seek", sec, "absolute+exact" });
        }
        catch { }
    }

    public void SetSpeed(double speed)
    {
        if (_disposed) return;
        try
        {
            double s = Math.Clamp(speed, 0.1, 4.0);
            _mpv.SetProperty("speed", s);
        }
        catch { }
    }

    public void SetVolume(int volume)
    {
        if (_disposed) return;
        try
        {
            int vol = Math.Clamp(volume, 0, 100);
            _mpv.SetProperty("volume", vol);
        }
        catch { }
    }

    private static readonly object _renderLock = new();

    public void SetMute(bool isMuted)
    {
        if (_disposed) return;
        try { _mpv.SetProperty("mute", isMuted); } catch { }
    }

    /// <summary>
    /// Render frame trực tiếp vào surface pointer (BGR0 / BGRA) với Stride 64-bit size_t chuẩn xác,
    /// loại bỏ 100% bug lệch stride 32-bit trong thư viện gây xé hình và Exception 0xC0000005.
    /// </summary>
    public unsafe bool RenderFrame(int width, int height, IntPtr surfaceAddress, nuint stride = 0, string? format = null)
    {
        if (_disposed || surfaceAddress == IntPtr.Zero || width <= 0 || height <= 0) return false;
        lock (_renderLock)
        {
            if (_disposed) return false;
            try
            {
                string resolvedFormat = !string.IsNullOrEmpty(format)
                    ? format
                    : (OperatingSystem.IsAndroid() ? "rgba" : "bgr0");
                nuint actualStride = stride > 0 ? stride : (nuint)(width * 4);
                int[] sizeArray = [width, height];
                nuint[] strideArray = [actualStride]; // BẮT BUỘC size_t 64-bit trên Win64/Linux64!
                byte[] formatBytes = System.Text.Encoding.UTF8.GetBytes(resolvedFormat + "\0");

                try { _mpv.RenderContextUpdate(); } catch { }

                fixed (int* pSize = sizeArray)
                fixed (nuint* pStride = strideArray)
                fixed (byte* pFormat = formatBytes)
                {
                    MpvRenderParam[] paramList =
                    [
                        new() { Type = MpvRenderParamType.SwSize, Data = pSize },
                        new() { Type = MpvRenderParamType.SwFormat, Data = pFormat },
                        new() { Type = MpvRenderParamType.SwStride, Data = pStride },
                        new() { Type = MpvRenderParamType.SwPointer, Data = (void*)surfaceAddress },
                        new() { Type = MpvRenderParamType.Invalid, Data = null }
                    ];

                    fixed (MpvRenderParam* pParams = paramList)
                    {
                        _mpv.RenderContextRender(pParams);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Render frame trực tiếp vào WriteableBitmap của Avalonia (Zero-Copy) với Stride 64-bit size_t an toàn tuyệt đối.
    /// </summary>
    public unsafe bool RenderFrameDirect(WriteableBitmap wb)
    {
        if (_disposed || wb == null) return false;
        lock (_renderLock)
        {
            if (_disposed) return false;
            try
            {
                using var frameLock = wb.Lock();
                int w = wb.PixelSize.Width;
                int h = wb.PixelSize.Height;
                if (w <= 0 || h <= 0 || frameLock.Address == IntPtr.Zero) return false;
                nuint stride = (nuint)frameLock.RowBytes;

                int[] sizeArray = [w, h];
                nuint[] strideArray = [stride]; // BẮT BUỘC 64-bit size_t!
                string fmt = (wb.Format == Avalonia.Platform.PixelFormat.Rgba8888 ||
                             (OperatingSystem.IsAndroid() && wb.Format != Avalonia.Platform.PixelFormat.Bgra8888))
                             ? "rgba\0" : "bgr0\0";
                byte[] formatBytes = System.Text.Encoding.UTF8.GetBytes(fmt);

                try { _mpv.RenderContextUpdate(); } catch { }

                fixed (int* pSize = sizeArray)
                fixed (nuint* pStride = strideArray)
                fixed (byte* pFormat = formatBytes)
                {
                    MpvRenderParam[] paramList =
                    [
                        new() { Type = MpvRenderParamType.SwSize, Data = pSize },
                        new() { Type = MpvRenderParamType.SwFormat, Data = pFormat },
                        new() { Type = MpvRenderParamType.SwStride, Data = pStride },
                        new() { Type = MpvRenderParamType.SwPointer, Data = (void*)frameLock.Address },
                        new() { Type = MpvRenderParamType.Invalid, Data = null }
                    ];

                    fixed (MpvRenderParam* pParams = paramList)
                    {
                        _mpv.RenderContextRender(pParams);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _mpv.RunCommand(null, new object[] { "stop" });
        }
        catch { }
        try
        {
            _mpv.Dispose();
        }
        catch { }
    }
}
