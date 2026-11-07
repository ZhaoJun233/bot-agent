using BotAgent.Domain.Configuration;
using BotAgent.Panel;

namespace BotAgent.Adapters.Panel.Zba;

internal sealed partial class ZbaHostBackend
{
    public async ValueTask<ZbaPluginDiscovery> DiscoverPluginSourceAsync(
        ConfigurationCaller caller, string sourceUrl, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        return await Market.DiscoverSourceAsync(sourceUrl, CallerScope(caller), ct).ConfigureAwait(false);
    }

    public async ValueTask<ZbaPluginPreview> PreviewPluginAsync(
        ConfigurationCaller caller, ZbaPluginPreviewRequest request, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        return await Market.PreviewAsync(request, CallerScope(caller), ct).ConfigureAwait(false);
    }

    private async Task PersistInstallDisabledAsync(ConfigurationCaller caller, string pluginId, CancellationToken ct)
    {
        // The package service exposes the validated ID only in this async flow, before durable Pending.
        var current = await ReadConfigurationAsync(caller, ConfigurationReadKind.Editable, ct).ConfigureAwait(false);
        if (current.Code != ConfigurationResultCode.Ok)
            throw new ZbaBackendException(503, "plugin_disable_unavailable", "无法读取安装停用配置，未安装。");
        var patch = new ConfigurationPatch(null, null, null, null, null, null, null,
            new RegisteredConfigurationChanges([new PluginEnabledEdit(new(pluginId), false)], [], []));
        var request = new ZbaConfigurationSaveRequest(current.ContractRevision, current.Version,
            new(current.Generation, current.ServerUnixSeconds, Guid.NewGuid()), patch, []);
        var result = await SaveConfigurationAsync(caller, request, ct).ConfigureAwait(false);
        if (!(result.Code == ConfigurationResultCode.Ok && result.Receipt?.CommitState == CommitState.Committed ||
            result.Code == ConfigurationResultCode.NoChanges && result.Receipt?.CommitState == CommitState.NotCommitted))
            throw new ZbaBackendException(409, "plugin_disable_failed", "安装停用配置未确认持久化，请刷新后重试。");
    }
}