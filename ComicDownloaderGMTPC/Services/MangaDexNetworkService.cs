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
/// 3. Reverse Proxy Gateways (hỗ trợ cả AllOrigins wrapper, CORSProxy, CodeTabs)
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
        "https://api.allorigins.win/get?url=",
        "https://api.allorigins.hexxy.media/get?url=",
        "https://corsproxy.io/?url=",
        "https://api.codetabs.com/v1/proxy?quest=",
        "https://proxy.corsfix.com/?"
    ];

    public MangaDexNetworkService()
    {
        var dohHandler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            },
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    var ips = await DoHResolver.Instance.ResolveAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
                    if (ips.Length == 0)
                    {
                        ips = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
                    }
                    await socket.ConnectAsync(ips, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

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
    /// Lấy chuỗi JSON từ MangaDex API với cơ chế kết hợp DoH, Curl Engine, Reverse Proxy và Cloudflare WARP.
    /// </summary>
    public async Task<string> GetJsonAsync(string originalUrl, CancellationToken ct = default)
    {
        Exception? lastError = null;

        // Tầng 1: Direct HttpClient với DNS-over-HTTPS (DoH)
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, originalUrl);
            using var res = await _dohClient.SendAsync(req, ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                string cleanJson = TryUnwrapProxyJson(json);
                if (IsValidJson(cleanJson)) return cleanJson;
            }
        }
        catch (Exception ex)
        {
            lastError = ex;
        }

        // Tầng 2: Curl CLI Engine (Bypass TLS handshake và SNI filtering của ISP cực kỳ hiệu quả)
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                string curlResult = await FetchWithCurlAsync(originalUrl, ct).ConfigureAwait(false);
                string cleanJson = TryUnwrapProxyJson(curlResult);
                if (IsValidJson(cleanJson))
                {
                    return cleanJson;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        // Tầng 3: Native WebView Service (Headless Chromium engine)
        if (NativeWebViewService.Instance.IsAvailable)
        {
            try
            {
                string? webViewJson = await NativeWebViewService.Instance.FetchJsonAsync(originalUrl, timeoutSeconds: 15, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(webViewJson))
                {
                    string cleanJson = TryUnwrapProxyJson(webViewJson);
                    if (IsValidJson(cleanJson))
                    {
                        return cleanJson;
                    }
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        // Tầng 4: Reverse Proxy Gateways
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
                    string cleanJson = TryUnwrapProxyJson(pJson);
                    if (IsValidJson(cleanJson))
                    {
                        return cleanJson;
                    }
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
    /// Tải tệp ảnh trực tiếp từ MangaDex Network (*.mangadex.network, uploads.mangadex.org).
    /// Tự động fallback qua Curl Engine nếu HttpClient bị nhà mạng chặn SNI/IP.
    /// </summary>
    public async Task<bool> DownloadImageFileAsync(string imageUrl, string destinationPath, string? refererUrl, CancellationToken ct)
    {
        // 1. Thử tải qua Direct HttpClient DoH
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            req.Headers.Add("Referer", string.IsNullOrWhiteSpace(refererUrl) ? "https://mangadex.org/" : refererUrl);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");

            using var res = await _dohClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                byte[] data = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (data.Length > 0)
                {
                    await File.WriteAllBytesAsync(destinationPath, data, ct).ConfigureAwait(false);
                    return true;
                }
            }
        }
        catch
        {
            // Fallback sang Curl Engine
        }

        // 2. Fallback qua Curl CLI Engine
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                bool curlSuccess = await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();

                    string curlPath = OperatingSystem.IsWindows()
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe")
                        : "curl";

                    if (OperatingSystem.IsWindows() && !File.Exists(curlPath))
                    {
                        curlPath = "curl.exe";
                    }

                    string tempDest = destinationPath + ".tmp";
                    string refHeader = string.IsNullOrWhiteSpace(refererUrl) ? "https://mangadex.org/" : refererUrl;

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = curlPath,
                        Arguments = $"-s -L --max-time 20 --insecure -H \"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)\" -H \"Referer: {refHeader}\" -o \"{tempDest}\" \"{imageUrl}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    };

                    using var process = new Process { StartInfo = startInfo };
                    process.Start();

                    using var reg = ct.Register(() => { try { process.Kill(); } catch {} });
                    process.WaitForExit(22000);

                    if (process.ExitCode == 0 && File.Exists(tempDest) && new FileInfo(tempDest).Length > 100)
                    {
                        if (File.Exists(destinationPath)) File.Delete(destinationPath);
                        File.Move(tempDest, destinationPath);
                        return true;
                    }

                    try { if (File.Exists(tempDest)) File.Delete(tempDest); } catch {}
                    return false;
                }, ct).ConfigureAwait(false);

                if (curlSuccess) return true;
            }
            catch
            {
                // Ignore
            }
        }

        return false;
    }

    private static string TryUnwrapProxyJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        string trimmed = text.Trim();
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.TryGetProperty("contents", out var contentsProp) && contentsProp.ValueKind == JsonValueKind.String)
            {
                string inner = contentsProp.GetString() ?? string.Empty;
                if (IsValidJson(inner)) return inner;
            }
        }
        catch
        {
            // Ignore parsing error and return original text
        }
        return trimmed;
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
                Arguments = $"-s -L --max-time 15 --insecure -H \"Accept: application/json, text/plain, */*\" -H \"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36\" -H \"Referer: https://mangadex.org/\" \"{url}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            using var reg = ct.Register(() => { try { process.Kill(); } catch {} });
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(16000);

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                return output;
            }

            throw new HttpRequestException("curl execution failed or timed out.");
        }, ct).ConfigureAwait(false);
    }
}
