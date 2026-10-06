using BotAgent.Domain.Ports;

namespace BotAgent.Platforms;

internal static class Clock
{
    private static IClock _clock = new DefaultClock();
    public static void Use(IClock clock) => _clock = clock ?? new DefaultClock();
    public static DateTimeOffset Now => _clock.Now;
    public static DateTimeOffset UtcNow => _clock.UtcNow;
    public static DateTime LocalDateTime => _clock.LocalDateTime;
    public static long TickCount => _clock.TickCount;
    public static Task Delay(TimeSpan delay, CancellationToken ct = default) => _clock.Delay(delay, ct);
    public static Task Delay(int ms, CancellationToken ct = default) => _clock.Delay(ms, ct);

    private sealed class DefaultClock : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.Now;
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public DateTime LocalDateTime => DateTime.Now;
        public long TickCount => Environment.TickCount64;
        public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.Delay(delay, ct);
        public Task Delay(int ms, CancellationToken ct = default) => Task.Delay(ms, ct);
    }
}
