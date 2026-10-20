using BotAgent.Domain.Configuration;

namespace BotAgent.Panel;

public sealed partial class ZbaEndpoint
{
    private async ValueTask<ZbaResponse> HandleConfigurationReadAsync(
        ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaConfigurationReadRequest>(request);
        if (!Enum.IsDefined(body.Kind)) throw new ZbaBadRequestException("invalid_read_kind");
        var result = await _backend.ReadConfigurationAsync(caller, body.Kind, ct).ConfigureAwait(false);
        return Json(result, requestId, Status(result.Code));
    }

    private async ValueTask<ZbaResponse> HandleConfigurationSaveAsync(
        ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaConfigurationSaveRequest>(request);
        ValidatePatchShape(body.ContractRevision, body.Changes, body.Secrets);
        if (body.ExpectedVersion.Value <= 0) throw new ZbaBadRequestException("invalid_expected_version");
        ZbaRules.ValidateKey(body.Key);
        ZbaRules.ValidateSecrets(body.Secrets);
        var result = await _backend.SaveConfigurationAsync(caller, body, ct).ConfigureAwait(false);
        return Json(result, requestId, Status(result.Code));
    }

    private async ValueTask<ZbaResponse> HandleConfigurationFindAsync(
        ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaConfigurationFindRequest>(request);
        ZbaRules.ValidateKey(body.Key);
        var result = await _backend.FindConfigurationAsync(caller, body.Key, ct).ConfigureAwait(false);
        return Json(result, requestId, Status(result.Code));
    }
}
