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
    double CurrentPositionSeconds { get; }
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
    private MpvVideoPlayerService? _mpvAudioPlayer;
    private INativeAudioPlayerInstance? _androidPlayer;
    private Process? _linuxProcess;
    private bool _isDisposed;
    private bool _isPaused;
    private int _volume = 100;
    private bool _isMuted = false;
    private double _playbackRate = 1.0;
    private readonly Stopwatch _clock = new();
    private double _clockOffsetSec;

    public bool IsPlaying { get; private set; }
    public int Volume => _volume;
    public bool IsMuted => _isMuted;
    public double PlaybackRate => _playbackRate;

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

            // 0. ƯU TIÊN: Nếu hệ thống hỗ trợ LibMpv (Windows / Linux), dùng MPV để phát âm thanh cho mọi định dạng (.webm, .mp4, .mkv, .ts...)
            // Đặc biệt là .webm (loại bỏ hoàn toàn modal popup WMP và crash 0xC0000005)
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            bool isWebm = ext == ".webm";

            if (MpvVideoPlayerService.IsSupported)
            {
                try
                {
                    var mpv = MpvVideoPlayerService.Create(filePath);
                    if (mpv != null)
                    {
                        _mpvAudioPlayer = mpv;
                        mpv.SetVolume(_isMuted ? 0 : _volume);
                        mpv.SetMute(_isMuted);
                        mpv.SetSpeed(_playbackRate);
                        if (startSeconds > 0)
                        {
                            mpv.Seek((long)(startSeconds * 1000));
                        }
                        mpv.Play();
                        IsPlaying = true;
                        _clockOffsetSec = Math.Max(0, startSeconds);
                        _clock.Restart();
                        return;
                    }
                }
                catch { }
            }

            // 1. Windows: Sử dụng WMPlayer.OCX COM ở chế độ Headless / Windowless (CHỈ DÀNH CHO CÁC ĐỊNH DẠNG WMP HỖ TRỢ, TUYỆT ĐỐI KHÔNG GỌI VỚI .WEBM)
            if (OperatingSystem.IsWindows() && !isWebm)
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
                            try { wmp.windowlessVideo = true; } catch { }
                            try { wmp.uiMode = "invisible"; } catch { }
                            try { wmp.settings.autoStart = true; } catch { }
                            try { wmp.settings.volume = _isMuted ? 0 : _volume; } catch { }
                            try { wmp.settings.mute = _isMuted; } catch { }
                            try { wmp.settings.rate = _playbackRate; } catch { }
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
                            _clockOffsetSec = Math.Max(0, startSeconds);
                            _clock.Restart();
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
                    if (IsPlaying)
                    {
                        _clockOffsetSec = Math.Max(0, startSeconds);
                        _clock.Restart();
                    }
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
                    if (IsPlaying)
                    {
                        _clockOffsetSec = Math.Max(0, startSeconds);
                        _clock.Restart();
                    }
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

            if (_mpvAudioPlayer != null)
            {
                try { _mpvAudioPlayer.Pause(); } catch { }
                _isPaused = true;
            }

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

            _isPaused = true;
            _clock.Stop();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_isPaused)
            {
                if (_mpvAudioPlayer != null)
                {
                    try { _mpvAudioPlayer.Play(); } catch { }
                    _isPaused = false;
                }

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

                _isPaused = false;
                _clock.Start();
            }
        }
    }

    public void Seek(double seconds)
    {
        lock (_lock)
        {
            double safeSec = Math.Max(0, seconds);
            if (_mpvAudioPlayer != null)
            {
                try { _mpvAudioPlayer.Seek((long)(safeSec * 1000)); } catch { }
            }

            if (OperatingSystem.IsWindows() && _wmpRaw != null)
            {
                try
                {
                    dynamic wmp = _wmpRaw;
                    wmp.controls.currentPosition = safeSec;
                }
                catch { }
            }

            if (_androidPlayer != null)
            {
                try
                {
                    _androidPlayer.Seek(safeSec);
                }
                catch { }
            }

            _clockOffsetSec = safeSec;
            _clock.Restart();
        }
    }

    public void SetVolume(int volumePercent)
    {
        lock (_lock)
        {
            _volume = Math.Clamp(volumePercent, 0, 100);
            if (_mpvAudioPlayer != null)
            {
                try { _mpvAudioPlayer.SetVolume(_isMuted ? 0 : _volume); } catch { }
            }

            if (OperatingSystem.IsWindows() && _wmpRaw != null)
            {
                try
                {
                    dynamic wmp = _wmpRaw;
                    wmp.settings.volume = _isMuted ? 0 : _volume;
                }
                catch { }
            }
        }
    }

    public void SetMute(bool isMuted)
    {
        lock (_lock)
        {
            _isMuted = isMuted;
            if (_mpvAudioPlayer != null)
            {
                try { _mpvAudioPlayer.SetMute(isMuted); } catch { }
            }

            if (OperatingSystem.IsWindows() && _wmpRaw != null)
            {
                try
                {
                    dynamic wmp = _wmpRaw;
                    wmp.settings.mute = isMuted;
                    if (!isMuted)
                    {
                        wmp.settings.volume = _volume;
                    }
                }
                catch { }
            }
        }
    }

    public void SetPlaybackRate(double rate)
    {
        lock (_lock)
        {
            _playbackRate = Math.Clamp(rate, 0.5, 2.0);
            if (_mpvAudioPlayer != null)
            {
                try { _mpvAudioPlayer.SetSpeed(_playbackRate); } catch { }
            }

            if (OperatingSystem.IsWindows() && _wmpRaw != null)
            {
                try
                {
                    dynamic wmp = _wmpRaw;
                    wmp.settings.rate = _playbackRate;
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
                if (_mpvAudioPlayer != null)
                {
                    try
                    {
                        long ms = _mpvAudioPlayer.CurrentPositionMs;
                        if (ms > 0) return ms / 1000.0;
                    }
                    catch { }
                }

                if (OperatingSystem.IsWindows() && _wmpRaw != null)
                {
                    try
                    {
                        dynamic wmp = _wmpRaw;
                        double wmpPos = (double)wmp.controls.currentPosition;
                        if (wmpPos > 0 || _clock.ElapsedMilliseconds > 600)
                            return wmpPos;
                    }
                    catch { }
                }

                if (_androidPlayer != null)
                {
                    try
                    {
                        double aPos = _androidPlayer.CurrentPositionSeconds;
                        if (aPos >= 0) return aPos;
                    }
                    catch { }
                }

                // Fallback tính theo đồng hồ thời gian thực siêu chính xác cho Linux / Fallback
                if (IsPlaying)
                {
                    return _clockOffsetSec + (_isPaused ? 0 : _clock.Elapsed.TotalSeconds);
                }
                return _clockOffsetSec;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            IsPlaying = false;
            _isPaused = false;
            _clock.Reset();
            _clockOffsetSec = 0;

            if (_mpvAudioPlayer != null)
            {
                try { _mpvAudioPlayer.Dispose(); } catch { }
                _mpvAudioPlayer = null;
            }

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
