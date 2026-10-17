using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;

namespace BotAgent.Services.Qq;

/// <summary>A host-created execution lease; message bodies cannot construct or renew it.</summary>
public sealed class PlatformSourceLease
{
    private readonly Func<bool> _isCurrent;
    internal ChannelRouter Owner { get; }
    internal IQqChatSource Source { get; }
    public PlatformMessageOrigin Origin { get; }
    public PlatformContext Context { get; }
    public CancellationToken Lifetime { get; }
    public bool IsCurrent => !Lifetime.IsCancellationRequested && _isCurrent();

    internal PlatformSourceLease(ChannelRouter owner, IQqChatSource source, PlatformMessageOrigin origin,
        CancellationToken lifetime, Func<bool> isCurrent)
    {
        Owner = owner;
        Source = source;
        Origin = origin;
        Context = new(origin.Driver.PlatformId, origin.Driver.AccountScope, origin.Driver.InstanceId);
        Lifetime = lifetime;
        _isCurrent = isCurrent;
    }

    public override string ToString() => "PlatformSourceLease";
}

/// <summary>Per-execution routing only; queues must explicitly capture and restore the lease.</summary>
public static class PlatformSourceExecution
{
    private static readonly AsyncLocal<PlatformSourceLease?> Ambient = new();
    public static PlatformSourceLease? Current => Ambient.Value;

    internal static IDisposable Enter(PlatformSourceLease? source)
    {
        var previous = Ambient.Value;
        Ambient.Value = source;
        return new Restore(previous);
    }

    private sealed class Restore(PlatformSourceLease? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = previous;
        }
    }
}
