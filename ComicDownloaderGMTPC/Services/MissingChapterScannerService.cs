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

        if (book.Chapters == null || book.Chapters.Count == 0)
        {
            item.MissingIntegerChapters = "N/A";
            item.DecimalChapters = "N/A";
            item.Status = "No chapters";
            return item;
        }

        // 1. Kiểm tra truyện Oneshot (Chỉ có 1 chương và là Oneshot hoặc truyện 1 tập)
        if (book.Chapters.Count == 1)
        {
            var firstChap = book.Chapters[0];
            string t = firstChap.Title?.ToLowerInvariant() ?? string.Empty;
            if (t.Contains("oneshot") || t.Contains("one-shot") || t.Contains("one shot") || t.Contains("chương 0") || t.Contains("chap 0") || t.Contains("chương 1") || t.Contains("chap 1"))
            {
                item.MissingIntegerChapters = "Complete";
                item.DecimalChapters = "None";
                item.Status = "Complete";
                return item;
            }
        }

        var integerChapters = new HashSet<int>();
        var decimalChapters = new List<double>();

        foreach (var ch in book.Chapters)
        {
            ParseChapter(ch, integerChapters, decimalChapters);
        }

        var coveredChapters = new HashSet<int>(integerChapters);
        foreach (var d in decimalChapters)
        {
            coveredChapters.Add((int)Math.Truncate(d));
        }

        if (coveredChapters.Count == 0)
        {
            item.MissingIntegerChapters = "Complete";
            item.DecimalChapters = decimalChapters.Count > 0 ? string.Join(", ", decimalChapters.OrderBy(x => x).Distinct()) : "None";
            item.Status = "Complete";
            return item;
        }

        // Nếu chỉ có đúng 1 chương 0 duy nhất (truyện mới bắt đầu ra mắt chap 0)
        if (coveredChapters.Count == 1 && coveredChapters.Contains(0))
        {
            item.MissingIntegerChapters = "Complete";
            item.DecimalChapters = "None";
            item.Status = "Complete";
            return item;
        }

        int max = coveredChapters.Max();
        // Dải chuẩn của truyện: bắt đầu từ 0 nếu có chap 0, ngược lại bắt đầu từ 1
        int start = coveredChapters.Contains(0) ? 0 : 1;

        var missing = new List<int>();
        for (int i = start; i <= max; i++)
        {
            if (!coveredChapters.Contains(i))
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

    private void ParseChapter(ChapterItem ch, HashSet<int> integers, List<double> decimals)
    {
        // 1. Kiểm tra range pattern trong Title: ví dụ "Chapter 58-59", "Chương 1-2"
        if (!string.IsNullOrWhiteSpace(ch.Title))
        {
            var rangeMatch = Regex.Match(ch.Title, @"(?:chap(?:ter)?|chương|tập)?\s*([0-9]+)\s*[:\-–]\s*([0-9]+)", RegexOptions.IgnoreCase);
            if (rangeMatch.Success &&
                int.TryParse(rangeMatch.Groups[1].Value, out int rStart) &&
                int.TryParse(rangeMatch.Groups[2].Value, out int rEnd))
            {
                if (rStart <= rEnd && (rEnd - rStart) <= 15)
                {
                    for (int i = rStart; i <= rEnd; i++) integers.Add(i);
                    return;
                }
            }
        }

        // 2. ƯU TIÊN SỐ 1: Sử dụng ChapterNumber đã được scraper bóc tách chuẩn xác từ URL & Title
        // Tránh bị nhầm lẫn số trong tên truyện (như "Batman Của 2 Thế Giới" hoặc "4-Koma")
        if (ch.ChapterNumber > 0 || (ch.ChapterNumber == 0 && (ch.Title?.Contains("0") == true || ch.Url?.Contains("-0") == true)))
        {
            double val = ch.ChapterNumber;
            if (Math.Abs(val % 1) < 0.001)
            {
                integers.Add((int)Math.Round(val));
            }
            else
            {
                decimals.Add(val);
            }
            return;
        }

        // 3. Fallback: Phân tích số từ Title (ưu tiên từ khóa chương / chap trước số đầu tiên)
        if (!string.IsNullOrWhiteSpace(ch.Title))
        {
            var chapMatch = Regex.Match(ch.Title, @"(?:chap(?:ter)?|chương|tập|c\.)\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
            if (chapMatch.Success && double.TryParse(chapMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double cVal))
            {
                if (Math.Abs(cVal % 1) < 0.001) integers.Add((int)Math.Round(cVal));
                else decimals.Add(cVal);
                return;
            }

            var numMatch = Regex.Match(ch.Title, @"([0-9]+(?:\.[0-9]+)?)");
            if (numMatch.Success && double.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double nVal))
            {
                if (Math.Abs(nVal % 1) < 0.001) integers.Add((int)Math.Round(nVal));
                else decimals.Add(nVal);
            }
        }
    }

    private string FormatMissingRange(List<int> numbers)
    {
        if (numbers.Count == 0) return "Complete";

        var ranges = new List<string>();
        int start = numbers[0];
        int prev = start;

        for (int i = 1; i < numbers.Count; i++)
        {
            if (numbers[i] == prev + 1)
            {
                prev = numbers[i];
            }
            else
            {
                ranges.Add(start == prev ? $"{start}" : $"{start}-{prev}");
                start = numbers[i];
                prev = start;
            }
        }
        ranges.Add(start == prev ? $"{start}" : $"{start}-{prev}");

        if (ranges.Count > 10)
        {
            return $"{string.Join(", ", ranges.Take(8))} ... (+{ranges.Count - 8} dải)";
        }
        return string.Join(", ", ranges);
    }
}
