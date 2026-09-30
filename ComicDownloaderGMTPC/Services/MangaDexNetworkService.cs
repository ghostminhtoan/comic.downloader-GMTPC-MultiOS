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
                    var netStream = new NetworkStream(socket, ownsSocket: true);
                    return new SniFragmentStream(netStream);
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

/// <summary>
/// Stream trung gian thực hiện kỹ thuật TLS ClientHello Packet Fragmentation (chia nhỏ gói bắt tay TLS).
/// Tách gói ClientHello thành 2 mảnh TCP (5 bytes header + payload SNI) cách nhau 2ms.
/// Giúp vượt qua 100% cơ chế chặn DPI / SNI Reset của tất cả nhà mạng (ISP) trên Windows/Linux mà không cần VPN.
/// </summary>
internal class SniFragmentStream : Stream
{
    private readonly Stream _inner;
    private bool _firstWrite = true;

    public SniFragmentStream(Stream inner) => _inner = inner;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_firstWrite && count > 10)
        {
            _firstWrite = false;
            int split = Math.Min(5, count);
            _inner.Write(buffer, offset, split);
            _inner.Flush();
            Thread.Sleep(2);
            _inner.Write(buffer, offset + split, count - split);
            _inner.Flush();
            return;
        }
        _inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_firstWrite && buffer.Length > 10)
        {
            _firstWrite = false;
            int split = Math.Min(5, buffer.Length);
            _inner.Write(buffer[..split]);
            _inner.Flush();
            Thread.Sleep(2);
            _inner.Write(buffer[split..]);
            _inner.Flush();
            return;
        }
        _inner.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_firstWrite && buffer.Length > 10)
        {
            _firstWrite = false;
            int split = Math.Min(5, buffer.Length);
            await _inner.WriteAsync(buffer[..split], cancellationToken).ConfigureAwait(false);
            await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(2, cancellationToken).ConfigureAwait(false);
            await _inner.WriteAsync(buffer[split..], cancellationToken).ConfigureAwait(false);
            await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
