using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using ComicDownloaderGMTPC.Models;
using SkiaSharp;

namespace ComicDownloaderGMTPC.Services;

public sealed class PdfPageStreamInfo
{
    public int PageIndex { get; set; }
    public int PageObjectId { get; set; }
    public int ImageObjectId { get; set; }
    public long StreamOffset { get; set; }
    public long StreamLength { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int BitsPerComponent { get; set; } = 8;
    public int Colors { get; set; } = 3;
    public int Predictor { get; set; } = 1;
    public string Filter { get; set; } = string.Empty;
    public string ColorSpace { get; set; } = "/DeviceRGB";
    public byte[]? PaletteBytes { get; set; }
}

public sealed class PdfDocumentMetadata
{
    public string FilePath { get; set; } = string.Empty;
    public DateTime LastWriteTimeUtc { get; set; }
    public List<PdfPageStreamInfo> Pages { get; set; } = new();
    public List<PdfBookmarkItem> Bookmarks { get; set; } = new();
}

/// <summary>
/// Bộ phân tích và giải mã tệp PDF truyện tranh tốc độ cao (Zero-RAM Bloat Stream Seeker).
/// Hỗ trợ:
/// - Nhảy trực tiếp qua các luồng nhị phân bằng FileStream.Seek (quét file PDF hàng GB chỉ trong vài ms).
/// - Giải mã trực tiếp luồng ảnh JPEG (/DCTDecode) và PNG/Zlib (/FlateDecode kèm PNG Predictor 10..15 hoặc Raw RGB/Gray/Indexed).
/// - Trích xuất cây mục lục chương (/Outlines Bookmarks) hỗ trợ cả UTF-16BE Hex (&lt;FEFF...&gt;) và chuỗi chuẩn PDF.
/// </summary>
public sealed class PdfComicParser
{
    private static readonly Lazy<PdfComicParser> _lazy = new(() => new PdfComicParser());
    public static PdfComicParser Instance => _lazy.Value;

    private readonly ConcurrentDictionary<string, PdfDocumentMetadata> _metadataCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex ObjHeaderRegex = new(@"(?:^|[\r\n])\s*(\d+)\s+(\d+)\s+obj\b", RegexOptions.Compiled);
    private static readonly Regex LengthDirectRegex = new(@"/Length\s+(\d+)(?!\s+\d+\s+R)", RegexOptions.Compiled);
    private static readonly Regex WidthRegex = new(@"/Width\s+(\d+)", RegexOptions.Compiled);
    private static readonly Regex HeightRegex = new(@"/Height\s+(\d+)", RegexOptions.Compiled);
    private static readonly Regex BitsRegex = new(@"/BitsPerComponent\s+(\d+)", RegexOptions.Compiled);
    private static readonly Regex PredictorRegex = new(@"/Predictor\s+(\d+)", RegexOptions.Compiled);
    private static readonly Regex ColorsRegex = new(@"/Colors\s+(\d+)", RegexOptions.Compiled);
    private static readonly Regex FilterRegex = new(@"/Filter\s*(?:\[\s*)?/([A-Za-z0-9]+)", RegexOptions.Compiled);
    private static readonly Regex IndexedPaletteHexRegex = new(@"/Indexed\s+/DeviceRGB\s+\d+\s*<([0-9A-Fa-f\s]+)>", RegexOptions.Compiled);
    private static readonly Regex XObjectRefRegex = new(@"/Im\d*\s+(\d+)\s+\d+\s+R", RegexOptions.Compiled);
    private static readonly Regex AnyXObjectRefRegex = new(@"/XObject\s*<<[^>]*/\S+\s+(\d+)\s+\d+\s+R", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex DestPageRefRegex = new(@"/Dest\s*\[\s*(\d+)\s+\d+\s+R", RegexOptions.Compiled);
    private static readonly Regex TitleHexRegex = new(@"/Title\s*<([0-9A-Fa-f\s]+)>", RegexOptions.Compiled);
    private static readonly Regex TitleLiteralRegex = new(@"/Title\s*\(((?:\\.|[^\\\)])*)\)", RegexOptions.Compiled);

    public PdfDocumentMetadata InspectPdf(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
        {
            return new PdfDocumentMetadata { FilePath = pdfPath ?? string.Empty };
        }

        string fullPath;
        DateTime lastWriteUtc;
        try
        {
            fullPath = Path.GetFullPath(pdfPath);
            lastWriteUtc = File.GetLastWriteTimeUtc(fullPath);
        }
        catch
        {
            fullPath = pdfPath;
            lastWriteUtc = DateTime.MinValue;
        }

        if (_metadataCache.TryGetValue(fullPath, out var cached) &&
            cached.LastWriteTimeUtc == lastWriteUtc &&
            cached.Pages.Count > 0)
        {
            return cached;
        }

        var parsed = ParsePdfStructure(fullPath, lastWriteUtc);
        if (parsed.Pages.Count > 0)
        {
            _metadataCache[fullPath] = parsed;
        }
        return parsed;
    }

    private static PdfDocumentMetadata ParsePdfStructure(string fullPath, DateTime lastWriteUtc)
    {
        var result = new PdfDocumentMetadata
        {
            FilePath = fullPath,
            LastWriteTimeUtc = lastWriteUtc
        };

        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536);
            long fileLen = fs.Length;
            if (fileLen < 32) return result;

            var imageByObjId = new Dictionary<int, PdfPageStreamInfo>();
            var imageInOrder = new List<PdfPageStreamInfo>();
            var pageToImgMap = new List<(int PageObjId, int ImgObjId)>();
            var rawBookmarks = new List<(int ObjId, string Title, int TargetPageObjId)>();

            byte[] buffer = new byte[65536];
            long pos = 0;

            while (pos < fileLen)
            {
                fs.Seek(pos, SeekOrigin.Begin);
                int bytesRead = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, fileLen - pos));
                if (bytesRead <= 0) break;

