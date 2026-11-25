using BotAgent.Domain.Ports;
using BotAgent.Domain.Configuration;
using BotAgent.Panel;
using BotAgent.Services.Conversations;

namespace BotAgent.Adapters.Panel.Zba;

internal sealed partial class ZbaHostBackend
{
    private readonly ConversationRegistry? _quotaRegistry;
    private readonly IQuotaOperations? _quotaStore;

    public ValueTask<ZbaQuotaPage> ListQuotasAsync(ConfigurationCaller caller, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(QuotaOperation(() => QuotaStore.List(QuotaRegistry.Snapshot()
            .Select(c => c.SourceKey).ToArray(), _dto.MaskName)));
    }
    public ValueTask<ZbaQuotaSaveResult> SaveQuotaAsync(ConfigurationCaller caller, ZbaQuotaSaveRequest request, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(QuotaOperation(() => QuotaStore.Save(request, CallerScope(caller),
            key => QuotaRegistry.Find(key) is not null, _dto.MaskName)));
    }
    public ValueTask<ZbaQuotaFindResult> FindQuotaAsync(ConfigurationCaller caller, string idempotencyKey, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(QuotaOperation(() => QuotaStore.Find(idempotencyKey, CallerScope(caller), _dto.MaskName)));
    }
    private ConversationRegistry QuotaRegistry => _quotaRegistry
        ?? throw new ZbaBackendException(503, "quota_management_unavailable", "Quota management is unavailable.");
    private IQuotaOperations QuotaStore => _quotaStore
        ?? throw new ZbaBackendException(503, "quota_management_unavailable", "Quota management is unavailable.");
    private static T QuotaOperation<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (InvalidOperationException ex) when (ex.Message is "plugin_contribution_unavailable" or "plugin_contribution_missing")
        { throw new ZbaBackendException(503, "quota_management_unavailable", "Quota management is unavailable."); }
        catch (QuotaOperationException ex) { throw new ZbaBackendException(ex.Status, ex.Code, ex.Message); }
    }
}
