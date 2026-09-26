using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ComicDownloaderGMTPC.Services;

public class ChapterFilter
{
    public List<Tuple<double, double>> Ranges { get; } = new();
    public HashSet<double> Singles { get; } = new();
    public List<string> TextKeywords { get; } = new();

    public bool IsMatch(string chapterTitle, double parsedChapNum = -1)
    {
        // 1. Kiểm tra khớp theo từ khóa chữ (không phân biệt hoa thường)
        if (!string.IsNullOrWhiteSpace(chapterTitle) && TextKeywords.Count > 0)
        {
            foreach (var kw in TextKeywords)
            {
                if (chapterTitle.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // 2. Nếu có số chapter (hoặc parse được từ title)
        if (parsedChapNum < 0 && !string.IsNullOrWhiteSpace(chapterTitle))
        {
            if (TryParseChapterNumber(chapterTitle, out double extracted))
            {
                parsedChapNum = extracted;
            }
        }

        if (parsedChapNum >= 0)
        {
            foreach (var single in Singles)
            {
                if (Math.Abs(parsedChapNum - single) < 0.0001)
                {
                    return true;
                }
            }

            foreach (var range in Ranges)
            {
                if (parsedChapNum >= range.Item1 - 0.0001 && parsedChapNum <= range.Item2 + 0.0001)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool TryParseChapterNumber(string text, out double number)
    {
        number = 0d;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Ưu tiên khớp số sau tiền tố chỉ chương
        Match match = Regex.Match(
            text,
            @"(?:^|[\s/_-])(?:chap|chapter|chuong|chương|ch|c|vol|tập)\s*(?:[#_: -]+)?\s*(?<num>\d+(?:[.,]\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            match = Regex.Match(text, @"(?<!\d)(?<num>\d+(?:[.,]\d+)?)", RegexOptions.CultureInvariant);
        }

        if (!match.Success) return false;

        string token = match.Groups["num"].Value.Replace(',', '.');
        return double.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
    }
}

public static class ChapterRangeParser
{
    /// <summary>
    /// Parse chuỗi người dùng nhập: hỗ trợ số nguyên (1, 2), số thập phân (1.5, 2.1), range (1-10, 1.5-5.5) và chữ (Oneshot, Extra, Side Story...).
    /// </summary>
    public static ChapterFilter? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null; // Tải tất cả
        }

        var filter = new ChapterFilter();
        string[] segments = input.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string rawSegment in segments)
        {
            string trimmed = rawSegment.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Kiểm tra cú pháp Range: ví dụ 1-10 hoặc 1.5-5.5
            if (trimmed.Contains('-') && !trimmed.StartsWith("-") && !trimmed.EndsWith("-"))
            {
                string[] parts = trimmed.Split(new[] { '-' }, 2);
                if (parts.Length == 2 &&
                    double.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double start) &&
                    double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double end))
                {
                    if (start <= end)
                    {
                        filter.Ranges.Add(new Tuple<double, double>(start, end));
                        continue;
                    }
                }
            }

            // Kiểm tra số đơn (nguyên hoặc thập phân)
            if (double.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out double singleNum))
            {
                filter.Singles.Add(singleNum);
                continue;
            }

            // Nếu không phải số, tính là từ khóa chữ (chữ cái ví dụ "Oneshot", "Extra", "Prologue", "End")
            filter.TextKeywords.Add(trimmed);
        }

        return (filter.Ranges.Count > 0 || filter.Singles.Count > 0 || filter.TextKeywords.Count > 0) ? filter : null;
    }

    public static string ToDisplayString(ChapterFilter? filter)
    {
        if (filter == null || (filter.Ranges.Count == 0 && filter.Singles.Count == 0 && filter.TextKeywords.Count == 0))
        {
            return "Tất cả";
        }

        var parts = new List<string>();
        foreach (var r in filter.Ranges)
        {
            parts.Add($"{r.Item1.ToString(CultureInfo.InvariantCulture)}-{r.Item2.ToString(CultureInfo.InvariantCulture)}");
        }
        foreach (var s in filter.Singles)
        {
            parts.Add(s.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var kw in filter.TextKeywords)
        {
            parts.Add(kw);
        }

        return string.Join(", ", parts);
    }
}
