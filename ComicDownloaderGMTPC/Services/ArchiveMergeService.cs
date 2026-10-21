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

    private static readonly HashSet<string> ValidArchiveAndDocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".cbz", ".rar", ".cbr", ".7z", ".tar", ".gz",
        ".pdf", ".mobi", ".prc", ".azw", ".azw3",
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".jfif", ".gif"
    };

    /// <summary>
    /// Chỉ bóc tách phần mở rộng tệp nếu nó thực sự là một định dạng file nén, tài liệu hoặc ảnh đã biết.
    /// Tuyệt đối giữ nguyên vẹn tên chapter có số thập phân như "3.1", "3.2", "Chap 3.5".
    /// </summary>
    public static string StripArchiveOrDocExtension(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        string ext = Path.GetExtension(name);
        if (!string.IsNullOrEmpty(ext) && ValidArchiveAndDocExtensions.Contains(ext))
        {
            return Path.GetFileNameWithoutExtension(name);
        }
        return name;
    }

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
    /// Thu thập các file chapter lẻ (.pdf, .zip, .cbz, .rar, .cbr, .mobi) hoặc thư mục con chứa ảnh.
    /// Tự động đào sâu qua các thư mục cha lồng nhau để bóc tách chính xác từng thư mục chapter chứa ảnh.
    /// </summary>
    private List<ArchiveMergeSourceItem> CollectSourceItems(string inputFolder)
    {
        var list = new List<ArchiveMergeSourceItem>();

        try
        {
            var files = Directory.GetFiles(inputFolder, "*.*", SearchOption.TopDirectoryOnly);
            foreach (var f in files)
            {
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

            // Quét các folder chapter con (hỗ trợ cả cấu trúc thư mục phẳng lẫn lồng nhau)
            CollectChapterFoldersRecursive(inputFolder, list, currentDepth: 0, maxDepth: 4);
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

    private void CollectChapterFoldersRecursive(string currentFolder, List<ArchiveMergeSourceItem> list, int currentDepth, int maxDepth)
    {
        if (currentDepth > maxDepth) return;

        string[] dirs;
        try { dirs = Directory.GetDirectories(currentFolder, "*", SearchOption.TopDirectoryOnly); }
        catch { dirs = Array.Empty<string>(); }

        foreach (var d in dirs)
        {
            string dirName = Path.GetFileName(d);
            if (dirName.Equals("merged", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("packed", StringComparison.OrdinalIgnoreCase) ||
                dirName.StartsWith('.'))
            {
                continue;
            }

            // Kiểm tra xem thư mục này có chứa file ảnh trực tiếp không
            bool hasDirectImages = false;
            try
            {
                hasDirectImages = Directory.EnumerateFiles(d, "*.*", SearchOption.TopDirectoryOnly)
                    .Any(f => ValidImageExtensions.Contains(Path.GetExtension(f)));
            }
            catch { }

            if (hasDirectImages)
            {
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
            else
            {
                // Thư mục này không có ảnh trực tiếp, có thể là thư mục cha (ví dụ "Conan" hoặc "Season 1")
                // Tiếp tục đào sâu xuống các thư mục con bên trong để tìm các thư mục chapter thực sự chứa ảnh
                CollectChapterFoldersRecursive(d, list, currentDepth + 1, maxDepth);
            }
        }
    }

    /// <summary>
    /// Trích xuất số chương từ tên file/folder (nhận diện cả chap số nguyên và chap số thập phân như 3.1, 3.2, 100.1, 100.5)
    /// </summary>
    public static void ParseChapterInfo(string name, out double chapterNumber, out int baseInteger, out string formattedLabel)
    {
        string nameWithoutExt = StripArchiveOrDocExtension(name);

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
    /// Đặt tên an toàn cho folder con bên trong file nén (ví dụ "Chap 001", "3.1", "3.2", "Chap 016.1", "Chương 100").
    /// Giữ nguyên vẹn 100% tên chapter gốc và các số thập phân.
    /// </summary>
    private string MakeSafeFolderName(ArchiveMergeSourceItem item)
    {
        string raw = item.IsDirectory ? item.Name : StripArchiveOrDocExtension(item.Name);
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
                        string imgExt = DetectImageExtension(imgBytes);
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
                        string imgExt = DetectImageExtension(imgBytes);
                        string outPath = Path.Combine(subDir, $"p_{p + 1:D4}{imgExt}");
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

    private static string DetectImageExtension(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 4) return ".jpg";
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return ".gif";
        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return ".webp";
        if (bytes[0] == 0x42 && bytes[1] == 0x4D) return ".bmp";
        return ".jpg";
    }


    /// <summary>
    /// Nối thêm các chương mới từ thư mục vào một file đóng gói đã có (.pdf, .cbz, .zip, .mobi).
    /// Bảo tồn 100% cấu trúc từng chapter riêng biệt và cây Bookmarks mục lục.
    /// </summary>
    public async Task<(bool success, string message, string finalFilePath)> AppendChaptersToFileAsync(
        string baseFilePath,
        string newChaptersFolder,
        bool overwriteOriginal,
        string customOutputPath,
        bool preserveBookmarks,
        IProgress<(int current, int total, string message)>? progress,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(baseFilePath) || !File.Exists(baseFilePath))
            {
                LogEmitted?.Invoke("ERROR", $"Tệp gốc không tồn tại: {baseFilePath}");
                return (false, "Tệp gốc không tồn tại.", string.Empty);
            }

            if (string.IsNullOrWhiteSpace(newChaptersFolder) || !Directory.Exists(newChaptersFolder))
            {
                LogEmitted?.Invoke("ERROR", $"Thư mục chứa chapter mới không tồn tại: {newChaptersFolder}");
                return (false, "Thư mục chứa chapter mới không tồn tại.", string.Empty);
            }

            string ext = Path.GetExtension(baseFilePath).ToLowerInvariant();
            if (ext != ".pdf" && ext != ".cbz" && ext != ".zip" && ext != ".mobi")
            {
                LogEmitted?.Invoke("ERROR", $"Định dạng tệp không được hỗ trợ để nối chapter: {ext}");
                return (false, $"Định dạng tệp {ext} không được hỗ trợ để nối.", string.Empty);
            }

            // 1. Quét các chapter mới từ newChaptersFolder
            LogEmitted?.Invoke("INFO", $"Đang quét các chapter mới từ: {newChaptersFolder}...");
            var newItems = CollectSourceItems(newChaptersFolder);

            // Fallback: nếu newChaptersFolder chứa trực tiếp các file ảnh (người dùng tải 1 chap thẳng vào folder đó)
            if (newItems.Count == 0)
            {
                var directImages = Directory.EnumerateFiles(newChaptersFolder, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => ValidImageExtensions.Contains(Path.GetExtension(f)))
                    .ToList();
                if (directImages.Count > 0)
                {
                    string dirName = Path.GetFileName(newChaptersFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    ParseChapterInfo(dirName, out double num, out int baseInt, out string formatted);
                    newItems.Add(new ArchiveMergeSourceItem
                    {
                        FullPath = newChaptersFolder,
                        Name = dirName,
                        IsDirectory = true,
                        ParsedChapterNumber = num,
                        IntegerBaseNumber = baseInt,
                        FormattedChapterLabel = formatted
                    });
                }
            }

            if (newItems.Count == 0)
            {
                LogEmitted?.Invoke("WARN", "Không tìm thấy chương truyện hoặc hình ảnh nào trong thư mục mới.");
                return (false, "Không tìm thấy chương truyện nào trong thư mục mới.", string.Empty);
            }

            newItems = newItems.OrderBy(x => x.ParsedChapterNumber).ThenBy(x => x.Name, NaturalSortComparer.Instance).ToList();
            LogEmitted?.Invoke("INFO", $"Phát hiện {newItems.Count} chương mới cần nối vào tệp gốc: {Path.GetFileName(baseFilePath)}");

            // 2. Xử lý theo từng định dạng file gốc
            if (ext == ".pdf")
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "comic_append_pdf_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    var allImages = new List<string>();
                    var bookmarks = new List<(string Title, int PageIndex)>();

                    LogEmitted?.Invoke("INFO", $"Đang nạp file PDF gốc: {Path.GetFileName(baseFilePath)}...");
                    progress?.Report((0, 100, "Đang đọc cấu trúc file PDF gốc..."));

                    var pdfMeta = PdfComicParser.Instance.InspectPdf(baseFilePath);
                    int oldTotalPages = pdfMeta.Pages.Count;

                    // Bóc tách trang ảnh cũ từ PDF gốc
                    for (int p = 0; p < oldTotalPages; p++)
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            byte[]? imgBytes = PdfComicParser.Instance.ExtractPageImageBytes(baseFilePath, p, out string imgExt);
                            if (imgBytes != null && imgBytes.Length > 0)
                            {
                                string pPath = Path.Combine(tempDir, $"old_p_{p + 1:D5}{imgExt}");
                                File.WriteAllBytes(pPath, imgBytes);
                                allImages.Add(pPath);
                            }
                        }
                        catch { }

                        if (p % 10 == 0 || p == oldTotalPages - 1)
                        {
                            int pct = (int)((double)(p + 1) / Math.Max(1, oldTotalPages) * 35.0);
                            progress?.Report((pct, 100, $"Đang nạp trang cũ ({p + 1}/{oldTotalPages})..."));
                        }
                    }

                    // Nạp Bookmarks cũ
                    if (preserveBookmarks)
                    {
                        if (pdfMeta.Bookmarks != null && pdfMeta.Bookmarks.Count > 0)
                        {
                            foreach (var bm in pdfMeta.Bookmarks)
                            {
                                bookmarks.Add((bm.Title, bm.PageIndex));
                            }
                        }
                        else
                        {
                            string baseTitle = StripArchiveOrDocExtension(Path.GetFileName(baseFilePath));
                            if (string.IsNullOrWhiteSpace(baseTitle)) baseTitle = "Phần gốc";
                            bookmarks.Add((baseTitle, 0));
                        }
                    }

                    // Nạp các chapter mới
                    int totalNew = newItems.Count;
                    for (int i = 0; i < totalNew; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        var item = newItems[i];
                        string chapterLabel = MakeSafeFolderName(item);
                        LogEmitted?.Invoke("INFO", $"  -> Đang ghép nối chương mới [{item.FormattedChapterLabel}]: {item.Name}...");

                        int startPage = allImages.Count;
                        var chapterImages = ExtractChapterImagesToTemp(item, tempDir, ct);
                        if (chapterImages.Count > 0)
                        {
                            allImages.AddRange(chapterImages);
                            if (preserveBookmarks)
                            {
                                bookmarks.Add((chapterLabel, startPage));
                            }
                        }

                        int pct = 35 + (int)((double)(i + 1) / totalNew * 50.0);
                        progress?.Report((pct, 100, $"[{i + 1}/{totalNew}] Đã nạp chương mới: {item.Name}"));
                    }

                    if (allImages.Count == 0 || ct.IsCancellationRequested)
                    {
                        return (false, "Đã hủy hoặc không có ảnh nào được xuất.", string.Empty);
                    }

                    progress?.Report((90, 100, "Đang biên soạn và đóng gói file PDF hoàn chỉnh..."));

                    string outFilePath;
                    string tempPdfOut = Path.Combine(tempDir, "output.pdf");

                    if (overwriteOriginal)
                    {
                        outFilePath = baseFilePath;
                    }
                    else
                    {
                        outFilePath = !string.IsNullOrWhiteSpace(customOutputPath)
                            ? customOutputPath
                            : Path.Combine(Path.GetDirectoryName(baseFilePath)!, $"{Path.GetFileNameWithoutExtension(baseFilePath)}_updated.pdf");
                    }

                    int dummy = 0;
                    FilePackerService.CreatePdfFromImages(allImages, tempPdfOut, preserveBookmarks ? bookmarks : new List<(string Title, int PageIndex)>(), allImages.Count, ref dummy, null, ct);

                    if (overwriteOriginal)
                    {
                        string bakPath = baseFilePath + ".bak";
                        try { if (File.Exists(bakPath)) File.Delete(bakPath); } catch { }
                        File.Move(baseFilePath, bakPath);
                        File.Move(tempPdfOut, baseFilePath);
                        try { File.Delete(bakPath); } catch { }
                    }
                    else
                    {
                        if (File.Exists(outFilePath)) File.Delete(outFilePath);
                        File.Move(tempPdfOut, outFilePath);
                    }

                    PdfComicParser.Instance.InvalidateCache(baseFilePath);
                    PdfComicParser.Instance.InvalidateCache(outFilePath);

                    progress?.Report((100, 100, "Hoàn tất nối chapter vào PDF!"));
                    string msg = $"Đã nối thành công {totalNew} chương mới vào file PDF! (Tổng: {allImages.Count} trang, {bookmarks.Count} mục bookmarks)";
                    LogEmitted?.Invoke("SUCCESS", msg);
                    return (true, msg, outFilePath);
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
            else if (ext == ".cbz" || ext == ".zip")
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "comic_append_archive_" + Guid.NewGuid().ToString("N"));
                string extractDir = Path.Combine(tempDir, "extracted");
                Directory.CreateDirectory(extractDir);

                try
                {
                    LogEmitted?.Invoke("INFO", $"Đang giải nén nội dung file {ext.ToUpper()} gốc: {Path.GetFileName(baseFilePath)}...");
                    progress?.Report((0, 100, "Đang đọc các chương trong file nén gốc..."));

                    using (var zip = ZipFile.OpenRead(baseFilePath))
                    {
                        int entryCount = zip.Entries.Count;
                        int extracted = 0;
                        foreach (var entry in zip.Entries)
                        {
                            if (ct.IsCancellationRequested) break;
                            if (string.IsNullOrEmpty(entry.Name)) continue;

                            string destPath = Path.Combine(extractDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                            string? destParent = Path.GetDirectoryName(destPath);
                            if (!string.IsNullOrEmpty(destParent) && !Directory.Exists(destParent))
                            {
                                Directory.CreateDirectory(destParent);
                            }

                            entry.ExtractToFile(destPath, overwrite: true);
                            extracted++;
                            if (extracted % 20 == 0 || extracted == entryCount)
                            {
                                int pct = (int)((double)extracted / Math.Max(1, entryCount) * 35.0);
                                progress?.Report((pct, 100, $"Đang giải nén file cũ ({extracted}/{entryCount})..."));
                            }
                        }
                    }

                    // Nếu file zip cũ không có subfolder mà toàn bộ ảnh ở root -> gom vào folder mang tên file gốc
                    var subDirs = Directory.GetDirectories(extractDir);
                    if (subDirs.Length == 0)
                    {
                        string baseChapDir = Path.Combine(extractDir, Path.GetFileNameWithoutExtension(baseFilePath));
                        Directory.CreateDirectory(baseChapDir);
                        foreach (var file in Directory.GetFiles(extractDir))
                        {
                            if (ValidImageExtensions.Contains(Path.GetExtension(file)))
                            {
                                string targetFile = Path.Combine(baseChapDir, Path.GetFileName(file));
                                File.Move(file, targetFile);
                            }
                        }
                    }

                    // Nạp các chapter mới vào extractDir
                    int totalNew = newItems.Count;
                    for (int i = 0; i < totalNew; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        var item = newItems[i];
                        string chapFolder = Path.Combine(extractDir, MakeSafeFolderName(item));
                        if (!Directory.Exists(chapFolder))
                        {
                            Directory.CreateDirectory(chapFolder);
                        }

                        LogEmitted?.Invoke("INFO", $"  -> Đang đóng gói chương mới [{item.FormattedChapterLabel}]: {item.Name}...");

                        var chapterImages = ExtractChapterImagesToTemp(item, tempDir, ct);
                        for (int imgIdx = 0; imgIdx < chapterImages.Count; imgIdx++)
                        {
                            string srcImg = chapterImages[imgIdx];
                            string destImgName = $"{imgIdx + 1:D4}_{Path.GetFileName(srcImg)}";
                            string destImgPath = Path.Combine(chapFolder, destImgName);
                            File.Copy(srcImg, destImgPath, true);
                        }

                        int pct = 35 + (int)((double)(i + 1) / totalNew * 45.0);
                        progress?.Report((pct, 100, $"[{i + 1}/{totalNew}] Đã ghép chương mới: {item.Name}"));
                    }

                    if (ct.IsCancellationRequested)
                    {
                        return (false, "Đã hủy bởi người dùng.", string.Empty);
                    }

                    progress?.Report((85, 100, $"Đang nén đóng gói lại file {ext.ToUpper()} hoàn chỉnh..."));

                    string outFilePath;
                    string tempArchiveOut = Path.Combine(tempDir, "output" + ext);

                    if (overwriteOriginal)
                    {
                        outFilePath = baseFilePath;
                    }
                    else
                    {
                        outFilePath = !string.IsNullOrWhiteSpace(customOutputPath)
                            ? customOutputPath
                            : Path.Combine(Path.GetDirectoryName(baseFilePath)!, $"{Path.GetFileNameWithoutExtension(baseFilePath)}_updated{ext}");
                    }

                    ZipFile.CreateFromDirectory(extractDir, tempArchiveOut, CompressionLevel.Fastest, false);

                    if (overwriteOriginal)
                    {
                        string bakPath = baseFilePath + ".bak";
                        try { if (File.Exists(bakPath)) File.Delete(bakPath); } catch { }
                        File.Move(baseFilePath, bakPath);
                        File.Move(tempArchiveOut, baseFilePath);
                        try { File.Delete(bakPath); } catch { }
                    }
                    else
                    {
                        if (File.Exists(outFilePath)) File.Delete(outFilePath);
                        File.Move(tempArchiveOut, outFilePath);
                    }

                    progress?.Report((100, 100, $"Hoàn tất nối chapter vào {ext.ToUpper()}!"));
                    string msg = $"Đã nối thành công {totalNew} chương mới vào file {ext.ToUpper()}!";
                    LogEmitted?.Invoke("SUCCESS", msg);
                    return (true, msg, outFilePath);
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
            else if (ext == ".mobi")
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "comic_append_mobi_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    var chapterData = new List<(string Title, List<string> Images)>();

                    LogEmitted?.Invoke("INFO", $"Đang nạp file MOBI gốc: {Path.GetFileName(baseFilePath)}...");
                    progress?.Report((0, 100, "Đang đọc các trang file MOBI gốc..."));

                    var meta = MobiComicParser.Instance.InspectMobi(baseFilePath);
                    int oldTotalPages = meta.Pages.Count;

                    var oldImages = new List<string>();
                    for (int p = 0; p < oldTotalPages; p++)
                    {
                        if (ct.IsCancellationRequested) break;
                        byte[]? imgBytes = MobiComicParser.Instance.ReadPageBytes(baseFilePath, p);
                        if (imgBytes != null && imgBytes.Length > 0)
                        {
                            string imgExt = DetectImageExtension(imgBytes);
                            string pPath = Path.Combine(tempDir, $"old_p_{p + 1:D5}{imgExt}");
                            File.WriteAllBytes(pPath, imgBytes);
                            oldImages.Add(pPath);
                        }
                    }

                    if (oldImages.Count > 0)
                    {
                        chapterData.Add((Path.GetFileNameWithoutExtension(baseFilePath), oldImages));
                    }

                    int totalNew = newItems.Count;
                    for (int i = 0; i < totalNew; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        var item = newItems[i];
                        string chapterLabel = MakeSafeFolderName(item);
                        LogEmitted?.Invoke("INFO", $"  -> Đang ghép nối chương mới: {item.Name}...");

                        var chapterImages = ExtractChapterImagesToTemp(item, tempDir, ct);
                        if (chapterImages.Count > 0)
                        {
                            chapterData.Add((chapterLabel, chapterImages));
                        }
                    }

                    string outFilePath;
                    string tempMobiOut = Path.Combine(tempDir, "output.mobi");
                    if (overwriteOriginal)
                    {
                        outFilePath = baseFilePath;
                    }
                    else
                    {
                        outFilePath = !string.IsNullOrWhiteSpace(customOutputPath)
                            ? customOutputPath
                            : Path.Combine(Path.GetDirectoryName(baseFilePath)!, $"{Path.GetFileNameWithoutExtension(baseFilePath)}_updated.mobi");
                    }

                    string bookTitle = Path.GetFileNameWithoutExtension(outFilePath);
                    MobiWriterService.CreateMobiFromImages(tempMobiOut, bookTitle, chapterData, null, ct);

                    if (overwriteOriginal)
                    {
                        string bakPath = baseFilePath + ".bak";
                        try { if (File.Exists(bakPath)) File.Delete(bakPath); } catch { }
                        File.Move(baseFilePath, bakPath);
                        File.Move(tempMobiOut, baseFilePath);
                        try { File.Delete(bakPath); } catch { }
                    }
                    else
                    {
                        if (File.Exists(outFilePath)) File.Delete(outFilePath);
                        File.Move(tempMobiOut, outFilePath);
                    }

                    string msg = $"Đã nối thành công {totalNew} chương mới vào file MOBI!";
                    LogEmitted?.Invoke("SUCCESS", msg);
                    return (true, msg, outFilePath);
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }

            return (false, "Định dạng không hợp lệ.", string.Empty);
        });
    }

    /// <summary>
    /// Phân tích tệp gốc để trả về thông tin tóm tắt cho giao diện UI
    /// </summary>
    public string AnalyzeBaseFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return string.Empty;

        try
        {
            var fi = new FileInfo(filePath);
            string sizeStr = fi.Length >= 1024 * 1024
                ? $"{(double)fi.Length / (1024 * 1024):F1} MB"
                : $"{(double)fi.Length / 1024:F0} KB";

            string ext = fi.Extension.ToLowerInvariant();
            if (ext == ".pdf")
            {
                var meta = PdfComicParser.Instance.InspectPdf(filePath);
                int bmCount = meta.Bookmarks?.Count ?? 0;
                return $"📄 File PDF ({sizeStr}) • Tổng {meta.Pages.Count} trang" + (bmCount > 0 ? $" • {bmCount} mục mục lục (Bookmarks)" : string.Empty);
            }
            if (ext == ".cbz" || ext == ".zip")
            {
                using var zip = ZipFile.OpenRead(filePath);
                int fileCount = zip.Entries.Count(e => !string.IsNullOrEmpty(e.Name));
                var distinctDirs = zip.Entries
                    .Select(e => Path.GetDirectoryName(e.FullName.Replace('/', Path.DirectorySeparatorChar)))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct()
                    .Count();

                return $"📦 File {ext.ToUpper()} ({sizeStr}) • {fileCount} tệp ảnh" + (distinctDirs > 0 ? $" • {distinctDirs} thư mục chương" : string.Empty);
            }
            if (ext == ".mobi")
            {
                var meta = MobiComicParser.Instance.InspectMobi(filePath);
                return $"📱 File MOBI Kindle ({sizeStr}) • {meta.Pages.Count} trang ảnh";
            }

            return $"📁 Tệp {ext.ToUpper()} ({sizeStr})";
        }
        catch (Exception ex)
        {
            return $"⚠️ Lỗi đọc tệp: {ex.Message}";
        }
    }

    /// <summary>
    /// Quét nhanh thư mục chứa chapter mới để trả về thông tin tóm tắt cho giao diện UI
    /// </summary>
    public string AnalyzeNewFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return string.Empty;

        try
        {
            var items = CollectSourceItems(folderPath);
            if (items.Count > 0)
            {
                var first = items.OrderBy(x => x.ParsedChapterNumber).First();
                var last = items.OrderBy(x => x.ParsedChapterNumber).Last();
                return $"📁 Phát hiện {items.Count} chương mới • Từ: [{first.FormattedChapterLabel}] đến: [{last.FormattedChapterLabel}]";
            }

            // Kiểm tra ảnh trực tiếp
            var directImgs = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => ValidImageExtensions.Contains(Path.GetExtension(f)))
                .Count();

            if (directImgs > 0)
            {
                return $"📁 Phát hiện 1 chương (chứa {directImgs} tệp ảnh trực tiếp)";
            }

            return "⚠️ Thư mục trống hoặc không chứa chương truyện / hình ảnh hợp lệ.";
        }
        catch (Exception ex)
        {
            return $"⚠️ Lỗi quét thư mục: {ex.Message}";
        }
    }

}