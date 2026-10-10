extern alias StorageModule;

using BotAgent.Domain.Ports;

namespace BotAgent.Adapters.Time;

/// <summary>Explicit startup binding; modules depend on IClock, never on the host.</summary>
public static class ClockBindings
{
    /// <summary>Stable source: every operation observes the current host clock, not a startup snapshot.</summary>
    public static IClock Source { get; } = new ForwardingClock();

    public static void InitializePlatforms() => BotAgent.Platforms.PlatformClock.Initialize(Source);
    public static void InitializeStorage()
    {
        StorageModule::BotAgent.Storage.StorageClock.Initialize(Source);
        StorageModule::BotAgent.Storage.StoragePaths.Initialize(BotAgent.Services.AppPaths.RuntimeRoot);
        StorageModule::BotAgent.Storage.StorageLogging.WriteHandler = BotAgent.Services.FileLog.Write;
        StorageModule::BotAgent.Storage.StorageLogging.WarnHandler = BotAgent.Services.FileLog.Warn;
    }

    private sealed class ForwardingClock : IClock
    {
        public DateTimeOffset Now => Clock.Current.Now;
        public DateTimeOffset UtcNow => Clock.Current.UtcNow;
        public DateTime LocalDateTime => Clock.Current.LocalDateTime;
        public long TickCount => Clock.Current.TickCount;
        public Task Delay(TimeSpan delay, CancellationToken ct = default) => Clock.Current.Delay(delay, ct);
        public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Clock.Current.Delay(millisecondsDelay, ct);
    }
}
