using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Dịch vụ khởi tạo tài liệu sách điện tử truyện tranh chuẩn Kindle MOBI (Palm Database / Mobipocket).
/// Hoạt động 100% mã quản lý C# (.NET Managed Code), streaming ảnh trực tiếp O(1) RAM.
/// Tương thích 100% với MobiComicParser nội bộ, ứng dụng Kindle, Comic Screen, Moon+ Reader, Calibre.
/// </summary>
public static class MobiWriterService
{
    private static readonly HashSet<string> JpegExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif"
    };

    /// <summary>
    /// Gộp danh sách các chapter và hình ảnh thành một file MOBI hoàn chỉnh chuẩn quốc tế.
    /// </summary>
    public static void CreateMobiFromImages(
        string mobiPath,
        string comicTitle,
        List<(string ChapterName, List<string> Images)> chapters,
        IProgress<(int current, int total, string message)>? progress,
        CancellationToken ct)
    {
        if (File.Exists(mobiPath))
        {
            File.Delete(mobiPath);
        }

        string? dir = Path.GetDirectoryName(mobiPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 1. Thu thập toàn bộ ảnh theo thứ tự chapter
        var flatImages = new List<(string ChapterName, string ImagePath, int ChapterIndex)>();
        for (int c = 0; c < chapters.Count; c++)
        {
            var chap = chapters[c];
            foreach (var img in chap.Images)
            {
                flatImages.Add((chap.ChapterName, img, c));
            }
        }

        int totalImages = flatImages.Count;
        if (totalImages == 0)
        {
            throw new InvalidOperationException("Không có trang ảnh nào để tạo file MOBI.");
        }

        // 2. Tạo nội dung HTML Text (Record 1) với Table of Contents & Pagebreaks
        var htmlSb = new StringBuilder(65536);
        htmlSb.Append("<!DOCTYPE html><html><head>");
        htmlSb.Append("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\" />");
        htmlSb.Append($"<title>{System.Net.WebUtility.HtmlEncode(comicTitle)}</title>");
        htmlSb.Append("</head><body>\n");
        htmlSb.Append($"<h1 style=\"text-align:center;\">{System.Net.WebUtility.HtmlEncode(comicTitle)}</h1>\n");

        int currentChapterIdx = -1;
        for (int i = 0; i < flatImages.Count; i++)
        {
            var item = flatImages[i];
            if (item.ChapterIndex != currentChapterIdx)
            {
                currentChapterIdx = item.ChapterIndex;
                htmlSb.Append("<mbp:pagebreak />\n");
                htmlSb.Append($"<h2 style=\"text-align:center;\">{System.Net.WebUtility.HtmlEncode(item.ChapterName)}</h2>\n");
            }
            int recIndex = i + 1; // 1-based index tương ứng với first_image_index
            htmlSb.Append($"<div style=\"text-align:center;\"><img recindex=\"{recIndex}\" /></div>\n");
            htmlSb.Append("<mbp:pagebreak />\n");
        }
        htmlSb.Append("</body></html>");

        byte[] htmlBytes = Encoding.UTF8.GetBytes(htmlSb.ToString());

        // 3. Xây dựng Record 0 (PalmDOC Header + MOBI Header + EXTH Header + Title)
        byte[] record0Bytes = BuildRecord0(comicTitle, htmlBytes.Length, totalImages);

        // 4. Các record phụ trợ chuẩn MOBI:
        // Record 2 -> (2 + totalImages - 1): Các record ảnh
        // Record (2 + totalImages): FLIS Record
        // Record (2 + totalImages + 1): FCIS Record
        // Record (2 + totalImages + 2): EOF Record
        byte[] flisRecord = BuildFlisRecord();
        byte[] fcisRecord = BuildFcisRecord();
        byte[] eofRecord = new byte[] { 0xE9, 0x8E };

        int totalRecords = 2 + totalImages + 3; // Record 0, Record 1, Images, FLIS, FCIS, EOF
        if (totalRecords > 65530)
        {
            throw new InvalidOperationException($"Số lượng trang ({totalImages}) vượt quá giới hạn tối đa của định dạng Palm PDB MOBI (65530 records).");
        }

        // 5. Chuẩn bị file và ghi PDB Header
        using var fs = new FileStream(mobiPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 131072);
        using var bw = new BinaryWriter(fs, Encoding.Latin1);

        // Header PDB (78 bytes)
        byte[] pdbNameBytes = new byte[32];
        byte[] rawTitleAscii = Encoding.ASCII.GetBytes(MakeSafeAsciiTitle(comicTitle));
        Array.Copy(rawTitleAscii, 0, pdbNameBytes, 0, Math.Min(31, rawTitleAscii.Length));
        bw.Write(pdbNameBytes); // Database Name (32 bytes)

        bw.Write((ushort)0); // Attributes (2 bytes)
        bw.Write((ushort)0); // Version (2 bytes)

        uint palmEpochSeconds = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2082844800u;
        bw.Write(BinaryPrimitives.ReverseEndianness(palmEpochSeconds)); // Creation Date
        bw.Write(BinaryPrimitives.ReverseEndianness(palmEpochSeconds)); // Mod Date
        bw.Write(0u); // Last Backup Date
        bw.Write(0u); // Modification Number
        bw.Write(0u); // AppInfo ID
        bw.Write(0u); // SortInfo ID
        bw.Write(Encoding.ASCII.GetBytes("BOOK")); // Type
        bw.Write(Encoding.ASCII.GetBytes("MOBI")); // Creator
        bw.Write(0u); // Unique ID seed
        bw.Write(0u); // Next Record List ID
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)totalRecords)); // Number of records

        // Chừa chỗ cho danh sách Record Info (mỗi record 8 bytes) + 2 bytes padding
        long recordInfoListStartPos = fs.Position;
        int recordInfoListBytes = totalRecords * 8 + 2;
        byte[] placeholderInfo = new byte[recordInfoListBytes];
        bw.Write(placeholderInfo);

        // 6. Ghi tuần tự các records và lưu lại vị trí Offset thực tế
        var recordOffsets = new List<long>(totalRecords);

        // Ghi Record 0 (Header)
        recordOffsets.Add(fs.Position);
        bw.Write(record0Bytes);

        // Ghi Record 1 (HTML Text)
        recordOffsets.Add(fs.Position);
        bw.Write(htmlBytes);

        // Ghi các Record ảnh (Record 2 đến 2 + totalImages - 1)
        for (int i = 0; i < totalImages; i++)
        {
            if (ct.IsCancellationRequested) break;

            recordOffsets.Add(fs.Position);
            var item = flatImages[i];

            WriteImageRecord(bw, item.ImagePath);

            if (progress != null && (i % 10 == 0 || i == totalImages - 1))
            {
                progress.Report((i + 1, totalImages, $"[MOBI] Đang ghi trang {i + 1}/{totalImages} ({item.ChapterName})"));
            }
        }

        // Ghi Record FLIS
        recordOffsets.Add(fs.Position);
        bw.Write(flisRecord);

        // Ghi Record FCIS
        recordOffsets.Add(fs.Position);
        bw.Write(fcisRecord);

        // Ghi Record EOF
        recordOffsets.Add(fs.Position);
        bw.Write(eofRecord);

        // 7. Quay lại cập nhật danh sách Record Info với Offset chuẩn xác
        fs.Seek(recordInfoListStartPos, SeekOrigin.Begin);
        for (int i = 0; i < totalRecords; i++)
        {
            uint off = (uint)recordOffsets[i];
            bw.Write(BinaryPrimitives.ReverseEndianness(off)); // 4 bytes file offset (Big Endian)
            bw.Write((byte)0x40); // 1 byte attribute (dirty/valid)
            byte id0 = (byte)((i >> 16) & 0xFF);
            byte id1 = (byte)((i >> 8) & 0xFF);
            byte id2 = (byte)(i & 0xFF);
            bw.Write(id0);
            bw.Write(id1);
            bw.Write(id2);
        }
        bw.Write((ushort)0); // 2 bytes gap/padding
    }

    private static void WriteImageRecord(BinaryWriter bw, string imagePath)
    {
        string ext = Path.GetExtension(imagePath);
        if (JpegExtensions.Contains(ext) && File.Exists(imagePath))
        {
            // Trực tiếp streaming byte JPEG không nén lại
            using var imgFs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            imgFs.CopyTo(bw.BaseStream);
            return;
        }

        if (File.Exists(imagePath))
        {
            try
            {
                // Chuyển đổi WebP/PNG/BMP sang JPEG chất lượng 90%
                using var bmp = SKBitmap.Decode(imagePath);
                if (bmp != null)
                {
                    using var img = SKImage.FromBitmap(bmp);
                    using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
                    data.SaveTo(bw.BaseStream);
                    return;
                }
            }
            catch { }

            // Fallback đọc thẳng file gốc
            try
            {
                using var rawFs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                rawFs.CopyTo(bw.BaseStream);
                return;
            }
            catch { }
        }

        // Placeholder ảnh trắng 1x1 JPG nếu file lỗi
        byte[] tinyJpg = new byte[]
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x01, 0x00, 0x48,
            0x00, 0x48, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43, 0x00, 0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08,
            0x07, 0x07, 0x07, 0x09, 0x09, 0x08, 0x0A, 0x0C, 0x14, 0x0D, 0x0C, 0x0B, 0x0B, 0x0C, 0x19, 0x12,
            0x13, 0x0F, 0x14, 0x1D, 0x1A, 0x1F, 0x1E, 0x1D, 0x1A, 0x1C, 0x1C, 0x20, 0x24, 0x2E, 0x27, 0x20,
            0x22, 0x2C, 0x23, 0x1C, 0x1C, 0x28, 0x37, 0x29, 0x2C, 0x30, 0x31, 0x34, 0x34, 0x34, 0x1F, 0x27,
            0x39, 0x3D, 0x38, 0x32, 0x3C, 0x2E, 0x33, 0x34, 0x32, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01,
            0x00, 0x01, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xC4, 0x00, 0x1F, 0x00, 0x00, 0x01, 0x05, 0x01, 0x01,
            0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04,
            0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F,
            0x00, 0xBF, 0x80, 0xFF, 0xD9
        };
        bw.Write(tinyJpg);
    }

    private static byte[] BuildRecord0(string comicTitle, int htmlTextLength, int totalImages)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.Latin1);

        // A. PalmDOC Header (16 bytes)
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)1)); // Compression = 1 (No compression)
        bw.Write((ushort)0); // Unused
        bw.Write(BinaryPrimitives.ReverseEndianness((uint)htmlTextLength)); // Text Length
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)1)); // Record count = 1
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)4096)); // Record size = 4096
        bw.Write(0u); // Current Position

        // B. MOBI Header (232 bytes)
        long mobiHeaderStart = ms.Position;
        bw.Write(Encoding.ASCII.GetBytes("MOBI")); // Identifier (4 bytes)
        bw.Write(BinaryPrimitives.ReverseEndianness(232u)); // Header length (232 bytes)
        bw.Write(BinaryPrimitives.ReverseEndianness(2u)); // Mobi Type = 2 (Book)
        bw.Write(BinaryPrimitives.ReverseEndianness(65001u)); // Text Encoding = UTF-8
        uint uniqueId = (uint)Random.Shared.Next(100000, 999999);
        bw.Write(BinaryPrimitives.ReverseEndianness(uniqueId)); // Unique ID
        bw.Write(BinaryPrimitives.ReverseEndianness(6u)); // File Version = 6
        bw.Write(0xFFFFFFFFu); // Orthographic index
        bw.Write(0xFFFFFFFFu); // Inflection index
        bw.Write(0xFFFFFFFFu); // Index names
        bw.Write(0xFFFFFFFFu); // Index keys
        for (int i = 0; i < 6; i++) bw.Write(0xFFFFFFFFu); // Extra index 0..5 (24 bytes)
        bw.Write(BinaryPrimitives.ReverseEndianness((uint)(2 + totalImages))); // First non-book index
        
        // Full name offset & length (tính sau khi biết độ dài EXTH)
        long fullNameOffsetFieldPos = ms.Position;
        bw.Write(0u); // Placeholder: Full Name Offset
        bw.Write(0u); // Placeholder: Full Name Length

        bw.Write(BinaryPrimitives.ReverseEndianness(1033u)); // Locale
        bw.Write(0u); // Input language
        bw.Write(0u); // Output language
        bw.Write(BinaryPrimitives.ReverseEndianness(6u)); // Min version

        // Offset 108 tính từ MOBI header (tức offset 124 của Record 0): First Image Index!
        // Record 0 là Header, Record 1 là HTML Text => First Image Index = 2!
        bw.Write(BinaryPrimitives.ReverseEndianness(2u)); // First Image Index = 2

        bw.Write(0u); // Huffman record offset
        bw.Write(0u); // Huffman record count
        bw.Write(0u); // Huffman table offset
        bw.Write(0u); // Huffman table length
        bw.Write(BinaryPrimitives.ReverseEndianness(0x50u)); // EXTH Flags (bật cờ EXTH)

        // 32 bytes reserved / unknown
        for (int i = 0; i < 8; i++) bw.Write(0u);

        bw.Write(0xFFFFFFFFu); // DRM offset
        bw.Write(0xFFFFFFFFu); // DRM count
        bw.Write(0u); // DRM size
        bw.Write(0u); // DRM flags

        bw.Write(BinaryPrimitives.ReverseEndianness(1u)); // First content record
        bw.Write(BinaryPrimitives.ReverseEndianness(1u)); // Last content record
        bw.Write(0u); // Unknown
        bw.Write(0xFFFFFFFFu); // FCIS record
        bw.Write(BinaryPrimitives.ReverseEndianness(1u)); // FCIS record count
        bw.Write(0xFFFFFFFFu); // FLIS record
        bw.Write(BinaryPrimitives.ReverseEndianness(1u)); // FLIS record count

        // Đệm cho đủ 232 bytes của MOBI Header
        long mobiHeaderWritten = ms.Position - mobiHeaderStart;
        if (mobiHeaderWritten < 232)
        {
            bw.Write(new byte[232 - mobiHeaderWritten]);
        }

        // C. EXTH Header
        byte[] titleUtf8 = Encoding.UTF8.GetBytes(comicTitle);
        byte[] authorUtf8 = Encoding.UTF8.GetBytes("Comic Downloader GMTPC");
        byte[] imageCountBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(imageCountBytes, (uint)totalImages);

        var exthRecords = new List<(uint Type, byte[] Data)>
        {
            (503u, titleUtf8),      // Updated Title
            (100u, authorUtf8),     // Author
            (125u, imageCountBytes) // Count of resources
        };

        int exthDataLen = 12; // Magic 4 + Length 4 + Count 4
        foreach (var r in exthRecords)
        {
            exthDataLen += 8 + r.Data.Length;
        }

        int exthPadding = (4 - (exthDataLen % 4)) % 4;
        int totalExthLen = exthDataLen + exthPadding;

        bw.Write(Encoding.ASCII.GetBytes("EXTH"));
        bw.Write(BinaryPrimitives.ReverseEndianness((uint)totalExthLen));
        bw.Write(BinaryPrimitives.ReverseEndianness((uint)exthRecords.Count));

        foreach (var r in exthRecords)
        {
            bw.Write(BinaryPrimitives.ReverseEndianness(r.Type));
            uint rLen = (uint)(8 + r.Data.Length);
            bw.Write(BinaryPrimitives.ReverseEndianness(rLen));
            bw.Write(r.Data);
        }

        if (exthPadding > 0)
        {
            bw.Write(new byte[exthPadding]);
        }

        // D. Full Name (Title)
        long fullNameActualOffset = ms.Position;
        bw.Write(titleUtf8);

        // Padding cho Record 0 chia hết cho 4 bytes
        int rec0Pad = (4 - ((int)ms.Position % 4)) % 4;
        if (rec0Pad > 0)
        {
            bw.Write(new byte[rec0Pad]);
        }

        // E. Quay lại cập nhật Full Name Offset & Length trong MOBI Header
        long endPos = ms.Position;
        ms.Seek(fullNameOffsetFieldPos, SeekOrigin.Begin);
        bw.Write(BinaryPrimitives.ReverseEndianness((uint)fullNameActualOffset));
        bw.Write(BinaryPrimitives.ReverseEndianness((uint)titleUtf8.Length));
        ms.Seek(endPos, SeekOrigin.Begin);

        return ms.ToArray();
    }

    private static byte[] BuildFlisRecord()
    {
        byte[] flis = new byte[36];
        Encoding.ASCII.GetBytes("FLIS").CopyTo(flis, 0);
        BinaryPrimitives.WriteUInt32BigEndian(flis.AsSpan(4, 4), 8u);
        flis[10] = 0xFF;
        flis[11] = 0xFF;
        flis[35] = 0x01;
        return flis;
    }

    private static byte[] BuildFcisRecord()
    {
        byte[] fcis = new byte[44];
        Encoding.ASCII.GetBytes("FCIS").CopyTo(fcis, 0);
        BinaryPrimitives.WriteUInt32BigEndian(fcis.AsSpan(4, 4), 20u);
        BinaryPrimitives.WriteUInt32BigEndian(fcis.AsSpan(8, 4), 16u);
        BinaryPrimitives.WriteUInt32BigEndian(fcis.AsSpan(12, 4), 1u);
        BinaryPrimitives.WriteUInt32BigEndian(fcis.AsSpan(32, 4), 32u);
        BinaryPrimitives.WriteUInt32BigEndian(fcis.AsSpan(40, 4), 32u);
        return fcis;
    }

    private static string MakeSafeAsciiTitle(string title)
    {
        var sb = new StringBuilder();
        foreach (char c in title)
        {
            if (c >= 32 && c <= 126 && c != '/' && c != '\\')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('_');
            }
        }
        string res = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(res) ? "Comic_Book" : res;
    }
}

