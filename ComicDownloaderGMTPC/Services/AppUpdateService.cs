using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ComicDownloaderGMTPC.Services;

public class AppUpdateService
{
    private static readonly Lazy<AppUpdateService> _instance = new(() => new AppUpdateService());
    public static AppUpdateService Instance => _instance.Value;

    public static event Action<string>? InstallApkRequested;

    private readonly HttpClient _httpClient;

    public AppUpdateService()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
    }

    private static string GetWritableUpdateDirectory()
    {
        if (OperatingSystem.IsAndroid())
        {
            var candidates = new List<string>();
            try
            {
                string extApp = DownloadEngineService.GetAppSpecificExternalPath();
                if (!string.IsNullOrWhiteSpace(extApp)) candidates.Add(extApp);
            }
            catch { }

            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Download"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Download"));
            candidates.Add(Path.Combine(Path.GetTempPath(), "Download"));

            foreach (var dir in candidates)
            {
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string testFile = Path.Combine(dir, $".probe_{Guid.NewGuid():N}.tmp");
                    File.WriteAllBytes(testFile, new byte[] { 0x47, 0x4D });
                    if (File.Exists(testFile))
                    {
                        File.Delete(testFile);
                        return dir;
                    }
                }
                catch { }
            }
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                string winTemp = Path.Combine(Path.GetTempPath(), "ComicGMTPC_Update");
                if (!Directory.Exists(winTemp)) Directory.CreateDirectory(winTemp);
                return winTemp;
            }
            catch { }
        }

        string userDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(userDownloads)) userDownloads = AppContext.BaseDirectory;
        return userDownloads;
    }

    public async Task<bool> DownloadAndInstallUpdateAsync(
        string[] candidateUrls,
        Action<string, string> logCallback,
        Action<double>? progressCallback = null,
        CancellationToken ct = default)
    {
        try
        {
            logCallback("INFO", "[Cập nhật tự động] Đang kết nối máy chủ cập nhật GitHub...");

            HttpResponseMessage? response = null;
            string finalUrl = candidateUrls.Length > 0 ? candidateUrls[0] : "";

            foreach (var initialUrl in candidateUrls)
            {
                if (ct.IsCancellationRequested) break;
                string currentUrl = initialUrl;
                int redirectCount = 0;

                while (redirectCount < 10)
                {
                    try
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                        req.Headers.TryAddWithoutValidation("Accept", "*/*");

                        var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                        if (resp.StatusCode == HttpStatusCode.Redirect ||
                            resp.StatusCode == HttpStatusCode.MovedPermanently ||
                            resp.StatusCode == HttpStatusCode.Found ||
                            resp.StatusCode == HttpStatusCode.SeeOther ||
                            (int)resp.StatusCode == 307 ||
                            (int)resp.StatusCode == 308)
                        {
                            var redirectLocation = resp.Headers.Location;
                            resp.Dispose();
                            if (redirectLocation != null)
                            {
                                currentUrl = redirectLocation.IsAbsoluteUri ? redirectLocation.AbsoluteUri : new Uri(new Uri(currentUrl), redirectLocation).AbsoluteUri;
                                redirectCount++;
                                continue;
                            }
                        }

                        if (resp.IsSuccessStatusCode)
                        {
                            response = resp;
                            finalUrl = currentUrl;
                            break;
                        }
                        else
                        {
                            logCallback("WARN", $"[Cập nhật tự động] Máy chủ phản hồi mã {resp.StatusCode} cho URL {currentUrl}");
                            resp.Dispose();
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        logCallback("WARN", $"[Cập nhật tự động] Không thể kết nối {currentUrl}: {ex.Message}");
                        break;
                    }
                }

                if (response != null && response.IsSuccessStatusCode) break;
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                logCallback("ERROR", "[Cập nhật tự động] Không thể kết nối tới máy chủ cập nhật GitHub Releases. Vui lòng kiểm tra kết nối mạng!");
                return false;
            }

            using (response)
            {
                long totalBytes = response.Content.Headers.ContentLength ?? -1L;
                double totalMb = totalBytes > 0 ? (totalBytes / (1024.0 * 1024.0)) : 0;

                string updateDir = GetWritableUpdateDirectory();
                string fileName = OperatingSystem.IsAndroid()
                    ? "com.CompanyName.ComicDownloaderGMTPC-Signed.apk"
                    : (OperatingSystem.IsWindows() ? "ComicDownloaderGMTPC.Desktop.exe" : "ComicDownloaderGMTPC-linux-x64.tar.gz");
                string targetPath = Path.Combine(updateDir, fileName);

                if (File.Exists(targetPath))
                {
                    try { File.Delete(targetPath); } catch { }
                }

                string fileTypeDesc = OperatingSystem.IsAndroid() 
                    ? "APK Android" 
                    : (OperatingSystem.IsWindows() ? "Windows EXE" : "Linux Portable .tar.gz");
                if (totalBytes > 0)
                {
                    logCallback("INFO", $"[Cập nhật tự động] Đã tìm thấy gói {fileTypeDesc} ({totalMb:F1} MB). Bắt đầu tải...");
                }
                else
                {
                    logCallback("INFO", $"[Cập nhật tự động] Đã tìm thấy gói {fileTypeDesc}. Bắt đầu tải dữ liệu...");
                }

                await using (var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, useAsync: true))
                {
                    byte[] buffer = new byte[131072];
                    long totalDownloaded = 0;
                    int bytesRead;
                    DateTime lastLogTime = DateTime.UtcNow;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                        totalDownloaded += bytesRead;

                        double percent;
                        if (totalBytes > 0)
                        {
                            percent = (totalDownloaded * 100.0) / totalBytes;
                        }
                        else
                        {
                            double estimatedSize = OperatingSystem.IsAndroid() ? (48.0 * 1024.0 * 1024.0) : (70.0 * 1024.0 * 1024.0);
                            percent = Math.Min(99.0, (totalDownloaded * 100.0) / estimatedSize);
                        }

                        progressCallback?.Invoke(percent);

                        if ((DateTime.UtcNow - lastLogTime).TotalMilliseconds >= 800)
                        {
                            lastLogTime = DateTime.UtcNow;
                            double currentMb = totalDownloaded / (1024.0 * 1024.0);
                            if (totalBytes > 0)
                            {
                                logCallback("INFO", $"[Cập nhật tự động] Đã tải {percent:F0}% ({currentMb:F1} MB / {totalMb:F1} MB)...");
                            }
                            else
                            {
                                logCallback("INFO", $"[Cập nhật tự động] Đã tải {currentMb:F1} MB ({percent:F0}%)...");
                            }
                        }
                    }
                }

                progressCallback?.Invoke(100.0);
                double finalMb = new FileInfo(targetPath).Length / (1024.0 * 1024.0);
                logCallback("SUCCESS", $"[Cập nhật tự động] Tải xong 100% ({finalMb:F1} MB)!");

                if (OperatingSystem.IsAndroid())
                {
                    if (InstallApkRequested != null)
                    {
                        logCallback("INFO", "[Cập nhật tự động] Đang kích hoạt trình cài đặt hệ thống Android...");
                        InstallApkRequested.Invoke(targetPath);
                        logCallback("SUCCESS", "[Cập nhật tự động] Đã gửi lệnh cài đặt đến hệ thống Android. Vui lòng bấm 'Cập nhật' trên màn hình!");
                        return true;
                    }
                    else
                    {
                        logCallback("WARN", "[Cập nhật tự động] Trình cài đặt Android chưa sẵn sàng. Tệp APK lưu tại: " + targetPath);
                    }
                }
                else if (OperatingSystem.IsWindows())
                {
                    logCallback("INFO", "[Cập nhật tự động] Đang kích hoạt trình tự động ghi đè và khởi động lại ứng dụng...");
                    return ApplyWindowsUpdateAndRestart(targetPath, logCallback);
                }
                else
                {
                    logCallback("SUCCESS", $"[Cập nhật tự động] Đã tải bản mới về: {targetPath}");
                }

                return true;
            }
        }
        catch (OperationCanceledException)
        {
            logCallback("WARN", "[Cập nhật tự động] Đã hủy tiến trình tải cập nhật.");
            return false;
        }
        catch (Exception ex)
        {
            logCallback("ERROR", $"[Cập nhật tự động] Thất bại: {ex.Message}");
            return false;
        }
    }

    private static bool ApplyWindowsUpdateAndRestart(string newExePath, Action<string, string> logCallback)
    {
        try
        {
            if (!File.Exists(newExePath))
            {
                logCallback("ERROR", "[Cập nhật tự động] Không tìm thấy tệp bản mới đã tải.");
                return false;
            }

            var fi = new FileInfo(newExePath);
            if (fi.Length < 1024 * 1024)
            {
                logCallback("ERROR", $"[Cập nhật tự động] Tệp tải về không hợp lệ (kích thước quá nhỏ: {fi.Length} bytes).");
                return false;
            }

            string currentExePath = Environment.ProcessPath ?? "";
            if (string.IsNullOrWhiteSpace(currentExePath) || !File.Exists(currentExePath))
            {
                try
                {
                    using var curProc = Process.GetCurrentProcess();
                    currentExePath = curProc.MainModule?.FileName ?? "";
                }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(currentExePath) || !File.Exists(currentExePath))
            {
                currentExePath = Path.Combine(AppContext.BaseDirectory, "ComicDownloaderGMTPC.Desktop.exe");
            }

            int currentPid = Environment.ProcessId;
            string targetDir = Path.GetDirectoryName(currentExePath) ?? AppContext.BaseDirectory;
            string scriptPath = Path.Combine(Path.GetTempPath(), $"gmtpc_updater_{Guid.NewGuid():N}.cmd");

            // Tạo nội dung script batch tự động ghi đè và khởi động lại ngầm không cửa sổ
            string scriptContent = $@"@echo off
chcp 65001 >nul
set PID={currentPid}
set NEW_EXE=""{newExePath}""
set TARGET_EXE=""{currentExePath}""

:: 1. Doi ung dung cu thoat hoan toan
:wait_loop
timeout /t 1 /nobreak >nul 2>&1
tasklist /fi ""PID eq %PID%"" 2>nul | findstr /i ""%PID%"" >nul 2>&1
if not errorlevel 1 goto wait_loop

:: Cho them 500ms de Windows giai phong toan bo handle
timeout /t 1 /nobreak >nul 2>&1

:: 2. Tien hanh ghi de file moi vao vi tri file cu (thu toi da 10 lan)
set RETRY=0
:copy_loop
move /y %NEW_EXE% %TARGET_EXE% >nul 2>&1
if errorlevel 1 (
    copy /y %NEW_EXE% %TARGET_EXE% >nul 2>&1
    if errorlevel 1 (
        set /a RETRY+=1
        if %RETRY% leq 10 (
            timeout /t 1 /nobreak >nul 2>&1
            goto copy_loop
        )
    )
)

:: 3. Khoi dong lai ung dung moi
start """" %TARGET_EXE%

:: 4. Tu dong don dep file script
del /f /q %NEW_EXE% >nul 2>&1
(goto) 2>nul & del /f /q ""%~f0"" >nul 2>&1
";

            File.WriteAllText(scriptPath, scriptContent, new System.Text.UTF8Encoding(false));

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{scriptPath}\"\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = targetDir
            };

            Process.Start(psi);

            logCallback("SUCCESS", "[Cập nhật tự động] Đã chuẩn bị xong bản mới! Ứng dụng sẽ tự động khởi động lại sau 2 giây...");

            // Đóng ứng dụng an toàn sau 2 giây để nhường quyền ghi đè file
            Task.Run(async () =>
            {
                await Task.Delay(2000).ConfigureAwait(false);
                Environment.Exit(0);
            });

            return true;
        }
        catch (Exception ex)
        {
            logCallback("ERROR", $"[Cập nhật tự động] Lỗi khi tạo tiến trình cập nhật Windows: {ex.Message}");
            return false;
        }
    }
}
