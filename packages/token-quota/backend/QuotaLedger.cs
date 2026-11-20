using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Ports;
using BotAgent.Services.Ops;

namespace BotAgent.Packages.TokenQuota;

/// <summary>Quota transactions project independent metering alongside limits and durable receipts.</summary>
internal sealed class QuotaLedger : ITenantQuotaManagement
{
    private readonly IPackageDataStore _data;

    public QuotaLedger(IPackageDataStore data) => _data = data ?? throw new ArgumentNullException(nameof(data));
    public const int MaximumReceiptsPerCaller = 4096;
    private static readonly JsonSerializerOptions ReceiptJson = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record Receipt(string Fingerprint, TenantQuotaManagementSnapshot Result);

    public TenantQuotaSnapshot GetQuota(string tenantId) => ReadManagement(tenantId).Quota;

    public TenantQuotaManagementSnapshot ReadManagement(string tenantId)
    {
        var tenant = NormalizeTenant(tenantId);
        return _data.Write(session => new TenantQuotaManagementSnapshot(Load(session, tenant), Revision(session, tenant)));
    }

    public TenantQuotaSnapshot SetDailyTokenLimit(string tenantId, int dailyTokenLimit)
        => SetDailyTokenLimitWithPrevious(tenantId, dailyTokenLimit).After;

    internal (TenantQuotaSnapshot Before, TenantQuotaSnapshot After) SetDailyTokenLimitWithPrevious(string tenantId, int dailyTokenLimit)
    {
        ValidateLimit(dailyTokenLimit);
        var tenant = NormalizeTenant(tenantId);
        return _data.Write(session =>
        {
            var before = Load(session, tenant);
            var after = ChangeLimit(session, before, dailyTokenLimit);
            SetMeta(session, RevisionKey(tenant), checked(Revision(session, tenant) + 1).ToString(CultureInfo.InvariantCulture));
            return (before, after);
        });
    }

    /// <summary>Package-safe audit intent; the host appends it inside the same commit.</summary>
    public TenantQuotaManagementSnapshot SaveManagementWithAudit(string tenantId, int dailyTokenLimit,
        long expectedRevision, string idempotencyKey, string callerScope,
        Func<TenantQuotaSnapshot, TenantQuotaSnapshot, AuditEvent> audit)
        => SaveCore(tenantId, dailyTokenLimit, expectedRevision, idempotencyKey, callerScope,
            (session, before, after) => session.AppendAudit(audit(before, after)));

    internal TenantQuotaManagementSnapshot SaveCore(string tenantId, int dailyTokenLimit,
        long expectedRevision, string idempotencyKey, string callerScope,
        Action<IPackageDataSession, TenantQuotaSnapshot, TenantQuotaSnapshot>? audit)
    {
        ValidateLimit(dailyTokenLimit);
        ValidateOperation(expectedRevision, idempotencyKey, callerScope);
        var tenant = NormalizeTenant(tenantId);
        var fingerprint = Hash(JsonSerializer.Serialize(new { tenant, dailyTokenLimit, expectedRevision }));
        return _data.Write(session =>
        {
            var journalKey = JournalKey(callerScope);
            var journal = ReadJournal(session, journalKey);
            var receiptKey = Hash(idempotencyKey);
            journal.TryGetValue(receiptKey, out var receipt);
            if (receipt is not null)
            {
                if (receipt.Fingerprint != fingerprint) throw new TenantQuotaManagementException("idempotency_conflict");
                return receipt.Result;
            }
            if (journal.Count >= MaximumReceiptsPerCaller)
                throw new TenantQuotaManagementException("quota_receipt_capacity");
            var revision = Revision(session, tenant);
            if (revision != expectedRevision) throw new TenantQuotaManagementException("quota_revision_conflict");
            var before = Load(session, tenant);
            var quota = ChangeLimit(session, before, dailyTokenLimit);
            var next = checked(revision + 1);
            SetMeta(session, RevisionKey(tenant), next.ToString(CultureInfo.InvariantCulture));
            var result = new TenantQuotaManagementSnapshot(quota, next);
            journal.Add(receiptKey, new Receipt(fingerprint, result));
            SetMeta(session, journalKey, JsonSerializer.Serialize(journal, ReceiptJson));
            session.RecordReceipt(new(callerScope, idempotencyKey, fingerprint, "committed"));
            audit?.Invoke(session, before, quota);
            return result;
        });
    }

    public TenantQuotaManagementSnapshot? FindManagement(string idempotencyKey, string callerScope)
    {
        ValidateOperation(0, idempotencyKey, callerScope);
        return _data.Read(session => ReadJournal(session, JournalKey(callerScope)).GetValueOrDefault(Hash(idempotencyKey))?.Result);
    }

    internal static TenantQuotaSnapshot Load(IPackageDataSession session, string tenant)
    {
        var limit = ReadOrSetLimit(session, tenant, null);
        var usage = session.ReadUsage(tenant);
        var quota = new TenantQuotaSnapshot(tenant, limit, usage.UsedPromptTokens, usage.UsedCompletionTokens,
            usage.ResetDate, usage.UsedTotalTokens >= limit);
        return quota;
    }

    private static TenantQuotaSnapshot ChangeLimit(IPackageDataSession session, TenantQuotaSnapshot quota, int limit)
    {
        var updated = quota with { DailyTokenLimit = limit, EnergySaving = quota.UsedTotalTokens >= limit };
        ReadOrSetLimit(session, quota.TenantId, limit);
        return updated;
    }

    private static int ReadOrSetLimit(IPackageDataSession session, string tenant, int? limit)
    {
        return Convert.ToInt32(session.Scalar("""
            INSERT INTO tenant_quotas(tenant_id, daily_token_limit, updated_at)
            VALUES ($id, COALESCE($limit,$default), datetime('now'))
            ON CONFLICT(tenant_id) DO UPDATE SET
              daily_token_limit = COALESCE($limit,tenant_quotas.daily_token_limit), updated_at = excluded.updated_at
            RETURNING daily_token_limit;
            """, new("$id", tenant), new("$limit", limit), new("$default", TenantQuotaPolicy.DefaultDailyTokenLimit)));
    }

    private static string? Meta(IPackageDataSession session, string key) => session.ReadState(key);
    private static void SetMeta(IPackageDataSession session, string key, string value) => session.WriteState(key, value);

    private static long Revision(IPackageDataSession session, string tenant)
    {
        var raw = Meta(session, RevisionKey(tenant));
        if (raw is null) return 0;
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0)
            throw new TenantQuotaManagementException("quota_management_corrupt");
        return revision;
    }

    private static Dictionary<string, Receipt> ReadJournal(IPackageDataSession session, string key)
    {
        var raw = Meta(session, key);
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
    internal static string NormalizeTenant(string? tenantId) => string.IsNullOrWhiteSpace(tenantId) ? "default:general" : tenantId.Trim();
    private static void ValidateLimit(int limit)
    {
        if (!TenantQuotaPolicy.IsValidDailyTokenLimit(limit)) throw new ArgumentOutOfRangeException(nameof(limit));
    }
    private static void ValidateOperation(long revision, string key, string scope)
    {
        if (revision < 0 || string.IsNullOrWhiteSpace(key) || key.Length > 256 || key.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(scope) || scope.Length > 256 || scope.Any(char.IsControl))
            throw new ArgumentException("quota_invalid_operation");
    }
}
