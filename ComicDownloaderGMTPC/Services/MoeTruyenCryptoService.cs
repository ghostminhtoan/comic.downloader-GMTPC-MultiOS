using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Service giải mã và tải truyện thuần C# cho Mòe Truyện (moetruyen.net).
/// Tự động giải mã bảo mật DRM IMGX (hỗ trợ cả Version 3 và Version 4):
/// - ECDH P-256 (NIST P-256) Key Exchange
/// - HKDF-SHA256 Key Derivation
/// - HMAC-SHA256 Token Proofs (chuẩn hóa chapterId số nguyên + pubHash)
/// - FNV-1a (32-bit) & Xorshift32 PRNG Key Unmasking
/// - AES-256-GCM Payload Decryption -> WebP nguyên bản chất lượng cao.
/// Hoạt động 100% độc lập, hỗ trợ cả Windows, Linux và Android mà không cần trình duyệt ngoài.
/// </summary>
public sealed class MoeTruyenCryptoService
{
    private static readonly Lazy<MoeTruyenCryptoService> _lazy = new(() => new MoeTruyenCryptoService());
    public static MoeTruyenCryptoService Instance => _lazy.Value;

    private readonly CookieContainer _cookieContainer;
    private readonly HttpClient _httpClient;

    private MoeTruyenCryptoService()
    {
        _cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookieContainer,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");
    }

