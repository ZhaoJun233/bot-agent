using BotAgent.Domain.Plugins;
using BotAgent.Services.Plugins;

namespace BotAgent.EngineProbe;

internal static class PluginDependencyChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly ReplyPolicyContext Context = new("synthetic", false, true);

    internal static async Task Run()
    {
        await Readiness();
        await BoundGenerationAndTransitiveDrain();
        await PreparationRace();
        await ContributionInvocationLease();
        await DisabledMetadataIsNotReady();
        await ShutdownDependencyOrder();
        await PluginContributionChecks.Run();
    }

    private static async Task Readiness()
    {
        var registry = new PluginManager(_ => { });
        var provider = new Policy("test.provider");
        void Reject() => Expect(() => registry.PrepareDependencies("test.consumer", [new(provider.Id, "1.0.0")]),
            "plugin_dependency_unavailable");
        Reject();
        registry.Register(provider);
        Ensure(!registry.IsReady(provider.Id), "registration is not readiness");
        Reject();
        await registry.StartAllAsync(default);
        Ensure(registry.IsReady(provider.Id), "successful initialization is ready");
        registry.SetEnabled(provider.Id, false); Reject();
        registry.SetEnabled(provider.Id, true);
        using (var prepared = registry.PrepareDependencies("test.consumer", [new(provider.Id, provider.Version)]))
            await ExpectAsync(() => registry.ReplaceAsync(new Policy("test.consumer"), true, prepared, 2), "plugin_dependency_unavailable");
        Expect(() => registry.PrepareDependencies("test.consumer", [new(provider.Id, "2.0.0")]), "plugin_dependency_unavailable");
        Expect(() => registry.PrepareDependencies("test.consumer", [new(provider.Id, "1.0.0")], 2), "plugin_dependency_unavailable");
        Expect(() => registry.PrepareDependencies(provider.Id, [new(provider.Id, "1.0.0")]), "plugin_dependency_cycle");
        var broken = new Policy("test.broken") { FailInitialize = true };
        registry.Register(broken);
        await registry.StartAllAsync(default);
        Ensure(!registry.IsReady(broken.Id), "failed initializer is never ready");
        Expect(() => registry.PrepareDependencies("test.consumer", [new(broken.Id, "1.0.0")]), "plugin_dependency_unavailable");
        await registry.StopAllAsync(default);
    }

    private static async Task Bind(PluginManager registry, Policy consumer, string provider)
    {
        using var prepared = registry.PrepareDependencies(consumer.Id, [new(provider, "1.0.0")]);
        await registry.ReplaceAsync(consumer, true, prepared, 1);
    }

    private static async Task BoundGenerationAndTransitiveDrain()
    {
        var registry = new PluginManager(_ => { });
        var root = new Policy("test.root");
        var middle = new Policy("test.middle");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var leaf = new Policy("test.leaf") { Callback = () => { entered.Set(); release.Wait(); } };
        try
        {
            await registry.ReplaceAsync(root, true);
            await Bind(registry, middle, root.Id);
            await Bind(registry, leaf, middle.Id);
            var facade = (IReplyPolicyPlugin)registry.Find(leaf.Id)!;
            registry.SetEnabled(root.Id, false);
            Ensure(!registry.IsReady(leaf.Id) && facade.BlockReason(Context) == "plugin_dependency_unavailable"
                && leaf.Calls == 0, "disabled transitive provider prevents the consumer callback");
            registry.SetEnabled(root.Id, true);
            Ensure(registry.IsReady(leaf.Id), "same live provider can resume");
            Expect(() => registry.PrepareDependencies(root.Id, [new(leaf.Id, "1.0.0")]), "plugin_dependency_cycle");
            var call = Task.Run(() => facade.BlockReason(Context));
            Ensure(entered.Wait(Deadline), "consumer call entered");
            var replacement = new Policy(root.Id);
            var replacing = registry.ReplaceAsync(replacement, true);
            Ensure(!replacing.IsCompleted && root.Stops == 0, "transitive provider remains alive for consumer call");
            Ensure(!registry.IsReady(leaf.Id) && facade.BlockReason(Context) == "plugin_dependency_unavailable",
                "same-version replacement cannot silently rebind the old consumer");
            release.Set();
            await call.WaitAsync(Deadline);
            await replacing.WaitAsync(Deadline);
            Ensure(root.Stops == 1 && leaf.Calls == 1, "lease drains before provider shutdown");
            await Bind(registry, new Policy(middle.Id), root.Id);
            Ensure(!registry.IsReady(leaf.Id), "middle replacement also requires leaf rebind");
            var rebound = new Policy(leaf.Id);
            await Bind(registry, rebound, middle.Id);
            Ensure(registry.IsReady(leaf.Id) && facade.BlockReason(Context) is null && rebound.Calls == 1,
                "reinitialized chain resumes through existing facade");
        }
        finally { release.Set(); await registry.StopAllAsync(default).WaitAsync(Deadline); }
    }

    private static async Task PreparationRace()
    {
        var registry = new PluginManager(_ => { });
        var provider = new Policy("test.provider");
        await registry.ReplaceAsync(provider, true);
        using var prepared = registry.PrepareDependencies("test.consumer", [new(provider.Id, "1.0.0")]);
        var replacing = registry.ReplaceAsync(new Policy(provider.Id), true);
        try
        {
            Ensure(!replacing.IsCompleted && provider.Stops == 0, "initialization preparation pins provider");
            await ExpectAsync(() => registry.ReplaceAsync(new Policy("test.consumer"), true, prepared, 1), "plugin_dependency_busy");
            Ensure(registry.Find("test.consumer") is null && provider.Stops == 0, "publication contention returns without abandoning preparation lease");
        }
        finally { prepared.Dispose(); }
        await replacing.WaitAsync(Deadline);
        await ExpectAsync(() => registry.ReplaceAsync(new Policy("test.consumer"), true, prepared, 1), "plugin_dependency_lease_expired");
        await registry.StopAllAsync(default).WaitAsync(Deadline);
    }

    private static async Task ContributionInvocationLease()
    {
        var registry = new PluginManager(_ => { });
        var provider = new Policy("test.provider");
        await registry.ReplaceAsync(provider, true, 2);
        var consumer = new Policy("test.python");
        PluginManager.InvocationGate gate;
        using (var prepared = registry.PrepareDependencies(consumer.Id, [new(provider.Id, provider.Version)], 2))
            gate = await registry.ReplaceAndBindAsync(consumer, true, 2, prepared);
        using var invocation = gate.TryAcquire();
        Ensure(invocation is not null && gate.IsReady, "external contribution acquires exact-generation dependency lease");
        var replacing = registry.ReplaceAsync(new Policy(provider.Id), true, 2);
        Ensure(!replacing.IsCompleted && provider.Stops == 0, "external async contribution keeps provider alive");
        Ensure(!gate.IsReady && gate.TryAcquire() is null, "external contribution gate refuses stale generation");
        invocation!.Dispose();
        invocation.Dispose();
        await replacing.WaitAsync(Deadline);
        Ensure(provider.Stops == 1, "external contribution lease releases provider exactly once");
        await registry.StopAllAsync(default).WaitAsync(Deadline);
    }

    private static async Task DisabledMetadataIsNotReady()
    {
        var registry = new PluginManager(_ => { });
        var provider = new Policy("test.disabled");
        var gate = await registry.ReplaceAndBindAsync(provider, false, 1);
        registry.SetEnabled(provider.Id, true);
        await registry.StartAllAsync(default);
        Ensure(!registry.IsReady(provider.Id) && !gate.IsReady && gate.TryAcquire() is null,
            "metadata-only package cannot become ready through an intent toggle");
        Ensure(((IReplyPolicyPlugin)registry.Find(provider.Id)!).BlockReason(Context) == "plugin_not_ready" && provider.Calls == 0,
            "metadata-only generation cannot execute before runtime preparation");
        Expect(() => registry.PrepareDependencies("test.consumer", [new(provider.Id, provider.Version)]), "plugin_dependency_unavailable");
        await registry.StopAllAsync(default).WaitAsync(Deadline);
    }

    private static async Task ShutdownDependencyOrder()
    {
        var stopped = new List<string>();
        Policy Make(string id) => new(id) { ShutdownCallback = () => stopped.Add(id) };
        var registry = new PluginManager(_ => { });
        await registry.ReplaceAsync(Make("test.root"), true);
        await Bind(registry, Make("test.middle"), "test.root");
        await Bind(registry, Make("test.leaf"), "test.middle");
        await registry.StopAllAsync(default).WaitAsync(Deadline);
        Ensure(stopped.SequenceEqual(["test.leaf", "test.middle", "test.root"]),
            "shutdown completes consumer cleanup before stopping providers");
    }

    private static void Ensure(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Expect(Func<IDisposable> action, string code)
    {
        try { using var unexpected = action(); }
        catch (InvalidOperationException ex) when (ex.Message == code) { return; }
        throw new InvalidOperationException("expected " + code);
    }
    private static async Task ExpectAsync(Func<Task> action, string code)
    {
        try { await action().WaitAsync(Deadline); }
        catch (InvalidOperationException ex) when (ex.Message == code) { return; }
        throw new InvalidOperationException("expected " + code);
    }

    private sealed class Policy(string id) : IReplyPolicyPlugin
    {
        public string Id => id;
        public string Name => id;
        public string Version => "1.0.0";
        public string Description => "Synthetic dependency fixture";
        public PluginCategory Category => PluginCategory.Feature;
        public bool FailInitialize { get; init; }
        public Action? Callback { get; init; }
        public Action? ShutdownCallback { get; init; }
        public int Calls, Stops;
        public Task InitializeAsync(CancellationToken ct) => FailInitialize
            ? Task.FromException(new InvalidOperationException("synthetic_failure")) : Task.CompletedTask;
        public string? BlockReason(ReplyPolicyContext context) { Calls++; Callback?.Invoke(); return null; }
        public Task ShutdownAsync(CancellationToken ct) { Stops++; ShutdownCallback?.Invoke(); return Task.CompletedTask; }
    }
}
