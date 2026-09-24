using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;

namespace ComicDownloaderGMTPC.Services;

public class LanguageService
{
    private static readonly Lazy<LanguageService> _instance = new(() => new LanguageService());
    public static LanguageService Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, string> _enToVi = new(StringComparer.OrdinalIgnoreCase);
    public string CurrentLanguage { get; set; } = "VI"; // "VI" or "EN"

    public event Action? LanguageChanged;

    public LanguageService()
    {
        LoadDefaultDictionary();
        LoadFromLanguagesFile();
    }

    public void SetLanguage(string lang)
    {
        if (string.Equals(CurrentLanguage, lang, StringComparison.OrdinalIgnoreCase)) return;
        CurrentLanguage = lang.ToUpperInvariant();
        LanguageChanged?.Invoke();
    }

    public void ToggleLanguage()
    {
        SetLanguage(CurrentLanguage == "VI" ? "EN" : "VI");
    }

    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        if (CurrentLanguage == "EN")
        {
            return key;
        }

        if (_enToVi.TryGetValue(key, out var vi) && !string.IsNullOrWhiteSpace(vi))
        {
            return vi;
        }

        return key;
    }

    private void LoadFromLanguagesFile()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] possiblePaths = new[]
            {
                Path.Combine(baseDir, "languages.md"),
                Path.Combine(baseDir, "..", "..", "..", "languages.md"),
                Path.Combine(baseDir, "..", "..", "..", "..", "languages.md")
            };

            string? foundPath = null;
            foreach (var p in possiblePaths)
            {
                if (File.Exists(p))
                {
                    foundPath = Path.GetFullPath(p);
                    break;
                }
            }

            if (foundPath != null)
            {
                var lines = File.ReadAllLines(foundPath, System.Text.Encoding.UTF8);
                var rowRegex = new Regex(@"^\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|$", RegexOptions.Compiled);

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("|") || trimmed.Contains("---") || trimmed.StartsWith("| English"))
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
                            _enToVi[en] = vi;
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback dictionary is already loaded
        }
    }

    private void LoadDefaultDictionary()
    {
        // Core fallbacks to guarantee instant responsive UI even before disk access
        _enToVi["AppTitle"] = "Comic-GMTPC Avalonia v1.0 - Tiếng Việt";
        _enToVi["STEP 1"] = "BƯỚC 1";
        _enToVi["STEP 2"] = "BƯỚC 2";
        _enToVi["Source"] = "Nguồn";
        _enToVi["Download"] = "TẢI VỀ";
        _enToVi["GET LINK"] = "LẤY LINK";
        _enToVi["PASTE & GET"] = "DÁN & LẤY";
        _enToVi["GET MORE"] = "LẤY THÊM";
        _enToVi["CLEAR"] = "XÓA LINK";
        _enToVi["DOWNLOAD ALL"] = "TẢI TẤT CẢ";
        _enToVi["STOP"] = "DỪNG";
        _enToVi["RETRY"] = "THỬ LẠI";
        _enToVi["CLEAR QUEUE"] = "XÓA HÀNG CHỜ";
        _enToVi["AUTO DOWNLOAD"] = "TỰ ĐỘNG TẢI";
        _enToVi["AUTO RETRY UNTIL SETTLED"] = "TỰ THỬ LẠI ĐẾN KHI XONG";
        _enToVi["COMPACT ROW"] = "NÉN DÒNG";
        _enToVi["POPUP PREVIEW"] = "XEM TRƯỚC POPUP";
        _enToVi["DETAILS"] = "DANH SÁCH";
        _enToVi["THUMBNAIL"] = "ẢNH BÌA";
        _enToVi["EXTRACTED GALLERY LINKS"] = "Danh sách truyện chờ tải";
        _enToVi["SCAN MISSING INTEGER CHAPTER"] = "Scan chap số nguyên thiếu";
        _enToVi["LOGS"] = "Nhật ký";
        _enToVi["STATUS"] = "Trạng thái";
        _enToVi["PROCESS"] = "Tiến trình";
        _enToVi["LATEST CHAPTER"] = "CHƯƠNG MỚI NHẤT";
        _enToVi["Single comic"] = "Truyện đơn";
        _enToVi["Multi-comic"] = "Nhiều truyện";
    }
}
