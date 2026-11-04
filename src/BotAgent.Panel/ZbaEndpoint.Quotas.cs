using System.Globalization;
using BotAgent.Domain.Ops;
namespace BotAgent.Panel;

public sealed partial class ZbaEndpoint
{
    private async ValueTask<ZbaResponse> HandleQuotaListAsync(ConfigurationCaller caller, string requestId, CancellationToken ct)
        => Json(await _backend.ListQuotasAsync(caller, ct).ConfigureAwait(false), requestId);

    private async ValueTask<ZbaResponse> HandleQuotaSaveAsync(ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaQuotaSaveRequest>(request);
        if (string.IsNullOrWhiteSpace(body.SourceKey) || body.SourceKey.Length > 256
            || body.SourceKey.Trim() != body.SourceKey || body.SourceKey.Any(char.IsControl))
            throw new ZbaBadRequestException("invalid_source_key");
        if (!long.TryParse(body.ExpectedRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0)
            throw new ZbaBadRequestException("invalid_quota_revision");
        ZbaRules.ValidatePluginIdempotencyKey(body.IdempotencyKey);
        if (!TenantQuotaPolicy.IsValidDailyTokenLimit(body.DailyTokenLimit))
            throw new ZbaBadRequestException("invalid_daily_token_limit");
        return Json(await _backend.SaveQuotaAsync(caller, body, ct).ConfigureAwait(false), requestId);
    }

    private async ValueTask<ZbaResponse> HandleQuotaFindAsync(ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaQuotaFindRequest>(request);
        ZbaRules.ValidatePluginIdempotencyKey(body.IdempotencyKey);
        return Json(await _backend.FindQuotaAsync(caller, body.IdempotencyKey, ct).ConfigureAwait(false), requestId);
    }
}
