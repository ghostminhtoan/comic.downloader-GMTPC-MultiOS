// ====================================================================================================

// FILE SUMMARY: DownloadEngineService.cs

// ----------------------------------------------------------------------------------------------------

// VAI TRÒ & MỤC ĐÍCH:

//   Dịch vụ Tải Truyện Đa Luồng & Quản Lý Hàng Đợi Cốt Lõi (Core Download Engine & Queue Pipeline).

//

// CÁC TÍNH NĂNG & THÀNH PHẦN CHÍNH:

//   - Tải song song đa luồng có kiểm soát (Parallel HTTP Downloads).

//   - Đường ống thử lại tự động (Retry Pipeline) khi gặp lỗi mạng hoặc HTTP timeout.

//   - Tự động cắt ảnh dài khi tải (Auto Split Long Images) nếu được bật.

//   - Báo cáo tiến độ thời gian thực cho từng trang, từng chapter và toàn bộ bộ truyện.

//   - Ghi nhận lỗi chi tiết vào thư mục crash log mà không làm gián đoạn toàn bộ hàng đợi tải.

//

// LƯU Ý KỸ THUẬT & QUY TẮC BẢO TRÌ:

//   - Luôn tôn trọng CancellationToken của từng mục tải để hỗ trợ dừng/tạm dừng tức thì.

// ====================================================================================================



using System;

using System.Collections.Generic;

using System.Diagnostics;

using System.IO;

using System.Linq;

using System.Net.Http;

using System.Runtime.InteropServices;

using System.Text.Json;

using System.Threading;

using System.Threading.Tasks;

using ComicDownloaderGMTPC.Models;



namespace ComicDownloaderGMTPC.Services;



public class DownloadEngineService

{

    private static readonly Lazy<DownloadEngineService> _instance = new(() => new DownloadEngineService());

    public static DownloadEngineService Instance => _instance.Value;



    private readonly HttpClient _httpClient;

    private readonly ComicScraperService _scraperService = ComicScraperService.Instance;

    private CancellationTokenSource? _cts;

    private bool _isDownloading;

    private volatile bool _isPaused;

    private volatile bool _isStopping;

