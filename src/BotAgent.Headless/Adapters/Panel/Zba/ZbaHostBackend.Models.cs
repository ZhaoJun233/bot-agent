using BotAgent.Domain.Configuration;
using BotAgent.Domain.Model;
using BotAgent.Model;
using BotAgent.Panel;

namespace BotAgent.Adapters.Panel.Zba;

internal sealed partial class ZbaHostBackend
{
    private readonly Func<string, ModelProviderRoute?>? _providerRoute;
    private readonly ProviderModelDiscovery? _discovery;

    /// <summary>模型发现失败也只回 <c>errorCode</c>（HTTP 200），前端始终保留手填。</summary>
    public async ValueTask<ZbaProviderModels> ListProviderModelsAsync(
        ConfigurationCaller caller, string providerKey, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        if (_discovery is null || _providerRoute is null)
            return new ZbaProviderModels(Array.Empty<string>(), "model_route_unavailable");
        // 只接受已登记（含新存停用）的 provider；路由缺失一律明确失败，不猜地址或密钥。
        var route = _providerRoute(providerKey);
        if (route is null) return new ZbaProviderModels(Array.Empty<string>(), "model_route_unavailable");
        var result = await _discovery.ListAsync(route, ct).ConfigureAwait(false);
        return new ZbaProviderModels(result.Models, result.ErrorCode);
    }

}
