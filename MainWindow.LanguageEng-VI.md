# Quy Chuẩn Ngôn Ngữ Song Ngữ (ENG - VI) - Comic Downloader GMTPC Avalonia

File quy chuẩn bảng ánh xạ ngôn ngữ và giao diện song ngữ Tiếng Anh (ENG) - Tiếng Việt (VI) cho toàn bộ ứng dụng Comic Downloader GMTPC Avalonia.

---

## 1. Bảng Ngôn Ngữ Chuẩn Hóa (ENG - VI Dictionary)

| English (ENG) | Tiếng Việt (VI) | Phạm Vi Sử Dụng |
| :--- | :--- | :--- |
| Single Comic | Tải Truyện Đơn | Mode Selection |
| Batch Multi-Comic | Tải Hàng Loạt | Mode Selection |
| Get Links | Lấy Link | Action Button |
| Add More Links | Thêm Link | Action Button |
| Clear Input | Xóa Link | Action Button |
| Paste Link | Dán Link | Action Button |
| Auto Paste Clipboard | Tự Dán Link | Toggle Feature |
| Download Queue | Hàng Chờ Tải | Tab Header |
| Scan Missing Chapters | Scan Thiếu Chap | Tab Header |
| Folder Tools | Công Cụ Thư Mục | Tab Header |
| Image Enhancer | Nâng Cấp Ảnh | Tab Header |
| System Logs | Nhật Ký Hệ Thống | Tab Header |
| Download All | Tải Tất Cả | Main Action |
| Download New | Tải Mới | Main Action |
| Auto Download | Tự Động Tải | Download Option |
| Auto Retry | Tự Thử Lại | Download Option |
| Compact Row | Nén Dòng | Layout Option |
| Auto Scroll (10s) | Tự Cuộn (10s) | Queue Navigation |
| Auto Split Chapters | Tự Động Tách Chương | Performance Option |
| Auto Split Long Images | Tự Cắt Ảnh Dài | Image Processing |
| Select All | Chọn Tất Cả | Queue Action |
| Unselect All | Bỏ Chọn | Queue Action |
| Invert Selection | Đảo Chọn | Queue Action |
| Check Selected | Tích Hàng Chọn | Queue Action |
| Check Errors | Tích Hàng Lỗi | Queue Action |
| Check Duplicates | Tích Hàng Trùng | Queue Action |
| Clear Completed | Xóa Đã Xong | Queue Action |
| Delete Selected | Xóa Hàng Chọn | Queue Action |
| Original Book | Thư Mục Gốc | Split Tree Status |
| Split Range | Nhánh Con | Split Tree Status |
| Expand | [+] Xem thêm phần | Tree Collapse Toggle |
| Collapse | [-] Thu gọn | Tree Collapse Toggle |
| Analyze Domain | Kiểm Tra Trang | Source Analyze |
| Crawl New | Cào Mới (Crawl) | Source Action |
| Crawl More | Cào Thêm (Crawl More) | Source Action |
| Stop Crawl | Dừng Cào | Source Action |
| Update Stable | Cập Nhật Stable | System Update |
| Update Beta | Cập Nhật Beta | System Update |

---

## 2. Quy Tắc Giao Diện Song Ngữ

1. **Hiển thị trực quan**: Mọi nhãn nút bấm và tiêu đề tab trên giao diện đều có icon minh họa kèm theo thuật ngữ tiếng Việt rõ ràng, ngắn gọn và dễ hiểu.
2. **Tooltip song ngữ**: Các ToolTip hướng dẫn chi tiết hỗ trợ tiếng Việt đầy đủ, giúp người dùng trên mọi hệ điều hành (Windows, Linux, Android) thao tác thuận tiện.
3. **Đồng bộ runtime**: `LanguageService.cs` tự động nạp bảng từ vựng từ `languages.md` và `MainWindow.LanguageEng-VI.md`, hỗ trợ chuyển đổi ngôn ngữ tức thì không cần khởi động lại ứng dụng.

