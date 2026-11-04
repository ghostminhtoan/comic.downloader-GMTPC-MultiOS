// ====================================================================================================
// FILE: MainViewModel.QueueBatch.cs
// VAI TRÒ & MỤC ĐÍCH:
//   Tính năng chọn / hủy chọn dải truyện hàng loạt theo Part (Queue Batch Select Engine) trên Quick Float Button.
//   Cơ chế hoạt động chuẩn phong cách Dual Picture Sort:
//   - Chạm nút Start (Part X) trên Quick Float Button nổi -> Chuyển sang màu cam nổi bật chờ thao tác.
//   - Chạm truyện ĐẦU (Start) -> Ghi nhận điểm mốc, đổi text sang "Part X: Cuối".
//   - Chạm truyện CUỐI (End) -> Tự động xác định toàn bộ dải truyện nằm giữa và áp dụng:
//       + Nếu truyện ban đầu vốn đã tick sẵn HOẶC toàn bộ dải đã tick -> UNTICK (Hủy chọn dải).
//       + Nếu truyện ban đầu chưa tick -> TICK (Chọn dải).
//   - Hoàn tất Part X: Tự động tăng Part lên X+1, quay về trạng thái sẵn sàng tiếp theo.
// ====================================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicDownloaderGMTPC.Models;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    private int _queueBatchPartNumber = 1;

    [ObservableProperty]
    private bool _isQueueBatchModeActive = false;

    [ObservableProperty]
    private ComicBookItem? _queueBatchStartItem = null;

    private bool _queueBatchInitialStartWasChecked = false;

    public string QueueBatchButtonText
    {
        get
        {
            bool isVi = _langService.CurrentLanguage == "VI";
            if (!IsQueueBatchModeActive)
                return $"⚡ Part {QueueBatchPartNumber}";
            if (QueueBatchStartItem == null)
                return isVi ? $"📍 P{QueueBatchPartNumber}: Đầu" : $"📍 P{QueueBatchPartNumber}: Start";
            return isVi ? $"🏁 P{QueueBatchPartNumber}: Cuối" : $"🏁 P{QueueBatchPartNumber}: End";
        }
    }

    public string QueueBatchButtonBg => IsQueueBatchModeActive ? "#D97706" : "#1E293B";
    public string QueueBatchButtonBorder => IsQueueBatchModeActive ? "#F59E0B" : "#38BDF8";
    public string QueueBatchButtonFg => IsQueueBatchModeActive ? "#FFFFFF" : "#38BDF8";

    public void RefreshQueueBatchProperties()
    {
        OnPropertyChanged(nameof(QueueBatchButtonText));
        OnPropertyChanged(nameof(QueueBatchButtonBg));
        OnPropertyChanged(nameof(QueueBatchButtonBorder));
        OnPropertyChanged(nameof(QueueBatchButtonFg));
    }

    [RelayCommand]
    public void ToggleQueueBatchSelectMode()
    {
        if (IsQueueBatchModeActive)
        {
            // Bấm lại khi đang chờ -> Hủy part dở dang, khôi phục lại trạng thái truyện bắt đầu nếu có
            if (QueueBatchStartItem != null)
            {
                QueueBatchStartItem.IsChecked = _queueBatchInitialStartWasChecked;
            }
            IsQueueBatchModeActive = false;
            QueueBatchStartItem = null;
            AddLog("INFO", $"Đã hủy chọn dải Part {QueueBatchPartNumber}. Giữ nguyên các truyện đã chọn trước đó.");
        }
        else
        {
            IsQueueBatchModeActive = true;
            QueueBatchStartItem = null;
            AddLog("INFO", $"⚡ Bắt đầu Part {QueueBatchPartNumber}: Hãy chạm/click vào truyện BẮT ĐẦU trong danh sách hàng đợi.");
        }
        RefreshQueueBatchProperties();
    }

    public void HandleQueueBatchItemClick(ComicBookItem item)
    {
        if (!IsQueueBatchModeActive) return;

        if (QueueBatchStartItem == null)
        {
            // Chạm lần 1: Ghi nhận trạng thái gốc và chọn truyện bắt đầu Part X
            _queueBatchInitialStartWasChecked = item.IsChecked;
            QueueBatchStartItem = item;
            item.IsChecked = !_queueBatchInitialStartWasChecked; // Phản hồi thị giác tức thì
            string actionGoal = _queueBatchInitialStartWasChecked ? "BỎ CHỌN" : "CHỌN";
            AddLog("INFO", $"📍 Part {QueueBatchPartNumber} ({actionGoal}): Đã chọn truyện ĐẦU ('{item.Title}'). Hãy chạm/click truyện CUỐI.");
            RefreshQueueBatchProperties();
        }
        else
        {
            // Chạm lần 2: Chọn truyện kết thúc Part X
            var sourceList = ActiveQueueItemsSource.ToList();
            int idxStart = sourceList.IndexOf(QueueBatchStartItem);
            int idxEnd = sourceList.IndexOf(item);

            if (idxStart < 0 || idxEnd < 0)
            {
                idxStart = ComicBooks.IndexOf(QueueBatchStartItem);
                idxEnd = ComicBooks.IndexOf(item);
                sourceList = ComicBooks.ToList();
            }

            if (idxStart >= 0 && idxEnd >= 0)
            {
                int minIdx = Math.Min(idxStart, idxEnd);
                int maxIdx = Math.Max(idxStart, idxEnd);

                // Nếu truyện bắt đầu vốn đã tick sẵn HOẶC toàn bộ dải đều đã tick -> UNTICK (IsChecked = false)
                // Ngược lại -> TICK (IsChecked = true)
                bool allCurrentlyChecked = true;
                for (int i = minIdx; i <= maxIdx; i++)
                {
                    if (sourceList[i] != QueueBatchStartItem && !sourceList[i].IsChecked)
                    {
                        allCurrentlyChecked = false;
                        break;
                    }
                }

                bool targetChecked = !(_queueBatchInitialStartWasChecked || allCurrentlyChecked);

                for (int i = minIdx; i <= maxIdx; i++)
                {
                    sourceList[i].IsChecked = targetChecked;
                }

                int countChanged = maxIdx - minIdx + 1;
                string actionText = targetChecked ? "chọn" : "bỏ chọn";
                AddLog("SUCCESS", $"🏁 Part {QueueBatchPartNumber} hoàn tất: Đã {actionText} dải {countChanged} truyện (từ vị trí #{minIdx + 1} đến #{maxIdx + 1}). Hiện có {ComicBooks.Count(x => x.IsChecked)} truyện đang được chọn.");

                // Kết thúc Part X: Tăng Part, quay về trạng thái bình thường
                QueueBatchPartNumber++;
                QueueBatchStartItem = null;
                IsQueueBatchModeActive = false;
                RefreshQueueBatchProperties();
                UpdateStats();
                RequestQueueAutoSave(250);
            }
            else
            {
                _queueBatchInitialStartWasChecked = item.IsChecked;
                QueueBatchStartItem = item;
                item.IsChecked = !_queueBatchInitialStartWasChecked;
                AddLog("INFO", $"📍 Đã đổi mục: Đã chọn truyện ĐẦU ('{item.Title}'). Hãy chạm/click truyện CUỐI.");
                RefreshQueueBatchProperties();
            }
        }
    }
}

