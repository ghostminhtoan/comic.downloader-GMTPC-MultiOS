using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;

namespace ComicDownloaderGMTPC.Services;

public class ImageEnhancerOptions
{
    public float Contrast { get; set; } = 0f;       // -100 to +100
    public float Brightness { get; set; } = 0f;     // -100 to +100
    public float Saturation { get; set; } = 100f;   // 0 to 200
    public float Sharpness { get; set; } = 0f;      // 0 to 10
    public int NoiseReduce { get; set; } = 0;       // 0 to 5
    public int Quality { get; set; } = 90;          // 10 to 100
    public bool OverwriteOriginal { get; set; } = false;
    public int MaxThreads { get; set; } = 4;
    public string OutputFormat { get; set; } = "original"; // "original", "jpg", "gif", "webp"
}

public class PreviewStatsResult
{
    public byte[]? PreviewBytes { get; set; }
    public int OrigWidth { get; set; }
    public int OrigHeight { get; set; }
    public long OriginalSizeBytes { get; set; }
    public long ProcessedSizeBytes { get; set; }
    public double SizeDeltaPercent { get; set; }
    public string ClippingInfo { get; set; } = string.Empty;
}

public class ImageEnhancerService
{
    public event Action<string, string>? LogEmitted;
    public event Action<double, string>? ProgressUpdated;

