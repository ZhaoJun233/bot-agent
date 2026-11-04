using BotAgent.Domain.Ports;

namespace BotAgent.Engine.Runtime;

/// <summary>
/// Engine 的时钟入口：Host 在启动时把进程时钟绑定进来，Engine 不直接读系统时间。
/// 未绑定时使用零时钟（确定性、无系统时间读取），便于纯单元测试；生产装配必须显式绑定。
/// </summary>
public static class EngineClock
{
    private static IClock _current = new ZeroClock();

    public static void Initialize(IClock clock)
        => Volatile.Write(ref _current, clock ?? throw new ArgumentNullException(nameof(clock)));

    public static IClock Current => Volatile.Read(ref _current);

    public static DateTimeOffset Now => Current.Now;
    public static DateTimeOffset UtcNow => Current.UtcNow;
    public static DateTime LocalDateTime => Current.LocalDateTime;
    public static long TickCount => Current.TickCount;

    public static Task Delay(TimeSpan delay, CancellationToken ct = default) => Current.Delay(delay, ct);
    public static Task Delay(int millisecondsDelay, CancellationToken ct = default) => Current.Delay(millisecondsDelay, ct);

    private sealed class ZeroClock : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.UnixEpoch;
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        public DateTime LocalDateTime => DateTime.UnixEpoch;
        public long TickCount => 0;
        public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.CompletedTask;
        public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Task.CompletedTask;
    }
}

