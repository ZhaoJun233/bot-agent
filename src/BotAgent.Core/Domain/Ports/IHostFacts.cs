namespace BotAgent.Domain.Ports;

/// <summary>
/// 宿主事实（只读）端口（由 <c>Adapters/Persistence/HostMetrics</c> 实现）。
///
/// 为什么要有它：健康日报要说“容器用了多少 / 上限多少 / 负载多高”，而这些是**读 /proc、/sys 读来的**。
/// 读法（路径、cgroup v1/v2 的差异、非 Linux 上根本没有）是 IO 实现细节，不该长在用例里；
/// 用例只该看到“上限是多少”这个结论。有了端口，日报的文案可以在**不读宿主**的前提下用假数据测。
/// </summary>
public interface IHostFacts
{
    /// <summary>容器内存上限（拿不到 = null）。</summary>
    long? MemoryLimitBytes();

    /// <summary>1 分钟负载均值（非 Linux、读不到 = null）。</summary>
    double? LoadAverage();
}
