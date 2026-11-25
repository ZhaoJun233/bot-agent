using BotAgent.Domain.Ports;
using BotAgent.Domain.Ops;
using Microsoft.Data.Sqlite;

namespace BotAgent.Adapters.Persistence;

/// <summary>Created by trusted composition, then supplied as the resource-scoped Core interface.</summary>
public sealed class PackageDataStore : IPackageDataStore
{
    private static readonly AsyncLocal<PackageDataSession?> Current = new();
    private readonly HashSet<string> _tables;
    private readonly string[] _statePrefixes;
    private readonly bool _usage;
    private readonly bool _audit;
    private readonly OperationReceiptBinding? _receipts;

    public PackageDataStore(IEnumerable<string> tables, IEnumerable<string> statePrefixes,
        bool allowUsage = false, bool allowAudit = false)
        : this(tables, statePrefixes, allowUsage, allowAudit, null) { }

    public PackageDataStore(IEnumerable<string> tables, IEnumerable<string> statePrefixes,
        bool allowUsage, bool allowAudit, OperationReceiptBinding? receipts)
    {
        _tables = new(tables, StringComparer.OrdinalIgnoreCase);
        _statePrefixes = statePrefixes.ToArray();
        if (_tables.Any(table => string.IsNullOrEmpty(table) || table.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)
            || table.Equals("meta", StringComparison.OrdinalIgnoreCase) || table.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            || _statePrefixes.Any(prefix => string.IsNullOrEmpty(prefix) || !prefix.EndsWith(':') || prefix.Any(char.IsControl)
                || OperationRecoveryStore.Prefix.StartsWith(prefix, StringComparison.Ordinal)
                || prefix.StartsWith(OperationRecoveryStore.Prefix, StringComparison.Ordinal)))
            throw new ArgumentException("package_data_invalid_grant");
        _usage = allowUsage;
        _audit = allowAudit;
        if (receipts is not null) OperationRecoveryStore.ValidateBinding(receipts);
        _receipts = receipts;
    }

    public T Read<T>(Func<IPackageDataSession, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        RejectNested();
        using var connection = AppDatabase.OpenIsolated();
        using var transaction = connection.BeginTransaction(deferred: true);
        return Invoke(connection, writable: false, today: null, operation);
    }

    public T Write<T>(Func<IPackageDataSession, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        RejectNested();
        // Sample before database access, preserving the clock-before-storage contract.
        var today = _usage ? TokenUsageStore.Today() : null;
        T result = default!;
        AppDatabase.WriteIsolated(connection => result = Invoke(connection, writable: true, today, operation));
        return result;
    }

    private T Invoke<T>(SqliteConnection connection, bool writable, string? today, Func<IPackageDataSession, T> operation)
    {
        using var session = new PackageDataSession(connection, _tables, _statePrefixes, writable, today, _audit, _receipts);
        Current.Value = session;
        try
        {
            var result = operation(session);
            var resultType = result?.GetType();
            if (result is Task or ValueTask || resultType is { IsGenericType: true }
                && resultType.GetGenericTypeDefinition() == typeof(ValueTask<>))
                throw new InvalidOperationException("package_data_async_operation");
            session.EnsureSuccessful();
            return result;
        }
        finally { Current.Value = null; }
    }

    private static void RejectNested() => Current.Value?.RejectNested();
}
