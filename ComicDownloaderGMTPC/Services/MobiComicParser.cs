using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using ComicDownloaderGMTPC.Models;
using SkiaSharp;

namespace ComicDownloaderGMTPC.Services;

public sealed class MobiPageInfo
{
    public int PageIndex { get; set; }
    public long StreamOffset { get; set; }
    public int StreamLength { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class MobiDocumentMetadata
{
    public string FilePath { get; set; } = string.Empty;
    public DateTime LastWriteTimeUtc { get; set; }
    public string Title { get; set; } = string.Empty;
    public List<MobiPageInfo> Pages { get; set; } = new();
}

/// <summary>
/// Bộ phân tích và trích xuất hình ảnh truyện tranh từ tệp định dạng MOBI / PRC / AZW.
/// Hoạt động 100% mã quản lý C# (.NET Managed Code), không phụ thuộc thư viện native ngoài.
/// Trích xuất ảnh theo cơ chế Zero-RAM On-Demand Seeking O(1).
/// </summary>
public sealed class MobiComicParser
{
    private static readonly Lazy<MobiComicParser> _lazy = new(() => new MobiComicParser());
    public static MobiComicParser Instance => _lazy.Value;

    private readonly ConcurrentDictionary<string, MobiDocumentMetadata> _metadataCache = new(StringComparer.OrdinalIgnoreCase);

    private MobiComicParser() { }

    public MobiDocumentMetadata InspectMobi(string mobiPath)
    {
        if (string.IsNullOrWhiteSpace(mobiPath) || !File.Exists(mobiPath))
        {
            return new MobiDocumentMetadata { FilePath = mobiPath ?? string.Empty };
        }

        string fullPath;
        DateTime lastWriteUtc;
        try
        {
            fullPath = Path.GetFullPath(mobiPath);
            lastWriteUtc = File.GetLastWriteTimeUtc(fullPath);
        }
        catch
        {
            fullPath = mobiPath;
            lastWriteUtc = DateTime.MinValue;
        }

        if (_metadataCache.TryGetValue(fullPath, out var cached) &&
            cached.LastWriteTimeUtc == lastWriteUtc &&
            cached.Pages.Count > 0)
        {
            return cached;
        }

        var parsed = ParseMobiStructure(fullPath, lastWriteUtc);
        if (parsed.Pages.Count > 0)
        {
            _metadataCache[fullPath] = parsed;
        }
        return parsed;
    }

    private static MobiDocumentMetadata ParseMobiStructure(string fullPath, DateTime lastWriteUtc)
    {
        var meta = new MobiDocumentMetadata
        {
            FilePath = fullPath,
            LastWriteTimeUtc = lastWriteUtc,
            Title = Path.GetFileNameWithoutExtension(fullPath)
        };

        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            long fileLength = fs.Length;
            if (fileLength < 80) return meta;

            // 1. Đọc Palm Database (PDB) header
            byte[] header = new byte[78];
            if (fs.Read(header, 0, 78) < 78) return meta;

            ushort numRecords = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(76, 2));
            if (numRecords <= 1) return meta;

            // 2. Đọc danh sách Record Offset (mỗi record 8 bytes: 4 bytes offset, 4 bytes id/attr)
            int recordListBytes = numRecords * 8;
            byte[] recordListData = new byte[recordListBytes];
            if (fs.Read(recordListData, 0, recordListBytes) < recordListBytes) return meta;

            var recordOffsets = new List<long>(numRecords);
            for (int i = 0; i < numRecords; i++)
            {
                uint off = BinaryPrimitives.ReadUInt32BigEndian(recordListData.AsSpan(i * 8, 4));
                recordOffsets.Add(off);
            }

            // 3. Phân tích Record 0 để tìm first_image_index trong MOBI header
            uint firstImageIndex = 0;
            if (recordOffsets.Count > 0)
            {
                long rec0Offset = recordOffsets[0];
                long rec0Len = (recordOffsets.Count > 1 ? recordOffsets[1] : fileLength) - rec0Offset;
                if (rec0Len >= 132)
                {
                    fs.Seek(rec0Offset, SeekOrigin.Begin);
                    byte[] rec0Header = new byte[Math.Min(256, (int)rec0Len)];
                    int r = fs.Read(rec0Header, 0, rec0Header.Length);
                    if (r >= 132)
                    {
                        // Check "MOBI" magic tại offset 16 của Record 0
                        if (rec0Header[16] == 0x4D && rec0Header[17] == 0x4F && rec0Header[18] == 0x42 && rec0Header[19] == 0x49)
                        {
                            // first_image_index nằm tại offset 108 (0x6C) tính từ MOBI header (tức offset 16 + 108 = 124 của Record 0)
                            firstImageIndex = BinaryPrimitives.ReadUInt32BigEndian(rec0Header.AsSpan(124, 4));
                        }
                    }
                }
            }

            // 4. Quét các records để tìm các trang ảnh
            int startScan = (firstImageIndex > 0 && firstImageIndex < numRecords) ? (int)firstImageIndex : 1;
            byte[] magicBuf = new byte[16];

            int localPageIdx = 0;
            for (int i = startScan; i < numRecords; i++)
            {
                long rOff = recordOffsets[i];
                long nextOff = (i < numRecords - 1) ? recordOffsets[i + 1] : fileLength;
                long rLen = nextOff - rOff;

                if (rLen < 16 || rLen > 50_000_000) continue;

                fs.Seek(rOff, SeekOrigin.Begin);
                int readBytes = fs.Read(magicBuf, 0, 16);
                if (readBytes < 4) continue;

                if (IsImageMagic(magicBuf, readBytes))
                {
                    meta.Pages.Add(new MobiPageInfo
                    {
                        PageIndex = localPageIdx++,
                        StreamOffset = rOff,
                        StreamLength = (int)rLen
                    });
                }
            }
        }
        catch { }

        return meta;
    }

