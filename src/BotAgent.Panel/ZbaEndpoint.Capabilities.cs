using BotAgent.Domain.Management;

namespace BotAgent.Panel;

public sealed partial class ZbaEndpoint
{
    private async ValueTask<ZbaResponse> HandleCapabilitiesListAsync(
        ConfigurationCaller caller, ZbaQuery query, string requestId, CancellationToken ct)
    {
        query.AllowOnly("kind", "limit", "cursor");
        ManagedKind? kind = query.Get("kind") is null ? null : (ManagedKind)query.GetInt("kind", 0, 0, 4);
        var limit = Math.Min(
            query.GetInt("limit", ZbaContract.DefaultPageSize, 1, ZbaContract.MaxPageSize),
            ZbaContract.MaxCapabilityPageSize);
        var page = await _backend.ListCapabilitiesAsync(
            caller, new ManagedPageRequest(kind, limit, query.GetCursor()), ct).ConfigureAwait(false);
        return Json(page, requestId, Status(page.Code));
    }

    private async ValueTask<ZbaResponse> HandleCapabilityInspectAsync(
        ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaCapabilityInspectRequest>(request);
        if (body.Key is null) throw new ZbaBadRequestException("missing_key");
        var result = await _backend.InspectCapabilityAsync(caller, body.Key, ct).ConfigureAwait(false);
        return Json(result, requestId, Status(result.Code));
    }

    private async ValueTask<ZbaResponse> HandleCapabilityValidateAsync(
        ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaConfigurationValidationRequest>(request);
        ValidatePatchShape(body.ContractRevision, body.Changes, body.Secrets);
        if (body.ExpectedVersion.Value <= 0) throw new ZbaBadRequestException("invalid_expected_version");
        ZbaRules.ValidateSecrets(body.Secrets);
        var result = await _backend.ValidateCapabilityAsync(caller, body, ct).ConfigureAwait(false);
        return Json(result, requestId, Status(result.Code));
    }

    private async ValueTask<ZbaResponse> HandleCapabilityExecuteAsync(
        ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ManagedExecuteRequest>(request);
        if (string.IsNullOrWhiteSpace(body.ContractRevision)) throw new ZbaBadRequestException("missing_contract_revision");
        if (body.Command is null) throw new ZbaBadRequestException("missing_command");
        var result = await _backend.ExecuteCapabilityAsync(caller, body, ct).ConfigureAwait(false);
        return Json(result, requestId, Status(result.Code));
    }

    private async ValueTask<ZbaResponse> HandleCapabilityQueryAsync(
        ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var key = Body<ManagedOperationKey>(request);
        var result = await _backend.QueryCapabilityAsync(caller, key, ct).ConfigureAwait(false);
        return Json(result, requestId, Status(result.Code));
    }

    private async ValueTask<ZbaResponse> HandleCapabilityCancelAsync(
        ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var key = Body<ManagedOperationKey>(request);
        var result = await _backend.CancelCapabilityAsync(caller, key, ct).ConfigureAwait(false);
        return Json(result, requestId, Status(result.Code));
    }

    internal static void ValidatePatchShape(
        string? contractRevision, object? changes, IReadOnlyList<ZbaSecretEdit>? secrets)
    {
        if (string.IsNullOrWhiteSpace(contractRevision) || contractRevision.Length > 64)
            throw new ZbaBadRequestException("invalid_contract_revision");
        if (changes is null) throw new ZbaBadRequestException("missing_changes");
        if (secrets is null) throw new ZbaBadRequestException("missing_secrets");
        if (secrets.Count > 6) throw new ZbaBadRequestException("too_many_secrets");
    }
}
