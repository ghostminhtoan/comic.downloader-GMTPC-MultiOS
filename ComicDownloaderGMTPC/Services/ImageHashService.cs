using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ComicDownloaderGMTPC.Models;
using SkiaSharp;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Dịch vụ tính toán mã băm nhận thức (Perceptual Difference Hash - dHash 64-bit) cho ảnh bìa truyện tranh.
/// Giúp phát hiện chính xác các bản truyện trùng lặp dù khác tên, khác ngôn ngữ dịch (Anh, Nhật, Trung, Tây Ban Nha...),
/// hoặc bị nén lại/resize với tốc độ so sánh O(1) qua Bitwise XOR và PopCount.
/// </summary>
public static class ImageHashService
{
    /// <summary>
    /// Ngưỡng khoảng cách Hamming tối đa (0-64) để xem 2 ảnh là trùng nhau.
    /// Giá trị 6 tương đương độ tương đồng visual >= 90.6%, loại bỏ hoàn toàn false positive.
    /// </summary>
    public const int DefaultMaxHammingDistance = 6;

    /// <summary>
    /// Tính toán mã băm dHash 64-bit từ file ảnh trên ổ đĩa.
    /// </summary>
    public static ulong ComputeDHash(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return 0;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            return ComputeDHash(stream);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Tính toán mã băm dHash 64-bit từ mảng byte ảnh.
    /// </summary>
    public static ulong ComputeDHash(byte[] imageBytes)
    {
        if (imageBytes == null || imageBytes.Length < 64)
        {
            return 0;
        }

        try
        {
            using var ms = new MemoryStream(imageBytes);
            return ComputeDHash(ms);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Tính toán mã băm dHash 64-bit từ Stream ảnh.
    /// 1. Resize ảnh về kích thước 9x8 pixels (9 cột, 8 hàng).
    /// 2. Chuyển đổi thành độ sáng xám (Grayscale Luminance).
    /// 3. So sánh từng pixel với pixel kề phải (pixel[x] > pixel[x+1] => 1 bit; ngược lại 0).
    /// 4. Thu được chính xác 64 bit (8 hàng x 8 phép so sánh) đóng gói vào 1 ulong.
    /// </summary>
    public static ulong ComputeDHash(Stream stream)
    {
        try
        {
            using var codec = SKCodec.Create(stream);
            if (codec == null) return 0;

            using var original = SKBitmap.Decode(codec);
            if (original == null) return 0;

            var info = new SKImageInfo(9, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var resized = original.Resize(info, SKSamplingOptions.Default);
            if (resized == null) return 0;

            return ComputeDHashFrom9x8Bitmap(resized);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Tính toán mã băm dHash 64-bit từ SKBitmap đã được scale về 9x8 hoặc scale bitmap bất kỳ về 9x8.
    /// </summary>
    public static ulong ComputeDHash(SKBitmap originalBitmap)
    {
        if (originalBitmap == null || originalBitmap.Width <= 0 || originalBitmap.Height <= 0)
        {
            return 0;
        }

        try
        {
            if (originalBitmap.Width == 9 && originalBitmap.Height == 8)
            {
                return ComputeDHashFrom9x8Bitmap(originalBitmap);
            }

            var info = new SKImageInfo(9, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var resized = originalBitmap.Resize(info, SKSamplingOptions.Default);
            if (resized == null) return 0;

            return ComputeDHashFrom9x8Bitmap(resized);
        }
        catch
        {
            return 0;
        }
    }

    private static ulong ComputeDHashFrom9x8Bitmap(SKBitmap bitmap)
    {
        ulong hash = 0;
        int bitIndex = 0;

        // Ma trận 9 cột x 8 hàng -> tính độ sáng Grayscale Luminance
        Span<int> luminance = stackalloc int[9 * 8];

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 9; x++)
            {
                var color = bitmap.GetPixel(x, y);
                // Công thức độ sáng chuẩn Rec.601 (L = 0.299R + 0.587G + 0.114B)
                int lum = (color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000;
                luminance[y * 9 + x] = lum;
            }
        }

        // So sánh từng pixel với pixel kề phải (8 phép so sánh trên mỗi hàng)
        for (int y = 0; y < 8; y++)
        {
            int rowOffset = y * 9;
            for (int x = 0; x < 8; x++)
            {
                if (luminance[rowOffset + x] > luminance[rowOffset + x + 1])
                {
                    hash |= (1UL << bitIndex);
                }
                bitIndex++;
            }
        }

        return hash;
    }

    /// <summary>
    /// Tính khoảng cách Hamming giữa 2 mã băm dHash (số lượng bit khác biệt giữa 2 ảnh).
    /// Sử dụng phép toán Bitwise XOR kết hợp lệnh phần cứng CPU PopCount (0-1 xung nhịp).
    /// </summary>
    public static int ComputeHammingDistance(ulong hashA, ulong hashB)
    {
        if (hashA == 0 || hashB == 0) return 64; // Không thể so sánh nếu một trong 2 hash rỗng
        ulong xor = hashA ^ hashB;
        return BitOperations.PopCount(xor);
    }

    /// <summary>
    /// Đảm bảo tính toán dHash cho một truyện: kiểm tra file cache cover.dhash -> file thumbnail -> file cover gốc -> tải ảnh online.
    /// </summary>
    public static async Task<ulong> EnsureItemDHashAsync(ComicBookItem item, CancellationToken ct = default)
    {
        if (item == null) return 0;
        if (item.CoverDHash != 0) return item.CoverDHash;

        string cacheDir = ThumbnailPreviewService.Instance.GetItemCacheDirectory(item);
        string dhashFile = Path.Combine(cacheDir, "cover.dhash");

        // 1. Kiểm tra cache file dhash đã lưu trên đĩa
        if (File.Exists(dhashFile))
        {
            try
            {
                string text = (await File.ReadAllTextAsync(dhashFile, ct).ConfigureAwait(false)).Trim();
                if (ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out ulong cachedHash) && cachedHash != 0)
                {
                    item.CoverDHash = cachedHash;
                    return cachedHash;
                }
            }
            catch { }
        }

        // 2. Thử đọc từ thumbnail file hoặc cover gốc đã có
        string thumbFile = Path.Combine(cacheDir, "cover.thumb.jpg");
        string originalFile = Path.Combine(cacheDir, "cover.jpg");

        string? existingFile = null;
        if (File.Exists(thumbFile) && new FileInfo(thumbFile).Length > 0)
        {
            existingFile = thumbFile;
        }
        else if (File.Exists(originalFile) && new FileInfo(originalFile).Length > 0)
        {
            existingFile = originalFile;
        }

        ulong computedHash = 0;
        if (existingFile != null)
        {
            computedHash = ComputeDHash(existingFile);
        }

        // 3. Nếu chưa có ảnh trên đĩa, tải thumbnail qua ThumbnailPreviewService
        if (computedHash == 0 && !ct.IsCancellationRequested)
        {
            try
            {
                await ThumbnailPreviewService.Instance.EnsureThumbnailCacheAsync(item, ct, isPriority: true).ConfigureAwait(false);
                if (File.Exists(thumbFile))
                {
                    computedHash = ComputeDHash(thumbFile);
                }
                else if (File.Exists(originalFile))
                {
                    computedHash = ComputeDHash(originalFile);
                }
            }
            catch { }
        }

        // 4. Lưu lại hash vào item và file cache
        if (computedHash != 0)
        {
            item.CoverDHash = computedHash;
            try
            {
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);
                await File.WriteAllTextAsync(dhashFile, computedHash.ToString("X16"), ct).ConfigureAwait(false);
            }
            catch { }
        }

        return computedHash;
    }

    /// <summary>
    /// Quét và nhận diện toàn bộ các truyện trùng ảnh bìa trong danh sách.
    /// Gom nhóm các truyện có khoảng cách Hamming <= maxDistance (mặc định 6).
    /// </summary>
    public static async Task<ImageDuplicateResult> DetectImageDuplicatesAsync(
        IReadOnlyList<ComicBookItem> items,
        int maxDistance = DefaultMaxHammingDistance,
        Action<int, int>? onProgress = null,
        CancellationToken ct = default)
    {
        var result = new ImageDuplicateResult();
        if (items == null || items.Count == 0) return result;

        var targetItems = items.Where(x => x != null && !x.IsParallelSplitChild).ToList();
        if (targetItems.Count <= 1) return result;

        // 1. Tính toán dHash song song cho các truyện chưa có
        int completedCount = 0;
        int total = targetItems.Count;

        // Giới hạn số luồng xử lý đồng thời để tránh nghẽn I/O
        int parallelDegree = OperatingSystem.IsAndroid() ? 4 : 8;
        using var semaphore = new SemaphoreSlim(parallelDegree, parallelDegree);

        var tasks = targetItems.Select(async item =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (ct.IsCancellationRequested) return;
                await EnsureItemDHashAsync(item, ct).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
                int current = Interlocked.Increment(ref completedCount);
                onProgress?.Invoke(current, total);
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return result;

        // 2. Gom nhóm các truyện có dHash tương đồng (Clustering)
        // Lấy danh sách các truyện có dHash hợp lệ
        var validHashItems = targetItems.Where(x => x.CoverDHash != 0).ToList();

        // Thuật toán Union-Find (Disjoint Set) để gom cụm các ảnh trùng nhau
        var parent = new int[validHashItems.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]]; // Path compression
                i = parent[i];
            }
            return i;
        }

        void Union(int i, int j)
        {
            int rootI = Find(i);
            int rootJ = Find(j);
            if (rootI != rootJ)
            {
                parent[rootI] = rootJ;
            }
        }

        // So sánh cặp O(N^2) với phép XOR PopCount siêu tốc (< 1 microsecond mỗi cặp)
        for (int i = 0; i < validHashItems.Count; i++)
        {
            ulong hashA = validHashItems[i].CoverDHash;
            for (int j = i + 1; j < validHashItems.Count; j++)
            {
                ulong hashB = validHashItems[j].CoverDHash;
                int distance = ComputeHammingDistance(hashA, hashB);
                if (distance <= maxDistance)
                {
                    Union(i, j);
                }
            }
        }

        // Gom các nhóm lại
        var clusterGroups = new Dictionary<int, List<ComicBookItem>>();
        for (int i = 0; i < validHashItems.Count; i++)
        {
            int root = Find(i);
            if (!clusterGroups.TryGetValue(root, out var group))
            {
                group = new List<ComicBookItem>();
                clusterGroups[root] = group;
            }
            group.Add(validHashItems[i]);
        }

        // 3. Reset và gán cờ cho các item
        foreach (var item in targetItems)
        {
            item.IsImageDuplicate = false;
            item.ImageDuplicateGroupId = null;
        }

        int groupIndex = 1;
        var duplicateGroups = new List<List<ComicBookItem>>();

        foreach (var kvp in clusterGroups)
        {
            var group = kvp.Value;
            // Chỉ coi là trùng nếu nhóm có từ 2 truyện trở lên
            if (group.Count > 1)
            {
                string groupId = $"IMG_GRP_{groupIndex++}";
                foreach (var item in group)
                {
                    item.IsImageDuplicate = true;
                    item.ImageDuplicateGroupId = groupId;
                }
                duplicateGroups.Add(group);
            }
        }

        result.DuplicateGroups = duplicateGroups;
        result.TotalDuplicateBooks = duplicateGroups.Sum(g => g.Count);
        result.TotalGroupsCount = duplicateGroups.Count;

        return result;
    }
}

/// <summary>
/// Kết quả tổng hợp sau khi quét trùng lặp ảnh bìa.
/// </summary>
public sealed class ImageDuplicateResult
{
    public List<List<ComicBookItem>> DuplicateGroups { get; set; } = new();
    public int TotalDuplicateBooks { get; set; }
    public int TotalGroupsCount { get; set; }
}

