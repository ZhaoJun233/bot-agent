using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using Microsoft.Data.Sqlite;

namespace BotAgent.Adapters.Persistence;

/// <summary>One transaction owns usage, limit revisions and durable management receipts.</summary>
public class TenantQuotaStore : ITenantQuotaLedger, ITokenUsageRecorder
{
    private static readonly object Gate = new();
    public const int MaximumReceiptsPerCaller = 4096;
    private static readonly JsonSerializerOptions ReceiptJson = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record Receipt(string Fingerprint, TenantQuotaManagementSnapshot Result);

    void ITenantQuotaLedger.RecordUsage(string tenantId, int promptTokens, int completionTokens)
        => RecordUsage(tenantId, promptTokens, completionTokens);

    void ITokenUsageRecorder.RecordUsage(string tenantId, int promptTokens, int completionTokens)
        => RecordUsage(tenantId, promptTokens, completionTokens);

    public TenantQuotaSnapshot GetQuota(string tenantId) => ReadManagement(tenantId).Quota;

    public TenantQuotaManagementSnapshot ReadManagement(string tenantId)
    {
        var today = Today();
        var tenant = NormalizeTenant(tenantId);
        TenantQuotaManagementSnapshot? result = null;
        lock (Gate) AppDatabase.Write(conn => result = new(Load(conn, tenant, today), Revision(conn, tenant)));
        return result!;
    }

    public TenantQuotaSnapshot SetDailyTokenLimit(string tenantId, int dailyTokenLimit)
    {
        ValidateLimit(dailyTokenLimit);
        var today = Today();
        var tenant = NormalizeTenant(tenantId);
        TenantQuotaSnapshot result = default;
        lock (Gate) AppDatabase.Write(conn =>
        {
            result = ChangeLimit(conn, Load(conn, tenant, today), dailyTokenLimit);
            SetMeta(conn, RevisionKey(tenant), checked(Revision(conn, tenant) + 1).ToString(CultureInfo.InvariantCulture));
        });
        return result;
    }

    /// <summary>Replay is checked before CAS, and the receipt commits with the limit. Scope is the authenticated caller.</summary>
    public TenantQuotaManagementSnapshot SaveManagement(string tenantId, int dailyTokenLimit,
        long expectedRevision, string idempotencyKey, string callerScope,
        Action<SqliteConnection, TenantQuotaSnapshot, TenantQuotaSnapshot>? audit = null)
    {
        ValidateLimit(dailyTokenLimit);
        ValidateOperation(expectedRevision, idempotencyKey, callerScope);
        var today = Today();
        var tenant = NormalizeTenant(tenantId);
        var fingerprint = Hash(JsonSerializer.Serialize(new { tenant, dailyTokenLimit, expectedRevision }));
        TenantQuotaManagementSnapshot? result = null;
        lock (Gate) AppDatabase.Write(conn =>
        {
            var journalKey = JournalKey(callerScope);
            var journal = ReadJournal(conn, journalKey);
            var receiptKey = Hash(idempotencyKey);
            journal.TryGetValue(receiptKey, out var receipt);
            if (receipt is not null)
            {
                if (receipt.Fingerprint != fingerprint) throw new TenantQuotaManagementException("idempotency_conflict");
                result = receipt.Result;
                return;
            }
            if (journal.Count >= MaximumReceiptsPerCaller)
                throw new TenantQuotaManagementException("quota_receipt_capacity");
            var revision = Revision(conn, tenant);
            if (revision != expectedRevision) throw new TenantQuotaManagementException("quota_revision_conflict");
            var before = Load(conn, tenant, today);
            var quota = ChangeLimit(conn, before, dailyTokenLimit);
            var next = checked(revision + 1);
            SetMeta(conn, RevisionKey(tenant), next.ToString(CultureInfo.InvariantCulture));
            result = new(quota, next);
            journal.Add(receiptKey, new Receipt(fingerprint, result));
            SetMeta(conn, journalKey, JsonSerializer.Serialize(journal, ReceiptJson));
            audit?.Invoke(conn, before, quota);
        });
        return result!;
    }

    public TenantQuotaManagementSnapshot? FindManagement(string idempotencyKey, string callerScope)
    {
        ValidateOperation(0, idempotencyKey, callerScope);
        lock (Gate)
        {
            using var conn = AppDatabase.Open();
            return ReadJournal(conn, JournalKey(callerScope)).GetValueOrDefault(Hash(idempotencyKey))?.Result;
        }
    }

    public TenantQuotaSnapshot RecordUsage(string tenantId, int promptTokens, int completionTokens)
    {
        var today = Today();
        var tenant = NormalizeTenant(tenantId);
        TenantQuotaSnapshot result = default;
        lock (Gate) AppDatabase.Write(conn =>
        {
            var quota = Load(conn, tenant, today);
            var prompt = checked(quota.UsedPromptTokens + Math.Max(0, promptTokens));
            var completion = checked(quota.UsedCompletionTokens + Math.Max(0, completionTokens));
            result = quota with { UsedPromptTokens = prompt, UsedCompletionTokens = completion,
                EnergySaving = (long)prompt + completion >= quota.DailyTokenLimit };
            Save(conn, result);
        });
        return result;
    }

