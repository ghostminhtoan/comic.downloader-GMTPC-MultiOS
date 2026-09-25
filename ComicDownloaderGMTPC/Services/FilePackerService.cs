using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace ComicDownloaderGMTPC.Services;

public class FilePackerOptions
{
    public bool CreateZip { get; set; } = false;
    public bool CreateCbz { get; set; } = true;
    public bool CreatePdf { get; set; } = false;
    public bool PackSubfoldersIndividually { get; set; } = true; // Mỗi chapter / folder con 1 file
}

public class FilePackerService
{
    private static readonly HashSet<string> ValidImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".jfif"
    };

    public Action<string, string>? LogEmitted { get; set; }

    /// <summary>
    /// Sắp xếp tên file theo thứ tự tự nhiên của con người (1, 2, ..., 9, 10 thay vì 1, 10, 2).
    /// </summary>
    public static List<string> NaturalSort(IEnumerable<string> files)
    {
        return files.OrderBy(f => Regex.Replace(Path.GetFileName(f), @"\d+", m => m.Value.PadLeft(10, '0')), StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Tiến trình đóng gói thư mục theo cấu hình đã chọn kèm báo cáo tiến trình mượt mà theo từng ảnh.
    /// </summary>
    public async Task<(int successCount, int errorCount)> ProcessPackingAsync(
        string inputFolder,
        string outputFolder,
        FilePackerOptions options,
        IProgress<(int current, int total, string currentFile)>? progress,
        CancellationToken ct)
    {
        int success = 0;
        int errors = 0;

        if (!Directory.Exists(inputFolder))
        {
            LogEmitted?.Invoke("ERROR", $"Thư mục nguồn không tồn tại: {inputFolder}");
            return (0, 1);
        }

        if (!Directory.Exists(outputFolder))
        {
            Directory.CreateDirectory(outputFolder);
        }

        // Xác định danh sách các thư mục cần đóng gói
        List<string> targetsToPack = new();
        if (options.PackSubfoldersIndividually)
        {
            var subs = Directory.GetDirectories(inputFolder, "*", SearchOption.TopDirectoryOnly);
            if (subs.Length > 0)
            {
                targetsToPack.AddRange(subs);
            }
            else
            {
                // Không có thư mục con, đóng gói chính thư mục này
                targetsToPack.Add(inputFolder);
            }
        }
        else
        {
            targetsToPack.Add(inputFolder);
        }

        // Quét trước để tính tổng số đơn vị công việc (ảnh x số định dạng chọn)
        int formatCount = (options.CreateZip ? 1 : 0) + (options.CreateCbz ? 1 : 0) + (options.CreatePdf ? 1 : 0);
        if (formatCount == 0) formatCount = 1;

        var targetWorkList = new List<(string dir, string dirName, List<string> images)>();
        int totalUnits = 0;

        foreach (var targetDir in targetsToPack)
        {
            string dirName = Path.GetFileName(targetDir);
            if (string.IsNullOrEmpty(dirName)) dirName = Path.GetFileName(Path.GetDirectoryName(targetDir) ?? "Archive");

            var imgFiles = NaturalSort(
                Directory.GetFiles(targetDir, "*.*", SearchOption.AllDirectories)
                    .Where(f => ValidImageExtensions.Contains(Path.GetExtension(f)))
            );

            if (imgFiles.Count > 0)
            {
                targetWorkList.Add((targetDir, dirName, imgFiles));
                totalUnits += imgFiles.Count * formatCount;
            }
            else
            {
                LogEmitted?.Invoke("WARN", $"Bỏ qua {dirName}: Không tìm thấy file ảnh hợp lệ.");
            }
        }

        if (totalUnits == 0)
        {
            progress?.Report((0, 100, "Không có file ảnh nào để đóng gói."));
            return (0, 0);
        }

        int currentUnit = 0;
        progress?.Report((0, totalUnits, $"Bắt đầu đóng gói {targetWorkList.Count} thư mục ({totalUnits} lượt tệp)..."));

        int totalFolders = targetWorkList.Count;
        int folderIndex = 0;

        foreach (var (targetDir, dirName, imgFiles) in targetWorkList)
        {
            if (ct.IsCancellationRequested) break;
            folderIndex++;
            LogEmitted?.Invoke("INFO", $"[{folderIndex}/{totalFolders}] Đang xử lý '{dirName}' ({imgFiles.Count} ảnh)...");

            bool targetSuccess = true;

            try
            {
                // 1. Đóng gói CBZ (Comic Book Zip)
                if (options.CreateCbz)
                {
                    string cbzPath = Path.Combine(outputFolder, $"{dirName}.cbz");
                    await Task.Run(() => CreateArchiveFromImages(imgFiles, cbzPath, targetDir, "CBZ", totalUnits, ref currentUnit, progress, ct), ct);
                    LogEmitted?.Invoke("SUCCESS", $"Đã tạo CBZ: {Path.GetFileName(cbzPath)} ({imgFiles.Count} ảnh)");
                }

                // 2. Đóng gói ZIP
                if (options.CreateZip)
                {
                    string zipPath = Path.Combine(outputFolder, $"{dirName}.zip");
                    await Task.Run(() => CreateArchiveFromImages(imgFiles, zipPath, targetDir, "ZIP", totalUnits, ref currentUnit, progress, ct), ct);
                    LogEmitted?.Invoke("SUCCESS", $"Đã tạo ZIP: {Path.GetFileName(zipPath)} ({imgFiles.Count} ảnh)");
                }

                // 3. Đóng gói PDF
                if (options.CreatePdf)
                {
                    string pdfPath = Path.Combine(outputFolder, $"{dirName}.pdf");
                    await Task.Run(() => CreatePdfFromImages(imgFiles, pdfPath, totalUnits, ref currentUnit, progress, ct), ct);
                    LogEmitted?.Invoke("SUCCESS", $"Đã tạo PDF: {Path.GetFileName(pdfPath)} ({imgFiles.Count} trang)");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                targetSuccess = false;
                LogEmitted?.Invoke("ERROR", $"Lỗi khi đóng gói {dirName}: {ex.Message}");
            }

            if (targetSuccess) success++;
            else errors++;
        }

        return (success, errors);
    }

    /// <summary>
    /// Nén danh sách ảnh vào file Zip / Cbz kèm báo cáo tiến trình chi tiết từng file.
    /// </summary>
    private static void CreateArchiveFromImages(
        List<string> images,
        string archivePath,
        string baseDir,
        string formatName,
        int totalUnits,
        ref int currentUnit,
        IProgress<(int current, int total, string currentFile)>? progress,
        CancellationToken ct)
    {
        if (File.Exists(archivePath)) File.Delete(archivePath);

        using var zipToOpen = new FileStream(archivePath, FileMode.Create);
        using var archive = new ZipArchive(zipToOpen, ZipArchiveMode.Create);

        foreach (var img in images)
        {
            if (ct.IsCancellationRequested) return;
            string relPath = Path.GetRelativePath(baseDir, img);
            archive.CreateEntryFromFile(img, relPath, CompressionLevel.Optimal);

            int cur = Interlocked.Increment(ref currentUnit);
            progress?.Report((cur, totalUnits, $"[{formatName}] {Path.GetFileName(img)} ({cur}/{totalUnits})"));
        }
    }

    /// <summary>
    /// Tạo tài liệu PDF đa trang từ danh sách ảnh sử dụng SkiaSharp thuần túy kèm báo cáo tiến trình.
    /// </summary>
    private static void CreatePdfFromImages(
        List<string> images,
        string pdfPath,
        int totalUnits,
        ref int currentUnit,
        IProgress<(int current, int total, string currentFile)>? progress,
        CancellationToken ct)
    {
        if (File.Exists(pdfPath)) File.Delete(pdfPath);

        using var outputStream = new FileStream(pdfPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var document = SKDocument.CreatePdf(outputStream);
        if (document == null)
        {
            throw new InvalidOperationException("Không thể khởi tạo SKDocument PDF trên hệ điều hành này.");
        }

        foreach (var imgPath in images)
        {
            if (ct.IsCancellationRequested) return;

            byte[] bytes = File.ReadAllBytes(imgPath);
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap != null)
            {
                using var pageCanvas = document.BeginPage(bitmap.Width, bitmap.Height);
                pageCanvas.DrawBitmap(bitmap, 0, 0);
                document.EndPage();
            }

            int cur = Interlocked.Increment(ref currentUnit);
            progress?.Report((cur, totalUnits, $"[PDF] Trang {Path.GetFileName(imgPath)} ({cur}/{totalUnits})"));
        }

        document.Close();
    }
}
