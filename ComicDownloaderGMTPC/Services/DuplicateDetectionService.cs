using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ComicDownloaderGMTPC.Models;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Dịch vụ chuyên sâu nhận diện truyện trùng lặp, bóc tách tên chuẩn hóa, 
/// phân loại biến thể kiểm duyệt/màu sắc (Censorship / Full Color) và biến thể số tập.
/// Được porting và tối ưu từ thuật toán gốc của Comic Downloader GMTPC WPF.
/// </summary>
public static class DuplicateDetectionService
{
    private static readonly Regex BracketRegex1 = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex BracketRegex2 = new(@"\{[^\}]*\}", RegexOptions.Compiled);
    private static readonly Regex BracketRegex3 = new(@"\([^\)]*\)", RegexOptions.Compiled);

    private static readonly Regex CommonKeywordsRegex = new(
        @"(?:extra\s+version|copy\s+of|part\s+\d+|part\d+|pt\s+\d+|pt\d+|vol\s+\d+|vol\d+|ch\s+\d+|ch\d+|chap\s+\d+|chap\d+|chapter\s+\d+|chapter\d+|extra|extras|version|rewrite|copy|doujin|dj|\bch\b|\bchap\b|\bpart\b|\bpt\b|\bvol\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VariantKeywordsRegex = new(
        @"(?:minidoujin|doujinshi|decensored|uncensored|censored|full\s*color|fullcolor|colorized|colored|color)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NumberBoundaryRegex = new(@"\b\d+\b", RegexOptions.Compiled);
    private static readonly Regex NonAlphaNumericRegex = new(@"[^a-z0-9]", RegexOptions.Compiled);

    private static readonly Regex KeywordNumberVariantRegex = new(
        @"\b(?:chapter|chap|ch|book|vol|volume|part|pt)\s*\d+(?:[.,]\d+)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SuffixNumberVariantRegex = new(
        @"(?:^|[\s_-])\d+(?:[.,]\d+)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GetNumberedVariantCoreRegex1 = new(
        @"\b(?:chapter|chap|ch|book|vol|volume|part|pt)\s*\d+(?:[.,]\d+)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GetNumberedVariantCoreRegex2 = new(
        @"\b\d+(?:[.,]\d+)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HasUncensoredVariantRegex = new(
        @"(?:^|[^a-zA-Z])(?:decensored|uncensored)(?:$|[^a-zA-Z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HasFullColorVariantRegex = new(
        @"(?:^|[^a-zA-Z])(?:full\s*color|fullcolor|colorized|colored)(?:$|[^a-zA-Z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] HentaiDomains =
    {
        "hentaiforce", "nhentai", "hentai2read", "hentaiera",
        "vi-hentai", "daomeoden", "damconuong", "truyengg", "sayhentai", "hitomi",
        "e-hentai", "exhentai"
    };

    /// <summary>
    /// Rút gọn tiêu đề truyện về chuỗi cốt lõi (Core string) để so khớp độ trùng lặp.
    /// </summary>
    public static string GetSimilarityCore(string? name, bool mergeCensorshipColorVariants = false)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        string core = name.ToLowerInvariant();
        core = BracketRegex1.Replace(core, " ");
        core = BracketRegex2.Replace(core, " ");
        core = BracketRegex3.Replace(core, " ");

        core = CommonKeywordsRegex.Replace(core, " ");

        if (mergeCensorshipColorVariants)
        {
            core = VariantKeywordsRegex.Replace(core, " ");
        }

        core = NumberBoundaryRegex.Replace(core, " ");
        core = NonAlphaNumericRegex.Replace(core, string.Empty);
        return core.Trim();
    }

    public static bool HasUncensoredVariant(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return HasUncensoredVariantRegex.IsMatch(name);
    }

    public static bool HasFullColorVariant(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return HasFullColorVariantRegex.IsMatch(name);
    }

    public static bool HasCensorshipColorVariant(string? name)
    {
        return HasUncensoredVariant(name) || HasFullColorVariant(name);
    }

    public static bool IsHentaiDuplicateCandidate(ComicBookItem? item)
    {
        if (item == null) return false;
        string source = (item.Domain ?? string.Empty).Trim();
        string link = (item.Url ?? string.Empty).Trim();
        string haystack = (source + " " + link).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(haystack)) return false;

        return HentaiDomains.Any(domain => haystack.Contains(domain));
    }

