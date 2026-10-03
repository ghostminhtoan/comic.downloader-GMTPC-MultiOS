using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ComicDownloaderGMTPC.Services;

public class OfflineChapterItem
{
    public string Name { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public int PageCount { get; set; }
    public double? ParsedNumber { get; set; }
    public bool IsDecimal { get; set; }
    public bool HasMissingIntegerGap { get; set; }
    public DateTime LastModified { get; set; }
    public string DisplayForeground { get; set; } = "#E2E8F0";
}

public class OfflineChapterAnalysis
{
    public int IntegerCount { get; set; }
    public int DecimalCount { get; set; }
    public int UnknownCount { get; set; }
    public List<string> MissingRanges { get; } = new();
    public int TotalMissingCount { get; set; }
    public string MissingSummaryText => MissingRanges.Count == 0 ? "Đầy đủ chap nguyên" : string.Join(", ", MissingRanges);
    public bool HasMissing => MissingRanges.Count > 0;
}

public class OfflineMangaItem
{
    public string Title { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public List<OfflineChapterItem> Chapters { get; set; } = new();
    public OfflineChapterAnalysis Analysis { get; set; } = new();
    public int ChapterCount => Chapters.Count;
    public string StatusBadgeText => Analysis.HasMissing ? $"Thiếu: {Analysis.MissingSummaryText}" : "Đầy đủ";
    public string StatusBadgeBackground => Analysis.HasMissing ? "#DC2626" : "#059669";
    public string StatusBadgeForeground => "#FFFFFF";
}

public class OfflineMissingChapterService
{
    private static readonly Lazy<OfflineMissingChapterService> _instance = new(() => new OfflineMissingChapterService());
    public static OfflineMissingChapterService Instance => _instance.Value;

    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"
    };

    public Task<List<OfflineMangaItem>> ScanFolderAsync(string rootPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var results = new List<OfflineMangaItem>();
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                return results;
            }

            try
            {
                var subDirs = Directory.GetDirectories(rootPath);
                if (subDirs.Length > 0 && subDirs.Any(d => IsLikelyChapterFolderName(Path.GetFileName(d))))
                {
                    var manga = ScanSingleMangaFolder(rootPath);
                    if (manga != null)
                    {
                        results.Add(manga);
                        return results;
                    }
                }

                CollectMangaFolders(rootPath, 1, results, ct);
            }
            catch { }

