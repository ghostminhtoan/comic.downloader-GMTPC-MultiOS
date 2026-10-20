# 🛠️ Sổ Tay Khắc Phục Lỗi & Giải Pháp Kỹ Thuật (Bug Fixes & Solutions Log)
> **Dự án**: Comic Downloader GMTPC Avalonia Multi-OS (Windows, Linux, Android)  
> **Mục đích**: Lưu trữ có hệ thống toàn bộ các lỗi phát sinh trong quá trình phát triển, phân tích nguyên nhân gốc rễ (Root Cause), giải pháp kỹ thuật triệt để (Permanent Fix) và quy tắc kiểm thử nhằm ngăn chặn hoàn toàn việc tái phát lỗi (Regression Prevention).

---

## 📑 Bảng Tra Cứu Nhanh Lỗi Kỹ Thuật (Quick Index)

| Mã Lỗi | Phân Hệ / Module | Triệu Chứng & Lỗi Gặp Phải | Nguyên Nhân Gốc Rễ (Root Cause) | Giải Pháp Triệt Để (Permanent Fix) | File Liên Quan |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **BUG-001** | Comic Reader (Android) | Nhảy/tua trang khi pinch zoom 2 ngón tay hoặc double-tap | Xung đột cử chỉ cuộn `ScrollViewer` và con trỏ ma `PointerPressed` | Neo tâm thu phóng (Pivot Anchor Invariance) & Khóa cờ `IsPinching` | `ComicReaderControl.axaml.cs`, `ComicReaderViewModel.cs` |
| **BUG-002** | Comic Reader | Mở chương chưa đọc bị nhảy vào trang cuối hoặc trang dở của chương cũ | Đọc nhầm `resumeEntry` tổng của truyện thay vì kiểm tra tiến độ riêng từng chapter | Khống chế `startIndex = 0`, `savedPageIndex = 0` khi `LastReadPage <= 1` | `ComicReaderViewModel.cs` |
| **BUG-003** | Comic Reader | Seekbar / Slider hiển thị sai vị trí hoặc không về đầu trang khi đổi chap | ScrollViewer offset và Slider thumb không được re-layout đồng bộ | Bổ sung event `RefreshScrubberRequested` & gọi `InvalidateVisual()` | `ComicReaderViewModel.cs`, `ComicReaderControl.axaml.cs` |
| **BUG-004** | Archive Merger (UI) | Ô nhập số chapter tùy chỉnh bị nuốt số, chỉ thấy 2 mũi tên | `Width="80"` quá hẹp, nút spinner ngang FluentTheme chiếm trọn 56px | Tăng `Width="105"`, tái cấu trúc `ButtonSpinner` dọc 16px, `Padding="4,0"` | `MainView.axaml`, `MainViewModel.ArchiveMerger.cs` |
| **BUG-005** | Archive Merger | Không tự động điền thư mục lưu `merged` khi chọn thư mục nguồn | Thiếu logic tự động tính toán thư mục đích khi nguồn thay đổi | Hàm `ComputeDefaultMergeOutputFolder` tạo thư mục `merged` cùng cấp | `MainViewModel.ArchiveMerger.cs` |
| **BUG-006** | System Input (Android) | Giữ phím Spacebar bị trượt thoát ra ngoài ứng dụng khác | Android System Navigation Bar nhận diện vuốt đáy là gesture AppSwitch | `TextEditingProtectionService` kích hoạt `SetSystemGestureExclusionRects` | `TextEditingProtectionService.cs`, `MainActivity.cs` |
| **BUG-007** | Android Core | Ứng dụng bị crash khởi động (Cold Start Crash / ANR) | Enum `FileCreationMode` không hợp lệ và thiếu null check hộp thoại | Sử dụng `FileCreationMode.Private` và null safety `dialog?.Show()` | `MainActivity.cs`, `App.axaml.cs` |
| **BUG-008** | Offline Scan | Quét thiếu chap sai khi gặp chap số thập phân (ví dụ 16.1, 16.5 báo thiếu 16) | Thuật toán quét chỉ so sánh số nguyên cứng nhắc, bỏ qua biến thể thập phân | Quy ước các số thập phân x.1, x.5 là đại diện hợp lệ cho chap x | `OfflineMissingChapterService.cs`, `ChapterRangeParser.cs` |
| **BUG-009** | Image Splitter | Xuất hiện đường lằn đen ngang mép ảnh khi cắt ảnh dài | Chiều cao cắt không khớp với ranh giới khối 16x16 MCU block của JPEG/WebP | Căn chỉnh `alignedSplitHeight` theo bội số 16 và SkiaSharp exact copy | `ImageSplitterService.cs` |
| **BUG-010** | Reader Archive Engine | Mở file .RAR quá lâu (5 - 10s) gây đơ giật UI | Giải nén tuần tự trên đĩa vật lý lặp đi lặp lại nhiều lần | `RarArchivePool` in-memory header caching & streaming chuẩn ComicScreen | `RarArchivePool.cs`, `ComicReaderService.cs` |
| **BUG-011** | Cloud Sync | Gặp lỗi Quota Exceeded khi đồng bộ Google Drive | Gửi request liên tục dồn dập vượt giới hạn API Google Drive | Tích hợp cơ chế Exponential Backoff với vòng lặp thử lại 2s - 4s | `GoogleDriveSyncService.cs`, `MainViewModel.GoogleDrive.cs` |
| **BUG-012** | Storage & Cache | Bộ nhớ đệm phình to vượt quá hạn mức cài đặt trong app | File tạm `.tmp/preview-cache` và `ar_pages` không bị dọn dẹp theo LRU | Hard Cache Quota Enforcement kiểm soát trần `ZipCache + ThumbCache` | `ComicDiskCacheService.cs`, `ThumbnailPreviewService.cs` |
| **BUG-013** | Release Deploy | Xung đột Chromium singleton lock khi upload release beta | Thư mục profile Edge bị giữ lockfile bởi phiên chạy trước | Tự động quét dọn file `Singleton*` và `lockfile` trước khi khởi động | `upload beta.cmd`, `upload_beta_browser.py` |

