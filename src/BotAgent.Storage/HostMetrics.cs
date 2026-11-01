using System.Diagnostics;
using System.Globalization;
using System.IO;
using BotAgent.Domain.Ports;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 宿主事实（只读）：cgroup 的内存上限、<c>/proc/loadavg</c>。
///
/// 为什么放在持久化层：健康日报要报"容器用了多少 / 上限多少"，而这些是**读文件**读来的 ——
/// 读法（路径与格式、cgroup v1/v2 的差异、非 Linux 上根本没有）属于 IO 实现细节，
/// 不该长在用例里（用例只该看到"内存上限是多少"这个结论）。
/// </summary>
public class HostMetrics : IHostFacts
{
    /// <summary>容器内存上限（cgroup v2 的 memory.max / v1 的 memory.limit_in_bytes；拿不到 = null）。</summary>
    public long? MemoryLimitBytes()
    {
        foreach (var path in new[] { "/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory/memory.limit_in_bytes" })
        {
            try
            {
                if (ReadTrimmed(path) is not { } raw)
                {
                    continue;
                }
                if (long.TryParse(raw, out var value) && value > 0 && value < (1L << 50))
                {
                    return value;
                }
            }
            catch
            {
                // 单个文件读不到就换下一个（容器里这些路径不一定都有）
            }
        }

        return null;
    }

    /// <summary>1 分钟负载均值（非 Linux、或读不到 = null）。</summary>
    public double? LoadAverage()
    {
        try
        {
            var first = ReadTrimmed("/proc/loadavg")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// swap 用量与总量（读 <c>/proc/meminfo</c> 的 SwapTotal / SwapFree，单位 kB）。
    /// 非 Linux、或容器没开 swap = 总量 0，这两种都返回 null：日报与水位里不显示这一项。
    /// </summary>
    public (long Used, long Total)? SwapBytes()
    {
        if (ReadTrimmed("/proc/meminfo") is not { } text)
        {
            return null;
        }

        long? totalKb = null;
        long? freeKb = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("SwapTotal:", StringComparison.Ordinal)) totalKb = ParseKb(line);
            else if (line.StartsWith("SwapFree:", StringComparison.Ordinal)) freeKb = ParseKb(line);
        }

        if (totalKb is not { } total || total <= 0 || freeKb is not { } free)
        {
            return null;
        }

        return (Math.Max(0, total - free) * 1024, total * 1024);
    }

    /// <summary>把 <c>SwapTotal: 2097148 kB</c> 这种行里的数字取出来（单位 kB）。</summary>
    private static long? ParseKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// 读一个系统文件的文本（去掉首尾空白）；读不到 = null。
    ///
    /// 统一走这一个入口：这些 /proc、/sys 路径在非 Linux、受限容器里经常不存在，
    /// 每个调用点各自判断存在性只会让“直接文件 IO”的处数越堆越多（架构探针按处数计棘轮）。
    /// </summary>
    private static string? ReadTrimmed(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 进程启动时刻。进程级的 <c>Process.StartTime</c> 在 Linux 上要读 /proc 的 stat 与 boot time，
    /// 偶尔会因权限或容器时钟漂移抛异常 —— 抛了就返回 null，调用方按“未知”处理。
    /// </summary>
    public DateTimeOffset? ProcessStartTime()
    {
        try
        {
            return new DateTimeOffset(Process.GetCurrentProcess().StartTime);
        }
        catch
        {
            return null;
        }
    }
}
