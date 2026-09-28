using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Trình tự động nhận diện và tính toán số luồng CPU logic (Logical Processors) tối đa của hệ thống
/// cho cả Windows, Linux và Android (hỗ trợ toàn diện máy đơn chip, máy đa socket, AMD Threadripper, Intel Xeon 64/72/128 luồng...).
/// </summary>
public static class CpuTopologyHelper
{
    private static int _cachedMaxThreads = 0;

    private const ushort ALL_PROCESSOR_GROUPS = 0xFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

    /// <summary>
    /// Lấy số luồng CPU logic tối đa mà hệ điều hành và phần cứng hỗ trợ.
    /// </summary>
    public static int GetMaxLogicalProcessorCount()
    {
        if (_cachedMaxThreads > 0)
        {
            return _cachedMaxThreads;
        }

        int count = Environment.ProcessorCount;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Trên Windows (đặc biệt là máy Dual Xeon, Quad Xeon, AMD Threadripper/EPYC có >64 luồng hoặc chia processor group)
                uint allGroupCores = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS);
                if (allGroupCores > 0 && allGroupCores > count)
                {
                    count = (int)allGroupCores;
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                // Trên Linux kiểm tra thêm từ sysfs nếu có
                if (File.Exists("/sys/devices/system/cpu/online"))
                {
                    string online = File.ReadAllText("/sys/devices/system/cpu/online").Trim();
                    int parsed = ParseCpuOnlineRange(online);
                    if (parsed > count)
                    {
                        count = parsed;
                    }
                }
            }
        }
        catch
        {
            // Fallback an toàn về Environment.ProcessorCount
        }

        // Đảm bảo tối thiểu là 1 luồng
        _cachedMaxThreads = Math.Max(1, count);
        return _cachedMaxThreads;
    }

    private static int ParseCpuOnlineRange(string rangeText)
    {
        try
        {
            // Định dạng thường gặp: "0-63" hoặc "0-71" hoặc "0-15,32-47"
            int total = 0;
            var parts = rangeText.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var dash = part.Split('-');
                if (dash.Length == 2 && int.TryParse(dash[0], out int start) && int.TryParse(dash[1], out int end))
                {
                    total += (end - start + 1);
                }
                else if (dash.Length == 1 && int.TryParse(dash[0], out _))
                {
                    total += 1;
                }
            }
            return total > 0 ? total : 0;
        }
        catch
        {
            return 0;
        }
    }
}
