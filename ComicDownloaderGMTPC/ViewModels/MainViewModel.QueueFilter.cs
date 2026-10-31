using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    private string _queueSearchFilter = string.Empty;

    [ObservableProperty]
    private bool _isFilterDuplicatesOnly = false;

    [ObservableProperty]
    private bool _isFilterUncenOnly = false;

    [ObservableProperty]
    private string _filteredQueueCountText = string.Empty;

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

    public void ApplyQueueFilter()
    {
        string filter = (QueueSearchFilter ?? string.Empty).Trim();
        bool filterDup = IsFilterDuplicatesOnly;
        bool filterUncen = IsFilterUncenOnly;

        int visibleCount = 0;
        foreach (var book in ComicBooks)
        {
            if (book == null) continue;

            bool matchesText = string.IsNullOrWhiteSpace(filter) ||
                               (book.Title?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                               (book.Url?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true);

            bool matchesDup = !filterDup || (book.IsDuplicate || book.IsCensorshipColorDuplicate);
            bool matchesUncen = !filterUncen || (book.IsCensorshipUncensoredVariant);

            bool isVisible = matchesText && matchesDup && matchesUncen;
            book.IsFilteredOut = !isVisible;
            if (isVisible) visibleCount++;
        }

        if (!string.IsNullOrWhiteSpace(filter) || filterDup || filterUncen)
        {
            FilteredQueueCountText = $"({visibleCount}/{ComicBooks.Count})";
        }
        else
        {
            FilteredQueueCountText = string.Empty;
        }
    }
}
