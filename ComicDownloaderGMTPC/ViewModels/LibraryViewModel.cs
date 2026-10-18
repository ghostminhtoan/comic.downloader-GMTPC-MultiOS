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
using Avalonia.Media;
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
    [NotifyPropertyChangedFor(nameof(IsStorageTabActive))]
    [NotifyPropertyChangedFor(nameof(IsDirectoryTabActive))]
    [NotifyPropertyChangedFor(nameof(IsHistoryTabActive))]
    [NotifyPropertyChangedFor(nameof(IsBookmarkTabActive))]
    [NotifyPropertyChangedFor(nameof(StorageTabForeground))]
    [NotifyPropertyChangedFor(nameof(DirectoryTabForeground))]
    [NotifyPropertyChangedFor(nameof(HistoryTabForeground))]
    [NotifyPropertyChangedFor(nameof(BookmarkTabForeground))]
    [NotifyPropertyChangedFor(nameof(StorageTabLineBackground))]
    [NotifyPropertyChangedFor(nameof(DirectoryTabLineBackground))]
    [NotifyPropertyChangedFor(nameof(HistoryTabLineBackground))]
    [NotifyPropertyChangedFor(nameof(BookmarkTabLineBackground))]
    [NotifyPropertyChangedFor(nameof(StorageTabFontWeight))]
    [NotifyPropertyChangedFor(nameof(DirectoryTabFontWeight))]
    [NotifyPropertyChangedFor(nameof(HistoryTabFontWeight))]
    [NotifyPropertyChangedFor(nameof(BookmarkTabFontWeight))]
    private string _activeSubTab = "Storage"; // Storage, Directory, History, Bookmark

    public bool IsStorageTabActive => ActiveSubTab == "Storage";
    public bool IsDirectoryTabActive => ActiveSubTab == "Directory";
    public bool IsHistoryTabActive => ActiveSubTab == "History";
    public bool IsBookmarkTabActive => ActiveSubTab == "Bookmark";

    public string StorageTabForeground => IsStorageTabActive ? "#38BDF8" : "#94A3B8";
    public string DirectoryTabForeground => IsDirectoryTabActive ? "#38BDF8" : "#94A3B8";
    public string HistoryTabForeground => IsHistoryTabActive ? "#38BDF8" : "#94A3B8";
    public string BookmarkTabForeground => IsBookmarkTabActive ? "#38BDF8" : "#94A3B8";

    public string StorageTabLineBackground => IsStorageTabActive ? "#38BDF8" : "Transparent";
    public string DirectoryTabLineBackground => IsDirectoryTabActive ? "#38BDF8" : "Transparent";
    public string HistoryTabLineBackground => IsHistoryTabActive ? "#38BDF8" : "Transparent";
    public string BookmarkTabLineBackground => IsBookmarkTabActive ? "#38BDF8" : "Transparent";

    public FontWeight StorageTabFontWeight => IsStorageTabActive ? FontWeight.Bold : FontWeight.Normal;
    public FontWeight DirectoryTabFontWeight => IsDirectoryTabActive ? FontWeight.Bold : FontWeight.Normal;
    public FontWeight HistoryTabFontWeight => IsHistoryTabActive ? FontWeight.Bold : FontWeight.Normal;
    public FontWeight BookmarkTabFontWeight => IsBookmarkTabActive ? FontWeight.Bold : FontWeight.Normal;

    public ObservableCollection<StorageItemModel> StorageItems { get; } = new();
    public ObservableCollection<DirectoryItemModel> DirectoryItems { get; } = new();
    public ObservableCollection<HistoryItemModel> HistoryItems { get; } = new();
    public ObservableCollection<BookmarkGroupModel> BookmarkGroups { get; } = new();

    [ObservableProperty]
    private string _currentDirectoryPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadableCurrentDirectory))]
    private string _currentDirectoryReadableStatus = string.Empty;

    public bool HasReadableCurrentDirectory => !string.IsNullOrWhiteSpace(CurrentDirectoryReadableStatus);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectoryViewModeIcon))]
    [NotifyPropertyChangedFor(nameof(DirectoryViewModeText))]
    [NotifyPropertyChangedFor(nameof(IsDirectoryListView))]
    private bool _isDirectoryGridView;

    public bool IsDirectoryListView => !IsDirectoryGridView;

    [ObservableProperty]
    private int _directoryGridColumns = 3;

    public string DirectoryViewModeIcon => IsDirectoryGridView ? "📋" : "⊞";
    public string DirectoryViewModeText => IsDirectoryGridView ? "Danh sách" : "Lưới ô";

    [ObservableProperty]
    private string _directorySortMode = "NameAsc"; // NameAsc, NameDesc, DateDesc, SizeDesc

    [ObservableProperty]
    private bool _isDirectoryLoading;

    private CancellationTokenSource? _loadDirCts;

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
        CurrentDirectoryPath = LibraryRootPath;
        LoadStorageItems();
        LoadDirectory(CurrentDirectoryPath);
        _ = LoadHistoryItemsAsync();
        LoadBookmarkGroups();
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
                Title = "Chọn File Truyện CBZ / ZIP / CBR / RAR / PDF / MOBI Để Đọc Ngay",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Tất cả định dạng truyện (*.cbz, *.zip, *.cbr, *.rar, *.pdf, *.mobi)")
                    {
                        Patterns = new[] { "*.cbz", "*.zip", "*.cbr", "*.rar", "*.pdf", "*.mobi", "*.prc", "*.azw", "*.azw3" },
                        MimeTypes = new[] { "application/x-mobipocket-ebook", "application/octet-stream", "application/zip", "application/x-zip-compressed", "application/x-cbr", "application/x-rar-compressed", "application/pdf", "*/*" }
                    },
                    new FilePickerFileType("Comic Archives (*.cbz, *.zip, *.cbr, *.rar)")
                    {
                        Patterns = new[] { "*.cbz", "*.zip", "*.cbr", "*.rar" },
                        MimeTypes = new[] { "application/zip", "application/x-zip-compressed", "application/x-cbr", "application/x-rar-compressed", "application/octet-stream" }
                    },
                    new FilePickerFileType("PDF Document (*.pdf)")
                    {
                        Patterns = new[] { "*.pdf" },
                        MimeTypes = new[] { "application/pdf" }
                    },
                    new FilePickerFileType("Kindle eBook (*.mobi, *.azw, *.azw3)")
                    {
                        Patterns = new[] { "*.mobi", "*.prc", "*.azw", "*.azw3" },
                        MimeTypes = new[] { "application/x-mobipocket-ebook", "application/octet-stream", "*/*" }
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
                    var quickComic = await Task.Run(() => _scanner.CreateQuickComicFromPath(normalized));
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

    /// <summary>
    /// Mở tệp truyện hoặc hình ảnh trực tiếp từ đường dẫn tệp (khi double-click hoặc mở từ Context Menu hệ điều hành).
    /// </summary>
    public async Task OpenStandaloneFileDirectlyAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        try
        {
            var quickComic = await Task.Run(() => _scanner.CreateQuickComicFromPath(filePath));
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
        catch (Exception ex)
        {
            StatusMessage = $"❌ Lỗi mở file: {ex.Message}";
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
                _ = LoadHistoryItemsAsync();
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
                    _ = LoadHistoryItemsAsync();
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
                _ = LoadHistoryItemsAsync();
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
                    _ = LoadHistoryItemsAsync();
                });
            };
            ComicReaderService.AndroidLaunchSeparateReaderTask.Invoke(readerVm);
            await readerVm.InitializeAsync(comic, chapter);
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
                _ = LoadHistoryItemsAsync();
            }

            readerVm.CloseRequested += OnInlineClose;
            InlineReaderVm = readerVm;
            IsInlineReaderVisible = true;
            await readerVm.InitializeAsync(comic, chapter);
        }
    }

    [RelayCommand]
    public void SelectSubTab(string? tab)
    {
        if (string.IsNullOrWhiteSpace(tab)) return;
        ActiveSubTab = tab;
        if (tab == "Storage")
        {
            LoadStorageItems();
        }
        else if (tab == "Directory")
        {
            if (DirectoryItems.Count == 0 || string.IsNullOrWhiteSpace(CurrentDirectoryPath))
            {
                LoadDirectory(CurrentDirectoryPath);
            }
        }
        else if (tab == "History")
        {
            _ = LoadHistoryItemsAsync();
        }
        else if (tab == "Bookmark")
        {
            LoadBookmarkGroups();
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return string.Empty;
        if (bytes >= 1024L * 1024L * 1024L)
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
        if (bytes >= 1024L * 1024L)
            return $"{bytes / (1024.0 * 1024.0):F0} MB";
        if (bytes >= 1024L)
            return $"{bytes / 1024.0:F0} kB";
        return $"{bytes} B";
    }

    public void LoadStorageItems()
    {
        try
        {
            StorageItems.Clear();

            // 1. Recent
            var recentPath = ReadProgressStore.Instance.GetKnownReadComicPaths().FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(recentPath) && (File.Exists(recentPath) || Directory.Exists(recentPath)))
            {
                StorageItems.Add(new StorageItemModel
                {
                    Type = StorageItemType.Recent,
                    Title = "Recent",
                    Path = recentPath,
                    IconText = "🕒",
                    IconColor = "#38BDF8"
                });
            }
            else
            {
                StorageItems.Add(new StorageItemModel
                {
                    Type = StorageItemType.Recent,
                    Title = "Recent",
                    Path = "(Chưa có lịch sử đọc gần đây)",
                    IconText = "🕒",
                    IconColor = "#64748B"
                });
            }

            // 2. Device
            string devicePath = OperatingSystem.IsAndroid()
                ? "/storage/emulated/0"
                : (DownloadEngineService.Instance.DownloadRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            StorageItems.Add(new StorageItemModel
            {
                Type = StorageItemType.Device,
                Title = "Device",
                Path = devicePath,
                IconText = "📱",
                IconColor = "#94A3B8"
            });

            // 3. Thẻ SD / Ổ đĩa
            if (OperatingSystem.IsAndroid())
            {
                var detectedSdRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // A. Lấy từ Android Native GetExternalFilesDirs
                try
                {
                    var nativePaths = LibraryScannerService.AndroidGetExternalStoragePaths?.Invoke();
                    if (nativePaths != null)
                    {
                        foreach (var np in nativePaths)
                        {
                            if (!string.IsNullOrWhiteSpace(np) && Directory.Exists(np))
                            {
                                detectedSdRoots.Add(np);
                            }
                        }
                    }
                }
                catch { }

                // B. Quét từ /proc/mounts
                try
                {
                    if (File.Exists("/proc/mounts"))
                    {
                        var lines = File.ReadAllLines("/proc/mounts");
                        foreach (var line in lines)
                        {
                            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2)
                            {
                                string mountPoint = parts[1];
                                if (mountPoint.StartsWith("/storage/", StringComparison.OrdinalIgnoreCase) &&
                                    !mountPoint.StartsWith("/storage/emulated", StringComparison.OrdinalIgnoreCase) &&
                                    !mountPoint.StartsWith("/storage/self", StringComparison.OrdinalIgnoreCase) &&
                                    !mountPoint.Contains("media_rw", StringComparison.OrdinalIgnoreCase) &&
                                    !mountPoint.Contains("knox", StringComparison.OrdinalIgnoreCase))
                                {
                                    string norm = mountPoint.TrimEnd('/', '\\');
                                    if (Directory.Exists(norm))
                                    {
                                        detectedSdRoots.Add(norm);
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }

                // C. Fallback quét trực tiếp /storage (loại bỏ hoàn toàn /mnt/media_rw vì không có quyền trực tiếp)
                try
                {
                    string[] scanBases = { "/storage" };
                    var enumOpts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
                    foreach (var baseDir in scanBases)
                    {
                        if (Directory.Exists(baseDir))
                        {
                            foreach (var d in Directory.EnumerateDirectories(baseDir, "*", enumOpts))
                            {
                                string name = Path.GetFileName(d);
                                if (!string.Equals(name, "emulated", StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(name, "self", StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(name, "rw", StringComparison.OrdinalIgnoreCase) &&
                                    !name.Contains("media_rw", StringComparison.OrdinalIgnoreCase) &&
                                    !name.StartsWith("knox", StringComparison.OrdinalIgnoreCase) &&
                                    !name.StartsWith("."))
                                {
                                    detectedSdRoots.Add(d);
                                }
                            }
                        }
                    }
                }
                catch { }

                foreach (var sdPath in detectedSdRoots)
                {
                    if (string.IsNullOrWhiteSpace(sdPath)) continue;
                    if (sdPath.Contains("media_rw", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = Path.GetFileName(sdPath);
                    if (string.Equals(name, "rw", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(name)) continue;

                    StorageItems.Add(new StorageItemModel
                    {
                        Type = StorageItemType.SDCard,
                        Title = $"Thẻ SD ({name})",
                        Path = sdPath,
                        IconText = "💾",
                        IconColor = "#38BDF8"
                    });
                }
            }
            else
            {
                try
                {
                    foreach (var d in DriveInfo.GetDrives())
                    {
                        if (d.IsReady)
                        {
                            string driveName = d.Name.TrimEnd('\\', '/');
                            StorageItems.Add(new StorageItemModel
                            {
                                Type = StorageItemType.SDCard,
                                Title = $"Ổ đĩa {driveName}",
                                Path = d.RootDirectory.FullName,
                                IconText = "💾",
                                IconColor = "#38BDF8"
                            });
                        }
                    }
                }
                catch { }
            }

            // 4. Pinned Folders
            var pinned = ReadProgressStore.Instance.GetPinnedFolders();
            foreach (var p in pinned)
            {
                string name = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(name)) name = p;

                StorageItems.Add(new StorageItemModel
                {
                    Type = StorageItemType.PinnedFolder,
                    Title = name,
                    Path = p,
                    IconText = "📁",
                    IconColor = "#A78BFA"
                });
            }
        }
        catch { }
    }

    [RelayCommand]
    public async Task SelectStorageItemAsync(StorageItemModel? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Path)) return;

        if (item.Type == StorageItemType.Recent && File.Exists(item.Path))
        {
            await OpenPathToReadAsync(item.Path);
            return;
        }

        string targetDir = item.Path;
        if (File.Exists(targetDir))
        {
            targetDir = Path.GetDirectoryName(targetDir) ?? targetDir;
        }

        if (Directory.Exists(targetDir))
        {
            CurrentDirectoryPath = targetDir;
            LoadDirectory(targetDir);
            SelectSubTab("Directory");
        }
        else
        {
            StatusMessage = $"⚠️ Đường dẫn không tồn tại: {item.Path}";
        }
    }

    [RelayCommand]
    public async Task AddStorageFolderAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Chọn Thư Mục Để Ghim Vào Storage",
                AllowMultiple = false
            });

            if (folders != null && folders.Count > 0)
            {
                var selected = folders[0];
                string? raw = selected.TryGetLocalPath() ?? selected.Path?.LocalPath ?? selected.Path?.ToString();
                string normalized = DownloadEngineService.NormalizeStoragePath(raw, selected.Name);
                if (!string.IsNullOrWhiteSpace(normalized) && Directory.Exists(normalized))
                {
                    ReadProgressStore.Instance.AddPinnedFolder(normalized);
                    LoadStorageItems();
                    StatusMessage = $"📌 Đã ghim thư mục: {normalized}";
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Lỗi ghim thư mục: {ex.Message}";
        }
    }

    [RelayCommand]
    public void RemovePinnedFolder(StorageItemModel? item)
    {
        if (item == null) return;
        ReadProgressStore.Instance.RemovePinnedFolder(item.Path);
        StorageItems.Remove(item);
        StatusMessage = $"🗑️ Đã bỏ ghim thư mục: {item.Title}";
    }

    private CancellationTokenSource? _directoryThumbnailCts;

    public void LoadDirectory(string? path)
    {
        _ = LoadDirectoryAsync(path);
    }

    public async Task LoadDirectoryAsync(string? path)
    {
        _loadDirCts?.Cancel();
        _loadDirCts = new CancellationTokenSource();
        var ct = _loadDirCts.Token;

        _directoryThumbnailCts?.Cancel();
        _directoryThumbnailCts = new CancellationTokenSource();
        var thumbCt = _directoryThumbnailCts.Token;

        string dir = path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            dir = LibraryRootPath;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                dir = DownloadEngineService.Instance.DownloadRoot;
            }
        }

        if (!Directory.Exists(dir)) return;

        CurrentDirectoryPath = dir;
        ReadProgressStore.Instance.SaveLastLibraryRootPath(dir);
        IsDirectoryLoading = true;

        try
        {
            var result = await Task.Run(() =>
            {
                var list = new List<DirectoryItemModel>();
                int directImagesCount = 0;
                int chapterFoldersCount = 0;
                int comicFilesCount = 0;

                var enumOptions = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false
                };

                // 1. Thư mục con (Bảo vệ tuyệt đối khỏi crash hệ thống Android & thẻ nhớ MicroSD)
                try
                {
                    foreach (var d in Directory.EnumerateDirectories(dir, "*", enumOptions))
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            string name = Path.GetFileName(d);
                            if (string.IsNullOrWhiteSpace(name)) continue;

                            // Bỏ qua các thư mục ẩn, thư mục rác hệ thống và thư mục cấm trên Android
                            if (name.StartsWith(".") || name.StartsWith("$")) continue;
                            if (name.Equals("LOST.DIR", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals(".android_secure", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals(".thumbnails", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            // Thư mục "Android" (chứa data/obb bị SELinux cấm) -> hiển thị như folder thường, cấm chọc sâu
                            if (name.Equals("Android", StringComparison.OrdinalIgnoreCase))
                            {
                                list.Add(new DirectoryItemModel
                                {
                                    IsFolder = true,
                                    IsImageFile = false,
                                    CanReadDirectly = false,
                                    Name = name,
                                    FullPath = d,
                                    ItemCountOrSize = "Thư mục hệ thống",
                                    IconText = "📁",
                                    IconColor = "#64748B"
                                });
                                continue;
                            }

                            var di = new DirectoryInfo(d);
                            string label = "Thư mục";
                            string icon = "📁";
                            string iconCol = "#A78BFA";

                            // Kiểm tra siêu tốc (Non-blocking): chỉ thử tìm tối đa 1 file ảnh đầu tiên nếu không phải Android hoặc danh sách nhỏ
                            if (!OperatingSystem.IsAndroid() && list.Count < 30)
                            {
                                try
                                {
                                    bool hasDirectImages = di.EnumerateFiles("*", enumOptions)
                                        .Any(f => LibraryScannerService.IsImageFile(f.FullName));
                                    if (hasDirectImages)
                                    {
                                        label = "Chương truyện";
                                        icon = "🖼️";
                                        iconCol = "#10B981";
                                        chapterFoldersCount++;
                                    }
                                }
                                catch { }
                            }

                            list.Add(new DirectoryItemModel
                            {
                                IsFolder = true,
                                IsImageFile = false,
                                CanReadDirectly = true,
                                Name = name,
                                FullPath = d,
                                ItemCountOrSize = label,
                                IconText = icon,
                                IconColor = iconCol,
                                LastModified = di.LastWriteTime
                            });
                        }
                        catch { }
                    }
                }
                catch { }

                // 2. File truyện (.cbz, .zip, .pdf, .cbr, .rar) và FILE ẢNH ĐƠN LẺ!
                try
                {
                    int imageIdx = 0;
                    foreach (var f in Directory.EnumerateFiles(dir, "*", enumOptions))
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            string ext = Path.GetExtension(f);
                            if (string.IsNullOrEmpty(ext)) continue;

                            bool isArchive = LibraryScannerService.IsArchiveFile(f);
                            bool isPdf = LibraryScannerService.IsPdfFile(f);
                            bool isMobi = LibraryScannerService.IsMobiFile(f);
                            bool isImg = LibraryScannerService.IsImageFile(f);
                            if (!isArchive && !isPdf && !isMobi && !isImg) continue;

                            var fi = new FileInfo(f);
                            string fileSizeText = FormatBytes(fi.Length);
                            if (isImg)
                            {
                                directImagesCount++;
                                list.Add(new DirectoryItemModel
                                {
                                    IsFolder = false,
                                    IsImageFile = true,
                                    CanReadDirectly = true,
                                    Name = fi.Name,
                                    FullPath = f,
                                    FileSizeText = fileSizeText,
                                    ItemCountOrSize = fileSizeText,
                                    IconText = "🖼️",
                                    IconColor = "#38BDF8",
                                    Extension = ext,
                                    LastModified = fi.LastWriteTime,
                                    FileSizeBytes = fi.Length,
                                    ImageIndexInFolder = imageIdx++
                                });
                            }
                            else
                            {
                                comicFilesCount++;
                                int saved = ReadProgressStore.Instance.GetLastReadPage(f);
                                int total = ReadProgressStore.Instance.GetChapterTotalPages(f);
                                string progressText = string.Empty;
                                double progressPercent = 0;
                                if (saved > 0 && total > 0)
                                {
                                    progressPercent = Math.Clamp(Math.Round((double)saved * 100.0 / total), 0, 100);
                                    progressText = $"{progressPercent:0}%\n{saved}/{total}";
                                }

                                string summary = !string.IsNullOrEmpty(progressText)
                                    ? $"{fileSizeText}  {progressPercent:0}%"
                                    : fileSizeText;

                                list.Add(new DirectoryItemModel
                                {
                                    IsFolder = false,
                                    IsImageFile = false,
                                    CanReadDirectly = true,
                                    Name = fi.Name,
                                    FullPath = f,
                                    FileSizeText = fileSizeText,
                                    ReadingProgressText = progressText,
                                    ProgressPercent = progressPercent,
                                    ItemCountOrSize = summary,
                                    IconText = isPdf ? "📄" : (isMobi ? "📙" : "📦"),
                                    IconColor = isMobi ? "#F59E0B" : "#38BDF8",
                                    Extension = ext,
                                    LastModified = fi.LastWriteTime,
                                    FileSizeBytes = fi.Length
                                });
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                SortDirectoryInternal(list, DirectorySortMode);
                return (list, directImagesCount, chapterFoldersCount, comicFilesCount);
            }, ct);

            if (!ct.IsCancellationRequested)
            {
                var (list, directImagesCount, chapterFoldersCount, comicFilesCount) = result;
                if (directImagesCount > 0)
                {
                    CurrentDirectoryReadableStatus = $"📖 ĐỌC CHƯƠNG NÀY ({directImagesCount} ảnh)";
                }
                else if (chapterFoldersCount > 0)
                {
                    CurrentDirectoryReadableStatus = $"📖 ĐỌC TOÀN BỘ TRUYỆN ({chapterFoldersCount} chương)";
                }
                else if (comicFilesCount > 0)
                {
                    CurrentDirectoryReadableStatus = $"📖 ĐỌC THƯ MỤC TRUYỆN ({comicFilesCount} file)";
                }
                else
                {
                    CurrentDirectoryReadableStatus = string.Empty;
                }

                DirectoryItems.Clear();
                foreach (var item in list)
                {
                    DirectoryItems.Add(item);
                }

                // Tải Cover Thumbnail ngầm bất đồng bộ cho danh sách tệp/truyện (chuẩn Comic Screen)
                _ = Task.Run(async () =>
                {
                    foreach (var item in list)
                    {
                        if (thumbCt.IsCancellationRequested || ct.IsCancellationRequested) break;
                        if (item.HasCover) continue;
                        try
                        {
                            var bmp = _scanner.DecodeCoverThumbnailForPath(item.FullPath, 220);
                            if (bmp != null && !thumbCt.IsCancellationRequested && !ct.IsCancellationRequested)
                            {
                                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => item.Cover = bmp);
                            }
                        }
                        catch { }
                    }
                }, thumbCt);
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                StatusMessage = $"❌ Không thể mở thư mục: {ex.Message}";
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsDirectoryLoading = false;
            }
        }
    }

    private static void SortDirectoryInternal(List<DirectoryItemModel> list, string mode)
    {
        switch (mode)
        {
            case "NameDesc":
                list.Sort((a, b) =>
                {
                    if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
                    return NaturalSortComparer.Instance.Compare(b.Name, a.Name);
                });
                break;
            case "DateDesc":
                list.Sort((a, b) =>
                {
                    if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
                    return b.LastModified.CompareTo(a.LastModified);
                });
                break;
            case "SizeDesc":
                list.Sort((a, b) =>
                {
                    if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
                    return b.FileSizeBytes.CompareTo(a.FileSizeBytes);
                });
                break;
            default: // NameAsc
                list.Sort((a, b) =>
                {
                    if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
                    return NaturalSortComparer.Instance.Compare(a.Name, b.Name);
                });
                break;
        }
    }

    [RelayCommand]
    public void NavigateUpDirectory()
    {
        if (string.IsNullOrWhiteSpace(CurrentDirectoryPath)) return;
        try
        {
            string? parent = Path.GetDirectoryName(CurrentDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            {
                CurrentDirectoryPath = parent;
                LoadDirectory(parent);
            }
            else
            {
                SelectSubTab("Storage");
            }
        }
        catch { }
    }

    [RelayCommand]
    public async Task ReadCurrentDirectoryAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentDirectoryPath)) return;
        await ReadEntireFolderAsync(CurrentDirectoryPath);
    }

    [RelayCommand]
    public async Task ReadEntireFolderAsync(string? folderPath)
    {
        string target = folderPath ?? CurrentDirectoryPath;
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target)) return;

        IsDirectoryLoading = true;
        StatusMessage = "⏳ Đang chuẩn bị đọc truyện toàn bộ thư mục...";
        try
        {
            var comic = await Task.Run(() => _scanner.BuildComicFromFolderAllChapters(target));
            if (comic != null && comic.Chapters.Count > 0)
            {
                ReadProgressStore.Instance.RegisterReadComic(comic.RootFolderPath, touchTimestamp: true);
                var targetChap = comic.GetBestResumeChapter() ?? comic.Chapters[0];
                await OpenChapterInternalAsync(comic, targetChap);
                _ = LoadHistoryItemsAsync();
                LoadStorageItems();
            }
            else
            {
                await OpenPathToReadInternalAsync(target);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Lỗi khi đọc thư mục: {ex.Message}";
        }
        finally
        {
            IsDirectoryLoading = false;
        }
    }

    [RelayCommand]
    public async Task ReadDirectoryItemDirectlyAsync(DirectoryItemModel? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FullPath)) return;
        await OpenPathToReadInternalAsync(item.FullPath, item.IsImageFile ? item.ImageIndexInFolder : -1);
    }

    [RelayCommand]
    public async Task SelectDirectoryItemAsync(DirectoryItemModel? item)
    {
        if (item == null) return;
        _directoryThumbnailCts?.Cancel();
        if (item.IsFolder)
        {
            CurrentDirectoryPath = item.FullPath;
            LoadDirectory(item.FullPath);
        }
        else if (item.IsImageFile)
        {
            await OpenPathToReadInternalAsync(item.FullPath, item.ImageIndexInFolder);
        }
        else
        {
            await OpenPathToReadInternalAsync(item.FullPath);
        }
    }

    [RelayCommand]
    public void PinCurrentDirectoryToStorage()
    {
        if (string.IsNullOrWhiteSpace(CurrentDirectoryPath) || !Directory.Exists(CurrentDirectoryPath)) return;
        ReadProgressStore.Instance.AddPinnedFolder(CurrentDirectoryPath);
        LoadStorageItems();
        StatusMessage = $"📌 Đã ghim thư mục hiện tại vào Storage: {CurrentDirectoryPath}";
    }

    [RelayCommand]
    public void SetCurrentDirectoryAsLibraryRoot()
    {
        if (string.IsNullOrWhiteSpace(CurrentDirectoryPath) || !Directory.Exists(CurrentDirectoryPath)) return;
        LibraryRootPath = CurrentDirectoryPath;
        ReadProgressStore.Instance.SaveLastLibraryRootPath(CurrentDirectoryPath);
        StatusMessage = $"📁 Đã đặt thư mục Thư viện mặc định: {CurrentDirectoryPath}";
    }

    [RelayCommand]
    public void ToggleDirectoryViewMode()
    {
        IsDirectoryGridView = !IsDirectoryGridView;
    }

    [RelayCommand]
    public void SortDirectory(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        DirectorySortMode = mode;
        var currentList = DirectoryItems.ToList();
        SortDirectoryInternal(currentList, DirectorySortMode);
        DirectoryItems.Clear();
        foreach (var item in currentList)
        {
            DirectoryItems.Add(item);
        }
    }

    [RelayCommand]
    public void RefreshCurrentDirectory()
    {
        LoadDirectory(CurrentDirectoryPath);
        StatusMessage = $"🔄 Đã làm mới thư mục: {CurrentDirectoryPath}";
    }

    [RelayCommand]
    public void RefreshAllReadStatuses()
    {
        foreach (var comic in AllComics)
        {
            foreach (var ch in comic.Chapters)
            {
                ch.LastReadPage = ReadProgressStore.Instance.GetLastReadPage(ch.EffectiveProgressKey);
            }
            comic.RefreshReadStatus();
        }
        ApplyFilter();
    }

    [RelayCommand]
    public async Task RefreshHistoryAsync()
    {
        await LoadHistoryItemsAsync();
        StatusMessage = "✓ Đã làm mới danh sách lịch sử đọc";
    }

    public async Task LoadHistoryItemsAsync()
    {
        try
        {
            if (OperatingSystem.IsAndroid())
            {
                LibraryScannerService.AndroidGetExternalStoragePaths?.Invoke();
            }

            var unifiedList = ReadProgressStore.Instance.GetUnifiedHistoryItems();
            var items = new List<HistoryItemModel>();

            foreach (var u in unifiedList)
            {
                string sizeText = string.Empty;
                string dateText = string.Empty;

                if (u.HasLocalPath)
                {
                    try
                    {
                        if (File.Exists(u.Path))
                        {
                            var fi = new FileInfo(u.Path);
                            sizeText = FormatBytes(fi.Length);
                            dateText = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                        }
                        else if (Directory.Exists(u.Path))
                        {
                            var di = new DirectoryInfo(u.Path);
                            dateText = di.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                        }
                    }
                    catch { }
                }
                else
                {
                    sizeText = "☁️ Đồng bộ đám mây";
                    if (u.OrderStamp > 0)
                    {
                        try
                        {
                            dateText = DateTimeOffset.FromUnixTimeSeconds(u.OrderStamp + 1700000000L).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                        }
                        catch { }
                    }
                }

                int lastPage = u.LastReadPage;
                int totalPages = u.TotalPages;
                int percent = totalPages > 0 ? Math.Clamp((int)Math.Round((double)lastPage / totalPages * 100), 0, 100) : 0;
                string progressText = totalPages > 0 ? $"{lastPage}/{totalPages}    {percent}%" : (lastPage > 0 ? $"Trang {lastPage}" : "0%");

                items.Add(new HistoryItemModel
                {
                    Path = u.Path,
                    Title = u.Title,
                    ChapterOrSubTitle = u.ChapterOrSubTitle,
                    FileSizeText = sizeText,
                    LastReadDateText = dateText,
                    CurrentPage = lastPage,
                    TotalPages = totalPages,
                    ProgressPercent = percent,
                    ProgressText = progressText,
                    ProgressBarColor = percent >= 100 ? "#10B981" : "#E91E63",
                    IsCloudSynced = !string.IsNullOrWhiteSpace(u.DeviceName),
                    CloudDeviceName = u.DeviceName
                });
            }

            HistoryItems.Clear();
            foreach (var it in items)
            {
                HistoryItems.Add(it);
            }

            // Tải ảnh bìa ngầm trên task nền cho các truyện có đường dẫn cục bộ
            _ = Task.Run(() =>
            {
                foreach (var h in HistoryItems)
                {
                    if (h.Cover != null || !h.HasLocalPath) continue;
                    try
                    {
                        var bmp = _scanner.DecodeCoverThumbnailForPath(h.Path, 200);
                        if (bmp != null)
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => h.Cover = bmp);
                        }
                    }
                    catch { }
                }
            });
        }
        catch { }
    }

    [RelayCommand]
    public async Task OpenHistoryItemAsync(HistoryItemModel? item)
    {
        if (item == null) return;

        if (item.HasLocalPath)
        {
            await OpenPathToReadAsync(item.Path);
            return;
        }

        // Thử tìm nhanh đường dẫn cục bộ trên thiết bị
        string? resolved = ReadProgressStore.Instance.TryResolveLocalPathForComic(item.Title);
        if (!string.IsNullOrWhiteSpace(resolved) && (File.Exists(resolved) || Directory.Exists(resolved)))
        {
            item.Path = resolved;
            await OpenPathToReadAsync(resolved);
            _ = LoadHistoryItemsAsync();
            return;
        }

        string devMsg = !string.IsNullOrWhiteSpace(item.CloudDeviceName) ? $" từ [{item.CloudDeviceName}]" : string.Empty;
        StatusMessage = $"⚠️ Bộ truyện '{item.Title}' được đồng bộ{devMsg} nhưng chưa tìm thấy trên thiết bị này.";
        App.TryGetSharedMainViewModel()?.ShowCenterToast(
            "CHƯA TÌM THẤY FILE TRUYỆN",
            $"Bộ truyện '{item.Title}' được đọc{devMsg} nhưng chưa có tệp trên máy này. Vui lòng đặt file/thư mục truyện vào bộ nhớ để tiếp tục đọc.",
            "☁️",
            "LỊCH SỬ ĐỒNG BỘ",
            3500);
    }

    [RelayCommand]
    public void OpenHistoryItemFolder(HistoryItemModel? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Path)) return;
        string targetDir = item.Path;
        if (File.Exists(targetDir))
        {
            targetDir = Path.GetDirectoryName(targetDir) ?? targetDir;
        }

        if (Directory.Exists(targetDir))
        {
            CurrentDirectoryPath = targetDir;
            LoadDirectory(targetDir);
            SelectSubTab("Directory");
        }
    }

    [RelayCommand]
    public void RemoveHistoryItem(HistoryItemModel? item)
    {
        if (item == null) return;
        ReadProgressStore.Instance.RemoveHistory(item.Path);
        HistoryItems.Remove(item);
        LoadStorageItems();
        StatusMessage = $"🗑️ Đã xóa '{item.Title}' khỏi Lịch sử đọc.";
    }

    [RelayCommand]
    public void ClearAllHistory()
    {
        ReadProgressStore.Instance.ClearAllHistory();
        HistoryItems.Clear();
        LoadStorageItems();
        StatusMessage = "🗑️ Đã xóa toàn bộ Lịch sử đọc.";
    }

    [RelayCommand]
    public void ClearComicResume(object? param)
    {
        string? path = null;
        string? title = null;

        if (param is HistoryItemModel h)
        {
            path = h.Path;
            title = h.Title;
            h.CurrentPage = 0;
            h.ProgressPercent = 0;
            h.ProgressText = h.TotalPages > 0 ? $"0/{h.TotalPages}    0%" : "0%";
        }
        else if (param is LibraryComicItem c)
        {
            path = c.RootFolderPath;
            title = c.Title;
        }
        else if (param is DirectoryItemModel d)
        {
            path = d.FullPath;
            title = d.Name;
            d.ReadingProgressText = string.Empty;
            d.ProgressPercent = 0;
        }
        else if (param is string s)
        {
            path = s;
            title = Path.GetFileName(s.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        if (string.IsNullOrWhiteSpace(path)) return;

        ReadProgressStore.Instance.ClearProgressForComic(path);

        if (param is LibraryComicItem comicItem)
        {
            comicItem.RefreshReadStatus();
            ApplyFilter();
        }
        else
        {
            var existing = AllComics.FirstOrDefault(ci => string.Equals(ci.RootFolderPath, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.RefreshReadStatus();
                ApplyFilter();
            }
        }

        StatusMessage = $"🔄 Đã xóa vị trí đọc dở của '{title ?? path}'.";
    }

    [RelayCommand]
    public void ClearComicBookmarks(object? param)
    {
        string? path = null;
        string? title = null;

        if (param is HistoryItemModel h)
        {
            path = h.Path;
            title = h.Title;
        }
        else if (param is LibraryComicItem c)
        {
            path = c.RootFolderPath;
            title = c.Title;
        }
        else if (param is BookmarkGroupModel bg)
        {
            path = bg.ComicRootPath;
            title = bg.ComicTitle;
        }
        else if (param is DirectoryItemModel d)
        {
            path = d.FullPath;
            title = d.Name;
        }
        else if (param is string s)
        {
            path = s;
            title = Path.GetFileName(s.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(title)) return;

        int count = ReadProgressStore.Instance.ClearBookmarksForComic(path ?? string.Empty, title);
        LoadBookmarkGroups();

        StatusMessage = count > 0
            ? $"📑 Đã xóa toàn bộ {count} dấu trang của '{title ?? path}'."
            : $"📑 Truyện '{title ?? path}' không có dấu trang nào.";
    }

    public void LoadBookmarkGroups()
    {
        try
        {
            var rawBookmarks = ReadProgressStore.Instance.GetBookmarks(null);
            BookmarkGroups.Clear();

            var groups = rawBookmarks
                .GroupBy(b => !string.IsNullOrWhiteSpace(b.ComicTitle) ? b.ComicTitle : Path.GetFileName(b.ComicRootPath))
                .OrderBy(g => g.Key, NaturalSortComparer.Instance);

            foreach (var g in groups)
            {
                var grpModel = new BookmarkGroupModel
                {
                    ComicTitle = g.Key,
                    ComicRootPath = g.FirstOrDefault()?.ComicRootPath ?? string.Empty
                };

                foreach (var bm in g.OrderByDescending(b => b.CreatedAtUnixSeconds))
                {
                    int dispPage = bm.DisplayPageNumber;
                    int tot = bm.TotalPages;
                    string pageText = tot > 0 ? $"Trang {dispPage} / {tot}" : $"Trang {dispPage}";
                    string pctText = tot > 0 ? $"{(dispPage * 100 / tot)}%" : string.Empty;
                    string dateStr = DateTimeOffset.FromUnixTimeSeconds(bm.CreatedAtUnixSeconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

                    grpModel.Bookmarks.Add(new BookmarkItemDisplayModel
                    {
                        Bookmark = bm,
                        ChapterTitle = !string.IsNullOrWhiteSpace(bm.ChapterTitle) ? bm.ChapterTitle : bm.ComicTitle,
                        PageProgressText = pageText,
                        PercentText = pctText,
                        DateText = dateStr,
                        Memo = bm.Memo
                    });
                }

                BookmarkGroups.Add(grpModel);
            }
        }
        catch { }
    }

    [RelayCommand]
    public async Task OpenBookmarkAsync(BookmarkItemDisplayModel? item)
    {
        if (item?.Bookmark == null) return;
        var bm = item.Bookmark;

        string comicPath = bm.ComicRootPath;
        if (string.IsNullOrWhiteSpace(comicPath) || (!File.Exists(comicPath) && !Directory.Exists(comicPath)))
        {
            comicPath = bm.ChapterLocalPath;
        }
        if (string.IsNullOrWhiteSpace(comicPath)) return;

        var comic = await Task.Run(() => _scanner.CreateQuickComicFromPath(comicPath));
        if (comic == null || comic.Chapters.Count == 0) return;

        LibraryChapterItem? targetChapter = null;
        if (!string.IsNullOrWhiteSpace(bm.ChapterProgressKey))
        {
            targetChapter = comic.Chapters.FirstOrDefault(c => string.Equals(c.EffectiveProgressKey, bm.ChapterProgressKey, StringComparison.OrdinalIgnoreCase));
        }
        if (targetChapter == null && !string.IsNullOrWhiteSpace(bm.ChapterLocalPath))
        {
            targetChapter = comic.Chapters.FirstOrDefault(c => string.Equals(c.LocalPath, bm.ChapterLocalPath, StringComparison.OrdinalIgnoreCase));
        }
        if (targetChapter == null && !string.IsNullOrWhiteSpace(bm.ChapterTitle))
        {
            targetChapter = comic.Chapters.FirstOrDefault(c => string.Equals(c.Title, bm.ChapterTitle, StringComparison.OrdinalIgnoreCase));
        }
        if (targetChapter == null)
        {
            int idx = Math.Clamp(bm.ChapterIndex - 1, 0, comic.Chapters.Count - 1);
            targetChapter = comic.Chapters[idx];
        }

        ReadProgressStore.Instance.SaveProgress(targetChapter.EffectiveProgressKey, bm.PageIndex);
        await OpenChapterInternalAsync(comic, targetChapter);
    }

    [RelayCommand]
    public void RemoveBookmark(BookmarkItemDisplayModel? item)
    {
        if (item?.Bookmark == null) return;
        ReadProgressStore.Instance.RemoveBookmark(item.Bookmark.Id);
        LoadBookmarkGroups();
        StatusMessage = $"🗑️ Đã xóa bookmark trang {item.PageProgressText}.";
    }

    [RelayCommand]
    public void ClearAllBookmarks()
    {
        ReadProgressStore.Instance.ClearAllBookmarks();
        BookmarkGroups.Clear();
        StatusMessage = "🗑️ Đã xóa toàn bộ Bookmark.";
    }

    [RelayCommand]
    public async Task BackupReaderDataAsync()
    {
        int bmCount = ReadProgressStore.Instance.GetBookmarkCount();
        int histCount = ReadProgressStore.Instance.GetAllHistoryPaths(checkPathExists: false).Count;

        if (bmCount == 0 && histCount == 0)
        {
            StatusMessage = "⚠️ Chưa có Dấu trang hoặc Lịch sử đọc nào để sao lưu.";
            return;
        }

        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null && topLevel.StorageProvider.CanSave)
            {
                IStorageFolder? suggestedFolder = null;
                try
                {
                    string autoDir = QueueBackupService.GetAutoSaveDirectory();
                    if (Directory.Exists(autoDir))
                    {
                        suggestedFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(autoDir);
                    }
                }
                catch { }

                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Chọn nơi lưu file sao lưu Dấu trang & Lịch sử đọc (.md)",
                    SuggestedFileName = ReadProgressStore.SavedReaderFileName,
                    DefaultExtension = "md",
                    ShowOverwritePrompt = true,
                    SuggestedStartLocation = suggestedFolder,
                    FileTypeChoices = new List<FilePickerFileType>
                    {
                        new("Markdown Reader Backup (*.md)") { Patterns = new[] { "*.md" } },
                        new("All Files (*.*)") { Patterns = new[] { "*" } }
                    }
                });

                if (file == null)
                {
                    StatusMessage = "Đã hủy chọn nơi lưu file sao lưu (.md).";
                    return;
                }

                string? localPath = file.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(localPath))
                {
                    string savedPath = await ReadProgressStore.Instance.SaveReaderToMarkdownAsync(localPath);
                    StatusMessage = $"💾 Đã sao lưu thành công {bmCount} dấu trang và {histCount} lịch sử ra file Markdown: {Path.GetFileName(savedPath)}";
                }
                else
                {
                    string content = ReadProgressStore.Instance.BuildReaderMarkdownContent();
                    await using var stream = await file.OpenWriteAsync();
                    stream.SetLength(0);
                    await using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
                    await writer.WriteAsync(content);
                    await writer.FlushAsync();
                    StatusMessage = $"💾 Đã sao lưu thành công {bmCount} dấu trang và {histCount} lịch sử ra file Markdown: {file.Name}";
                }
                return;
            }

            string path = await ReadProgressStore.Instance.SaveReaderToMarkdownAsync();
            StatusMessage = $"💾 Đã sao lưu thành công ra file Markdown: {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Lỗi khi sao lưu Dấu trang & Lịch sử: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task RestoreReaderDataAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null && topLevel.StorageProvider.CanOpen)
            {
                IStorageFolder? suggestedFolder = null;
                try
                {
                    string autoDir = QueueBackupService.GetAutoSaveDirectory();
                    if (Directory.Exists(autoDir))
                    {
                        suggestedFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(autoDir);
                    }
                }
                catch { }

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Chọn file Markdown (.md) để phục hồi Dấu trang & Lịch sử đọc",
                    AllowMultiple = false,
                    SuggestedStartLocation = suggestedFolder,
                    FileTypeFilter = new List<FilePickerFileType>
                    {
                        new("Markdown Reader Backup (*.md)") { Patterns = new[] { "*.md", "*.txt" } },
                        new("All Files (*.*)") { Patterns = new[] { "*" } }
                    }
                });

                if (files == null || files.Count == 0)
                {
                    StatusMessage = "Đã hủy chọn file phục hồi Markdown (.md).";
                    return;
                }

                var selectedFile = files[0];
                string? localPath = selectedFile.TryGetLocalPath();
                ReaderRestoreResult res;

                if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
                {
                    res = await ReadProgressStore.Instance.LoadReaderFromMarkdownAsync(localPath, mergeWithExisting: true);
                }
                else
                {
                    await using var stream = await selectedFile.OpenReadAsync();
                    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                    string content = await reader.ReadToEndAsync();
                    res = ReadProgressStore.Instance.ParseReaderMarkdownContent(content, mergeWithExisting: true, selectedFile.Name);
                }

                if (res.HasValidStructure)
                {
                    _ = LoadHistoryItemsAsync();
                    LoadBookmarkGroups();
                    LoadStorageItems();
                    RefreshAllReadStatuses();
                    StatusMessage = $"✅ Đã phục hồi thành công {res.RestoredBookmarks} dấu trang và {res.RestoredHistory} mục lịch sử từ file Markdown!";
                }
                else
                {
                    StatusMessage = "⚠️ File Markdown không đúng định dạng sao lưu của Comic Downloader GMTPC.";
                }
                return;
            }

            var fallbackRes = await ReadProgressStore.Instance.LoadReaderFromMarkdownAsync(null, mergeWithExisting: true);
            if (fallbackRes.HasValidStructure)
            {
                _ = LoadHistoryItemsAsync();
                LoadBookmarkGroups();
                LoadStorageItems();
                RefreshAllReadStatuses();
                StatusMessage = $"✅ Đã phục hồi thành công {fallbackRes.RestoredBookmarks} dấu trang và {fallbackRes.RestoredHistory} mục lịch sử!";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Lỗi khi phục hồi Dấu trang & Lịch sử: {ex.Message}";
        }
    }

    public Action? OpenGoogleDriveAction { get; set; }

    [RelayCommand]
    public void OpenGoogleDrive()
    {
        OpenGoogleDriveAction?.Invoke();
    }

    public async Task ReloadAllLibraryStateAsync()
    {
        await LoadHistoryItemsAsync();
        LoadBookmarkGroups();
        LoadStorageItems();
        RefreshAllReadStatuses();
    }

    [RelayCommand]
    public async Task OpenMostRecentComicAsync()
    {
        var recentPaths = ReadProgressStore.Instance.GetKnownReadComicPaths();
        string? latestPath = recentPaths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)));

        if (string.IsNullOrWhiteSpace(latestPath) && OperatingSystem.IsAndroid() && recentPaths.Count > 0)
        {
            LibraryScannerService.AndroidGetExternalStoragePaths?.Invoke();
            await Task.Delay(100);
            latestPath = recentPaths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)));
        }

        if (!string.IsNullOrWhiteSpace(latestPath))
        {
            await OpenPathToReadInternalAsync(latestPath);
        }
        else
        {
            StatusMessage = "ℹ️ Chưa có truyện đọc gần đây.";
        }
    }

    [RelayCommand]
    public async Task OpenPathToReadAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        await OpenPathToReadInternalAsync(path);
    }

    private async Task OpenPathToReadInternalAsync(string path, int targetPageIndex = -1)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _directoryThumbnailCts?.Cancel();
        try
        {
            string actualComicPath = path;
            int pageToJump = targetPageIndex;

            // Nếu path là file ảnh đơn lẻ: chuyển hướng sang thư mục cha chứa ảnh và tìm index của trang
            if (File.Exists(path) && LibraryScannerService.IsImageFile(path))
            {
                actualComicPath = Path.GetDirectoryName(path) ?? path;
                if (pageToJump < 0)
                {
                    try
                    {
                        var enumOpts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
                        var allImgs = Directory.EnumerateFiles(actualComicPath, "*", enumOpts)
                            .Where(LibraryScannerService.IsImageFile)
                            .NaturalSort();
                        int idx = allImgs.FindIndex(img => string.Equals(img, path, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0) pageToJump = idx;
                    }
                    catch { }
                }
            }

            var quickComic = await Task.Run(() => _scanner.CreateQuickComicFromPath(actualComicPath));
            if ((quickComic == null || quickComic.Chapters.Count == 0) && OperatingSystem.IsAndroid() && actualComicPath.Contains("/storage/"))
            {
                LibraryScannerService.AndroidGetExternalStoragePaths?.Invoke();
                await Task.Delay(150);
                quickComic = await Task.Run(() => _scanner.CreateQuickComicFromPath(actualComicPath));
            }

            if (quickComic != null && quickComic.Chapters.Count > 0)
            {
                ReadProgressStore.Instance.RegisterReadComic(quickComic.RootFolderPath, touchTimestamp: true);

                LibraryChapterItem? targetChap = null;
                string chDir = Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(chDir))
                {
                    targetChap = quickComic.Chapters.FirstOrDefault(c =>
                        string.Equals(c.LocalPath.TrimEnd('\\', '/'), chDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.Title, Path.GetFileName(chDir), StringComparison.OrdinalIgnoreCase));
                }

                targetChap ??= quickComic.GetBestResumeChapter() ?? quickComic.Chapters[0];

                if (pageToJump >= 0)
                {
                    ReadProgressStore.Instance.SaveProgress(targetChap.EffectiveProgressKey, pageToJump);
                    targetChap.LastReadPage = pageToJump + 1;
                }

                await OpenChapterInternalAsync(quickComic, targetChap);
                _ = LoadHistoryItemsAsync();
                LoadStorageItems();
            }
            else
            {
                StatusMessage = $"⚠️ Không tìm thấy ảnh hoặc chương trong: {path}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Không thể mở đọc: {ex.Message}";
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

    // =========================================================================
    // MODAL QUẢN LÝ BỘ NHỚ ĐỆM (CACHE DATA) CHUẨN COMIC SCREEN
    // =========================================================================
    [ObservableProperty]
    private bool _isCacheDataModalOpen = false;

    [ObservableProperty]
    private string _thumbnailStorageSummaryText = "Thumbnails 0 B / 1.0 GB";

    [ObservableProperty]
    private string _archiveCacheSummaryText = "0 B";

    [ObservableProperty]
    private string _retentionPeriodSummaryText = "4 days";

    [ObservableProperty]
    private string _totalCacheSummaryText = "0 B";

    [ObservableProperty]
    private string _zipCacheStorageSummaryText = string.Empty;

    [ObservableProperty]
    private bool _isThumbnailQuotaDialogOpen = false;

    [ObservableProperty]
    private int _selectedThumbnailQuotaMb = 1000;

    [ObservableProperty]
    private bool _isZipQuotaDialogOpen = false;

    [ObservableProperty]
    private int _selectedZipQuotaMb = 1000;

    [ObservableProperty]
    private bool _isRetentionDialogOpen = false;

    [ObservableProperty]
    private int _selectedRetentionDays = 4;

    public void RefreshCacheSummary()
    {
        ThumbnailStorageSummaryText = ComicDiskCacheService.Instance.GetThumbnailsStorageSummaryText();
        ZipCacheStorageSummaryText = ComicDiskCacheService.Instance.GetZipCacheStorageSummaryText();
        ArchiveCacheSummaryText = ComicDiskCacheService.Instance.GetArchiveCacheSummaryText();
        RetentionPeriodSummaryText = ComicDiskCacheService.Instance.GetRetentionPeriodSummaryText();
        TotalCacheSummaryText = ComicDiskCacheService.FormatBytes(ComicDiskCacheService.Instance.GetTotalCacheSizeBytes());
        SelectedThumbnailQuotaMb = ComicDiskCacheService.Instance.ThumbnailQuotaMb;
        SelectedZipQuotaMb = ComicDiskCacheService.Instance.ZipCacheQuotaMb;
        SelectedRetentionDays = ComicDiskCacheService.Instance.RetentionDays;
    }

    [RelayCommand]
    public void OpenCacheDataModal()
    {
        RefreshCacheSummary();
        IsCacheDataModalOpen = true;
    }

    [RelayCommand]
    public void CloseCacheDataModal()
    {
        IsCacheDataModalOpen = false;
        IsThumbnailQuotaDialogOpen = false;
        IsZipQuotaDialogOpen = false;
        IsRetentionDialogOpen = false;
    }

    [RelayCommand]
    public void OpenZipQuotaDialog()
    {
        SelectedZipQuotaMb = ComicDiskCacheService.Instance.ZipCacheQuotaMb;
        IsZipQuotaDialogOpen = true;
    }

    [RelayCommand]
    public void CloseZipQuotaDialog()
    {
        IsZipQuotaDialogOpen = false;
    }

    [RelayCommand]
    public void SelectZipQuota(object? quotaObj)
    {
        if (quotaObj != null && int.TryParse(quotaObj.ToString(), out int mb))
        {
            SelectedZipQuotaMb = mb;
        }
    }

    [RelayCommand]
    public void ConfirmZipQuota()
    {
        ComicDiskCacheService.Instance.ZipCacheQuotaMb = SelectedZipQuotaMb;
        IsZipQuotaDialogOpen = false;
        RefreshCacheSummary();
        StatusMessage = $"✓ Đã lưu dung lượng zip cache: {SelectedZipQuotaMb} MB";
    }

    [RelayCommand]
    public void OpenThumbnailQuotaDialog()
    {
        SelectedThumbnailQuotaMb = ComicDiskCacheService.Instance.ThumbnailQuotaMb;
        IsThumbnailQuotaDialogOpen = true;
    }

    [RelayCommand]
    public void CloseThumbnailQuotaDialog()
    {
        IsThumbnailQuotaDialogOpen = false;
    }

    [RelayCommand]
    public void SelectThumbnailQuota(object? quotaObj)
    {
        if (quotaObj != null && int.TryParse(quotaObj.ToString(), out int mb))
        {
            SelectedThumbnailQuotaMb = mb;
        }
    }

    [RelayCommand]
    public void ConfirmThumbnailQuota()
    {
        ComicDiskCacheService.Instance.ThumbnailQuotaMb = SelectedThumbnailQuotaMb;
        IsThumbnailQuotaDialogOpen = false;
        RefreshCacheSummary();
        StatusMessage = $"✓ Đã lưu hạn mức bộ nhớ ảnh bìa: {SelectedThumbnailQuotaMb} MB";
    }

    [RelayCommand]
    public void OpenRetentionDialog()
    {
        SelectedRetentionDays = ComicDiskCacheService.Instance.RetentionDays;
        IsRetentionDialogOpen = true;
    }

    [RelayCommand]
    public void CloseRetentionDialog()
    {
        IsRetentionDialogOpen = false;
    }

    [RelayCommand]
    public void SelectRetentionDays(object? daysObj)
    {
        if (daysObj != null && int.TryParse(daysObj.ToString(), out int days))
        {
            SelectedRetentionDays = days;
        }
    }

    [RelayCommand]
    public void ConfirmRetentionDays()
    {
        ComicDiskCacheService.Instance.RetentionDays = SelectedRetentionDays;
        IsRetentionDialogOpen = false;
        RefreshCacheSummary();
        string text = SelectedRetentionDays <= 0 ? "Vĩnh viễn (Không bao giờ xóa)" : $"{SelectedRetentionDays} ngày";
        StatusMessage = $"✓ Đã lưu thời hạn lưu trữ tệp nén: {text}";
    }

    [RelayCommand]
    public void ClearThumbnails()
    {
        ComicDiskCacheService.Instance.ClearThumbnails();
        RefreshCacheSummary();
        StatusMessage = "✓ Đã dọn dẹp sạch bộ nhớ đệm ảnh bìa (Thumbnails)";
    }

    [RelayCommand]
    public void ClearArchiveCache()
    {
        ComicDiskCacheService.Instance.ClearArchiveCache();
        RefreshCacheSummary();
        StatusMessage = "✓ Đã dọn dẹp bộ nhớ đệm tệp nén & tài liệu (.zip, .cbz, .rar, .cbr, .pdf, .mobi)";
    }

    [RelayCommand]
    public void ClearResumeCache()
    {
        ComicDiskCacheService.Instance.ClearResumeCache();
        RefreshCacheSummary();
        StatusMessage = "✓ Đã xóa dữ liệu vị trí trang đang đọc dở (Resume cache)";
    }

    [RelayCommand]
    public void ClearBookmarks()
    {
        ComicDiskCacheService.Instance.ClearBookmarks();
        RefreshCacheSummary();
        LoadBookmarkGroups();
        StatusMessage = "✓ Đã xóa toàn bộ dấu trang (Bookmarks)";
    }

    [RelayCommand]
    public void ClearHistory()
    {
        ComicDiskCacheService.Instance.ClearHistory();
        RefreshCacheSummary();
        _ = LoadHistoryItemsAsync();
        StatusMessage = "✓ Đã xóa toàn bộ lịch sử đọc truyện (History)";
    }

    [RelayCommand]
    public void ClearAllCache()
    {
        ComicDiskCacheService.Instance.ClearAllCache();
        RefreshCacheSummary();
        LoadBookmarkGroups();
        _ = LoadHistoryItemsAsync();
        StatusMessage = "✓ Đã dọn dẹp toàn bộ 100% bộ nhớ đệm và dữ liệu đọc";
    }
}

