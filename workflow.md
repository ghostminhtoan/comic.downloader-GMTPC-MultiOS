# Workflow hiện tại - Comic Downloader GMTPC

Chuẩn làm việc repo hiện tại. Mục tiêu: sửa đúng chỗ, ít file, build sạch, không phá lane khác.

## 1. Quy tắc nền
- Trả lời tiếng Việt.
- Dùng emotion phong phú, không lặp lại.
- Dùng skill `ponyman` khi code, kể cả không tag skill.
- Sửa tối thiểu, không thêm abstraction thừa.
- Chuẩn UTF-8, cấm lỗi mobijake.
- WPF/UI qua `Dispatcher` từ luồng nền.
- Giữ `async/await`, tôn trọng `CancellationToken`.
- Lỗi lẻ download/scan không sập app; log crash `.tmp\crash`, đánh dấu lỗi, tiếp tục batch an toàn.
- Build sạch `0 error, 0 warning`.
- Build xong: tự động commit local, chỉ push GitHub khi user yêu cầu; trả mã hash commit local.
- UI song ngữ: check ENG/VI, file gốc duy nhất là `languages.md` (phân vùng theo Section).
- Không revert thay đổi của user nếu không yêu cầu.
- Xong việc: đánh giá, gợi ý file cần sửa/kiểm tra, rà soát EN/VI.
- Luôn luôn test build 2 bước: bước 1 là tự tải khởi tạo thư viện, bước 2 là app mở lên, có UI hoàn tất thì mới xem như là build thành công.
- Luôn xuất 3 dòng trạng thái chuẩn ở cuối câu trả lời:
  - `commit local: <mã hash>`
  - `commit github: "không"`
  - `path exe: <đường dẫn exe>`
- Luôn đánh giá, cập nhật `workflow.md`.
- Đánh giá prompt, gợi ý tính năng/file thiết kế mới; cập nhật workflow.md/prompt.md khi cần.

## 2. Snapshot kiến trúc hiện tại

### Nền tảng
- WPF .NET Framework `4.8`.
- Project: `Comic-GMTPC.csproj`.
- Chạy portable:
  - root download: `PortablePaths.DefaultDownloadRoot`
  - data: `.portable`
  - temp: `.tmp`
  - autosave: `save gallery.md`
- Single-instance:
  - cùng folder: chặn mở nhiều instance
  - khác folder: mở song song
  - logic tại `App.xaml.cs`, mutex theo `PortablePaths.AppRoot`

### Dependency đang dùng
- `Microsoft.Web.WebView2`
- `Selenium.WebDriver`
- `System.Data.SQLite.Core`
- Firecrawl qua API / CLI fallback tại `MainWindow.SystemFirecrawl.cs`

## 3. Bản đồ file quan trọng

