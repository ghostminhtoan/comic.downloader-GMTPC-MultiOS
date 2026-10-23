# Workflow hiện tại - Comic Downloader GMTPC Avalonia Multi-OS
Chuẩn làm việc repo Avalonia hiện tại. Mục tiêu: sửa đúng chỗ, ít file, build sạch, không phá lane khác, sau khi build luôn luôn upload beta.

## 1. Quy tắc nền
- **QUY TẮC BẮT BUỘC SỐ 1 - TỰ ĐỘNG UPLOAD BETA DÙ USER KHÔNG NHẮC**: Bất kể người dùng có nhắc đến từ khóa "upload beta" hay không, sau khi viết code hoặc sửa bug xong, BẮT BUỘC PHẢI LUÔN LUÔN chạy `build.bat` đóng gói toàn diện cả 3 hệ điều hành (Windows, Linux, Android) và TỰ ĐỘNG UPLOAD RELEASE BETA lên GitHub Release tag `beta` (áp dụng flow mới: rotate backup asset cũ kèm date time + upload asset mới + speed guard sau 12s dưới 10Mbps thì retry vô hạn). Tuyệt đối không được bỏ qua hoặc đợi user nhắc!
- **QUY TẮC BẮT BUỘC: LUÔN HIỂN THỊ CỬA SỔ COMMAND PROMPT KHI BUILD & UPDATE BETA**: Việc build và update beta từ nay về sau BẮT BUỘC LUÔN LUÔN hiển thị trong cửa sổ Command Prompt riêng biệt trên Desktop (`build_in_cmd.py` / `CREATE_NEW_CONSOLE`) để người dùng luôn luôn nhìn thấy rõ ràng tiến độ đang làm đến đâu (từng bước build [1/4] -> [4/4] và tiến trình upload từng giây).
- Trả lời tiếng Việt.
- Dùng emotion phong phú, không lặp lại.
- Dùng skill [SKILL.md](codeContext;file:///c%3A/Users/Admin/source/repos/ghostminhtoan/subtitle%20edit%20nikse/.agents/skills/teamwork-preview-protocol/SKILL.md)[SKILL.md](codeContext;file:///c%3A/Users/Admin/source/repos/ghostminhtoan/subtitle%20edit%20nikse/.agents/skills/prompt-optimizer/SKILL.md)

 khi code, kể cả không tag skill.
- Sửa tối thiểu, không thêm abstraction thừa.
- Chuẩn UTF-8, cấm lỗi mobijake.
- Avalonia UI qua `Dispatcher.UIThread` từ luồng nền.
- Giữ `async/await`, tôn trọng `CancellationToken`.
- Lỗi lẻ download/scan không sập app; log crash `.tmp\crash`, đánh dấu lỗi, tiếp tục batch an toàn.
- Build sạch `0 error, 0 warning`.
- Build xong: tự động commit local, chỉ push GitHub khi user yêu cầu; trả mã hash commit local.
- UI song ngữ: check ENG/VI, file gốc duy nhất là `languages.md` (phân vùng theo Section).
- Không revert thay đổi của user nếu không yêu cầu.
- Xong việc: đánh giá, gợi ý file cần sửa/kiểm tra, rà soát EN/VI.
- Luôn luôn test build 2 bước: bước 1 là tự tải khởi tạo thư viện, bước 2 là app mở lên, có UI hoàn tất thì mới xem như là build thành công.
- **Quy tắc Bắt Buộc Tự Động Upload Release Beta**: Bắt buộc luôn luôn upload release beta sau khi build/đóng gói xong để cập nhật 3 gói cài đặt (`.apk`, `.exe`, `.tar.gz`) lên GitHub Release tag `beta`: `https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/tag/beta`. Quy trình tự động đã được tích hợp trong bước [4/4] của `build.bat`, hoặc chạy trực tiếp lệnh `upload beta.cmd --no-pause` qua công nghệ Microsoft Edge Direct-to-S3 siêu tốc.
- **Quy tắc Build & Thư mục Publish**:
  - Script build chính thức: `build.bat` (đã tích hợp đóng gói Windows, Linux, Android và tự động upload beta; hỗ trợ cờ `--no-upload` để chỉ build local). Luôn luôn nhớ build toàn bộ hệ điều hành (Windows, Linux, Android) và tự động upload release beta sau khi hoàn tất.
  - Khi build / publish file cho toàn bộ các hệ điều hành (Windows, Linux, Android), toàn bộ file standalone `.exe`, binary Linux, `.tar.gz`, `.deb` và `.apk` **bắt buộc luôn luôn ở chung một folder duy nhất `\Comic Downloader GMTPC AVALONIA\publish\`**, tuyệt đối **không được cho vào các subfolder như `windows`, `linux`, `android`**.
- Luôn xuất khối trạng thái chuẩn ở cuối câu trả lời:
  - `commit local: <mã hash>`
  - `commit github: "không"`
  - `path exe: Comic Downloader GMTPC AVALONIA\publish\ComicDownloaderGMTPC.Desktop.exe`
  - `path linux: Comic Downloader GMTPC AVALONIA\publish\ComicDownloaderGMTPC`
  - `path android apk: Comic Downloader GMTPC AVALONIA\publish\com.CompanyName.ComicDownloaderGMTPC-Signed.apk`
- Luôn đánh giá, cập nhật `workflow-avalonia.md`.
- Chuẩn Icon App: Dùng mẫu Concept 06 (Comic Cloud Rocket) đồng bộ trên toàn bộ nền tảng: Avalonia Desktop (`app-icon.ico` & Window Icon), Android (`Icon.png`), Linux (`comic-downloader.desktop` `Icon=Icon`).
- Đánh giá prompt, gợi ý tính năng/file thiết kế mới; cập nhật `workflow-avalonia.md` khi cần.

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
  - `nettruyenviet10.com`: WebView `Xem thêm` mở ngầm song song theo số scan task; không khóa WatchMore WebView còn 1 cửa.
  - WatchMore WebView: đóng, dispose sau khi lấy HTML/cookie; giảm `multiple check` không mở vượt limit mới.
  - `nettruyenviet10.com`: ưu tiên API/AJAX chapter list; chỉ mở WebView `Xem thêm` khi AJAX thiếu/thất bại.
  - `nettruyenviet10.com`: nguồn chapter đầy đủ ưu tiên `/Comic/Services/ComicService.asmx/ChapterList?slug=<book-slug>` (API của nút `Xem thêm`, tránh hụt chap đầu như `thuong-hoang-tro-ve`).
  - Scan thiếu chap số nguyên 1-3: tự quét lại tối đa 3 lần trước khi lưu.
  - Mọi domain: label chap `số:số`, `số-số`, `số - số` tính là range phủ đủ các số trong khoảng, không báo thiếu số trong range.
  - Domain trả chapter label riêng với link (ví dụ Nettruyen API `chapter_name`): cache `ReaderChapterItem.Name` giữ label thật, không tự build lại từ link làm mất range như `Chapter 58: 59`.
  - Cột missing integer chapter: word wrap.
  - Progress scan song song: hiện số hoàn thành thật và số hàng vừa xong; cột `#` có checkbox + số thứ tự hàng.
  - Scan missing integer chapter: phân biệt truyện bằng link/domain; cùng link chia task chỉ quét 1 lần.
  - Scan chap thiếu: ngoại lệ chapter số thập phân mang phần nguyên (như 16.1, 16.5) luôn phủ mốc số nguyên tương ứng (16), không bao giờ báo thiếu oan chap nguyên khi đã có các chap thập phân con.
  - Tab Tool - Gộp File Lẻ Truyện (Archive Merger): hỗ trợ gộp các file lẻ (.pdf, .zip, .cbz, .rar, .cbr, .mobi) thành một hoặc nhiều file lớn.
  - Cấu trúc file gộp: bắt buộc giữ từng folder chapter riêng biệt bên trong archive để đọc thuận tiện, không xả ảnh lung tung ra root.
  - Gom nhóm theo mốc chapter cấu hình (100, 200, 300, tất cả): không tính theo tổng count; các chap thập phân (như 100.1, 100.2, 100.5) đều gom về mốc 100; tên file cuối cùng luôn biến thiên theo chapter cuối thực tế (ví dụ: 201-330 hoặc 201-351).
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
  - Khi lấy link MangaDex, bắt buộc hỏi người dùng chọn Tiếng Việt (`vi`) hoặc Tiếng Anh (`en`) qua hộp thoại Modal.
  - Cào chapter feed theo `offset` với `limit=100` để quét đủ 100% chapters.
    - MangaDex là truyện tranh (manga), cấm gắn hậu tố `[MD-...]` hay sinh file `.md` vào tên truyện hoặc thư mục.
    - Dịch vụ mạng đa tầng `MangaDexNetworkService` kết hợp toàn diện:
      * Tầng 1: Direct HttpClient với DoH (`DoHResolver` giải quyết DNS qua Cloudflare DoH `1.1.1.1` và Google DoH `8.8.8.8`, chống đầu độc DNS).
      * Tầng 2: Native WebView Service (`NativeWebViewService` chạy ngầm headless Edge/Chrome/Chromium trên đa hệ điều hành).
      * Tầng 3: Reverse Proxy Gateways chuyển tiếp an toàn khi ISP chặn SNI/TLS.
      * Tầng 4: Curl fallback hỗ trợ streaming dữ liệu nén.
      * Tự động chẩn đoán và hướng dẫn trạng thái Cloudflare WARP (`CloudflareWarpService`) khi phát hiện nhà mạng chặn.
    - Tải ảnh qua `mangadex.network` hỗ trợ đa luồng curl và HttpClient song song, có Native WebView fallback an toàn.
- `loppytoonn.com`:
  - route `/truyen/<slug>`, chapter `/truyen/<slug>/<chapter-slug>`.
  - book info & chapter list lấy từ `.episode-list a`.
  - ảnh chapter lấy từ `.chapter-content img`, tự động lọc bỏ ảnh watermark credit (`credit.jpg`), logo, icon.
- `nettruyenviet10.com`:
  - download folder/process tách riêng `nettruyenviet10.com`.
  - tự động loại bỏ banner quảng cáo đầu truyện (`nettruyenviet.webp`, `assets/images`).
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


## 15. Tài Liệu Hướng Dẫn, Sổ Tay Sửa Lỗi & Lưu Trữ (Documentation & Logs)
> 🛠️ **Sổ Tay Khắc Phục Lỗi & Giải Pháp Kỹ Thuật**: Tra cứu 13 lỗi kinh điển, nguyên nhân gốc rễ và giải pháp triệt để tại [`bug-fixes-and-solutions.md`](file:///r:/HDD%20R/ZC%20SYMLINK/USERS/source/repos/ghostminhtoan/COMIC%20DOWNLOADER%20GMTPC%20ALL/Comic%20Downloader%20GMTPC%20AVALONIA/bug-fixes-and-solutions.md).
> 📜 **Kho Lưu Trữ Lịch Sử Nâng Cấp**: Tra cứu chi tiết toàn bộ các cập nhật cũ từ mục 15.1 đến mục 28 tại [`workflow-history.md`](file:///r:/HDD%20R/ZC%20SYMLINK/USERS/source/repos/ghostminhtoan/COMIC%20DOWNLOADER%20GMTPC%20ALL/Comic%20Downloader%20GMTPC%20AVALONIA/workflow-history.md).
> Mặc định chỉ duy trì các quy tắc nền cốt lõi và các mục cập nhật gần nhất dưới đây để tối ưu hóa context token.

## 16. Các Cập Nhật Kỹ Thuật Gần Nhất (Recent Updates)

29. **Tối Ưu Responsive WrapPanel Tab Gộp File Lẻ & Chuẩn Hóa Mở Trang Đầu Cho Chương Chưa Xem Trong Reader Kèm Làm Mới Seekbar (`MainView.axaml`, `ComicReaderViewModel.cs`, `ComicReaderControl.axaml.cs`, `MainActivity.cs`)**:
    - **Giao Diện Responsive WrapPanel Tab Gộp File Lẻ (`MainView.axaml`)**:
      + Bọc toàn bộ vùng điều khiển và cấu hình bên trong `ScrollViewer VerticalScrollBarVisibility="Auto"`, cho phép cuộn tự do và hiển thị đầy đủ mọi nút bấm trên các thiết bị màn hình nhỏ hoặc khi bật bàn phím ảo.
      + Tiêu đề thư mục nguồn và thư mục đích được cấu hình `TextWrapping="Wrap"`, cùng với cụm nút "📁 Chọn Thư Mục", "📂 Mở", "🔄 Đổi App Mở" tự động xuống hàng linh hoạt trên Android, triệt tiêu hoàn toàn lỗi bị cắt xén chuỗi `(PDF, ZIP, CB...` và `(Mặc định: thư mục 'merg...`.
      + Tái thiết kế cụm "Số Chapter mỗi file" và khung tùy chỉnh "Hoặc / Tùy chỉnh: [ 50 ^ v ] chap/file" bằng `WrapPanel`: Căn chỉnh nền `#1E293B`, viền sáng `#38BDF8`, chữ số `NumericUpDown` đậm rõ ràng, tách bạch nhãn `chap/file` màu sáng `#CBD5E1`, giúp người dùng dễ nhìn, dễ tương tác và không bị đè cạnh viền.
      + Toàn bộ RadioButton định dạng xuất và ghi chú thông minh đều hỗ trợ ngắt dòng co giãn thích ứng.
    - **Quy Tắc Cốt Lõi: Mở Trang Đầu (Index 0) Cho Chương Chưa Xem (`ComicReaderViewModel.cs`)**:
      + Nhận diện chính xác trạng thái "Chương chưa xem": Khi `chapter.LastReadPage <= 1` (hoặc `!chapter.HasReadProgress`) và trong `_progressStore` không có tiến độ cục bộ riêng cho chapter này (`GetLastReadPage <= 1`).
      + Khi người dùng chủ động chọn chương chưa xem từ danh sách chương (Chapter Drawer / ComboBox / Next Chapter): Bắt buộc gán `savedPageIndex = 0` và `startIndex = 0`, triệt tiêu hoàn toàn việc lấy nhầm tiến độ đọc dở của chương khác từ `resumeEntry` của toàn bộ truyện.
      + Ngay khi bắt đầu nạp chương (`LoadChapterInternalAsync`), lập tức reset `CurrentPageIndex = 0`, làm sạch visual cũ và triệt tiêu quán tính cuộn.
    - **Làm Mới & Đồng Bộ Tuyệt Đối Thanh Seekbar / Slider Trong Reader (`ComicReaderViewModel.cs`, `ComicReaderControl.axaml.cs`)**:
      + Bổ sung event `RefreshScrubberRequested` trong `ComicReaderViewModel` và hàm xử lý `RefreshScrubberSlider()` trong `ComicReaderControl.axaml.cs`.
      + `RefreshScrubberSlider()` đồng bộ cưỡng bức `scrubber.Minimum = 0`, `scrubber.Maximum = Math.Max(0, vm.TotalPages - 1)` và `scrubber.Value = vm.CurrentPageIndex`, gọi `InvalidateVisual()` và `InvalidateArrange()` để Thumb của thanh trượt luôn hiển thị chuẩn xác 100% tại trang 1 ngay khi chuyển sang chương mới.
      + Lắng nghe đồng bộ tự động khi `CurrentPageIndex`, `TotalPages`, `MaxPageIndex` thay đổi hoặc khi hoàn tất nạp chapter (`IsLoadingChapter == false`).
    - **Sửa Lỗi Khởi Tạo AlertDialog & FileCreationMode Trên Android (`MainActivity.cs`)**:
      + Thay thế đúng enum chuẩn `FileCreationMode.Private` cho `GetSharedPreferences`.
      + Bổ sung null check an toàn `dialog?.Show()`, đảm bảo biên dịch đạt chuẩn tuyệt đối 0 Error, 0 Warning trên toàn bộ các nền tảng.

30. **Tự Động Điền Thư Mục Lưu Cho Tab Gộp File Lẻ (Archive Merger) Khi Chọn Thư Mục Nguồn (`MainViewModel.ArchiveMerger.cs`)**:
    - **Thuật Toán Tính Thư Mục Lưu Mặc Định (`ComputeDefaultMergeOutputFolder`)**:
      + Tự động xác định thư mục `merged` cùng cấp với thư mục nguồn chứa các chương truyện lẻ: `Directory.GetParent(inputFolder)/merged`.
      + Nếu thư mục nguồn là ổ đĩa gốc không có thư mục cha (ví dụ `D:\`), tự động fallback sang `inputFolder/merged`.
    - **Tự Động Điền Thư Mục Lưu Ngay Khi Chọn Nguồn (`BrowseMergeInputFolderAsync`)**:
      + Ngay khi người dùng chọn xong thư mục nguồn từ hộp thoại duyệt file, ô `MergeOutputFolderPath` được tự động điền đường dẫn thư mục `merged` chuẩn xác.
    - **Tự Động Cập Nhật Khi Dán Hoặc Nhập Thủ Công (`OnMergeInputFolderPathChanged`)**:
      + Lắng nghe sự kiện thay đổi đường dẫn nguồn: Nếu ô thư mục lưu đang để trống hoặc đang là thư mục `merged` tự sinh trước đó, hệ thống sẽ tự động cập nhật lại đường dẫn `merged` mới tương ứng với thư mục nguồn mới.
    - **Điền Sẵn Thư Mục Mặc Định Khi Khởi Động (`InitArchiveMerger`)**:
      + Khởi tạo sẵn đường dẫn `merged` ngay khi mở ứng dụng, triệt tiêu trạng thái ô nhập bị bỏ trống.
    - **Tự Động Tạo Thư Mục Khi Bấm Mở (`OpenMergeOutputFolder`)**:
      + Kiểm tra và tự động tạo thư mục trên đĩa nếu chưa tồn tại trước khi gọi mở trong trình quản lý tệp của hệ điều hành.


31. **Tối Ưu Kích Thước Ô Nhập Số Chapter Tùy Chỉnh Trong Tab Gộp File Lẻ (`MainView.axaml`, `MainViewModel.ArchiveMerger.cs`)**:
    - **Khắc Phục Lỗi Khuất Số Hiển Thị Trên NumericUpDown**:
      + Tăng chiều rộng ô `NumericUpDown` từ `80` lên `105`, triệt tiêu hoàn toàn hiện tượng 2 nút mũi tên tăng/giảm nuốt trọn diện tích hiển thị của ô số.
      + Tái cấu trúc template `ButtonSpinner` sang layout 2 nút mũi tên dọc (`Grid RowDefinitions="*,*" Width="16"`) chuẩn phong cách XnConvert / Desktop & Mobile, chỉ chiếm đúng 16px mép phải thay vì chiếm tới 56px như giao diện ngang mặc định của FluentTheme.
      + Thiết lập `Padding="4,0"` và `Background="Transparent"` cho `TextBox#PART_TextBox`, giúp số hiển thị to rõ, căn giữa chuẩn xác, màu xanh cyan `#38BDF8` nổi bật, không bị che khuất bởi padding thừa.
    - **Tự Động Kích Hoạt Tùy Chỉnh Khi Nhập Số (`OnMergeCustomChunkInputChanged`)**:
      + Tự động bật `IsMergeCustomChunk = true` ngay khi người dùng gõ số hoặc bấm mũi tên tăng/giảm trên ô nhập số tùy chỉnh, đem lại trải nghiệm mượt mà, tiện lợi.

32. **Tái Cấu Trúc File Quy Tắc & Lưu Trữ Lịch Sử (Workflow History Archive & Context Token Optimization)**:
    - **Tách Lịch Sử Cũ Sang `workflow-history.md`**: Lưu trữ toàn bộ 3.100+ dòng nhật ký từ mục 15.1 đến 28 vào [`workflow-history.md`](file:///r:/HDD%20R/ZC%20SYMLINK/USERS/source/repos/ghostminhtoan/COMIC%20DOWNLOADER%20GMTPC%20ALL/Comic%20Downloader%20GMTPC%20AVALONIA/workflow-history.md) (475 KB).
    - **Tối Ưu 93.6% Dung Lượng `workflow-avalonia.md`**: Giảm từ 507 KB (>3.550 dòng, ~90.000 tokens) xuống còn ~33 KB (~440 dòng, ~5.000 tokens), giữ trọn vẹn 100% quy tắc nền (Mục 1-14) và các bản cập nhật gần nhất.
    - **Tiết Kiệm Token Đột Phá**: Giúp AI đọc hiểu quy tắc nhanh gấp nhiều lần và tiết kiệm hơn 85.000 tokens mỗi lần nạp context.


33. **Bổ Sung File Summary Toàn Diện Cho Mã Nguồn & Tạo Sổ Tay Khắc Phục Lỗi (`bug-fixes-and-solutions.md`)**:
    - **Chuẩn Hóa File Summary Đầu Mỗi File (21 Files Trọng Tâm)**: Đã bổ sung khối chú thích chuẩn hóa ở đầu 14 ViewModel (`ComicReaderViewModel.cs`, `LibraryViewModel.cs`, `MainViewModel.*.cs`...) và 7 Services cốt lõi (`ArchiveMergeService.cs`, `ComicDiskCacheService.cs`, `ComicReaderService.cs`...), định nghĩa rõ: Vai trò & Mục đích, Các tính năng & Thành phần chính, Lưu ý kỹ thuật & Quy tắc bảo trì.
    - **Sổ Tay Kỹ Thuật Khắc Phục Lỗi (`bug-fixes-and-solutions.md`)**: Lập tài liệu chuyên sâu ghi nhận 13 mã lỗi lớn (BUG-001 đến BUG-013) kèm bảng tra cứu nhanh (Quick Index), phân tích nguyên nhân gốc rễ, giải pháp triệt để, file can thiệp và quy tắc kiểm thử phòng ngừa tái phát.

34. **Tích Hợp Chế Độ Nối Chap Vào Tệp Đóng Gói Có Sẵn (Append Chapters to Existing Archive Mode) Cho Tab Gộp File Lẻ (ArchiveMergeService.cs, MainViewModel.ArchiveMerger.cs, MainView.axaml, LanguageService.cs, bug-fixes-and-solutions.md)**:
    - **Ý Tưởng & Giải Pháp Kỹ Thuật (BUG-021)**: Cho phép người dùng đã có tệp truyện tổng hợp trước đó (.pdf, .cbz, .zip, .mobi) nối tiếp các chapter mới tải về mà không làm mất cấu trúc thư mục chương hay hệ thống Bookmarks mục lục ban đầu.
    - **Core Service (ArchiveMergeService.AppendChaptersToFileAsync)**:
      + **PDF (.pdf)**: Bóc tách ảnh trang cũ, lưu giữ toàn bộ Bookmarks cũ (pdfMeta.Bookmarks), nạp tiếp các chapter mới theo Natural Sort và tự động tạo Bookmarks nối tiếp chuẩn trang.
      + **CBZ / ZIP (.cbz, .zip)**: Giải nén cấu trúc chapter cũ ra thư mục tạm, nạp thêm các folder chapter mới, bảo toàn 100% cấu trúc thư mục con riêng biệt và nén lại thành file đóng gói chuẩn.
      + **MOBI (.mobi)**: Tái tạo luồng ảnh cũ qua MobiComicParser và kết hợp các chương mới qua MobiWriterService.
      + **Atomic Safe Overwrite**: Tự động tạo bản sao lưu .bak trước khi ghi đè, bảo vệ tệp gốc tuyệt đối chống hỏng hóc nếu quá trình ghi bị ngắt quãng.
    - **ViewModel & UI Reactive**: Thiết kế thanh chuyển đổi 2 chế độ (Mode Selector: Gộp Hàng Loạt vs Nối Chap Vào File), quy trình 3 bước trực quan (Chọn tệp gốc ➔ Chọn folder chapter mới ➔ Tùy chọn ghi đè & bảo tồn Bookmarks), hiển thị thông số phân tích dung lượng/số trang/số chap theo thời gian thực và đồng bộ tiến trình % lên Bubble/Notification.


35. **Khắc Phục Lỗi Bubble Trên Windows & Linux, Xây Dựng Cửa Sổ Bong Bóng Nổi Độc Lập Chuẩn Android (`FloatingBubbleWindow.axaml`, `FloatingBubbleWindow.axaml.cs`, `MainWindow.axaml.cs`, `bug-fixes-and-solutions.md`)**:
    - **Triệt Tiêu Lỗi Co Rúm Cửa Sổ Chính (BUG-022)**: Loại bỏ hoàn toàn mã ép kích thước MainWindow thành 380x240 px gây vỡ nát giao diện; tự động thu nhỏ MainWindow khi bật Bong bóng để giải phóng không gian làm việc.
    - **Cửa Sổ Bong Bóng Nổi Độc Lập (`FloatingBubbleWindow`)**:
      + Cửa sổ tròn 76x76 px, không viền (`WindowDecorations="None"`), nền trong suốt, luôn nổi trên cùng (`Topmost="True"`), không chiếm chỗ taskbar.
      + **Circular Progress Ring**: Tự vẽ bằng Avalonia `StreamGeometry` và `DrawingContext`, góc quét -90 độ theo chiều kim đồng hồ, nét bo tròn `PenLineCap.Round`, đổi màu theo tác vụ (Download: Cyber Cyan, Enhancer: Purple Neon, Packer: Amber Cam, Splitter: Rose Hồng, Folder: Emerald Xanh, Merger: Sky Blue).
      + **Badge % Động**: Tự động chuyển vị trí: ở nửa bên phải màn hình thì Badge nằm ở góc dưới bên trái; ở nửa bên trái màn hình thì Badge nằm ở góc dưới bên phải.
      + **Kéo Thả Tự Do & Hít Mép**: Người dùng có thể kéo đặt ở bất kỳ đâu trên màn hình desktop (cả ở giữa màn hình lẫn mép); tự động hít nhẹ vào mép viền khi kéo sát mép (< 30px).
      + **Tự Động Thụt Mép & Làm Mờ (Auto-dock & Fade 3.5s)**: Sau 3.5 giây nhàn rỗi, tự động thụt 55% vào mép và làm mờ 45%; chuột rê vào (`PointerEntered`) hoặc chạm lập tức khôi phục 100% độ rõ và thụt ra ngoài.
      + **Nhấn Nhanh Mở Lại App & Menu 3 Giây / Chuột Phải**: Click đơn khôi phục MainWindow nguyên vẹn kích thước ban đầu; nhấn giữ 3s hoặc click chuột phải mở menu tùy chọn nổi (🚫 Tắt bong bóng / 🛑 Tắt app hoàn toàn).


36. **Bổ Sung Tùy Chọn Ghi Đè Giữ Bookmark vs Tạo File Mới, Bảo Toàn Cấu Trúc Folder Từng Chapter Kèm Chuẩn Hóa Responsive Android Cho Chức Năng Nối Chap (`MainView.axaml`, `MainViewModel.ArchiveMerger.cs`, `LanguageService.cs`, `bug-fixes-and-solutions.md`)**:
    - **Ý Tưởng & Giải Pháp Kỹ Thuật (BUG-023)**: Phân tách rõ ràng 2 kịch bản xuất file khi nối chapter:
      + **Option 1 (Mặc định & Khuyên dùng)**: "⚡ Ghi đè vào file cũ" (giữ nguyên cấu trúc folder, bookmark & tiến độ trang đọc dở của app đọc truyện theo File Path). Tự động sao lưu bản dự phòng `.bak` an toàn 100% trước khi cập nhật.
      + **Option 2**: "📄 Tạo file mới độc lập" (xuất file riêng biệt, tự động điền đường dẫn gợi ý `_updated`). Khung đổi vị trí lưu chỉ xuất hiện khi chọn Option này.
    - **Bảo Toàn 100% Cấu Trúc Folder & Bookmarks**: Dù ghi đè hay tạo file mới, toàn bộ các chapter cũ và mới trong file CBZ/ZIP đều được trích xuất và phân tách vào từng thư mục con độc lập (`extractDir/Chap_XXX/`), triệt tiêu hoàn toàn nguy cơ xáo trộn hoặc làm phẳng bừa bãi. Đối với PDF, tự động đánh số Bookmarks mục lục nối tiếp từng chapter.
    - **Responsive Toàn Diện Trên Android**: Toàn bộ TextBlock trong cụm tùy chọn đều kích hoạt `TextWrapping="Wrap"` và nằm trong layout `StackPanel`/`WrapPanel` co giãn thích ứng, thẻ RadioButton mở rộng vùng chạm `HorizontalAlignment="Stretch"`, giải quyết triệt để lỗi chữ dài bị tràn viền hoặc cắt xén trên màn hình điện thoại di động.

37. **Khắc Phục Lỗi Gộp Số Thập Phân (3.1, 3.2...) & Bảo Toàn Cấu Trúc Folder Chapter Khi Nối Vào PDF (`ArchiveMergeService.cs`, `PdfComicParser.cs`, `bug-fixes-and-solutions.md`)**:
    - **Ý Tưởng & Giải Pháp Kỹ Thuật (BUG-024)**:
      + **Fix Lỗi Bị Gộp Vào Chapter 3**: Khắc phục hành vi mặc định của .NET `Path.GetFileNameWithoutExtension("3.1")` coi `.1` là file extension và cắt bỏ. Xây dựng hàm `StripArchiveOrDocExtension` thông minh chỉ bóc tách đuôi file nén/tài liệu hợp lệ, bảo toàn 100% số thập phân trong `ParseChapterInfo` (`ParsedChapterNumber = 3.1`, `FormattedLabel = "3.1"`) và `MakeSafeFolderName` (giữ nguyên tên folder `3.1`, `3.2` riêng biệt trong CBZ/ZIP/MOBI/PDF).
      + **Fix Lỗi Mất Cấu Trúc Folder Khi Nối Vào PDF**: Triển khai thuật toán quét đệ quy `CollectChapterFoldersRecursive` tự động đào sâu qua các tầng thư mục cha để bóc tách chính xác từng leaf chapter folder chứa ảnh trực tiếp, ngăn chặn việc gom cả thư mục mẹ thành 1 chapter duy nhất; đồng thời bảo toàn toàn bộ hệ thống Bookmarks phân đoạn chapter cũ/mới và bổ sung cơ chế `PdfComicParser.Instance.InvalidateCache` xóa cache ngay sau khi ghi đè để giao diện Thư viện và Reader luôn cập nhật dữ liệu mới nhất.

38. **Tối Ưu Hóa Tốc Độ Nối Thư Mục Vào File PDF Ngang Với ZIP & Bảo Toàn 100% Kích Thước Pixel Ảnh Gốc (`FilePackerService.cs`, `UniversalImageDecoder.cs`, `ArchiveMergeService.cs`, `PdfComicParser.cs`, `bug-fixes-and-solutions.md`)**:
    - **Ý Tưởng & Giải Pháp Kỹ Thuật (BUG-025)**:
      + **Zero-Conversion Native Direct Streaming (`FilePackerService.CreatePdfFromPageSources`)**: Thay thế hoàn toàn cơ chế bóc tách ảnh trang cũ ra ổ đĩa bằng cơ chế sao chép trực tiếp luồng byte (`StreamOffset` và `StreamLength`) từ file PDF gốc sang file PDF mới với buffer lớn 128KB, 0ms decode, 0 file tạm, giảm thời gian xử lý từ vài phút xuống dưới 100ms.
      + **Bảo Toàn 100% Kích Thước & Chất Lượng Ảnh Gốc**: Mở rộng `TryGetFastJpegInfo` và `TryGetPdfDirectJpegInfo` hỗ trợ cả SOF0 Baseline và SOF2 Progressive, stream trực tiếp 100% byte JPEG gốc (DCTDecode) và PNG IDAT (FlateDecode) không qua SkiaSharp re-encode, giữ nguyên 100% kích thước pixel Width x Height và dung lượng ảnh gốc.
      + **Xử Lý Song Song Đa Luồng Cho Ảnh WebP/Khác**: Tự động nhận diện và tiền xử lý đa luồng (`Parallel.ForEach`) trên toàn bộ CPU với `FastEncodeToJpegStream`, giải mã trực tiếp từ FileStream không tạo SKSurface trung gian.
      + **Bảo Vệ Đa Lớp Fallback**: Tự động fallback trích xuất trang đơn lẻ nếu gặp PDF có cấu trúc dị biệt không resolve trực tiếp được offset, đảm bảo 100% an toàn dữ liệu không bao giờ bị mất trang hay hỏng file.

39. **Tối Ưu Dual Picture Sort (Landscape Grid 1 - 3, Bỏ Tự Động Tick Checkbox Ảnh Đầu Tiên) & Đổi Tên Thư Mục Dữ Liệu Android Tránh Nhầm Lẫn Sang utosaveComicDownloaderGMTPC (MainViewModel.DualPictureSort.cs, MainViewModel.cs, MainView.axaml, QueueBackupService.cs, App.axaml.cs, ug-fixes-and-solutions.md)**:
    - **Ý Tưởng & Giải Pháp Kỹ Thuật (BUG-026)**:
      + **Tối Ưu Hiển Thị Landscape Trên Mọi Hệ Điều Hành**: Khắc phục hiện tượng ảnh ngang (16:9, widescreen, 1216x832...) bị co nhỏ thành 108x74 px và lộ 2 dải đen trên dưới trên Windows/Linux. Thay thế toàn bộ 4 ItemsControl sang <UniformGrid Columns="{Binding DualSortGridColumns}" HorizontalAlignment="Stretch" />, loại bỏ Width="116/110" cố định để card tự động giãn nở khít cột.
      + **Lựa Chọn Grid Cột Linh Hoạt (Grid 1 - 3)**: Bổ sung thuộc tính DualSortGridColumns (1, 2, 3 cột) và chiều cao thumbnail thích ứng DualSortThumbHeight (chuẩn tỷ lệ 16:9 / 16:10 cho cả Desktop và Mobile), tích hợp cụm nút chọn Grid trực quan ⊞ Grid: [ 1 | 2 | 3 ] trên Toolbar Desktop và Switcher Tabs Mobile. Tăng kích thước decode thumbnail lên 480px giúp hình ảnh sắc nét tuyệt đối.
      + **Bỏ Tự Động Tick Checkbox Ảnh Đầu Tiên**: Tách rời phương thức LoadPreviewItem(item) độc lập với SelectDualSortItem(item), nạp ảnh xem trước to rõ ở giữa ngay khi mở thư mục mà không tự động tick IsSelected = true của ảnh đầu tiên.
      + **Đổi Tên Thư Mục Android Tránh Trùng Lặp Sang utosaveComicDownloaderGMTPC**: Đổi tên thư mục dữ liệu trên Android thành /storage/emulated/0/Download/autosaveComicDownloaderGMTPC để tách biệt hoàn toàn với thư mục truyện ComicDownloads. Thiết kế cơ chế an toàn QueueBackupService.EnsureAutoSaveMigration() chạy ngay đầu OnFrameworkInitializationCompleted trước khi nạp data, tự động di chuyển toàn bộ dữ liệu cũ mà không mất 1 byte nào và dọn dẹp thư mục cũ.
40. **Vertical ScrollBar Tự Động Phóng To 3 Giây Khi Cuộn & Nâng Cấp ComboBox Chọn Grid 1-6 Desktop / 1-3 Android (ScrollBarAutoExpandService.cs, App.axaml, MainView.axaml, MainView.axaml.cs, MainViewModel.DualPictureSort.cs, ug-fixes-and-solutions.md)**:
    - **Ý Tưởng & Giải Pháp Kỹ Thuật (BUG-027)**:
      + **Vertical ScrollBar Auto-Expand 3 Giây Trên Mọi Hệ Điều Hành**: Xây dựng ScrollBarAutoExpandService tự động lắng nghe sự kiện cuộn từ mọi thao tác (lăn chuột, vuốt chạm ngón tay Android, kéo Thumb, click track, phím bấm). Khi cuộn, thanh cuộn dọc nở to tức thì từ 8px lên 22px (Thumb từ 6px lên 18px, bo tròn 9px, đổi màu Cyber Cyan #38BDF8), tự động duy trì trong 3 giây và thu nhỏ lại mượt mà với Avalonia Transitions.
      + **Triệt Tiêu Lỗi Mojibake Bằng Vector PathIcon**: Thay thế ký tự Unicode bị lỗi bằng PathIcon vector hình lưới 4 ô vuông (M3 3h7v7H3V3zm11 0h7v7h-7V3zm0 11h7v7h-7v-7zm-11 0h7v7H3v-7z) chuẩn XML/XAML 100% không lo lỗi font.
      + **ComboBox Chọn Grid Tối Ưu Diện Tích**: Chuyển đổi cụm chọn Grid sang ComboBox gọn gàng: hỗ trợ từ 1 đến 6 cột trên Desktop (Windows & Linux) và từ 1 đến 3 cột trên Android (DualSortGridOptions). Chiều cao thumbnail DualSortThumbHeight tự động tính toán chuẩn tỷ lệ landscape widescreen 16:9 tương ứng với từng số cột (1 -> 6).
### 41. Bố Cục Tab Dual Picture Sort & Chuẩn Tự Động Phóng To ScrollBar 3 Giây Toàn Cục
- **Dual Picture Sort 2 Tab**: Bố cục phân loại ảnh kép thống nhất dùng 2 Tab độc lập (`📁 THƯ MỤC GỐC` và `✨ ĐÃ LỌC`) trên toàn bộ các nền tảng (Desktop Windows, Linux và Android Mobile/Tablet). Tuyệt đối không dùng 2 panel song song (side-by-side) chia đôi màn hình để bảo toàn 100% không gian hiển thị cho lưới ảnh từ 1 đến 6 cột.
- **Tự động phóng to ScrollBar dọc 3 giây**: Lắng nghe toàn cục thao tác cuộn (lăn chuột Tunnel/Bubble trên Desktop, cử chỉ vuốt chạm cảm ứng ngón tay Touch Drag trên Android, ScrollChanged từ mọi ScrollViewer). Khi có thao tác cuộn (kể cả khi nội dung ở đỉnh/đáy không cuộn thêm được), toàn bộ ScrollBar dọc đang hiển thị lập tức mở rộng từ 8px lên 22px (Thumb 18px màu Cyber Cyan `#38BDF8`) trong 3.0 giây rồi tự động thu nhỏ lại mượt mà qua Avalonia Transitions. Ngoại lệ duy nhất: Toàn bộ ScrollBar bên trong trình đọc truyện Reader (`ComicReaderControl`/`ComicReaderWindow`) không được phóng to để không che khuất trang truyện.


### 42. Nâng Cấp Dual Picture Sort (Hiển Thị File Khi Chọn Source, Finish Folder & Chuẩn Hóa Song Ngữ EN-VI Toàn Bộ Tab)
- **Hiển Thị File Xem Trước Danh Sách Ảnh Khi Duyệt Thư Mục Gốc (`OpenFilePickerAsync`)**:
  + Thay thế `OpenFolderPickerAsync` bằng `OpenFilePickerAsync` kèm bộ lọc định dạng ảnh (*.jpg, *.png, *.webp, *.bmp, *.gif, *.avif, *.jxl) và Tất cả tệp (*.*). Người dùng khi mở duyệt thư mục sẽ thấy toàn bộ danh sách file ảnh và thumbnail xem trước, chọn 1 file bất kỳ hoặc bấm Open là hệ thống tự động nhận diện thư mục cha làm `DualSortSourcePath`.
- **Tích Hợp Thư Mục Hoàn Thành (Finish Folder) & Tự Động Chuyển Bộ Kế Tiếp**:
  + Bổ sung nút `🏁 Finish` trên Floating BATCH TOOL ngay phía trên hàng nút `Prev / Next`.
  + Bổ sung cấu hình `🏁 Thư mục đích (Finish):` (nút Chọn, Mở, Finish Folder, TextBox `DualSortFinishPath`) ngay bên dưới dòng Thư mục đã lọc.
  + Thao tác Finish sẽ di chuyển toàn bộ thư mục bộ ảnh đang thao tác sang `DualSortFinishPath` (tự động xử lý trùng lặp tên folder an toàn), xóa cache Undo/Redo và tự động nạp thư mục anh em tiếp theo.
- **Chuẩn Hóa Đa Ngôn Ngữ EN - VI Toàn Diện**:
  + Tự động bóc tách hậu tố emoji/icon ở cuối chuỗi (`Tiếp ⏭️`) và dấu câu đuôi (`:`, `...`, `!`, `?`) trong `LanguageService.Translate`.
  + Cập nhật từ điển song ngữ trong `languages.md` và `LanguageService.cs` đầy đủ cho các thành phần Dual Picture Sort và Batch Tool.
  + Mở rộng `TranslateElement` hỗ trợ tự động dịch `PlaceholderText` của TextBox và `Content` của RadioButton.
  + Tự động cập nhật các nút trạng thái động (`DualSortBatchToggleBtnText`, `DualSortBatchButtonText`, `DualSortPreviewButtonText`) khi thay đổi ngôn ngữ.


### 42. Nâng Cấp Dual Picture Sort (Hiển Thị File Khi Chọn Source, Finish Folder & Chuẩn Hóa Song Ngữ EN-VI Toàn Bộ Tab)
- **Hiển Thị File Xem Trước Danh Sách Ảnh Khi Duyệt Thư Mục Gốc (`OpenFilePickerAsync`)**:
  + Thay thế `OpenFolderPickerAsync` bằng `OpenFilePickerAsync` kèm bộ lọc định dạng ảnh (*.jpg, *.png, *.webp, *.bmp, *.gif, *.avif, *.jxl) và Tất cả tệp (*.*). Người dùng khi mở duyệt thư mục sẽ thấy toàn bộ danh sách file ảnh và thumbnail xem trước, chọn 1 file bất kỳ hoặc bấm Open là hệ thống tự động nhận diện thư mục cha làm `DualSortSourcePath`.
- **Tích Hợp Thư Mục Hoàn Thành (Finish Folder) & Tự Động Chuyển Bộ Kế Tiếp**:
  + Bổ sung nút `🏁 Finish` trên Floating BATCH TOOL ngay phía trên hàng nút `Prev / Next`.
  + Bổ sung cấu hình `🏁 Thư mục đích (Finish):` (nút Chọn, Mở, Finish Folder, TextBox `DualSortFinishPath`) ngay bên dưới dòng Thư mục đã lọc.
  + Thao tác Finish sẽ di chuyển toàn bộ thư mục bộ ảnh đang thao tác sang `DualSortFinishPath` (tự động xử lý trùng lặp tên folder an toàn), xóa cache Undo/Redo và tự động nạp thư mục anh em tiếp theo.
- **Chuẩn Hóa Đa Ngôn Ngữ EN - VI Toàn Diện**:
  + Tự động bóc tách hậu tố emoji/icon ở cuối chuỗi (`Tiếp ⏭️`) và dấu câu đuôi (`:`, `...`, `!`, `?`) trong `LanguageService.Translate`.
  + Cập nhật từ điển song ngữ trong `languages.md` và `LanguageService.cs` đầy đủ cho các thành phần Dual Picture Sort và Batch Tool.
  + Mở rộng `TranslateElement` hỗ trợ tự động dịch `PlaceholderText` của TextBox và `Content` của RadioButton.
  + Tự động cập nhật các nút trạng thái động (`DualSortBatchToggleBtnText`, `DualSortBatchButtonText`, `DualSortPreviewButtonText`) khi thay đổi ngôn ngữ.


### 43. Rebuild Toàn Diện Hệ Thống Điều Hướng Reader & Đồng Bộ Seekbar (BUG-028)
- **Tái Cấu Trúc Máy Trạng Thái FSM (`ReaderNavigationState.cs`, `ComicReaderViewModel.cs`, `ComicReaderControl.axaml.cs`)**:
  + Thiết lập FSM 4 trạng thái độc lập: `IdleReading`, `UserScrubbing`, `ChapterTransitioning`, `ProgrammaticScrolling`.
  + Triệt tiêu hoàn toàn race condition và xung đột giữa sự kiện cuộn ScrollViewer, thao tác kéo thanh trượt Seekbar và nạp chương mới.
- **Nguyên Tử Hóa Quy Trình Đổi Chương (Atomic Chapter Transition)**:
  + Lưu chính xác tiến độ của chương cũ (`SaveChapterReadingSession`) thông qua `OnCurrentChapterChanging` TRƯỚC KHI con trỏ `CurrentChapter` trỏ sang chương mới.
  + Triệt tiêu hoàn toàn lỗi ô nhiễm tiến độ khiến chương mới chưa xem bị gán nhầm trang cuối hoặc trang giữa của chương trước đó.
- **Thuật Toán Điểm Đọc Trọng Tâm Read Anchor 35% Viewport & Kiểm Tra Container Thực Tế**:
  + Loại bỏ vòng lặp Hysteresis cũ (`visibleHeight >= vh * 0.25`) gây kẹt trang và hiện tượng Seekbar không cập nhật khi cuộn đọc sang ảnh khác.
  + Kiểm tra trực tiếp tọa độ tương đối của các phần tử `ListBoxItem` đang hiện thực hóa trong Viewport (`TranslatePoint` so với `ScrollViewer`), kết hợp fallback tích lũy hình học chính xác O(N).
  + Lọc chặt chẽ nguồn sự kiện cuộn (`e.Source == GetReaderScrollViewer()`), loại bỏ hoàn toàn các sự kiện cuộn từ Drawer hay Popup cài đặt.
- **Tách Bạch Luồng Dữ Liệu Seekbar (Decoupled Flow)**:
  + Khi người dùng chạm kéo Seekbar: FSM kích hoạt `UserScrubbing`, chỉ cập nhật bóng báo số trang (HUD Preview).
  + Chỉ khi nhả tay (`PointerReleased`), hệ thống mới phát lệnh `ScrubToPage` duy nhất một lần và khôi phục `IdleReading` mượt mà, không giật lag.
