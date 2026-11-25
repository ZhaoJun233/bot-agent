using BotAgent.Domain.Ops;
using BotAgent.Services.Ops;

namespace BotAgent.Domain.Ports;

/// <summary>One synchronous repository operation; sessions cannot escape its creating thread or callback.</summary>
public interface IPackageDataStore
{
    T Read<T>(Func<IPackageDataSession, T> operation);
    T Write<T>(Func<IPackageDataSession, T> operation);
}

/// <summary>Host-bound resources only. Values are materialized; no connection or lazy reader is exposed.</summary>
public interface IPackageDataSession
{
    object? Scalar(string sql, params PackageDataParameter[] parameters);
    IReadOnlyList<IReadOnlyList<object?>> Query(string sql, params PackageDataParameter[] parameters);
    int Execute(string sql, params PackageDataParameter[] parameters);
    string? ReadState(string key);
    void WriteState(string key, string value);
    TokenUsageSnapshot ReadUsage(string tenantId);
    void AppendAudit(AuditEvent auditEvent);
    void RecordReceipt(OperationReceiptWrite receipt);
}

public readonly record struct PackageDataParameter(string Name, object? Value);
