using System;
using System.Collections.Concurrent;
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

    public Task<int> SplitByChapterCountAsync(string rootFolder, int groupSize, string folderType, bool mergeRemainder, CancellationToken ct)
        => SplitByChapterCountAsync(rootFolder, groupSize, folderType, mergeRemainder, 20, ct);

    public async Task<int> SplitByChapterCountAsync(string rootFolder, int groupSize, string folderType, bool mergeRemainder, int maxDegree, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder) || groupSize <= 0)
        {
            return 0;
        }

        await _folderLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int effectiveDegree = Math.Clamp(maxDegree, 1, 128);
            LogEmitted?.Invoke("INFO", $"[Tách Folder] Bắt đầu quét thư mục tại: {rootFolder} | Cỡ nhóm: {groupSize} chap | Kiểu: {folderType} | Xử lý song song: {effectiveDegree} folder");
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

            LogEmitted?.Invoke("INFO", $"[Tách Folder] Đã tìm thấy {totalChapters} chapter trong {bookGroups.Count} bộ truyện. Bắt đầu phân chia siêu tốc ({effectiveDegree} folders cùng lúc)...");

            var allMoveTasks = new List<(ChapterFolderItem chapter, string destPath)>();

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
                        string destPath = Path.Combine(groupFolderPath, chapter.FolderName);
                        if (!string.Equals(chapter.SourcePath, destPath, StringComparison.OrdinalIgnoreCase))
                        {
                            allMoveTasks.Add((chapter, destPath));
                        }
                    }
                }
            }

            await Parallel.ForEachAsync(allMoveTasks, new ParallelOptions { MaxDegreeOfParallelism = effectiveDegree, CancellationToken = ct }, (task, token) =>
            {
                try
                {
                    SafeMoveDirectory(task.chapter.SourcePath, task.destPath);
                    int done = Interlocked.Increment(ref splitCount);
                    long now = Environment.TickCount64;
                    if (done == totalChapters || now - Volatile.Read(ref lastProgressTicks) >= 120)
                    {
                        Interlocked.Exchange(ref lastProgressTicks, now);
                        double pct = (double)done / totalChapters * 100.0;
                        ProgressChanged?.Invoke(done, totalChapters, $"Đã tách {done}/{totalChapters} chapter ({pct:F0}%)...");
                    }
                }
                catch (Exception ex)
                {
                    LogEmitted?.Invoke("ERROR", $"[Tách Lỗi] Không thể chuyển '{task.chapter.SourcePath}': {ex.Message}");
                }
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

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

    public Task<int> MergeByChapterCountAsync(string rootFolder, CancellationToken ct)
        => MergeByChapterCountAsync(rootFolder, 20, ct);

    public async Task<int> MergeByChapterCountAsync(string rootFolder, int maxDegree, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
        {
            return 0;
        }

        await _folderLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int effectiveDegree = Math.Clamp(maxDegree, 1, 128);
            LogEmitted?.Invoke("INFO", $"[Gộp Chapter] Bắt đầu quét chapter về thư mục truyện gốc: {rootFolder} | Xử lý song song: {effectiveDegree} folder");
            ProgressChanged?.Invoke(0, 1, "Đang quét chapter cần gộp...");

            var allCollected = CollectChapterFolders(rootFolder);
            var itemsToMerge = allCollected
                .Where(item =>
                {
                    string targetBook = !string.IsNullOrWhiteSpace(item.BookFolderPath) ? item.BookFolderPath : rootFolder;
                    string expectedDest = Path.Combine(targetBook, item.FolderName);
                    return !string.Equals(item.SourcePath, expectedDest, StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(item => item.Depth)
                .ThenBy(item => item.ChapterNumber)
                .ThenBy(item => item.FolderName, NaturalSortComparer.Instance)
                .ToList();

            int mergedCount = 0;
            int total = itemsToMerge.Count;
            long lastProgressTicks = 0;

            if (total == 0)
            {
                LogEmitted?.Invoke("WARN", $"[Gộp Chapter] Không tìm thấy chapter nào nằm trong thư mục con cần gộp tại: {rootFolder}");
                ProgressChanged?.Invoke(0, 0, "Không tìm thấy chapter nào cần gộp.");
                return 0;
            }

            LogEmitted?.Invoke("INFO", $"[Gộp Chapter] Tìm thấy {total} chapter cần gộp về thư mục truyện gốc. Bắt đầu di chuyển siêu tốc ({effectiveDegree} folders cùng lúc)...");

            var groupsToClean = new ConcurrentBag<string>();

            await Parallel.ForEachAsync(itemsToMerge, new ParallelOptions { MaxDegreeOfParallelism = effectiveDegree, CancellationToken = ct }, (chapter, token) =>
            {
                if (string.IsNullOrWhiteSpace(chapter.SourcePath) || string.IsNullOrWhiteSpace(chapter.FolderName)) return ValueTask.CompletedTask;

                string targetBookFolder = !string.IsNullOrWhiteSpace(chapter.BookFolderPath) ? chapter.BookFolderPath : rootFolder;
                string destPath = Path.Combine(targetBookFolder, chapter.FolderName);

                if (string.Equals(chapter.SourcePath, destPath, StringComparison.OrdinalIgnoreCase)) return ValueTask.CompletedTask;

                string? sourceParent = Path.GetDirectoryName(chapter.SourcePath);
                if (!string.IsNullOrEmpty(sourceParent) && !string.Equals(sourceParent, targetBookFolder, StringComparison.OrdinalIgnoreCase))
                {
                    groupsToClean.Add(sourceParent);
                }

                try
                {
                    SafeMoveDirectory(chapter.SourcePath, destPath);
                    int done = Interlocked.Increment(ref mergedCount);
                    long now = Environment.TickCount64;
                    if (done == total || now - Volatile.Read(ref lastProgressTicks) >= 120)
                    {
                        Interlocked.Exchange(ref lastProgressTicks, now);
                        double pct = (double)done / total * 100.0;
                        ProgressChanged?.Invoke(done, total, $"Đã gộp {done}/{total} chapter ({pct:F0}%)...");
                    }
                }
                catch (Exception ex)
                {
                    LogEmitted?.Invoke("ERROR", $"[Gộp Lỗi] Lỗi gộp '{chapter.SourcePath}': {ex.Message}");
                }
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            // Dọn dẹp tất cả các group folders cũ tức thì
            foreach (var g in groupsToClean.Distinct())
            {
                TryCleanEmptyOrJunkDirectory(g);
            }

            DeleteEmptyDirectoriesBottomUp(rootFolder);
            ProgressChanged?.Invoke(total, total, $"Hoàn tất: Đã gộp {mergedCount}/{total} chapter về gốc.");
            LogEmitted?.Invoke("SUCCESS", $"[Gộp Chapter] Hoàn tất gộp {mergedCount} chapter folders về thư mục truyện gốc.");
            return mergedCount;
        }
        finally
        {
            _folderLock.Release();
        }
    }

    public Task<int> SplitByAlphabetAsync(string rootFolder, List<string> rawRanges, bool ignoreLeadingTags, CancellationToken ct)
        => SplitByAlphabetAsync(rootFolder, rawRanges, ignoreLeadingTags, 20, ct);

    public async Task<int> SplitByAlphabetAsync(string rootFolder, List<string> rawRanges, bool ignoreLeadingTags, int maxDegree, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder) || rawRanges == null || rawRanges.Count == 0)
        {
            return 0;
        }

        await _folderLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int effectiveDegree = Math.Clamp(maxDegree, 1, 128);
            var parsedRanges = new List<AlphabetRangeItem>();
            foreach (var raw in rawRanges)
            {
                if (AlphabetRangeItem.TryParse(raw, out var item) && item != null)
                {
                    parsedRanges.Add(item);
                }
            }

            if (parsedRanges.Count == 0) return 0;

            LogEmitted?.Invoke("INFO", $"[Tách Alphabet] Bắt đầu phân loại theo chữ cái tại: {rootFolder} | Dải: {string.Join(", ", parsedRanges.Select(r => r.DisplayName))} | Xử lý song song: {effectiveDegree} folder");
            ProgressChanged?.Invoke(0, 1, "Đang quét danh sách thư mục...");

            var excludedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "[Number]", "Number", "[Numbers]", "Numbers", "[0-9]", "0-9", "#", "[#]",
                "other language", "Other Language", "other languages", "Other Languages", "[Other Latin]",
                "[Other]", "Other"
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

            LogEmitted?.Invoke("INFO", $"[Tách Alphabet] Tìm thấy {total} thư mục cần phân loại. Bắt đầu di chuyển siêu tốc ({effectiveDegree} folders cùng lúc)...");

            var moveList = new List<(DirectoryInfo subDir, string destPath, string destParentDir)>();
            foreach (var subDir in candidateDirs)
            {
                string categoryName = DetermineAlphabetCategory(subDir.Name, parsedRanges, ignoreLeadingTags);
                string destParentDir = Path.Combine(rootFolder, categoryName);
                string destPath = Path.Combine(destParentDir, subDir.Name);

                if (!string.Equals(subDir.FullName, destPath, StringComparison.OrdinalIgnoreCase))
                {
                    try { if (!Directory.Exists(destParentDir)) Directory.CreateDirectory(destParentDir); } catch { }
                    moveList.Add((subDir, destPath, destParentDir));
                }
            }

            await Parallel.ForEachAsync(moveList, new ParallelOptions { MaxDegreeOfParallelism = effectiveDegree, CancellationToken = ct }, (task, token) =>
            {
                try
                {
                    SafeMoveDirectory(task.subDir.FullName, task.destPath);
                    int done = Interlocked.Increment(ref splitCount);
                    long now = Environment.TickCount64;
                    if (done == total || now - Volatile.Read(ref lastProgressTicks) >= 120)
                    {
                        Interlocked.Exchange(ref lastProgressTicks, now);
                        double pct = (double)done / total * 100.0;
                        ProgressChanged?.Invoke(done, total, $"Đã phân loại {done}/{total} thư mục ({pct:F0}%)...");
                    }
                }
                catch (Exception ex)
                {
                    LogEmitted?.Invoke("ERROR", $"[Tách Alphabet Lỗi] Không thể chuyển '{task.subDir.FullName}': {ex.Message}");
                }
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

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

    public Task<int> MergeByAlphabetAsync(string rootFolder, List<string> rawRanges, CancellationToken ct)
        => MergeByAlphabetAsync(rootFolder, rawRanges, 20, ct);

    public async Task<int> MergeByAlphabetAsync(string rootFolder, List<string> rawRanges, int maxDegree, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
        {
            return 0;
        }

        await _folderLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int effectiveDegree = Math.Clamp(maxDegree, 1, 128);
            LogEmitted?.Invoke("INFO", $"[Gộp Alphabet] Bắt đầu quét thư mục chữ cái về gốc: {rootFolder} | Xử lý song song: {effectiveDegree} folder");
            ProgressChanged?.Invoke(0, 1, "Đang quét thư mục chữ cái...");

            var categoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "[Number]", "Number", "[Numbers]", "Numbers", "[0-9]", "0-9", "#", "[#]",
                "other language", "Other Language", "other languages", "Other Languages", "[Other Latin]",
                "[Other]", "Other"
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
            var catDirsToClean = new List<DirectoryInfo>();

            foreach (var catDir in subDirs)
            {
                if (catDir.Attributes.HasFlag(FileAttributes.Hidden) ||
                    catDir.Attributes.HasFlag(FileAttributes.System) ||
                    catDir.Name.StartsWith(".") ||
                    catDir.Name.EndsWith("-tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool isAlphabetCategory = categoryNames.Contains(catDir.Name) || 
                                          AlphabetRangeItem.TryParse(catDir.Name, out _) ||
                                          (catDir.Name.Length == 1 && ((catDir.Name[0] >= 'A' && catDir.Name[0] <= 'Z') || (catDir.Name[0] >= 'a' && catDir.Name[0] <= 'z')));
                
                if (!isAlphabetCategory) continue;

                catDirsToClean.Add(catDir);
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
                foreach (var dir in catDirsToClean)
                {
                    TryCleanEmptyOrJunkDirectory(dir.FullName);
                }
                LogEmitted?.Invoke("WARN", $"[Gộp Alphabet] Không tìm thấy bộ truyện nào trong các thư mục chữ cái tại: {rootFolder}");
                ProgressChanged?.Invoke(0, 0, "Không có thư mục nào cần gộp.");
                return 0;
            }

            LogEmitted?.Invoke("INFO", $"[Gộp Alphabet] Tìm thấy {total} bộ truyện cần gộp về gốc. Bắt đầu di chuyển siêu tốc ({effectiveDegree} folders cùng lúc)...");

            await Parallel.ForEachAsync(allItemsToMerge, new ParallelOptions { MaxDegreeOfParallelism = effectiveDegree, CancellationToken = ct }, (item, token) =>
            {
                string destPath = Path.Combine(rootFolder, item.comicDir.Name);
                if (!string.Equals(item.comicDir.FullName, destPath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        SafeMoveDirectory(item.comicDir.FullName, destPath);
                        int done = Interlocked.Increment(ref mergedCount);
                        long now = Environment.TickCount64;
                        if (done == total || now - Volatile.Read(ref lastProgressTicks) >= 120)
                        {
                            Interlocked.Exchange(ref lastProgressTicks, now);
                            double pct = (double)done / total * 100.0;
                            ProgressChanged?.Invoke(done, total, $"Đã gộp {done}/{total} bộ truyện ({pct:F0}%)...");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogEmitted?.Invoke("ERROR", $"[Gộp Alphabet Lỗi] Lỗi gộp '{item.comicDir.FullName}': {ex.Message}");
                    }
                }
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            foreach (var dir in catDirsToClean)
            {
                TryCleanEmptyOrJunkDirectory(dir.FullName);
            }

            DeleteEmptyDirectoriesBottomUp(rootFolder);
            ProgressChanged?.Invoke(total, total, $"Hoàn tất: Đã gộp {mergedCount}/{total} bộ truyện về gốc.");
            LogEmitted?.Invoke("SUCCESS", $"[Gộp Alphabet] Hoàn tất gộp {mergedCount} bộ truyện về {rootFolder}");
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

                bool isBucket = IsGroupBucketFolder(folderName);
                if (!isBucket && ChapterFilter.TryParseChapterNumber(folderName, out double chapNum) && DirectoryContainsImages(folder))
                {
                    string parent = Path.GetDirectoryName(folder) ?? rootFolder;
                    string parentName = Path.GetFileName(parent);
                    string bookPath = parent;

                    if (IsGroupBucketFolder(parentName))
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

    private static bool IsGroupBucketFolder(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return Regex.IsMatch(name, @"(^|[-_\s])(chap|chapter|vol|volume|tap|tập)\s*\d+[\s\-_–—]+\d+$", RegexOptions.IgnoreCase);
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
    /// Dọn dẹp thư mục nếu rỗng hoặc chỉ chứa file rác/hệ thống (.nomedia, Thumbs.db, desktop.ini...).
    /// </summary>
    private static void TryCleanEmptyOrJunkDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            var entries = Directory.GetFileSystemEntries(path);
            if (entries.Length == 0)
            {
                Directory.Delete(path, false);
                return;
            }

            var junkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".nomedia", "Thumbs.db", "desktop.ini", ".DS_Store", "ehthumbs.db", "ehthumbs_vista.db"
            };

            bool allJunk = entries.All(e => junkNames.Contains(Path.GetFileName(e)));
            if (allJunk)
            {
                foreach (var e in entries)
                {
                    try
                    {
                        if (File.Exists(e)) File.Delete(e);
                        else if (Directory.Exists(e)) Directory.Delete(e, true);
                    }
                    catch { }
                }
                Directory.Delete(path, false);
            }
        }
        catch { }
    }

    /// <summary>
    /// Di chuyển thư mục an toàn tuyệt đối trên mọi hệ điều hành (kể cả Android Scoped Storage / Cross-mount).
    /// </summary>
    private static void SafeMoveDirectory(string source, string dest)
    {
        if (string.Equals(source, dest, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(source)) return;

        // Tránh đệ quy vô tận nếu dest nằm bên trong source hoặc ngược lại
        string normSource = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normDest = dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (normDest.StartsWith(normSource, StringComparison.OrdinalIgnoreCase) || normSource.StartsWith(normDest, StringComparison.OrdinalIgnoreCase)) return;

        // 1. Luôn đảm bảo thư mục cha của dest phải tồn tại TRƯỚC TIÊN
        string? parentDest = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(parentDest) && !Directory.Exists(parentDest))
        {
            try { Directory.CreateDirectory(parentDest); } catch { }
        }

        // 2. Nếu dest đã tồn tại, kiểm tra xem có rỗng hoặc chỉ chứa file rác (Thumbs.db, .nomedia...) không
        TryCleanEmptyOrJunkDirectory(dest);

        // 3. Thử Directory.Move nhanh trước nếu thư mục đích chưa tồn tại
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

        // 4. Fallback sang Merge đệ quy an toàn cho từng file
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

    private static void MergeDirectoryContents(string source, string dest, int depth = 0)
    {
        if (depth > 10) return; // Bảo vệ chống đệ quy lặp vô tận (StackOverflow)
        if (string.Equals(source, dest, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(source)) return;

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
                MergeDirectoryContents(dir, Path.Combine(dest, Path.GetFileName(dir)), depth + 1);
            }
            catch {}
        }

        TryCleanEmptyOrJunkDirectory(source);
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
                TryCleanEmptyOrJunkDirectory(dir);
            }
        }
        catch {}
    }

    /// <summary>
    /// Item đại diện cho một thư mục chapter phục vụ đổi tên đa tầng sâu.
    /// </summary>
    public class RenameChapterItem
    {
        public string SourcePath { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public string BookFolderPath { get; set; } = string.Empty;
        public string BookName { get; set; } = string.Empty;
        public int Depth { get; set; }
    }

    /// <summary>
    /// Chuẩn hóa chuỗi văn bản thành slug ASCII chữ thường không dấu (URL/FileSystem friendly).
    /// </summary>
    public static string ToSlug(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        string text = input.Replace("đ", "d").Replace("Đ", "d");
        string normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (char c in normalized)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(c);
            if (uc != UnicodeCategory.NonSpacingMark)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
                else if (c == ' ' || c == '-' || c == '_' || c == '.')
                {
                    sb.Append('-');
                }
            }
        }

        string result = sb.ToString();
        result = Regex.Replace(result, @"-+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(result) ? "chapter" : result;
    }

    /// <summary>
    /// Bóc tách phần tên chapter thuần túy bằng cách loại bỏ book name prefix (nếu đã có).
    /// </summary>
    public static string ExtractPureChapterName(string currentName, string bookName)
    {
        if (string.IsNullOrWhiteSpace(currentName)) return string.Empty;
        if (string.IsNullOrWhiteSpace(bookName)) return currentName;

        string trimmedCurrent = currentName.Trim();
        string trimmedBook = bookName.Trim();

        var prefixes = new List<string>
        {
            trimmedBook + " - ",
            trimmedBook + "-",
            trimmedBook + "_",
            trimmedBook + " ",
            ToSlug(trimmedBook) + "-"
        };

        foreach (var prefix in prefixes)
        {
            if (trimmedCurrent.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string pure = trimmedCurrent.Substring(prefix.Length).Trim();
                if (!string.IsNullOrEmpty(pure))
                {
                    return pure;
                }
            }
        }

        var match = Regex.Match(trimmedCurrent, @"^.+?[-_–—]\s*(?<chap>(?:chap|chapter|chuong|chương|vol|volume|tap|tập|c)\s*\d+.*)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            string extracted = match.Groups["chap"].Value.Trim();
            if (!string.IsNullOrEmpty(extracted))
            {
                return extracted;
            }
        }

        return trimmedCurrent;
    }

    /// <summary>
    /// Kiểm tra xem một thư mục có chứa thư mục con nào cũng chứa ảnh hoặc chapter không.
    /// Giúp phân biệt chính xác Thư mục Bộ Truyện / Gom Nhóm với Thư mục Chapter lá.
    /// </summary>
    private static bool HasSubdirectoriesWithImages(string folder)
    {
        try
        {
            var subDirs = Directory.GetDirectories(folder);
            if (subDirs.Length == 0) return false;

            foreach (var sub in subDirs)
            {
                string subName = Path.GetFileName(sub);
                if (subName.StartsWith(".") || subName.EndsWith("-tmp", StringComparison.OrdinalIgnoreCase)) continue;
                if (DirectoryContainsImages(sub)) return true;
                if (ChapterFilter.TryParseChapterNumber(subName, out _)) return true;

                // Kiểm tra 1 tầng con nông tiếp theo để bảo vệ an toàn
                try
                {
                    if (Directory.EnumerateDirectories(sub).Any(child => DirectoryContainsImages(child)))
                    {
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Xác định chính xác đường dẫn và tên của bộ truyện cha cho một chapter folder trong cây thư mục đa tầng sâu.
    /// </summary>
    private static (string bookFolderPath, string bookName) ResolveBookInfo(string chapterFolderPath, string rootFolder)
    {
        string? current = Path.GetDirectoryName(chapterFolderPath);
        if (string.IsNullOrEmpty(current))
        {
            string rootName = Path.GetFileName(rootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return (rootFolder, string.IsNullOrWhiteSpace(rootName) ? "Comic" : rootName);
        }

        string normRoot = rootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normCurrent = current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Nếu chapter nằm ngay trong rootFolder
        if (string.Equals(normCurrent, normRoot, StringComparison.OrdinalIgnoreCase))
        {
            string rootName = Path.GetFileName(normRoot);
            return (rootFolder, string.IsNullOrWhiteSpace(rootName) ? "Comic" : rootName);
        }

        // Đi ngược lên bỏ qua các thư mục gom trung gian (như "chap 0001-0200")
        while (!string.IsNullOrEmpty(current) &&
               !string.Equals(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normRoot, StringComparison.OrdinalIgnoreCase))
        {
            string dirName = Path.GetFileName(current);
            if (IsGroupBucketFolder(dirName))
            {
                string? parent = Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(parent))
                {
                    current = parent;
                    continue;
                }
            }
            break;
        }

        string finalBookPath = current ?? rootFolder;
        string finalBookName = Path.GetFileName(finalBookPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(finalBookName))
        {
            finalBookName = Path.GetFileName(normRoot);
        }
        if (string.IsNullOrWhiteSpace(finalBookName))
        {
            finalBookName = "Comic";
        }

        return (finalBookPath, finalBookName);
    }

    /// <summary>
    /// Thu thập danh sách các thư mục chapter lá trong cây thư mục đa tầng sâu.
    /// Đảm bảo không nhận nhầm thư mục bộ truyện (có chứa ảnh bìa) thành chapter.
    /// </summary>
    private List<RenameChapterItem> CollectChapterFoldersForRename(string rootFolder)
    {
        var items = new List<RenameChapterItem>();
        if (!Directory.Exists(rootFolder)) return items;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(rootFolder);
        visited.Add(rootFolder);

        while (stack.Count > 0)
        {
            string current = stack.Pop();
            string[] subDirs;
            try { subDirs = Directory.GetDirectories(current); } catch { continue; }

            var sortedSubDirs = subDirs.OrderBy(d => Path.GetFileName(d), NaturalSortComparer.Instance);

            foreach (var folder in sortedSubDirs)
            {
                if (!visited.Add(folder)) continue;

                string folderName = Path.GetFileName(folder);
                if (folderName.StartsWith(".") || folderName.EndsWith("-tmp", StringComparison.OrdinalIgnoreCase)) continue;

                bool isBucket = IsGroupBucketFolder(folderName);
                bool hasImages = DirectoryContainsImages(folder);
                bool hasSubWithImages = HasSubdirectoriesWithImages(folder);
                bool isChapterName = ChapterFilter.TryParseChapterNumber(folderName, out _) ||
                                     Regex.IsMatch(folderName, @"(^|[-_\s])(chap|chapter|chuong|chương|vol|volume|c|tap|tập)[\s\-_]*\d+", RegexOptions.IgnoreCase);

                // Chỉ coi là Chapter Folder khi nó là thư mục lá chứa ảnh và KHÔNG chứa thư mục con có ảnh
                if (!isBucket && !hasSubWithImages && (hasImages || isChapterName))
                {
                    var (bookPath, bookName) = ResolveBookInfo(folder, rootFolder);

                    items.Add(new RenameChapterItem
                    {
                        SourcePath = folder,
                        FolderName = folderName,
                        BookFolderPath = bookPath,
                        BookName = bookName,
                        Depth = folder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).Length
                    });
                }
                else
                {
                    // Nếu là thư mục cha (Book folder, Bucket folder, hoặc Category) thì đi sâu vào các con
                    stack.Push(folder);
                }
            }
        }

        return items;
    }

    /// <summary>
    /// Đổi tên thư mục an toàn tuyệt đối trên Windows, Linux và Android.
    /// Xử lý hoàn hảo trường hợp chỉ khác hoa/thường trên Windows (case-insensitive) và fallback Merge.
    /// </summary>
    private static bool SafeRenameDirectory(string currentPath, string targetPath)
    {
        if (string.Equals(currentPath, targetPath, StringComparison.Ordinal))
        {
            return true;
        }

        if (!Directory.Exists(currentPath)) return false;

        string? parentDir = Path.GetDirectoryName(currentPath);
        if (string.IsNullOrEmpty(parentDir)) return false;

        try
        {
            // Trường hợp chỉ khác chữ hoa/thường trên Windows filesystem (case-insensitive)
            if (string.Equals(currentPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                string tempPath = Path.Combine(parentDir, $"{Path.GetFileName(currentPath)}_rnmtmp_{Guid.NewGuid():N}");
                bool tempMoved = false;
                for (int retry = 0; retry < 3; retry++)
                {
                    try
                    {
                        Directory.Move(currentPath, tempPath);
                        tempMoved = true;
                        break;
                    }
                    catch
                    {
                        Thread.Sleep(30);
                    }
                }

                if (tempMoved)
                {
                    SafeMoveDirectory(tempPath, targetPath);
                    return Directory.Exists(targetPath);
                }
                else
                {
                    SafeMoveDirectory(currentPath, targetPath);
                    return Directory.Exists(targetPath);
                }
            }
            else
            {
                // Thử rename nhanh với retry 3 lần trước khi fallback sang SafeMoveDirectory
                if (!Directory.Exists(targetPath))
                {
                    for (int retry = 0; retry < 3; retry++)
                    {
                        try
                        {
                            Directory.Move(currentPath, targetPath);
                            return true;
                        }
                        catch
                        {
                            Thread.Sleep(30);
                        }
                    }
                }

                SafeMoveDirectory(currentPath, targetPath);
                return Directory.Exists(targetPath);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Đổi tên thư mục chapter đa tầng sâu theo 2 chế độ:
    /// - "Single comic": {book-name-slug}\{chapter-slug} (lược bỏ book name prefix nếu có)
    /// - "Multi-comic": {book-name-slug}\{book-name-slug}-{chapter-slug} (ghép book name prefix vào tên chapter)
    /// Hỗ trợ Slugify ASCII và điều tiết tốc độ chống sập UI/JNI.
    /// </summary>
    public async Task<int> RenameChapterFoldersAsync(
        string rootFolder,
        string renameMode,
        bool isSlugify,
        int maxDegree,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
        {
            return 0;
        }

        await _folderLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int effectiveDegree = Math.Clamp(maxDegree, 1, 128);
            if (OperatingSystem.IsAndroid())
            {
                effectiveDegree = Math.Min(effectiveDegree, 4); // Giới hạn luồng an toàn cho Android SAF
            }

            LogEmitted?.Invoke("INFO", $"[Đổi Tên Thư Mục] Bắt đầu quét thư mục tại: {rootFolder} | Chế độ: {renameMode} | Slugify: {isSlugify} | Song song: {effectiveDegree} folder");
            ProgressChanged?.Invoke(0, 1, "Đang quét danh sách chapter...");

            var chapterItems = CollectChapterFoldersForRename(rootFolder);
            int total = chapterItems.Count;

            if (total == 0)
            {
                LogEmitted?.Invoke("WARN", $"[Đổi Tên Thư Mục] Không tìm thấy thư mục chapter chứa ảnh nào tại: {rootFolder}");
                ProgressChanged?.Invoke(1, 1, "Không tìm thấy thư mục chapter nào.");
                return 0;
            }

            LogEmitted?.Invoke("INFO", $"[Đổi Tên Thư Mục] Tìm thấy {total} thư mục chapter. Bắt đầu xử lý...");
            int processed = 0;
            int successCount = 0;
            int skippedCount = 0;
            int errorCount = 0;
            long lastProgressTicks = 0;

            bool isMultiComic = renameMode.Contains("Multi", StringComparison.OrdinalIgnoreCase);

            // 1. Đổi tên toàn bộ các thư mục chapter lá trước
            await Parallel.ForEachAsync(chapterItems, new ParallelOptions
            {
                MaxDegreeOfParallelism = effectiveDegree,
                CancellationToken = ct
            }, (item, token) =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string currentPath = item.SourcePath;
                    if (!Directory.Exists(currentPath))
                    {
                        Interlocked.Increment(ref skippedCount);
                        return ValueTask.CompletedTask;
                    }

                    string currentName = item.FolderName;
                    string bookName = item.BookName;

                    string pureChapter = ExtractPureChapterName(currentName, bookName);

                    string newName;
                    if (isMultiComic)
                    {
                        if (isSlugify)
                        {
                            string bSlug = ToSlug(bookName);
                            string cSlug = ToSlug(pureChapter);
                            newName = $"{bSlug}-{cSlug}";
                        }
                        else
                        {
                            string safeBook = DownloadEngineService.MakeSafeFilename(bookName, 80);
                            string safeChap = DownloadEngineService.MakeSafeFilename(pureChapter, 80);
                            newName = $"{safeBook}-{safeChap}";
                        }
                    }
                    else
                    {
                        if (isSlugify)
                        {
                            newName = ToSlug(pureChapter);
                        }
                        else
                        {
                            newName = DownloadEngineService.MakeSafeFilename(pureChapter, 120);
                        }
                    }

                    if (string.Equals(currentName, newName, StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref skippedCount);
                        return ValueTask.CompletedTask;
                    }

                    string? parentDir = Path.GetDirectoryName(currentPath);
                    if (string.IsNullOrEmpty(parentDir))
                    {
                        Interlocked.Increment(ref skippedCount);
                        return ValueTask.CompletedTask;
                    }

                    string targetPath = Path.Combine(parentDir, newName);

                    bool ok = SafeRenameDirectory(currentPath, targetPath);
                    if (ok)
                    {
                        Interlocked.Increment(ref successCount);
                        LogEmitted?.Invoke("SUCCESS", $"[Đổi tên] {currentName} -> {newName}");
                    }
                    else
                    {
                        Interlocked.Increment(ref errorCount);
                        LogEmitted?.Invoke("ERROR", $"[Đổi tên thất bại] Không thể đổi tên {currentName} -> {newName}");
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref errorCount);
                    LogEmitted?.Invoke("ERROR", $"[Đổi tên lỗi] {item.FolderName}: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        int done = Interlocked.Increment(ref processed);
                        long now = Environment.TickCount64;
                        if (done == total || now - Volatile.Read(ref lastProgressTicks) >= 150)
                        {
                            Volatile.Write(ref lastProgressTicks, now);
                            ProgressChanged?.Invoke(done, total, $"Đang đổi tên: {done}/{total} ({Volatile.Read(ref successCount)} thành công, {Volatile.Read(ref skippedCount)} bỏ qua, {Volatile.Read(ref errorCount)} lỗi)...");
                        }
                    }
                    catch
                    {
                        // Nuốt lỗi progress notification không để ném ra Parallel.ForEachAsync
                    }
                }

                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            // 2. Nếu tùy chọn Slugify bật, chuẩn hóa luôn tên của các thư mục bộ truyện (Book Folders)
            if (isSlugify)
            {
                var bookPaths = chapterItems
                    .Select(x => x.BookFolderPath)
                    .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                string normRoot = rootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                foreach (var bookPath in bookPaths)
                {
                    if (ct.IsCancellationRequested) break;
                    string normBook = bookPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(normBook, normRoot, StringComparison.OrdinalIgnoreCase)) continue;

                    string curBookName = Path.GetFileName(normBook);
                    string targetBookSlug = ToSlug(curBookName);
                    if (string.Equals(curBookName, targetBookSlug, StringComparison.Ordinal)) continue;

                    string? parent = Path.GetDirectoryName(normBook);
                    if (string.IsNullOrEmpty(parent)) continue;

                    string targetBookPath = Path.Combine(parent, targetBookSlug);
                    if (SafeRenameDirectory(normBook, targetBookPath))
                    {
                        LogEmitted?.Invoke("SUCCESS", $"[Đổi tên bộ truyện] {curBookName} -> {targetBookSlug}");
                    }
                }
            }

            LogEmitted?.Invoke("INFO", $"[Đổi Tên Thư Mục] Hoàn tất! Đã đổi: {successCount}, Giữ nguyên/Bỏ qua: {skippedCount}, Lỗi: {errorCount}.");
            ProgressChanged?.Invoke(total, total, $"Hoàn tất: {successCount} đổi tên, {skippedCount} bỏ qua.");
            return successCount;
        }
        finally
        {
            _folderLock.Release();
        }
    }
}
