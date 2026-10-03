using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class MainViewModel
{
    // ==========================================
    // PASSWORD MANAGER OBSERVABLE PROPERTIES
    // ==========================================

    [ObservableProperty]
    private string _damconuongUsername = string.Empty;

    [ObservableProperty]
    private string _damconuongPassword = string.Empty;

    [ObservableProperty]
    private bool _isDamconuongPasswordVisible = false;

    [ObservableProperty]
    private string _mangadexUsername = string.Empty;

    [ObservableProperty]
    private string _mangadexPassword = string.Empty;

    [ObservableProperty]
    private bool _isMangadexPasswordVisible = false;

    [ObservableProperty]
    private string _passwordManagerStatusText = string.Empty;

    private readonly Dictionary<string, PasswordManagerEntry> _passwordManagerEntries = new(StringComparer.OrdinalIgnoreCase);

    // ==========================================
    // PASSWORD MANAGER COMMANDS
    // ==========================================

    [RelayCommand]
    public void TogglePasswordVisibility(string domain)
    {
        if (string.Equals(domain, "damconuong.shop", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(domain, "damconuong", StringComparison.OrdinalIgnoreCase))
        {
            IsDamconuongPasswordVisible = !IsDamconuongPasswordVisible;
        }
        else if (string.Equals(domain, "mangadex.org", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(domain, "mangadex", StringComparison.OrdinalIgnoreCase))
        {
            IsMangadexPasswordVisible = !IsMangadexPasswordVisible;
        }
    }

    [RelayCommand]
    public async Task ApplyPasswordAsync(string domain)
    {
        try
        {
            SyncUiToPasswordManagerEntries();
            SavePasswordManagerSettings();

            if (string.Equals(domain, "damconuong.shop", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(domain, "damconuong", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(domain, "damconuong.pet", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(DamconuongUsername) || string.IsNullOrWhiteSpace(DamconuongPassword))
                {
                    PasswordManagerStatusText = CurrentLanguage == "VI" ? "Thiếu username hoặc password cho Dâm Cô Nương." : "Missing username or password for Damconuong.";
                    return;
                }

                ComicScraperService.Instance.SetDamconuongCredentials(DamconuongUsername, DamconuongPassword);

                PasswordManagerStatusText = CurrentLanguage == "VI" ? "Đang kiểm tra redirect & đăng nhập damconuong..." : "Probing redirect & logging in to damconuong...";
                await EnsureDamconuongRedirectDomainAsync().ConfigureAwait(false);

                string baseDomain = string.IsNullOrWhiteSpace(DomainDamconuongRedirectDomain) ? "https://damconuong.pet" : DomainDamconuongRedirectDomain;
                bool loginSuccess = await ComicScraperService.Instance.LoginDamconuongAsync(baseDomain, DamconuongUsername, DamconuongPassword).ConfigureAwait(false);

                if (!loginSuccess && !baseDomain.Contains("shop", StringComparison.OrdinalIgnoreCase))
                {
                    loginSuccess = await ComicScraperService.Instance.LoginDamconuongAsync("https://damconuong.shop", DamconuongUsername, DamconuongPassword).ConfigureAwait(false);
                }

                if (loginSuccess)
                {
                    PasswordManagerStatusText = CurrentLanguage == "VI"
                        ? $"Đã đăng nhập tài khoản '{DamconuongUsername}' cho {baseDomain} thành công!"
                        : $"Successfully logged in as '{DamconuongUsername}' for {baseDomain}!";
                    AddLog("SUCCESS", $"[damconuong] Đã đăng nhập tài khoản '{DamconuongUsername}' thành công ({baseDomain})");
                }
                else
                {
                    PasswordManagerStatusText = CurrentLanguage == "VI"
                        ? $"Đã lưu thông tin tài khoản '{DamconuongUsername}' cho Dâm Cô Nương (sẽ tự động đăng nhập khi tải)."
                        : $"Saved credentials for '{DamconuongUsername}' (will auto-login on download).";
                    AddLog("INFO", "Password Manager: Đã lưu thông tin tài khoản cho damconuong.");
                }
            }
            else if (string.Equals(domain, "mangadex.org", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(domain, "mangadex", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(MangadexUsername) || string.IsNullOrWhiteSpace(MangadexPassword))
                {
                    PasswordManagerStatusText = CurrentLanguage == "VI" ? "Thiếu username hoặc password cho MangaDex." : "Missing username or password for MangaDex.";
                    return;
                }

                PasswordManagerStatusText = CurrentLanguage == "VI" ? "Đang áp dụng tài khoản cho mangadex.org..." : "Applying credentials for mangadex.org...";
                await Task.Delay(200);
                PasswordManagerStatusText = CurrentLanguage == "VI" ? "Đã lưu và áp dụng thông tin đăng nhập mangadex.org thành công!" : "Successfully saved and applied credentials for mangadex.org!";
                AddLog("INFO", "Password Manager: Đã áp dụng tài khoản cho mangadex.org.");
            }
        }
        catch (Exception ex)
        {
            PasswordManagerStatusText = (CurrentLanguage == "VI" ? "Lỗi áp dụng mật khẩu: " : "Error applying password: ") + ex.Message;
            AddLog("ERROR", $"Password Manager error: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task ApplyAllPasswordsAsync()
    {
        try
        {
            SyncUiToPasswordManagerEntries();
            SavePasswordManagerSettings();

            int applied = 0;
            if (!string.IsNullOrWhiteSpace(DamconuongUsername) && !string.IsNullOrWhiteSpace(DamconuongPassword))
            {
                ComicScraperService.Instance.SetDamconuongCredentials(DamconuongUsername, DamconuongPassword);
                applied++;
            }
            if (!string.IsNullOrWhiteSpace(MangadexUsername) && !string.IsNullOrWhiteSpace(MangadexPassword))
            {
                applied++;
            }

            await Task.Delay(200);
            PasswordManagerStatusText = CurrentLanguage == "VI"
                ? $"Đã áp dụng toàn bộ mật khẩu ({applied} domain có thông tin) và lưu vào autosave_password.md!"
                : $"Applied all credentials ({applied} domains) and saved to autosave_password.md!";
            AddLog("INFO", $"Password Manager: {PasswordManagerStatusText}");
        }
        catch (Exception ex)
        {
            PasswordManagerStatusText = (CurrentLanguage == "VI" ? "Lỗi áp dụng: " : "Error applying all: ") + ex.Message;
        }
    }

    [RelayCommand]
    public async Task ImportPasswordsAsync()
    {
        try
        {
            string settingsPath = GetPasswordManagerSettingsPath();
            if (!File.Exists(settingsPath))
            {
                PasswordManagerStatusText = CurrentLanguage == "VI" ? $"Không tìm thấy file: {settingsPath}" : $"File not found: {settingsPath}";
                return;
            }

            string content = await File.ReadAllTextAsync(settingsPath, Encoding.UTF8);
            LoadPasswordManagerSettingsFromMarkdown(content);
            SyncPasswordManagerEntriesToUi();

            PasswordManagerStatusText = CurrentLanguage == "VI" ? "Đã nạp thành công thông tin mật khẩu từ autosave_password.md!" : "Successfully imported credentials from autosave_password.md!";
            AddLog("INFO", "Password Manager: Đã nạp thông tin từ autosave_password.md.");
        }
        catch (Exception ex)
        {
            PasswordManagerStatusText = (CurrentLanguage == "VI" ? "Lỗi Import: " : "Import error: ") + ex.Message;
        }
    }

    [RelayCommand]
    public async Task ExportPasswordsAsync()
    {
        try
        {
            SyncUiToPasswordManagerEntries();
            SavePasswordManagerSettings();
            string path = GetPasswordManagerSettingsPath();
            PasswordManagerStatusText = CurrentLanguage == "VI" ? $"Đã xuất cấu hình mật khẩu ra: {path}" : $"Exported passwords to: {path}";
            AddLog("INFO", $"Password Manager: Đã lưu vào {path}");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            PasswordManagerStatusText = (CurrentLanguage == "VI" ? "Lỗi Export: " : "Export error: ") + ex.Message;
        }
    }

    // ==========================================
    // PASSWORD STORAGE & PERSISTENCE
    // ==========================================

    private string GetPasswordManagerSettingsPath()
    {
        string dir = ComicScraperService.GetConfigDirectory();
        if (!OperatingSystem.IsAndroid())
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string portableDir = Path.Combine(baseDir, ".portable");
            if (Directory.Exists(portableDir))
            {
                dir = portableDir;
            }
            else
            {
                try { Directory.CreateDirectory(portableDir); dir = portableDir; } catch { }
            }
        }
        return Path.Combine(dir, "autosave_password.md");
    }

    public void LoadPasswordManagerSettings()
    {
        try
        {
            string path = GetPasswordManagerSettingsPath();
            if (File.Exists(path))
            {
                string content = File.ReadAllText(path, Encoding.UTF8);
                LoadPasswordManagerSettingsFromMarkdown(content);
                SyncPasswordManagerEntriesToUi();

                if (!string.IsNullOrWhiteSpace(DamconuongUsername) && !string.IsNullOrWhiteSpace(DamconuongPassword))
                {
                    ComicScraperService.Instance.SetDamconuongCredentials(DamconuongUsername, DamconuongPassword);
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Không thể nạp autosave_password.md: {ex.Message}");
        }
    }

    public void SavePasswordManagerSettings()
    {
        try
        {
            string path = GetPasswordManagerSettingsPath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, BuildPasswordManagerMarkdown(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Không thể ghi autosave_password.md: {ex.Message}");
        }
    }

    private void SyncUiToPasswordManagerEntries()
    {
        _passwordManagerEntries["damconuong.shop"] = new PasswordManagerEntry
        {
            Username = DamconuongUsername?.Trim() ?? string.Empty,
            Password = DamconuongPassword ?? string.Empty
        };

        _passwordManagerEntries["mangadex.org"] = new PasswordManagerEntry
        {
            Username = MangadexUsername?.Trim() ?? string.Empty,
            Password = MangadexPassword ?? string.Empty
        };
    }

    private void SyncPasswordManagerEntriesToUi()
    {
        if (_passwordManagerEntries.TryGetValue("damconuong.shop", out var damconuong))
        {
            DamconuongUsername = damconuong.Username ?? string.Empty;
            DamconuongPassword = damconuong.Password ?? string.Empty;
        }

        if (_passwordManagerEntries.TryGetValue("mangadex.org", out var mangadex))
        {
            MangadexUsername = mangadex.Username ?? string.Empty;
            MangadexPassword = mangadex.Password ?? string.Empty;
        }
    }

    private string BuildPasswordManagerMarkdown()
    {
        var lines = new List<string>
        {
            "# autosave_password",
            string.Empty
        };

        foreach (var pair in _passwordManagerEntries
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"## {pair.Key}");
            lines.Add($"- Username: {pair.Value?.Username ?? string.Empty}");
            lines.Add($"- Password: {pair.Value?.Password ?? string.Empty}");
            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void LoadPasswordManagerSettingsFromMarkdown(string content)
    {
        _passwordManagerEntries.Clear();

        string currentDomain = string.Empty;
        foreach (string rawLine in (content ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            string line = rawLine?.Trim() ?? string.Empty;
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                currentDomain = line.Substring(3).Trim();
                if (!string.IsNullOrWhiteSpace(currentDomain) && !_passwordManagerEntries.ContainsKey(currentDomain))
                {
                    _passwordManagerEntries[currentDomain] = new PasswordManagerEntry();
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(currentDomain) || !_passwordManagerEntries.ContainsKey(currentDomain))
            {
                continue;
            }

            if (line.StartsWith("- Username:", StringComparison.OrdinalIgnoreCase))
            {
                _passwordManagerEntries[currentDomain].Username = line.Substring("- Username:".Length).Trim();
            }
            else if (line.StartsWith("- Password:", StringComparison.OrdinalIgnoreCase))
            {
                _passwordManagerEntries[currentDomain].Password = line.Substring("- Password:".Length).Trim();
            }
        }
    }
}

public class PasswordManagerEntry
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
