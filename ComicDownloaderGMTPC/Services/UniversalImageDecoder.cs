using System;
using System.IO;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Bộ giải mã và xử lý hình ảnh đa năng độc lập 100% (Universal Image Codec).
/// Tích hợp sẵn bộ giải mã WebP (VP8, VP8L, VP8X, Lossless, Lossy), PNG, JPEG, BMP, GIF, TIFF
/// chạy đồng nhất trên Windows, Linux và Android mà không phụ thuộc codec hệ điều hành.
/// </summary>
public static class UniversalImageDecoder
{
    public static bool IsJpeg(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
    }

    public static bool IsWebP(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 12 &&
               bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
               bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';
    }

    public static bool IsPng(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
    }

    /// <summary>
    /// Chuyển đổi bất kỳ tệp ảnh nào (WebP, PNG, BMP, GIF, Fake JPG...) thành luồng byte JPEG chuẩn 100% để nhúng vào tài liệu PDF.
    /// Đảm bảo không bao giờ xuất byte WebP/rác vào luồng DCTDecode của PDF làm phát sinh lỗi Blank Image (ảnh trắng).
    /// </summary>
    public static (byte[] JpegBytes, int Width, int Height) ConvertToStandardJpeg(byte[] rawBytes, int quality = 95)
    {
        if (rawBytes == null || rawBytes.Length == 0)
        {
            return (CreateBlankJpeg(1080, 1920), 1080, 1920);
        }

        // 1. Nếu là file JPEG thực sự
        if (IsJpeg(rawBytes))
        {
            if (TryGetJpegDimensions(rawBytes, out int w, out int h) && w > 0 && h > 0)
            {
                return (rawBytes, w, h);
            }
        }

        // 2. Sử dụng SixLabors.ImageSharp giải mã WebP, PNG, BMP, TIFF, GIF (100% Managed, không cần cài codec hệ điều hành)
        try
        {
            using var image = Image.Load<Rgb24>(rawBytes);
            int width = image.Width;
            int height = image.Height;

            using var ms = new MemoryStream();
            var encoder = new JpegEncoder
            {
                Quality = Math.Clamp(quality, 50, 100)
            };
            image.SaveAsJpeg(ms, encoder);
            return (ms.ToArray(), width, height);
        }
        catch
        {
            // Fallback sang SkiaSharp
        }

        // 3. Fallback sang SkiaSharp nếu cần
        try
        {
            using var skData = SKData.CreateCopy(rawBytes);
            using var codec = SKCodec.Create(skData);
            if (codec != null)
            {
                using var bitmap = SKBitmap.Decode(codec);
                if (bitmap != null)
                {
                    using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgb888x, SKAlphaType.Opaque));
                    if (surface != null)
                    {
                        var canvas = surface.Canvas;
                        canvas.Clear(SKColors.White);
                        canvas.DrawBitmap(bitmap, 0, 0);
                        using var img = surface.Snapshot();
                        using var data = img.Encode(SKEncodedImageFormat.Jpeg, quality);
                        if (data != null)
                        {
                            return (data.ToArray(), bitmap.Width, bitmap.Height);
                        }
                    }

                    using var dataDirect = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality);
                    if (dataDirect != null)
                    {
                        return (dataDirect.ToArray(), bitmap.Width, bitmap.Height);
                    }
                }
            }
        }
        catch
        {
        }

        // 4. Fallback an toàn: Tạo ảnh placeholder trắng hợp lệ thay vì nhét byte WebP làm hỏng PDF
        return (CreateBlankJpeg(1080, 1920), 1080, 1920);
    }

    /// <summary>
    /// Giải mã bất kỳ file ảnh nào thành SKBitmap an toàn đa nền tảng (hỗ trợ đầy đủ WebP, PNG, JPG, BMP...).
    /// </summary>
    public static SKBitmap? DecodeToSkBitmap(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            return DecodeToSkBitmap(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Giải mã mảng byte thành SKBitmap an toàn đa nền tảng.
    /// </summary>
    public static SKBitmap? DecodeToSkBitmap(byte[] rawBytes)
    {
        if (rawBytes == null || rawBytes.Length == 0) return null;

        // 1. Thử SkiaSharp trước (nhanh nhất)
        try
        {
            using var data = SKData.CreateCopy(rawBytes);
            using var codec = SKCodec.Create(data);
            if (codec != null)
            {
                var bitmap = SKBitmap.Decode(codec);
                if (bitmap != null) return bitmap;
            }
        }
        catch { }

        try
        {
            var bmp = SKBitmap.Decode(rawBytes);
            if (bmp != null) return bmp;
        }
        catch { }

        // 2. Giải mã bằng SixLabors.ImageSharp (hỗ trợ 100% WebP VP8/VP8L/VP8X, PNG, GIF, BMP)
        try
        {
            using var image = Image.Load<Rgba32>(rawBytes);
            var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var skBitmap = new SKBitmap(info);
            byte[] pixelBytes = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixelBytes);
            Marshal.Copy(pixelBytes, 0, skBitmap.GetPixels(), pixelBytes.Length);
            return skBitmap;
        }
        catch
        {
            // Fallback chuyển qua Jpeg rồi nạp vào SkiaSharp
            try
            {
                using var image = Image.Load<Rgb24>(rawBytes);
                using var ms = new MemoryStream();
                image.SaveAsJpeg(ms);
                ms.Position = 0;
                return SKBitmap.Decode(ms);
            }
            catch { }
        }

        return null;
    }

    private static bool TryGetJpegDimensions(byte[] rawBytes, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            using var ms = new MemoryStream(rawBytes);
            using var codec = SKCodec.Create(ms);
            if (codec != null && codec.Info.Width > 0 && codec.Info.Height > 0)
            {
                width = codec.Info.Width;
                height = codec.Info.Height;
                return true;
            }
        }
        catch { }

        try
        {
            var info = Image.Identify(rawBytes);
            if (info != null && info.Width > 0 && info.Height > 0)
            {
                width = info.Width;
                height = info.Height;
                return true;
            }
        }
        catch { }

        return false;
    }

    private static byte[] CreateBlankJpeg(int width, int height)
    {
        try
        {
            using var image = new Image<Rgb24>(width, height, new Rgb24(255, 255, 255));
            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms);
            return ms.ToArray();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }
}
