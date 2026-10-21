using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ComicDownloaderGMTPC.Services;

public class LanguageService
{
    private static readonly Lazy<LanguageService> _instance = new(() => new LanguageService());
    public static LanguageService Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, string> _enToVi = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _viToEn = new(StringComparer.OrdinalIgnoreCase);

    public string CurrentLanguage { get; set; } = "VI"; // "VI" or "EN"

    public bool IsVietnamese => string.Equals(CurrentLanguage, "VI", StringComparison.OrdinalIgnoreCase);

    public event Action? LanguageChanged;

    public LanguageService()
    {
        LoadBuiltinDictionary();
        LoadFromLanguagesFiles();
    }

    public void SetLanguage(string lang)
    {
        string normalized = string.Equals(lang, "EN", StringComparison.OrdinalIgnoreCase) ? "EN" : "VI";
        if (string.Equals(CurrentLanguage, normalized, StringComparison.OrdinalIgnoreCase)) return;
        CurrentLanguage = normalized;
        LanguageChanged?.Invoke();
    }

    public void ToggleLanguage()
    {
        SetLanguage(IsVietnamese ? "EN" : "VI");
    }

    public string Get(string key)
    {
        return Translate(key, IsVietnamese);
    }

    public string Translate(string text, bool toVietnamese)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        string trimmed = text.Trim();

        // 1. Bóc tách tiền tố emoji / icon
        string prefix = string.Empty;
        var iconMatch = Regex.Match(trimmed, @"^([^\p{L}\p{N}]+)(?=[\p{L}\p{N}])");
        if (iconMatch.Success)
        {
            prefix = iconMatch.Groups[1].Value;
            trimmed = trimmed.Substring(prefix.Length).Trim();
        }

        // 2. Xử lý mẫu chuỗi song ngữ lồng nhau dạng "Left (Right)"
        // Ví dụ: "TAB SOURCE (NGUỒN TRUYỆN)", "Mở Web (Browse)", "CÀO MỚI (CRAWL)", "Bong bóng (Bubble)", "Queue (Hàng chờ)"
        var mixedMatch = Regex.Match(trimmed, @"^(?<left>[^()]+?)\s*\((?<right>[^()]+)\)$");
        if (mixedMatch.Success)
        {
            string left = mixedMatch.Groups["left"].Value.Trim();
            string right = mixedMatch.Groups["right"].Value.Trim();

            if (toVietnamese)
            {
                if (LooksLikeVietnamese(left) && !LooksLikeVietnamese(right))
                    return prefix + Translate(left, true);
                if (LooksLikeVietnamese(right) && !LooksLikeVietnamese(left))
                    return prefix + Translate(right, true);

                if (_enToVi.TryGetValue(left, out var viFromLeft)) return prefix + viFromLeft;
                if (_enToVi.TryGetValue(right, out var viFromRight)) return prefix + viFromRight;
                if (_viToEn.ContainsKey(left)) return prefix + left;
                if (_viToEn.ContainsKey(right)) return prefix + right;

                return prefix + (LooksLikeVietnamese(right) ? right : left);
            }
            else
            {
                if (!LooksLikeVietnamese(left) && LooksLikeVietnamese(right))
                    return prefix + Translate(left, false);
                if (!LooksLikeVietnamese(right) && LooksLikeVietnamese(left))
                    return prefix + Translate(right, false);

                if (_viToEn.TryGetValue(left, out var enFromLeft)) return prefix + enFromLeft;
                if (_viToEn.TryGetValue(right, out var enFromRight)) return prefix + enFromRight;
                if (_enToVi.ContainsKey(left)) return prefix + left;
                if (_enToVi.ContainsKey(right)) return prefix + right;

                return prefix + (!LooksLikeVietnamese(left) ? left : right);
            }
        }

        // 3. Tra từ điển chuẩn xác theo core text
        if (toVietnamese)
        {
            if (_enToVi.TryGetValue(trimmed, out var vi) && !string.IsNullOrWhiteSpace(vi))
            {
                return prefix + vi;
            }
            if (_viToEn.ContainsKey(trimmed))
            {
                return prefix + trimmed;
            }
        }
        else
        {
            if (_viToEn.TryGetValue(trimmed, out var en) && !string.IsNullOrWhiteSpace(en))
            {
                return prefix + en;
            }
            if (_enToVi.ContainsKey(trimmed))
            {
                return prefix + trimmed;
            }
        }

