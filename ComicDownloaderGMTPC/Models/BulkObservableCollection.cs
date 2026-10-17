using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ComicDownloaderGMTPC.Models;

/// <summary>
/// Bộ sưu tập ObservableCollection hiệu năng cao hỗ trợ nạp hàng loạt (Bulk / Batch Insert).
/// Với danh sách hàng ngàn trang (1,000 - 10,000 trang), phương thức AddRange chỉ phát ra DUY NHẤT 1 sự kiện Reset,
/// thay vì hàng ngàn sự kiện Add riêng lẻ, triệt tiêu 100% hiện tượng nghẽn UI Thread và đơ giao diện trên Android.
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;

    public void AddRange(IEnumerable<T> items)
    {
        if (items == null) return;

        _suppressNotification = true;
        try
        {
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }
        finally
        {
            _suppressNotification = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification)
        {
            base.OnCollectionChanged(e);
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotification)
        {
            base.OnPropertyChanged(e);
        }
    }
}

