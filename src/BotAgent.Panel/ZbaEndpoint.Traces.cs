namespace BotAgent.Panel;

public sealed partial class ZbaEndpoint
{
    /// <summary>
    /// 只读轨迹：返回最近若干轮的形状与进程级缓存用量合计。
    /// 刻意**不接受会话过滤参数** —— 面板轨迹页只回答"多少条、多少 token、多少未知"，
    /// 要查具体会话内容应该去审批过的会话接口，不在这里开新口子。
    /// </summary>
    private async ValueTask<ZbaResponse> HandleTracesAsync(
        ConfigurationCaller caller, ZbaQuery query, string requestId, CancellationToken ct)
    {
        query.AllowOnly("limit");
        var limit = query.GetInt("limit", 20, 1, ZbaContract.MaxPageSize);
        var page = await _backend.ListTracesAsync(caller, limit, ct).ConfigureAwait(false);
        return Json(page, requestId);
    }
}
