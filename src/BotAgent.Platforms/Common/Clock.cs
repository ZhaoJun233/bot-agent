using BotAgent.Domain.Ports;

namespace BotAgent.Platforms;

/// <summary>Bind the module before use; only the composition root supplies time.</summary>
public static class PlatformClock
{
    private static IClock? _source;

    public static IClock Current => Volatile.Read(ref _source)
        ?? throw new InvalidOperationException("clock_not_initialized");

    /// <summary>Same-instance repeats are allowed; null or rebinding never changes time.</summary>
    public static void Initialize(IClock source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var previous = Interlocked.CompareExchange(ref _source, source, null);
        if (previous is not null && !ReferenceEquals(previous, source))
            throw new InvalidOperationException("clock_already_initialized");
    }
}

internal static class Clock
{
    public static DateTimeOffset Now => PlatformClock.Current.Now;
    public static DateTimeOffset UtcNow => PlatformClock.Current.UtcNow;
    public static DateTime LocalDateTime => PlatformClock.Current.LocalDateTime;
    public static long TickCount => PlatformClock.Current.TickCount;
    public static Task Delay(TimeSpan delay, CancellationToken ct = default) => PlatformClock.Current.Delay(delay, ct);
    public static Task Delay(int ms, CancellationToken ct = default) => PlatformClock.Current.Delay(ms, ct);
}