> File partial class được tổ chức vào thư mục `Partial\<Group>\` trong project root.
> `MainWindow.xaml` và `MainWindow.xaml.cs` giữ nguyên ở root (WPF yêu cầu XAML + code-behind cùng folder).

### Khởi động và portable
- `App.xaml.cs`: bootstrap runtime, single-instance theo folder, long path, hardware acceleration.
- `PortablePaths.cs`: toàn bộ path chuẩn app portable.
- `PortableRuntimeBootstrap.cs`
- `PortableArchiveBootstrap.cs`

### Main window
- `MainWindow.xaml`: layout chính, toggle ENG/VI, combo `Single comic` / `Multi-comic`, toggle download/retry/copy/focus/global key.
- `MainWindow.xaml.cs`: constructor, nối partial.
- `Partial\System\MainWindow.SystemProgress-Preview.cs`: hover preview queue + duplicate names, badge `preview`, delay `500ms`, cache ảnh, prefetch bitmap.
- Extracted gallery list có 2 mode:
  - `details list`: `dgResults`
  - `details list` có toggle `Compact row` / `Dòng gọn` (mặc định bật xem nhiều hàng; tắt để row tự cao xem link/progress phụ).
  - Compact row: hiện mini progress bar cột status khi tải/pause; ẩn text % để giữ row thấp.
  - `thumbnail list`: 7 cột (9 cột khi compact), tile hẹp ảnh đứng, dùng chung `_scrapedItems`, selection/keyboard/context menu/drag như `dgResults`.
  - `Dòng gọn` bật ở `thumbnail list`: 9 cột, fit chiều cao tile thấy đúng 2 hàng; metadata phụ ẩn, hover/popup xem đủ chi tiết.
  - Thumbnail preview: cache `.tmp\preview-cache`, lưu file gốc đúng đuôi, sinh `.thumb.jpg` nhỏ cho grid.
  - Thumbnail list: auto tải thumbnail nhỏ hiện thẳng grid, không phụ thuộc hover.
  - Nút `popup preview`: giữa `details list` và `thumbnail list`, chỉ bật/tắt popup hover phóng to cho `details list`, `thumbnail list`, `duplicate names`; thumbnail grid auto load độc lập nút này.
  - Bật/tắt `popup preview`: `thumbnail list` tự refresh, auto load lại thumbnail ngay, không cần hover kích hoạt.
  - Thumbnail item: focus/selected highlight rõ ràng khi click hoặc chọn phím.
  - Checkbox thumbnail list: ô vuông không kèm chữ, đặt góc trên ảnh thumbnail.
  - Chọn thumbnail chuột/phím: viền/nền đổi màu vàng rõ ràng.
  - Title thumbnail list: đủ cao hiện 3 dòng.
  - Popup preview hover: hiện ảnh, title; nếu có scan chap thiếu thì hiện `latest chapter: <chapter mới nhất>`.
  - Popup preview: hiện trạng thái `missing integer chapter: complete/thiếu chapter số nguyên`.
  - Auto scan missing integer chapter: chạy theo `_scrapedItems` chung (cả `details list` và `thumbnail list`); reset/bulk add tự trigger scan item chưa quét.
  - Tab `Scan missing integer chapter` / `Scan chap số nguyên thiếu`: combo `multiple check`/`check song song` từ 1-16 (mặc định 8), điều khiển số scan task song song thật.
  - Scan missing integer chapter: tự chạy sau import/get link/load list khi có item mới, không chờ download.
  - Row sync tab scan: tạo row trống tự kick scan ngay.
  - Scan lấy thứ tự list truyện hiện tại, từ book trên cùng; không lấy thứ tự từ grid scan/cached row cũ.
  - List truyện đổi khi đang scan: hủy scan cũ, tự scan list mới; task cũ không ghi đè list mới.
  - Scan/rescan không phụ thuộc checkbox book (checkbox chỉ để chọn/copy/toggle).
  - Right click tab scan: copy book link, copy missing integer chapter, copy decimal chapter; Ctrl+C chỉ copy link truyện.
  - Mọi domain scan missing integer chapter phải tôn trọng `multiple check`; cấm semaphore/lock toàn domain gây single check.
  - `nettruyen.tech`/`nettruyenviet10.com`: WebView `Xem thêm` mở ngầm song song theo số scan task; không khóa WatchMore WebView còn 1 cửa.
  - WatchMore WebView: đóng, dispose sau khi lấy HTML/cookie; giảm `multiple check` không mở vượt limit mới.
  - `nettruyen.tech`/`nettruyenviet10.com`: ưu tiên API/AJAX chapter list; chỉ mở WebView `Xem thêm` khi AJAX thiếu/thất bại.
  - `nettruyen.tech`/`nettruyenviet10.com`: nguồn chapter đầy đủ ưu tiên `/Comic/Services/ComicService.asmx/ChapterList?slug=<book-slug>` (API của nút `Xem thêm`, tránh hụt chap đầu như `thuong-hoang-tro-ve`).
  - Scan thiếu chap số nguyên 1-3: tự quét lại tối đa 3 lần trước khi lưu.
  - Mọi domain: label chap `số:số`, `số-số`, `số - số` tính là range phủ đủ các số trong khoảng, không báo thiếu số trong range.
  - Domain trả chapter label riêng với link (ví dụ Nettruyen API `chapter_name`): cache `ReaderChapterItem.Name` giữ label thật, không tự build lại từ link làm mất range như `Chapter 58: 59`.
  - Cột missing integer chapter: word wrap.
  - Progress scan song song: hiện số hoàn thành thật và số hàng vừa xong; cột `#` có checkbox + số thứ tự hàng.
  - Scan missing integer chapter: phân biệt truyện bằng link/domain; cùng link chia task chỉ quét 1 lần.
  - Nhiều domain cùng tên truyện: token missing integer chapter trùng giữa các domain tô màu vàng; chỉ thiếu ở 1 domain tô màu trắng.
  - Cột chap thập phân có toggle `WRAP`: tắt để tránh hàng cao (màu đỏ, không wrap); bật màu xanh (chỉ cột chap thập phân xuống dòng).
  - Cấm giữ bitmap RAM toàn cục cho preview.

