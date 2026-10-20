// ====================================================================================================
// FILE SUMMARY: ArchiveMergeService.cs
// ----------------------------------------------------------------------------------------------------
// VAI TRÒ & MỤC ĐÍCH:
//   Dịch vụ xử lý Gộp & Đóng Gói File Truyện Lẻ (Archive Merger Engine) sang các định dạng CBZ, ZIP,
//   PDF (kèm bookmarks phân cấp) và sách điện tử MOBI Kindle.
//
// CÁC TÍNH NĂNG & THÀNH PHẦN CHÍNH:
//   - Đọc và giải nén các định dạng đầu vào: PDF, ZIP, CBZ, RAR, CBR, MOBI.
//   - Tự động gom nhóm chapter theo mốc người dùng chỉ định (100, 200, 300, Tất cả hoặc Tùy chọn).
//   - Giữ nguyên cấu trúc thư mục chapter bên trong file nén để độc giả tiện theo dõi.
//   - Tạo tài liệu PDF chuyên nghiệp với SkiaSharp + PDF Outlines tương ứng từng chương truyện.
//   - Hỗ trợ tạo sách điện tử MOBI đa chương chuẩn Amazon Kindle.
//
// LƯU Ý KỸ THUẬT & QUY TẮC BẢO TRÌ:
//   - Luôn sử dụng bộ giải nén in-memory hoặc streaming khi có thể để tránh tạo file rác đầy ổ cứng.
//   - Tôn trọng CancellationToken trên từng vòng lặp ảnh và báo cáo tiến độ chi tiết qua IProgress.
// ====================================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ComicDownloaderGMTPC.Services;

public class ArchiveMergeOptions
{
    public string InputFolder { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = string.Empty;
    /// <summary>
    /// Số lượng chapter tối đa trên mỗi file gộp (ví dụ: 100, 200, 300...).
    /// Nếu đặt là 0 thì gộp toàn bộ vào một file duy nhất.
    /// </summary>
    public int ChapterChunkSize { get; set; } = 100;
    /// <summary>
    /// Định dạng xuất: ".cbz" hoặc ".zip"
    /// </summary>
    public string OutputFormat { get; set; } = ".cbz";
    public CompressionLevel Compression { get; set; } = CompressionLevel.Fastest;
}

public class ArchiveMergeSourceItem
{
    public string FullPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public double ParsedChapterNumber { get; set; }
    public int IntegerBaseNumber { get; set; }
    public string FormattedChapterLabel { get; set; } = string.Empty;
}

public class ArchiveMergeService
{
    private static readonly Lazy<ArchiveMergeService> _instance = new(() => new ArchiveMergeService());
    public static ArchiveMergeService Instance => _instance.Value;

    private static readonly HashSet<string> ValidImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".jfif", ".gif"
    };

    public Action<string, string>? LogEmitted { get; set; }

    /// <summary>
    /// Thực hiện quét và gộp các file lẻ (.pdf, .zip, .cbz, .rar, .cbr, .mobi) thành một hoặc nhiều file lớn.
    /// Bảo đảm 100% giữ nguyên từng folder chapter bên trong archive đích để dễ đọc.
    /// </summary>
    public async Task<(int fileCount, int errorCount, string finalOutputDir)> MergeArchivesAsync(
        ArchiveMergeOptions options,
        IProgress<(int current, int total, string message)>? progress,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(options.InputFolder) || !Directory.Exists(options.InputFolder))
            {
                LogEmitted?.Invoke("ERROR", $"Thư mục nguồn không tồn tại: {options.InputFolder}");
                return (0, 1, options.OutputFolder);
            }

            string inputDirName = Path.GetFileName(options.InputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(inputDirName)) inputDirName = "Merged_Comic";

