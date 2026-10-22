namespace BotAgent.Domain.Ops;

/// <summary>熔断器可观察状态快照（metrics 与健康检查用），不含任何正文或敏感凭据。</summary>
public readonly record struct CircuitStatusSnapshot(string Type, string Id, string State);
