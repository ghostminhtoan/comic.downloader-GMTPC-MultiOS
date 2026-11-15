using System;
using System.Diagnostics;
using System.IO;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Giao diện điều khiển audio native đồng bộ cho mọi nền tảng
/// </summary>
public interface INativeAudioPlayerInstance : IDisposable
{
    void Pause();
    void Resume();
    void Seek(double seconds);
    void Stop();
}

/// <summary>
/// Dịch vụ phát âm thanh video native đa nền tảng (Windows, Android, Linux):
/// - Trên Windows: Sử dụng Windows Media Player OCX COM siêu nhẹ có sẵn trên 100% máy Windows (0 dependency).
/// - Trên Android: Tận dụng Android.Media.MediaPlayer native của hệ điều hành.
/// - Trên Linux: Tận dụng ffplay / mpv / paplay chạy ngầm.
/// </summary>
public sealed class NativeAudioPlaybackService : IDisposable
{
    private static readonly object _lock = new();

    // Android provider delegates
    public static Func<string, double, INativeAudioPlayerInstance?>? ExternalAndroidAudioPlayerProvider { get; set; }

    private object? _wmpRaw;
    private INativeAudioPlayerInstance? _androidPlayer;
    private Process? _linuxProcess;
    private bool _isDisposed;
    private bool _isPaused;

    public bool IsPlaying { get; private set; }

    public static NativeAudioPlaybackService CreateAndPlay(string filePath, double startSeconds = 0)
    {
        var service = new NativeAudioPlaybackService();
        service.Play(filePath, startSeconds);
        return service;
    }

    public void Play(string filePath, double startSeconds = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

        lock (_lock)
        {
            Stop();
            _isDisposed = false;
            _isPaused = false;

            // 1. Windows: Sử dụng WMPlayer.OCX COM có sẵn 100% trên mọi Windows
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var wmpType = Type.GetTypeFromProgID("WMPlayer.OCX");
                    if (wmpType != null)
                    {
                        var inst = Activator.CreateInstance(wmpType);
                        if (inst != null)
                        {
                            _wmpRaw = inst;
                            dynamic wmp = inst;
                            try { wmp.settings.autoStart = true; } catch { }
                            try { wmp.settings.volume = 100; } catch { }
                            wmp.URL = filePath;
                            if (startSeconds > 0)
                            {
                                try
                                {
                                    wmp.controls.currentPosition = startSeconds;
                                }
                                catch { }
                            }
                            try { wmp.controls.play(); } catch { }
                            IsPlaying = true;
                            return;
                        }
                    }
                }
                catch { }
            }

            // 2. Android: Gọi qua Android Native Media Player Provider
            if (OperatingSystem.IsAndroid() && ExternalAndroidAudioPlayerProvider != null)
            {
                try
                {
                    _androidPlayer = ExternalAndroidAudioPlayerProvider(filePath, startSeconds);
                    IsPlaying = _androidPlayer != null;
                    return;
                }
                catch { }
            }

            // 3. Linux: Dùng ffplay / mpv chạy ngầm không hiển thị hình
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    string secStr = startSeconds > 0 ? $"-ss {startSeconds:F2}" : "";
                    var psi = new ProcessStartInfo
                    {
                        FileName = "ffplay",
                        Arguments = $"-nodisp -autoexit {secStr} -i \"{filePath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    _linuxProcess = Process.Start(psi);
                    IsPlaying = _linuxProcess != null && !_linuxProcess.HasExited;
                }
                catch { }
            }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!IsPlaying || _isPaused) return;

            if (OperatingSystem.IsWindows() && _wmpRaw != null)
            {
                try
                {
                    dynamic wmp = _wmpRaw;
                    wmp.controls.pause();
                    _isPaused = true;
                }
                catch { }
            }

            if (_androidPlayer != null)
            {
                try
                {
                    _androidPlayer.Pause();
                    _isPaused = true;
                }
                catch { }
            }
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_isPaused)
            {
                if (OperatingSystem.IsWindows() && _wmpRaw != null)
                {
                    try
                    {
                        dynamic wmp = _wmpRaw;
                        wmp.controls.play();
                        _isPaused = false;
                    }
                    catch { }
                }

                if (_androidPlayer != null)
                {
                    try
                    {
                        _androidPlayer.Resume();
                        _isPaused = false;
                    }
                    catch { }
                }
            }
        }
    }

    public void Seek(double seconds)
    {
        lock (_lock)
        {
            if (OperatingSystem.IsWindows() && _wmpRaw != null)
            {
                try
                {
                    dynamic wmp = _wmpRaw;
                    wmp.controls.currentPosition = Math.Max(0, seconds);
                }
                catch { }
            }

            if (_androidPlayer != null)
            {
                try
                {
                    _androidPlayer.Seek(seconds);
                }
                catch { }
            }
        }
    }

    public double CurrentPositionSeconds
    {
        get
        {
            lock (_lock)
            {
                if (OperatingSystem.IsWindows() && _wmpRaw != null)
                {
                    try
                    {
                        dynamic wmp = _wmpRaw;
                        return (double)wmp.controls.currentPosition;
                    }
                    catch { }
                }
                return 0;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            IsPlaying = false;
            _isPaused = false;

            if (OperatingSystem.IsWindows() && _wmpRaw != null)
            {
                try
                {
                    dynamic wmp = _wmpRaw;
                    wmp.controls.stop();
                    wmp.close();
                }
                catch { }
                _wmpRaw = null;
            }

            if (_androidPlayer != null)
            {
                try
                {
                    _androidPlayer.Stop();
                    _androidPlayer.Dispose();
                }
                catch { }
                _androidPlayer = null;
            }

            if (_linuxProcess != null)
            {
                try
                {
                    if (!_linuxProcess.HasExited) _linuxProcess.Kill();
                    _linuxProcess.Dispose();
                }
                catch { }
                _linuxProcess = null;
            }
        }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            Stop();
        }
    }
}
