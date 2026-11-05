using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ComicDownloaderGMTPC.Services;

public sealed class UpdateHashRecord
{
    public string Channel { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string ServerHash { get; set; } = string.Empty;
    public string LocalSha256 { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime LastDownloadedUtc { get; set; } = DateTime.MinValue;
}

public sealed class UpdateCheckResult
{
    public bool Success { get; set; }
    public bool HasUpdate { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string ServerHash { get; set; } = string.Empty;
    public string SavedHash { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime? LastModifiedUtc { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class AppUpdateService
{
    private static readonly Lazy<AppUpdateService> _instance = new(() => new AppUpdateService());
    public static AppUpdateService Instance => _instance.Value;

    public static event Action<string>? InstallApkRequested;

    private const string UpdateHashCacheFileName = "update_hash_cache.json";

    private static string GetHashCacheFilePath()
    {
        string configDir = ComicScraperService.GetConfigDirectory();
        if (!Directory.Exists(configDir))
        {
            try { Directory.CreateDirectory(configDir); } catch { }
        }
        return Path.Combine(configDir, UpdateHashCacheFileName);
    }

    public static string CleanHashString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string clean = raw.Trim();
        if (clean.StartsWith("W/\"", StringComparison.OrdinalIgnoreCase) && clean.EndsWith("\"")) clean = clean[3..^1];
        else if (clean.StartsWith("\"") && clean.EndsWith("\"")) clean = clean[1..^1];
        return clean.Trim();
    }

    public static UpdateHashRecord? GetSavedUpdateHash(string channel)
    {
        try
        {
            string path = GetHashCacheFilePath();
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, UpdateHashRecord>>(json);
                if (dict != null && dict.TryGetValue(channel.ToLowerInvariant(), out var record))
                {
                    return record;
                }
            }
        }
        catch { }
        return null;
    }

    public static void SaveUpdateHash(string channel, string serverHash, string localSha256, long size)
    {
        try
        {
            string path = GetHashCacheFilePath();
            var dict = new Dictionary<string, UpdateHashRecord>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(path))
            {
                try
                {
                    string oldJson = File.ReadAllText(path, System.Text.Encoding.UTF8);
                    var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, UpdateHashRecord>>(oldJson);
                    if (loaded != null) dict = loaded;
                }
                catch { }
            }

            dict[channel.ToLowerInvariant()] = new UpdateHashRecord
            {
                Channel = channel,
                Platform = OperatingSystem.IsAndroid() ? "Android" : (OperatingSystem.IsWindows() ? "Windows" : "Linux"),
                ServerHash = CleanHashString(serverHash),
                LocalSha256 = localSha256?.ToLowerInvariant() ?? string.Empty,
                FileSizeBytes = size,
                LastDownloadedUtc = DateTime.UtcNow
            };

            string json = System.Text.Json.JsonSerializer.Serialize(dict, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, System.Text.Encoding.UTF8);
        }
        catch { }
    }

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

    /// <summary>
    /// Kiểm tra phiên bản cập nhật trên máy chủ GitHub và đối chiếu mã hash với phiên bản đã tải trước đó.
    /// </summary>
    public async Task<UpdateCheckResult> CheckUpdateAsync(string[] candidateUrls, string channel, CancellationToken ct = default)
    {
        var result = new UpdateCheckResult { Channel = channel };
        try
        {
            HttpResponseMessage? headResponse = null;

            foreach (var initialUrl in candidateUrls)
            {
                if (ct.IsCancellationRequested) break;
                string currentUrl = initialUrl;
                int redirectCount = 0;

                while (redirectCount < 10)
                {
                    try
                    {
                        var req = new HttpRequestMessage(HttpMethod.Head, currentUrl);
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
                            var loc = resp.Headers.Location;
                            resp.Dispose();
                            if (loc != null)
                            {
                                currentUrl = loc.IsAbsoluteUri ? loc.AbsoluteUri : new Uri(new Uri(currentUrl), loc).AbsoluteUri;
                                redirectCount++;
                                continue;
                            }
                        }

                        if (resp.IsSuccessStatusCode)
                        {
                            headResponse = resp;
                            break;
                        }
                        else
                        {
                            resp.Dispose();
                            // Fallback GET với Range 0-0 nếu máy chủ chặn HEAD
                            var getReq = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                            getReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                            getReq.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
                            var getResp = await _httpClient.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                            if (getResp.IsSuccessStatusCode)
                            {
                                headResponse = getResp;
                                break;
                            }
                            getResp.Dispose();
                            break;
                        }
                    }
                    catch
                    {
                        break;
                    }
                }

                if (headResponse != null && headResponse.IsSuccessStatusCode) break;
            }

            if (headResponse == null || !headResponse.IsSuccessStatusCode)
            {
                result.Success = false;
                result.Message = "Không thể kết nối máy chủ GitHub Release để kiểm tra bản cập nhật.";
                return result;
            }

            using (headResponse)
            {
                string etag = CleanHashString(headResponse.Headers.ETag?.Tag);
                long contentLength = headResponse.Content.Headers.ContentLength ?? 0;
                DateTimeOffset? lastMod = headResponse.Content.Headers.LastModified;

                result.ServerHash = !string.IsNullOrEmpty(etag) ? etag : (lastMod.HasValue ? lastMod.Value.UtcTicks.ToString() : $"{contentLength}");
                result.FileSizeBytes = contentLength;
                result.LastModifiedUtc = lastMod?.UtcDateTime;

                var saved = GetSavedUpdateHash(channel);
                result.SavedHash = saved?.ServerHash ?? string.Empty;

                string shortServerHash = result.ServerHash.Length > 12 ? result.ServerHash[..12] : result.ServerHash;
                string shortSavedHash = !string.IsNullOrEmpty(result.SavedHash)
                    ? (result.SavedHash.Length > 12 ? result.SavedHash[..12] : result.SavedHash)
                    : "(Chưa có)";

                // Đối chiếu mã hash máy chủ vs bản đã tải trước đó
                if (string.IsNullOrEmpty(result.SavedHash))
                {
                    result.HasUpdate = true;
                    result.Message = $"Đã có phiên bản cập nhật mới trên máy chủ! (Mã hash: {shortServerHash})";
                }
                else if (!string.Equals(result.ServerHash, result.SavedHash, StringComparison.OrdinalIgnoreCase))
                {
                    result.HasUpdate = true;
                    result.Message = $"Đã có phiên bản cập nhật mới! (Mã hash mới: {shortServerHash} | Bản đã tải: {shortSavedHash})";
                }
                else
                {
                    result.HasUpdate = false;
                    result.Message = $"Bạn đang sử dụng phiên bản mới nhất! (Mã hash: {shortServerHash})";
                }

                result.Success = true;
                return result;
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Lỗi kiểm tra cập nhật: {ex.Message}";
            return result;
        }
    }

    public async Task<bool> DownloadAndInstallUpdateAsync(
        string[] candidateUrls,
        Action<string, string> logCallback,
        Action<double>? progressCallback = null,
        string channel = "release",
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

                // Tự động trích xuất và lưu mã hash từ server và mã hash cục bộ vào bộ nhớ
                try
                {
                    string serverHash = CleanHashString(response.Headers.ETag?.Tag);
                    if (string.IsNullOrEmpty(serverHash) && response.Content.Headers.LastModified.HasValue)
                    {
                        serverHash = response.Content.Headers.LastModified.Value.UtcTicks.ToString();
                    }

                    string localSha = string.Empty;
                    using (var fs = File.OpenRead(targetPath))
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    {
                        byte[] hashBytes = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
                        localSha = Convert.ToHexString(hashBytes).ToLowerInvariant();
                    }

                    if (string.IsNullOrEmpty(serverHash)) serverHash = localSha;
                    SaveUpdateHash(channel, serverHash, localSha, new FileInfo(targetPath).Length);
                    string shortHash = serverHash.Length > 12 ? serverHash[..12] : serverHash;
                    logCallback("INFO", $"[Cập nhật tự động] Đã tự động lưu mã hash phiên bản [{channel.ToUpperInvariant()}]: {shortHash}");
                }
                catch { }

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
            if (fi.Length < 10 * 1024)
            {
                logCallback("ERROR", $"[Cập nhật tự động] Tệp tải về không hợp lệ (kích thước quá nhỏ: {fi.Length} bytes).");
                return false;
            }

            string currentExePath = Path.Combine(AppContext.BaseDirectory, "ComicDownloaderGMTPC.Desktop.exe");
            if (!File.Exists(currentExePath))
            {
                currentExePath = Environment.ProcessPath ?? "";
                if (string.IsNullOrWhiteSpace(currentExePath) || !File.Exists(currentExePath))
                {
                    try
                    {
                        using var curProc = Process.GetCurrentProcess();
                        currentExePath = curProc.MainModule?.FileName ?? "";
                    }
                    catch { }
                }
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