### `Partial\System\` — System/UI
- `MainWindow.SystemBootstrap.cs`: init app, hotkey global/project, clipboard auto paste, http client/cookie state.
- `MainWindow.SourceSearch.cs`:
  - tab `Search` cạnh `Password`
  - ô `Search book` + combo checkbox domain style `CyberpunkComboBox`
  - nút `Search` mở Google: `https://www.google.com/search?q=site:<domain không http/https>+<tên truyện encode>`
  - domain search lấy theo home/redirect thật của tab (ví dụ `truyenqq` -> `truyenqqko.com`).
- `MainWindow.SystemActions.cs`
- `MainWindow.SystemBuild.cs`
- `MainWindow.SystemUpdate.cs`
- `MainWindow.SystemFolders.cs`
- `MainWindow.SystemExplorer.cs`
- `MainWindow.SystemComboBox.cs`
- `MainWindow.SystemMessageBox.cs`
- `MainWindow.SystemFloatingControlWindow.cs`
- `MainWindow.Login.cs`
- `MainWindow.Logs.cs`
- `MainWindow.RestoreCompat.cs`
- `MainWindow.SystemFirecrawl.cs`
- `MainWindow.SystemWebviewCpu.cs`

### `Partial\UI\` — UI
- `MainWindow.WorkspaceLayout.cs`
- `MainWindow.Theme.cs`
- `MainWindow.UIBootstrap.cs`
- `MainWindow.UIResponsive.cs`
- `MainWindow.UIResultsGrid.cs`
- `MainWindow.UILogs.cs`
- `MainWindow.UIEnglish.cs`
- `MainWindow.UIVietnamese.cs`
- `MainWindow.UIFold.cs`
- `MainWindow.UINewFeatures.cs`
- `MainWindow.UIExtensions.cs`

### `Partial\Download\` — Download
- `MainWindow.Download.cs`: flow tải chính, path/file naming, pause/resume/stop state.
- `MainWindow.DownloadPipeline.cs`: profile theo domain, retry/rate-limit/browser session, manifest tải trang.
- `MainWindow.DownloadState.cs`: state/cancel/token/phối hợp queue.
- `MainWindow.PostDownload.cs`
- `MainWindow.singlemulticomic.cs`:
  - nguồn sự thật mode folder type
  - `GetDownloadChapterFolderName()` chỉ trả tên folder chapter
  - caller tự ghép book/chapter cho multi-comic.

### `Partial\Tabs\` — Routing và scraper theo domain
- `MainWindow.TabRouting.cs`: nhận URL, chọn lane, điều hướng tab, import direct link.
- `MainWindow.Tab*.cs`: partial riêng từng domain.
- `MainWindow.LightNovelDesk.cs`
- `MainWindow.Reader.cs`
- `MainWindow.TabWatch.cs`

### `Partial\Captcha\` — Captcha / Anti-bot
- `MainWindow.SystemCaptcha.cs`
- `MainWindow.CaptchaGeneral.cs`
- `MainWindow.CaptchaSpecial.cs`
- `MainWindow.Captchawatchmore.cs`

### `Partial\PreviewTag\` — Preview tag theo domain
- `MainWindow.previewtagTruyenqq.cs`
- `MainWindow.previewtagNettruyenviet10.cs`
- `MainWindow.previewtagThuviensach.cs`

### Window phụ (root)
- `CaptchaWindow.xaml.cs`
- `DuplicateWindow.xaml.cs`
- `DirectDownloadWindow.xaml.cs`
- `BookmarkHistoryWindow.xaml.cs`
- `ErrorLogWindow.xaml.cs`
- `ErrorReportWindow.xaml.cs`

### Standalone helpers (root)
- `HakoChapterCaptureWindow.cs`
- `ChapterRangeParser.cs`


## 4. Domain đang support

### Manga
- `truyenqq`:
  - preview cover ưu tiên `div.book_avatar img`
  - giữ nguyên query string URL ảnh (`.jpg?...`), không cắt sau `?`
  - `book_avatar` có `src` và `data-ni` hoặc nhiều host ảnh: thử tuần tự từng URL, không fail ngay ở URL đầu.
