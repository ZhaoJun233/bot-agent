using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Ports;
using BotAgent.Panel;
using BotAgent.Services.Ops;

namespace BotAgent.Packages.TokenQuota;

internal sealed partial class QuotaOperations(ITenantQuotaManagement ledger) : IQuotaOperations
{
    public ZbaQuotaPage List(string[] sourceKeys, Func<string, string> displayName) => Run(() => new ZbaQuotaPage(sourceKeys
        .OrderBy(key => key, StringComparer.Ordinal)
        .Select(key => Map(ledger.ReadManagement(key), displayName(key))).ToArray()));

    public ZbaQuotaSaveResult Save(ZbaQuotaSaveRequest request, string callerScope, Func<string, bool> knownSource, Func<string, string> displayName)
    {
        QuotaRequestRules.ValidateSave(request, QuotaRequestRules.ValidateKey);
        if (!knownSource(request.SourceKey))
            throw new QuotaOperationException(404, "quota_source_not_found", "Quota source is not registered.");
        return Run(() => new ZbaQuotaSaveResult(Map(ledger.SaveManagementWithAudit(request.SourceKey,
            request.DailyTokenLimit, long.Parse(request.ExpectedRevision, CultureInfo.InvariantCulture), request.IdempotencyKey,
            callerScope, (before, after) => Audit(callerScope, before, after)), displayName(request.SourceKey))));
    }

    public ZbaQuotaFindResult Find(string key, string callerScope, Func<string, string> displayName)
    {
        QuotaRequestRules.ValidateKey(key);
        return Run(() =>
        {
            var saved = ledger.FindManagement(key, callerScope);
            return saved is null ? new ZbaQuotaFindResult(false, null)
                : new ZbaQuotaFindResult(true, Map(saved, displayName(saved.Quota.TenantId)));
        });
    }

    private static T Run<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (TenantQuotaManagementException ex)
        {
            throw new QuotaOperationException(ex.Code is "quota_revision_conflict" or "idempotency_conflict" ? 409 : 503,
                ex.Code, "Quota management request could not be completed.");
        }
    }

    private static AuditEvent Audit(string callerScope, TenantQuotaSnapshot before, TenantQuotaSnapshot after)
    {
        static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
        var detail = JsonSerializer.Serialize(new { result = "applied", resource = "tenant_quota",
            sourceFingerprint = Fingerprint(after.TenantId), oldDailyTokenLimit = before.DailyTokenLimit,
            newDailyTokenLimit = after.DailyTokenLimit });
        return new("config_change", "zba:" + Fingerprint(callerScope), "zba", detail, "2.1");
    }

    private static ZbaQuota Map(TenantQuotaManagementSnapshot state, string name)
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