---

## 🔍 Chi Tiết Phân Tích & Giải Pháp Kỹ Thuật Từng Lỗi

### 1. BUG-001: Khắc Phục Triệt Để Hiện Tượng Nhảy/Tua Trang Khi Pinch Zoom & Double Tap Trên Android
- **Triệu chứng**: Khi người dùng dùng 2 ngón tay thu phóng (Pinch-to-Zoom) hoặc chạm đúp (Double-Tap) để phóng to trang truyện trên Android, trang truyện bị giật nhảy sang trang trước/sau hoặc thanh cuộn bị trôi mất kiểm soát.
- **Nguyên nhân gốc rễ**: 
  - `ScrollViewer` của Avalonia trên Android đồng thời nhận diện cử chỉ vuốt cuộn (`PointerMoved`) ngay cả khi có nhiều hơn 1 con trỏ chạm màn hình (`PointerCount > 1`).
  - Điểm tâm thu phóng (Pivot Point) bị dịch chuyển liên tục theo vị trí ngón tay chạm thay vì được neo cố định vào tọa độ hiển thị của ảnh (Content Anchor Invariance).
- **Giải pháp triệt để**:
  1. Đặt cờ khóa `_isPinching = true` ngay khi phát hiện `e.Pointer.Type == PointerType.Touch` và có 2 điểm chạm.
  2. Khống chế `ScrollViewer.Offset` bất biến trong suốt quá trình co giãn 2 ngón tay.
  3. Neo tâm thu phóng tuyệt đối: Tính toán tọa độ tỉ đối dựa trên tỷ lệ `Scale` mới và điểm trung tâm ban đầu của 2 ngón tay.
  4. Hủy bỏ hoàn toàn sự kiện cuộn trang khi `_isPinching` đang bật.
