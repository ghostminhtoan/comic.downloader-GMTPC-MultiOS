using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Dịch vụ quản lý File Association (Mở mặc định) và Context Menu chuột phải
/// cho Windows (HKCU Registry - không cần quyền Admin) và Linux (XDG Desktop & MIME).
/// Tuyệt đối không can thiệp trên Android.
/// </summary>
public sealed class FileAssociationService
{
    private static readonly Lazy<FileAssociationService> _lazy = new(() => new FileAssociationService());
    public static FileAssociationService Instance => _lazy.Value;

    // Danh sách định dạng mở mặc định (Ảnh và Sách truyện)
    public static readonly string[] DefaultImageBookExtensions = new[]
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif", ".pdf", ".mobi"
    };

    // Danh sách định dạng tệp nén chỉ thêm Context Menu (giữ nguyên WinRAR / 7-Zip mở mặc định)
    public static readonly string[] ArchiveContextMenuExtensions = new[]
    {
        ".zip", ".rar", ".cbz", ".cbr"
    };

    public const string ProgId = "ComicDownloaderGMTPC.Doc";
    public const string AppExecutableBaseName = "ComicDownloaderGMTPC.Desktop.exe";
    public const string LinuxAppDesktopName = "comicdownloadergmtpc.desktop";
    public const string ContextMenuVerb = "ComicDownloaderGMTPC";
    public const string ContextMenuTitle = "📖 Đọc truyện bằng Comic Downloader GMTPC";

    public bool IsSupportedPlatform => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public string PlatformDescription
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return "🪟 Windows (Hỗ trợ 100% qua User Registry HKCU, không cần Administrator)";
            if (OperatingSystem.IsLinux())
                return "🐧 Linux (Hỗ trợ 100% qua XDG Desktop Entry & ~/.config/mimeapps.list, không cần Root)";
            return "📱 Android (Không áp dụng File Association hệ thống)";
        }
    }

    #region Win32 Registry API P/Invoke (advapi32.dll & shell32.dll)

    private static readonly UIntPtr HKEY_CURRENT_USER = new UIntPtr(0x80000001u);
    private const int KEY_ALL_ACCESS = 0xF003F;
    private const int KEY_READ = 0x20019;
    private const int REG_SZ = 1;

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegCreateKeyEx(
        UIntPtr hKey,
        string lpSubKey,
        int Reserved,
        string? lpClass,
        int dwOptions,
        int samDesired,
        IntPtr lpSecurityAttributes,
        out UIntPtr phkResult,
        out int lpdwDisposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyEx(
        UIntPtr hKey,
        string lpSubKey,
        int ulOptions,
        int samDesired,
        out UIntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueEx(
        UIntPtr hKey,
        string? lpValueName,
        int Reserved,
        int dwType,
        string lpData,
        int cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueEx(
        UIntPtr hKey,
        string? lpValueName,
        int lpReserved,
        out int lpType,
        StringBuilder lpData,
        ref int lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegDeleteValue(UIntPtr hKey, string? lpValueName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegDeleteTree(UIntPtr hKey, string? lpSubKey);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(UIntPtr hKey);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    #endregion

    /// <summary>
    /// Lấy đường dẫn file thực thi của ứng dụng hiện tại trên Windows hoặc Linux.
    /// </summary>
    public string GetExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) && File.Exists(processPath))
            return processPath;

        try
        {
            string? modulePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(modulePath) && File.Exists(modulePath))
                return modulePath;
        }
        catch { }

        string fallbackName = OperatingSystem.IsWindows() ? "ComicDownloaderGMTPC.Desktop.exe" : "ComicDownloaderGMTPC";
        string fallbackPath = Path.Combine(AppContext.BaseDirectory, fallbackName);
        return fallbackPath;
    }

    #region Public Unified API

    public (bool Success, string Message) RegisterDefaultImageAndBookAssociations()
    {
        if (OperatingSystem.IsWindows())
            return RegisterWindowsDefaultAssociations();
        if (OperatingSystem.IsLinux())
            return RegisterLinuxDefaultAssociations();

        return (false, "Nền tảng này không hỗ trợ thiết lập File Association.");
    }

    public (bool Success, string Message) UnregisterDefaultImageAndBookAssociations()
    {
        if (OperatingSystem.IsWindows())
            return UnregisterWindowsDefaultAssociations();
        if (OperatingSystem.IsLinux())
            return UnregisterLinuxDefaultAssociations();

        return (false, "Nền tảng này không hỗ trợ thiết lập File Association.");
    }

    public (bool Success, string Message) RegisterArchiveContextMenu()
    {
        if (OperatingSystem.IsWindows())
            return RegisterWindowsArchiveContextMenu();
        if (OperatingSystem.IsLinux())
            return RegisterLinuxArchiveContextMenu();

        return (false, "Nền tảng này không hỗ trợ thiết lập Context Menu.");
    }

    public (bool Success, string Message) UnregisterArchiveContextMenu()
    {
        if (OperatingSystem.IsWindows())
            return UnregisterWindowsArchiveContextMenu();
        if (OperatingSystem.IsLinux())
            return UnregisterLinuxArchiveContextMenu();

        return (false, "Nền tảng này không hỗ trợ thiết lập Context Menu.");
    }

    public (bool Success, string Message) RegisterAll()
    {
        var r1 = RegisterDefaultImageAndBookAssociations();
        var r2 = RegisterArchiveContextMenu();
        bool ok = r1.Success && r2.Success;
        string msg = ok
            ? "✅ Đã kích hoạt toàn bộ: Mở mặc định (Ảnh, PDF, MOBI) và Menu ngữ cảnh (ZIP, RAR, CBZ, CBR) thành công!"
            : $"{r1.Message}\n{r2.Message}";
        return (ok, msg);
    }

    public (bool Success, string Message) UnregisterAll()
    {
        var r1 = UnregisterDefaultImageAndBookAssociations();
        var r2 = UnregisterArchiveContextMenu();
        bool ok = r1.Success && r2.Success;
        string msg = ok
            ? "✅ Đã gỡ bỏ toàn bộ liên kết tệp và menu ngữ cảnh, khôi phục trạng thái ban đầu của hệ điều hành!"
            : $"{r1.Message}\n{r2.Message}";
        return (ok, msg);
    }

    public bool CheckDefaultImageBookRegistered()
    {
        if (OperatingSystem.IsWindows())
            return CheckWindowsDefaultRegistered();
        if (OperatingSystem.IsLinux())
            return CheckLinuxDefaultRegistered();
        return false;
    }

    public bool CheckArchiveContextMenuRegistered()
    {
        if (OperatingSystem.IsWindows())
            return CheckWindowsArchiveContextMenuRegistered();
        if (OperatingSystem.IsLinux())
            return CheckLinuxArchiveContextMenuRegistered();
        return false;
    }

    #endregion

    #region Windows Implementation (HKCU Registry)

    private (bool Success, string Message) RegisterWindowsDefaultAssociations()
    {
        try
        {
            string exePath = GetExecutablePath();
            string commandStr = $"\"{exePath}\" \"%1\"";
            string iconStr = $"\"{exePath}\",0";

            // 1. Tạo ProgID: HKCU\Software\Classes\ComicDownloaderGMTPC.Doc
            SetRegistryValue(@"Software\Classes\" + ProgId, null, "Comic Downloader GMTPC Document");
            SetRegistryValue(@"Software\Classes\" + ProgId, "FriendlyTypeName", "Tệp Truyện / Hình Ảnh (Comic Downloader GMTPC)");
            SetRegistryValue($@"Software\Classes\{ProgId}\DefaultIcon", null, iconStr);
            SetRegistryValue($@"Software\Classes\{ProgId}\shell\open\command", null, commandStr);

            // 2. Đăng ký Application trong HKCU\Software\Classes\Applications\ComicDownloaderGMTPC.Desktop.exe
            string appKey = @"Software\Classes\Applications\" + Path.GetFileName(exePath);
            SetRegistryValue(appKey, "FriendlyAppName", "Comic Downloader GMTPC");
            SetRegistryValue($@"{appKey}\DefaultIcon", null, iconStr);
            SetRegistryValue($@"{appKey}\shell\open\command", null, commandStr);
            foreach (var ext in DefaultImageBookExtensions.Concat(ArchiveContextMenuExtensions))
            {
                SetRegistryValue($@"{appKey}\SupportedTypes", ext, string.Empty);
            }

            // 3. Đăng ký mở mặc định cho các định dạng: .png, .bmp, .jpg, .jpeg, .webp, .pdf, .mobi
            foreach (var ext in DefaultImageBookExtensions)
            {
                SetRegistryValue($@"Software\Classes\{ext}", null, ProgId);
                SetRegistryValue($@"Software\Classes\{ext}\OpenWithProgids", ProgId, string.Empty);
                SetRegistryValue($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\OpenWithProgids", ProgId, string.Empty);
            }

            // 4. Phát tín hiệu SHChangeNotify để Windows Explorer cập nhật icon và liên kết ngay lập tức
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

            return (true, "✅ Đã thiết lập mở mặc định các tệp ảnh (.png, .jpg, .jpeg, .bmp, .webp, .gif) và sách (.pdf, .mobi) bằng app!");
        }
        catch (Exception ex)
        {
            return (false, $"❌ Lỗi thiết lập Windows Association: {ex.Message}");
        }
    }

    private (bool Success, string Message) UnregisterWindowsDefaultAssociations()
    {
        try
        {
            // 1. Xóa ProgID
            RegDeleteTree(HKEY_CURRENT_USER, @"Software\Classes\" + ProgId);

            // 2. Gỡ liên kết từng phần đuôi mở rộng
            foreach (var ext in DefaultImageBookExtensions)
            {
                string? current = GetRegistryValue($@"Software\Classes\{ext}", null);
                if (string.Equals(current, ProgId, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteRegistryValue($@"Software\Classes\{ext}", null);
                }

                DeleteRegistryValue($@"Software\Classes\{ext}\OpenWithProgids", ProgId);
                DeleteRegistryValue($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\OpenWithProgids", ProgId);
            }

            // 3. Làm mới Windows Explorer
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

            return (true, "✅ Đã gỡ bỏ mở mặc định các tệp ảnh và sách, trả về ứng dụng mặc định trước đó!");
        }
        catch (Exception ex)
        {
            return (false, $"❌ Lỗi gỡ bỏ Windows Association: {ex.Message}");
        }
    }

    private (bool Success, string Message) RegisterWindowsArchiveContextMenu()
    {
        try
        {
            string exePath = GetExecutablePath();
            string commandStr = $"\"{exePath}\" \"%1\"";
            string iconStr = $"\"{exePath}\",0";

            // 1. Đăng ký context menu cho .zip, .rar, .cbz, .cbr
            // Đăng ký vào cả SystemFileAssociations và Classes/{ext}/shell để tương thích 100% Win 7, 8, 10, 11
            foreach (var ext in ArchiveContextMenuExtensions)
            {
                // SystemFileAssociations (chuẩn Windows Explorer)
                string sysKey = $@"Software\Classes\SystemFileAssociations\{ext}\shell\{ContextMenuVerb}";
                SetRegistryValue(sysKey, null, ContextMenuTitle);
                SetRegistryValue(sysKey, "Icon", iconStr);
                SetRegistryValue($@"{sysKey}\command", null, commandStr);

                // Classes/{ext}/shell (fallback tương thích cổ điển)
                string directKey = $@"Software\Classes\{ext}\shell\{ContextMenuVerb}";
                SetRegistryValue(directKey, null, ContextMenuTitle);
                SetRegistryValue(directKey, "Icon", iconStr);
                SetRegistryValue($@"{directKey}\command", null, commandStr);
            }

            // 2. Đăng ký context menu chuột phải cho Thư mục (Folder) và Nền thư mục (Directory Background)
            string dirKey = $@"Software\Classes\Directory\shell\{ContextMenuVerb}";
            SetRegistryValue(dirKey, null, ContextMenuTitle);
            SetRegistryValue(dirKey, "Icon", iconStr);
            SetRegistryValue($@"{dirKey}\command", null, commandStr);

            string dirBgKey = $@"Software\Classes\Directory\Background\shell\{ContextMenuVerb}";
            string commandBgStr = $"\"{exePath}\" \"%V\"";
            SetRegistryValue(dirBgKey, null, ContextMenuTitle);
            SetRegistryValue(dirBgKey, "Icon", iconStr);
            SetRegistryValue($@"{dirBgKey}\command", null, commandBgStr);

            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

            return (true, "✅ Đã thêm menu chuột phải 'Đọc truyện bằng Comic Downloader GMTPC' cho Thư mục và tệp nén (.zip, .rar, .cbz, .cbr)!");
        }
        catch (Exception ex)
        {
            return (false, $"❌ Lỗi đăng ký Windows Context Menu: {ex.Message}");
        }
    }

    private (bool Success, string Message) UnregisterWindowsArchiveContextMenu()
    {
        try
        {
            foreach (var ext in ArchiveContextMenuExtensions)
            {
                RegDeleteTree(HKEY_CURRENT_USER, $@"Software\Classes\SystemFileAssociations\{ext}\shell\{ContextMenuVerb}");
                RegDeleteTree(HKEY_CURRENT_USER, $@"Software\Classes\{ext}\shell\{ContextMenuVerb}");
            }

            RegDeleteTree(HKEY_CURRENT_USER, $@"Software\Classes\Directory\shell\{ContextMenuVerb}");
            RegDeleteTree(HKEY_CURRENT_USER, $@"Software\Classes\Directory\Background\shell\{ContextMenuVerb}");

            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

            return (true, "✅ Đã gỡ bỏ menu chuột phải cho Thư mục và tệp nén (.zip, .rar, .cbz, .cbr)!");
        }
        catch (Exception ex)
        {
            return (false, $"❌ Lỗi gỡ bỏ Windows Context Menu: {ex.Message}");
        }
    }


    private bool CheckWindowsDefaultRegistered()
    {
        try
        {
            string? prog = GetRegistryValue(@"Software\Classes\.png", null);
            if (string.Equals(prog, ProgId, StringComparison.OrdinalIgnoreCase))
                return true;

            string? jpgProg = GetRegistryValue(@"Software\Classes\.jpg", null);
            return string.Equals(jpgProg, ProgId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool CheckWindowsArchiveContextMenuRegistered()
    {
        try
        {
            string dirKey = $@"Software\Classes\Directory\shell\{ContextMenuVerb}";
            string? dirTitle = GetRegistryValue(dirKey, null);
            if (!string.IsNullOrEmpty(dirTitle))
                return true;

            string sysKey = $@"Software\Classes\SystemFileAssociations\.zip\shell\{ContextMenuVerb}";
            string? title = GetRegistryValue(sysKey, null);
            if (!string.IsNullOrEmpty(title))
                return true;

            string directKey = $@"Software\Classes\.zip\shell\{ContextMenuVerb}";
            string? directTitle = GetRegistryValue(directKey, null);
            return !string.IsNullOrEmpty(directTitle);

        }
        catch
        {
            return false;
        }
    }

    private static bool SetRegistryValue(string subKey, string? valueName, string data)
    {
        int res = RegCreateKeyEx(
            HKEY_CURRENT_USER,
            subKey,
            0,
            null,
            0,
            KEY_ALL_ACCESS,
            IntPtr.Zero,
            out UIntPtr hKey,
            out _);

        if (res != 0) return false;

        try
        {
            int cbData = (data.Length + 1) * 2;
            int setRes = RegSetValueEx(hKey, valueName, 0, REG_SZ, data, cbData);
            return setRes == 0;
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    private static string? GetRegistryValue(string subKey, string? valueName)
    {
        int res = RegOpenKeyEx(HKEY_CURRENT_USER, subKey, 0, KEY_READ, out UIntPtr hKey);
        if (res != 0) return null;

        try
        {
            int bufferSize = 1024;
            var sb = new StringBuilder(bufferSize);
            int queryRes = RegQueryValueEx(hKey, valueName, 0, out int type, sb, ref bufferSize);
            return queryRes == 0 ? sb.ToString() : null;
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    private static bool DeleteRegistryValue(string subKey, string? valueName)
    {
        int res = RegOpenKeyEx(HKEY_CURRENT_USER, subKey, 0, KEY_ALL_ACCESS, out UIntPtr hKey);
        if (res != 0) return false;

        try
        {
            int delRes = RegDeleteValue(hKey, valueName);
            return delRes == 0;
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    #endregion

    #region Linux Implementation (XDG Desktop Entry & ~/.config/mimeapps.list)

    private static readonly Dictionary<string, string> LinuxImageBookMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "image/png", ".png" },
        { "image/jpeg", ".jpg, .jpeg" },
        { "image/bmp", ".bmp" },
        { "image/webp", ".webp" },
        { "image/gif", ".gif" },
        { "application/pdf", ".pdf" },
        { "application/x-mobipocket-ebook", ".mobi" }
    };

    private static readonly Dictionary<string, string> LinuxArchiveMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "application/zip", ".zip" },
        { "application/vnd.rar", ".rar" },
        { "application/x-cbr", ".cbr" },
        { "application/x-cbz", ".cbz" }
    };

    private string GetLinuxDesktopFilePath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "applications", LinuxAppDesktopName);
    }

    private string GetLinuxMimeAppsListPath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "mimeapps.list");
    }

    private bool EnsureLinuxDesktopFileExists()
    {
        try
        {
            string desktopPath = GetLinuxDesktopFilePath();
            string dir = Path.GetDirectoryName(desktopPath)!;
            Directory.CreateDirectory(dir);

            string exePath = GetExecutablePath();
            string content =
$@"[Desktop Entry]
Type=Application
Name=Comic Downloader GMTPC
Comment=Comic Downloader & Reader
Exec=""{exePath}"" %f
Icon=comicdownloadergmtpc
Terminal=false
Categories=Graphics;Viewer;Office;
MimeType=image/png;image/jpeg;image/bmp;image/webp;image/gif;application/pdf;application/x-mobipocket-ebook;application/zip;application/vnd.rar;application/x-cbr;application/x-cbz;
";
            File.WriteAllText(desktopPath, content, new UTF8Encoding(false));

            // Copy icon nếu có
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string iconTargetDir = Path.Combine(home, ".local", "share", "icons", "hicolor", "256x256", "apps");
                Directory.CreateDirectory(iconTargetDir);
                string iconTarget = Path.Combine(iconTargetDir, "comicdownloadergmtpc.png");

                string candidateIcon = Path.Combine(AppContext.BaseDirectory, "Icon.png");
                if (File.Exists(candidateIcon) && !File.Exists(iconTarget))
                {
                    File.Copy(candidateIcon, iconTarget, true);
                }
            }
            catch { }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private (bool Success, string Message) RegisterLinuxDefaultAssociations()
    {
        try
        {
            EnsureLinuxDesktopFileExists();
            string mimePath = GetLinuxMimeAppsListPath();
            Directory.CreateDirectory(Path.GetDirectoryName(mimePath)!);

            var lines = File.Exists(mimePath)
                ? File.ReadAllLines(mimePath).ToList()
                : new List<string>();

            // Ghi vào section [Default Applications]
            EnsureLinuxMimeSection(lines, "Default Applications", LinuxImageBookMimeTypes.Keys, LinuxAppDesktopName);

            File.WriteAllLines(mimePath, lines, new UTF8Encoding(false));
            UpdateLinuxDesktopDatabase();

            return (true, "✅ [Linux] Đã đặt Comic Downloader GMTPC làm trình mở mặc định cho ảnh và sách truyện!");
        }
        catch (Exception ex)
        {
            return (false, $"❌ [Linux] Lỗi thiết lập MIME mặc định: {ex.Message}");
        }
    }

    private (bool Success, string Message) UnregisterLinuxDefaultAssociations()
    {
        try
        {
            string mimePath = GetLinuxMimeAppsListPath();
            if (!File.Exists(mimePath))
                return (true, "✅ [Linux] Chưa có liên kết nào để gỡ bỏ.");

            var lines = File.ReadAllLines(mimePath).ToList();
            RemoveLinuxMimeAssociations(lines, "Default Applications", LinuxImageBookMimeTypes.Keys, LinuxAppDesktopName);

            File.WriteAllLines(mimePath, lines, new UTF8Encoding(false));
            UpdateLinuxDesktopDatabase();

            return (true, "✅ [Linux] Đã gỡ bỏ mở mặc định cho ảnh và sách truyện!");
        }
        catch (Exception ex)
        {
            return (false, $"❌ [Linux] Lỗi gỡ bỏ MIME mặc định: {ex.Message}");
        }
    }

    private (bool Success, string Message) RegisterLinuxArchiveContextMenu()
    {
        try
        {
            EnsureLinuxDesktopFileExists();
            string mimePath = GetLinuxMimeAppsListPath();
            Directory.CreateDirectory(Path.GetDirectoryName(mimePath)!);

            var lines = File.Exists(mimePath)
                ? File.ReadAllLines(mimePath).ToList()
                : new List<string>();

            // Ghi vào section [Added Associations] (để xuất hiện trong danh sách "Open With", KHÔNG làm default)
            EnsureLinuxMimeSection(lines, "Added Associations", LinuxArchiveMimeTypes.Keys, LinuxAppDesktopName, isAppendedList: true);

            File.WriteAllLines(mimePath, lines, new UTF8Encoding(false));
            UpdateLinuxDesktopDatabase();

            return (true, "✅ [Linux] Đã thêm Comic Downloader GMTPC vào menu ngữ cảnh (Open With) cho .zip, .rar, .cbz, .cbr!");
        }
        catch (Exception ex)
        {
            return (false, $"❌ [Linux] Lỗi thêm menu ngữ cảnh: {ex.Message}");
        }
    }

    private (bool Success, string Message) UnregisterLinuxArchiveContextMenu()
    {
        try
        {
            string mimePath = GetLinuxMimeAppsListPath();
            if (!File.Exists(mimePath))
                return (true, "✅ [Linux] Chưa có menu ngữ cảnh nào để gỡ bỏ.");

            var lines = File.ReadAllLines(mimePath).ToList();
            RemoveLinuxMimeAssociations(lines, "Added Associations", LinuxArchiveMimeTypes.Keys, LinuxAppDesktopName);

            File.WriteAllLines(mimePath, lines, new UTF8Encoding(false));
            UpdateLinuxDesktopDatabase();

            return (true, "✅ [Linux] Đã gỡ bỏ menu ngữ cảnh cho các tệp nén!");
        }
        catch (Exception ex)
        {
            return (false, $"❌ [Linux] Lỗi gỡ bỏ menu ngữ cảnh: {ex.Message}");
        }
    }

    private bool CheckLinuxDefaultRegistered()
    {
        try
        {
            string mimePath = GetLinuxMimeAppsListPath();
            if (!File.Exists(mimePath)) return false;

            var lines = File.ReadAllLines(mimePath);
            bool inDefaultSection = false;
            foreach (var line in lines)
            {
                string trim = line.Trim();
                if (trim.StartsWith("[") && trim.EndsWith("]"))
                {
                    inDefaultSection = string.Equals(trim, "[Default Applications]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (inDefaultSection && trim.StartsWith("image/png=", StringComparison.OrdinalIgnoreCase))
                {
                    return trim.Contains(LinuxAppDesktopName, StringComparison.OrdinalIgnoreCase);
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private bool CheckLinuxArchiveContextMenuRegistered()
    {
        try
        {
            string mimePath = GetLinuxMimeAppsListPath();
            if (!File.Exists(mimePath)) return false;

            var lines = File.ReadAllLines(mimePath);
            bool inAddedSection = false;
            foreach (var line in lines)
            {
                string trim = line.Trim();
                if (trim.StartsWith("[") && trim.EndsWith("]"))
                {
                    inAddedSection = string.Equals(trim, "[Added Associations]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (inAddedSection && trim.StartsWith("application/zip=", StringComparison.OrdinalIgnoreCase))
                {
                    return trim.Contains(LinuxAppDesktopName, StringComparison.OrdinalIgnoreCase);
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureLinuxMimeSection(
        List<string> lines,
        string sectionName,
        IEnumerable<string> mimeTypes,
        string desktopFileName,
        bool isAppendedList = false)
    {
        string header = $"[{sectionName}]";
        int sectionIndex = lines.FindIndex(l => string.Equals(l.Trim(), header, StringComparison.OrdinalIgnoreCase));
        if (sectionIndex < 0)
        {
            lines.Add(string.Empty);
            lines.Add(header);
            sectionIndex = lines.Count - 1;
        }

        // Tìm điểm kết thúc của section
        int nextSectionIndex = lines.FindIndex(sectionIndex + 1, l => l.Trim().StartsWith("[") && l.Trim().EndsWith("]"));
        int insertPos = nextSectionIndex >= 0 ? nextSectionIndex : lines.Count;

        foreach (var mime in mimeTypes)
        {
            string prefix = mime + "=";
            int existingLineIdx = -1;
            for (int i = sectionIndex + 1; i < insertPos; i++)
            {
                if (lines[i].Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    existingLineIdx = i;
                    break;
                }
            }

            if (existingLineIdx >= 0)
            {
                if (isAppendedList)
                {
                    string existingVal = lines[existingLineIdx].Substring(lines[existingLineIdx].IndexOf('=') + 1).Trim();
                    var apps = existingVal.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()).ToList();
                    if (!apps.Contains(desktopFileName, StringComparer.OrdinalIgnoreCase))
                    {
                        apps.Add(desktopFileName);
                        lines[existingLineIdx] = $"{prefix}{string.Join(";", apps)};";
                    }
                }
                else
                {
                    lines[existingLineIdx] = $"{prefix}{desktopFileName}";
                }
            }
            else
            {
                string newLine = isAppendedList
                    ? $"{prefix}{desktopFileName};"
                    : $"{prefix}{desktopFileName}";
                lines.Insert(insertPos, newLine);
                insertPos++;
            }
        }
    }

    private static void RemoveLinuxMimeAssociations(
        List<string> lines,
        string sectionName,
        IEnumerable<string> mimeTypes,
        string desktopFileName)
    {
        string header = $"[{sectionName}]";
        int sectionIndex = lines.FindIndex(l => string.Equals(l.Trim(), header, StringComparison.OrdinalIgnoreCase));
        if (sectionIndex < 0) return;

        int nextSectionIndex = lines.FindIndex(sectionIndex + 1, l => l.Trim().StartsWith("[") && l.Trim().EndsWith("]"));
        int endPos = nextSectionIndex >= 0 ? nextSectionIndex : lines.Count;

        var mimeSet = new HashSet<string>(mimeTypes, StringComparer.OrdinalIgnoreCase);

        for (int i = endPos - 1; i > sectionIndex; i--)
        {
            string line = lines[i].Trim();
            int eqIdx = line.IndexOf('=');
            if (eqIdx <= 0) continue;

            string mime = line.Substring(0, eqIdx).Trim();
            if (!mimeSet.Contains(mime)) continue;

            string val = line.Substring(eqIdx + 1).Trim();
            var apps = val.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()).ToList();
            apps.RemoveAll(a => string.Equals(a, desktopFileName, StringComparison.OrdinalIgnoreCase));

            if (apps.Count == 0)
            {
                lines.RemoveAt(i);
            }
            else
            {
                lines[i] = $"{mime}={string.Join(";", apps)};";
            }
        }
    }

    private static void UpdateLinuxDesktopDatabase()
    {
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string appDir = Path.Combine(home, ".local", "share", "applications");
            var psi = new ProcessStartInfo
            {
                FileName = "update-desktop-database",
                Arguments = $"\"{appDir}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi);
        }
        catch { }
    }

    #endregion
}

