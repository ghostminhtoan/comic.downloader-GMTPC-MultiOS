# 🧭 BẢN ĐỒ MÃ NGUỒN & MỤC LỤC ĐIỀU HƯỚNG TỔNG THỂ (MAINWINDOW.MENU.MD)
> **TÀI LIỆU HOA TIÊU ĐIỀU HƯỚNG BẮT BUỘC DÀNH CHO AGENT / AI PAIR-PROGRAMMER**
> *Áp dụng cho mọi phiên làm việc mới: BẮT BUỘC ĐỌC TRƯỚC KHI ĐỌC HOẶC SỬA BẤT KỲ FILE CODE NÀO ĐỂ BẢO VỆ TOKEN CONTEXT.*

---

## 🎯 1. NGUYÊN LÝ HOẠT ĐỘNG & TIẾT KIỆM TOKEN (TOKEN ECONOMY PROTOCOL)

1. **Thực trạng kích thước file**:
   - Dự án sở hữu các file mã nguồn rất lớn (`MainView.axaml` ~840 KB / 13.400 dòng, `ComicScraperService.cs` ~350 KB, `MainViewModel.cs` ~275 KB / 7.000 dòng, `MainView.axaml.cs` ~126 KB / 4.000 dòng...).
   - Nếu Agent nạp toàn bộ file vào context sẽ gây cạn kiệt token, làm chậm phản hồi và dẫn đến việc bị hệ thống cắt ngắn (truncation) hoặc sinh code ảo giác.

2. **Quy tắc vàng của Bản đồ Neo (Anchor Tagging)**:
   - **Quy chuẩn thẻ neo (Section Tag)**:
     - Trong C#: `// [SEC-...]` hoặc `#region [SEC-...] ... #endregion`
     - Trong XAML: `<!-- [SEC-XAML-...] -->`
     - Trong Markdown/Doc: `## [SEC-...]`
   - **Quy trình tra cứu & sửa code chuẩn**:
     - **Bước 1**: Mở `MainWindow.Menu.md` $\rightarrow$ Tra cứu tính năng cần can thiệp.
     - **Bước 2**: Xác định File đích & Mã thẻ Section Tag (Ví dụ: `[SEC-VM-AUDIO-PLAYER]` trong `MainViewModel.cs`).
     - **Bước 3**: Dùng công cụ tìm kiếm từ khóa hoặc đọc theo phạm vi (chỉ đọc đúng Section đó, tuyệt đối không nạp toàn bộ file lớn).
     - **Bước 4**: Sửa code trong phạm vi Section đó bằng `edit_file`.

---

## ⚡ 2. BẢNG TRA CỨU NGHIỆP VỤ NHANH (QUICK FEATURE-TO-FILE MATRIX)

