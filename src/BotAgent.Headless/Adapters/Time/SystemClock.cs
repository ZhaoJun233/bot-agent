using BotAgent.Domain.Ports;

namespace BotAgent.Adapters.Time;

/// <summary>
/// 时间的**唯一实现**：整个 <c>src/</c> 里只有这个文件读系统时钟
/// （架构探针把这条钉成硬规则 —— 见 <c>ArchitectureProbe</c> 的"读系统时间只允许出现在这里"）。
///
/// 别的文件一律走 <see cref="Clock" /> 这个进程内入口（或它们各自注入的 <see cref="IClock" />）；
/// 想在测试里把时间钉死，进程启动时 <c>Clock.Use(fake)</c> 一次即可。
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateTime LocalDateTime => DateTime.Now;

    public long TickCount => Environment.TickCount64;

    public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.Delay(delay, ct);

    public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Task.Delay(millisecondsDelay, ct);
}

/// <summary>
/// 进程内的时钟入口（默认走 <see cref="SystemClock" />）。
///
/// 为什么是"静态入口 + 可换实现"而不是把 <see cref="IClock" /> 注进**每一个**构造函数：
///   • 时间在这个进程里本来就是**环境**（一次回复会经过 6~7 个组件，各自拿一份注入反而要保证它们拿到同一个）；
///   • 很多读点在**属性初始化器与 DTO 默认值**里（<c>ChatMessage.Timestamp</c>、<c>BotConversation.LastTime</c>、
///     会话台账的 <c>CreatedAt/UpdatedAt</c>）—— 构造函数注入**够不到**这些地方；
///   • 目标是"确定性测试"：进程启动时换一次，全进程（含上面那些默认值）一起确定，比逐个注入更彻底。
/// 需要局部替身时仍然可以注入 <see cref="IClock" />（端口就在 <c>Domain/Ports</c>，接口是公开的）。
/// </summary>
public static class Clock
{
    private static IClock _current = new SystemClock();

    /// <summary>当前生效的实现（换实现是原子的）。</summary>
    public static IClock Current => Volatile.Read(ref _current);

    public static DateTimeOffset Now => Current.Now;

    public static DateTimeOffset UtcNow => Current.UtcNow;

    public static DateTime LocalDateTime => Current.LocalDateTime;

    public static long TickCount => Current.TickCount;

    public static Task Delay(TimeSpan delay, CancellationToken ct = default) => Current.Delay(delay, ct);

    public static Task Delay(int millisecondsDelay, CancellationToken ct = default) => Current.Delay(millisecondsDelay, ct);

    /// <summary>换一个时钟实现（**测试专用**：进程启动时调一次；生产代码不要碰）。</summary>
    public static void Use(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Interlocked.Exchange(ref _current, clock);
    }

    /// <summary>恢复系统时钟（测试收尾用）。</summary>
    public static void UseSystemClock() => Use(new SystemClock());
}
