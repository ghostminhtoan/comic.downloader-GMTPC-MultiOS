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
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.Services;
using ComicDownloaderGMTPC.Views;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class ComicReaderViewModel : ViewModelBase
{
    private readonly ComicReaderService _readerService = ComicReaderService.Instance;
    private readonly ReadProgressStore _progressStore = ReadProgressStore.Instance;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _prefetchCts;
    private CancellationTokenSource? _effectRefreshCts;
    private bool _suppressChapterSelectionChanged;
    private bool _suppressPdfBookmarkSelectionChanged;
    private bool _suppressScrollSync;
    private bool _suppressBrightnessAutoUncheck;
    private double _viewportWidth = 1080;
    private double _viewportHeight = 1920;

    public ComicReaderViewModel()
    {
        LoadShortcuts();
    }

    public LibraryComicItem? ParentComic { get; private set; }

    public ObservableCollection<LibraryChapterItem> ChapterList { get; } = new();

    public ObservableCollection<PdfBookmarkItem> PdfBookmarks { get; } = new();

    public BulkObservableCollection<ReaderPageItem> Pages { get; } = new();

    public ObservableCollection<ReaderBookmarkItem> Bookmarks { get; } = new();

    public bool HasBookmarks => Bookmarks.Count > 0;

    [ObservableProperty]
    private PdfBookmarkItem? _selectedPdfBookmark;

    public bool HasPdfBookmarks => PdfBookmarks.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddBookmarkLeftInfoText))]
    private string _comicTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderSubTitleText))]
    [NotifyPropertyChangedFor(nameof(AddBookmarkLeftInfoText))]
    private string _chapterTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoPrevChapter))]
    [NotifyPropertyChangedFor(nameof(CanGoNextChapter))]
    [NotifyPropertyChangedFor(nameof(ShowBoundaryNextChapterButton))]
    [NotifyPropertyChangedFor(nameof(ShowBoundaryPrevChapterButton))]
    private LibraryChapterItem? _currentChapter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageDisplayNumber))]
    [NotifyPropertyChangedFor(nameof(PageCounterText))]
    [NotifyPropertyChangedFor(nameof(ComicScreenPageBadgeText))]
    [NotifyPropertyChangedFor(nameof(HeaderSubTitleText))]
    [NotifyPropertyChangedFor(nameof(AddBookmarkLeftInfoText))]
    [NotifyPropertyChangedFor(nameof(AddBookmarkRightInfoText))]
    [NotifyPropertyChangedFor(nameof(ProgressPercentText))]
    [NotifyPropertyChangedFor(nameof(CanGoPrevPage))]
    [NotifyPropertyChangedFor(nameof(CanGoNextPage))]
    [NotifyPropertyChangedFor(nameof(ShowBoundaryNextChapterButton))]
    [NotifyPropertyChangedFor(nameof(ShowBoundaryPrevChapterButton))]
    private int _currentPageIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageCounterText))]
    [NotifyPropertyChangedFor(nameof(ComicScreenPageBadgeText))]
    [NotifyPropertyChangedFor(nameof(AddBookmarkRightInfoText))]
    [NotifyPropertyChangedFor(nameof(MaxPageIndex))]
    [NotifyPropertyChangedFor(nameof(ShowBoundaryNextChapterButton))]
    [NotifyPropertyChangedFor(nameof(ShowBoundaryPrevChapterButton))]
    private int _totalPages;

    public int MaxPageIndex => Math.Max(0, TotalPages - 1);

    public int CurrentPageDisplayNumber => TotalPages == 0 ? 0 : CurrentPageIndex + 1;

    public string PageCounterText => TotalPages == 0 ? "0 / 0" : $"Trang {CurrentPageIndex + 1} / {TotalPages}";

    /// <summary>
    /// Badge hiển thị số trang kiểu ComicScreen ngay phía trên thanh trượt (VD: "12-13 / 13" khi ghép 2 trang, hoặc "12 / 13" khi 1 trang).
    /// </summary>
    public string ComicScreenPageBadgeText
    {
        get
        {
            if (TotalPages <= 0) return "0 / 0";
            int p1 = CurrentPageIndex + 1;
            if (IsFlipMode && IsDoublePage && SecondaryPage != null && p1 + 1 <= TotalPages)
            {
                return $"{p1}-{p1 + 1} / {TotalPages}";
            }
            return $"{p1} / {TotalPages}";
        }
    }

    /// <summary>
    /// Dòng tiêu đề phụ trên thanh Top Bar kiểu ComicScreen (hiển thị tên chương + tên file trang hiện tại như "012.jpg").
    /// </summary>
    public string HeaderSubTitleText
    {
        get
        {
            string pageName = CurrentPage?.Title ?? string.Empty;
            if (string.IsNullOrWhiteSpace(ChapterTitle)) return pageName;
            if (string.IsNullOrWhiteSpace(pageName) || string.Equals(ChapterTitle, pageName, StringComparison.OrdinalIgnoreCase))
            {
                return ChapterTitle;
            }
            return $"{ChapterTitle}  •  {pageName}";
        }
    }

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

    public bool ShowBoundaryNextChapterButton =>
        !IsContinuousChaptersMode && CanGoNextChapter && TotalPages > 0 &&
        (CurrentPageIndex >= TotalPages - 1 || IsScrollAtEnd);

    public bool ShowBoundaryPrevChapterButton =>
        !IsContinuousChaptersMode && CanGoPrevChapter && TotalPages > 0 &&
        (CurrentPageIndex <= 0 || IsScrollAtStart);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBoundaryNextChapterButton))]
    [NotifyPropertyChangedFor(nameof(ShowBoundaryPrevChapterButton))]
    private bool _isScrollAtEnd;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBoundaryNextChapterButton))]
    [NotifyPropertyChangedFor(nameof(ShowBoundaryPrevChapterButton))]
    private bool _isScrollAtStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftFlipPage))]
    [NotifyPropertyChangedFor(nameof(RightFlipPage))]
    [NotifyPropertyChangedFor(nameof(HeaderSubTitleText))]
    [NotifyPropertyChangedFor(nameof(ComicScreenPageBadgeText))]
    private ReaderPageItem? _currentPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftFlipPage))]
    [NotifyPropertyChangedFor(nameof(RightFlipPage))]
    [NotifyPropertyChangedFor(nameof(HasSecondaryPage))]
    [NotifyPropertyChangedFor(nameof(ComicScreenPageBadgeText))]
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

    // =========================================================================
    // THANH CÔNG CỤ 11 NÚT KIỂU COMICSCREEN (SWIPE GIỮA 1-5, 6-10, VÀ NÚT 11)
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToolbarPageOne))]
    [NotifyPropertyChangedFor(nameof(IsToolbarPageTwo))]
    [NotifyPropertyChangedFor(nameof(IsToolbarPageThree))]
    [NotifyPropertyChangedFor(nameof(ToolbarPageIndicatorText))]
    [NotifyPropertyChangedFor(nameof(ToolbarArrowIconText))]
    private int _toolbarPageIndex = 0; // 0: "1-5", 1: "6-10", 2: "11"

    public bool IsToolbarPageOne => ToolbarPageIndex == 0;
    public bool IsToolbarPageTwo => ToolbarPageIndex == 1;
    public bool IsToolbarPageThree => ToolbarPageIndex == 2;

    public string ToolbarPageIndicatorText => ToolbarPageIndex switch
    {
        0 => "1-5",
        1 => "6-10",
        _ => "11"
    };

    public string ToolbarArrowIconText => ToolbarPageIndex switch
    {
        0 => "❯",
        1 => "❯",
        _ => "❮"
    };

    [RelayCommand]
    public void ToggleToolbarPage()
    {
        SetToolbarPageIndex((ToolbarPageIndex + 1) % 3);
    }

    public void NextToolbarPage()
    {
        SetToolbarPageIndex((ToolbarPageIndex + 1) % 3);
    }

    public void PrevToolbarPage()
    {
        SetToolbarPageIndex((ToolbarPageIndex + 2) % 3);
    }

    public void SetToolbarPageIndex(int index)
    {
        int clamped = (index % 3 + 3) % 3;
        if (ToolbarPageIndex == clamped) return;
        ToolbarPageIndex = clamped;
        if (!IsMarginEditorOpen)
        {
            ActivePopup = string.Empty;
        }
    }

    public void SetToolbarPage(bool pageTwo)
    {
        SetToolbarPageIndex(pageTwo ? 1 : 0);
    }

    // =========================================================================
    // QUẢN LÝ POPUP ĐANG MỞ (CHỈ MỞ 1 POPUP TẠI 1 THỜI ĐIỂM)
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivePopup))]
    [NotifyPropertyChangedFor(nameof(IsPageScrollingPopupOpen))]
    [NotifyPropertyChangedFor(nameof(IsPageLayoutPopupOpen))]
    [NotifyPropertyChangedFor(nameof(IsMarginEditorOpen))]
    [NotifyPropertyChangedFor(nameof(IsBrightnessPopupOpen))]
    [NotifyPropertyChangedFor(nameof(IsZoomPopupOpen))]
    [NotifyPropertyChangedFor(nameof(IsFilterPopupOpen))]
    [NotifyPropertyChangedFor(nameof(IsPageSpacingPopupOpen))]
    [NotifyPropertyChangedFor(nameof(IsAddBookmarkPopupOpen))]
    [NotifyPropertyChangedFor(nameof(IsBookmarkListPopupOpen))]
    [NotifyPropertyChangedFor(nameof(IsChapterDrawerOpen))]
    [NotifyPropertyChangedFor(nameof(IsMoreMenuOpen))]
    [NotifyPropertyChangedFor(nameof(IsPageButtonAreaPopupOpen))]
    [NotifyPropertyChangedFor(nameof(IsDoubleTapZoomPopupOpen))]
    [NotifyPropertyChangedFor(nameof(IsSettingsPopupOpen))]
    [NotifyPropertyChangedFor(nameof(IsStandardOverlayBarsVisible))]
    private string _activePopup = string.Empty;

    public bool HasActivePopup => !string.IsNullOrEmpty(ActivePopup);
    public bool IsPageScrollingPopupOpen => ActivePopup == "PageScrolling";
    public bool IsPageLayoutPopupOpen => ActivePopup == "PageLayout";
    public bool IsMarginEditorOpen => ActivePopup == "MarginCrop";
    public bool IsBrightnessPopupOpen => ActivePopup == "Brightness";
    public bool IsZoomPopupOpen => ActivePopup == "ZoomControl";
    public bool IsFilterPopupOpen => ActivePopup == "Filter";
    public bool IsPageSpacingPopupOpen => ActivePopup == "PageSpacing";
    public bool IsAddBookmarkPopupOpen => ActivePopup == "AddBookmark";
    public bool IsBookmarkListPopupOpen => ActivePopup == "BookmarkList";
    public bool IsChapterDrawerOpen => ActivePopup == "ChapterDrawer";
    public bool IsMoreMenuOpen => ActivePopup == "MoreMenu";
    public bool IsPageButtonAreaPopupOpen => ActivePopup == "PageButtonArea";
    public bool IsDoubleTapZoomPopupOpen => ActivePopup == "DoubleTapZoom";
    public bool IsSettingsPopupOpen => ActivePopup == "Settings";

    public bool IsStandardOverlayBarsVisible => IsOverlayVisible && !IsMarginEditorOpen;

    private void TogglePopup(string popupName)
    {
        ActivePopup = (ActivePopup == popupName) ? string.Empty : popupName;
    }

    [RelayCommand]
    public void CloseAllPopups()
    {
        ActivePopup = string.Empty;
    }

    [RelayCommand]
    public void ClosePopup()
    {
        CloseAllPopups();
    }

    [RelayCommand]
    public void ToggleChapterDrawer() => TogglePopup("ChapterDrawer");

    [RelayCommand]
    public void ToggleMoreMenu() => TogglePopup("MoreMenu");

    /// <summary>
    /// Xử lý nút Back trên Android / phím Esc trên Desktop theo thứ tự ưu tiên:
    /// 1. Đóng Margin Crop Editor (nếu đang mở)
    /// 2. Đóng Popup chức năng đang mở (Button 1..10)
    /// 3. Ẩn thanh Overlay
    /// </summary>
    public bool HandleBackNavigation()
    {
        if (IsListeningForShortcutKey)
        {
            CancelAssignKey();
            return true;
        }

        if (IsMarginEditorOpen)
        {
            CancelMarginEditor();
            return true;
        }

        if (IsSettingsPopupOpen)
        {
            CloseSettingsPopup();
            return true;
        }

        if (HasActivePopup)
        {
            ActivePopup = string.Empty;
            return true;
        }

        if (IsOverlayVisible)
        {
            IsOverlayVisible = false;
            return true;
        }

        return false;
    }

    // =========================================================================
    // BUTTON 1: PAGE SCROLLING (6 CHẾ ĐỘ CUỘN / LẬT TRANG NHƯ COMICSCREEN)
    // 0 = Right (Flip LTR), 1 = Left (Flip RTL), 2 = Right+Vertical (Scroll RTL),
    // 3 = Right Slide (Slide LTR), 4 = Left Slide (Slide RTL), 5 = Left+Vertical (Scroll LTR)
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Button1IconBadgeText))]
    [NotifyPropertyChangedFor(nameof(ScrollMode0Border))]
    [NotifyPropertyChangedFor(nameof(ScrollMode1Border))]
    [NotifyPropertyChangedFor(nameof(ScrollMode2Border))]
    [NotifyPropertyChangedFor(nameof(ScrollMode3Border))]
    [NotifyPropertyChangedFor(nameof(ScrollMode4Border))]
    [NotifyPropertyChangedFor(nameof(ScrollMode5Border))]
    [NotifyPropertyChangedFor(nameof(ScrollMode0Bg))]
    [NotifyPropertyChangedFor(nameof(ScrollMode1Bg))]
    [NotifyPropertyChangedFor(nameof(ScrollMode2Bg))]
    [NotifyPropertyChangedFor(nameof(ScrollMode3Bg))]
    [NotifyPropertyChangedFor(nameof(ScrollMode4Bg))]
    [NotifyPropertyChangedFor(nameof(ScrollMode5Bg))]
    private int _pageScrollingMode = 5;

    public string Button1IconBadgeText => PageScrollingMode switch
    {
        0 => "→",
        1 => "←",
        2 => "R↓",
        3 => "S→",
        4 => "←S",
        _ => "↓L"
    };

    public string ScrollMode0Border => PageScrollingMode == 0 ? "#4FC3F7" : "#4B5563";
    public string ScrollMode1Border => PageScrollingMode == 1 ? "#4FC3F7" : "#4B5563";
    public string ScrollMode2Border => PageScrollingMode == 2 ? "#4FC3F7" : "#4B5563";
    public string ScrollMode3Border => PageScrollingMode == 3 ? "#4FC3F7" : "#4B5563";
    public string ScrollMode4Border => PageScrollingMode == 4 ? "#4FC3F7" : "#4B5563";
    public string ScrollMode5Border => PageScrollingMode == 5 ? "#4FC3F7" : "#4B5563";

    public string ScrollMode0Bg => PageScrollingMode == 0 ? "#1E3A5F" : "#2C323B";
    public string ScrollMode1Bg => PageScrollingMode == 1 ? "#1E3A5F" : "#2C323B";
    public string ScrollMode2Bg => PageScrollingMode == 2 ? "#1E3A5F" : "#2C323B";
    public string ScrollMode3Bg => PageScrollingMode == 3 ? "#1E3A5F" : "#2C323B";
    public string ScrollMode4Bg => PageScrollingMode == 4 ? "#1E3A5F" : "#2C323B";
    public string ScrollMode5Bg => PageScrollingMode == 5 ? "#1E3A5F" : "#2C323B";

    [RelayCommand]
    public void OpenPageScrollingPopup() => TogglePopup("PageScrolling");

    public FlowDirection ReaderFlowDirection =>
        (IsHorizontalScrollMode && IsRightToLeft) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    [RelayCommand]
    public void SelectPageScrollingMode(string? modeParam)
    {
        if (!int.TryParse(modeParam, out int mode)) return;
        int targetMode = Math.Clamp(mode, 0, 5);
        int preservedPageIndex = CurrentPageIndex;

        PageScrollingMode = targetMode;

        switch (PageScrollingMode)
        {
            case 0: // Right (Flip LTR)
                IsScrollMode = false;
                IsHorizontalScrollMode = false;
                IsRightToLeft = false;
                FitMode = ImageFitMode.FitScreen; // Khớp trọn vẹn màn hình theo yêu cầu
                ShowTransientToast("📄 Lật trang: trái (☞ﾟヮﾟ)☞ phải");
                break;
            case 1: // Left (Flip RTL)
                IsScrollMode = false;
                IsHorizontalScrollMode = false;
                IsRightToLeft = true;
                FitMode = ImageFitMode.FitScreen; // Khớp trọn vẹn màn hình theo yêu cầu
                ShowTransientToast("📖 Lật trang: phải ☜(ﾟヮﾟ☜) trái");
                break;
            case 2: // Right + Vertical (Cuộn dọc RTL)
                IsScrollMode = true;
                IsHorizontalScrollMode = false;
                IsRightToLeft = true;
                FitMode = ImageFitMode.FitScreen;
                ShowTransientToast("📜 Cuộn dọc RTL: phải ☜(ﾟヮﾟ☜) trái");
                break;
            case 3: // Right S (Horizontal Scroll LTR - Cuộn ngang liên tục Trái → Phải)
                IsScrollMode = true;
                IsHorizontalScrollMode = true;
                IsRightToLeft = false;
                FitMode = ImageFitMode.FitScreen; // Khớp trọn vẹn màn hình (Fit Screen) theo yêu cầu chuẩn đa nền tảng
                ShowTransientToast("↔ Cuộn ngang: trái (☞ﾟヮﾟ)☞ phải");
                break;
            case 4: // Left S (Horizontal Scroll RTL - Cuộn ngang liên tục Phải → Trái)
                IsScrollMode = true;
                IsHorizontalScrollMode = true;
                IsRightToLeft = true;
                FitMode = ImageFitMode.FitScreen; // Khớp trọn vẹn màn hình (Fit Screen) theo yêu cầu chuẩn đa nền tảng
                ShowTransientToast("↔ Cuộn ngang: phải ☜(ﾟヮﾟ☜) trái");
                break;
            default: // 5 = Left + Vertical (Webtoon Scroll LTR)
                IsScrollMode = true;
                IsHorizontalScrollMode = false;
                IsRightToLeft = false;
                FitMode = ImageFitMode.FitScreen; // Mặc định Fit Screen cho Webtoon theo yêu cầu
                ShowTransientToast("📜 Cuộn dọc: trái (☞ﾟヮﾟ)☞ phải (Webtoon)");
                break;
        }

        OnPropertyChanged(nameof(ReaderFlowDirection));
        ApplyFitModeLayout();

        if (Pages.Count > 0)
        {
            CurrentPageIndex = Math.Clamp(preservedPageIndex, 0, Pages.Count - 1);
        }

        UpdateCurrentFlipPages();
        TriggerPrefetch(CurrentPageIndex);

        if (CurrentPage != null && !CurrentPage.IsLoaded)
        {
            _ = EnsureSinglePageLoadedAsync(CurrentPage);
        }
        if (SecondaryPage != null && !SecondaryPage.IsLoaded)
        {
            _ = EnsureSinglePageLoadedAsync(SecondaryPage);
        }

        if (IsScrollMode)
        {
            Dispatcher.UIThread.Post(() => RequestScrollToPage(CurrentPageIndex), DispatcherPriority.Background);
        }
        ActivePopup = string.Empty;
    }

    // =========================================================================
    // BUTTON 2: ORIENTATION LOCK (0 = Auto, 1 = Landscape, 2 = Portrait)
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OrientationBadgeText))]
    [NotifyPropertyChangedFor(nameof(Button2IconTint))]
    private int _orientationLockMode = 0;

    public string OrientationBadgeText => OrientationLockMode switch
    {
        1 => "NGANG",
        2 => "DỌC",
        _ => "AUTO"
    };

    public string Button2IconTint => OrientationLockMode == 0 ? "#E2E8F0" : "#4FC3F7";

    [RelayCommand]
    public void CycleOrientationLock()
    {
        ActivePopup = string.Empty;
        OrientationLockMode = (OrientationLockMode + 1) % 3;
        ComicReaderService.AndroidSetOrientationMode?.Invoke(OrientationLockMode);

        string msg = OrientationLockMode switch
        {
            1 => "📲 Đã khóa xoay màn hình: Nằm ngang (Landscape)",
            2 => "📲 Đã khóa xoay màn hình: Thẳng đứng (Portrait)",
            _ => "📲 Xoay màn hình: Tự động theo cảm biến (Auto)"
        };
        ShowTransientToast(msg);
    }

    // =========================================================================
    // BUTTON 3: PAGE LAYOUT (0 = Off, 1 = Single page, 2 = Dual pages, 3 = Auto)
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Button3BadgeText))]
    [NotifyPropertyChangedFor(nameof(LayoutMode0Border))]
    [NotifyPropertyChangedFor(nameof(LayoutMode1Border))]
    [NotifyPropertyChangedFor(nameof(LayoutMode2Border))]
    [NotifyPropertyChangedFor(nameof(LayoutMode3Border))]
    [NotifyPropertyChangedFor(nameof(LayoutMode0Bg))]
    [NotifyPropertyChangedFor(nameof(LayoutMode1Bg))]
    [NotifyPropertyChangedFor(nameof(LayoutMode2Bg))]
    [NotifyPropertyChangedFor(nameof(LayoutMode3Bg))]
    private int _pageLayoutMode = 0;

    public string Button3BadgeText => PageLayoutMode switch
    {
        1 => "1P",
        2 => "2P",
        3 => "A",
        _ => "OFF"
    };

    public string LayoutMode0Border => PageLayoutMode == 0 ? "#4FC3F7" : "#4B5563";
    public string LayoutMode1Border => PageLayoutMode == 1 ? "#4FC3F7" : "#4B5563";
    public string LayoutMode2Border => PageLayoutMode == 2 ? "#4FC3F7" : "#4B5563";
    public string LayoutMode3Border => PageLayoutMode == 3 ? "#4FC3F7" : "#4B5563";

    public string LayoutMode0Bg => PageLayoutMode == 0 ? "#1E3A5F" : "#2C323B";
    public string LayoutMode1Bg => PageLayoutMode == 1 ? "#1E3A5F" : "#2C323B";
    public string LayoutMode2Bg => PageLayoutMode == 2 ? "#1E3A5F" : "#2C323B";
    public string LayoutMode3Bg => PageLayoutMode == 3 ? "#1E3A5F" : "#2C323B";

    [RelayCommand]
    public void OpenPageLayoutPopup() => TogglePopup("PageLayout");

    [RelayCommand]
    public void SelectPageLayoutMode(string? modeParam)
    {
        if (!int.TryParse(modeParam, out int mode)) return;
        PageLayoutMode = Math.Clamp(mode, 0, 3);

        switch (PageLayoutMode)
        {
            case 0: // Off
                IsDoublePage = false;
                ShowTransientToast("📄 Page layout: Off (1 trang mặc định)");
                break;
            case 1: // Single page
                IsDoublePage = false;
                ZoomLevel = 1.0;
                ShowTransientToast("📄 Page layout: Single page (1 trang chuẩn)");
                break;
            case 2: // Dual pages
                IsDoublePage = true;
                if (IsScrollMode)
                {
                    IsScrollMode = false;
                    PageScrollingMode = IsRightToLeft ? 1 : 0;
                }
                ShowTransientToast("📖 Page layout: Dual pages (Ghép 2 trang song song)");
                break;
            case 3: // Auto
                bool landscape = _viewportWidth > _viewportHeight * 1.05;
                IsDoublePage = landscape;
                if (landscape && IsScrollMode)
                {
                    IsScrollMode = false;
                    PageScrollingMode = IsRightToLeft ? 1 : 0;
                }
                ShowTransientToast($"📐 Page layout: Auto ({(landscape ? "2 trang Ngang" : "1 trang Dọc")})");
                break;
        }

        UpdateCurrentFlipPages();
        TriggerPrefetch(CurrentPageIndex);
        ActivePopup = string.Empty;
    }

    [RelayCommand]
    public void TogglePageLayout()
    {
        // Phím 2: Chuyển đổi tuần tự các chế độ bố cục: 0 (Off) -> 1 (Single) -> 2 (Dual) -> 3 (Auto) -> 0 (Off)
        int nextMode = (PageLayoutMode + 1) % 4;
        SelectPageLayoutMode(nextMode.ToString());
    }

    [RelayCommand]
    public void SetOriginalSizeImage()
    {
        // Phím 1: Hiển thị kích thước gốc 1:1 của ảnh (Original size)
        FitMode = ImageFitMode.Original;
        ZoomLevel = 1.0;
        ApplyFitModeLayout();
        ShowTransientToast("🔍 1:1 Kích thước gốc (Original size)");
    }

    // =========================================================================
    // CHẾ ĐỘ VỪA KHUNG HÌNH (IMAGE FIT MODE: FIT WIDTH / FIT HEIGHT / STRETCH FULL / FIT SCREEN / ORIGINAL)
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FitModeBadgeText))]
    [NotifyPropertyChangedFor(nameof(FitModeTooltipText))]
    private ImageFitMode _fitMode = ImageFitMode.FitScreen;

    public string FitModeBadgeText => FitMode switch
    {
        ImageFitMode.FitWidth => "FIT W",
        ImageFitMode.FitHeight => "FIT H",
        ImageFitMode.FitScreen => "FIT SCR",
        ImageFitMode.StretchFull => "STRETCH",
        _ => "1:1"
    };

    public string FitModeTooltipText => FitMode switch
    {
        ImageFitMode.FitWidth => "Khớp chiều ngang màn hình (Fit Width)",
        ImageFitMode.FitHeight => "Khớp chiều dọc màn hình (Fit Height)",
        ImageFitMode.FitScreen => "Khớp trọn vẹn màn hình (Fit Screen / Best Fit)",
        ImageFitMode.StretchFull => "Kéo giãn tràn toàn bộ màn hình (Stretch Full)",
        _ => "Kích thước gốc 1:1 (Original)"
    };

    partial void OnFitModeChanged(ImageFitMode value)
    {
        ApplyFitModeLayout();
    }

    [RelayCommand]
    public void CycleFitMode()
    {
        FitMode = FitMode switch
        {
            ImageFitMode.FitWidth => ImageFitMode.FitHeight,
            ImageFitMode.FitHeight => ImageFitMode.FitScreen,
            ImageFitMode.FitScreen => ImageFitMode.StretchFull,
            ImageFitMode.StretchFull => ImageFitMode.FitWidth,
            _ => ImageFitMode.FitWidth
        };
        ShowTransientToast($"📐 Chế độ khung hình: {FitModeTooltipText}");
    }


    [RelayCommand]
    public void SetFitMode(string? modeStr)
    {
        if (Enum.TryParse<ImageFitMode>(modeStr, true, out var parsed))
        {
            FitMode = parsed;
            ShowTransientToast($"📐 Chế độ khung hình: {FitModeTooltipText}");
        }
    }

    public Thickness FixedDividingPadding => new Thickness(0);

    public Avalonia.Layout.HorizontalAlignment LeftFlipPageAlignment =>
        (IsDoublePage && HasSecondaryPage) ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Center;

    public Avalonia.Layout.HorizontalAlignment RightFlipPageAlignment =>
        (IsDoublePage && HasSecondaryPage) ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Center;

    public double FlipDisplayWidth
    {
        get
        {
            double vw = Math.Max(240, _viewportWidth);
            double singlePageVw = (IsDoublePage && SecondaryPage != null) ? Math.Max(120, vw * 0.5) : vw;
            if (FitMode == ImageFitMode.StretchFull || FitMode == ImageFitMode.FitWidth || FitMode == ImageFitMode.FitScreen)
            {
                return Math.Round(singlePageVw * ZoomLevel);
            }
            return double.NaN;
        }
    }

    public double FlipDisplayHeight
    {
        get
        {
            double vh = Math.Max(240, _viewportHeight);
            if (FitMode == ImageFitMode.StretchFull || FitMode == ImageFitMode.FitHeight || FitMode == ImageFitMode.FitScreen)
            {
                return Math.Round(vh * ZoomLevel);
            }
            return double.NaN;
        }
    }

    public Avalonia.Media.Stretch FlipImageStretch =>
        FitMode == ImageFitMode.StretchFull ? Avalonia.Media.Stretch.Fill : Avalonia.Media.Stretch.Uniform;

    public void ApplyFitModeLayout()
    {
        double vw = Math.Max(240, _viewportWidth);
        double vh = Math.Max(240, _viewportHeight);

        double singlePageVw = (IsFlipMode && IsDoublePage) ? Math.Max(120, vw * 0.5) : vw;

        foreach (var page in Pages)
        {
            page.RecalculateDimensions(FitMode, singlePageVw, vh, ZoomLevel);
        }

        ApplyPageSpacingToAllItems();

        OnPropertyChanged(nameof(FlipDisplayWidth));
        OnPropertyChanged(nameof(FlipDisplayHeight));
        OnPropertyChanged(nameof(FlipImageStretch));
        OnPropertyChanged(nameof(FlipPageMargin));
        OnPropertyChanged(nameof(FixedDividingPadding));
        OnPropertyChanged(nameof(LeftFlipPageAlignment));
        OnPropertyChanged(nameof(RightFlipPageAlignment));
    }

    public void UpdateViewportDimensions(double width, double height)
    {
        if (width <= 10 || height <= 10) return;
        bool sizeChanged = Math.Abs(_viewportWidth - width) > 1 || Math.Abs(_viewportHeight - height) > 1;
        _viewportWidth = width;
        _viewportHeight = height;
        OnPropertyChanged(nameof(CropFrameMargin));

        if (PageLayoutMode == 3)
        {
            bool shouldBeDouble = width > height * 1.05;
            if (IsDoublePage != shouldBeDouble)
            {
                IsDoublePage = shouldBeDouble;
                UpdateCurrentFlipPages();
            }
        }

        if (sizeChanged)
        {
            int savedPageIndex = CurrentPageIndex;
            ApplyFitModeLayout();
            if (savedPageIndex >= 0 && savedPageIndex < Pages.Count && CurrentPageIndex != savedPageIndex)
            {
                CurrentPageIndex = savedPageIndex;
            }
        }
    }

    // =========================================================================
    // BUTTON 4: MARGIN / CROP EDITOR (KHUNG CẮT LỀ HỒNG #E91E63 NHƯ COMICSCREEN)
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CropFrameMargin))]
    [NotifyPropertyChangedFor(nameof(MarginStatusSummaryText))]
    private double _pendingCropLeft = 2.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CropFrameMargin))]
    [NotifyPropertyChangedFor(nameof(MarginStatusSummaryText))]
    private double _pendingCropTop = 2.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CropFrameMargin))]
    [NotifyPropertyChangedFor(nameof(MarginStatusSummaryText))]
    private double _pendingCropRight = 2.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CropFrameMargin))]
    [NotifyPropertyChangedFor(nameof(MarginStatusSummaryText))]
    private double _pendingCropBottom = 2.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveCrop))]
    [NotifyPropertyChangedFor(nameof(Button4IconTint))]
    private double _appliedCropLeft;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveCrop))]
    [NotifyPropertyChangedFor(nameof(Button4IconTint))]
    private double _appliedCropTop;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveCrop))]
    [NotifyPropertyChangedFor(nameof(Button4IconTint))]
    private double _appliedCropRight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveCrop))]
    [NotifyPropertyChangedFor(nameof(Button4IconTint))]
    private double _appliedCropBottom;

    public bool HasActiveCrop =>
        AppliedCropLeft > 0.1 || AppliedCropTop > 0.1 || AppliedCropRight > 0.1 || AppliedCropBottom > 0.1;

    public string Button4IconTint => HasActiveCrop ? "#4FC3F7" : "#E2E8F0";

    public Thickness CropFrameMargin
    {
        get
        {
            double w = Math.Max(320, _viewportWidth);
            double h = Math.Max(400, _viewportHeight - 68);
            double left = Math.Round(w * Math.Clamp(PendingCropLeft, 0.5, 30.0) / 100.0);
            double right = Math.Round(w * Math.Clamp(PendingCropRight, 0.5, 30.0) / 100.0);
            double top = Math.Round(h * Math.Clamp(PendingCropTop, 0.5, 30.0) / 100.0);
            double bottom = Math.Round(h * Math.Clamp(PendingCropBottom, 0.5, 30.0) / 100.0);
            return new Thickness(left, top, right, bottom);
        }
    }

    public string MarginStatusSummaryText =>
        $"Dọc (Trên/Dưới): {PendingCropTop:0}% / {PendingCropBottom:0}%   •   Ngang (Trái/Phải): {PendingCropLeft:0}% / {PendingCropRight:0}%";

    [RelayCommand]
    public void OpenMarginEditor()
    {
        if (IsMarginEditorOpen)
        {
            ActivePopup = string.Empty;
            return;
        }

        if (HasActiveCrop)
        {
            PendingCropLeft = AppliedCropLeft;
            PendingCropTop = AppliedCropTop;
            PendingCropRight = AppliedCropRight;
            PendingCropBottom = AppliedCropBottom;
        }
        else
        {
            PendingCropLeft = 3.0;
            PendingCropTop = 3.0;
            PendingCropRight = 3.0;
            PendingCropBottom = 3.0;
        }

        IsOverlayVisible = true;
        ActivePopup = "MarginCrop";
    }

    [RelayCommand]
    public void MarginAutoTrim()
    {
        byte[]? rawBytes = _readerService.GetPageRawBytes(CurrentPage);
        if (rawBytes != null && rawBytes.Length > 0)
        {
            var (l, t, r, b) = ComicReaderService.DetectSmartMargins(rawBytes);
            PendingCropLeft = l;
            PendingCropTop = t;
            PendingCropRight = r;
            PendingCropBottom = b;
            ShowTransientToast($"✨ Smart Auto Margin: Trên {t:0}% • Dưới {b:0}% • Trái {l:0}% • Phải {r:0}%");
        }
        else
        {
            if (Math.Abs(PendingCropTop - 5.0) < 0.5 && Math.Abs(PendingCropLeft - 4.0) < 0.5)
            {
                PendingCropTop = 8.0;
                PendingCropBottom = 8.0;
                PendingCropLeft = 6.0;
                PendingCropRight = 6.0;
            }
            else
            {
                PendingCropTop = 5.0;
                PendingCropBottom = 5.0;
                PendingCropLeft = 4.0;
                PendingCropRight = 4.0;
            }
            ShowTransientToast($"✨ Auto Margin: Dọc {PendingCropTop:0}% • Ngang {PendingCropLeft:0}%");
        }
    }

    [RelayCommand]
    public void MarginCycleVertical()
    {
        double next = PendingCropTop switch
        {
            < 3.0 => 4.0,
            < 6.0 => 8.0,
            < 10.0 => 12.0,
            < 15.0 => 18.0,
            _ => 0.0
        };
        PendingCropTop = next;
        PendingCropBottom = next;
        ShowTransientToast($"⇕ Lề Dọc (Vertical): {next:0}%");
    }

    [RelayCommand]
    public void MarginCycleHorizontal()
    {
        double next = PendingCropLeft switch
        {
            < 3.0 => 4.0,
            < 6.0 => 8.0,
            < 10.0 => 12.0,
            < 15.0 => 18.0,
            _ => 0.0
        };
        PendingCropLeft = next;
        PendingCropRight = next;
        ShowTransientToast($"⇔ Lề Ngang (Horizontal): {next:0}%");
    }

    [RelayCommand]
    public void MarginReset()
    {
        PendingCropLeft = 0.0;
        PendingCropTop = 0.0;
        PendingCropRight = 0.0;
        PendingCropBottom = 0.0;
        ShowTransientToast("↺ Đã đặt lại khung cắt lề về 0%");
    }

    [RelayCommand]
    public void MarginApply()
    {
        AppliedCropLeft = PendingCropLeft;
        AppliedCropTop = PendingCropTop;
        AppliedCropRight = PendingCropRight;
        AppliedCropBottom = PendingCropBottom;

        ActivePopup = string.Empty;
        IsOverlayVisible = false;
        SyncAndRefreshVisualEffects();

        if (HasActiveCrop)
        {
            ShowTransientToast($"✓ Đã áp dụng cắt lề: Dọc {AppliedCropTop:0}% • Ngang {AppliedCropLeft:0}%");
        }
        else
        {
            ShowTransientToast("✓ Đã tắt cắt lề trang truyện");
        }
    }

    public void CancelMarginEditor()
    {
        ActivePopup = string.Empty;
    }

    public void AdjustPendingCropByDrag(double deltaLeftPct, double deltaTopPct, double deltaRightPct, double deltaBottomPct)
    {
        PendingCropLeft = Math.Clamp(Math.Round(PendingCropLeft + deltaLeftPct, 1), 0.0, 28.0);
        PendingCropTop = Math.Clamp(Math.Round(PendingCropTop + deltaTopPct, 1), 0.0, 28.0);
        PendingCropRight = Math.Clamp(Math.Round(PendingCropRight + deltaRightPct, 1), 0.0, 28.0);
        PendingCropBottom = Math.Clamp(Math.Round(PendingCropBottom + deltaBottomPct, 1), 0.0, 28.0);
    }

    // =========================================================================
    // BUTTON 5: BRIGHTNESS, BACKGROUND, KEEP SCREEN, IMAGE BRIGHTNESS, SATURATION, BLUE LIGHT
    // =========================================================================

    [ObservableProperty]
    private bool _isAutoSystemBrightness = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SystemBrightnessText))]
    private double _systemBrightnessPercent = 35.0;

    public string SystemBrightnessText => $"{Math.Round(SystemBrightnessPercent):0}%";

    [ObservableProperty]
    private string _readerBackgroundColor = "#05080F";

    [ObservableProperty]
    private bool _isKeepScreenOn = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageBrightnessOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(ImageBrightnessOverlayColor))]
    [NotifyPropertyChangedFor(nameof(ImageBrightnessOverlayOpacity))]
    private bool _isImageBrightnessEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageBrightnessOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(ImageBrightnessOverlayColor))]
    [NotifyPropertyChangedFor(nameof(ImageBrightnessOverlayOpacity))]
    [NotifyPropertyChangedFor(nameof(ImageBrightnessText))]
    private double _imageBrightnessValue = 100.0;

    public string ImageBrightnessText => $"{Math.Round(ImageBrightnessValue):0}%";

    public bool IsImageBrightnessOverlayVisible =>
        IsImageBrightnessEnabled && Math.Abs(ImageBrightnessValue - 100.0) > 1.0;

    public string ImageBrightnessOverlayColor =>
        ImageBrightnessValue < 100.0 ? "#000000" : "#FFFFFF";

    public double ImageBrightnessOverlayOpacity
    {
        get
        {
            if (!IsImageBrightnessEnabled) return 0.0;
            if (ImageBrightnessValue < 100.0)
            {
                return Math.Clamp((100.0 - ImageBrightnessValue) / 105.0, 0.0, 0.88);
            }
            if (ImageBrightnessValue > 100.0)
            {
                return Math.Clamp((ImageBrightnessValue - 100.0) / 140.0, 0.0, 0.35);
            }
            return 0.0;
        }
    }

    [ObservableProperty]
    private bool _isSaturationEnabled = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaturationText))]
    private double _saturationValue = 100.0;

    public string SaturationText => $"{Math.Round(SaturationValue):0}%";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlueLightOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(BlueLightOverlayOpacity))]
    private bool _isBlueLightEnabled = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlueLightOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(BlueLightOverlayOpacity))]
    [NotifyPropertyChangedFor(nameof(BlueLightText))]
    private double _blueLightValue = 35.0;

    public string BlueLightText => $"{Math.Round(BlueLightValue):0}%";

    public bool IsBlueLightOverlayVisible => IsBlueLightEnabled && BlueLightValue > 1.0;

    public double BlueLightOverlayOpacity =>
        IsBlueLightEnabled ? Math.Clamp(BlueLightValue / 100.0 * 0.46, 0.0, 0.46) : 0.0;

    partial void OnIsAutoSystemBrightnessChanged(bool value)
    {
        ComicReaderService.AndroidApplyDisplaySettings?.Invoke(value, SystemBrightnessPercent, IsKeepScreenOn);
    }

    partial void OnSystemBrightnessPercentChanged(double value)
    {
        if (!_suppressBrightnessAutoUncheck && IsAutoSystemBrightness)
        {
            _suppressBrightnessAutoUncheck = true;
            IsAutoSystemBrightness = false;
            _suppressBrightnessAutoUncheck = false;
        }
        ComicReaderService.AndroidApplyDisplaySettings?.Invoke(IsAutoSystemBrightness, value, IsKeepScreenOn);
    }

    partial void OnIsKeepScreenOnChanged(bool value)
    {
        ComicReaderService.AndroidApplyDisplaySettings?.Invoke(IsAutoSystemBrightness, SystemBrightnessPercent, value);
    }

    partial void OnIsSaturationEnabledChanged(bool value)
    {
        SyncAndRefreshVisualEffects();
    }

    partial void OnSaturationValueChanged(double value)
    {
        if (IsSaturationEnabled)
        {
            SyncAndRefreshVisualEffects(debounced: true);
        }
    }

    [RelayCommand]
    public void OpenBrightnessPopup() => TogglePopup("Brightness");

    [RelayCommand]
    public void CycleBackgroundColor()
    {
        ReaderBackgroundColor = ReaderBackgroundColor switch
        {
            "#05080F" => "#1E293B",
            "#1E293B" => "#282015",
            "#282015" => "#E2E8F0",
            _ => "#05080F"
        };
        ShowTransientToast($"🎨 Đã đổi màu nền trình đọc: {ReaderBackgroundColor}");
    }

    [RelayCommand]
    public void ResetBrightnessDefaults()
    {
        _suppressBrightnessAutoUncheck = true;
        IsAutoSystemBrightness = true;
        SystemBrightnessPercent = 35.0;
        _suppressBrightnessAutoUncheck = false;

        ReaderBackgroundColor = "#05080F";
        IsKeepScreenOn = true;
        IsImageBrightnessEnabled = true;
        ImageBrightnessValue = 100.0;
        IsSaturationEnabled = false;
        SaturationValue = 100.0;
        IsBlueLightEnabled = false;
        BlueLightValue = 35.0;

        ComicReaderService.AndroidApplyDisplaySettings?.Invoke(IsAutoSystemBrightness, SystemBrightnessPercent, IsKeepScreenOn);
        SyncAndRefreshVisualEffects();
        ShowTransientToast("☀ Đã khôi phục cài đặt Độ sáng & Màu sắc mặc định");
    }

    // =========================================================================
    // BUTTON 6: PINCH ZOOM & SMART ZOOM CONTROLLER & AUTO-SCROLL HANDS-FREE
    // =========================================================================

    [RelayCommand]
    public void OpenZoomPopup() => TogglePopup("ZoomControl");

    [RelayCommand]
    public void SmartDoubleTapZoom()
    {
        ExecuteDoubleTapZoom(true);
    }

    [RelayCommand]
    public void SetZoomPreset(string? zoomStr)
    {
        if (double.TryParse(zoomStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double targetZoom))
        {
            ZoomLevel = Math.Clamp(targetZoom, 0.4, 4.0);
            ShowTransientToast($"🔍 Zoom: {ZoomPercentText}");
        }
    }

    // --- TỰ ĐỘNG CUỘN TRANG RẢNH TAY (AUTO-SCROLL HANDS-FREE) ---

    private DispatcherTimer? _autoScrollTimer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoScrollButtonText))]
    [NotifyPropertyChangedFor(nameof(AutoScrollButtonBg))]
    private bool _isAutoScrolling;

    [ObservableProperty]
    private double _autoScrollSpeed = 3.0;

    public string AutoScrollButtonText => IsAutoScrolling ? "⏸ Dừng Cuộn" : "▶ Tự Cuộn";
    public string AutoScrollButtonBg => IsAutoScrolling ? "#DC2626" : "#1E293B";

    public event Action<double>? AutoScrollStepRequested;

    [RelayCommand]
    public void ToggleAutoScroll()
    {
        if (IsAutoScrolling)
        {
            StopAutoScroll();
            ShowTransientToast("⏸ Đã dừng tự động cuộn trang");
        }
        else
        {
            if (!IsScrollMode)
            {
                IsScrollMode = true;
                PageScrollingMode = IsRightToLeft ? 2 : 5;
                UpdateCurrentFlipPages();
            }
            StartAutoScroll();
            ShowTransientToast("▶ Đã bật tự động cuộn trang rảnh tay (Hands-Free)");
        }
    }

    public void StartAutoScroll()
    {
        if (_autoScrollTimer == null)
        {
            _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _autoScrollTimer.Tick += (s, e) =>
            {
                if (!IsAutoScrolling || !IsScrollMode) return;
                AutoScrollStepRequested?.Invoke(AutoScrollSpeed);
            };
        }
        IsAutoScrolling = true;
        _autoScrollTimer.Start();
    }

    public void StopAutoScroll()
    {
        IsAutoScrolling = false;
        _autoScrollTimer?.Stop();
    }

    // --- XEM TRƯỚC HÌNH THU NHỎ KHI TRƯỢT SEEKBAR (FILMSTRIP THUMBNAIL SCRUBBER) ---

    [ObservableProperty]
    private ReaderPageItem? _scrubberPreviewPage;

    [ObservableProperty]
    private bool _isScrubberPreviewVisible;

    public void UpdateScrubberHover(int pageIndex, bool isVisible)
    {
        IsScrubberPreviewVisible = isVisible;
        if (!isVisible || Pages.Count == 0)
        {
            ScrubberPreviewPage = null;
            return;
        }

        int clamped = Math.Clamp(pageIndex, 0, Pages.Count - 1);
        var target = Pages[clamped];
        ScrubberPreviewPage = target;
        if (!target.IsLoaded)
        {
            _ = _readerService.EnsurePageLoadedAsync(target);
        }
    }

    // =========================================================================
    // BUTTON 7: FILTER (GRAY SCALE, AUTO CONTRAST, UPSCALING, SHARPEN, INVERT, BLUE LIGHT)
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Button7IconTint))]
    private bool _isGrayScale = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Button7IconTint))]
    private bool _isAutoContrast = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Button7IconTint))]
    private bool _isUpscaling = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Button7IconTint))]
    private bool _isSharpenFilter = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SharpenStrengthText))]
    private double _sharpenStrength = 50.0;

    public string SharpenStrengthText => $"{Math.Round(SharpenStrength):0}%";

    [RelayCommand]
    public void IncreaseSharpenStrength()
    {
        SharpenStrength = Math.Clamp(Math.Round(SharpenStrength) + 1.0, 0.0, 100.0);
    }

    [RelayCommand]
    public void DecreaseSharpenStrength()
    {
        SharpenStrength = Math.Clamp(Math.Round(SharpenStrength) - 1.0, 0.0, 100.0);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Button7IconTint))]
    private bool _isDenoiseFilter = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DenoiseStrengthText))]
    private double _denoiseStrength = 30.0;

    public string DenoiseStrengthText => $"{Math.Round(DenoiseStrength):0}%";

    [RelayCommand]
    public void IncreaseDenoiseStrength()
    {
        DenoiseStrength = Math.Clamp(Math.Round(DenoiseStrength) + 1.0, 0.0, 100.0);
    }

    [RelayCommand]
    public void DecreaseDenoiseStrength()
    {
        DenoiseStrength = Math.Clamp(Math.Round(DenoiseStrength) - 1.0, 0.0, 100.0);
    }

    [ObservableProperty]
    private bool _isImagePropertiesHudVisible = false;

    [ObservableProperty]
    private string _imagePropertiesDetailsText = string.Empty;

    [RelayCommand]
    public void ToggleImagePropertiesHud()
    {
        IsImagePropertiesHudVisible = !IsImagePropertiesHudVisible;
        if (IsImagePropertiesHudVisible)
        {
            UpdateImagePropertiesDetails();
        }
    }

    public void UpdateImagePropertiesDetails()
    {
        if (CurrentPageIndex >= 0 && CurrentPageIndex < Pages.Count)
        {
            var page = Pages[CurrentPageIndex];
            ImagePropertiesDetailsText = ComicReaderService.GetImagePropertiesSummary(page, ZoomLevel);
        }
        else
        {
            ImagePropertiesDetailsText = "Chưa có thông tin trang hiện tại.";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Button7IconTint))]
    private bool _isInvertColor = false;

    public string Button7IconTint =>
        (IsGrayScale || IsAutoContrast || IsSharpenFilter || IsDenoiseFilter || IsInvertColor || IsBlueLightEnabled)
            ? "#4FC3F7"
            : "#E2E8F0";

    [RelayCommand]
    public void OpenFilterPopup() => TogglePopup("Filter");

    partial void OnIsGrayScaleChanged(bool value) => SyncAndRefreshVisualEffects();
    partial void OnIsAutoContrastChanged(bool value) => SyncAndRefreshVisualEffects();
    partial void OnIsSharpenFilterChanged(bool value) => SyncAndRefreshVisualEffects();
    partial void OnSharpenStrengthChanged(double value) => SyncAndRefreshVisualEffects(debounced: true);
    partial void OnIsDenoiseFilterChanged(bool value) => SyncAndRefreshVisualEffects();
    partial void OnDenoiseStrengthChanged(double value) => SyncAndRefreshVisualEffects(debounced: true);
    partial void OnIsInvertColorChanged(bool value) => SyncAndRefreshVisualEffects();

    partial void OnIsUpscalingChanged(bool value)
    {
        ShowTransientToast(value
            ? "🪄 Đã bật làm nét ảnh độ phân giải cao (Upscaling)"
            : "🪄 Đã tắt Upscaling");
    }

    private void SyncAndRefreshVisualEffects(bool debounced = false)
    {
        OnPropertyChanged(nameof(Button7IconTint));

        var settings = _readerService.ActiveEffectSettings;
        settings.CropLeftPercent = AppliedCropLeft;
        settings.CropTopPercent = AppliedCropTop;
        settings.CropRightPercent = AppliedCropRight;
        settings.CropBottomPercent = AppliedCropBottom;
        settings.IsGrayScale = IsGrayScale;
        settings.IsAutoContrast = IsAutoContrast;
        settings.IsSharpenFilter = IsSharpenFilter;
        settings.SharpenStrength = SharpenStrength;
        settings.IsDenoiseFilter = IsDenoiseFilter;
        settings.DenoiseStrength = DenoiseStrength;
        settings.IsInvertColor = IsInvertColor;
        settings.IsSaturationEnabled = IsSaturationEnabled;
        settings.SaturationValue = SaturationValue;


        _effectRefreshCts?.Cancel();
        _effectRefreshCts = new CancellationTokenSource();
        var ct = _effectRefreshCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                if (debounced)
                {
                    await Task.Delay(180, ct);
                }
                if (ct.IsCancellationRequested) return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var p in Pages)
                    {
                        p.Unload();
                    }
                });

                if (Pages.Count > 0 && !ct.IsCancellationRequested)
                {
                    TriggerPrefetch(CurrentPageIndex);
                }
            }
            catch { }
        }, ct);
    }

    // =========================================================================
    // BUTTON 8: PAGE SPACING (KHOẢNG CÁCH GIỮA CÁC TRANG TRUYỆN)
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Button8IconTint))]
    [NotifyPropertyChangedFor(nameof(FlipPageMargin))]
    [NotifyPropertyChangedFor(nameof(FixedDividingPadding))]
    private bool _isPageSpacingEnabled = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageSpacingFormattedText))]
    [NotifyPropertyChangedFor(nameof(FlipPageMargin))]
    [NotifyPropertyChangedFor(nameof(FixedDividingPadding))]
    private double _pageSpacingPercent = 0.0;

    public string Button8IconTint => IsPageSpacingEnabled ? "#4FC3F7" : "#E2E8F0";

    public string PageSpacingFormattedText => $"{Math.Max(0.0, PageSpacingPercent):0.0} %";

    public Thickness FlipPageMargin
    {
        get
        {
            if (!IsPageSpacingEnabled || PageSpacingPercent <= 0.01) return new Thickness(0);
            double px = Math.Round(PageSpacingPercent * 0.5);
            return new Thickness(Math.Max(0, px), 0);
        }
    }

    [RelayCommand]
    public void OpenPageSpacingPopup() => TogglePopup("PageButtonArea");

    partial void OnIsPageSpacingEnabledChanged(bool value)
    {
        ApplyFitModeLayout();
        OnPropertyChanged(nameof(FixedDividingPadding));
    }

    partial void OnPageSpacingPercentChanged(double value)
    {
        if (value < 0.0)
        {
            PageSpacingPercent = 0.0;
            return;
        }
        if (!IsPageSpacingEnabled && value > 0.2)
        {
            IsPageSpacingEnabled = true;
            return;
        }
        ApplyFitModeLayout();
        OnPropertyChanged(nameof(FixedDividingPadding));
    }

    private Thickness CalculateCurrentPageItemMargin()
    {
        if (!IsPageSpacingEnabled) return new Thickness(0);
        if (IsHorizontalScrollMode)
        {
            double hPx = PageSpacingPercent > 0 ? Math.Round(PageSpacingPercent * 1.5) : 0;
            return new Thickness(hPx, 0, hPx, 0);
        }
        else
        {
            double vPx = PageSpacingPercent > 0 ? Math.Round(PageSpacingPercent * 0.5) : 0;
            return new Thickness(0, vPx, 0, vPx);
        }
    }

    private void ApplyPageSpacingToAllItems()
    {
        var margin = CalculateCurrentPageItemMargin();
        foreach (var page in Pages)
        {
            page.PageMargin = margin;
        }
    }

    // =========================================================================
    // BUTTON 11: PAGE BUTTON AREA (VÙNG NÚT CHUYỂN TRANG ẢO CHUẨN 100% COMICSCREEN ẢNH 3)
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Button11IconTint))]
    [NotifyPropertyChangedFor(nameof(PageButtonAreaQuickToggleIcon))]
    [NotifyPropertyChangedFor(nameof(PageButtonAreaQuickToggleText))]
    private bool _isPageButtonAreaEnabled = true;

    public string Button11IconTint => IsPageButtonAreaEnabled ? "#4FC3F7" : "#E2E8F0";
    public string PageButtonAreaQuickToggleIcon => IsPageButtonAreaEnabled ? "🟢" : "⚪";
    public string PageButtonAreaQuickToggleText => IsPageButtonAreaEnabled ? "BẬT" : "TẮT";

    [RelayCommand]
    public void TogglePageButtonAreaEnabled()
    {
        IsPageButtonAreaEnabled = !IsPageButtonAreaEnabled;
        ShowTransientToast(IsPageButtonAreaEnabled ? "🔘 Đã BẬT phím ảo chuyển trang (Page button area)" : "🔘 Đã TẮT phím ảo chuyển trang");
    }

    // =========================================================================
    // CHẾ ĐỘ ĐỌC LIỀN MẠCH / PHÂN CHƯƠNG (CONTINUOUS READING VS PER CHAPTER)
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContinuousModeBadgeText))]
    [NotifyPropertyChangedFor(nameof(ContinuousModeTooltipText))]
    [NotifyPropertyChangedFor(nameof(ShowBoundaryNextChapterButton))]
    [NotifyPropertyChangedFor(nameof(ShowBoundaryPrevChapterButton))]
    private bool _isContinuousChaptersMode = true;

    public string ContinuousModeBadgeText => IsContinuousChaptersMode ? "LIỀN MẠCH" : "THEO CHƯƠNG";

    public string ContinuousModeTooltipText => IsContinuousChaptersMode
        ? "Đọc liền mạch: Tự động chuyển chương khi đến cuối trang hoặc cuộn chạm mép"
        : "Phân chương: Dừng lại ở cuối chương, yêu cầu bấm nút để chuyển chương";

    [RelayCommand]
    public void ToggleContinuousChaptersMode()
    {
        IsContinuousChaptersMode = !IsContinuousChaptersMode;
        ShowTransientToast(IsContinuousChaptersMode
            ? "📖⚡ Đã BẬT Đọc liền mạch: Tự động chuyển chương khi hết trang"
            : "📑 Đã chuyển sang Phân chương: Dừng lại ở cuối chương, chuyển chương thủ công");
    }

    private bool _isNavigatingToPreviousChapter = false;

    [ObservableProperty]
    private bool _isPageButtonAreaDisablePrev = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageButtonAreaFirstSymbol))]
    [NotifyPropertyChangedFor(nameof(PageButtonAreaSecondSymbol))]
    private bool _isPageButtonAreaReverse = false;

    public string PageButtonAreaFirstSymbol => IsPageButtonAreaReverse ? "+" : "-";
    public string PageButtonAreaSecondSymbol => IsPageButtonAreaReverse ? "-" : "+";

    [ObservableProperty]
    private bool _isPageButtonAreaFixed = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageButtonAreaType0))]
    [NotifyPropertyChangedFor(nameof(IsPageButtonAreaType1))]
    [NotifyPropertyChangedFor(nameof(IsPageButtonAreaType2))]
    [NotifyPropertyChangedFor(nameof(IsPageButtonAreaType3))]
    private int _pageButtonAreaType = 0; // 0: Bottom Trái Phải, 1: Left Side Top Bot, 2: Horizontal Center Left Right, 3: Vertical Center Top Bot

    public bool IsPageButtonAreaType0
    {
        get => PageButtonAreaType == 0;
        set { if (value) PageButtonAreaType = 0; }
    }

    public bool IsPageButtonAreaType1
    {
        get => PageButtonAreaType == 1;
        set { if (value) PageButtonAreaType = 1; }
    }

    public bool IsPageButtonAreaType2
    {
        get => PageButtonAreaType == 2;
        set { if (value) PageButtonAreaType = 2; }
    }

    public bool IsPageButtonAreaType3
    {
        get => PageButtonAreaType == 3;
        set { if (value) PageButtonAreaType = 3; }
    }

    partial void OnPageButtonAreaTypeChanged(int value)
    {
        OnPropertyChanged(nameof(IsPageButtonAreaType0));
        OnPropertyChanged(nameof(IsPageButtonAreaType1));
        OnPropertyChanged(nameof(IsPageButtonAreaType2));
        OnPropertyChanged(nameof(IsPageButtonAreaType3));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageButtonAreaTop))]
    private bool _isPageButtonAreaBottom = true; // true = Bottom, false = Top

    public bool IsPageButtonAreaTop
    {
        get => !IsPageButtonAreaBottom;
        set { IsPageButtonAreaBottom = !value; }
    }

    partial void OnIsPageButtonAreaBottomChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPageButtonAreaTop));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageButtonAreaOpacityText))]
    [NotifyPropertyChangedFor(nameof(PageButtonAreaComputedOpacity))]
    private double _pageButtonAreaOpacity = 100.0; // 10..100% (Ảnh 3 là 100)

    public string PageButtonAreaOpacityText => $"{PageButtonAreaOpacity:0}";
    public double PageButtonAreaComputedOpacity => Math.Clamp(PageButtonAreaOpacity / 100.0, 0.1, 1.0);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageButtonAreaSizeText))]
    [NotifyPropertyChangedFor(nameof(PageButtonAreaComputedSize))]
    [NotifyPropertyChangedFor(nameof(PageButtonAreaFontSize))]
    [NotifyPropertyChangedFor(nameof(PageButtonAreaComputedWidth))]
    [NotifyPropertyChangedFor(nameof(PageButtonAreaComputedHeight))]
    [NotifyPropertyChangedFor(nameof(PageButtonAreaComputedHitHeight))]
    [NotifyPropertyChangedFor(nameof(PageButtonAreaComputedHitWidth))]
    [NotifyPropertyChangedFor(nameof(PageButtonAreaIconFontSize))]
    private double _pageButtonAreaSize = 26.0; // 10..100 (Ảnh 3 là 26)

    public string PageButtonAreaSizeText => $"{PageButtonAreaSize:0}";
    public double PageButtonAreaComputedSize => Math.Round(38.0 + (PageButtonAreaSize - 10.0) * 0.52);
    public double PageButtonAreaFontSize => Math.Round(PageButtonAreaComputedSize * 0.54);
    public double PageButtonAreaComputedWidth => PageButtonAreaComputedHitWidth;
    public double PageButtonAreaComputedHeight => PageButtonAreaComputedHitHeight;

    /// <summary>
    /// Chiều cao vùng cảm ứng bấm (Hitbox) cho Type 0 (Bottom) và Type 3 (Top/Bottom) chuẩn ComicScreen.
    /// </summary>
    public double PageButtonAreaComputedHitHeight => Math.Round(50.0 + (PageButtonAreaSize - 10.0) * 1.35);

    /// <summary>
    /// Chiều rộng vùng cảm ứng bấm (Hitbox) cho Type 1 (Left Side) và Type 2 (Left/Right Side) chuẩn ComicScreen.
    /// </summary>
    public double PageButtonAreaComputedHitWidth => Math.Round(55.0 + (PageButtonAreaSize - 10.0) * 1.35);

    /// <summary>
    /// Kích thước font icon dấu - và + nhỏ gọn, tinh tế, không viền vướng mắt.
    /// </summary>
    public double PageButtonAreaIconFontSize => Math.Round(16.0 + (PageButtonAreaSize - 10.0) * 0.12);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageButtonAreaScrollDistanceText))]
    private double _pageButtonAreaScrollDistance = 50.0; // 10..100 (Ảnh 3 là 50)

    public string PageButtonAreaScrollDistanceText => $"{PageButtonAreaScrollDistance:0}";

    [ObservableProperty]
    private bool _isPageButtonAreaScrollAnimation = true;

    public event Action<double, bool>? PageButtonAreaScrollRequested;

    [RelayCommand]
    public void OpenPageButtonAreaPopup() => TogglePopup("PageButtonArea");

    [RelayCommand]
    public void SelectPageButtonAreaType(string? typeParam)
    {
        if (int.TryParse(typeParam, out int t))
        {
            PageButtonAreaType = Math.Clamp(t, 0, 3);
        }
    }

    [RelayCommand]
    public void SetPageButtonAreaPosition(string? posParam)
    {
        IsPageButtonAreaBottom = !string.Equals(posParam, "Top", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    public void ResetPageButtonAreaDefault()
    {
        IsPageButtonAreaEnabled = true;
        IsPageButtonAreaDisablePrev = false;
        IsPageButtonAreaReverse = false;
        IsPageButtonAreaFixed = true;
        PageButtonAreaType = 0;
        IsPageButtonAreaBottom = true;
        PageButtonAreaOpacity = 100.0;
        PageButtonAreaSize = 26.0;
        PageButtonAreaScrollDistance = 50.0;
        IsPageButtonAreaScrollAnimation = true;
    }

    [RelayCommand]
    public void PageButtonAreaPrev()
    {
        if (IsPageButtonAreaReverse)
        {
            ExecutePageButtonAreaNextAction();
        }
        else
        {
            ExecutePageButtonAreaPrevAction();
        }
    }

    [RelayCommand]
    public void PageButtonAreaNext()
    {
        if (IsPageButtonAreaReverse)
        {
            ExecutePageButtonAreaPrevAction();
        }
        else
        {
            ExecutePageButtonAreaNextAction();
        }
    }

    private void ExecutePageButtonAreaPrevAction()
    {
        if (IsFlipMode)
        {
            PrevPage();
        }
        else
        {
            double fraction = -(PageButtonAreaScrollDistance / 100.0);
            PageButtonAreaScrollRequested?.Invoke(fraction, IsPageButtonAreaScrollAnimation);
        }
    }

    private void ExecutePageButtonAreaNextAction()
    {
        if (IsFlipMode)
        {
            NextPage();
        }
        else
        {
            double fraction = PageButtonAreaScrollDistance / 100.0;
            PageButtonAreaScrollRequested?.Invoke(fraction, IsPageButtonAreaScrollAnimation);
        }
    }

    // =========================================================================
    // DOUBLE TAP ZOOM (CHẾ ĐỘ CHẠM ĐÚP PHÓNG TO CHUẨN 100% COMICSCREEN ẢNH 4)
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDoubleTapZoomOff))]
    [NotifyPropertyChangedFor(nameof(IsDoubleTapZoomFitScreen))]
    [NotifyPropertyChangedFor(nameof(IsDoubleTapZoomZoom))]
    [NotifyPropertyChangedFor(nameof(IsDoubleTapZoomSmart))]
    private int _doubleTapZoomMode = 3; // 0: Off, 1: Fit Screen, 2: Zoom, 3: Zoom(inside) + Fit Screen(outside) (Ảnh 4 mặc định 3)

    public bool IsDoubleTapZoomOff => DoubleTapZoomMode == 0;
    public bool IsDoubleTapZoomFitScreen => DoubleTapZoomMode == 1;
    public bool IsDoubleTapZoomZoom => DoubleTapZoomMode == 2;
    public bool IsDoubleTapZoomSmart => DoubleTapZoomMode == 3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DoubleTapZoomScaleText))]
    private double _doubleTapZoomScale = 125.0; // 100..400% (Mặc định 125%)

    public string DoubleTapZoomScaleText => $"{DoubleTapZoomScale:0}%";

    [RelayCommand]
    public void OpenDoubleTapZoomPopup() => TogglePopup("DoubleTapZoom");

    [RelayCommand]
    public void SelectDoubleTapZoomMode(string? modeParam)
    {
        if (int.TryParse(modeParam, out int m))
        {
            DoubleTapZoomMode = Math.Clamp(m, 0, 3);
        }
    }

    [RelayCommand]
    public void ResetDoubleTapZoomDefault()
    {
        DoubleTapZoomMode = 3;
        DoubleTapZoomScale = 125.0;
    }

    public void ExecuteDoubleTapZoom(bool isInsideImage)
    {
        switch (DoubleTapZoomMode)
        {
            case 0: // Off
                return;
            case 1: // Fit Screen
                FitMode = ImageFitMode.FitScreen;
                ZoomLevel = 1.0;
                ApplyFitModeLayout();
                ShowTransientToast("⛶ Double-Tap: Fit Screen");
                break;
            case 2: // Zoom
                if (ZoomLevel > 1.05)
                {
                    ZoomLevel = 1.0;
                    ShowTransientToast("🔍 Double-Tap: 100%");
                }
                else
                {
                    ZoomLevel = Math.Clamp(DoubleTapZoomScale / 100.0, 1.0, 4.0);
                    ShowTransientToast($"🔍 Double-Tap: {DoubleTapZoomScale:0}%");
                }
                ApplyFitModeLayout();
                break;
            case 3: // Zoom(inside) + Fit Screen(outside)
            default:
                if (isInsideImage)
                {
                    if (ZoomLevel > 1.05)
                    {
                        ZoomLevel = 1.0;
                        ShowTransientToast("🔍 Double-Tap (trong ảnh): 100%");
                    }
                    else
                    {
                        ZoomLevel = Math.Clamp(DoubleTapZoomScale / 100.0, 1.0, 4.0);
                        ShowTransientToast($"🔍 Double-Tap (trong ảnh): {DoubleTapZoomScale:0}%");
                    }
                }
                else
                {
                    FitMode = ImageFitMode.FitScreen;
                    ZoomLevel = 1.0;
                    ShowTransientToast("⛶ Double-Tap (ngoài lề): Fit Screen");
                }
                ApplyFitModeLayout();
                break;
        }
    }

    // =========================================================================
    // BUTTON 9 & 10: ADD BOOKMARK & BOOKMARK LIST
    // =========================================================================

    [ObservableProperty]
    private string _bookmarkMemoInput = string.Empty;

    public string AddBookmarkComicText =>
        !string.IsNullOrWhiteSpace(ComicTitle) ? ComicTitle : "Bộ truyện";

    public string AddBookmarkChapterOnlyText =>
        !string.IsNullOrWhiteSpace(ChapterTitle) ? ChapterTitle : (!string.IsNullOrWhiteSpace(CurrentChapter?.Title) ? CurrentChapter.Title : "Chương hiện tại");

    public string AddBookmarkChapterText
    {
        get
        {
            string chap = !string.IsNullOrWhiteSpace(ChapterTitle) ? ChapterTitle : (!string.IsNullOrWhiteSpace(CurrentChapter?.Title) ? CurrentChapter.Title : string.Empty);
            string comic = ComicTitle ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(chap) && !string.IsNullOrWhiteSpace(comic) && !chap.Contains(comic, StringComparison.OrdinalIgnoreCase))
            {
                return $"{comic} - {chap}";
            }
            return !string.IsNullOrWhiteSpace(chap) ? chap : (!string.IsNullOrWhiteSpace(comic) ? comic : "Chương hiện tại");
        }
    }

    public string AddBookmarkPageText =>
        TotalPages > 0 ? $"Trang {CurrentPageDisplayNumber} / {TotalPages}" : $"Trang {CurrentPageDisplayNumber}";

    public string AddBookmarkLeftInfoText => AddBookmarkChapterText;

    public string AddBookmarkRightInfoText => AddBookmarkPageText;

    [RelayCommand]
    public void OpenAddBookmarkPopup()
    {
        BookmarkMemoInput = string.Empty;
        OnPropertyChanged(nameof(AddBookmarkComicText));
        OnPropertyChanged(nameof(AddBookmarkChapterOnlyText));
        OnPropertyChanged(nameof(AddBookmarkChapterText));
        OnPropertyChanged(nameof(AddBookmarkPageText));
        OnPropertyChanged(nameof(AddBookmarkLeftInfoText));
        OnPropertyChanged(nameof(AddBookmarkRightInfoText));
        ActivePopup = "AddBookmark";
    }

    [RelayCommand]
    public void CancelAddBookmark()
    {
        ActivePopup = string.Empty;
    }

    [RelayCommand]
    public void ConfirmAddBookmark()
    {
        if (CurrentChapter == null || TotalPages <= 0)
        {
            ActivePopup = string.Empty;
            return;
        }

        string rootPath = ParentComic?.RootFolderPath ?? CurrentChapter.LocalPath;
        string resolvedChapterTitle = !string.IsNullOrWhiteSpace(CurrentChapter.Title)
            ? CurrentChapter.Title
            : (!string.IsNullOrWhiteSpace(ChapterTitle) ? ChapterTitle : System.IO.Path.GetFileName(CurrentChapter.LocalPath));

        var item = new ReaderBookmarkItem
        {
            ComicRootPath = rootPath,
            ComicTitle = string.IsNullOrWhiteSpace(ComicTitle) ? resolvedChapterTitle : ComicTitle,
            ChapterLocalPath = CurrentChapter.LocalPath,
            ChapterProgressKey = CurrentChapter.EffectiveProgressKey,
            ChapterIndex = CurrentChapter.Index,
            ChapterTitle = resolvedChapterTitle,
            PageIndex = CurrentPageIndex,
            TotalPages = TotalPages,
            Memo = (BookmarkMemoInput ?? string.Empty).Trim()
        };

        _progressStore.AddOrUpdateBookmark(item);
        RefreshBookmarksList();
        ActivePopup = string.Empty;
        ShowTransientToast($"🔖 Đã thêm dấu trang: {resolvedChapterTitle} - Trang {CurrentPageDisplayNumber}/{TotalPages}");
    }

    [RelayCommand]
    public void ToggleBookmarkCurrentPage()
    {
        if (CurrentChapter == null || TotalPages <= 0) return;

        string rootPath = ParentComic?.RootFolderPath ?? CurrentChapter.LocalPath;
        string resolvedChapterTitle = !string.IsNullOrWhiteSpace(CurrentChapter.Title)
            ? CurrentChapter.Title
            : (!string.IsNullOrWhiteSpace(ChapterTitle) ? ChapterTitle : System.IO.Path.GetFileName(CurrentChapter.LocalPath));

        var existing = Bookmarks.FirstOrDefault(b =>
            b.ChapterProgressKey == CurrentChapter.EffectiveProgressKey &&
            b.PageIndex == CurrentPageIndex);

        if (existing != null)
        {
            _progressStore.RemoveBookmark(existing.Id);
            RefreshBookmarksList();
            ShowTransientToast($"🗑️ Đã xóa dấu trang: {resolvedChapterTitle} - Trang {CurrentPageDisplayNumber}/{TotalPages}");
        }

        else
        {
            var item = new ReaderBookmarkItem
            {
                ComicRootPath = rootPath,
                ComicTitle = string.IsNullOrWhiteSpace(ComicTitle) ? resolvedChapterTitle : ComicTitle,
                ChapterLocalPath = CurrentChapter.LocalPath,
                ChapterProgressKey = CurrentChapter.EffectiveProgressKey,
                ChapterIndex = CurrentChapter.Index,
                ChapterTitle = resolvedChapterTitle,
                PageIndex = CurrentPageIndex,
                TotalPages = TotalPages,
                Memo = string.Empty
            };
            _progressStore.AddOrUpdateBookmark(item);
            RefreshBookmarksList();
            ShowTransientToast($"🔖 Đã thêm dấu trang: {resolvedChapterTitle} - Trang {CurrentPageDisplayNumber}/{TotalPages}");
        }
    }


    [RelayCommand]
    public void OpenBookmarkListPopup()
    {
        RefreshBookmarksList();
        TogglePopup("BookmarkList");
    }

    [RelayCommand]
    public void CloseBookmarkListPopup()
    {
        ActivePopup = string.Empty;
    }

    [RelayCommand]
    public async Task BackupBookmarksAsync()
    {
        int bmCount = _progressStore.GetBookmarkCount();
        if (bmCount == 0)
        {
            ShowTransientToast("⚠️ Chưa có dấu trang nào để sao lưu.");
            return;
        }

        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null && topLevel.StorageProvider.CanSave)
            {
                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Chọn nơi lưu file sao lưu Dấu trang & Lịch sử đọc (.md)",
                    SuggestedFileName = ReadProgressStore.SavedReaderFileName,
                    DefaultExtension = "md",
                    ShowOverwritePrompt = true,
                    FileTypeChoices = new List<FilePickerFileType>
                    {
                        new("Markdown Reader Backup (*.md)") { Patterns = new[] { "*.md" } },
                        new("All Files (*.*)") { Patterns = new[] { "*" } }
                    }
                });

                if (file == null) return;

                string? localPath = file.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(localPath))
                {
                    string saved = await _progressStore.SaveReaderToMarkdownAsync(localPath);
                    ShowTransientToast($"💾 Đã sao lưu {bmCount} dấu trang ra {Path.GetFileName(saved)}");
                }
                else
                {
                    string content = _progressStore.BuildReaderMarkdownContent();
                    await using var stream = await file.OpenWriteAsync();
                    stream.SetLength(0);
                    await using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
                    await writer.WriteAsync(content);
                    await writer.FlushAsync();
                    ShowTransientToast($"💾 Đã sao lưu {bmCount} dấu trang ra {file.Name}");
                }
                return;
            }

            string defSaved = await _progressStore.SaveReaderToMarkdownAsync();
            ShowTransientToast($"💾 Đã sao lưu ra {Path.GetFileName(defSaved)}");
        }
        catch (Exception ex)
        {
            ShowTransientToast($"❌ Lỗi sao lưu: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task RestoreBookmarksAsync()
    {
        try
        {
            var topLevel = GetTopLevel();
            if (topLevel?.StorageProvider != null && topLevel.StorageProvider.CanOpen)
            {
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Chọn file Markdown (.md) để phục hồi Dấu trang",
                    AllowMultiple = false,
                    FileTypeFilter = new List<FilePickerFileType>
                    {
                        new("Markdown Reader Backup (*.md)") { Patterns = new[] { "*.md", "*.txt" } },
                        new("All Files (*.*)") { Patterns = new[] { "*" } }
                    }
                });

                if (files == null || files.Count == 0) return;

                var selectedFile = files[0];
                string? localPath = selectedFile.TryGetLocalPath();
                ReaderRestoreResult res;

                if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
                {
                    res = await _progressStore.LoadReaderFromMarkdownAsync(localPath, mergeWithExisting: true);
                }
                else
                {
                    await using var stream = await selectedFile.OpenReadAsync();
                    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                    string content = await reader.ReadToEndAsync();
                    res = _progressStore.ParseReaderMarkdownContent(content, mergeWithExisting: true, selectedFile.Name);
                }

                if (res.HasValidStructure)
                {
                    RefreshBookmarksList();
                    ShowTransientToast($"✅ Đã phục hồi {res.RestoredBookmarks} dấu trang từ file Markdown!");
                }
                else
                {
                    ShowTransientToast("⚠️ File Markdown không đúng định dạng sao lưu.");
                }
                return;
            }

            var fallbackRes = await _progressStore.LoadReaderFromMarkdownAsync(null, mergeWithExisting: true);
            if (fallbackRes.HasValidStructure)
            {
                RefreshBookmarksList();
                ShowTransientToast($"✅ Đã phục hồi {fallbackRes.RestoredBookmarks} dấu trang!");
            }
        }
        catch (Exception ex)
        {
            ShowTransientToast($"❌ Lỗi phục hồi: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task SyncGoogleDriveBookmarksAsync()
    {
        if (!GoogleDriveSyncService.Instance.IsConnected)
        {
            ShowTransientToast("⚠️ Chưa kết nối Google Drive. Vui lòng kết nối từ màn hình chính.");
            return;
        }

        try
        {
            ShowTransientToast("⏳ Đang đồng bộ Dấu trang lên Google Drive...");
            var (ok, msg) = await GoogleDriveSyncService.Instance.BackupReaderToCloudAsync();
            if (ok)
            {
                ShowTransientToast("✅ Đã sao lưu Dấu trang lên Google Drive (\\GMTPC\\Comic Downloader GMTPC\\)");
            }
            else
            {
                ShowTransientToast($"❌ Lỗi: {msg}");
            }
        }
        catch (Exception ex)
        {
            ShowTransientToast($"❌ Lỗi đồng bộ: {ex.Message}");
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

    [RelayCommand]
    public async Task JumpToBookmarkAsync(ReaderBookmarkItem? item)
    {
        if (item == null) return;
        ActivePopup = string.Empty;

        var targetChap = ChapterList.FirstOrDefault(c =>
            string.Equals(c.EffectiveProgressKey, item.ChapterProgressKey, StringComparison.OrdinalIgnoreCase) ||
            (c.Index == item.ChapterIndex && string.Equals(c.Title, item.ChapterTitle, StringComparison.OrdinalIgnoreCase)));

        if (targetChap != null && !ReferenceEquals(targetChap, CurrentChapter))
        {
            _progressStore.SaveProgress(targetChap.EffectiveProgressKey, item.PageIndex);
            _suppressChapterSelectionChanged = true;
            CurrentChapter = targetChap;
            _suppressChapterSelectionChanged = false;
            await LoadChapterInternalAsync(targetChap);
        }

        if (Pages.Count > 0)
        {
            int targetIdx = Math.Clamp(item.PageIndex, 0, Pages.Count - 1);
            CurrentPageIndex = targetIdx;
            RequestScrollToPage(targetIdx);
            ShowTransientToast($"🔖 Đã nhảy tới dấu trang: {item.LeftDisplayText}");
        }
    }

    [RelayCommand]
    public void DeleteBookmark(ReaderBookmarkItem? item)
    {
        if (item == null) return;
        _progressStore.RemoveBookmark(item.Id);
        RefreshBookmarksList();
        ShowTransientToast("🗑 Đã xóa dấu trang");
    }

    private void RefreshBookmarksList()
    {
        string? filterKey = ParentComic?.RootFolderPath ?? CurrentChapter?.LocalPath ?? ComicTitle;
        var list = _progressStore.GetBookmarks(filterKey);
        Bookmarks.Clear();
        foreach (var bm in list)
        {
            Bookmarks.Add(bm);
        }
        OnPropertyChanged(nameof(HasBookmarks));
    }

    // =========================================================================
    // CÁC THUỘC TÍNH CHẾ ĐỘ ĐỌC CỐT LÕI (SCROLL / FLIP / RTL / DOUBLE PAGE / ZOOM)
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFlipMode))]
    [NotifyPropertyChangedFor(nameof(ModeButtonText))]
    [NotifyPropertyChangedFor(nameof(ModeBadgeBackground))]
    [NotifyPropertyChangedFor(nameof(ComicScreenPageBadgeText))]
    private bool _isScrollMode = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeButtonText))]
    [NotifyPropertyChangedFor(nameof(PageScrollOrientation))]
    private bool _isHorizontalScrollMode = false;

    public Avalonia.Layout.Orientation PageScrollOrientation =>
        IsHorizontalScrollMode ? Avalonia.Layout.Orientation.Horizontal : Avalonia.Layout.Orientation.Vertical;

    partial void OnIsHorizontalScrollModeChanged(bool value)
    {
        ApplyFitModeLayout();
        if (IsScrollMode)
        {
            Dispatcher.UIThread.Post(() => RequestScrollToPage(CurrentPageIndex), DispatcherPriority.Background);
        }
    }

    public bool IsFlipMode => !IsScrollMode;

    public string ModeButtonText =>
        !IsScrollMode
            ? "📄 Chế độ: Lật Trang"
            : (IsHorizontalScrollMode ? "↔ Chế độ: Cuộn Ngang" : "📜 Chế độ: Cuộn Dọc");

    public string ModeBadgeBackground =>
        !IsScrollMode
            ? "#7C3AED"
            : (IsHorizontalScrollMode ? "#0D9488" : "#0284C7");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RtlButtonText))]
    [NotifyPropertyChangedFor(nameof(RtlButtonBackground))]
    [NotifyPropertyChangedFor(nameof(LeftFlipPage))]
    [NotifyPropertyChangedFor(nameof(RightFlipPage))]
    private bool _isRightToLeft = false;

    public string RtlButtonText => IsRightToLeft ? "◀ RTL: Phải → Trái" : "▶ LTR: Trái → Phải";

    public string RtlButtonBackground => IsRightToLeft ? "#D97706" : "#1E293B";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DoublePageButtonText))]
    [NotifyPropertyChangedFor(nameof(DoublePageButtonBackground))]
    [NotifyPropertyChangedFor(nameof(FlipGridColumns))]
    [NotifyPropertyChangedFor(nameof(HasSecondaryPage))]
    [NotifyPropertyChangedFor(nameof(LeftFlipPage))]
    [NotifyPropertyChangedFor(nameof(RightFlipPage))]
    [NotifyPropertyChangedFor(nameof(ComicScreenPageBadgeText))]
    private bool _isDoublePage = false;

    public string DoublePageButtonText => IsDoublePage ? "📖 2 Trang Kép" : "📄 1 Trang Đơn";

    public string DoublePageButtonBackground => IsDoublePage ? "#059669" : "#1E293B";

    public int FlipGridColumns => (IsDoublePage && SecondaryPage != null) ? 2 : 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStandardOverlayBarsVisible))]
    private bool _isOverlayVisible = false;

    public static event Action<bool>? AndroidOverlayVisibilityChanged;

    /// <summary>
    /// Khoảng đệm an toàn góc trên bên trái giúp nút Back không bị che bởi camera nốt ruồi / giọt nước trên điện thoại.
    /// </summary>
    public Thickness ReaderHeaderSafeMargin => OperatingSystem.IsAndroid()
        ? new Thickness(26, 6, 8, 6)
        : new Thickness(8, 6, 8, 6);

    partial void OnIsOverlayVisibleChanged(bool value)
    {
        if (!value && !IsMarginEditorOpen)
        {
            ActivePopup = string.Empty;
        }
        AndroidOverlayVisibilityChanged?.Invoke(value);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullScreenButtonText))]
    private bool _isFullScreen = true;

    public string FullScreenButtonText => IsFullScreen ? "🗗 Thoát Fullscreen" : "⛶ Toàn Màn Hình";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercentText))]
    [NotifyPropertyChangedFor(nameof(PageDisplayWidth))]
    private double _zoomLevel = 1.0;

    public string ZoomPercentText => $"{Math.Round(ZoomLevel * 100)}%";

    public double PageDisplayWidth => Math.Round(Math.Max(240, _viewportWidth) * ZoomLevel);

    partial void OnZoomLevelChanged(double value)
    {
        ApplyFitModeLayout();
    }

    [ObservableProperty]
    private bool _isLoadingChapter;

    [ObservableProperty]
    private double _chapterLoadingProgress;

    [ObservableProperty]
    private string _chapterLoadingStatusText = string.Empty;

    [ObservableProperty]
    private bool _isChapterLoadingIndeterminate;

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
        if (comic != null && !string.IsNullOrWhiteSpace(comic.Title))
        {
            ComicTitle = comic.Title;
        }
        else
        {
            string inferredTitle = string.Empty;
            if (!string.IsNullOrWhiteSpace(targetChapter.LocalPath))
            {
                try
                {
                    string clean = targetChapter.LocalPath.TrimEnd('\\', '/');
                    string? pDir = Path.GetDirectoryName(clean);
                    if (!string.IsNullOrWhiteSpace(pDir) && Directory.Exists(pDir))
                    {
                        inferredTitle = Path.GetFileName(pDir);
                    }
                }
                catch { }
            }
            ComicTitle = !string.IsNullOrWhiteSpace(inferredTitle) ? inferredTitle : targetChapter.Title;
        }

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

        RefreshBookmarksList();
        OnPropertyChanged(nameof(CanGoPrevChapter));
        OnPropertyChanged(nameof(CanGoNextChapter));

        _readerService.SetReaderActive(true);
        await LoadChapterInternalAsync(targetChapter);
    }

    partial void OnCurrentChapterChanged(LibraryChapterItem? value)
    {
        if (_suppressChapterSelectionChanged || value == null) return;
        SaveCurrentReadingSession();
        _isNavigatingToPreviousChapter = false;
        ActivePopup = string.Empty;
        RefreshBookmarksList();
        _ = LoadChapterInternalAsync(value);
    }

    partial void OnSelectedPdfBookmarkChanged(PdfBookmarkItem? value)
    {
        if (_suppressPdfBookmarkSelectionChanged || value == null || Pages.Count == 0) return;
        int targetIndex = Math.Clamp(value.PageIndex, 0, Pages.Count - 1);
        if (CurrentPageIndex != targetIndex)
        {
            CurrentPageIndex = targetIndex;
            RequestScrollToPage(targetIndex);
            ShowTransientToast($"📑 Đã chuyển tới mục lục PDF: {value.Title} (Trang {targetIndex + 1})");
        }
        ActivePopup = string.Empty;
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        if (Pages.Count == 0) return;
        int clamped = Math.Clamp(value, 0, Pages.Count - 1);
        if (clamped != value)
        {
            _currentPageIndex = clamped;
        }

        IsScrollAtStart = (clamped <= 0);
        IsScrollAtEnd = (clamped >= Pages.Count - 1);

        UpdateCurrentFlipPages();
        SyncActivePdfBookmark(clamped);
        RecordReadingProgress(clamped);
        TriggerPrefetch(clamped);
        if (IsImagePropertiesHudVisible)
        {
            UpdateImagePropertiesDetails();
        }
    }


    public void RequestScrollToPage(int pageIndex)
    {
        if (!IsScrollMode || _suppressScrollSync || Pages.Count == 0) return;
        int clamped = Math.Clamp(pageIndex, 0, Pages.Count - 1);
        ScrollToPageRequested?.Invoke(clamped);
    }

    public void ScrubToPage(int targetPageIndex)
    {
        if (Pages.Count == 0) return;
        int clamped = Math.Clamp(targetPageIndex, 0, Pages.Count - 1);
        if (CurrentPageIndex == clamped) return;

        // Hủy ngay toàn bộ task nạp ảnh cũ đang dở dang để ưu tiên 100% CPU/RAM giải mã trang đích tức thì
        _prefetchCts?.Cancel();
        _prefetchCts = new CancellationTokenSource();

        CurrentPageIndex = clamped;
        RequestScrollToPage(clamped);

        // Nạp tức thì trang đích với ưu tiên cao nhất
        var targetPage = Pages[clamped];
        if (!targetPage.IsLoaded && !targetPage.IsLoading)
        {
            _ = _readerService.EnsurePageLoadedAsync(targetPage);
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

    [RelayCommand]
    public async Task ReloadCurrentChapterAsync()
    {
        if (CurrentChapter == null) return;
        ActivePopup = string.Empty;
        ShowTransientToast("↻ Đang tải lại trang và chương hiện tại...");
        await LoadChapterInternalAsync(CurrentChapter);
    }

    private async Task LoadChapterInternalAsync(LibraryChapterItem chapter)
    {
        _loadCts?.Cancel();
        _prefetchCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        IsLoadingChapter = true;
        ChapterLoadingProgress = 0;
        ChapterLoadingStatusText = "Đang mở và phân tích tệp...";
        IsChapterLoadingIndeterminate = false;
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
            var progress = new Progress<(double percent, string status)>(val =>
            {
                ChapterLoadingProgress = Math.Clamp(val.percent, 0, 100);
                ChapterLoadingStatusText = val.status;
            });

            var loadedPages = await _readerService.LoadChapterPagesAsync(chapter, progress, ct);
            if (ct.IsCancellationRequested) return;

            ChapterLoadingProgress = 95;
            ChapterLoadingStatusText = $"Đang tối ưu hiển thị {loadedPages.Count} trang...";

            Thickness currentMargin = CalculateCurrentPageItemMargin();
            foreach (var p in loadedPages)
            {
                p.PageMargin = currentMargin;
            }
            Pages.AddRange(loadedPages);
            ApplyFitModeLayout();

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
            if (TotalPages > 0)
            {
                _progressStore.SaveChapterTotalPages(chapter.EffectiveProgressKey, TotalPages);
                if (!string.IsNullOrWhiteSpace(chapter.LocalPath))
                {
                    _progressStore.SaveChapterTotalPages(chapter.LocalPath, TotalPages);
                }
                if (ParentComic != null && !string.IsNullOrWhiteSpace(ParentComic.RootFolderPath) && ParentComic.TotalChapters <= 1)
                {
                    _progressStore.SaveChapterTotalPages(ParentComic.RootFolderPath, TotalPages);
                }
            }

            int savedPageIndex = _isNavigatingToPreviousChapter
                ? Math.Max(0, TotalPages - 1)
                : _progressStore.GetLastPageIndex(chapter.EffectiveProgressKey, TotalPages);

            // Nếu máy hiện tại chưa có tiến độ đọc cục bộ cho chapter này (savedPageIndex <= 0),
            // kiểm tra xem chapter.LastReadPage hoặc resume.md có ghi nhận trang đọc dở không
            if (!_isNavigatingToPreviousChapter && savedPageIndex <= 0)
            {
                if (chapter.LastReadPage > 1)
                {
                    savedPageIndex = Math.Clamp(chapter.LastReadPage - 1, 0, Math.Max(0, TotalPages - 1));
                }
                else
                {
                    var resumeEntry = _progressStore.FindResumeEntryForComic(ParentComic?.Title ?? ComicTitle, chapter.LocalPath);
                    if (resumeEntry != null && resumeEntry.LastReadPage > 1)
                    {
                        double chNum = ReadProgressStore.ExtractChapterNumber(chapter.Title);
                        bool isMatching = false;
                        if (chNum >= 0 && resumeEntry.ChapterNumber >= 0 && Math.Abs(chNum - resumeEntry.ChapterNumber) < 0.001)
                        {
                            isMatching = true;
                        }
                        else if (!string.IsNullOrWhiteSpace(resumeEntry.ChapterTitle) &&
                                 string.Equals(ReadProgressStore.NormalizeChapterName(chapter.Title), ReadProgressStore.NormalizeChapterName(resumeEntry.ChapterTitle), StringComparison.OrdinalIgnoreCase))
                        {
                            isMatching = true;
                        }
                        else if (resumeEntry.ChapterIndex > 0 && chapter.Index == resumeEntry.ChapterIndex)
                        {
                            isMatching = true;
                        }

                        if (isMatching)
                        {
                            savedPageIndex = Math.Clamp(resumeEntry.LastReadPage - 1, 0, Math.Max(0, TotalPages - 1));
                        }
                    }
                }
            }

            _isNavigatingToPreviousChapter = false;
            int startIndex = Math.Clamp(savedPageIndex, 0, Math.Max(0, TotalPages - 1));

            IsScrollAtStart = (startIndex <= 0);
            IsScrollAtEnd = (startIndex >= TotalPages - 1 && TotalPages > 0);

            _suppressScrollSync = true;
            CurrentPageIndex = startIndex;

            UpdateCurrentFlipPages();
            SyncActivePdfBookmark(startIndex);

            chapter.TotalPages = TotalPages;
            chapter.LastReadPage = startIndex + 1;
            if (CurrentChapter != null)
            {
                CurrentChapter.TotalPages = TotalPages;
                CurrentChapter.LastReadPage = startIndex + 1;
            }

            if (TotalPages > 0)
            {
                var loadTask1 = _readerService.EnsurePageLoadedAsync(Pages[startIndex], ct);
                var loadTask2 = (IsDoublePage && startIndex + 1 < TotalPages)
                    ? _readerService.EnsurePageLoadedAsync(Pages[startIndex + 1], ct)
                    : Task.CompletedTask;
                await Task.WhenAll(loadTask1, loadTask2);

                var firstTarget = Pages[startIndex];
                if (firstTarget.AspectWidth > 0 && firstTarget.AspectHeight > 0)
                {
                    int w = firstTarget.AspectWidth;
                    int h = firstTarget.AspectHeight;
                    foreach (var p in Pages)
                    {
                        if (!p.IsLoaded && (p.AspectWidth == 900 && p.AspectHeight == 1350 || p.AspectWidth <= 0))
                        {
                            p.SetKnownDimensionsFast(w, h);
                        }
                    }
                    ApplyFitModeLayout();
                }
            }

            TriggerPrefetch(startIndex);
            _readerService.StartBackgroundChapterPrecaching(Pages, startIndex);

            if (startIndex > 0)
            {
                string bookmarkHint = SelectedPdfBookmark != null ? $" ({SelectedPdfBookmark.Title})" : string.Empty;
                ShowTransientToast($"📖 Đã khôi phục vị trí đọc: {chapter.Title} - Trang {startIndex + 1}/{TotalPages}{bookmarkHint}");
                Dispatcher.UIThread.Post(() =>
                {
                    RequestScrollToPage(startIndex);
                    _suppressScrollSync = false;
                }, DispatcherPriority.Loaded);
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    RequestScrollToPage(0);
                    _suppressScrollSync = false;
                }, DispatcherPriority.Loaded);
            }
        }
        finally
        {
            IsLoadingChapter = false;
            SaveCurrentReadingSession();
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
            OnPropertyChanged(nameof(HasSecondaryPage));
            OnPropertyChanged(nameof(ComicScreenPageBadgeText));
            OnPropertyChanged(nameof(FlipDisplayWidth));
            OnPropertyChanged(nameof(FlipDisplayHeight));
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
        OnPropertyChanged(nameof(HasSecondaryPage));
        OnPropertyChanged(nameof(LeftFlipPage));
        OnPropertyChanged(nameof(RightFlipPage));
        OnPropertyChanged(nameof(ComicScreenPageBadgeText));
        OnPropertyChanged(nameof(HeaderSubTitleText));
        OnPropertyChanged(nameof(FlipDisplayWidth));
        OnPropertyChanged(nameof(FlipDisplayHeight));
        OnPropertyChanged(nameof(FlipImageStretch));
        OnPropertyChanged(nameof(LeftFlipPageAlignment));
        OnPropertyChanged(nameof(RightFlipPageAlignment));

        // Nạp và giải mã tức thời cả 2 trang lật (trang đơn & trang kép) để triệt tiêu lỗi stuck '⏳ Đang nạp trang...'
        if (CurrentPage != null && !CurrentPage.IsLoaded)
        {
            _ = EnsureSinglePageLoadedAsync(CurrentPage);
        }
        if (SecondaryPage != null && !SecondaryPage.IsLoaded)
        {
            _ = EnsureSinglePageLoadedAsync(SecondaryPage);
        }
    }

    private void RecordReadingProgress(int pageIndex)
    {
        if (IsLoadingChapter || _suppressScrollSync || CurrentChapter == null || TotalPages <= 0) return;

        int page1Based = Math.Clamp(pageIndex + 1, 1, TotalPages);
        CurrentChapter.TotalPages = TotalPages;
        CurrentChapter.LastReadPage = page1Based;

        _progressStore.SaveProgress(CurrentChapter.EffectiveProgressKey, pageIndex);
        SaveCurrentReadingSession();
    }

    /// <summary>
    /// Lưu toàn diện phiên đọc hiện tại: Chương đang xem, trang đang xem, tổng số trang,
    /// đồng bộ trực tiếp vào ReadProgressStore và resume.md (CrossDeviceResumeEntry) để ghi nhớ
    /// chính xác 100% chương vừa đọc khi thoát Reader trên mọi hệ điều hành.
    /// </summary>
    public void SaveCurrentReadingSession()
    {
        if (CurrentChapter == null) return;

        int totalPages = Math.Max(TotalPages, Pages.Count);
        int page1Based = totalPages > 0 ? Math.Clamp(CurrentPageIndex + 1, 1, totalPages) : Math.Max(1, CurrentPageIndex + 1);

        CurrentChapter.TotalPages = totalPages;
        CurrentChapter.LastReadPage = page1Based;

        if (!string.IsNullOrWhiteSpace(CurrentChapter.EffectiveProgressKey))
        {
            _progressStore.SaveProgress(CurrentChapter.EffectiveProgressKey, CurrentPageIndex);
            if (totalPages > 0)
            {
                _progressStore.SaveChapterTotalPages(CurrentChapter.EffectiveProgressKey, totalPages);
            }
        }

        string? comicRoot = ParentComic?.RootFolderPath;
        string? title = ParentComic?.Title ?? ComicTitle;

        if (string.IsNullOrWhiteSpace(comicRoot) && !string.IsNullOrWhiteSpace(CurrentChapter.LocalPath))
        {
            try
            {
                string cleanChPath = CurrentChapter.LocalPath.TrimEnd('\\', '/');
                string? parentDir = Path.GetDirectoryName(cleanChPath);
                if (!string.IsNullOrWhiteSpace(parentDir) && Directory.Exists(parentDir))
                {
                    comicRoot = parentDir;
                }
                else
                {
                    comicRoot = cleanChPath;
                }
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(comicRoot))
        {
            try
            {
                title = Path.GetFileName(comicRoot.TrimEnd('\\', '/'));
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(CurrentChapter.Title))
        {
            title = CurrentChapter.Title;
        }

        if (!string.IsNullOrWhiteSpace(comicRoot))
        {
            _progressStore.SaveLastActiveChapterIndex(comicRoot, CurrentChapter.Index);
        }
        if (!string.IsNullOrWhiteSpace(CurrentChapter.LocalPath))
        {
            _progressStore.SaveLastActiveChapterIndex(CurrentChapter.LocalPath, CurrentChapter.Index);
        }
        if (!string.IsNullOrWhiteSpace(title))
        {
            _progressStore.SaveLastActiveChapterForTitle(title, CurrentChapter.Index);
        }

        _progressStore.RecordSessionResume(
            comicTitle: title,
            comicRootOrFilePath: comicRoot ?? CurrentChapter.LocalPath,
            chapterTitle: CurrentChapter.Title,
            chapterIndex: CurrentChapter.Index,
            chapterNumber: ReadProgressStore.ExtractChapterNumber(CurrentChapter.Title),
            pageIndex: CurrentPageIndex,
            totalPages: totalPages
        );

        ParentComic?.RefreshReadStatus();
        _progressStore.FlushImmediately();
    }

    [ObservableProperty]
    private bool _isEcoMode = OperatingSystem.IsAndroid();

    public string EcoModeStatusText => IsEcoMode ? "🍃 BẬT (Mát máy)" : "Tắt (Tiêu chuẩn)";

    partial void OnIsEcoModeChanged(bool value)
    {
        OnPropertyChanged(nameof(EcoModeStatusText));
        TriggerPrefetch(CurrentPageIndex);
        if (value)
        {
            ShowTransientToast("🍃 Đã bật Chế độ Mát Máy (Tiết kiệm pin & RAM tối đa)");
        }
    }

    private int _lastPrefetchedIndex = -1;
    private long _lastPrefetchTick = 0;

    private void TriggerPrefetch(int centerIndex)
    {
        long now = Environment.TickCount64;
        // Nếu sự kiện cuộn xảy ra quá nhanh (< 120ms) và khoảng cách trang chỉ lệch 1 trang:
        // không hủy task cũ để các worker đang giải mã tiếp tục hoàn thành
        if (Math.Abs(centerIndex - _lastPrefetchedIndex) <= 1 && now - _lastPrefetchTick < 120)
        {
            return;
        }

        bool isBigJump = _lastPrefetchedIndex >= 0 && Math.Abs(centerIndex - _lastPrefetchedIndex) >= 4;
        if (isBigJump)
        {
            _prefetchCts?.Cancel();
            _prefetchCts = new CancellationTokenSource();
        }
        else if (_prefetchCts == null || _prefetchCts.IsCancellationRequested)
        {
            _prefetchCts = new CancellationTokenSource();
        }

        _lastPrefetchedIndex = centerIndex;
        _lastPrefetchTick = now;

        var ct = _prefetchCts.Token;
        var snapshot = Pages.ToList();
        bool scrollMode = IsScrollMode;
        bool ecoMode = IsEcoMode;

        _ = Task.Run(async () =>
        {
            try
            {
                await _readerService.UpdatePrefetchWindowAsync(snapshot, centerIndex, scrollMode, ecoMode, ct);
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
        if (IsMarginEditorOpen) return;
        if (HasActivePopup)
        {
            ActivePopup = string.Empty;
            return;
        }
        IsOverlayVisible = !IsOverlayVisible;
    }

    [RelayCommand]
    public void ToggleMode()
    {
        IsScrollMode = !IsScrollMode;
        if (IsHorizontalScrollMode && IsScrollMode)
        {
            PageScrollingMode = IsRightToLeft ? 4 : 3;
        }
        else
        {
            PageScrollingMode = IsScrollMode
                ? (IsRightToLeft ? 2 : 5)
                : (IsRightToLeft ? 1 : 0);
        }

        UpdateCurrentFlipPages();
        TriggerPrefetch(CurrentPageIndex);

        if (IsScrollMode)
        {
            ShowTransientToast(IsHorizontalScrollMode
                ? "↔ Đã chuyển sang chế độ Cuộn ngang liên tiếp"
                : "📜 Đã chuyển sang chế độ Cuộn dọc (Webtoon)");
            Dispatcher.UIThread.Post(() => RequestScrollToPage(CurrentPageIndex), DispatcherPriority.Background);
        }
        else
        {
            ShowTransientToast("📄 Đã chuyển sang chế độ Lật trang (Flip)");
        }
    }

    [RelayCommand]
    public void ToggleRtl()
    {
        IsRightToLeft = !IsRightToLeft;
        if (IsHorizontalScrollMode && IsScrollMode)
        {
            PageScrollingMode = IsRightToLeft ? 4 : 3;
        }
        else
        {
            PageScrollingMode = IsScrollMode
                ? (IsRightToLeft ? 2 : 5)
                : (IsRightToLeft ? 1 : 0);
        }
        UpdateCurrentFlipPages();
        ShowTransientToast(IsRightToLeft
            ? "📖 Đọc Manga: phải ☜(ﾟヮﾟ☜) trái"
            : "📄 Đọc chuẩn: trái (☞ﾟヮﾟ)☞ phải");
    }

    [RelayCommand]
    public void ToggleDoublePage()
    {
        IsDoublePage = !IsDoublePage;
        PageLayoutMode = IsDoublePage ? 2 : 1;
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
            RequestScrollToPage(CurrentPageIndex);
        }
        else if (CurrentPageIndex < Pages.Count - 1)
        {
            CurrentPageIndex = Pages.Count - 1;
            RequestScrollToPage(CurrentPageIndex);
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
            RequestScrollToPage(CurrentPageIndex);
        }
        else if (CurrentPageIndex > 0)
        {
            CurrentPageIndex = 0;
            RequestScrollToPage(CurrentPageIndex);
        }
        else if (CanGoPrevChapter)
        {
            _isNavigatingToPreviousChapter = true;
            PrevChapter();
        }
    }

    [RelayCommand]
    public void TapLeftZone()
    {
        if (IsMarginEditorOpen) return;
        if (HasActivePopup)
        {
            ActivePopup = string.Empty;
            return;
        }

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

    [RelayCommand]
    public void TapRightZone()
    {
        if (IsMarginEditorOpen) return;
        if (HasActivePopup)
        {
            ActivePopup = string.Empty;
            return;
        }

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
        ActivePopup = string.Empty;
        if (Pages.Count > 0)
        {
            CurrentPageIndex = 0;
            RequestScrollToPage(0);
        }
    }

    [RelayCommand]
    public void LastPage()
    {
        ActivePopup = string.Empty;
        if (Pages.Count > 0)
        {
            CurrentPageIndex = Pages.Count - 1;
            RequestScrollToPage(CurrentPageIndex);
        }
    }

    [RelayCommand]
    public void NextChapter()
    {
        ActivePopup = string.Empty;
        if (CurrentChapter == null) return;
        int idx = ChapterList.IndexOf(CurrentChapter);
        if (idx >= 0 && idx < ChapterList.Count - 1)
        {
            _isNavigatingToPreviousChapter = false;
            CurrentChapter = ChapterList[idx + 1];
            ShowTransientToast($"⏭ Chuyển sang: {CurrentChapter.Title}");
        }
    }

    [RelayCommand]
    public void PrevChapter()
    {
        ActivePopup = string.Empty;
        if (CurrentChapter == null) return;
        int idx = ChapterList.IndexOf(CurrentChapter);
        if (idx > 0)
        {
            _isNavigatingToPreviousChapter = true;
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
        ActivePopup = string.Empty;
        IsFullScreen = !IsFullScreen;
        FullScreenChangedRequested?.Invoke(IsFullScreen);
    }

    [RelayCommand]
    public void CloseReader()
    {
        SaveCurrentReadingSession();
        Cleanup();
        CloseRequested?.Invoke();
    }

    public void Cleanup()
    {
        try
        {
            SaveCurrentReadingSession();
            StopAutoScroll();
            _loadCts?.Cancel();
            _prefetchCts?.Cancel();
            _effectRefreshCts?.Cancel();
            _progressStore.FlushImmediately();
            DisposeLoadedBitmaps();
            _readerService.SetReaderActive(false);
            _ = Task.Run(() => ComicDiskCacheService.Instance.EnforceGlobalCacheQuota());
            if (OperatingSystem.IsAndroid() || IsEcoMode)
            {
                Task.Run(() =>
                {
                    try { GC.Collect(0, GCCollectionMode.Optimized); } catch { }
                });
            }
        }
        catch { }
    }

    private void DisposeLoadedBitmaps()
    {
        foreach (var p in Pages)
        {
            p.Unload();
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

    // =========================================================================
    // HOẠT ẢNH & TỐC ĐỘ (ANIMATION SPEED: GIF / WEBP ĐỘNG TỪ 0.1x ĐẾN 8.0x)
    // =========================================================================
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnimationSpeedText))]
    private double _animationSpeed = ComicReaderService.GlobalAnimationSpeed;

    public string AnimationSpeedText => $"{AnimationSpeed:0.0}x ({Math.Round(AnimationSpeed * 100):0}%)";

    partial void OnAnimationSpeedChanged(double value)
    {
        double clamped = Math.Clamp(Math.Round(value, 1), 0.1, 8.0);
        ComicReaderService.GlobalAnimationSpeed = clamped;
        foreach (var page in Pages)
        {
            page.SetAnimationSpeed(clamped);
        }
        LeftFlipPage?.SetAnimationSpeed(clamped);
        RightFlipPage?.SetAnimationSpeed(clamped);
    }

    [RelayCommand]
    public void IncreaseAnimationSpeed()
    {
        SetAnimationSpeed(Math.Min(8.0, Math.Round(AnimationSpeed + 0.1, 1)));
    }

    [RelayCommand]
    public void DecreaseAnimationSpeed()
    {
        SetAnimationSpeed(Math.Max(0.1, Math.Round(AnimationSpeed - 0.1, 1)));
    }

    [RelayCommand]
    public void ResetAnimationSpeed()
    {
        SetAnimationSpeed(1.0);
    }

    public void SetAnimationSpeed(double speed)
    {
        double clamped = Math.Clamp(Math.Round(speed, 1), 0.1, 8.0);
        AnimationSpeed = clamped;
    }

    // =========================================================================
    // CÀI ĐẶT & PHÍM TẮT CHUẨN BANDIVIEW (SETTINGS & SHORTCUTS MODAL F5)
    // =========================================================================
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShortcutTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsAnimationTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsReadingModeTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsCacheDataTabSelected))]
    private int _settingsTabIndex = 0; // 0 = Phím tắt, 1 = Hoạt ảnh, 2 = Chế độ đọc, 3 = Bộ nhớ đệm (Cache data)

    public bool IsShortcutTabSelected => SettingsTabIndex == 0;
    public bool IsAnimationTabSelected => SettingsTabIndex == 1;
    public bool IsReadingModeTabSelected => SettingsTabIndex == 2;
    public bool IsCacheDataTabSelected => SettingsTabIndex == 3;

    // =========================================================================
    // DỮ LIỆU BỘ NHỚ ĐỆM (CACHE DATA) CHUẨN COMIC SCREEN
    // =========================================================================
    [ObservableProperty]
    private string _thumbnailStorageSummaryText = "Thumbnails 0 B / 1.0 GB";

    [ObservableProperty]
    private string _archiveCacheSummaryText = "0 B";

    [ObservableProperty]
    private string _retentionPeriodSummaryText = "4 days";

    [ObservableProperty]
    private string _zipCacheStorageSummaryText = string.Empty;

    [ObservableProperty]
    private string _totalCacheSummaryText = "0 B";

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
        ShowTransientToast($"✓ Đã lưu dung lượng zip cache: {SelectedZipQuotaMb} MB");
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
        ShowTransientToast($"✓ Đã lưu hạn mức bộ nhớ ảnh bìa: {SelectedThumbnailQuotaMb} MB");
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
        ShowTransientToast($"✓ Đã lưu thời hạn lưu trữ tệp nén: {text}");
    }

    [RelayCommand]
    public void ClearThumbnails()
    {
        ComicDiskCacheService.Instance.ClearThumbnails();
        RefreshCacheSummary();
        ShowTransientToast("✓ Đã dọn dẹp sạch bộ nhớ đệm ảnh bìa (Thumbnails)");
    }

    [RelayCommand]
    public void ClearArchiveCache()
    {
        ComicDiskCacheService.Instance.ClearArchiveCache();
        RefreshCacheSummary();
        ShowTransientToast("✓ Đã dọn dẹp bộ nhớ đệm tệp nén & tài liệu (.zip, .cbz, .rar, .cbr, .pdf, .mobi)");
    }

    [RelayCommand]
    public void ClearResumeCache()
    {
        ComicDiskCacheService.Instance.ClearResumeCache();
        RefreshCacheSummary();
        ShowTransientToast("✓ Đã xóa dữ liệu vị trí trang đang đọc dở (Resume cache)");
    }

    [RelayCommand]
    public void ClearBookmarks()
    {
        ComicDiskCacheService.Instance.ClearBookmarks();
        RefreshCacheSummary();
        ShowTransientToast("✓ Đã xóa toàn bộ dấu trang (Bookmarks)");
    }

    [RelayCommand]
    public void ClearHistory()
    {
        ComicDiskCacheService.Instance.ClearHistory();
        RefreshCacheSummary();
        ShowTransientToast("✓ Đã xóa toàn bộ lịch sử đọc truyện (History)");
    }

    [RelayCommand]
    public void ClearAllCache()
    {
        ComicDiskCacheService.Instance.ClearAllCache();
        RefreshCacheSummary();
        ShowTransientToast("✓ Đã dọn dẹp toàn bộ 100% bộ nhớ đệm và dữ liệu đọc");
    }

    [RelayCommand]
    public void SelectSettingsTab(string? tabStr)
    {
        if (int.TryParse(tabStr, out int idx))
        {
            SettingsTabIndex = idx;
        }
    }

    [ObservableProperty]
    private string _shortcutSearchText = string.Empty;

    [ObservableProperty]
    private bool _isListeningForShortcutKey = false;

    [ObservableProperty]
    private ReaderShortcutEntry? _activeEditingShortcut;

    [ObservableProperty]
    private string _shortcutListeningPrompt = string.Empty;

    public ObservableCollection<ReaderShortcutEntry> ShortcutEntries { get; } = new();

    public void LoadShortcuts()
    {
        ShortcutEntries.Clear();
        var list = ReaderShortcutService.Instance.Entries;
        if (!string.IsNullOrWhiteSpace(ShortcutSearchText))
        {
            list = list.Where(e => e.Name.Contains(ShortcutSearchText, StringComparison.OrdinalIgnoreCase) ||
                                   e.Group.Contains(ShortcutSearchText, StringComparison.OrdinalIgnoreCase) ||
                                   e.Description.Contains(ShortcutSearchText, StringComparison.OrdinalIgnoreCase) ||
                                   e.CurrentKeysText.Contains(ShortcutSearchText, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        foreach (var item in list)
        {
            ShortcutEntries.Add(item);
        }
    }

    partial void OnShortcutSearchTextChanged(string value)
    {
        LoadShortcuts();
    }

    [RelayCommand]
    public void OpenSettingsPopup(int tabIndex = 0)
    {
        SettingsTabIndex = tabIndex;
        CancelAssignKey();
        LoadShortcuts();
        RefreshCacheSummary();
        ActivePopup = "Settings";
    }

    [RelayCommand]
    public void CloseSettingsPopup()
    {
        CancelAssignKey();
        IsThumbnailQuotaDialogOpen = false;
        IsZipQuotaDialogOpen = false;
        IsRetentionDialogOpen = false;
        if (ActivePopup == "Settings")
        {
            ActivePopup = string.Empty;
        }
    }

    [RelayCommand]
    public void StartAssignShortcut(ReaderShortcutEntry? entry)
    {
        if (entry == null) return;
        ActiveEditingShortcut = entry;
        IsListeningForShortcutKey = true;
        ShortcutListeningPrompt = $"⌨ Đang chờ nhấn phím cho '{entry.Name}'... (Nhấn phím bất kỳ, Esc để hủy)";
    }

    [RelayCommand]
    public void CancelAssignKey()
    {
        ActiveEditingShortcut = null;
        IsListeningForShortcutKey = false;
        ShortcutListeningPrompt = string.Empty;
    }

    public void AssignKeyToEditingShortcut(Key key, KeyModifiers modifiers)
    {
        if (!IsListeningForShortcutKey || ActiveEditingShortcut == null) return;

        // Bỏ qua nếu chỉ là phím bổ trợ (Ctrl/Alt/Shift đơn độc)
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift)
        {
            return;
        }

        string gesture = ReaderShortcutService.BuildGestureString(key, modifiers);
        ReaderShortcutService.Instance.AssignShortcut(ActiveEditingShortcut.Id, gesture);
        ShowTransientToast($"✓ Đã gán phím '{gesture}' cho {ActiveEditingShortcut.Name}");
        CancelAssignKey();
        LoadShortcuts();
    }

    [RelayCommand]
    public void ResetAllShortcuts()
    {
        ReaderShortcutService.Instance.ResetToDefaults();
        CancelAssignKey();
        LoadShortcuts();
        ShowTransientToast("✓ Đã khôi phục toàn bộ phím tắt về mặc định chuẩn BandiView");
    }
}