    private static bool IsImageMagic(byte[] buf, int len)
    {
        if (len < 4) return false;

        // JPEG: FF D8 FF
        if (buf[0] == 0xFF && buf[1] == 0xD8 && buf[2] == 0xFF) return true;

        // PNG: 89 50 4E 47
        if (buf[0] == 0x89 && buf[1] == 0x50 && buf[2] == 0x4E && buf[3] == 0x47) return true;

        // GIF: 47 49 46 (GIF)
        if (buf[0] == 0x47 && buf[1] == 0x49 && buf[2] == 0x46) return true;

        // WebP: RIFF .... WEBP
        if (len >= 12 && buf[0] == 0x52 && buf[1] == 0x49 && buf[2] == 0x46 && buf[3] == 0x46 &&
            buf[8] == 0x57 && buf[9] == 0x45 && buf[10] == 0x42 && buf[11] == 0x50) return true;

        // BMP: BM
        if (buf[0] == 0x42 && buf[1] == 0x4D) return true;

        return false;
    }

    public byte[]? ReadPageBytes(string mobiPath, int pageIndex)
    {
        var meta = InspectMobi(mobiPath);
        if (meta.Pages.Count == 0 || pageIndex < 0 || pageIndex >= meta.Pages.Count) return null;

        var page = meta.Pages[pageIndex];
        try
        {
            using var fs = new FileStream(mobiPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            fs.Seek(page.StreamOffset, SeekOrigin.Begin);
            byte[] bytes = new byte[page.StreamLength];
            int read = fs.Read(bytes, 0, bytes.Length);
            return (read == bytes.Length) ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    public Bitmap? DecodePageBitmap(string mobiPath, int pageIndex)
    {
        byte[]? raw = ReadPageBytes(mobiPath, pageIndex);
        if (raw == null || raw.Length == 0) return null;

        try
        {
            using var ms = new MemoryStream(raw, writable: false);
            return new Bitmap(ms);
        }
        catch
        {
            try
            {
                using var data = SKData.CreateCopy(raw);
                using var codec = SKCodec.Create(data);
                if (codec == null) return null;

                using var bmp = SKBitmap.Decode(codec);
                if (bmp == null) return null;

                using var img = SKImage.FromBitmap(bmp);
                using var encoded = img.Encode(SKEncodedImageFormat.Jpeg, 92);
                using var stream = encoded.AsStream();
                return new Bitmap(stream);
            }
            catch
            {
                return null;
            }
        }
    }

    public Bitmap? DecodeCoverThumbnail(string mobiPath, int targetWidth)
    {
        var meta = InspectMobi(mobiPath);
        if (meta.Pages.Count == 0) return null;

        byte[]? raw = ReadPageBytes(mobiPath, 0);
        if (raw == null || raw.Length == 0) return null;

        try
        {
            using var data = SKData.CreateCopy(raw);
            using var codec = SKCodec.Create(data);
            if (codec == null) return null;

            int origW = codec.Info.Width;
            int origH = codec.Info.Height;
            if (origW <= 0 || origH <= 0) return null;

            int dstW = Math.Max(32, targetWidth);
            int dstH = Math.Max(32, (int)Math.Round((double)origH * dstW / origW));

            using var srcBmp = SKBitmap.Decode(codec);
            if (srcBmp == null) return null;

            var info = new SKImageInfo(dstW, dstH, srcBmp.ColorType, srcBmp.AlphaType);
            using var resizedBitmap = srcBmp.Resize(info, SKSamplingOptions.Default);
            if (resizedBitmap == null) return null;

            using var img = SKImage.FromBitmap(resizedBitmap);
            using var encoded = img.Encode(SKEncodedImageFormat.Jpeg, 85);
            using var stream = encoded.AsStream();
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    public List<LibraryChapterItem> ExpandMobiIntoChapters(string mobiPath, ref int nextIndex)
    {
        var result = new List<LibraryChapterItem>();
        var meta = InspectMobi(mobiPath);
        if (meta.Pages.Count == 0) return result;

        string baseTitle = meta.Title;
        if (string.IsNullOrWhiteSpace(baseTitle))
        {
            baseTitle = Path.GetFileNameWithoutExtension(mobiPath);
        }

        DateTime lastMod = DateTime.MinValue;
        try { lastMod = File.GetLastWriteTime(mobiPath); } catch { }

        int totalPages = meta.Pages.Count;

        // Nếu file MOBI dài (> 120 trang), chia thành các phần hợp lý để đọc mượt mà
        if (totalPages > 120)
        {
            int chunkSize = 80;
            int partNum = 1;
            for (int start = 0; start < totalPages; start += chunkSize)
            {
                int end = Math.Min(start + chunkSize - 1, totalPages - 1);
                int pCount = end - start + 1;
                string progressKey = $"{mobiPath}::mobi_part_{start}_{end}";

                result.Add(new LibraryChapterItem
                {
                    Index = nextIndex++,
                    Title = $"{baseTitle} - Phần {partNum} (Tr.{start + 1}-{end + 1})",
                    LocalPath = mobiPath,
                    ProgressKey = progressKey,
                    IsMobi = true,
                    MobiStartPageIndex = start,
                    MobiEndPageIndex = end,
                    TotalPages = pCount,
                    LastModified = lastMod
                });
                partNum++;
            }
        }
        else
        {
            result.Add(new LibraryChapterItem
            {
                Index = nextIndex++,
                Title = baseTitle,
                LocalPath = mobiPath,
                ProgressKey = mobiPath,
                IsMobi = true,
                MobiStartPageIndex = 0,
                MobiEndPageIndex = totalPages - 1,
                TotalPages = totalPages,
                LastModified = lastMod
            });
        }

        return result;
    }
}
