namespace BotAgent.Panel;

public sealed partial class ZbaEndpoint
{
    private async ValueTask<ZbaResponse> HandleTasksListAsync(
        ConfigurationCaller caller, ZbaQuery query, string requestId, CancellationToken ct)
    {
        query.AllowOnly("limit", "cursor");
        var limit = query.GetInt("limit", ZbaContract.DefaultPageSize, 1, ZbaContract.MaxPageSize);
        var page = await _backend.ListTasksAsync(caller, limit, query.GetCursor(), ct).ConfigureAwait(false);
        return Json(page, requestId);
    }

    private async ValueTask<ZbaResponse> HandleTaskSubmitAsync(
        ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaTaskSubmission>(request);
        ValidateSubmission(body);
        var idempotencyKey = IdempotencyKey(request);
        var accepted = await _backend.SubmitTaskAsync(caller, body, idempotencyKey, ct).ConfigureAwait(false);
        return Json(accepted, requestId);
    }

    private async ValueTask<ZbaResponse> HandleTaskGetAsync(
        ConfigurationCaller caller, string id, string requestId, CancellationToken ct)
    {
        var task = await _backend.GetTaskAsync(caller, id, ct).ConfigureAwait(false);
        return task is null
            ? Error(404, "task_not_found", "任务不存在。", requestId: requestId)
            : Json(task, requestId);
    }

    private async ValueTask<ZbaResponse> HandleTaskCancelAsync(
        ConfigurationCaller caller, string id, string requestId, CancellationToken ct)
    {
        var task = await _backend.GetTaskAsync(caller, id, ct).ConfigureAwait(false);
        if (task is null) return Error(404, "task_not_found", "任务不存在。", requestId: requestId);
        var result = await _backend.CancelTaskAsync(caller, id, ct).ConfigureAwait(false);
        return Json(result, requestId);
    }

    private static void ValidateSubmission(ZbaTaskSubmission body)
    {
        if (string.IsNullOrWhiteSpace(body.SourceKey) || body.SourceKey.Length > 256 || HasControl(body.SourceKey))
            throw new ZbaBadRequestException("invalid_source_key");
        if (string.IsNullOrWhiteSpace(body.SessionId) || body.SessionId.Length > 128 || HasControl(body.SessionId))
            throw new ZbaBadRequestException("invalid_session_id");
        if (string.IsNullOrWhiteSpace(body.Prompt) || body.Prompt.Length > ZbaContract.MaxPromptChars || body.Prompt.Contains('\0'))
            throw new ZbaBadRequestException("invalid_prompt");
        if (body.Target != "server")
            throw new ZbaBadRequestException("invalid_target");
        if (body.TimeoutSeconds is < 10 or > 900)
            throw new ZbaBadRequestException("invalid_timeout");
    }

    private static string IdempotencyKey(ZbaRequest request)
    {
        if (!request.Headers.TryGetValue("Idempotency-Key", out var key) || string.IsNullOrWhiteSpace(key))
            throw new ZbaBadRequestException("missing_idempotency_key");
        if (key.Length is < 8 or > 128 ||
            !key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~'))
            throw new ZbaBadRequestException("invalid_idempotency_key");
        return key;
    }

    private static bool HasControl(string value) => value.Any(char.IsControl);
}
