namespace ComicDownloaderGMTPC.Models;

public class ChapterItem
{
    public double ChapterNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Status { get; set; } = "Waiting";
    public int TotalPages { get; set; }
    public int DownloadedPages { get; set; }
    public System.Collections.Generic.List<string> ImageUrls { get; set; } = new();
}