            return results.OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }, ct);
    }

    private void CollectMangaFolders(string currentDir, int depth, List<OfflineMangaItem> results, CancellationToken ct)
    {
        if (depth > 4 || ct.IsCancellationRequested) return;

        try
        {
            string[] subDirs = Directory.GetDirectories(currentDir);
            if (subDirs.Length == 0) return;

            bool hasChapterSubDirs = subDirs.Any(d => IsLikelyChapterFolderName(Path.GetFileName(d)));
            if (hasChapterSubDirs)
            {
                var manga = ScanSingleMangaFolder(currentDir);
                if (manga != null && manga.Chapters.Count > 0)
                {
                    results.Add(manga);
                    return;
                }
            }

            foreach (var dir in subDirs)
            {
                if (ct.IsCancellationRequested) break;
                string dirName = Path.GetFileName(dir);
                if (dirName.StartsWith(".") || dirName.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || dirName.Equals(".portable", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                CollectMangaFolders(dir, depth + 1, results, ct);
            }
        }
        catch { }
    }

    public OfflineMangaItem? ScanSingleMangaFolder(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath)) return null;

            string mangaTitle = Path.GetFileName(folderPath);
            var chapterDirs = Directory.GetDirectories(folderPath);
            var chapters = new List<OfflineChapterItem>();

            foreach (var chDir in chapterDirs)
            {
                string chName = Path.GetFileName(chDir);
                int pageCount = 0;
                DateTime lastModified = DateTime.MinValue;

                try
                {
                    var files = Directory.GetFiles(chDir);
                    pageCount = files.Count(f => SupportedImageExtensions.Contains(Path.GetExtension(f)));
                    lastModified = Directory.GetLastWriteTime(chDir);
                }
                catch { }

                chapters.Add(new OfflineChapterItem
                {
                    Name = chName,
                    FolderPath = chDir,
                    PageCount = pageCount,
                    LastModified = lastModified
                });
            }

            if (chapters.Count == 0) return null;

            var analysis = AnalyzeChapters(chapters);

            chapters.Sort((a, b) =>
            {
                if (a.ParsedNumber.HasValue && b.ParsedNumber.HasValue)
                {
                    return a.ParsedNumber.Value.CompareTo(b.ParsedNumber.Value);
                }
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            return new OfflineMangaItem
            {
                Title = mangaTitle,
                FolderPath = folderPath,
                Chapters = chapters,
                Analysis = analysis
            };
        }
        catch
        {
            return null;
        }
    }

    public static OfflineChapterAnalysis AnalyzeChapters(List<OfflineChapterItem> chapters)
    {
        var analysis = new OfflineChapterAnalysis();
        if (chapters == null || chapters.Count == 0) return analysis;

        var integerMap = new Dictionary<int, List<OfflineChapterItem>>();
        var integerNumbers = new List<int>();
        var decimalInts = new HashSet<int>();

        foreach (var ch in chapters)
        {
            ch.ParsedNumber = null;
            ch.IsDecimal = false;
            ch.HasMissingIntegerGap = false;
            ch.DisplayForeground = "#E2E8F0";

            if (!TryParseChapterNumber(ch.Name, out double num, out bool isDecimal))
            {
                analysis.UnknownCount++;
                ch.DisplayForeground = "#94A3B8";
                continue;
            }

            ch.ParsedNumber = num;
            ch.IsDecimal = isDecimal;

            if (TryParseChapterIntegerRange(ch.Name, out int rangeStart, out int rangeEnd))
            {
                for (int n = rangeStart; n <= rangeEnd; n++)
                {
                    if (!integerMap.TryGetValue(n, out var list))
                    {
                        list = new List<OfflineChapterItem>();
                        integerMap[n] = list;
                        integerNumbers.Add(n);
                    }
                    list.Add(ch);
                }
                analysis.IntegerCount += Math.Max(1, rangeEnd - rangeStart + 1);
                ch.DisplayForeground = "#38BDF8";
                continue;
            }

            if (isDecimal)
            {
                analysis.DecimalCount++;
                decimalInts.Add((int)Math.Truncate(num));
                ch.DisplayForeground = "#22D3EE";
                continue;
            }

            analysis.IntegerCount++;
            int intNum = (int)Math.Round(num);
            if (!integerMap.TryGetValue(intNum, out var items))
            {
                items = new List<OfflineChapterItem>();
                integerMap[intNum] = items;
                integerNumbers.Add(intNum);
            }
            items.Add(ch);
        }

        foreach (int decInt in decimalInts)
        {
            if (!integerMap.ContainsKey(decInt))
            {
                integerNumbers.Add(decInt);
                integerMap[decInt] = new List<OfflineChapterItem>();
            }
        }

        integerNumbers.Sort();

        var gapBoundaries = new HashSet<int>();
        int totalMissing = 0;

        if (integerNumbers.Count > 0 && integerNumbers[0] > 1)
        {
            gapBoundaries.Add(integerNumbers[0]);
            int missingFromStart = integerNumbers[0] - 1;
            totalMissing += missingFromStart;
            analysis.MissingRanges.Add(integerNumbers[0] == 2 ? "1" : $"1-{integerNumbers[0] - 1}");
        }

        for (int i = 1; i < integerNumbers.Count; i++)
        {
            int prev = integerNumbers[i - 1];
            int curr = integerNumbers[i];
            int missing = curr - prev - 1;
            if (missing <= 0) continue;

            gapBoundaries.Add(prev);
            gapBoundaries.Add(curr);
            totalMissing += missing;

            analysis.MissingRanges.Add(missing == 1 ? $"{prev + 1}" : $"{prev + 1}-{curr - 1}");
        }

        analysis.TotalMissingCount = totalMissing;

        foreach (var pair in integerMap)
        {
            if (gapBoundaries.Contains(pair.Key))
            {
                foreach (var ch in pair.Value)
                {
                    ch.HasMissingIntegerGap = true;
                    if (!ch.IsDecimal)
                    {
                        ch.DisplayForeground = "#FBBF24";
                    }
                }
            }
        }

        return analysis;
    }

    public static bool IsLikelyChapterFolderName(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return false;
        string clean = folderName.Trim();
        return Regex.IsMatch(clean, @"^(?:chap|chapter|chuong|chương|ch|c|vol|volume|ep|tập)\b|(?:\s|^)\d+(?:[.,]\d+)?\b", RegexOptions.IgnoreCase);
    }

    public static bool TryParseChapterNumber(string chapterName, out double number, out bool isDecimal)
    {
        number = 0d;
        isDecimal = false;
        if (string.IsNullOrWhiteSpace(chapterName)) return false;

        Match match = Regex.Match(
            chapterName,
            @"(?:^|[\s/_-])(?:chap|chapter|chuong|chương|ch|c)\s*(?:[#_: -]+)?\s*(?<num>\d+(?:[.,]\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            match = Regex.Match(chapterName, @"(?<!\d)(?<num>\d+(?:[.,]\d+)?)", RegexOptions.CultureInvariant);
        }

        if (!match.Success) return false;

        string token = match.Groups["num"].Value.Replace(',', '.');
        if (!double.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }

        isDecimal = Math.Abs(number - Math.Truncate(number)) > 0.0001d;
        return true;
    }

    public static bool TryParseChapterIntegerRange(string chapterName, out int start, out int end)
    {
        start = 0;
        end = 0;
        if (string.IsNullOrWhiteSpace(chapterName)) return false;

        Match match = Regex.Match(
            chapterName,
            @"(?<![\d.,])(?<start>\d+)\s*(?:-|:)\s*(?:(?:chapter|chap|chương|ch|c)\s*)?(?<end>\d+)(?![\d.,])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success ||
            !int.TryParse(match.Groups["start"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out start) ||
            !int.TryParse(match.Groups["end"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out end))
        {
            return false;
        }

        if (end < start || end - start > 50) return false;
        return true;
    }
}
