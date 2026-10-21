using System;
using System.IO;
using System.Text.Json;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Quản lý cấu hình toàn cục của ứng dụng (Multi-instance, Chế độ tải thủ công: Trực tiếp / Nhúng).
/// </summary>
public sealed class InstanceConfigService
{
    private static readonly Lazy<InstanceConfigService> _lazy = new(() => new InstanceConfigService());
    public static InstanceConfigService Instance => _lazy.Value;

    private readonly string _configFilePath;
    private bool _isMultiInstanceEnabled = false;
    private bool _isManualDownloadMode = false;
    private string _manualDownloadMode = "Direct"; // "Direct" (Trực tiếp) hoặc "Embedded" (Nhúng)

    public bool IsMultiInstanceEnabled
    {
        get => _isMultiInstanceEnabled;
        set
        {
            if (_isMultiInstanceEnabled != value)
            {
                _isMultiInstanceEnabled = value;
                SaveConfig();
            }
        }
    }

    /// <summary>
    /// Checkbox bật/tắt chế độ tải thủ công (mặc định là tắt: false).
    /// Khi tắt: Tải mặc định theo như app đang có.
    /// Khi bật: Bất kể web gì cũng sẽ tự tải theo chế độ đã cài sẵn (ManualDownloadMode).
    /// </summary>
    public bool IsManualDownloadMode
    {
        get => _isManualDownloadMode;
        set
        {
            if (_isManualDownloadMode != value)
            {
                _isManualDownloadMode = value;
                SaveConfig();
            }
        }
    }

    /// <summary>
    /// Chế độ tải cài sẵn: "Direct" (Trực tiếp) hoặc "Embedded" (Nhúng).
    /// </summary>
    public string ManualDownloadMode
    {
        get => _manualDownloadMode;
        set
        {
            string normalized = string.Equals(value, "Embedded", StringComparison.OrdinalIgnoreCase) ? "Embedded" : "Direct";
            if (_manualDownloadMode != normalized)
            {
                _manualDownloadMode = normalized;
                SaveConfig();
            }
        }
    }

    private InstanceConfigService()
    {
        try
        {
            string configDir = ComicScraperService.GetConfigDirectory();
            _configFilePath = Path.Combine(configDir, "instance_config.json");
            LoadConfig();
        }
        catch
        {
            _configFilePath = Path.Combine(AppContext.BaseDirectory, "instance_config.json");
        }
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                string json = File.ReadAllText(_configFilePath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("IsMultiInstanceEnabled", out var prop))
                {
                    _isMultiInstanceEnabled = prop.GetBoolean();
                }
                if (doc.RootElement.TryGetProperty("IsManualDownloadMode", out var propManual))
                {
                    _isManualDownloadMode = propManual.GetBoolean();
                }
                if (doc.RootElement.TryGetProperty("ManualDownloadMode", out var propMode))
                {
                    string? modeStr = propMode.GetString();
                    _manualDownloadMode = string.Equals(modeStr, "Embedded", StringComparison.OrdinalIgnoreCase) ? "Embedded" : "Direct";
                }
            }
            else
            {
                _isMultiInstanceEnabled = false; // Mặc định là TẮT
                _isManualDownloadMode = false;   // Mặc định là TẮT
                _manualDownloadMode = "Direct";
            }
        }
        catch
        {
            _isMultiInstanceEnabled = false;
            _isManualDownloadMode = false;
            _manualDownloadMode = "Direct";
        }
    }

    private void SaveConfig()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var model = new
            {
                IsMultiInstanceEnabled = _isMultiInstanceEnabled,
                IsManualDownloadMode = _isManualDownloadMode,
                ManualDownloadMode = _manualDownloadMode
            };
            string json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configFilePath, json);
        }
        catch { }
    }
}

