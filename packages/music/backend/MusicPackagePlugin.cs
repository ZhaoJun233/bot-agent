using System.Reflection;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Music;

namespace BotAgent.Packages.Music;

/// <summary>Independent package implementation for <c>preset.feature.music</c>.</summary>
public sealed class MusicPackagePlugin : IBotPlugin, IPluginContributionSource
{
    private MusicCoordinator? _coordinator;

    public string Id => "preset.feature.music";
    public string Name => "群聊音乐卡片与波形解析";
    public string Version => typeof(MusicPackagePlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "支持群聊音乐卡片解析、低码率音频波形特征提取与网易云音乐卡片分享。";
    public PluginCategory Category => PluginCategory.Media;

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ports = context.Services.GetService(typeof(IMusicHostPorts)) as IMusicHostPorts;
        _coordinator = new MusicCoordinator(new MusicPolicyEngine(), ports);
        return Task.CompletedTask;
    }

    public object? GetContribution(Type contract)
        => contract == typeof(IMusicRuntimeContribution) || contract == typeof(IMusicPolicyContribution)
            ? _coordinator
            : null;

    public Task ShutdownAsync(CancellationToken ct)
    {
        _coordinator = null;
        return Task.CompletedTask;
    }
}