                string chunk = Encoding.Latin1.GetString(buffer, 0, bytesRead);
                var match = ObjHeaderRegex.Match(chunk);
                if (!match.Success)
                {
                    // Tiến tới đoạn tiếp theo (chừa lại 32 byte phòng trường hợp chữ "obj" bị cắt ngang biên buffer)
                    if (bytesRead < buffer.Length) break;
                    pos += buffer.Length - 32;
                    continue;
                }

                if (!int.TryParse(match.Groups[1].Value, out int objId))
                {
                    pos += match.Index + match.Length;
                    continue;
                }

                long objBodyStart = pos + match.Index + match.Length;
                fs.Seek(objBodyStart, SeekOrigin.Begin);

                int dictRead = fs.Read(buffer, 0, (int)Math.Min(16384, fileLen - objBodyStart));
                if (dictRead <= 0) break;

                string dictChunk = Encoding.Latin1.GetString(buffer, 0, dictRead);

                int endObjIdx = FindTokenIndex(dictChunk, "endobj");
                int streamIdx = FindStreamKeywordIndex(dictChunk, out int streamDataStartInChunk);

                bool hasStream = streamIdx >= 0 && (endObjIdx < 0 || streamIdx < endObjIdx);
                string headerText = hasStream
                    ? dictChunk.Substring(0, streamIdx)
                    : (endObjIdx >= 0 ? dictChunk.Substring(0, endObjIdx) : dictChunk);

