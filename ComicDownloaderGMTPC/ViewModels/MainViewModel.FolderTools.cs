using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class MainViewModel
{
    private readonly FolderToolsService _folderTools = FolderToolsService.Instance;
    private CancellationTokenSource? _folderToolsCts;
    private readonly ConcurrentQueue<string> _folderLogBuffer = new();
    private System.Threading.Timer? _folderLogFlushTimer;

    // ==========================================
    // TÁCH / GỘP THEO SỐ LƯỢNG CHAPTER (SINGLE COMIC)
    // ==========================================
    [ObservableProperty]
    private string _folderSplitRootPath = string.Empty;

    [ObservableProperty]
    private int _folderSplitGroupSize = 200;

    [ObservableProperty]
    private string _folderSplitType = "chapter"; // "chapter" (chap 0001-0200) hoặc "book-chapter" (TenTruyen-chap 0001-0200)

    [ObservableProperty]
    private bool _isMergeRemainderFolder = true;

    // ==========================================
    // TÁCH / GỘP THEO BẢNG CHỮ CÁI (ALPHABET)
    // ==========================================
    [ObservableProperty]
    private string _folderAlphabetRootPath = string.Empty;

    [ObservableProperty]
    private bool _isAlphabetIgnoreLeadingTags = true;

    [ObservableProperty]
    private string _newAlphabetRangeInput = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _alphabetRanges = new() { "A-G", "H-P", "Q-Z" };

    // ==========================================
    // TIẾN TRÌNH & NHẬT KÝ
    // ==========================================
    [ObservableProperty]
    private bool _isFolderToolRunning = false;

    [ObservableProperty]
    private double _folderToolProgress = 0;

    [ObservableProperty]
    private string _folderToolProgressText = "0%";

    [ObservableProperty]
    private string _folderToolStatusText = "Sẵn sàng.";

    [ObservableProperty]
    private ObservableCollection<string> _folderToolLogs = new();

    private void InitFolderToolsService()
    {
        // 1. Nhận log từ service đưa vào Queue an toàn đa luồng
        _folderTools.LogEmitted += (lvl, msg) =>
        {
            _folderLogBuffer.Enqueue($"[{DateTime.Now:HH:mm:ss}] [{lvl}] {msg}");
        };

        // 2. Timer xả log định kỳ (60ms) theo mẻ để UI Thread không bao giờ bị nghẽn/đơ
        _folderLogFlushTimer = new System.Threading.Timer(_ =>
        {
            if (_folderLogBuffer.IsEmpty) return;

            var batch = new List<string>();
            while (batch.Count < 30 && _folderLogBuffer.TryDequeue(out var logItem))
            {
                batch.Add(logItem);
            }

            if (batch.Count > 0)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    for (int i = 0; i < batch.Count; i++)
                    {
                        FolderToolLogs.Insert(0, batch[i]);
                    }
                    while (FolderToolLogs.Count > 300)
                    {
                        FolderToolLogs.RemoveAt(FolderToolLogs.Count - 1);
                    }
                }, Avalonia.Threading.DispatcherPriority.Background);
            }
        }, null, 100, 60);

        // 3. Nhận sự kiện thay đổi tiến trình mượt mà
        _folderTools.ProgressChanged += (current, total, msg) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                double pct = 0;
                if (total > 0)
                {
                    pct = Math.Clamp((double)current / total * 100.0, 0.0, 100.0);
                    FolderToolProgress = pct;
                    FolderToolProgressText = $"{FolderToolProgress:F0}%";
                }
                FolderToolStatusText = msg;
                BackgroundExecutionService.Instance.ReportProgress("folder_tools", "Tách/Gộp Thư Mục", msg, pct, true);
            }, Avalonia.Threading.DispatcherPriority.Render);
        };

        // Gợi ý thư mục mặc định
        if (string.IsNullOrEmpty(FolderSplitRootPath)) FolderSplitRootPath = _downloadEngine.DownloadRoot;
        if (string.IsNullOrEmpty(FolderAlphabetRootPath)) FolderAlphabetRootPath = _downloadEngine.DownloadRoot;
    }

    [RelayCommand]
    public async Task BrowseFolderSplitRootAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel?.StorageProvider == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _langService.CurrentLanguage == "VI" ? "Chọn thư mục truyện gốc" : "Select Comic Root Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            string raw = folders[0].Path.LocalPath;
            FolderSplitRootPath = DownloadEngineService.NormalizeStoragePath(raw, folders[0].Name);
            AddLog("INFO", $"[Tách/Gộp] Đã chọn thư mục: {FolderSplitRootPath}");
        }
    }

    [RelayCommand]
    public void OpenFolderSplitRoot()
    {
        string path = string.IsNullOrWhiteSpace(FolderSplitRootPath) ? _downloadEngine.DownloadRoot : FolderSplitRootPath;
        _downloadEngine.OpenDirectoryInExplorer(path);
    }

    [RelayCommand]
    public async Task SplitByChapterCountAsync()
    {
        if (string.IsNullOrWhiteSpace(FolderSplitRootPath) || !Directory.Exists(FolderSplitRootPath))
        {
            AddLog("WARN", "[Tách/Gộp] Vui lòng chọn thư mục hợp lệ trước!");
            return;
        }

        if (FolderSplitGroupSize <= 0)
        {
            AddLog("WARN", "[Tách/Gộp] Số lượng chapter mỗi nhóm không hợp lệ!");
            return;
        }

        IsFolderToolRunning = true;
        FolderToolProgress = 0;
        FolderToolProgressText = "0%";
        FolderToolStatusText = "Đang tách thư mục theo số lượng chapter...";
        _folderToolsCts = new CancellationTokenSource();

        try
        {
            int count = await _folderTools.SplitByChapterCountAsync(
                FolderSplitRootPath,
                FolderSplitGroupSize,
                FolderSplitType,
                IsMergeRemainderFolder,
                _folderToolsCts.Token);

            FolderToolProgress = 100;
            FolderToolProgressText = "100%";
            FolderToolStatusText = $"Hoàn tất: Đã tách {count} chapter folders.";
            AddLog("SUCCESS", $"[Tách/Gộp] Hoàn tất tách {count} chapter folders tại {FolderSplitRootPath}");
            SoundNotificationService.Instance.PlayDownloadFinish();
        }
        catch (OperationCanceledException)
        {
            FolderToolStatusText = "Đã dừng bởi người dùng.";
        }
        catch (Exception ex)
        {
            FolderToolStatusText = "Lỗi: " + ex.Message;
            AddLog("ERROR", $"[Tách/Gộp Lỗi] {ex.Message}");
            SoundNotificationService.Instance.PlayDownloadError();
        }
        finally
        {
            IsFolderToolRunning = false;
            BackgroundExecutionService.Instance.CompleteTask("folder_tools", "Tách/Gộp Thư Mục", "Hoàn tất");
        }
    }

    [RelayCommand]
    public async Task MergeByChapterCountAsync()
    {
        if (string.IsNullOrWhiteSpace(FolderSplitRootPath) || !Directory.Exists(FolderSplitRootPath))
        {
            AddLog("WARN", "[Tách/Gộp] Vui lòng chọn thư mục hợp lệ trước!");
            return;
        }

        IsFolderToolRunning = true;
        FolderToolProgress = 0;
        FolderToolProgressText = "0%";
        FolderToolStatusText = "Đang gộp chapter về thư mục gốc...";
        _folderToolsCts = new CancellationTokenSource();

        try
        {
            int count = await _folderTools.MergeByChapterCountAsync(
                FolderSplitRootPath,
                _folderToolsCts.Token);

            FolderToolProgress = 100;
            FolderToolProgressText = "100%";
            FolderToolStatusText = $"Hoàn tất: Đã gộp {count} chapter folders về gốc.";
            AddLog("SUCCESS", $"[Tách/Gộp] Hoàn tất gộp {count} chapter folders về {FolderSplitRootPath}");
            SoundNotificationService.Instance.PlayDownloadFinish();
        }
        catch (OperationCanceledException)
        {
            FolderToolStatusText = "Đã dừng bởi người dùng.";
        }
        catch (Exception ex)
        {
            FolderToolStatusText = "Lỗi: " + ex.Message;
            AddLog("ERROR", $"[Tách/Gộp Lỗi] {ex.Message}");
            SoundNotificationService.Instance.PlayDownloadError();
        }
        finally
        {
            IsFolderToolRunning = false;
            BackgroundExecutionService.Instance.CompleteTask("folder_tools", "Tách/Gộp Thư Mục", "Hoàn tất");
        }
    }

    [RelayCommand]
    public async Task BrowseFolderAlphabetRootAsync()
    {
        var topLevel = GetTopLevel();
        if (topLevel?.StorageProvider == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _langService.CurrentLanguage == "VI" ? "Chọn thư mục gốc cần phân loại chữ cái" : "Select Alphabet Root Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            string raw = folders[0].Path.LocalPath;
            FolderAlphabetRootPath = DownloadEngineService.NormalizeStoragePath(raw, folders[0].Name);
            AddLog("INFO", $"[Alphabet] Đã chọn thư mục: {FolderAlphabetRootPath}");
        }
    }

    [RelayCommand]
    public void OpenFolderAlphabetRoot()
    {
        string path = string.IsNullOrWhiteSpace(FolderAlphabetRootPath) ? _downloadEngine.DownloadRoot : FolderAlphabetRootPath;
        _downloadEngine.OpenDirectoryInExplorer(path);
    }

    [RelayCommand]
    public void AddAlphabetRange()
    {
        string text = NewAlphabetRangeInput?.Trim()?.ToUpperInvariant() ?? "";
        if (!string.IsNullOrWhiteSpace(text) && !AlphabetRanges.Contains(text))
        {
            AlphabetRanges.Add(text);
            NewAlphabetRangeInput = string.Empty;
        }
    }

    [RelayCommand]
    public void RemoveAlphabetRange(string range)
    {
        if (AlphabetRanges.Contains(range))
        {
            AlphabetRanges.Remove(range);
        }
    }

    [RelayCommand]
    public void ResetAlphabetRanges()
    {
        AlphabetRanges.Clear();
        AlphabetRanges.Add("A-G");
        AlphabetRanges.Add("H-P");
        AlphabetRanges.Add("Q-Z");
    }

    [RelayCommand]
    public async Task SplitByAlphabetAsync()
    {
        if (string.IsNullOrWhiteSpace(FolderAlphabetRootPath) || !Directory.Exists(FolderAlphabetRootPath))
        {
            AddLog("WARN", "[Alphabet] Vui lòng chọn thư mục hợp lệ trước!");
            return;
        }

        if (AlphabetRanges.Count == 0)
        {
            AddLog("WARN", "[Alphabet] Vui lòng thiết lập ít nhất 1 khoảng chữ cái (ví dụ: A-G)!");
            return;
        }

        IsFolderToolRunning = true;
        FolderToolProgress = 0;
        FolderToolProgressText = "0%";
        FolderToolStatusText = "Đang phân loại thư mục theo chữ cái...";
        _folderToolsCts = new CancellationTokenSource();

        try
        {
            int count = await _folderTools.SplitByAlphabetAsync(
                FolderAlphabetRootPath,
                new List<string>(AlphabetRanges),
                IsAlphabetIgnoreLeadingTags,
                _folderToolsCts.Token);

            FolderToolProgress = 100;
            FolderToolProgressText = "100%";
            FolderToolStatusText = $"Hoàn tất: Đã phân loại {count} thư mục theo bảng chữ cái.";
            AddLog("SUCCESS", $"[Alphabet] Hoàn tất chia {count} thư mục theo chữ cái tại {FolderAlphabetRootPath}");
            SoundNotificationService.Instance.PlayDownloadFinish();
        }
        catch (OperationCanceledException)
        {
            FolderToolStatusText = "Đã dừng bởi người dùng.";
        }
        catch (Exception ex)
        {
            FolderToolStatusText = "Lỗi: " + ex.Message;
            AddLog("ERROR", $"[Alphabet Lỗi] {ex.Message}");
            SoundNotificationService.Instance.PlayDownloadError();
        }
        finally
        {
            IsFolderToolRunning = false;
            BackgroundExecutionService.Instance.CompleteTask("folder_tools", "Tách/Gộp Thư Mục", "Hoàn tất");
        }
    }

    [RelayCommand]
    public async Task MergeByAlphabetAsync()
    {
        if (string.IsNullOrWhiteSpace(FolderAlphabetRootPath) || !Directory.Exists(FolderAlphabetRootPath))
        {
            AddLog("WARN", "[Alphabet] Vui lòng chọn thư mục hợp lệ trước!");
            return;
        }

        IsFolderToolRunning = true;
        FolderToolProgress = 0;
        FolderToolProgressText = "0%";
        FolderToolStatusText = "Đang gộp các thư mục chữ cái về gốc...";
        _folderToolsCts = new CancellationTokenSource();

        try
        {
            int count = await _folderTools.MergeByAlphabetAsync(
                FolderAlphabetRootPath,
                new List<string>(AlphabetRanges),
                _folderToolsCts.Token);

            FolderToolProgress = 100;
            FolderToolProgressText = "100%";
            FolderToolStatusText = $"Hoàn tất: Đã gộp {count} thư mục về thư mục gốc.";
            AddLog("SUCCESS", $"[Alphabet] Hoàn tất gộp {count} thư mục về {FolderAlphabetRootPath}");
            SoundNotificationService.Instance.PlayDownloadFinish();
        }
        catch (OperationCanceledException)
        {
            FolderToolStatusText = "Đã dừng bởi người dùng.";
        }
        catch (Exception ex)
        {
            FolderToolStatusText = "Lỗi: " + ex.Message;
            AddLog("ERROR", $"[Alphabet Lỗi] {ex.Message}");
            SoundNotificationService.Instance.PlayDownloadError();
        }
        finally
        {
            IsFolderToolRunning = false;
            BackgroundExecutionService.Instance.CompleteTask("folder_tools", "Tách/Gộp Thư Mục", "Hoàn tất");
        }
    }

    [RelayCommand]
    public void StopFolderTool()
    {
        if (_folderToolsCts != null && !_folderToolsCts.IsCancellationRequested)
        {
            _folderToolsCts.Cancel();
            AddLog("WARN", "[Tách/Gộp] Đang gửi yêu cầu dừng...");
        }
    }

    [RelayCommand]
    public void ClearFolderToolLogs()
    {
        FolderToolLogs.Clear();
        while (_folderLogBuffer.TryDequeue(out _)) { }
    }
}
