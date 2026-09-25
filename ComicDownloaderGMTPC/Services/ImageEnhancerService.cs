using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    private static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

    /// <summary>
    /// Áp dụng các bộ lọc Contrast, Brightness, Saturation, Sharpness và Noise Reduction lên SKBitmap.
    /// </summary>
    public SKBitmap ProcessBitmap(SKBitmap src, ImageEnhancerOptions options)
    {
        // 1. Tính toán ma trận màu kết hợp (Combined Color Matrix: Contrast * Saturation + Brightness)
        // Contrast: -100..100 -> hệ số c (c = 1.0 khi Contrast = 0)
        float c = options.Contrast >= 0 ? 1.0f + (options.Contrast / 50.0f) : (100.0f + options.Contrast) / 100.0f;
        // Brightness: -100..100 -> độ dịch b chuẩn hóa [-1.0 .. +1.0] cho Skia (b = 0 khi Brightness = 0)
        float b = options.Brightness / 100.0f;
        // Saturation: 0..200 -> hệ số s [0.0 .. 3.0] (s = 1.0 khi Saturation = 100)
        float s = Math.Clamp(options.Saturation / 100.0f, 0.0f, 3.0f);

        // Rec.709 Luma weights
        const float rWeight = 0.2126f;
        const float gWeight = 0.7152f;
        const float bWeight = 0.0722f;

        float sr = (1.0f - s) * rWeight;
        float sg = (1.0f - s) * gWeight;
        float sb = (1.0f - s) * bWeight;

        // Điểm xoay tương phản chuẩn hóa trong không gian Skia [0.0 .. 1.0] là 0.5 (tương ứng với 128 trong [0..255])
        float t = (1.0f - c) * 0.5f + b;

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
                gain: 1.0f,
                bias: 0.0f,
                kernelOffset: new SKPointI(1, 1),
                tileMode: SKShaderTileMode.Clamp,
                convolveAlpha: false,
                input: imageFilter);

            imageFilter = sharpenFilter;
        }

        var dst = new SKBitmap(src.Width, src.Height, src.ColorType, src.AlphaType);
        using (var canvas = new SKCanvas(dst))
        {
            using var paint = new SKPaint
            {
                ColorFilter = colorFilter,
                ImageFilter = imageFilter
            };
            canvas.DrawBitmap(src, 0, 0, paint);
        }

        imageFilter?.Dispose();
        return dst;
    }

    /// <summary>
    /// Xử lý ảnh mẫu để tạo luồng byte xem trước (Live Preview).
    /// </summary>
    public byte[]? GeneratePreviewBytes(string filePath, ImageEnhancerOptions options, int maxDimension = 900)
    {
        try
        {
            if (!File.Exists(filePath)) return null;

            using var src = SKBitmap.Decode(filePath);
            if (src == null) return null;

            // Thu nhỏ nếu ảnh quá lớn để xem trước mượt mà
            SKBitmap workingBitmap = src;
            bool isResized = false;

            if (src.Width > maxDimension || src.Height > maxDimension)
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

            using var enhanced = ProcessBitmap(workingBitmap, options);
            if (isResized) workingBitmap.Dispose();

            using var ms = new MemoryStream();
            enhanced.Encode(ms, SKEncodedImageFormat.Jpeg, 85);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            LogEmitted?.Invoke("WARN", $"Lỗi tạo xem trước ảnh: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Xử lý ảnh mẫu và trích xuất đầy đủ thông số kích thước, dung lượng trước/sau và chỉ số ánh sáng (FastStone style).
    /// </summary>
    public PreviewStatsResult? GeneratePreviewWithStats(string filePath, ImageEnhancerOptions options, int maxDimension = 900)
    {
        try
        {
            if (!File.Exists(filePath)) return null;

            var fileInfo = new FileInfo(filePath);
            long originalSize = fileInfo.Length;

            using var src = SKBitmap.Decode(filePath);
            if (src == null) return null;

            int origW = src.Width;
            int origH = src.Height;

            SKBitmap workingBitmap = src;
            bool isResized = false;

            if (src.Width > maxDimension || src.Height > maxDimension)
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

            using var enhanced = ProcessBitmap(workingBitmap, options);
            if (isResized) workingBitmap.Dispose();

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            var format = ext switch
            {
                ".png" => SKEncodedImageFormat.Png,
                ".webp" => SKEncodedImageFormat.Webp,
                _ => SKEncodedImageFormat.Jpeg
            };

            using var ms = new MemoryStream();
            enhanced.Encode(ms, format, Math.Clamp(options.Quality, 10, 100));
            byte[] bytes = ms.ToArray();

            // Ước tính dung lượng nén cho toàn bộ kích thước gốc
            long previewEncodedSize = bytes.Length;
            long estimatedBytes = isResized
                ? (long)(previewEncodedSize * ((double)(origW * origH) / (workingBitmap.Width * workingBitmap.Height)))
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
    /// Lấy danh sách toàn bộ file ảnh hợp lệ trong cây thư mục phục vụ duyệt Next/Previous.
    /// </summary>
    public List<string> GetAllImagesInFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return new List<string>();
        try
        {
            return Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Tìm kiếm đệ quy ảnh đầu tiên trong cây thư mục để làm mẫu xem trước.
    /// </summary>
    public string? FindFirstSampleImage(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return null;
        try
        {
            // Ưu tiên chọn ảnh có dung lượng hợp lệ (> 10KB) để tránh các file tạm / rỗng
            return Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .FirstOrDefault(f =>
                {
                    try { return new FileInfo(f).Length > 10000; }
                    catch { return true; }
                })
                ?? Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .FirstOrDefault(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Xử lý hàng loạt toàn bộ ảnh trong thư mục đa tầng (hỗ trợ phân định Input & Output folder).
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

        // Quét đệ quy toàn bộ thư mục con đa tầng
        var files = Directory.GetFiles(inputFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0)
        {
            LogEmitted?.Invoke("WARN", $"Không tìm thấy ảnh hợp lệ trong cây thư mục: {inputFolder}");
            return (0, 0);
        }

        string targetFolder = string.IsNullOrWhiteSpace(outputFolder) ? Path.Combine(inputFolder, "Enhanced") : outputFolder;
        if (!options.OverwriteOriginal && !Directory.Exists(targetFolder))
        {
            Directory.CreateDirectory(targetFolder);
            LogEmitted?.Invoke("INFO", $"Đã tạo thư mục lưu ảnh nâng cao: {targetFolder}");
        }

        string modeText = options.OverwriteOriginal ? "Ghi đè file gốc" : $"Lưu vào: {targetFolder}";
        LogEmitted?.Invoke("INFO", $"Bắt đầu xử lý {files.Count} ảnh đa tầng ({modeText}) [Độ sáng: {options.Brightness}, Tương phản: {options.Contrast}%, Bão hòa: {options.Saturation}%, Nét: {options.Sharpness}, Khử nhiễu: {options.NoiseReduce}]...");

        int total = files.Count;
        int completed = 0;
        int success = 0;
        int errors = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.MaxThreads),
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(files, parallelOptions, async (filePath, token) =>
        {
            token.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(filePath);
            string relPath = Path.GetRelativePath(inputFolder, filePath);
            string destPath = options.OverwriteOriginal
                ? filePath + ".tmp_enh"
                : Path.Combine(targetFolder, relPath);

            try
            {
                string? destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                await Task.Run(() =>
                {
                    using var src = SKBitmap.Decode(filePath);
                    if (src == null) throw new InvalidOperationException("Không thể giải mã dữ liệu ảnh.");

                    using var enhanced = ProcessBitmap(src, options);

                    string ext = Path.GetExtension(filePath).ToLowerInvariant();
                    var format = ext switch
                    {
                        ".png" => SKEncodedImageFormat.Png,
                        ".webp" => SKEncodedImageFormat.Webp,
                        _ => SKEncodedImageFormat.Jpeg
                    };

                    using var fs = File.OpenWrite(destPath);
                    enhanced.Encode(fs, format, Math.Clamp(options.Quality, 10, 100));
                }, token);

                if (options.OverwriteOriginal)
                {
                    File.Move(destPath, filePath, true);
                }

                Interlocked.Increment(ref success);
                LogEmitted?.Invoke("SUCCESS", $"[Xong] {relPath}");
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
                ProgressUpdated?.Invoke(percent, relPath);
            }
        });

        LogEmitted?.Invoke("INFO", $"Hoàn tất nâng cao ảnh! Thành công: {success}/{total}, Lỗi: {errors}");
        return (success, errors);
    }
}
