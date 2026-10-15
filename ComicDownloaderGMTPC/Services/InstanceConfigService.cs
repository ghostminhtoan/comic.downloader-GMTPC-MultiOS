using System;
using System.IO;
using System.Text.Json;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Quản lý cấu hình Multi-instance của ứng dụng chính (Comic Downloader GMTPC).
/// Mặc định là TẮT (Single-instance: chỉ cho phép 1 cửa sổ tải truyện duy nhất).
/// </summary>
public sealed class InstanceConfigService
{
    private static readonly Lazy<InstanceConfigService> _lazy = new(() => new InstanceConfigService());
    public static InstanceConfigService Instance => _lazy.Value;

    private readonly string _configFilePath;
    private bool _isMultiInstanceEnabled = false;

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

    private InstanceConfigService()
    {
        try
        {
            string portableDir = Path.Combine(AppContext.BaseDirectory, ".portable");
            Directory.CreateDirectory(portableDir);
            _configFilePath = Path.Combine(portableDir, "instance_config.json");
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
            }
            else
            {
                _isMultiInstanceEnabled = false; // Mặc định là TẮT
            }
        }
        catch
        {
            _isMultiInstanceEnabled = false;
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

            var model = new { IsMultiInstanceEnabled = _isMultiInstanceEnabled };
            string json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configFilePath, json);
        }
        catch { }
    }
}

