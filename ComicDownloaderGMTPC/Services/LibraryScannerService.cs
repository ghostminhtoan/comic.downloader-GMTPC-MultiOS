using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ComicDownloaderGMTPC.Models;

namespace ComicDownloaderGMTPC.Services;

public sealed class LibraryScannerService
{
    private static readonly Lazy<LibraryScannerService> _lazy = new(() => new LibraryScannerService());
    public static LibraryScannerService Instance => _lazy.Value;

    public static readonly HashSet<string> ValidImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".jfif"
    };

    public static readonly HashSet<string> ValidArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cbz", ".zip"
    };

    private readonly SemaphoreSlim _coverDecodeSemaphore = new(OperatingSystem.IsAndroid() ? 2 : 4);

    public static bool IsImageFile(string path)
    {
        string ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && ValidImageExtensions.Contains(ext);
    }

    public static bool IsArchiveFile(string path)
    {
        string ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && ValidArchiveExtensions.Contains(ext);
    }

    public static bool IsPdfFile(string path)
    {
        return string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    public Task<List<LibraryComicItem>> ScanLibraryRootAsync(string rootPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var comics = new List<LibraryComicItem>();
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                return comics;
            }

            try
            {
                // Nếu chính thư mục chọn là 1 file/folder chương đơn lẻ hoặc bộ truyện
                ScanDirectoryRecursive(rootPath, rootPath, depth: 0, maxDepth: 4, comics, ct);
            }
            catch { }

            return comics
                .OrderBy(c => c.Title, NaturalSortComparer.Instance)
                .ToList();
        }, ct);
    }

    private void ScanDirectoryRecursive(
        string currentDir,
        string rootDir,
        int depth,
        int maxDepth,
        List<LibraryComicItem> results,
        CancellationToken ct)
    {
        if (depth > maxDepth || ct.IsCancellationRequested) return;

        string dirName = Path.GetFileName(currentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (depth > 0 && (dirName.StartsWith(".", StringComparison.Ordinal) ||
                          dirName.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
                          dirName.Equals(".portable", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        string[] subDirs;
        string[] files;
        try
        {
            subDirs = Directory.GetDirectories(currentDir);
            files = Directory.GetFiles(currentDir);
        }
        catch
        {
            return;
        }

        var archiveFiles = files.Where(IsArchiveFile).NaturalSort();
        var pdfFiles = files.Where(IsPdfFile).NaturalSort();
        var directImages = files.Where(IsImageFile).NaturalSort();

        // Kiểm tra xem các thư mục con có phải là thư mục chương chứa ảnh không
        var chapterDirs = new List<(string DirPath, List<string> ImageFiles)>();
        var nonChapterDirs = new List<string>();

        foreach (string sub in subDirs.NaturalSort())
        {
            if (ct.IsCancellationRequested) return;
            string subName = Path.GetFileName(sub);
            if (subName.StartsWith(".", StringComparison.Ordinal)) continue;

            var imgs = GetDirectImagesSafe(sub);
            if (imgs.Count > 0)
            {
                chapterDirs.Add((sub, imgs));
            }
            else
            {
                nonChapterDirs.Add(sub);
            }
        }

        // Nếu thư mục hiện tại có các chương con hoặc có các file .cbz/.zip/.pdf -> Đây là 1 Bộ Truyện
        if (chapterDirs.Count > 0 || archiveFiles.Count > 0 || pdfFiles.Count > 0)
        {
            var comic = BuildComicItem(currentDir, rootDir, chapterDirs, archiveFiles, pdfFiles);
            if (comic.Chapters.Count > 0)
            {
                results.Add(comic);
            }

            // Nếu vẫn còn thư mục con không phải chương (ví dụ thư mục nhóm hoặc subfolder), quét tiếp
            foreach (string nextDir in nonChapterDirs)
            {
                if (ct.IsCancellationRequested) break;
                ScanDirectoryRecursive(nextDir, rootDir, depth + 1, maxDepth, results, ct);
            }
            return;
        }

        // Nếu người dùng chọn trực tiếp 1 thư mục chỉ chứa ảnh rời ở ngay root (depth == 0)
        if (depth == 0 && directImages.Count > 0)
        {
            var singleChapterList = new List<(string DirPath, List<string> ImageFiles)>
            {
                (currentDir, directImages)
            };
            var comic = BuildComicItem(currentDir, rootDir, singleChapterList, new List<string>(), new List<string>());
            if (comic.Chapters.Count > 0)
            {
                results.Add(comic);
            }
            return;
        }

        // Duyệt xuống tầng tiếp theo
        foreach (string nextDir in nonChapterDirs)
        {
            if (ct.IsCancellationRequested) break;
            ScanDirectoryRecursive(nextDir, rootDir, depth + 1, maxDepth, results, ct);
        }
    }

    private LibraryComicItem BuildComicItem(
        string comicDir,
        string rootDir,
        List<(string DirPath, List<string> ImageFiles)> chapterDirs,
        List<string> archiveFiles,
        List<string> pdfFiles)
    {
        string cleanComicDir = comicDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string title = Path.GetFileName(cleanComicDir);
        if (string.IsNullOrWhiteSpace(title)) title = "Truyện Đã Tải";

        string category = string.Empty;
        try
        {
            string? parent = Path.GetDirectoryName(cleanComicDir);
            if (!string.IsNullOrWhiteSpace(parent) &&
                !string.Equals(parent.TrimEnd('\\', '/'), rootDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                category = Path.GetFileName(parent.TrimEnd('\\', '/'));
            }
        }
        catch { }

        var comic = new LibraryComicItem
        {
            Title = title,
            RootFolderPath = cleanComicDir,
            CategoryName = category
        };

        int idx = 1;

        // 1. Nạp các thư mục chương
        foreach (var (dirPath, imgFiles) in chapterDirs)
        {
            string chTitle = Path.GetFileName(dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            int lastRead = ReadProgressStore.Instance.GetLastReadPage(dirPath);
            DateTime lastMod = DateTime.MinValue;
            try { lastMod = Directory.GetLastWriteTime(dirPath); } catch { }

            comic.Chapters.Add(new LibraryChapterItem
            {
                Index = idx++,
                Title = chTitle,
                LocalPath = dirPath,
                IsCbz = false,
                IsPdf = false,
                TotalPages = imgFiles.Count,
                LastReadPage = lastRead,
                LastModified = lastMod
            });

            if (string.IsNullOrEmpty(comic.CoverFilePath) && imgFiles.Count > 0)
            {
                comic.CoverFilePath = imgFiles[0];
            }
        }

        // 2. Nạp các file .cbz / .zip
        foreach (string archivePath in archiveFiles)
        {
            string chTitle = Path.GetFileNameWithoutExtension(archivePath);
            int lastRead = ReadProgressStore.Instance.GetLastReadPage(archivePath);
            DateTime lastMod = DateTime.MinValue;
            try { lastMod = File.GetLastWriteTime(archivePath); } catch { }

            var (pageCount, firstEntry) = InspectArchiveQuick(archivePath);
            if (pageCount <= 0) continue;

            comic.Chapters.Add(new LibraryChapterItem
            {
                Index = idx++,
                Title = chTitle,
                LocalPath = archivePath,
                IsCbz = true,
                IsPdf = false,
                TotalPages = pageCount,
                LastReadPage = lastRead,
                LastModified = lastMod
            });

            if (string.IsNullOrEmpty(comic.CoverFilePath) && string.IsNullOrEmpty(comic.CoverArchivePath) && !string.IsNullOrEmpty(firstEntry))
            {
                comic.CoverArchivePath = archivePath;
                comic.CoverArchiveEntry = firstEntry;
            }
        }

        // 3. Nạp các file .pdf (mỗi file .pdf là 1 mục liền mạch + kèm danh sách mục lục chương nếu có)
        foreach (string pdfPath in pdfFiles)
        {
            string chTitle = Path.GetFileNameWithoutExtension(pdfPath);
            int lastRead = ReadProgressStore.Instance.GetLastReadPage(pdfPath);
            DateTime lastMod = DateTime.MinValue;
            try { lastMod = File.GetLastWriteTime(pdfPath); } catch { }

            var pdfMeta = PdfComicParser.Instance.InspectPdf(pdfPath);
            if (pdfMeta.Pages.Count <= 0) continue;

            comic.Chapters.Add(new LibraryChapterItem
            {
                Index = idx++,
                Title = chTitle,
                LocalPath = pdfPath,
                IsCbz = false,
                IsPdf = true,
                PdfBookmarks = pdfMeta.Bookmarks,
                TotalPages = pdfMeta.Pages.Count,
                LastReadPage = lastRead,
                LastModified = lastMod
            });

            if (string.IsNullOrEmpty(comic.CoverFilePath) &&
                string.IsNullOrEmpty(comic.CoverArchivePath) &&
                string.IsNullOrEmpty(comic.CoverPdfPath))
            {
                comic.CoverPdfPath = pdfPath;
            }
        }

        comic.RefreshReadStatus();
        return comic;
    }

    public LibraryComicItem? CreateQuickComicFromPath(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return null;

        if (File.Exists(targetPath))
        {
            if (IsArchiveFile(targetPath))
            {
                string dir = Path.GetDirectoryName(targetPath) ?? string.Empty;
                var comic = BuildComicItem(
                    string.IsNullOrEmpty(dir) ? targetPath : dir,
                    dir,
                    new List<(string, List<string>)>(),
                    new List<string> { targetPath },
                    new List<string>());
                comic.Title = Path.GetFileNameWithoutExtension(targetPath);
                return comic;
            }

            if (IsPdfFile(targetPath))
            {
                string dir = Path.GetDirectoryName(targetPath) ?? string.Empty;
                var comic = BuildComicItem(
                    string.IsNullOrEmpty(dir) ? targetPath : dir,
                    dir,
                    new List<(string, List<string>)>(),
                    new List<string>(),
                    new List<string> { targetPath });
                comic.Title = Path.GetFileNameWithoutExtension(targetPath);
                return comic;
            }
        }

        if (Directory.Exists(targetPath))
        {
            var directImgs = GetDirectImagesSafe(targetPath);
            if (directImgs.Count > 0)
            {
                return BuildComicItem(
                    targetPath,
                    targetPath,
                    new List<(string, List<string>)> { (targetPath, directImgs) },
                    new List<string>(),
                    new List<string>());
            }
        }

        return null;
    }

    private static List<string> GetDirectImagesSafe(string folderPath)
    {
        try
        {
            return Directory.EnumerateFiles(folderPath)
                .Where(IsImageFile)
                .NaturalSort();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static (int PageCount, string? FirstEntryName) InspectArchiveQuick(string archivePath)
    {
        try
        {
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var imgEntries = zip.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) && IsImageFile(e.Name) && e.Length > 0)
                .Select(e => e.FullName)
                .NaturalSort();

            return (imgEntries.Count, imgEntries.FirstOrDefault());
        }
        catch
        {
            return (0, null);
        }
    }

    public async Task LoadCoversAsync(IEnumerable<LibraryComicItem> comics, CancellationToken ct = default)
    {
        var list = comics.Where(c => c.Cover == null).ToList();
        foreach (var comic in list)
        {
            if (ct.IsCancellationRequested) break;
            await _coverDecodeSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Bitmap? bmp = await Task.Run(() => DecodeCoverThumbnail(comic), ct).ConfigureAwait(false);
                if (bmp != null)
                {
                    Dispatcher.UIThread.Post(() => comic.Cover = bmp);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }
            finally
            {
                _coverDecodeSemaphore.Release();
            }
        }
    }

    private static Bitmap? DecodeCoverThumbnail(LibraryComicItem comic)
    {
        try
        {
            if (!string.IsNullOrEmpty(comic.CoverPdfPath) && File.Exists(comic.CoverPdfPath) &&
                string.IsNullOrEmpty(comic.CoverFilePath) && string.IsNullOrEmpty(comic.CoverArchivePath))
            {
                return PdfComicParser.Instance.DecodeCoverThumbnail(comic.CoverPdfPath, 220);
            }

            byte[]? rawBytes = null;
            if (!string.IsNullOrEmpty(comic.CoverFilePath) && File.Exists(comic.CoverFilePath))
            {
                rawBytes = File.ReadAllBytes(comic.CoverFilePath);
            }
            else if (!string.IsNullOrEmpty(comic.CoverArchivePath) &&
                     !string.IsNullOrEmpty(comic.CoverArchiveEntry) &&
                     File.Exists(comic.CoverArchivePath))
            {
                using var fs = new FileStream(comic.CoverArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                var entry = zip.GetEntry(comic.CoverArchiveEntry);
                if (entry != null)
                {
                    using var es = entry.Open();
                    using var ms = new MemoryStream();
                    es.CopyTo(ms);
                    rawBytes = ms.ToArray();
                }
            }

            if (rawBytes == null || rawBytes.Length == 0) return null;

            try
            {
                using var ms = new MemoryStream(rawBytes);
                return Bitmap.DecodeToWidth(ms, 220);
            }
            catch
            {
                var (jpegBytes, _, _) = UniversalImageDecoder.ConvertToStandardJpeg(rawBytes, 85);
                if (jpegBytes.Length > 0)
                {
                    using var ms2 = new MemoryStream(jpegBytes);
                    return Bitmap.DecodeToWidth(ms2, 220);
                }
            }
        }
        catch { }

        return null;
    }
}

