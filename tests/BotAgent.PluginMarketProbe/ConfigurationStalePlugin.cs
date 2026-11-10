using BotAgent.Domain.Plugins;

// Real synthetic fixture assembly: disabled materialization is permitted, Initialize is forbidden.
public sealed class ConfigurationStalePlugin : IBotPlugin
{
    public string Id => "probe.install.stale";
    public string Name => "Synthetic stale-state plugin";
    public string Version => "1.0.0";
    public string Description => "Synthetic stale-state regression fixture";
    public PluginCategory Category => PluginCategory.Feature;
    public Task InitializeAsync(PluginContext context, CancellationToken ct) => throw new InvalidOperationException("disabled_plugin_initialized");
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}