            // Xác định thư mục đầu ra
            string targetFolder = string.IsNullOrWhiteSpace(options.OutputFolder)
                ? (Directory.GetParent(options.InputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName != null
                    ? Path.Combine(Directory.GetParent(options.InputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!.FullName, "merged")
                    : Path.Combine(options.InputFolder, "merged"))
                : options.OutputFolder;

            if (!Directory.Exists(targetFolder))
            {
                Directory.CreateDirectory(targetFolder);
                LogEmitted?.Invoke("INFO", $"Đã tạo thư mục lưu kết quả gộp: {targetFolder}");
            }

            // 1. Quét toàn bộ file và thư mục chapter con trong thư mục nguồn
            LogEmitted?.Invoke("INFO", $"Đang quét các file chương lẻ tại: {options.InputFolder}...");
            var sourceItems = CollectSourceItems(options.InputFolder);

            if (sourceItems.Count == 0)
            {
                LogEmitted?.Invoke("WARN", "Không tìm thấy file chương (.pdf, .zip, .cbz, .rar, .cbr, .mobi) hoặc folder chapter nào trong thư mục nguồn.");
                return (0, 0, targetFolder);
            }

            LogEmitted?.Invoke("INFO", $"Phát hiện {sourceItems.Count} chương/tệp cần gộp.");

            // 2. Chia nhóm (Chunking) theo mốc số chapter người dùng đã cấu hình
            var groups = GroupSourceItems(sourceItems, options.ChapterChunkSize);

            int totalGroups = groups.Count;
            int currentStep = 0;
            int totalSteps = sourceItems.Count;
            int successFiles = 0;
            int errorFiles = 0;

            progress?.Report((0, totalSteps, $"Chuẩn bị gộp {sourceItems.Count} chương thành {totalGroups} file archive..."));

            string ext = options.OutputFormat.StartsWith('.') ? options.OutputFormat : $".{options.OutputFormat}";
            if (!ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".cbz", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".mobi", StringComparison.OrdinalIgnoreCase))
            {
                ext = ".cbz";
            }

            for (int gIdx = 0; gIdx < groups.Count; gIdx++)
            {
                if (ct.IsCancellationRequested) break;

                var group = groups[gIdx];
                string outFileName = GenerateMergedFileName(inputDirName, group, options.ChapterChunkSize, gIdx == groups.Count - 1, ext);
                string outFilePath = Path.Combine(targetFolder, outFileName);

                LogEmitted?.Invoke("INFO", $"[{gIdx + 1}/{totalGroups}] Bắt đầu tạo file: {outFileName} ({group.Items.Count} chương)...");

                try
                {
                    if (File.Exists(outFilePath))
                    {
                        File.Delete(outFilePath);
                    }

                    if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        // Gộp thành tài liệu PDF kèm Bookmarks mục lục từng chapter
                        string tempDir = Path.Combine(Path.GetTempPath(), "comic_merge_pdf_" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tempDir);
                        try
                        {
                            var allImages = new List<string>();
                            var bookmarks = new List<(string Title, int PageIndex)>();

                            for (int i = 0; i < group.Items.Count; i++)
                            {
                                if (ct.IsCancellationRequested) break;
                                var item = group.Items[i];
                                string chapterLabel = MakeSafeFolderName(item);
                                LogEmitted?.Invoke("INFO", $"  -> Đang nạp chương [{item.FormattedChapterLabel}]: {item.Name}...");

                                int startPage = allImages.Count;
                                var chapterImages = ExtractChapterImagesToTemp(item, tempDir, ct);
                                if (chapterImages.Count > 0)
                                {
                                    allImages.AddRange(chapterImages);
                                    bookmarks.Add((chapterLabel, startPage));
                                }

                                currentStep++;
                                progress?.Report((currentStep, totalSteps, $"[{currentStep}/{totalSteps}] Đã nạp: {item.Name}"));
                            }

                            if (allImages.Count > 0 && !ct.IsCancellationRequested)
                            {
                                int dummyUnit = 0;
                                FilePackerService.CreatePdfFromImages(allImages, outFilePath, bookmarks, allImages.Count, ref dummyUnit, null, ct);
                                successFiles++;
                                LogEmitted?.Invoke("SUCCESS", $"Đã hoàn thành file PDF ({gIdx + 1}/{totalGroups}): {outFileName} ({allImages.Count} trang, {bookmarks.Count} chương)");
                            }
                        }
                        finally
                        {
                            try { Directory.Delete(tempDir, true); } catch { }
                        }
                    }
                    else if (ext.Equals(".mobi", StringComparison.OrdinalIgnoreCase))
                    {
                        // Gộp thành sách điện tử Kindle MOBI đa chương
                        string tempDir = Path.Combine(Path.GetTempPath(), "comic_merge_mobi_" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tempDir);
                        try
                        {
                            var chapterData = new List<(string ChapterName, List<string> Images)>();

                            for (int i = 0; i < group.Items.Count; i++)
                            {
                                if (ct.IsCancellationRequested) break;
                                var item = group.Items[i];
                                string chapterLabel = MakeSafeFolderName(item);
                                LogEmitted?.Invoke("INFO", $"  -> Đang nạp chương [{item.FormattedChapterLabel}]: {item.Name}...");

                                var chapterImages = ExtractChapterImagesToTemp(item, tempDir, ct);
                                if (chapterImages.Count > 0)
                                {
                                    chapterData.Add((chapterLabel, chapterImages));
                                }

                                currentStep++;
                                progress?.Report((currentStep, totalSteps, $"[{currentStep}/{totalSteps}] Đã nạp: {item.Name}"));
                            }

                            if (chapterData.Count > 0 && !ct.IsCancellationRequested)
                            {
                                string bookTitle = Path.GetFileNameWithoutExtension(outFileName);
                                MobiWriterService.CreateMobiFromImages(outFilePath, bookTitle, chapterData, null, ct);
                                successFiles++;
                                int totalImgs = chapterData.Sum(c => c.Images.Count);
                                LogEmitted?.Invoke("SUCCESS", $"Đã hoàn thành file MOBI ({gIdx + 1}/{totalGroups}): {outFileName} ({totalImgs} trang, {chapterData.Count} chương)");
                            }
                        }
                        finally
                        {
                            try { Directory.Delete(tempDir, true); } catch { }
                        }
                    }
                    else
                    {
                        // Gộp thành CBZ hoặc ZIP
                        using (var zipFs = new FileStream(outFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536))
                        using (var zipArchive = new ZipArchive(zipFs, ZipArchiveMode.Create))
                        {
                            for (int i = 0; i < group.Items.Count; i++)
                            {
                                if (ct.IsCancellationRequested) break;
                                var item = group.Items[i];

                                string chapterFolderName = MakeSafeFolderName(item);
                                LogEmitted?.Invoke("INFO", $"  -> Đang đóng gói chương [{item.FormattedChapterLabel}]: {item.Name}...");

                                // Trích xuất toàn bộ ảnh từ file chương nguồn đưa vào thư mục chapter bên trong zipArchive
                                ExtractAndAppendImages(item, chapterFolderName, zipArchive, options.Compression, ct);

                                currentStep++;
                                progress?.Report((currentStep, totalSteps, $"[{currentStep}/{totalSteps}] Đã gom: {item.Name}"));
                            }
                        }

                        successFiles++;
                        LogEmitted?.Invoke("SUCCESS", $"Đã hoàn thành file gộp ({gIdx + 1}/{totalGroups}): {outFileName}");
                    }
                }
                catch (Exception ex)
                {
                    errorFiles++;
                    LogEmitted?.Invoke("ERROR", $"Lỗi khi tạo file gộp {outFileName}: {ex.Message}");
                }
            }

