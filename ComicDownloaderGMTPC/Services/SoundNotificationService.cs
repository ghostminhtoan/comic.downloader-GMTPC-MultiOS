using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace ComicDownloaderGMTPC.Services;

public enum SoundNotificationType
{
    Startup,
    DownloadFinish,
    DownloadError
}

public class SoundNotificationService
{
    private static readonly Lazy<SoundNotificationService> _lazy = new(() => new SoundNotificationService());
    public static SoundNotificationService Instance => _lazy.Value;

    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Action cho Native Android audio player
    public static Action<SoundNotificationType, string?>? AndroidPlaySoundHandler { get; set; }

    public static string RingtonesFolder
    {
        get
        {
            string baseFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComicDownloaderGMTPC", "ringtones");
            try
            {
                if (!Directory.Exists(baseFolder)) Directory.CreateDirectory(baseFolder);
            }
            catch { }
            return baseFolder;
        }
    }

    private static readonly (SoundNotificationType Type, string FileName, string Url)[] SoundMappings = new[]
    {
        (SoundNotificationType.Startup, "Startup.wav", "https://github.com/ghostminhtoan/comic.downloader.gmtpc/releases/download/accessories/ringtones-Startup.wav"),
        (SoundNotificationType.DownloadFinish, "download-finish.wav", "https://github.com/ghostminhtoan/comic.downloader.gmtpc/releases/download/accessories/ringtones-download-finish.wav"),
        (SoundNotificationType.DownloadError, "error.wav", "https://github.com/ghostminhtoan/comic.downloader.gmtpc/releases/download/accessories/ringtones-error.wav")
    };

    /// <summary>
    /// Phát âm thanh thông báo bất đồng bộ theo loại âm thanh.
    /// </summary>
    public void PlaySound(SoundNotificationType soundType)
    {
        Task.Run(async () =>
        {
            try
            {
                string? soundPath = await EnsureSoundFileAsync(soundType);

                // 1. Nếu trên Android và có handler đăng ký
                if (AndroidPlaySoundHandler != null)
                {
                    AndroidPlaySoundHandler.Invoke(soundType, soundPath);
                    return;
                }

                // 2. Trên Windows
                if (OperatingSystem.IsWindows())
                {
                    if (!string.IsNullOrEmpty(soundPath) && File.Exists(soundPath))
                    {
                        PlayWindowsWav(soundPath);
                    }
                    else
                    {
                        PlayWindowsFallback(soundType);
                    }
                    return;
                }

                // 3. Trên Linux
                if (OperatingSystem.IsLinux())
                {
                    if (!string.IsNullOrEmpty(soundPath) && File.Exists(soundPath))
                    {
                        PlayLinuxWav(soundPath);
                    }
                    else
                    {
                        Console.Beep();
                    }
                    return;
                }

                // Fallback chung
                Console.Beep();
            }
            catch
            {
                // Âm thanh phụ trợ không bao giờ làm gián đoạn luồng chính
            }
        });
    }

    public void PlayStartup() => PlaySound(SoundNotificationType.Startup);
    public void PlayDownloadFinish() => PlaySound(SoundNotificationType.DownloadFinish);
    public void PlayDownloadError() => PlaySound(SoundNotificationType.DownloadError);

    private async Task<string?> EnsureSoundFileAsync(SoundNotificationType type)
    {
        try
        {
            foreach (var item in SoundMappings)
            {
                if (item.Type == type)
                {
                    string localPath = Path.Combine(RingtonesFolder, item.FileName);
                    if (File.Exists(localPath) && new FileInfo(localPath).Length > 1000)
                    {
                        return localPath;
                    }

                    // Tải ngầm nếu chưa có
                    try
                    {
                        var data = await _httpClient.GetByteArrayAsync(item.Url);
                        if (data != null && data.Length > 0)
                        {
                            await File.WriteAllBytesAsync(localPath, data);
                            return localPath;
                        }
                    }
                    catch
                    {
                        // Bỏ qua nếu không có mạng
                    }
                    return localPath;
                }
            }
        }
        catch { }
        return null;
    }

    private static void PlayWindowsWav(string filePath)
    {
        try
        {
            // Dùng PowerShell hoặc Windows API phát WAV chuẩn
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-c \"(New-Object Media.SoundPlayer '{filePath}').PlaySync()\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch
        {
            if (OperatingSystem.IsWindows())
            {
                Console.Beep();
            }
        }
    }

    private static void PlayWindowsFallback(SoundNotificationType type)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            switch (type)
            {
                case SoundNotificationType.Startup:
                    Console.Beep(800, 150);
                    Console.Beep(1200, 200);
                    break;
                case SoundNotificationType.DownloadFinish:
                    Console.Beep(1000, 120);
                    Console.Beep(1500, 250);
                    break;
                case SoundNotificationType.DownloadError:
                    Console.Beep(400, 300);
                    break;
            }
        }
        catch { }
    }

    private static void PlayLinuxWav(string filePath)
    {
        try
        {
            string[] players = { "paplay", "aplay", "pw-play" };
            foreach (var player in players)
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = player,
                        Arguments = $"\"{filePath}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        proc.WaitForExit(3000);
                        return;
                    }
                }
                catch { }
            }
        }
        catch { }
    }
}
