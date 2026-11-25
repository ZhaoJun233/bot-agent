using BotAgent.Domain.Plugins;
using BotAgent.Services.Plugins;

namespace BotAgent.EngineProbe;

internal static class PluginContributionChecks
{
    internal static async Task Run()
    {
        var registry = new PluginManager(_ => { });
        var provider = new Provider();
        registry.Register(provider);
        Reject(() => registry.GetContribution<ICompute>(provider.Id), "plugin_contribution_unavailable");
        await registry.StartAllAsync(default);
        Reject(() => registry.GetContribution<ILazy>(provider.Id), "plugin_contribution_contract_invalid");
        Reject(() => registry.GetContribution<INestedAsync>(provider.Id), "plugin_contribution_contract_invalid");
        Reject(() => registry.GetContribution<IResource>(provider.Id), "plugin_contribution_contract_invalid");
        var service = registry.GetContribution<ICompute>(provider.Id);
        Check(service.Sum(2, out var label) == 3 && label == "synthetic", "sync values and out parameters");
        Reject(() => service.Fail(), "synthetic_contribution_fault");
        Check(await service.ReadAsync() == 7 && await service.ValueAsync() == 8, "Task and ValueTask results");
        await service.FlushAsync();
        await service.FlushValueAsync();
        try { await service.CancelAsync(new CancellationToken(true)); throw new Exception("cancel_ignored"); }
        catch (OperationCanceledException) { }
        registry.SetEnabled(provider.Id, false);
        Reject(() => service.Sum(1, out _), "plugin_contribution_unavailable");
        registry.SetEnabled(provider.Id, true);
        var consumer = new Consumer();
        using (var dependencies = registry.PrepareDependencies(consumer.Id, [new(provider.Id, provider.Version)]))
            await registry.ReplaceAsync(consumer, true, dependencies, 1);
        var dependent = registry.GetDependencyContribution<ICompute>(consumer.Id, provider.Id);
        Check(dependent.Sum(4, out _) == 5, "exact dependency contribution");
        registry.SetEnabled(consumer.Id, false);
        Reject(() => dependent.Sum(1, out _), "plugin_contribution_unavailable");
        Reject(() => registry.GetDependencyContribution<ICompute>("synthetic.undeclared", provider.Id), "plugin_dependency_unavailable");
        registry.SetEnabled(consumer.Id, true);
        provider.AfterWait = () => registry.RemoveAsync(provider.Id);
        var call = dependent.WaitAsync();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var remove = registry.RemoveAsync(provider.Id);
        Check(!remove.IsCompleted && provider.Stops == 0, "async call retains provider before completion");
        Reject(() => service.Sum(1, out _), "plugin_contribution_unavailable");
        provider.Release.SetResult();
        try { await call.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("callback_reentry_allowed"); }
        catch (InvalidOperationException error) when (error.Message == "plugin_lifecycle_reentry") { }
        await remove.WaitAsync(TimeSpan.FromSeconds(5));
        Check(provider.Stops == 1, "faulted async call releases lease exactly once");
        PluginManager.InvocationGate? bound = null;
        await registry.ReplaceWithBindingsAsync(new Provider(), true, 1, gate =>
        {
            bound = gate;
            Check(!gate.IsReady && gate.TryAcquire() is null, "bindings exist while candidate admission remains closed");
        });
        Check(bound!.IsReady, "publication opens the already-installed binding");
        Reject(() => service.Sum(1, out _), "plugin_contribution_unavailable");
        Reject(() => dependent.Sum(1, out _), "plugin_contribution_unavailable");
        PluginManager.InvocationGate? consumerBinding = null;
        using (var dependencies = registry.PrepareDependencies(consumer.Id, [new(provider.Id, provider.Version)]))
            await registry.ReplaceWithBindingsAsync(new Consumer(), true, 1, gate =>
            {
                consumerBinding = gate;
                Reject(() => gate.GetDependency<ICompute>(provider.Id), "plugin_dependency_unavailable");
            }, dependencies);
        Check(consumerBinding!.GetDependency<ICompute>(provider.Id).Sum(6, out _) == 7, "published consumer resolves exact dependency");
        using (var dependencies = registry.PrepareDependencies(consumer.Id, [new(provider.Id, provider.Version)]))
            await registry.ReplaceAsync(new Consumer(), true, dependencies, 1);
        Reject(() => consumerBinding.GetDependency<ICompute>(provider.Id), "plugin_dependency_unavailable");
        await registry.StopAllAsync(default);
        await ControlLifecycle();
    }
    private static async Task ControlLifecycle()
    {
        var registry = new PluginManager(_ => { });
        var control = new ControlAdapter();
        var endpoint = registry.CreateControlEntryPoint<IControl>(control.Id);
        Reject(() => endpoint.Read(), "plugin_contribution_unavailable");
        registry.Register(new ControlAdapter(), defaultEnabled: false);
        Reject(() => registry.GetControlContribution<IControl>(control.Id), "plugin_contribution_unavailable");
        var provider = new Provider();
        await registry.ReplaceAsync(provider, true);
        PluginManager.InvocationGate? controlGate = null;
        using (var dependencies = registry.PrepareDependencies(control.Id, [new(provider.Id, provider.Version)]))
            await registry.ReplaceWithBindingsAsync(control, false, 1, gate =>
            {
                controlGate = gate.ForControl();
                Check(!controlGate.IsReady && controlGate.TryAcquire() is null, "control remains closed during preparation");
            }, dependencies);
        var service = registry.GetControlContribution<IControl>(control.Id);
        Check(endpoint.Read() == 11, "host endpoint resolves control only after readiness");
        Check(service.Read() == 11 && !registry.IsReady(control.Id), "prepared control works with policy disabled");
        var dependency = controlGate!.GetDependency<ICompute>(provider.Id);
        Check(dependency.Sum(2, out _) == 3, "disabled control resolves only its exact ready dependency");
        registry.SetEnabled(provider.Id, false);
        Reject(() => service.Read(), "plugin_contribution_unavailable");
        Reject(() => dependency.Sum(1, out _), "plugin_contribution_unavailable");
        registry.SetEnabled(provider.Id, true);
        var pending = service.WaitAsync();
        await control.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var remove = registry.RemoveAsync(control.Id);
        Check(!remove.IsCompleted && control.Stops == 0, "async control holds unload lease");
        Reject(() => service.Read(), "plugin_contribution_unavailable");
        control.Release.SetResult();
        Check(await pending == 12, "control call finishes before retirement");
        await remove;
        Check(control.Stops == 1, "control shutdown exactly once");
        await registry.ReplaceAsync(new ControlAdapter(), false);
        Check(endpoint.Read() == 11, "independent host request resolves newly prepared control generation");
        Reject(() => service.Read(), "plugin_contribution_unavailable");
        Reject(() => controlGate.GetDependency<ICompute>(provider.Id), "plugin_dependency_unavailable");
        await registry.StopAllAsync(default);
    }
    public interface IControl : IPluginControlContribution { int Read(); Task<int> WaitAsync(); }
    private sealed class ControlAdapter : IBotPlugin, IPluginControlSource, IControl
    {
        public string Id => "synthetic.control";
        public string Name => Id;
        public string Version => "1.0.0";
        public string Description => "Synthetic control loader adapter";
        public PluginCategory Category => PluginCategory.Management;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Stops;
        public object? GetControlContribution(Type type) => type == typeof(IControl) ? this : null;
        public int Read() => 11;
        public async Task<int> WaitAsync() { Entered.SetResult(); await Release.Task; return 12; }
        public Task ShutdownAsync(CancellationToken ct) { Stops++; return Task.CompletedTask; }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string code)
    {
        try { action(); } catch (InvalidOperationException error) when (error.Message == code) { return; }
        throw new InvalidOperationException("expected_" + code);
    }
    public interface ICompute : IPluginContribution
    {
        int Sum(int value, out string label);
        void Fail();
        Task<int> ReadAsync();
        ValueTask<int> ValueAsync();
        Task FlushAsync();
        ValueTask FlushValueAsync();
        Task CancelAsync(CancellationToken ct);
        Task<int> WaitAsync();
    }
    public interface ILazy : IPluginContribution { IEnumerable<int> Values(); }
    public interface INestedAsync : IPluginContribution { Task<Task<int>> Value(); }
    public interface IResource : IPluginContribution { AsyncResource Open(); }
    public sealed class AsyncResource : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Provider : IBotPlugin, ICompute
    {
        public string Id => "synthetic.contribution-provider";
        public string Name => Id;
        public string Version => "1.0.0";
        public string Description => "Synthetic contribution provider";
        public PluginCategory Category => PluginCategory.Feature;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<Task>? AfterWait;
        internal int Stops;
        public int Sum(int value, out string label) { label = "synthetic"; return value + 1; }
        public void Fail() => throw new InvalidOperationException("synthetic_contribution_fault");
        public Task<int> ReadAsync() => Task.FromResult(7);
        public ValueTask<int> ValueAsync() => ValueTask.FromResult(8);
        public Task FlushAsync() => Task.CompletedTask;
        public ValueTask FlushValueAsync() => ValueTask.CompletedTask;
        public Task CancelAsync(CancellationToken ct) => Task.FromCanceled(ct);
        public async Task<int> WaitAsync()
        {
            Entered.SetResult();
            await Release.Task;
            if (AfterWait is not null) await AfterWait();
            return 9;
        }
        public Task ShutdownAsync(CancellationToken ct) { Stops++; return Task.CompletedTask; }
    }
    private sealed class Consumer : IBotPlugin
    {
        public string Id => "synthetic.contribution-consumer";
        public string Name => Id;
        public string Version => "1.0.0";
        public string Description => "Synthetic declared consumer";
        public PluginCategory Category => PluginCategory.Feature;
        public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
