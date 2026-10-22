namespace BotAgent.Panel;

public sealed partial class ZbaEndpoint
{
    private async ValueTask<ZbaResponse> HandleApprovalsListAsync(
        ConfigurationCaller caller, string requestId, CancellationToken ct)
    {
        var approvals = await _backend.ListApprovalsAsync(caller, ct).ConfigureAwait(false);
        return Json(approvals, requestId);
    }

    private async ValueTask<ZbaResponse> HandleApprovalDecideAsync(
        ConfigurationCaller caller, string id, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaApprovalDecision>(request);
        var result = await _backend.DecideApprovalAsync(caller, id, body.Approve, ct).ConfigureAwait(false);
        if (result.Ok) return Json(result, requestId);
        var status = result.ReasonCode switch
        {
            "unknown_request" => 404,
            "not_an_approver" => 403,
            "expired" or "already_decided" or "policy_changed" or "source_retired" => 409,
            _ => 409
        };
        return Error(status, status == 404 ? "approval_not_found" : "approval_rejected",
            "审批未执行。", result.ReasonCode, requestId: requestId);
    }

    private async ValueTask<ZbaResponse> HandleEventsAsync(
        ConfigurationCaller caller, ZbaQuery query, string requestId, CancellationToken ct)
    {
        query.AllowOnly("after", "limit");
        var after = query.GetLong("after", 0, 0, long.MaxValue);
        var limit = query.GetInt("limit", 100, 1, ZbaContract.MaxPageSize);
        var page = await _backend.ReadEventsAsync(caller, after, limit, ct).ConfigureAwait(false);
        return Json(page, requestId);
    }
}
