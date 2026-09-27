using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ComicDownloaderGMTPC.Services;

public class AlphabetRangeItem
{
    public string DisplayName { get; set; } = string.Empty;
    public char StartLetter { get; set; }
    public char EndLetter { get; set; }

    public static bool TryParse(string input, out AlphabetRangeItem? item)
    {
        item = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string clean = input.Trim().ToUpperInvariant();
        var parts = clean.Split(new[] { '-', '–', '—' }, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 2)
        {
            string p1 = parts[0].Trim();
            string p2 = parts[1].Trim();
            if (p1.Length > 0 && p2.Length > 0)
            {
                char c1 = p1[0];
                char c2 = p2[0];
                if (c1 >= 'A' && c1 <= 'Z' && c2 >= 'A' && c2 <= 'Z')
                {
                    char start = c1 <= c2 ? c1 : c2;
                    char end = c1 <= c2 ? c2 : c1;
                    item = new AlphabetRangeItem
                    {
                        DisplayName = $"{start}-{end}",
                        StartLetter = start,
                        EndLetter = end
                    };
                    return true;
                }
            }
        }
        else if (parts.Length == 1)
        {
            string p = parts[0].Trim();
            if (p.Length > 0 && p[0] >= 'A' && p[0] <= 'Z')
            {
                item = new AlphabetRangeItem
                {
                    DisplayName = p[0].ToString(),
                    StartLetter = p[0],
                    EndLetter = p[0]
                };
                return true;
            }
        }

        return false;
    }
}

public class FolderToolsService
{
    private static readonly Lazy<FolderToolsService> _instance = new(() => new FolderToolsService());
    public static FolderToolsService Instance => _instance.Value;

    private readonly SemaphoreSlim _folderLock = new(1, 1);

    public event Action<string, string>? LogEmitted;
    public event Action<int, int, string>? ProgressChanged;

