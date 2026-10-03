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
    private string _renameTargetMode = "Files"; // "Files" hoặc "Folders"

    public void RefreshRenamePreview()
    {
        _batchRenameService.UpdatePreview(RenameItems, RenameMethods);
        RenameItemsValidCount = RenameItems.Count(it => it.IsValid);
        RenameItemsChangedCount = RenameItems.Count(it => it.IsChanged);
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
                    foreach (var f in files)
                    {
                        string path = f.Path.LocalPath;
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        {
                            if (!RenameItems.Any(it => it.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                            {
                                RenameItems.Add(new RenameItem
                                {
                                    OriginalPath = path,
                                    IsDirectory = false
                                });
                            }
                        }
                    }
                    RefreshRenamePreview();
                    RenameStatusMessage = $"Đã thêm {files.Count} file vào danh sách.";
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
                    Title = "Chọn Thư Mục (Đổi Tên Thư Mục Hoặc File Bên Trong)",
                    AllowMultiple = true
                };

                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
                if (folders != null && folders.Count > 0)
                {
                    int addedCount = 0;
                    foreach (var f in folders)
                    {
                        string path = f.Path.LocalPath;
                        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                        {
                            if (RenameTargetMode == "Folders")
                            {
                                if (!RenameItems.Any(it => it.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                                {
                                    RenameItems.Add(new RenameItem
                                    {
                                        OriginalPath = path,
                                        IsDirectory = true
                                    });
                                    addedCount++;
                                }

                                foreach (var subDir in Directory.GetDirectories(path))
                                {
                                    if (!RenameItems.Any(it => it.OriginalPath.Equals(subDir, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        RenameItems.Add(new RenameItem
                                        {
                                            OriginalPath = subDir,
                                            IsDirectory = true
                                        });
                                        addedCount++;
                                    }
                                }
                            }
                            else
                            {
                                foreach (var file in Directory.GetFiles(path))
                                {
                                    if (!RenameItems.Any(it => it.OriginalPath.Equals(file, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        RenameItems.Add(new RenameItem
                                        {
                                            OriginalPath = file,
                                            IsDirectory = false
                                        });
                                        addedCount++;
                                    }
                                }
                            }
                        }
                    }
                    RefreshRenamePreview();
                    RenameStatusMessage = $"Đã nạp {addedCount} mục vào danh sách.";
                }
            }
        }
        catch (Exception ex)
        {
            RenameStatusMessage = $"Lỗi chọn thư mục: {ex.Message}";
        }
    }

    public void AddDroppedPathsToRename(IEnumerable<string> paths)
    {
        if (paths == null) return;
        int added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (File.Exists(path))
            {
                if (!RenameItems.Any(it => it.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                {
                    RenameItems.Add(new RenameItem { OriginalPath = path, IsDirectory = false });
                    added++;
                }
            }
            else if (Directory.Exists(path))
            {
                if (RenameTargetMode == "Folders")
                {
                    if (!RenameItems.Any(it => it.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    {
                        RenameItems.Add(new RenameItem { OriginalPath = path, IsDirectory = true });
                        added++;
                    }
                }
                else
                {
                    foreach (var file in Directory.GetFiles(path))
                    {
                        if (!RenameItems.Any(it => it.OriginalPath.Equals(file, StringComparison.OrdinalIgnoreCase)))
                        {
                            RenameItems.Add(new RenameItem { OriginalPath = file, IsDirectory = false });
                            added++;
                        }
                    }
                }
            }
        }

        if (added > 0)
        {
            RefreshRenamePreview();
            RenameStatusMessage = $"Đã kéo thả thêm {added} mục vào danh sách.";
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