- `mangadex.org`:
  - ưu tiên route `tag / title / chapter`.
  - dùng API chính chủ lấy chapter list, cover preview, ảnh chapter.
  - MangaDex là truyện tranh (manga), cấm gắn hậu tố `[MD-...]` hay sinh file `.md` vào tên truyện hoặc thư mục.
  - Dùng WebView2 fetcher ngầm xử lý API MangaDex để vượt qua lỗi SSL/TLS Schannel của Windows. Fallback OpenGraph HTML khi URL thiếu slug.
  - Tải ảnh qua `mangadex.network` hỗ trợ đa luồng curl và HttpClient song song, có WebView2 fallback an toàn.
- `nettruyen`
- `nettruyen.tech`:
  - preview cover từ `div.col-image img` (`.jpg`, `.png`, `.webp`); hover book hiện badge trắng `preview` khi tích hợp thành công.
  - download folder/process tách riêng `nettruyen.tech`, không chung `nettruyen` hay `nettruyenviet10.com`.
- `nettruyenviet10.com`:
  - download folder/process tách riêng `nettruyenviet10.com`, không chung `nettruyen` hay `nettruyen.tech`.
  - AJAX `ProcessChapterList`/`GetListChapter` trả đủ list: gán lại `chapterLinks` bằng kết quả AJAX, không chỉ đổi HTML trung gian.
- `dilib.vn / thuviensach.vn`:
  - book slug chứa số: nhận dạng book từ chapter URL chỉ cắt sau marker chapter (`-chap-...` hoặc `/chuong...`), không xóa số cuối book slug.
  - book URL hợp lệ gồm cả `/{book-slug}.html` và `/truyen-tranh/{book-slug}`; `/truyen-tranh/{book-slug}` không được route nhầm thành category.
  - scan missing integer chapter: tên `ReaderChapterItem` lấy từ chapter URL/label (`chap 469`), không dùng title chứa tên book (tránh lỗi parse số từ book như `7 Viên...` thành chapter 7).

### Hentai / ảnh
- `daomeoden`
- `damconuong.shop`
- `vi-hentai`
- `truyengg` / `sayhentai`
- `hentaiforce`
- `hentai2read`
- `hentaiera`
- `e-hentai.org` / `exhentai.org`

### Light novel
- `hako.vn`
- `hako.re`
- `docln.net`

Nguồn sự thật routing: `MainWindow.TabRouting.cs`.

## 5. Folder type hiện tại
- `Single comic`: `root\book name\chapter name\page files`
- `Multi-comic`:
  - tạo thư mục theo book, tên chapter/path lấy qua flow chung trong downloader
  - sửa folder logic: sửa toàn bộ flow gọi `GetDownloadChapterFolderName()`, không sửa riêng từng domain.
- Combo UI: `MainWindow.xaml`
- State/event: `MainWindow.singlemulticomic.cs`
- Float window sync lại folder type khi đổi mode.

## 6. Quy tắc queue/download
- Chỉ dừng đúng item/book thao tác; không rơi book khác.
- Untick checkbox:
  - status về `Stopped`
  - request đang chạy honor cancel token
  - tick lại mới tải tiếp.
- `Completed` chỉ set khi book tải xong thật (không set do clear queue/error).
- Không remove book giữa chừng khi chưa hoàn tất flow.
- Resume tôn trọng file có sẵn và manifest trong `.tmp\.manifest`.
- Ảnh nhỏ/hỏng phải retry, không tính xong.
- Profile throttle/retry domain nằm tại `MainWindow.DownloadPipeline.cs`.

