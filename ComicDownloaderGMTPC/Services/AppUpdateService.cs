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
        string[] candidateUrls,
        Action<string, string> logCallback,
        Action<double>? progressCallback = null,
        CancellationToken ct = default)
    {
        try
        {
            logCallback("INFO", "[Cập nhật tự động] Đang tìm kiếm và kết nối bản cập nhật mới nhất...");

            HttpResponseMessage? response = null;
            string finalUrl = candidateUrls.Length > 0 ? candidateUrls[0] : "";

            foreach (var url in candidateUrls)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, url);
                    var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        response = resp;
                        finalUrl = url;
                        break;
                    }
                    resp.Dispose();
                }
                catch
                {
                    // Thử URL tiếp theo
                }
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                logCallback("ERROR", "[Cập nhật tự động] Không thể kết nối tới máy chủ cập nhật GitHub Releases. Vui lòng kiểm tra lại kết nối mạng!");
                return false;
            }

            using (response)
            {
                long totalBytes = response.Content.Headers.ContentLength ?? -1L;
                double totalMb = totalBytes > 0 ? (totalBytes / (1024.0 * 1024.0)) : 0;

                string targetPath;
                if (OperatingSystem.IsAndroid())
                {
                    string appDir = DownloadEngineService.GetAppSpecificExternalPath();
                    if (!Directory.Exists(appDir)) Directory.CreateDirectory(appDir);
                    targetPath = Path.Combine(appDir, "ComicDownloaderGMTPC_Update.apk");
                }
                else
                {
                    string userDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    if (!Directory.Exists(userDownloads))
                    {
                        userDownloads = AppContext.BaseDirectory;
                    }
                    string fileName = Path.GetFileName(new Uri(finalUrl).AbsolutePath);
                    if (string.IsNullOrWhiteSpace(fileName)) fileName = "ComicDownloaderGMTPC.Desktop.exe";
                    targetPath = Path.Combine(userDownloads, fileName);
                }

                if (File.Exists(targetPath))
                {
                    try { File.Delete(targetPath); } catch { }
                }

                string fileTypeDesc = OperatingSystem.IsAndroid() ? "APK Android" : "EXE Windows";
                logCallback("INFO", $"[Cập nhật tự động] Đang tải trực tiếp gói {fileTypeDesc} ({totalMb:F1} MB)...");

                await using (var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    byte[] buffer = new byte[81920];
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
                            // Ước tính kích thước khoảng 50MB trên Android và 70MB trên Desktop nếu không có Content-Length
                            double estimatedSize = OperatingSystem.IsAndroid() ? (50.0 * 1024.0 * 1024.0) : (70.0 * 1024.0 * 1024.0);
                            percent = Math.Min(99.0, (totalDownloaded * 100.0) / estimatedSize);
                        }

                        progressCallback?.Invoke(percent);

                        if ((DateTime.UtcNow - lastLogTime).TotalMilliseconds >= 1000)
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
                logCallback("SUCCESS", $"[Cập nhật tự động] Tải xong 100% ({totalMb:F1} MB)!");

                if (OperatingSystem.IsAndroid())
                {
                    if (InstallApkRequested != null)
                    {
                        logCallback("INFO", "[Cập nhật tự động] Đang kích hoạt trình cài đặt hệ thống Android...");
                        InstallApkRequested.Invoke(targetPath);
                        logCallback("SUCCESS", "[Cập nhật tự động] Đã gửi lệnh cài đặt đến hệ thống Android. Vui lòng bấm 'Cập nhật' trên thông báo hệ thống!");
                        return true;
                    }
                    else
                    {
                        logCallback("WARN", "[Cập nhật tự động] Trình cài đặt Android chưa sẵn sàng. Bạn có thể mở tệp tại: " + targetPath);
                    }
                }
                else
                {
                    // Trên máy tính Desktop Windows / Linux
                    try
                    {
                        if (OperatingSystem.IsWindows())
                        {
                            logCallback("SUCCESS", $"[Cập nhật tự động] Đã tải bản mới về thư mục Downloads: {targetPath}");
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                Arguments = $"/select,\"{targetPath}\"",
                                UseShellExecute = true
                            });
                        }
                    }
                    catch { }
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
}
