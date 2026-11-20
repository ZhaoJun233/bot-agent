using System.Reflection;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Voice;

namespace BotAgent.Packages.Voice;

/// <summary>Independent package implementation for <c>preset.feature.voice</c>.</summary>
public sealed class VoicePackagePlugin : IBotPlugin, IPluginContributionSource
{
    private VoiceCoordinator? _coordinator;

    public string Id => "preset.feature.voice";
    public string Name => "TTS 语音合成与多态发声";
    public string Version => typeof(VoicePackagePlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "支持语音回复合成、音色语速控制与防刷屏连发熔断。";
    public PluginCategory Category => PluginCategory.Media;

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ports = context.Services.GetService(typeof(IVoiceHostPorts)) as IVoiceHostPorts;
        _coordinator = new VoiceCoordinator(new VoicePolicyEngine(), ports);
        return Task.CompletedTask;
    }

    public object? GetContribution(Type contract)
        => contract == typeof(IVoiceRuntimeContribution) || contract == typeof(IVoicePolicyContribution)
            ? _coordinator
            : null;

    public Task ShutdownAsync(CancellationToken ct)
    {
        _coordinator = null;
        return Task.CompletedTask;
    }
}
