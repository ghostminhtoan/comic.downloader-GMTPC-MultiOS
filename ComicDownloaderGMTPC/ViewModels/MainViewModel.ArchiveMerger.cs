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
    private readonly ArchiveMergeService _archiveMergeService = ArchiveMergeService.Instance;
    private CancellationTokenSource? _mergeCts;
    private readonly ConcurrentQueue<string> _mergeLogBuffer = new();
    private System.Threading.Timer? _mergeLogFlushTimer;

    // ==========================================
    // CẤU HÌNH GỘP FILE LẺ TRUYỆN (ARCHIVE MERGER)
    // ==========================================
    [ObservableProperty]
    private string _mergeInputFolderPath = string.Empty;

    [ObservableProperty]
    private string _mergeOutputFolderPath = string.Empty;

    /// <summary>
    /// Số lượng chapter mỗi file: 100, 200, 300 hoặc 0 (Tất cả thành 1 file)
    /// </summary>
    [ObservableProperty]
    private int _mergeChapterChunkSize = 100;

    [ObservableProperty]
    private bool _isMergeCustomChunk = false;

    [ObservableProperty]
    private decimal _mergeCustomChunkInput = 50m;

    [ObservableProperty]
    private bool _isMergeFormatCbz = true;

    [ObservableProperty]
    private bool _isMergeFormatZip = false;

    [ObservableProperty]
    private bool _isMergeFormatPdf = false;

    [ObservableProperty]
    private bool _isMergeFormatMobi = false;

    partial void OnIsMergeFormatCbzChanged(bool value)
    {
        if (value)
        {
            if (IsMergeFormatZip) IsMergeFormatZip = false;
            if (IsMergeFormatPdf) IsMergeFormatPdf = false;
            if (IsMergeFormatMobi) IsMergeFormatMobi = false;
        }
    }

    partial void OnIsMergeFormatZipChanged(bool value)
    {
        if (value)
        {
            if (IsMergeFormatCbz) IsMergeFormatCbz = false;
            if (IsMergeFormatPdf) IsMergeFormatPdf = false;
            if (IsMergeFormatMobi) IsMergeFormatMobi = false;
        }
    }

    partial void OnIsMergeFormatPdfChanged(bool value)
    {
        if (value)
        {
            if (IsMergeFormatCbz) IsMergeFormatCbz = false;
            if (IsMergeFormatZip) IsMergeFormatZip = false;
            if (IsMergeFormatMobi) IsMergeFormatMobi = false;
        }
    }

    partial void OnIsMergeFormatMobiChanged(bool value)
    {
        if (value)
        {
            if (IsMergeFormatCbz) IsMergeFormatCbz = false;
            if (IsMergeFormatZip) IsMergeFormatZip = false;
            if (IsMergeFormatPdf) IsMergeFormatPdf = false;
        }
    }

    // ==========================================
    // TRẠNG THÁI & TIẾN TRÌNH
    // ==========================================
    [ObservableProperty]
    private bool _isMergingArchives = false;

    [ObservableProperty]
    private double _mergeProgress = 0;

    [ObservableProperty]
    private string _mergeProgressText = "0%";

    [ObservableProperty]
    private string _mergeStatusText = "Sẵn sàng.";

    [ObservableProperty]
    private ObservableCollection<string> _mergeLogs = new();

    [ObservableProperty]
    private bool _isMergeCompletedVisible = false;

    [ObservableProperty]
    private string _mergeCompletedMessage = string.Empty;

    public void InitArchiveMerger()
    {
        try
        {
            // 1. Nhận log từ service đưa vào Queue an toàn đa luồng
            _archiveMergeService.LogEmitted = (lvl, msg) =>
            {
                try
                {
                    _mergeLogBuffer.Enqueue($"[{DateTime.Now:HH:mm:ss}] [{lvl}] {msg}");
                }
                catch { }
            };

            // 2. Timer xả log định kỳ (200ms) theo mẻ để UI Thread luôn mượt mà
            _mergeLogFlushTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (_mergeLogBuffer.IsEmpty) return;

                    var batch = new List<string>();
                    while (batch.Count < 20 && _mergeLogBuffer.TryDequeue(out var logItem))
                    {
                        batch.Add(logItem);
                    }

                    if (batch.Count > 0)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                for (int i = 0; i < batch.Count; i++)
                                {
                                    MergeLogs.Insert(0, batch[i]);
                                }
                                while (MergeLogs.Count > 150)
                                {
                                    MergeLogs.RemoveAt(MergeLogs.Count - 1);
                                }
                            }
                            catch { }
                        }, Avalonia.Threading.DispatcherPriority.Background);
                    }
                }
                catch { }
            }, null, 200, 200);

            if (string.IsNullOrEmpty(MergeInputFolderPath))
            {
                MergeInputFolderPath = _downloadEngine.DownloadRoot;
            }
        }
        catch { }
    }

    [RelayCommand]
    public async Task BrowseMergeInputFolderAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = _langService.CurrentLanguage == "VI" ? "Chọn thư mục chứa các file chương lẻ cần gộp" : "Select Comic Folder Containing Chapter Files",
                AllowMultiple = false
            });

            if (folders != null && folders.Count > 0)
            {
                var selected = folders[0];
                string? raw = selected.TryGetLocalPath() ?? selected.Path?.LocalPath ?? selected.Path?.ToString();
                string normalized = DownloadEngineService.NormalizeStoragePath(raw, selected.Name);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    MergeInputFolderPath = normalized;
                    AddLog("INFO", $"[Gộp File Lẻ] Đã chọn thư mục nguồn: {MergeInputFolderPath}");
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"[Gộp File Lẻ] Lỗi khi chọn thư mục nguồn: {ex.Message}");
        }
    }

    [RelayCommand]
    public void OpenMergeInputFolder()
    {
        string path = string.IsNullOrWhiteSpace(MergeInputFolderPath) ? _downloadEngine.DownloadRoot : MergeInputFolderPath;
        _downloadEngine.OpenDirectoryInExplorer(path);
    }

    [RelayCommand]
    public async Task BrowseMergeOutputFolderAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = _langService.CurrentLanguage == "VI" ? "Chọn thư mục lưu file sau khi gộp" : "Select Output Folder For Merged Files",
                AllowMultiple = false
            });

            if (folders != null && folders.Count > 0)
            {
                var selected = folders[0];
                string? raw = selected.TryGetLocalPath() ?? selected.Path?.LocalPath ?? selected.Path?.ToString();
                string normalized = DownloadEngineService.NormalizeStoragePath(raw, selected.Name);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    MergeOutputFolderPath = normalized;
                    AddLog("INFO", $"[Gộp File Lẻ] Đã chọn thư mục lưu: {MergeOutputFolderPath}");
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"[Gộp File Lẻ] Lỗi khi chọn thư mục lưu: {ex.Message}");
        }
    }

    [RelayCommand]
    public void OpenMergeOutputFolder()
    {
        string path = string.IsNullOrWhiteSpace(MergeOutputFolderPath)
            ? (string.IsNullOrWhiteSpace(MergeInputFolderPath) ? _downloadEngine.DownloadRoot : Path.Combine(MergeInputFolderPath, "merged"))
            : MergeOutputFolderPath;
        _downloadEngine.OpenDirectoryInExplorer(path);
    }

    [RelayCommand]
    public void ResetAndroidFolderOpener()
    {
        _downloadEngine.RequestResetAndroidFolderOpener();
        AddLog("INFO", "[Android] Đã đặt lại cấu hình ứng dụng mở thư mục mặc định.");
    }

    [RelayCommand]
    public void SetMergeChunk100() => SetMergeChunk(100);

    [RelayCommand]
    public void SetMergeChunk200() => SetMergeChunk(200);

    [RelayCommand]
    public void SetMergeChunk300() => SetMergeChunk(300);

    [RelayCommand]
    public void SetMergeChunkAll() => SetMergeChunk(0);

    public void SetMergeChunk(int size)
    {
        MergeChapterChunkSize = size;
        IsMergeCustomChunk = false;
    }

    [RelayCommand]
    public void EnableMergeCustomChunk()
    {
        IsMergeCustomChunk = true;
    }

    [RelayCommand]
    public async Task StartArchiveMergeAsync()
    {
        if (IsMergingArchives)
        {
            AddLog("WARN", "[Gộp File Lẻ] Tiến trình gộp file đang chạy!");
            return;
        }

        if (string.IsNullOrWhiteSpace(MergeInputFolderPath) || !Directory.Exists(MergeInputFolderPath))
        {
            AddLog("WARN", "[Gộp File Lẻ] Vui lòng chọn thư mục chứa các file chương lẻ hợp lệ!");
            return;
        }

        int finalChunk = IsMergeCustomChunk ? Math.Max(1, (int)MergeCustomChunkInput) : MergeChapterChunkSize;

        IsMergingArchives = true;
        MergeProgress = 0;
        MergeProgressText = "0%";
        MergeStatusText = "Đang quét các file chương lẻ...";
        IsMergeCompletedVisible = false;
        _mergeCts = new CancellationTokenSource();

        BackgroundExecutionService.Instance.ReportProgress("archive_merger", "Gộp File Lẻ Truyện", "Đang gộp file...", 0, true);

        var progressHandler = new Progress<(int current, int total, string message)>(p =>
        {
            double pct = p.total > 0 ? Math.Clamp((double)p.current / p.total * 100.0, 0, 100) : 0;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                MergeProgress = pct;
                MergeProgressText = $"{pct:F0}%";
                MergeStatusText = p.message;
            }, Avalonia.Threading.DispatcherPriority.Background);

            BackgroundExecutionService.Instance.ReportProgress("archive_merger", "Gộp File Lẻ Truyện", p.message, pct, true);
        });

        try
        {
            string chosenFormat = IsMergeFormatPdf ? ".pdf"
                : IsMergeFormatMobi ? ".mobi"
                : IsMergeFormatZip ? ".zip"
                : ".cbz";

            var options = new ArchiveMergeOptions
            {
                InputFolder = MergeInputFolderPath,
                OutputFolder = MergeOutputFolderPath,
                ChapterChunkSize = finalChunk,
                OutputFormat = chosenFormat,
                Compression = System.IO.Compression.CompressionLevel.Fastest
            };

            var (fileCount, errorCount, outDir) = await _archiveMergeService.MergeArchivesAsync(options, progressHandler, _mergeCts.Token);

            MergeProgress = 100;
            MergeProgressText = "100%";
            MergeStatusText = $"Hoàn tất: Đã tạo {fileCount} file lớn tại {outDir}. (Lỗi: {errorCount})";
            MergeCompletedMessage = $"Đã gộp thành công {fileCount} file lớn ({options.OutputFormat}) tại:\n{outDir}";
            IsMergeCompletedVisible = true;

            AddLog("SUCCESS", $"[Gộp File Lẻ] Hoàn tất gộp {fileCount} file lớn tại: {outDir}");
            try { SoundNotificationService.Instance.PlayDownloadFinish(); } catch { }
        }
        catch (OperationCanceledException)
        {
            MergeStatusText = "Đã dừng bởi người dùng.";
            AddLog("WARN", "[Gộp File Lẻ] Người dùng đã dừng tiến trình gộp file.");
        }
        catch (Exception ex)
        {
            MergeStatusText = "Lỗi: " + ex.Message;
            AddLog("ERROR", $"[Gộp File Lẻ Lỗi] {ex.Message}");
            try { SoundNotificationService.Instance.PlayDownloadError(); } catch { }
        }
        finally
        {
            IsMergingArchives = false;
            BackgroundExecutionService.Instance.CompleteTask("archive_merger", "Gộp File Lẻ Truyện", "Hoàn tất");
        }
    }

    [RelayCommand]
    public void StopArchiveMerge()
    {
        if (_mergeCts != null && !_mergeCts.IsCancellationRequested)
        {
            _mergeCts.Cancel();
            AddLog("WARN", "[Gộp File Lẻ] Đang gửi yêu cầu dừng...");
        }
    }

    [RelayCommand]
    public void ClearMergeLogs()
    {
        MergeLogs.Clear();
        while (_mergeLogBuffer.TryDequeue(out _)) { }
    }

    [RelayCommand]
    public void DismissMergeCompleted()
    {
        IsMergeCompletedVisible = false;
    }
}

