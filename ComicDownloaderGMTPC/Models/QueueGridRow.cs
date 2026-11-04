using ComicDownloaderGMTPC.Models;

namespace ComicDownloaderGMTPC.Models;

/// <summary>
/// Đại diện cho một hàng trong chế độ Lưới ảo hóa (Virtualized Multi-Column Queue Grid).
/// Chứa tối đa 3 truyện trên Windows/Linux hoặc 2 truyện trên Android, giúp tận dụng
/// VirtualizingStackPanel nguyên bản của Avalonia để duy trì tốc độ 60fps, triệt tiêu 100% lag.
/// </summary>
public sealed class QueueGridRow
{
    public ComicBookItem? Col1 { get; }
    public ComicBookItem? Col2 { get; }
    public ComicBookItem? Col3 { get; }

    public bool HasCol1 => Col1 != null;
    public bool HasCol2 => Col2 != null;
    public bool HasCol3 => Col3 != null;

    public int ColumnsCount { get; }

    public string GridColumnsDefinition => ColumnsCount == 2 ? "*, *" : "*, *, *";

    public QueueGridRow(ComicBookItem col1, ComicBookItem? col2 = null, ComicBookItem? col3 = null, int columns = 3)
    {
        Col1 = col1;
        Col2 = col2;
        Col3 = col3;
        ColumnsCount = columns;
    }
}

