namespace BotAgent.Services;

/// <summary>Owns one exclusively locked delivery snapshot until disposal.</summary>
public interface IOfficialDeliverySnapshot : IDisposable
{
    public const int MaxBytes = 32 * 1024 * 1024;

    /// <summary>Null means absent. Corruption and unavailable storage must throw.</summary>
    string? Load();

    /// <summary>Publish one complete, flushed snapshot atomically or throw. Never silently discard a write.</summary>
    void Commit(string snapshot);
}
