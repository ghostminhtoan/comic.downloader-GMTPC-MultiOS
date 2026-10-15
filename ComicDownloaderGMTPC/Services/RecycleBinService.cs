using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Dịch vụ xóa tệp và thư mục an toàn vào Thùng rác (Recycle Bin trên Windows, Trash trên Linux).
/// </summary>
public static class RecycleBinService
{
    // ==========================================
    // WIN32 SHELL API CHO WINDOWS
    // ==========================================
    private const int FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;      // Chuyển vào Recycle Bin
    private const ushort FOF_NOCONFIRMATION = 0x0010; // Không hiện hộp thoại hỏi xác nhận
    private const ushort FOF_SILENT = 0x0004;         // Không hiện progress dialog
    private const ushort FOF_NOERRORUI = 0x0400;       // Không hiện dialog lỗi

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>
    /// Xóa tệp hoặc thư mục chuyển vào Thùng rác (Recycle Bin / Trash).
    /// </summary>
    public static bool DeleteToRecycleBin(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return DeleteWindowsRecycleBin(path);
            }
            else if (OperatingSystem.IsLinux())
            {
                return DeleteLinuxTrash(path);
            }
            else
            {
                // Fallback trên Android hoặc OS khác
                return PermanentDelete(path);
            }
        }
        catch
        {
            return PermanentDelete(path);
        }
    }

    private static bool DeleteWindowsRecycleBin(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                return true;

            // SHFileOperation yêu cầu double-null-terminated string
            var fileOp = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = fullPath + '\0' + '\0',
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
            };

            int result = SHFileOperation(ref fileOp);
            if (result == 0 && !fileOp.fAnyOperationsAborted)
            {
                return true;
            }

            // Nếu Win32 API trả lỗi, fallback xóa thường
            return PermanentDelete(fullPath);
        }
        catch
        {
            return PermanentDelete(path);
        }
    }

    private static bool DeleteLinuxTrash(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                return true;

            // 1. Thử lệnh `gio trash` (chuẩn GNOME/GTK trên hầu hết Linux distro hiện đại)
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "gio",
                    Arguments = $"trash \"{fullPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(5000);
                    if (proc.ExitCode == 0) return true;
                }
            }
            catch { }

            // 2. Thử lệnh `trash-put` (gói trash-cli phổ biến trên Linux)
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "trash-put",
                    Arguments = $"\"{fullPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(5000);
                    if (proc.ExitCode == 0) return true;
                }
            }
            catch { }

            // 3. Fallback theo chuẩn FreeDesktop.org Trash Specification
            if (MoveToXdgTrash(fullPath))
            {
                return true;
            }

            return PermanentDelete(fullPath);
        }
        catch
        {
            return PermanentDelete(path);
        }
    }

    private static bool MoveToXdgTrash(string fullPath)
    {
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home)) return false;

            string trashDir = Path.Combine(home, ".local", "share", "Trash");
            string filesDir = Path.Combine(trashDir, "files");
            string infoDir = Path.Combine(trashDir, "info");

            Directory.CreateDirectory(filesDir);
            Directory.CreateDirectory(infoDir);

            string name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string uniqueName = name;
            int counter = 1;
            while (File.Exists(Path.Combine(filesDir, uniqueName)) || Directory.Exists(Path.Combine(filesDir, uniqueName)))
            {
                string stem = Path.GetFileNameWithoutExtension(name);
                string ext = Path.GetExtension(name);
                uniqueName = $"{stem}_{counter}{ext}";
                counter++;
            }

            string targetFile = Path.Combine(filesDir, uniqueName);
            string targetInfo = Path.Combine(infoDir, $"{uniqueName}.trashinfo");

            // Tạo file .trashinfo theo chuẩn XDG
            string infoContent = $"[Trash Info]\nPath={fullPath}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n";
            File.WriteAllText(targetInfo, infoContent);

            if (File.Exists(fullPath))
            {
                File.Move(fullPath, targetFile);
                return true;
            }
            else if (Directory.Exists(fullPath))
            {
                Directory.Move(fullPath, targetFile);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool PermanentDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
                return true;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}