                if (hasStream)
                {
                    long streamDataOffset = objBodyStart + streamDataStartInChunk;
                    long streamLen = 0;

                    var lenMatch = LengthDirectRegex.Match(headerText);
                    if (lenMatch.Success && long.TryParse(lenMatch.Groups[1].Value, out long parsedLen) && parsedLen >= 0)
                    {
                        streamLen = parsedLen;
                    }
                    else
                    {
                        streamLen = FindEndStreamDistance(fs, streamDataOffset, fileLen);
                    }

                    // Kiểm tra nếu đây là Image XObject (/Subtype /Image)
                    if (headerText.Contains("/Image", StringComparison.Ordinal) &&
                        headerText.Contains("/Subtype", StringComparison.Ordinal))
                    {
                        var imgInfo = ParseImageDictionary(objId, headerText, streamDataOffset, streamLen);
                        if (imgInfo.Width > 0 && imgInfo.Height > 0 && streamLen > 0)
                        {
                            imageByObjId[objId] = imgInfo;
                            imageInOrder.Add(imgInfo);
                        }
                    }

                    // Nhảy qua toàn bộ vùng dữ liệu nhị phân của stream
                    long nextPos = streamDataOffset + Math.Max(1, streamLen);
                    pos = Math.Min(fileLen, nextPos);
                }
                else
                {
                    // Đối tượng không có stream: kiểm tra /Type /Page hoặc Bookmark Outline Item
                    if (IsPageObjectHeader(headerText))
                    {
                        int imgRef = ExtractImageObjectIdFromPage(headerText);
                        pageToImgMap.Add((objId, imgRef));
                    }
                    else if (headerText.Contains("/Title", StringComparison.Ordinal) &&
                             headerText.Contains("/Dest", StringComparison.Ordinal))
                    {
                        var destMatch = DestPageRefRegex.Match(headerText);
                        if (destMatch.Success && int.TryParse(destMatch.Groups[1].Value, out int targetPageObjId))
                        {
                            string bmTitle = ExtractBookmarkTitle(headerText);
                            if (!string.IsNullOrWhiteSpace(bmTitle))
                            {
                                rawBookmarks.Add((objId, bmTitle, targetPageObjId));
                            }
                        }
                    }

                    if (endObjIdx >= 0)
                    {
                        pos = objBodyStart + endObjIdx + 6;
                    }
                    else
                    {
                        pos = objBodyStart + Math.Min(dictRead, 512);
                    }
                }
            }

            // 1. Xây dựng danh sách trang theo đúng thứ tự Page Object
            var pageObjIdToIndex = new Dictionary<int, int>();
            if (pageToImgMap.Count > 0 && imageByObjId.Count > 0)
            {
                int fallbackOrderedIdx = 0;
                foreach (var (pageObjId, imgObjId) in pageToImgMap)
                {
                    PdfPageStreamInfo? resolved = null;
                    if (imgObjId > 0 && imageByObjId.TryGetValue(imgObjId, out var found))
                    {
                        resolved = found;
                    }
                    else if (fallbackOrderedIdx < imageInOrder.Count)
                    {
                        resolved = imageInOrder[fallbackOrderedIdx];
                    }
                    fallbackOrderedIdx++;

                    if (resolved != null)
                    {
                        int newIndex = result.Pages.Count;
                        pageObjIdToIndex[pageObjId] = newIndex;
                        result.Pages.Add(new PdfPageStreamInfo
                        {
                            PageIndex = newIndex,
                            PageObjectId = pageObjId,
                            ImageObjectId = resolved.ImageObjectId,
                            StreamOffset = resolved.StreamOffset,
                            StreamLength = resolved.StreamLength,
                            Width = resolved.Width,
                            Height = resolved.Height,
                            BitsPerComponent = resolved.BitsPerComponent,
                            Colors = resolved.Colors,
                            Predictor = resolved.Predictor,
                            Filter = resolved.Filter,
                            ColorSpace = resolved.ColorSpace,
                            PaletteBytes = resolved.PaletteBytes
                        });
                    }
                }
            }

            // Nếu PDF có cấu trúc Resources gián tiếp, dùng danh sách Image XObject theo thứ tự xuất hiện
            if (result.Pages.Count == 0 && imageInOrder.Count > 0)
            {
                for (int i = 0; i < imageInOrder.Count; i++)
                {
                    var img = imageInOrder[i];
                    img.PageIndex = i;
                    result.Pages.Add(img);
                }
            }

