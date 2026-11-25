using System.Text.Json.Serialization;

namespace BotAgent.Domain.Ops;

/// <summary>Trusted composition binds ownership and implementation identity, never the submitted request.</summary>
public sealed record OperationReceiptBinding(string Owner, string Contract, string ContractVersion, string Generation);
public sealed record OperationReceiptWrite(string CallerScope, string IdempotencyKey, string RequestDigest, string State);

/// <summary>Safe historical evidence only; business result bytes and raw caller/key never enter this record.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationRecoveryReceipt(int FormatVersion, string Owner, string ScopeDigest, string KeyDigest,
    string RequestDigest, string Contract, string ContractVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Generation, string State, string Provenance);
