using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;

internal partial class Program
{
    internal static async Task<int> RunGovernanceDeadlineChecks(string root)
    {
        var checks = 0;
        var failures = 0;
        var observations = new List<object>();
        void Check(bool pass, string label)
        {
            checks++;
            if (!pass) failures++;
            Console.WriteLine($"{(pass ? "PASS" : "FAIL")} governance deadline: {label}");
        }
        async Task<bool> Complete(Task task, CancellationTokenSource cleanup, Stopwatch watch, string scenario)
        {
            using var timer = new CancellationTokenSource();
            var remaining = TimeSpan.FromMilliseconds(Math.Max(1, 6500 - watch.Elapsed.TotalMilliseconds));
            var timely = await Task.WhenAny(task, Task.Delay(remaining, timer.Token)) == task;
            timer.Cancel();
            var elapsed = watch.Elapsed.TotalMilliseconds;
            if (!timely) cleanup.Cancel();
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) when (cleanup.IsCancellationRequested) { }
            observations.Add(new { scenario, timely, elapsedMilliseconds = Math.Round(elapsed) });
            return timely;
        }
        Task<IDisposable> Commit(PluginGovernanceService policy, PluginPackageManifest manifest)
            => (Task<IDisposable>)typeof(PluginGovernanceService).GetMethod("AcquireCommitAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(policy, [manifest, CancellationToken.None])!;

        var headers = new GovernanceFixture(root, "probe.deadline-headers");
        var headerCancelled = false;
        headers.Http.GovernanceResponse = async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { headerCancelled = true; throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        using (var cleanup = new CancellationTokenSource())
        {
            var revision = (await headers.Market.ReadInstalledAsync(default)).Revision;
            var watch = Stopwatch.StartNew();
            var preview = headers.Market.PreviewAsync(new("json", revision, Manifest: headers.Manifest), "deadline", cleanup.Token);
            var timely = await Complete(preview, cleanup, watch, "preview-slow-headers");
            Check(timely, "actual JSON preview finishes within 6500ms despite stalled headers");
            Check(timely && headerCancelled && preview.IsCompletedSuccessfully && preview.Result.CanInstall &&
                preview.Result.Governance is { Status: "unknown", Blocked: false } &&
                preview.Result.GovernanceSource is { Status: "unavailable", ErrorCode: "plugin_governance_unavailable" },
                "header timeout cancels transport and returns explicit unknown preview");
            Check((await headers.Packages.GetInstalledAsync()).Count == 0, "preview timeout leaves installation untouched");
        }
        headers.Http.GovernanceResponse = null;
        await headers.Packages.Governance.RefreshAsync(default);
        Check(headers.Packages.Governance.Source.Status == "fresh", "header timeout releases refresh gate for recovery");

        var body = new GovernanceFixture(root, "probe.deadline-body");
        var stream = new StalledGovernanceStream();
        body.Http.GovernanceResponse = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(stream) });
        using (var cleanup = new CancellationTokenSource())
        {
            var watch = Stopwatch.StartNew();
            var refresh = body.Packages.Governance.RefreshAsync(cleanup.Token);
            await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var timely = await Complete(refresh, cleanup, watch, "slow-response-body");
            Check(timely, "response body has the same end-to-end deadline");
            Check(timely && stream.Cancelled && stream.Disposed, "body timeout cancels read and disposes response stream");
            Check(timely && body.Packages.Governance.Source is { Status: "unavailable", ErrorCode: "plugin_governance_unavailable" },
                "body timeout reports unavailable instead of fresh");
        }

