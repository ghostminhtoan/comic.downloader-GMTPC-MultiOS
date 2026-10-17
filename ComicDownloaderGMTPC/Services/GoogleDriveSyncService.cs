using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ComicDownloaderGMTPC.Models;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Trạng thái lưu trữ xác thực và cấu hình Google Drive Sync.
/// </summary>
public sealed class GoogleDriveAuthConfig
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; } = DateTime.MinValue;
    public string UserEmail { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string UserPictureUrl { get; set; } = string.Empty;
    public bool AutoSyncEnabled { get; set; } = false;
    public DateTime LastSyncUtc { get; set; } = DateTime.MinValue;
    public string CachedAppFolderId { get; set; } = string.Empty;
}

/// <summary>
/// Thông tin tệp tin trên Google Drive.
/// </summary>
public sealed class GoogleDriveFileInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime? ModifiedTime { get; set; }
    public long Size { get; set; }
    public bool Exists => !string.IsNullOrEmpty(Id);
}

/// <summary>
/// Báo cáo trạng thái tổng thể giữa Local và Cloud.
/// </summary>
public sealed class GoogleDriveSyncStatus
{
    public bool IsConnected { get; set; }
    public string AccountEmail { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string TargetFolderPath { get; set; } = @"\GMTPC\Comic Downloader GMTPC\";
    public string TargetFolderId { get; set; } = string.Empty;
    public GoogleDriveFileInfo QueueCloudFile { get; set; } = new();
    public GoogleDriveFileInfo ReaderCloudFile { get; set; } = new();
    public GoogleDriveFileInfo ManifestCloudFile { get; set; } = new();
    public DateTime? LastSyncTimeUtc { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
}

/// <summary>
/// Kết quả đồng bộ toàn diện.
/// </summary>
public sealed class FullSyncResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool QueueSynced { get; set; }
    public bool ReaderSynced { get; set; }
    public int QueueItemCount { get; set; }
    public int RestoredBookmarks { get; set; }
    public int RestoredHistory { get; set; }
}

/// <summary>
/// Dịch vụ Đồng bộ Hàng đợi Tải & Dấu trang / Lịch sử đọc lên Google Drive Cloud.
/// Thư mục chỉ định trên Google Drive: \GMTPC\Comic Downloader GMTPC\
/// </summary>
public sealed class GoogleDriveSyncService
{
    private static readonly Lazy<GoogleDriveSyncService> _instance = new(() => new GoogleDriveSyncService());
    public static GoogleDriveSyncService Instance => _instance.Value;

    public const string DefaultTargetFolderPath = @"\GMTPC\Comic Downloader GMTPC\";
    public const string RootParentFolderName = "GMTPC";
    public const string AppSubFolderName = "Comic Downloader GMTPC";

    public const string QueueFileName = "autosave.md";
    public const string ReaderFileName = "saved_reader_bookmarks.md";
    public const string ManifestFileName = "cloud_sync_manifest.json";
    private const string AuthConfigFileName = "gdrive_auth.json";

    // Google OAuth Endpoints
    private const string GoogleAuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string GoogleTokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string GoogleUserInfoEndpoint = "https://www.googleapis.com/oauth2/v3/userinfo";
    private const string GoogleDriveApiBase = "https://www.googleapis.com/drive/v3";
    private const string GoogleDriveUploadBase = "https://www.googleapis.com/upload/drive/v3";

    // Scope chuẩn: drive.file chỉ cấp quyền thao tác trên các file/folder do chính app tạo ra
    private const string OAuthScopes = "https://www.googleapis.com/auth/drive.file openid email profile";

    // Client ID công khai được kiểm thử sẵn (hỗ trợ Quick Connect tức thì không cần tạo project)
    public const string CommunityClientId = "202264815644.apps.googleusercontent.com";
    public const string CommunityClientSecret = "X4Z3ca8xfWDb1Voo-F9a7ZxJ";

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private GoogleDriveAuthConfig _config = new();
    private System.Threading.Timer? _autoSyncDebounceTimer;
    private bool _hasPendingAutoSync;

    public event Action<GoogleDriveSyncStatus>? StatusChanged;

    private GoogleDriveSyncService()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            AutomaticDecompression = DecompressionMethods.All
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        LoadConfigFromDisk();
    }

    public bool IsConnected => !string.IsNullOrWhiteSpace(_config.RefreshToken) && !string.IsNullOrWhiteSpace(_config.UserEmail);
    public string UserEmail => _config.UserEmail;
    public string UserName => _config.UserName;
    public string UserPictureUrl => _config.UserPictureUrl;
    public bool AutoSyncEnabled
    {
        get => _config.AutoSyncEnabled;
        set
        {
            if (_config.AutoSyncEnabled != value)
            {
                _config.AutoSyncEnabled = value;
                SaveConfigToDisk();
            }
        }
    }
    public DateTime LastSyncUtc => _config.LastSyncUtc;
    public string TargetFolderId => _config.CachedAppFolderId;

    #region Configuration Storage

    private static string GetConfigFilePath()
    {
        string dir = ComicScraperService.GetConfigDirectory();
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
        return Path.Combine(dir, AuthConfigFileName);
    }

