using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;
using ComicDownloaderGMTPC.Models;
using SkiaSharp;
using WebAssembly;
using WebAssembly.Runtime;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Model đại diện cho một trang ảnh của Cứu Truyện.
/// </summary>
public sealed class CuuTruyenPageItem
{
    public int Id { get; set; }
    public int Order { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
    public string DrmData { get; set; } = string.Empty;
    public bool HasDrm => !string.IsNullOrWhiteSpace(DrmData);
}

/// <summary>
/// Model kết quả phân tích tag / danh mục của Cứu Truyện.
/// </summary>
public sealed class CuuTruyenTagResult
{
    public string Domain { get; set; } = "cuutruyen";
    public string TagName { get; set; } = string.Empty;
    public string TagUrl { get; set; } = string.Empty;
    public int CurrentPage { get; set; } = 1;
    public int TotalPages { get; set; } = 1;
    public int TotalComics { get; set; }
    public List<ComicBookItem> Comics { get; set; } = new();
    public string StatusMessage { get; set; } = string.Empty;
}

/// <summary>
/// Định nghĩa các hàm export của WebAssembly module giải mã Cứu Truyện.
/// </summary>
public abstract class CuuDrmWasmExports
{
    public abstract UnmanagedMemory memory { get; }
    public abstract void render_image(int img, int ctx, int ptr, int len);
    public abstract int __wbindgen_malloc(int size);
    public abstract int __wbindgen_realloc(int ptr, int old_size, int new_size);
    public abstract void __wbindgen_exn_store(int ex);
}

/// <summary>
/// Engine giải mã WebAssembly thuần C# (.NET 10) cho ảnh xáo trộn (DRM Slice Scrambling) của Cứu Truyện.
/// Hoạt động 100% độc lập, không phụ thuộc trình duyệt ngoài, tương thích Windows, Linux, Android.
/// </summary>
public sealed class CuuTruyenDrmEngine
{
    private static readonly Lazy<CuuTruyenDrmEngine> _lazy = new(() => new CuuTruyenDrmEngine());
    public static CuuTruyenDrmEngine Instance => _lazy.Value;

    private readonly object _syncLock = new();
    private readonly InstanceCreator<CuuDrmWasmExports>? _wasmCreator;
    private CuuDrmWasmExports? _exports;
    private readonly List<(int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh)> _currentDrawCalls = new();
    private int _currentWidth = 2048;
    private bool _isInitialized;
    private int _callCount = 0;

    public bool IsAvailable => _wasmCreator != null;

    private CuuTruyenDrmEngine()
    {
        try
        {
            byte[]? wasmBytes = LoadWasmBytes();
            if (wasmBytes != null && wasmBytes.Length > 0)
            {
                using var stream = new MemoryStream(wasmBytes);
                _wasmCreator = Compile.FromBinary<CuuDrmWasmExports>(stream);
            }
        }
        catch
        {
            _wasmCreator = null;
        }
    }

