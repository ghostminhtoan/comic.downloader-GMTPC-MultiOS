using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class ComicReaderViewModel : ViewModelBase
{
    private readonly ComicReaderService _readerService = ComicReaderService.Instance;
    private readonly ReadProgressStore _progressStore = ReadProgressStore.Instance;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _prefetchCts;
    private bool _suppressChapterSelectionChanged;
    private bool _suppressPdfBookmarkSelectionChanged;
    private bool _suppressScrollSync;

    public LibraryComicItem? ParentComic { get; private set; }

    public ObservableCollection<LibraryChapterItem> ChapterList { get; } = new();

    public ObservableCollection<PdfBookmarkItem> PdfBookmarks { get; } = new();

    public ObservableCollection<ReaderPageItem> Pages { get; } = new();

    [ObservableProperty]
    private PdfBookmarkItem? _selectedPdfBookmark;

    public bool HasPdfBookmarks => PdfBookmarks.Count > 0;

    [ObservableProperty]
    private string _comicTitle = string.Empty;

    [ObservableProperty]
    private string _chapterTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoPrevChapter))]
    [NotifyPropertyChangedFor(nameof(CanGoNextChapter))]
    private LibraryChapterItem? _currentChapter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageDisplayNumber))]
    [NotifyPropertyChangedFor(nameof(PageCounterText))]
    [NotifyPropertyChangedFor(nameof(ProgressPercentText))]
    [NotifyPropertyChangedFor(nameof(CanGoPrevPage))]
    [NotifyPropertyChangedFor(nameof(CanGoNextPage))]
    private int _currentPageIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageCounterText))]
    [NotifyPropertyChangedFor(nameof(MaxPageIndex))]
    private int _totalPages;

    public int MaxPageIndex => Math.Max(0, TotalPages - 1);

    public int CurrentPageDisplayNumber => TotalPages == 0 ? 0 : CurrentPageIndex + 1;

    public string PageCounterText => TotalPages == 0 ? "0 / 0" : $"Trang {CurrentPageIndex + 1} / {TotalPages}";

    public string ProgressPercentText =>
        TotalPages > 0
            ? $"{Math.Clamp((double)(CurrentPageIndex + 1) / TotalPages * 100.0, 0, 100):0}%"
            : "0%";

    public bool CanGoPrevPage => CurrentPageIndex > 0;

    public bool CanGoNextPage => CurrentPageIndex < TotalPages - 1;

    public bool CanGoPrevChapter =>
        CurrentChapter != null && ChapterList.IndexOf(CurrentChapter) > 0;

    public bool CanGoNextChapter =>
        CurrentChapter != null &&
        ChapterList.IndexOf(CurrentChapter) >= 0 &&
        ChapterList.IndexOf(CurrentChapter) < ChapterList.Count - 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftFlipPage))]
    [NotifyPropertyChangedFor(nameof(RightFlipPage))]
    private ReaderPageItem? _currentPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftFlipPage))]
    [NotifyPropertyChangedFor(nameof(RightFlipPage))]
    [NotifyPropertyChangedFor(nameof(HasSecondaryPage))]
    private ReaderPageItem? _secondaryPage;

    public bool HasSecondaryPage => IsDoublePage && SecondaryPage != null;

    public ReaderPageItem? LeftFlipPage =>
        !IsDoublePage
            ? CurrentPage
            : (IsRightToLeft ? (SecondaryPage ?? CurrentPage) : CurrentPage);

    public ReaderPageItem? RightFlipPage =>
        !IsDoublePage
            ? null
            : (IsRightToLeft ? CurrentPage : SecondaryPage);

    /// <summary>
    /// Mặc định mở ở chế độ Scroll dọc (Webtoon). False = Chế độ Lật trang (Flip).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFlipMode))]
    [NotifyPropertyChangedFor(nameof(ModeButtonText))]
    [NotifyPropertyChangedFor(nameof(ModeBadgeBackground))]
    private bool _isScrollMode = true;

    public bool IsFlipMode => !IsScrollMode;

    public string ModeButtonText => IsScrollMode ? "📜 Chế độ: Cuộn Dọc" : "📄 Chế độ: Lật Trang";

    public string ModeBadgeBackground => IsScrollMode ? "#0284C7" : "#7C3AED";

    /// <summary>
    /// Bật/tắt chế độ đọc từ Phải sang Trái (Manga RTL) khi ở chế độ Lật trang.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RtlButtonText))]
    [NotifyPropertyChangedFor(nameof(RtlButtonBackground))]
    [NotifyPropertyChangedFor(nameof(LeftFlipPage))]
    [NotifyPropertyChangedFor(nameof(RightFlipPage))]
    private bool _isRightToLeft = false;

    public string RtlButtonText => IsRightToLeft ? "◀ RTL: Phải → Trái" : "▶ LTR: Trái → Phải";

    public string RtlButtonBackground => IsRightToLeft ? "#D97706" : "#1E293B";

    /// <summary>
    /// Bật/tắt ghép 2 trang song song trong chế độ Lật trang.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DoublePageButtonText))]
    [NotifyPropertyChangedFor(nameof(DoublePageButtonBackground))]
    [NotifyPropertyChangedFor(nameof(FlipGridColumns))]
    [NotifyPropertyChangedFor(nameof(HasSecondaryPage))]
    [NotifyPropertyChangedFor(nameof(LeftFlipPage))]
    [NotifyPropertyChangedFor(nameof(RightFlipPage))]
    private bool _isDoublePage = false;

    public string DoublePageButtonText => IsDoublePage ? "📖 2 Trang Kép" : "📄 1 Trang Đơn";

    public string DoublePageButtonBackground => IsDoublePage ? "#059669" : "#1E293B";

    public int FlipGridColumns => (IsDoublePage && SecondaryPage != null) ? 2 : 1;

    /// <summary>
    /// Lớp phủ điều khiển kiểu ComicScreen (mặc định ẩn khi mở để đọc toàn màn hình, chạm vào giữa để bật/tắt).
    /// </summary>
    [ObservableProperty]
    private bool _isOverlayVisible = false;

    /// <summary>
    /// Tự động toàn màn hình khi mở trình đọc.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullScreenButtonText))]
    private bool _isFullScreen = true;

    public string FullScreenButtonText => IsFullScreen ? "🗗 Thoát Fullscreen" : "⛶ Toàn Màn Hình";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercentText))]
    [NotifyPropertyChangedFor(nameof(PageDisplayWidth))]
    private double _zoomLevel = 1.0;

    public string ZoomPercentText => $"{Math.Round(ZoomLevel * 100)}%";

    public double PageDisplayWidth => Math.Round(900 * ZoomLevel);

    [ObservableProperty]
    private bool _isLoadingChapter;

    [ObservableProperty]
    private string _statusToastText = string.Empty;

    [ObservableProperty]
    private bool _isStatusToastVisible;

    public event Action<int>? ScrollToPageRequested;
    public event Action<bool>? FullScreenChangedRequested;
    public event Action? CloseRequested;

    public async Task InitializeAsync(LibraryComicItem? comic, LibraryChapterItem targetChapter)
    {
        ParentComic = comic;
        ComicTitle = comic?.Title ?? targetChapter.Title;

        _suppressChapterSelectionChanged = true;
        ChapterList.Clear();
        if (comic != null && comic.Chapters.Count > 0)
        {
            foreach (var ch in comic.Chapters)
            {
                ChapterList.Add(ch);
            }
        }
        else
        {
            ChapterList.Add(targetChapter);
        }
        CurrentChapter = targetChapter;
        _suppressChapterSelectionChanged = false;

        OnPropertyChanged(nameof(CanGoPrevChapter));
        OnPropertyChanged(nameof(CanGoNextChapter));

        await LoadChapterInternalAsync(targetChapter);
    }

    partial void OnCurrentChapterChanged(LibraryChapterItem? value)
    {
        if (_suppressChapterSelectionChanged || value == null) return;
        _ = LoadChapterInternalAsync(value);
    }

    partial void OnSelectedPdfBookmarkChanged(PdfBookmarkItem? value)
    {
        if (_suppressPdfBookmarkSelectionChanged || value == null || Pages.Count == 0) return;
        int targetIndex = Math.Clamp(value.PageIndex, 0, Pages.Count - 1);
        if (CurrentPageIndex != targetIndex)
        {
            CurrentPageIndex = targetIndex;
            ShowTransientToast($"📑 Đã chuyển tới mục lục PDF: {value.Title} (Trang {targetIndex + 1})");
        }
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        if (Pages.Count == 0) return;
        int clamped = Math.Clamp(value, 0, Pages.Count - 1);
        if (clamped != value)
        {
            _currentPageIndex = clamped;
        }

        UpdateCurrentFlipPages();
        SyncActivePdfBookmark(clamped);
        RecordReadingProgress(clamped);
        TriggerPrefetch(clamped);

        if (IsScrollMode && !_suppressScrollSync)
        {
            ScrollToPageRequested?.Invoke(clamped);
        }
    }

    private void SyncActivePdfBookmark(int pageIndex)
    {
        if (PdfBookmarks.Count == 0) return;
        PdfBookmarkItem? active = null;
        for (int i = 0; i < PdfBookmarks.Count; i++)
        {
            if (PdfBookmarks[i].PageIndex <= pageIndex)
                active = PdfBookmarks[i];
            else
                break;
        }

        if (!ReferenceEquals(SelectedPdfBookmark, active))
        {
            _suppressPdfBookmarkSelectionChanged = true;
            SelectedPdfBookmark = active;
            _suppressPdfBookmarkSelectionChanged = false;
        }
    }

    public void UpdatePageIndexFromScroll(int visiblePageIndex)
    {
        if (Pages.Count == 0) return;
        int clamped = Math.Clamp(visiblePageIndex, 0, Pages.Count - 1);
        if (clamped == CurrentPageIndex) return;

        _suppressScrollSync = true;
        try
        {
            CurrentPageIndex = clamped;
        }
        finally
        {
            _suppressScrollSync = false;
        }
    }

    private async Task LoadChapterInternalAsync(LibraryChapterItem chapter)
    {
        _loadCts?.Cancel();
        _prefetchCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        IsLoadingChapter = true;
        ChapterTitle = chapter.Title;
        DisposeLoadedBitmaps();
        Pages.Clear();
        TotalPages = 0;

        _suppressPdfBookmarkSelectionChanged = true;
        PdfBookmarks.Clear();
        SelectedPdfBookmark = null;
        _suppressPdfBookmarkSelectionChanged = false;
        OnPropertyChanged(nameof(HasPdfBookmarks));

        try
        {
            var loadedPages = await _readerService.LoadChapterPagesAsync(chapter, ct);
            if (ct.IsCancellationRequested) return;

            foreach (var p in loadedPages)
            {
                Pages.Add(p);
            }

            if (chapter.PdfBookmarks != null && chapter.PdfBookmarks.Count > 0)
            {
                _suppressPdfBookmarkSelectionChanged = true;
                foreach (var bm in chapter.PdfBookmarks)
                {
                    PdfBookmarks.Add(bm);
                }
                _suppressPdfBookmarkSelectionChanged = false;
            }
            OnPropertyChanged(nameof(HasPdfBookmarks));

            TotalPages = Pages.Count;
            chapter.TotalPages = TotalPages;

            int savedPageIndex = _progressStore.GetLastPageIndex(chapter.LocalPath, TotalPages);
            // Nếu chương đã đọc tới trang cuối trước đó và người dùng mở lại, giữ ở trang đã lưu hoặc trang đầu
            int startIndex = Math.Clamp(savedPageIndex, 0, Math.Max(0, TotalPages - 1));

            _suppressScrollSync = true;
            CurrentPageIndex = startIndex;
            _suppressScrollSync = false;

            UpdateCurrentFlipPages();
            SyncActivePdfBookmark(startIndex);
            RecordReadingProgress(startIndex);

            // Nạp ngay trang mục tiêu trước khi cuộn tới
            if (TotalPages > 0)
            {
                await _readerService.EnsurePageLoadedAsync(Pages[startIndex], ct);
                if (IsDoublePage && startIndex + 1 < TotalPages)
                {
                    await _readerService.EnsurePageLoadedAsync(Pages[startIndex + 1], ct);
                }
            }

            TriggerPrefetch(startIndex);

            if (startIndex > 0)
            {
                string bookmarkHint = SelectedPdfBookmark != null ? $" ({SelectedPdfBookmark.Title})" : string.Empty;
                ShowTransientToast($"📖 Đã khôi phục vị trí đọc: Trang {startIndex + 1}/{TotalPages}{bookmarkHint}");
                Dispatcher.UIThread.Post(() => ScrollToPageRequested?.Invoke(startIndex), DispatcherPriority.Background);
            }
            else
            {
                Dispatcher.UIThread.Post(() => ScrollToPageRequested?.Invoke(0), DispatcherPriority.Background);
            }
        }
        finally
        {
            IsLoadingChapter = false;
            OnPropertyChanged(nameof(CanGoPrevChapter));
            OnPropertyChanged(nameof(CanGoNextChapter));
            OnPropertyChanged(nameof(CanGoPrevPage));
            OnPropertyChanged(nameof(CanGoNextPage));
        }
    }

    private void UpdateCurrentFlipPages()
    {
        if (Pages.Count == 0)
        {
            CurrentPage = null;
            SecondaryPage = null;
            OnPropertyChanged(nameof(FlipGridColumns));
            return;
        }

        int idx = Math.Clamp(CurrentPageIndex, 0, Pages.Count - 1);
        CurrentPage = Pages[idx];

        if (IsDoublePage && idx + 1 < Pages.Count)
        {
            SecondaryPage = Pages[idx + 1];
        }
        else
        {
            SecondaryPage = null;
        }

        OnPropertyChanged(nameof(FlipGridColumns));
        OnPropertyChanged(nameof(LeftFlipPage));
        OnPropertyChanged(nameof(RightFlipPage));
    }

    private void RecordReadingProgress(int pageIndex)
    {
        if (CurrentChapter == null || TotalPages <= 0) return;

        int page1Based = Math.Clamp(pageIndex + 1, 1, TotalPages);
        CurrentChapter.TotalPages = TotalPages;
        CurrentChapter.LastReadPage = page1Based;
        ParentComic?.RefreshReadStatus();

        _progressStore.SaveProgress(CurrentChapter.LocalPath, pageIndex);
    }

    private void TriggerPrefetch(int centerIndex)
    {
        _prefetchCts?.Cancel();
        _prefetchCts = new CancellationTokenSource();
        var ct = _prefetchCts.Token;
        var snapshot = Pages.ToList();
        bool scrollMode = IsScrollMode;

        _ = Task.Run(async () =>
        {
            try
            {
                await _readerService.UpdatePrefetchWindowAsync(snapshot, centerIndex, scrollMode, ct);
            }
            catch { }
        }, ct);
    }

    public async Task EnsureSinglePageLoadedAsync(ReaderPageItem? item)
    {
        if (item == null || item.IsLoaded) return;
        var ct = _loadCts?.Token ?? CancellationToken.None;
        await _readerService.EnsurePageLoadedAsync(item, ct);
    }

    [RelayCommand]
    public void ToggleOverlay()
    {
        IsOverlayVisible = !IsOverlayVisible;
    }

    [RelayCommand]
    public void ToggleMode()
    {
        IsScrollMode = !IsScrollMode;
        UpdateCurrentFlipPages();
        TriggerPrefetch(CurrentPageIndex);

        if (IsScrollMode)
        {
            ShowTransientToast("📜 Đã chuyển sang chế độ Cuộn dọc (Webtoon)");
            Dispatcher.UIThread.Post(() => ScrollToPageRequested?.Invoke(CurrentPageIndex), DispatcherPriority.Background);
        }
        else
        {
            ShowTransientToast("📄 Đã chuyển sang chế độ Lật trang (Manga)");
        }
    }

    [RelayCommand]
    public void ToggleRtl()
    {
        IsRightToLeft = !IsRightToLeft;
        UpdateCurrentFlipPages();
        ShowTransientToast(IsRightToLeft
            ? "◀ Đã bật chế độ đọc Manga (Phải → Trái)"
            : "▶ Đã bật chế độ đọc chuẩn (Trái → Phải)");
    }

    [RelayCommand]
    public void ToggleDoublePage()
    {
        IsDoublePage = !IsDoublePage;
        UpdateCurrentFlipPages();
        TriggerPrefetch(CurrentPageIndex);
        ShowTransientToast(IsDoublePage
            ? "📖 Đã bật chế độ ghép 2 trang song song"
            : "📄 Đã chuyển về chế độ 1 trang đơn");
    }

    [RelayCommand]
    public void NextPage()
    {
        if (Pages.Count == 0) return;
        int step = (IsFlipMode && IsDoublePage) ? 2 : 1;
        if (CurrentPageIndex + step < Pages.Count)
        {
            CurrentPageIndex += step;
        }
        else if (CurrentPageIndex < Pages.Count - 1)
        {
            CurrentPageIndex = Pages.Count - 1;
        }
        else if (CanGoNextChapter)
        {
            NextChapter();
        }
    }

    [RelayCommand]
    public void PrevPage()
    {
        if (Pages.Count == 0) return;
        int step = (IsFlipMode && IsDoublePage) ? 2 : 1;
        if (CurrentPageIndex - step >= 0)
        {
            CurrentPageIndex -= step;
        }
        else if (CurrentPageIndex > 0)
        {
            CurrentPageIndex = 0;
        }
        else if (CanGoPrevChapter)
        {
            PrevChapter();
        }
    }

    /// <summary>
    /// Xử lý chạm/nhấn phía bên TRÁI màn hình ở chế độ Lật trang (tự động đảo chiều nếu bật RTL).
    /// </summary>
    [RelayCommand]
    public void TapLeftZone()
    {
        if (IsScrollMode)
        {
            ToggleOverlay();
            return;
        }

        if (IsRightToLeft)
            NextPage();
        else
            PrevPage();
    }

    /// <summary>
    /// Xử lý chạm/nhấn phía bên PHẢI màn hình ở chế độ Lật trang (tự động đảo chiều nếu bật RTL).
    /// </summary>
    [RelayCommand]
    public void TapRightZone()
    {
        if (IsScrollMode)
        {
            ToggleOverlay();
            return;
        }

        if (IsRightToLeft)
            PrevPage();
        else
            NextPage();
    }

    [RelayCommand]
    public void FirstPage()
    {
        if (Pages.Count > 0)
        {
            CurrentPageIndex = 0;
        }
    }

    [RelayCommand]
    public void LastPage()
    {
        if (Pages.Count > 0)
        {
            CurrentPageIndex = Pages.Count - 1;
        }
    }

    [RelayCommand]
    public void NextChapter()
    {
        if (CurrentChapter == null) return;
        int idx = ChapterList.IndexOf(CurrentChapter);
        if (idx >= 0 && idx < ChapterList.Count - 1)
        {
            CurrentChapter = ChapterList[idx + 1];
            ShowTransientToast($"⏭ Chuyển sang: {CurrentChapter.Title}");
        }
    }

    [RelayCommand]
    public void PrevChapter()
    {
        if (CurrentChapter == null) return;
        int idx = ChapterList.IndexOf(CurrentChapter);
        if (idx > 0)
        {
            CurrentChapter = ChapterList[idx - 1];
            ShowTransientToast($"⏮ Quay lại: {CurrentChapter.Title}");
        }
    }

    [RelayCommand]
    public void ZoomIn()
    {
        ZoomLevel = Math.Clamp(Math.Round(ZoomLevel + 0.15, 2), 0.4, 3.0);
    }

    [RelayCommand]
    public void ZoomOut()
    {
        ZoomLevel = Math.Clamp(Math.Round(ZoomLevel - 0.15, 2), 0.4, 3.0);
    }

    [RelayCommand]
    public void ResetZoom()
    {
        ZoomLevel = 1.0;
    }

    [RelayCommand]
    public void ToggleFullScreen()
    {
        IsFullScreen = !IsFullScreen;
        FullScreenChangedRequested?.Invoke(IsFullScreen);
    }

    [RelayCommand]
    public void CloseReader()
    {
        Cleanup();
        CloseRequested?.Invoke();
    }

    public void Cleanup()
    {
        try
        {
            _loadCts?.Cancel();
            _prefetchCts?.Cancel();
            _progressStore.FlushImmediately();
            DisposeLoadedBitmaps();
        }
        catch { }
    }

    private void DisposeLoadedBitmaps()
    {
        foreach (var p in Pages)
        {
            if (p.Image != null)
            {
                var img = p.Image;
                p.Image = null;
                p.IsLoaded = false;
                try { img.Dispose(); } catch { }
            }
        }
    }

    private CancellationTokenSource? _toastCts;

    public void ShowTransientToast(string message, int durationMs = 1800)
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;

        StatusToastText = message;
        IsStatusToastVisible = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(durationMs, token);
                if (!token.IsCancellationRequested)
                {
                    Dispatcher.UIThread.Post(() => IsStatusToastVisible = false);
                }
            }
            catch { }
        }, token);
    }
}