            // 2. Ánh xạ Bookmarks sang PageIndex (0-based)
            if (rawBookmarks.Count > 0 && result.Pages.Count > 0)
            {
                foreach (var bm in rawBookmarks)
                {
                    if (pageObjIdToIndex.TryGetValue(bm.TargetPageObjId, out int pageIdx))
                    {
                        result.Bookmarks.Add(new PdfBookmarkItem
                        {
                            Title = bm.Title,
                            PageIndex = Math.Clamp(pageIdx, 0, result.Pages.Count - 1)
                        });
                    }
                }

                result.Bookmarks = result.Bookmarks
                    .OrderBy(b => b.PageIndex)
                    .ToList();
            }
        }
        catch { }

        return result;
    }

    public Bitmap? DecodePageBitmap(string pdfPath, int pageIndex)
    {
        try
        {
            var meta = InspectPdf(pdfPath);
            if (pageIndex < 0 || pageIndex >= meta.Pages.Count) return null;

            var pageInfo = meta.Pages[pageIndex];
            if (pageInfo.StreamLength <= 0 || pageInfo.StreamLength > 120 * 1024 * 1024) return null;

            byte[] rawStream = new byte[pageInfo.StreamLength];
            using (var fs = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536))
            {
                if (pageInfo.StreamOffset + pageInfo.StreamLength > fs.Length) return null;
                fs.Seek(pageInfo.StreamOffset, SeekOrigin.Begin);
                int totalRead = 0;
                while (totalRead < rawStream.Length)
                {
                    int r = fs.Read(rawStream, totalRead, rawStream.Length - totalRead);
                    if (r <= 0) break;
                    totalRead += r;
                }
            }

            return DecodeStreamToBitmap(rawStream, pageInfo, isThumbnail: false);
        }
        catch
        {
            return null;
        }
    }

    public Bitmap? DecodeCoverThumbnail(string pdfPath, int targetWidth = 280)
    {
        try
        {
            var meta = InspectPdf(pdfPath);
            if (meta.Pages.Count == 0) return null;

            var pageInfo = meta.Pages[0];
            if (pageInfo.StreamLength <= 0 || pageInfo.StreamLength > 80 * 1024 * 1024) return null;

            byte[] rawStream = new byte[pageInfo.StreamLength];
            using (var fs = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536))
            {
                if (pageInfo.StreamOffset + pageInfo.StreamLength > fs.Length) return null;
                fs.Seek(pageInfo.StreamOffset, SeekOrigin.Begin);
                int totalRead = 0;
                while (totalRead < rawStream.Length)
                {
                    int r = fs.Read(rawStream, totalRead, rawStream.Length - totalRead);
                    if (r <= 0) break;
                    totalRead += r;
                }
            }

            return DecodeStreamToBitmap(rawStream, pageInfo, isThumbnail: true, targetWidth: targetWidth);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? DecodeStreamToBitmap(byte[] rawStream, PdfPageStreamInfo info, bool isThumbnail, int targetWidth = 280)
    {
        if (rawStream == null || rawStream.Length == 0) return null;

        // 1. Nếu là JPEG (/DCTDecode) hoặc có magic header JPEG/PNG/WebP
        if (info.Filter.Equals("DCTDecode", StringComparison.OrdinalIgnoreCase) ||
            UniversalImageDecoder.IsJpeg(rawStream) ||
            UniversalImageDecoder.IsPng(rawStream) ||
            UniversalImageDecoder.IsWebP(rawStream))
        {
            try
            {
                using var ms = new MemoryStream(rawStream);
                if (isThumbnail)
                {
                    return Bitmap.DecodeToWidth(ms, targetWidth);
                }
                if (OperatingSystem.IsAndroid() && rawStream.Length > 4 * 1024 * 1024)
                {
                    return Bitmap.DecodeToWidth(ms, 1440);
                }
                return new Bitmap(ms);
            }
            catch
            {
                var (jpegBytes, _, _) = UniversalImageDecoder.ConvertToStandardJpeg(rawStream, 92);
                if (jpegBytes.Length > 0)
                {
                    using var ms2 = new MemoryStream(jpegBytes);
                    return isThumbnail ? Bitmap.DecodeToWidth(ms2, targetWidth) : new Bitmap(ms2);
                }
            }
        }

        // 2. Nếu là FlateDecode (PNG IDAT Predictor 10..15 hoặc Raw Deflate RGB/Gray/Indexed)
        if (info.Filter.Equals("FlateDecode", StringComparison.OrdinalIgnoreCase) || info.Width > 0)
        {
            byte[]? decompressed = TryDecompressZlibOrDeflate(rawStream);
            if (decompressed != null && decompressed.Length > 0)
            {
                // Trường hợp bên trong FlateDecode là một file ảnh hoàn chỉnh
                if (UniversalImageDecoder.IsJpeg(decompressed) || UniversalImageDecoder.IsPng(decompressed))
                {
                    using var ms = new MemoryStream(decompressed);
                    return isThumbnail ? Bitmap.DecodeToWidth(ms, targetWidth) : new Bitmap(ms);
                }

                using var skBmp = ReconstructBitmapFromFlatePixels(decompressed, info);
                if (skBmp != null)
                {
                    using var image = SKImage.FromBitmap(skBmp);
                    using var data = image.Encode(SKEncodedImageFormat.Jpeg, 93);
                    if (data != null)
                    {
                        using var outMs = new MemoryStream();
                        data.SaveTo(outMs);
                        outMs.Position = 0;
                        return isThumbnail ? Bitmap.DecodeToWidth(outMs, targetWidth) : new Bitmap(outMs);
                    }
                }
            }
        }

        return null;
    }

    private static byte[]? TryDecompressZlibOrDeflate(byte[] compressed)
    {
        // Thử giải nén chuẩn Zlib (RFC 1950 - có 2 byte header 0x78...)
        try
        {
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(compressed.Length * 2);
            zlib.CopyTo(output);
            return output.ToArray();
        }
        catch { }

        // Fallback: Raw Deflate (bỏ qua 2 byte đầu hoặc đọc từ offset 0)
        foreach (int skip in new[] { 0, 2 })
        {
            if (compressed.Length <= skip) continue;
            try
            {
                using var input = new MemoryStream(compressed, skip, compressed.Length - skip);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream(compressed.Length * 2);
                deflate.CopyTo(output);
                if (output.Length > 0)
                {
                    return output.ToArray();
                }
            }
            catch { }
        }

        return null;
    }

    private static SKBitmap? ReconstructBitmapFromFlatePixels(byte[] data, PdfPageStreamInfo info)
    {
        int width = info.Width;
        int height = info.Height;
        if (width <= 0 || height <= 0) return null;

        int bpc = info.BitsPerComponent > 0 ? info.BitsPerComponent : 8;
        int colors = info.Colors > 0 ? info.Colors : 3;
        if (info.ColorSpace.Contains("DeviceGray", StringComparison.OrdinalIgnoreCase) ||
            info.ColorSpace.Contains("Indexed", StringComparison.OrdinalIgnoreCase))
        {
            colors = 1;
        }
        else if (info.ColorSpace.Contains("DeviceCMYK", StringComparison.OrdinalIgnoreCase))
        {
            colors = 4;
        }

        int stride = (width * colors * bpc + 7) / 8;
        int bytesPerPixel = Math.Max(1, (colors * bpc + 7) / 8);

        bool hasPngFilterByte = info.Predictor >= 10 || data.Length == height * (stride + 1);
        byte[] unfiltered = new byte[height * stride];

        if (hasPngFilterByte && data.Length >= height * (stride + 1))
        {
            int srcOffset = 0;
            for (int y = 0; y < height; y++)
            {
                byte filterType = data[srcOffset++];
                int dstRowOffset = y * stride;
                int prevRowOffset = (y - 1) * stride;

                for (int x = 0; x < stride; x++)
                {
                    byte raw = data[srcOffset++];
                    byte a = x >= bytesPerPixel ? unfiltered[dstRowOffset + x - bytesPerPixel] : (byte)0;
                    byte b = y > 0 ? unfiltered[prevRowOffset + x] : (byte)0;
                    byte c = (y > 0 && x >= bytesPerPixel) ? unfiltered[prevRowOffset + x - bytesPerPixel] : (byte)0;

                    unfiltered[dstRowOffset + x] = filterType switch
                    {
                        0 => raw,
                        1 => (byte)(raw + a),
                        2 => (byte)(raw + b),
                        3 => (byte)(raw + ((a + b) >> 1)),
                        4 => (byte)(raw + PaethPredictor(a, b, c)),
                        _ => raw
                    };
                }
            }
        }
        else if (data.Length >= height * stride)
        {
            Buffer.BlockCopy(data, 0, unfiltered, 0, height * stride);
        }
        else
        {
            return null;
        }

        var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        byte[] rgba = new byte[width * height * 4];
        byte[]? palette = info.PaletteBytes;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            int dstRowStart = y * width * 4;

            for (int x = 0; x < width; x++)
            {
                int dstIdx = dstRowStart + (x * 4);
                if (colors == 3 && bpc == 8)
                {
                    int srcIdx = rowStart + (x * 3);
                    rgba[dstIdx] = unfiltered[srcIdx];
                    rgba[dstIdx + 1] = unfiltered[srcIdx + 1];
                    rgba[dstIdx + 2] = unfiltered[srcIdx + 2];
                    rgba[dstIdx + 3] = 255;
                }
                else if (colors == 1 && bpc == 8)
                {
                    byte val = unfiltered[rowStart + x];
                    if (palette != null && val * 3 + 2 < palette.Length)
                    {
                        rgba[dstIdx] = palette[val * 3];
                        rgba[dstIdx + 1] = palette[val * 3 + 1];
                        rgba[dstIdx + 2] = palette[val * 3 + 2];
                        rgba[dstIdx + 3] = 255;
                    }
                    else
                    {
                        rgba[dstIdx] = val;
                        rgba[dstIdx + 1] = val;
                        rgba[dstIdx + 2] = val;
                        rgba[dstIdx + 3] = 255;
                    }
                }
                else if (colors == 4 && bpc == 8)
                {
                    int srcIdx = rowStart + (x * 4);
                    int c = unfiltered[srcIdx];
                    int m = unfiltered[srcIdx + 1];
                    int yCol = unfiltered[srcIdx + 2];
                    int k = unfiltered[srcIdx + 3];
                    rgba[dstIdx] = (byte)Math.Clamp(255 - Math.Min(255, c * (255 - k) / 255 + k), 0, 255);
                    rgba[dstIdx + 1] = (byte)Math.Clamp(255 - Math.Min(255, m * (255 - k) / 255 + k), 0, 255);
                    rgba[dstIdx + 2] = (byte)Math.Clamp(255 - Math.Min(255, yCol * (255 - k) / 255 + k), 0, 255);
                    rgba[dstIdx + 3] = 255;
                }
            }
        }

        IntPtr ptr = bmp.GetPixels();
        if (ptr != IntPtr.Zero)
        {
            System.Runtime.InteropServices.Marshal.Copy(rgba, 0, ptr, rgba.Length);
            return bmp;
        }

        bmp.Dispose();
        return null;
    }

    private static byte PaethPredictor(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static PdfPageStreamInfo ParseImageDictionary(int objId, string headerText, long streamOffset, long streamLen)
    {
        var info = new PdfPageStreamInfo
        {
            ImageObjectId = objId,
            StreamOffset = streamOffset,
            StreamLength = streamLen
        };

        var wMatch = WidthRegex.Match(headerText);
        if (wMatch.Success && int.TryParse(wMatch.Groups[1].Value, out int w)) info.Width = w;

        var hMatch = HeightRegex.Match(headerText);
        if (hMatch.Success && int.TryParse(hMatch.Groups[1].Value, out int h)) info.Height = h;

        var bpcMatch = BitsRegex.Match(headerText);
        if (bpcMatch.Success && int.TryParse(bpcMatch.Groups[1].Value, out int bpc)) info.BitsPerComponent = bpc;

        var predMatch = PredictorRegex.Match(headerText);
        if (predMatch.Success && int.TryParse(predMatch.Groups[1].Value, out int pred)) info.Predictor = pred;

        var colMatch = ColorsRegex.Match(headerText);
        if (colMatch.Success && int.TryParse(colMatch.Groups[1].Value, out int cols)) info.Colors = cols;

        var filterMatch = FilterRegex.Match(headerText);
        if (filterMatch.Success) info.Filter = filterMatch.Groups[1].Value;

        if (headerText.Contains("/DeviceGray", StringComparison.Ordinal))
        {
            info.ColorSpace = "/DeviceGray";
            if (!colMatch.Success) info.Colors = 1;
        }
        else if (headerText.Contains("/Indexed", StringComparison.Ordinal))
        {
            info.ColorSpace = "/Indexed";
            if (!colMatch.Success) info.Colors = 1;
            var palMatch = IndexedPaletteHexRegex.Match(headerText);
            if (palMatch.Success)
            {
                info.PaletteBytes = ParseHexBytes(palMatch.Groups[1].Value);
            }
        }
        else if (headerText.Contains("/DeviceCMYK", StringComparison.Ordinal))
        {
            info.ColorSpace = "/DeviceCMYK";
            if (!colMatch.Success) info.Colors = 4;
        }
        else
        {
            info.ColorSpace = "/DeviceRGB";
        }

        return info;
    }

    private static bool IsPageObjectHeader(string headerText)
    {
        int idx = headerText.IndexOf("/Type", StringComparison.Ordinal);
        if (idx < 0) return false;
        return Regex.IsMatch(headerText, @"/Type\s*/Page\b(?!s)");
    }

    private static int ExtractImageObjectIdFromPage(string headerText)
    {
        var m1 = XObjectRefRegex.Match(headerText);
        if (m1.Success && int.TryParse(m1.Groups[1].Value, out int id1)) return id1;

        var m2 = AnyXObjectRefRegex.Match(headerText);
        if (m2.Success && int.TryParse(m2.Groups[1].Value, out int id2)) return id2;

        return 0;
    }

    private static string ExtractBookmarkTitle(string headerText)
    {
        var hexMatch = TitleHexRegex.Match(headerText);
        if (hexMatch.Success)
        {
            byte[] bytes = ParseHexBytes(hexMatch.Groups[1].Value);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2).Trim();
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2).Trim();
            }
            return Encoding.UTF8.GetString(bytes).Trim();
        }

        var litMatch = TitleLiteralRegex.Match(headerText);
        if (litMatch.Success)
        {
            string raw = litMatch.Groups[1].Value
                .Replace(@"\(", "(")
                .Replace(@"\)", ")")
                .Replace(@"\\", @"\");
            return raw.Trim();
        }

        return string.Empty;
    }

    private static byte[] ParseHexBytes(string hex)
    {
        var cleaned = new StringBuilder(hex.Length);
        foreach (char c in hex)
        {
            if (!char.IsWhiteSpace(c)) cleaned.Append(c);
        }
        if (cleaned.Length % 2 != 0) cleaned.Append('0');

        byte[] result = new byte[cleaned.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            if (byte.TryParse(cleaned.ToString(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            {
                result[i] = b;
            }
        }
        return result;
    }

    private static int FindTokenIndex(string text, string token)
    {
        return text.IndexOf(token, StringComparison.Ordinal);
    }

    private static int FindStreamKeywordIndex(string text, out int dataStartIndex)
    {
        dataStartIndex = -1;
        int searchFrom = 0;
        while (searchFrom < text.Length)
        {
            int idx = text.IndexOf("stream", searchFrom, StringComparison.Ordinal);
            if (idx < 0) return -1;

            // Đảm bảo không phải là "endstream"
            if (idx >= 3 && text.Substring(idx - 3, 3) == "end")
            {
                searchFrom = idx + 6;
                continue;
            }

            int after = idx + 6;
            if (after < text.Length && text[after] == '\r')
            {
                after++;
                if (after < text.Length && text[after] == '\n') after++;
                dataStartIndex = after;
                return idx;
            }
            if (after < text.Length && text[after] == '\n')
            {
                after++;
                dataStartIndex = after;
                return idx;
            }

            searchFrom = idx + 6;
        }
        return -1;
    }

    private static long FindEndStreamDistance(FileStream fs, long streamStart, long fileLen)
    {
        long savedPos = fs.Position;
        try
        {
            fs.Seek(streamStart, SeekOrigin.Begin);
            byte[] buf = new byte[65536];
            long cur = streamStart;
            while (cur < fileLen)
            {
                int r = fs.Read(buf, 0, (int)Math.Min(buf.Length, fileLen - cur));
                if (r <= 0) break;
                string s = Encoding.Latin1.GetString(buf, 0, r);
                int idx = s.IndexOf("endstream", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    long endPos = cur + idx;
                    if (endPos > streamStart && idx >= 1 && (s[idx - 1] == '\n' || s[idx - 1] == '\r')) endPos--;
                    if (endPos > streamStart && idx >= 2 && s[idx - 2] == '\r') endPos--;
                    return Math.Max(0, endPos - streamStart);
                }
                if (r < buf.Length) break;
                cur += buf.Length - 16;
                fs.Seek(cur, SeekOrigin.Begin);
            }
        }
        catch { }
        finally
        {
            fs.Position = savedPos;
        }
        return 0;
    }
}

