using BotAgent.Domain.Ops;

namespace BotAgent.Domain.Ports;

/// <summary>Reads current-day usage; a UTC-day change durably resets the counters.</summary>
public interface ITokenUsageReader
{
    TokenUsageSnapshot GetUsage(string tenantId);
}