            return (successFiles, errorFiles, targetFolder);
        }, ct);
    }

    /// <summary>
    /// Thu thập các file chapter lẻ (.pdf, .zip, .cbz, .rar, .cbr, .mobi) hoặc thư mục con chứa ảnh
    /// </summary>
    private List<ArchiveMergeSourceItem> CollectSourceItems(string inputFolder)
    {
        var list = new List<ArchiveMergeSourceItem>();

        try
        {
            var files = Directory.GetFiles(inputFolder, "*.*", SearchOption.TopDirectoryOnly);
            foreach (var f in files)
            {
                string extension = Path.GetExtension(f);
                bool isSupported = LibraryScannerService.IsArchiveFile(f) ||
                                   LibraryScannerService.IsPdfFile(f) ||
                                   LibraryScannerService.IsMobiFile(f);

                if (!isSupported) continue;

                string name = Path.GetFileName(f);
                ParseChapterInfo(name, out double num, out int baseInt, out string formatted);

                list.Add(new ArchiveMergeSourceItem
                {
                    FullPath = f,
                    Name = name,
                    IsDirectory = false,
                    ParsedChapterNumber = num,
                    IntegerBaseNumber = baseInt,
                    FormattedChapterLabel = formatted
                });
            }

            // Quét cả các folder chapter con (nếu có)
            var dirs = Directory.GetDirectories(inputFolder, "*", SearchOption.TopDirectoryOnly);
            foreach (var d in dirs)
            {
                string dirName = Path.GetFileName(d);
                if (dirName.Equals("merged", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals("packed", StringComparison.OrdinalIgnoreCase) ||
                    dirName.StartsWith('.'))
                {
                    continue;
                }

                ParseChapterInfo(dirName, out double num, out int baseInt, out string formatted);

                list.Add(new ArchiveMergeSourceItem
                {
                    FullPath = d,
                    Name = dirName,
                    IsDirectory = true,
                    ParsedChapterNumber = num,
                    IntegerBaseNumber = baseInt,
                    FormattedChapterLabel = formatted
                });
            }
        }
        catch { }

        // Sắp xếp thứ tự tự nhiên chuẩn xác theo số chapter hoặc NaturalSort
        list.Sort((a, b) =>
        {
            if (Math.Abs(a.ParsedChapterNumber - b.ParsedChapterNumber) > 0.0001)
            {
                return a.ParsedChapterNumber.CompareTo(b.ParsedChapterNumber);
            }
            return NaturalSortComparer.Instance.Compare(a.Name, b.Name);
        });

        return list;
    }

    /// <summary>
    /// Trích xuất số chương từ tên file/folder (nhận diện cả chap số nguyên và chap số thập phân như 100.1, 100.5)
    /// </summary>
    public static void ParseChapterInfo(string name, out double chapterNumber, out int baseInteger, out string formattedLabel)
    {
        string nameWithoutExt = Path.GetFileNameWithoutExtension(name);

        // Regex ưu tiên nhận diện từ khóa chapter: chap, chapter, c, chương, tập, ep, vol
        var match = Regex.Match(nameWithoutExt, @"(?:chap(?:ter)?|chương|tập|ep|c)\s*[:\-–]?\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            // Nhận diện dạng có ngoặc hoặc range: [100.5] hoặc #100
            match = Regex.Match(nameWithoutExt, @"[\[(]#?\s*([0-9]+(?:\.[0-9]+)?)\s*[\])]", RegexOptions.IgnoreCase);
        }

        if (!match.Success)
        {
            // Nhận diện bất kỳ cụm số nào
            var matches = Regex.Matches(nameWithoutExt, @"([0-9]+(?:\.[0-9]+)?)");
            if (matches.Count > 0)
            {
                // Lấy số cuối cùng hoặc số đầu tiên
                match = matches[^1];
            }
        }

        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
        {
            chapterNumber = parsed;
            baseInteger = (int)Math.Truncate(parsed);
            formattedLabel = Math.Abs(parsed % 1) < 0.0001 ? $"{baseInteger}" : parsed.ToString("0.###", CultureInfo.InvariantCulture);
            return;
        }

        chapterNumber = 999999;
        baseInteger = 999999;
        formattedLabel = nameWithoutExt;
    }

    public class ChapterMergeGroup
    {
        public int BucketIndex { get; set; }
        public double MinChapter { get; set; }
        public double MaxChapter { get; set; }
        public List<ArchiveMergeSourceItem> Items { get; set; } = new();
    }

    /// <summary>
    /// Gom nhóm các chương theo mốc số chapter người dùng đã cấu hình (100, 200, 300 hoặc 0/Tất cả).
    /// </summary>
    private List<ChapterMergeGroup> GroupSourceItems(List<ArchiveMergeSourceItem> items, int chunkSize)
    {
        var groups = new List<ChapterMergeGroup>();
        if (items.Count == 0) return groups;

        // Nếu người dùng chọn 0 hoặc nhỏ hơn: Gom tất cả vào 1 file duy nhất
        if (chunkSize <= 0)
        {
            var singleGroup = new ChapterMergeGroup
            {
                BucketIndex = 0,
                MinChapter = items.Min(x => x.ParsedChapterNumber),
                MaxChapter = items.Max(x => x.ParsedChapterNumber),
                Items = new List<ArchiveMergeSourceItem>(items)
            };
            groups.Add(singleGroup);
            return groups;
        }

        // Gom theo mốc số chapter:
        // Quy tắc quan trọng: Đừng tính theo tổng số chap vì sẽ có truyện có số thập phân gây lệch.
        // Phải là con số người dùng đã chọn. Trong trường hợp chap tròn chục bị ngoại lệ (98, 99, 100.1, 100.2, 100.5) đều gom vào mốc 100.
        // Tức là: Bucket 0: [0/1 -> 100]. Các chap có baseInteger <= 100 nằm ở Bucket 0.
        // Bucket 1: [101 -> 200]. Các chap có baseInteger từ 101 đến 200 nằm ở Bucket 1.
        // Bucket k: [k*chunkSize + 1 -> (k+1)*chunkSize].
        var bucketMap = new SortedDictionary<int, List<ArchiveMergeSourceItem>>();

        foreach (var item in items)
        {
            int bucket;
            if (item.IntegerBaseNumber <= 0)
            {
                bucket = 0;
            }
            else if (item.IntegerBaseNumber == 999999)
            {
                // File không có số chap: gom vào nhóm cuối cùng hoặc nhóm 0
                bucket = 0;
            }
            else
            {
                bucket = (item.IntegerBaseNumber - 1) / chunkSize;
            }

            if (!bucketMap.TryGetValue(bucket, out var bList))
            {
                bList = new List<ArchiveMergeSourceItem>();
                bucketMap[bucket] = bList;
            }
            bList.Add(item);
        }

        foreach (var kvp in bucketMap)
        {
            var g = new ChapterMergeGroup
            {
                BucketIndex = kvp.Key,
                MinChapter = kvp.Value.Min(x => x.ParsedChapterNumber),
                MaxChapter = kvp.Value.Max(x => x.ParsedChapterNumber),
                Items = kvp.Value
            };
            groups.Add(g);
        }

        return groups;
    }

    /// <summary>
    /// Tạo tên file gộp theo đúng quy chuẩn:
    /// Nếu chọn 100: [TenTruyen] [001-100].cbz, [TenTruyen] [101-200].cbz...
    /// Nhóm cuối cùng luôn biến thiên theo chap cuối thực tế: [TenTruyen] [201-351].cbz
    /// </summary>
    private string GenerateMergedFileName(string comicTitle, ChapterMergeGroup group, int chunkSize, bool isLastGroup, string extension)
    {
        string safeTitle = MakeCleanFileName(comicTitle);

        if (chunkSize <= 0)
        {
            if (group.MaxChapter < 999999 && group.MinChapter < 999999)
            {
                string minStr = FormatChapterBound(group.MinChapter);
                string maxStr = FormatChapterBound(group.MaxChapter);
                return $"{safeTitle} [Chap {minStr}-{maxStr}]{extension}";
            }
            return $"{safeTitle} [Full]{extension}";
        }

        int bucket = group.BucketIndex;
        int startBound = bucket == 0 ? (group.MinChapter <= 0 ? 0 : 1) : (bucket * chunkSize + 1);

        string startStr = startBound.ToString("D3");
        string endStr;

        if (isLastGroup)
        {
            // "folder cuối luôn luôn variable theo chap cuối"
            if (group.MaxChapter < 999999)
            {
                endStr = FormatChapterBound(group.MaxChapter);
            }
            else
            {
                endStr = ((bucket + 1) * chunkSize).ToString("D3");
            }
        }
        else
        {
            int standardEnd = (bucket + 1) * chunkSize;
            endStr = standardEnd.ToString("D3");
        }

        return $"{safeTitle} [Chap {startStr}-{endStr}]{extension}";
    }

    private static string FormatChapterBound(double val)
    {
        if (Math.Abs(val % 1) < 0.0001)
        {
            int intVal = (int)Math.Truncate(val);
            return intVal.ToString("D3");
        }
        return val.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Đặt tên an toàn cho folder con bên trong file nén (ví dụ "Chap 001", "Chap 016.1", "Chương 100")
    /// </summary>
    private string MakeSafeFolderName(ArchiveMergeSourceItem item)
    {
        string raw = Path.GetFileNameWithoutExtension(item.Name);
        raw = Regex.Replace(raw, @"[\\/:*?""<>|]", "_").Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = $"Chapter_{item.FormattedChapterLabel}";
        }
        return raw;
    }

    private static string MakeCleanFileName(string name)
    {
        string safe = Regex.Replace(name, @"[\\/:*?""<>|]", "_").Trim();
        return string.IsNullOrWhiteSpace(safe) ? "Comic" : safe;
    }

    /// <summary>
    /// Trích xuất toàn bộ ảnh từ tệp chương nguồn và ghi trực tiếp vào thư mục chapter bên trong ZipArchive đích.
    /// Hỗ trợ đầy đủ: .zip, .cbz, .rar, .cbr, .pdf, .mobi và folder chứa ảnh.
    /// </summary>
    private void ExtractAndAppendImages(
        ArchiveMergeSourceItem item,
        string chapterFolderName,
        ZipArchive destinationArchive,
        CompressionLevel compressionLevel,
        CancellationToken ct)
    {
        if (item.IsDirectory)
        {
            // Nguồn là thư mục con chứa ảnh
            if (!Directory.Exists(item.FullPath)) return;
            var imgFiles = NaturalSortComparer.Instance != null
                ? Directory.EnumerateFiles(item.FullPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => ValidImageExtensions.Contains(Path.GetExtension(f)))
                    .OrderBy(f => f, NaturalSortComparer.Instance)
                    .ToList()
                : Directory.EnumerateFiles(item.FullPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => ValidImageExtensions.Contains(Path.GetExtension(f)))
                    .ToList();

            foreach (var imgPath in imgFiles)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    string imgFileName = Path.GetFileName(imgPath);
                    string entryPath = $"{chapterFolderName}/{imgFileName}";

                    var entry = destinationArchive.CreateEntry(entryPath, compressionLevel);
                    using var es = entry.Open();
                    using var fs = new FileStream(imgPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                    fs.CopyTo(es);
                }
                catch { }
            }
            return;
        }

        string ext = Path.GetExtension(item.FullPath).ToLowerInvariant();

        // 1. Nguồn là PDF
        if (LibraryScannerService.IsPdfFile(item.FullPath))
        {
            var pdfMeta = PdfComicParser.Instance.InspectPdf(item.FullPath);
            int totalPages = pdfMeta.Pages.Count;
            for (int p = 0; p < totalPages; p++)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    byte[]? imgBytes = PdfComicParser.Instance.ExtractPageImageBytes(item.FullPath, p, out string imgExt);
                    if (imgBytes != null && imgBytes.Length > 0)
                    {
                        string entryPath = $"{chapterFolderName}/page_{p + 1:D4}{imgExt}";
                        var entry = destinationArchive.CreateEntry(entryPath, compressionLevel);
                        using var es = entry.Open();
                        es.Write(imgBytes, 0, imgBytes.Length);
                    }
                }
                catch { }
            }
            return;
        }

        // 2. Nguồn là MOBI / PRC / AZW
        if (LibraryScannerService.IsMobiFile(item.FullPath))
        {
            var meta = MobiComicParser.Instance.InspectMobi(item.FullPath);
            int totalPages = meta.Pages.Count;
            for (int p = 0; p < totalPages; p++)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    byte[]? imgBytes = MobiComicParser.Instance.ReadPageBytes(item.FullPath, p);
                    if (imgBytes != null && imgBytes.Length > 0)
                    {
                        string entryPath = $"{chapterFolderName}/page_{p + 1:D4}.jpg";
                        var entry = destinationArchive.CreateEntry(entryPath, compressionLevel);
                        using var es = entry.Open();
                        es.Write(imgBytes, 0, imgBytes.Length);
                    }
                }
                catch { }
            }
            return;
        }

        // 3. Nguồn là RAR / CBR
        if (LibraryScannerService.IsRarFile(item.FullPath))
        {
            var entries = RarArchivePool.Instance.GetImageEntriesInfo(item.FullPath);
            var sortedEntries = entries.OrderBy(e => e.FullName, NaturalSortComparer.Instance).ToList();

            int pIdx = 1;
            foreach (var e in sortedEntries)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    byte[]? imgBytes = RarArchivePool.Instance.ReadEntryBytes(item.FullPath, e.FullName);
                    if (imgBytes != null && imgBytes.Length > 0)
                    {
                        string imgName = Path.GetFileName(e.Name);
                        if (string.IsNullOrWhiteSpace(imgName)) imgName = $"page_{pIdx:D4}.jpg";
                        string entryPath = $"{chapterFolderName}/{imgName}";

                        var entry = destinationArchive.CreateEntry(entryPath, compressionLevel);
                        using var es = entry.Open();
                        es.Write(imgBytes, 0, imgBytes.Length);
                        pIdx++;
                    }
                }
                catch { }
            }
            return;
        }

        // 4. Nguồn là ZIP / CBZ
        if (LibraryScannerService.IsArchiveFile(item.FullPath))
        {
            try
            {
                using var sourceFs = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                using var sourceZip = new ZipArchive(sourceFs, ZipArchiveMode.Read);

                var imgEntries = sourceZip.Entries
                    .Where(e => !e.FullName.EndsWith('/') && ValidImageExtensions.Contains(Path.GetExtension(e.Name)))
                    .OrderBy(e => e.FullName, NaturalSortComparer.Instance)
                    .ToList();

                int pIdx = 1;
                foreach (var se in imgEntries)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        string imgName = Path.GetFileName(se.Name);
                        if (string.IsNullOrWhiteSpace(imgName)) imgName = $"page_{pIdx:D4}.jpg";
                        string entryPath = $"{chapterFolderName}/{imgName}";

                        var entry = destinationArchive.CreateEntry(entryPath, compressionLevel);
                        using var es = entry.Open();
                        using var ss = se.Open();
                        ss.CopyTo(es);
                        pIdx++;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                LogEmitted?.Invoke("WARN", $"Không thể giải nén {item.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Trích xuất toàn bộ ảnh của một chapter nguồn ra thư mục tạm hoặc lấy danh sách ảnh có sẵn.
    /// Hỗ trợ đầy đủ: folder ảnh, .zip, .cbz, .pdf, .mobi, .rar, .cbr.
    /// </summary>
    private List<string> ExtractChapterImagesToTemp(ArchiveMergeSourceItem item, string tempDir, CancellationToken ct)
    {
        var result = new List<string>();

        if (item.IsDirectory)
        {
            if (!Directory.Exists(item.FullPath)) return result;
            return Directory.EnumerateFiles(item.FullPath, "*.*", SearchOption.AllDirectories)
                .Where(f => ValidImageExtensions.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, NaturalSortComparer.Instance)
                .ToList();
        }

        string subDir = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(subDir);

        // 1. Nguồn là PDF
        if (LibraryScannerService.IsPdfFile(item.FullPath))
        {
            var pdfMeta = PdfComicParser.Instance.InspectPdf(item.FullPath);
            int totalPages = pdfMeta.Pages.Count;
            for (int p = 0; p < totalPages; p++)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    byte[]? imgBytes = PdfComicParser.Instance.ExtractPageImageBytes(item.FullPath, p, out string imgExt);
                    if (imgBytes != null && imgBytes.Length > 0)
                    {
                        string outPath = Path.Combine(subDir, $"p_{p + 1:D4}{imgExt}");
                        File.WriteAllBytes(outPath, imgBytes);
                        result.Add(outPath);
                    }
                }
                catch { }
            }
            return result;
        }

        // 2. Nguồn là MOBI
        if (LibraryScannerService.IsMobiFile(item.FullPath))
        {
            var meta = MobiComicParser.Instance.InspectMobi(item.FullPath);
            int totalPages = meta.Pages.Count;
            for (int p = 0; p < totalPages; p++)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    byte[]? imgBytes = MobiComicParser.Instance.ReadPageBytes(item.FullPath, p);
                    if (imgBytes != null && imgBytes.Length > 0)
                    {
                        string outPath = Path.Combine(subDir, $"p_{p + 1:D4}.jpg");
                        File.WriteAllBytes(outPath, imgBytes);
                        result.Add(outPath);
                    }
                }
                catch { }
            }
            return result;
        }

        // 3. Nguồn là RAR / CBR
        if (LibraryScannerService.IsRarFile(item.FullPath))
        {
            var entries = RarArchivePool.Instance.GetImageEntriesInfo(item.FullPath);
            var sortedEntries = entries.OrderBy(e => e.FullName, NaturalSortComparer.Instance).ToList();
            int pIdx = 1;
            foreach (var e in sortedEntries)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    byte[]? imgBytes = RarArchivePool.Instance.ReadEntryBytes(item.FullPath, e.FullName);
                    if (imgBytes != null && imgBytes.Length > 0)
                    {
                        string ext = Path.GetExtension(e.Name);
                        if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
                        string outPath = Path.Combine(subDir, $"p_{pIdx:D4}{ext}");
                        File.WriteAllBytes(outPath, imgBytes);
                        result.Add(outPath);
                        pIdx++;
                    }
                }
                catch { }
            }
            return result;
        }

        // 4. Nguồn là ZIP / CBZ
        if (LibraryScannerService.IsArchiveFile(item.FullPath))
        {
            try
            {
                using var sourceFs = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                using var sourceZip = new ZipArchive(sourceFs, ZipArchiveMode.Read);
                var imgEntries = sourceZip.Entries
                    .Where(e => !e.FullName.EndsWith('/') && ValidImageExtensions.Contains(Path.GetExtension(e.Name)))
                    .OrderBy(e => e.FullName, NaturalSortComparer.Instance)
                    .ToList();

                int pIdx = 1;
                foreach (var se in imgEntries)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        string ext = Path.GetExtension(se.Name);
                        if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
                        string outPath = Path.Combine(subDir, $"p_{pIdx:D4}{ext}");
                        using var es = se.Open();
                        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
                        es.CopyTo(fs);
                        result.Add(outPath);
                        pIdx++;
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        return result;
    }
}


