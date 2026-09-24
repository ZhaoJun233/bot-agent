namespace BotAgent.Domain.Ports;

/// <summary>
/// 时间端口（V3 / architecture-optimization.md §6.3）：**冷却、节流、TTL 类的判定一律读它**，
/// 而不是直接读系统时钟 —— 这样这些规则可以在测试里用假时钟做到毫秒级确定性（不必真的等几秒）。
///
/// 为什么同时提供 <see cref="LocalDateTime" />：这个项目从第一天起就用**本地时间**做
/// 日志时间戳与"最近活跃"（`DateTime.Now` 口径）。改口径是行为变更，这里只把"读哪儿"收口，
/// 不偷偷把本地时间换成 UTC。
///
/// 为什么还要 <see cref="TickCount" />：节流日志（"同一来源每分钟最多一条"）用的是**单调时钟**
/// （<c>Clock.TickCount</c>）—— 它不受系统时间被调整的影响，比 <c>Now</c> 更适合做限流。
/// </summary>
public interface IClock
{
    /// <summary>当前本地时间（带偏移）。冷却 / TTL / 有效期判定用它。</summary>
    DateTimeOffset Now { get; }

    /// <summary>当前 UTC 时间。只在与外部协议对时间的地方用（token 过期、HTTP 日期）。</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>当前本地时间（<see cref="DateTime" /> 口径）：日志时间戳与既有 DateTime 判定用它。</summary>
    DateTime LocalDateTime { get; }

    /// <summary>单调毫秒（不受系统时间调整影响）：节流窗口用它。</summary>
    long TickCount { get; }

    /// <summary>等待（节奏、退让、合并窗口）。真实实现就是 <c>Task.Delay</c>；假时钟可以立刻返回。</summary>
    Task Delay(TimeSpan delay, CancellationToken ct = default);

    /// <summary>同上，毫秒写法（既有调用点大多是 <c>Task.Delay(150)</c> 这种）。</summary>
    Task Delay(int millisecondsDelay, CancellationToken ct = default);
}