        var cached = new GovernanceFixture(root, "probe.deadline-cached");
        cached.Feed("revoked");
        await cached.Packages.Governance.RefreshAsync(default);
        cached.Http.GovernanceResponse = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        using (var cleanup = new CancellationTokenSource())
        {
            var watch = Stopwatch.StartNew();
            var timely = await Complete(cached.Packages.Governance.RefreshAsync(cleanup.Token), cleanup, watch, "cached-revocation");
            Check(timely && cached.Packages.Governance.Source is { Status: "cached", ErrorCode: "plugin_governance_unavailable" },
                "source timeout explicitly retains cached policy");
            Check(cached.Packages.Governance.Evaluate(cached.Manifest).Blocked &&
                new PluginPackageService(cached.PackagesRoot, cached.Http).Governance.Evaluate(cached.Manifest).Blocked,
                "timeout cannot erase in-process or persisted revocation");
            var denied = false;
            try { await Commit(cached.Packages.Governance, cached.Manifest); }
            catch (BotAgent.Panel.ZbaBackendException ex) when (ex.Code == "plugin_governance_revoked") { denied = true; }
            Check(denied, "commit guard still denies cached revoked target");
        }

        var gated = new GovernanceFixture(root, "probe.deadline-gate");
        await gated.Packages.Governance.RefreshAsync(default);
        var before = gated.Packages.Governance.Source;
        using (await Commit(gated.Packages.Governance, gated.Manifest))
        using (var cleanup = new CancellationTokenSource())
        {
            var calls = gated.Http.Calls;
            var watch = Stopwatch.StartNew();
            var timely = await Complete(gated.Packages.Governance.RefreshAsync(cleanup.Token), cleanup, watch, "held-commit-gate");
            Check(timely, "waiting for commit gate consumes the refresh deadline");
            Check(gated.Http.Calls == calls && gated.Packages.Governance.Source == before,
                "queued timeout sends no request and publishes no out-of-order source state");
        }
        using (await Commit(gated.Packages.Governance, gated.Manifest))
        using (var caller = new CancellationTokenSource())
        {
            var refresh = gated.Packages.Governance.RefreshAsync(caller.Token);
            caller.Cancel();
            var propagated = false;
            try { await refresh.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) when (caller.IsCancellationRequested) { propagated = true; }
            Check(propagated && gated.Packages.Governance.Source == before, "caller cancellation while queued remains cancellation");
        }

        var cancelled = new GovernanceFixture(root, "probe.deadline-caller");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledTransport = false;
        cancelled.Http.GovernanceResponse = async ct =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelledTransport = true; throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        using (var caller = new CancellationTokenSource())
        {
            var prior = cancelled.Packages.Governance.Source;
            var refresh = cancelled.Packages.Governance.RefreshAsync(caller.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            caller.Cancel();
            var propagated = false;
            try { await refresh.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) when (caller.IsCancellationRequested) { propagated = true; }
            Check(propagated && cancelledTransport && cancelled.Packages.Governance.Source == prior,
                "caller cancellation during request propagates without success publication");
        }
        cancelled.Http.GovernanceResponse = null;
        await cancelled.Packages.Governance.RefreshAsync(default);
        Check(cancelled.Packages.Governance.Source.Status == "fresh", "caller cancellation releases refresh gate");

        var sharedBudget = new GovernanceFixture(root, "probe.deadline-shared-budget");
        var lease = await Commit(sharedBudget.Packages.Governance, sharedBudget.Manifest);
        try
        {
            sharedBudget.Http.GovernanceResponse = async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            };
            using var cleanup = new CancellationTokenSource();
            var watch = Stopwatch.StartNew();
            var refresh = sharedBudget.Packages.Governance.RefreshAsync(cleanup.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(3500));
            lease.Dispose(); lease = null!;
            var timely = await Complete(refresh, cleanup, watch, "queue-plus-headers");
            Check(timely && sharedBudget.Packages.Governance.Source.ErrorCode == "plugin_governance_unavailable",
                "network gets remaining queue budget rather than a new full deadline");
        }
        finally { lease?.Dispose(); }

        if (Environment.GetEnvironmentVariable("BOTAGENT_IT_GOVERNANCE_EVIDENCE") is { Length: > 0 } evidence)
        {
            Directory.CreateDirectory(evidence);
            await File.WriteAllTextAsync(Path.Combine(evidence, "observations.json"),
                JsonSerializer.Serialize(new { checks, failures, observations }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (failures != 0) throw new InvalidOperationException($"governance deadline: {checks - failures} passed / {failures} failed");
        return checks;
    }

    private sealed class StalledGovernanceStream : Stream
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Cancelled;
        internal bool Disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
