using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class MainViewModel
{
    private readonly OfflineMissingChapterService _offlineMissingService = OfflineMissingChapterService.Instance;
    private CancellationTokenSource? _offlineScanCts;

    [ObservableProperty]
    private string _offlineScanRootPath = string.Empty;

    [ObservableProperty]
    private bool _isScanningOfflineMissing = false;

    [ObservableProperty]
    private string _offlineStatusMessage = "Sẵn sàng quét thư mục truyện offline.";

    [ObservableProperty]
    private bool _offlineFilterOnlyMissing = false;

    [ObservableProperty]
    private string _offlineSearchQuery = string.Empty;

    [ObservableProperty]
    private ObservableCollection<OfflineMangaItem> _offlineMangaList = new();

    [ObservableProperty]
    private ObservableCollection<OfflineMangaItem> _offlineFilteredMangaList = new();

    [ObservableProperty]
    private OfflineMangaItem? _selectedOfflineManga;

    [ObservableProperty]
    private ObservableCollection<OfflineChapterItem> _selectedOfflineChapters = new();

    partial void OnOfflineFilterOnlyMissingChanged(bool value)
    {
        ApplyOfflineMangaFilter();
    }

    partial void OnOfflineSearchQueryChanged(string value)
    {
        ApplyOfflineMangaFilter();
    }

    partial void OnSelectedOfflineMangaChanged(OfflineMangaItem? value)
    {
        SelectedOfflineChapters.Clear();
        if (value != null && value.Chapters != null)
        {
            foreach (var ch in value.Chapters)
            {
                SelectedOfflineChapters.Add(ch);
            }
        }
    }

    private void ApplyOfflineMangaFilter()
    {
        OfflineFilteredMangaList.Clear();
        var query = (OfflineSearchQuery ?? string.Empty).Trim();

        foreach (var m in OfflineMangaList)
        {
            if (OfflineFilterOnlyMissing && !m.Analysis.HasMissing)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(query) && m.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            OfflineFilteredMangaList.Add(m);
        }

        if (SelectedOfflineManga != null && !OfflineFilteredMangaList.Contains(SelectedOfflineManga))
        {
            SelectedOfflineManga = OfflineFilteredMangaList.FirstOrDefault();
        }
    }

    [RelayCommand]
    public async Task BrowseOfflineScanFolderAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null)
            {
                var options = new FolderPickerOpenOptions
                {
                    Title = "Chọn Thư Mục Truyện / Root Để Quét Chap Thiếu Offline",
                    AllowMultiple = false
                };

                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
                if (folders != null && folders.Count > 0)
                {
                    string path = folders[0].Path.LocalPath;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        OfflineScanRootPath = path;
                        await ScanOfflineFolderAsync();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("ERROR", $"Lỗi khi chọn thư mục quét offline: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task ScanOfflineFolderAsync()
    {
        string path = OfflineScanRootPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = _downloadEngine.DownloadRoot;
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                OfflineScanRootPath = path;
            }
            else
            {
                OfflineStatusMessage = "Vui lòng chọn thư mục cần quét!";
                return;
            }
        }

        if (!Directory.Exists(path))
        {
            OfflineStatusMessage = "Thư mục không tồn tại!";
            return;
        }

        _offlineScanCts?.Cancel();
        _offlineScanCts = new CancellationTokenSource();
        var ct = _offlineScanCts.Token;

        IsScanningOfflineMissing = true;
        OfflineStatusMessage = $"Đang quét thư mục: {path}...";
        OfflineMangaList.Clear();
        OfflineFilteredMangaList.Clear();
        SelectedOfflineManga = null;
        SelectedOfflineChapters.Clear();

        try
        {
            var mangas = await _offlineMissingService.ScanFolderAsync(path, ct);

            foreach (var m in mangas)
            {
                OfflineMangaList.Add(m);
            }

            ApplyOfflineMangaFilter();
            SelectedOfflineManga = OfflineFilteredMangaList.FirstOrDefault();

            int missingCount = mangas.Count(m => m.Analysis.HasMissing);
            OfflineStatusMessage = $"Đã quét xong: {mangas.Count} bộ truyện ({missingCount} bộ thiếu chap).";
            AddLog("INFO", $"[Offline Missing] Đã quét {mangas.Count} bộ truyện tại '{path}'. Có {missingCount} bộ bị thiếu chap.");
        }
        catch (OperationCanceledException)
        {
            OfflineStatusMessage = "Đã dừng quét.";
        }
        catch (Exception ex)
        {
            OfflineStatusMessage = $"Lỗi khi quét: {ex.Message}";
            AddLog("ERROR", $"Lỗi quét chap thiếu offline: {ex.Message}");
        }
        finally
        {
            IsScanningOfflineMissing = false;
        }
    }

    [RelayCommand]
    public void StopScanOfflineFolder()
    {
        _offlineScanCts?.Cancel();
        IsScanningOfflineMissing = false;
        OfflineStatusMessage = "Đã yêu cầu dừng quét.";
    }

    [RelayCommand]
    public async Task CopySelectedMissingAsync()
    {
        if (SelectedOfflineManga == null)
        {
            OfflineStatusMessage = "Chưa chọn bộ truyện nào!";
            return;
        }

        string text;
        if (SelectedOfflineManga.Analysis.HasMissing)
        {
            text = $"[{SelectedOfflineManga.Title}] Thiếu: {SelectedOfflineManga.Analysis.MissingSummaryText} (Tổng thiếu: {SelectedOfflineManga.Analysis.TotalMissingCount} chap)";
        }
        else
        {
            text = $"[{SelectedOfflineManga.Title}] Đầy đủ chap nguyên ({SelectedOfflineManga.Analysis.IntegerCount} chap)";
        }

        await SetClipboardTextAsync(text);
        OfflineStatusMessage = $"Đã sao chép chap thiếu của '{SelectedOfflineManga.Title}' vào Clipboard!";
        AddLog("INFO", $"[Offline Missing] Đã copy: {text}");
    }

    [RelayCommand]
    public async Task CopyAllMissingAsync()
    {
        var missingMangas = OfflineMangaList.Where(m => m.Analysis.HasMissing).ToList();
        if (missingMangas.Count == 0)
        {
            OfflineStatusMessage = "Tất cả các bộ truyện đã quét đều đầy đủ chap!";
            return;
        }

        var lines = new List<string>
        {
            $"=== DANH SÁCH CHAP THIẾU OFFLINE ({missingMangas.Count} bộ truyện) ==="
        };

        foreach (var m in missingMangas)
        {
            lines.Add($"- {m.Title} | Thiếu: {m.Analysis.MissingSummaryText} (Tổng thiếu: {m.Analysis.TotalMissingCount} chap)");
        }

        string text = string.Join(Environment.NewLine, lines);
        await SetClipboardTextAsync(text);
        OfflineStatusMessage = $"Đã sao chép toàn bộ chap thiếu của {missingMangas.Count} bộ truyện vào Clipboard!";
        AddLog("INFO", $"[Offline Missing] Đã copy danh sách {missingMangas.Count} bộ truyện thiếu chap.");
    }

    [RelayCommand]
    public void GoogleSearchMissing()
    {
        if (SelectedOfflineManga == null)
        {
            OfflineStatusMessage = "Chưa chọn bộ truyện nào để tìm Google!";
            return;
        }

        string title = SelectedOfflineManga.Title;
        string firstMissing = SelectedOfflineManga.Analysis.MissingRanges.FirstOrDefault() ?? string.Empty;
        string query = !string.IsNullOrEmpty(firstMissing) ? $"{title} chap {firstMissing}" : title;

        string url = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            OfflineStatusMessage = $"Đã mở tìm kiếm Google cho '{query}'";
        }
        catch (Exception ex)
        {
            OfflineStatusMessage = $"Lỗi mở trình duyệt: {ex.Message}";
        }
    }

    [RelayCommand]
    public void OpenSelectedMangaFolder()
    {
        if (SelectedOfflineManga == null || string.IsNullOrEmpty(SelectedOfflineManga.FolderPath))
        {
            OfflineStatusMessage = "Chưa chọn truyện hoặc thư mục không tồn tại!";
            return;
        }

        OpenFolder(SelectedOfflineManga.FolderPath);
    }

    [RelayCommand]
    public void OpenSelectedChapterFolder(OfflineChapterItem? chapter)
    {
        if (chapter != null && Directory.Exists(chapter.FolderPath))
        {
            OpenFolder(chapter.FolderPath);
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = true });
            }
        }
        catch { }
    }

    private async Task SetClipboardTextAsync(string text)
    {
        try
        {
            var clipboard = GetClipboard();
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch { }
    }
}
