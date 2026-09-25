using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Dịch vụ kiểm tra và chẩn đoán trạng thái Cloudflare WARP (1.1.1.1).
/// </summary>
public class CloudflareWarpService
{
    private static readonly Lazy<CloudflareWarpService> _instance = new(() => new CloudflareWarpService());
    public static CloudflareWarpService Instance => _instance.Value;

    private readonly string? _warpCliPath;

    public bool IsWarpInstalled => !string.IsNullOrEmpty(_warpCliPath) && File.Exists(_warpCliPath);

    public CloudflareWarpService()
    {
        _warpCliPath = DetectWarpCliPath();
    }

    /// <summary>
    /// Kiểm tra xem kết nối hiện tại có đang đi qua đường truyền Cloudflare WARP (Tunnel) hay không.
    /// </summary>
    public async Task<bool> IsWarpTunnelActiveAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            string trace = await client.GetStringAsync("https://1.1.1.1/cdn-cgi/trace", ct).ConfigureAwait(false);
            return trace.Contains("warp=on", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Lấy thông tin chẩn đoán trạng thái WARP và hướng dẫn khắc phục khi bị nhà mạng chặn.
    /// </summary>
    public async Task<string> GetDiagnosticGuidanceAsync(CancellationToken ct = default)
    {
        bool isTunnelActive = await IsWarpTunnelActiveAsync(ct).ConfigureAwait(false);
        if (isTunnelActive)
        {
            return "Đường truyền Cloudflare WARP đang hoạt động tốt (warp=on).";
        }

        if (IsWarpInstalled)
        {
            return "Máy đã cài Cloudflare WARP nhưng hiện chưa bật chế độ WARP toàn phần (Tunnel). " +
                   "Vui lòng mở ứng dụng 1.1.1.1 và chọn chế độ '1.1.1.1 with WARP' để vượt rào cản nhà mạng.";
        }

        return "Nhà mạng (ISP) chặn SNI/TLS tới máy chủ MangaDex. " +
               "Khuyến nghị cài đặt Cloudflare WARP (1.1.1.1) từ https://1.1.1.1/ hoặc sử dụng VPN để tải truyện ổn định nhất.";
    }

    private static string? DetectWarpCliPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cloudflare", "Cloudflare WARP", "warp-cli.exe");
            if (File.Exists(defaultPath))
            {
                return defaultPath;
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (File.Exists("/usr/bin/warp-cli"))
            {
                return "/usr/bin/warp-cli";
            }
        }

        return null;
    }
}
