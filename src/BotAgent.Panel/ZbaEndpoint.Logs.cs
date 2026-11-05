using BotAgent.Domain.Configuration;

namespace BotAgent.Panel;

public sealed partial class ZbaEndpoint
{
    private async ValueTask<ZbaResponse> HandleLogFeedAsync(
        ConfigurationCaller caller, ZbaQuery query, string requestId, CancellationToken ct)
    {
        query.AllowOnly("cursor");
        ZbaLogFeedCursor? cursor = null;
        if (query.Get("cursor") is { } text)
        {
            if (!ZbaLogFeedCursor.TryParse(text, out var parsed))
                throw new ZbaBadRequestException("invalid_log_cursor");
            cursor = parsed;
        }
        var page = await _backend.ReadLogFeedAsync(caller, cursor, ct).ConfigureAwait(false);
        return Json(page, requestId);
    }

    /// <summary>
    /// 只读运行日志的安全摘要：默认 200 条、上限 600 条（与宿主环形缓冲一致）。
    /// 投影由宿主侧完成，端点只做参数边界与鉴权；**绝不返回原始日志文本**。
    /// </summary>
    private async ValueTask<ZbaResponse> HandleLogsAsync(
        ConfigurationCaller caller, ZbaQuery query, string requestId, CancellationToken ct)
    {
        query.AllowOnly("limit");
        var limit = query.GetInt("limit", 200, 1, ZbaContract.MaxLogEntries);
        var page = await _backend.ListLogsAsync(caller, limit, ct).ConfigureAwait(false);
        return Json(page, requestId);
    }
}
