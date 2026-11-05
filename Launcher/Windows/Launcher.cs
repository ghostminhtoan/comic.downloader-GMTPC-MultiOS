using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ComicDownloaderGMTPC.Launcher
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const string RuntimeDownloadUrl = "https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/beta/runtime-net10-win-x64.zip";

        [STAThread]
        private static int Main(string[] args)
        {
            string appRoot = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                string loc = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    string dir = Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(dir)) appRoot = dir;
                }
            }
            catch { }

            string netAvaloniaDir = Path.Combine(appRoot, ".portable", "bin", "net avalonia");
            string targetExe = Path.Combine(netAvaloniaDir, "ComicDownloaderGMTPC.Desktop.exe");
            if (!File.Exists(targetExe))
            {
                string altExe = Path.Combine(netAvaloniaDir, "ComicDownloaderGMTPC.Core.exe");
                if (File.Exists(altExe)) targetExe = altExe;
            }

            // Neu da co san thu vien thi khoi chay ngay lap tuc (khong mo console)
            if (File.Exists(targetExe))
            {
                return LaunchApplication(targetExe, appRoot, netAvaloniaDir, args);
            }

            // Neu chua co, bat Console truc quan de thong bao va tu dong tai
            AllocConsole();
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.Title = "Comic Downloader GMTPC - Khởi tạo Thư viện Portable";

                Console.WriteLine("==============================================================");
                Console.WriteLine("  Comic Downloader GMTPC - Khởi tạo Thư viện .NET Avalonia 10");
                Console.WriteLine("==============================================================");
                Console.WriteLine("[*] Phát hiện máy chưa có thư viện tại:");
                Console.WriteLine("    .portable\\bin\\net avalonia\\");
                Console.WriteLine("[*] Đang tự động tải bộ thư viện Portable từ GitHub Release...");
                Console.WriteLine("    " + RuntimeDownloadUrl);
                Console.WriteLine("--------------------------------------------------------------");

                string tempDir = Path.Combine(appRoot, ".tmp");
                Directory.CreateDirectory(tempDir);
                string tempZip = Path.Combine(tempDir, "runtime-net10-win-x64.zip");

                // Dam bao TLS 1.2 / TLS 1.3
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;

                bool downloadSuccess = DownloadWithProgress(RuntimeDownloadUrl, tempZip);
                if (!downloadSuccess || !File.Exists(tempZip) || new FileInfo(tempZip).Length < 1024 * 1024)
                {
                    Console.WriteLine("\n[ERROR] Tải thư viện không thành công hoặc tệp bị hỏng!");
                    Console.WriteLine("[!] Vui lòng kiểm tra lại kết nối mạng và thử lại.");
                    Console.WriteLine("Nhấn phím bất kỳ để thoát...");
                    Console.ReadKey(true);
                    return 1;
                }

                Console.WriteLine("\n[*] Đang giải nén bộ thư viện vào: .portable\\bin\\net avalonia\\...");
                Directory.CreateDirectory(netAvaloniaDir);

                ExtractZipWithOverwrite(tempZip, netAvaloniaDir);

                try { File.Delete(tempZip); } catch { }

                Console.WriteLine("[*] Khởi tạo thư viện hoàn tất 100%!");
                Console.WriteLine("[*] Đang khởi động ứng dụng...");
                Thread.Sleep(800);
            }
            catch (Exception ex)
            {
                Console.WriteLine("\n[ERROR] Lỗi khởi tạo: " + ex.Message);
                Console.WriteLine("Nhấn phím bất kỳ để thoát...");
                Console.ReadKey(true);
                return 1;
            }
            finally
            {
                FreeConsole();
            }

            if (File.Exists(targetExe))
            {
                return LaunchApplication(targetExe, appRoot, netAvaloniaDir, args);
            }
            else
            {
                string altExe = Path.Combine(netAvaloniaDir, "ComicDownloaderGMTPC.Core.exe");
                if (File.Exists(altExe))
                {
                    return LaunchApplication(altExe, appRoot, netAvaloniaDir, args);
                }
            }

            return 1;
        }

        private static bool DownloadWithProgress(string url, string destPath)
        {
            try
            {
                if (File.Exists(destPath)) File.Delete(destPath);

                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "ComicDownloaderGMTPC-Launcher/1.0");

                    int lastPercent = -1;
                    client.DownloadProgressChanged += (s, e) =>
                    {
                        if (e.ProgressPercentage != lastPercent)
                        {
                            lastPercent = e.ProgressPercentage;
                            RenderProgressBar(e.BytesReceived, e.TotalBytesToReceive, e.ProgressPercentage);
                        }
                    };

                    var sync = new object();
                    bool done = false;
                    Exception downloadEx = null;

                    client.DownloadFileCompleted += (s, e) =>
                    {
                        lock (sync)
                        {
                            done = true;
                            downloadEx = e.Error;
                            Monitor.Pulse(sync);
                        }
                    };

                    lock (sync)
                    {
                        client.DownloadFileAsync(new Uri(url), destPath);
                        while (!done)
                        {
                            Monitor.Wait(sync);
                        }
                    }

                    if (downloadEx != null)
                    {
                        Console.WriteLine("\n[ERROR] " + downloadEx.Message);
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("\n[ERROR] " + ex.Message);
                return false;
            }
        }

        private static void RenderProgressBar(long bytesReceived, long totalBytesToReceive, int percentage)
        {
            int barWidth = 26;
            int filled = (int)((double)percentage / 100.0 * barWidth);
            if (filled > barWidth) filled = barWidth;
            string bar = new string('=', Math.Max(0, filled - 1)) + (filled > 0 ? ">" : "") + new string(' ', Math.Max(0, barWidth - filled));
            double mbRec = bytesReceived / (1024.0 * 1024.0);
            double mbTot = totalBytesToReceive / (1024.0 * 1024.0);
            Console.Write("\r[*] Tiến độ: [{0}] {1,3}% ({2:F1} MB / {3:F1} MB)   ", bar, percentage, mbRec, mbTot);
        }

        private static void ExtractZipWithOverwrite(string zipPath, string extractDir)
        {
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string fullPath = Path.Combine(extractDir, entry.FullName);
                    string dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        continue;
                    }
                    entry.ExtractToFile(fullPath, true);
                }
            }
        }

        private static int LaunchApplication(string exePath, string appRoot, string netAvaloniaDir, string[] args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exePath;
                psi.WorkingDirectory = appRoot;
                psi.Arguments = FormatArguments(args);
                psi.UseShellExecute = false;

                psi.EnvironmentVariables["COMIC_DOWNLOADER_ROOT"] = appRoot;
                psi.EnvironmentVariables["DOTNET_ROOT"] = netAvaloniaDir;

                Process proc = Process.Start(psi);
                if (proc == null) return 1;
                return 0;
            }
            catch (Exception ex)
            {
                AllocConsole();
                Console.WriteLine("[ERROR] Khong the khoi dong tien trinh ung dung: " + ex.Message);
                Console.WriteLine("Nhan phim bat ky de thoat...");
                Console.ReadKey(true);
                FreeConsole();
                return 1;
            }
        }

        private static string FormatArguments(string[] args)
        {
            if (args == null || args.Length == 0) return string.Empty;
            StringBuilder sb = new StringBuilder();
            foreach (string arg in args)
            {
                if (sb.Length > 0) sb.Append(" ");
                if (arg.Contains(" ") || arg.Contains("\""))
                {
                    sb.Append("\"").Append(arg.Replace("\"", "\\\"")).Append("\"");
                }
                else
                {
                    sb.Append(arg);
                }
            }
            return sb.ToString();
        }
    }
}

