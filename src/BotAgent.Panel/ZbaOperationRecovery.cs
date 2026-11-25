using System.Text.Json.Serialization;
using BotAgent.Domain.Ops;

namespace BotAgent.Panel;

public sealed record ZbaOperationFindRequest([property: JsonRequired] string Owner, [property: JsonRequired] string IdempotencyKey);
public sealed record ZbaOperationFindResult([property: JsonRequired] bool Found,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] OperationRecoveryReceipt? Receipt,
    [property: JsonRequired] string Details);

public sealed partial class ZbaEndpoint
{
    private async ValueTask<ZbaResponse> HandleOperationFindAsync(ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaOperationFindRequest>(request);
        ZbaRules.ValidatePluginId(body.Owner);
        ZbaRules.ValidatePluginIdempotencyKey(body.IdempotencyKey);
        return Json(await _backend.FindOperationAsync(caller, body, ct).ConfigureAwait(false), requestId);
    }
}
