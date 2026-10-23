using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Dịch vụ chọn thư mục hỗ trợ hiển thị danh sách tập tin ảnh trực quan trên cả Windows và Linux.
/// Người dùng vừa có thể nhìn thấy toàn bộ file ảnh bên trong, vừa có thể chọn thư mục hiện tại
/// hoặc click chọn một file ảnh bất kỳ thì ứng dụng đều tự động lấy thư mục đang chọn.
/// </summary>
public static class FolderOrFilePickerService
{
    private static readonly string[] ValidImageExtensions = new[]
    {
        "*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp", "*.gif", "*.avif", "*.jxl"
    };

    public static async Task<string?> PickFolderWithFileViewAsync(TopLevel? topLevel, string? initialPath, string language = "VI")
    {
        // 1. Trên Windows: Sử dụng COM IFileOpenDialog tối ưu hiển thị danh sách file ảnh kèm nút 'Select Folder'
        if (OperatingSystem.IsWindows())
        {
            try
            {
                string? winResult = await PickOnWindowsAsync(initialPath, language);
                if (!string.IsNullOrWhiteSpace(winResult))
                {
                    return winResult;
                }
            }
            catch
            {
                // Fallback nếu COM gặp sự cố
            }
        }

        // 2. Trên Linux: Sử dụng zenity hoặc kdialog nếu có để vừa hiện file vừa chọn folder
        if (OperatingSystem.IsLinux())
        {
            try
            {
                string? linuxResult = await PickOnLinuxAsync(initialPath, language);
                if (!string.IsNullOrWhiteSpace(linuxResult))
                {
                    return linuxResult;
                }
            }
            catch
            {
                // Fallback nếu không có zenity/kdialog
            }
        }

        // 3. Fallback tiêu chuẩn / Android: Sử dụng Avalonia OpenFilePickerAsync hiển thị danh sách file ảnh
        if (topLevel?.StorageProvider != null)
        {
            try
            {
                IStorageFolder? startFolder = null;
                if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
                {
                    try
                    {
                        startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(initialPath);
                    }
                    catch { }
                }

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = language == "VI"
                        ? "Chọn thư mục ảnh nguồn (Chọn thư mục hoặc click ảnh bất kỳ để lấy thư mục)"
                        : "Select Source Picture Folder (Pick folder or click any image to select)",
                    AllowMultiple = false,
                    SuggestedStartLocation = startFolder,
                    FileTypeFilter = new List<FilePickerFileType>
                    {
                        new(language == "VI" ? "Tất cả định dạng ảnh (*.jpg, *.png, *.webp, *.bmp, *.gif, *.avif, *.jxl)" : "All Image Formats (*.jpg, *.png, *.webp, *.bmp, *.gif, *.avif, *.jxl)")
                        {
                            Patterns = ValidImageExtensions
                        },
                        new(language == "VI" ? "Tất cả tệp (*.*)" : "All Files (*.*)")
                        {
                            Patterns = new[] { "*" }
                        }
                    }
                });

                if (files != null && files.Count > 0)
                {
                    var selected = files[0];
                    string? raw = selected.TryGetLocalPath() ?? selected.Path?.LocalPath ?? selected.Path?.ToString();
                    string normalized = DownloadEngineService.NormalizeStoragePath(raw, selected.Name);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        if (File.Exists(normalized))
                        {
                            return Path.GetDirectoryName(normalized) ?? normalized;
                        }
                        if (Directory.Exists(normalized))
                        {
                            return normalized;
                        }
                        return Path.GetDirectoryName(normalized) ?? normalized;
                    }
                }
            }
            catch { }
        }

        return null;
    }

    #region Windows Win32 Shell COM Implementation

    [SupportedOSPlatform("windows")]
    private static Task<string?> PickOnWindowsAsync(string? initialPath, string language)
    {
        var tcs = new TaskCompletionSource<string?>();
        var thread = new Thread(() =>
        {
            try
            {
                string? res = ShowWindowsOpenFileDialog(initialPath, language);
                tcs.TrySetResult(res);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task;
    }

    [SupportedOSPlatform("windows")]
    private static string? ShowWindowsOpenFileDialog(string? initialPath, string language)
    {
        IFileOpenDialog? dialog = null;
        try
        {
            dialog = (IFileOpenDialog)new FileOpenDialogRCW();

            string title = language == "VI"
                ? "Chọn thư mục ảnh gốc cần lọc (Chọn thư mục hoặc click ảnh bất kỳ)"
                : "Select Source Picture Folder (Pick folder or click any image)";

            string okLabel = language == "VI" ? "Chọn Thư Mục" : "Select Folder";
            string placeholderName = language == "VI" ? "[Chọn thư mục này]" : "[Select this folder]";

            dialog.SetTitle(title);
            dialog.SetOkButtonLabel(okLabel);
            dialog.SetFileName(placeholderName);

            // Cấu hình Options:
            // FOS_NOVALIDATE (0x00000100): Cho phép chấp nhận tên placeholder mà không báo lỗi file not found
            // FOS_DONTADDTORECENT (0x02000000): Không thêm vào danh sách Recent của Windows
            // FOS_PATHMUSTEXIST (0x00000800): Thư mục phải tồn tại
            // KHÔNG bật FOS_FILEMUSTEXIST (0x00001000)
            // KHÔNG bật FOS_PICKFOLDERS (0x00000020) -> Để Windows hiển thị toàn bộ file ảnh trực quan
            uint options = 0x00000100 | 0x02000000 | 0x00000800;
            dialog.SetOptions(options);

            // Cấu hình Filter ảnh
            var filterSpec = new COMDLG_FILTERSPEC[]
            {
                new()
                {
                    pszName = language == "VI" ? "Tất cả ảnh (*.jpg, *.png, *.webp, *.bmp, *.gif, *.avif, *.jxl)" : "All Image Files (*.jpg, *.png, *.webp, *.bmp, *.gif, *.avif, *.jxl)",
                    pszSpec = "*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif;*.avif;*.jxl"
                },
                new()
                {
                    pszName = language == "VI" ? "Tất cả tệp (*.*)" : "All Files (*.*)",
                    pszSpec = "*.*"
                }
            };
            dialog.SetFileTypes((uint)filterSpec.Length, filterSpec);
            dialog.SetFileTypeIndex(1);

            // Thiết lập thư mục khởi đầu nếu có
            if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
            {
                try
                {
                    var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                    if (SHCreateItemFromParsingName(initialPath, IntPtr.Zero, iid, out IShellItem initItem) == 0 && initItem != null)
                    {
                        dialog.SetFolder(initItem);
                        Marshal.ReleaseComObject(initItem);
                    }
                }
                catch { }
            }

            uint hr = dialog.Show(IntPtr.Zero);
            if (hr != 0) // Người dùng bấm Cancel hoặc đóng cửa sổ
            {
                return null;
            }

            string? selectedPath = null;

            // 1. Lấy kết quả từ GetResult
            IShellItem? resultItem = null;
            try
            {
                if (dialog.GetResult(out resultItem) == 0 && resultItem != null)
                {
                    if (resultItem.GetDisplayName(0x80058000 /* SIGDN_FILESYSPATH */, out string pszPath) == 0)
                    {
                        selectedPath = pszPath;
                    }
                }
            }
            catch { }
            finally
            {
                if (resultItem != null) Marshal.ReleaseComObject(resultItem);
            }

            // 2. Lấy kết quả từ GetFolder (thư mục đang mở trong dialog)
            string? currentFolderPath = null;
            IShellItem? folderItem = null;
            try
            {
                if (dialog.GetFolder(out folderItem) == 0 && folderItem != null)
                {
                    if (folderItem.GetDisplayName(0x80058000 /* SIGDN_FILESYSPATH */, out string pszFolder) == 0)
                    {
                        currentFolderPath = pszFolder;
                    }
                }
            }
            catch { }
            finally
            {
                if (folderItem != null) Marshal.ReleaseComObject(folderItem);
            }

            // 3. Phân tích kết quả
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                if (Directory.Exists(selectedPath))
                {
                    return selectedPath;
                }
                if (File.Exists(selectedPath))
                {
                    return Path.GetDirectoryName(selectedPath) ?? selectedPath;
                }

                // Nếu là dummy placeholder hoặc tên chưa tồn tại trên đĩa, lấy thư mục cha của nó
                string? parentDir = Path.GetDirectoryName(selectedPath);
                if (!string.IsNullOrWhiteSpace(parentDir) && Directory.Exists(parentDir))
                {
                    return parentDir;
                }
            }

            if (!string.IsNullOrWhiteSpace(currentFolderPath) && Directory.Exists(currentFolderPath))
            {
                return currentFolderPath;
            }

            return null;
        }
        finally
        {
            if (dialog != null)
            {
                Marshal.ReleaseComObject(dialog);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
    }

    [SupportedOSPlatform("windows")]
    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] uint BindToHandler([In] IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
        [PreserveSig] uint GetParent(out IShellItem ppsi);
        [PreserveSig] uint GetDisplayName([In] uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        [PreserveSig] uint GetAttributes([In] uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] uint Compare([In] IShellItem psi, [In] uint hint, out int piOrder);
    }

    [SupportedOSPlatform("windows")]
    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRCW { }

    [SupportedOSPlatform("windows")]
    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] uint Show([In] IntPtr hwndOwner);
        [PreserveSig] uint SetFileTypes([In] uint cFileTypes, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] COMDLG_FILTERSPEC[] rgFilter);
        [PreserveSig] uint SetFileTypeIndex([In] uint iFileType);
        [PreserveSig] uint GetFileTypeIndex(out uint piFileType);
        [PreserveSig] uint Advise([In] IntPtr pfde, out uint pdwCookie);
        [PreserveSig] uint Unadvise([In] uint dwCookie);
        [PreserveSig] uint SetOptions([In] uint fos);
        [PreserveSig] uint GetOptions(out uint pfos);
        [PreserveSig] uint SetDefaultFolder([In] IShellItem psi);
        [PreserveSig] uint SetFolder([In] IShellItem psi);
        [PreserveSig] uint GetFolder(out IShellItem ppsi);
        [PreserveSig] uint GetCurrentSelection(out IShellItem ppsi);
        [PreserveSig] uint SetFileName([In, MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] uint GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        [PreserveSig] uint SetTitle([In, MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        [PreserveSig] uint SetOkButtonLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszText);
        [PreserveSig] uint SetFileNameLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        [PreserveSig] uint GetResult(out IShellItem ppsi);
        [PreserveSig] uint AddPlace([In] IShellItem psi, [In] int fdap);
        [PreserveSig] uint SetDefaultExtension([In, MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        [PreserveSig] uint Close([In] int hr);
        [PreserveSig] uint SetClientGuid([In] ref Guid guid);
        [PreserveSig] uint ClearClientData();
        [PreserveSig] uint SetFilter([In] IntPtr pFilter);
        [PreserveSig] uint GetResults(out IntPtr ppenum);
        [PreserveSig] uint GetSelectedItems(out IntPtr ppsai);
    }

    [SupportedOSPlatform("windows")]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IShellItem ppv);

    #endregion

    #region Linux Zenity / Kdialog Implementation

    [SupportedOSPlatform("linux")]
    private static async Task<string?> PickOnLinuxAsync(string? initialPath, string language)
    {
        string title = language == "VI"
            ? "Chọn thư mục ảnh gốc cần lọc (Chọn thư mục hoặc click ảnh bất kỳ)"
            : "Select Source Picture Folder (Pick folder or click any image)";

        // 1. Thử zenity
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "zenity",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("--file-selection");
            psi.ArgumentList.Add($"--title={title}");
            psi.ArgumentList.Add("--file-filter=Image Files | *.jpg *.jpeg *.png *.webp *.bmp *.gif *.avif *.jxl");
            psi.ArgumentList.Add("--file-filter=All Files | *");

            if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
            {
                string norm = initialPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "/";
                psi.ArgumentList.Add($"--filename={norm}");
            }

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    string path = output.Trim();
                    if (File.Exists(path))
                    {
                        return Path.GetDirectoryName(path) ?? path;
                    }
                    if (Directory.Exists(path))
                    {
                        return path;
                    }
                }
            }
        }
        catch { }

        // 2. Thử kdialog
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "kdialog",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("--getopenfilename");
            psi.ArgumentList.Add(!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath) ? initialPath : "~");
            psi.ArgumentList.Add("*.jpg *.jpeg *.png *.webp *.bmp *.gif *.avif *.jxl | Image Files");
            psi.ArgumentList.Add($"--title={title}");

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    string path = output.Trim();
                    if (File.Exists(path))
                    {
                        return Path.GetDirectoryName(path) ?? path;
                    }
                    if (Directory.Exists(path))
                    {
                        return path;
                    }
                }
            }
        }
        catch { }

        return null;
    }

    #endregion
}
