using BotAgent.Domain.Ports;

namespace BotAgent.Adapters.Persistence;

/// <summary>Transitional adapter over the host facade of the sole Storage database.</summary>
public sealed class FeishuWebhookDedupStore : IWebhookDedupStore
{
    public bool TryRegister(IReadOnlyList<string> keys, DateTimeOffset seenAt, TimeSpan ttl)
        => AppDatabase.TryRegisterFeishuWebhooks(keys, seenAt, ttl);
}
