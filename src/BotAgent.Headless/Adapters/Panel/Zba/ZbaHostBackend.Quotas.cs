using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using BotAgent.Services.Ops;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Ops;
using BotAgent.Panel;
using BotAgent.Services.Conversations;

namespace BotAgent.Adapters.Panel.Zba;

internal sealed partial class ZbaHostBackend
{
    private readonly ConversationRegistry? _quotaRegistry;
    private readonly TenantQuotaStore? _quotaStore;

    public ValueTask<ZbaQuotaPage> ListQuotasAsync(ConfigurationCaller caller, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(QuotaOperation(() => new ZbaQuotaPage(QuotaRegistry.Snapshot()
            .OrderBy(c => c.SourceKey, StringComparer.Ordinal)
            .Select(c => MapQuota(QuotaStore.ReadManagement(c.SourceKey), _dto.MaskName(c.SourceKey))).ToArray())));
    }

    public ValueTask<ZbaQuotaSaveResult> SaveQuotaAsync(ConfigurationCaller caller, ZbaQuotaSaveRequest request, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        var conversation = QuotaRegistry.Find(request.SourceKey)
            ?? throw new ZbaBackendException(404, "quota_source_not_found", "Quota source is not registered.");
        return ValueTask.FromResult(QuotaOperation(() => new ZbaQuotaSaveResult(MapQuota(
            QuotaStore.SaveManagement(request.SourceKey, request.DailyTokenLimit,
                long.Parse(request.ExpectedRevision, CultureInfo.InvariantCulture), request.IdempotencyKey, CallerScope(caller),
                (conn, before, after) => AppendQuotaAudit(conn, caller, before, after)),
            _dto.MaskName(conversation.SourceKey)))));
    }

    public ValueTask<ZbaQuotaFindResult> FindQuotaAsync(ConfigurationCaller caller, string idempotencyKey, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(QuotaOperation(() =>
        {
            var saved = QuotaStore.FindManagement(idempotencyKey, CallerScope(caller));
            if (saved is null) return new ZbaQuotaFindResult(false, null);
            var conversation = QuotaRegistry.Find(saved.Quota.TenantId);
            return new ZbaQuotaFindResult(true, MapQuota(saved,
                conversation is null ? _dto.MaskName(saved.Quota.TenantId) : _dto.MaskName(conversation.SourceKey)));
        }));
    }

    private static void AppendQuotaAudit(Microsoft.Data.Sqlite.SqliteConnection conn, ConfigurationCaller caller,
        TenantQuotaSnapshot before, TenantQuotaSnapshot after)
    {
        // Raw source keys and credentials never enter the audit chain. Fingerprints remain useful with masking disabled too.
        static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
        var detail = JsonSerializer.Serialize(new { result = "applied", resource = "tenant_quota",
            sourceFingerprint = Fingerprint(after.TenantId), oldDailyTokenLimit = before.DailyTokenLimit,
            newDailyTokenLimit = after.DailyTokenLimit });
        new AuditLogStore().AppendInTransaction(conn,
            new AuditEvent("config_change", "zba:" + Fingerprint(CallerScope(caller)), "zba", detail, "2.1"));
    }

    private ConversationRegistry QuotaRegistry => _quotaRegistry
        ?? throw new ZbaBackendException(503, "quota_management_unavailable", "Quota management is unavailable.");
    private TenantQuotaStore QuotaStore => _quotaStore
        ?? throw new ZbaBackendException(503, "quota_management_unavailable", "Quota management is unavailable.");

    private static T QuotaOperation<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (TenantQuotaManagementException ex)
        {
            var status = ex.Code is "quota_revision_conflict" or "idempotency_conflict" ? 409 : 503;
            throw new ZbaBackendException(status, ex.Code, "Quota management request could not be completed.");
        }
    }

    private static ZbaQuota MapQuota(TenantQuotaManagementSnapshot state, string name)
    {
        var q = state.Quota;
        var next = DateTimeOffset.ParseExact(q.ResetDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).AddDays(1);
        return new(q.TenantId, name, state.Revision.ToString(CultureInfo.InvariantCulture), q.DailyTokenLimit,
            q.UsedPromptTokens, q.UsedCompletionTokens, q.UsedTotalTokens, q.RemainingTokens,
            q.ResetDate, next.ToString("O", CultureInfo.InvariantCulture), q.EnergySaving,
            TenantQuotaPolicy.MinimumDailyTokenLimit, TenantQuotaPolicy.MaximumDailyTokenLimit);
    }
}