    private static byte[]? LoadWasmBytes()
    {
        // 1. Thử nạp từ Avalonia AssetLoader
        try
        {
            var uri = new Uri("avares://ComicDownloaderGMTPC/Assets/cuutruyen_drm.wasm");
            if (AssetLoader.Exists(uri))
            {
                using var stream = AssetLoader.Open(uri);
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }
        catch
        {
            // Bỏ qua nếu môi trường test không có AssetLoader
        }

        // 2. Thử nạp từ thư mục ứng dụng trên đĩa
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string localPath = Path.Combine(appDir, "Assets", "cuutruyen_drm.wasm");
        if (File.Exists(localPath))
        {
            return File.ReadAllBytes(localPath);
        }

        // 3. Fallback thư mục làm việc hiện tại
        string curPath = Path.Combine(Directory.GetCurrentDirectory(), "Assets", "cuutruyen_drm.wasm");
        if (File.Exists(curPath))
        {
            return File.ReadAllBytes(curPath);
        }

        return null;
    }

    private unsafe void WriteInt32(int byteOffset, int value)
    {
        if (_exports?.memory == null) return;
        var ptr = (byte*)_exports.memory.Start.ToPointer() + byteOffset;
        *(int*)ptr = value;
    }

    private unsafe string GetStringFromWasm(int ptr, int len)
    {
        if (_exports?.memory == null) return string.Empty;
        byte* src = (byte*)_exports.memory.Start.ToPointer() + ptr;
        return Encoding.UTF8.GetString(src, len);
    }

    private unsafe (int ptr, int len) PassStringToWasm(string t)
    {
        if (_exports == null) return (0, 0);
        byte[] bytes = Encoding.UTF8.GetBytes(t);
        int ptr = _exports.__wbindgen_malloc(bytes.Length);
        byte* dest = (byte*)_exports.memory.Start.ToPointer() + ptr;
        Marshal.Copy(bytes, 0, (IntPtr)dest, bytes.Length);
        return (ptr, bytes.Length);
    }

    private void EnsureInstanceLocked()
    {
        if (_isInitialized && _exports != null) return;
        if (_wasmCreator == null) throw new InvalidOperationException("WASM module compiler is not available.");

        var cuudrm_bg = new Dictionary<string, RuntimeImport>
        {
            ["__wbindgen_object_drop_ref"] = new FunctionImport(new Action<int>((id) => { })),
            ["__wbg_instanceof_Window_c5579e140698a9dc"] = new FunctionImport(new Func<int, int>((id) => 1)),
            ["__wbg_document_508774c021174a52"] = new FunctionImport(new Func<int, int>((id) => 1)),
            ["__wbg_sessionStorage_263f344230ee7188"] = new FunctionImport(new Func<int, int>((id) => 1)),
            ["__wbg_querySelector_41d5da02fa776534"] = new FunctionImport(new Func<int, int, int, int>((doc, ptr, len) => 1)),

            ["__wbg_getItem_84095995ffbc84fc"] = new FunctionImport(new Action<int, int, int, int>((retPtr, storage, keyPtr, keyLen) =>
            {
                var (ptr, len) = PassStringToWasm("\u200b4283056037");
                WriteInt32(retPtr + 0, ptr);
                WriteInt32(retPtr + 4, len);
            })),

            ["__wbg_innerHTML_32dde7e4f768561a"] = new FunctionImport(new Action<int, int>((retPtr, el) =>
            {
                var (ptr, len) = PassStringToWasm("effort-mournful-county-flagstick");
                WriteInt32(retPtr + 0, ptr);
                WriteInt32(retPtr + 4, len);
            })),

            ["__wbg_textAlign_ccea4a063f2d1a6a"] = new FunctionImport(new Action<int, int>((retPtr, ctx) =>
            {
                var (ptr, len) = PassStringToWasm("right");
                WriteInt32(retPtr + 0, ptr);
                WriteInt32(retPtr + 4, len);
            })),

            ["__wbg_drawImage_b0048171a8941c61"] = new FunctionImport(new Action<int, int, double, double, double, double, double, double, double, double>((ctx, img, sx, sy, sw, sh, dx, dy, dw, dh) =>
            {
                _currentDrawCalls.Add(((int)sx, (int)sy, (int)sw, (int)sh, (int)dx, (int)dy, (int)dw, (int)dh));
            })),

            ["__wbg_width_9b5b21f79c220ac9"] = new FunctionImport(new Func<int, int>((img) => _currentWidth)),
            ["__wbg_crypto_70a96de3b6b73dac"] = new FunctionImport(new Func<int, int>((id) => 1)),
            ["__wbindgen_is_object"] = new FunctionImport(new Func<int, int>((id) => 1)),
            ["__wbg_process_dd1577445152112e"] = new FunctionImport(new Func<int, int>((id) => 0)),
            ["__wbg_versions_58036bec3add9e6f"] = new FunctionImport(new Func<int, int>((id) => 0)),
            ["__wbg_node_6a9d28205ed5b0d8"] = new FunctionImport(new Func<int, int>((id) => 0)),
            ["__wbindgen_is_string"] = new FunctionImport(new Func<int, int>((id) => 0)),
            ["__wbg_require_f05d779769764e82"] = new FunctionImport(new Func<int>(() => 0)),
            ["__wbindgen_string_new"] = new FunctionImport(new Func<int, int, int>((ptr, len) => 1)),
            ["__wbg_msCrypto_adbc770ec9eca9c7"] = new FunctionImport(new Func<int, int>((id) => 0)),
            ["__wbg_getRandomValues_3774744e221a22ad"] = new FunctionImport(new Action<int, int>((a, b) => { })),
            ["__wbg_randomFillSync_e950366c42764a07"] = new FunctionImport(new Action<int, int>((a, b) => { })),
            ["__wbindgen_is_function"] = new FunctionImport(new Func<int, int>((id) => 0)),
            ["__wbg_newnoargs_c9e6043b8ad84109"] = new FunctionImport(new Func<int, int, int>((ptr, len) => 1)),
            ["__wbg_call_557a2f2deacc4912"] = new FunctionImport(new Func<int, int, int>((fn, th) => 1)),
            ["__wbg_self_742dd6eab3e9211e"] = new FunctionImport(new Func<int>(() => 1)),
            ["__wbg_window_c409e731db53a0e2"] = new FunctionImport(new Func<int>(() => 1)),
            ["__wbg_globalThis_b70c095388441f2d"] = new FunctionImport(new Func<int>(() => 1)),
            ["__wbg_global_1c72617491ed7194"] = new FunctionImport(new Func<int>(() => 1)),
            ["__wbindgen_is_undefined"] = new FunctionImport(new Func<int, int>((id) => 0)),
            ["__wbg_call_587b30eea3e09332"] = new FunctionImport(new Func<int, int, int, int>((fn, th, arg) => 1)),
            ["__wbg_buffer_55ba7a6b1b92e2ac"] = new FunctionImport(new Func<int, int>((id) => 1)),
            ["__wbg_newwithbyteoffsetandlength_88d1d8be5df94b9b"] = new FunctionImport(new Func<int, int, int, int>((a, b, c) => 1)),
            ["__wbg_new_09938a7d020f049b"] = new FunctionImport(new Func<int, int>((id) => 1)),
            ["__wbg_set_3698e3ca519b3c3c"] = new FunctionImport(new Action<int, int, int>((a, b, c) => { })),
            ["__wbg_newwithlength_89eeca401d8918c2"] = new FunctionImport(new Func<int, int>((len) => 1)),
            ["__wbg_subarray_d82be056deb4ad27"] = new FunctionImport(new Func<int, int, int, int>((id, s, e) => 1)),
            ["__wbindgen_object_clone_ref"] = new FunctionImport(new Func<int, int>((id) => id)),
            ["__wbindgen_debug_string"] = new FunctionImport(new Action<int, int>((retPtr, id) =>
            {
                var (ptr, len) = PassStringToWasm($"[Object {id}]");
                WriteInt32(retPtr + 0, ptr);
                WriteInt32(retPtr + 4, len);
            })),
            ["__wbindgen_throw"] = new FunctionImport(new Action<int, int>((ptr, len) =>
            {
                string msg = GetStringFromWasm(ptr, len);
                throw new InvalidOperationException($"WASM Exception: {msg}");
            })),
            ["__wbindgen_memory"] = new FunctionImport(new Func<int>(() => 1))
        };

        var imports = new Dictionary<string, IDictionary<string, RuntimeImport>>
        {
            ["./cuudrm_bg.js"] = cuudrm_bg
        };

        var instance = _wasmCreator(imports);
        _exports = instance.Exports;
        _isInitialized = true;
    }

    /// <summary>
    /// Tính toán các mảnh cắt và toạ độ ghép (Draw Calls) từ DRM data của Cứu Truyện.
    /// </summary>
    public List<(int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh)> GetSliceDestinations(int width, int height, string drmData)
    {
        if (string.IsNullOrWhiteSpace(drmData)) return new List<(int, int, int, int, int, int, int, int)>();

        lock (_syncLock)
        {
            // Reset instance định kỳ sau mỗi 300 lần giải mã để làm sạch bộ nhớ WASM heap
            if (++_callCount > 300)
            {
                _callCount = 0;
                _exports = null;
                _isInitialized = false;
            }

            EnsureInstanceLocked();
            _currentWidth = width;
            _currentDrawCalls.Clear();

            string cleanDrm = drmData.Trim();
            var (drmPtr, drmLen) = PassStringToWasm(cleanDrm);

            _exports!.render_image(127, 126, drmPtr, drmLen);

            return new List<(int, int, int, int, int, int, int, int)>(_currentDrawCalls);
        }
    }

    /// <summary>
    /// Giải mã bức ảnh bị xáo trộn lát cắt của Cứu Truyện bằng SkiaSharp siêu tốc.
    /// </summary>
    public byte[] DescrambleImage(byte[] rawScrambledBytes, int width, int height, string drmData)
    {
        if (rawScrambledBytes == null || rawScrambledBytes.Length == 0) return Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(drmData)) return rawScrambledBytes;

        try
        {
            var slices = GetSliceDestinations(width, height, drmData);
            if (slices == null || slices.Count == 0) return rawScrambledBytes;

            using var srcBitmap = SKBitmap.Decode(rawScrambledBytes);
            if (srcBitmap == null) return rawScrambledBytes;

            int targetW = width > 0 ? width : srcBitmap.Width;
            int targetH = height > 0 ? height : srcBitmap.Height;

            using var dstBitmap = new SKBitmap(targetW, targetH);
            using var canvas = new SKCanvas(dstBitmap);

            foreach (var s in slices)
            {
                var srcRect = new SKRect(s.sx, s.sy, s.sx + s.sw, s.sy + s.sh);
                var dstRect = new SKRect(s.dx, s.dy, s.dx + s.dw, s.dy + s.dh);
                canvas.DrawBitmap(srcBitmap, srcRect, dstRect);
            }

            using var image = SKImage.FromBitmap(dstBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            return data.ToArray();
        }
        catch
        {
            // Fallback trả về ảnh gốc nếu giải mã gặp sự cố
            return rawScrambledBytes;
        }
    }
}

/// <summary>
/// Service chính cung cấp API bóc tách, tải và giải mã truyện từ Cứu Truyện (cuutruyen.net).
/// Tối ưu hóa đa luồng, connection pooling, tải theo thứ tự từ dưới lên và tuân thủ số connection.
/// </summary>
public sealed class CuuTruyenService
{
    private static readonly Lazy<CuuTruyenService> _lazy = new(() => new CuuTruyenService());
    public static CuuTruyenService Instance => _lazy.Value;

