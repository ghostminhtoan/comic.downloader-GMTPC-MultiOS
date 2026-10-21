using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Hiện thực dịch vụ Native WebView / Headless Browser đa nền tảng.
/// Hỗ trợ chạy trình duyệt ngầm trên Windows (Edge/Chrome), Linux (Chrome/Chromium), macOS (Chrome/Edge).
/// Không cần thư viện bên thứ ba, bảo đảm 100% không cảnh báo (0 Warnings, 0 Errors) khi build.
/// </summary>
public class NativeWebViewService : INativeWebViewService
{
    private static readonly Lazy<NativeWebViewService> _instance = new(() => new NativeWebViewService());
    public static NativeWebViewService Instance => _instance.Value;

    private readonly string? _browserExecutablePath;
    private readonly string _engineName;
    private readonly string _cacheDirectory;

    public bool IsAvailable => !string.IsNullOrEmpty(_browserExecutablePath) && File.Exists(_browserExecutablePath);
    public string EngineName => _engineName;

    public NativeWebViewService()
    {
        _cacheDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".tmp", "webview_cache");
        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }
        }
        catch
        {
            // Ignore directory creation failure in read-only environment
        }

        _browserExecutablePath = DetectBrowserExecutable(out _engineName);
    }

    /// <summary>
    /// Tải HTML của trang web chạy ngầm (headless) có thực thi JavaScript đầy đủ.
    /// </summary>
    public async Task<string?> FetchHtmlAsync(string url, int timeoutSeconds = 30, CancellationToken ct = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // Tự động dọn dẹp các file lock cũ của Chromium để chống bị chặn Singleton trên Windows
            try
            {
                if (Directory.Exists(_cacheDirectory))
                {
                    string lockFile = Path.Combine(_cacheDirectory, "SingletonLock");
                    if (File.Exists(lockFile)) File.Delete(lockFile);
                    string cookieFile = Path.Combine(_cacheDirectory, "SingletonCookie");
                    if (File.Exists(cookieFile)) File.Delete(cookieFile);
                    string socketFile = Path.Combine(_cacheDirectory, "SingletonSocket");
                    if (File.Exists(socketFile)) File.Delete(socketFile);
                }
            }
            catch { }

            var startInfo = new ProcessStartInfo
            {
                FileName = _browserExecutablePath!,
                Arguments = $"--headless=new --disable-gpu --disable-software-rasterizer --no-sandbox " +
                            $"--disable-logging --log-level=3 " +
                            $"--disable-features=Translate,OptimizationHints,MediaRouter " +
                            $"--user-data-dir=\"{_cacheDirectory}\" " +
                            $"--user-agent=\"Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Mobile Safari/537.36\" " +
                            $"--virtual-time-budget=6000 " +
                            $"--enable-features=DnsOverHttps " +
                            $"--dns-over-https-templates=\"https://cloudflare-dns.com/dns-query\" " +
                            $"--dump-dom \"{url}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false, // Không redirect stderr để chống hoàn toàn lỗi tràn buffer anonymous pipe gây treo deadlock trên Windows
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();

                using var registration = ct.Register(() =>
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                });

                var readTask = process.StandardOutput.ReadToEndAsync(ct);
                bool exited = process.WaitForExit(timeoutSeconds * 1000);

                if (!exited)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return null;
                }

                string output = readTask.GetAwaiter().GetResult();

                if (string.IsNullOrWhiteSpace(output))
                {
                    return null;
                }

                // Loại bỏ các log tiền tố của Chromium nếu có
                int htmlStart = output.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
                if (htmlStart >= 0)
                {
                    return output[htmlStart..];
                }

                return output;
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Tải dữ liệu JSON chạy ngầm (headless) qua native browser engine.
    /// Chromium sẽ bọc nội dung JSON thô trong thẻ &lt;pre&gt; khi dump DOM.
    /// </summary>
    public async Task<string?> FetchJsonAsync(string url, int timeoutSeconds = 15, CancellationToken ct = default)
    {
        string? html = await FetchHtmlAsync(url, timeoutSeconds, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        // 1. Trường hợp trả về trực tiếp định dạng JSON
        string trimmed = html.Trim();
        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) || (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
        {
            return trimmed;
        }

        // 2. Trường hợp Chromium bọc JSON trong thẻ <pre>...</pre>
        var preMatch = Regex.Match(html, @"<pre[^>]*>(.*?)</pre>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (preMatch.Success)
        {
            string rawJson = WebUtility.HtmlDecode(preMatch.Groups[1].Value).Trim();
            if ((rawJson.StartsWith('{') && rawJson.EndsWith('}')) || (rawJson.StartsWith('[') && rawJson.EndsWith(']')))
            {
                return rawJson;
            }
        }

        return null;
    }

    private static string? DetectBrowserExecutable(out string engineName)
    {
        engineName = "None";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string[] candidates =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
            ];

            foreach (string path in candidates)
            {
                if (File.Exists(path))
                {
                    engineName = path.Contains("msedge", StringComparison.OrdinalIgnoreCase) ? "Microsoft Edge (Chromium)" : "Google Chrome";
                    return path;
                }
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string[] candidates =
            [
                "/usr/bin/google-chrome",
                "/usr/bin/google-chrome-stable",
                "/usr/bin/chromium",
                "/usr/bin/chromium-browser",
                "/snap/bin/chromium"
            ];

            foreach (string path in candidates)
            {
                if (File.Exists(path))
                {
                    engineName = path.Contains("google-chrome", StringComparison.OrdinalIgnoreCase) ? "Google Chrome (Linux)" : "Chromium (Linux)";
                    return path;
                }
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            string[] candidates =
            [
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"
            ];

            foreach (string path in candidates)
            {
                if (File.Exists(path))
                {
                    engineName = path.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ? "Google Chrome (macOS)" : "Microsoft Edge (macOS)";
                    return path;
                }
            }
        }

        return null;
    }
}
