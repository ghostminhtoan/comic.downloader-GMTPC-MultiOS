namespace ComicDownloaderGMTPC.Models;

/// <summary>
/// Máy trạng thái hữu hạn (FSM) điều phối toàn diện hệ thống điều hướng và thanh Seekbar trong Reader.
/// Triệt tiêu hoàn toàn race condition giữa cuộn thị giác, thao tác kéo thanh trượt và nạp chương mới.
/// </summary>
public enum ReaderNavigationState
{
    /// <summary>
    /// Đang đọc bình thường: Sự kiện cuộn ScrollViewer được phép phân tích vị trí trang và cập nhật Seekbar.
    /// </summary>
    IdleReading,

    /// <summary>
    /// Người dùng đang chạm/kéo thanh trượt Seekbar: Chỉ hiển thị bong bóng số trang (HUD Preview),
    /// tuyệt đối không cho phép sự kiện cuộn ScrollViewer can thiệp làm giật ngược thanh trượt.
    /// </summary>
    UserScrubbing,

    /// <summary>
    /// Đang trong quá trình đổi và nạp chương mới: Khóa toàn bộ sự kiện cuộn và đồng bộ Seekbar
    /// cho đến khi bố cục ảnh mới được khởi tạo và đo đạc hoàn tất.
    /// </summary>
    ChapterTransitioning,

    /// <summary>
    /// Đang cuộn tự động có chủ đích tới một trang cụ thể (ScrubToPage, Nhảy bookmark, Lật trang).
    /// </summary>
    ProgrammaticScrolling
}

