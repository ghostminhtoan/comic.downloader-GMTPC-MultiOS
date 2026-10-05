using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// Bộ giải giải quyết phân giải tên miền qua HTTPS (DNS-over-HTTPS - DoH).
/// Giúp vượt qua hoàn toàn việc ô nhiễm / đầu độc DNS (DNS Poisoning / Hijacking) từ nhà mạng (ISP).
/// Hỗ trợ Cloudflare DoH (1.1.1.1) và Google DoH (8.8.8.8) với bộ nhớ đệm (Cache) theo TTL.
/// </summary>
public class DoHResolver
{
    private static readonly Lazy<DoHResolver> _instance = new(() => new DoHResolver());
    public static DoHResolver Instance => _instance.Value;

    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, (IPAddress[] IPs, DateTime Expiry)> _cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] DoHEndpoints =
    [
        "https://1.1.1.1/dns-query?name={0}&type=A",
        "https://1.0.0.1/dns-query?name={0}&type=A",
        "https://8.8.8.8/resolve?name={0}&type=A",
        "https://8.8.4.4/resolve?name={0}&type=A",
        "https://cloudflare-dns.com/dns-query?name={0}&type=A",
        "https://dns.google/resolve?name={0}&type=A"
    ];

    public DoHResolver()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true
            }
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        _client.DefaultRequestHeaders.Add("Accept", "application/dns-json");
        _client.DefaultRequestHeaders.Add("User-Agent", "ComicDownloader-DoH/1.0");
    }

    /// <summary>
    /// Tạo SocketsHttpHandler đa tầng tích hợp DoH và SniFragmentStream giúp vượt qua 100% việc chặn DNS và TLS SNI trên Android / Windows / Linux.
    /// </summary>
    public static SocketsHttpHandler CreateBypassHandler(CookieContainer? cookieContainer = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true
            },
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    var ips = await Instance.ResolveAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
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

        if (cookieContainer != null)
        {
            handler.CookieContainer = cookieContainer;
        }

        return handler;
    }

    /// <summary>
    /// Phân giải tên miền ra danh sách IPAddress thông qua DNS-over-HTTPS.
    /// Nếu phân giải DoH thất bại, tự động fallback về Dns.GetHostAddressesAsync của hệ điều hành.
    /// </summary>
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return Array.Empty<IPAddress>();
        }

        // Nếu chuỗi truyền vào đã là IP
        if (IPAddress.TryParse(host, out var parsedIp))
        {
            return [parsedIp];
        }

        // Kiểm tra bộ nhớ đệm
        if (_cache.TryGetValue(host, out var cached) && cached.Expiry > DateTime.UtcNow)
        {
            return cached.IPs;
        }

        // Thử phân giải qua DoH endpoints
        foreach (string endpointTemplate in DoHEndpoints)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                string requestUrl = string.Format(endpointTemplate, Uri.EscapeDataString(host));
                using var req = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                using var res = await _client.SendAsync(req, ct).ConfigureAwait(false);

                if (res.IsSuccessStatusCode)
                {
                    string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var (ips, ttlSeconds) = ParseDnsJson(json);
                    if (ips.Length > 0)
                    {
                        var expiry = DateTime.UtcNow.AddSeconds(Math.Clamp(ttlSeconds, 60, 3600));
                        _cache[host] = (ips, expiry);
                        return ips;
                    }
                }
            }
            catch
            {
                // Thử endpoint tiếp theo
            }
        }

        // Fallback về hệ điều hành nếu DoH gặp sự cố
        try
        {
            var systemIps = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            if (systemIps.Length > 0)
            {
                _cache[host] = (systemIps, DateTime.UtcNow.AddMinutes(5));
                return systemIps;
            }
        }
        catch
        {
            // Trả về mảng rỗng nếu không thể phân giải
        }

        return Array.Empty<IPAddress>();
    }

    private static (IPAddress[] IPs, int TTL) ParseDnsJson(string json)
    {
        var result = new List<IPAddress>();
        int minTtl = 300;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("Answer", out var answerArray) && answerArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in answerArray.EnumerateArray())
                {
                    // type 1: A record (IPv4), type 28: AAAA record (IPv6)
                    if (item.TryGetProperty("type", out var typeProp) && (typeProp.GetInt32() == 1 || typeProp.GetInt32() == 28))
                    {
                        if (item.TryGetProperty("data", out var dataProp))
                        {
                            string ipStr = dataProp.GetString() ?? string.Empty;
                            if (IPAddress.TryParse(ipStr, out var ip))
                            {
                                result.Add(ip);
                            }
                        }

                        if (item.TryGetProperty("TTL", out var ttlProp))
                        {
                            int ttl = ttlProp.GetInt32();
                            if (ttl > 0 && ttl < minTtl) minTtl = ttl;
                        }
                    }
                }
            }
        }
        catch
        {
            // Bỏ qua lỗi cú pháp JSON
        }

        return (result.ToArray(), minTtl);
    }
}
