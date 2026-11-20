using BotAgent.Domain.Plugins;
using BotAgent.Plugins.Execution;
using BotAgent.Services.Plugins;
using BotAgent.Services.Resilience;

namespace BotAgent.Adapters.Plugins;

/// <summary>Host circuit access whose lease outlives the result when work or callbacks run late.</summary>
internal sealed class PluginActivationExecution(ToolCircuitBreaker execution) : IToolExecution
{
    private PluginManager.InvocationGate? _gate;

    internal static bool Requested(PluginPackageManifest? manifest)
        => manifest?.Permissions?.Contains(IToolExecution.Permission, StringComparer.Ordinal) == true;

    internal static PluginActivationExecution? Create(PluginPackageManifest manifest, PluginContext context)
        => !Requested(manifest) ? null : new(context.Services.GetService(typeof(IToolExecution)) as ToolCircuitBreaker
            ?? throw new InvalidOperationException("plugin_tool_execution_missing"));

    internal PluginActivationExecution ControlView() => new(execution);
    internal void Publish(PluginManager.InvocationGate gate) => Volatile.Write(ref _gate, gate);

    public Task<ToolRunResult<T>> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        var lease = Volatile.Read(ref _gate)?.TryAcquire()
            ?? throw new InvalidOperationException("plugin_tool_execution_not_ready");
        // The concrete host runner owns the lease, including rejection, late work and cancellation callbacks.
        return execution.RunWithLifetimeAsync(operation, ct, lease);
    }
}