| Nghiệp vụ / Tính năng | File chịu trách nhiệm chính | Mã Thẻ Neo (Section Tag) | Mô tả tóm tắt |
| :--- | :--- | :--- | :--- |
| **Giao diện Tải về & Hàng chờ** | `MainView.axaml` | `<!-- [SEC-XAML-TAB-DOWNLOAD] -->` | Layout danh sách truyện tải, ô nhập URL, nút tải, thanh tiến trình |
| **Logic Tải & Điều phối Hàng đợi** | `MainViewModel.cs`<br>`DownloadEngineService.cs` | `[SEC-VM-DOWNLOAD-CTRL]`<br>`[SEC-ENGINE-WORKER]` | Thêm/xóa link, Start/Pause/Resume, phân luồng tải ảnh |
| **Tách chương song song (Parallel Split)** | `MainViewModel.cs` | `[SEC-VM-SPLIT-PARALLEL]` | Chia nhỏ truyện nhiều chap thành các nhánh con tải độc lập |
| **Scan Thiếu Chap Online & Offline** | `MainView.axaml`<br>`MainViewModel.cs`<br>`LibraryScannerService.cs` | `<!-- [SEC-XAML-TAB-SCAN] -->`<br>`[SEC-VM-SCAN-CHAPS]` | So sánh chapter trên web với thư mục ổ cứng |
| **Nguồn Manga & Hentai (Scrapers)** | `ComicScraperService.cs`<br>`MainView.axaml` | `[SEC-SCRAPER-DOMAINS]`<br>`<!-- [SEC-XAML-TAB-SOURCES] -->` | Logic cào HTML/API từ 15+ website truyện |
| **Tìm kiếm truyện hàng loạt** | `MainView.axaml`<br>`MainViewModel.cs` | `<!-- [SEC-XAML-TAB-SEARCH] -->`<br>`[SEC-VM-SEARCH-BATCH]` | Nhập danh sách tên truyện, cào kết quả từ nhiều nguồn |
| **Thư viện truyện (Library)** | `LibraryView.axaml`<br>`LibraryViewModel.cs` | `[SEC-LIB-VIEW]`<br>`[SEC-LIB-VM]` | Quản lý kho truyện offline, lọc thể loại, tác giả |
| **Trình đọc truyện (Comic Reader)** | `ComicReaderControl.axaml`<br>`ComicReaderControl.axaml.cs`<br>`ComicReaderViewModel.cs` | `[SEC-READER-VIEW]`<br>`[SEC-READER-EVENTS]`<br>`[SEC-READER-VM]` | Xem ảnh Webtoon, 2 trang, lật trang, zoom, lưu vị trí đọc |
| **Phát nhạc nền & Trích xuất Video** | `NativeAudioPlaybackService.cs`<br>`WindowsMediaFoundationExtractor.cs`<br>`VideoMetadataParser.cs` | `[SEC-AUDIO-CORE]`<br>`[SEC-WMF-EXTRACTOR]`<br>`[SEC-VIDEO-PARSER]` | Phát audio native, trích xuất frame video bằng Windows Media Foundation |
| **Đồng bộ Google Drive & Autosave** | `GoogleDriveSyncService.cs`<br>`ReadProgressStore.cs`<br>`MainView.axaml` | `[SEC-DRIVE-SERVICE]`<br>`[SEC-PROGRESS-STORE]`<br>`<!-- [SEC-XAML-MODALS-DRIVE] -->` | Sao lưu/khôi phục `autosave.md`, đồng bộ tiến độ đọc |
| **Công cụ Cắt ảnh dài & Nâng cấp ảnh**| `MainView.axaml`<br>`ImageEnhancerService.cs` | `<!-- [SEC-XAML-TAB-TOOLS] -->`<br>`[SEC-TOOL-IMAGE]` | Cắt ảnh cuộn dài, chỉnh sáng tối, contrast, sharpening |
| **Công cụ Gộp file / Nối chap** | `MainView.axaml`<br>`ArchiveMergerService.cs` | `<!-- [SEC-XAML-TOOL-MERGER] -->`<br>`[SEC-TOOL-MERGER]` | Ghép nhiều zip/cbz/folder thành volume hoàn chỉnh |
| **Dual Picture Sort** | `MainViewModel.DualPictureSort.cs`<br>`MainView.axaml` | `[SEC-DUAL-SORT]`<br>`<!-- [SEC-XAML-TOOL-DUALSORT] -->` | Phân loại và so sánh ảnh kép chuyên sâu |
| **Thao tác chuột, Kéo thả & Hotkeys** | `MainView.axaml.cs` | `[SEC-VIEW-INTERACTION]`<br>`[SEC-VIEW-HOTKEYS]` | Pan/Zoom ảnh, Floating bar, phím tắt toàn màn hình |
| **Tự động Cập nhật Release & Beta** | `MainViewModel.cs`<br>`MainView.axaml` | `[SEC-VM-UPDATE-SYS]`<br>`<!-- [SEC-XAML-HEADER] -->` | Kiểm tra version, Speed guard > 10Mbps, Backup rotation |

---

## 🗺️ 3. BẢN ĐỒ SECTION TAGS CHI TIẾT CÁC FILE LỚN

### 📄 `MainView.axaml` (~840 KB, ~13.400 dòng)
```xml
<!-- [SEC-XAML-RES] -->             : Khai báo Resources, Converters, ComicQueueCardTemplate
<!-- [SEC-XAML-HEADER] -->          : Header bar, Window Title, Button Cập nhật Release/Beta
<!-- [SEC-XAML-TAB-DOWNLOAD] -->    : Tab 📥 Tải về (Bảng nhập URL, Mode đơn/loạt, Nút hành động, Queue List)
<!-- [SEC-XAML-TAB-SCAN] -->        : Tab 🔍 Scan Thiếu Chap (Giao diện đối soát chap thiếu online/offline)
<!-- [SEC-XAML-TAB-LOGS] -->        : Tab 📜 Nhật Ký Hệ Thống
<!-- [SEC-XAML-TAB-REVERSE] -->     : Tab 🔃 Đảo Ngược Danh Sách
<!-- [SEC-XAML-TAB-SOURCES] -->     : Tab 🌐 Nguồn truyện (Manga, English, Hentai, Novel, Password)
<!-- [SEC-XAML-TAB-TOOLS] -->       : Tab 🛠️ Công cụ (Tách/gộp, Cắt ảnh dài, Xử lý ảnh, Đóng gói, Rename, Dual Sort)
<!-- [SEC-XAML-TAB-LIBRARY] -->     : Tab 📚 Thư Viện Đọc Truyện
<!-- [SEC-XAML-TAB-SEARCH] -->      : Tab 🔍 Tìm Truyện Hàng Loạt
<!-- [SEC-XAML-FLOATING-PLAYER] --> : Floating Bar Media Player điều khiển nhạc nền ở góc màn hình
<!-- [SEC-XAML-MODALS-DRIVE] -->    : Dialog / Overlay Cấu hình Google Drive Sync & Cloud Backup
```

