using System;
using System.IO;
using System.Text;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Trình phân tích container video siêu nhẹ thuần C# (0ms, 0 phụ thuộc thư viện ngoài).
/// Hỗ trợ đọc chính xác thời lượng (Duration) và kích thước (Width x Height) của các định dạng:
/// - MP4 / MOV / M4V / 3GP (chuẩn ISO/IEC 14496-12 qua Box 'mvhd')
/// - WebM / MKV (chuẩn Matroska/EBML qua Segment Info 'Duration' & 'TimestampScale')
/// Hoạt động 100% trên cả Windows, Linux và Android.
/// </summary>
public static class VideoMetadataParser
{
    public static long GetVideoDurationMs(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) return 0;

        try
        {
            string ext = Path.GetExtension(videoPath).ToLowerInvariant();
            if (ext == ".mp4" || ext == ".mov" || ext == ".m4v" || ext == ".3gp")
            {
                return ParseMp4DurationMs(videoPath);
            }
            if (ext == ".webm" || ext == ".mkv")
            {
                return ParseMatroskaDurationMs(videoPath);
            }
        }
        catch { }

        return 0;
    }

    public static bool GetVideoDimensions(string videoPath, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) return false;

        try
        {
            string ext = Path.GetExtension(videoPath).ToLowerInvariant();
            if (ext == ".mp4" || ext == ".mov" || ext == ".m4v" || ext == ".3gp")
            {
                if (ParseMp4Dimensions(videoPath, out width, out height))
                    return true;
            }
            else if (ext == ".webm" || ext == ".mkv")
            {
                if (ParseMatroskaDimensions(videoPath, out width, out height))
                    return true;
            }
        }
        catch { }

        return false;
    }

    #region MP4 / MOV Parser ('moov' -> 'mvhd')
    private static long ParseMp4DurationMs(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            long fileLength = fs.Length;
            while (fs.Position < fileLength - 8)
            {
                long boxStart = fs.Position;
                uint boxSize = ReadBigEndianUInt32(br);
                byte[] typeBytes = br.ReadBytes(4);
                if (typeBytes.Length < 4) break;
                string boxType = Encoding.ASCII.GetString(typeBytes);

                long actualBoxSize = boxSize;
                if (boxSize == 1) // 64-bit large box size
                {
                    actualBoxSize = (long)ReadBigEndianUInt64(br);
                }
                else if (boxSize == 0) // Kéo dài đến cuối file
                {
                    actualBoxSize = fileLength - boxStart;
                }

                if (actualBoxSize < 8) break;

                if (boxType == "moov")
                {
                    // Quét bên trong box 'moov' để tìm 'mvhd'
                    long moovEnd = boxStart + actualBoxSize;
                    while (fs.Position < moovEnd - 8)
                    {
                        long subStart = fs.Position;
                        uint subSize = ReadBigEndianUInt32(br);
                        byte[] subTypeBytes = br.ReadBytes(4);
                        if (subTypeBytes.Length < 4) break;
                        string subType = Encoding.ASCII.GetString(subTypeBytes);

                        long actualSubSize = subSize == 1 ? (long)ReadBigEndianUInt64(br) : subSize;
                        if (actualSubSize < 8) break;

                        if (subType == "mvhd")
                        {
                            byte version = br.ReadByte();
                            br.ReadBytes(3); // flags

                            uint timescale;
                            ulong duration;

                            if (version == 1) // 64-bit timestamps
                            {
                                br.ReadBytes(16); // creation + modification time
                                timescale = ReadBigEndianUInt32(br);
                                duration = ReadBigEndianUInt64(br);
                            }
                            else // 32-bit timestamps (version 0)
                            {
                                br.ReadBytes(8); // creation + modification time
                                timescale = ReadBigEndianUInt32(br);
                                duration = ReadBigEndianUInt32(br);
                            }

                            if (timescale > 0 && duration > 0)
                            {
                                return (long)Math.Round((double)duration * 1000.0 / timescale);
                            }
                            return 0;
                        }

                        fs.Position = subStart + actualSubSize;
                    }
                    break;
                }

                fs.Position = boxStart + actualBoxSize;
            }
        }
        catch { }

        return 0;
    }

    private static bool ParseMp4Dimensions(string filePath, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            long fileLength = fs.Length;
            while (fs.Position < fileLength - 8)
            {
                long boxStart = fs.Position;
                uint boxSize = ReadBigEndianUInt32(br);
                byte[] typeBytes = br.ReadBytes(4);
                if (typeBytes.Length < 4) break;
                string boxType = Encoding.ASCII.GetString(typeBytes);

                long actualBoxSize = boxSize == 1 ? (long)ReadBigEndianUInt64(br) : (boxSize == 0 ? fileLength - boxStart : boxSize);
                if (actualBoxSize < 8) break;

                if (boxType == "moov")
                {
                    long moovEnd = boxStart + actualBoxSize;
                    while (fs.Position < moovEnd - 8)
                    {
                        long trakStart = fs.Position;
                        uint trakSize = ReadBigEndianUInt32(br);
                        byte[] trakTypeBytes = br.ReadBytes(4);
                        if (trakTypeBytes.Length < 4) break;
                        string trakType = Encoding.ASCII.GetString(trakTypeBytes);

                        long actualTrakSize = trakSize == 1 ? (long)ReadBigEndianUInt64(br) : trakSize;
                        if (actualTrakSize < 8) break;

                        if (trakType == "trak")
                        {
                            long trakEnd = trakStart + actualTrakSize;
                            while (fs.Position < trakEnd - 8)
                            {
                                long subStart = fs.Position;
                                uint subSize = ReadBigEndianUInt32(br);
                                byte[] subTypeBytes = br.ReadBytes(4);
                                if (subTypeBytes.Length < 4) break;
                                string subType = Encoding.ASCII.GetString(subTypeBytes);

                                long actualSubSize = subSize == 1 ? (long)ReadBigEndianUInt64(br) : subSize;
                                if (actualSubSize < 8) break;

                                if (subType == "tkhd")
                                {
                                    byte version = br.ReadByte();
                                    br.ReadBytes(3); // flags

                                    if (version == 1)
                                    {
                                        br.ReadBytes(32); // creation(8) + mod(8) + track_id(4) + res(4) + duration(8)
                                    }
                                    else
                                    {
                                        br.ReadBytes(20); // creation(4) + mod(4) + track_id(4) + res(4) + duration(4)
                                    }

                                    br.ReadBytes(8 + 2 + 2 + 2 + 2); // res(8) + layer(2) + alt(2) + vol(2) + res(2) = 16 bytes
                                    int a = ReadBigEndianInt32(br);
                                    int b = ReadBigEndianInt32(br);
                                    br.ReadBytes(4); // u
                                    int c = ReadBigEndianInt32(br);
                                    int d = ReadBigEndianInt32(br);
                                    br.ReadBytes(16); // v(4) + x(4) + y(4) + w(4) = 16 bytes

                                    uint wFixed = ReadBigEndianUInt32(br);
                                    uint hFixed = ReadBigEndianUInt32(br);

                                    int trackW = (int)(wFixed >> 16);
                                    int trackH = (int)(hFixed >> 16);

                                    // Nếu video xoay 90 độ hoặc 270 độ (chuẩn matrix transformation của MP4 video dọc quay trên điện thoại)
                                    if (a == 0 && b != 0 && c != 0 && d == 0)
                                    {
                                        int temp = trackW;
                                        trackW = trackH;
                                        trackH = temp;
                                    }

                                    if (trackW > 0 && trackH > 0)
                                    {
                                        width = trackW;
                                        height = trackH;
                                        return true;
                                    }
                                }

                                fs.Position = subStart + actualSubSize;
                            }
                        }

                        fs.Position = trakStart + actualTrakSize;
                    }
                    break;
                }

                fs.Position = boxStart + actualBoxSize;
            }
        }
        catch { }

        return false;
    }
    #endregion

    #region WebM / MKV Parser (EBML Info -> Duration & TimestampScale)
    private static long ParseMatroskaDurationMs(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // Đọc 128KB đầu tiên (Info box của WebM/MKV luôn nằm trong vài KB đầu)
            int bufferSize = (int)Math.Min(131072, fs.Length);
            byte[] buf = new byte[bufferSize];
            int read = fs.Read(buf, 0, bufferSize);
            if (read < 16) return 0;

            // Kiểm tra EBML Header: 0x1A 0x45 0xDF 0xA3
            if (buf[0] != 0x1A || buf[1] != 0x45 || buf[2] != 0xDF || buf[3] != 0xA3) return 0;

            long timeScale = 1_000_000; // Mặc định trong Matroska là 1,000,000 ns (1ms)
            double durationRaw = 0;

            int i = 4;
            while (i < read - 6)
            {
                // Tìm TimestampScale: Element ID 0x2AD7B1 (3 bytes)
                if (buf[i] == 0x2A && buf[i + 1] == 0xD7 && buf[i + 2] == 0xB1)
                {
                    int lenIdx = i + 3;
                    if (lenIdx < read)
                    {
                        int len = buf[lenIdx] & 0x7F;
                        if (lenIdx + 1 + len <= read && len >= 1 && len <= 8)
                        {
                            long val = 0;
                            for (int b = 0; b < len; b++)
                            {
                                val = (val << 8) | buf[lenIdx + 1 + b];
                            }
                            if (val > 0) timeScale = val;
                        }
                    }
                }

                // Tìm Duration: Element ID 0x4489 (2 bytes)
                if (buf[i] == 0x44 && buf[i + 1] == 0x89)
                {
                    int lenIdx = i + 2;
                    if (lenIdx < read)
                    {
                        int len = buf[lenIdx] & 0x7F;
                        if (lenIdx + 1 + len <= read)
                        {
                            if (len == 4) // Float 32-bit big-endian
                            {
                                byte[] fBytes = new byte[4];
                                Array.Copy(buf, lenIdx + 1, fBytes, 0, 4);
                                if (BitConverter.IsLittleEndian) Array.Reverse(fBytes);
                                durationRaw = BitConverter.ToSingle(fBytes, 0);
                            }
                            else if (len == 8) // Double 64-bit big-endian
                            {
                                byte[] dBytes = new byte[8];
                                Array.Copy(buf, lenIdx + 1, dBytes, 0, 8);
                                if (BitConverter.IsLittleEndian) Array.Reverse(dBytes);
                                durationRaw = BitConverter.ToDouble(dBytes, 0);
                            }

                            if (durationRaw > 0)
                            {
                                double totalNs = durationRaw * timeScale;
                                return (long)Math.Round(totalNs / 1_000_000.0);
                            }
                        }
                    }
                }

                i++;
            }
        }
        catch { }

        return 0;
    }

    private static bool ParseMatroskaDimensions(string filePath, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int bufferSize = (int)Math.Min(262144, fs.Length);
            byte[] buf = new byte[bufferSize];
            int read = fs.Read(buf, 0, bufferSize);
            if (read < 16) return false;

            if (buf[0] != 0x1A || buf[1] != 0x45 || buf[2] != 0xDF || buf[3] != 0xA3) return false;

            int i = 4;
            while (i < read - 6)
            {
                // PixelWidth: Element ID 0xB0 (trong Video settings của Track)
                if (buf[i] == 0xB0 && i > 0 && buf[i - 1] != 0)
                {
                    int wLen = ParseEbmlVint(buf, i + 1, read, out int wVintBytes);
                    if (wLen >= 1 && wLen <= 4 && i + 1 + wVintBytes + wLen <= read)
                    {
                        int valW = 0;
                        for (int b = 0; b < wLen; b++)
                        {
                            valW = (valW << 8) | buf[i + 1 + wVintBytes + b];
                        }

                        // Tìm PixelHeight (0xBA) trong phạm vi 60 bytes tiếp theo
                        int searchEnd = Math.Min(read - 4, i + 1 + wVintBytes + wLen + 60);
                        for (int j = i + 1 + wVintBytes + wLen; j < searchEnd; j++)
                        {
                            if (buf[j] == 0xBA)
                            {
                                int hLen = ParseEbmlVint(buf, j + 1, read, out int hVintBytes);
                                if (hLen >= 1 && hLen <= 4 && j + 1 + hVintBytes + hLen <= read)
                                {
                                    int valH = 0;
                                    for (int b = 0; b < hLen; b++)
                                    {
                                        valH = (valH << 8) | buf[j + 1 + hVintBytes + b];
                                    }

                                    if (valW > 0 && valH > 0)
                                    {
                                        width = valW;
                                        height = valH;
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
                i++;
            }
        }
        catch { }

        return false;
    }

    private static int ParseEbmlVint(byte[] buf, int offset, int maxLen, out int vintBytes)
    {
        vintBytes = 0;
        if (offset >= maxLen) return 0;
        byte b = buf[offset];
        int mask = 0x80;
        int length = 1;
        while (mask > 0 && (b & mask) == 0)
        {
            mask >>= 1;
            length++;
        }
        if (length > 8 || offset + length > maxLen) return 0;
        vintBytes = length;
        int val = b & (mask - 1);
        for (int i = 1; i < length; i++)
        {
            val = (val << 8) | buf[offset + i];
        }
        return val;
    }
    #endregion

    #region Helpers
    private static uint ReadBigEndianUInt32(BinaryReader br)
    {
        byte[] bytes = br.ReadBytes(4);
        if (bytes.Length < 4) return 0;
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static int ReadBigEndianInt32(BinaryReader br)
    {
        return (int)ReadBigEndianUInt32(br);
    }

    private static ulong ReadBigEndianUInt64(BinaryReader br)
    {
        byte[] bytes = br.ReadBytes(8);
        if (bytes.Length < 8) return 0;
        return ((ulong)bytes[0] << 56) | ((ulong)bytes[1] << 48) | ((ulong)bytes[2] << 40) | ((ulong)bytes[3] << 32)
             | ((ulong)bytes[4] << 24) | ((ulong)bytes[5] << 16) | ((ulong)bytes[6] << 8) | bytes[7];
    }

    public static string FormatDuration(long durationMs)
    {
        if (durationMs <= 0) return "00:00";
        var ts = TimeSpan.FromMilliseconds(durationMs);
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
        return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
    #endregion
}