    public async Task<int> SplitByChapterCountAsync(string rootFolder, int groupSize, string folderType, bool mergeRemainder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder) || groupSize <= 0)
        {
            return 0;
        }

        await _folderLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            LogEmitted?.Invoke("INFO", $"[Tách Folder] Bắt đầu quét thư mục tại: {rootFolder} | Cỡ nhóm: {groupSize} chap | Kiểu: {folderType}");
            ProgressChanged?.Invoke(0, 1, "Đang quét danh sách chapter...");
            
            var chapterItems = CollectChapterFolders(rootFolder);
            int totalChapters = chapterItems.Count;

            if (totalChapters == 0)
            {
                LogEmitted?.Invoke("WARN", $"[Tách Folder] Không tìm thấy chapter hợp lệ nào chứa ảnh tại: {rootFolder}");
                ProgressChanged?.Invoke(0, 0, "Không tìm thấy chapter nào.");
                return 0;
            }

            var bookGroups = chapterItems.GroupBy(x => x.BookFolderPath, StringComparer.OrdinalIgnoreCase).ToList();
            int splitCount = 0;
            long lastProgressTicks = 0;

            LogEmitted?.Invoke("INFO", $"[Tách Folder] Đã tìm thấy {totalChapters} chapter trong {bookGroups.Count} bộ truyện. Bắt đầu phân chia...");

            foreach (var bookGroup in bookGroups)
            {
                if (ct.IsCancellationRequested) break;

                string bookFolder = bookGroup.Key;
                var chapters = bookGroup
                    .OrderBy(item => item.ChapterNumber)
                    .ThenBy(item => item.FolderName, NaturalSortComparer.Instance)
                    .ToList();

                if (chapters.Count < 2)
                {
                    continue;
                }

                var rawBuckets = chapters
                    .GroupBy(item => (Math.Max(1, item.ChapterNumber) - 1) / groupSize)
                    .OrderBy(g => g.Key)
                    .Select(g => new
                    {
                        Key = g.Key,
                        Items = g.OrderBy(item => item.ChapterNumber)
                                 .ThenBy(item => item.FolderName, NaturalSortComparer.Instance)
                                 .ToList()
                    })
                    .Where(b => b.Items.Count > 0)
                    .ToList();

                if (rawBuckets.Count == 0)
                {
                    continue;
                }

                var bucketList = rawBuckets.Select(b => new List<ChapterFolderItem>(b.Items)).ToList();
                var bucketKeys = rawBuckets.Select(b => b.Key).ToList();

                if (bucketList.Count > 1 && mergeRemainder)
                {
                    int lastIndex = bucketList.Count - 1;
                    var lastBucket = bucketList[lastIndex];
                    int lastBucketKey = bucketKeys[lastIndex];
                    int lastEnd = lastBucket.Max(item => Math.Max(1, item.ChapterNumber));
                    int expectedEnd = (lastBucketKey + 1) * groupSize;

                    if (lastEnd < expectedEnd)
                    {
                        bucketList[lastIndex - 1].AddRange(lastBucket);
                        bucketList.RemoveAt(lastIndex);
                        bucketKeys.RemoveAt(lastIndex);
                    }
                }

                foreach (var bucket in bucketList)
                {
                    if (ct.IsCancellationRequested) break;
                    if (bucket.Count == 0) continue;

                    int start = bucket.Min(item => Math.Max(1, item.ChapterNumber));
                    int end = bucket.Max(item => Math.Max(1, item.ChapterNumber));

                    string groupFolderName = BuildGroupFolderName(bookFolder, start, end, folderType);
                    string groupFolderPath = Path.Combine(bookFolder, groupFolderName);

                    try
                    {
                        if (!Directory.Exists(groupFolderPath))
                        {
                            Directory.CreateDirectory(groupFolderPath);
                        }
                    }
                    catch { }

                    foreach (var chapter in bucket)
                    {
                        if (ct.IsCancellationRequested) break;

                        string destPath = Path.Combine(groupFolderPath, chapter.FolderName);
                        if (string.Equals(chapter.SourcePath, destPath, StringComparison.OrdinalIgnoreCase)) continue;

                        try
                        {
                            SafeMoveDirectory(chapter.SourcePath, destPath);
                            splitCount++;
                            LogEmitted?.Invoke("SUCCESS", $"[Tách] Đã chuyển '{chapter.FolderName}' -> '{groupFolderName}'");
                        }
                        catch (Exception ex)
                        {
                            LogEmitted?.Invoke("ERROR", $"[Tách Lỗi] Không thể chuyển '{chapter.SourcePath}': {ex.Message}");
                        }

                        long now = Environment.TickCount64;
                        if (splitCount == totalChapters || now - lastProgressTicks >= 100)
                        {
                            lastProgressTicks = now;
                            double pct = (double)splitCount / totalChapters * 100.0;
                            ProgressChanged?.Invoke(splitCount, totalChapters, $"Đã tách {splitCount}/{totalChapters} chapter ({pct:F0}%)...");
                        }

                        await Task.Yield();
                    }
                }
            }

            // Dọn dẹp các thư mục rỗng 1 lần duy nhất ở cuối để tránh lặp quét đĩa gây lag
            DeleteEmptyDirectoriesBottomUp(rootFolder);
            ProgressChanged?.Invoke(totalChapters, totalChapters, $"Hoàn tất: Đã tách {splitCount}/{totalChapters} chapter.");
            LogEmitted?.Invoke("SUCCESS", $"[Tách Folder] Hoàn tất tách {splitCount} chapter folders tại {rootFolder}");
            return splitCount;
        }
        finally
        {
            _folderLock.Release();
        }
    }

    public async Task<int> MergeByChapterCountAsync(string rootFolder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
        {
            return 0;
        }

        await _folderLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            LogEmitted?.Invoke("INFO", $"[Gộp Chapter] Bắt đầu quét chapter về thư mục gốc: {rootFolder}");
            ProgressChanged?.Invoke(0, 1, "Đang quét chapter cần gộp...");

            var items = CollectChapterFolders(rootFolder)
                .Where(item => !string.Equals(item.SourcePath, rootFolder, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Depth)
                .ThenBy(item => item.ChapterNumber)
                .ThenBy(item => item.FolderName, NaturalSortComparer.Instance)
                .ToList();

            int mergedCount = 0;
            int total = items.Count;
            long lastProgressTicks = 0;

            if (total == 0)
            {
                LogEmitted?.Invoke("WARN", $"[Gộp Chapter] Không tìm thấy chapter nào cần gộp tại: {rootFolder}");
                ProgressChanged?.Invoke(0, 0, "Không tìm thấy chapter nào cần gộp.");
                return 0;
            }

            LogEmitted?.Invoke("INFO", $"[Gộp Chapter] Tìm thấy {total} chapter cần gộp về gốc. Bắt đầu di chuyển...");

            foreach (var chapter in items)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(chapter.SourcePath) || string.IsNullOrWhiteSpace(chapter.FolderName)) continue;

                string destPath = Path.Combine(rootFolder, chapter.FolderName);
                if (string.Equals(chapter.SourcePath, destPath, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    SafeMoveDirectory(chapter.SourcePath, destPath);
                    mergedCount++;
                    LogEmitted?.Invoke("SUCCESS", $"[Gộp] Đã chuyển '{chapter.FolderName}' -> thư mục gốc");
                }
                catch (Exception ex)
                {
                    LogEmitted?.Invoke("ERROR", $"[Gộp Lỗi] Lỗi gộp '{chapter.SourcePath}': {ex.Message}");
                }

                long now = Environment.TickCount64;
                if (mergedCount == total || now - lastProgressTicks >= 100)
                {
                    lastProgressTicks = now;
                    double pct = (double)mergedCount / total * 100.0;
                    ProgressChanged?.Invoke(mergedCount, total, $"Đã gộp {mergedCount}/{total} chapter ({pct:F0}%)...");
                }

                await Task.Yield();
            }

            DeleteEmptyDirectoriesBottomUp(rootFolder);
            ProgressChanged?.Invoke(total, total, $"Hoàn tất: Đã gộp {mergedCount}/{total} chapter về gốc.");
            LogEmitted?.Invoke("SUCCESS", $"[Gộp Chapter] Hoàn tất gộp {mergedCount} chapter folders về {rootFolder}");
            return mergedCount;
        }
        finally
        {
            _folderLock.Release();
        }
    }

    public async Task<int> SplitByAlphabetAsync(string rootFolder, List<string> rawRanges, bool ignoreLeadingTags, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder) || rawRanges == null || rawRanges.Count == 0)
        {
            return 0;
        }

        await _folderLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var parsedRanges = new List<AlphabetRangeItem>();
            foreach (var raw in rawRanges)
            {
                if (AlphabetRangeItem.TryParse(raw, out var item) && item != null)
                {
                    parsedRanges.Add(item);
                }
            }

            if (parsedRanges.Count == 0) return 0;

            LogEmitted?.Invoke("INFO", $"[Tách Alphabet] Bắt đầu phân loại theo chữ cái tại: {rootFolder} | Dải: {string.Join(", ", parsedRanges.Select(r => r.DisplayName))}");
            ProgressChanged?.Invoke(0, 1, "Đang quét danh sách thư mục...");

            var excludedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "[Number]", "Number", "[Numbers]", "Numbers",
                "other language", "Other Language", "other languages", "Other Languages"
            };
            foreach (var r in parsedRanges) excludedNames.Add(r.DisplayName);

            var dirInfo = new DirectoryInfo(rootFolder);
            DirectoryInfo[] subDirs;
            try { subDirs = dirInfo.GetDirectories(); } catch { subDirs = Array.Empty<DirectoryInfo>(); }

            var candidateDirs = subDirs.Where(subDir =>
                !subDir.Attributes.HasFlag(FileAttributes.Hidden) &&
                !subDir.Attributes.HasFlag(FileAttributes.System) &&
                !subDir.Name.StartsWith(".") &&
                !subDir.Name.EndsWith("-tmp", StringComparison.OrdinalIgnoreCase) &&
                !excludedNames.Contains(subDir.Name)
            ).ToList();

            int splitCount = 0;
            int total = candidateDirs.Count;
            long lastProgressTicks = 0;

            if (total == 0)
            {
                LogEmitted?.Invoke("WARN", $"[Tách Alphabet] Không tìm thấy thư mục nào cần phân loại tại: {rootFolder}");
                ProgressChanged?.Invoke(0, 0, "Không có thư mục cần phân loại.");
                return 0;
            }

            for (int i = 0; i < candidateDirs.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var subDir = candidateDirs[i];

                string categoryName = DetermineAlphabetCategory(subDir.Name, parsedRanges, ignoreLeadingTags);
                string destParentDir = Path.Combine(rootFolder, categoryName);
                string destPath = Path.Combine(destParentDir, subDir.Name);

                if (string.Equals(subDir.FullName, destPath, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    if (!Directory.Exists(destParentDir))
                    {
                        Directory.CreateDirectory(destParentDir);
                    }
                }
                catch { }

                try
                {
                    SafeMoveDirectory(subDir.FullName, destPath);
                    splitCount++;
                    LogEmitted?.Invoke("SUCCESS", $"[Tách Alphabet] '{subDir.Name}' -> '{categoryName}'");
                }
                catch (Exception ex)
                {
                    LogEmitted?.Invoke("ERROR", $"[Tách Alphabet Lỗi] Không thể chuyển '{subDir.FullName}': {ex.Message}");
                }

                long now = Environment.TickCount64;
                if (i + 1 == total || now - lastProgressTicks >= 100)
                {
                    lastProgressTicks = now;
                    double pct = (double)(i + 1) / total * 100.0;
                    ProgressChanged?.Invoke(i + 1, total, $"Đã phân loại {splitCount}/{total} thư mục ({pct:F0}%)...");
                }

                await Task.Yield();
            }

            DeleteEmptyDirectoriesBottomUp(rootFolder);
            ProgressChanged?.Invoke(total, total, $"Hoàn tất: Đã phân loại {splitCount}/{total} thư mục.");
            LogEmitted?.Invoke("SUCCESS", $"[Tách Alphabet] Hoàn tất chia {splitCount} thư mục theo bảng chữ cái.");
            return splitCount;
        }
        finally
        {
            _folderLock.Release();
        }
    }

    public async Task<int> MergeByAlphabetAsync(string rootFolder, List<string> rawRanges, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
        {
            return 0;
        }

        await _folderLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            LogEmitted?.Invoke("INFO", $"[Gộp Alphabet] Bắt đầu quét thư mục chữ cái về gốc: {rootFolder}");
            ProgressChanged?.Invoke(0, 1, "Đang quét thư mục chữ cái...");

            var categoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "[Number]", "Number", "[Numbers]", "Numbers",
                "other language", "Other Language", "other languages", "Other Languages", "[Other Latin]"
            };

            if (rawRanges != null)
            {
                foreach (var raw in rawRanges)
                {
                    if (AlphabetRangeItem.TryParse(raw, out var item) && item != null)
                    {
                        categoryNames.Add(item.DisplayName);
                    }
                    else if (!string.IsNullOrWhiteSpace(raw))
                    {
                        categoryNames.Add(raw.Trim());
                    }
                }
            }

            var rootDirInfo = new DirectoryInfo(rootFolder);
            DirectoryInfo[] subDirs;
            try { subDirs = rootDirInfo.GetDirectories(); } catch { subDirs = Array.Empty<DirectoryInfo>(); }

            var allItemsToMerge = new List<(DirectoryInfo catDir, DirectoryInfo comicDir)>();
            var emptyDirs = new List<DirectoryInfo>();

            foreach (var catDir in subDirs)
            {
                if (catDir.Attributes.HasFlag(FileAttributes.Hidden) ||
                    catDir.Attributes.HasFlag(FileAttributes.System) ||
                    catDir.Name.StartsWith(".") ||
                    catDir.Name.EndsWith("-tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool isAlphabetCategory = categoryNames.Contains(catDir.Name) || AlphabetRangeItem.TryParse(catDir.Name, out _);
                if (!isAlphabetCategory) continue;

                emptyDirs.Add(catDir);
                DirectoryInfo[] cDirs;
                try { cDirs = catDir.GetDirectories(); } catch { cDirs = Array.Empty<DirectoryInfo>(); }

                foreach (var comicDir in cDirs)
                {
                    allItemsToMerge.Add((catDir, comicDir));
                }
            }

            int mergedCount = 0;
            int total = allItemsToMerge.Count;
            long lastProgressTicks = 0;

            if (total == 0)
            {
                LogEmitted?.Invoke("WARN", $"[Gộp Alphabet] Không tìm thấy thư mục nào cần gộp tại: {rootFolder}");
                ProgressChanged?.Invoke(0, 0, "Không có thư mục nào cần gộp.");
                return 0;
            }

            LogEmitted?.Invoke("INFO", $"[Gộp Alphabet] Tìm thấy {total} thư mục cần gộp về gốc. Bắt đầu di chuyển...");

            for (int i = 0; i < allItemsToMerge.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var (catDir, comicDir) = allItemsToMerge[i];

                string destPath = Path.Combine(rootFolder, comicDir.Name);
                if (string.Equals(comicDir.FullName, destPath, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    SafeMoveDirectory(comicDir.FullName, destPath);
                    mergedCount++;
                    LogEmitted?.Invoke("SUCCESS", $"[Gộp Alphabet] '{catDir.Name}\\{comicDir.Name}' -> '{comicDir.Name}'");
                }
                catch (Exception ex)
                {
                    LogEmitted?.Invoke("ERROR", $"[Gộp Alphabet Lỗi] Lỗi gộp '{comicDir.FullName}': {ex.Message}");
                }

                long now = Environment.TickCount64;
                if (i + 1 == total || now - lastProgressTicks >= 100)
                {
                    lastProgressTicks = now;
                    double pct = (double)(i + 1) / total * 100.0;
                    ProgressChanged?.Invoke(i + 1, total, $"Đã gộp {mergedCount}/{total} thư mục ({pct:F0}%)...");
                }

                await Task.Yield();
            }

            foreach (var dir in emptyDirs)
            {
                try
                {
                    if (Directory.Exists(dir.FullName) && !Directory.EnumerateFileSystemEntries(dir.FullName).Any())
                    {
                        Directory.Delete(dir.FullName, false);
                    }
                }
                catch {}
            }

            DeleteEmptyDirectoriesBottomUp(rootFolder);
            ProgressChanged?.Invoke(total, total, $"Hoàn tất: Đã gộp {mergedCount}/{total} thư mục về gốc.");
            LogEmitted?.Invoke("SUCCESS", $"[Gộp Alphabet] Hoàn tất gộp {mergedCount} thư mục về {rootFolder}");
            return mergedCount;
        }
        finally
        {
            _folderLock.Release();
        }
    }

    private string DetermineAlphabetCategory(string folderName, List<AlphabetRangeItem> ranges, bool ignoreLeadingTags)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return "other language";

        string cleanName = folderName.Trim();
        if (ignoreLeadingTags)
        {
            cleanName = StripLeadingTags(cleanName);
        }

        char firstChar = '\0';
        foreach (char c in cleanName)
        {
            if (char.IsWhiteSpace(c) || c == '_' || c == '-' || c == '.' || c == '~') continue;
            firstChar = c;
            break;
        }

        if (firstChar == '\0') return "other language";

        if (char.IsDigit(firstChar)) return "[Number]";

        char latin = ToNormalizedLatinLetter(firstChar);
        if (latin >= 'A' && latin <= 'Z')
        {
            foreach (var r in ranges)
            {
                if (latin >= r.StartLetter && latin <= r.EndLetter)
                {
                    return r.DisplayName;
                }
            }
            return latin.ToString();
        }

        return "other language";
    }

    private static string StripLeadingTags(string name)
    {
        string current = name.Trim();
        bool stripped;
        do
        {
            stripped = false;
            current = current.Trim();

            if (current.StartsWith("[") && current.Contains("]"))
            {
                int closeIdx = current.IndexOf(']');
                string rem = current.Substring(closeIdx + 1).Trim();
                if (!string.IsNullOrEmpty(rem)) { current = rem; stripped = true; continue; }
            }
            if (current.StartsWith("(") && current.Contains(")"))
            {
                int closeIdx = current.IndexOf(')');
                string rem = current.Substring(closeIdx + 1).Trim();
                if (!string.IsNullOrEmpty(rem)) { current = rem; stripped = true; continue; }
            }
            if (current.StartsWith("【") && current.Contains("】"))
            {
                int closeIdx = current.IndexOf('】');
                string rem = current.Substring(closeIdx + 1).Trim();
                if (!string.IsNullOrEmpty(rem)) { current = rem; stripped = true; continue; }
            }
        } while (stripped && current.Length > 0);

        return string.IsNullOrWhiteSpace(current) ? name : current;
    }

    private static char ToNormalizedLatinLetter(char c)
    {
        if (c == 'Đ' || c == 'đ') return 'D';
        string normalized = c.ToString().Normalize(NormalizationForm.FormD);
        if (normalized.Length > 0)
        {
            char baseChar = char.ToUpperInvariant(normalized[0]);
            if (baseChar >= 'A' && baseChar <= 'Z') return baseChar;
        }
        return '\0';
    }

    private static string BuildGroupFolderName(string bookFolder, int start, int end, string folderType)
    {
        string range = $"chap {start:0000}-{end:0000}";
        if (string.Equals(folderType, "book-chapter", StringComparison.OrdinalIgnoreCase))
        {
            string bookName = Path.GetFileName(bookFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(bookName) ? range : $"{bookName}-{range}";
        }
        return range;
    }

    private class ChapterFolderItem
    {
        public string SourcePath { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public string BookFolderPath { get; set; } = string.Empty;
        public int ChapterNumber { get; set; }
        public int Depth { get; set; }
    }

    private List<ChapterFolderItem> CollectChapterFolders(string rootFolder)
    {
        var items = new List<ChapterFolderItem>();
        if (!Directory.Exists(rootFolder)) return items;

        var stack = new Stack<string>();
        stack.Push(rootFolder);

        while (stack.Count > 0)
        {
            string current = stack.Pop();
            string[] subDirs;
            try { subDirs = Directory.GetDirectories(current); } catch { continue; }

            // Sắp xếp tự nhiên để duyệt theo đúng thứ tự
            var sortedSubDirs = subDirs.OrderBy(d => Path.GetFileName(d), NaturalSortComparer.Instance);

            foreach (var folder in sortedSubDirs)
            {
                string folderName = Path.GetFileName(folder);
                if (folderName.StartsWith(".") || folderName.EndsWith("-tmp", StringComparison.OrdinalIgnoreCase)) continue;

                if (DirectoryContainsImages(folder) && ChapterFilter.TryParseChapterNumber(folderName, out double chapNum))
                {
                    string parent = Path.GetDirectoryName(folder) ?? rootFolder;
                    string parentName = Path.GetFileName(parent);
                    string bookPath = parent;

                    if (Regex.IsMatch(parentName, @"(^|-)chap\s+\d{4}-\d{4}$", RegexOptions.IgnoreCase))
                    {
                        string grandParent = Path.GetDirectoryName(parent) ?? rootFolder;
                        bookPath = grandParent;
                    }

                    items.Add(new ChapterFolderItem
                    {
                        SourcePath = folder,
                        FolderName = folderName,
                        BookFolderPath = bookPath,
                        ChapterNumber = Math.Max(1, (int)Math.Floor(chapNum)),
                        Depth = folder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).Length
                    });
                }
                else
                {
                    stack.Push(folder);
                }
            }
        }

        return items;
    }

    private static bool DirectoryContainsImages(string folder)
    {
        try
        {
            var ext = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".jfif", ".avif", ".heic", ".heif" };
            return Directory.EnumerateFiles(folder).Any(f => ext.Contains(Path.GetExtension(f)));
        }
        catch { return false; }
    }

    /// <summary>
    /// Di chuyển thư mục an toàn tuyệt đối trên mọi hệ điều hành (kể cả Android Scoped Storage / Cross-mount).
    /// </summary>
    private static void SafeMoveDirectory(string source, string dest)
    {
        if (string.Equals(source, dest, StringComparison.OrdinalIgnoreCase)) return;

        // Tránh đệ quy vô tận nếu dest nằm bên trong source
        string normSource = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normDest = dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (normDest.StartsWith(normSource, StringComparison.OrdinalIgnoreCase)) return;

        // 1. Luôn đảm bảo thư mục cha của dest phải tồn tại TRƯỚC TIÊN
        string? parentDest = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(parentDest) && !Directory.Exists(parentDest))
        {
            try { Directory.CreateDirectory(parentDest); } catch { }
        }

        // 2. Thử Directory.Move nhanh trước nếu thư mục đích chưa tồn tại
        try
        {
            if (!Directory.Exists(dest))
            {
                Directory.Move(source, dest);
                return;
            }
        }
        catch
        {
            // Bỏ qua lỗi và chuyển sang fallback an toàn
        }

        // 3. Fallback sang Merge đệ quy an toàn cho từng file
        try
        {
            Directory.CreateDirectory(dest);
            MergeDirectoryContents(source, dest);
        }
        catch
        {
            // Bảo toàn không sập ứng dụng
        }
    }

    private static void MergeDirectoryContents(string source, string dest)
    {
        if (string.Equals(source, dest, StringComparison.OrdinalIgnoreCase)) return;

        string normSource = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normDest = dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (normDest.StartsWith(normSource, StringComparison.OrdinalIgnoreCase)) return;

        try { Directory.CreateDirectory(dest); } catch { }

        string[] files;
        try { files = Directory.GetFiles(source); } catch { files = Array.Empty<string>(); }

        foreach (var file in files)
        {
            try
            {
                string destFile = Path.Combine(dest, Path.GetFileName(file));
                if (File.Exists(destFile))
                {
                    var sInfo = new FileInfo(file);
                    var dInfo = new FileInfo(destFile);
                    if (sInfo.Length == dInfo.Length)
                    {
                        try { File.Delete(file); } catch { }
                        continue;
                    }
                }

                // Thử File.Move trước
                try
                {
                    File.Move(file, destFile, overwrite: true);
                }
                catch
                {
                    // Fallback copy + delete nếu File.Move bị lỗi quyền/cross-device trên Android
                    File.Copy(file, destFile, overwrite: true);
                    try { File.Delete(file); } catch { }
                }
            }
            catch {}
        }

        string[] subDirs;
        try { subDirs = Directory.GetDirectories(source); } catch { subDirs = Array.Empty<string>(); }

        foreach (var dir in subDirs)
        {
            if (string.Equals(dir, dest, StringComparison.OrdinalIgnoreCase)) continue;
            string normDir = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (normDest.StartsWith(normDir, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                MergeDirectoryContents(dir, Path.Combine(dest, Path.GetFileName(dir)));
            }
            catch {}
        }

        try
        {
            if (Directory.Exists(source) && !Directory.EnumerateFileSystemEntries(source).Any())
            {
                Directory.Delete(source, false);
            }
        }
        catch {}
    }

    /// <summary>
    /// Xóa thư mục rỗng an toàn từ dưới lên trên (Bottom-Up theo Depth) không bao giờ ném Exception khi gặp thư mục bị hạn chế quyền.
    /// </summary>
    private static void DeleteEmptyDirectoriesBottomUp(string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder)) return;

        try
        {
            var allDirs = new List<string>();
            var stack = new Stack<string>();
            stack.Push(rootFolder);

            while (stack.Count > 0)
            {
                string current = stack.Pop();
                string[] subs;
                try
                {
                    subs = Directory.GetDirectories(current);
                }
                catch
                {
                    continue;
                }

                foreach (var sub in subs)
                {
                    allDirs.Add(sub);
                    stack.Push(sub);
                }
            }

            int GetPathDepth(string path) => path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).Length;

            // Xóa từ dưới lên trên (thư mục có cấp độ sâu nhất xóa trước)
            foreach (var dir in allDirs.OrderByDescending(d => GetPathDepth(d)).ThenByDescending(d => d.Length))
            {
                if (string.Equals(dir, rootFolder, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir, false);
                    }
                }
                catch {}
            }
        }
        catch {}
    }
}
