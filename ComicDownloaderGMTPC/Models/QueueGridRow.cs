using CommunityToolkit.Mvvm.ComponentModel;
using ComicDownloaderGMTPC.Models;

namespace ComicDownloaderGMTPC.Models;

/// <summary>
/// Đại diện cho một hàng trong chế độ Lưới ảo hóa (Virtualized Multi-Column Queue Grid).
/// Hỗ trợ cập nhật in-place để tái sử dụng tối đa Visual Tree của Avalonia, loại bỏ 100% hiện tượng drop FPS / giật lag khi tải hàng loạt.
/// </summary>
public sealed partial class QueueGridRow : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCol1))]
    private ComicBookItem? _col1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCol2))]
    private ComicBookItem? _col2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCol3))]
    private ComicBookItem? _col3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCol4))]
    private ComicBookItem? _col4;

    public bool HasCol1 => Col1 != null;
    public bool HasCol2 => Col2 != null;
    public bool HasCol3 => Col3 != null;
    public bool HasCol4 => Col4 != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridColumnsDefinition))]
    private int _columnsCount;

    public string GridColumnsDefinition => ColumnsCount switch
    {
        2 => "*, *",
        4 => "*, *, *, *",
        _ => "*, *, *"
    };

    public QueueGridRow(ComicBookItem col1, ComicBookItem? col2 = null, ComicBookItem? col3 = null, ComicBookItem? col4 = null, int columns = 2)
    {
        _col1 = col1;
        _col2 = col2;
        _col3 = col3;
        _col4 = col4;
        _columnsCount = columns;
    }

    /// <summary>
    /// Cập nhật in-place các cell trong hàng để tránh hủy và cấp phát lại visual element
    /// </summary>
    public void Update(ComicBookItem? col1, ComicBookItem? col2, ComicBookItem? col3, ComicBookItem? col4, int columns)
    {
        if (!ReferenceEquals(Col1, col1)) Col1 = col1;
        if (!ReferenceEquals(Col2, col2)) Col2 = col2;
        if (!ReferenceEquals(Col3, col3)) Col3 = col3;
        if (!ReferenceEquals(Col4, col4)) Col4 = col4;
        if (ColumnsCount != columns) ColumnsCount = columns;
    }
}
