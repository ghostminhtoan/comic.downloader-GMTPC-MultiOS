using System;
using System.Collections.Concurrent;
using System.Linq;

namespace ComicDownloaderGMTPC.Services;

public class BackgroundTaskInfo
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public double ProgressPercentage { get; set; } = 0;
    public bool IsRunning { get; set; } = true;
    public DateTime StartTime { get; set; } = DateTime.Now;
}

public class BackgroundExecutionService
{
    private static readonly Lazy<BackgroundExecutionService> _lazy = new(() => new BackgroundExecutionService());
    public static BackgroundExecutionService Instance => _lazy.Value;

    private readonly ConcurrentDictionary<string, BackgroundTaskInfo> _activeTasks = new();
    private readonly object _lock = new();

    // Sự kiện C# cho ViewModel & UI
    public event Action<BackgroundTaskInfo>? TaskProgressChanged;
    public event Action<bool>? AnyTaskRunningChanged;
    public event Action<bool>? BubbleModeChanged;

    // Delegate kết nối với Native Platform (Android Foreground Service / WakeLock / PiP / Desktop Window)
    public static Action<string, string, int>? NativeStartOrUpdateForegroundNotification { get; set; }
    public static Action? NativeStopForegroundNotification { get; set; }
    public static Action? NativeRequestEnterBubbleMode { get; set; }
    public static Action? NativeRequestExitBubbleMode { get; set; }
    public static Func<bool>? NativeIsBubbleOrPipSupported { get; set; }
    public static Action? NativeMinimizeOrHide { get; set; }
    public static Action? NativeForceExit { get; set; }

    public bool IsBubbleMode { get; private set; } = false;

    public bool HasActiveTasks => _activeTasks.Values.Any(t => t.IsRunning);

    public int ActiveTaskCount => _activeTasks.Values.Count(t => t.IsRunning);

    public BackgroundTaskInfo? GetPrimaryTask()
    {
        return _activeTasks.Values.Where(t => t.IsRunning).OrderByDescending(t => t.StartTime).FirstOrDefault();
    }

    public void ReportProgress(string taskId, string taskTitle, string detail, double progressPercentage, bool isRunning)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;

        BackgroundTaskInfo task;
        if (isRunning)
        {
            task = _activeTasks.AddOrUpdate(taskId,
                _ => new BackgroundTaskInfo
                {
                    Id = taskId,
                    Title = taskTitle,
                    Detail = detail,
                    ProgressPercentage = Math.Clamp(progressPercentage, 0, 100),
                    IsRunning = true,
                    StartTime = DateTime.Now
                },
                (_, existing) =>
                {
                    existing.Title = string.IsNullOrWhiteSpace(taskTitle) ? existing.Title : taskTitle;
                    existing.Detail = detail;
                    existing.ProgressPercentage = Math.Clamp(progressPercentage, 0, 100);
                    existing.IsRunning = true;
                    return existing;
                });
        }
        else
        {
            _activeTasks.TryRemove(taskId, out _);
            task = new BackgroundTaskInfo
            {
                Id = taskId,
                Title = taskTitle,
                Detail = detail,
                ProgressPercentage = 100,
                IsRunning = false
            };
        }

        TaskProgressChanged?.Invoke(task);
        bool anyRunning = HasActiveTasks;
        AnyTaskRunningChanged?.Invoke(anyRunning);

        // Kích hoạt Native Foreground Notification khi có tác vụ
        if (anyRunning)
        {
            var primary = GetPrimaryTask() ?? task;
            int percent = (int)Math.Round(primary.ProgressPercentage);
            string title = $"Comic-GMTPC: {primary.Title}";
            string notifText = $"{primary.Detail} ({percent}%)";
            NativeStartOrUpdateForegroundNotification?.Invoke(title, notifText, percent);
        }
        else
        {
            NativeStopForegroundNotification?.Invoke();
        }
    }

    public void CompleteTask(string taskId, string taskTitle = "", string completionMessage = "Hoàn tất")
    {
        ReportProgress(taskId, taskTitle, completionMessage, 100, false);
    }

    public void SetBubbleMode(bool enabled)
    {
        if (IsBubbleMode == enabled) return;
        IsBubbleMode = enabled;
        BubbleModeChanged?.Invoke(enabled);

        if (enabled)
        {
            NativeRequestEnterBubbleMode?.Invoke();
        }
        else
        {
            NativeRequestExitBubbleMode?.Invoke();
        }
    }

    public void ToggleBubbleMode()
    {
        SetBubbleMode(!IsBubbleMode);
    }

    public void MinimizeOrHide()
    {
        NativeMinimizeOrHide?.Invoke();
    }

    public void ForceExit()
    {
        NativeStopForegroundNotification?.Invoke();
        NativeRequestExitBubbleMode?.Invoke();
        if (NativeForceExit != null)
        {
            NativeForceExit.Invoke();
        }
        else
        {
            Environment.Exit(0);
        }
    }
}
