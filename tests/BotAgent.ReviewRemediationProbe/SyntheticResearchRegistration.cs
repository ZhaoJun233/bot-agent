using BotAgent.Domain.Plugins;

namespace BotAgent.ReviewRemediationProbe;

// Engine gate probes supply synthetic ports; actual package adoption is covered by PluginMarketProbe.
internal sealed class SyntheticResearchRegistration : IBotPlugin
{
    public string Id => "preset.feature.websearch";
    public string Name => "synthetic research";
    public string Version => "1.0.0";
    public string Description => "synthetic gate registration";
    public PluginCategory Category => PluginCategory.Management;
    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}

internal sealed class SyntheticPresetRegistration(string id, string name, PluginCategory category = PluginCategory.Feature) : IBotPlugin
{
    public string Id => id;
    public string Name => name;
    public string Version => "1.0.0";
    public string Description => "synthetic gate registration";
    public PluginCategory Category => category;
    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
