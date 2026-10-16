using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Input;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Đại diện cho một mục cấu hình phím tắt trong trình đọc truyện.
/// </summary>
public sealed class ReaderShortcutEntry
{
    public string Id { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> DefaultKeys { get; set; } = new();
    public List<string> AssignedKeys { get; set; } = new();

    public string CurrentKeysText =>
        AssignedKeys.Count > 0 ? string.Join(", ", AssignedKeys) : string.Join(", ", DefaultKeys);
}

/// <summary>
/// Dịch vụ quản lý phím tắt chuẩn BandiView / Honeyview và hỗ trợ người dùng tùy biến phím tắt.
/// Lưu trữ bền vững tại file JSON trong thư mục cấu hình của ứng dụng.
/// </summary>
public sealed class ReaderShortcutService
{
    private static readonly Lazy<ReaderShortcutService> _lazy = new(() => new ReaderShortcutService());
    public static ReaderShortcutService Instance => _lazy.Value;

    private readonly List<ReaderShortcutEntry> _entries = new();
    private readonly object _lock = new();

    public IReadOnlyList<ReaderShortcutEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToList();
            }
        }
    }

    private ReaderShortcutService()
    {
        InitDefaultEntries();
        LoadFromDisk();
    }

    private static string GetConfigPath()
    {
        string dir = ComicScraperService.GetConfigDirectory();
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
        return Path.Combine(dir, "reader_shortcuts.json");
    }

    private void InitDefaultEntries()
    {
        _entries.Clear();

        // 1. NHÓM DI CHUYỂN & TRANG (MOVE) - Chuẩn BandiView
        AddEntry("PageNext", "Di chuyển (Move)", "Trang sau (Next Page)", "Chuyển sang trang tiếp theo hoặc cuộn xuống",
            new[] { "Right", "PageDown", "Down", "Space", "D" });

        AddEntry("PagePrev", "Di chuyển (Move)", "Trang trước (Previous Page)", "Quay lại trang trước hoặc cuộn lên",
            new[] { "Left", "PageUp", "Up", "Shift+Space", "Back", "A" });

        AddEntry("PageFirst", "Di chuyển (Move)", "Trang đầu tiên (First Image)", "Về trang đầu tiên của chương",
            new[] { "Home", "Ctrl+Home" });

        AddEntry("PageLast", "Di chuyển (Move)", "Trang cuối cùng (Last Image)", "Đến trang cuối cùng của chương",
            new[] { "End", "Ctrl+End" });

        AddEntry("ChapterNext", "Di chuyển (Move)", "Chương sau (Next Chapter)", "Chuyển sang chương kế tiếp trong danh sách",
            new[] { "OemCloseBrackets", "Ctrl+Right", "Shift+Right" });

        AddEntry("ChapterPrev", "Di chuyển (Move)", "Chương trước (Previous Chapter)", "Quay về chương trước đó trong danh sách",
            new[] { "OemOpenBrackets", "Ctrl+Left", "Shift+Left" });

        // 2. NHÓM XEM & THU PHÓNG (VIEW) - Chuẩn BandiView
        AddEntry("FullScreen", "Hiển thị (View)", "Bật/Tắt Toàn màn hình (Full Screen)", "Mở hoặc thoát chế độ toàn màn hình",
            new[] { "F11", "Alt+Enter", "Enter" });

        AddEntry("ToggleOverlay", "Hiển thị (View)", "Ẩn/Hiện thanh công cụ (Toggle Overlay)", "Ẩn hoặc hiện thanh công cụ và thông tin trang",
            new[] { "Tab" });

        AddEntry("ZoomIn", "Hiển thị (View)", "Phóng to (Zoom In)", "Phóng to trang truyện",
            new[] { "OemPlus", "Add", "Ctrl+OemPlus" });

        AddEntry("ZoomOut", "Hiển thị (View)", "Thu nhỏ (Zoom Out)", "Thu nhỏ trang truyện",
            new[] { "OemMinus", "Subtract", "Ctrl+OemMinus" });

        AddEntry("ResetZoom", "Hiển thị (View)", "Đặt lại thu phóng (Reset Zoom)", "Khôi phục độ phóng to về mặc định",
            new[] { "Alt+D0", "Z" });

        AddEntry("Zoom100", "Hiển thị (View)", "Kích thước gốc 100% (Original 1:1)", "Hiển thị kích thước gốc 100% của ảnh (Phím 0)",
            new[] { "D0", "NumPad0" });

        AddEntry("CycleFitMode", "Kiểu đọc (View Mode)", "Chuyển đổi vừa khung hình (Cycle Fit Mode)", "Chuyển đổi tuần tự: Fit Width → Fit Height → Fit Screen → Stretch Full (Phím 1)",
            new[] { "D1", "NumPad1" });

        // 3. NHÓM CHẾ ĐỘ ĐỌC (VIEW MODE) - Chuẩn BandiView
        AddEntry("TogglePageLayout", "Kiểu đọc (View Mode)", "Bố cục trang (Toggle Page Layout)", "Chuyển đổi bố cục trang: Tắt / 1 Trang / 2 Trang / Tự động (Phím 2)",
            new[] { "D2", "NumPad2" });

        AddEntry("FitWidth", "Kiểu đọc (View Mode)", "Vừa chiều rộng (Fit to Width)", "Tỉ lệ vừa khít chiều ngang màn hình (Phím 8/W)",
            new[] { "D8", "NumPad8", "W" });

        AddEntry("FitHeight", "Kiểu đọc (View Mode)", "Vừa chiều cao (Fit to Height/Window)", "Tỉ lệ vừa khít chiều dọc màn hình (Phím 9/H)",
            new[] { "D9", "NumPad9", "H" });

        AddEntry("FitScreen", "Kiểu đọc (View Mode)", "Vừa màn hình (Fit to Screen)", "Hiển thị toàn bộ trang trong khung nhìn (Ctrl+0 / Phím 3 / B)",
            new[] { "Ctrl+D0", "Ctrl+NumPad0", "D3", "NumPad3", "B" });

        AddEntry("StretchFull", "Kiểu đọc (View Mode)", "Kéo tràn màn hình (Stretch Full)", "Kéo giãn ảnh lấp đầy toàn bộ khung nhìn (Phím S)",
            new[] { "S" });

        AddEntry("WebtoonMode", "Kiểu đọc (View Mode)", "Kiểu đọc Webtoon (Cuộn dọc)", "Chuyển sang chế độ cuộn dọc liên tục (Phím 5)",
            new[] { "D5", "NumPad5" });

        AddEntry("MangaRtlMode", "Kiểu đọc (View Mode)", "Manga Phải → Trái (Flip RTL)", "Lật trang từ Phải sang Trái (Phím 6)",
            new[] { "D6", "NumPad6" });

        AddEntry("MangaLtrMode", "Kiểu đọc (View Mode)", "Manga Trái → Phải (Flip LTR)", "Lật trang từ Trái sang Phải (Phím 7)",
            new[] { "D7", "NumPad7" });

        AddEntry("RtlScrollMode", "Kiểu đọc (View Mode)", "Cuộn ngang Phải → Trái (RTL Scroll)", "Cuộn ngang liên tục từ Phải sang Trái (Ctrl+6)",
            new[] { "Ctrl+D6", "Ctrl+NumPad6" });

        AddEntry("LtrScrollMode", "Kiểu đọc (View Mode)", "Cuộn ngang Trái → Phải (LTR Scroll)", "Cuộn ngang liên tục từ Trái sang Phải (Ctrl+7)",
            new[] { "Ctrl+D7", "Ctrl+NumPad7" });

        // 4. NHÓM THAO TÁC & HOẠT ẢNH (ACTION & ANIMATION)
        AddEntry("ToggleBookmark", "Thao tác (Action)", "Đánh dấu trang (Toggle Bookmark)", "Thêm hoặc xóa dấu trang cho trang hiện tại (Ctrl+D)",
            new[] { "Ctrl+D" });

        AddEntry("BookmarkList", "Thao tác (Action)", "Danh sách dấu trang (Bookmark List)", "Mở danh sách các trang đã đánh dấu (Shift+D)",
            new[] { "Shift+D" });

        AddEntry("ImageProperties", "Hiển thị (View)", "Thuộc tính hình ảnh (Image Properties)", "Bật/Tắt bảng thông tin chi tiết độ phân giải, dung lượng, codec, container (Phím `)",
            new[] { "OemTilde", "Oem3" });


        AddEntry("ChapterDrawer", "Thao tác (Action)", "Danh sách chương (Chapter Drawer)", "Mở danh sách các chương của truyện",
            new[] { "C", "F" });

        AddEntry("AutoScroll", "Thao tác (Action)", "Tự động cuộn (Auto Scroll)", "Bật/Tắt chế độ tự động cuộn trang",
            new[] { "V" });

        AddEntry("OpenSettings", "Thao tác (Action)", "Cài đặt trình đọc (Configuration)", "Mở bảng cấu hình và phím tắt (F5)",

            new[] { "F5" });

        AddEntry("CloseReader", "Thao tác (Action)", "Thoát trình đọc (Exit Reader)", "Thoát chế độ đọc truyện",
            new[] { "Escape", "Ctrl+W", "X" });
    }

    private void AddEntry(string id, string group, string name, string description, string[] defaultKeys)
    {
        _entries.Add(new ReaderShortcutEntry
        {
            Id = id,
            Group = group,
            Name = name,
            Description = description,
            DefaultKeys = defaultKeys.ToList(),
            AssignedKeys = defaultKeys.ToList()
        });
    }

    public static string BuildGestureString(Key key, KeyModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");

        string keyStr = key.ToString();
        parts.Add(keyStr);
        return string.Join("+", parts);
    }

    /// <summary>
    /// Tra cứu xem phím vừa nhấn có khớp với hành động nào trong danh mục phím tắt hay không.
    /// </summary>
    public string? MatchAction(Key key, KeyModifiers modifiers)
    {
        string gesture = BuildGestureString(key, modifiers);

        lock (_lock)
        {
            foreach (var entry in _entries)
            {
                var keys = entry.AssignedKeys.Count > 0 ? entry.AssignedKeys : entry.DefaultKeys;
                foreach (var k in keys)
                {
                    if (string.Equals(k, gesture, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.Id;
                    }
                    // Trường hợp không có modifier
                    if (modifiers == KeyModifiers.None && string.Equals(k, key.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.Id;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gán phím tắt mới cho một hành động.
    /// </summary>
    public void AssignShortcut(string actionId, string gesture)
    {
        if (string.IsNullOrWhiteSpace(actionId) || string.IsNullOrWhiteSpace(gesture)) return;

        lock (_lock)
        {
            var target = _entries.FirstOrDefault(e => string.Equals(e.Id, actionId, StringComparison.OrdinalIgnoreCase));
            if (target != null)
            {
                // Xóa gesture này ở các action khác nếu bị trùng để tránh xung đột
                foreach (var other in _entries)
                {
                    other.AssignedKeys.RemoveAll(k => string.Equals(k, gesture, StringComparison.OrdinalIgnoreCase));
                }

                if (!target.AssignedKeys.Contains(gesture, StringComparer.OrdinalIgnoreCase))
                {
                    target.AssignedKeys.Insert(0, gesture);
                }
            }
        }

        SaveToDisk();
    }

    /// <summary>
    /// Đặt lại toàn bộ phím tắt về giá trị mặc định chuẩn BandiView.
    /// </summary>
    public void ResetToDefaults()
    {
        lock (_lock)
        {
            foreach (var entry in _entries)
            {
                entry.AssignedKeys = entry.DefaultKeys.ToList();
            }
        }

        SaveToDisk();
    }

    public void SaveToDisk()
    {
        try
        {
            string path = GetConfigPath();
            var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            lock (_lock)
            {
                foreach (var entry in _entries)
                {
                    dict[entry.Id] = entry.AssignedKeys;
                }
            }

            string json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, System.Text.Encoding.UTF8);
        }
        catch { }
    }

    private void LoadFromDisk()
    {
        try
        {
            string path = GetConfigPath();
            if (!File.Exists(path)) return;

            string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            var dict = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
            if (dict == null) return;

            lock (_lock)
            {
                foreach (var entry in _entries)
                {
                    if (dict.TryGetValue(entry.Id, out var keys) && keys != null && keys.Count > 0)
                    {
                        entry.AssignedKeys = keys;
                    }
                }
            }
        }
        catch { }
    }
}

