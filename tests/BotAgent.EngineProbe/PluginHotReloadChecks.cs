using BotAgent.Domain.Plugins;
using BotAgent.Services.Plugins;

namespace BotAgent.EngineProbe;

internal static class PluginHotReloadChecks
{
    private static readonly ReplyPolicyContext Context = new("group:10001", false, false);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    internal static async Task Run()
    {
        await ReplacementPublishesBeforeDrain();
        await RemovalDrainsFaultedCall();
        await LifecycleOperationsAreSerialized();
        await CancellationBeforePublicationRetainsOwnership();
        await StopDrainsDisabledInstances();
        await ShutdownFailureDoesNotUndoPublication();
        await CallbackLifecycleReentryFailsFast();
    }

    private static async Task ReplacementPublishesBeforeDrain()
    {
        var registry = new PluginManager(_ => { });
        using var release = new ManualResetEventSlim();
        var old = new ProbePolicy("old_policy", release);
        registry.Register(old);
        var stale = (IReplyPolicyPlugin)registry.Find(old.Id)!;
        Ensure(!ReferenceEquals(stale, old), "Find must not expose the raw plugin");
        var call = Task.Run(() => stale.BlockReason(Context));
        Task replacement = Task.CompletedTask;
        try
        {
            await old.Entered.Task.WaitAsync(Deadline);
            var candidate = new ProbePolicy("new_policy");
            using var cancelledAfterPublish = new CancellationTokenSource();
            replacement = registry.ReplaceAsync(candidate, true, cancelledAfterPublish.Token);
            Ensure(!replacement.IsCompleted && old.ShutdownCalls == 0, "old call must delay shutdown and completion");
            Ensure(registry.Find(old.Id)?.Version == "new_policy", "candidate must publish before old drain");
            Ensure(new PluginReplyPolicies(registry).BlockReason(Context) == "new_policy", "candidate must dispatch during old drain");
            Ensure(stale.BlockReason(Context) == "new_policy" && old.CallCount == 1,
                "stale lookup must lease the replacement rather than bypass both policies");
            registry.SetEnabled(old.Id, false);
            Ensure(new PluginReplyPolicies(registry).BlockReason(Context) is null, "disable must target published candidate");
            registry.SetEnabled(old.Id, true);
            Ensure(new PluginReplyPolicies(registry).BlockReason(Context) == "new_policy", "enable must not resurrect old generation");
            cancelledAfterPublish.Cancel();
            Ensure(!replacement.IsCompleted, "post-publication cancellation must not abandon drain");
            release.Set();
            Ensure(await call.WaitAsync(Deadline) == "old_policy", "admitted old call must finish normally");
            await replacement.WaitAsync(Deadline);
            Ensure(old.ShutdownCalls == 1 && candidate.ShutdownCalls == 0, "only retired generation shuts down");
            await stale.ShutdownAsync(default);
            Ensure(registry.Find(old.Id)?.Version == "new_policy", "stale facade shutdown must not remove candidate");
            await registry.StartAllAsync(default);
            Ensure(candidate.InitializeCalls == 0, "prepared replacement must not initialize twice");
            await registry.StopAllAsync(default);
            await registry.StopAllAsync(default);
            Ensure(old.ShutdownCalls == 1 && candidate.ShutdownCalls == 1, "shutdown must happen once per generation");
        }
        finally
        {
            release.Set();
            await call.WaitAsync(Deadline);
            await replacement.WaitAsync(Deadline);
            await registry.StopAllAsync(default);
        }
    }

