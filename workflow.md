# WORKFLOW - COMIC DOWNLOADER GMTPC AVALONIA
> File điều hướng tự động trỏ đến: [workflow-avalonia.md](workflow-avalonia.md)

## 🚨 QUY TẮC BẮT BUỘC TỐI THƯỢNG (PHẢI THỰC HIỆN TỰ ĐỘNG)
1. **QUY TẮC BẮT BUỘC: ĐỌC BẢN ĐỒ MÃ NGUỒN (`MainWindow.Menu.md`) TRƯỚC KHI ĐỌC/SỬA**:
   - Trước khi mở, tìm kiếm hoặc sửa bất kỳ file mã nguồn nào, Agent **BẮT BUỘC PHẢI ĐỌC [MainWindow.Menu.md](MainWindow.Menu.md)** để tra cứu chính xác File đích và Thẻ neo `[SEC-...]` tương ứng.
   - Tuyệt đối cấm nạp nguyên file lớn (>1000 dòng như `MainView.axaml`, `ComicScraperService.cs`, `MainViewModel.cs`, `MainView.axaml.cs`) khi chưa xác định Section Tag.
2. **Sau khi viết code, chỉnh sửa hoặc sửa lỗi xong**, BẮT BUỘC PHẢI LUÔN LUÔN chạy `build.bat` để đóng gói toàn diện cả 3 hệ điều hành (Windows, Linux, Android) và **TỰ ĐỘNG UPLOAD RELEASE BETA** lên GitHub Release tag `beta`.
3. **KỂ CẢ KHI NGƯỜI DÙNG KHÔNG NHẮC ĐẾN 'upload beta', AGENT CŨNG BẮT BUỘC PHẢI TỰ ĐỘNG THỰC HIỆN! TUYỆT ĐỐI KHÔNG ĐƯỢC BỎ QUA HOẶC CHỜ USER NHẮC!**
4. Luôn tuân thủ đầy đủ tài liệu chi tiết tại [workflow-avalonia.md](workflow-avalonia.md) và danh mục giải pháp [bug-fixes-and-solutions.md](bug-fixes-and-solutions.md).
5. Phân bổ tác vụ và vận hành theo bản đồ 9 Subagent chuyên biệt tại [subagent.md](subagent.md).
