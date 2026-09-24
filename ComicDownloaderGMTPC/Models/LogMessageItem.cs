using System;

namespace ComicDownloaderGMTPC.Models;

public class LogMessageItem
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string TimeText => Timestamp.ToString("HH:mm:ss");
    public string Level { get; set; } = "INFO"; // INFO, SUCCESS, WARN, ERROR
    public string Message { get; set; } = string.Empty;
}
