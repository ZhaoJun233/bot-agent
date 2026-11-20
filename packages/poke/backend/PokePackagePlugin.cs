using System.Reflection;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Poke;

namespace BotAgent.Packages.Poke;

/// <summary>Independent package implementation for <c>preset.feature.poke</c>.</summary>
public sealed class PokePackagePlugin : IBotPlugin, IPluginContributionSource
{
    private PokePolicyEngine? _policy;

    public string Id => "preset.feature.poke";
    public string Name => "双向戳一戳交互";
    public string Version => typeof(PokePackagePlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "识别群聊与私聊双向戳一戳动作，具备冷却门限判定与自主拟人化回戳逻辑。";
    public PluginCategory Category => PluginCategory.Feature;

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var factSink = context.Services.GetService(typeof(IPokeInteractionFactSink)) as IPokeInteractionFactSink;
        _policy = new PokePolicyEngine(factSink);
        return Task.CompletedTask;
    }

    public object? GetContribution(Type contract)
        => contract == typeof(IPokePolicy) ? _policy : null;

    public Task ShutdownAsync(CancellationToken ct)
    {
        _policy = null;
        return Task.CompletedTask;
    }
}
