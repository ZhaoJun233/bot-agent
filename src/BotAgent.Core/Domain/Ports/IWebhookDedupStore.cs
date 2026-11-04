namespace BotAgent.Domain.Ports;

/// <summary>Atomic persistent registration of the keys belonging to one webhook.</summary>
public interface IWebhookDedupStore
{
    /// <summary>
    /// Register all keys, unchanged, when none are still within the TTL.
    /// Return false for a duplicate without registering any other key.
    /// Persistence failures must throw, not be reported as duplicates.
    /// </summary>
    bool TryRegister(IReadOnlyList<string> keys, DateTimeOffset seenAt, TimeSpan ttl);
}