### 📄 `MainViewModel.cs` (~275 KB, ~7.000 dòng)
```csharp
#region [SEC-VM-INIT]            : Constructor, Service Dependency Injection, Load Config & Lifecycle
#region [SEC-VM-PROPS]           : Core Observable Properties binding với UI (AppTitle, Lang, Tabs, Status)
#region [SEC-VM-QUEUE-CORE]      : Quản lý hàng đợi tải (ObservableCollection ComicQueue, AddLinks, Clipboard)
#region [SEC-VM-DOWNLOAD-CTRL]   : Điều khiển tải (StartDownload, Pause, Resume, Stop, Retry Errors, Download All)
#region [SEC-VM-SPLIT-PARALLEL]  : Tách chương song song theo cụm (50, 100, 200, 300, 400 chap)
#region [SEC-VM-SOURCES]         : Xử lý nguồn truyện (Kiểm tra domain, đăng nhập tài khoản nguồn, crawl status)
#region [SEC-VM-UPDATE-SYS]      : Cơ chế cập nhật Release & Beta (Speed Guard 10Mbps, Backup asset rotation)
#region [SEC-VM-DRIVE-SYNC]      : Tích hợp Google Drive (OAuth login, Backup autosave.md, Restore queue)
#region [SEC-VM-AUDIO-PLAYER]    : Điều khiển âm thanh nền (TogglePlayAudio, StopAudio, Next/Prev Track, Volume)
```

### 📄 `MainView.axaml.cs` (~126 KB, ~4.000 dòng)
```csharp
#region [SEC-VIEW-INIT]          : Khởi tạo View, Singleton Instance, Loaded / Unloaded handlers
#region [SEC-VIEW-VM-EVENTS]     : Đăng ký & Hủy sự kiện từ MainViewModel (RegisterVmEvents, OnVmPropertyChanged)
#region [SEC-VIEW-INTERACTION]   : Kéo thả phần tử, Thanh nổi Floating Bar (SetupFloatingBarDraggable)
#region [SEC-VIEW-PAN-ZOOM]      : Thu phóng và di chuyển ảnh mượt mà (SetupComicScreenPanAndZoom)
#region [SEC-VIEW-AUTO-SCROLL]   : Timer tự động cuộn đến truyện đang tải (SetupAutoScrollTimer)
#region [SEC-VIEW-VIRTUAL-THUMB] : Quét và nạp thumbnail ảnh bìa ảo hóa (ScanAndLoadVisibleThumbnails)
#region [SEC-VIEW-HOVER-PREVIEW] : Xem trước ảnh bìa phóng to khi rê chuột (TriggerDelayedQueueHoverPreview)
#region [SEC-VIEW-HOTKEYS]       : Xử lý sự kiện bàn phím toàn cục (KeyDown, KeyUp, Shortcut commands)
```

### 📄 `ComicScraperService.cs` (~350 KB)
```csharp
#region [SEC-SCRAPER-BASE]       : Khởi tạo HttpClient, CookieContainer, FlareSolverr bypass, Proxy
#region [SEC-SCRAPER-DOMAINS]    : Logic bóc tách DOM theo từng domain cụ thể (TruyenQQ, NetTruyen, MangaDex...)
#region [SEC-SCRAPER-CHAPTER]    : Lấy danh sách chương, link từng trang ảnh, giải mã link ảnh bảo mật
```

### 📄 `ComicReaderControl.axaml.cs` & `ComicReaderViewModel.cs` (~370 KB tổng cộng)
```csharp
#region [SEC-READER-VIEW]        : Điều khiển giao diện hiển thị ảnh đọc truyện (Webtoon / Dual page)
#region [SEC-READER-NAVIGATION]  : Chuyển trang, nhảy chap, phím mũi tên / cuộn chuột
#region [SEC-READER-PROGRESS]    : Ghi nhận và lưu lịch sử đọc vào ReadProgressStore (tránh mất trang khi đóng app)
```

---

## 📌 4. QUY TẮC BẢO TRÌ VÀ CẬP NHẬT MENU NÀY

1. **Khi thêm tính năng mới**: Bắt buộc phải gắn thẻ neo `[SEC-...]` tương ứng vào code và bổ sung 1 dòng mô tả vào file `MainWindow.Menu.md` này.
2. **Tuyệt đối KHÔNG ghi số dòng cố định**: Chỉ dùng mã Section Tag duy nhất để không bị sai lệch khi code mở rộng.
3. **Quy chuẩn song ngữ**: Khi thay đổi nhãn hoặc text hiển thị, bắt buộc đối chiếu với `MainWindow.LanguageEng-VI.md`.
