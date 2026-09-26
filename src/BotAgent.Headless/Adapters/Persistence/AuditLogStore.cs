using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BotAgent.Services.Ops;
using BotAgent.Adapters.Time;

namespace BotAgent.Adapters.Persistence;

/// <summary>SQLite SHA-256 审计链。只接受结构化摘要，不接受聊天正文或密钥值。</summary>
public sealed class AuditLogStore : IAuditChain
{
    private const string EmptyHash = "";
    private readonly object _gate = new();

    public void Append(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        var eventType = Require(auditEvent.EventType, nameof(auditEvent.EventType));
        var actorId = Require(auditEvent.ActorId, nameof(auditEvent.ActorId));
        var tenantId = Require(auditEvent.TenantId, nameof(auditEvent.TenantId));
        var detail = Require(auditEvent.ActionDetail, nameof(auditEvent.ActionDetail));
        var policy = Require(auditEvent.PolicyVersion, nameof(auditEvent.PolicyVersion));
        var createdAt = Clock.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        lock (_gate)
        {
            AppDatabase.Write(conn =>
            {
                var previous = ReadPreviousHash(conn) ?? EmptyHash;
                var current = ComputeHash(eventType, actorId, tenantId, detail, policy, previous, createdAt);
                AppDatabase.Exec(conn, """
                    INSERT INTO security_audit_log
                      (event_type, actor_id, tenant_id, action_detail, policy_version, prev_hash, curr_hash, created_at)
                    VALUES ($eventType, $actorId, $tenantId, $detail, $policy, $previous, $current, $createdAt);
                    """,
                    ("$eventType", eventType), ("$actorId", actorId), ("$tenantId", tenantId),
                    ("$detail", detail), ("$policy", policy), ("$previous", previous),
                    ("$current", current), ("$createdAt", createdAt));
            });
        }
    }

    public AuditVerification Verify()
    {
        var rows = AppDatabase.Query("""
            SELECT id, event_type, actor_id, tenant_id, action_detail, policy_version,
                   prev_hash, curr_hash, created_at
            FROM security_audit_log ORDER BY id;
            """, r => new AuditRow(
                r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8)));

        var previous = EmptyHash;
        foreach (var row in rows)
        {
            if (!string.Equals(row.PrevHash, previous, StringComparison.Ordinal))
            {
                return new AuditVerification(false, row.Id, "prev_hash_mismatch", rows.Count);
            }

            var expected = ComputeHash(row.EventType, row.ActorId, row.TenantId, row.ActionDetail,
                row.PolicyVersion, row.PrevHash, row.CreatedAt);
            if (!string.Equals(row.CurrHash, expected, StringComparison.Ordinal))
            {
                return new AuditVerification(false, row.Id, "curr_hash_mismatch", rows.Count);
            }

            previous = row.CurrHash;
        }

        return new AuditVerification(true, null, null, rows.Count);
    }

    private static string? ReadPreviousHash(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT curr_hash FROM security_audit_log ORDER BY id DESC LIMIT 1";
        var value = command.ExecuteScalar();
        return value is null || value is DBNull ? null : value.ToString();
    }

    private static string ComputeHash(string eventType, string actorId, string tenantId,
        string detail, string policy, string previous, string createdAt)
    {
        var canonical = string.Join("\n", eventType, actorId, tenantId, detail, policy, previous, createdAt);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string Require(string? value, string name)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("审计字段不能为空", name) : value.Trim();

    private sealed record AuditRow(int Id, string EventType, string ActorId, string TenantId,
        string ActionDetail, string PolicyVersion, string PrevHash, string CurrHash, string CreatedAt);
}
