using System.Reflection;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Conversations;

namespace BotAgent.Packages.Vibes;

/// <summary>Independent package implementation for <c>preset.feature.vibes</c>.</summary>
public sealed class VibesPackagePlugin : IBotPlugin, IPluginContributionSource
{
    private VibesEngine? _engine;

    public string Id => "preset.feature.vibes";
    public string Name => "群聊氛围感知";
    public string Version => typeof(VibesPackagePlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "保留会话氛围与情绪底色，为后续聊天和主动关心提供上下文。";
    public PluginCategory Category => PluginCategory.Feature;

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _engine = new VibesEngine();
        return Task.CompletedTask;
    }

    public object? GetContribution(Type contract)
        => contract == typeof(IVibesContribution) ? _engine : null;

    public Task ShutdownAsync(CancellationToken ct)
    {
        _engine = null;
        return Task.CompletedTask;
    }
}
