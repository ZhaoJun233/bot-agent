using BotAgent.Services.Memory;

internal static partial class Probe
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main()
    {
        var clock = new ProbeClock();
        BotAgent.Platforms.PlatformClock.Initialize(clock);
        BotAgent.Model.ModelClock.Initialize(clock);
        await ScopeChecks();
        await ModelChecks();
        await PlatformChecks();
        await LifecycleChecks();
        await CadenceChecks();
        Console.WriteLine($"MemoryDeliveryProbe: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task Run(string name, Func<Task> test)
    {
        try
        {
            await test().WaitAsync(TimeSpan.FromSeconds(10));
            _passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("FAIL " + name + " type=" + ex.GetType().Name + " frame=" + ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim());
        }
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("synthetic_assertion_failed");
    }

    private static async Task IgnoreDenial(Func<Task> action)
    {
        try { await action(); }
        catch (MemoryUseInvalidatedException) { }
        catch (BotAgent.Model.ModelFault) { }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class MemoryGate
    {
        private readonly object _gate = new();
        private bool _current = true;
        public bool TryUse(Action commit)
        {
            lock (_gate)
            {
                if (!_current) return false;
                commit();
                return true;
            }
        }
        public void Invalidate() { lock (_gate) _current = false; }
    }

    private static async Task ScopeChecks()
    {
        await Run("scope.no-memory-and-empty-unchanged", () =>
        {
            var commits = 0;
            Require(MemoryUseScope.Capture() is null && MemoryUseScope.CanUse);
            Require(MemoryUseScope.Use(() => ++commits) == 1);
            using (MemoryUseScope.Begin()) Require(MemoryUseScope.TryUse(() => commits++));
            Require(commits == 2 && MemoryUseScope.Capture() is null);
            return Task.CompletedTask;
        });
        await Run("scope.all-receipts-and-nested-parent-required", () =>
        {
            var first = new MemoryGate();
            var second = new MemoryGate();
            using var parent = MemoryUseScope.Begin();
            MemoryUseScope.Register(first.TryUse);
            using var child = MemoryUseScope.Begin();
            MemoryUseScope.Register(second.TryUse);
            Require(MemoryUseScope.CanUse);
            first.Invalidate();
            var commits = 0;
            Require(!MemoryUseScope.TryUse(() => commits++) && commits == 0);
            return Task.CompletedTask;
        });
        await Run("scope.validation-error-fails-closed", () =>
        {
            using var scope = MemoryUseScope.Begin();
            MemoryUseScope.Register(_ => throw new InvalidOperationException("synthetic_backend_error"));
            var commits = 0;
            Require(!MemoryUseScope.TryUse(() => commits++) && commits == 0);
            return Task.CompletedTask;
        });
        await Run("scope.commit-error-not-hidden", () =>
        {
            using var scope = MemoryUseScope.Begin();
            MemoryUseScope.Register(new MemoryGate().TryUse);
            var expected = new InvalidOperationException("synthetic_commit_error");
            Exception? observed = null;
            try { MemoryUseScope.TryUse(() => throw expected); } catch (Exception ex) { observed = ex; }
            Require(ReferenceEquals(expected, observed));
            return Task.CompletedTask;
        });
        await Run("scope.task-run-shares-late-registration", async () =>
        {
            using var scope = MemoryUseScope.Begin();
            var release = Signal();
            var work = Task.Run(async () =>
            {
                await release.Task;
                return MemoryUseScope.CanUse;
            });
            var receipt = new MemoryGate();
            MemoryUseScope.Register(receipt.TryUse);
            receipt.Invalidate();
            release.SetResult();
            Require(!await work);
        });
        await Run("scope.late-registration-invalidates-active-receipt-snapshot", () =>
        {
            using var scope = MemoryUseScope.Begin();
            MemoryUseScope.Register(commit =>
            {
                MemoryUseScope.Register(_ => false);
                commit();
                return true;
            });
            var commits = 0;
            Require(!MemoryUseScope.TryUse(() => commits++) && commits == 0);
            return Task.CompletedTask;
        });
        await Run("scope.bound-action-survives-dispose-without-execution-context", async () =>
        {
            var receipt = new MemoryGate();
            var commits = 0;
            Action queued;
            using (MemoryUseScope.Begin())
            {
                MemoryUseScope.Register(receipt.TryUse);
                queued = MemoryUseScope.Bind(() => { MemoryUseScope.TryUse(() => commits++); });
            }
            receipt.Invalidate();
            Task work;
            using (ExecutionContext.SuppressFlow()) work = Task.Run(queued);
            await work;
            Require(commits == 0 && MemoryUseScope.Capture() is null);
        });
        await Run("scope.bound-async-action-rechecks-after-await", async () =>
        {
            var receipt = new MemoryGate();
            var started = Signal();
            var release = Signal();
            var commits = 0;
            Func<CancellationToken, Task> queued;
            using (MemoryUseScope.Begin())
            {
                MemoryUseScope.Register(receipt.TryUse);
                queued = MemoryUseScope.Bind(async ct =>
                {
                    started.SetResult();
                    await release.Task.WaitAsync(ct);
                    MemoryUseScope.TryUse(() => commits++);
                });
            }
            Task work;
            using (ExecutionContext.SuppressFlow()) work = Task.Run(() => queued(CancellationToken.None));
            await started.Task;
            receipt.Invalidate();
            release.SetResult();
            await work;
            Require(commits == 0);
        });
        await Run("scope.external-await-does-not-hold-receipt-lock", async () =>
        {
            using var scope = MemoryUseScope.Begin();
            var receipt = new MemoryGate();
            MemoryUseScope.Register(receipt.TryUse);
            var response = Signal();
            var inFlight = MemoryUseScope.Use(() => response.Task);
            await Task.Run(receipt.Invalidate);
            Require(!MemoryUseScope.CanUse && !inFlight.IsCompleted);
            response.SetResult();
            await inFlight;
        });
    }
}