    private static async Task RemovalDrainsFaultedCall()
    {
        var registry = new PluginManager(_ => { });
        using var release = new ManualResetEventSlim();
        var old = new ProbePolicy("old_policy", release) { ThrowOnCall = true };
        registry.Register(old);
        var stale = (IReplyPolicyPlugin)registry.Find(old.Id)!;
        var call = Task.Run(() => new PluginReplyPolicies(registry).BlockReason(Context));
        Task removal = Task.CompletedTask;
        try
        {
            await old.Entered.Task.WaitAsync(Deadline);
            removal = registry.RemoveAsync(old.Id);
            Ensure(registry.Find(old.Id) is null && registry.GetAll().Count == 0, "remove must unpublish before drain");
            Ensure(stale.BlockReason(Context) is null, "removed policy facade must be harmless");
            Ensure(!removal.IsCompleted && old.ShutdownCalls == 0, "remove must wait for old call");
            release.Set();
            Ensure(await call.WaitAsync(Deadline) == "reply_policy_error", "exception must retain fail-closed behavior");
            await removal.WaitAsync(Deadline);
            await registry.RemoveAsync(old.Id);
            Ensure(old.ShutdownCalls == 1, "faulted call must release its lease exactly once");
        }
        finally
        {
            release.Set();
            await call.WaitAsync(Deadline);
            await removal.WaitAsync(Deadline);
            await registry.StopAllAsync(default);
        }
    }

    private static async Task LifecycleOperationsAreSerialized()
    {
        var registry = new PluginManager(_ => { });
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new ProbePolicy("old_policy") { InitializeRelease = initialized.Task };
        registry.Register(old);
        var start = registry.StartAllAsync(default);
        Task replacement = Task.CompletedTask;
        try
        {
            await old.InitializeEntered.Task.WaitAsync(Deadline);
            var candidate = new ProbePolicy("new_policy");
            replacement = registry.ReplaceAsync(candidate, true);
            Ensure(!replacement.IsCompleted && old.ShutdownCalls == 0, "replacement must wait for initialization");
            registry.SetEnabled(old.Id, false);
            Ensure(!registry.IsEnabled(old.Id), "SetEnabled must not block on plugin initialization");
            initialized.SetResult();
            await start.WaitAsync(Deadline);
            await replacement.WaitAsync(Deadline);
            Ensure(old.InitializeCalls == 1 && old.ShutdownCalls == 1, "start and retirement must not overlap");
            await registry.StartAllAsync(default);
            Ensure(candidate.InitializeCalls == 0, "StartAll must skip runtime-prepared generation");
        }
        finally
        {
            initialized.TrySetResult();
            await start.WaitAsync(Deadline);
            await replacement.WaitAsync(Deadline);
            await registry.StopAllAsync(default);
        }
    }

    private static async Task CancellationBeforePublicationRetainsOwnership()
    {
        var registry = new PluginManager(_ => { });
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new ProbePolicy("old_policy") { InitializeRelease = initialized.Task };
        registry.Register(old);
        var start = registry.StartAllAsync(default);
        try
        {
            await old.InitializeEntered.Task.WaitAsync(Deadline);
            var candidate = new ProbePolicy("new_policy");
            using var cancellation = new CancellationTokenSource();
            var replacement = registry.ReplaceAsync(candidate, true, cancellation.Token);
            cancellation.Cancel();
            try
            {
                await replacement.WaitAsync(Deadline);
                throw new InvalidOperationException("queued replacement must honor cancellation");
            }
            catch (OperationCanceledException) { }
            Ensure(registry.Find(old.Id)?.Version == "old_policy", "cancelled candidate must not publish");
            Ensure(candidate.InitializeCalls == 0 && candidate.ShutdownCalls == 0, "rejected candidate stays caller-owned");
        }
        finally
        {
            initialized.TrySetResult();
            await start.WaitAsync(Deadline);
            await registry.StopAllAsync(default);
        }
    }

    private static async Task StopDrainsDisabledInstances()
    {
        var registry = new PluginManager(_ => { });
        using var release = new ManualResetEventSlim();
        var old = new ProbePolicy("old_policy", release);
        registry.Register(old);
        var stale = (IReplyPolicyPlugin)registry.Find(old.Id)!;
        var call = Task.Run(() => stale.BlockReason(Context));
        Task stop = Task.CompletedTask;
        try
        {
            await old.Entered.Task.WaitAsync(Deadline);
            registry.SetEnabled(old.Id, false);
            stop = registry.StopAllAsync(default);
            Ensure(!stop.IsCompleted && old.ShutdownCalls == 0, "disabled instances still need a drain");
            Ensure(registry.Find(old.Id) is null && stale.BlockReason(Context) is null, "stop must revoke all dispatch");
            var candidate = new ProbePolicy("new_policy");
            var replacement = registry.ReplaceAsync(candidate, true);
            release.Set();
            await call.WaitAsync(Deadline);
            await stop.WaitAsync(Deadline);
            try
            {
                await replacement.WaitAsync(Deadline);
                throw new InvalidOperationException("stopped registry must reject publication");
            }
            catch (InvalidOperationException ex) when (ex.Message == "plugin_registry_stopped") { }
            Ensure(old.ShutdownCalls == 1 && candidate.ShutdownCalls == 0, "stop cannot dispose an unaccepted candidate");
        }
        finally
        {
            release.Set();
            await call.WaitAsync(Deadline);
            await stop.WaitAsync(Deadline);
            await registry.StopAllAsync(default);
        }
    }

