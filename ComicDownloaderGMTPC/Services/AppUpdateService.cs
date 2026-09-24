using System;
using System.Diagnostics;
using System.IO;
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
            MaxAutomaticRedirections = 10
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ComicDownloaderGMTPC-Updater/1.0");
    }

    public async Task<bool> DownloadAndInstallUpdateAsync(
        string apkUrl,
        Action<string, string> logCallback,
        Action<double>? progressCallback = null,
        CancellationToken ct = default)
    {
        try
        {
            logCallback("INFO", "[Cập nhật tự động] Bắt đầu kết nối tải bản cập nhật mới nhất...");

            using var response = await _httpClient.GetAsync(apkUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1L;
            double totalMb = totalBytes > 0 ? (totalBytes / (1024.0 * 1024.0)) : 0;

            string targetApkPath;
            if (OperatingSystem.IsAndroid())
            {
                string appDir = DownloadEngineService.GetAppSpecificExternalPath();
                if (!Directory.Exists(appDir)) Directory.CreateDirectory(appDir);
                targetApkPath = Path.Combine(appDir, "ComicDownloaderGMTPC_Update.apk");
            }
            else
            {
                string userDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (!Directory.Exists(userDownloads))
                {
                    userDownloads = AppContext.BaseDirectory;
                }
                targetApkPath = Path.Combine(userDownloads, "com.CompanyName.ComicDownloaderGMTPC-Signed.apk");
            }

            if (File.Exists(targetApkPath))
            {
                try { File.Delete(targetApkPath); } catch { }
            }

            logCallback("INFO", $"[Cập nhật tự động] Đang tải tệp APK ({totalMb:F1} MB)...");

            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var fileStream = new FileStream(targetApkPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long totalDownloaded = 0;
                int bytesRead;
                DateTime lastLogTime = DateTime.UtcNow;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                    totalDownloaded += bytesRead;

                    if (totalBytes > 0)
                    {
                        double percent = (totalDownloaded * 100.0) / totalBytes;
                        progressCallback?.Invoke(percent);

                        if ((DateTime.UtcNow - lastLogTime).TotalMilliseconds >= 1200)
                        {
                            lastLogTime = DateTime.UtcNow;
                            double currentMb = totalDownloaded / (1024.0 * 1024.0);
                            logCallback("INFO", $"[Cập nhật tự động] Đã tải {percent:F0}% ({currentMb:F1} MB / {totalMb:F1} MB)...");
                        }
                    }
                }
            }

            progressCallback?.Invoke(100.0);
            logCallback("SUCCESS", $"[Cập nhật tự động] Tải xong 100% ({totalMb:F1} MB)! Đang kích hoạt cài đặt...");

            if (OperatingSystem.IsAndroid())
            {
                if (InstallApkRequested != null)
                {
                    InstallApkRequested.Invoke(targetApkPath);
                    logCallback("SUCCESS", "[Cập nhật tự động] Đã gửi lệnh cài đặt đến hệ thống Android. Vui lòng bấm 'Cập nhật' trên thông báo hệ thống!");
                    return true;
                }
                else
                {
                    logCallback("WARN", "[Cập nhật tự động] Trình cài đặt Android chưa sẵn sàng. Bạn có thể mở tệp tại: " + targetApkPath);
                }
            }
            else
            {
                // Trên máy tính Desktop Windows/Linux
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{targetApkPath}\"",
                            UseShellExecute = true
                        });
                        logCallback("SUCCESS", $"[Cập nhật tự động] Đã tải APK vào thư mục Downloads: {targetApkPath}");
                    }
                }
                catch { }
            }

            return true;
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
}
