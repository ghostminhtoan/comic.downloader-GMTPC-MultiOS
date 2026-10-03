using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class MainViewModel
{
    private readonly BatchRenameService _batchRenameService = BatchRenameService.Instance;

    [ObservableProperty]
    private ObservableCollection<RenameItem> _renameItems = new();

    [ObservableProperty]
    private ObservableCollection<RenameMethodBase> _renameMethods = new();

    [ObservableProperty]
    private RenameMethodBase? _selectedRenameMethod;

    [ObservableProperty]
    private RenameItem? _selectedRenameItem;

    [ObservableProperty]
    private bool _isRenamingBatch = false;

    [ObservableProperty]
    private string _renameStatusMessage = "Sẵn sàng đổi tên (hỗ trợ kéo thả hoặc bấm nút Thêm).";

    [ObservableProperty]
    private bool _canUndoRename = false;

    [ObservableProperty]
    private int _renameItemsValidCount = 0;

    [ObservableProperty]
    private int _renameItemsChangedCount = 0;

    [ObservableProperty]
    private string _newMethodSelection = "NewName"; // "NewName", "Replace", "Renumber", "Case", "Remove", "OptimizeZero"

    [ObservableProperty]
    private string _renameTargetMode = "Files"; // "Files", "Folders", "All"

    [ObservableProperty]
    private bool _isRecursiveScan = true; // Quét đa tầng tất cả thư mục con

    [ObservableProperty]
    private string _manualRenameFolderPath = string.Empty;

    public void RefreshRenamePreview()
    {
        _batchRenameService.UpdatePreview(RenameItems, RenameMethods);
        RenameItemsValidCount = RenameItems.Count(it => it.IsValid);
        RenameItemsChangedCount = RenameItems.Count(it => it.IsChanged);
    }

    private static string ResolveStorageItemPath(IStorageItem item)
    {
        string? raw = item.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(raw) && item.Path != null)
        {
            raw = item.Path.IsAbsoluteUri ? item.Path.LocalPath : item.Path.ToString();
        }
        return DownloadEngineService.NormalizeStoragePath(raw, item.Name);
    }

    private int ScanFolderInternal(string rootDir, bool recursive, string targetMode)
    {
        int added = 0;
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir)) return 0;

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        // 1. Quét Thư mục nếu targetMode là Folders hoặc All
        if (targetMode.Equals("Folders", StringComparison.OrdinalIgnoreCase) || targetMode.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            // Thêm chính rootDir nếu chưa có
            if (!RenameItems.Any(it => it.OriginalPath.Equals(rootDir, StringComparison.OrdinalIgnoreCase)))
            {
                RenameItems.Add(new RenameItem
                {
                    OriginalPath = rootDir,
                    IsDirectory = true
                });
                added++;
            }

            try
            {
                var directories = Directory.GetDirectories(rootDir, "*", searchOption);
                foreach (var dir in directories)
                {
                    string dirName = Path.GetFileName(dir);
                    if (dirName.StartsWith(".") || dirName.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || dirName.Equals(".portable", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!RenameItems.Any(it => it.OriginalPath.Equals(dir, StringComparison.OrdinalIgnoreCase)))
                    {
                        RenameItems.Add(new RenameItem
                        {
                            OriginalPath = dir,
                            IsDirectory = true
                        });
                        added++;
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"[Rename] Lỗi quét thư mục con tại '{rootDir}': {ex.Message}");
            }
        }

        // 2. Quét File nếu targetMode là Files hoặc All
        if (targetMode.Equals("Files", StringComparison.OrdinalIgnoreCase) || targetMode.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var files = Directory.GetFiles(rootDir, "*.*", searchOption);
                foreach (var file in files)
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.StartsWith(".")) continue;

                    if (!RenameItems.Any(it => it.OriginalPath.Equals(file, StringComparison.OrdinalIgnoreCase)))
                    {
                        RenameItems.Add(new RenameItem
                        {
                            OriginalPath = file,
                            IsDirectory = false
                        });
                        added++;
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"[Rename] Lỗi quét file tại '{rootDir}': {ex.Message}");
            }
        }

        return added;
    }

    [RelayCommand]
    public async Task AddRenameFilesAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null)
            {
                var options = new FilePickerOpenOptions
                {
                    Title = "Chọn Các File Cần Đổi Tên",
                    AllowMultiple = true
                };

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);
                if (files != null && files.Count > 0)
                {
                    int added = 0;
                    foreach (var f in files)
                    {
                        string path = ResolveStorageItemPath(f);
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        {
                            if (!RenameItems.Any(it => it.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                            {
                                RenameItems.Add(new RenameItem
                                {
                                    OriginalPath = path,
                                    IsDirectory = false
                                });
                                added++;
                            }
                        }
                    }
                    RefreshRenamePreview();
                    RenameStatusMessage = $"Đã thêm {added} file vào danh sách.";
                }
            }
        }
        catch (Exception ex)
        {
            RenameStatusMessage = $"Lỗi chọn file: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task AddRenameFolderAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null)
            {
                var options = new FolderPickerOpenOptions
                {
                    Title = "Chọn Thư Mục Để Nạp Đổi Tên (Hỗ Trợ Quét Đa Tầng)",
                    AllowMultiple = true
                };

                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
                if (folders != null && folders.Count > 0)
                {
                    int addedCount = 0;
                    foreach (var f in folders)
                    {
                        string path = ResolveStorageItemPath(f);
                        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                        {
                            ManualRenameFolderPath = path;
                            addedCount += ScanFolderInternal(path, IsRecursiveScan, RenameTargetMode);
                        }
                    }
                    RefreshRenamePreview();
                    RenameStatusMessage = $"Đã nạp {addedCount} mục vào danh sách đổi tên (Đa tầng: {(IsRecursiveScan ? "BẬT" : "TẮT")}).";
                }
            }
        }
        catch (Exception ex)
        {
            RenameStatusMessage = $"Lỗi chọn thư mục: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task BrowseManualRenameFolderAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null)
            {
                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Chọn Thư Mục Quét Đổi Tên",
                    AllowMultiple = false
                });

                if (folders != null && folders.Count > 0)
                {
                    string path = ResolveStorageItemPath(folders[0]);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        ManualRenameFolderPath = path;
                        ScanManualRenameFolder();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RenameStatusMessage = $"Lỗi chọn thư mục: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ScanManualRenameFolder()
    {
        string path = (ManualRenameFolderPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            RenameStatusMessage = "Vui lòng nhập hoặc chọn đường dẫn thư mục!";
            return;
        }

        path = DownloadEngineService.NormalizeStoragePath(path);
        if (!Directory.Exists(path))
        {
            RenameStatusMessage = "Thư mục không tồn tại!";
            return;
        }

        ManualRenameFolderPath = path;
        int added = ScanFolderInternal(path, IsRecursiveScan, RenameTargetMode);
        RefreshRenamePreview();
        RenameStatusMessage = $"Đã quét và nạp {added} mục từ '{path}' (Đa tầng: {(IsRecursiveScan ? "BẬT" : "TẮT")}).";
    }

    public void AddDroppedPathsToRename(IEnumerable<string> paths)
    {
        if (paths == null) return;
        int added = 0;
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            string clean = DownloadEngineService.NormalizeStoragePath(p);

            if (File.Exists(clean))
            {
                if (!RenameItems.Any(it => it.OriginalPath.Equals(clean, StringComparison.OrdinalIgnoreCase)))
                {
                    RenameItems.Add(new RenameItem { OriginalPath = clean, IsDirectory = false });
                    added++;
                }
            }
            else if (Directory.Exists(clean))
            {
                ManualRenameFolderPath = clean;
                added += ScanFolderInternal(clean, IsRecursiveScan, RenameTargetMode);
            }
        }

        if (added > 0)
        {
            RefreshRenamePreview();
            RenameStatusMessage = $"Đã kéo thả thêm {added} mục vào danh sách (Đa tầng: {(IsRecursiveScan ? "BẬT" : "TẮT")}).";
        }
    }

    [RelayCommand]
    public void ClearRenameItems()
    {
        RenameItems.Clear();
        RefreshRenamePreview();
        RenameStatusMessage = "Đã xóa toàn bộ danh sách file/folder.";
    }

    [RelayCommand]
    public void RemoveSelectedRenameItem()
    {
        if (SelectedRenameItem != null)
        {
            RenameItems.Remove(SelectedRenameItem);
            RefreshRenamePreview();
        }
    }

    [RelayCommand]
    public void AddRenameMethod(string? type = null)
    {
        string targetType = type ?? NewMethodSelection ?? "NewName";
        RenameMethodBase? method = targetType switch
        {
            "NewName" => new NewNameMethod(),
            "Replace" => new ReplaceMethod(),
            "Renumber" => new RenumberMethod(),
            "Case" => new ChangeCaseMethod(),
            "Remove" => new RemoveMethod(),
            "OptimizeZero" => new OptimizeZeroMethod(),
            _ => new NewNameMethod()
        };

        method.ConfigurationChanged += RefreshRenamePreview;
        RenameMethods.Add(method);
        SelectedRenameMethod = method;
        RefreshRenamePreview();
        RenameStatusMessage = $"Đã thêm method '{method.Name}'.";
    }

    [RelayCommand]
    public void RemoveSelectedRenameMethod()
    {
        if (SelectedRenameMethod != null)
        {
            string name = SelectedRenameMethod.Name;
            SelectedRenameMethod.ConfigurationChanged -= RefreshRenamePreview;
            RenameMethods.Remove(SelectedRenameMethod);
            SelectedRenameMethod = RenameMethods.LastOrDefault();
            RefreshRenamePreview();
            RenameStatusMessage = $"Đã xóa method '{name}'.";
        }
    }

    [RelayCommand]
    public void MoveRenameMethodUp()
    {
        if (SelectedRenameMethod == null) return;
        int idx = RenameMethods.IndexOf(SelectedRenameMethod);
        if (idx > 0)
        {
            var item = SelectedRenameMethod;
            RenameMethods.RemoveAt(idx);
            RenameMethods.Insert(idx - 1, item);
            SelectedRenameMethod = item;
            RefreshRenamePreview();
        }
    }

    [RelayCommand]
    public void MoveRenameMethodDown()
    {
        if (SelectedRenameMethod == null) return;
        int idx = RenameMethods.IndexOf(SelectedRenameMethod);
        if (idx >= 0 && idx < RenameMethods.Count - 1)
        {
            var item = SelectedRenameMethod;
            RenameMethods.RemoveAt(idx);
            RenameMethods.Insert(idx + 1, item);
            SelectedRenameMethod = item;
            RefreshRenamePreview();
        }
    }

    [RelayCommand]
    public async Task ExecuteBatchRenameAsync()
    {
        if (RenameItems.Count == 0)
        {
            RenameStatusMessage = "Danh sách trống, vui lòng thêm file hoặc thư mục!";
            return;
        }

        RefreshRenamePreview();

        var toRename = RenameItems.Where(it => it.IsChanged && it.IsValid).ToList();
        if (toRename.Count == 0)
        {
            RenameStatusMessage = "Không có mục nào hợp lệ cần đổi tên!";
            return;
        }

        IsRenamingBatch = true;
        RenameStatusMessage = $"Đang tiến hành đổi tên {toRename.Count} mục...";

        try
        {
            var (succeeded, failed, message) = await _batchRenameService.ExecuteBatchAsync(RenameItems);
            CanUndoRename = _batchRenameService.CanUndo;
            RefreshRenamePreview();
            RenameStatusMessage = message;
            AddLog("INFO", $"[Batch Rename] {message}");
        }
        catch (Exception ex)
        {
            RenameStatusMessage = $"Lỗi đổi tên: {ex.Message}";
            AddLog("ERROR", $"Lỗi đổi tên: {ex.Message}");
        }
        finally
        {
            IsRenamingBatch = false;
        }
    }

    [RelayCommand]
    public async Task UndoBatchRenameAsync()
    {
        if (!CanUndoRename)
        {
            RenameStatusMessage = "Không có thao tác nào để hoàn tác!";
            return;
        }

        IsRenamingBatch = true;
        RenameStatusMessage = "Đang hoàn tác đổi tên...";

        try
        {
            var (restored, message) = await _batchRenameService.UndoBatchAsync();
            CanUndoRename = _batchRenameService.CanUndo;
            RefreshRenamePreview();
            RenameStatusMessage = message;
            AddLog("INFO", $"[Batch Rename Undo] {message}");
        }
        catch (Exception ex)
        {
            RenameStatusMessage = $"Lỗi hoàn tác: {ex.Message}";
            AddLog("ERROR", $"Lỗi hoàn tác: {ex.Message}");
        }
        finally
        {
            IsRenamingBatch = false;
        }
    }
}
