using BotAgent.Domain.Plugins;

public class LifecycleRuntimePlugin : IBotPlugin
{
    public string Id => "probe.lifecycle.runtime";
    public string Name => "Synthetic lifecycle runtime";
    public virtual string Version => "1.0.0";
    public string Description => "Synthetic lifecycle fixture";
    public PluginCategory Category => PluginCategory.Feature;
    public virtual Task InitializeAsync(PluginContext context, CancellationToken ct)
    { (context.Services.GetService(typeof(Action<string>)) as Action<string>)?.Invoke(Version); return Task.CompletedTask; }
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
public sealed class LifecycleRuntimeV2 : LifecycleRuntimePlugin { public override string Version => "2.0.0"; }
public sealed class LifecycleRuntimeBroken : LifecycleRuntimePlugin
{
    public override string Version => "3.0.0";
    public override Task InitializeAsync(PluginContext context, CancellationToken ct) => throw new InvalidOperationException("synthetic initialization failure");
}