    private static async Task ShutdownFailureDoesNotUndoPublication()
    {
        var registry = new PluginManager(_ => throw new InvalidOperationException("synthetic_log_failure"));
        var old = new ProbePolicy("old_policy") { ThrowOnShutdown = true };
        registry.Register(old);
        var candidate = new ProbePolicy("new_policy");
        await registry.ReplaceAsync(candidate, true);
        Ensure(old.ShutdownCalls == 1 && registry.Find(old.Id)?.Version == "new_policy", "shutdown or log failure cannot undo publication");
        await registry.StopAllAsync(default);
        Ensure(candidate.ShutdownCalls == 1, "remaining candidate must still retire");
    }

    private static async Task CallbackLifecycleReentryFailsFast()
    {
        var registry = new PluginManager(_ => { });
        var rejected = 0;
        async Task Reenter()
        {
            try { await registry.StopAllAsync(default); }
            catch (InvalidOperationException error) when (error.Message == "plugin_lifecycle_reentry")
            { Interlocked.Increment(ref rejected); return; }
            throw new InvalidOperationException("callback lifecycle reentry must be rejected");
        }
        var plugin = new ProbePolicy("reentry")
        {
            OnInitialize = Reenter, OnShutdown = Reenter,
            OnCall = () => Reenter().GetAwaiter().GetResult()
        };
        registry.Register(plugin);
        await registry.StartAllAsync(default).WaitAsync(Deadline);
        Ensure(new PluginReplyPolicies(registry).BlockReason(Context) == "reentry", "read-only dispatch remains callable");
        await registry.StopAllAsync(default).WaitAsync(Deadline);
        Ensure(rejected == 3, "initialize, policy and shutdown reentry must all fail fast without deadlock");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ProbePolicy(string reason, ManualResetEventSlim? release = null) : IReplyPolicyPlugin
    {
        public string Id => "synthetic.reload.policy";
        public string Name => "Synthetic reload policy";
        public string Version => reason;
        public string Description => "Synthetic lifecycle and invocation barriers";
        public PluginCategory Category => PluginCategory.Management;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InitializeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? InitializeRelease { get; init; }
        public Func<Task>? OnInitialize { get; init; }
        public Func<Task>? OnShutdown { get; init; }
        public Action? OnCall { get; init; }
        public bool ThrowOnCall { get; init; }
        public bool ThrowOnShutdown { get; init; }
        public int InitializeCalls;
        public int ShutdownCalls;
        public int CallCount;

        public async Task InitializeAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref InitializeCalls);
            InitializeEntered.TrySetResult();
            if (InitializeRelease is not null) await InitializeRelease.WaitAsync(ct);
            if (OnInitialize is not null) await OnInitialize();
        }

        public string? BlockReason(ReplyPolicyContext context)
        {
            Interlocked.Increment(ref CallCount);
            Entered.TrySetResult();
            OnCall?.Invoke();
            if (release is not null && !release.Wait(Deadline)) throw new TimeoutException("synthetic_barrier_timeout");
            if (ThrowOnCall) throw new InvalidOperationException("synthetic_policy_failure");
            return reason;
        }

        public Task ShutdownAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref ShutdownCalls);
            if (ThrowOnShutdown) throw new InvalidOperationException("synthetic_shutdown_failure");
            return OnShutdown?.Invoke() ?? Task.CompletedTask;
        }
    }
}