    public static string AppendMetadataToName(string name, string metadata)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        if (name.IndexOf(metadata, StringComparison.OrdinalIgnoreCase) >= 0) return name;

        int squareBracketIndex = name.IndexOf('[');
        if (squareBracketIndex >= 0)
        {
            string prefix = name.Substring(0, squareBracketIndex).Trim();
            string suffix = name.Substring(squareBracketIndex);
            return $"{prefix} ({metadata}) {suffix}";
        }
        return $"{name.Trim()} ({metadata})";
    }

    private static string StripDuplicateMetadata(string name)
    {
        string core = (name ?? string.Empty).ToLowerInvariant();
        core = BracketRegex1.Replace(core, " ");
        core = BracketRegex2.Replace(core, " ");
        core = BracketRegex3.Replace(core, " ");
        return core;
    }

    private static bool HasKeywordNumberVariantPattern(string name)
    {
        return KeywordNumberVariantRegex.IsMatch(StripDuplicateMetadata(name));
    }

    private static bool HasSuffixNumberVariantPattern(string name)
    {
        return SuffixNumberVariantRegex.IsMatch(StripDuplicateMetadata(name));
    }

    private static bool HasAnyNumberVariantPattern(string name)
    {
        return HasKeywordNumberVariantPattern(name) || HasSuffixNumberVariantPattern(name);
    }

    private static string GetNumberedVariantCore(string name)
    {
        string core = StripDuplicateMetadata(name);
        core = GetNumberedVariantCoreRegex1.Replace(core, " ");
        core = GetNumberedVariantCoreRegex2.Replace(core, " ");
        core = NonAlphaNumericRegex.Replace(core, string.Empty);
        return core.Trim();
    }

    public static string GetSharedSuffixVariantCore(IEnumerable<string> names)
    {
        var suffixCores = (names ?? Enumerable.Empty<string>())
            .Where(HasSuffixNumberVariantPattern)
            .Select(GetNumberedVariantCore)
            .Where(core => !string.IsNullOrWhiteSpace(core))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (suffixCores.Count != 1)
        {
            return string.Empty;
        }

        return suffixCores[0];
    }

    private static bool IsSuffixVariantBaseTitle(string name, string suffixVariantCore)
    {
        if (string.IsNullOrWhiteSpace(suffixVariantCore) || HasAnyNumberVariantPattern(name))
        {
            return false;
        }

        return string.Equals(GetNumberedVariantCore(name), suffixVariantCore, StringComparison.Ordinal);
    }

    internal static bool IsNumberedTitleVariant(string name, string suffixVariantCore)
    {
        return HasKeywordNumberVariantPattern(name) ||
               HasSuffixNumberVariantPattern(name) ||
               IsSuffixVariantBaseTitle(name, suffixVariantCore);
    }

    private static bool HasUncensoredInTags(ComicBookItem item)
    {
        if (string.IsNullOrWhiteSpace(item.PreviewTagsText)) return false;
        return item.PreviewTagsText.Contains("uncensored", StringComparison.OrdinalIgnoreCase) ||
               item.PreviewTagsText.Contains("decensored", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasFullColorInTags(ComicBookItem item)
    {
        if (string.IsNullOrWhiteSpace(item.PreviewTagsText)) return false;
        return item.PreviewTagsText.Contains("full color", StringComparison.OrdinalIgnoreCase) ||
               item.PreviewTagsText.Contains("colorized", StringComparison.OrdinalIgnoreCase) ||
               item.PreviewTagsText.Contains("colored", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Thực hiện quét toàn bộ danh sách truyện trong hàng đợi, phân tích và gán các cờ trùng lặp / biến thể chuẩn xác.
    /// </summary>
    public static void RunDuplicateDetection(IEnumerable<ComicBookItem>? items)
    {
        if (items == null) return;
        var itemList = items.Where(x => x != null && !x.IsParallelSplitChild).ToList();
        if (itemList.Count == 0) return;

        // 1. Tự động chèn metadata vào tên truyện dựa trên tag Hitomi/Nhentai/E-Hentai nếu có
        foreach (var item in itemList)
        {
            bool isHitomi = string.Equals(item.Domain, "hitomi.la", StringComparison.OrdinalIgnoreCase) ||
                            (item.Url != null && item.Url.Contains("hitomi.la", StringComparison.OrdinalIgnoreCase));
            bool isNhentai = string.Equals(item.Domain, "nhentai.net", StringComparison.OrdinalIgnoreCase) ||
                             (item.Url != null && item.Url.Contains("nhentai.net", StringComparison.OrdinalIgnoreCase));
            bool isEHentai = string.Equals(item.Domain, "e-hentai.org", StringComparison.OrdinalIgnoreCase) ||
                             (item.Url != null && (item.Url.Contains("e-hentai.org", StringComparison.OrdinalIgnoreCase) ||
                                                    item.Url.Contains("exhentai.org", StringComparison.OrdinalIgnoreCase)));

            if (isHitomi || isNhentai || isEHentai)
            {
                if (HasUncensoredInTags(item) && !HasUncensoredVariant(item.Title))
                {
                    item.Title = AppendMetadataToName(item.Title, "uncensored");
                }
                if (HasFullColorInTags(item) && !HasFullColorVariant(item.Title))
                {
                    item.Title = AppendMetadataToName(item.Title, "full color");
                }
            }
        }

        // 2. Reset toàn bộ cờ trùng lặp trước khi tính toán lại
        foreach (var item in itemList)
        {
            item.IsDuplicate = false;
            item.IsCensorshipColorDuplicate = false;
            item.IsCensorshipUncensoredVariant = false;
            item.IsCensorshipFullColorVariant = false;
            item.IsNumberedVariantDuplicate = false;
        }

        // 3. Gom nhóm theo SimilarityCore nguyên bản (không gộp biến thể kiểm duyệt/màu)
        var groups = itemList
            .GroupBy(item => GetSimilarityCore(item.Title, false))
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .ToList();

        foreach (var group in groups)
        {
            if (group.Count() > 1)
            {
                string suffixVariantCore = GetSharedSuffixVariantCore(group.Select(item => item.Title));
                foreach (var item in group)
                {
                    item.IsDuplicate = true;
                    item.IsNumberedVariantDuplicate = IsNumberedTitleVariant(item.Title, suffixVariantCore);
                }
            }
        }

        // 4. Nhận diện các biến thể màu sắc / không che cho truyện
        foreach (var item in itemList)
        {
            bool isHitomi = string.Equals(item.Domain, "hitomi.la", StringComparison.OrdinalIgnoreCase) ||
                            (item.Url != null && item.Url.Contains("hitomi.la", StringComparison.OrdinalIgnoreCase));
            bool isNhentai = string.Equals(item.Domain, "nhentai.net", StringComparison.OrdinalIgnoreCase) ||
                             (item.Url != null && item.Url.Contains("nhentai.net", StringComparison.OrdinalIgnoreCase));
            bool isEHentai = string.Equals(item.Domain, "e-hentai.org", StringComparison.OrdinalIgnoreCase) ||
                             (item.Url != null && (item.Url.Contains("e-hentai.org", StringComparison.OrdinalIgnoreCase) ||
                                                    item.Url.Contains("exhentai.org", StringComparison.OrdinalIgnoreCase)));

            if (isHitomi || isNhentai || isEHentai || IsHentaiDuplicateCandidate(item))
            {
                item.IsCensorshipFullColorVariant = HasFullColorVariant(item.Title) || HasFullColorInTags(item);
                item.IsCensorshipUncensoredVariant = HasUncensoredVariant(item.Title) || HasUncensoredInTags(item);
            }
        }

        // 5. Gom nhóm phát hiện trùng lặp theo biến thể Censorship & Color
        var censorshipColorGroups = itemList
            .Where(IsHentaiDuplicateCandidate)
            .GroupBy(item => GetSimilarityCore(item.Title, true))
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToList();

        foreach (var group in censorshipColorGroups)
        {
            if (group.Count() <= 1 || !group.Any(item => HasCensorshipColorVariant(item.Title) || HasUncensoredInTags(item) || HasFullColorInTags(item)))
            {
                continue;
            }

            foreach (var item in group)
            {
                item.IsCensorshipColorDuplicate = true;
                item.IsCensorshipFullColorVariant = HasFullColorVariant(item.Title) || HasFullColorInTags(item);
                item.IsCensorshipUncensoredVariant = HasUncensoredVariant(item.Title) || HasUncensoredInTags(item);
            }
        }

        // 6. Cập nhật lại màu sắc TitleBrush trên từng truyện
        foreach (var item in itemList)
        {
            item.NotifyTitleBrushChanged();
        }
    }
}