    private Task? _runningDownloadTask;

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);



    public bool IsStopping => _isStopping;

    private TaskCompletionSource<bool>? _pauseTcs;

    private long _totalBytesDownloadedInWindow;

    private DateTime _lastSpeedCheckTime = DateTime.UtcNow;

    private string? _lastSuccessfulNettruyenCdnServer;



    public bool IsDownloading => _isDownloading;

    public bool IsPaused => _isPaused;

    public string DownloadRoot { get; set; }

    public string CurrentSpeedText { get; private set; } = "0.0 KB/s";



    public event Action<string, string>? LogEmitted;

    public event Action? ProgressUpdated;

    public event Action<bool>? PauseStateChanged;

    public event Action<bool>? DownloadingStateChanged;

    public static event Action<string>? AndroidOpenFolderRequested;

    public static event Action? OpenStorageSettingsRequested;

    public static event Action? ResetAndroidFolderOpenerRequested;



    public bool AutoSplitLongImages { get; set; } = false;

    public int AutoSplitHeight { get; set; } = 5000;
    public bool AutoProcessImages { get; set; } = false;
    public Func<ImageEnhancerOptions>? GetImageEnhancerOptions { get; set; }

    public bool AddOrderPrefixToTitle { get; set; } = false;
    public int OrderPrefixStartIndex { get; set; } = 1;



    private int _orderCounter = 0;

    private string _lastOrderServerDir = string.Empty;

    private readonly object _orderLock = new();



    public int GetNextOrderIndex(string targetServerDir)

    {

        lock (_orderLock)

        {

            if (!string.Equals(_lastOrderServerDir, targetServerDir, StringComparison.OrdinalIgnoreCase))

            {

                _lastOrderServerDir = targetServerDir;

                int maxFound = 0;

                try

                {

                    if (Directory.Exists(targetServerDir))

                    {

                        var dirs = Directory.GetDirectories(targetServerDir);

                        foreach (var d in dirs)

                        {

                            string dirName = Path.GetFileName(d);

                            var match = System.Text.RegularExpressions.Regex.Match(dirName, @"^(\d{1,5})[\.\s\-_]");

                            if (match.Success && int.TryParse(match.Groups[1].Value, out int num))

                            {

                                if (num > maxFound) maxFound = num;

                            }

                        }

                    }

                }

                catch { }



                _orderCounter = maxFound;

            }



            _orderCounter++;

            return _orderCounter;

        }

    }



    public int AutoSplitQuality { get; set; } = 90;



    // SỐ TRUYỆN TẢI CÙNG LÚC & SỐ LUỒNG TẢI ẢNH

    public int ConcurrentComicDownloads { get; set; } = 2; // 1 đến 8 (mặc định 2)

    public int ImageDownloadThreads { get; set; } = 3; // 1 đến 16 (mặc định 3)



    private IList<ComicBookItem>? _liveQueueSource;

    private readonly HashSet<ComicBookItem> _activeItems = new();

    private readonly HashSet<ComicBookItem> _attemptedInSession = new();

    private readonly object _queueLock = new();

    private string _activeMode = "Single comic";



    public void BindLiveQueueSource(IList<ComicBookItem> liveSource)

    {

        lock (_queueLock)

        {

            _liveQueueSource = liveSource;

        }

    }



    public void RequestOpenStorageSettings() => OpenStorageSettingsRequested?.Invoke();

    public void RequestResetAndroidFolderOpener() => ResetAndroidFolderOpenerRequested?.Invoke();



    public DownloadEngineService()

    {

        var handler = DoHResolver.CreateBypassHandler();

        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");



        DownloadRoot = InitDownloadRoot();

    }





    private string InitDownloadRoot()

    {

        string appDir = AppDomain.CurrentDomain.BaseDirectory;

        string configFilePath = Path.Combine(appDir, "download_path.cfg");



        try

        {

            if (File.Exists(configFilePath))

            {

                string saved = File.ReadAllText(configFilePath).Trim();

                string normalizedSaved = NormalizeStoragePath(saved);



                if (!string.IsNullOrWhiteSpace(normalizedSaved))

                {

                    try

                    {

                        if (!Directory.Exists(normalizedSaved)) Directory.CreateDirectory(normalizedSaved);

                        if (!string.Equals(saved, normalizedSaved, StringComparison.Ordinal))

                        {

                            File.WriteAllText(configFilePath, normalizedSaved);

                        }

                        return normalizedSaved;

                    }

                    catch {}

                }

            }

        }

        catch {}



        if (OperatingSystem.IsAndroid())

        {

            string androidPath = GetDefaultAndroidDownloadPath();

            try { File.WriteAllText(configFilePath, androidPath); } catch {}

            return androidPath;

        }



        string defaultPath = Path.Combine(appDir, "Downloads");

        try

        {

            if (!Directory.Exists(defaultPath)) Directory.CreateDirectory(defaultPath);

            return defaultPath;

        }

        catch

        {

            string docsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ComicDownloads");

            try { if (!Directory.Exists(docsPath)) Directory.CreateDirectory(docsPath); } catch {}

            return docsPath;

        }

    }



    public void SetDownloadRoot(string newPath)

    {

        if (string.IsNullOrWhiteSpace(newPath)) return;

        string normalized = NormalizeStoragePath(newPath);



        try

        {

            if (!Directory.Exists(normalized)) Directory.CreateDirectory(normalized);

            DownloadRoot = normalized;



            string configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "download_path.cfg");

            File.WriteAllText(configFilePath, normalized);

            LogEmitted?.Invoke("SUCCESS", $"Đã lưu cấu hình thư mục tải về: {normalized}");

        }

        catch (Exception ex)

        {

            LogEmitted?.Invoke("WARN", $"Không thể tạo thư mục '{normalized}': {ex.Message}. Đang chuyển về thư mục mặc định.");

            if (OperatingSystem.IsAndroid())

            {

                DownloadRoot = GetDefaultAndroidDownloadPath();

            }

        }

    }



    public static string NormalizeStoragePath(string? rawPath, string? folderName = null)

    {

        if (string.IsNullOrWhiteSpace(rawPath))

        {

            return OperatingSystem.IsAndroid() ? GetDefaultAndroidDownloadPath() : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads");

        }



        string path = rawPath.Trim().TrimEnd('/', '\\');



        if (OperatingSystem.IsAndroid())

        {

            string decoded = Uri.UnescapeDataString(path);



            if (decoded.StartsWith("/storage/", StringComparison.OrdinalIgnoreCase) ||

                decoded.StartsWith("/sdcard/", StringComparison.OrdinalIgnoreCase) ||

                decoded.StartsWith("/data/", StringComparison.OrdinalIgnoreCase))

            {

                if (decoded.StartsWith("/sdcard", StringComparison.OrdinalIgnoreCase))

                {

                    decoded = "/storage/emulated/0" + decoded.Substring(7);

                }

                return decoded.TrimEnd('/', '\\');

            }



            if (decoded.StartsWith("raw:", StringComparison.OrdinalIgnoreCase))

            {

                return decoded.Substring(4).TrimEnd('/', '\\');

            }



            int colonIdx = decoded.LastIndexOf(':');

            if (colonIdx >= 0 && colonIdx < decoded.Length - 1)

            {

                string volumePart = decoded.Substring(0, colonIdx);

                string subPath = decoded.Substring(colonIdx + 1).Trim().Trim('/', '\\');



                int slashBeforeColon = volumePart.LastIndexOfAny(new[] { '/', '%', ':' });

                string volumeId = slashBeforeColon >= 0 ? volumePart.Substring(slashBeforeColon + 1) : volumePart;



                if (volumeId.Equals("primary", StringComparison.OrdinalIgnoreCase) || volumeId.Equals("0", StringComparison.OrdinalIgnoreCase))

                {

                    if (string.IsNullOrWhiteSpace(subPath))

                    {

                        return !string.IsNullOrWhiteSpace(folderName)

                            ? Path.Combine("/storage/emulated/0", folderName)

                            : "/storage/emulated/0";

                    }

                    return $"/storage/emulated/0/{subPath}";

                }

                else if (System.Text.RegularExpressions.Regex.IsMatch(volumeId, @"^[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}$"))

                {

                    if (string.IsNullOrWhiteSpace(subPath))

                    {

                        return !string.IsNullOrWhiteSpace(folderName)

                            ? Path.Combine($"/storage/{volumeId}", folderName)

                            : $"/storage/{volumeId}";

                    }

                    return $"/storage/{volumeId}/{subPath}";

                }

                else if (!string.IsNullOrWhiteSpace(subPath))

                {

                    return $"/storage/emulated/0/{subPath}";

                }

            }



            string searchStr = decoded.ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(folderName) && 

                !folderName.Equals("primary", StringComparison.OrdinalIgnoreCase) &&

                !folderName.Equals("0", StringComparison.OrdinalIgnoreCase))

            {

                if (searchStr.Contains("download")) return Path.Combine("/storage/emulated/0/Download", folderName);

                if (searchStr.Contains("document")) return Path.Combine("/storage/emulated/0/Documents", folderName);

                if (searchStr.Contains("picture")) return Path.Combine("/storage/emulated/0/Pictures", folderName);

                if (searchStr.Contains("dcim")) return Path.Combine("/storage/emulated/0/DCIM", folderName);

                return Path.Combine("/storage/emulated/0", folderName);

            }



            if (searchStr.Contains("download")) return "/storage/emulated/0/Download";

            if (searchStr.Contains("document")) return "/storage/emulated/0/Documents";

            if (searchStr.Contains("picture")) return "/storage/emulated/0/Pictures";



            return GetDefaultAndroidDownloadPath();

        }



        return path;

    }



    public static string GetDefaultAndroidDownloadPath()

    {

        string pub = "/storage/emulated/0/Download/ComicDownloads";

        if (CanWriteToDirectory(pub)) return pub;



        string appExt = GetAppSpecificExternalPath();

        if (CanWriteToDirectory(appExt)) return appExt;



        string docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ComicDownloads");

        try { if (!Directory.Exists(docs)) Directory.CreateDirectory(docs); } catch {}

        return docs;

    }



    public static string GetAppSpecificExternalPath()

    {

        string pkgName = "com.CompanyName.ComicDownloaderGMTPC";

        string path = $"/storage/emulated/0/Android/data/{pkgName}/files/Download";

        try

        {

            if (!Directory.Exists(path)) Directory.CreateDirectory(path);

        }

        catch {}

        return path;

    }



    public static bool CanWriteToDirectory(string dirPath)

    {

        if (string.IsNullOrWhiteSpace(dirPath)) return false;

        try

        {

            if (!Directory.Exists(dirPath))

            {

                Directory.CreateDirectory(dirPath);

            }

            string testFile = Path.Combine(dirPath, $".probe_{Guid.NewGuid():N}.tmp");

            File.WriteAllBytes(testFile, new byte[] { 0x47, 0x4D, 0x54 });

            if (File.Exists(testFile))

            {

                File.Delete(testFile);

                return true;

            }

            return false;

        }

        catch

        {

            return false;

        }

    }



    private string? _lastVerifiedWritableRoot;



    public string EnsureWritableDownloadRoot(string candidateRoot)

    {

        if (!OperatingSystem.IsAndroid())

        {

            return candidateRoot;

        }



        candidateRoot = NormalizeStoragePath(candidateRoot);

        if (!string.IsNullOrWhiteSpace(_lastVerifiedWritableRoot) &&

            string.Equals(_lastVerifiedWritableRoot, candidateRoot, StringComparison.OrdinalIgnoreCase) &&

            Directory.Exists(_lastVerifiedWritableRoot))

        {

            return _lastVerifiedWritableRoot;

        }



        if (CanWriteToDirectory(candidateRoot))

        {

            _lastVerifiedWritableRoot = candidateRoot;

            return candidateRoot;

        }



        string pubDownload = "/storage/emulated/0/Download/ComicDownloads";

        if (CanWriteToDirectory(pubDownload))

        {

            _lastVerifiedWritableRoot = pubDownload;

            SetDownloadRoot(pubDownload);

            return pubDownload;

        }



        string appStorage = GetAppSpecificExternalPath();

        if (CanWriteToDirectory(appStorage))

        {

            _lastVerifiedWritableRoot = appStorage;

            SetDownloadRoot(appStorage);

            LogEmitted?.Invoke("WARN", $"[Bộ nhớ Android] Thư mục ngoài bị chặn Access Denied. Đã tự động kích hoạt thư mục an toàn:\n{appStorage}\nToàn bộ ảnh sẽ được tải đầy đủ vào đây!");

            return appStorage;

        }



        string docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ComicDownloads");

        try { if (!Directory.Exists(docs)) Directory.CreateDirectory(docs); } catch {}

        _lastVerifiedWritableRoot = docs;

        SetDownloadRoot(docs);

        return docs;

    }



    private long _lastEngineProgressTick;



    public void NotifyConcurrencyChanged()

    {

        ProgressUpdated?.Invoke();

    }



    private static bool IsCompletedStatus(string? status) =>

        string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) ||

        string.Equals(status, "Hoàn tất", StringComparison.OrdinalIgnoreCase);



    private static bool IsWaitingOrReadyStatus(string? status) =>

        string.IsNullOrWhiteSpace(status) ||

        string.Equals(status, "Waiting", StringComparison.OrdinalIgnoreCase) ||

        string.Equals(status, "Ready", StringComparison.OrdinalIgnoreCase) ||

        string.Equals(status, "Chờ tải", StringComparison.OrdinalIgnoreCase);



    /// <summary>

    /// Thông báo khi người dùng thêm truyện mới hoặc đổi vị trí truyện (MoveUp / MoveToTop / MoveDown / MoveToBottom)

    /// để tiến trình tải luôn ưu tiên lấy đúng thứ tự từ trên xuống dưới theo danh sách hiển thị hiện tại.

    /// </summary>

    public void NotifyQueueOrderChanged(ComicBookItem? prioritizedItem = null)

    {

        lock (_queueLock)

        {

            if (prioritizedItem != null)

            {

                MakeItemEligibleUnderLock(prioritizedItem);

                if (prioritizedItem.IsParallelSplitParent && prioritizedItem.ParallelSplitChildren != null)

                {

                    foreach (var child in prioritizedItem.ParallelSplitChildren)

                    {

                        if (child != null) MakeItemEligibleUnderLock(child);

                    }

                }

            }

        }

        ProgressUpdated?.Invoke();

    }



    private void MakeItemEligibleUnderLock(ComicBookItem item)

    {

        if (_activeItems.Contains(item) || IsCompletedStatus(item.Status))

            return;



        _attemptedInSession.Remove(item);

        if (_isDownloading && item.IsChecked && !item.IsParallelSplitParent)

        {

            if (!string.Equals(item.Status, "Downloading", StringComparison.OrdinalIgnoreCase) &&

                !IsWaitingOrReadyStatus(item.Status))

            {

                item.Status = "Waiting";

                item.StatusMessage = "Đang chờ đến lượt tải...";

            }

        }

    }



    private ComicBookItem? TryDequeueNextTopToBottomItem()

    {

        lock (_queueLock)

        {

            if (_liveQueueSource == null || _liveQueueSource.Count == 0)

                return null;



            for (int i = 0; i < _liveQueueSource.Count; i++)

            {

                var candidate = _liveQueueSource[i];

                if (candidate == null || !candidate.IsChecked || candidate.IsParallelSplitParent)

                    continue;



                if (_activeItems.Contains(candidate))

                    continue;



                if (IsCompletedStatus(candidate.Status))

                    continue;



                if (_attemptedInSession.Contains(candidate))

                    continue;



                if (IsWaitingOrReadyStatus(candidate.Status))

                {

                    _activeItems.Add(candidate);

                    _attemptedInSession.Add(candidate);

                    return candidate;

                }

            }



            return null;

        }

    }



    private bool HasPendingEligibleItems()

    {

        lock (_queueLock)

        {

            if (_liveQueueSource == null || _liveQueueSource.Count == 0)

                return false;



            for (int i = 0; i < _liveQueueSource.Count; i++)

            {

                var candidate = _liveQueueSource[i];

                if (candidate == null || !candidate.IsChecked || candidate.IsParallelSplitParent)

                    continue;



                if (_activeItems.Contains(candidate) || IsCompletedStatus(candidate.Status) || _attemptedInSession.Contains(candidate))

                    continue;



                if (IsWaitingOrReadyStatus(candidate.Status))

                    return true;

            }



            return false;

        }

    }



    public async Task StartDownloadAsync(IEnumerable<ComicBookItem> items, string mode, CancellationToken externalCt = default)

    {

        await _lifecycleLock.WaitAsync().ConfigureAwait(false);

        try

        {

            if (_runningDownloadTask != null && !_runningDownloadTask.IsCompleted)

            {

                if (_cts != null && !_cts.IsCancellationRequested)

                {

                    try { _cts.Cancel(); } catch { }

                }

                await Task.WhenAny(_runningDownloadTask, Task.Delay(3000)).ConfigureAwait(false);

            }



            _isStopping = false;

            _isDownloading = true;

            DownloadingStateChanged?.Invoke(true);

            _activeMode = mode;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);

            var ct = _cts.Token;

        _lastSpeedCheckTime = DateTime.UtcNow;

        _totalBytesDownloadedInWindow = 0;



        int concurrentComics = Math.Clamp(ConcurrentComicDownloads, 1, 16);

        int imageThreads = Math.Max(1, ImageDownloadThreads);



        int eligibleCount = 0;

        lock (_queueLock)

        {

            if (items is IList<ComicBookItem> liveList)

            {

                _liveQueueSource = liveList;

            }



            _activeItems.Clear();

            _attemptedInSession.Clear();



            var validItems = items.Where(item => item != null && item.IsChecked && !item.IsParallelSplitParent && !IsCompletedStatus(item.Status)).ToList();

            if (validItems.Count == 0)

            {

                validItems = items.Where(item => item != null && item.IsChecked && !item.IsParallelSplitParent).ToList();

            }



            foreach (var item in validItems)

            {

                item.Status = "Waiting";

                if (item.DownloadedChapters <= 0 && item.ProgressPercentage <= 0)

                {

                    item.StatusMessage = "Đang chờ đến lượt tải...";

                }

                eligibleCount++;

            }



            // Nếu truyền vào một tập con (ví dụ RetryAsync), chỉ cho phép các mục ngoài tập con nhưng đang ở trạng thái Waiting cùng tham gia

            _liveQueueSource = validItems;

        }



        if (eligibleCount == 0)

        {

            LogEmitted?.Invoke("WARN", "Không có truyện nào được tích chọn để tải.");

            _isDownloading = false;

            DownloadingStateChanged?.Invoke(false);

            return;

        }



        LogEmitted?.Invoke("INFO", $"Bắt đầu tải danh sách truyện theo thứ tự từ trên xuống dưới (Song song: {concurrentComics} truyện, {imageThreads} luồng ảnh | Thư mục: {mode}) tới: {DownloadRoot}");



        var currentTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _runningDownloadTask = currentTcs.Task;



        try

        {

            ReportOverallProgress(null, "Bắt đầu tải danh sách truyện...", force: true);



            while (!ct.IsCancellationRequested && HasPendingEligibleItems())

            {

                int currentConcurrency = Math.Clamp(ConcurrentComicDownloads, 1, 16);

                var workerTasks = Enumerable.Range(0, currentConcurrency).Select(async _ =>

                {

                    while (!ct.IsCancellationRequested)

                    {

                        var item = TryDequeueNextTopToBottomItem();

                        if (item == null)

                        {

                            break;

                        }



                        try

                        {

                            ThermalAndPowerOptimizationService.Instance.NotifyUserActivity();

                            int currentImageThreads = Math.Max(1, ImageDownloadThreads);

                            if (ThermalAndPowerOptimizationService.Instance.IsThermalThrottled)

                            {

                                currentImageThreads = Math.Min(2, currentImageThreads);

                            }

                            await DownloadComicBookAsync(item, _activeMode, currentImageThreads, ct).ConfigureAwait(false);

                        }

                        catch (OperationCanceledException)

                        {

                            break;

                        }

                        catch (Exception ex)

                        {

                            LogEmitted?.Invoke("ERROR", $"Lỗi tải truyện '{item.Title}': {ex.Message}");

                        }

                        finally

                        {

                            lock (_queueLock)

                            {

                                _activeItems.Remove(item);

                            }

                        }

                    }

                }).ToList();



                await Task.WhenAll(workerTasks).ConfigureAwait(false);

            }



            LogEmitted?.Invoke("SUCCESS", "Hoàn tất toàn bộ tác vụ tải trong hàng chờ.");

            SoundNotificationService.Instance.PlaySound(SoundNotificationType.DownloadFinish);

        }

        catch (OperationCanceledException)

        {

            LogEmitted?.Invoke("WARN", "Tiến trình tải đã được người dùng dừng lại.");

        }

        catch (Exception ex)

        {

            LogEmitted?.Invoke("ERROR", $"Tiến trình tải gặp lỗi: {ex.Message}");

            SoundNotificationService.Instance.PlaySound(SoundNotificationType.DownloadError);

        }

        finally

        {

            lock (_queueLock)

            {

                _activeItems.Clear();

            }

            _isDownloading = false;

            _isStopping = false;

            _isPaused = false;

            _pauseTcs?.TrySetResult(true);

            _pauseTcs = null;

            CurrentSpeedText = "0.0 KB/s";

            BackgroundExecutionService.Instance.CompleteTask("download_queue", "Tải Truyện", "Hoàn tất tải truyện");

            DownloadingStateChanged?.Invoke(false);

            PauseStateChanged?.Invoke(false);

            ProgressUpdated?.Invoke();

            currentTcs?.TrySetResult(true);

        }

    }

    finally

    {

        _lifecycleLock.Release();

    }

}



    public async Task DownloadNewAsync(IEnumerable<ComicBookItem> items, string mode, CancellationToken externalCt = default)

    {

        var pendingItems = items.Where(item => item != null && item.IsChecked && !item.IsParallelSplitParent && !IsCompletedStatus(item.Status)).ToList();

        if (pendingItems.Count == 0)

        {

            LogEmitted?.Invoke("WARN", "Không có truyện mới nào được tích chọn để tải tiếp.");

            return;

        }



        if (_isDownloading)

        {

            int enqueuedCount = 0;

            lock (_queueLock)

            {

                foreach (var item in pendingItems)

                {

                    if (!_activeItems.Contains(item))

                    {

                        _attemptedInSession.Remove(item);

                        item.Status = "Waiting";

                        item.StatusMessage = "Đã xếp vào hàng chờ tải tiếp (theo thứ tự từ trên xuống)...";

                        enqueuedCount++;

                    }

                }

            }

            LogEmitted?.Invoke("INFO", $"Đã nạp thêm {enqueuedCount} truyện mới vào tiến trình tải đang chạy (tự động ưu tiên theo vị trí từ trên xuống dưới).");

            ProgressUpdated?.Invoke();

            ReportOverallProgress(null, $"Đã nạp thêm {enqueuedCount} truyện vào hàng chờ...", force: true);

        }

        else

        {

            await StartDownloadAsync( _liveQueueSource ?? pendingItems, mode, externalCt).ConfigureAwait(false);

        }

    }



    public void Pause()

    {

        if (!_isDownloading || _isPaused) return;

        _isPaused = true;

        _pauseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        LogEmitted?.Invoke("WARN", "⏸️ Đã tạm dừng tải truyện. Bấm TIẾP TỤC để tiếp tục tiến trình.");

        ReportOverallProgress(null, "Đã tạm dừng tải truyện...", force: true);

        PauseStateChanged?.Invoke(true);

        ProgressUpdated?.Invoke();

    }



    public void Resume()

    {

        if (!_isPaused) return;

        _isPaused = false;

        _pauseTcs?.TrySetResult(true);

        _pauseTcs = null;

        LogEmitted?.Invoke("INFO", "▶️ Tiếp tục tiến trình tải truyện...");

        ReportOverallProgress(null, "Đang tiếp tục tải...", force: true);

        PauseStateChanged?.Invoke(false);

        ProgressUpdated?.Invoke();

    }



    public void TogglePause()

    {

        if (_isPaused) Resume();

        else Pause();

    }



    private async Task WaitIfPausedAsync(CancellationToken ct)

    {

        var tcs = _pauseTcs;

        if (_isPaused && tcs != null)

        {

            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

            try

            {

                await tcs.Task.ConfigureAwait(false);

            }

            catch (OperationCanceledException) {}

        }

    }



    public void Stop()

    {

        _isStopping = true;

        _isPaused = false;

        _pauseTcs?.TrySetCanceled();

        _pauseTcs = null;



        if (_cts != null && !_cts.IsCancellationRequested)

        {

            try

            {

                _cts.Cancel();

            }

            catch { }

            LogEmitted?.Invoke("WARN", "⏹️ Đã yêu cầu dừng toàn bộ tác vụ tải.");

        }



        CurrentSpeedText = "0.0 KB/s";

        BackgroundExecutionService.Instance.CompleteTask("download_queue", "Tải Truyện", "Đã dừng tải");

        ProgressUpdated?.Invoke();

    }



    public void OpenDirectoryInExplorer(string path)

    {

        try

        {

            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))

            {

                path = DownloadRoot;

            }



            if (!Directory.Exists(path))

            {

                Directory.CreateDirectory(path);

            }



            if (OperatingSystem.IsWindows())

            {

                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });

            }

            else if (OperatingSystem.IsMacOS())

            {

                Process.Start(new ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = true });

            }

            else if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())

            {

                Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = true });

            }

            else if (OperatingSystem.IsAndroid())

            {

                AndroidOpenFolderRequested?.Invoke(path);

            }

        }

        catch (Exception ex)

        {

            LogEmitted?.Invoke("WARN", $"Không thể mở thư mục '{path}': {ex.Message}");

        }

    }



    private async Task DownloadComicBookAsync(ComicBookItem book, string mode, int imageThreads, CancellationToken ct)

    {

        book.Status = "Downloading";

        book.StatusMessage = "Đang khởi tạo thư mục và nạp danh sách chương...";

        book.DetailProgressText = "Đang khởi tạo...";

        ProgressUpdated?.Invoke();

        ReportOverallProgress(book, $"{book.Title} • Đang khởi tạo...", force: true);



        string effectiveRoot = EnsureWritableDownloadRoot(DownloadRoot);

        string serverFolder = DomainRoutingService.GetServerFolderName(book.Domain, book.Url);

        string rawSubfolder = string.IsNullOrWhiteSpace(book.Subfolder) ? "[default]" : book.Subfolder.Trim();

        string subFolder = MakeSafeFilename(rawSubfolder, 50);

        if (string.IsNullOrWhiteSpace(subFolder)) subFolder = "[default]";



        string targetServerDir = Path.Combine(effectiveRoot, serverFolder, subFolder);



        if (AddOrderPrefixToTitle)
        {
            // Bao toan tuyet doi STT da gan (khong renumber lai khi cac truyen truoc do tai xong va bi tu dong xoa)
            if (!book.HasOrderPrefixApplied || book.AppliedOrderNumber <= 0)
            {
                int baseIndex = book.Index > 0 ? book.Index : 1;
                int startIdx = OrderPrefixStartIndex > 0 ? OrderPrefixStartIndex : 1;
                int orderNumber = (baseIndex - 1) + startIdx;

                // Bao toan RawTitle tuyet doi cho truyen bat dau bang so (vd: 100 Kanojo, 300 Spartans, 86...)
                if (string.IsNullOrWhiteSpace(book.RawTitle))
                {
                    book.RawTitle = book.Title;
                }

                string baseTitle = !string.IsNullOrWhiteSpace(book.RawTitle) ? book.RawTitle : book.Title;
                string prefix = $"{orderNumber:D3}. ";

                book.HasOrderPrefixApplied = true;
                book.AppliedOrderNumber = orderNumber;
                book.Title = $"{prefix}{baseTitle}";
            }
        }
        else if (book.HasOrderPrefixApplied && !string.IsNullOrWhiteSpace(book.RawTitle))
        {
            book.Title = book.RawTitle;
            book.HasOrderPrefixApplied = false;
            book.AppliedOrderNumber = 0;
        }



        string safeBookName = MakeSafeFilename(book.Title, 80);

        string bookDir = Path.Combine(targetServerDir, safeBookName);

        book.LocalDirectory = bookDir;



        try

        {

            bookDir = SafeCreateDirectory(bookDir);

            book.LocalDirectory = bookDir;



            // Trích xuất danh sách chapter nếu chưa có

            if (book.Chapters.Count == 0)

            {

                var refreshed = await _scraperService.ScrapeBookAsync(book.Url, book.Index, book.PreferredLanguage, true, ct).ConfigureAwait(false);

                book.Chapters = refreshed.Chapters;

                book.TotalChapters = refreshed.TotalChapters;

                if (!string.IsNullOrWhiteSpace(refreshed.StatusMessage))

                {

                    book.StatusMessage = refreshed.StatusMessage;

                }

            }



            if (book.Chapters.Count == 0)

            {

                book.Status = "Error";

                book.StatusMessage = "Không có chương nào để tải (Hoặc lỗi kết nối/bị chặn).";

                book.DetailProgressText = "0 chương";

                LogEmitted?.Invoke("ERROR", $"Truyện '{book.Title}' không thể tải vì 0 chương: {book.StatusMessage}");

                ProgressUpdated?.Invoke();

                return;

            }



            // XỬ LÝ CHỌN SỐ CHAPTER ĐỂ TẢI (CHẤP NHẬN CHỮ, SỐ NGUYÊN, SỐ THẬP PHÂN)

            List<ChapterItem> targetChapters = book.Chapters;

            if (!string.IsNullOrWhiteSpace(book.ChapterSelectionText))

            {

                var chapterFilter = ChapterRangeParser.Parse(book.ChapterSelectionText);

                if (chapterFilter != null)

                {

                    var filtered = new List<ChapterItem>();

                    for (int i = 0; i < book.Chapters.Count; i++)

                    {

                        var ch = book.Chapters[i];

                        if (chapterFilter.IsMatch(ch.Title, ch.ChapterNumber))

                        {

                            filtered.Add(ch);

                        }

                    }



                    if (filtered.Count > 0)

                    {

                        targetChapters = filtered;

                        LogEmitted?.Invoke("INFO", $"[{book.Title}] Bộ lọc chapter '{book.ChapterSelectionText}': Đã chọn {filtered.Count}/{book.Chapters.Count} chương");

                    }

                    else

                    {

                        LogEmitted?.Invoke("WARN", $"[{book.Title}] Bộ lọc '{book.ChapterSelectionText}' không khớp chương nào. Tải toàn bộ {book.Chapters.Count} chương.");

                    }

                }

            }



            // KHỬ TRÙNG CHAPTER (DEDUPLICATION DEFENSE-IN-DEPTH):

            // Bảo vệ tuyệt đối không bao giờ tải trùng số chapter kể cả khi danh sách từ queue / autosave.md có bản ghi trùng lặp

            if (targetChapters.Count > 1)

            {

                var dedupedList = new List<ChapterItem>();

                var seenNumbers = new HashSet<double>();

                foreach (var c in targetChapters)

                {

                    if (c.ChapterNumber > 0)

                    {

                        if (seenNumbers.Add(c.ChapterNumber))

                        {

                            dedupedList.Add(c);

                        }

                    }

                    else

                    {

                        dedupedList.Add(c);

                    }

                }

                targetChapters = dedupedList;

            }



            int totalChapters = Math.Max(1, targetChapters.Count);

            int completedChapters = 0;

            int consecutiveExtractErrors = 0;



            for (int chIdx = 0; chIdx < targetChapters.Count; chIdx++)

            {

                if (ct.IsCancellationRequested)

                {

                    book.Status = "Stopped";

                    book.StatusMessage = "Đã dừng bởi người dùng";

                    if (book.ProgressPercentage <= 0 && book.DownloadedChapters <= 0 && book.DownloadedPages <= 0)

                    {

                        book.DetailProgressText = "Đã dừng";

                    }

                    ProgressUpdated?.Invoke();

                    return;

                }



                await WaitIfPausedAsync(ct).ConfigureAwait(false);



                var chapter = targetChapters[chIdx];

                string chapterDirName = MakeSafeChapterDirName(chapter.Title, chIdx + 1, 60);



                // Loại bỏ folder con thừa 'Full Gallery' / 'Single Chapter' cho các trang doujinshi / gallery 1 chapter

                bool isSingleGallery = targetChapters.Count == 1 &&

                    (string.Equals(chapter.Title, "Full Gallery", StringComparison.OrdinalIgnoreCase) ||

                     string.Equals(chapter.Title, "Single Chapter", StringComparison.OrdinalIgnoreCase) ||

                     string.Equals(chapter.Title, "Gallery", StringComparison.OrdinalIgnoreCase) ||

                     string.Equals(chapter.Title, "Chapter 1", StringComparison.OrdinalIgnoreCase) ||

                     (book.Domain != null && (book.Domain.Contains("hitomi") || book.Domain.Contains("e-hentai") || book.Domain.Contains("exhentai") || book.Domain.Contains("hentaiforce"))));



                // Luôn cập nhật động theo Subfolder mới nhất của book (đề phòng người dùng đổi Subfolder trong lúc tải)

                string currentRawSubfolder = string.IsNullOrWhiteSpace(book.Subfolder) ? "[default]" : book.Subfolder.Trim();

                string currentSubFolder = MakeSafeFilename(currentRawSubfolder, 50);

                if (string.IsNullOrWhiteSpace(currentSubFolder)) currentSubFolder = "[default]";



                string currentTargetServerDir = Path.Combine(effectiveRoot, serverFolder, currentSubFolder);

                string currentBookDir = Path.Combine(currentTargetServerDir, safeBookName);

                book.LocalDirectory = currentBookDir;

                SafeCreateDirectory(currentBookDir);



                string chapterDir;

                bool isMultiMode = mode != null && (mode.Contains("Multi", StringComparison.OrdinalIgnoreCase) || mode.Contains("Nhiều", StringComparison.OrdinalIgnoreCase));



                if (isMultiMode)

                {

                    chapterDir = Path.Combine(currentTargetServerDir, MakeSafeFilename($"{safeBookName}-{chapterDirName}", 100));

                }

                else

                {

                    chapterDir = isSingleGallery ? currentBookDir : Path.Combine(currentBookDir, chapterDirName);

                }



                chapterDir = SafeCreateDirectory(chapterDir);



                string manifestFile = Path.Combine(chapterDir, ".manifest.json");

                var manifest = LoadManifest(manifestFile);



                if (manifest.Count > 0 &&

                    (string.Equals(chapter.Status, "Completed", StringComparison.OrdinalIgnoreCase) ||

                     (chapter.TotalPages > 0 && manifest.Count >= chapter.TotalPages)) &&

                    AreAllManifestFilesValid(chapterDir, manifest))

                {

                    completedChapters++;

                    chapter.Status = "Completed";

                    chapter.TotalPages = Math.Max(chapter.TotalPages, manifest.Count);

                    chapter.DownloadedPages = chapter.TotalPages;

                    book.StatusMessage = $"Bỏ qua chương đã tải đủ: {chapter.Title}";

                    book.UpdateProgress(completedChapters, totalChapters, chapter.DownloadedPages, chapter.TotalPages);

                    ProgressUpdated?.Invoke();

                    continue;

                }



                book.StatusMessage = $"Đang trích xuất ảnh {chapter.Title}...";

                book.UpdateProgress(Math.Max(completedChapters, Math.Min(book.DownloadedChapters, chIdx)), totalChapters, 0, 0);

                ProgressUpdated?.Invoke();



                await WaitIfPausedAsync(ct).ConfigureAwait(false);



                // XỬ LÝ CHUYÊN BIỆT CHO MÒE TRUYỆN (DRM IMGX DECRYPTION THUẦN C#)

                if (book.Domain != null && book.Domain.Contains("moetruyen"))

                {

                    book.StatusMessage = $"Đang giải mã và tải {chapter.Title}...";

                    ProgressUpdated?.Invoke();



                    int moeDone = 0;

                    try

                    {

                        moeDone = await MoeTruyenCryptoService.Instance.DownloadChapterPagesAsync(

                            chapter.Url,

                            chapterDir,

                            manifest,

                            (done, total) =>

                            {

                                chapter.DownloadedPages = done;

                                chapter.TotalPages = total;

                                book.UpdateProgressThrottled(

                                    completedChapters,

                                    totalChapters,

                                    done,

                                    total,

                                    $"Đang tải {chapter.Title}: {done}/{total} trang",

                                    force: done == total);

                                ReportOverallProgress(book, $"{book.Title} • {chapter.Title} ({done}/{total})", force: false);

                            },

                            ct).ConfigureAwait(false);

                    }

                    catch (Exception ex)

                    {

                        LogEmitted?.Invoke("ERROR", $"Lỗi tải chapter '{chapter.Title}' Mòe Truyện: {ex.Message}");

                    }



                    SaveManifest(manifestFile, manifest);

                    if (moeDone > 0)

                    {

                        consecutiveExtractErrors = 0;

                        completedChapters++;

                        chapter.Status = "Completed";

                        chapter.TotalPages = Math.Max(chapter.TotalPages, manifest.Count);

                        chapter.DownloadedPages = chapter.TotalPages;

                        book.UpdateProgress(completedChapters, totalChapters, chapter.DownloadedPages, chapter.TotalPages);

                        ProgressUpdated?.Invoke();

                    }

                    continue;

                }

                // XỬ LÝ CHUYÊN BIỆT CHO CỨU TRUYỆN (DRM WASM SLICE SCRAMBLING DECRYPTION THUẦN C#)

                if (book.Domain != null && book.Domain.Contains("cuutruyen"))

                {

                    book.StatusMessage = $"Đang giải mã và tải {chapter.Title}...";

                    ProgressUpdated?.Invoke();



                    int cuuDone = 0;

                    try

                    {

                        cuuDone = await CuuTruyenService.Instance.DownloadChapterPagesAsync(

                            chapter.Url,

                            chapterDir,

                            manifest,

                            (done, total) =>

                            {

                                chapter.DownloadedPages = done;

                                chapter.TotalPages = total;

                                book.UpdateProgressThrottled(

                                    completedChapters,

                                    totalChapters,

                                    done,

                                    total,

                                    $"Đang tải {chapter.Title}: {done}/{total} trang",

                                    force: done == total);

                                ReportOverallProgress(book, $"{book.Title} • {chapter.Title} ({done}/{total})", force: false);

                            },

                            ct).ConfigureAwait(false);

                    }

                    catch (Exception ex)

                    {

                        LogEmitted?.Invoke("ERROR", $"Lỗi tải chapter '{chapter.Title}' Cứu Truyện: {ex.Message}");

                    }



                    SaveManifest(manifestFile, manifest);

                    if (cuuDone > 0)

                    {

                        consecutiveExtractErrors = 0;

                        completedChapters++;

                        chapter.Status = "Completed";

                        chapter.TotalPages = Math.Max(chapter.TotalPages, manifest.Count);

                        chapter.DownloadedPages = chapter.TotalPages;

                        book.UpdateProgress(completedChapters, totalChapters, chapter.DownloadedPages, chapter.TotalPages);

                        ProgressUpdated?.Invoke();

                    }

                    continue;

                }



                var readerUrls = new List<string>();

                var imageUrls = await _scraperService.ExtractChapterImageUrlsAsync(chapter.Url, book.Domain ?? string.Empty, ct, readerUrls).ConfigureAwait(false);

                if (imageUrls.Count == 0)

                {

                    for (int retryExtract = 1; retryExtract <= 2; retryExtract++)

                    {

                        if (ct.IsCancellationRequested) break;

                        await Task.Delay(800 * retryExtract, ct).ConfigureAwait(false);

                        imageUrls = await _scraperService.ExtractChapterImageUrlsAsync(chapter.Url, book.Domain ?? string.Empty, ct, readerUrls).ConfigureAwait(false);

                        if (imageUrls.Count > 0) break;

                    }

                }



                chapter.ImageUrls = imageUrls;

                chapter.ReaderUrls = readerUrls;

                chapter.TotalPages = imageUrls.Count;



                if (imageUrls.Count > 0)

                {

                    int downloadedCount = 0;

                    var indexedUrls = imageUrls.Select((url, idx) => (Url: url, Index: idx)).ToList();



                    int maxParallel = Math.Max(1, imageThreads);

                    if (OperatingSystem.IsAndroid())

                    {

                        // Trên Android khống chế tối đa 2-3 luồng để bảo vệ RAM và CPU không bị Low Memory Killer (LMK) kill process

                        maxParallel = Math.Clamp(maxParallel, 1, 3);

                    }



                    // CƠ CHẾ ĐIỀU PHỐI TẢI TUẦN TỰ ƯU TIÊN SỐ TRANG (Sequential Sliding Window Worker Pool)
                    // Bất kể mở bao nhiêu connection tải ảnh, các luồng luôn tập trung tải tuần tự theo thứ tự trang (001, 002, 003...)
                    // Không bao giờ cho phép một luồng nhảy cóc vượt quá xa so với trang chưa hoàn thành nhỏ nhất!
                    int totalPagesInChapter = imageUrls.Count;
                    var pageCompleted = new bool[totalPagesInChapter];
                    int lowestIncompleteIndex = 0;
                    int nextDispatchIndex = 0;
                    var windowLock = new object();
                    using var windowSignal = new SemaphoreSlim(0, int.MaxValue);

                    var workerTasks = Enumerable.Range(0, maxParallel).Select(async workerId =>
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            (string Url, int Index)? workItem = null;

                            lock (windowLock)
                            {
                                if (nextDispatchIndex >= totalPagesInChapter)
                                {
                                    break;
                                }

                                // Chỉ dispatch nếu trang nằm trong cửa sổ trượt: [lowestIncompleteIndex, lowestIncompleteIndex + maxParallel)
                                if (nextDispatchIndex < lowestIncompleteIndex + maxParallel)
                                {
                                    int dispatchIdx = nextDispatchIndex++;
                                    workItem = (imageUrls[dispatchIdx], dispatchIdx);
                                }
                            }

                            if (workItem == null)
                            {
                                // Chờ một trang nhỏ hơn hoàn tất để trượt cửa sổ sang trang tiếp theo
                                try
                                {
                                    await windowSignal.WaitAsync(ct).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                    break;
                                }
                                continue;
                            }

                            var item = workItem.Value;
                            if (ct.IsCancellationRequested) break;
                            await WaitIfPausedAsync(ct).ConfigureAwait(false);

                            int pIdx = item.Index;
                            try
                            {
                                string pageUrl = item.Url;
                                string pageExt = GetSafeImageExtensionFromUrl(pageUrl);
                                string pageBaseName = $"{(pIdx + 1):D3}";
                                string pageFileName = $"{pageBaseName}{pageExt}";
                                string pageFilePath = Path.Combine(chapterDir, pageFileName);

                                // Kiểm tra file tồn tại: Chỉ chấp nhận nếu file > 512 bytes và có trong manifest.
                                // Hỗ trợ linh hoạt cả khi file đã được chuyển đổi sang định dạng khác (.jpg, .webp, .png, .gif) do In-flight Image Enhancement
                                bool isExistingValid = false;
                                string? foundValidFile = null;

                                if (File.Exists(pageFilePath))
                                {
                                    var fi = new FileInfo(pageFilePath);
                                    if (fi.Length > 512)
                                    {
                                        foundValidFile = pageFilePath;
                                    }
                                    else
                                    {
                                        try { File.Delete(pageFilePath); } catch {}
                                        lock (manifest) { manifest.Remove(pageFileName); }
                                    }
                                }

                                if (foundValidFile == null)
                                {
                                    foreach (var altExt in new[] { ".jpg", ".jpeg", ".webp", ".png", ".gif" })
                                    {
                                        string altPath = Path.Combine(chapterDir, $"{pageBaseName}{altExt}");
                                        if (File.Exists(altPath))
                                        {
                                            var fi = new FileInfo(altPath);
                                            if (fi.Length > 512)
                                            {
                                                foundValidFile = altPath;
                                                break;
                                            }
                                        }
                                    }
                                }

                                if (foundValidFile != null)
                                {
                                    string foundName = Path.GetFileName(foundValidFile);
                                    if (manifest.ContainsKey(pageFileName) || manifest.ContainsKey(foundName) || manifest.ContainsValue(pageUrl))
                                    {
                                        isExistingValid = true;
                                    }
                                }

                                bool downloaded = false;
                                if (isExistingValid)
                                {
                                    // Nếu file đã tồn tại hợp lệ nhưng người dùng bật "Xử lý ảnh khi tải",
                                    // kiểm tra xem file đã được nâng cấp sang định dạng đích mong muốn (hoặc tối ưu nén) chưa:
                                    if (AutoProcessImages && foundValidFile != null && File.Exists(foundValidFile))
                                    {
                                        try
                                        {
                                            var options = GetImageEnhancerOptions?.Invoke() ?? new ImageEnhancerOptions { OverwriteOriginal = true, OutputFormat = "original" };
                                            string expectedTargetExt = ImageEnhancerService.GetTargetExtension(foundValidFile, options.OutputFormat);
                                            string currentExt = Path.GetExtension(foundValidFile).ToLowerInvariant();

                                            bool isSameExt = string.Equals(currentExt, expectedTargetExt, StringComparison.OrdinalIgnoreCase);
                                            bool hasCustomFilter = Math.Abs(options.Contrast) > 0.01f || Math.Abs(options.Brightness) > 0.01f ||
                                                                   Math.Abs(options.Saturation - 100f) > 0.01f || options.Sharpness > 0.01f ||
                                                                   options.NoiseReduce > 0 || options.Quality < 95;
                                            bool needsEnhance = !isSameExt || hasCustomFilter;
                                            if (needsEnhance)
                                            {
                                                bool enhanced = ImageEnhancerService.TryEnhanceImageFile(foundValidFile, options, out string finalEnhancedPath);
                                                if (enhanced)
                                                {
                                                    string fmtLabel = string.IsNullOrWhiteSpace(options.OutputFormat) ? "ORIGINAL" : options.OutputFormat.ToUpperInvariant();
                                                    LogEmitted?.Invoke("INFO", $"[Xử lý ảnh có sẵn] Đã nâng cấp '{Path.GetFileName(foundValidFile)}' -> '{Path.GetFileName(finalEnhancedPath)}' ({fmtLabel}, Q:{options.Quality})");
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            LogEmitted?.Invoke("WARN", $"[Xử lý ảnh có sẵn] Lỗi tối ưu '{Path.GetFileName(foundValidFile)}': {ex.Message}");
                                        }
                                    }

                                    downloaded = true;
                                }
                                else
                                {
                                    string? pageReaderUrl = (chapter.ReaderUrls != null && pIdx < chapter.ReaderUrls.Count) ? chapter.ReaderUrls[pIdx] : null;
                                    downloaded = await DownloadImageWithRetryAsync(pageUrl, pageFilePath, chapter.Url, pageReaderUrl, ct).ConfigureAwait(false);
                                }

                                int doneNow = downloaded ? Interlocked.Increment(ref downloadedCount) : downloadedCount;
                                if (downloaded)
                                {
                                    lock (manifest)
                                    {
                                        manifest[pageFileName] = pageUrl;
                                        // Ghi nhận cả định dạng thực tế nếu In-flight Image Enhancer đã chuyển đổi sang (.jpg, .webp, v.v.)
                                        foreach (var altExt in new[] { ".jpg", ".jpeg", ".webp", ".png", ".gif" })
                                        {
                                            string altName = $"{pageBaseName}{altExt}";
                                            if (File.Exists(Path.Combine(chapterDir, altName)))
                                            {
                                                manifest[altName] = pageUrl;
                                            }
                                        }
                                    }
                                    chapter.DownloadedPages = doneNow;
                                }

                                book.UpdateProgressThrottled(
                                    completedChapters,
                                    totalChapters,
                                    doneNow,
                                    totalPagesInChapter,
                                    $"Đang tải {chapter.Title}: {doneNow}/{totalPagesInChapter} trang",
                                    force: doneNow == totalPagesInChapter);

                                CalculateSpeed();
                                ReportOverallProgress(book, $"{book.Title} • {chapter.Title} ({doneNow}/{totalPagesInChapter})", force: false);

                                long nowTick = Environment.TickCount64;
                                long minEngineInterval = OperatingSystem.IsAndroid() ? 1000 : 200;
                                if (doneNow == totalPagesInChapter || nowTick - Interlocked.Read(ref _lastEngineProgressTick) >= minEngineInterval)
                                {
                                    Interlocked.Exchange(ref _lastEngineProgressTick, nowTick);
                                    ProgressUpdated?.Invoke();
                                }

                                // Thu gom rác nhẹ định kỳ mỗi 20 ảnh trên Android để giải phóng ngay bộ nhớ đệm socket/stream, triệt tiêu OOM
                                if (OperatingSystem.IsAndroid() && doneNow % 20 == 0)
                                {
                                    try
                                    {
                                        GC.Collect(0, GCCollectionMode.Optimized, false, false);
                                    }
                                    catch { }
                                }
                            }
                            finally
                            {
                                // Đánh dấu hoàn tất trang này và trượt cửa sổ
                                lock (windowLock)
                                {
                                    pageCompleted[pIdx] = true;
                                    while (lowestIncompleteIndex < totalPagesInChapter && pageCompleted[lowestIncompleteIndex])
                                    {
                                        lowestIncompleteIndex++;
                                    }
                                    // Đánh thức các worker đang đợi cửa sổ trượt
                                    windowSignal.Release(maxParallel);
                                }
                            }
                        }

                        // Kích hoạt đánh thức worker còn lại nếu có
                        try { windowSignal.Release(1); } catch { }
                    });

                    await Task.WhenAll(workerTasks).ConfigureAwait(false);



                    if (ct.IsCancellationRequested)

                    {

                        SaveManifest(manifestFile, manifest);

                        chapter.ImageUrls.Clear();

                        chapter.ReaderUrls.Clear();

                        book.Status = "Stopped";

                        book.StatusMessage = "Đã dừng bởi người dùng";

                        if (book.ProgressPercentage <= 0 && book.DownloadedChapters <= 0 && book.DownloadedPages <= 0)

                        {

                            book.DetailProgressText = "Đã dừng";

                        }

                        ProgressUpdated?.Invoke();

                        return;

                    }



                    if (downloadedCount == 0)

                    {

                        chapter.Status = "Error";

                        LogEmitted?.Invoke("ERROR", $"[{book.Title}] Tải ảnh thất bại cho '{chapter.Title}' (0/{imageUrls.Count} trang tải được).");

                    }

                    else

                    {

                        completedChapters++;

                        chapter.Status = "Completed";

                        book.UpdateProgress(completedChapters, totalChapters, imageUrls.Count, imageUrls.Count);

                        ReportOverallProgress(book, $"{book.Title} • Đã xong {chapter.Title}", force: false);

                    }

                }

                else

                {

                    if (ct.IsCancellationRequested)

                    {

                        book.Status = "Stopped";

                        book.StatusMessage = "Đã dừng bởi người dùng";

                        if (book.ProgressPercentage <= 0 && book.DownloadedChapters <= 0 && book.DownloadedPages <= 0)

                        {

                            book.DetailProgressText = "Đã dừng";

                        }

                        ProgressUpdated?.Invoke();

                        return;

                    }



                    string txtPath = Path.Combine(chapterDir, "chapter_info.txt");

                    if (!File.Exists(txtPath))

                    {

                        await File.WriteAllTextAsync(txtPath, $"Title: {chapter.Title}\nURL: {chapter.Url}\nDate: {DateTime.Now}", ct).ConfigureAwait(false);

                    }

                    chapter.Status = "Error";

                    LogEmitted?.Invoke("WARN", $"[{book.Title}] Không thể bóc tách ảnh cho '{chapter.Title}'.");



                    consecutiveExtractErrors++;

                    if (consecutiveExtractErrors >= 3)

                    {

                        book.Status = "Error";

                        book.StatusMessage = "Comix.to giới hạn kết nối hoặc yêu cầu xác thực. Tạm dừng để bảo vệ IP.";

                        LogEmitted?.Invoke("ERROR", $"[{book.Title}] Không thể bóc tách ảnh 3 chương liên tiếp. Tạm dừng để bảo vệ IP.");

                        ProgressUpdated?.Invoke();

                        break;

                    }

                    await Task.Delay(1500, ct).ConfigureAwait(false);

                }



                SaveManifest(manifestFile, manifest);

                // Giải phóng danh sách URL trang ảnh của chapter vừa tải xong để chống tích tụ RAM khi tải > 30 truyện

                chapter.ImageUrls.Clear();

                chapter.ReaderUrls.Clear();

                if (OperatingSystem.IsAndroid())

                {

                    try

                    {

                        GC.Collect(1, GCCollectionMode.Optimized, false, false);

                    }

                    catch { }

                }

                ProgressUpdated?.Invoke();

            }



            if (ct.IsCancellationRequested)

            {

                book.Status = "Stopped";

                book.StatusMessage = "Đã dừng bởi người dùng";

                book.DetailProgressText = "Đã dừng";

                ProgressUpdated?.Invoke();

                return;

            }



            book.Status = "Completed";

            book.StatusMessage = $"Đã tải xong toàn bộ {completedChapters}/{totalChapters} chương";

            book.UpdateProgress(completedChapters, totalChapters, 0, 0);

            book.DetailProgressText = $"Hoàn tất {completedChapters}/{totalChapters} chaps • 100%";

            LogEmitted?.Invoke("SUCCESS", $"Đã hoàn tất truyện: '{book.Title}' ({completedChapters} chaps) -> {book.LocalDirectory}");

            ReportOverallProgress(book, $"{book.Title} • Đã hoàn tất", force: true);

        }

        catch (OperationCanceledException)

        {

            book.Status = "Stopped";

            book.StatusMessage = "Tác vụ tải đã dừng";

            book.DetailProgressText = "Đã dừng";

        }

        catch (Exception ex)

        {

            book.Status = "Error";

            book.StatusMessage = ex.Message;

            book.DetailProgressText = "Lỗi tải";

            LogEmitted?.Invoke("ERROR", $"Lỗi tải '{book.Title}': {ex.Message}");

            SoundNotificationService.Instance.PlaySound(SoundNotificationType.DownloadError);

        }

        finally

        {

            ProgressUpdated?.Invoke();

        }

    }



    public static bool IsValidImageData(byte[]? data)

    {

        if (data == null || data.Length < 512) return false;



        // 1. JPEG: FF D8 FF

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)

            return true;



        // 2. PNG: 89 50 4E 47 0D 0A 1A 0A

        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&

            data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)

            return true;



        // 3. WebP: RIFF (bytes 0-3) + WEBP (bytes 8-11)

        if (data.Length >= 12 &&

            data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&

            data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)

            return true;



        // 4. GIF: GIF87a / GIF89a

        if (data.Length >= 6 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38)

            return true;



        // 5. BMP: BM (42 4D)

        if (data.Length >= 2 && data[0] == 0x42 && data[1] == 0x4D)

            return true;



        // 6. AVIF / HEIC: ftyp at offset 4

        if (data.Length >= 12 && data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70)

            return true;



        // 7. TIFF

        if (data.Length >= 4 && ((data[0] == 0x49 && data[1] == 0x49 && data[2] == 0x2A && data[3] == 0x00) ||

                                (data[0] == 0x4D && data[1] == 0x4D && data[2] == 0x00 && data[3] == 0x2A)))

            return true;



        return false;

    }



    private async Task<bool> DownloadImageWithRetryAsync(string imageUrl, string destinationPath, string refererUrl, string? readerUrl, CancellationToken ct)

    {

        bool isMangaDex = imageUrl.Contains("mangadex.network", StringComparison.OrdinalIgnoreCase) ||

                          imageUrl.Contains("mangadex.org", StringComparison.OrdinalIgnoreCase);



        bool isNettruyen = imageUrl.Contains("kcgsbok.com", StringComparison.OrdinalIgnoreCase) ||

                           imageUrl.Contains("/nettruyen/", StringComparison.OrdinalIgnoreCase) ||

                           (refererUrl != null && refererUrl.Contains("nettruyen", StringComparison.OrdinalIgnoreCase));



        bool isEHentai = imageUrl.Contains("hath.network", StringComparison.OrdinalIgnoreCase) ||

                         imageUrl.Contains("ehgt.org", StringComparison.OrdinalIgnoreCase) ||

                         imageUrl.Contains("e-hentai.org", StringComparison.OrdinalIgnoreCase) ||

                         imageUrl.Contains("exhentai.org", StringComparison.OrdinalIgnoreCase) ||

                         (refererUrl != null && (refererUrl.Contains("e-hentai") || refererUrl.Contains("exhentai")));



        var candidateUrls = new List<string> { imageUrl };



        // Nếu link là trang reader E-Hentai (/s/...), resolve ra direct image URL trước

        if (isEHentai && (imageUrl.Contains("/s/") || !imageUrl.Contains("/h/")))

        {

            try

            {

                string rUrl = !string.IsNullOrEmpty(readerUrl) ? readerUrl : imageUrl;

                string? resolvedUrl = await _scraperService.ResolveFreshEHentaiImageUrlAsync(rUrl, null, ct).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(resolvedUrl))

                {

                    candidateUrls.Insert(0, resolvedUrl);

                }

            }

            catch {}

        }



        if (isMangaDex)

        {

            var mdMatch = System.Text.RegularExpressions.Regex.Match(imageUrl, @"/(?<type>data|data-saver)/(?<hash>[a-f0-9]+)/(?<file>[^?#]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (mdMatch.Success)

            {

                string mHash = mdMatch.Groups["hash"].Value;

                string mFile = mdMatch.Groups["file"].Value;

                string officialData = $"https://uploads.mangadex.org/data/{mHash}/{mFile}";

                string officialSaver = $"https://uploads.mangadex.org/data-saver/{mHash}/{mFile}";

                if (!candidateUrls.Contains(officialData, StringComparer.OrdinalIgnoreCase)) candidateUrls.Add(officialData);

                if (!candidateUrls.Contains(officialSaver, StringComparer.OrdinalIgnoreCase)) candidateUrls.Add(officialSaver);

            }

        }

        else if (isNettruyen)

        {

            var ntMatch = System.Text.RegularExpressions.Regex.Match(imageUrl, @"^(?<prefix>https?://)image\d*(?<suffix>\.[^/]+/.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (ntMatch.Success)

            {

                string prefix = ntMatch.Groups["prefix"].Value;

                string suffix = ntMatch.Groups["suffix"].Value;



                var candidateServers = new List<string> { "image2", "image1", "image3", "image4", "image" };

                if (!string.IsNullOrEmpty(_lastSuccessfulNettruyenCdnServer))

                {

                    candidateServers.Remove(_lastSuccessfulNettruyenCdnServer);

                    candidateServers.Insert(0, _lastSuccessfulNettruyenCdnServer);

                }



                foreach (var srv in candidateServers)

                {

                    string candidate = $"{prefix}{srv}{suffix}";

                    if (!candidateUrls.Contains(candidate, StringComparer.OrdinalIgnoreCase))

                    {

                        candidateUrls.Add(candidate);

                    }

                }



                if (!string.IsNullOrEmpty(_lastSuccessfulNettruyenCdnServer))

                {

                    string preferredUrl = $"{prefix}{_lastSuccessfulNettruyenCdnServer}{suffix}";

                    if (candidateUrls.Remove(preferredUrl))

                    {

                        candidateUrls.Insert(0, preferredUrl);

                    }

                }

            }

        }



        // Candidate fallback cho Hitomi.la: Tu dong bo sung subdomain du phong w1 <-> w2
        if (imageUrl.Contains("gold-usergeneratedcontent.net", StringComparison.OrdinalIgnoreCase) ||
            imageUrl.Contains("hitomi.la", StringComparison.OrdinalIgnoreCase))
        {
            if (imageUrl.Contains("//w1.gold-usergeneratedcontent.net/"))
            {
                string altW2 = imageUrl.Replace("//w1.gold-usergeneratedcontent.net/", "//w2.gold-usergeneratedcontent.net/");
                if (!candidateUrls.Contains(altW2, StringComparer.OrdinalIgnoreCase)) candidateUrls.Add(altW2);
            }
            else if (imageUrl.Contains("//w2.gold-usergeneratedcontent.net/"))
            {
                string altW1 = imageUrl.Replace("//w2.gold-usergeneratedcontent.net/", "//w1.gold-usergeneratedcontent.net/");
                if (!candidateUrls.Contains(altW1, StringComparer.OrdinalIgnoreCase)) candidateUrls.Add(altW1);
            }
            else if (imageUrl.Contains("//w3.gold-usergeneratedcontent.net/"))
            {
                string altW1 = imageUrl.Replace("//w3.gold-usergeneratedcontent.net/", "//w1.gold-usergeneratedcontent.net/");
                string altW2 = imageUrl.Replace("//w3.gold-usergeneratedcontent.net/", "//w2.gold-usergeneratedcontent.net/");
                if (!candidateUrls.Contains(altW1, StringComparer.OrdinalIgnoreCase)) candidateUrls.Add(altW1);
                if (!candidateUrls.Contains(altW2, StringComparer.OrdinalIgnoreCase)) candidateUrls.Add(altW2);
            }
        }

        // Candidate fallback cho Luscious (full-res 1280x0 -> 640x0 -> 315x0)

        if (imageUrl.Contains(".1280x0."))

        {

            string c640 = imageUrl.Replace(".1280x0.", ".640x0.");

            string c315 = imageUrl.Replace(".1280x0.", ".315x0.");

            if (!candidateUrls.Contains(c640, StringComparer.OrdinalIgnoreCase)) candidateUrls.Add(c640);

            if (!candidateUrls.Contains(c315, StringComparer.OrdinalIgnoreCase)) candidateUrls.Add(c315);

        }



        // Candidate fallback cho nhentai / hentaiforce / galleries khi tải ảnh full-res

        var numPageMatch = System.Text.RegularExpressions.Regex.Match(imageUrl, @"(?<prefix>https?://[^/]+/galleries/\d+/(?<page>\d+))\.(?<ext>webp|jpg|jpeg|png)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (numPageMatch.Success)

        {

            string pPrefix = numPageMatch.Groups["prefix"].Value;

            string pExt = numPageMatch.Groups["ext"].Value.ToLowerInvariant();

            string pPage = numPageMatch.Groups["page"].Value;

            string[] otherExts = ["jpg", "png", "webp"];

            foreach (var ext in otherExts)

            {

                if (!string.Equals(ext, pExt, StringComparison.OrdinalIgnoreCase))

                {

                    string altUrl = $"{pPrefix}.{ext}";

                    if (!candidateUrls.Contains(altUrl, StringComparer.OrdinalIgnoreCase)) candidateUrls.Add(altUrl);

                }

            }

            // Thêm cả thumbnail URL làm fallback an toàn cuối cùng

            string thumbFallback = $"{pPrefix.Substring(0, pPrefix.Length - pPage.Length)}{pPage}t.{pExt}";

            if (!candidateUrls.Contains(thumbFallback, StringComparer.OrdinalIgnoreCase)) candidateUrls.Add(thumbFallback);

        }



        for (int attempt = 1; attempt <= 4; attempt++)

        {

            if (ct.IsCancellationRequested) return false;



            byte[]? data = null;



            for (int cIdx = 0; cIdx < candidateUrls.Count; cIdx++)

            {

                string targetUrl = candidateUrls[cIdx];

                if (ct.IsCancellationRequested) return false;



                try

                {

                    using var req = new HttpRequestMessage(HttpMethod.Get, targetUrl);



                    string? effectiveReferer = refererUrl;

                    if (isMangaDex)

                    {

                        effectiveReferer = "https://mangadex.org/";

                    }

                    else if (targetUrl.Contains("suicaodex", StringComparison.OrdinalIgnoreCase) ||

                             (refererUrl != null && refererUrl.Contains("suicaodex", StringComparison.OrdinalIgnoreCase)))

                    {

                        effectiveReferer = "https://suicaodex.com/";

                    }

                    else if (isNettruyen)

                    {

                        effectiveReferer = !string.IsNullOrEmpty(refererUrl) ? refererUrl : "https://nettruyenviet10.com/";

                    }

                    else if (targetUrl.Contains("imggo.net", StringComparison.OrdinalIgnoreCase))

                    {

                        effectiveReferer = "https://daomeoden.net/";

                    }

                    else if (targetUrl.Contains("moetruyen", StringComparison.OrdinalIgnoreCase) || (refererUrl != null && refererUrl.Contains("moetruyen", StringComparison.OrdinalIgnoreCase)))

                    {

                        effectiveReferer = "https://moetruyen.net/";

                    }

                    else if (targetUrl.Contains("cuutruyen", StringComparison.OrdinalIgnoreCase) || (refererUrl != null && refererUrl.Contains("cuutruyen", StringComparison.OrdinalIgnoreCase)))

                    {

                        effectiveReferer = "https://cuutruyen.net/";

                    }

                    else if (targetUrl.Contains("pubtranxzyzz", StringComparison.OrdinalIgnoreCase) ||

                             (refererUrl != null && refererUrl.Contains("sayhentai", StringComparison.OrdinalIgnoreCase)))

                    {

                        effectiveReferer = "https://sayhentai.cx/";

                    }

                    else if (targetUrl.Contains("hentaicdn.com", StringComparison.OrdinalIgnoreCase) || targetUrl.Contains("hentai.direct", StringComparison.OrdinalIgnoreCase))

                    {

                        effectiveReferer = "https://hentai2read.com/";

                    }

                    else if (targetUrl.Contains("vi-hentai", StringComparison.OrdinalIgnoreCase))

                    {

                        effectiveReferer = "https://vi-hentai.pro/";

                    }

                    else if (targetUrl.Contains("gold-usergeneratedcontent.net", StringComparison.OrdinalIgnoreCase) ||

                             targetUrl.Contains("hitomi.la", StringComparison.OrdinalIgnoreCase) ||

                             (refererUrl != null && refererUrl.Contains("hitomi", StringComparison.OrdinalIgnoreCase)))

                    {

                        effectiveReferer = "https://hitomi.la/";

                    }

                    else if (targetUrl.Contains("ehgt.org", StringComparison.OrdinalIgnoreCase) ||

                             targetUrl.Contains("e-hentai.org", StringComparison.OrdinalIgnoreCase) ||

                             targetUrl.Contains("exhentai.org", StringComparison.OrdinalIgnoreCase) ||

                             targetUrl.Contains("hath.network", StringComparison.OrdinalIgnoreCase) ||

                             (refererUrl != null && (refererUrl.Contains("e-hentai") || refererUrl.Contains("exhentai"))))

                    {

                        effectiveReferer = "https://e-hentai.org/";

                        req.Headers.TryAddWithoutValidation("Cookie", "nw=1; nw=always");

                    }

                    else if (targetUrl.Contains("hentaiforce", StringComparison.OrdinalIgnoreCase) ||

                             (refererUrl != null && refererUrl.Contains("hentaiforce", StringComparison.OrdinalIgnoreCase)))

                    {

                        effectiveReferer = "https://hentaiforce.net/";

                    }



                    bool isComixOrWyr = targetUrl.Contains("wyr.", StringComparison.OrdinalIgnoreCase) ||

                                        targetUrl.Contains("blogofscott.site", StringComparison.OrdinalIgnoreCase) ||

                                        targetUrl.Contains("madisonscorner.site", StringComparison.OrdinalIgnoreCase) ||

                                        targetUrl.Contains("johnsnotes.site", StringComparison.OrdinalIgnoreCase) ||

                                        targetUrl.Contains("rxn.", StringComparison.OrdinalIgnoreCase) ||

                                        targetUrl.Contains(".site/", StringComparison.OrdinalIgnoreCase) ||

                                        targetUrl.Contains("softtouchgoods.site", StringComparison.OrdinalIgnoreCase) ||

                                        (refererUrl != null && refererUrl.Contains("comix", StringComparison.OrdinalIgnoreCase));



                    if (isComixOrWyr)

                    {

                        // CDN Comix (*.site, softtouchgoods.site...) nghiêm cấm gửi Referer comix.to (nếu gửi sẽ bị chặn 403 Forbidden).

                        // Chỉ gửi Origin comix.to và các header trình duyệt cross-site để tải mượt mà 100%.

                        req.Headers.TryAddWithoutValidation("Origin", "https://comix.to");

                        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "image");

                        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");

                        req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");

                    }

                    else

                    {

                        if (string.IsNullOrEmpty(effectiveReferer))

                        {

                            if (Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri))

                            {

                                effectiveReferer = $"{targetUri.Scheme}://{targetUri.Host}/";

                            }

                        }



                        if (!string.IsNullOrEmpty(effectiveReferer))

                        {

                            req.Headers.Add("Referer", effectiveReferer);

                        }

                    }



                    req.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");

                    req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");



                    using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                    if (res.IsSuccessStatusCode)

                    {

                        byte[] rawBytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

                        

                        // Kiểm tra tính hợp lệ dữ liệu ảnh (Chống hoàn toàn lỗi placeholder 15 bytes hoặc HTML lỗi)

                        if (IsValidImageData(rawBytes))

                        {

                            data = rawBytes;

                            if (isNettruyen)

                            {

                                var m = System.Text.RegularExpressions.Regex.Match(targetUrl, @"https?://(?<srv>image\d*)\.", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                                if (m.Success)

                                {

                                    _lastSuccessfulNettruyenCdnServer = m.Groups["srv"].Value;

                                }

                            }

                            break;

                        }

                        else

                        {

                            // Phát hiện file 15 bytes rác hoặc response lỗi -> Kích hoạt failover sang Node khác cho E-Hentai

                            if (isEHentai && !string.IsNullOrEmpty(readerUrl))

                            {

                                try

                                {

                                    string? freshUrl = await _scraperService.ResolveFreshEHentaiImageUrlAsync(readerUrl, targetUrl, ct).ConfigureAwait(false);

                                    if (!string.IsNullOrWhiteSpace(freshUrl) && !candidateUrls.Contains(freshUrl, StringComparer.OrdinalIgnoreCase))

                                    {

                                        candidateUrls.Add(freshUrl);

                                        LogEmitted?.Invoke("INFO", $"[E-Hentai Failover] Node Hath '{targetUrl}' trả về file hỏng ({rawBytes?.Length ?? 0}b). Đã tự động chuyển sang Node mới: {freshUrl}");

                                    }

                                }

                                catch {}

                            }

                        }

                    }

                    else if (isEHentai && !string.IsNullOrEmpty(readerUrl))

                    {

                        // HTTP status lỗi (404/503 Hath node offline) -> Đổi Node ngay

                        try

                        {

                            string? freshUrl = await _scraperService.ResolveFreshEHentaiImageUrlAsync(readerUrl, targetUrl, ct).ConfigureAwait(false);

                            if (!string.IsNullOrWhiteSpace(freshUrl) && !candidateUrls.Contains(freshUrl, StringComparer.OrdinalIgnoreCase))

                            {

                                candidateUrls.Add(freshUrl);

                            }

                        }

                        catch {}

                    }

                }

                catch (OperationCanceledException)

                {

                    return false;

                }

                catch (UnauthorizedAccessException)

                {

                    throw;

                }

                catch

                {

                    // Thử candidate URL tiếp theo

                }

            }



            // Tầng 4 dự phòng cho MangaDex qua DoH + SniFragmentStream + Proxy

            if ((data == null || !IsValidImageData(data)) && isMangaDex)

            {

                foreach (string targetUrl in candidateUrls)

                {

                    if (ct.IsCancellationRequested) return false;

                    try

                    {

                        byte[]? mdData = await MangaDexNetworkService.Instance.DownloadBytesAsync(targetUrl, "https://mangadex.org/", ct).ConfigureAwait(false);

                        if (IsValidImageData(mdData))

                        {

                            data = mdData;

                            break;

                        }

                    }

                    catch { }

                }

            }



            // Tầng 5 dự phòng cho E-Hentai: Nếu tất cả candidate cũ đều hỏng, reload fresh reader HTML lấy node mới tinh

            if ((data == null || !IsValidImageData(data)) && isEHentai && !string.IsNullOrEmpty(readerUrl))

            {

                try

                {

                    string? freshUrl = await _scraperService.ResolveFreshEHentaiImageUrlAsync(readerUrl, null, ct).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(freshUrl) && !candidateUrls.Contains(freshUrl, StringComparer.OrdinalIgnoreCase))

                    {

                        candidateUrls.Add(freshUrl);

                    }

                }

                catch {}

            }



            // Chỉ lưu khi dữ liệu là ảnh THỰC SỰ hợp lệ (Dung lượng >= 512b và đúng Magic Bytes)

            if (IsValidImageData(data))

            {

                try

                {

                    await File.WriteAllBytesAsync(destinationPath, data!, ct).ConfigureAwait(false);

                    Interlocked.Add(ref _totalBytesDownloadedInWindow, data!.Length);



                    if (AutoProcessImages && File.Exists(destinationPath))
                    {
                        try
                        {
                            var options = GetImageEnhancerOptions?.Invoke() ?? new ImageEnhancerOptions { OverwriteOriginal = true, OutputFormat = "original" };
                            bool enhanced = ImageEnhancerService.TryEnhanceImageFile(destinationPath, options, out string finalEnhancedPath);
                            if (enhanced)
                            {
                                destinationPath = finalEnhancedPath;
                                string fmtLabel = string.IsNullOrWhiteSpace(options.OutputFormat) ? "ORIGINAL" : options.OutputFormat.ToUpperInvariant();
                                LogEmitted?.Invoke("INFO", $"[Xử lý ảnh khi tải] Đã tối ưu '{Path.GetFileName(destinationPath)}' -> '{Path.GetFileName(finalEnhancedPath)}' ({fmtLabel}, Q:{options.Quality}, Contrast:{options.Contrast:+0;-0;0}, Bright:{options.Brightness:+0;-0;0})");
                            }
                        }
                        catch (Exception ex)
                        {
                            LogEmitted?.Invoke("WARN", $"[Xử lý ảnh khi tải] Lỗi tối ưu '{Path.GetFileName(destinationPath)}': {ex.Message}");
                        }
                    }

                    if (AutoSplitLongImages && File.Exists(destinationPath))

                    {

                        try

                        {

                            bool wasSplit = ImageSplitterService.TrySplitImageFile(destinationPath, AutoSplitHeight, AutoSplitQuality);

                            if (wasSplit)

                            {

                                LogEmitted?.Invoke("INFO", $"[Cắt ảnh dài] Đã tự động cắt ảnh '{Path.GetFileName(destinationPath)}' (ngưỡng {AutoSplitHeight}px)");

                            }

                        }

                        catch { }

                    }



                    return true;

                }

                catch (UnauthorizedAccessException uex)

                {

                    LogEmitted?.Invoke("ERROR", $"[Lỗi quyền bộ nhớ] Từ chối truy cập ghi tệp '{Path.GetFileName(destinationPath)}': {uex.Message}");



                    if (OperatingSystem.IsAndroid())

                    {

                        try

                        {

                            string safeAppStorage = GetAppSpecificExternalPath();

                            string safeFallbackPath = destinationPath.Replace("/storage/emulated/0/Download/ComicDownloads", safeAppStorage, StringComparison.OrdinalIgnoreCase);

                            string fallbackDir = Path.GetDirectoryName(safeFallbackPath)!;

                            if (!Directory.Exists(fallbackDir)) Directory.CreateDirectory(fallbackDir);

                            File.WriteAllBytes(safeFallbackPath, data!);
                            if (AutoProcessImages && File.Exists(safeFallbackPath))
                            {
                                try
                                {
                                    var options = GetImageEnhancerOptions?.Invoke() ?? new ImageEnhancerOptions { OverwriteOriginal = true, OutputFormat = "original" };
                                    bool enhanced = ImageEnhancerService.TryEnhanceImageFile(safeFallbackPath, options, out string finalEnhancedPath);
                                    if (enhanced)
                                    {
                                        safeFallbackPath = finalEnhancedPath;
                                        string fmtLabel = string.IsNullOrWhiteSpace(options.OutputFormat) ? "ORIGINAL" : options.OutputFormat.ToUpperInvariant();
                                        LogEmitted?.Invoke("INFO", $"[Xử lý ảnh khi tải] Đã tối ưu '{Path.GetFileName(safeFallbackPath)}' -> '{Path.GetFileName(finalEnhancedPath)}' ({fmtLabel}, Q:{options.Quality})");
                                    }
                                }
                                catch { }
                            }

                            Interlocked.Add(ref _totalBytesDownloadedInWindow, data!.Length);

                            LogEmitted?.Invoke("SUCCESS", $"[Lưu an toàn] Đã chuyển hướng lưu ảnh vào thư mục riêng của app: {Path.GetFileName(safeFallbackPath)}");

                            return true;

                        }

                        catch { }

                    }



                    return false;

                }

                catch { }

            }



            if (attempt == 4)

            {

                LogEmitted?.Invoke("WARN", $"Tải ảnh thất bại sau {attempt} lần '{imageUrl}'");

            }

            await Task.Delay(350 * attempt, ct).ConfigureAwait(false);

        }



        // Dọn dẹp an toàn: Nếu file tạo ra bị lỗi hoặc < 512 bytes thì xóa bỏ ngay

        try

        {

            if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length < 512)

            {

                File.Delete(destinationPath);

            }

        }

        catch {}



        return false;

    }



    private long _lastReportOverallProgressTick = 0;



    /// <summary>

    /// Báo cáo tiến trình tổng thể của toàn bộ hàng đợi tải (hỗ trợ cả 1 truyện và nhiều truyện).

    /// Đảm bảo con số phần trăm (%) trên Bong bóng nổi (Bubble) và Notification luôn được tính toán

    /// nhất quán, tăng dần mượt mà theo tổng tiến độ của toàn bộ các truyện đang tham gia đợt tải.

    /// </summary>

    public void ReportOverallProgress(ComicBookItem? currentBook = null, string? currentDetail = null, bool force = false)

    {

        long now = Environment.TickCount64;

        long minInterval = OperatingSystem.IsAndroid() ? 1000 : 200;

        if (!force && now - Interlocked.Read(ref _lastReportOverallProgressTick) < minInterval)

        {

            return;

        }

        Interlocked.Exchange(ref _lastReportOverallProgressTick, now);



        List<ComicBookItem> batch;

        lock (_queueLock)

        {

            if (_liveQueueSource == null || _liveQueueSource.Count == 0)

            {

                if (currentBook != null)

                {

                    batch = new List<ComicBookItem> { currentBook };

                }

                else

                {

                    return;

                }

            }

            else

            {

                batch = _liveQueueSource.Where(c => c != null && c.IsChecked && !c.IsParallelSplitParent).ToList();

                if (batch.Count == 0 && currentBook != null)

                {

                    batch = new List<ComicBookItem> { currentBook };

                }

            }

        }



        if (batch.Count == 0) return;



        double overallPercent;

        string title;

        string detail;



        if (batch.Count == 1)

        {

            var single = batch[0];

            overallPercent = Math.Clamp(single.ProgressPercentage, 0, 100);

            title = _isPaused ? "Tải Truyện (Tạm dừng)" : "Tải Truyện";

            detail = currentDetail ?? $"{single.Title} ({overallPercent:0}%)";

        }

        else

        {

            // Nhiều truyện: Tính tổng tiến độ dựa trên toàn bộ các truyện tham gia đợt tải

            double totalProgressSum = 0;

            int completedCount = 0;



            foreach (var b in batch)

            {

                if (IsCompletedStatus(b.Status))

                {

                    totalProgressSum += 100.0;

                    completedCount++;

                }

                else if (string.Equals(b.Status, "Downloading", StringComparison.OrdinalIgnoreCase))

                {

                    totalProgressSum += Math.Clamp(b.ProgressPercentage, 0, 100);

                }

                else

                {

                    if (b.TotalChapters > 0 && b.DownloadedChapters > 0)

                    {

                        double ratio = (double)b.DownloadedChapters / b.TotalChapters * 100.0;

                        totalProgressSum += Math.Clamp(ratio, 0, 100);

                    }

                }

            }



            overallPercent = Math.Clamp(totalProgressSum / batch.Count, 0, 100);

            title = _isPaused

                ? $"Tải Truyện (Tạm dừng - {completedCount}/{batch.Count})"

                : $"Tải Truyện ({completedCount}/{batch.Count})";



            if (!string.IsNullOrWhiteSpace(currentDetail))

            {

                detail = $"[{completedCount}/{batch.Count}] {currentDetail}";

            }

            else if (currentBook != null)

            {

                detail = $"[{completedCount}/{batch.Count}] {currentBook.Title} ({overallPercent:0}%)";

            }

            else

            {

                detail = $"Đang tải danh sách {batch.Count} truyện ({completedCount} xong) • {overallPercent:0}%";

            }

        }



        BackgroundExecutionService.Instance.ReportProgress("download_queue", title, detail, overallPercent, true);

    }



    private void CalculateSpeed()

    {

        var now = DateTime.UtcNow;

        var elapsed = (now - _lastSpeedCheckTime).TotalSeconds;

        if (elapsed >= 1.0)

        {

            long bytes = Interlocked.Exchange(ref _totalBytesDownloadedInWindow, 0);

            double speedKb = (bytes / 1024.0) / elapsed;

            if (speedKb > 1024.0)

            {

                CurrentSpeedText = $"{(speedKb / 1024.0):F1} MB/s";

            }

            else

            {

                CurrentSpeedText = $"{speedKb:F1} KB/s";

            }

            _lastSpeedCheckTime = now;

        }

    }



    private static Dictionary<string, string> LoadManifest(string path)

    {

        try

        {

            if (File.Exists(path))

            {

                string json = File.ReadAllText(path);

                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();

            }

        }

        catch {}

        return new Dictionary<string, string>();

    }



    private static bool AreAllManifestFilesValid(string chapterDir, Dictionary<string, string> manifest)

    {

        if (manifest == null || manifest.Count == 0 || !Directory.Exists(chapterDir))

            return false;



        try

        {

            foreach (var fileName in manifest.Keys)

            {

                if (string.IsNullOrWhiteSpace(fileName)) return false;

                string fullPath = Path.Combine(chapterDir, fileName);

                var fi = new FileInfo(fullPath);

                if (!fi.Exists || fi.Length <= 512)

                {

                    return false;

                }

            }

            return true;

        }

        catch

        {

            return false;

        }

    }



    private static void SaveManifest(string path, Dictionary<string, string> manifest)

    {

        try

        {

            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(path, json);

        }

        catch {}

    }



    public static string MakeSafeFilename(string name, int maxBytes = 80)

    {

        if (string.IsNullOrWhiteSpace(name)) return "Unnamed";

        

        // 1. Loại bỏ các ký tự không hợp lệ trên mọi hệ điều hành (Windows, Linux, Android)

        var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();

        var sb = new System.Text.StringBuilder();

        foreach (char c in name)

        {

            if (invalidChars.Contains(c) || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|' || c < 32)

            {

                sb.Append('_');

            }

            else

            {

                sb.Append(c);

            }

        }



        string cleaned = System.Text.RegularExpressions.Regex.Replace(sb.ToString().Trim(), @"\s+", " ").Trim('.', ' ', '_');

        if (string.IsNullOrWhiteSpace(cleaned)) return "Comic";



        // 2. Giới hạn độ dài theo byte UTF-8 (Android NAME_MAX = 255 bytes, đặt maxBytes 80 để an toàn cho cả path con)

        byte[] utf8Bytes = System.Text.Encoding.UTF8.GetBytes(cleaned);

        if (utf8Bytes.Length <= maxBytes) return cleaned;



        // Cắt ngắn chuỗi an toàn tại ranh giới ký tự

        int targetBytes = Math.Max(16, maxBytes - 7);

        while (cleaned.Length > 0 && System.Text.Encoding.UTF8.GetByteCount(cleaned) > targetBytes)

        {

            cleaned = cleaned.Substring(0, cleaned.Length - 1);

        }

        cleaned = cleaned.TrimEnd('.', ' ', '_');



        string hash = BitConverter.ToString(System.Security.Cryptography.MD5.HashData(utf8Bytes)).Replace("-", "").Substring(0, 6).ToLowerInvariant();

        return $"{cleaned}_{hash}";

    }



    public static string MakeSafeChapterDirName(string? title, int fallbackIndex, int maxBytes = 60)

    {

        if (string.IsNullOrWhiteSpace(title)) return $"Chapter {fallbackIndex}";



        string t = title.Trim();

        // Nếu title chứa "Chapter 1 - Tên truyện dài dằng dặc", chỉ rút gọn lại Chapter 1

        var matchChapter = System.Text.RegularExpressions.Regex.Match(t, @"^(Chapter|Chương|Chap|Vol|Volume)\s*[\d\.]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (matchChapter.Success && t.Length > 40)

        {

            string prefix = matchChapter.Value.Trim();

            string rest = t.Substring(matchChapter.Length).Trim('-', ' ', ':', '_');

            if (rest.Length > 25)

            {

                rest = rest.Substring(0, 25).Trim();

            }

            t = string.IsNullOrWhiteSpace(rest) ? prefix : $"{prefix} - {rest}";

        }



        return MakeSafeFilename(t, maxBytes);

    }



    public static string SafeCreateDirectory(string path)

    {

        try

        {

            if (!Directory.Exists(path))

            {

                Directory.CreateDirectory(path);

            }

            return path;

        }

        catch (Exception ex) when (ex is PathTooLongException || ex is IOException)

        {

            try

            {

                string? parent = Path.GetDirectoryName(path);

                string leaf = Path.GetFileName(path);

                if (!string.IsNullOrEmpty(parent))

                {

                    string safeLeaf = MakeSafeFilename(leaf, 40);

                    string fallbackPath = Path.Combine(parent, safeLeaf);

                    if (!Directory.Exists(fallbackPath))

                    {

                        Directory.CreateDirectory(fallbackPath);

                    }

                    return fallbackPath;

                }

            }

            catch { }

            throw;

        }

    }



    public static string GetSafeImageExtensionFromUrl(string url)

    {

        if (string.IsNullOrWhiteSpace(url)) return ".jpg";

        string path = url;

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri != null)

        {

            path = uri.AbsolutePath;

        }

        else

        {

            int queryIndex = url.IndexOf('?');

            if (queryIndex >= 0) path = url.Substring(0, queryIndex);

        }



        string ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();

        switch (ext)

        {

            case ".jpg":

            case ".jpeg":

            case ".png":

            case ".gif":

            case ".bmp":

            case ".webp":

            case ".avif":

                return ext;

            default:

                return ".jpg";

        }

    }

}



