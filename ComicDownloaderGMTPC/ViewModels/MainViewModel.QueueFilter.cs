using System;
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
