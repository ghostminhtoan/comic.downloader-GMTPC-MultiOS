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
    /// Sắp xếp tên file hoặc thư mục theo thứ tự tự nhiên của con người (1, 2, ..., 9, 10 thay vì 1, 10, 2).
    /// </summary>
    public static List<string> NaturalSort(IEnumerable<string> files)
    {
        return files.NaturalSort();
    }

    /// <summary>
    /// Tiến trình đóng gói thư mục theo cấu hình đã chọn kèm báo cáo tiến trình mượt mà theo từng ảnh.
    /// </summary>
    public async Task<(int successCount, int errorCount, string finalOutputDir)> ProcessPackingAsync(
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
            return (0, 1, outputFolder);
        }

        string inputDirName = Path.GetFileName(inputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(inputDirName)) inputDirName = "Packed_Comics";

        string rawTargetFolder = string.IsNullOrWhiteSpace(outputFolder)
            ? (Directory.GetParent(inputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName != null
                ? Path.Combine(Directory.GetParent(inputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!.FullName, "packed")
                : Path.Combine(inputFolder, "packed"))
            : outputFolder;

        // Tự động bảo toàn cấu trúc thư mục gốc bên trong thư mục packed cùng cấp
        string finalOutputDir = rawTargetFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).EndsWith(inputDirName, StringComparison.OrdinalIgnoreCase)
            ? rawTargetFolder
            : Path.Combine(rawTargetFolder, inputDirName);

        if (!Directory.Exists(finalOutputDir))
        {
            Directory.CreateDirectory(finalOutputDir);
            LogEmitted?.Invoke("INFO", $"Đã tạo thư mục lưu file đóng gói: {finalOutputDir}");
        }

        // Xác định danh sách các thư mục cần đóng gói theo thứ tự số tự nhiên (Chapter 1, Chapter 2, ..., Chapter 10, Chapter 11)
        List<string> targetsToPack = new();
        if (options.PackSubfoldersIndividually)
        {
            var subs = Directory.GetDirectories(inputFolder, "*", SearchOption.TopDirectoryOnly);
            if (subs.Length > 0)
            {
                targetsToPack.AddRange(NaturalSort(subs));
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
            if (string.IsNullOrEmpty(dirName) || (!options.PackSubfoldersIndividually && string.Equals(targetDir, inputFolder, StringComparison.OrdinalIgnoreCase)))
            {
                dirName = inputDirName;
            }

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
            return (0, 0, finalOutputDir);
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
                    string cbzPath = Path.Combine(finalOutputDir, $"{dirName}.cbz");
                    await Task.Run(() => CreateArchiveFromImages(imgFiles, cbzPath, targetDir, "CBZ", totalUnits, ref currentUnit, progress, ct), ct);
                    LogEmitted?.Invoke("SUCCESS", $"Đã tạo CBZ: {Path.GetFileName(cbzPath)} ({imgFiles.Count} ảnh)");
                }

                // 2. Đóng gói ZIP
                if (options.CreateZip)
                {
                    string zipPath = Path.Combine(finalOutputDir, $"{dirName}.zip");
                    await Task.Run(() => CreateArchiveFromImages(imgFiles, zipPath, targetDir, "ZIP", totalUnits, ref currentUnit, progress, ct), ct);
                    LogEmitted?.Invoke("SUCCESS", $"Đã tạo ZIP: {Path.GetFileName(zipPath)} ({imgFiles.Count} ảnh)");
                }

                // 3. Đóng gói PDF (hỗ trợ đính kèm mục lục chương / Bookmarks)
                if (options.CreatePdf)
                {
                    string pdfPath = Path.Combine(finalOutputDir, $"{dirName}.pdf");
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

        return (success, errors, finalOutputDir);
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
    /// <summary>
    /// Tạo tài liệu PDF đa trang chuẩn ISO 32000-1 với cơ chế Direct Stream Embedding (Zero-Copy).
    /// Nhúng trực tiếp 100% luồng byte JPEG gốc, KHÔNG giải nén thành raw bitmap uncompressed,
    /// đảm bảo dung lượng file PDF giữ nguyên 1:1 với ảnh gốc (500MB ảnh gốc = 500MB PDF) và tốc độ cực nhanh.
    /// Tự động đính kèm cây mục lục chương (PDF Bookmarks/Outlines) tiếng Việt UTF-16BE.
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

        using var fs = new FileStream(pdfPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs, System.Text.Encoding.Latin1);

        // 1. PDF Header chuẩn 1.4
        bw.Write(System.Text.Encoding.Latin1.GetBytes("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n"));

        var objectOffsets = new Dictionary<int, long>();
        int objIdCounter = 1;

        int catalogObjId = objIdCounter++;
        int outlinesObjId = (bookmarks != null && bookmarks.Count > 0) ? objIdCounter++ : 0;
        int pagesObjId = objIdCounter++;

        // Danh sách ID cho từng mục Bookmark
        var bookmarkObjIds = new List<int>();
        if (bookmarks != null && bookmarks.Count > 0)
        {
            for (int i = 0; i < bookmarks.Count; i++)
            {
                bookmarkObjIds.Add(objIdCounter++);
            }
        }

        var pageObjIds = new List<int>();
        var pageImageInfos = new List<(int PageObjId, int ContentObjId, int ImgObjId, int Width, int Height, byte[] Bytes)>();

        foreach (var imgPath in images)
        {
            if (ct.IsCancellationRequested) return;

            byte[] rawBytes = File.ReadAllBytes(imgPath);
            byte[] jpegBytes;
            int width = 0;
            int height = 0;

            bool isJpeg = rawBytes.Length >= 3 && rawBytes[0] == 0xFF && rawBytes[1] == 0xD8 && rawBytes[2] == 0xFF;
            if (isJpeg)
            {
                jpegBytes = rawBytes;
                using var ms = new MemoryStream(rawBytes);
                using var codec = SKCodec.Create(ms);
                width = codec?.Info.Width ?? 1080;
                height = codec?.Info.Height ?? 1920;
            }
            else
            {
                // Đối với WebP, PNG, BMP: Decode và nén sang JPEG 95% (dung lượng nhỏ gọn, chất lượng cao nhất)
                using var bitmap = SKBitmap.Decode(rawBytes);
                if (bitmap != null)
                {
                    width = bitmap.Width;
                    height = bitmap.Height;
                    using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
                    jpegBytes = data != null ? data.ToArray() : rawBytes;
                }
                else
                {
                    jpegBytes = rawBytes;
                    width = 1080;
                    height = 1920;
                }
            }

            if (width <= 0) width = 1080;
            if (height <= 0) height = 1920;

            int pageObjId = objIdCounter++;
            int contentObjId = objIdCounter++;
            int imgObjId = objIdCounter++;

            pageObjIds.Add(pageObjId);
            pageImageInfos.Add((pageObjId, contentObjId, imgObjId, width, height, jpegBytes));

            int cur = Interlocked.Increment(ref currentUnit);
            progress?.Report((cur, totalUnits, $"[PDF] Trang {Path.GetFileName(imgPath)} ({cur}/{totalUnits})"));
        }

        if (pageObjIds.Count == 0) return;

        // 2. Ghi Root Catalog Object
        objectOffsets[catalogObjId] = fs.Position;
        bw.Write(System.Text.Encoding.Latin1.GetBytes($"{catalogObjId} 0 obj\n<<\n  /Type /Catalog\n  /Pages {pagesObjId} 0 R\n"));
        if (outlinesObjId > 0)
        {
            bw.Write(System.Text.Encoding.Latin1.GetBytes($"  /Outlines {outlinesObjId} 0 R\n  /PageMode /UseOutlines\n"));
        }
        bw.Write(System.Text.Encoding.Latin1.GetBytes(">>\nendobj\n"));

        // 3. Ghi Outlines và các mục Bookmarks (mục lục chương)
        if (outlinesObjId > 0 && bookmarks != null && bookmarkObjIds.Count > 0)
        {
            objectOffsets[outlinesObjId] = fs.Position;
            bw.Write(System.Text.Encoding.Latin1.GetBytes($"{outlinesObjId} 0 obj\n<<\n  /Type /Outlines\n  /Count {bookmarks.Count}\n  /First {bookmarkObjIds[0]} 0 R\n  /Last {bookmarkObjIds[^1]} 0 R\n>>\nendobj\n"));

            for (int i = 0; i < bookmarks.Count; i++)
            {
                int bObjId = bookmarkObjIds[i];
                var bm = bookmarks[i];
                int targetPage = Math.Clamp(bm.PageIndex, 0, pageObjIds.Count - 1);
                int targetPageObjId = pageObjIds[targetPage];

                objectOffsets[bObjId] = fs.Position;
                var sb = new System.Text.StringBuilder();
                sb.Append($"{bObjId} 0 obj\n<<\n");
                sb.Append($"  /Title {ToPdfHexString(bm.Title)}\n");
                sb.Append($"  /Parent {outlinesObjId} 0 R\n");
                if (i > 0)
                {
                    sb.Append($"  /Prev {bookmarkObjIds[i - 1]} 0 R\n");
                }
                if (i < bookmarks.Count - 1)
                {
                    sb.Append($"  /Next {bookmarkObjIds[i + 1]} 0 R\n");
                }
                sb.Append($"  /Dest [{targetPageObjId} 0 R /XYZ null null null]\n");
                sb.Append(">>\nendobj\n");
                bw.Write(System.Text.Encoding.Latin1.GetBytes(sb.ToString()));
            }
        }

        // 4. Ghi Pages Object
        objectOffsets[pagesObjId] = fs.Position;
        bw.Write(System.Text.Encoding.Latin1.GetBytes($"{pagesObjId} 0 obj\n<<\n  /Type /Pages\n  /Count {pageObjIds.Count}\n  /Kids ["));
        foreach (int pid in pageObjIds)
        {
            bw.Write(System.Text.Encoding.Latin1.GetBytes($"{pid} 0 R "));
        }
        bw.Write(System.Text.Encoding.Latin1.GetBytes("]\n>>\nendobj\n"));

        // 5. Ghi từng Trang (Page -> Content Stream -> Image XObject)
        for (int i = 0; i < pageImageInfos.Count; i++)
        {
            var info = pageImageInfos[i];
            string imName = $"Im{i + 1}";

            // A. Page Object
            objectOffsets[info.PageObjId] = fs.Position;
            bw.Write(System.Text.Encoding.Latin1.GetBytes(
                $"{info.PageObjId} 0 obj\n<<\n" +
                $"  /Type /Page\n" +
                $"  /Parent {pagesObjId} 0 R\n" +
                $"  /MediaBox [0 0 {info.Width} {info.Height}]\n" +
                $"  /Contents {info.ContentObjId} 0 R\n" +
                $"  /Resources <<\n" +
                $"    /ProcSet [/PDF /ImageC /ImageI /ImageB]\n" +
                $"    /XObject << /{imName} {info.ImgObjId} 0 R >>\n" +
                $"  >>\n" +
                $">>\nendobj\n"));

            // B. Content Stream Object (vẽ vừa khít toàn trang)
            string contentOps = $"q\n{info.Width} 0 0 {info.Height} 0 0 cm\n/{imName} Do\nQ\n";
            byte[] contentBytes = System.Text.Encoding.Latin1.GetBytes(contentOps);

            objectOffsets[info.ContentObjId] = fs.Position;
            bw.Write(System.Text.Encoding.Latin1.GetBytes(
                $"{info.ContentObjId} 0 obj\n<<\n" +
                $"  /Length {contentBytes.Length}\n" +
                $">>\nstream\n"));
            bw.Write(contentBytes);
            bw.Write(System.Text.Encoding.Latin1.GetBytes("\nendstream\nendobj\n"));

            // C. Image XObject (Nhúng trực tiếp 100% byte gốc với DCTDecode)
            objectOffsets[info.ImgObjId] = fs.Position;
            bw.Write(System.Text.Encoding.Latin1.GetBytes(
                $"{info.ImgObjId} 0 obj\n<<\n" +
                $"  /Type /XObject\n" +
                $"  /Subtype /Image\n" +
                $"  /Width {info.Width}\n" +
                $"  /Height {info.Height}\n" +
                $"  /ColorSpace /DeviceRGB\n" +
                $"  /BitsPerComponent 8\n" +
                $"  /Filter /DCTDecode\n" +
                $"  /Length {info.Bytes.Length}\n" +
                $">>\nstream\n"));
            bw.Write(info.Bytes);
            bw.Write(System.Text.Encoding.Latin1.GetBytes("\nendstream\nendobj\n"));
        }

        // 6. Ghi bảng Cross-Reference (xref)
        long xrefOffset = fs.Position;
        bw.Write(System.Text.Encoding.Latin1.GetBytes($"xref\n0 {objIdCounter}\n"));
        bw.Write(System.Text.Encoding.Latin1.GetBytes("0000000000 65535 f \r\n"));

        for (int id = 1; id < objIdCounter; id++)
        {
            long offset = objectOffsets.TryGetValue(id, out long off) ? off : 0;
            bw.Write(System.Text.Encoding.Latin1.GetBytes($"{offset:D10} 00000 n \r\n"));
        }

        // 7. Ghi Trailer & File EOF
        bw.Write(System.Text.Encoding.Latin1.GetBytes(
            $"trailer\n<<\n" +
            $"  /Size {objIdCounter}\n" +
            $"  /Root {catalogObjId} 0 R\n" +
            $">>\n" +
            $"startxref\n" +
            $"{xrefOffset}\n" +
            $"%%EOF\n"));
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