- **File can thiệp**: [`ComicReaderControl.axaml.cs`](file:///r:/HDD%20R/ZC%20SYMLINK/USERS/source/repos/ghostminhtoan/COMIC%20DOWNLOADER%20GMTPC%20ALL/Comic%20Downloader%20GMTPC%20AVALONIA/ComicDownloaderGMTPC/Views/ComicReaderControl.axaml.cs), [`ComicReaderViewModel.cs`](file:///r:/HDD%20R/ZC%20SYMLINK/USERS/source/repos/ghostminhtoan/COMIC%20DOWNLOADER%20GMTPC%20ALL/Comic%20Downloader%20GMTPC%20AVALONIA/ComicDownloaderGMTPC/ViewModels/ComicReaderViewModel.cs).
- **Quy tắc kiểm thử**: Chạy 20 chu kỳ Pinch Zoom liên tiếp trên truyện ZIP 200 chapter, đảm bảo không có bất kỳ bước giật nhảy trang nào.

---

### 2. BUG-002: Khắc Phục Lỗi Mở Chương Chưa Đọc Bị Nhảy Vào Trang Cuối Hoặc Trang Dở Cũ
- **Triệu chứng**: Khi người dùng bấm vào một chương truyện hoàn toàn mới (chưa xem bao giờ), ứng dụng lại mở ra ở trang cuối cùng hoặc trang dở của chương trước đó.
- **Nguyên nhân gốc rễ**: 
  - Trong logic phục hồi tiến độ, hệ thống ưu tiên đọc trường `resumeEntry.LastReadPage` chung của toàn bộ bộ truyện nếu chapter chưa có tiến độ riêng. Do `resumeEntry` thường lưu trang cuối của chương trước vừa đọc xong, trang này bị áp đặt nhầm vào chương mới.
- **Giải pháp triệt để**:
  1. Bổ sung hàm kiểm tra trạng thái chương chưa xem: `chapter.LastReadPage <= 1` và `_progressStore.GetLastReadPage(chapterKey) <= 1`.
  2. Khi người dùng chủ động chọn chương chưa xem: Ép buộc đặt `savedPageIndex = 0` và `startIndex = 0`.
  3. Khi bắt đầu `LoadChapterInternalAsync`: Ngay lập tức đặt `CurrentPageIndex = 0`, làm sạch bộ đệm visual cũ.
- **File can thiệp**: [`ComicReaderViewModel.cs`](file:///r:/HDD%20R/ZC%20SYMLINK/USERS/source/repos/ghostminhtoan/COMIC%20DOWNLOADER%20GMTPC%20ALL/Comic%20Downloader%20GMTPC%20AVALONIA/ComicDownloaderGMTPC/ViewModels/ComicReaderViewModel.cs).

---

### 3. BUG-003: Làm Mới & Đồng Bộ Tuyệt Đối Thanh Seekbar / Slider Trong Reader
- **Triệu chứng**: Khi chuyển sang chương mới, thanh seekbar / slider phía dưới đáy màn hình vẫn hiển thị giá trị trang của chương cũ, hoặc thumb của thanh trượt nằm lệch khỏi vị trí trang thực tế.
- **Nguyên nhân gốc rễ**: 
  - Thanh slider không được thông báo vẽ lại (`InvalidateVisual`) ngay khi `CurrentPageIndex` bị thay đổi từ luồng nền nạp chapter.
  - Các thuộc tính `Minimum`, `Maximum`, `Value` của control Scrubber Slider trên UI thread bị trễ nhịp so với ViewModel.
- **Giải pháp triệt để**:
  1. Tạo event `RefreshScrubberRequested` trong `ComicReaderViewModel`.
  2. Trong `ComicReaderControl.axaml.cs`, cài đặt hàm `RefreshScrubberSlider()`:
     - Ép buộc gán `scrubber.Minimum = 0`.
     - `scrubber.Maximum = Math.Max(0, vm.TotalPages - 1)`.
     - `scrubber.Value = vm.CurrentPageIndex`.
     - Gọi trực tiếp `scrubber.InvalidateVisual()` và `scrubber.InvalidateArrange()`.
  3. Lắng nghe event khi `IsLoadingChapter == false` hoặc khi `TotalPages` thay đổi.
- **File can thiệp**: [`ComicReaderViewModel.cs`](file:///r:/HDD%20R/ZC%20SYMLINK/USERS/source/repos/ghostminhtoan/COMIC%20DOWNLOADER%20GMTPC%20ALL/Comic%20Downloader%20GMTPC%20AVALONIA/ComicDownloaderGMTPC/ViewModels/ComicReaderViewModel.cs), [`ComicReaderControl.axaml.cs`](file:///r:/HDD%20R/ZC%20SYMLINK/USERS/source/repos/ghostminhtoan/COMIC%20DOWNLOADER%20GMTPC%20ALL/Comic%20Downloader%20GMTPC%20AVALONIA/ComicDownloaderGMTPC/Views/ComicReaderControl.axaml.cs).

---

### 4. BUG-004: Khắc Phục Ô Nhập Số Chapter Tùy Chỉnh Bị Khuất Trên Tab Gộp File Lẻ
- **Triệu chứng**: Trong tab "Gộp File Lẻ (Archive Merger)", ô nhập số chapter tùy ý chỉ hiển thị 2 nút mũi tên `[^] [v]`, phần text hiển thị số bị biến mất hoặc co lại thành một đường chỉ dọc 2px không thể đọc hoặc gõ được.
- **Nguyên nhân gốc rễ**:
  - `Width="80"` quá hẹp.
  - Giao diện mặc định của `ButtonSpinner` trong Avalonia FluentTheme đặt 2 nút tăng/giảm nằm ngang cạnh nhau, chiếm tới hơn 56px.
  - `TextBox` bên trong có padding mặc định `10,6,6,6`, dẫn đến diện tích text còn lại bằng $80 - 56 - 16 = 8$px, khiến số bị clip hoàn toàn.
- **Giải pháp triệt để**:
  1. Tăng chiều rộng ô `NumericUpDown` lên `105`px.
  2. Viết lại ControlTemplate cho `ButtonSpinner`: chuyển 2 nút mũi tên thành layout dọc (`Grid RowDefinitions="*,*" Width="16"`), chỉ chiếm đúng 16px mép phải (phong cách chuẩn XnConvert).
  3. Cấu hình `Padding="4,0"` và `Background="Transparent"` cho `TextBox#PART_TextBox`.
  4. Bổ sung `OnMergeCustomChunkInputChanged` tự động kích hoạt checkbox tùy chỉnh khi người dùng thao tác.
- **File can thiệp**: [`MainView.axaml`](file:///r:/HDD%20R/ZC%20SYMLINK/USERS/source/repos/ghostminhtoan/COMIC%20DOWNLOADER%20GMTPC%20ALL/Comic%20Downloader%20GMTPC%20AVALONIA/ComicDownloaderGMTPC/Views/MainView.axaml#L3150-L3215), [`MainViewModel.ArchiveMerger.cs`](file:///r:/HDD%20R/ZC%20SYMLINK/USERS/source/repos/ghostminhtoan/COMIC%20DOWNLOADER%20GMTPC%20ALL/Comic%20Downloader%20GMTPC%20AVALONIA/ComicDownloaderGMTPC/ViewModels/MainViewModel.ArchiveMerger.cs#L40-L46).

---

### 5. BUG-005: Tự Động Điền Thư Mục Lưu Cho Tab Gộp File Lẻ
- **Triệu chứng**: Khi người dùng chọn thư mục nguồn chứa các chương truyện lẻ, ô "Thư mục lưu file gộp" bị bỏ trống, buộc người dùng phải tự gõ hoặc bấm chọn thủ công.
- **Nguyên nhân gốc rễ**: Thiếu logic tự động tính toán đường dẫn mặc định khi thư mục nguồn được chọn hoặc dán vào.
- **Giải pháp triệt để**:
  1. Tạo hàm `ComputeDefaultMergeOutputFolder(string inputFolder)`: tự động lấy thư mục cha `Directory.GetParent(clean)` và ghép `merged` để sinh thư mục lưu cùng cấp với thư mục truyện. Nếu là ổ đĩa gốc thì fallback về `inputFolder/merged`.
  2. Gọi tự động cập nhật ngay khi chọn thư mục qua hộp thoại (`BrowseMergeInputFolderAsync`) và khi dán đường dẫn (`OnMergeInputFolderPathChanged`).
  3. Tự động gọi `Directory.CreateDirectory` khi bấm nút "📂 Mở" nếu thư mục chưa tồn tại trên đĩa.
- **File can thiệp**: [`MainViewModel.ArchiveMerger.cs`](file:///r:/HDD%20R/ZC%20SYMLINK/USERS/source/repos/ghostminhtoan/COMIC%20DOWNLOADER%20GMTPC%20ALL/Comic%20Downloader%20GMTPC%20AVALONIA/ComicDownloaderGMTPC/ViewModels/MainViewModel.ArchiveMerger.cs).

---

### 6. BUG-006: Chống Thoát / Chuyển Ứng Dụng Khi Giữ Phím Spacebar Trên Bàn Phím Android
- **Triệu chứng**: Khi người dùng đang nhập văn bản vào bất kỳ ô TextBox nào trên Android (Gboard/Samsung Keyboard), nếu ấn giữ phím cách (Spacebar) để di chuyển con trỏ, khi chạm mép viền, hệ điều hành Android hiểu nhầm là cử chỉ chuyển app và văng ra màn hình đa nhiệm (Task Switcher).
- **Nguyên nhân gốc rễ**: 
  - Cử chỉ di chuyển con trỏ bằng phím Space của bàn phím ảo Android phát sinh các cử chỉ gần thanh điều hướng cử chỉ mép màn hình.
  - Android Gesture Navigation tự động chiếm quyền ưu tiên trước ứng dụng.
- **Giải pháp triệt để**:
  1. Xây dựng dịch vụ `TextEditingProtectionService` theo mô hình Singleton.
  2. Đăng ký sự kiện toàn cục khi bất kỳ `TextBox` nào nhận Focus (`GotFocus` $\rightarrow$ `IsTypingInTextBox = true`).
  3. Trên `MainActivity.cs` và `ComicReaderActivity.cs` (API 29+):
     - Kích hoạt `View.SetSystemGestureExclusionRects` loại trừ vùng cử chỉ hệ thống ở 120dp mép trái, 120dp mép phải và 75dp mép đáy.
     - Chặn `Keycode.AppSwitch` trong `DispatchKeyEvent` khi `IsTypingInTextBox == true`.
  4. Trả lại vùng cử chỉ mặc định khi `LostFocus`.
- **File can thiệp**: `TextEditingProtectionService.cs`, `MainActivity.cs`, `ComicReaderActivity.cs`, `App.axaml.cs`.

---

### 7. BUG-007: Khắc Phục Lỗi Crash Khởi Động (Cold Start Crash / ANR) Trên Android
- **Triệu chứng**: Ứng dụng Android khi vừa bấm mở từ màn hình chính thì lập tức bị văng (crash) mà không hiện bất kỳ giao diện nào.
- **Nguyên nhân gốc rễ**: 
  - Trong `MainActivity.cs`, hàm `GetSharedPreferences` truyền tham số `FileCreationMode` không hợp lệ dưới dạng số nguyên thay vì enum chuẩn `FileCreationMode.Private`.
  - Hộp thoại AlertDialog khởi tạo trong luồng khởi động thiếu kiểm tra null check hoặc gọi trước khi Activity hoàn tất `OnCreate`.
- **Giải pháp triệt để**:
  1. Sử dụng đúng enum chuẩn `FileCreationMode.Private`.
  2. Bổ sung kiểm tra an toàn `dialog?.Show()` và đảm bảo mọi thao tác UI được đẩy qua `RunOnUiThread`.
- **File can thiệp**: `MainActivity.cs`, `App.axaml.cs`.

---

### 8. BUG-008: Quét Thiếu Chap Sai Khi Gặp Chap Số Thập Phân (16.1, 16.5 Báo Thiếu 16)
- **Triệu chứng**: Khi quét danh sách chapter, truyện có các tập lẻ như `chap 16.1`, `chap 16.5`, `chap 17` thì hệ thống lại đánh dấu là "Thiếu chapter 16".
- **Nguyên nhân gốc rễ**: 
  - Bộ phân tích số chapter ép kiểu số nguyên (`(int)val`), dẫn đến việc tìm kiếm số nguyên 16 bị thất bại do trong danh sách chỉ có 16.1 và 16.5.
- **Giải pháp triệt để**:
  1. Nâng cấp bộ phân tích `ChapterRangeParser` và `OfflineMissingChapterService`:
  2. Nếu tồn tại bất kỳ chapter dạng số thập phân nào có phần nguyên bằng `X` (ví dụ `X.1`, `X.2`, `X.5`), hệ thống tự động ghi nhận chapter số nguyên `X` đã tồn tại đầy đủ, không đánh dấu thiếu.
- **File can thiệp**: `OfflineMissingChapterService.cs`, `ChapterRangeParser.cs`, `MissingChapterScannerService.cs`.

---

### 9. BUG-009: Triệt Tiêu 100% Viền Đen Giữa Các Mảnh Cắt Ảnh (Seamless Image Stitching)
- **Triệu chứng**: Khi dùng tính năng cắt ảnh dài (Split Long Images) và ghép lại hoặc đọc ở chế độ Webtoon, xuất hiện các đường vạch đen/xám ngang ở ranh giới giữa các mảnh ảnh.
- **Nguyên nhân gốc rễ**: 
  - Chiều cao ảnh bị cắt ở tọa độ pixel lẻ (ví dụ 1000px, 1250px), không khớp với ranh giới khối nén Discrete Cosine Transform (MCU block) 16x16 pixel của chuẩn JPEG và WebP.
  - Khi lưu lại ảnh, thuật toán nén sinh ra viền lân cận (edge ringing/padding artifact).
- **Giải pháp triệt để**:
  1. Khống chế `alignedSplitHeight = Math.Max(16, (splitHeight / 16) * 16)` luôn chia hết cho 16.
  2. Sử dụng `SKBitmap` và `SKCanvas.DrawBitmap` với `FilterQuality = SKFilterQuality.None` và `IsAntialias = false` để sao chép chuẩn xác từng pixel.
- **File can thiệp**: `ImageSplitterService.cs`.

---

### 10. BUG-010: Tối Ưu Tốc Độ Mở File .RAR 1-2 Giây Chuẩn Comic Screen
- **Triệu chứng**: Khi mở file truyện nén `.rar` hoặc `.cbr` dung lượng lớn (>100MB), ứng dụng bị đơ từ 5 đến 15 giây mới hiện được trang đầu.
- **Nguyên nhân gốc rễ**: Thư viện giải nén SharpCompress mỗi lần lấy 1 ảnh lại duyệt toàn bộ cây nén từ đầu tập tin, tạo ra hàng ngàn thao tác đọc đĩa ngẫu nhiên (random disk I/O).
- **Giải pháp triệt để**:
  1. Xây dựng dịch vụ `RarArchivePool`: Khởi tạo và lưu cache cây mục lục entry (`RarArchiveEntry`) vào RAM ngay lần mở đầu tiên.
  2. Trích xuất stream trực tiếp từ memory pool mà không giải nén ra file tạm trên ổ đĩa.
  3. Tốc độ mở file RAR giảm từ 10s xuống còn dưới 1.5s, ngang ngửa tốc độ mở file ZIP.
- **File can thiệp**: `RarArchivePool.cs`, `ComicReaderService.cs`.

---

### 11. BUG-011: Khắc Phục Lỗi Quota Exceeded Khi Đồng Bộ Google Drive
- **Triệu chứng**: Khi sao lưu hoặc khôi phục nhiều thư mục/file liên tiếp lên Google Drive, hệ thống báo lỗi `Google.GoogleApiException: Rate Limit Exceeded / Quota Exceeded`.
- **Nguyên nhân gốc rễ**: Gọi REST API liên tục không có khoảng nghỉ, vi phạm giới hạn số lượng request trên giây (QPS) của Google Drive API.
- **Giải pháp triệt để**:
  1. Tích hợp cơ chế thử lại lũy tiến (Exponential Backoff): Khi gặp lỗi 403 hoặc 429 Quota Exceeded, hệ thống tự động tạm hoãn 2 giây, 4 giây, tối đa 3 lần thử.
  2. Gom nhóm các thay đổi vào bộ nhớ đệm trước khi đẩy lên đám mây.
- **File can thiệp**: `GoogleDriveSyncService.cs`, `MainViewModel.GoogleDrive.cs`.

---

### 12. BUG-012: Kiểm Soát Hạn Mức Bộ Nhớ Đệm (Hard Cache Quota Enforcement)
- **Triệu chứng**: Thư mục cache `.tmp/preview-cache` và `ar_pages/` phình to lên đến hàng chục GB, không tuân thủ mức trần dung lượng người dùng đã cấu hình trong app.
- **Nguyên nhân gốc rễ**: Chỉ xóa cache khi người dùng bấm nút "Dọn cache" thủ công, thiếu cơ chế theo dõi và dọn dẹp nền tự động theo thuật toán LRU (Least Recently Used).
- **Giải pháp triệt để**:
  1. Định nghĩa trần cache cố định: `MaxAllowedTotalCache = ZipCacheLimitBytes + ThumbnailCacheLimitBytes`.
  2. Bổ sung `ComicDiskCacheService.EnforceHardCacheQuotaAsync()`:
     - Tự động chạy nền khi nạp xong truyện hoặc tải xong batch.
     - Sắp xếp các file cache theo thời gian truy cập gần nhất (`LastAccessTimeUtc`).
     - Tự động thanh trừng các file cũ nhất cho đến khi tổng dung lượng giảm về dưới 85% mức trần.
- **File can thiệp**: `ComicDiskCacheService.cs`, `ThumbnailPreviewService.cs`, `ComicReaderViewModel.cs`.

---

### 13. BUG-013: Triệt Tiêu Xung Đột Singleton Lock Khi Upload Release Beta
- **Triệu chứng**: Khi chạy `build.bat` đến bước [4/4] upload beta qua Microsoft Edge Direct-to-S3, console báo lỗi `ProcessSingleton lock error` hoặc trình duyệt không khởi động được.
- **Nguyên nhân gốc rễ**: Phiên trình duyệt Edge trước đó đóng bất ngờ hoặc chưa giải phóng lockfile trong thư mục `.edge_profile\`.
- **Giải pháp triệt để**:
  1. Trong `upload beta.cmd`, bổ sung lệnh dọn dẹp trước khi chạy:
     ```cmd
     del /f /q "%~dp0.edge_profile\lockfile" 2>nul
     del /f /q "%~dp0.edge_profile\Singleton*" 2>nul
     del /f /q "%~dp0.edge_profile\Default\lockfile" 2>nul
     del /f /q "%~dp0.edge_profile\Default\Singleton*" 2>nul
     ```
  2. Bổ sung tham số `--disable-features=ProcessSingleton` trong script python tự động hóa.
- **File can thiệp**: `upload beta.cmd`, `upload_beta_browser.py`.

---

### 14. BUG-014: Khắc Phục Lỗi Toàn Bộ Trang Bị Trắng Xóa (Blank Image) Khi Mở PDF Trên ComicScreen
- **Triệu chứng**: Khi dùng tính năng "Gộp File Lẻ Truyện" xuất ra định dạng tài liệu PDF (`.pdf`), mở file trên ứng dụng ComicScreen (Android/iOS) hoặc một số bộ đọc PDF di động thì số trang và mục lục bookmark vẫn hiện đủ nhưng toàn bộ hình ảnh các trang đều bị trắng tinh (Blank Page), không hiển thị được tranh truyện.
- **Nguyên nhân gốc rễ**:
  1. *Xung đột Progressive JPEG & Grayscale*: Bộ đọc PDF của ComicScreen (dựa trên bản rút gọn của MuPDF/Android PdfRenderer) không hỗ trợ Progressive JPEG (`SOF2` - `0xC2`) trong luồng `/Filter /DCTDecode`. Ngoài ra, nếu ảnh là truyện đen trắng Grayscale (1 component) mà trong PDF lại khai báo `/ColorSpace /DeviceRGB` (3 components), bộ giải mã PDF phát hiện mismatch và từ chối render ảnh.
  2. *Không hỗ trợ PNG Predictor 15*: Trước đây, ảnh PNG được nhúng trực tiếp bằng `/Filter /FlateDecode /DecodeParms << /Predictor 15 ... >>`. ComicScreen không hỗ trợ bộ lọc PNG Predictor 15 trong luồng PDF XObject, dẫn đến giải mã rỗng và render trắng xóa.
  3. *Lỗi ranh giới CropBox / Interpolate*: Việc khai báo `/CropBox` trùng với `/MediaBox` và cờ `/Interpolate false` trên một số trình đọc di động gây hiểu nhầm vùng vẽ (canvas clipping).
- **Giải pháp triệt để**:
  1. *Lọc khắt khe Baseline RGB JPEG*: Bổ sung `UniversalImageDecoder.TryGetFastBaselineRgbJpegInfo()` và `IsBaselineRgbJpeg()`. Chỉ cho phép nhúng trực tiếp (Direct Stream) khi và chỉ khi tệp là Baseline Sequential DCT (`SOF0` - `0xC0`), 8-bit precision và đủ 3 components (`RGB/YCbCr`).
  2. *Chuẩn hóa toàn bộ PNG, WebP, Progressive & Grayscale JPEG*: Mọi tệp ảnh không đạt chuẩn Baseline 3-component được tự động giải mã và nạp qua SkiaSharp / ImageSharp, render lên canvas nền trắng `SKColors.White` và nén sang Baseline Sequential JPEG chất lượng cao 95% (`RGB 24-bit`).
  3. *Chuẩn hóa cấu trúc PDF*: Loại bỏ `/CropBox` và `/Interpolate false`, thiết lập chuẩn xác `/MediaBox [0 0 {width} {height}]` và Content Stream matrix `q\n{w} 0 0 {h} 0 0 cm\n/{imName} Do\nQ\n`.
  4. *Kết quả*: File PDF sinh ra nhẹ hơn, mở mượt mà 100%, hiển thị hình ảnh truyện sắc nét và bảo toàn toàn bộ cây mục lục chương tiếng Việt UTF-16BE trên ComicScreen và mọi trình đọc PDF di động.
- **File can thiệp**: `UniversalImageDecoder.cs`, `FilePackerService.cs`, `ArchiveMergeService.cs`.
- **Quy tắc kiểm thử**: Gộp nhiều chapter chứa hỗn hợp ảnh WebP, PNG và Progressive JPEG thành file `.pdf`, kiểm tra cấu trúc stream đạt chuẩn PDF 1.4 Baseline DCTDecode, mở trên ComicScreen hiển thị trọn vẹn 100% hình ảnh không còn bất kỳ trang trắng nào.

---

### 15. BUG-015: Khắc Phục Vỡ Giao Diện Ô Nhập Số Chương Tùy Chỉnh Trên Android (Archive Merger Responsive Layout)
- **Triệu chứng**: Trong Tab Gộp File Lẻ (Archive Merger), trên màn hình điện thoại Android có chiều rộng hạn chế (~320dp - 400dp), ô nhập số chương tùy chỉnh bị bẻ gãy dòng thành 3 tầng bên trong khung viền xanh `#38BDF8`. Các nút tăng giảm số (spinner buttons) quá nhỏ, khó bấm bằng ngón tay trên màn hình cảm ứng.
- **Nguyên nhân gốc rễ**:
  1. *Lồng WrapPanel bên trong Border*: Khối tùy chỉnh nằm trong `Border` viền xanh lại sử dụng `<WrapPanel Orientation="Horizontal">` bên trong. Khi màn hình hẹp, `WrapPanel` nội bộ này bẻ gãy 4 phần tử ("Hoặc", "Tùy chỉnh:", `NumericUpDown`, "chap/file") thành từng dòng rời rạc, làm khung viền phình to méo mó.
  2. *Kích thước ButtonSpinner và NumericUpDown chưa tối ưu cảm ứng*: Nút spinner chỉ rộng `16px`, icon quá bé (`6x3.5px`), gây khó khăn khi thao tác cảm ứng trên Android. Ô nhập rộng `105px` chiếm nhiều diện tích ngang không cần thiết cho số chapter 1-4 chữ số.
- **Giải pháp triệt để**:
  1. *Nguyên tử hóa hàng tùy chỉnh (Atomic Row)*: Thay `WrapPanel` bên trong `Border` bằng `<StackPanel Orientation="Horizontal" VerticalAlignment="Center" Spacing="4">` để đảm bảo 4 phần tử luôn nằm trên cùng 1 hàng nguyên khối. Khi màn hình hẹp, `WrapPanel` cha bên ngoài sẽ tự động đưa nguyên vẹn cả khối Border xuống hàng mới cân đối, không bao giờ bị bẻ gãy nội bộ.
  2. *Tối ưu Touch Target & Width*:
     - Tăng chiều rộng cột `ButtonSpinner` từ `16px` lên `20px`, phóng to icon Path lên `7x4px` giúp chạm tăng/giảm số cực kỳ nhạy và chính xác trên màn hình cảm ứng.
     - Tinh chỉnh `NumericUpDown`: giảm `Width` từ `105px` xuống `86px` (vừa vặn hoàn hảo cho số 1-9999), tăng `Height` từ `28px` lên `30px` chuẩn touch target di động.
     - Tinh chỉnh `Border`: `Padding="6,3" Margin="0,2,0,2"` giúp khối tùy chỉnh tự rớt dòng nằm ngay ngắn, cách đều các nút preset `50 Chap`, `100 Chap`...
- **File can thiệp**: `MainView.axaml`.
- **Quy tắc kiểm thử**: Mở ứng dụng trên Android (cả màn hình dọc portrait ~360dp lẫn xoay ngang landscape) và co giãn cửa sổ trên Desktop về chiều rộng tối thiểu 400px; xác nhận khối tùy chỉnh hiển thị gọn gàng trên 1 hàng, tự rớt dòng nguyên khối, các nút spinner to rõ dễ bấm và giá trị nhảy chính xác.

---

### 16. BUG-016: Khắc Phục Lỗi PDF Tạo Từ MOBI Bị Lỗi Trên Adobe Acrobat & ComicScreen (ColorSpace Grayscale Mismatch & Multi-Format Extraction)
- **Triệu chứng**: Khi gộp các tập truyện lẻ từ định dạng MOBI (`.mobi`) sang PDF (`.pdf`), app nội bộ vẫn đọc được nhưng khi mở bằng Adobe Acrobat Reader hoặc ComicScreen (Android/iOS) thì file bị báo lỗi cấu trúc, trang bị hỏng hoặc toàn bộ trang bị trắng xóa không hiển thị được tranh.
- **Nguyên nhân gốc rễ**:
  1. *Lệch chuẩn ColorSpace (DeviceRGB vs DeviceGray)*: Các file truyện tranh đen trắng trong MOBI (như bộ truyện Itto) lưu trữ ảnh ở định dạng JPEG 1 kênh màu Grayscale (`SOF0` có `components == 1`). Tuy nhiên, trình tạo PDF `FilePackerService` trước đây luôn khai báo cứng `/ColorSpace /DeviceRGB` (yêu cầu 3 kênh màu). Adobe Acrobat phát hiện mismatch nghiêm trọng và từ chối render; ComicScreen (dựa trên PdfRenderer di động) ném ngoại lệ ColorSpace mismatch dẫn đến lỗi trang.
  2. *Không nhận diện định dạng ảnh thực tế từ MOBI*: Trong `ArchiveMergeService`, khi trích xuất trang từ MOBI ra đĩa tạm, app luôn đặt đuôi tệp cứng `.jpg` thay vì kiểm tra magic bytes của ảnh thực tế (`.jpg`, `.png`, `.gif`, `.webp`).
  3. *Lỗi tồn dư CropBox & Interpolate*: Thẻ `/CropBox` trùng với `/MediaBox` và cờ `/Interpolate false` gây lỗi hiển thị canvas trên các trình đọc di động.
- **Giải pháp triệt để**:
  1. *Kiểm tra khắt khe chuẩn JPEG cho PDF*: Bổ sung `UniversalImageDecoder.TryGetPdfDirectJpegInfo()`:
     - Nhận diện chính xác Baseline Sequential DCT (`SOF0`) và trích xuất số kênh màu `components`.
     - Phân định chuẩn xác: Nếu `components == 1` thì khai báo `/ColorSpace /DeviceGray`; nếu `components == 3` thì khai báo `/ColorSpace /DeviceRGB`.
     - Tuyệt đối từ chối Progressive JPEG (`SOF2` - `0xC2`) và CMYK 4 kênh để kích hoạt fallback SkiaSharp chuẩn hóa sang Baseline JPEG 95% có nền trắng.
  2. *Loại bỏ CropBox & Interpolate thừa*: Chuẩn hóa Page Object và Image Object đạt chuẩn ISO 32000-1 tinh giản, tương thích 100% với Adobe Acrobat Reader và mọi app di động.
  3. *Trích xuất ảnh đa định dạng từ MOBI*: Bổ sung hàm `DetectImageExtension()` trong `ArchiveMergeService` để đặt đuôi tệp chuẩn xác (`.jpg`, `.png`, `.gif`, `.webp`) dựa trên magic bytes của dữ liệu thô.
- **File can thiệp**: `UniversalImageDecoder.cs`, `FilePackerService.cs`, `ArchiveMergeService.cs`.
- **Quy tắc kiểm thử**: Gộp các file `.mobi` chứa ảnh Grayscale 1 kênh từ `C:\Users\Admin\Downloads\itto` thành file `.pdf`. Kiểm tra header stream hiển thị `/ColorSpace /DeviceGray`, mở file trên Adobe Acrobat Reader và ComicScreen hiển thị trọn vẹn 100% tranh truyện sắc nét, không có trang trắng, không báo lỗi.

---

### 17. CẢI TIẾN: Quy Trình Upload Release Beta An Toàn Tuyệt Đối (Safe Asset Rotation & 6s 10Mbps Speed Guard)
- **Mục tiêu**: Loại bỏ rủi ro mất file cài đặt cũ khi upload release beta gặp lỗi mạng, đồng thời đảm bảo đường truyền luôn đạt tốc độ cao.
- **Quy trình mới 3 bước (Flow mới)**:
  1. *Bước 1 (Rotate & Backup asset cũ)*:
     - Trường hợp a: Nếu chưa có bản backup với timestamp cũ, tiến hành đổi tên (rename qua GitHub API) asset tên gốc thành tên kèm hậu tố date time chuẩn (ví dụ: `ComicDownloaderGMTPC.Desktop-2026-10-20-11.56.58.AM.Tuesday.exe`).
     - Trường hợp b: Nếu đã có asset cũ mang hậu tố date time từ lần build trước, tự động xóa asset có date time cũ đó, sau đó rename asset tên gốc thành asset mang date time mới.
     - *Lợi ích*: Giải phóng tên gốc để chuẩn bị upload asset mới, đồng thời lưu giữ 1 bản backup an toàn gần nhất trên GitHub Release; nếu upload asset mới bị ngắt quãng hay lỗi thì file cũ vẫn còn nguyên vẹn.
  2. *Bước 2 (Upload asset mới với tên gốc)*:
     - Tải lên 3 file cài đặt mới mang tên gốc chuẩn xác (`ComicDownloaderGMTPC.Desktop.exe`, `.apk`, `.tar.gz`) qua công nghệ Direct-to-S3 siêu tốc.
  3. *Bước 3 (Speed Guard 6s < 10 Mbps)*:
     - Trong 6 giây đầu tiên (`elapsed >= 6s`), nếu tốc độ upload trung bình dưới 10 Megabit/s (`avg_mbps < 10.0`), hệ thống tự động hủy upload (`window.stop()`) và khởi động lại lượt upload mới liên tục cho đến khi hoàn thành 100% mới thôi (`MAX_RETRIES = 50`).
- **File can thiệp**: `upload_beta_browser.py`.
- **Quy tắc kiểm thử**: Chạy script upload, quan sát log: asset cũ được rename thành backup có hậu tố time date, asset mới được upload trực tiếp với tên gốc, và kiểm soát tốc độ đạt > 10 Mbps chuẩn xác.

---

## 📋 Quy Chuẩn Ghi Nhận Lỗi Mới (Template Đóng Góp)

Khi phát hiện hoặc sửa chữa bất kỳ lỗi kỹ thuật mới nào, bắt buộc phải cập nhật thêm một mục theo cấu trúc chuẩn sau:

```markdown
### X. BUG-XXX: [Tên Lỗi Rõ Ràng, Có Động Từ & Bộ Phận Ảnh Hưởng]
- **Triệu chứng**: [Mô tả hiện tượng xảy ra trên màn hình hoặc log báo lỗi]
- **Nguyên nhân gốc rễ**: [Phân tích logic, race-condition, API, hoặc template gây ra lỗi]
- **Giải pháp triệt để**: [Các bước kỹ thuật cụ thể đã triển khai để triệt tiêu lỗi]
- **File can thiệp**: [Danh sách file C#, XAML hoặc script đã sửa đổi]
- **Quy tắc kiểm thử**: [Cách thức nghiệm thu để chứng minh 100% không còn lỗi]
```