    public bool IsEnergySaving(string tenantId) => GetQuota(tenantId).EnergySaving;

    private static TenantQuotaSnapshot Load(SqliteConnection conn, string tenant, string today)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT daily_token_limit, used_prompt_tokens, used_completion_tokens, reset_date, energy_saving FROM tenant_quotas WHERE tenant_id = $id";
        cmd.Parameters.AddWithValue("$id", tenant);
        TenantQuotaSnapshot quota;
        using (var reader = cmd.ExecuteReader())
        {
            quota = reader.Read()
                ? new(tenant, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4) != 0)
                : new(tenant, TenantQuotaPolicy.DefaultDailyTokenLimit, 0, 0, today, false);
        }
        if (quota.ResetDate != today)
            quota = quota with { ResetDate = today, UsedPromptTokens = 0, UsedCompletionTokens = 0, EnergySaving = false };
        Save(conn, quota);
        return quota;
    }

    private static TenantQuotaSnapshot ChangeLimit(SqliteConnection conn, TenantQuotaSnapshot quota, int limit)
    {
        var updated = quota with { DailyTokenLimit = limit, EnergySaving = quota.UsedTotalTokens >= limit };
        Save(conn, updated);
        return updated;
    }

    private static void Save(SqliteConnection conn, TenantQuotaSnapshot quota)
        => AppDatabase.Exec(conn, """
            INSERT INTO tenant_quotas
              (tenant_id, daily_token_limit, used_prompt_tokens, used_completion_tokens, reset_date, energy_saving, updated_at)
            VALUES ($id, $limit, $prompt, $completion, $reset, $energy, datetime('now'))
            ON CONFLICT(tenant_id) DO UPDATE SET
              daily_token_limit = excluded.daily_token_limit,
              used_prompt_tokens = excluded.used_prompt_tokens,
              used_completion_tokens = excluded.used_completion_tokens,
              reset_date = excluded.reset_date, energy_saving = excluded.energy_saving, updated_at = excluded.updated_at;
            """, ("$id", quota.TenantId), ("$limit", quota.DailyTokenLimit), ("$prompt", quota.UsedPromptTokens),
            ("$completion", quota.UsedCompletionTokens), ("$reset", quota.ResetDate), ("$energy", quota.EnergySaving ? 1 : 0));

    private static string? Meta(SqliteConnection conn, string key) => AppDatabase.GetMetaInTransaction(conn, key);
    private static void SetMeta(SqliteConnection conn, string key, string value) => AppDatabase.SetMetaInTransaction(conn, key, value);

    private static long Revision(SqliteConnection conn, string tenant)
    {
        var raw = Meta(conn, RevisionKey(tenant));
        if (raw is null) return 0;
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0)
            throw new TenantQuotaManagementException("quota_management_corrupt");
        return revision;
    }

    private static Dictionary<string, Receipt> ReadJournal(SqliteConnection conn, string key)
    {
        var raw = Meta(conn, key);
        if (raw is null) return new(StringComparer.Ordinal);
        try
        {
            var journal = JsonSerializer.Deserialize<Dictionary<string, Receipt>>(raw, ReceiptJson);
            if (journal is null || journal.Count > MaximumReceiptsPerCaller || journal.Any(entry =>
                entry.Key.Length != 64 || entry.Value?.Fingerprint?.Length != 64 || entry.Value.Result is null
                || entry.Value.Result.Revision < 1 || string.IsNullOrWhiteSpace(entry.Value.Result.Quota.TenantId)
                || !TenantQuotaPolicy.IsValidDailyTokenLimit(entry.Value.Result.Quota.DailyTokenLimit)))
                throw new JsonException();
            return journal;
        }
        catch (JsonException) { throw new TenantQuotaManagementException("quota_management_corrupt"); }
    }

    private static string RevisionKey(string tenant) => "quota:revision:" + Hash(tenant);
    private static string JournalKey(string scope) => "quota:receipts:" + Hash(scope);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Today() => Clock.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string NormalizeTenant(string? tenantId) => string.IsNullOrWhiteSpace(tenantId) ? "default:general" : tenantId.Trim();
    private static void ValidateLimit(int limit)
    {
        if (!TenantQuotaPolicy.IsValidDailyTokenLimit(limit)) throw new ArgumentOutOfRangeException(nameof(limit));
    }
    private static void ValidateOperation(long revision, string key, string scope)
    {
        if (revision < 0 || string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(scope) || scope.Length > 256 || scope.Any(char.IsControl))
            throw new ArgumentException("quota_invalid_operation");
    }
}
