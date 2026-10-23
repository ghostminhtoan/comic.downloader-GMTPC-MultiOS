// ====================================================================================================
// FILE SUMMARY: ComicReaderService.cs
// ----------------------------------------------------------------------------------------------------
// VAI TRÒ & MỤC ĐÍCH:
//   Dịch vụ Giải Mã & Nạp Dữ Liệu Truyện Tranh (Comic Archive Decoder & Loader Service).
//   Chịu trách nhiệm trích xuất và cung cấp danh sách ảnh chương truyện cho trình đọc.
//
// CÁC TÍNH NĂNG & THÀNH PHẦN CHÍNH:
//   - Hỗ trợ toàn diện định dạng: ZIP, CBZ, RAR, CBR, 7Z, PDF, MOBI và Thư mục ảnh rời.
//   - Tích hợp RarArchivePool và ZipArchivePool cho tốc độ mở file tức thì (0ms latency).
//   - Tự động sắp xếp ảnh theo thứ tự số tự nhiên (Natural Sort Comparer).
//
// LƯU Ý KỸ THUẬT & QUY TẮC BẢO TRÌ:
//   - Quản lý chặt chẽ vòng đời của Stream và Bitmap để tránh rò rỉ bộ nhớ (Memory Leak) khi đọc nhiều chapter liên tiếp.
// ====================================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ComicDownloaderGMTPC.Models;
using ComicDownloaderGMTPC.ViewModels;
using SkiaSharp;

namespace ComicDownloaderGMTPC.Services;

public sealed class ReaderVisualEffectSettings
{
    public double CropLeftPercent { get; set; }
    public double CropTopPercent { get; set; }
    public double CropRightPercent { get; set; }
    public double CropBottomPercent { get; set; }

    public bool IsGrayScale { get; set; }
    public bool IsAutoContrast { get; set; }
    public bool IsSharpenFilter { get; set; }
    public double SharpenStrength { get; set; } = 50.0;
    public bool IsDenoiseFilter { get; set; }
    public double DenoiseStrength { get; set; } = 30.0;
    public bool IsInvertColor { get; set; }
    public bool IsSaturationEnabled { get; set; }
    public double SaturationValue { get; set; } = 100.0;

    public bool HasCrop =>
        CropLeftPercent > 0.1 ||
        CropTopPercent > 0.1 ||
        CropRightPercent > 0.1 ||
        CropBottomPercent > 0.1;

    public bool HasPixelFilter =>
        IsGrayScale ||
        IsAutoContrast ||
        IsSharpenFilter ||
        IsDenoiseFilter ||
        IsInvertColor ||
        (IsSaturationEnabled && Math.Abs(SaturationValue - 100.0) > 1.0);

    public bool HasAnyEffect => HasCrop || HasPixelFilter;

    public ReaderVisualEffectSettings Clone()
    {
        return new ReaderVisualEffectSettings
        {
            CropLeftPercent = CropLeftPercent,
            CropTopPercent = CropTopPercent,
            CropRightPercent = CropRightPercent,
            CropBottomPercent = CropBottomPercent,
            IsGrayScale = IsGrayScale,
            IsAutoContrast = IsAutoContrast,
            IsSharpenFilter = IsSharpenFilter,
            SharpenStrength = SharpenStrength,
            IsDenoiseFilter = IsDenoiseFilter,
            DenoiseStrength = DenoiseStrength,
            IsInvertColor = IsInvertColor,
            IsSaturationEnabled = IsSaturationEnabled,
            SaturationValue = SaturationValue
        };
    }
}


public sealed class ComicReaderService
{
    private static readonly Lazy<ComicReaderService> _lazy = new(() => new ComicReaderService());
    public static ComicReaderService Instance => _lazy.Value;

    /// <summary>
    /// Delegate khởi chạy Activity đọc truyện độc lập trên Android (New Task Launch Mode / Multiple Tasks).
    /// Khi kích hoạt, Android sẽ mở ComicReaderActivity ở một Task hoàn toàn mới (hiển thị thành 2 thẻ độc lập trong Đa nhiệm Recents).
    /// </summary>
    public static Action<ComicReaderViewModel>? AndroidLaunchSeparateReaderTask { get; set; }

    /// <summary>
    /// Delegate khóa xoay màn hình trên Android (0 = Auto, 1 = Landscape, 2 = Portrait).
    /// </summary>
    public static Action<int>? AndroidSetOrientationMode { get; set; }

    /// <summary>
    /// Delegate điều chỉnh độ sáng hệ thống và giữ sáng màn hình trên Android
    /// (isAutoBrightness, brightnessPercent 0..100, keepScreenOn).
    /// </summary>
    public static Action<bool, double, bool>? AndroidApplyDisplaySettings { get; set; }

    public static bool IsAndroidSeparateReaderSupported => AndroidLaunchSeparateReaderTask != null;

    /// <summary>
    /// Tốc độ phát ảnh động toàn cục (0.1x đến 4.0x, mặc định 1.0x).
    /// </summary>
    public static double GlobalAnimationSpeed { get; set; } = 1.0;

    public ComicReaderViewModel? ActiveExternalReaderViewModel { get; set; }

    public Action? ActiveExternalReaderClosedCallback { get; set; }

    public ReaderVisualEffectSettings ActiveEffectSettings { get; set; } = new();

    public bool IsReaderActive { get; private set; }
    public event Action<bool>? ReaderActiveStateChanged;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _l1RawPageBytesCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _l1CacheKeysQueue = new();
    private const int MaxL1CachedPages = 60;

    private static void PutL1Cache(string key, byte[] data)
    {
        if (string.IsNullOrEmpty(key) || data == null || data.Length == 0) return;
        _l1RawPageBytesCache[key] = data;
        _l1CacheKeysQueue.Enqueue(key);
        while (_l1CacheKeysQueue.Count > MaxL1CachedPages)
        {
            if (_l1CacheKeysQueue.TryDequeue(out var oldKey))
            {
                _l1RawPageBytesCache.TryRemove(oldKey, out _);
            }
        }
    }

    private CancellationTokenSource? _chapterPrecacheCts;

