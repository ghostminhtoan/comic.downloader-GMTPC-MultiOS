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
                    if (pdfMeta.Bookmarks.Count > 0 && chapter.PdfBookmarks.Count == 0)
                    {
                        chapter.PdfBookmarks = pdfMeta.Bookmarks;
                    }

                    for (int i = 0; i < pdfMeta.Pages.Count; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        var pInfo = pdfMeta.Pages[i];
                        pages.Add(new ReaderPageItem
                        {
                            Index = i,
                            Title = $"Trang {i + 1}",
                            SourcePath = chapter.LocalPath,
                            ArchiveEntryFullName = string.Empty,
                            IsPdfEntry = true,
                            PdfPageIndex = i,
                            FileSizeBytes = pInfo.StreamLength
                        });
                    }
                }
                else if (chapter.IsCbz || LibraryScannerService.IsArchiveFile(chapter.LocalPath))
                {
                    if (!File.Exists(chapter.LocalPath)) return pages;

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

                        pages.Add(new ReaderPageItem
                        {
                            Index = i,
                            Title = Path.GetFileName(file),
                            SourcePath = file,
                            ArchiveEntryFullName = string.Empty,
                            FileSizeBytes = size
                        });
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
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
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
        int preloadBehind = isScrollMode ? 2 : 2;
        int preloadAhead = isScrollMode ? 4 : 3;
        int keepRadius = isScrollMode ? 10 : 5;

        int minLoad = Math.Max(0, safeCurrent - preloadBehind);
        int maxLoad = Math.Min(pages.Count - 1, safeCurrent + preloadAhead);

        // 1. Giải phóng các trang nằm xa ngoài vùng nhìn để tiết kiệm RAM
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
                using var ms = new MemoryStream((int)Math.Min(entry.Length, 32 * 1024 * 1024));
                es.CopyTo(ms);
                rawBytes = ms.ToArray();
            }
            else
            {
                if (!File.Exists(page.SourcePath)) return null;
                rawBytes = File.ReadAllBytes(page.SourcePath);
            }

            if (rawBytes == null || rawBytes.Length == 0) return null;

            // Thử nạp trực tiếp qua Avalonia Bitmap (Skia backend)
            try
            {
                using var ms = new MemoryStream(rawBytes);
                if (OperatingSystem.IsAndroid() && rawBytes.Length > 4 * 1024 * 1024)
                {
                    return Bitmap.DecodeToWidth(ms, 1440);
                }
                return new Bitmap(ms);
            }
            catch
            {
                // Fallback qua UniversalImageDecoder (hỗ trợ mọi định dạng WebP VP8L/VP8X, Fake-JPG, PNG...)
                var (jpegBytes, _, _) = UniversalImageDecoder.ConvertToStandardJpeg(rawBytes, 93);
                if (jpegBytes.Length > 0)
                {
                    using var ms2 = new MemoryStream(jpegBytes);
                    return new Bitmap(ms2);
                }
            }
        }
        catch { }

        return null;
    }
}

