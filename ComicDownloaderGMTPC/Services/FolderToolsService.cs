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
            LogEmitted?.Invoke("INFO", $"[Split Chapters] Bắt đầu chia thư mục tại: {rootFolder} | Cỡ nhóm: {groupSize} chap | Kiểu: {folderType}");
            
            var chapterItems = CollectChapterFolders(rootFolder);
            var bookGroups = chapterItems.GroupBy(x => x.BookFolderPath, StringComparer.OrdinalIgnoreCase).ToList();

            int splitCount = 0;
            int totalBooks = bookGroups.Count;
            int processedBooks = 0;

            foreach (var bookGroup in bookGroups)
            {
                if (ct.IsCancellationRequested) break;

                string bookFolder = bookGroup.Key;
                var chapters = bookGroup
                    .OrderBy(item => item.ChapterNumber)
                    .ThenBy(item => item.FolderName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (chapters.Count < 2)
                {
                    processedBooks++;
                    continue;
                }

                var rawBuckets = chapters
                    .GroupBy(item => (Math.Max(1, item.ChapterNumber) - 1) / groupSize)
                    .OrderBy(g => g.Key)
                    .Select(g => new
                    {
                        Key = g.Key,
                        Items = g.OrderBy(item => item.ChapterNumber)
                                 .ThenBy(item => item.FolderName, StringComparer.OrdinalIgnoreCase)
                                 .ToList()
                    })
                    .Where(b => b.Items.Count > 0)
                    .ToList();

                if (rawBuckets.Count == 0)
                {
                    processedBooks++;
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

                    foreach (var chapter in bucket)
                    {
                        if (ct.IsCancellationRequested) break;

                        string destPath = Path.Combine(groupFolderPath, chapter.FolderName);
                        if (string.Equals(chapter.SourcePath, destPath, StringComparison.OrdinalIgnoreCase)) continue;

                        try
                        {
                            Directory.CreateDirectory(groupFolderPath);
                            if (!Directory.Exists(destPath))
                            {
                                Directory.Move(chapter.SourcePath, destPath);
                            }
                            else
                            {
                                MergeDirectoryContents(chapter.SourcePath, destPath);
                            }
                            splitCount++;
                            LogEmitted?.Invoke("SUCCESS", $"[Split] Đã chuyển '{chapter.FolderName}' -> '{groupFolderName}'");
                        }
                        catch (Exception ex)
                        {
                            LogEmitted?.Invoke("ERROR", $"[Split Error] Lỗi chuyển '{chapter.SourcePath}': {ex.Message}");
                        }
                    }
                }

                DeleteEmptyDirectoriesBottomUp(bookFolder);
                processedBooks++;
                ProgressChanged?.Invoke(processedBooks, Math.Max(1, totalBooks), $"Đã chia {splitCount} chapter...");
            }

            LogEmitted?.Invoke("SUCCESS", $"[Split Chapters] Hoàn tất chia {splitCount} chapter folders.");
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
            LogEmitted?.Invoke("INFO", $"[Merge Chapters] Bắt đầu gộp chapter về thư mục gốc: {rootFolder}");

            var items = CollectChapterFolders(rootFolder)
                .Where(item => !string.Equals(item.SourcePath, rootFolder, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Depth)
                .ThenBy(item => item.ChapterNumber)
                .ToList();

            int mergedCount = 0;
            int total = Math.Max(1, items.Count);

            foreach (var chapter in items)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(chapter.SourcePath) || string.IsNullOrWhiteSpace(chapter.FolderName)) continue;

                string destPath = Path.Combine(rootFolder, chapter.FolderName);
                if (string.Equals(chapter.SourcePath, destPath, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    if (!Directory.Exists(destPath))
                    {
                        Directory.Move(chapter.SourcePath, destPath);
                    }
                    else
                    {
                        MergeDirectoryContents(chapter.SourcePath, destPath);
                    }

                    mergedCount++;
                    LogEmitted?.Invoke("SUCCESS", $"[Merge] Đã gộp '{chapter.FolderName}' -> thư mục gốc");
                }
                catch (Exception ex)
                {
                    LogEmitted?.Invoke("ERROR", $"[Merge Error] Lỗi gộp '{chapter.SourcePath}': {ex.Message}");
                }

                ProgressChanged?.Invoke(mergedCount, total, $"Đã gộp {mergedCount}/{total} chapters...");
            }

            DeleteEmptyDirectoriesBottomUp(rootFolder);
            LogEmitted?.Invoke("SUCCESS", $"[Merge Chapters] Hoàn tất gộp {mergedCount} chapter folders về gốc.");
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

            LogEmitted?.Invoke("INFO", $"[Split Alphabet] Bắt đầu chia thư mục theo chữ cái: {rootFolder} | Ranges: {string.Join(", ", parsedRanges.Select(r => r.DisplayName))}");

            var excludedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "[Number]", "Number", "[Numbers]", "Numbers",
                "other language", "Other Language", "other languages", "Other Languages"
            };
            foreach (var r in parsedRanges) excludedNames.Add(r.DisplayName);

            var dirInfo = new DirectoryInfo(rootFolder);
            var subDirs = dirInfo.GetDirectories();
            int splitCount = 0;
            int total = Math.Max(1, subDirs.Length);

            for (int i = 0; i < subDirs.Length; i++)
            {
                if (ct.IsCancellationRequested) break;
                var subDir = subDirs[i];

                if (subDir.Attributes.HasFlag(FileAttributes.Hidden) ||
                    subDir.Attributes.HasFlag(FileAttributes.System) ||
                    subDir.Name.StartsWith(".") ||
                    subDir.Name.EndsWith("-tmp", StringComparison.OrdinalIgnoreCase) ||
                    excludedNames.Contains(subDir.Name))
                {
                    continue;
                }

                string categoryName = DetermineAlphabetCategory(subDir.Name, parsedRanges, ignoreLeadingTags);
                string destParentDir = Path.Combine(rootFolder, categoryName);
                string destPath = Path.Combine(destParentDir, subDir.Name);

                if (string.Equals(subDir.FullName, destPath, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    Directory.CreateDirectory(destParentDir);
                    if (!Directory.Exists(destPath))
                    {
                        Directory.Move(subDir.FullName, destPath);
                    }
                    else
                    {
                        MergeDirectoryContents(subDir.FullName, destPath);
                    }

                    splitCount++;
                    LogEmitted?.Invoke("SUCCESS", $"[Split Alphabet] '{subDir.Name}' -> '{categoryName}'");
                }
                catch (Exception ex)
                {
                    LogEmitted?.Invoke("ERROR", $"[Split Alphabet Error] Không thể chuyển '{subDir.FullName}': {ex.Message}");
                }

                ProgressChanged?.Invoke(i + 1, total, $"Đã phân loại {splitCount} thư mục...");
            }

            DeleteEmptyDirectoriesBottomUp(rootFolder);
            LogEmitted?.Invoke("SUCCESS", $"[Split Alphabet] Hoàn tất chia {splitCount} thư mục theo bảng chữ cái.");
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
            LogEmitted?.Invoke("INFO", $"[Merge Alphabet] Bắt đầu gộp thư mục chữ cái về gốc: {rootFolder}");

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
            var subDirs = rootDirInfo.GetDirectories();
            int mergedCount = 0;
            var emptyDirs = new List<DirectoryInfo>();

            foreach (var catDir in subDirs)
            {
                if (ct.IsCancellationRequested) break;

                if (catDir.Attributes.HasFlag(FileAttributes.Hidden) ||
                    catDir.Attributes.HasFlag(FileAttributes.System) ||
                    catDir.Name.StartsWith(".") ||
                    catDir.Name.EndsWith("-tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool isAlphabetCategory = categoryNames.Contains(catDir.Name) || AlphabetRangeItem.TryParse(catDir.Name, out _);
                if (!isAlphabetCategory) continue;

                var comicDirs = catDir.GetDirectories();
                foreach (var comicDir in comicDirs)
                {
                    if (ct.IsCancellationRequested) break;

                    string destPath = Path.Combine(rootFolder, comicDir.Name);
                    if (string.Equals(comicDir.FullName, destPath, StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        if (!Directory.Exists(destPath))
                        {
                            Directory.Move(comicDir.FullName, destPath);
                        }
                        else
                        {
                            MergeDirectoryContents(comicDir.FullName, destPath);
                        }
                        mergedCount++;
                        LogEmitted?.Invoke("SUCCESS", $"[Merge Alphabet] '{catDir.Name}\\{comicDir.Name}' -> '{comicDir.Name}'");
                    }
                    catch (Exception ex)
                    {
                        LogEmitted?.Invoke("ERROR", $"[Merge Alphabet Error] Lỗi gộp '{comicDir.FullName}': {ex.Message}");
                    }
                }

                emptyDirs.Add(catDir);
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
            LogEmitted?.Invoke("SUCCESS", $"[Merge Alphabet] Hoàn tất gộp {mergedCount} thư mục về gốc.");
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

            foreach (var folder in subDirs)
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
            var ext = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
            return Directory.EnumerateFiles(folder).Any(f => ext.Contains(Path.GetExtension(f)));
        }
        catch { return false; }
    }

    private static void MergeDirectoryContents(string source, string dest)
    {
        if (string.Equals(source, dest, StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(dest);

        foreach (var file in Directory.GetFiles(source))
        {
            string destFile = Path.Combine(dest, Path.GetFileName(file));
            try
            {
                if (File.Exists(destFile))
                {
                    var sInfo = new FileInfo(file);
                    var dInfo = new FileInfo(destFile);
                    if (sInfo.Length == dInfo.Length)
                    {
                        File.Delete(file);
                        continue;
                    }
                }
                File.Move(file, destFile, overwrite: true);
            }
            catch {}
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            MergeDirectoryContents(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(source).Any())
            {
                Directory.Delete(source, false);
            }
        }
        catch {}
    }

    private static void DeleteEmptyDirectoriesBottomUp(string rootFolder)
    {
        try
        {
            foreach (string directory in Directory.GetDirectories(rootFolder, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
            {
                if (string.Equals(directory, rootFolder, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory, false);
                    }
                }
                catch {}
            }
        }
        catch {}
    }
}