## 7. Browser session / captcha / anti-bot
- Challenge: qua WebView2 session hoặc Chrome fallback.
- Cookie + user-agent bơm lại vào `_httpClient`.
- Lane captcha chính (`Partial\Captcha\`): `MainWindow.SystemCaptcha.cs`, `MainWindow.CaptchaGeneral.cs`, `MainWindow.CaptchaSpecial.cs`, `MainWindow.Captchawatchmore.cs`.
- Focus off: không ép minimize main window sau captcha/webview nếu flow không yêu cầu.

## 8. Hotkey / toggle / UI state
- `Ctrl+Shift+F`: bật/tắt float button.
- `Alt+Shift+G`: bật/tắt global hotkey mode.
- Toggle download/retry/copy/focus/global key phản ánh đúng state thật.
- Khi sửa toggle: check click UI, hotkey, sync floating control, ENG/VI.

## 9. Cách chọn file trước khi sửa

### Nếu bug thuộc domain
1. `Partial\Tabs\MainWindow.TabRouting.cs` xem route tab nào.
2. Mở `Partial\Tabs\MainWindow.Tab<Domain>.cs`.
3. Lỗi tải file/chapter/path, đọc thêm:
   - `Partial\Download\MainWindow.Download.cs`
   - `Partial\Download\MainWindow.DownloadPipeline.cs`
   - `Partial\Download\MainWindow.singlemulticomic.cs`

### Nếu bug thuộc queue, checkbox, stop/resume
1. `Partial\Download\MainWindow.DownloadState.cs`
2. `Partial\Download\MainWindow.Download.cs`
3. Cần thiết mới đọc `Partial\Download\MainWindow.DownloadPipeline.cs`.

### Nếu bug thuộc toggle/layout/ngôn ngữ
1. `MainWindow.xaml`
2. partial UI tương ứng (`Partial\UI\` — `UI*`, `Theme`, `WorkspaceLayout`; `Partial\System\` — `SystemFloatingControlWindow`)
3. `MainWindow.ENG-VI.md`

### Nếu bug hoặc feature thuộc hover preview ở extracted gallery list
1. `MainWindow.xaml`
2. `Partial\System\MainWindow.SystemProgress-Preview.cs`
3. partial UI/window gắn host hover (`Partial\UI\MainWindow.UIResultsGrid.cs`, `DuplicateWindow.xaml`, `DuplicateWindow.xaml.cs`)
4. partial domain cấp dữ liệu preview (`Partial\Tabs\MainWindow.Tab*.cs`)

### Nếu bug hoặc feature thuộc thumbnail list của extracted gallery links
1. `MainWindow.xaml`
2. `Partial\UI\MainWindow.UIResultsGrid.cs`
3. `Partial\System\MainWindow.SystemProgress-Preview.cs`
4. partial domain cấp `HoverPreviewThumbnailUrl` hoặc data preview (`Partial\Tabs\MainWindow.Tab*.cs`)

### Nếu bug thuộc app portable / startup / multi-instance
1. `App.xaml.cs`
2. `PortablePaths.cs`
3. `PortableRuntimeBootstrap.cs`
4. `PortableArchiveBootstrap.cs`


## 10. Workflow sửa đúng
1. Đọc `workflow.md`.
2. Xác định lane (startup/portable, system/ui, queue/download, domain scraper, novel/reader/watch).
3. Tìm đúng partial/file nguồn sự thật.
4. Sửa ít file nhất.
5. Đụng text UI: cập nhật `languages.md` đúng section tương ứng.
6. Build `.\build.bat`.
7. Còn error/warning: sửa tiếp đến sạch `0 error, 0 warning`.
8. Kiểm tra `BuildInfo.cs` (auto stamp khi build release).
9. Thực hiện quy trình kiểm thử nghiệm thu 2 bước (Mục 11).
10. Tự động commit local, chỉ push GitHub khi người dùng yêu cầu.
11. Báo cáo 3 dòng trạng thái chuẩn.

## 11. Quy trình kiểm thử nghiệm thu (Verification & Acceptance Testing)

### 11.1. Kiểm thử biên dịch (Build Verification)
- **Lệnh thực thi**: `.\build.bat` từ thư mục gốc dự án.
- **Tiêu chuẩn nghiệm thu**:
  - Biên dịch Release sạch sẽ đạt đúng `0 Warning(s), 0 Error(s)`.
  - Tự động cập nhật timestamp trong `BuildInfo.cs`.
  - Copy đầy đủ các dependency native/managed và tài nguyên (`languages.md`, `regedit`, `tutorials`, `runtimes`, v.v.) vào `bin\Release\` và `release\Comic-GMTPC\`.

### 11.2. Kiểm thử 2 bước khởi chạy (Two-Step Startup Acceptance)
Quy tắc bắt buộc: App phải vượt qua cả 2 bước khởi động mới được xem là hoàn thành:
1. **Bước 1 — Tự tải khởi tạo thư viện (Portable Runtime Initialization)**:
   - Khi thư mục `bin\Release` vừa được dọn dẹp hoặc thiếu runtime trong `bin\`:
   - Hàm `NeedsInitialization()` nhận diện thiếu thư viện/ringtones.
   - Ứng dụng tự động hiển thị cửa sổ tải tiến trình `DownloadProgressWindow`, hoàn tất giải nén/tải các runtime cần thiết (`WebView2Loader.dll`, `SQLite.Interop.dll`, `WebDriver.dll`, `ringtones`, v.v.).
   - Khi tải xong, cửa sổ đóng lại và tiến trình Bước 1 kết thúc an toàn.
2. **Bước 2 — Khởi chạy giao diện chính (UI Launch & Responding Acceptance)**:
   - Khi chạy ứng dụng lần thứ hai (thư viện đã có sẵn đầy đủ):
   - `NeedsInitialization()` trả về `false`, ứng dụng đi thẳng vào luồng chính `MainWindow()`.
   - Nạp thành công toàn bộ bảng ngôn ngữ từ `languages.md` vào bộ từ điển RAM.
   - Cửa sổ giao diện chính xuất hiện trên màn hình, không bị treo cứng (hang/freeze), không kẹt vòng lặp vô hạn hay deadlock.
   - Tiến trình `Comic-GMTPC` đạt trạng thái phản hồi: `Responding == True`.
   - Tiêu đề cửa sổ phản ánh đúng ngôn ngữ đã chọn: `Comic-GMTPC v1.0 - Tiếng Việt` (hoặc English).

### 11.3. Kiểm thử tính toàn vẹn ngôn ngữ (i18n & Data Integrity)
- File nguồn sự thật duy nhất là [languages.md](file:///r:/HDD%20R/ZC%20SYMLINK/USERS/source/repos/ghostminhtoan/Comic%20Downloader%20GMTPC/languages.md).
- Toàn bộ các chuỗi song ngữ phải được phân vùng rõ ràng theo từng Section Markdown (`## <Tên Section>`), mỗi Section có bảng dịch độc lập.
- Cột 1 là English (mốc/key), cột 2 là Vietnamese.
- Tuyệt đối không để sót ký tự xuống dòng thô (raw newline) làm vỡ cấu trúc bảng Markdown (phải escape thành `\n`).
- Bộ nạp `LanguageHelper` phải parse trơn tru mọi bảng con lặp lại header, nạp từ điển RAM trong thời gian cực ngắn (< 15ms) và an toàn tuyệt đối trước ký tự mojibake marker (`\uFFFD`).

### 11.4. Quy tắc dọn dẹp & Báo cáo nghiệm thu
- Trước khi commit, phải xóa sạch các file tạm sinh ra trong quá trình kiểm thử (như `.tmp\*.ps1`, `.tmp\*.cs`, `.tmp\*.exe`, log tạm).
- Tự động commit local bằng Conventional Commit, **tuyệt đối KHÔNG push GitHub** trừ khi người dùng có lệnh rõ ràng.
- Kết thúc câu trả lời luôn luôn xuất 3 dòng trạng thái:
  ```
  commit local: <mã hash>
  commit github: "không"
  path exe: <đường dẫn exe>
  ```

## 12. Quy tắc build/release
- Luôn dùng `.\build.bat`.
- Script: kill `Comic-GMTPC.exe`, rebuild Release MSBuild, auto stamp `BuildInfo.cs`, publish `release\Comic-GMTPC`, auto mở exe mới.
- `BuildInfo.cs` đổi sau build release, đưa vào commit cuối cùng.
- Không chấp nhận warning mới.

## 13. Quy tắc git
- Commit theo thay đổi thật, scope nhỏ.
- Không kéo file test tạm, dump, html debug, log rác vào commit.
- Branch mặc định: `main`.
- Chỉ commit local; khi có yêu cầu push GitHub mới push `origin main`.

## 14. Ghi nhớ thực chiến
- Nhiều bug nằm ở state sync giữa: queue item, checkbox, toggle UI, cancellation token, folder type.
- Lỗi "status đúng nhưng hành vi sai": ưu tiên đọc flow state/cancel trước khi sửa parser.
- Lỗi "đúng domain này nhưng sai mọi domain khác": ưu tiên đọc flow chung thay vì vá từng tab.
- Cẩn trọng với vòng lặp xử lý chuỗi: luôn kiểm tra độ dài chuỗi marker (`marker.Length == 0`) để tránh rơi vào vòng lặp vô hạn gây đóng băng UI.

