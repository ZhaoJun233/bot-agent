namespace BotAgent.Services.Resilience;

public readonly record struct ProtocolBackoffDecision(
    bool Allowed,
    string Text,
    bool Restricted,
    string ReasonCode);

public sealed record ProtocolBackoffSnapshot(
    string SourceKey,
    DateTimeOffset Until,
    string ReasonCode,
    int Retcode,
    string Action);
