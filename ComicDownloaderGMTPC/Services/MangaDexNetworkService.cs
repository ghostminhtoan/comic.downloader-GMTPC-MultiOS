using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Dịch vụ kết nối mạng chuyên dụng cho MangaDex kết hợp toàn diện:
/// 1. Direct HttpClient với DoH (DNS-over-HTTPS via Cloudflare 1.1.1.1 / Google)
/// 2. Native WebView Service (Headless Edge / Chromium)
/// 3. Reverse Proxy Gateways
/// 4. Curl Fallback
/// 5. Chẩn đoán Cloudflare WARP thông minh
/// </summary>
public class MangaDexNetworkService
{
    private static readonly Lazy<MangaDexNetworkService> _instance = new(() => new MangaDexNetworkService());
    public static MangaDexNetworkService Instance => _instance.Value;

    private readonly HttpClient _dohClient;
    private readonly HttpClient _proxyClient;

    // Danh sách các cổng Reverse Proxy Gateway dự phòng cho MangaDex API
    private static readonly string[] GatewayProxies =
    [
        "https://corsproxy.io/?url=",
        "https://api.codetabs.com/v1/proxy?quest="
    ];

    public MangaDexNetworkService()
    {
        var dohHandler = DoHResolver.CreateBypassHandler();
        _dohClient = new HttpClient(dohHandler) { Timeout = TimeSpan.FromSeconds(15) };
        _dohClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        _dohClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");

        var proxyHandler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(12)
        };
        _proxyClient = new HttpClient(proxyHandler) { Timeout = TimeSpan.FromSeconds(20) };
        _proxyClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        _proxyClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
    }

    /// <summary>
    /// Lấy chuỗi JSON từ MangaDex API với cơ chế kết hợp Native WebView Service và DoH / Cloudflare WARP / Reverse Proxy.
    /// </summary>
    public async Task<string> GetJsonAsync(string originalUrl, CancellationToken ct = default)
    {
        Exception? lastError = null;

        // Tầng 1: Direct HttpClient với DNS-over-HTTPS (DoH)
        // Nhanh nhất khi người dùng bật 1.1.1.1 WARP hoặc mạng không bị chặn SNI/TLS
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, originalUrl);
            using var res = await _dohClient.SendAsync(req, ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (IsValidJson(json)) return json;
            }
        }
        catch (Exception ex)
        {
            lastError = ex;
        }

        // Tầng 2: Native WebView Service (Headless Edge / Chromium engine)
        // Dùng stack BoringSSL của Chromium vượt tường lửa SNI của ISP
        if (NativeWebViewService.Instance.IsAvailable)
        {
            try
            {
                string? webViewJson = await NativeWebViewService.Instance.FetchJsonAsync(originalUrl, timeoutSeconds: 15, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(webViewJson) && IsValidJson(webViewJson))
                {
                    return webViewJson;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        // Tầng 3: Reverse Proxy Gateways
        foreach (string gateway in GatewayProxies)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                string proxyUrl = gateway + Uri.EscapeDataString(originalUrl);
                using var pReq = new HttpRequestMessage(HttpMethod.Get, proxyUrl);
                using var pRes = await _proxyClient.SendAsync(pReq, ct).ConfigureAwait(false);
                if (pRes.IsSuccessStatusCode)
                {
                    string pJson = await pRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (IsValidJson(pJson))
                    {
                        return pJson;
                    }
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        // Tầng 4: Curl fallback (nếu có sẵn trên hệ thống)
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                string curlResult = await FetchWithCurlAsync(originalUrl, ct).ConfigureAwait(false);
                if (IsValidJson(curlResult))
                {
                    return curlResult;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        // Nếu tất cả các tầng đều thất bại: Trả về thông tin chẩn đoán thông minh
        string guidance = await CloudflareWarpService.Instance.GetDiagnosticGuidanceAsync(ct).ConfigureAwait(false);
        string errMsg = $"Không thể kết nối MangaDex API. {guidance}";
        if (lastError != null)
        {
            throw new HttpRequestException($"{errMsg} (Chi tiết: {lastError.Message})", lastError);
        }

        throw new HttpRequestException(errMsg);
    }

    /// <summary>
    /// Tải mảng byte dữ liệu (ảnh, tài nguyên nhị phân) từ MangaDex với cơ chế DoH, SniFragmentStream và Proxy dự phòng.
    /// </summary>
    public async Task<byte[]?> DownloadBytesAsync(string url, string? referer = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        // Tầng 1: Direct HttpClient với DoH + SniFragmentStream
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Referer", referer ?? "https://mangadex.org/");
            using var res = await _dohClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                var bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length > 0) return bytes;
            }
        }
        catch { }

        // Tầng 2: Reverse Proxy Gateway
        foreach (string gateway in GatewayProxies)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                string proxyUrl = gateway + Uri.EscapeDataString(url);
                using var pReq = new HttpRequestMessage(HttpMethod.Get, proxyUrl);
                pReq.Headers.Add("Referer", referer ?? "https://mangadex.org/");
                using var pRes = await _proxyClient.SendAsync(pReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (pRes.IsSuccessStatusCode)
                {
                    var pBytes = await pRes.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    if (pBytes.Length > 0) return pBytes;
                }
            }
            catch { }
        }

        return null;
    }

    private static bool IsValidJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim();
        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) || (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return true;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private static async Task<string> FetchWithCurlAsync(string url, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            string curlPath = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe")
                : "curl";

            if (OperatingSystem.IsWindows() && !File.Exists(curlPath))
            {
                curlPath = "curl.exe";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = curlPath,
                Arguments = $"-s -L --max-time 10 -H \"Accept: application/json\" -H \"User-Agent: Mozilla/5.0\" \"{url}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(12000);

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                return output;
            }

            throw new HttpRequestException("curl execution failed or timed out.");
        }, ct).ConfigureAwait(false);
    }
}
