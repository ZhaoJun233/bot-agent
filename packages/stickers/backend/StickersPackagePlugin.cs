using System.Reflection;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Stickers;

namespace BotAgent.Packages.Stickers;

/// <summary>Independent package implementation for <c>preset.feature.stickers</c>.</summary>
public sealed class StickersPackagePlugin : IBotPlugin, IPluginContributionSource
{
    private StickerPolicyEngine? _policy;
    private StickerCoordinator? _coordinator;

    public string Id => "preset.feature.stickers";
    public string Name => "表情包协同与安全库";
    public string Version => typeof(StickersPackagePlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "自动学习收录群聊表情包，提取标签并根据对话语境协同发送，内置安全魔数校验。";
    public PluginCategory Category => PluginCategory.Feature;

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _policy = new StickerPolicyEngine();
        if (context.Services.GetService(typeof(IStickerHostPorts)) is IStickerHostPorts ports)
        {
            _coordinator = new StickerCoordinator(ports);
        }
        return Task.CompletedTask;
    }

    public object? GetContribution(Type contract)
        => contract == typeof(IStickerPolicyContribution) ? _policy
            : contract == typeof(IStickerRuntimeContribution) ? _coordinator
            : null;

    public async Task ShutdownAsync(CancellationToken ct)
    {
        var coordinator = _coordinator;
        _coordinator = null;
        _policy = null;
        if (coordinator is not null)
        {
            await coordinator.ShutdownAsync(ct).ConfigureAwait(false);
        }
    }
}
