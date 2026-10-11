using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ComicDownloaderGMTPC.Models;

namespace ComicDownloaderGMTPC.Services;

public sealed class ComicReaderService
{
    private static readonly Lazy<ComicReaderService> _lazy = new(() => new ComicReaderService());
    public static ComicReaderService Instance => _lazy.Value;

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

            Bitmap? bmp = await Task.Run(() => DecodePageBitmap(page), ct).ConfigureAwait(false);
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

    private static Bitmap? DecodePageBitmap(ReaderPageItem page)
    {
        try
        {
            if (page.IsPdfEntry)
            {
                if (!File.Exists(page.SourcePath)) return null;
                return PdfComicParser.Instance.DecodePageBitmap(page.SourcePath, page.PdfPageIndex);
            }

            byte[]? rawBytes = null;

            if (page.IsArchiveEntry)
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
}