    private readonly HttpClient _httpClient;
    private readonly object _manifestLock = new();
    private static readonly ConcurrentDictionary<string, (IPAddress[] IPs, DateTime Expiry)> _dnsCache = new(StringComparer.OrdinalIgnoreCase);

    private CuuTruyenService()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                string host = context.DnsEndPoint.Host;
                IPAddress[]? ips = null;

                // Cache DNS trong RAM 30 phút để triệt tiêu độ trễ DoH lặp đi lặp lại
                if (_dnsCache.TryGetValue(host, out var cached) && cached.Expiry > DateTime.UtcNow)
                {
                    ips = cached.IPs;
                }
                else
                {
                    ips = await DoHResolver.Instance.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
                    if (ips == null || ips.Length == 0)
                    {
                        ips = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                    }
                    if (ips != null && ips.Length > 0)
                    {
                        _dnsCache[host] = (ips, DateTime.UtcNow.AddMinutes(30));
                    }
                }

                IPAddress ip = (ips != null && ips.Length > 0) ? ips[0] : (await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false))[0];
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                var netStream = new NetworkStream(socket, ownsSocket: true);
                return new SniFragmentStream(netStream);
            },
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(20),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 100,
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(45)
        };

        bool isAndroid = OperatingSystem.IsAndroid();
        string ua = isAndroid
            ? "Mozilla/5.0 (Linux; Android 12; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Mobile Safari/537.36"
            : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36";

        _httpClient.DefaultRequestHeaders.Add("User-Agent", ua);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");
        _httpClient.DefaultRequestHeaders.Add("Referer", "https://cuutruyen.net/");
        _httpClient.DefaultRequestHeaders.Add("Origin", "https://cuutruyen.net");
    }

    #region URL Helpers
    public static int ExtractMangaId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return 0;
        var match = Regex.Match(url, @"cuutruyen\.net/mangas/(?<id>\d+)", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups["id"].Value, out int id)) return id;
        return 0;
    }

    public static int ExtractChapterId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return 0;
        var match = Regex.Match(url, @"chapters/(?<id>\d+)", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups["id"].Value, out int id)) return id;
        return 0;
    }

    public static string ExtractTagSlug(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var match = Regex.Match(url, @"cuutruyen\.net/tag/(?<slug>[a-zA-Z0-9_\-]+)", RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups["slug"].Value;
        return string.Empty;
    }
    #endregion

    #region API Methods
    /// <summary>
    /// Bóc tách toàn bộ thông tin truyện và danh sách 100% chapters từ Cứu Truyện.
    /// Chắc chắn lấy đủ toàn bộ chapter (tương đương bấm xem thêm đến đáy)
    /// và sắp xếp theo đúng thứ tự từ dưới lên (từ Chương 1 -> Chương mới nhất).
    /// </summary>
    public async Task<ComicBookItem> GetMangaBookAsync(string url, int index = 0, CancellationToken ct = default)
    {
        int mangaId = ExtractMangaId(url);
        if (mangaId <= 0)
        {
            throw new InvalidOperationException($"URL truyện Cứu Truyện không hợp lệ: {url}");
        }

        var item = new ComicBookItem
        {
            Index = index,
            Url = url,
            Domain = "cuutruyen",
            Status = "Đang lấy thông tin...",
            StatusMessage = "Đang kết nối API Cứu Truyện..."
        };

        // 1. Lấy thông tin truyện: GET https://cuutruyen.net/api/v2/mangas/{mangaId}
        string mangaApiUrl = $"https://cuutruyen.net/api/v2/mangas/{mangaId}";
        using var mangaRes = await _httpClient.GetAsync(mangaApiUrl, ct).ConfigureAwait(false);
        mangaRes.EnsureSuccessStatusCode();

        string mangaJson = await mangaRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var mangaDoc = JsonDocument.Parse(mangaJson);
        var mangaData = mangaDoc.RootElement.GetProperty("data");

        item.Title = mangaData.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? $"Truyện {mangaId}" : $"Truyện {mangaId}";

        if (mangaData.TryGetProperty("author_name", out var authorNameProp))
        {
            item.PreviewArtist = authorNameProp.GetString() ?? string.Empty;
        }
        else if (mangaData.TryGetProperty("author", out var authorObj) && authorObj.ValueKind == JsonValueKind.Object && authorObj.TryGetProperty("name", out var aName))
        {
            item.PreviewArtist = aName.GetString() ?? string.Empty;
        }

        if (mangaData.TryGetProperty("cover_url", out var coverProp))
        {
            item.CoverUrl = coverProp.GetString() ?? string.Empty;
        }

        // 2. Lấy TOÀN BỘ danh sách chapter: GET https://cuutruyen.net/api/v2/mangas/{mangaId}/chapters
        string chaptersApiUrl = $"https://cuutruyen.net/api/v2/mangas/{mangaId}/chapters";
        using var chapsRes = await _httpClient.GetAsync(chaptersApiUrl, ct).ConfigureAwait(false);
        chapsRes.EnsureSuccessStatusCode();

        string chapsJson = await chapsRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var chapsDoc = JsonDocument.Parse(chapsJson);
        var chapsArray = chapsDoc.RootElement.GetProperty("data");

        var chaptersList = new List<ChapterItem>();
        if (chapsArray.ValueKind == JsonValueKind.Array)
        {
            // Mảng gốc của Cứu Truyện xếp từ chương mới nhất đến chương cũ nhất (từ trên xuống dưới).
            // Trên web, các chương cũ bị ẩn sau nút "Xem thêm" ở dưới đáy danh sách.
            // Để tải chapter đúng chuẩn "theo thứ tự từ dưới lên" (từ Chương 1 -> Chương mới nhất):
            // Ta đảo ngược (Reverse) mảng gốc để duyệt tuần tự từ dưới đáy lên trên!
            var reversedElements = chapsArray.EnumerateArray().Reverse().ToList();
            foreach (var ch in reversedElements)
            {
                int cId = ch.TryGetProperty("id", out var cIdProp) ? cIdProp.GetInt32() : 0;
                string cNum = ch.TryGetProperty("number", out var cNumProp) ? cNumProp.GetString() ?? string.Empty : string.Empty;
                string cName = ch.TryGetProperty("name", out var cNameProp) ? cNameProp.GetString() ?? string.Empty : string.Empty;

                double chapNum = 0;
                if (!string.IsNullOrWhiteSpace(cNum))
                {
                    var numMatch = Regex.Match(cNum, @"\d+(\.\d+)?");
                    if (numMatch.Success && double.TryParse(numMatch.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedNum))
                    {
                        chapNum = parsedNum;
                    }
                }

                string title = !string.IsNullOrWhiteSpace(cNum) ? $"Chương {cNum}" : $"Chương {cId}";
                if (!string.IsNullOrWhiteSpace(cName))
                {
                    title += $": {cName}";
                }

                string chapUrl = $"https://cuutruyen.net/mangas/{mangaId}/chapters/{cId}";

                chaptersList.Add(new ChapterItem
                {
                    ChapterNumber = chapNum,
                    Title = title,
                    Url = chapUrl,
                    Status = "Waiting"
                });
            }
        }

        item.Chapters = chaptersList;
        item.TotalChapters = chaptersList.Count;
        item.Status = "Hoàn tất";
        item.StatusMessage = $"Đã lấy đủ {chaptersList.Count} chương (theo thứ tự từ dưới lên).";

        return item;
    }

    /// <summary>
    /// Bóc tách danh sách các trang ảnh của 1 chapter Cứu Truyện.
    /// Ưu tiên CDN storage-charlie.cuutruyen.net cho tốc độ tải tối đa.
    /// </summary>
    public async Task<List<CuuTruyenPageItem>> ExtractChapterPagesAsync(string chapterUrl, CancellationToken ct = default)
    {
        int chapterId = ExtractChapterId(chapterUrl);
        if (chapterId <= 0)
        {
            throw new InvalidOperationException($"URL chapter Cứu Truyện không hợp lệ: {chapterUrl}");
        }

        string apiUrl = $"https://cuutruyen.net/api/v2/chapters/{chapterId}";
        using var res = await _httpClient.GetAsync(apiUrl, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();

        string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var pagesArray = doc.RootElement.GetProperty("data").GetProperty("pages");

        var result = new List<CuuTruyenPageItem>();
        if (pagesArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pagesArray.EnumerateArray())
            {
                int pId = p.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
                int pOrder = p.TryGetProperty("order", out var orderProp) ? orderProp.GetInt32() : 0;
                int pWidth = p.TryGetProperty("width", out var wProp) ? wProp.GetInt32() : 0;
                int pHeight = p.TryGetProperty("height", out var hProp) ? hProp.GetInt32() : 0;
                string drm = p.TryGetProperty("drm_data", out var drmProp) ? drmProp.GetString() ?? string.Empty : string.Empty;

                // Ưu tiên storage-charlie.cuutruyen.net để đạt tốc độ cao nhất (nhanh gấp 4 lần storage-bravo)
                string rawUrl = string.Empty;
                if (p.TryGetProperty("image_path", out var pathProp) && !string.IsNullOrWhiteSpace(pathProp.GetString()))
                {
                    rawUrl = "https://storage-charlie.cuutruyen.net" + pathProp.GetString();
                }
                else if (p.TryGetProperty("image_url", out var urlProp) && !string.IsNullOrWhiteSpace(urlProp.GetString()))
                {
                    rawUrl = urlProp.GetString()!
                        .Replace("https://storage-ct.lrclib.net", "https://storage-charlie.cuutruyen.net")
                        .Replace("https://storage-bravo.cuutruyen.net", "https://storage-charlie.cuutruyen.net")
                        .Replace("https://storage-ct-riften.site", "https://storage-charlie.cuutruyen.net");
                }

                result.Add(new CuuTruyenPageItem
                {
                    Id = pId,
                    Order = pOrder,
                    Width = pWidth,
                    Height = pHeight,
                    ImageUrl = rawUrl,
                    DrmData = drm
                });
            }
        }

        return result.OrderBy(p => p.Order).ToList();
    }

    /// <summary>
    /// Phân tích danh sách truyện theo tag có phân trang từ Cứu Truyện.
    /// </summary>
    public async Task<CuuTruyenTagResult> AnalyzeTagAsync(string tagUrl, int page = 1, CancellationToken ct = default)
    {
        string slug = ExtractTagSlug(tagUrl);
        if (string.IsNullOrWhiteSpace(slug))
        {
            throw new InvalidOperationException($"URL Tag Cứu Truyện không hợp lệ: {tagUrl}");
        }

        string apiUrl = $"https://cuutruyen.net/api/v2/tags/{slug}?page={page}&per_page=20";
        using var res = await _httpClient.GetAsync(apiUrl, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();

        string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        int totalPages = 1;
        int totalCount = 0;
        if (root.TryGetProperty("_metadata", out var meta))
        {
            if (meta.TryGetProperty("total_pages", out var tpProp)) totalPages = tpProp.GetInt32();
            if (meta.TryGetProperty("total_count", out var tcProp)) totalCount = tcProp.GetInt32();
        }

        string tagName = slug;
        var dataObj = root.GetProperty("data");
        if (dataObj.TryGetProperty("tag", out var tagObj) && tagObj.TryGetProperty("name", out var tnProp))
        {
            tagName = tnProp.GetString() ?? slug;
        }

        var comics = new List<ComicBookItem>();
        if (dataObj.TryGetProperty("mangas", out var mangasArray) && mangasArray.ValueKind == JsonValueKind.Array)
        {
            int idx = 1;
            foreach (var m in mangasArray.EnumerateArray())
            {
                int mId = m.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
                string mName = m.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? $"Truyện {mId}" : $"Truyện {mId}";
                string mCover = m.TryGetProperty("cover_url", out var cProp) ? cProp.GetString() ?? string.Empty : string.Empty;
                string mAuthor = m.TryGetProperty("author_name", out var aProp) ? aProp.GetString() ?? string.Empty : string.Empty;

                comics.Add(new ComicBookItem
                {
                    Index = idx++,
                    Title = mName,
                    Url = $"https://cuutruyen.net/mangas/{mId}",
                    CoverUrl = mCover,
                    PreviewArtist = mAuthor,
                    Domain = "cuutruyen",
                    Status = "Sẵn sàng"
                });
            }
        }

        return new CuuTruyenTagResult
        {
            Domain = "cuutruyen",
            TagName = tagName,
            TagUrl = tagUrl,
            CurrentPage = page,
            TotalPages = Math.Max(1, totalPages),
            TotalComics = totalCount > 0 ? totalCount : comics.Count,
            Comics = comics,
            StatusMessage = $"Tìm thấy {totalCount} truyện thuộc thể loại '{tagName}' ({totalPages} trang)."
        };
    }

    /// <summary>
    /// Tải toàn bộ các trang của 1 chapter Cứu Truyện, tự động giải mã DRM và lưu file.
    /// Tuân thủ nghiêm ngặt số connection (maxConnections) được cấu hình từ giao diện người dùng.
    /// </summary>
    public async Task<int> DownloadChapterPagesAsync(
        string chapterUrl,
        string chapterDir,
        IDictionary<string, string>? manifest,
        int maxConnections = 4,
        Action<int, int>? onProgress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(chapterDir);

        var pages = await ExtractChapterPagesAsync(chapterUrl, ct).ConfigureAwait(false);
        int total = pages.Count;
        if (total == 0) return 0;

        int concurrency = Math.Clamp(maxConnections, 1, 16);
        using var semaphore = new SemaphoreSlim(concurrency, concurrency);

        int doneCount = 0;
        var tasks = new List<Task>(pages.Count);

        for (int i = 0; i < pages.Count; i++)
        {
            int pageIndex = i;
            var page = pages[i];
            string fileName = $"{pageIndex + 1:D3}.jpg";
            string filePath = Path.Combine(chapterDir, fileName);

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();

                    // Kiểm tra nếu đã tải xong và có trong manifest
                    if (File.Exists(filePath) && new FileInfo(filePath).Length > 1024)
                    {
                        if (manifest != null)
                        {
                            lock (_manifestLock)
                            {
                                manifest[page.ImageUrl] = filePath;
                            }
                        }
                        int c = Interlocked.Increment(ref doneCount);
                        onProgress?.Invoke(c, total);
                        return;
                    }

                    // Tải raw bytes với fallback tự động giữa storage-charlie và storage-bravo
                    byte[]? rawBytes = null;
                    string primaryUrl = page.ImageUrl;
                    string fallbackUrl = primaryUrl.Contains("storage-charlie")
                        ? primaryUrl.Replace("storage-charlie", "storage-bravo")
                        : primaryUrl.Replace("storage-bravo", "storage-charlie");

                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, primaryUrl);
                        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                        if (res.IsSuccessStatusCode)
                        {
                            rawBytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        // Fallback sang server dự phòng
                    }

                    if (rawBytes == null || rawBytes.Length == 0)
                    {
                        using var reqFb = new HttpRequestMessage(HttpMethod.Get, fallbackUrl);
                        using var resFb = await _httpClient.SendAsync(reqFb, ct).ConfigureAwait(false);
                        resFb.EnsureSuccessStatusCode();
                        rawBytes = await resFb.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    }

                    // Giải mã nếu có DRM
                    byte[] finalBytes = rawBytes;
                    if (page.HasDrm)
                    {
                        finalBytes = CuuTruyenDrmEngine.Instance.DescrambleImage(rawBytes, page.Width, page.Height, page.DrmData);
                    }

                    await File.WriteAllBytesAsync(filePath, finalBytes, ct).ConfigureAwait(false);
                    if (manifest != null)
                    {
                        lock (_manifestLock)
                        {
                            manifest[page.ImageUrl] = filePath;
                        }
                    }

                    int current = Interlocked.Increment(ref doneCount);
                    onProgress?.Invoke(current, total);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return doneCount;
    }
    #endregion
}
