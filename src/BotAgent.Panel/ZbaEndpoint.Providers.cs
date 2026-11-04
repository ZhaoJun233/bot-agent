namespace BotAgent.Panel;

/// <summary>提供商模型发现：只读、失败不 5xx，浏览器不直接请求上游。</summary>
public sealed partial class ZbaEndpoint
{
    private async ValueTask<ZbaResponse> HandleProviderModelsAsync(
        ConfigurationCaller caller, string providerKey, string requestId, CancellationToken ct)
    {
        var result = await _backend.ListProviderModelsAsync(caller, providerKey, ct).ConfigureAwait(false);
        return Json(result, requestId);
    }
}
