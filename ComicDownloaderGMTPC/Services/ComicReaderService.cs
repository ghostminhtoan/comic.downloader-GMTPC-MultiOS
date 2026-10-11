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

    public ComicReaderViewModel? ActiveExternalReaderViewModel { get; set; }

    public Action? ActiveExternalReaderClosedCallback { get; set; }

    public ReaderVisualEffectSettings ActiveEffectSettings { get; set; } = new();

    private readonly SemaphoreSlim _decodeSemaphore = new(3, 3);

    public Task<List<ReaderPageItem>> LoadChapterPagesAsync(LibraryChapterItem chapter, CancellationToken ct = default)
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
                if (chapter.IsPdf || LibraryScannerService.IsPdfFile(chapter.LocalPath))
                {
                    if (!File.Exists(chapter.LocalPath)) return pages;

                    var pdfMeta = PdfComicParser.Instance.InspectPdf(chapter.LocalPath);
                    if (pdfMeta.Pages.Count == 0) return pages;

                    int startPage = Math.Clamp(chapter.PdfStartPageIndex, 0, pdfMeta.Pages.Count - 1);
                    int endPage = chapter.PdfEndPageIndex >= startPage
                        ? Math.Min(chapter.PdfEndPageIndex, pdfMeta.Pages.Count - 1)
                        : (pdfMeta.Pages.Count - 1);

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
                    }
                }
                else if (chapter.IsCbz || LibraryScannerService.IsArchiveFile(chapter.LocalPath))
                {
                    if (!File.Exists(chapter.LocalPath)) return pages;

                    if (chapter.ArchiveEntryNames != null && chapter.ArchiveEntryNames.Count > 0)
                    {
                        for (int i = 0; i < chapter.ArchiveEntryNames.Count; i++)
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
                        }
                    }
                    else
                    {
                        using var fs = new FileStream(chapter.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                        var sortedEntries = zip.Entries
                            .Where(e => !string.IsNullOrEmpty(e.Name) && LibraryScannerService.IsImageFile(e.Name) && e.Length > 0)
                            .OrderBy(e => e.FullName, NaturalSortComparer.Instance)
                            .ToList();

                        for (int i = 0; i < sortedEntries.Count; i++)
                        {
                            if (ct.IsCancellationRequested) break;
                            var entry = sortedEntries[i];
                            pages.Add(new ReaderPageItem
                            {
                                Index = i,
                                Title = entry.Name,
                                SourcePath = chapter.LocalPath,
                                ArchiveEntryFullName = entry.FullName,
                                FileSizeBytes = entry.Length
                            });
                        }
                    }
                }
                else if (Directory.Exists(chapter.LocalPath))
                {
                    var imageFiles = Directory.EnumerateFiles(chapter.LocalPath)
                        .Where(LibraryScannerService.IsImageFile)
                        .NaturalSort();

                    for (int i = 0; i < imageFiles.Count; i++)
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
                        else if (UniversalImageDecoder.TryGetFastPngInfo(file, out var pInfo) && pInfo.Width > 0 && pInfo.Height > 0)
                        {
                            item.SetKnownDimensions(pInfo.Width, pInfo.Height);
                        }

                        pages.Add(item);
                    }
                }
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
            Bitmap? bmp = await Task.Run(() => DecodePageBitmap(page, effects), ct).ConfigureAwait(false);
            if (bmp != null && !ct.IsCancellationRequested)
            {
                int pxW = bmp.PixelSize.Width;
                int pxH = bmp.PixelSize.Height;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (pxW > 0 && pxH > 0)
                    {
                        page.SetKnownDimensions(pxW, pxH);
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
        CancellationToken ct = default)
    {
        if (pages == null || pages.Count == 0 || ct.IsCancellationRequested) return;

        int safeCurrent = Math.Clamp(currentIndex, 0, pages.Count - 1);
        int preloadBehind = isScrollMode ? 3 : 2;
        int preloadAhead = isScrollMode ? 5 : 3;
        int keepRadius = isScrollMode ? 14 : 6;

        int minLoad = Math.Max(0, safeCurrent - preloadBehind);
        int maxLoad = Math.Min(pages.Count - 1, safeCurrent + preloadAhead);

        // 1. Giải phóng các trang nằm xa ngoài vùng nhìn để tiết kiệm RAM (nhưng vẫn giữ nguyên AspectWidth/AspectHeight)
        int minKeep = Math.Max(0, safeCurrent - keepRadius);
        int maxKeep = Math.Min(pages.Count - 1, safeCurrent + keepRadius);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            for (int i = 0; i < pages.Count; i++)
            {
                if (i < minKeep || i > maxKeep)
                {
                    var p = pages[i];
                    if (p.IsLoaded && p.Image != null)
                    {
                        var oldImg = p.Image;
                        p.Image = null;
                        p.IsLoaded = false;
                        try { oldImg.Dispose(); } catch { }
                    }
                }
            }
        });

        // 2. Ưu tiên nạp trang hiện tại trước, sau đó nạp các trang lân cận
        await EnsurePageLoadedAsync(pages[safeCurrent], ct).ConfigureAwait(false);

        var indicesToLoad = new List<int>();
        for (int offset = 1; offset <= Math.Max(preloadBehind, preloadAhead); offset++)
        {
            if (safeCurrent + offset <= maxLoad) indicesToLoad.Add(safeCurrent + offset);
            if (safeCurrent - offset >= minLoad) indicesToLoad.Add(safeCurrent - offset);
        }

        foreach (int idx in indicesToLoad)
        {
            if (ct.IsCancellationRequested) break;
            await EnsurePageLoadedAsync(pages[idx], ct).ConfigureAwait(false);
        }
    }

    private static Bitmap? DecodePageBitmap(ReaderPageItem page, ReaderVisualEffectSettings effects)
    {
        try
        {
            byte[]? rawBytes = null;

            if (page.IsPdfEntry)
            {
                if (!File.Exists(page.SourcePath)) return null;
                var pdfBmp = PdfComicParser.Instance.DecodePageBitmap(page.SourcePath, page.PdfPageIndex);
                if (pdfBmp == null) return null;
                if (!effects.HasAnyEffect)
                {
                    return pdfBmp;
                }

                using var pdfMs = new MemoryStream();
#pragma warning disable CS0618
                pdfBmp.Save(pdfMs);
#pragma warning restore CS0618
                pdfBmp.Dispose();
                rawBytes = pdfMs.ToArray();
            }
            else if (page.IsArchiveEntry)
            {
                if (!File.Exists(page.SourcePath)) return null;
                using var fs = new FileStream(page.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                var entry = zip.GetEntry(page.ArchiveEntryFullName);
                if (entry == null) return null;

                using var es = entry.Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                rawBytes = ms.ToArray();
            }
            else
            {
                if (!File.Exists(page.SourcePath)) return null;
                rawBytes = File.ReadAllBytes(page.SourcePath);
            }

            if (rawBytes == null || rawBytes.Length == 0) return null;

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

                if (effects.IsSharpenFilter)
                {
                    using var paint = new SKPaint();
                    float[] kernel =
                    {
                        0f, -0.85f, 0f,
                        -0.85f, 4.4f, -0.85f,
                        0f, -0.85f, 0f
                    };
                    using var conv = SKImageFilter.CreateMatrixConvolution(
                        new SKSizeI(3, 3),
                        kernel,
                        1.0f,
                        0.0f,
                        new SKPointI(1, 1),
                        SKShaderTileMode.Clamp,
                        false);
                    paint.ImageFilter = conv;
                    canvas.DrawBitmap(srcBitmap, srcRect, dstRect, paint);
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

    private static void ApplyPixelTransformsInPlace(SKBitmap bmp, ReaderVisualEffectSettings effects)
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
}
