using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class MainViewModel
{
    private AdvancedSearchViewModel? _advancedSearch;
    public AdvancedSearchViewModel AdvancedSearch => _advancedSearch ??= new AdvancedSearchViewModel(ApplyQueueFilter);

    [ObservableProperty]
    private string _queueSearchFilter = string.Empty;

    [ObservableProperty]
    private bool _isFilterDuplicatesOnly = false;

    [ObservableProperty]
    private bool _isFilterUncenOnly = false;

    [ObservableProperty]
    private string _filteredQueueCountText = string.Empty;

    [ObservableProperty]
    private string _filteredHeaderCountText = string.Empty;

    [ObservableProperty]
    private int _filteredBooksCount = 0;

    [ObservableProperty]
    private bool _isFilterActive = false;

    [ObservableProperty]
    private int _duplicateBooksCount = 0;

    [ObservableProperty]
    private bool _isNameSortAscending = false;

    [ObservableProperty]
    private string _sortByNameButtonText = "Sắp xếp theo tên";

    partial void OnQueueSearchFilterChanged(string value)
    {
        ApplyQueueFilter();
    }

    partial void OnIsFilterDuplicatesOnlyChanged(bool value)
    {
        ApplyQueueFilter();
    }

    partial void OnIsFilterUncenOnlyChanged(bool value)
    {
        ApplyQueueFilter();
    }

    [RelayCommand]
    public void ClearQueueFilter()
    {
        QueueSearchFilter = string.Empty;
        IsFilterDuplicatesOnly = false;
        IsFilterUncenOnly = false;
        AdvancedSearch.Clear();
        ApplyQueueFilter();
    }

    [RelayCommand]
    public void ToggleFilterDuplicates()
    {
        IsFilterDuplicatesOnly = !IsFilterDuplicatesOnly;
    }

    [RelayCommand]
    public void ToggleFilterUncen()
    {
        IsFilterUncenOnly = !IsFilterUncenOnly;
    }

    [RelayCommand]
    public void CheckAllDuplicates()
    {
        int count = 0;
        foreach (var b in ComicBooks)
        {
            if (b != null && (b.IsDuplicate || b.IsCensorshipColorDuplicate))
            {
                b.IsChecked = true;
                count++;
            }
        }
        AddLog("INFO", $"Đã tích chọn {count} truyện trùng lặp.");
    }

    [RelayCommand]
    public void UncheckAllDuplicates()
    {
        int count = 0;
        foreach (var b in ComicBooks)
        {
            if (b != null && (b.IsDuplicate || b.IsCensorshipColorDuplicate))
            {
                b.IsChecked = false;
                count++;
            }
        }
        AddLog("INFO", $"Đã bỏ tích {count} truyện trùng lặp.");
    }

    /// <summary>
    /// Sắp xếp truyện trong hàng chờ theo tên alphabet (A-Z / Z-A) bằng NaturalSortComparer
    /// </summary>
    [RelayCommand]
    public void SortByNameAlphabet()
    {
        if (ComicBooks.Count <= 1)
        {
            AddLog("INFO", "Hàng chờ có ít hơn 2 truyện, không cần sắp xếp.");
            return;
        }

        IsNameSortAscending = !IsNameSortAscending;
        SortByNameButtonText = IsNameSortAscending ? "Tên: A-Z ⇅" : "Tên: Z-A ⇅";

        // Nhóm các mục theo khối phân cấp (cha và các con song song nếu có)
        var groups = GroupComicBooksHierarchy();

        if (IsNameSortAscending)
        {
            groups.Sort((a, b) => NaturalSortComparer.Instance.Compare(a[0].Title, b[0].Title));
        }
        else
        {
            groups.Sort((a, b) => NaturalSortComparer.Instance.Compare(b[0].Title, a[0].Title));
        }

        ComicBooks.Clear();
        foreach (var group in groups)
        {
            foreach (var item in group)
            {
                ComicBooks.Add(item);
            }
        }

        ReindexComicBooks();
        ApplyQueueFilter();
        RecalculateDuplicates();
        _downloadEngine.NotifyQueueOrderChanged();
        UpdateStats();
        RequestQueueAutoSave(150);
        AddLog("SUCCESS", $"Đã sắp xếp danh sách hàng chờ theo tên alphabet ({(IsNameSortAscending ? "A-Z" : "Z-A")}).");
    }

    /// <summary>
    /// Khôi phục lại thứ tự ban đầu khi cào từ web về dựa vào OriginalIndex
    /// </summary>
    [RelayCommand]
    public void RestoreOriginalOrder()
    {
        if (ComicBooks.Count <= 1)
        {
            AddLog("INFO", "Hàng chờ có ít hơn 2 truyện, không cần khôi phục.");
            return;
        }

        for (int i = 0; i < ComicBooks.Count; i++)
        {
            if (ComicBooks[i].OriginalIndex <= 0)
            {
                ComicBooks[i].OriginalIndex = i + 1;
            }
        }

        // Reset trạng thái sắp xếp theo tên
        IsNameSortAscending = false;
        SortByNameButtonText = "Sắp xếp theo tên";

        // Nhóm các mục theo khối phân cấp
        var groups = GroupComicBooksHierarchy();

        groups.Sort((a, b) => a[0].OriginalIndex.CompareTo(b[0].OriginalIndex));

        ComicBooks.Clear();
        foreach (var group in groups)
        {
            foreach (var item in group)
            {
                ComicBooks.Add(item);
            }
        }

        ReindexComicBooks();
        ApplyQueueFilter();
        RecalculateDuplicates();
        _downloadEngine.NotifyQueueOrderChanged();
        UpdateStats();
        RequestQueueAutoSave(150);
        AddLog("SUCCESS", "Đã khôi phục lại thứ tự ban đầu từ web thành công.");
    }

    private List<List<ComicBookItem>> GroupComicBooksHierarchy()
    {
        var groups = new List<List<ComicBookItem>>();
        List<ComicBookItem>? currentGroup = null;

        for (int i = 0; i < ComicBooks.Count; i++)
        {
            var item = ComicBooks[i];
            if (item.IsParallelSplitChild && currentGroup != null)
            {
                currentGroup.Add(item);
            }
            else
            {
                currentGroup = new List<ComicBookItem> { item };
                groups.Add(currentGroup);
            }
        }
        return groups;
    }

    public void RecalculateDuplicates()
    {
        DuplicateDetectionService.RunDuplicateDetection(ComicBooks);
        DuplicateBooksCount = ComicBooks.Count(b => b != null && (b.IsDuplicate || b.IsCensorshipColorDuplicate));
        ApplyQueueFilter();
    }

    private static readonly ConcurrentDictionary<string, Regex> _exactWordRegexCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static Regex? GetExactWordRegex(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return null;
        if (_exactWordRegexCache.Count > 500) _exactWordRegexCache.Clear();

        return _exactWordRegexCache.GetOrAdd(keyword, kw =>
        {
            bool startWord = char.IsLetterOrDigit(kw[0]) || kw[0] == '_';
            bool endWord = char.IsLetterOrDigit(kw[^1]) || kw[^1] == '_';

            string pattern = (startWord ? @"\b" : "") + Regex.Escape(kw) + (endWord ? @"\b" : "");
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        });
    }

    public static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var normalizedString = text.Normalize(NormalizationForm.FormD);
        var stringBuilder = new StringBuilder(text.Length);

        foreach (var c in normalizedString)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                stringBuilder.Append(c);
            }
        }

        return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool ContainsKeyword(string? source, string keyword, bool exactWord, Regex? regexExact)
    {
        if (string.IsNullOrEmpty(source)) return false;

        if (!exactWord)
        {
            var compareInfo = CultureInfo.InvariantCulture.CompareInfo;
            return compareInfo.IndexOf(source, keyword, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
        }
        else
        {
            if (regexExact == null) return false;
            string normalizedSource = RemoveDiacritics(source);
            return regexExact.IsMatch(normalizedSource);
        }
    }

    private bool IsComicBookMatchKeyword(ComicBookItem book, string keyword, bool exactWordMatch)
    {
        if (book == null || string.IsNullOrWhiteSpace(keyword)) return false;

        string normalizedKeyword = keyword.Trim();
        string? searchPrefix = null;

        int colonIndex = normalizedKeyword.IndexOf(':');
        if (colonIndex > 0 && colonIndex < normalizedKeyword.Length - 1)
        {
            string prefix = normalizedKeyword.Substring(0, colonIndex).ToLowerInvariant();
            if (prefix is "name" or "link" or "tag")
            {
                searchPrefix = prefix;
                normalizedKeyword = normalizedKeyword.Substring(colonIndex + 1).Trim();
            }
        }

        Regex? regexExact = null;
        if (exactWordMatch)
        {
            string noDiacriticsKeyword = RemoveDiacritics(normalizedKeyword);
            regexExact = GetExactWordRegex(noDiacriticsKeyword);
            if (regexExact == null) return false;
        }

        if (searchPrefix == "name") return ContainsKeyword(book.Title, normalizedKeyword, exactWordMatch, regexExact);
        if (searchPrefix == "link") return ContainsKeyword(book.Url, normalizedKeyword, exactWordMatch, regexExact);
        if (searchPrefix == "tag") return ContainsKeyword(book.PreviewTagsText, normalizedKeyword, exactWordMatch, regexExact);

        return ContainsKeyword(book.Title, normalizedKeyword, exactWordMatch, regexExact) ||
               ContainsKeyword(book.Url, normalizedKeyword, exactWordMatch, regexExact) ||
               ContainsKeyword(book.PreviewTagsText, normalizedKeyword, exactWordMatch, regexExact) ||
               ContainsKeyword(book.MissingChapters, normalizedKeyword, exactWordMatch, regexExact) ||
               ContainsKeyword(book.Status, normalizedKeyword, exactWordMatch, regexExact) ||
               ContainsKeyword(book.DetailProgressText, normalizedKeyword, exactWordMatch, regexExact) ||
               ContainsKeyword(book.ChapterSelectionText, normalizedKeyword, exactWordMatch, regexExact);
    }

    public void ApplyQueueFilter()
    {
        string filter = (QueueSearchFilter ?? string.Empty).Trim();
        bool filterDup = IsFilterDuplicatesOnly;
        bool filterUncen = IsFilterUncenOnly;

        var posKeywords = AdvancedSearch.GetActivePositiveKeywords();
        var nonNegKeywords = AdvancedSearch.GetActiveNonNegativeKeywords();
        var nonPosKeywords = AdvancedSearch.GetActiveNonPositiveKeywords();
        var negKeywords = AdvancedSearch.GetActiveNegativeKeywords();
        bool exactWordMatch = AdvancedSearch.IsExactWord;

        bool hasAdvanced = posKeywords.Count > 0 || nonNegKeywords.Count > 0 || nonPosKeywords.Count > 0 || negKeywords.Count > 0;
        bool hasFilter = !string.IsNullOrWhiteSpace(filter) || hasAdvanced || filterDup || filterUncen;

        int visibleCount = 0;
        foreach (var book in ComicBooks)
        {
            if (book == null) continue;

            // 1. Kiểm tra bộ lọc tìm kiếm nhanh chính (QueueSearchFilter)
            if (!string.IsNullOrEmpty(filter))
            {
                if (!IsComicBookMatchKeyword(book, filter, exactWordMatch))
                {
                    book.IsFilteredOut = true;
                    continue;
                }
            }

            // 2. Kiểm tra Bắt buộc có (+) - AND logic (Phải chứa tất cả)
            if (posKeywords.Count > 0)
            {
                bool matchAll = true;
                foreach (var k in posKeywords)
                {
                    if (!IsComicBookMatchKeyword(book, k, exactWordMatch))
                    {
                        matchAll = false;
                        break;
                    }
                }
                if (!matchAll)
                {
                    book.IsFilteredOut = true;
                    continue;
                }
            }

            // 3. Kiểm tra Có thể có (OR) - OR logic (Chứa ít nhất 1)
            if (nonNegKeywords.Count > 0)
            {
                bool matchAny = false;
                foreach (var k in nonNegKeywords)
                {
                    if (IsComicBookMatchKeyword(book, k, exactWordMatch))
                    {
                        matchAny = true;
                        break;
                    }
                }
                if (!matchAny)
                {
                    book.IsFilteredOut = true;
                    continue;
                }
            }

            // 4. Kiểm tra Bắt buộc không có (-) - Exclude logic 1
            if (nonPosKeywords.Count > 0)
            {
                bool hasExcluded = false;
                foreach (var k in nonPosKeywords)
                {
                    if (IsComicBookMatchKeyword(book, k, exactWordMatch))
                    {
                        hasExcluded = true;
                        break;
                    }
                }
                if (hasExcluded)
                {
                    book.IsFilteredOut = true;
                    continue;
                }
            }

            // 5. Kiểm tra Loại trừ (-) - Exclude logic 2
            if (negKeywords.Count > 0)
            {
                bool hasExcluded = false;
                foreach (var k in negKeywords)
                {
                    if (IsComicBookMatchKeyword(book, k, exactWordMatch))
                    {
                        hasExcluded = true;
                        break;
                    }
                }
                if (hasExcluded)
                {
                    book.IsFilteredOut = true;
                    continue;
                }
            }

            // 6. Kiểm tra Lọc trùng tên
            if (filterDup && !(book.IsDuplicate || book.IsCensorshipColorDuplicate))
            {
                book.IsFilteredOut = true;
                continue;
            }

            // 7. Kiểm tra Lọc Uncen
            if (filterUncen && !book.IsCensorshipUncensoredVariant)
            {
                book.IsFilteredOut = true;
                continue;
            }

            book.IsFilteredOut = false;
            visibleCount++;
        }

        FilteredBooksCount = visibleCount;
        IsFilterActive = hasFilter;

        if (hasFilter)
        {
            FilteredQueueCountText = $"({visibleCount}/{ComicBooks.Count})";
            FilteredHeaderCountText = $"Kết quả tìm kiếm: {visibleCount}";
        }
        else
        {
            FilteredQueueCountText = string.Empty;
            FilteredHeaderCountText = string.Empty;
        }
    }
}
