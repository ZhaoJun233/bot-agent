using BotAgent.Domain.Configuration;
using BotAgent.Panel;

namespace BotAgent.Adapters.Panel.Zba;

internal sealed partial class ZbaHostBackend
{
    public async ValueTask<ZbaPluginLifecyclePreview> PreviewPluginLifecycleAsync(ConfigurationCaller caller,
        ZbaPluginLifecyclePreviewRequest request, CancellationToken cancellationToken)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        return await Market.PreviewLifecycleAsync(request, CallerScope(caller), cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<ZbaPluginOperationResult> RollbackPluginAsync(ConfigurationCaller caller,
        ZbaPluginMutationRequest request, CancellationToken cancellationToken)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        return await Market.RollbackAsync(request, CallerScope(caller), cancellationToken).ConfigureAwait(false);
    }
}