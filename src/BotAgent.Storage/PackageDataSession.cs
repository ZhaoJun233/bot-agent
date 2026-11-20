using BotAgent.Domain.Ops;
using BotAgent.Domain.Ports;
using BotAgent.Services.Ops;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace BotAgent.Adapters.Persistence;

internal sealed class PackageDataSession(SqliteConnection connection, HashSet<string> tables, string[] statePrefixes,
    bool writable, string? today, bool audit, OperationReceiptBinding? receipts) : IPackageDataSession, IDisposable
{
    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
        { "coalesce", "datetime", "date", "count", "sum", "min", "max", "avg", "total", "length", "lower", "upper", "abs", "hex", "ifnull", "nullif" };
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private int _disposed;
    private Exception? _failure;
    private bool _legacyAccess;

    public object? Scalar(string sql, params PackageDataParameter[] parameters)
        => Guard(() => Sql(sql, parameters, command =>
        {
            var value = command.ExecuteScalar();
            return value is DBNull ? null : value;
        }));

    public int Execute(string sql, params PackageDataParameter[] parameters)
        => Guard(() => Sql(sql, parameters, command => command.ExecuteNonQuery()));

    public IReadOnlyList<IReadOnlyList<object?>> Query(string sql, params PackageDataParameter[] parameters)
        => Guard(() => Sql(sql, parameters, command =>
        {
            using var reader = command.ExecuteReader();
            var rows = new List<IReadOnlyList<object?>>();
            do
            {
                while (reader.Read())
                {
                    var row = new object?[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(Array.AsReadOnly(row));
                }
            } while (reader.NextResult());
            return (IReadOnlyList<IReadOnlyList<object?>>)rows.AsReadOnly();
        }));

    public string? ReadState(string key) => Guard(() =>
    {
        ValidateStateKey(key);
        return AppDatabase.GetMetaInTransaction(connection, key);
    });

    public void WriteState(string key, string value) => Guard(() =>
    {
        RequireWrite();
        ValidateStateKey(key);
        ArgumentNullException.ThrowIfNull(value);
        AppDatabase.SetMetaInTransaction(connection, key, value);
        return true;
    });

    public TokenUsageSnapshot ReadUsage(string tenantId) => Guard(() =>
    {
        RequireWrite(); // UTC rollover can persist a reset.
        if (today is null) throw new InvalidOperationException("package_data_denied");
        return TokenUsageStore.ReadInTransaction(connection, TokenUsageStore.NormalizeTenant(tenantId), today);
    });

    public void AppendAudit(AuditEvent auditEvent) => Guard(() =>
    {
        RequireWrite();
        if (!audit) throw new InvalidOperationException("package_data_denied");
        new AuditLogStore().AppendInTransaction(connection, auditEvent);
        return true;
    });

    public void RecordReceipt(OperationReceiptWrite receipt) => Guard(() =>
    {
        RequireWrite();
        if (receipts is null) throw new InvalidOperationException("package_data_denied");
        OperationRecoveryStore.Record(connection, receipts, receipt);
        return true;
    });

    // Test-only recovery probes retain the old raw callback scenarios; this is not a package capability.
    internal T Legacy<T>(Func<SqliteConnection, T> operation) => Guard(() =>
    {
        RequireWrite();
        _legacyAccess = true;
        // Existing trusted callbacks retain their ABI, but their connection never returns to the package pool.
        SqliteConnection.ClearPool(connection);
        return operation(connection);
    });

    internal TokenUsageSnapshot RecordLegacyUsage(string tenant, int prompt, int completion) => Guard(() =>
    {
        RequireWrite();
        if (today is null) throw new InvalidOperationException("package_data_denied");
        return TokenUsageStore.RecordInTransaction(connection, tenant, today, prompt, completion);
    });

    private T Sql<T>(string sql, PackageDataParameter[] parameters, Func<SqliteCommand, T> execute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            if (parameter.Value is not null and not string and not long and not int and not short and not byte
                and not double and not float and not bool and not byte[])
                throw new ArgumentException("package_data_invalid_parameter");
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
        var denied = false;
        strdelegate_authorizer authorize = (_, action, first, second, database, origin) =>
        {
            var decision = !string.IsNullOrEmpty(origin) ? raw.SQLITE_DENY : action switch
            {
                raw.SQLITE_SELECT => raw.SQLITE_OK,
                raw.SQLITE_READ when database == "main" && tables.Contains(first) => raw.SQLITE_OK,
                // SQLite omits database/column for COUNT(*). The dedicated pool never receives legacy
                // connections; this interface cannot create temp objects or attach another database.
                raw.SQLITE_READ when !_legacyAccess && string.IsNullOrEmpty(database) && string.IsNullOrEmpty(second)
                    && tables.Contains(first) => raw.SQLITE_OK,
                raw.SQLITE_INSERT or raw.SQLITE_UPDATE or raw.SQLITE_DELETE
                    when writable && database == "main" && tables.Contains(first) => raw.SQLITE_OK,
                raw.SQLITE_FUNCTION when Functions.Contains(second) => raw.SQLITE_OK,
                _ => raw.SQLITE_DENY
            };
            if (decision == raw.SQLITE_DENY) denied = true;
            return decision;
        };
        if (raw.sqlite3_set_authorizer(connection.Handle, authorize, null) != raw.SQLITE_OK)
            throw new InvalidOperationException("package_data_guard_failed");
        try { return execute(command); }
        catch (SqliteException error) when (denied || error.SqliteErrorCode == 23)
        { throw new InvalidOperationException("package_data_denied", error); }
        finally
        {
            if (raw.sqlite3_set_authorizer(connection.Handle, (strdelegate_authorizer)null!, null) != raw.SQLITE_OK)
                throw new InvalidOperationException("package_data_guard_failed");
            GC.KeepAlive(authorize);
        }
    }

    private void ValidateStateKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Any(char.IsControl)
            || !statePrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length))
            throw new InvalidOperationException("package_data_denied");
    }

    private static void Denied() => throw new InvalidOperationException("package_data_denied");
    private void RequireWrite() { if (!writable) Denied(); }

    private T Guard<T>(Func<T> action)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("package_data_session_expired");
            EnsureSuccessful();
            return action();
        }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref _failure, error, null);
            throw;
        }
    }

    internal void EnsureSuccessful()
    {
        if (Volatile.Read(ref _failure) is not null) throw new InvalidOperationException("package_data_transaction_aborted");
        if (Volatile.Read(ref _disposed) != 0 || Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("package_data_session_expired");
    }

    internal void RejectNested() => Guard<bool>(() => throw new InvalidOperationException("package_data_nested_operation"));

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