        return text;
    }

    private static bool LooksLikeVietnamese(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        return Regex.IsMatch(s, @"[àáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđĐ]");
    }

    public void AddPair(string en, string vi)
    {
        if (string.IsNullOrWhiteSpace(en) || string.IsNullOrWhiteSpace(vi)) return;
        _enToVi[en.Trim()] = vi.Trim();
        _viToEn[vi.Trim()] = en.Trim();
    }

    private void LoadBuiltinDictionary()
    {
        // Core Fallbacks
        AddPair("AppTitle", "Comic-GMTPC Avalonia v1.0 - Tiếng Việt");

        // Main Tabs
        AddPair("TAB SOURCE", "TAB NGUỒN");
        AddPair("TAB SEARCH", "TAB TÌM TRUYỆN");
        AddPair("TAB DOWNLOAD", "TAB TẢI VỀ");
        AddPair("TAB TOOL", "TAB CÔNG CỤ");
        AddPair("TAB TOOLS", "TAB CÔNG CỤ");
        AddPair("TAB LIBRARY", "TAB THƯ VIỆN");
        AddPair("Source", "Nguồn");
        AddPair("Search", "Tìm truyện");
        AddPair("Download", "Tải về");
        AddPair("Tools", "Công cụ");
        AddPair("Tool", "Công cụ");
        AddPair("Library", "Thư viện");

        // Subtabs
        AddPair("Manga Source", "Nguồn Manga");
        AddPair("Hentai Source", "Nguồn Hentai");
        AddPair("Novel Source", "Nguồn Tiểu Thuyết");
        AddPair("Search Comic", "Tìm Truyện");
        AddPair("Search", "Tìm Kiếm");
        AddPair("Password / Account", "Tài Khoản / Mật Khẩu");
        AddPair("Queue", "Hàng Chờ");
        AddPair("Download Queue", "Hàng Chờ Tải");
        AddPair("Chờ Tải", "Hàng Chờ");
        AddPair("Missing Chapters", "Quét Thiếu Chap");
        AddPair("Scan Missing Chapters", "Scan Thiếu Chap");
        AddPair("Logs", "Nhật Ký");
        AddPair("Realtime Logs", "Nhật Ký Hệ Thống");

        // Tool Tabs
        AddPair("Folder Tools", "Tách / Gộp Thư Mục");
        AddPair("Split / Merge Folder", "Tách / Gộp Thư Mục");
        AddPair("Split Long Images", "Cắt Ảnh Dài");
        AddPair("Image Enhancer", "Nâng Cấp Ảnh");
        AddPair("Package & Convert", "Đóng Gói & Chuyển Đổi");
        AddPair("Archive Merger", "Gộp File Lẻ");
        AddPair("Merge / Append Chapters", "Gộp File / Nối Chap");
        AddPair("Append Chapters", "Nối Chap Vào File");
        AddPair("Offline Missing Scanner", "Quét Chap Thiếu Offline");
        AddPair("Batch Rename", "Đổi Tên Hàng Loạt");
        AddPair("Dual Picture Sort", "Phân Loại Ảnh Kép");
        AddPair("File Association", "Liên Kết Tệp");

        // Modes & Options
        AddPair("Single comic", "Truyện đơn");
        AddPair("Single Comic", "Truyện đơn");
        AddPair("Multi-comic", "Nhiều truyện");
        AddPair("Multi-Comic", "Nhiều truyện");
        AddPair("Folder:", "Thư mục:");
        AddPair("Bubble Mode", "Bong bóng");
        AddPair("Bubble", "Bong bóng");
        AddPair("Manual Download", "Tải thủ công");
        AddPair("Direct", "Trực tiếp");
        AddPair("Embedded", "Nhúng");

        // Main Action Buttons
        AddPair("GET LINKS", "LẤY LINK");
        AddPair("GET LINK", "LẤY LINK");
        AddPair("PASTE & GET", "DÁN & LẤY");
        AddPair("PASTE LINK", "DÁN LINK");
        AddPair("ADD MORE", "THÊM");
        AddPair("CLEAR", "XÓA");
        AddPair("CLEAR LINKS", "XÓA LINK");
        AddPair("DOWNLOAD ALL", "TẢI TẤT CẢ");
        AddPair("DOWNLOAD", "TẢI");
        AddPair("STOP", "DỪNG");
        AddPair("DOWNLOAD MORE", "TẢI TIẾP");
        AddPair("PAUSE", "TẠM DỪNG");
        AddPair("RESUME", "TIẾP TỤC");
        AddPair("RETRY", "THỬ LẠI");
        AddPair("CLEAR QUEUE", "XÓA HÀNG CHỜ");
        AddPair("EXIT", "THOÁT");
        AddPair("BACKUP MD", "SAO LƯU MD");
        AddPair("RESTORE MD", "PHỤC HỒI MD");
        AddPair("GOOGLE DRIVE", "GOOGLE DRIVE");
        AddPair("CACHE", "BỘ NHỚ ĐỆM");
        AddPair("CHANGE FOLDER", "ĐỔI THƯ MỤC");
        AddPair("OPEN FOLDER", "MỞ THƯ MỤC");

        // Checkbox Options
        AddPair("Auto Download", "Tự động tải");
        AddPair("Auto Retry", "Tự thử lại");
        AddPair("Compact Row", "Nén dòng");
        AddPair("Auto Scroll (10s)", "Tự cuộn (10s)");
        AddPair("Auto Delete Completed", "Tự xóa đã xong");
        AddPair("Auto Split Long Images", "Tự cắt ảnh dài");
        AddPair("Auto Paste", "Tự dán");
        AddPair("APPLY", "ÁP DỤNG");
        AddPair("Apply", "Áp dụng");

        // Source Web Actions
        AddPair("Browse", "Mở Web");
        AddPair("Open Web", "Mở Web");
        AddPair("Restore Default Tag", "Khôi phục Tag mặc định");
        AddPair("Browse History", "Lịch sử duyệt");
        AddPair("Save Bookmark", "Lưu Bookmark");
        AddPair("ANALYZE", "PHÂN TÍCH");
        AddPair("CRAWL", "CÀO MỚI");
        AddPair("CRAWL MORE", "CÀO THÊM");
        AddPair("Stop Crawl", "Dừng cào");

        // Queue Toolbar
        AddPair("Select All", "Tất cả");
        AddPair("Unselect All", "Bỏ chọn");
        AddPair("Invert", "Đảo chọn");
        AddPair("Invert Selection", "Đảo chọn");
        AddPair("Check", "Tích");
        AddPair("Check Selected", "Tích");
        AddPair("Errors", "Lỗi");
        AddPair("Duplicates", "Trùng");
        AddPair("Clear Completed", "Xóa đã xong");
        AddPair("Delete Selected", "Xóa");
        AddPair("Delete", "Xóa");
        AddPair("Rescrape Chapters", "Cào lại chương");
        AddPair("Rescan Covers", "Quét lại ảnh bìa");
        AddPair("Preview", "Xem trước");
        AddPair("Compact Rows", "Dòng gọn");
        AddPair("Lock All Subfolders", "Khóa tất cả Subfolder");
        AddPair("Unlock All Subfolders", "Mở tất cả Subfolder");

        // Update Flyout
        AddPair("Check Update Release", "Kiểm tra cập nhật Release");
        AddPair("Update Release Now", "Cập nhật Release ngay");
        AddPair("Check Update Beta", "Kiểm tra cập nhật Beta");
        AddPair("Update Beta Now", "Cập nhật Beta ngay");

        // Library Reader
        AddPair("Library", "Thư Viện");
        AddPair("Scan Library", "Quét Thư Viện");
        AddPair("Open CBZ / ZIP", "Mở CBZ / ZIP");
        AddPair("Continue Reading", "Đọc Tiếp");
        AddPair("Vertical Scroll", "Cuộn Dọc");
        AddPair("Page Flip", "Lật Trang");
        AddPair("Double Page", "2 Trang");
    }

    private void LoadFromLanguagesFiles()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] fileNames = new[] { "languages.md", "MainWindow.LanguageEng-VI.md" };

            foreach (var fn in fileNames)
            {
                string[] possiblePaths = new[]
                {
                    Path.Combine(baseDir, fn),
                    Path.Combine(baseDir, "..", "..", "..", fn),
                    Path.Combine(baseDir, "..", "..", "..", "..", fn),
                    Path.Combine(Directory.GetCurrentDirectory(), fn)
                };

                foreach (var p in possiblePaths)
                {
                    if (File.Exists(p))
                    {
                        try
                        {
                            LoadMarkdownTable(Path.GetFullPath(p));
                        }
                        catch { }
                        break;
                    }
                }
            }
        }
        catch { }
    }

    private void LoadMarkdownTable(string path)
    {
        var lines = File.ReadAllLines(path, System.Text.Encoding.UTF8);
        var rowRegex = new Regex(@"^\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*(?:\|.*)?$", RegexOptions.Compiled);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("|") || trimmed.Contains("---") || trimmed.StartsWith("| English") || trimmed.StartsWith("| Tiêu đề"))
            {
                continue;
            }

            var match = rowRegex.Match(trimmed);
            if (match.Success)
            {
                string en = match.Groups[1].Value.Trim().Replace("\\n", "\n");
                string vi = match.Groups[2].Value.Trim().Replace("\\n", "\n");

                if (!string.IsNullOrEmpty(en) && !string.IsNullOrEmpty(vi))
                {
                    AddPair(en, vi);
                }
            }
        }
    }
}