    public void StartBackgroundChapterPrecaching(IList<ReaderPageItem> pages, int startIndex = 0, bool ecoMode = false)
    {
        if (pages == null || pages.Count == 0) return;

        try
        {
            _chapterPrecacheCts?.Cancel();
            _chapterPrecacheCts?.Dispose();
        }
        catch { }
        _chapterPrecacheCts = new CancellationTokenSource();
        var ct = _chapterPrecacheCts.Token;

        Task.Run(async () =>
        {
            try
            {
                // Độ trễ nhẹ 40ms để nhường trọn vẹn luồng UI render trang đầu tiên
                await Task.Delay(40, ct).ConfigureAwait(false);

                int count = pages.Count;
                int current = Math.Clamp(startIndex, 0, count - 1);

                // Tạo danh sách thứ tự ưu tiên: bắt đầu từ trang hiện tại tỏa ra 2 phía (trước rồi sau)
                var order = new List<int>();
                for (int offset = 1; offset < count; offset++)
                {
                    if (current + offset < count) order.Add(current + offset);
                    if (current - offset >= 0) order.Add(current - offset);
                }

                // Tối ưu hạ nhiệt độ máy: Thay vì decode toàn bộ 80 trang ảnh full size cùng lúc,
                // chỉ decode 3-5 trang gần nhất (1 trang trước, 3-4 trang sau). Các trang khác nạp theo sliding window khi cuộn tới gần.
                int maxFullBitmapPreload = (ecoMode || OperatingSystem.IsAndroid()) ? 3 : 5;
                int processedCount = 0;

                var parallelBatch = new List<Task>();
                foreach (int idx in order)
                {
                    if (ct.IsCancellationRequested) break;
                    if (idx < 0 || idx >= pages.Count) continue;
                    var page = pages[idx];

                    if (!page.IsLoaded && !page.IsLoading)
                    {
                        if (processedCount < maxFullBitmapPreload)
                        {
                            // Giải mã trực tiếp ra Bitmap đưa vào page.Image!
                            parallelBatch.Add(EnsurePageLoadedAsync(page, ct));
                            processedCount++;

                            // Xử lý tuần tự nhẹ nhàng 2 trang một lần, kèm yield nghỉ 10ms để CPU hạ nhiệt
                            if (parallelBatch.Count >= 2)
                            {
                                await Task.WhenAll(parallelBatch).ConfigureAwait(false);
                                parallelBatch.Clear();
                                await Task.Delay(10, ct).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            // Các trang ở rất xa trong file hàng ngàn trang: chỉ trích xuất raw bytes vào L1/Disk Cache
                            string subKey = page.IsMobiEntry ? $"mobi_p_{page.MobiPageIndex}"
                                          : page.IsPdfEntry ? $"pdf_p_{page.PdfPageIndex}"
                                          : !string.IsNullOrEmpty(page.ArchiveEntryFullName) ? page.ArchiveEntryFullName
                                          : Path.GetFileName(page.SourcePath);

                            string cacheKey = ComicDiskCacheService.BuildPageCacheKey(page.SourcePath, subKey);
                            if (!string.IsNullOrEmpty(cacheKey) && !_l1RawPageBytesCache.ContainsKey(cacheKey))
                            {
                                byte[]? raw = ReadRawPageBytes(page);
                                if (raw != null && raw.Length > 0)
                                {
                                    PutL1Cache(cacheKey, raw);
                                }
                            }
                        }
                    }
                }

                if (parallelBatch.Count > 0 && !ct.IsCancellationRequested)
                {
                    await Task.WhenAll(parallelBatch).ConfigureAwait(false);
                }
            }
            catch { }
        }, ct);
    }

    public void SetReaderActive(bool isActive)
    {
        if (IsReaderActive == isActive) return;
        IsReaderActive = isActive;
        ReaderActiveStateChanged?.Invoke(isActive);
    }

    private readonly SemaphoreSlim _decodeSemaphore = new(2, 2);

    public Task<List<ReaderPageItem>> LoadChapterPagesAsync(LibraryChapterItem chapter, IProgress<(double percent, string status)>? progress = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var pages = new List<ReaderPageItem>();
            if (chapter == null || string.IsNullOrWhiteSpace(chapter.LocalPath))
            {
                return pages;
            }

            try
            {
                if (chapter.IsMobi || LibraryScannerService.IsMobiFile(chapter.LocalPath))
                {
                    if (!File.Exists(chapter.LocalPath)) return pages;

                    progress?.Report((10, "Đang phân tích cấu trúc tệp MOBI..."));
                    var mobiMeta = MobiComicParser.Instance.InspectMobi(chapter.LocalPath);
                    if (mobiMeta.Pages.Count == 0) return pages;

                    int startPage = Math.Clamp(chapter.MobiStartPageIndex, 0, mobiMeta.Pages.Count - 1);
                    int endPage = chapter.MobiEndPageIndex >= startPage
                        ? Math.Min(chapter.MobiEndPageIndex, mobiMeta.Pages.Count - 1)
                        : (mobiMeta.Pages.Count - 1);

                    int total = Math.Max(1, endPage - startPage + 1);
                    int localIdx = 0;
                    for (int pIdx = startPage; pIdx <= endPage; pIdx++)
                    {
                        if (ct.IsCancellationRequested) break;
                        var pInfo = mobiMeta.Pages[pIdx];
                        var item = new ReaderPageItem
                        {
                            Index = localIdx,
                            Title = $"Trang {localIdx + 1}",
                            SourcePath = chapter.LocalPath,
                            ArchiveEntryFullName = string.Empty,
                            IsMobiEntry = true,
                            MobiPageIndex = pIdx,
                            FileSizeBytes = pInfo.StreamLength
                        };
                        pages.Add(item);
                        localIdx++;

                        if (localIdx % Math.Max(1, total / 20) == 0 || localIdx == total)
                        {
                            double pct = 15 + (80.0 * localIdx / total);
                            progress?.Report((pct, $"Đang nạp trang {localIdx}/{total} ({pct:F0}%)..."));
                        }
                    }
                }
                else if (chapter.IsPdf || LibraryScannerService.IsPdfFile(chapter.LocalPath))
                {
                    if (!File.Exists(chapter.LocalPath)) return pages;

                    progress?.Report((10, "Đang phân tích cấu trúc tệp PDF..."));
                    var pdfMeta = PdfComicParser.Instance.InspectPdf(chapter.LocalPath);
                    if (pdfMeta.Pages.Count == 0) return pages;

                    int startPage = Math.Clamp(chapter.PdfStartPageIndex, 0, pdfMeta.Pages.Count - 1);
                    int endPage = chapter.PdfEndPageIndex >= startPage
                        ? Math.Min(chapter.PdfEndPageIndex, pdfMeta.Pages.Count - 1)
                        : (pdfMeta.Pages.Count - 1);

                    int total = Math.Max(1, endPage - startPage + 1);
                    int localIdx = 0;
                    for (int pIdx = startPage; pIdx <= endPage; pIdx++)
                    {
                        if (ct.IsCancellationRequested) break;
                        var pInfo = pdfMeta.Pages[pIdx];
                        var item = new ReaderPageItem
                        {
                            Index = localIdx,
                            Title = $"Trang {localIdx + 1}",
                            SourcePath = chapter.LocalPath,
                            ArchiveEntryFullName = string.Empty,
                            IsPdfEntry = true,
                            PdfPageIndex = pIdx,
                            FileSizeBytes = pInfo.StreamLength
                        };
                        if (pInfo.Width > 0 && pInfo.Height > 0)
                        {
                            item.SetKnownDimensions(pInfo.Width, pInfo.Height);
                        }
                        pages.Add(item);
                        localIdx++;

                        if (localIdx % Math.Max(1, total / 20) == 0 || localIdx == total)
                        {
                            double pct = 15 + (80.0 * localIdx / total);
                            progress?.Report((pct, $"Đang nạp trang {localIdx}/{total} ({pct:F0}%)..."));
                        }
                    }
                }
                else if (chapter.IsRar || LibraryScannerService.IsRarFile(chapter.LocalPath))
                {
                    if (!File.Exists(chapter.LocalPath)) return pages;

                    progress?.Report((15, "Đang quét danh sách tệp nén RAR..."));
                    if (chapter.ArchiveEntryNames != null && chapter.ArchiveEntryNames.Count > 0)
                    {
                        int total = chapter.ArchiveEntryNames.Count;
                        for (int i = 0; i < total; i++)
                        {
                            if (ct.IsCancellationRequested) break;
                            string entryFullName = chapter.ArchiveEntryNames[i];
                            pages.Add(new ReaderPageItem
                            {
                                Index = i,
                                Title = Path.GetFileName(entryFullName.Replace('\\', '/')),
                                SourcePath = chapter.LocalPath,
                                ArchiveEntryFullName = entryFullName,
                                IsRarEntry = true
                            });

                            if ((i + 1) % Math.Max(1, total / 20) == 0 || i + 1 == total)
                            {
                                double pct = 20 + (75.0 * (i + 1) / total);
                                progress?.Report((pct, $"Đang chuẩn bị trang {i + 1}/{total} ({pct:F0}%)..."));
                            }
                        }
                    }
                    else
                    {
                        var imageEntries = RarArchivePool.Instance.GetImageEntriesInfo(chapter.LocalPath);
                        int total = imageEntries.Count;
                        for (int i = 0; i < total; i++)
                        {
                            if (ct.IsCancellationRequested) break;
                            var (fullName, name, length) = imageEntries[i];
                            pages.Add(new ReaderPageItem
                            {
                                Index = i,
                                Title = name,
                                SourcePath = chapter.LocalPath,
                                ArchiveEntryFullName = fullName,
                                IsRarEntry = true,
                                FileSizeBytes = length
                            });

                            if ((i + 1) % Math.Max(1, total / 20) == 0 || i + 1 == total)
                            {
                                double pct = 20 + (75.0 * (i + 1) / total);
                                progress?.Report((pct, $"Đang chuẩn bị trang {i + 1}/{total} ({pct:F0}%)..."));
                            }
                        }
                    }
                }
                else if (chapter.IsCbz || LibraryScannerService.IsArchiveFile(chapter.LocalPath))
                {
                    if (!File.Exists(chapter.LocalPath)) return pages;

                    progress?.Report((15, "Đang quét danh sách tệp nén ZIP/CBZ..."));
                    if (chapter.ArchiveEntryNames != null && chapter.ArchiveEntryNames.Count > 0)
                    {
                        int total = chapter.ArchiveEntryNames.Count;
                        for (int i = 0; i < total; i++)
                        {
                            if (ct.IsCancellationRequested) break;
                            string entryFullName = chapter.ArchiveEntryNames[i];
                            pages.Add(new ReaderPageItem
                            {
                                Index = i,
                                Title = Path.GetFileName(entryFullName.Replace('\\', '/')),
                                SourcePath = chapter.LocalPath,
                                ArchiveEntryFullName = entryFullName
                            });

                            if ((i + 1) % Math.Max(1, total / 20) == 0 || i + 1 == total)
                            {
                                double pct = 20 + (75.0 * (i + 1) / total);
                                progress?.Report((pct, $"Đang chuẩn bị trang {i + 1}/{total} ({pct:F0}%)..."));
                            }
                        }
                    }
                    else
                    {
                        var imageEntries = ZipArchivePool.Instance.GetImageEntriesInfo(chapter.LocalPath);
                        int total = imageEntries.Count;
                        for (int i = 0; i < total; i++)
                        {
                            if (ct.IsCancellationRequested) break;
                            var (fullName, name, length) = imageEntries[i];
                            pages.Add(new ReaderPageItem
                            {
                                Index = i,
                                Title = name,
                                SourcePath = chapter.LocalPath,
                                ArchiveEntryFullName = fullName,
                                FileSizeBytes = length
                            });

                            if ((i + 1) % Math.Max(1, total / 20) == 0 || i + 1 == total)
                            {
                                double pct = 20 + (75.0 * (i + 1) / total);
                                progress?.Report((pct, $"Đang chuẩn bị trang {i + 1}/{total} ({pct:F0}%)..."));
                            }
                        }
                    }
                }
                else if (Directory.Exists(chapter.LocalPath))
                {
                    progress?.Report((15, "Đang quét các file ảnh trong thư mục..."));
                    var enumOptions = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = false,
                        ReturnSpecialDirectories = false
                    };
                    var imageFiles = Directory.EnumerateFiles(chapter.LocalPath, "*", enumOptions)
                        .Where(LibraryScannerService.IsImageFile)
                        .NaturalSort();

                    int total = imageFiles.Count;
                    for (int i = 0; i < total; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        string file = imageFiles[i];
                        long size = 0;
                        try { size = new FileInfo(file).Length; } catch { }

                        var item = new ReaderPageItem
                        {
                            Index = i,
                            Title = Path.GetFileName(file),
                            SourcePath = file,
                            ArchiveEntryFullName = string.Empty,
                            FileSizeBytes = size
                        };

                        if (UniversalImageDecoder.TryGetFastJpegInfo(file, out int jw, out int jh) && jw > 0 && jh > 0)
                        {
                            item.SetKnownDimensions(jw, jh);
                        }
                        else if (UniversalImageDecoder.TryGetFastWebpInfo(file, out int ww, out int wh) && ww > 0 && wh > 0)
                        {
                            item.SetKnownDimensions(ww, wh);
                        }
                        else if (UniversalImageDecoder.TryGetFastPngInfo(file, out var pInfo) && pInfo.Width > 0 && pInfo.Height > 0)
                        {
                            item.SetKnownDimensions(pInfo.Width, pInfo.Height);
                        }

                        pages.Add(item);

                        if ((i + 1) % Math.Max(1, total / 20) == 0 || i + 1 == total)
                        {
                            double pct = 20 + (75.0 * (i + 1) / total);
                            progress?.Report((pct, $"Đang nạp trang {i + 1}/{total} ({pct:F0}%)..."));
                        }
                    }
                }

                progress?.Report((100, $"Đã nạp {pages.Count} trang. Đang khởi tạo giao diện..."));
            }
            catch { }

            return pages;
        }, ct);
    }

    public async Task EnsurePageLoadedAsync(ReaderPageItem? page, CancellationToken ct = default)
    {
        if (page == null || page.IsLoaded || page.IsLoading || ct.IsCancellationRequested)
        {
            return;
        }

        page.IsLoading = true;
        bool acquired = false;
        try
        {
            await _decodeSemaphore.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            if (page.IsLoaded || ct.IsCancellationRequested) return;

            var effects = ActiveEffectSettings.Clone();

            byte[]? rawBytes = await Task.Run(() => ReadRawPageBytes(page), ct).ConfigureAwait(false);
            if (rawBytes == null || rawBytes.Length == 0 || ct.IsCancellationRequested) return;

            // 1. Kiểm tra và kích hoạt hoạt ảnh động (GIF hoặc WebP Animated)
            if (AnimatedPagePlayer.IsAnimatedImage(rawBytes, out int frameCount, out int animW, out int animH))
            {
                page.IsAnimated = true;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (animW > 0 && animH > 0)
                    {
                        page.SetKnownDimensions(animW, animH);
                    }
                });

                var player = new AnimatedPagePlayer(page, rawBytes, effects, GlobalAnimationSpeed);
                page.AnimationPlayer?.Dispose();
                page.AnimationPlayer = player;
                player.Start();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    page.IsLoaded = true;
                });
                return;
            }

            // 2. Xử lý ảnh tĩnh thông thường
            Bitmap? bmp = await Task.Run(() => DecodeRawBytesToBitmap(rawBytes, page, effects), ct).ConfigureAwait(false);
            if (bmp != null && !ct.IsCancellationRequested)
            {
                int pxW = bmp.PixelSize.Width;
                int pxH = bmp.PixelSize.Height;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (pxW > 0 && pxH > 0)
                    {
                        // Nếu tỉ lệ aspect đã tương đối khớp (< 2% chênh lệch), chỉ cập nhật thông số nội bộ không re-measure để tránh giật cuộn
                        double oldRatio = page.AspectHeight > 0 ? (double)page.AspectWidth / page.AspectHeight : 0;
                        double newRatio = (double)pxW / pxH;
                        if (Math.Abs(oldRatio - newRatio) < 0.02)
                        {
                            page.SetKnownDimensionsFast(pxW, pxH);
                        }
                        else
                        {
                            page.SetKnownDimensions(pxW, pxH);
                        }
                    }
                    page.Image = bmp;
                    page.IsLoaded = true;
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch { }
        finally
        {
            if (acquired)
            {
                _decodeSemaphore.Release();
            }
            Dispatcher.UIThread.Post(() => page.IsLoading = false);
        }
    }

    public async Task UpdatePrefetchWindowAsync(
        IList<ReaderPageItem> pages,
        int currentIndex,
        bool isScrollMode,
        bool isEcoMode = false,
        CancellationToken ct = default)
    {
        if (pages == null || pages.Count == 0 || ct.IsCancellationRequested) return;

        int total = pages.Count;
        int safeCurrent = Math.Clamp(currentIndex, 0, total - 1);

        // 1. QUẢN LÝ BỘ NHỚ THEO CHUẨN COMIC SCREEN:
        // Nếu chương có <= 80 trang (chiếm 99% các chương truyện tranh thông thường 20-50 trang):
        // TUYỆT ĐỐI KHÔNG UNLOAD BẤT KỲ TRANG NÀO TRONG CHƯƠNG!
        // Toàn bộ chương được giữ nguyên vẹn trong RAM, lướt tới lướt lui 0ms mượt mà tuyệt đối!
        if (total > 80)
        {
            // Chỉ unload khi là chương cực lớn hoặc chế độ đọc toàn bộ truyện (hàng trăm đến hàng ngàn trang):
            int keepAhead = isScrollMode ? 40 : 25;
            int keepBehind = isScrollMode ? 25 : 15;
            int minKeep = Math.Max(0, safeCurrent - keepBehind);
            int maxKeep = Math.Min(total - 1, safeCurrent + keepAhead);

            int releasedCount = 0;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                for (int i = 0; i < total; i++)
                {
                    if (i < minKeep || i > maxKeep)
                    {
                        var p = pages[i];
                        if (p.IsLoaded || p.HasImage)
                        {
                            p.Unload();
                            releasedCount++;
                        }
                    }
                }
            });

            if (releasedCount > 0 && (isEcoMode || OperatingSystem.IsAndroid()))
            {
                TriggerDebouncedGcCollection();
            }
        }

        // 2. ƯU TIÊN SỐ 1: NẠP TRANG HIỆN TẠI NGAY LẬP TỨC
        await EnsurePageLoadedAsync(pages[safeCurrent], ct).ConfigureAwait(false);

        // 3. TẠO DANH SÁCH CÁC TRANG CẦN PRELOAD THEO THỨ TỰ ƯU TIÊN TỎA RA 2 PHÍA
        int preloadAhead = (total <= 80) ? (total - safeCurrent - 1) : (isScrollMode ? 25 : 12);
        int preloadBehind = (total <= 80) ? safeCurrent : (isScrollMode ? 12 : 6);

        var indicesToLoad = new List<int>();
        int maxOffset = Math.Max(preloadAhead, preloadBehind);
        for (int offset = 1; offset <= maxOffset; offset++)
        {
            if (offset <= preloadAhead && safeCurrent + offset < total) indicesToLoad.Add(safeCurrent + offset);
            if (offset <= preloadBehind && safeCurrent - offset >= 0) indicesToLoad.Add(safeCurrent - offset);
        }

        // 4. NẠP SONG SONG CÁC TRANG LÂN CẬN (Task.WhenAll chạy qua _decodeSemaphore 4-6 luồng)
        var pendingTasks = indicesToLoad
            .Where(idx => idx >= 0 && idx < total && !pages[idx].IsLoaded && !pages[idx].IsLoading)
            .Select(idx => EnsurePageLoadedAsync(pages[idx], ct))
            .ToList();

        if (pendingTasks.Count > 0)
        {
            await Task.WhenAll(pendingTasks).ConfigureAwait(false);
        }
    }

    private static long _lastGcTick;
    private static void TriggerDebouncedGcCollection()
    {
        long now = Environment.TickCount64;
        if (now - _lastGcTick > 3500)
        {
            _lastGcTick = now;
            Task.Run(() =>
            {
                try
                {
                    GC.Collect(0, GCCollectionMode.Optimized);
                }
                catch { }
            });
        }
    }

    public static byte[]? ReadRawPageBytes(ReaderPageItem page)
    {
        try
        {
            // 1. Xây dựng Cache Key duy nhất theo nguồn truyện và chỉ mục/entry
            string subKey = page.IsMobiEntry ? $"mobi_p_{page.MobiPageIndex}"
                          : page.IsPdfEntry ? $"pdf_p_{page.PdfPageIndex}"
                          : !string.IsNullOrEmpty(page.ArchiveEntryFullName) ? page.ArchiveEntryFullName
                          : Path.GetFileName(page.SourcePath);

            string cacheKey = ComicDiskCacheService.BuildPageCacheKey(page.SourcePath, subKey);

            // 2. Kiểm tra bộ nhớ đệm L1 RAM và L2 Disk Cache trước (0ms siêu tốc)
            if (!string.IsNullOrEmpty(cacheKey))
            {
                if (_l1RawPageBytesCache.TryGetValue(cacheKey, out var memBytes) && memBytes != null && memBytes.Length > 0)
                {
                    return memBytes;
                }

                var cachedBytes = ComicDiskCacheService.Instance.TryGetCachedPageBytes(cacheKey);
                if (cachedBytes != null && cachedBytes.Length > 0)
                {
                    PutL1Cache(cacheKey, cachedBytes);
                    return cachedBytes;
                }
            }

            // 3. Nếu chưa có trong Disk Cache: trích xuất từ file gốc (ZIP / RAR / PDF / MOBI)
            byte[]? rawBytes = null;
            if (page.IsMobiEntry)
            {
                if (!File.Exists(page.SourcePath)) return null;
                rawBytes = MobiComicParser.Instance.ReadPageBytes(page.SourcePath, page.MobiPageIndex);
            }
            else if (page.IsPdfEntry)
            {
                if (!File.Exists(page.SourcePath)) return null;
                // Trích xuất trực tiếp byte stream ảnh JPEG gốc từ PDF trong 0.5ms (tránh 100% chi phí Double Decode / Double Encode)
                rawBytes = PdfComicParser.Instance.ExtractPageImageBytes(page.SourcePath, page.PdfPageIndex, out _);
                if (rawBytes == null)
                {
                    var pdfBmp = PdfComicParser.Instance.DecodePageBitmap(page.SourcePath, page.PdfPageIndex);
                    if (pdfBmp != null)
                    {
                        using var pdfMs = new MemoryStream();
#pragma warning disable CS0618
                        pdfBmp.Save(pdfMs);
#pragma warning restore CS0618
                        pdfBmp.Dispose();
                        rawBytes = pdfMs.ToArray();
                    }
                }
            }
            else if (page.IsRarEntry)
            {
                rawBytes = RarArchivePool.Instance.ReadEntryBytes(page.SourcePath, page.ArchiveEntryFullName);
            }
            else if (page.IsArchiveEntry)
            {
                rawBytes = ZipArchivePool.Instance.ReadEntryBytes(page.SourcePath, page.ArchiveEntryFullName);
            }
            else
            {
                if (!File.Exists(page.SourcePath)) return null;
                rawBytes = File.ReadAllBytes(page.SourcePath);
            }

            // 4. Lưu ngay vào L1 RAM và L2 Disk Cache để các lần cuộn lướt tiếp theo đạt tốc độ tức thì
            if (rawBytes != null && rawBytes.Length > 0 && !string.IsNullOrEmpty(cacheKey))
            {
                PutL1Cache(cacheKey, rawBytes);
                ComicDiskCacheService.Instance.SaveCachedPageBytes(cacheKey, rawBytes);
            }

            return rawBytes;
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? DecodeRawBytesToBitmap(byte[] rawBytes, ReaderPageItem page, ReaderVisualEffectSettings effects)
    {
        try
        {
            if (effects.HasAnyEffect)
            {
                var processedBmp = DecodeAndApplyEffectsWithSkia(rawBytes, effects);
                if (processedBmp != null)
                {
                    return processedBmp;
                }
            }

            try
            {
                using var ms = new MemoryStream(rawBytes, writable: false);
                return new Bitmap(ms);
            }
            catch
            {
                var (jpegBytes, _, _) = UniversalImageDecoder.ConvertToStandardJpeg(rawBytes, 92);
                if (jpegBytes.Length > 0)
                {
                    using var ms2 = new MemoryStream(jpegBytes, writable: false);
                    return new Bitmap(ms2);
                }
            }
        }
        catch { }

        return null;
    }

    private static Bitmap? DecodeAndApplyEffectsWithSkia(byte[] rawBytes, ReaderVisualEffectSettings effects)
    {
        try
        {
            using var skData = SKData.CreateCopy(rawBytes);
            using var codec = SKCodec.Create(skData);
            if (codec == null) return null;

            var decodeInfo = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var srcBitmap = SKBitmap.Decode(codec, decodeInfo);
            if (srcBitmap == null || srcBitmap.Width <= 4 || srcBitmap.Height <= 4) return null;

            int srcW = srcBitmap.Width;
            int srcH = srcBitmap.Height;

            int leftPx = (int)Math.Round(srcW * Math.Clamp(effects.CropLeftPercent, 0, 35) / 100.0);
            int rightPx = (int)Math.Round(srcW * Math.Clamp(effects.CropRightPercent, 0, 35) / 100.0);
            int topPx = (int)Math.Round(srcH * Math.Clamp(effects.CropTopPercent, 0, 35) / 100.0);
            int bottomPx = (int)Math.Round(srcH * Math.Clamp(effects.CropBottomPercent, 0, 35) / 100.0);

            int cropW = Math.Max(32, srcW - leftPx - rightPx);
            int cropH = Math.Max(32, srcH - topPx - bottomPx);

            var targetInfo = new SKImageInfo(cropW, cropH, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var dstBitmap = new SKBitmap(targetInfo);
            using (var canvas = new SKCanvas(dstBitmap))
            {
                canvas.Clear(SKColors.White);
                var srcRect = new SKRect(leftPx, topPx, leftPx + cropW, topPx + cropH);
                var dstRect = new SKRect(0, 0, cropW, cropH);

                if (effects.IsSharpenFilter || effects.IsDenoiseFilter)
                {
                    using var paint = new SKPaint();
                    SKImageFilter? combinedFilter = null;
                    SKImageFilter? blurFilter = null;
                    SKImageFilter? convFilter = null;

                    if (effects.IsDenoiseFilter)
                    {
                        float denoiseStrength = (float)Math.Clamp(effects.DenoiseStrength, 0.0, 100.0);
                        // Bán kính làm mờ mịn nhẹ từ 0.1f đến 1.8f để khử hạt nhiễu (noise/grain) mà không làm mờ nhoè nét chữ
                        float sigma = Math.Max(0.1f, (denoiseStrength / 100.0f) * 1.8f);
                        blurFilter = SKImageFilter.CreateBlur(sigma, sigma);
                    }

                    if (effects.IsSharpenFilter)
                    {
                        float strength = (float)Math.Clamp(effects.SharpenStrength, 0.0, 100.0);
                        float k = (strength / 100.0f) * 1.5f;
                        float center = 1.0f + 4.0f * k;
                        float[] kernel =
                        {
                            0f, -k, 0f,
                            -k, center, -k,
                            0f, -k, 0f
                        };
                        convFilter = SKImageFilter.CreateMatrixConvolution(
                            new SKSizeI(3, 3),
                            kernel,
                            1.0f,
                            0.0f,
                            new SKPointI(1, 1),
                            SKShaderTileMode.Clamp,
                            false);
                    }

                    if (blurFilter != null && convFilter != null)
                    {
                        // Giảm nhiễu trước, sau đó tôn nét cạnh
                        combinedFilter = SKImageFilter.CreateCompose(convFilter, blurFilter);
                    }
                    else if (blurFilter != null)
                    {
                        combinedFilter = blurFilter;
                    }
                    else if (convFilter != null)
                    {
                        combinedFilter = convFilter;
                    }

                    paint.ImageFilter = combinedFilter;
                    canvas.DrawBitmap(srcBitmap, srcRect, dstRect, paint);

                    if (combinedFilter != null && combinedFilter != blurFilter && combinedFilter != convFilter)
                    {
                        combinedFilter.Dispose();
                    }
                    blurFilter?.Dispose();
                    convFilter?.Dispose();
                }
                else
                {
                    canvas.DrawBitmap(srcBitmap, srcRect, dstRect);
                }

            }

            if (effects.IsAutoContrast ||
                effects.IsGrayScale ||
                effects.IsInvertColor ||
                (effects.IsSaturationEnabled && Math.Abs(effects.SaturationValue - 100.0) > 1.0))
            {
                ApplyPixelTransformsInPlace(dstBitmap, effects);
            }

            using var skImage = SKImage.FromBitmap(dstBitmap);
            using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, 92);
            if (encoded == null) return null;

            using var outMs = new MemoryStream(encoded.ToArray(), writable: false);
            return new Bitmap(outMs);
        }
        catch
        {
            return null;
        }
    }

    internal static void ApplyPixelTransformsInPlace(SKBitmap bmp, ReaderVisualEffectSettings effects)
    {
        IntPtr ptr = bmp.GetPixels();
        int totalBytes = bmp.ByteCount;
        if (ptr == IntPtr.Zero || totalBytes < 4) return;

        byte[] buffer = new byte[totalBytes];
        Marshal.Copy(ptr, buffer, 0, totalBytes);

        int minLum = 0;
        int maxLum = 255;

        if (effects.IsAutoContrast)
        {
            Span<int> hist = stackalloc int[256];
            int sampleStep = Math.Max(4, (totalBytes / 4 / 25000) * 4);
            int samples = 0;
            for (int i = 0; i <= totalBytes - 4; i += sampleStep)
            {
                int b = buffer[i];
                int g = buffer[i + 1];
                int r = buffer[i + 2];
                int lum = (r * 77 + g * 150 + b * 29) >> 8;
                hist[Math.Clamp(lum, 0, 255)]++;
                samples++;
            }

            if (samples > 100)
            {
                int lowCut = (int)(samples * 0.02);
                int highCut = (int)(samples * 0.98);
                int acc = 0;
                for (int k = 0; k < 256; k++)
                {
                    acc += hist[k];
                    if (acc >= lowCut) { minLum = k; break; }
                }
                acc = 0;
                for (int k = 255; k >= 0; k--)
                {
                    acc += hist[k];
                    if (acc >= (samples - highCut)) { maxLum = k; break; }
                }
                if (maxLum - minLum < 40)
                {
                    minLum = 0;
                    maxLum = 255;
                }
            }
        }

        bool doContrast = effects.IsAutoContrast && (minLum > 0 || maxLum < 255);
        float contrastScale = doContrast ? (255.0f / Math.Max(1, maxLum - minLum)) : 1.0f;

        bool doSat = effects.IsSaturationEnabled && !effects.IsGrayScale && Math.Abs(effects.SaturationValue - 100.0) > 1.0;
        float satFactor = (float)Math.Clamp(effects.SaturationValue / 100.0, 0.0, 2.5);

        bool doGray = effects.IsGrayScale;
        bool doInvert = effects.IsInvertColor;

        for (int i = 0; i <= totalBytes - 4; i += 4)
        {
            float b = buffer[i];
            float g = buffer[i + 1];
            float r = buffer[i + 2];

            if (doContrast)
            {
                r = Math.Clamp((r - minLum) * contrastScale, 0f, 255f);
                g = Math.Clamp((g - minLum) * contrastScale, 0f, 255f);
                b = Math.Clamp((b - minLum) * contrastScale, 0f, 255f);
            }

            if (doGray)
            {
                float lum = 0.299f * r + 0.587f * g + 0.114f * b;
                r = g = b = lum;
            }
            else if (doSat)
            {
                float lum = 0.299f * r + 0.587f * g + 0.114f * b;
                r = Math.Clamp(lum + (r - lum) * satFactor, 0f, 255f);
                g = Math.Clamp(lum + (g - lum) * satFactor, 0f, 255f);
                b = Math.Clamp(lum + (b - lum) * satFactor, 0f, 255f);
            }

            if (doInvert)
            {
                r = 255f - r;
                g = 255f - g;
                b = 255f - b;
            }

            buffer[i] = (byte)b;
            buffer[i + 1] = (byte)g;
            buffer[i + 2] = (byte)r;
        }

        Marshal.Copy(buffer, 0, ptr, totalBytes);
    }

    public byte[]? GetPageRawBytes(ReaderPageItem? page)
    {
        if (page == null) return null;
        try
        {
            if (page.IsArchiveEntry)
            {
                return ZipArchivePool.Instance.ReadEntryBytes(page.SourcePath, page.ArchiveEntryFullName);
            }
            if (page.IsPdfEntry)
            {
                var bmp = PdfComicParser.Instance.DecodePageBitmap(page.SourcePath, page.PdfPageIndex);
                if (bmp != null)
                {
                    using var ms = new MemoryStream();
#pragma warning disable CS0618
                    bmp.Save(ms);
#pragma warning restore CS0618
                    bmp.Dispose();
                    return ms.ToArray();
                }
                return null;
            }
            if (File.Exists(page.SourcePath))
            {
                return File.ReadAllBytes(page.SourcePath);
            }
        }
        catch { }
        return null;
    }

    public static (double LeftPct, double TopPct, double RightPct, double BottomPct) DetectSmartMargins(byte[] rawBytes)
    {
        try
        {
            using var skData = SKData.CreateCopy(rawBytes);
            using var codec = SKCodec.Create(skData);
            if (codec == null) return (3.0, 3.0, 3.0, 3.0);

            int origW = codec.Info.Width;
            int origH = codec.Info.Height;
            if (origW <= 32 || origH <= 32) return (2.0, 2.0, 2.0, 2.0);

            int thumbW = 200;
            int thumbH = Math.Max(50, (int)Math.Round(200.0 * origH / origW));
            if (thumbH > 320)
            {
                thumbH = 320;
                thumbW = Math.Max(50, (int)Math.Round(320.0 * origW / origH));
            }

            var thumbInfo = new SKImageInfo(thumbW, thumbH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bmp = new SKBitmap(thumbInfo);
            using var canvas = new SKCanvas(bmp);
            using var srcBmp = SKBitmap.Decode(codec);
            if (srcBmp == null) return (3.0, 3.0, 3.0, 3.0);
            canvas.DrawBitmap(srcBmp, new SKRect(0, 0, thumbW, thumbH));

            static bool IsBorderPixel(SKColor c)
            {
                int lum = (int)(0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue);
                return lum >= 238 || lum <= 16;
            }

            int top = 0;
            int maxTop = (int)(thumbH * 0.20);
            for (int y = 0; y < maxTop; y++)
            {
                int borderCount = 0;
                for (int x = 0; x < thumbW; x += 4)
                {
                    if (IsBorderPixel(bmp.GetPixel(x, y))) borderCount++;
                }
                if (borderCount >= (thumbW / 4) * 0.88)
                    top = y;
                else
                    break;
            }

            int bottom = 0;
            int maxBottom = (int)(thumbH * 0.20);
            for (int y = thumbH - 1; y >= thumbH - maxBottom; y--)
            {
                int borderCount = 0;
                for (int x = 0; x < thumbW; x += 4)
                {
                    if (IsBorderPixel(bmp.GetPixel(x, y))) borderCount++;
                }
                if (borderCount >= (thumbW / 4) * 0.88)
                    bottom = (thumbH - 1 - y);
                else
                    break;
            }

            int left = 0;
            int maxLeft = (int)(thumbW * 0.20);
            for (int x = 0; x < maxLeft; x++)
            {
                int borderCount = 0;
                for (int y = 0; y < thumbH; y += 4)
                {
                    if (IsBorderPixel(bmp.GetPixel(x, y))) borderCount++;
                }
                if (borderCount >= (thumbH / 4) * 0.88)
                    left = x;
                else
                    break;
            }

            int right = 0;
            int maxRight = (int)(thumbW * 0.20);
            for (int x = thumbW - 1; x >= thumbW - maxRight; x--)
            {
                int borderCount = 0;
                for (int y = 0; y < thumbH; y += 4)
                {
                    if (IsBorderPixel(bmp.GetPixel(x, y))) borderCount++;
                }
                if (borderCount >= (thumbH / 4) * 0.88)
                    right = (thumbW - 1 - x);
                else
                    break;
            }

            double topPct = Math.Clamp(Math.Round((double)top / thumbH * 100.0, 1), 0.0, 22.0);
            double bottomPct = Math.Clamp(Math.Round((double)bottom / thumbH * 100.0, 1), 0.0, 22.0);
            double leftPct = Math.Clamp(Math.Round((double)left / thumbW * 100.0, 1), 0.0, 22.0);
            double rightPct = Math.Clamp(Math.Round((double)right / thumbW * 100.0, 1), 0.0, 22.0);

            if (topPct < 1.0 && bottomPct < 1.0 && leftPct < 1.0 && rightPct < 1.0)
            {
                return (3.0, 3.0, 3.0, 3.0);
            }

            return (leftPct, topPct, rightPct, bottomPct);
        }
        catch
        {
            return (3.0, 3.0, 3.0, 3.0);
        }
    }

    /// <summary>
    /// Trích xuất toàn bộ siêu dữ liệu thuộc tính của trang ảnh hiện tại (phục vụ HUD thông tin phím `)
    /// </summary>
    public static string GetImagePropertiesSummary(ReaderPageItem? page, double currentZoomLevel = 1.0)
    {
        if (page == null) return "Không có thông tin trang.";

        var sb = new System.Text.StringBuilder();

        // 1. Phân giải & Tỉ lệ
        int aw = page.AspectWidth > 0 ? page.AspectWidth : 900;
        int ah = page.AspectHeight > 0 ? page.AspectHeight : 1350;
        double ratio = aw > 0 ? (double)ah / aw : 1.5;
        sb.AppendLine($"📐 Độ phân giải gốc: {aw} × {ah} px (Tỉ lệ 1 : {ratio:F2})");
        sb.AppendLine($"🖥️ Khung hiển thị: {Math.Round(page.DisplayWidth)} × {Math.Round(page.DisplayHeight)} px (Zoom {currentZoomLevel * 100:F0}%)");

        // 2. Dung lượng tệp
        long bytes = page.FileSizeBytes;
        if (bytes <= 0 && !string.IsNullOrEmpty(page.SourcePath) && !page.IsArchiveEntry && File.Exists(page.SourcePath))
        {
            try { bytes = new FileInfo(page.SourcePath).Length; } catch { }
        }

        string sizeStr = bytes switch
        {
            > 1024 * 1024 => $"{(double)bytes / (1024 * 1024):F2} MB ({bytes:N0} bytes)",
            > 1024 => $"{(double)bytes / 1024:F1} KB ({bytes:N0} bytes)",
            > 0 => $"{bytes} bytes",
            _ => "Không xác định"
        };
        sb.AppendLine($"💾 Dung lượng: {sizeStr}");

        // 3. Codec & Hoạt ảnh
        string ext = Path.GetExtension(!string.IsNullOrEmpty(page.ArchiveEntryFullName) ? page.ArchiveEntryFullName : page.SourcePath).ToUpperInvariant().TrimStart('.');
        if (string.IsNullOrEmpty(ext)) ext = "UNKNOWN";

        string codecInfo = ext switch
        {
            "WEBP" => "WebP (VP8/VP8L/VP8X Decoder)",
            "JPG" or "JPEG" => "JPEG (Baseline/Progressive SOF)",
            "PNG" => "PNG (Portable Network Graphics)",
            "GIF" => "GIF (Graphics Interchange Format)",
            "BMP" => "BMP (Windows Bitmap)",
            "AVIF" => "AVIF (AV1 Image File Format)",
            _ => ext
        };

        if (page.IsAnimated && page.AnimationPlayer != null)
        {
            codecInfo += $" [Hoạt ảnh {page.AnimationPlayer.FrameCount} frames @ {page.AnimationPlayer.Speed:F1}x]";
        }
        sb.AppendLine($"🎞️ Định dạng/Codec: {codecInfo}");

        // 4. Container & Vị trí lưu trữ
        string containerType;
        string locationDetails;

        if (page.IsRarEntry)
        {
            containerType = "CBR / RAR Archive";
            locationDetails = $"{Path.GetFileName(page.SourcePath)} -> {page.ArchiveEntryFullName}";
        }
        else if (page.IsPdfEntry)
        {
            containerType = "PDF Document";
            locationDetails = $"{Path.GetFileName(page.SourcePath)} (Trang {page.PdfPageIndex + 1})";
        }
        else if (page.IsMobiEntry)
        {
            containerType = "MOBI / AZW Ebook";
            locationDetails = $"{Path.GetFileName(page.SourcePath)} (Trang {page.MobiPageIndex + 1})";
        }
        else if (page.IsArchiveEntry)
        {
            containerType = "CBZ / ZIP Archive";
            locationDetails = $"{Path.GetFileName(page.SourcePath)} -> {page.ArchiveEntryFullName}";
        }
        else
        {
            containerType = "Tệp ảnh rời trên đĩa (Standalone Image)";
            locationDetails = page.SourcePath;
        }

        sb.AppendLine($"📦 Container: {containerType}");
        sb.AppendLine($"📍 Nguồn: {locationDetails}");

        return sb.ToString().TrimEnd();
    }
}

