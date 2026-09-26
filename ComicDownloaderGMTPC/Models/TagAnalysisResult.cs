namespace ComicDownloaderGMTPC.Models;

public class TagAnalysisResult
{
    public string Domain { get; set; } = string.Empty;
    public string TagTitle { get; set; } = string.Empty;
    public int TotalPages { get; set; } = 1;
    public string BaseUrl { get; set; } = string.Empty;
    public string StatusMessage { get; set; } = string.Empty;
    public bool IsSuccess { get; set; }
}
