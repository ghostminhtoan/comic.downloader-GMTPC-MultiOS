using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.Services;
using ComicDownloaderGMTPC.Views;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class LibraryViewModel : ViewModelBase
{
    private readonly LibraryScannerService _scanner = LibraryScannerService.Instance;
    private CancellationTokenSource? _scanCts;

    [ObservableProperty]
    private string _libraryRootPath = string.Empty;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterCurrentFolder))]
    [NotifyPropertyChangedFor(nameof(IsFilterInProgress))]
    [NotifyPropertyChangedFor(nameof(IsFilterUnread))]
    [NotifyPropertyChangedFor(nameof(IsFilterCompleted))]
    [NotifyPropertyChangedFor(nameof(FilterAllBackground))]
    [NotifyPropertyChangedFor(nameof(FilterCurrentFolderBackground))]
    [NotifyPropertyChangedFor(nameof(FilterInProgressBackground))]
    [NotifyPropertyChangedFor(nameof(FilterUnreadBackground))]
    [NotifyPropertyChangedFor(nameof(FilterCompletedBackground))]
    private string _filterMode = "All"; // All, CurrentFolder, InProgress, Unread, Completed

    public bool IsFilterAll => FilterMode == "All";
    public bool IsFilterCurrentFolder => FilterMode == "CurrentFolder";
    public bool IsFilterInProgress => FilterMode == "InProgress";
    public bool IsFilterUnread => FilterMode == "Unread";
    public bool IsFilterCompleted => FilterMode == "Completed";

    public string FilterAllBackground => IsFilterAll ? "#0284C7" : "#1E293B";
    public string FilterCurrentFolderBackground => IsFilterCurrentFolder ? "#0284C7" : "#1E293B";
    public string FilterInProgressBackground => IsFilterInProgress ? "#D97706" : "#1E293B";
    public string FilterUnreadBackground => IsFilterUnread ? "#475569" : "#1E293B";
    public string FilterCompletedBackground => IsFilterCompleted ? "#059669" : "#1E293B";

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusMessage = "Nhấn 'Quét Thư Viện' để nạp danh sách truyện đã tải và lịch sử đọc.";

    public ObservableCollection<LibraryComicItem> AllComics { get; } = new();

    public ObservableCollection<LibraryComicItem> FilteredComics { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedComic))]
    private LibraryComicItem? _selectedComic;

    public bool HasSelectedComic => SelectedComic != null;

    [ObservableProperty]
    private ObservableCollection<LibraryChapterItem> _selectedComicChapters = new();

    /// <summary>
    /// Bật/tắt chế độ mở trình đọc ở cửa sổ / Task độc lập (Mặc định BẬT trên cả Desktop và Android Đa nhiệm).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeparateWindowModeText))]
    [NotifyPropertyChangedFor(nameof(SeparateWindowModeBackground))]
    private bool _isSeparateReaderWindowEnabled = true;

    public string SeparateWindowModeText =>
        IsSeparateReaderWindowEnabled
            ? "🗗 Cửa sổ đọc riêng (Đa nhiệm): BẬT"
            : "📌 Đọc trên màn hình chính: BẬT";

    public string SeparateWindowModeBackground =>
        IsSeparateReaderWindowEnabled ? "#059669" : "#334155";

    /// <summary>
    /// Hỗ trợ trình đọc toàn màn hình trực tiếp trên Mobile/Android SingleView (khi tắt chế độ cửa sổ độc lập).
    /// </summary>
    [ObservableProperty]
    private bool _isInlineReaderVisible;

    [ObservableProperty]
    private ComicReaderViewModel? _inlineReaderVm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMobileLayout))]
    [NotifyPropertyChangedFor(nameof(IsDesktopLayout))]
    private bool _isCompactWidth;

    public bool IsMobileLayout => IsCompactWidth || OperatingSystem.IsAndroid();
    public bool IsDesktopLayout => !IsMobileLayout;

    /// <summary>
    /// Trên màn hình nhỏ/Android: 0 = Xem danh sách truyện (Comics), 1 = Xem danh sách chương (Chapters)
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingComicsTab))]
    [NotifyPropertyChangedFor(nameof(IsShowingChaptersTab))]
    [NotifyPropertyChangedFor(nameof(ComicsTabBackground))]
    [NotifyPropertyChangedFor(nameof(ChaptersTabBackground))]
    private int _mobileViewTabIndex = 0;

    public bool IsShowingComicsTab => MobileViewTabIndex == 0;
    public bool IsShowingChaptersTab => MobileViewTabIndex == 1;

    public string ComicsTabBackground => IsShowingComicsTab ? "#0284C7" : "#1E293B";
    public string ChaptersTabBackground => IsShowingChaptersTab ? "#7C3AED" : "#1E293B";

    [RelayCommand]
    public void SwitchMobileTab(string? tabIndexStr)
    {
        if (int.TryParse(tabIndexStr, out int idx))
        {
            MobileViewTabIndex = Math.Clamp(idx, 0, 1);
        }
    }

    [RelayCommand]
    public void BackToComicsList()
    {
        MobileViewTabIndex = 0;
    }

    [RelayCommand]
    public void SelectComicAndOpenChapters(LibraryComicItem? comic)
    {
        if (comic == null) return;
        SelectedComic = comic;
        if (IsMobileLayout)
        {
            MobileViewTabIndex = 1;
        }
    }

    public LibraryViewModel()
    {
        LibraryRootPath = ReadProgressStore.Instance.GetLastLibraryRootPath()
                          ?? DownloadEngineService.Instance.DownloadRoot;
    }

    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnFilterModeChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnSelectedComicChanged(LibraryComicItem? value)
    {
        if (value != null)
        {
            value.RefreshReadStatus();
            SelectedComicChapters = new ObservableCollection<LibraryChapterItem>(value.Chapters);
        }
        else
        {
            SelectedComicChapters = new ObservableCollection<LibraryChapterItem>();
        }
    }

    [RelayCommand]
    public void SetFilterMode(string mode)
    {
        if (!string.IsNullOrWhiteSpace(mode))
        {
            FilterMode = mode;
        }
    }

    public void ApplyFilter()
    {
        var prevSelected = SelectedComic;
        string q = (SearchQuery ?? string.Empty).Trim();

        var matches = new List<LibraryComicItem>();
        foreach (var comic in AllComics)
        {
            comic.RefreshReadStatus();

            if (FilterMode == "CurrentFolder" && comic.IsExternalHistoryItem)
                continue;
            if (FilterMode == "InProgress" && (!comic.HasAnyReadProgress || comic.IsAllChaptersRead))
                continue;
            if (FilterMode == "Unread" && (comic.HasAnyReadProgress || comic.IsExternalHistoryItem))
                continue;
            if (FilterMode == "Completed" && !comic.IsAllChaptersRead)
                continue;

            if (!string.IsNullOrEmpty(q) &&
                comic.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                comic.CategoryName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                comic.SourceLocationLabel.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                comic.RootFolderPath.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            matches.Add(comic);
        }

        if (FilterMode == "InProgress" || FilterMode == "Completed")
        {
            matches = matches
                .OrderByDescending(c => c.LastReadOrder)
                .ThenBy(c => c.Title, NaturalSortComparer.Instance)
                .ToList();
        }

        FilteredComics.Clear();
        foreach (var item in matches)
        {
            FilteredComics.Add(item);
        }

        if (prevSelected != null && FilteredComics.Contains(prevSelected))
        {
            SelectedComic = prevSelected;
        }
        else
        {
            SelectedComic = FilteredComics.FirstOrDefault();
        }
    }

    [RelayCommand]
    public async Task ScanLibraryAsync()
    {
        if (string.IsNullOrWhiteSpace(LibraryRootPath))
        {
            LibraryRootPath = ReadProgressStore.Instance.GetLastLibraryRootPath()
                              ?? DownloadEngineService.Instance.DownloadRoot;
        }

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        IsScanning = true;
        StatusMessage = $"⏳ Đang quét thư viện & đồng bộ lịch sử đọc...";

        try
        {
            var list = await _scanner.ScanLibraryRootAsync(LibraryRootPath, ct);
            if (ct.IsCancellationRequested) return;

            AllComics.Clear();
            foreach (var c in list)
            {
                AllComics.Add(c);
            }

            ApplyFilter();

            int totalChapters = list.Sum(c => c.TotalChapters);
            int externalHistoryCount = list.Count(c => c.IsExternalHistoryItem);
            int localCount = list.Count - externalHistoryCount;

            if (externalHistoryCount > 0)
            {
                StatusMessage = $"✅ Đã nạp {list.Count} bộ truyện ({localCount} tại thư mục đang chọn + {externalHistoryCount} từ Lịch sử đọc ở thư mục khác • {totalChapters} chương).";
            }
            else if (list.Count > 0)
            {
                StatusMessage = $"✅ Đã tìm thấy {list.Count} bộ truyện ({totalChapters} chương). Chọn chương và nhấn 'Đọc ngay'.";
            }
            else
            {
                StatusMessage = "ℹ️ Chưa có truyện trong thư mục đang chọn. Bạn có thể chọn thư mục khác hoặc mở trực tiếp file CBZ/ZIP/PDF.";
            }

            // Nạp ảnh bìa bất đồng bộ trên nền
            _ = _scanner.LoadCoversAsync(list, ct);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "⏹ Đã dừng quét thư viện.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Lỗi quét thư viện: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    public async Task BrowseLibraryFolderAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Chọn Thư Mục Thư Viện Truyện Đã Tải",
                AllowMultiple = false
            });

            if (folders != null && folders.Count > 0)
            {
                var selected = folders[0];
                string? raw = selected.TryGetLocalPath() ?? selected.Path?.LocalPath ?? selected.Path?.ToString();
                string normalized = DownloadEngineService.NormalizeStoragePath(raw, selected.Name);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    LibraryRootPath = normalized;
                    await ScanLibraryAsync();
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Không thể chọn thư mục: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task OpenCbzFileDirectAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Chọn File Truyện CBZ / ZIP / PDF Để Đọc Ngay",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Comic Archive & PDF (*.cbz, *.zip, *.pdf)")
                    {
                        Patterns = new[] { "*.cbz", "*.zip", "*.pdf" }
                    }
                }
            });

            if (files != null && files.Count > 0)
            {
                var selected = files[0];
                string? raw = selected.TryGetLocalPath() ?? selected.Path?.LocalPath ?? selected.Path?.ToString();
                string normalized = DownloadEngineService.NormalizeStoragePath(raw, selected.Name);
                if (!string.IsNullOrWhiteSpace(normalized) && File.Exists(normalized))
                {
                    var quickComic = _scanner.CreateQuickComicFromPath(normalized);
                    if (quickComic != null && quickComic.Chapters.Count > 0)
                    {
                        ReadProgressStore.Instance.RegisterReadComic(quickComic.RootFolderPath, touchTimestamp: true);
                        var existing = AllComics.FirstOrDefault(c =>
                            string.Equals(c.RootFolderPath, quickComic.RootFolderPath, StringComparison.OrdinalIgnoreCase));
                        if (existing == null)
                        {
                            AllComics.Insert(0, quickComic);
                            _ = _scanner.LoadCoversAsync(new[] { quickComic }, CancellationToken.None);
                        }
                        else
                        {
                            quickComic = existing;
                        }

                        ApplyFilter();
                        SelectedComic = quickComic;

                        var targetChap = quickComic.GetBestResumeChapter() ?? quickComic.Chapters[0];
                        StatusMessage = $"📖 Đã nạp '{quickComic.Title}' ({quickComic.TotalChapters} chương). Đang mở: {targetChap.Title}";
                        await OpenChapterInternalAsync(quickComic, targetChap);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Lỗi mở file truyện: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ToggleSeparateReaderWindow()
    {
        IsSeparateReaderWindowEnabled = !IsSeparateReaderWindowEnabled;
        StatusMessage = IsSeparateReaderWindowEnabled
            ? "🗗 Đã bật chế độ mở trình đọc ở Cửa sổ / Task độc lập (tách thẻ riêng trong Đa nhiệm Android)."
            : "📌 Đã chuyển sang chế độ đọc trực tiếp ngay trên màn hình chính.";
    }

    [RelayCommand]
    public async Task ReadChapterAsync(LibraryChapterItem? chapter)
    {
        if (chapter == null) return;
        await OpenChapterInternalAsync(SelectedComic, chapter, forceSeparateWindow: false);
    }

    [RelayCommand]
    public async Task ContinueReadingSelectedComicAsync(LibraryComicItem? comic)
    {
        var targetComic = comic ?? SelectedComic;
        if (targetComic == null || targetComic.Chapters.Count == 0) return;

        SelectedComic = targetComic;

        var targetChapter = targetComic.GetBestResumeChapter() ?? targetComic.Chapters[0];

        await OpenChapterInternalAsync(targetComic, targetChapter, forceSeparateWindow: false);
    }

    [RelayCommand]
    public async Task OpenSelectedComicInSeparateWindowAsync(LibraryComicItem? comic)
    {
        var targetComic = comic ?? SelectedComic;
        if (targetComic == null || targetComic.Chapters.Count == 0) return;

        SelectedComic = targetComic;

        var targetChapter = targetComic.GetBestResumeChapter() ?? targetComic.Chapters[0];

        await OpenChapterInternalAsync(targetComic, targetChapter, forceSeparateWindow: true);
    }

    [RelayCommand]
    public void PopOutInlineReader()
    {
        var activeVm = InlineReaderVm;
        if (activeVm == null) return;

        var parentComic = activeVm.ParentComic;
        IsInlineReaderVisible = false;
        InlineReaderVm = null;

        LaunchExternalReaderForViewModel(activeVm, parentComic);
    }

    private void LaunchExternalReaderForViewModel(ComicReaderViewModel readerVm, LibraryComicItem? comic)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
        {
            var win = new ComicReaderWindow
            {
                DataContext = readerVm
            };
            win.Closed += (_, _) =>
            {
                readerVm.Cleanup();
                comic?.RefreshReadStatus();
                ApplyFilter();
            };
            win.Show();
        }
        else if (ComicReaderService.AndroidLaunchSeparateReaderTask != null)
        {
            ComicReaderService.Instance.ActiveExternalReaderViewModel = readerVm;
            ComicReaderService.Instance.ActiveExternalReaderClosedCallback = () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    comic?.RefreshReadStatus();
                    ApplyFilter();
                });
            };
            ComicReaderService.AndroidLaunchSeparateReaderTask.Invoke(readerVm);
        }
    }

    private async Task OpenChapterInternalAsync(LibraryComicItem? comic, LibraryChapterItem chapter, bool forceSeparateWindow = false)
    {
        if (comic != null && !string.IsNullOrWhiteSpace(comic.RootFolderPath))
        {
            ReadProgressStore.Instance.RegisterReadComic(comic.RootFolderPath, touchTimestamp: true);
        }
        else if (!string.IsNullOrWhiteSpace(chapter.LocalPath))
        {
            ReadProgressStore.Instance.RegisterReadComic(chapter.LocalPath, touchTimestamp: true);
        }

        var readerVm = new ComicReaderViewModel();
        bool useSeparateWindow = forceSeparateWindow || IsSeparateReaderWindowEnabled;

        if (useSeparateWindow && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
        {
            var win = new ComicReaderWindow
            {
                DataContext = readerVm
            };
            win.Closed += (_, _) =>
            {
                readerVm.Cleanup();
                comic?.RefreshReadStatus();
                ApplyFilter();
            };
            win.Show();
            await readerVm.InitializeAsync(comic, chapter);
        }
        else if (useSeparateWindow && ComicReaderService.AndroidLaunchSeparateReaderTask != null)
        {
            // Android Multiple Tasks (New Task Launch Mode): Mở ComicReaderActivity ở Task độc lập (tách thành 2 thẻ riêng trong Recents)
            ComicReaderService.Instance.ActiveExternalReaderViewModel = readerVm;
            ComicReaderService.Instance.ActiveExternalReaderClosedCallback = () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    comic?.RefreshReadStatus();
                    ApplyFilter();
                });
            };
            await readerVm.InitializeAsync(comic, chapter);
            ComicReaderService.AndroidLaunchSeparateReaderTask.Invoke(readerVm);
        }
        else
        {
            // Chế độ đọc nội tuyến (Inline Overlay) ngay trên MainWindow / MainView
            void OnInlineClose()
            {
                readerVm.CloseRequested -= OnInlineClose;
                IsInlineReaderVisible = false;
                InlineReaderVm = null;
                comic?.RefreshReadStatus();
                ApplyFilter();
            }

            readerVm.CloseRequested += OnInlineClose;
            InlineReaderVm = readerVm;
            IsInlineReaderVisible = true;
            await readerVm.InitializeAsync(comic, chapter);
        }
    }

    private static TopLevel? GetTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            return desktop.MainWindow;
        }
        if (MainView.Instance != null)
        {
            return TopLevel.GetTopLevel(MainView.Instance);
        }
        return null;
    }
}

