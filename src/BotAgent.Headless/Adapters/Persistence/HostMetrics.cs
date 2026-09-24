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
public sealed class HostMetrics : IHostFacts
{
    /// <summary>容器内存上限（cgroup v2 的 memory.max / v1 的 memory.limit_in_bytes；拿不到 = null）。</summary>
    public long? MemoryLimitBytes()
    {
        foreach (var path in new[] { "/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory/memory.limit_in_bytes" })
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var raw = File.ReadAllText(path).Trim();
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
            if (!File.Exists("/proc/loadavg"))
            {
                return null; // 非 Linux：不报这一项
            }

            var first = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }
}
