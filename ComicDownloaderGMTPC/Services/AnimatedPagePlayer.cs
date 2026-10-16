using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ComicDownloaderGMTPC.Models;
using SkiaSharp;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Trình phát ảnh động chuyên dụng (GIF và WebP Animated) hiệu năng cao đa nền tảng (Windows, Linux, Android).
/// Sử dụng kiến trúc Double-Buffering qua WriteableBitmap kết hợp SKCodec native của Google Skia:
/// 1. Tận dụng 100% tài nguyên CPU luồng nền (Task.Run), 0 byte cấp phát rác (Zero GC per frame).
/// 2. Double-Buffering (wb1 & wb2) loại bỏ hoàn toàn hiện tượng nhấp nháy (Tearing/Flickering).
/// 3. Hỗ trợ đầy đủ bộ hiệu ứng hình ảnh (Invert, Grayscale, Contrast, Saturation) nếu người dùng bật.
/// </summary>
public sealed class AnimatedPagePlayer : IDisposable
{
    private readonly ReaderPageItem _page;
    private readonly ReaderVisualEffectSettings? _effects;
    private readonly SKData _skData;
    private readonly SKCodec _codec;
    private readonly int _frameCount;
    private readonly int _width;
    private readonly int _height;
    private readonly SKImageInfo _info;
    private readonly SKBitmap _canvasBitmap;
    private readonly WriteableBitmap _wb1;
    private readonly WriteableBitmap _wb2;
    private readonly int _byteCount;
    private readonly int[] _frameDurations;

    private CancellationTokenSource? _cts;
    private bool _isDisposed;
    private bool _isStarted;

    public int FrameCount => _frameCount;
    public int Width => _width;
    public int Height => _height;

    /// <summary>
    /// Kiểm tra nhanh xem mảng byte ảnh có phải là ảnh động (GIF hoặc WebP với nhiều hơn 1 frame) hay không.
    /// </summary>
    public static bool IsAnimatedImage(byte[]? rawBytes, out int frameCount, out int width, out int height)
    {
        frameCount = 0;
        width = 0;
        height = 0;
        if (rawBytes == null || rawBytes.Length < 16) return false;

        bool isGif = UniversalImageDecoder.IsGif(rawBytes);
        bool isWebp = UniversalImageDecoder.IsWebP(rawBytes);

        if (!isGif && !isWebp) return false;

        try
        {
            using var data = SKData.CreateCopy(rawBytes);
            using var codec = SKCodec.Create(data);
            if (codec != null && codec.FrameCount > 1)
            {
                frameCount = codec.FrameCount;
                width = codec.Info.Width;
                height = codec.Info.Height;
                return true;
            }
        }
        catch { }

        return false;
    }

    public AnimatedPagePlayer(ReaderPageItem page, byte[] rawBytes, ReaderVisualEffectSettings? effects = null)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        _effects = effects;

        _skData = SKData.CreateCopy(rawBytes);
        _codec = SKCodec.Create(_skData) ?? throw new InvalidOperationException("Không thể khởi tạo SKCodec từ dữ liệu ảnh.");
        _frameCount = _codec.FrameCount;
        _width = _codec.Info.Width;
        _height = _codec.Info.Height;

        _info = new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _canvasBitmap = new SKBitmap(_info);
        _byteCount = _info.BytesSize;

        _wb1 = new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        _wb2 = new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        _frameDurations = new int[_frameCount];
        for (int i = 0; i < _frameCount; i++)
        {
            int d = _codec.FrameInfo[i].Duration;
            if (d <= 15) d = 100; // Tiêu chuẩn web/GIF: nếu delay <= 15ms thì fallback về 100ms
            _frameDurations[i] = d;
        }
    }

    /// <summary>
    /// Bắt đầu nạp Frame 0 ngay lập tức lên UI và kích hoạt luồng phát ảnh động nền.
    /// </summary>
    public void Start()
    {
        if (_isDisposed || _isStarted || _frameCount <= 1) return;
        _isStarted = true;

        try
        {
            // Giải mã Frame 0 ngay lập tức để trang không bao giờ bị trắng
            var opts0 = new SKCodecOptions(0);
            _codec.GetPixels(_info, _canvasBitmap.GetPixels(), opts0);

            if (_effects != null && _effects.HasAnyEffect)
            {
                ComicReaderService.ApplyPixelTransformsInPlace(_canvasBitmap, _effects);
            }

            CopyBitmapToWriteableBitmap(_canvasBitmap, _wb1);

            Dispatcher.UIThread.Post(() =>
            {
                if (!_isDisposed && _page.IsLoaded)
                {
                    _page.Image = _wb1;
                }
            });

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(() => PlayLoopAsync(token), token);
        }
        catch { }
    }

    private async Task PlayLoopAsync(CancellationToken ct)
    {
        bool useWb1 = false; // Frame 0 đã dùng wb1, frame 1 tiếp theo sẽ dùng wb2
        int currentFrame = 1 % _frameCount;

        // Chờ thời lượng của Frame 0
        int initialDelay = _frameDurations[0];
        try
        {
            await Task.Delay(initialDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested && !_isDisposed)
        {
            int duration = _frameDurations[currentFrame];

            try
            {
                var opts = new SKCodecOptions(currentFrame);
                var res = _codec.GetPixels(_info, _canvasBitmap.GetPixels(), opts);

                if (res == SKCodecResult.Success || res == SKCodecResult.IncompleteInput)
                {
                    if (_effects != null && _effects.HasAnyEffect)
                    {
                        ComicReaderService.ApplyPixelTransformsInPlace(_canvasBitmap, _effects);
                    }

                    var targetWb = useWb1 ? _wb1 : _wb2;
                    useWb1 = !useWb1;

                    CopyBitmapToWriteableBitmap(_canvasBitmap, targetWb);

                    if (!ct.IsCancellationRequested && !_isDisposed)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (!ct.IsCancellationRequested && !_isDisposed && _page.IsLoaded)
                            {
                                _page.Image = targetWb;
                            }
                        }, DispatcherPriority.Render);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }

            try
            {
                await Task.Delay(duration, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            currentFrame = (currentFrame + 1) % _frameCount;
        }
    }

    private void CopyBitmapToWriteableBitmap(SKBitmap src, WriteableBitmap dst)
    {
        using var locked = dst.Lock();
        unsafe
        {
            Buffer.MemoryCopy(
                (void*)src.GetPixels(),
                (void*)locked.Address,
                _byteCount,
                _byteCount);
        }
    }

    /// <summary>
    /// Dừng luồng phát hoạt ảnh (khi cuộn trang ra xa hoặc chuyển chương).
    /// </summary>
    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch { }
        _cts = null;
        _isStarted = false;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Stop();

        try
        {
            _codec.Dispose();
            _skData.Dispose();
            _canvasBitmap.Dispose();
            _wb1.Dispose();
            _wb2.Dispose();
        }
        catch { }
    }
}

