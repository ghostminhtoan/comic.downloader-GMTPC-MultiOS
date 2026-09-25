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

        var targetWorkList = new List<(string dir, string dirName, List<string> images, List<(string Title, int PageIndex)> bookmarks)>();
        int totalUnits = 0;

        foreach (var targetDir in targetsToPack)
        {
            string dirName = Path.GetFileName(targetDir);
            if (string.IsNullOrEmpty(dirName)) dirName = Path.GetFileName(Path.GetDirectoryName(targetDir) ?? "Archive");

            var (imgFiles, chapterBookmarks) = CollectImagesAndBookmarks(targetDir);

            if (imgFiles.Count > 0)
            {
                targetWorkList.Add((targetDir, dirName, imgFiles, chapterBookmarks));
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

        foreach (var (targetDir, dirName, imgFiles, chapterBookmarks) in targetWorkList)
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

                // 3. Đóng gói PDF (hỗ trợ đính kèm mục lục chương / Bookmarks)
                if (options.CreatePdf)
                {
                    string pdfPath = Path.Combine(outputFolder, $"{dirName}.pdf");
                    await Task.Run(() => CreatePdfFromImages(imgFiles, pdfPath, chapterBookmarks, totalUnits, ref currentUnit, progress, ct), ct);
                    if (chapterBookmarks.Count > 0)
                    {
                        LogEmitted?.Invoke("SUCCESS", $"Đã tạo PDF: {Path.GetFileName(pdfPath)} ({imgFiles.Count} trang, {chapterBookmarks.Count} chương)");
                    }
                    else
                    {
                        LogEmitted?.Invoke("SUCCESS", $"Đã tạo PDF: {Path.GetFileName(pdfPath)} ({imgFiles.Count} trang)");
                    }
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
    /// Thu thập toàn bộ danh sách file ảnh và tự động nhận diện danh mục chương (Bookmarks)
    /// dựa trên các thư mục con theo thứ tự tự nhiên (Natural Sort).
    /// </summary>
    private static (List<string> images, List<(string Title, int PageIndex)> bookmarks) CollectImagesAndBookmarks(string targetDir)
    {
        var allImages = new List<string>();
        var bookmarks = new List<(string Title, int PageIndex)>();

        var subDirs = Directory.GetDirectories(targetDir, "*", SearchOption.TopDirectoryOnly);
        if (subDirs.Length > 0)
        {
            var sortedSubDirs = NaturalSort(subDirs);

            // 1. Kiểm tra ảnh ở thư mục gốc (nếu có: bìa, cover, info)
            var rootImages = NaturalSort(
                Directory.GetFiles(targetDir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => ValidImageExtensions.Contains(Path.GetExtension(f)))
            );
            if (rootImages.Count > 0)
            {
                bookmarks.Add(("Bìa / Giới thiệu", 0));
                allImages.AddRange(rootImages);
            }

            // 2. Thu thập ảnh theo từng thư mục con (Chapter)
            foreach (var sub in sortedSubDirs)
            {
                var subImages = NaturalSort(
                    Directory.GetFiles(sub, "*.*", SearchOption.AllDirectories)
                        .Where(f => ValidImageExtensions.Contains(Path.GetExtension(f)))
                );

                if (subImages.Count > 0)
                {
                    string chapterName = Path.GetFileName(sub);
                    bookmarks.Add((chapterName, allImages.Count));
                    allImages.AddRange(subImages);
                }
            }
        }
        else
        {
            // Không có thư mục con, quét toàn bộ ảnh
            allImages = NaturalSort(
                Directory.GetFiles(targetDir, "*.*", SearchOption.AllDirectories)
                    .Where(f => ValidImageExtensions.Contains(Path.GetExtension(f)))
            );
        }

        return (allImages, bookmarks);
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
    /// Tạo tài liệu PDF đa trang từ danh sách ảnh sử dụng SkiaSharp thuần túy kèm báo cáo tiến trình
    /// và tự động chèn mục lục chương (PDF Bookmarks/Outlines) nếu có.
    /// </summary>
    private static void CreatePdfFromImages(
        List<string> images,
        string pdfPath,
        List<(string Title, int PageIndex)> bookmarks,
        int totalUnits,
        ref int currentUnit,
        IProgress<(int current, int total, string currentFile)>? progress,
        CancellationToken ct)
    {
        if (File.Exists(pdfPath)) File.Delete(pdfPath);

        using (var outputStream = new FileStream(pdfPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var document = SKDocument.CreatePdf(outputStream))
        {
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

        // Đính kèm Bookmarks (Outlines) vào file PDF nếu có danh sách chương
        if (bookmarks != null && bookmarks.Count > 0 && File.Exists(pdfPath))
        {
            try
            {
                InjectPdfBookmarks(pdfPath, bookmarks);
            }
            catch (Exception ex)
            {
                // Bookmark là tính năng phụ trợ trải nghiệm, nếu gặp lỗi file PDF ảnh vẫn nguyên vẹn
                System.Diagnostics.Debug.WriteLine($"[PDF Bookmarks] Không thể chèn bookmarks: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Chèn cấu trúc mục lục chương (PDF Bookmarks / Outlines) theo chuẩn PDF Specification ISO 32000-1 (Incremental Update).
    /// Hỗ trợ đầy đủ Unicode tiếng Việt (UTF-16BE Hex String) và tự động mở bảng mục lục (/PageMode /UseOutlines).
    /// </summary>
    public static void InjectPdfBookmarks(string pdfPath, List<(string Title, int PageIndex)> bookmarks)
    {
        if (bookmarks == null || bookmarks.Count == 0 || !File.Exists(pdfPath)) return;

        byte[] pdfBytes = File.ReadAllBytes(pdfPath);
        string pdfText = System.Text.Encoding.Latin1.GetString(pdfBytes);

        // 1. Tìm tất cả Page Objects theo thứ tự trang: "X 0 obj << ... /Type /Page ..."
        var pageObjMatches = Regex.Matches(pdfText, @"(\d+)\s+0\s+obj\s*<<[^>]*?/Type\s*/Page\b", RegexOptions.Singleline);
        if (pageObjMatches.Count == 0) return;

        var pageObjIds = new List<int>();
        foreach (Match m in pageObjMatches)
        {
            if (int.TryParse(m.Groups[1].Value, out int id))
            {
                pageObjIds.Add(id);
            }
        }
        if (pageObjIds.Count == 0) return;

        // 2. Tìm Root Catalog Object ID
        int rootObjId = 0;
        var trailerMatch = Regex.Match(pdfText, @"trailer\s*<<.*?/Root\s+(\d+)\s+0\s+R.*?>>", RegexOptions.Singleline);
        if (trailerMatch.Success)
        {
            int.TryParse(trailerMatch.Groups[1].Value, out rootObjId);
        }
        else
        {
            var catalogMatch = Regex.Match(pdfText, @"(\d+)\s+0\s+obj\s*<<[^>]*?/Type\s*/Catalog\b", RegexOptions.Singleline);
            if (catalogMatch.Success)
            {
                int.TryParse(catalogMatch.Groups[1].Value, out rootObjId);
            }
        }
        if (rootObjId == 0) return;

        // 3. Tìm Max Object ID hiện có
        var allObjMatches = Regex.Matches(pdfText, @"(\d+)\s+0\s+obj\b");
        int maxObjId = 0;
        foreach (Match m in allObjMatches)
        {
            if (int.TryParse(m.Groups[1].Value, out int id) && id > maxObjId)
            {
                maxObjId = id;
            }
        }

        // 4. Chuẩn bị IDs cho các object mục lục mới:
        // - Outlines dictionary: maxObjId + 1
        // - Các mục bookmark: maxObjId + 2 -> maxObjId + 1 + bookmarks.Count
        int outlinesObjId = maxObjId + 1;
        int firstItemObjId = maxObjId + 2;
        int lastItemObjId = maxObjId + 1 + bookmarks.Count;
        int nextObjId = lastItemObjId + 1;

        // Tìm nội dung Catalog gốc để giữ lại các thuộc tính như /Pages, /Type
        string rootObjPattern = $@"{rootObjId}\s+0\s+obj\s*<<(.*?)>>\s*endobj";
        var rootMatch = Regex.Match(pdfText, rootObjPattern, RegexOptions.Singleline);
        string rootContent = rootMatch.Success ? rootMatch.Groups[1].Value : "";

        // Loại bỏ /Outlines hoặc /PageMode cũ nếu có trong root
        rootContent = Regex.Replace(rootContent, @"/Outlines\s+\d+\s+0\s+R", "");
        rootContent = Regex.Replace(rootContent, @"/PageMode\s*/\w+", "").Trim();

        var updateSb = new System.Text.StringBuilder();
        var newObjectOffsets = new Dictionary<int, long>();
        long baseLength = pdfBytes.Length;

        // A. Cập nhật Root Catalog kèm /Outlines và /PageMode /UseOutlines
        long currentOffset = baseLength + System.Text.Encoding.Latin1.GetByteCount(updateSb.ToString());
        newObjectOffsets[rootObjId] = currentOffset;
        updateSb.Append($"{rootObjId} 0 obj\n<<\n");
        if (!string.IsNullOrEmpty(rootContent))
        {
            updateSb.Append($"  {rootContent}\n");
        }
        updateSb.Append($"  /Outlines {outlinesObjId} 0 R\n");
        updateSb.Append("  /PageMode /UseOutlines\n");
        updateSb.Append(">>\nendobj\n");

        // B. Tạo Outlines Dictionary
        currentOffset = baseLength + System.Text.Encoding.Latin1.GetByteCount(updateSb.ToString());
        newObjectOffsets[outlinesObjId] = currentOffset;
        updateSb.Append($"{outlinesObjId} 0 obj\n<<\n");
        updateSb.Append("  /Type /Outlines\n");
        updateSb.Append($"  /Count {bookmarks.Count}\n");
        updateSb.Append($"  /First {firstItemObjId} 0 R\n");
        updateSb.Append($"  /Last {lastItemObjId} 0 R\n");
        updateSb.Append(">>\nendobj\n");

        // C. Tạo từng Outline Item trỏ đến trang tương ứng
        for (int i = 0; i < bookmarks.Count; i++)
        {
            int itemObjId = firstItemObjId + i;
            var bm = bookmarks[i];
            int targetPage = Math.Clamp(bm.PageIndex, 0, pageObjIds.Count - 1);
            int targetPageObjId = pageObjIds[targetPage];

            currentOffset = baseLength + System.Text.Encoding.Latin1.GetByteCount(updateSb.ToString());
            newObjectOffsets[itemObjId] = currentOffset;

            updateSb.Append($"{itemObjId} 0 obj\n<<\n");
            updateSb.Append($"  /Title {ToPdfHexString(bm.Title)}\n");
            updateSb.Append($"  /Parent {outlinesObjId} 0 R\n");
            if (i > 0)
            {
                updateSb.Append($"  /Prev {itemObjId - 1} 0 R\n");
            }
            if (i < bookmarks.Count - 1)
            {
                updateSb.Append($"  /Next {itemObjId + 1} 0 R\n");
            }
            updateSb.Append($"  /Dest [{targetPageObjId} 0 R /XYZ null null null]\n");
            updateSb.Append(">>\nendobj\n");
        }

        // D. Bảng xref incremental (mỗi dòng đúng 20 bytes theo ISO 32000-1)
        long xrefOffset = baseLength + System.Text.Encoding.Latin1.GetByteCount(updateSb.ToString());
        updateSb.Append("xref\n");

        // Section 1: Root Catalog
        updateSb.Append($"{rootObjId} 1\n");
        updateSb.Append($"{newObjectOffsets[rootObjId]:D10} 00000 n \r\n");

        // Section 2: Outlines và các Outline Items
        int continuousCount = lastItemObjId - outlinesObjId + 1;
        updateSb.Append($"{outlinesObjId} {continuousCount}\n");
        for (int id = outlinesObjId; id <= lastItemObjId; id++)
        {
            updateSb.Append($"{newObjectOffsets[id]:D10} 00000 n \r\n");
        }

        // E. Trailer với /Prev chỉ tới startxref cũ
        var startXrefMatch = Regex.Matches(pdfText, @"startxref\s+(\d+)\s+%%EOF");
        long prevXref = 0;
        if (startXrefMatch.Count > 0)
        {
            long.TryParse(startXrefMatch[^1].Groups[1].Value, out prevXref);
        }

        int totalSize = Math.Max(nextObjId, rootObjId + 1);

        updateSb.Append("trailer\n<<\n");
        updateSb.Append($"  /Size {totalSize}\n");
        updateSb.Append($"  /Root {rootObjId} 0 R\n");
        if (prevXref > 0)
        {
            updateSb.Append($"  /Prev {prevXref}\n");
        }
        updateSb.Append(">>\n");
        updateSb.Append("startxref\n");
        updateSb.Append($"{xrefOffset}\n");
        updateSb.Append("%%EOF\n");

        // Ghi nối tiếp phần incremental update vào cuối file PDF
        using var fs = new FileStream(pdfPath, FileMode.Append, FileAccess.Write, FileShare.None);
        byte[] updateBytes = System.Text.Encoding.Latin1.GetBytes(updateSb.ToString());
        fs.Write(updateBytes, 0, updateBytes.Length);
    }

    /// <summary>
    /// Chuyển chuỗi Unicode tiếng Việt thành PDF Hexadecimal String chuẩn UTF-16BE kèm Byte Order Mark FEFF.
    /// </summary>
    private static string ToPdfHexString(string text)
    {
        if (string.IsNullOrEmpty(text)) return "<FEFF>";
        byte[] bytes = System.Text.Encoding.BigEndianUnicode.GetBytes(text);
        var sb = new System.Text.StringBuilder();
        sb.Append("<FEFF");
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("X2"));
        }
        sb.Append('>');
        return sb.ToString();
    }
}
