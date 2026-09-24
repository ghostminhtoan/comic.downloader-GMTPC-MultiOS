using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ComicDownloaderGMTPC.Models;

namespace ComicDownloaderGMTPC.Services;

public class MissingChapterScannerService
{
    private static readonly Lazy<MissingChapterScannerService> _instance = new(() => new MissingChapterScannerService());
    public static MissingChapterScannerService Instance => _instance.Value;

    public MissingScanItem ScanChapters(ComicBookItem book)
    {
        var item = new MissingScanItem
        {
            Index = book.Index,
            Domain = book.Domain,
            Title = book.Title,
            Url = book.Url,
            LatestChapter = book.LatestChapter,
            Status = "Scanning..."
        };

        if (book.Chapters.Count == 0)
        {
            item.MissingIntegerChapters = "N/A";
            item.DecimalChapters = "N/A";
            item.Status = "No chapters";
            return item;
        }

        var integerChapters = new HashSet<int>();
        var decimalChapters = new List<double>();

        foreach (var ch in book.Chapters)
        {
            ParseChapterLabel(ch.Title, integerChapters, decimalChapters);
        }

        if (integerChapters.Count == 0)
        {
            item.MissingIntegerChapters = "Complete";
            item.DecimalChapters = decimalChapters.Count > 0 ? string.Join(", ", decimalChapters.OrderBy(x => x)) : "None";
            item.Status = "Complete";
            return item;
        }

        int min = integerChapters.Min();
        int max = integerChapters.Max();

        var missing = new List<int>();
        // Scan missing from 1 to max (or min to max if min > 1)
        int start = min <= 3 ? 1 : min;
        for (int i = start; i <= max; i++)
        {
            if (!integerChapters.Contains(i))
            {
                missing.Add(i);
            }
        }

        if (missing.Count == 0)
        {
            item.MissingIntegerChapters = "Complete";
            item.Status = "Complete";
        }
        else
        {
            item.MissingIntegerChapters = FormatMissingRange(missing);
            item.Status = $"Missing {missing.Count} chap";
        }

        item.DecimalChapters = decimalChapters.Count > 0 ? string.Join(", ", decimalChapters.OrderBy(x => x).Distinct()) : "None";
        return item;
    }

    private void ParseChapterLabel(string title, HashSet<int> integers, List<double> decimals)
    {
        // Check range patterns: e.g. "Chapter 58: 59" or "58-59" or "58 - 59"
        var rangeMatch = Regex.Match(title, @"(?:chap(?:ter)?|chương|tập)?\s*([0-9]+)\s*[:\-–]\s*([0-9]+)", RegexOptions.IgnoreCase);
        if (rangeMatch.Success &&
            int.TryParse(rangeMatch.Groups[1].Value, out int rStart) &&
            int.TryParse(rangeMatch.Groups[2].Value, out int rEnd))
        {
            if (rStart <= rEnd && (rEnd - rStart) <= 10)
            {
                for (int i = rStart; i <= rEnd; i++) integers.Add(i);
                return;
            }
        }

        // Standard number extraction
        var matches = Regex.Matches(title, @"([0-9]+(?:\.[0-9]+)?)");
        foreach (Match m in matches)
        {
            if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
            {
                if (Math.Abs(val % 1) < 0.001)
                {
                    integers.Add((int)val);
                }
                else
                {
                    decimals.Add(val);
                }
                break; // Take the first meaningful chapter number
            }
        }
    }

    private string FormatMissingRange(List<int> numbers)
    {
        if (numbers.Count == 0) return "Complete";
        if (numbers.Count > 15)
        {
            return $"{string.Join(", ", numbers.Take(12))} ... (+{numbers.Count - 12} more)";
        }
        return string.Join(", ", numbers);
    }
}
