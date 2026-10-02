using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Dịch vụ xử lý cắt ảnh dài (Long Images / Webtoon / Manhwa Strips) đa nền tảng.
/// Hỗ trợ cả 2 dạng:
/// 1. Tự động cắt khi tải (Auto Split Long Images during download).
/// 2. Cắt thủ công hàng loạt theo thư mục do người dùng chọn (Manual Folder Batch Split).
/// Chạy 100% mượt mà trên Windows, Linux và Android bằng SkiaSharp.
/// </summary>
public class ImageSplitterService
{
    private static readonly Lazy<ImageSplitterService> _instance = new(() => new ImageSplitterService());
    public static ImageSplitterService Instance => _instance.Value;

    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp"
    };

    /// <summary>
    /// Thử cắt một file ảnh nếu chiều cao vượt quá ngưỡng chỉ định (mặc định 5000px).
    /// </summary>
    public static bool TrySplitImageFile(string filePath, int splitHeight = 5000, int quality = 90)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || splitHeight < 100)
        {
            return false;
        }

        try
        {
            using var srcBitmap = UniversalImageDecoder.DecodeToSkBitmap(filePath);
            if (srcBitmap == null || srcBitmap.Height <= splitHeight)
            {
                return false;
            }

            // Cố định splitHeight theo bội số 16px (16-pixel MCU block alignment)
            // Triệt tiêu 100% hiện tượng lệch khối 16x16 làm sinh ra đường lằn ngang/vạch đen ở mép cắt JPEG/WebP
            int alignedSplitHeight = Math.Max(16, (splitHeight / 16) * 16);

            int width = srcBitmap.Width;
            int height = srcBitmap.Height;
            int count = (int)Math.Ceiling((double)height / alignedSplitHeight);
            if (count <= 1) return false;

            string? dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir)) return false;

            string nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            var format = SKEncodedImageFormat.Jpeg;
            if (ext == ".png") format = SKEncodedImageFormat.Png;
            else if (ext == ".webp") format = SKEncodedImageFormat.Webp;

            var createdFiles = new List<string>();

            try
            {
                for (int i = 0; i < count; i++)
                {
                    int y = i * alignedSplitHeight;
                    int h = Math.Min(alignedSplitHeight, height - y);

                    // Sử dụng SKBitmap mới độc lập với bộ nhớ pixel liên tục 100% thay vì ExtractSubset
                    // Đảm bảo không bị xê dịch stride/rowbytes hay anti-alias bleed gây lằn ngang mép ảnh
                    using var subset = new SKBitmap(new SKImageInfo(width, h, srcBitmap.ColorType, srcBitmap.AlphaType));
                    using (var canvas = new SKCanvas(subset))
                    {
                        using var paint = new SKPaint { IsAntialias = false };
                        canvas.DrawBitmap(srcBitmap, new SKRect(0, y, width, y + h), new SKRect(0, 0, width, h), paint);
                    }

                    string outPath = Path.Combine(dir, $"{nameWithoutExt}-split-{i + 1}{ext}");
                    using (var fsOut = File.Create(outPath))
                    {
                        subset.Encode(fsOut, format, Math.Clamp(quality, 85, 100));
                    }
                    createdFiles.Add(outPath);
                }

                // Xóa file ảnh gốc dài sau khi đã cắt và lưu toàn bộ các phần con thành công
                try
                {
                    File.Delete(filePath);
                }
                catch
                {
                    // Bỏ qua nếu file đang bị khóa tạm thời
                }

                return true;
            }
            catch
            {
                // Nếu xảy ra lỗi giữa chừng, dọn dẹp các mảnh cắt dang dở để tránh rác
                foreach (var partialFile in createdFiles)
                {
                    try { if (File.Exists(partialFile)) File.Delete(partialFile); } catch { }
                }
                throw;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Thông tin tiến trình quét và cắt ảnh thư mục.
    /// </summary>
    public class SplitProgressInfo
    {
        public int TotalFiles { get; set; }
        public int ProcessedFiles { get; set; }
        public int SplitCount { get; set; }
        public int ErrorCount { get; set; }
        public string CurrentFile { get; set; } = string.Empty;
        public string? LogMessage { get; set; }
    }

    public class SplitSummary
    {
        public int TotalFiles { get; set; }
        public int ProcessedFiles { get; set; }
        public int SplitCount { get; set; }
        public int ErrorCount { get; set; }
        public bool IsCancelled { get; set; }
    }

    /// <summary>
    /// Xử lý quét toàn bộ thư mục và tự động cắt các ảnh dài vượt chiều cao quy định.
    /// </summary>
    public async Task<SplitSummary> ProcessFolderAsync(
        string folderPath,
        int splitHeight = 5000,
        int quality = 90,
        int threads = 4,
        IProgress<SplitProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var summary = new SplitSummary();
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return summary;
            }

            var allFiles = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(f => SupportedImageExtensions.Contains(Path.GetExtension(f)))
                // Bỏ qua những file đã cắt trước đó (đã có hậu tố -split-)
                .Where(f => !Path.GetFileNameWithoutExtension(f).Contains("-split-"))
                .NaturalSort();

            summary.TotalFiles = allFiles.Count;
            if (summary.TotalFiles == 0)
            {
                progress?.Report(new SplitProgressInfo
                {
                    TotalFiles = 0,
                    ProcessedFiles = 0,
                    LogMessage = "Thư mục không có file ảnh hợp lệ (.jpg, .jpeg, .png, .webp, .bmp)."
                });
                return summary;
            }

            int processed = 0;
            int splitCount = 0;
            int errorCount = 0;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, threads),
                CancellationToken = ct
            };

            try
            {
                Parallel.ForEach(allFiles, parallelOptions, (file, state) =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        state.Stop();
                        return;
                    }

                    string logMsg = string.Empty;
                    try
                    {
                        bool wasSplit = TrySplitImageFile(file, splitHeight, quality);
                        if (wasSplit)
                        {
                            Interlocked.Increment(ref splitCount);
                            logMsg = $"[ĐÃ CẮT] {Path.GetFileName(file)}";
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errorCount);
                        logMsg = $"[LỖI] {Path.GetFileName(file)}: {ex.Message}";
                    }

                    int curProcessed = Interlocked.Increment(ref processed);

                    progress?.Report(new SplitProgressInfo
                    {
                        TotalFiles = summary.TotalFiles,
                        ProcessedFiles = curProcessed,
                        SplitCount = splitCount,
                        ErrorCount = errorCount,
                        CurrentFile = Path.GetFileName(file),
                        LogMessage = !string.IsNullOrEmpty(logMsg) ? logMsg : null
                    });
                });
            }
            catch (OperationCanceledException)
            {
                summary.IsCancelled = true;
            }

            summary.ProcessedFiles = processed;
            summary.SplitCount = splitCount;
            summary.ErrorCount = errorCount;
            return summary;
        }, ct).ConfigureAwait(false);
    }
}