    private void LoadConfigFromDisk()
    {
        try
        {
            string path = GetConfigFilePath();
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<GoogleDriveAuthConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded != null)
                {
                    _config = loaded;

                    // Tự động chuyển đổi secret cũ bị vô hiệu hóa sang secret mới hợp lệ
                    if (_config.ClientSecret == "XXZa4bkAmKiuh1SBcwGCJxj_" ||
                        (_config.ClientId == CommunityClientId && _config.ClientSecret != CommunityClientSecret))
                    {
                        _config.ClientSecret = CommunityClientSecret;
                        SaveConfigToDisk();
                    }
                }
            }
        }
        catch { }
    }

    private void SaveConfigToDisk()
    {
        try
        {
            string path = GetConfigFilePath();
            string json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        catch { }
    }

    public void SaveCustomClientCredentials(string clientId, string clientSecret)
    {
        _config.ClientId = clientId?.Trim() ?? string.Empty;
        _config.ClientSecret = clientSecret?.Trim() ?? string.Empty;
        SaveConfigToDisk();
    }

    public (string ClientId, string ClientSecret) GetClientCredentials()
    {
        return (_config.ClientId, _config.ClientSecret);
    }

    public bool HasConfiguredClientId =>
        !string.IsNullOrWhiteSpace(_config.ClientId) &&
        !_config.ClientId.Contains("gmtpccomicdownloader");

    public void ApplyCommunityCredentials()
    {
        _config.ClientId = CommunityClientId;
        _config.ClientSecret = CommunityClientSecret;
        SaveConfigToDisk();
    }

    public (bool Success, string ClientId, string ClientSecret, string Message) ImportCredentialsFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement target = default;
            if (root.TryGetProperty("installed", out var inst)) target = inst;
            else if (root.TryGetProperty("web", out var web)) target = web;
            else target = root;

            string cid = target.TryGetProperty("client_id", out var idProp) ? idProp.GetString() ?? "" : "";
            string sec = target.TryGetProperty("client_secret", out var secProp) ? secProp.GetString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(cid))
            {
                return (false, "", "", "Tệp JSON không chứa trường 'client_id' hợp lệ.");
            }

            SaveCustomClientCredentials(cid, sec);
            return (true, cid, sec, "Đã nhập cấu hình Google Client ID thành công từ tệp JSON!");
        }
        catch (Exception ex)
        {
            return (false, "", "", $"Lỗi đọc tệp JSON: {ex.Message}");
        }
    }

    #endregion

    #region OAuth2 PKCE Authentication

    /// <summary>
    /// Tạo mã ngẫu nhiên Code Verifier chuẩn RFC 7636.
    /// </summary>
    private static string GenerateCodeVerifier()
    {
        byte[] bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Tạo Code Challenge S256 từ Code Verifier.
    /// </summary>
    private static string GenerateCodeChallenge(string codeVerifier)
    {
        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        string base64 = Convert.ToBase64String(bytes);
        return base64.Replace("+", "-").Replace("/", "_").Replace("=", "");
    }

    private static int FindFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Tạo Auth URL để người dùng mở trình duyệt hoặc copy thủ công.
    /// </summary>
    public (string AuthUrl, string CodeVerifier, string RedirectUri) CreateAuthRequest(string? customClientId = null)
    {
        string clientId = !string.IsNullOrWhiteSpace(customClientId) && !customClientId.Contains("gmtpccomicdownloader")
            ? customClientId.Trim()
            : (HasConfiguredClientId ? _config.ClientId.Trim() : CommunityClientId);

        string codeVerifier = GenerateCodeVerifier();
        string codeChallenge = GenerateCodeChallenge(codeVerifier);

        int port = FindFreeLoopbackPort();
        string redirectUri = $"http://127.0.0.1:{port}/callback/";

        var query = new StringBuilder();
        query.Append($"{GoogleAuthEndpoint}?");
        query.Append($"client_id={Uri.EscapeDataString(clientId)}");
        query.Append($"&redirect_uri={Uri.EscapeDataString(redirectUri)}");
        query.Append($"&response_type=code");
        query.Append($"&scope={Uri.EscapeDataString(OAuthScopes)}");
        query.Append($"&code_challenge={Uri.EscapeDataString(codeChallenge)}");
        query.Append($"&code_challenge_method=S256");
        query.Append($"&access_type=offline");
        query.Append($"&prompt=consent");

        return (query.ToString(), codeVerifier, redirectUri);
    }

    /// <summary>
    /// Bắt đầu quy trình đăng nhập Google Drive (Loopback server trên Desktop + Browser).
    /// </summary>
    public async Task<(bool Success, string Message)> LoginWithBrowserAsync(string? customClientId = null, string? customClientSecret = null, CancellationToken ct = default)
    {
        string clientId = !string.IsNullOrWhiteSpace(customClientId) && !customClientId.Contains("gmtpccomicdownloader")
            ? customClientId.Trim()
            : (HasConfiguredClientId ? _config.ClientId.Trim() : CommunityClientId);

        string clientSecret = !string.IsNullOrWhiteSpace(customClientSecret)
            ? customClientSecret.Trim()
            : (!string.IsNullOrWhiteSpace(_config.ClientSecret) && _config.ClientSecret != "XXZa4bkAmKiuh1SBcwGCJxj_"
                ? _config.ClientSecret.Trim()
                : CommunityClientSecret);

        if (clientId == CommunityClientId && (string.IsNullOrWhiteSpace(clientSecret) || clientSecret == "XXZa4bkAmKiuh1SBcwGCJxj_"))
        {
            clientSecret = CommunityClientSecret;
        }

        int port = FindFreeLoopbackPort();
        string redirectUri = $"http://127.0.0.1:{port}/callback/";
        string codeVerifier = GenerateCodeVerifier();
        string codeChallenge = GenerateCodeChallenge(codeVerifier);

        string authUrl = $"{GoogleAuthEndpoint}?" +
            $"client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&response_type=code" +
            $"&scope={Uri.EscapeDataString(OAuthScopes)}" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
            $"&code_challenge_method=S256" +
            $"&access_type=offline" +
            $"&prompt=consent";

        HttpListener? listener = null;
        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add(redirectUri);
            listener.Start();
        }
        catch (Exception ex)
        {
            return (false, $"Không thể khởi tạo cổng lắng nghe cục bộ ({ex.Message}). Vui lòng dùng phương thức nhập mã thủ công.");
        }

        try
        {
            // Mở trình duyệt mặc định
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = authUrl,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Nếu không mở được shell, thử dùng explorer trên Windows
                if (OperatingSystem.IsWindows())
                {
                    Process.Start("explorer", authUrl);
                }
            }

            // Chờ phản hồi callback từ trình duyệt tối đa 2 phút
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var contextTask = listener.GetContextAsync();
            var completedTask = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, linkedCts.Token));

            if (completedTask != contextTask)
            {
                listener.Stop();
                return (false, "Hết thời gian chờ cấp quyền từ trình duyệt (2 phút) hoặc người dùng đã hủy.");
            }

            var context = await contextTask;
            var request = context.Request;
            var response = context.Response;

            string? authCode = request.QueryString["code"];
            string? error = request.QueryString["error"];

            // Trả trang HTML đẹp mắt cho trình duyệt
            string responseHtml;
            if (!string.IsNullOrEmpty(authCode))
            {
                responseHtml = @"<!DOCTYPE html>
<html>
<head><meta charset='utf-8'><title>Comic Downloader GMTPC - Kết Nối Google Drive</title>
<style>
body { background: #0B0F19; color: #F8FAFC; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; }
.card { background: #111827; border: 1px solid #0284C7; padding: 36px 44px; border-radius: 12px; text-align: center; box-shadow: 0 10px 30px rgba(0,0,0,0.6); max-width: 480px; }
h1 { color: #38BDF8; font-size: 22px; margin-bottom: 12px; }
p { color: #94A3B8; font-size: 15px; line-height: 1.5; margin: 8px 0; }
.badge { display: inline-block; background: #064E3B; color: #34D399; font-weight: bold; padding: 4px 12px; border-radius: 20px; font-size: 13px; margin-top: 10px; }
</style>
</head>
<body>
<div class='card'>
  <h1>⚡ Đăng Nhập Google Drive Thành Công!</h1>
  <p>Tài khoản Google Drive đã được xác thực an toàn cho <b>Comic Downloader GMTPC</b>.</p>
  <div class='badge'>✓ ĐÃ SẴN SÀNG ĐỒNG BỘ</div>
  <p style='margin-top:16px; font-size:13px; color:#64748B;'>Bạn có thể đóng tab trình duyệt này và quay lại ứng dụng.</p>
</div>
</body>
</html>";
            }
            else
            {
                responseHtml = $@"<!DOCTYPE html>
<html>
<head><meta charset='utf-8'><title>Comic Downloader GMTPC - Lỗi Xác Thực</title>
<style>
body {{ background: #0B0F19; color: #F8FAFC; font-family: sans-serif; display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; }}
.card {{ background: #111827; border: 1px solid #DC2626; padding: 36px; border-radius: 12px; text-align: center; max-width: 480px; }}
h1 {{ color: #EF4444; font-size: 20px; }}
p {{ color: #94A3B8; font-size: 14px; }}
</style>
</head>
<body>
<div class='card'>
  <h1>❌ Xác Thực Không Thành Công</h1>
  <p>Lỗi: {WebUtility.HtmlEncode(error ?? "Không nhận được authorization code")}</p>
  <p>Vui lòng thử lại trong ứng dụng.</p>
</div>
</body>
</html>";
            }

            byte[] buffer = Encoding.UTF8.GetBytes(responseHtml);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, ct);
            response.OutputStream.Close();
            listener.Stop();

            if (!string.IsNullOrEmpty(error))
            {
                return (false, $"Google báo lỗi: {error}");
            }

            if (string.IsNullOrEmpty(authCode))
            {
                return (false, "Không tìm thấy mã ủy quyền trong phản hồi từ Google.");
            }

            // Đổi Auth Code lấy Tokens
            return await ExchangeCodeForTokensAsync(authCode, codeVerifier, redirectUri, clientId, clientSecret, ct);
        }
        catch (OperationCanceledException)
        {
            return (false, "Tiến trình đăng nhập đã bị hủy.");
        }
        catch (Exception ex)
        {
            return (false, $"Lỗi trong quá trình đăng nhập: {ex.Message}");
        }
        finally
        {
            try { listener?.Stop(); listener?.Close(); } catch { }
        }
    }

    /// <summary>
    /// Đổi Authorization Code thành Access Token & Refresh Token.
    /// </summary>
    public async Task<(bool Success, string Message)> ExchangeCodeForTokensAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        string? clientId = null,
        string? clientSecret = null,
        CancellationToken ct = default)
    {
        try
        {
            string actualClientId = !string.IsNullOrWhiteSpace(clientId) && !clientId.Contains("gmtpccomicdownloader")
                ? clientId.Trim()
                : (HasConfiguredClientId ? _config.ClientId.Trim() : CommunityClientId);

            string actualSecret = !string.IsNullOrWhiteSpace(clientSecret)
                ? clientSecret.Trim()
                : (!string.IsNullOrWhiteSpace(_config.ClientSecret) && _config.ClientSecret != "XXZa4bkAmKiuh1SBcwGCJxj_"
                    ? _config.ClientSecret.Trim()
                    : CommunityClientSecret);

            if (actualClientId == CommunityClientId && (string.IsNullOrWhiteSpace(actualSecret) || actualSecret == "XXZa4bkAmKiuh1SBcwGCJxj_"))
            {
                actualSecret = CommunityClientSecret;
            }

            var postData = new Dictionary<string, string>
            {
                ["code"] = code.Trim(),
                ["client_id"] = actualClientId,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = codeVerifier
            };

            if (!string.IsNullOrWhiteSpace(actualSecret))
            {
                postData["client_secret"] = actualSecret;
            }

            using var content = new FormUrlEncodedContent(postData);
            using var response = await _httpClient.PostAsync(GoogleTokenEndpoint, content, ct);
            string responseJson = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"Không thể nhận token từ Google ({response.StatusCode}): {responseJson}");
            }

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            string accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "";
            string refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
            int expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

            if (string.IsNullOrEmpty(accessToken))
            {
                return (false, "Google không trả về access_token.");
            }

            _config.ClientId = actualClientId;
            _config.ClientSecret = actualSecret;
            _config.AccessToken = accessToken;
            if (!string.IsNullOrEmpty(refreshToken))
            {
                _config.RefreshToken = refreshToken;
            }
            _config.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn - 120);

            // Tải thông tin người dùng
            await FetchAndApplyUserInfoAsync(accessToken, ct);
            SaveConfigToDisk();

            NotifyStatusChanged("Đăng nhập thành công!");
            return (true, $"Đăng nhập Google Drive thành công: {_config.UserEmail}");
        }
        catch (Exception ex)
        {
            return (false, $"Lỗi xử lý mã token: {ex.Message}");
        }
    }

    private async Task FetchAndApplyUserInfoAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, GoogleUserInfoEndpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await _httpClient.SendAsync(req, ct);
            if (res.IsSuccessStatusCode)
            {
                string json = await res.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("email", out var email)) _config.UserEmail = email.GetString() ?? "";
                if (root.TryGetProperty("name", out var name)) _config.UserName = name.GetString() ?? "";
                if (root.TryGetProperty("picture", out var pic)) _config.UserPictureUrl = pic.GetString() ?? "";
            }
        }
        catch { }
    }

    /// <summary>
    /// Đảm bảo Access Token còn hạn sử dụng, tự động refresh bằng RefreshToken nếu cần.
    /// </summary>
    private async Task<string?> EnsureValidAccessTokenAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.RefreshToken))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_config.AccessToken) && DateTime.UtcNow < _config.ExpiresAtUtc)
        {
            return _config.AccessToken;
        }

        // Cần làm mới token
        try
        {
            string actualClientId = !string.IsNullOrWhiteSpace(_config.ClientId) ? _config.ClientId : CommunityClientId;
            string actualSecret = _config.ClientSecret == "XXZa4bkAmKiuh1SBcwGCJxj_" || (actualClientId == CommunityClientId && string.IsNullOrWhiteSpace(_config.ClientSecret))
                ? CommunityClientSecret
                : _config.ClientSecret;

            var postData = new Dictionary<string, string>
            {
                ["client_id"] = actualClientId,
                ["refresh_token"] = _config.RefreshToken,
                ["grant_type"] = "refresh_token"
            };

            if (!string.IsNullOrWhiteSpace(actualSecret))
            {
                postData["client_secret"] = actualSecret;
            }

            using var content = new FormUrlEncodedContent(postData);
            using var response = await _httpClient.PostAsync(GoogleTokenEndpoint, content, ct);
            string responseJson = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                // Nếu refresh token bị thu hồi hoặc sai client_id
                if (responseJson.Contains("invalid_grant"))
                {
                    _config.AccessToken = string.Empty;
                    _config.RefreshToken = string.Empty;
                    SaveConfigToDisk();
                    NotifyStatusChanged("Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.");
                }
                return null;
            }

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("access_token", out var at))
            {
                _config.AccessToken = at.GetString() ?? "";
                int expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
                _config.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn - 120);
                SaveConfigToDisk();
                return _config.AccessToken;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Đăng xuất khỏi Google Drive.
    /// </summary>
    public void Logout()
    {
        _config.AccessToken = string.Empty;
        _config.RefreshToken = string.Empty;
        _config.UserEmail = string.Empty;
        _config.UserName = string.Empty;
        _config.UserPictureUrl = string.Empty;
        _config.CachedAppFolderId = string.Empty;
        SaveConfigToDisk();
        NotifyStatusChanged("Đã đăng xuất Google Drive.");
    }

    #endregion

    #region Google Drive Hierarchy Operations: \GMTPC\Comic Downloader GMTPC\

    /// <summary>
    /// Đảm bảo và lấy ID của thư mục chỉ định: \GMTPC\Comic Downloader GMTPC\
    /// </summary>
    public async Task<string?> EnsureTargetFolderAsync(CancellationToken ct = default)
    {
        string? token = await EnsureValidAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return null;

        // Nếu đã cache folder ID, kiểm tra tính hợp lệ nhanh
        if (!string.IsNullOrEmpty(_config.CachedAppFolderId))
        {
            try
            {
                using var checkReq = new HttpRequestMessage(HttpMethod.Get, $"{GoogleDriveApiBase}/files/{_config.CachedAppFolderId}?fields=id,name,trashed");
                checkReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var checkRes = await _httpClient.SendAsync(checkReq, ct);
                if (checkRes.IsSuccessStatusCode)
                {
                    string checkJson = await checkRes.Content.ReadAsStringAsync(ct);
                    using var checkDoc = JsonDocument.Parse(checkJson);
                    bool trashed = checkDoc.RootElement.TryGetProperty("trashed", out var tr) && tr.GetBoolean();
                    if (!trashed)
                    {
                        return _config.CachedAppFolderId;
                    }
                }

                // Nếu folder trên cloud đã bị xóa hoặc không còn hợp lệ, xóa cache để tạo lại
                _config.CachedAppFolderId = string.Empty;
                SaveConfigToDisk();
            }
            catch { }
        }

        // Bước 1: Tìm hoặc tạo folder cha 'GMTPC' trực thuộc 'root'
        string? gmtpcFolderId = await FindOrCreateFolderAsync("GMTPC", "root", token, ct);
        if (string.IsNullOrEmpty(gmtpcFolderId)) return null;

        // Bước 2: Tìm hoặc tạo folder con 'Comic Downloader GMTPC' trực thuộc 'GMTPC'
        string? appFolderId = await FindOrCreateFolderAsync(AppSubFolderName, gmtpcFolderId, token, ct);
        if (!string.IsNullOrEmpty(appFolderId))
        {
            _config.CachedAppFolderId = appFolderId;
            SaveConfigToDisk();
        }

        return appFolderId;
    }

    private async Task<string?> FindOrCreateFolderAsync(string folderName, string parentId, string token, CancellationToken ct)
    {
        try
        {
            // Query tìm folder
            string query = $"name = '{folderName.Replace("'", "\\'")}' and mimeType = 'application/vnd.google-apps.folder' and '{parentId}' in parents and trashed = false";
            string url = $"{GoogleDriveApiBase}/files?q={Uri.EscapeDataString(query)}&fields=files(id, name)&pageSize=1";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await _httpClient.SendAsync(req, ct);

            if (res.IsSuccessStatusCode)
            {
                string json = await res.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
                {
                    return files[0].GetProperty("id").GetString();
                }
            }

            // Nếu chưa có, tạo mới: với folder cha là root không truyền trường 'parents' để Google Drive v3 tự đặt ở root của user
            object payload = parentId == "root"
                ? new
                {
                    name = folderName,
                    mimeType = "application/vnd.google-apps.folder"
                }
                : new
                {
                    name = folderName,
                    mimeType = "application/vnd.google-apps.folder",
                    parents = new[] { parentId }
                };

            using var createReq = new HttpRequestMessage(HttpMethod.Post, $"{GoogleDriveApiBase}/files");
            createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            createReq.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var createRes = await _httpClient.SendAsync(createReq, ct);
            if (createRes.IsSuccessStatusCode)
            {
                string createdJson = await createRes.Content.ReadAsStringAsync(ct);
                using var createdDoc = JsonDocument.Parse(createdJson);
                return createdDoc.RootElement.GetProperty("id").GetString();
            }
            else
            {
                string err = await createRes.Content.ReadAsStringAsync(ct);
                Debug.WriteLine($"[GoogleDriveSync] Tạo thư mục '{folderName}' thất bại ({createRes.StatusCode}): {err}");
            }

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GoogleDriveSync] Ngoại lệ khi tạo/tìm thư mục '{folderName}': {ex.Message}");
            return null;
        }
    }

    #endregion

    #region File Upload & Download Operations

    /// <summary>
    /// Tìm file trong thư mục đích trên Google Drive.
    /// </summary>
    public async Task<GoogleDriveFileInfo> GetFileInfoAsync(string folderId, string fileName, CancellationToken ct = default)
    {
        string? token = await EnsureValidAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return new GoogleDriveFileInfo();

        try
        {
            string query = $"name = '{fileName.Replace("'", "\\'")}' and '{folderId}' in parents and trashed = false";
            string url = $"{GoogleDriveApiBase}/files?q={Uri.EscapeDataString(query)}&fields=files(id, name, modifiedTime, size)&pageSize=1";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await _httpClient.SendAsync(req, ct);

            if (res.IsSuccessStatusCode)
            {
                string json = await res.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
                {
                    var file = files[0];
                    string id = file.GetProperty("id").GetString() ?? "";
                    string name = file.GetProperty("name").GetString() ?? "";
                    DateTime? mod = null;
                    if (file.TryGetProperty("modifiedTime", out var mt) && DateTime.TryParse(mt.GetString(), out var dt))
                    {
                        mod = dt.ToUniversalTime();
                    }
                    long size = file.TryGetProperty("size", out var sz) && long.TryParse(sz.GetString(), out var s) ? s : 0;

                    return new GoogleDriveFileInfo
                    {
                        Id = id,
                        Name = name,
                        ModifiedTime = mod,
                        Size = size
                    };
                }
            }
        }
        catch { }

        return new GoogleDriveFileInfo();
    }

    private static string SanitizeMediaType(string? rawMimeType, string fallback = "text/plain")
    {
        if (string.IsNullOrWhiteSpace(rawMimeType)) return fallback;
        int idx = rawMimeType.IndexOf(';');
        string clean = (idx >= 0 ? rawMimeType[..idx] : rawMimeType).Trim();
        return string.IsNullOrWhiteSpace(clean) ? fallback : clean;
    }

    /// <summary>
    /// Tải lên hoặc cập nhật tệp tin trong thư mục đích trên Google Drive.
    /// </summary>
    public async Task<(bool Success, string FileId, string Message)> UploadOrUpdateFileAsync(
        string folderId,
        string fileName,
        string content,
        string mimeType = "text/markdown",
        CancellationToken ct = default)
    {
        string? token = await EnsureValidAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return (false, "", "Chưa đăng nhập hoặc không lấy được token Google Drive.");

        string cleanMime = SanitizeMediaType(mimeType, "text/plain");

        try
        {
            var existing = await GetFileInfoAsync(folderId, fileName, ct);

            if (existing.Exists)
            {
                // PATCH cập nhật nội dung file
                string uploadUrl = $"{GoogleDriveUploadBase}/files/{existing.Id}?uploadType=media";
                using var patchReq = new HttpRequestMessage(HttpMethod.Patch, uploadUrl);
                patchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                patchReq.Content = new StringContent(content, Encoding.UTF8, cleanMime);

                using var patchRes = await _httpClient.SendAsync(patchReq, ct);
                if (patchRes.IsSuccessStatusCode)
                {
                    return (true, existing.Id, $"Đã cập nhật tệp {fileName} trên Google Drive.");
                }

                string patchErr = await patchRes.Content.ReadAsStringAsync(ct);
                return (false, existing.Id, $"Lỗi cập nhật {fileName} ({patchRes.StatusCode}): {patchErr}");
            }
            else
            {
                // POST tạo mới bằng Multipart Upload (chuẩn multipart/related của Google Drive REST API v3)
                string boundary = "----GMTPCGoogleDriveBoundary" + DateTime.UtcNow.Ticks.ToString("x");
                string uploadUrl = $"{GoogleDriveUploadBase}/files?uploadType=multipart";

                var metadata = new
                {
                    name = fileName,
                    parents = new[] { folderId }
                };
                string metaJson = JsonSerializer.Serialize(metadata);

                var multipart = new MultipartContent("related", boundary);
                var metaContent = new StringContent(metaJson, Encoding.UTF8, "application/json");
                multipart.Add(metaContent);

                var fileContent = new StringContent(content, Encoding.UTF8, cleanMime);
                multipart.Add(fileContent);

                using var postReq = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
                postReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                postReq.Content = multipart;

                using var postRes = await _httpClient.SendAsync(postReq, ct);
                if (postRes.IsSuccessStatusCode)
                {
                    string resJson = await postRes.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(resJson);
                    string id = doc.RootElement.GetProperty("id").GetString() ?? "";
                    return (true, id, $"Đã tải lên tệp mới {fileName} vào thư mục {DefaultTargetFolderPath}");
                }

                string postErr = await postRes.Content.ReadAsStringAsync(ct);
                return (false, "", $"Lỗi tạo tệp {fileName} ({postRes.StatusCode}): {postErr}");
            }
        }
        catch (Exception ex)
        {
            return (false, "", $"Lỗi khi tải tệp {fileName} lên Google Drive: {ex.Message}");
        }
    }

    /// <summary>
    /// Tải nội dung tệp tin từ Google Drive.
    /// </summary>
    public async Task<(bool Success, string Content, DateTime? ModifiedTime, string Message)> DownloadFileContentAsync(
        string folderId,
        string fileName,
        CancellationToken ct = default)
    {
        string? token = await EnsureValidAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return (false, "", null, "Chưa đăng nhập Google Drive.");

        try
        {
            var file = await GetFileInfoAsync(folderId, fileName, ct);
            if (!file.Exists)
            {
                return (false, "", null, $"Không tìm thấy tệp {fileName} trong thư mục Google Drive.");
            }

            string downloadUrl = $"{GoogleDriveApiBase}/files/{file.Id}?alt=media";
            using var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var res = await _httpClient.SendAsync(req, ct);
            if (res.IsSuccessStatusCode)
            {
                string content = await res.Content.ReadAsStringAsync(ct);
                return (true, content, file.ModifiedTime, $"Đã tải thành công {fileName}.");
            }

            return (false, "", null, $"Không thể đọc tệp {fileName} ({res.StatusCode}).");
        }
        catch (Exception ex)
        {
            return (false, "", null, $"Lỗi tải tệp {fileName}: {ex.Message}");
        }
    }

    #endregion

    #region High-Level Sync Operations (Queue & Reader)

    /// <summary>
    /// Lấy báo cáo trạng thái chi tiết của đám mây Google Drive.
    /// </summary>
    public async Task<GoogleDriveSyncStatus> CheckCloudStatusAsync(CancellationToken ct = default)
    {
        var status = new GoogleDriveSyncStatus
        {
            IsConnected = IsConnected,
            AccountEmail = _config.UserEmail,
            AccountName = _config.UserName,
            TargetFolderPath = DefaultTargetFolderPath,
            LastSyncTimeUtc = _config.LastSyncUtc == DateTime.MinValue ? null : _config.LastSyncUtc
        };

        if (!IsConnected)
        {
            status.StatusMessage = "Chưa kết nối tài khoản Google Drive.";
            return status;
        }

        string? folderId = await EnsureTargetFolderAsync(ct);
        if (string.IsNullOrEmpty(folderId))
        {
            status.StatusMessage = "Không thể truy cập thư mục \\GMTPC\\Comic Downloader GMTPC\\ trên Google Drive.";
            return status;
        }

        status.TargetFolderId = folderId;
        status.QueueCloudFile = await GetFileInfoAsync(folderId, QueueFileName, ct);
        status.ReaderCloudFile = await GetFileInfoAsync(folderId, ReaderFileName, ct);
        status.ManifestCloudFile = await GetFileInfoAsync(folderId, ManifestFileName, ct);
        status.StatusMessage = "Đã kết nối và đồng bộ thông tin thư mục thành công.";

        return status;
    }

    /// <summary>
    /// Sao lưu Queue (Hàng chờ tải) hiện tại lên Google Drive (autosave.md).
    /// </summary>
    public async Task<(bool Success, string Message)> BackupQueueToCloudAsync(IEnumerable<ComicBookItem> items, CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            string? folderId = await EnsureTargetFolderAsync(ct);
            if (string.IsNullOrEmpty(folderId)) return (false, "Không thể mở hoặc tạo thư mục trên Google Drive.");

            string mdContent = QueueBackupService.Instance.BuildQueueMarkdownContent(items);
            var (ok, _, msg) = await UploadOrUpdateFileAsync(folderId, QueueFileName, mdContent, "text/markdown", ct);

            if (ok)
            {
                _config.LastSyncUtc = DateTime.UtcNow;
                SaveConfigToDisk();
                await UpdateSyncManifestAsync(folderId, ct);
                NotifyStatusChanged($"Đã sao lưu Queue lên Google Drive ({DateTime.Now:HH:mm:ss})");
            }

            return (ok, msg);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Phục hồi Queue (Hàng chờ tải) từ Google Drive về máy.
    /// </summary>
    public async Task<(bool Success, QueueRestoreResult? Result, string Message)> RestoreQueueFromCloudAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            string? folderId = await EnsureTargetFolderAsync(ct);
            if (string.IsNullOrEmpty(folderId)) return (false, null, "Không thể kết nối thư mục Google Drive.");

            var (ok, content, _, msg) = await DownloadFileContentAsync(folderId, QueueFileName, ct);
            if (!ok || string.IsNullOrWhiteSpace(content))
            {
                return (false, null, msg);
            }

            var result = QueueBackupService.Instance.ParseQueueWithMetadataFromMarkdownContent(content, "Google Drive:\\GMTPC\\Comic Downloader GMTPC\\autosave.md");
            return (true, result, $"Đã nhận {result.Items.Count} truyện từ Google Drive.");
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Sao lưu Dấu trang & Lịch sử đọc lên Google Drive (saved_reader_bookmarks.md).
    /// </summary>
    public async Task<(bool Success, string Message)> BackupReaderToCloudAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            string? folderId = await EnsureTargetFolderAsync(ct);
            if (string.IsNullOrEmpty(folderId)) return (false, "Không thể kết nối thư mục Google Drive.");

            string mdContent = ReadProgressStore.Instance.BuildReaderMarkdownContent();
            var (ok, _, msg) = await UploadOrUpdateFileAsync(folderId, ReaderFileName, mdContent, "text/markdown", ct);

            if (ok)
            {
                _config.LastSyncUtc = DateTime.UtcNow;
                SaveConfigToDisk();
                await UpdateSyncManifestAsync(folderId, ct);
                NotifyStatusChanged($"Đã sao lưu Bookmark & History lên Google Drive ({DateTime.Now:HH:mm:ss})");
            }

            return (ok, msg);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Phục hồi Dấu trang & Lịch sử đọc từ Google Drive về máy.
    /// </summary>
    public async Task<(bool Success, ReaderRestoreResult? Result, string Message)> RestoreReaderFromCloudAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            string? folderId = await EnsureTargetFolderAsync(ct);
            if (string.IsNullOrEmpty(folderId)) return (false, null, "Không thể kết nối thư mục Google Drive.");

            var (ok, content, _, msg) = await DownloadFileContentAsync(folderId, ReaderFileName, ct);
            if (!ok || string.IsNullOrWhiteSpace(content))
            {
                return (false, null, msg);
            }

            var result = ReadProgressStore.Instance.ParseReaderMarkdownContent(content, mergeWithExisting: true, loadedFromPath: "Google Drive:\\GMTPC\\Comic Downloader GMTPC\\saved_reader_bookmarks.md");
            return (true, result, $"Đã phục hồi {result.RestoredBookmarks} dấu trang và {result.RestoredHistory} lịch sử đọc từ Google Drive.");
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Đồng bộ 2 trong 1: Sao lưu toàn diện cả Queue tải và Reader lên Google Drive.
    /// </summary>
    public async Task<FullSyncResult> FullSyncUploadAsync(IEnumerable<ComicBookItem> queueItems, CancellationToken ct = default)
    {
        var result = new FullSyncResult();
        var (qOk, qMsg) = await BackupQueueToCloudAsync(queueItems, ct);
        result.QueueSynced = qOk;

        var (rOk, rMsg) = await BackupReaderToCloudAsync(ct);
        result.ReaderSynced = rOk;

        result.Success = qOk && rOk;
        if (result.Success)
        {
            result.Message = $"Đã sao lưu thành công cả Hàng chờ tải và Dấu trang / Lịch sử đọc lên Google Drive ({DefaultTargetFolderPath}).";
        }
        else
        {
            result.Message = $"Sao lưu một phần: Queue ({ (qOk ? "OK" : qMsg) }), Reader ({ (rOk ? "OK" : rMsg) })";
        }
        return result;
    }

    /// <summary>
    /// Phục hồi toàn diện cả Queue và Reader từ Google Drive.
    /// </summary>
    public async Task<(FullSyncResult Result, QueueRestoreResult? QueueResult, ReaderRestoreResult? ReaderResult)> FullSyncRestoreAsync(CancellationToken ct = default)
    {
        var result = new FullSyncResult();
        var (qOk, qResult, qMsg) = await RestoreQueueFromCloudAsync(ct);
        result.QueueSynced = qOk;
        if (qResult != null) result.QueueItemCount = qResult.Items.Count;

        var (rOk, rResult, rMsg) = await RestoreReaderFromCloudAsync(ct);
        result.ReaderSynced = rOk;
        if (rResult != null)
        {
            result.RestoredBookmarks = rResult.RestoredBookmarks;
            result.RestoredHistory = rResult.RestoredHistory;
        }

        result.Success = qOk || rOk;
        result.Message = result.Success
            ? $"Phục hồi từ Google Drive thành công: {result.QueueItemCount} truyện trong Queue, {result.RestoredBookmarks} dấu trang, {result.RestoredHistory} lịch sử đọc."
            : $"Phục hồi thất bại: {qMsg} | {rMsg}";

        return (result, qResult, rResult);
    }

    private async Task UpdateSyncManifestAsync(string folderId, CancellationToken ct)
    {
        try
        {
            var manifest = new
            {
                app = "Comic Downloader GMTPC",
                version = "1.0",
                device = Environment.MachineName,
                os = Environment.OSVersion.ToString(),
                synced_at_utc = DateTime.UtcNow.ToString("o"),
                target_folder = DefaultTargetFolderPath
            };
            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            await UploadOrUpdateFileAsync(folderId, ManifestFileName, json, "application/json", ct);
        }
        catch { }
    }

    /// <summary>
    /// Kích hoạt tự động đồng bộ ngầm có debounce (30 giây) khi có thay đổi.
    /// </summary>
    public void TriggerDebouncedAutoSync(Func<IEnumerable<ComicBookItem>> getQueueItems)
    {
        if (!IsConnected || !AutoSyncEnabled) return;

        lock (this)
        {
            _hasPendingAutoSync = true;
            if (_autoSyncDebounceTimer == null)
            {
                _autoSyncDebounceTimer = new System.Threading.Timer(async _ =>
                {
                    bool shouldSync = false;
                    lock (this)
                    {
                        shouldSync = _hasPendingAutoSync;
                        _hasPendingAutoSync = false;
                    }
                    if (shouldSync)
                    {
                        try
                        {
                            var items = getQueueItems();
                            await FullSyncUploadAsync(items, CancellationToken.None);
                        }
                        catch { }
                    }
                }, null, 30000, Timeout.Infinite);
            }
            else
            {
                _autoSyncDebounceTimer.Change(30000, Timeout.Infinite);
            }
        }
    }

    private void NotifyStatusChanged(string message)
    {
        try
        {
            var status = new GoogleDriveSyncStatus
            {
                IsConnected = IsConnected,
                AccountEmail = _config.UserEmail,
                AccountName = _config.UserName,
                TargetFolderPath = DefaultTargetFolderPath,
                TargetFolderId = _config.CachedAppFolderId,
                LastSyncTimeUtc = _config.LastSyncUtc == DateTime.MinValue ? null : _config.LastSyncUtc,
                StatusMessage = message
            };
            StatusChanged?.Invoke(status);
        }
        catch { }
    }

    #endregion
}