    #region Base64Url Helpers
    public static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static byte[] Base64UrlDecode(string input)
    {
        string s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
    #endregion

    #region EC Key Helpers
    public static (byte[] rawBytes, string b64u) ExportP256PublicKey(ECDiffieHellman ecdh)
    {
        var p = ecdh.ExportParameters(false);
        byte[] raw = new byte[65];
        raw[0] = 0x04;
        Array.Copy(p.Q.X!, 0, raw, 1, 32);
        Array.Copy(p.Q.Y!, 0, raw, 33, 32);
        return (raw, Base64UrlEncode(raw));
    }

    public static ECDiffieHellmanPublicKey ImportP256PublicKey(byte[] uncompressedKey)
    {
        if (uncompressedKey.Length != 65 || uncompressedKey[0] != 0x04)
        {
            throw new ArgumentException("Invalid uncompressed P-256 public key length or header.");
        }

        byte[] x = new byte[32];
        byte[] y = new byte[32];
        Array.Copy(uncompressedKey, 1, x, 0, 32);
        Array.Copy(uncompressedKey, 33, y, 0, 32);

        var ecParams = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y }
        };
        using var tempEcdh = ECDiffieHellman.Create(ecParams);
        return tempEcdh.PublicKey;
    }
    #endregion

    #region Crypto Math (FNV-1a & Xorshift32 PRNG)
    public static uint Fnv1a(string str)
    {
        uint hash = 2166136261u;
        byte[] bytes = Encoding.UTF8.GetBytes(str);
        foreach (byte b in bytes)
        {
            hash = (hash ^ b) * 16777619u;
        }
        return hash == 0 ? 2654435769u : hash;
    }

    public static uint Xorshift32(uint state)
    {
        uint t = state;
        t ^= (t << 13);
        t ^= (t >> 17);
        t ^= (t << 5);
        return t;
    }

    public static byte[] DeriveMask(string seedStr, int length)
    {
        uint r = Fnv1a(seedStr);
        byte[] mask = new byte[length];
        for (int i = 0; i < length; i++)
        {
            if (i % 4 == 0)
            {
                r = Xorshift32(r + (uint)i + 2654435769u);
            }
            mask[i] = (byte)((r >> ((i % 4) * 8)) & 0xff);
        }
        return mask;
    }

    public static byte[] DecryptAesGcm(byte[] key, byte[] iv, byte[] ciphertextWithTag, byte[]? aad = null)
    {
        if (ciphertextWithTag.Length < 16)
        {
            throw new CryptographicException("Ciphertext is too short to contain auth tag.");
        }

        int ctLen = ciphertextWithTag.Length - 16;
        ReadOnlySpan<byte> ct = ciphertextWithTag.AsSpan(0, ctLen);
        ReadOnlySpan<byte> tag = ciphertextWithTag.AsSpan(ctLen, 16);
        byte[] plaintext = new byte[ctLen];

        using var aesGcm = new AesGcm(key, 16);
        aesGcm.Decrypt(iv, ct, tag, plaintext, aad);
        return plaintext;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        byte[] res = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, res, 0, a.Length);
        Buffer.BlockCopy(b, 0, res, a.Length, b.Length);
        return res;
    }

    private static string GetStringOrNumber(JsonElement elem, string prop, string fallback = "")
    {
        if (elem.TryGetProperty(prop, out var p))
        {
            return p.ValueKind switch
            {
                JsonValueKind.String => p.GetString() ?? fallback,
                JsonValueKind.Number => p.GetRawText(),
                _ => fallback
            };
        }
        return fallback;
    }
    #endregion

    #region Chapter Data Models
    public class MediaItem
    {
        public int pageIndex { get; set; }
        public string? storageKey { get; set; }
        public int width { get; set; }
        public int height { get; set; }
    }

    public class ChapterSessionData
    {
        public string Html { get; set; } = string.Empty;
        public string ChapterUrl { get; set; } = string.Empty;
        public string BootstrapUrl { get; set; } = string.Empty;
        public long ChapterId { get; set; }
        public string ReqPath { get; set; } = string.Empty;
        public List<MediaItem> MediaItems { get; set; } = new();
    }
    #endregion

    /// <summary>
    /// Lấy thông tin session và metadata ảnh từ trang chapter.
    /// </summary>
    public async Task<ChapterSessionData> FetchChapterSessionAsync(string chapterUrl, CancellationToken ct)
    {
        // Trích xuất book URL làm Referer chuẩn
        string refererUrl = Regex.Replace(chapterUrl, @"/chapters/\d+.*$", "");

        using var req = new HttpRequestMessage(HttpMethod.Get, chapterUrl);
        req.Headers.Add("Sec-Ch-Ua", "\"Not A(Brand\";v=\"8\", \"Chromium\";v=\"133\", \"Google Chrome\";v=\"133\"");
        req.Headers.Add("Sec-Ch-Ua-Mobile", "?0");
        req.Headers.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
        req.Headers.Add("Sec-Fetch-Dest", "document");
        req.Headers.Add("Sec-Fetch-Mode", "navigate");
        req.Headers.Add("Sec-Fetch-Site", "same-origin");
        req.Headers.Add("Referer", !string.IsNullOrWhiteSpace(refererUrl) ? refererUrl : "https://moetruyen.net/");

        using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        string html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var data = new ChapterSessionData
        {
            Html = html,
            ChapterUrl = chapterUrl
        };

        // Bootstrap URL
        var bootMatch = Regex.Match(html, @"bootstrapUrl:\s*[""']([^""']+)[""']");
        if (bootMatch.Success && !string.IsNullOrWhiteSpace(bootMatch.Groups[1].Value))
        {
            string bPath = bootMatch.Groups[1].Value.Trim();
            data.BootstrapUrl = bPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? bPath : $"https://moetruyen.net{bPath}";
        }
        else
        {
            var bootAttrMatch = Regex.Match(html, @"data-imgx-bootstrap-url=[""']([^""']+)[""']");
            if (bootAttrMatch.Success && !string.IsNullOrWhiteSpace(bootAttrMatch.Groups[1].Value))
            {
                string bPath = bootAttrMatch.Groups[1].Value.Trim();
                data.BootstrapUrl = bPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? bPath : $"https://moetruyen.net{bPath}";
            }
        }

        // ChapterId (bắt buộc lấy ID số nguyên từ database Mòe Truyện)
        var chapIdMatch = Regex.Match(html, @"chapterId:\s*(\d+)");
        if (chapIdMatch.Success && long.TryParse(chapIdMatch.Groups[1].Value, out long parsedId))
        {
            data.ChapterId = parsedId;
        }

        // ReqPath
        var uri = new Uri(chapterUrl);
        data.ReqPath = uri.AbsolutePath.TrimEnd('/') + "/page-access";

        // Media items
        var mediaMatch = Regex.Match(html, @"media:\s*(\[\s*\{[\s\S]*?\}\s*\])");
        if (mediaMatch.Success)
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<MediaItem>>(mediaMatch.Groups[1].Value);
                if (items != null) data.MediaItems = items;
            }
            catch { }
        }

        return data;
    }

    /// <summary>
    /// Tải toàn bộ các trang ảnh của một chapter Mòe Truyện, giải mã DRM IMGX thuần C# và lưu vào thư mục đích.
    /// </summary>
    public async Task<int> DownloadChapterPagesAsync(
        string chapterUrl,
        string destinationDir,
        Dictionary<string, string> manifest,
        Action<int, int>? onProgress,
        CancellationToken ct)
    {
        var session = await FetchChapterSessionAsync(chapterUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(session.BootstrapUrl))
        {
            throw new InvalidOperationException("Không thể tìm thấy Bootstrap URL trên trang chapter của Mòe Truyện.");
        }

        if (session.ChapterId <= 0)
        {
            throw new InvalidOperationException("Không thể tìm thấy ChapterId hợp lệ từ trang Mòe Truyện.");
        }

        if (session.MediaItems.Count == 0)
        {
            throw new InvalidOperationException("Không tìm thấy danh sách media trang ảnh trên Mòe Truyện.");
        }

        int totalPages = session.MediaItems.Count;
        int completedCount = 0;

        // Khởi tạo ECDH P-256 của Client
        using var clientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var (clientPubBytes, clientPubB64) = ExportP256PublicKey(clientEcdh);
        byte[] clientPubHashBytes = SHA256.HashData(clientPubBytes);
        string clientPubHashHex = Convert.ToHexStringLower(clientPubHashBytes);

        byte[] proofBytes = RandomNumberGenerator.GetBytes(32);
        string bootstrapProof = Base64UrlEncode(proofBytes);

        // 1. Gửi Bootstrap POST
        var bootPayload = new
        {
            readerPublicKey = clientPubB64,
            bootstrapProof = bootstrapProof,
            initialPageIndexes = new[] { session.MediaItems[0].pageIndex }
        };

        using var bootReq = new HttpRequestMessage(HttpMethod.Post, session.BootstrapUrl);
        bootReq.Headers.Add("Accept", "application/json");
        bootReq.Headers.Add("Referer", chapterUrl);
        bootReq.Content = new StringContent(JsonSerializer.Serialize(bootPayload), Encoding.UTF8, "application/json");

        using var bootRes = await _httpClient.SendAsync(bootReq, ct).ConfigureAwait(false);
        bootRes.EnsureSuccessStatusCode();
        string bootJsonStr = await bootRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var bootDoc = JsonDocument.Parse(bootJsonStr);
        var rootBoot = bootDoc.RootElement;

        if (!rootBoot.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
        {
            throw new InvalidOperationException($"Bootstrap Mòe Truyện từ chối yêu cầu: {bootJsonStr}");
        }

        double serverTime = rootBoot.GetProperty("serverTime").GetDouble();
        var sealedCap = rootBoot.GetProperty("sealedCapability");
        string capServerPubB64 = sealedCap.GetProperty("publicKey").GetString()!;
        string capIvB64 = sealedCap.GetProperty("iv").GetString()!;
        string capCtB64 = sealedCap.GetProperty("ciphertext").GetString()!;

        // 2. Giải mã sealedCapability
        byte[] capServerPubBytes = Base64UrlDecode(capServerPubB64);
        using var capServerPubKey = ImportP256PublicKey(capServerPubBytes);
        byte[] capSharedSecret = clientEcdh.DeriveRawSecretAgreement(capServerPubKey);

        byte[] capHkdfKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            capSharedSecret,
            32,
            Encoding.UTF8.GetBytes(bootstrapProof),
            Encoding.UTF8.GetBytes("imgx-reader-channel-v1"));

        byte[] capIv = Base64UrlDecode(capIvB64);
        byte[] capCtAll = Base64UrlDecode(capCtB64);

        string capAadStr = JsonSerializer.Serialize(new object[]
        {
            "imgx-reader-channel-v1",
            clientPubB64,
            capServerPubB64,
            bootstrapProof
        });
        byte[] capAad = Encoding.UTF8.GetBytes(capAadStr);

        byte[] capPlain = DecryptAesGcm(capHkdfKey, capIv, capCtAll, capAad);
        using var capPlainDoc = JsonDocument.Parse(Encoding.UTF8.GetString(capPlain));
        var capElem = capPlainDoc.RootElement[0];
        string readerInstanceId = capElem.GetProperty("readerInstanceId").GetString()!;
        string secretB64 = capElem.GetProperty("secret").GetString()!;
        byte[] secretBytes = Base64UrlDecode(secretB64);

        // 3. Chia các pageIndex thành từng chunk tối đa 10 trang
        var allPageIndexes = session.MediaItems.Select(m => m.pageIndex).ToList();
        var chunks = new List<List<int>>();
        for (int i = 0; i < allPageIndexes.Count; i += 10)
        {
            chunks.Add(allPageIndexes.Skip(i).Take(10).ToList());
        }

        int sequence = 0;
        var grantsList = new List<(JsonElement PageElem, byte[] SharedSecret, string ProofB64, string SealedPubB64)>();

        foreach (var chunk in chunks)
        {
            sequence++;
            long issuedAt = (long)serverTime;

            // Chú ý: session.ChapterId BẮT BUỘC là số nguyên long để JSON serialize thành số chuẩn
            string proofMsg = JsonSerializer.Serialize(new object[]
            {
                "imgx-page-access-proof-v3",
                readerInstanceId,
                session.ChapterId,
                session.ReqPath,
                "",
                chunk,
                issuedAt,
                sequence,
                clientPubHashHex
            });

            byte[] proofHmac = HMACSHA256.HashData(secretBytes, Encoding.UTF8.GetBytes(proofMsg));
            string proofB64 = Base64UrlEncode(proofHmac);

            var pageAccessBody = new
            {
                pageIndexes = chunk,
                pageAccessProof = new
                {
                    version = "imgx-page-access-proof-v3",
                    readerInstanceId = readerInstanceId,
                    issuedAt = issuedAt,
                    sequence = sequence,
                    proof = proofB64
                },
                readerPublicKey = clientPubB64
            };

            string accessUrl = $"https://moetruyen.net{session.ReqPath}";
            using var accessReq = new HttpRequestMessage(HttpMethod.Post, accessUrl);
            accessReq.Headers.Add("Accept", "application/json");
            accessReq.Headers.Add("Referer", chapterUrl);
            accessReq.Content = new StringContent(JsonSerializer.Serialize(pageAccessBody), Encoding.UTF8, "application/json");

            using var accessRes = await _httpClient.SendAsync(accessReq, ct).ConfigureAwait(false);
            accessRes.EnsureSuccessStatusCode();
            string accessJsonStr = await accessRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var accessDoc = JsonDocument.Parse(accessJsonStr);

            var sealedPages = accessDoc.RootElement.GetProperty("sealedPages");
            string sPubB64 = sealedPages.GetProperty("publicKey").GetString()!;
            string pIvB64 = sealedPages.GetProperty("iv").GetString()!;
            string pCtB64 = sealedPages.GetProperty("ciphertext").GetString()!;

            byte[] sPubBytes = Base64UrlDecode(sPubB64);
            using var sPubKey = ImportP256PublicKey(sPubBytes);
            byte[] sSharedSecret = clientEcdh.DeriveRawSecretAgreement(sPubKey);

            byte[] sHkdfKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                sSharedSecret,
                32,
                Encoding.UTF8.GetBytes(proofB64),
                Encoding.UTF8.GetBytes("imgx-reader-channel-v1"));

            byte[] pIv = Base64UrlDecode(pIvB64);
            byte[] pCtAll = Base64UrlDecode(pCtB64);

            string pAadStr = JsonSerializer.Serialize(new object[]
            {
                "imgx-reader-channel-v1",
                clientPubB64,
                sPubB64,
                proofB64
            });
            byte[] pAad = Encoding.UTF8.GetBytes(pAadStr);

            byte[] pPlain = DecryptAesGcm(sHkdfKey, pIv, pCtAll, pAad);
            var pagesDoc = JsonDocument.Parse(Encoding.UTF8.GetString(pPlain));

            foreach (var pElem in pagesDoc.RootElement.EnumerateArray())
            {
                grantsList.Add((pElem.Clone(), sSharedSecret, proofB64, sPubB64));
            }
        }

        // 4. Giải mã từng trang ảnh và tải trực tiếp
        for (int idx = 0; idx < grantsList.Count; idx++)
        {
            if (ct.IsCancellationRequested) break;

            var (pElem, sSharedSecret, proofB64, sPubB64) = grantsList[idx];
            int pageIndex = pElem.GetProperty("pageIndex").GetInt32();
            string storageKey = pElem.GetProperty("storageKey").GetString()!;
            string cleanStorageKey = storageKey.TrimStart('/');

            string pageFileName = $"{(idx + 1):D3}.webp";
            string pageFilePath = Path.Combine(destinationDir, pageFileName);

            // Kiểm tra nếu đã tải đủ và hợp lệ
            if (File.Exists(pageFilePath))
            {
                var fi = new FileInfo(pageFilePath);
                if (fi.Length > 512 && manifest.ContainsKey(pageFileName))
                {
                    completedCount++;
                    onProgress?.Invoke(completedCount, totalPages);
                    continue;
                }
            }

            var g = pElem.GetProperty("grant");
            string imageId = GetStringOrNumber(g, "imageId");
            string gVersion = GetStringOrNumber(g, "version");
            string gAlgorithm = GetStringOrNumber(g, "algorithm");
            long gIssuedAt = g.TryGetProperty("issuedAt", out var issProp) ? issProp.GetInt64() : 0;
            long gExpiresAt = g.TryGetProperty("expiresAt", out var expProp) ? expProp.GetInt64() : 0;
            string nonce = GetStringOrNumber(g, "nonce");
            string keyNonce = GetStringOrNumber(g, "keyNonce");
            string signature = GetStringOrNumber(g, "signature");

            var chKeys = g.GetProperty("channelKeys");
            string ckIvB64 = chKeys.GetProperty("iv").GetString()!;
            string ckCtB64 = chKeys.GetProperty("ciphertext").GetString()!;

            // Giải mã channelKeys
            byte[] ckHkdfKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                sSharedSecret,
                32,
                Encoding.UTF8.GetBytes(proofB64),
                Encoding.UTF8.GetBytes("IMGX-READER-PAGE-KEY-v1"));

            var binding = new[] { clientPubB64, sPubB64, proofB64 };
            string ckAadStr = JsonSerializer.Serialize(new object[]
            {
                "IMGX-READER-PAGE-KEY-v1",
                binding,
                pageIndex,
                storageKey,
                imageId,
                gIssuedAt,
                gExpiresAt,
                nonce,
                signature
            });
            byte[] ckAad = Encoding.UTF8.GetBytes(ckAadStr);
            byte[] ckIv = Base64UrlDecode(ckIvB64);
            byte[] ckCtAll = Base64UrlDecode(ckCtB64);

            byte[] ckPlain = DecryptAesGcm(ckHkdfKey, ckIv, ckCtAll, ckAad);
            using var grantKeysDoc = JsonDocument.Parse(Encoding.UTF8.GetString(ckPlain));

            // Mở khóa bằng Mask
            string seedStr = $"IMGX-GRANT-WRAP-v1.{gVersion}.{gAlgorithm}.{imageId}.{gIssuedAt}.{gExpiresAt}.{nonce}.{keyNonce}.{signature}.{cleanStorageKey}";
            byte[] mask = DeriveMask(seedStr, 32);

            // Tải payload nhị phân IMGX
            string dlUrl = $"https://i.truyen.moe/{storageKey}";
            using var imgReq = new HttpRequestMessage(HttpMethod.Get, dlUrl);
            imgReq.Headers.Add("Referer", "https://moetruyen.net/");
            using var imgRes = await _httpClient.SendAsync(imgReq, ct).ConfigureAwait(false);
            imgRes.EnsureSuccessStatusCode();
            byte[] encImg = await imgRes.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            if (encImg.Length < 13 || encImg[0] != 'I' || encImg[1] != 'M' || encImg[2] != 'G' || encImg[3] != 'X')
            {
                throw new InvalidOperationException($"Payload ảnh trang {idx + 1} không đúng định dạng IMGX chuẩn.");
            }

            int imgVersion = encImg[4];
            byte[] webpBytes;

            if (imgVersion == 4)
            {
                // Giải mã version 4 (XChaCha/AES Envelope)
                string wrappedV4KeyB64 = grantKeysDoc.RootElement.GetProperty("wrappedV4Key").GetString()!;
                byte[] wrappedV4KeyBytes = Base64UrlDecode(wrappedV4KeyB64);
                byte[] v4Key = new byte[32];
                for (int b = 0; b < 32; b++) v4Key[b] = (byte)(wrappedV4KeyBytes[b] ^ mask[b]);

                byte[] salt = encImg[13..45]; // 32 bytes
                byte[] infoBytes = Encoding.UTF8.GetBytes("IMGX-v4.envelope");
                byte[] derived64 = HKDF.DeriveKey(HashAlgorithmName.SHA256, v4Key, 64, salt, infoBytes);

                byte[] envKey = derived64[..32];
                byte[] payloadKey = derived64[32..64];

                byte[] envIv = encImg[45..57]; // 12 bytes
                byte[] envCtAll = encImg[57..78]; // 21 bytes

                byte[] ctxBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { "IMGX-v4", imageId, cleanStorageKey }));
                byte[] envAad = Concat(encImg[..57], ctxBytes);

                byte[] metaPlain = DecryptAesGcm(envKey, envIv, envCtAll, envAad);
                byte[] payloadAad = Concat(encImg[..78], ctxBytes);
                byte[] payloadData = encImg[78..];
                byte[] payloadIv = payloadData[..12];
                byte[] payloadCtAll = payloadData[12..];

                webpBytes = DecryptAesGcm(payloadKey, payloadIv, payloadCtAll, payloadAad);
            }
            else
            {
                // Giải mã version 3 (AES-256-GCM chuẩn)
                string wrappedKeyB64 = grantKeysDoc.RootElement.GetProperty("wrappedContentKey").GetString()!;
                byte[] wrappedKeyBytes = Base64UrlDecode(wrappedKeyB64);
                byte[] contentKey = new byte[32];
                for (int b = 0; b < 32; b++) contentKey[b] = (byte)(wrappedKeyBytes[b] ^ mask[b]);

                uint width = (uint)((encImg[5] << 24) | (encImg[6] << 16) | (encImg[7] << 8) | encImg[8]);
                uint height = (uint)((encImg[9] << 24) | (encImg[10] << 16) | (encImg[11] << 8) | encImg[12]);
                byte[] imgIv = encImg[13..25];
                byte[] imgCtAll = encImg[25..];
                string imgAadStr = $"IMGX-v3.{imageId}.{cleanStorageKey}.{width}.{height}";
                webpBytes = DecryptAesGcm(contentKey, imgIv, imgCtAll, Encoding.UTF8.GetBytes(imgAadStr));
            }

            await File.WriteAllBytesAsync(pageFilePath, webpBytes, ct).ConfigureAwait(false);

            lock (manifest)
            {
                manifest[pageFileName] = storageKey;
            }

            completedCount++;
            onProgress?.Invoke(completedCount, totalPages);
        }

        return completedCount;
    }
}
