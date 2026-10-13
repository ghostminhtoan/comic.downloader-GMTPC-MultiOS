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

    public static Func<List<string>>? AndroidGetExternalStoragePaths;

    public static readonly HashSet<string> ValidImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".jfif"
    };

    public static readonly HashSet<string> ValidArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cbz", ".zip"
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

        // Phân loại các file .zip/.cbz/.pdf:
        // - Nếu 1 file .pdf hoặc .zip/.cbz chứa nhiều chương bên trong (ví dụ Ta Luyện Khí Ba Ngàn Năm.pdf có 381 chương,
        //   hoặc Một Ngày Nọ Tôi Bỗng Thành Nàng Công Chúa.zip có hàng trăm thư mục chương), hoặc nằm trong thư mục gốc chung
        //   không có chapterDirs -> Tạo thành 1 Bộ Truyện (LibraryComicItem) độc lập với đầy đủ danh sách chương con!
        var singleChapterArchives = new List<string>();
        var singleChapterPdfs = new List<string>();

        foreach (string archPath in archiveFiles)
        {
            if (ct.IsCancellationRequested) return;
            int tempIdx = 1;
            var expanded = ExpandArchiveIntoChapters(archPath, ref tempIdx, out string? firstEntry);
            if (expanded.Count == 0) continue;

            if (expanded.Count >= 2 || (chapterDirs.Count == 0 && archiveFiles.Count + pdfFiles.Count >= 1))
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

            if (expanded.Count >= 2 || (chapterDirs.Count == 0 && archiveFiles.Count + pdfFiles.Count >= 1))
            {
                var standaloneComic = BuildStandaloneComicFromExpandedFile(pdfPath, currentDir, rootDir, expanded, isPdf: true, firstArchiveEntry: null);
                results.Add(standaloneComic);
            }
            else
            {
                singleChapterPdfs.Add(pdfPath);
            }
        }

        if (chapterDirs.Count > 0 || singleChapterArchives.Count > 0 || singleChapterPdfs.Count > 0)
        {
            var comic = BuildComicItem(currentDir, rootDir, chapterDirs, singleChapterArchives, singleChapterPdfs);
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

        // 2. Nạp các file .cbz / .zip (tự động tách thành các chương nhỏ nếu trong file nén có nhiều thư mục chương)
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

    private static List<LibraryChapterItem> ExpandArchiveIntoChapters(
        string archivePath,
        ref int nextIndex,
        out string? firstCoverEntry)
    {
        firstCoverEntry = null;
        var list = new List<LibraryChapterItem>();

        try
        {
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

            var allImageEntries = zip.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) && IsImageFile(e.Name) && e.Length > 0)
                .Select(e => e.FullName)
                .ToList();

            if (allImageEntries.Count == 0) return list;

            DateTime lastMod = DateTime.MinValue;
            try { lastMod = File.GetLastWriteTime(archivePath); } catch { }

            string archiveBaseName = Path.GetFileNameWithoutExtension(archivePath);
            int legacyGlobalPage1Based = ReadProgressStore.Instance.GetLastReadPage(archivePath);

            // Nhóm các ảnh theo thư mục cha bên trong file .zip / .cbz
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

            // TRƯỜNG HỢP 1: File .zip/.cbz chứa nhiều thư mục chương con (ví dụ: Chuong 1/, Chuong 2/, ...)
            if (folderGroups.Count >= 2)
            {
                var sortedFolderKeys = folderGroups.Keys
                    .OrderBy(k => k, NaturalSortComparer.Instance)
                    .ToList();

                int runningPages = 0;
                foreach (string folderKey in sortedFolderKeys)
                {
                    var sortedEntries = folderGroups[folderKey]
                        .OrderBy(e => e, NaturalSortComparer.Instance)
                        .ToList();

                    if (sortedEntries.Count == 0) continue;
                    firstCoverEntry ??= sortedEntries[0];

                    string chTitle = ExtractReadableChapterTitleFromArchiveFolder(folderKey, archiveBaseName);
                    string progressKey = $"{archivePath}::zip_{folderKey}";
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
                        IsCbz = true,
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

            // TRƯỜNG HỢP 2: File .zip/.cbz phẳng nhưng có quá nhiều ảnh (> 120 trang) -> chia thành các chương 80 trang
            if (flatSorted.Count > 120)
            {
                int partNum = 1;
                for (int start = 0; start < flatSorted.Count; start += DefaultAutoSplitPagesPerChapter)
                {
                    int count = Math.Min(DefaultAutoSplitPagesPerChapter, flatSorted.Count - start);
                    var slice = flatSorted.GetRange(start, count);
                    int end = start + count - 1;

                    string progressKey = $"{archivePath}::zip_part_{start}_{end}";
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
                        IsCbz = true,
                        ArchiveEntryNames = slice,
                        TotalPages = slice.Count,
                        LastReadPage = savedPage,
                        LastModified = lastMod
                    });
                    partNum++;
                }

                return list;
            }

            // TRƯỜNG HỢP 3: File .cbz/.zip 1 chương bình thường
            list.Add(new LibraryChapterItem
            {
                Index = nextIndex++,
                Title = archiveBaseName,
                LocalPath = archivePath,
                ProgressKey = archivePath,
                IsCbz = true,
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
        }

        if (Directory.Exists(targetPath))
        {
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

    public Bitmap? DecodeCoverThumbnailForPath(string path, int targetWidth = 220)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            if (File.Exists(path))
            {
                if (IsPdfFile(path))
                {
                    return PdfComicParser.Instance.DecodeCoverThumbnail(path, targetWidth);
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
