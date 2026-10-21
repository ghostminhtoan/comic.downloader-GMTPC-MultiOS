// ====================================================================================================
// FILE SUMMARY: MainViewModel.ArchiveMerger.cs
// ----------------------------------------------------------------------------------------------------
// VAI TRÒ & MỤC ĐÍCH:
//   Partial ViewModel phụ trách tính năng Gộp File Lẻ Truyện (Archive Merger) - gom các chương truyện
//   riêng lẻ thành các tập sách lớn hoặc tập nén đa chương chuẩn quốc tế.
//
// CÁC TÍNH NĂNG & THÀNH PHẦN CHÍNH:
//   - Hỗ trợ nguồn vào đa dạng: PDF, ZIP, CBZ, RAR, CBR, MOBI.
//   - Mốc gom nhóm linh hoạt: 100 chap, 200 chap, 300 chap, Tất cả vào 1 file, hoặc số chapter tùy chỉnh.
//   - Định dạng xuất chuẩn: CBZ (.cbz), ZIP (.zip), PDF (.pdf có mục lục bookmarks từng chap), MOBI (.mobi Kindle).
//   - Tự động hóa thông minh: Tự động điền thư mục lưu 'merged' cùng cấp, tự động tạo thư mục khi mở.
//   - Tự động nhận diện chapter: Gom số thập phân (100.1, 100.5) vào mốc nguyên, biến thiên file cuối theo chap cuối.
//
// LƯU Ý KỸ THUẬT & QUY TẮC BẢO TRÌ:
//   - Tự động kích hoạt cờ IsMergeCustomChunk = true khi người dùng thay đổi giá trị ô số tùy chỉnh.
//   - Đảm bảo CancellationTokenSource được truyền xuyên suốt quá trình nén để hỗ trợ dừng tác vụ tức thì.
// ====================================================================================================

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

    partial void OnMergeCustomChunkInputChanged(decimal value)
    {
        IsMergeCustomChunk = true;
    }

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
            if (string.IsNullOrEmpty(MergeOutputFolderPath) && !string.IsNullOrWhiteSpace(MergeInputFolderPath))
            {
                MergeOutputFolderPath = ComputeDefaultMergeOutputFolder(MergeInputFolderPath);
            }
        }
        catch { }
    }

    public static string ComputeDefaultMergeOutputFolder(string inputFolder)
    {
        if (string.IsNullOrWhiteSpace(inputFolder)) return string.Empty;
        try
        {
            string clean = inputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(clean);
            if (parent != null && !string.IsNullOrWhiteSpace(parent.FullName))
            {
                return Path.Combine(parent.FullName, "merged");
            }
            return Path.Combine(clean, "merged");
        }
        catch
        {
            return Path.Combine(inputFolder, "merged");
        }
    }

    partial void OnMergeInputFolderPathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        // Tự động điền thư mục lưu 'merged' nếu ô lưu đang trống hoặc đang là thư mục merged tự động sinh trước đó
        if (string.IsNullOrWhiteSpace(MergeOutputFolderPath) ||
            MergeOutputFolderPath.EndsWith(Path.DirectorySeparatorChar + "merged", StringComparison.OrdinalIgnoreCase) ||
            MergeOutputFolderPath.EndsWith(Path.AltDirectorySeparatorChar + "merged", StringComparison.OrdinalIgnoreCase))
        {
            string autoMerge = ComputeDefaultMergeOutputFolder(value);
            if (!string.IsNullOrWhiteSpace(autoMerge))
            {
                MergeOutputFolderPath = autoMerge;
            }
        }
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
                    MergeOutputFolderPath = ComputeDefaultMergeOutputFolder(normalized);
                    AddLog("INFO", $"[Gộp File Lẻ] Đã chọn thư mục nguồn: {MergeInputFolderPath}");
                    AddLog("INFO", $"[Gộp File Lẻ] Đã tự động điền thư mục lưu file gộp: {MergeOutputFolderPath}");
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
            ? (string.IsNullOrWhiteSpace(MergeInputFolderPath) ? _downloadEngine.DownloadRoot : ComputeDefaultMergeOutputFolder(MergeInputFolderPath))
            : MergeOutputFolderPath;

        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }
        catch { }

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

    // ==========================================
    // CẤU HÌNH CHẾ ĐỘ NỐI CHAP (APPEND MODE)
    // ==========================================
    [ObservableProperty]
    private bool _isMergeAppendMode = false;

    [ObservableProperty]
    private string _mergeAppendBaseFilePath = string.Empty;

    [ObservableProperty]
    private string _mergeAppendBaseFileInfoText = string.Empty;

    [ObservableProperty]
    private string _mergeAppendNewFolderPath = string.Empty;

    [ObservableProperty]
    private string _mergeAppendNewFolderInfoText = string.Empty;

    [ObservableProperty]
    private bool _isMergeAppendOverwrite = true;

    [ObservableProperty]
    private string _mergeAppendOutputFilePath = string.Empty;

    [ObservableProperty]
    private bool _isMergeAppendPreserveBookmarks = true;

    [RelayCommand]
    public void SetMergeModeBatch() => IsMergeAppendMode = false;

    [RelayCommand]
    public void SetMergeModeAppend() => IsMergeAppendMode = true;

    partial void OnMergeAppendBaseFilePathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !File.Exists(value))
        {
            MergeAppendBaseFileInfoText = string.Empty;
            return;
        }

        try
        {
            MergeAppendBaseFileInfoText = _archiveMergeService.AnalyzeBaseFile(value);
            string dir = Path.GetDirectoryName(value) ?? string.Empty;
            string nameNoExt = Path.GetFileNameWithoutExtension(value);
            string ext = Path.GetExtension(value);
            if (!string.IsNullOrEmpty(dir))
            {
                MergeAppendOutputFilePath = Path.Combine(dir, $"{nameNoExt}_updated{ext}");
            }
        }
        catch { }
    }

    partial void OnMergeAppendNewFolderPathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Directory.Exists(value))
        {
            MergeAppendNewFolderInfoText = string.Empty;
            return;
        }

        try
        {
            MergeAppendNewFolderInfoText = _archiveMergeService.AnalyzeNewFolder(value);
        }
        catch { }
    }

    [RelayCommand]
    public async Task BrowseMergeAppendBaseFileAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = _langService.CurrentLanguage == "VI"
                    ? "Chọn file gốc cần nối chapter (.pdf, .cbz, .zip, .mobi)"
                    : "Select Base Comic File To Append Chapters (.pdf, .cbz, .zip, .mobi)",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new FilePickerFileType("Tệp truyện đóng gói (*.pdf, *.cbz, *.zip, *.mobi)")
                    {
                        Patterns = new[] { "*.pdf", "*.cbz", "*.zip", "*.mobi" }
                    },
                    FilePickerFileTypes.All
                }
            });

            if (files != null && files.Count > 0)
            {
                var selected = files[0];
                string? raw = selected.TryGetLocalPath() ?? selected.Path?.LocalPath ?? selected.Path?.ToString();
                string normalized = DownloadEngineService.NormalizeStoragePath(raw, selected.Name);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    MergeAppendBaseFilePath = normalized;
                    AddLog("INFO", $"[Nối Chap] Đã chọn tệp gốc: {MergeAppendBaseFilePath}");
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"[Nối Chap] Lỗi khi chọn tệp gốc: {ex.Message}");
        }
    }

    [RelayCommand]
    public void OpenMergeAppendBaseFileFolder()
    {
        if (string.IsNullOrWhiteSpace(MergeAppendBaseFilePath) || !File.Exists(MergeAppendBaseFilePath))
        {
            _downloadEngine.OpenDirectoryInExplorer(_downloadEngine.DownloadRoot);
            return;
        }

        string? dir = Path.GetDirectoryName(MergeAppendBaseFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            _downloadEngine.OpenDirectoryInExplorer(dir);
        }
    }

    [RelayCommand]
    public async Task BrowseMergeAppendNewFolderAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = _langService.CurrentLanguage == "VI"
                    ? "Chọn thư mục chứa các chapter mới cần nối vào file"
                    : "Select Folder Containing New Chapters To Append",
                AllowMultiple = false
            });

            if (folders != null && folders.Count > 0)
            {
                var selected = folders[0];
                string? raw = selected.TryGetLocalPath() ?? selected.Path?.LocalPath ?? selected.Path?.ToString();
                string normalized = DownloadEngineService.NormalizeStoragePath(raw, selected.Name);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    MergeAppendNewFolderPath = normalized;
                    AddLog("INFO", $"[Nối Chap] Đã chọn thư mục chapter mới: {MergeAppendNewFolderPath}");
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"[Nối Chap] Lỗi khi chọn thư mục chapter mới: {ex.Message}");
        }
    }

    [RelayCommand]
    public void OpenMergeAppendNewFolder()
    {
        string path = string.IsNullOrWhiteSpace(MergeAppendNewFolderPath) ? _downloadEngine.DownloadRoot : MergeAppendNewFolderPath;
        _downloadEngine.OpenDirectoryInExplorer(path);
    }

    [RelayCommand]
    public async Task BrowseMergeAppendOutputFileAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider == null) return;

            string ext = !string.IsNullOrWhiteSpace(MergeAppendBaseFilePath)
                ? Path.GetExtension(MergeAppendBaseFilePath).TrimStart('.')
                : "pdf";

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = _langService.CurrentLanguage == "VI" ? "Chọn vị trí lưu tệp kết quả sau khi nối" : "Select Output Location For Appended File",
                DefaultExtension = ext,
                SuggestedFileName = !string.IsNullOrWhiteSpace(MergeAppendBaseFilePath)
                    ? $"{Path.GetFileNameWithoutExtension(MergeAppendBaseFilePath)}_updated.{ext}"
                    : $"comic_updated.{ext}"
            });

            if (file != null)
            {
                string? raw = file.TryGetLocalPath() ?? file.Path?.LocalPath ?? file.Path?.ToString();
                string normalized = DownloadEngineService.NormalizeStoragePath(raw, file.Name);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    MergeAppendOutputFilePath = normalized;
                    AddLog("INFO", $"[Nối Chap] Đã chọn vị trí lưu tệp kết quả: {MergeAppendOutputFilePath}");
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"[Nối Chap] Lỗi khi chọn vị trí lưu: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task StartMergeAppendAsync()
    {
        if (IsMergingArchives)
        {
            AddLog("WARN", "[Nối Chap] Đang có tiến trình gộp file đang chạy!");
            return;
        }

        if (string.IsNullOrWhiteSpace(MergeAppendBaseFilePath) || !File.Exists(MergeAppendBaseFilePath))
        {
            AddLog("WARN", "[Nối Chap] Vui lòng chọn tệp gốc (.pdf, .cbz, .zip, .mobi) hợp lệ!");
            return;
        }

        if (string.IsNullOrWhiteSpace(MergeAppendNewFolderPath) || !Directory.Exists(MergeAppendNewFolderPath))
        {
            AddLog("WARN", "[Nối Chap] Vui lòng chọn thư mục chứa các chapter mới hợp lệ!");
            return;
        }

        IsMergingArchives = true;
        MergeProgress = 0;
        MergeProgressText = "0%";
        MergeStatusText = "Đang khởi tạo tiến trình nối chapter...";
        IsMergeCompletedVisible = false;
        _mergeCts = new CancellationTokenSource();

        BackgroundExecutionService.Instance.ReportProgress("archive_merger", "Nối Chap Vào File", "Bắt đầu nối chapter...", 0, true);

        var progressHandler = new Progress<(int current, int total, string message)>(p =>
        {
            double pct = p.total > 0 ? Math.Clamp((double)p.current / p.total * 100.0, 0, 100) : 0;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                MergeProgress = pct;
                MergeProgressText = $"{pct:F0}%";
                MergeStatusText = p.message;
            }, Avalonia.Threading.DispatcherPriority.Background);

            BackgroundExecutionService.Instance.ReportProgress("archive_merger", "Nối Chap Vào File", p.message, pct, true);
        });

        try
        {
            AddLog("INFO", $"[Nối Chap] Bắt đầu nối chapter từ [{Path.GetFileName(MergeAppendNewFolderPath)}] vào tệp [{Path.GetFileName(MergeAppendBaseFilePath)}]...");

            var (success, msg, outPath) = await _archiveMergeService.AppendChaptersToFileAsync(
                MergeAppendBaseFilePath,
                MergeAppendNewFolderPath,
                IsMergeAppendOverwrite,
                MergeAppendOutputFilePath,
                IsMergeAppendPreserveBookmarks,
                progressHandler,
                _mergeCts.Token);

            if (success)
            {
                MergeProgress = 100;
                MergeProgressText = "100%";
                MergeStatusText = $"Hoàn tất: {msg}";
                MergeCompletedMessage = $"Nối chương thành công!\n\n{msg}\n\nVị trí tệp:\n{outPath}";
                IsMergeCompletedVisible = true;

                // Tự động phân tích lại tệp sau khi ghi đè để cập nhật UI
                if (IsMergeAppendOverwrite)
                {
                    MergeAppendBaseFileInfoText = _archiveMergeService.AnalyzeBaseFile(MergeAppendBaseFilePath);
                }

                AddLog("SUCCESS", $"[Nối Chap] Hoàn tất! Tệp: {outPath}");
                try { SoundNotificationService.Instance.PlayDownloadFinish(); } catch { }
            }
            else
            {
                MergeStatusText = $"Thất bại: {msg}";
                AddLog("ERROR", $"[Nối Chap Thất Bại] {msg}");
                try { SoundNotificationService.Instance.PlayDownloadError(); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            MergeStatusText = "Đã dừng bởi người dùng.";
            AddLog("WARN", "[Nối Chap] Người dùng đã dừng tiến trình.");
        }
        catch (Exception ex)
        {
            MergeStatusText = "Lỗi: " + ex.Message;
            AddLog("ERROR", $"[Nối Chap Lỗi] {ex.Message}");
            try { SoundNotificationService.Instance.PlayDownloadError(); } catch { }
        }
        finally
        {
            IsMergingArchives = false;
            BackgroundExecutionService.Instance.CompleteTask("archive_merger", "Nối Chap Vào File", "Hoàn tất");
        }
    }

}