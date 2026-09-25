using System.Threading;
using System.Threading.Tasks;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Giao diện dịch vụ Native WebView / Headless Browser đa nền tảng
/// Cho phép tải web ngầm, thực thi JavaScript và vượt rào cản bảo mật (Cloudflare, DDoS-Guard).
/// </summary>
public interface INativeWebViewService
{
    /// <summary>
    /// Cho biết trên hệ thống hiện tại có sẵn engine browser native để chạy ngầm hay không.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Tên engine đang được sử dụng (ví dụ: Microsoft Edge, Google Chrome, Chromium).
    /// </summary>
    string EngineName { get; }

    /// <summary>
    /// Tải HTML của trang web chạy ngầm (headless) có thực thi JavaScript đầy đủ.
    /// </summary>
    Task<string?> FetchHtmlAsync(string url, int timeoutSeconds = 15, CancellationToken ct = default);

    /// <summary>
    /// Tải dữ liệu JSON chạy ngầm (headless) qua native browser engine.
    /// </summary>
    Task<string?> FetchJsonAsync(string url, int timeoutSeconds = 15, CancellationToken ct = default);
}
