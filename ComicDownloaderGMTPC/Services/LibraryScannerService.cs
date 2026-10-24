using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ComicDownloaderGMTPC.Models;

namespace ComicDownloaderGMTPC.Services;

public sealed class LibraryScannerService
{
    private static readonly Lazy<LibraryScannerService> _lazy = new(() => new LibraryScannerService());
    public static LibraryScannerService Instance => _lazy.Value;

    public static Func<List<string>>? AndroidGetExternalStoragePaths;

    public static readonly HashSet<string> ValidImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".jfif"
    };

    public static readonly HashSet<string> ValidArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cbz", ".zip", ".cbr", ".rar"
    };

    public static readonly HashSet<string> ValidMobiExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mobi", ".prc", ".azw", ".azw3"
    };

    private const int DefaultAutoSplitPagesPerChapter = 80;

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

    public static bool IsRarFile(string path)
    {
        string ext = Path.GetExtension(path);
        return string.Equals(ext, ".cbr", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".rar", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMobiFile(string path)
    {
        string ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && ValidMobiExtensions.Contains(ext);
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
            if (!string.IsNullOrWhiteSpace(rootPath))
            {
                if (File.Exists(rootPath))
                {
                    var single = CreateQuickComicFromPath(rootPath);
                    if (single != null)
                    {
                        single.IsExternalHistoryItem = false;
                        single.SourceLocationLabel = BuildShortLocationBadge(single.RootFolderPath, isExternal: false);
                        comics.Add(single);
                    }
                }
                else if (Directory.Exists(rootPath))
                {
                    ReadProgressStore.Instance.SaveLastLibraryRootPath(rootPath);
                    try
                    {
                        ScanDirectoryRecursive(rootPath, rootPath, depth: 0, maxDepth: 4, comics, ct);
                    }
                    catch { }

                    foreach (var c in comics)
                    {
                        c.IsExternalHistoryItem = false;
                        c.SourceLocationLabel = BuildShortLocationBadge(c.RootFolderPath, isExternal: false);
                    }
                }
            }

            // Luôn nạp bổ sung các bộ truyện đã đọc dở / đã đọc xong từ Lịch sử đọc toàn cục (ở bất kỳ thư mục/ổ đĩa nào)
            AppendGlobalHistoryComics(comics, ct);

            return comics
                .OrderByDescending(c => c.HasAnyReadProgress ? 1 : 0)
                .ThenByDescending(c => c.LastReadOrder)
                .ThenBy(c => c.Title, NaturalSortComparer.Instance)
                .ToList();
        }, ct);
    }

    private void AppendGlobalHistoryComics(List<LibraryComicItem> currentList, CancellationToken ct)
    {
        try
        {
            var knownPaths = ReadProgressStore.Instance.GetKnownReadComicPaths();
            if (knownPaths.Count == 0) return;

            var existingNormalizedPaths = new HashSet<string>(
                currentList
                    .Where(c => !string.IsNullOrWhiteSpace(c.RootFolderPath))
                    .Select(c => ReadProgressStore.NormalizeKey(c.RootFolderPath)),
                StringComparer.OrdinalIgnoreCase);

            foreach (string histPath in knownPaths)
            {
                if (ct.IsCancellationRequested) break;
                string normHist = ReadProgressStore.NormalizeKey(histPath);
                if (string.IsNullOrWhiteSpace(normHist) || existingNormalizedPaths.Contains(normHist))
                    continue;

                // Nếu truyện đã được di chuyển sang thư mục hiện tại (trùng tên bộ truyện) và đã khớp qua Portable Identity Key, tránh nhân đôi
                string histName = Path.GetFileNameWithoutExtension(normHist.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (currentList.Any(c =>
                        !c.IsExternalHistoryItem &&
                        string.Equals(c.Title, histName, StringComparison.OrdinalIgnoreCase) &&
                        c.HasAnyReadProgress))
                {
                    continue;
                }

                var histComic = CreateQuickComicFromPath(normHist);
                if (histComic != null && histComic.Chapters.Count > 0 && histComic.HasAnyReadProgress)
                {
                    histComic.IsExternalHistoryItem = true;
                    histComic.LastReadOrder = ReadProgressStore.Instance.GetComicLastReadOrder(normHist);
                    histComic.SourceLocationLabel = BuildShortLocationBadge(histComic.RootFolderPath, isExternal: true);
                    currentList.Add(histComic);
                    existingNormalizedPaths.Add(ReadProgressStore.NormalizeKey(histComic.RootFolderPath));
                }
            }
        }
        catch { }
    }

    public static string BuildShortLocationBadge(string fullPath, bool isExternal)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return string.Empty;
        try
        {
            string clean = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(clean);
            string folderName = !string.IsNullOrWhiteSpace(parent)
                ? Path.GetFileName(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : clean;
            if (string.IsNullOrWhiteSpace(folderName)) folderName = clean;

            return isExternal ? $"🕒 Lịch sử • {folderName}" : $"📁 {folderName}";
        }
        catch
        {
            return isExternal ? "🕒 Lịch sử đọc" : string.Empty;
        }
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
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false
            };
            subDirs = Directory.GetDirectories(currentDir, "*", enumOptions);
            files = Directory.GetFiles(currentDir, "*", enumOptions);
        }
        catch
        {
            return;
        }

        var archiveFiles = files.Where(IsArchiveFile).NaturalSort();
        var pdfFiles = files.Where(IsPdfFile).NaturalSort();
        var mobiFiles = files.Where(IsMobiFile).NaturalSort();
        var directImages = files.Where(IsImageFile).NaturalSort();

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

        // Phân loại các file .zip/.cbz/.cbr/.rar/.pdf/.mobi:
        // - Nếu 1 file chứa nhiều chương bên trong hoặc nằm trong thư mục gốc chung không có chapterDirs -> Tạo thành 1 Bộ Truyện (LibraryComicItem) độc lập!
        var singleChapterArchives = new List<string>();
        var singleChapterPdfs = new List<string>();
        var singleChapterMobis = new List<string>();

        foreach (string archPath in archiveFiles)
        {
            if (ct.IsCancellationRequested) return;
            int tempIdx = 1;
            var expanded = ExpandArchiveIntoChapters(archPath, ref tempIdx, out string? firstEntry);
            if (expanded.Count == 0) continue;

            if (expanded.Count >= 2 || (chapterDirs.Count == 0 && archiveFiles.Count + pdfFiles.Count + mobiFiles.Count >= 1))
            {
                var standaloneComic = BuildStandaloneComicFromExpandedFile(archPath, currentDir, rootDir, expanded, isPdf: false, firstArchiveEntry: firstEntry);
                results.Add(standaloneComic);
            }
            else
            {
                singleChapterArchives.Add(archPath);
            }
        }

        foreach (string pdfPath in pdfFiles)
        {
            if (ct.IsCancellationRequested) return;
            int tempIdx = 1;
            var expanded = ExpandPdfIntoChapters(pdfPath, ref tempIdx);
            if (expanded.Count == 0) continue;

            if (expanded.Count >= 2 || (chapterDirs.Count == 0 && archiveFiles.Count + pdfFiles.Count + mobiFiles.Count >= 1))
            {
                var standaloneComic = BuildStandaloneComicFromExpandedFile(pdfPath, currentDir, rootDir, expanded, isPdf: true, firstArchiveEntry: null);
                results.Add(standaloneComic);
            }
            else
            {
                singleChapterPdfs.Add(pdfPath);
            }
        }

        foreach (string mobiPath in mobiFiles)
        {
            if (ct.IsCancellationRequested) return;
            int tempIdx = 1;
            var expanded = MobiComicParser.Instance.ExpandMobiIntoChapters(mobiPath, ref tempIdx);
            if (expanded.Count == 0) continue;

            if (expanded.Count >= 2 || (chapterDirs.Count == 0 && archiveFiles.Count + pdfFiles.Count + mobiFiles.Count >= 1))
            {
                var standaloneComic = BuildStandaloneComicFromExpandedFile(mobiPath, currentDir, rootDir, expanded, isPdf: false, firstArchiveEntry: null);
                standaloneComic.CoverMobiPath = mobiPath;
                results.Add(standaloneComic);
            }
            else
            {
                singleChapterMobis.Add(mobiPath);
            }
        }

        if (chapterDirs.Count > 0 || singleChapterArchives.Count > 0 || singleChapterPdfs.Count > 0 || singleChapterMobis.Count > 0)
        {
            var comic = BuildComicItem(currentDir, rootDir, chapterDirs, singleChapterArchives, singleChapterPdfs, singleChapterMobis);
            if (comic.Chapters.Count > 0)
            {
                results.Add(comic);
            }

            foreach (string nextDir in nonChapterDirs)
            {
                if (ct.IsCancellationRequested) break;
                ScanDirectoryRecursive(nextDir, rootDir, depth + 1, maxDepth, results, ct);
            }
            return;
        }

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

        foreach (string nextDir in nonChapterDirs)
        {
            if (ct.IsCancellationRequested) break;
            ScanDirectoryRecursive(nextDir, rootDir, depth + 1, maxDepth, results, ct);
        }
    }

    private LibraryComicItem BuildStandaloneComicFromExpandedFile(
        string filePath,
        string parentDir,
        string rootDir,
        List<LibraryChapterItem> chapters,
        bool isPdf,
        string? firstArchiveEntry)
    {
        string title = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(title)) title = "Truyện Đã Tải";

        string category = string.Empty;
        try
        {
            string cleanParent = parentDir.TrimEnd('\\', '/');
            if (!string.IsNullOrWhiteSpace(cleanParent) &&
                !string.Equals(cleanParent, rootDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                category = Path.GetFileName(cleanParent);
            }
        }
        catch { }

        var comic = new LibraryComicItem
        {
            Title = title,
            RootFolderPath = filePath,
            CategoryName = category
        };

        if (isPdf)
        {
            comic.CoverPdfPath = filePath;
        }
        else if (!string.IsNullOrEmpty(firstArchiveEntry))
        {
            comic.CoverArchivePath = filePath;
            comic.CoverArchiveEntry = firstArchiveEntry;
        }

        for (int i = 0; i < chapters.Count; i++)
        {
            chapters[i].Index = i + 1;
            comic.Chapters.Add(chapters[i]);
        }

        comic.RefreshReadStatus();
        return comic;
    }

    private LibraryComicItem BuildComicItem(
        string comicDir,
        string rootDir,
        List<(string DirPath, List<string> ImageFiles)> chapterDirs,
        List<string> archiveFiles,
        List<string> pdfFiles,
        List<string>? mobiFiles = null)
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
                ProgressKey = dirPath,
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

        // 2. Nạp các file .cbz / .zip / .cbr / .rar (tự động tách thành các chương nhỏ nếu trong file nén có nhiều thư mục chương)
        foreach (string archivePath in archiveFiles)
        {
            var expanded = ExpandArchiveIntoChapters(archivePath, ref idx, out string? firstEntry);
            foreach (var ch in expanded)
            {
                comic.Chapters.Add(ch);
            }

            if (string.IsNullOrEmpty(comic.CoverFilePath) &&
                string.IsNullOrEmpty(comic.CoverArchivePath) &&
                !string.IsNullOrEmpty(firstEntry))
            {
                comic.CoverArchivePath = archivePath;
                comic.CoverArchiveEntry = firstEntry;
            }
        }

        // 3. Nạp các file .pdf (tự động tách thành các chương nhỏ theo Mục lục /Outlines hoặc chia phần thông minh)
        foreach (string pdfPath in pdfFiles)
        {
            var expanded = ExpandPdfIntoChapters(pdfPath, ref idx);
            foreach (var ch in expanded)
            {
                comic.Chapters.Add(ch);
            }

            if (string.IsNullOrEmpty(comic.CoverFilePath) &&
                string.IsNullOrEmpty(comic.CoverArchivePath) &&
                string.IsNullOrEmpty(comic.CoverPdfPath))
            {
                comic.CoverPdfPath = pdfPath;
            }
        }

        // 4. Nạp các file .mobi (eBook / Comic Kindle)
        if (mobiFiles != null)
        {
            foreach (string mobiPath in mobiFiles)
            {
                var expanded = MobiComicParser.Instance.ExpandMobiIntoChapters(mobiPath, ref idx);
                foreach (var ch in expanded)
                {
                    comic.Chapters.Add(ch);
                }

                if (string.IsNullOrEmpty(comic.CoverFilePath) &&
                    string.IsNullOrEmpty(comic.CoverArchivePath) &&
                    string.IsNullOrEmpty(comic.CoverPdfPath) &&
                    string.IsNullOrEmpty(comic.CoverMobiPath))
                {
                    comic.CoverMobiPath = mobiPath;
                }
            }
        }

        comic.RefreshReadStatus();
        return comic;
    }

    private static List<LibraryChapterItem> ExpandPdfIntoChapters(string pdfPath, ref int nextIndex)
    {
        var list = new List<LibraryChapterItem>();
        var pdfMeta = PdfComicParser.Instance.InspectPdf(pdfPath);
        int totalPdfPages = pdfMeta.Pages.Count;
        if (totalPdfPages <= 0) return list;

        DateTime lastMod = DateTime.MinValue;
        try { lastMod = File.GetLastWriteTime(pdfPath); } catch { }

        int legacyGlobalPage1Based = ReadProgressStore.Instance.GetLastReadPage(pdfPath);

        // Lọc và chuẩn hóa danh sách Bookmarks hợp lệ
        var validBookmarks = new List<PdfBookmarkItem>();
        if (pdfMeta.Bookmarks != null && pdfMeta.Bookmarks.Count > 0)
        {
            foreach (var bm in pdfMeta.Bookmarks.OrderBy(b => b.PageIndex))
            {
                int clampedPage = Math.Clamp(bm.PageIndex, 0, totalPdfPages - 1);
                if (validBookmarks.Count > 0 && validBookmarks[^1].PageIndex == clampedPage)
                {
                    // Nếu 2 bookmark cùng trỏ vào 1 trang (ví dụ node tên truyện và Chương 1), ưu tiên giữ tên Chương cụ thể hơn
                    validBookmarks[^1] = new PdfBookmarkItem
                    {
                        Title = bm.Title,
                        PageIndex = clampedPage
                    };
                }
                else
                {
                    validBookmarks.Add(new PdfBookmarkItem
                    {
                        Title = bm.Title,
                        PageIndex = clampedPage
                    });
                }
            }
        }

        // TRƯỜNG HỢP 1: File PDF có chứa Mục lục chương (/Outlines Bookmarks >= 2 chương)
        if (validBookmarks.Count >= 2)
        {
            if (validBookmarks[0].PageIndex > 0)
            {
                int introEnd = validBookmarks[0].PageIndex - 1;
                int introCount = introEnd + 1;
                string introKey = $"{pdfPath}::pdf_0_{introEnd}";
                int introSaved = ReadProgressStore.Instance.GetLastReadPage(introKey);

                list.Add(new LibraryChapterItem
                {
                    Index = nextIndex++,
                    Title = "Mở đầu / Bìa",
                    LocalPath = pdfPath,
                    ProgressKey = introKey,
                    IsPdf = true,
                    PdfStartPageIndex = 0,
                    PdfEndPageIndex = introEnd,
                    TotalPages = introCount,
                    LastReadPage = introSaved,
                    LastModified = lastMod
                });
            }

            for (int i = 0; i < validBookmarks.Count; i++)
            {
                var bm = validBookmarks[i];
                int startPage = bm.PageIndex;
                int endPage = (i + 1 < validBookmarks.Count)
                    ? Math.Max(startPage, validBookmarks[i + 1].PageIndex - 1)
                    : (totalPdfPages - 1);
                int count = endPage - startPage + 1;

                string progressKey = $"{pdfPath}::pdf_{startPage}_{endPage}";
                int savedPage = ReadProgressStore.Instance.GetLastReadPage(progressKey);

                // Tự động chuyển đổi vị trí trang tổng (legacy) sang đúng chương và trang con
                if (savedPage <= 0 && legacyGlobalPage1Based > 1)
                {
                    int legacyIdx0 = legacyGlobalPage1Based - 1;
                    if (legacyIdx0 >= startPage && legacyIdx0 <= endPage)
                    {
                        savedPage = (legacyIdx0 - startPage) + 1;
                        ReadProgressStore.Instance.SaveProgress(progressKey, savedPage - 1);
                        ReadProgressStore.Instance.SaveLastActiveChapterIndex(pdfPath, nextIndex);
                    }
                }

                list.Add(new LibraryChapterItem
                {
                    Index = nextIndex++,
                    Title = string.IsNullOrWhiteSpace(bm.Title) ? $"Chương {i + 1}" : bm.Title,
                    LocalPath = pdfPath,
                    ProgressKey = progressKey,
                    IsPdf = true,
                    PdfStartPageIndex = startPage,
                    PdfEndPageIndex = endPage,
                    TotalPages = count,
                    LastReadPage = savedPage,
                    LastModified = lastMod
                });
            }

            return list;
        }

        // TRƯỜNG HỢP 2: File PDF lớn (> 100 trang) nhưng không có Bookmarks -> Tự động chia thành các chương nhỏ 80 trang
        if (totalPdfPages > 100)
        {
            int partNum = 1;
            for (int startPage = 0; startPage < totalPdfPages; startPage += DefaultAutoSplitPagesPerChapter)
            {
                int endPage = Math.Min(totalPdfPages - 1, startPage + DefaultAutoSplitPagesPerChapter - 1);
                int count = endPage - startPage + 1;
                string progressKey = $"{pdfPath}::pdf_{startPage}_{endPage}";
                int savedPage = ReadProgressStore.Instance.GetLastReadPage(progressKey);

                if (savedPage <= 0 && legacyGlobalPage1Based > 1)
                {
                    int legacyIdx0 = legacyGlobalPage1Based - 1;
                    if (legacyIdx0 >= startPage && legacyIdx0 <= endPage)
                    {
                        savedPage = (legacyIdx0 - startPage) + 1;
                        ReadProgressStore.Instance.SaveProgress(progressKey, savedPage - 1);
                        ReadProgressStore.Instance.SaveLastActiveChapterIndex(pdfPath, nextIndex);
                    }
                }

                list.Add(new LibraryChapterItem
                {
                    Index = nextIndex++,
                    Title = $"Chương {partNum} (Trang {startPage + 1} - {endPage + 1})",
                    LocalPath = pdfPath,
                    ProgressKey = progressKey,
                    IsPdf = true,
                    PdfStartPageIndex = startPage,
                    PdfEndPageIndex = endPage,
                    TotalPages = count,
                    LastReadPage = savedPage,
                    LastModified = lastMod
                });
                partNum++;
            }

            return list;
        }

        // TRƯỜNG HỢP 3: File PDF ngắn (<= 100 trang) -> 1 chương duy nhất
        list.Add(new LibraryChapterItem
        {
            Index = nextIndex++,
            Title = Path.GetFileNameWithoutExtension(pdfPath),
            LocalPath = pdfPath,
            ProgressKey = pdfPath,
            IsPdf = true,
            PdfStartPageIndex = 0,
            PdfEndPageIndex = totalPdfPages - 1,
            PdfBookmarks = validBookmarks,
            TotalPages = totalPdfPages,
            LastReadPage = legacyGlobalPage1Based,
            LastModified = lastMod
        });

        return list;
    }

    public static List<LibraryChapterItem> ExpandArchiveIntoChapters(
        string archivePath,
        ref int nextIndex,
        out string? firstCoverEntry)
    {
        firstCoverEntry = null;
        var list = new List<LibraryChapterItem>();

        try
        {
            bool isRar = IsRarFile(archivePath);
            List<string> allImageEntries;

            if (isRar)
            {
                var rarEntries = RarArchivePool.Instance.GetImageEntriesInfo(archivePath);
                allImageEntries = rarEntries.Select(e => e.FullName).ToList();
            }
            else
            {
                using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

                allImageEntries = zip.Entries
                    .Where(e => !string.IsNullOrEmpty(e.Name) && IsImageFile(e.Name) && e.Length > 0)
                    .Select(e => e.FullName)
                    .ToList();
            }

            if (allImageEntries.Count == 0) return list;

            DateTime lastMod = DateTime.MinValue;
            try { lastMod = File.GetLastWriteTime(archivePath); } catch { }

            string archiveBaseName = Path.GetFileNameWithoutExtension(archivePath);
            int legacyGlobalPage1Based = ReadProgressStore.Instance.GetLastReadPage(archivePath);
            string scheme = isRar ? "rar" : "zip";

            // Nhóm các ảnh theo thư mục cha bên trong file .zip / .cbz / .rar / .cbr
            var folderGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string fullName in allImageEntries)
            {
                string folderKey = GetArchiveEntryParentFolder(fullName);
                if (!folderGroups.TryGetValue(folderKey, out var groupList))
                {
                    groupList = new List<string>();
                    folderGroups[folderKey] = groupList;
                }
                groupList.Add(fullName);
            }

            // TRƯỜNG HỢP 1: File .zip/.cbz/.rar/.cbr chứa nhiều thư mục chương con (ví dụ: Chuong 1/, Chuong 2/, ...)
            if (folderGroups.Count >= 2)
            {
                var sortedFolderKeys = folderGroups.Keys
                    .OrderBy(k => k, NaturalSortComparer.Instance)
                    .ToList();

                int runningPages = 0;
                foreach (string folderKey in sortedFolderKeys)
                {
                    var sortedEntries = folderGroups[folderKey];
                    sortedEntries.Sort(NaturalSortComparer.Instance);

                    if (sortedEntries.Count == 0) continue;
                    firstCoverEntry ??= sortedEntries[0];

                    string chTitle = ExtractReadableChapterTitleFromArchiveFolder(folderKey, archiveBaseName);
                    string progressKey = $"{archivePath}::{scheme}_{folderKey}";
                    int savedPage = ReadProgressStore.Instance.GetLastReadPage(progressKey);

                    if (savedPage <= 0 && legacyGlobalPage1Based > 1)
                    {
                        int legacyIdx0 = legacyGlobalPage1Based - 1;
                        if (legacyIdx0 >= runningPages && legacyIdx0 < runningPages + sortedEntries.Count)
                        {
                            savedPage = (legacyIdx0 - runningPages) + 1;
                            ReadProgressStore.Instance.SaveProgress(progressKey, savedPage - 1);
                            ReadProgressStore.Instance.SaveLastActiveChapterIndex(archivePath, nextIndex);
                        }
                    }

                    runningPages += sortedEntries.Count;

                    list.Add(new LibraryChapterItem
                    {
                        Index = nextIndex++,
                        Title = chTitle,
                        LocalPath = archivePath,
                        ProgressKey = progressKey,
                        IsCbz = !isRar,
                        IsRar = isRar,
                        ArchiveEntryNames = sortedEntries,
                        TotalPages = sortedEntries.Count,
                        LastReadPage = savedPage,
                        LastModified = lastMod
                    });
                }

                return list;
            }

            // Sắp xếp toàn bộ ảnh trong trường hợp chỉ có 1 thư mục
            var flatSorted = allImageEntries
                .OrderBy(e => e, NaturalSortComparer.Instance)
                .ToList();
            firstCoverEntry = flatSorted[0];

            // TRƯỜNG HỢP 2: File phẳng nhưng có quá nhiều ảnh (> 120 trang) -> chia thành các chương 80 trang
            if (flatSorted.Count > 120)
            {
                int partNum = 1;
                for (int start = 0; start < flatSorted.Count; start += DefaultAutoSplitPagesPerChapter)
                {
                    int count = Math.Min(DefaultAutoSplitPagesPerChapter, flatSorted.Count - start);
                    var slice = flatSorted.GetRange(start, count);
                    int end = start + count - 1;

                    string progressKey = $"{archivePath}::{scheme}_part_{start}_{end}";
                    int savedPage = ReadProgressStore.Instance.GetLastReadPage(progressKey);

                    if (savedPage <= 0 && legacyGlobalPage1Based > 1)
                    {
                        int legacyIdx0 = legacyGlobalPage1Based - 1;
                        if (legacyIdx0 >= start && legacyIdx0 <= end)
                        {
                            savedPage = (legacyIdx0 - start) + 1;
                            ReadProgressStore.Instance.SaveProgress(progressKey, savedPage - 1);
                            ReadProgressStore.Instance.SaveLastActiveChapterIndex(archivePath, nextIndex);
                        }
                    }

                    list.Add(new LibraryChapterItem
                    {
                        Index = nextIndex++,
                        Title = $"Chương {partNum} (Trang {start + 1} - {end + 1})",
                        LocalPath = archivePath,
                        ProgressKey = progressKey,
                        IsCbz = !isRar,
                        IsRar = isRar,
                        ArchiveEntryNames = slice,
                        TotalPages = slice.Count,
                        LastReadPage = savedPage,
                        LastModified = lastMod
                    });
                    partNum++;
                }

                return list;
            }

            // TRƯỜNG HỢP 3: File 1 chương bình thường
            list.Add(new LibraryChapterItem
            {
                Index = nextIndex++,
                Title = archiveBaseName,
                LocalPath = archivePath,
                ProgressKey = archivePath,
                IsCbz = !isRar,
                IsRar = isRar,
                ArchiveEntryNames = flatSorted,
                TotalPages = flatSorted.Count,
                LastReadPage = legacyGlobalPage1Based,
                LastModified = lastMod
            });
        }
        catch { }

        return list;
    }

    private static string GetArchiveEntryParentFolder(string entryFullName)
    {
        if (string.IsNullOrEmpty(entryFullName)) return string.Empty;
        string normalized = entryFullName.Replace('\\', '/').TrimStart('/');
        int lastSlash = normalized.LastIndexOf('/');
        return lastSlash > 0 ? normalized.Substring(0, lastSlash) : string.Empty;
    }

    private static string ExtractReadableChapterTitleFromArchiveFolder(string folderKey, string archiveBaseName)
    {
        if (string.IsNullOrWhiteSpace(folderKey)) return archiveBaseName;
        string[] parts = folderKey.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return archiveBaseName;

        // Nếu thư mục gốc trùng tên file zip (ví dụ: "Một Ngày Nọ.../Chuong 1"), lấy phần tử con ("Chuong 1")
        if (parts.Length >= 2 && string.Equals(parts[0], archiveBaseName, StringComparison.OrdinalIgnoreCase))
        {
            return string.Join(" - ", parts.Skip(1));
        }

        return parts[^1];
    }

    /// <summary>
    /// Kiểm tra tên thư mục có khớp với định dạng chương truyện (Chap, Chapter, Vol, Số thứ tự...) hay không.
    /// </summary>
    public static bool IsChapterNamePattern(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string clean = name.Trim();
        if (Regex.IsMatch(clean, @"^(chap|ch|chapter|vol|volume|tập|tap|hồi|hoi|bài|bai|ep|episode)(\b|\s|_|-|\.|\d)", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(clean, @"^\d+$"))
            return true;
        if (Regex.IsMatch(clean, @"^[cvep]\d+$", RegexOptions.IgnoreCase))
            return true;
        return false;
    }

    /// <summary>
    /// Nhận diện xem một đường dẫn tệp ảnh hoặc thư mục chương có thuộc về một thư mục truyện cha hay không.
    /// Giúp nhận diện chính xác Comic Root (folder tổng của truyện) và Chapter, tránh ghi nhận các chương con
    /// (như 'chap 0410', 'chap 0401') thành các truyện độc lập riêng lẻ làm rác danh sách History.
    /// </summary>
    /// <summary>
    /// Kiểm tra xem một đường dẫn thư mục có phải là thư mục hệ thống hoặc thư mục người dùng chung
    /// (Downloads, Desktop, Documents, Pictures, root ổ đĩa, Android storage root...) hay không.
    /// Tuyệt đối KHÔNG BAO GIỜ nhận các thư mục này làm Parent Comic Root!
    /// </summary>
    public static bool IsCommonSystemOrUserDirectory(string? dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath)) return true;
        try
        {
            string clean = dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? root = Path.GetPathRoot(clean);
            if (string.IsNullOrEmpty(root)) return true;
            if (string.Equals(clean, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return true;

            var specialFolders = new[]
            {
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.Desktop,
                Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.MyPictures,
                Environment.SpecialFolder.MyMusic,
                Environment.SpecialFolder.MyVideos,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.System,
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.CommonApplicationData
            };

            foreach (var sf in specialFolders)
            {
                try
                {
                    string p = Environment.GetFolderPath(sf);
                    if (!string.IsNullOrWhiteSpace(p) && string.Equals(clean, p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch { }
            }

            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(userProfile))
                {
                    string downloads = Path.Combine(userProfile, "Downloads");
                    if (string.Equals(clean, downloads.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch { }

            try
            {
                string temp = Path.GetTempPath();
                if (!string.IsNullOrWhiteSpace(temp) && string.Equals(clean, temp.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch { }

            if (OperatingSystem.IsAndroid() || clean.StartsWith("/storage/", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("/sdcard", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(clean, "/storage/emulated/0", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(clean, "/storage/emulated/0/Download", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(clean, "/storage/emulated/0/DCIM", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(clean, "/storage/emulated/0/Pictures", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(clean, "/sdcard", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(clean, "/sdcard/Download", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    public static bool TryGetParentComicRoot(string targetPath, out string parentComicRoot, out string chapterName)
    {
        parentComicRoot = string.Empty;
        chapterName = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(targetPath)) return false;

            // Xử lý khi targetPath là FILE (.zip, .cbz, .rar, .cbr, .pdf, .mobi hoặc file ảnh)
            if (File.Exists(targetPath))
            {
                string? fileDir = Path.GetDirectoryName(targetPath);
                if (string.IsNullOrWhiteSpace(fileDir) || !Directory.Exists(fileDir)) return false;

                string cleanFileDir = fileDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string? rootOfFileDir = Path.GetPathRoot(cleanFileDir);
                if (string.Equals(cleanFileDir, rootOfFileDir?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string fileName = Path.GetFileName(targetPath);
                string fileNameNoExt = Path.GetFileNameWithoutExtension(targetPath);
                bool fileIsChapter = IsChapterNamePattern(fileNameNoExt) || ReadProgressStore.ExtractChapterNumber(fileNameNoExt) >= 0;

                var enumOpts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
                int siblingArchives = 0;
                int siblingImages = 0;
                try
                {
                    siblingArchives = Directory.EnumerateFiles(cleanFileDir, "*", enumOpts)
                        .Count(f => IsArchiveFile(f) || IsPdfFile(f) || IsMobiFile(f));
                    siblingImages = Directory.EnumerateFiles(cleanFileDir, "*", enumOpts).Count(IsImageFile);
                }
                catch { }

                // Nếu là file nén/sách và trong thư mục có nhiều file nén anh em hoặc tên file là chapter:
                // Thư mục chứa nó chính là Comic Root của bộ truyện (miễn là không phải thư mục hệ thống)!
                if ((IsArchiveFile(targetPath) || IsPdfFile(targetPath) || IsMobiFile(targetPath)) && (siblingArchives >= 2 || fileIsChapter))
                {
                    if (!IsCommonSystemOrUserDirectory(cleanFileDir))
                    {
                        parentComicRoot = cleanFileDir;
                        chapterName = !string.IsNullOrWhiteSpace(fileNameNoExt) ? fileNameNoExt : fileName;
                        return true;
                    }
                }

                // Nếu là file ảnh trong thư mục chương con:
                if (IsImageFile(targetPath))
                {
                    string folderName = Path.GetFileName(cleanFileDir);
                    if (IsChapterNamePattern(folderName))
                    {
                        string? parent = Path.GetDirectoryName(cleanFileDir);
                        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent) && !IsCommonSystemOrUserDirectory(parent))
                        {
                            parentComicRoot = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            chapterName = folderName;
                            return true;
                        }
                        else if (!IsCommonSystemOrUserDirectory(cleanFileDir))
                        {
                            parentComicRoot = cleanFileDir;
                            chapterName = folderName;
                            return true;
                        }
                    }
                    else if (siblingImages >= 1 && !IsCommonSystemOrUserDirectory(cleanFileDir))
                    {
                        parentComicRoot = cleanFileDir;
                        chapterName = folderName;
                        return true;
                    }
                }

                return false;
            }

            // Xử lý khi targetPath là DIRECTORY (Thư mục)
            if (Directory.Exists(targetPath))
            {
                string cleanDir = targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string? parent = Path.GetDirectoryName(cleanDir);
                if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent)) return false;

                // Không coi Root của ổ đĩa hoặc thư mục hệ thống (Downloads, Desktop...) là Comic Root
                if (IsCommonSystemOrUserDirectory(parent))
                {
                    return false;
                }

                // Tránh nhận diện nhầm thư mục quét thư viện chính làm Comic Root
                string? lastLibRoot = ReadProgressStore.Instance.GetLastLibraryRootPath();
                if (!string.IsNullOrWhiteSpace(lastLibRoot) &&
                    string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                  lastLibRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                  StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string currentFolderName = Path.GetFileName(cleanDir);
                bool isChapterName = IsChapterNamePattern(currentFolderName);

                var enumOpts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
                var siblingDirs = Directory.GetDirectories(parent, "*", enumOpts);
                int siblingWithImages = 0;
                int siblingChapterCount = 0;

                foreach (var sib in siblingDirs)
                {
                    string sibName = Path.GetFileName(sib);
                    if (sibName.StartsWith(".", StringComparison.Ordinal)) continue;
                    if (IsChapterNamePattern(sibName)) siblingChapterCount++;

                    try
                    {
                        if (Directory.EnumerateFiles(sib, "*", enumOpts).Any(IsImageFile))
                        {
                            siblingWithImages++;
                        }
                    }
                    catch { }
                }

                int siblingArchives = 0;
                try
                {
                    siblingArchives = Directory.EnumerateFiles(parent, "*", enumOpts).Count(f => IsArchiveFile(f) || IsPdfFile(f) || IsMobiFile(f));
                }
                catch { }

                // Chỉ quy về parent khi:
                // 1. Tên folder là chapter pattern VÀ parent có chứa các chapter sibling khác hoặc archive anh em
                // 2. Hoặc trong parent có từ 2 chapter con trở lên có ảnh
                // 3. Hoặc có từ 2 sibling mang tên chapter
                if ((isChapterName && (siblingDirs.Length > 1 || siblingArchives > 0)) ||
                    siblingWithImages >= 2 ||
                    siblingChapterCount >= 2)
                {
                    parentComicRoot = parent;
                    chapterName = currentFolderName;
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    public LibraryComicItem? CreateQuickComicFromPath(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return null;

        if (File.Exists(targetPath))
        {
            if (IsArchiveFile(targetPath))
            {
                string dir = Path.GetDirectoryName(targetPath) ?? string.Empty;
                int idx = 1;
                var expanded = ExpandArchiveIntoChapters(targetPath, ref idx, out string? firstEntry);
                if (expanded.Count == 0) return null;
                var comic = BuildStandaloneComicFromExpandedFile(targetPath, dir, dir, expanded, isPdf: false, firstArchiveEntry: firstEntry);
                comic.SourceLocationLabel = BuildShortLocationBadge(comic.RootFolderPath, comic.IsExternalHistoryItem);
                return comic;
            }

            if (IsPdfFile(targetPath))
            {
                string dir = Path.GetDirectoryName(targetPath) ?? string.Empty;
                int idx = 1;
                var expanded = ExpandPdfIntoChapters(targetPath, ref idx);
                if (expanded.Count == 0) return null;
                var comic = BuildStandaloneComicFromExpandedFile(targetPath, dir, dir, expanded, isPdf: true, firstArchiveEntry: null);
                comic.SourceLocationLabel = BuildShortLocationBadge(comic.RootFolderPath, comic.IsExternalHistoryItem);
                return comic;
            }

            if (IsMobiFile(targetPath))
            {
                string dir = Path.GetDirectoryName(targetPath) ?? string.Empty;
                int idx = 1;
                var expanded = MobiComicParser.Instance.ExpandMobiIntoChapters(targetPath, ref idx);
                if (expanded.Count == 0) return null;
                var comic = BuildStandaloneComicFromExpandedFile(targetPath, dir, dir, expanded, isPdf: false, firstArchiveEntry: null);
                comic.CoverMobiPath = targetPath;
                comic.SourceLocationLabel = BuildShortLocationBadge(comic.RootFolderPath, comic.IsExternalHistoryItem);
                return comic;
            }

            if (IsImageFile(targetPath))
            {
                string? dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    // Kiểm tra nếu thư mục chứa ảnh này là một chương con của bộ truyện cha
                    if (TryGetParentComicRoot(targetPath, out string parentRoot, out string chName))
                    {
                        var parentComic = BuildComicFromFolderAllChapters(parentRoot);
                        if (parentComic != null && parentComic.Chapters.Count > 0)
                        {
                            var matchedChap = parentComic.Chapters.FirstOrDefault(c =>
                                string.Equals(c.Title, chName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(c.LocalPath.TrimEnd('\\', '/'), dir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
                            matchedChap ??= parentComic.Chapters[0];

                            var siblingImgs = GetDirectImagesSafe(dir);
                            int targetIdx = siblingImgs.FindIndex(f => string.Equals(f, targetPath, StringComparison.OrdinalIgnoreCase));
                            matchedChap.LastReadPage = targetIdx >= 0 ? targetIdx + 1 : 1;

                            ReadProgressStore.Instance.SaveLastActiveChapterIndex(parentRoot, matchedChap.Index);
                            parentComic.SourceLocationLabel = BuildShortLocationBadge(parentComic.RootFolderPath, isExternal: true);
                            return parentComic;
                        }
                    }

                    // Tái sử dụng danh sách ảnh trực tiếp đã quét, không quét lại 20.000 file lần thứ hai!
                    var directImgs = GetDirectImagesSafe(dir);
                    if (directImgs.Count > 0)
                    {
                        int targetIdx = directImgs.FindIndex(f => string.Equals(f, targetPath, StringComparison.OrdinalIgnoreCase));
                        int initialPage = targetIdx >= 0 ? targetIdx + 1 : 1;

                        var comic = BuildComicItem(
                            dir,
                            dir,
                            new List<(string, List<string>)> { (dir, directImgs) },
                            new List<string>(),
                            new List<string>());
                        comic.SourceLocationLabel = BuildShortLocationBadge(comic.RootFolderPath, comic.IsExternalHistoryItem);
                        if (comic.Chapters.Count > 0)
                        {
                            comic.Chapters[0].LastReadPage = initialPage;
                        }
                        return comic;
                    }
                }

                var singleList = new List<string> { targetPath };
                string parent = Path.GetDirectoryName(targetPath) ?? targetPath;
                var singleComic = BuildComicItem(
                    parent,
                    parent,
                    new List<(string, List<string>)> { (parent, singleList) },
                    new List<string>(),
                    new List<string>());
                singleComic.Title = Path.GetFileName(targetPath);
                singleComic.SourceLocationLabel = BuildShortLocationBadge(targetPath, isExternal: true);
                return singleComic;
            }
        }

        if (Directory.Exists(targetPath))
        {
            // Kiểm tra nếu thư mục này là một chương con của bộ truyện cha
            if (TryGetParentComicRoot(targetPath, out string parentRoot, out string chName))
            {
                var parentComic = BuildComicFromFolderAllChapters(parentRoot);
                if (parentComic != null && parentComic.Chapters.Count > 0)
                {
                    var matchedChap = parentComic.Chapters.FirstOrDefault(c =>
                        string.Equals(c.Title, chName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.LocalPath.TrimEnd('\\', '/'), targetPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
                    if (matchedChap != null)
                    {
                        ReadProgressStore.Instance.SaveLastActiveChapterIndex(parentRoot, matchedChap.Index);
                    }
                    parentComic.SourceLocationLabel = BuildShortLocationBadge(parentComic.RootFolderPath, isExternal: true);
                    return parentComic;
                }
            }

            var directImgs = GetDirectImagesSafe(targetPath);
            if (directImgs.Count > 0)
            {
                var comic = BuildComicItem(
                    targetPath,
                    targetPath,
                    new List<(string, List<string>)> { (targetPath, directImgs) },
                    new List<string>(),
                    new List<string>());
                comic.SourceLocationLabel = BuildShortLocationBadge(comic.RootFolderPath, comic.IsExternalHistoryItem);
                return comic;
            }

            var tempResults = new List<LibraryComicItem>();
            try
            {
                ScanDirectoryRecursive(targetPath, targetPath, depth: 0, maxDepth: 4, tempResults, CancellationToken.None);
            }
            catch { }

            if (tempResults.Count > 0)
            {
                var best = tempResults.FirstOrDefault(c =>
                               string.Equals(
                                   c.RootFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                   targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                   StringComparison.OrdinalIgnoreCase))
                           ?? tempResults[0];
                best.SourceLocationLabel = BuildShortLocationBadge(best.RootFolderPath, best.IsExternalHistoryItem);
                return best;
            }
        }

        return null;
    }

    /// <summary>
    /// Xây dựng một bộ truyện hoàn chỉnh từ một thư mục chứa nhiều chapter con (hoặc nhiều file nén chapter con),
    /// cho phép đọc truyện xuyên suốt toàn bộ folder từ Chapter 1 đến Chapter N mà không bị gián đoạn.
    /// </summary>
    public LibraryComicItem? BuildComicFromFolderAllChapters(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return null;

        try
        {
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false
            };

            var subDirs = Directory.GetDirectories(folderPath, "*", enumOptions);
            var files = Directory.GetFiles(folderPath, "*", enumOptions);

            var chapters = new List<LibraryChapterItem>();
            int chapterIndex = 1;

            // 1. Quét các thư mục chapter con (ví dụ: Chapter 1, Chapter 2, Chap 01...)
            foreach (string sub in subDirs.NaturalSort())
            {
                string subName = Path.GetFileName(sub);
                if (subName.StartsWith(".", StringComparison.Ordinal)) continue;

                // Tối ưu hóa siêu tốc (Fast Probe): Chỉ kiểm tra có ảnh không thay vì load và sort toàn bộ ảnh của mọi chapter
                bool hasImages = Directory.EnumerateFiles(sub, "*", enumOptions).Any(IsImageFile);
                if (hasImages)
                {
                    int cachedPages = ReadProgressStore.Instance.GetChapterTotalPages(sub);
                    if (cachedPages <= 0)
                    {
                        cachedPages = Directory.EnumerateFiles(sub, "*", enumOptions).Count(IsImageFile);
                        if (cachedPages > 0)
                        {
                            ReadProgressStore.Instance.SaveChapterTotalPages(sub, cachedPages);
                        }
                    }

                    chapters.Add(new LibraryChapterItem
                    {
                        Index = chapterIndex++,
                        Title = subName,
                        LocalPath = sub,
                        TotalPages = cachedPages > 0 ? cachedPages : 1,
                        LastReadPage = ReadProgressStore.Instance.GetLastReadPage(sub)
                    });
                }
            }

            // 2. Quét các file nén / file chapter độc lập (ví dụ: Chap 1.zip, Chap 2.rar, Chap 3.cbz...)
            var archiveFiles = files.Where(IsArchiveFile).NaturalSort();
            foreach (string arch in archiveFiles)
            {
                int tempIdx = 1;
                var expanded = ExpandArchiveIntoChapters(arch, ref tempIdx, out string? firstEntry);
                if (expanded.Count > 0)
                {
                    foreach (var exp in expanded)
                    {
                        exp.Index = chapterIndex++;
                        chapters.Add(exp);
                    }
                }
            }

            // 3. Quét các file PDF / MOBI
            var pdfFiles = files.Where(IsPdfFile).NaturalSort();
            foreach (string pdf in pdfFiles)
            {
                int tempIdx = 1;
                var expanded = ExpandPdfIntoChapters(pdf, ref tempIdx);
                if (expanded.Count > 0)
                {
                    foreach (var exp in expanded)
                    {
                        exp.Index = chapterIndex++;
                        chapters.Add(exp);
                    }
                }
            }

            var mobiFiles = files.Where(IsMobiFile).NaturalSort();
            foreach (string mobi in mobiFiles)
            {
                int tempIdx = 1;
                var expanded = MobiComicParser.Instance.ExpandMobiIntoChapters(mobi, ref tempIdx);
                if (expanded.Count > 0)
                {
                    foreach (var exp in expanded)
                    {
                        exp.Index = chapterIndex++;
                        chapters.Add(exp);
                    }
                }
            }

            // 4. Nếu không có subfolder hay file nén, kiểm tra ảnh trực tiếp trong thư mục này
            if (chapters.Count == 0)
            {
                var directImgs = GetDirectImagesSafe(folderPath);
                if (directImgs.Count > 0)
                {
                    chapters.Add(new LibraryChapterItem
                    {
                        Index = 1,
                        Title = Path.GetFileName(folderPath),
                        LocalPath = folderPath,
                        TotalPages = directImgs.Count,
                        LastReadPage = ReadProgressStore.Instance.GetLastReadPage(folderPath)
                    });
                }
            }

            if (chapters.Count == 0) return null;

            // Đánh số lại Index chuẩn 1..N
            for (int i = 0; i < chapters.Count; i++)
            {
                chapters[i].Index = i + 1;
            }

            string comicTitle = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(comicTitle)) comicTitle = "Truyện Đã Tải";

            var comic = new LibraryComicItem
            {
                Title = comicTitle,
                RootFolderPath = folderPath,
                SourceLocationLabel = BuildShortLocationBadge(folderPath, isExternal: true)
            };

            foreach (var ch in chapters)
            {
                comic.Chapters.Add(ch);
            }

            // Gán ảnh bìa từ chapter đầu tiên
            var firstChap = chapters[0];
            if (firstChap.IsPdf)
            {
                comic.CoverPdfPath = firstChap.LocalPath;
            }
            else if (firstChap.IsMobi)
            {
                comic.CoverMobiPath = firstChap.LocalPath;
            }
            else if (firstChap.IsCbz || firstChap.IsRar)
            {
                comic.CoverArchivePath = firstChap.LocalPath;
                if (firstChap.ArchiveEntryNames != null && firstChap.ArchiveEntryNames.Count > 0)
                {
                    comic.CoverArchiveEntry = firstChap.ArchiveEntryNames[0];
                }
            }
            else if (Directory.Exists(firstChap.LocalPath))
            {
                var imgs = GetDirectImagesSafe(firstChap.LocalPath);
                if (imgs.Count > 0) comic.CoverFilePath = imgs[0];
            }

            comic.RefreshReadStatus();
            return comic;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> GetDirectImagesSafe(string folderPath)
    {
        try
        {
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false
            };
            return Directory.EnumerateFiles(folderPath, "*", enumOptions)
                .Where(IsImageFile)
                .NaturalSort();
        }
        catch
        {
            return new List<string>();
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
            if (!string.IsNullOrEmpty(comic.CoverMobiPath) && File.Exists(comic.CoverMobiPath))
            {
                return MobiComicParser.Instance.DecodeCoverThumbnail(comic.CoverMobiPath, 220);
            }

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
                if (IsRarFile(comic.CoverArchivePath))
                {
                    rawBytes = RarArchivePool.Instance.ReadEntryBytes(comic.CoverArchivePath, comic.CoverArchiveEntry);
                }
                else
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

    public Bitmap? DecodeCoverThumbnailForPath(string path, int targetWidth = 220)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            // 1. Kiểm tra Thumbnail Disk Cache trước (O(1) < 0.001ms, triệt tiêu đọc lại file gốc)
            var fastCachedBmp = ComicDiskCacheService.Instance.TryGetCachedThumbnail(path, targetWidth);
            if (fastCachedBmp != null)
            {
                return fastCachedBmp;
            }

            var bmp = DecodeCoverThumbnailInternal(path, targetWidth);
            if (bmp != null)
            {
                try
                {
                    using var ms = new MemoryStream();
#pragma warning disable CS0618
                    bmp.Save(ms);
#pragma warning restore CS0618
                    byte[] bytes = ms.ToArray();
                    if (bytes.Length > 0)
                    {
                        ComicDiskCacheService.Instance.SaveCachedThumbnail(path, targetWidth, bytes);
                    }
                }
                catch { }
            }
            return bmp;
        }
        catch { }

        return null;
    }

    private Bitmap? DecodeCoverThumbnailInternal(string path, int targetWidth)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            if (File.Exists(path))
            {
                if (IsMobiFile(path))
                {
                    return MobiComicParser.Instance.DecodeCoverThumbnail(path, targetWidth);
                }

                if (IsPdfFile(path))
                {
                    return PdfComicParser.Instance.DecodeCoverThumbnail(path, targetWidth);
                }

                if (IsRarFile(path))
                {
                    return RarArchivePool.Instance.DecodeCoverThumbnail(path, targetWidth);
                }

                if (IsArchiveFile(path))
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                    var imgEntry = zip.Entries
                        .Where(e => IsImageFile(e.FullName) && !e.FullName.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(e => e.FullName, NaturalSortComparer.Instance)
                        .FirstOrDefault();

                    if (imgEntry != null)
                    {
                        using var es = imgEntry.Open();
                        using var ms = new MemoryStream();
                        es.CopyTo(ms);
                        byte[] raw = ms.ToArray();
                        try
                        {
                            using var imgStream = new MemoryStream(raw);
                            return Bitmap.DecodeToWidth(imgStream, targetWidth);
                        }
                        catch
                        {
                            var (jpeg, _, _) = UniversalImageDecoder.ConvertToStandardJpeg(raw, 85);
                            if (jpeg.Length > 0)
                            {
                                using var ms2 = new MemoryStream(jpeg);
                                return Bitmap.DecodeToWidth(ms2, targetWidth);
                            }
                        }
                    }
                }

                if (IsImageFile(path))
                {
                    byte[] raw = File.ReadAllBytes(path);
                    try
                    {
                        using var imgStream = new MemoryStream(raw);
                        return Bitmap.DecodeToWidth(imgStream, targetWidth);
                    }
                    catch
                    {
                        var (jpeg, _, _) = UniversalImageDecoder.ConvertToStandardJpeg(raw, 85);
                        if (jpeg.Length > 0)
                        {
                            using var ms2 = new MemoryStream(jpeg);
                            return Bitmap.DecodeToWidth(ms2, targetWidth);
                        }
                    }
                }
            }
            else if (Directory.Exists(path))
            {
                var firstImg = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                    .Where(IsImageFile)
                    .OrderBy(f => f, NaturalSortComparer.Instance)
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(firstImg) && File.Exists(firstImg))
                {
                    byte[] raw = File.ReadAllBytes(firstImg);
                    try
                    {
                        using var imgStream = new MemoryStream(raw);
                        return Bitmap.DecodeToWidth(imgStream, targetWidth);
                    }
                    catch
                    {
                        var (jpeg, _, _) = UniversalImageDecoder.ConvertToStandardJpeg(raw, 85);
                        if (jpeg.Length > 0)
                        {
                            using var ms2 = new MemoryStream(jpeg);
                            return Bitmap.DecodeToWidth(ms2, targetWidth);
                        }
                    }
                }
            }
        }
        catch { }

        return null;
    }
}