    private static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };

    /// <summary>
    /// Lấy đuôi mở rộng file đích dựa trên định dạng người dùng lựa chọn.
    /// </summary>
    public static string GetTargetExtension(string sourceFilePath, string? outputFormat)
    {
        string srcExt = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        if (string.IsNullOrEmpty(srcExt)) srcExt = ".jpg";

        return (outputFormat ?? "original").ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => ".jpg",
            "gif" => ".gif",
            "webp" => ".webp",
            "png" => ".png",
            "bmp" => ".bmp",
            _ => srcExt // "original" giữ nguyên extension gốc
        };
    }

    /// <summary>
    /// Áp dụng các bộ lọc Contrast, Brightness, Saturation, Sharpness và Noise Reduction lên SKBitmap.
    /// Chuẩn hóa 100% không gian màu RGBA8888, triệt tiêu hoàn toàn lỗi đảo kênh màu Red/Blue và lệch tâm xoay tương phản.
    /// </summary>
    public SKBitmap ProcessBitmap(SKBitmap src, ImageEnhancerOptions options)
    {
        // 0. Đảm bảo nguồn bitmap luôn chuẩn hóa định dạng RGBA8888 để ma trận màu xử lý đúng thứ tự kênh
        SKBitmap workingSrc = src;
        bool isTempSrc = false;
        if (src.ColorType != SKColorType.Rgba8888)
        {
            var rgba = new SKBitmap(new SKImageInfo(src.Width, src.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var tempCanvas = new SKCanvas(rgba))
            {
                tempCanvas.DrawBitmap(src, 0, 0);
            }
            workingSrc = rgba;
            isTempSrc = true;
        }

        // 1. Tính toán ma trận màu kết hợp (Combined Color Matrix: Contrast * Saturation + Brightness)
        // Contrast [-100..100] -> hệ số c (c = 1.0 khi Contrast = 0)
        float c = options.Contrast >= 0 ? 1.0f + (options.Contrast / 50.0f) : (100.0f + options.Contrast) / 100.0f;
        // Brightness [-100..100] -> độ dịch sáng kênh màu byte [-255..+255] (b = 0 khi Brightness = 0)
        float b = options.Brightness * 2.55f;
        // Saturation [0..200] -> hệ số bão hòa s [0.0..3.0] (s = 1.0 khi Saturation = 100)
        float s = Math.Clamp(options.Saturation / 100.0f, 0.0f, 3.0f);

        // Chuẩn Rec.709 Luma weights
        const float rWeight = 0.2126f;
        const float gWeight = 0.7152f;
        const float bWeight = 0.0722f;

        float sr = (1.0f - s) * rWeight;
        float sg = (1.0f - s) * gWeight;
        float sb = (1.0f - s) * bWeight;

        // Điểm xoay tương phản (Pivot) chuẩn hóa quanh 128 (xám trung tính):
        // R' = c * (R - 128) + 128 + b = c * R + (128 * (1 - c) + b)
        float t = 128.0f * (1.0f - c) + b;

        float[] colorMatrix = new float[]
        {
            c * (sr + s), c * sg,       c * sb,       0, t,
            c * sr,       c * (sg + s), c * sb,       0, t,
            c * sr,       c * sg,       c * (sb + s), 0, t,
            0,            0,            0,            1, 0
        };

        using var colorFilter = SKColorFilter.CreateColorMatrix(colorMatrix);

        // 2. Xây dựng ImageFilter chain: Noise reduction -> Sharpening
        SKImageFilter? imageFilter = null;

        if (options.NoiseReduce > 0)
        {
            float sigma = options.NoiseReduce * 0.4f;
            imageFilter = SKImageFilter.CreateBlur(sigma, sigma);
        }

        if (options.Sharpness > 0)
        {
            float k = options.Sharpness * 0.35f;
            float center = 1.0f + 4.0f * k;
            float edge = -k;

            float[] sharpenKernel = new float[]
            {
                 0,    edge,   0,
                edge, center, edge,
                 0,    edge,   0
            };

            var sharpenFilter = SKImageFilter.CreateMatrixConvolution(
                new SKSizeI(3, 3),
                sharpenKernel,
                1.0f,
                0.0f,
                new SKPointI(1, 1),
                SKShaderTileMode.Clamp,
                false);

            imageFilter = imageFilter != null
                ? SKImageFilter.CreateCompose(imageFilter, sharpenFilter)
                : sharpenFilter;
        }

        // 3. Render bitmap mới với Paint phối hợp bộ lọc màu + bộ lọc ảnh trên không gian RGBA8888
        var dst = new SKBitmap(new SKImageInfo(workingSrc.Width, workingSrc.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(dst))
        {
            using var paint = new SKPaint
            {
                ColorFilter = colorFilter,
                ImageFilter = imageFilter,
                IsAntialias = true
            };

            canvas.DrawBitmap(workingSrc, 0, 0, paint);
        }

        imageFilter?.Dispose();
        if (isTempSrc)
        {
            workingSrc.Dispose();
        }

        return dst;
    }

    /// <summary>
    /// Xử lý và lưu một file ảnh cụ thể (Tự động nhận diện Ảnh Động Animated GIF/WebP vs Ảnh Tĩnh).
    /// </summary>
    public void EnhanceAndSaveImage(string inputPath, string outputPath, ImageEnhancerOptions options)
    {
        string srcExt = Path.GetExtension(inputPath).ToLowerInvariant();
        string targetExt = GetTargetExtension(inputPath, options.OutputFormat);

        bool isAnimated = false;
        if (srcExt == ".gif" || srcExt == ".webp")
        {
            try
            {
                using var codec = SKCodec.Create(inputPath);
                if (codec != null && codec.FrameCount > 1)
                {
                    isAnimated = true;
                }
            }
            catch { }
        }

        if (isAnimated)
        {
            // Xử lý chuỗi khung hình cho ảnh động (Animated GIF / Animated WebP)
            using var animatedImage = Image.Load<Rgba32>(inputPath);
            byte[] pixelBuffer = Array.Empty<byte>();

            for (int f = 0; f < animatedImage.Frames.Count; f++)
            {
                var frame = animatedImage.Frames[f];
                int w = frame.Width;
                int h = frame.Height;
                int requiredBytes = w * h * 4;
                if (pixelBuffer.Length != requiredBytes)
                {
                    pixelBuffer = new byte[requiredBytes];
                }
                frame.CopyPixelDataTo(pixelBuffer);

                using var skBmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
                Marshal.Copy(pixelBuffer, 0, skBmp.GetPixels(), requiredBytes);

                using var processedBmp = ProcessBitmap(skBmp, options);
                Marshal.Copy(processedBmp.GetPixels(), pixelBuffer, 0, requiredBytes);

                frame.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        var rowSpan = accessor.GetRowSpan(y);
                        var sourceSpan = MemoryMarshal.Cast<byte, Rgba32>(pixelBuffer.AsSpan(y * accessor.Width * 4, accessor.Width * 4));
                        sourceSpan.CopyTo(rowSpan);
                    }
                });
            }

            using var fs = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            if (targetExt == ".webp")
            {
                animatedImage.SaveAsWebp(fs, new WebpEncoder
                {
                    Quality = Math.Clamp(options.Quality, 10, 100),
                    FileFormat = WebpFileFormatType.Lossy
                });
            }
            else if (targetExt == ".gif")
            {
                animatedImage.SaveAsGif(fs, new GifEncoder
                {
                    ColorTableMode = GifColorTableMode.Global
                });
            }
            else if (targetExt == ".jpg" || targetExt == ".jpeg")
            {
                animatedImage.Frames.CloneFrame(0).SaveAsJpeg(fs, new JpegEncoder
                {
                    Quality = Math.Clamp(options.Quality, 10, 100)
                });
            }
            else
            {
                animatedImage.Frames.CloneFrame(0).SaveAsPng(fs);
            }
        }
        else
        {
            // Xử lý ảnh tĩnh thông thường với SkiaSharp siêu tốc
            using var src = UniversalImageDecoder.DecodeToSkBitmap(inputPath);
            if (src == null) throw new InvalidOperationException("Không thể giải mã dữ liệu ảnh.");

            using var enhanced = ProcessBitmap(src, options);

            using var fs = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            if (targetExt == ".gif")
            {
                using var ms = new MemoryStream();
                using (var img = SKImage.FromBitmap(enhanced))
                using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
                {
                    data.SaveTo(ms);
                }
                ms.Position = 0;
                using var imageSharpImg = Image.Load(ms);
                imageSharpImg.SaveAsGif(fs);
            }
            else
            {
                var format = targetExt switch
                {
                    ".png" => SKEncodedImageFormat.Png,
                    ".webp" => SKEncodedImageFormat.Webp,
                    ".bmp" => SKEncodedImageFormat.Bmp,
                    _ => SKEncodedImageFormat.Jpeg
                };
                enhanced.Encode(fs, format, Math.Clamp(options.Quality, 10, 100));
            }
        }
    }

    /// <summary>
    /// Xử lý ảnh xem trước và trả về thống kê chi tiết (Dung lượng Before/After, tỉ lệ % nén, cảnh báo cháy sáng/chết tối).
    /// </summary>
    public PreviewStatsResult? GeneratePreviewStats(string filePath, ImageEnhancerOptions options, int maxDimension = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

        try
        {
            long originalSize = new FileInfo(filePath).Length;

            using var src = UniversalImageDecoder.DecodeToSkBitmap(filePath);
            if (src == null) return null;

            int origW = src.Width;
            int origH = src.Height;

            SKBitmap workingBitmap = src;
            bool isResized = false;

            if (maxDimension > 0 && (src.Width > maxDimension || src.Height > maxDimension))
            {
                float scale = Math.Min((float)maxDimension / src.Width, (float)maxDimension / src.Height);
                int w = Math.Max(1, (int)(src.Width * scale));
                int h = Math.Max(1, (int)(src.Height * scale));

                var resized = src.Resize(new SKImageInfo(w, h, src.ColorType, src.AlphaType), SKSamplingOptions.Default);
                if (resized != null)
                {
                    workingBitmap = resized;
                    isResized = true;
                }
            }

            int workW = workingBitmap.Width;
            int workH = workingBitmap.Height;

            using var enhanced = ProcessBitmap(workingBitmap, options);
            if (isResized)
            {
                workingBitmap.Dispose();
            }

            string targetExt = GetTargetExtension(filePath, options.OutputFormat);
            var format = targetExt switch
            {
                ".png" => SKEncodedImageFormat.Png,
                ".webp" => SKEncodedImageFormat.Webp,
                ".bmp" => SKEncodedImageFormat.Bmp,
                _ => SKEncodedImageFormat.Jpeg
            };

            using var ms = new MemoryStream();
            enhanced.Encode(ms, format, Math.Clamp(options.Quality, 10, 100));
            byte[] bytes = ms.ToArray();

            // Ước tính dung lượng nén cho toàn bộ kích thước gốc
            long previewEncodedSize = bytes.Length;
            long estimatedBytes = isResized && workW > 0 && workH > 0
                ? (long)(previewEncodedSize * ((double)(origW * origH) / (workW * workH)))
                : previewEncodedSize;

            double delta = originalSize > 0
                ? ((double)(estimatedBytes - originalSize) / originalSize) * 100.0
                : 0.0;

            // Phân tích vùng cháy sáng (highlight) và chết tối (shadow)
            int shadows = 0, highlights = 0;
            int stepX = Math.Max(1, enhanced.Width / 40);
            int stepY = Math.Max(1, enhanced.Height / 40);
            int sampleCount = 0;

            for (int y = 0; y < enhanced.Height; y += stepY)
            {
                for (int x = 0; x < enhanced.Width; x += stepX)
                {
                    var color = enhanced.GetPixel(x, y);
                    float luma = 0.2126f * color.Red + 0.7152f * color.Green + 0.0722f * color.Blue;
                    if (luma < 5f) shadows++;
                    else if (luma > 250f) highlights++;
                    sampleCount++;
                }
            }

            float shadowPct = sampleCount > 0 ? (float)shadows / sampleCount * 100f : 0f;
            float highlightPct = sampleCount > 0 ? (float)highlights / sampleCount * 100f : 0f;

            string clippingText;
            if (highlightPct > 12f) clippingText = $"⚠️ Cháy sáng ({highlightPct:F1}%)";
            else if (shadowPct > 15f) clippingText = $"⚠️ Chết tối ({shadowPct:F1}%)";
            else clippingText = "✅ Cân bằng tốt";

            return new PreviewStatsResult
            {
                PreviewBytes = bytes,
                OrigWidth = origW,
                OrigHeight = origH,
                OriginalSizeBytes = originalSize,
                ProcessedSizeBytes = estimatedBytes,
                SizeDeltaPercent = delta,
                ClippingInfo = clippingText
            };
        }
        catch (Exception ex)
        {
            LogEmitted?.Invoke("WARN", $"Lỗi tạo xem trước ảnh: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Lấy danh sách toàn bộ file ảnh hợp lệ trong cây thư mục phục vụ duyệt Next/Previous theo thứ tự tự nhiên (1, 2, ..., 10, 11).
    /// </summary>
    public List<string> GetAllImagesInFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return new List<string>();
        try
        {
            return Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .NaturalSort();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Tìm kiếm đệ quy ảnh đầu tiên trong cây thư mục để làm mẫu xem trước theo thứ tự tự nhiên (1, 2, ..., 10).
    /// </summary>
    public string? FindFirstSampleImage(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return null;
        try
        {
            var validImages = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .NaturalSort();

            // Ưu tiên chọn ảnh có dung lượng hợp lệ (> 10KB) để tránh các file tạm / rỗng
            return validImages.FirstOrDefault(f =>
            {
                try { return new FileInfo(f).Length > 10000; }
                catch { return true; }
            }) ?? validImages.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Xử lý hàng loạt toàn bộ ảnh trong thư mục đa tầng (hỗ trợ phân định Input & Output folder) theo đúng thứ tự số tự nhiên.
    /// </summary>
    public async Task<(int successCount, int errorCount)> ProcessFolderAsync(
        string inputFolder,
        string? outputFolder,
        ImageEnhancerOptions options,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inputFolder) || !Directory.Exists(inputFolder))
        {
            LogEmitted?.Invoke("ERROR", $"Thư mục nguồn không tồn tại: {inputFolder}");
            return (0, 0);
        }

        // Quét đệ quy toàn bộ thư mục con đa tầng theo thứ tự tự nhiên của số (1, 2, ..., 9, 10, 11)
        var files = Directory.GetFiles(inputFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .NaturalSort();

        if (files.Count == 0)
        {
            LogEmitted?.Invoke("WARN", $"Không tìm thấy ảnh hợp lệ trong cây thư mục: {inputFolder}");
            return (0, 0);
        }

        string inputDirName = Path.GetFileName(inputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(inputDirName)) inputDirName = "Enhanced_Images";

        string rawTargetFolder = string.IsNullOrWhiteSpace(outputFolder)
            ? (Directory.GetParent(inputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName != null
                ? Path.Combine(Directory.GetParent(inputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!.FullName, "enhanced")
                : Path.Combine(inputFolder, "enhanced"))
            : outputFolder;

        // Nếu thư mục đích chưa kết thúc bằng tên thư mục gốc, tự động lồng tên thư mục gốc vào trong để bảo toàn 100% cấu trúc
        string baseTargetFolder = options.OverwriteOriginal
            ? inputFolder
            : (rawTargetFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).EndsWith(inputDirName, StringComparison.OrdinalIgnoreCase)
                ? rawTargetFolder
                : Path.Combine(rawTargetFolder, inputDirName));

        if (!options.OverwriteOriginal && !Directory.Exists(baseTargetFolder))
        {
            Directory.CreateDirectory(baseTargetFolder);
            LogEmitted?.Invoke("INFO", $"Đã tạo thư mục lưu ảnh nâng cao: {baseTargetFolder}");
        }

        string formatLabel = (options.OutputFormat ?? "ORIGINAL").ToUpperInvariant();
        string modeText = options.OverwriteOriginal ? "Ghi đè file gốc" : $"Lưu vào: {baseTargetFolder}";
        LogEmitted?.Invoke("INFO", $"Bắt đầu xử lý {files.Count} ảnh đa tầng ({modeText}) [Định dạng: {formatLabel}, Độ sáng: {options.Brightness}, Tương phản: {options.Contrast}%, Bão hòa: {options.Saturation}%, Nét: {options.Sharpness}, Khử nhiễu: {options.NoiseReduce}]...");

        int total = files.Count;
        int completed = 0;
        int success = 0;
        int errors = 0;

        // Giới hạn số luồng xử lý đồ họa trên thiết bị di động để tránh tràn native memory OOM killer của Android, trên Windows/Linux tôn trọng tối đa số luồng cấu hình
        int effectiveThreads = OperatingSystem.IsAndroid()
            ? Math.Clamp(options.MaxThreads, 1, 4)
            : Math.Max(1, options.MaxThreads);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = effectiveThreads,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(files, parallelOptions, (filePath, token) =>
        {
            token.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(filePath);
            string srcExt = Path.GetExtension(filePath).ToLowerInvariant();
            string targetExt = GetTargetExtension(filePath, options.OutputFormat);
            string relPath = Path.GetRelativePath(inputFolder, filePath);
            string targetRelPath = Path.ChangeExtension(relPath, targetExt);

            string destPath = options.OverwriteOriginal
                ? Path.ChangeExtension(filePath, targetExt) + ".tmp_enh"
                : Path.Combine(baseTargetFolder, targetRelPath);

            try
            {
                string? destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                EnhanceAndSaveImage(filePath, destPath, options);

                if (options.OverwriteOriginal)
                {
                    string finalTargetPath = Path.ChangeExtension(filePath, targetExt);
                    if (!string.Equals(finalTargetPath, filePath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(filePath)) File.Delete(filePath);
                    }
                    File.Move(destPath, finalTargetPath, true);
                }

                Interlocked.Increment(ref success);
                LogEmitted?.Invoke("SUCCESS", $"[Xong] {targetRelPath}");
            }
            catch (OperationCanceledException)
            {
                if (File.Exists(destPath)) try { File.Delete(destPath); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                if (File.Exists(destPath)) try { File.Delete(destPath); } catch { }
                Interlocked.Increment(ref errors);
                LogEmitted?.Invoke("ERROR", $"[Lỗi] {relPath}: {ex.Message}");
            }
            finally
            {
                int current = Interlocked.Increment(ref completed);
                double percent = (double)current / total * 100.0;
                ProgressUpdated?.Invoke(percent, targetRelPath);

                // Dọn dẹp bộ nhớ định kỳ trên Android để đảm bảo chạy mượt khi tắt màn hình/chuyển ứng dụng
                if (OperatingSystem.IsAndroid() && current % 20 == 0)
                {
                    GC.Collect(2, GCCollectionMode.Optimized);
                }
            }

            return ValueTask.CompletedTask;
        });

        LogEmitted?.Invoke("INFO", $"Hoàn tất nâng cao ảnh! Thành công: {success}/{total}, Lỗi: {errors}");
        return (success, errors);
    }
}
