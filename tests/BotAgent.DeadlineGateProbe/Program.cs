using System.Diagnostics;
using System.Reflection;
using BotAgent.Services.Resilience;
using Microsoft.Data.Sqlite;

var failed = 0;
var passed = 0;
void Check(bool condition, string name)
{
    if (condition) { passed++; Console.WriteLine("PASS " + name); }
    else { failed++; Console.WriteLine("FAIL " + name); }
}

var breaker = new ToolCircuitBreaker("synthetic", timeoutThreshold: 1,
    hardTimeout: TimeSpan.FromMilliseconds(30));
var complete = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
var timer = Stopwatch.StartNew();
var run = breaker.RunAsync(_ => complete.Task);
var winner = await Task.WhenAny(run, Task.Delay(250));
Check(winner == run, "uncooperative async operation returns within deadline budget");
complete.SetResult(10001);
var result = await run;
Check(result.TimedOut && !result.Succeeded, "late success is not reported as success");
await Task.Delay(30);
Check(breaker.Snapshot().State == ToolCircuitState.Open, "late success cannot close circuit");

breaker = new ToolCircuitBreaker("synthetic-sync", hardTimeout: TimeSpan.FromMilliseconds(30));
timer.Restart();
result = await breaker.RunAsync(_ => { Thread.Sleep(250); return Task.FromResult(10001); });
Check(result.TimedOut && timer.ElapsedMilliseconds < 180, "synchronous delegate cannot block caller deadline");

using (var cancel = new CancellationTokenSource(30))
{
    breaker = new ToolCircuitBreaker("synthetic-cancel", hardTimeout: TimeSpan.FromSeconds(2));
    complete = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    run = breaker.RunAsync(_ => complete.Task, cancel.Token);
    winner = await Task.WhenAny(run, Task.Delay(250));
    Check(winner == run, "external cancellation bounds uncooperative await");
    complete.SetException(new InvalidOperationException("synthetic late fault"));
    try { await run; Check(false, "external cancellation propagates"); }
    catch (OperationCanceledException) { Check(true, "external cancellation propagates"); }
    Check(breaker.Snapshot().ConsecutiveTimeouts == 0, "external cancellation does not count as timeout");
}

// 2026-10-18（Wave3 复审 F6）：ResizableReplyGate 已随回复链迁入 BotAgent.Engine，
// 不能再从 ToolCircuitBreaker 所在的 Host 程序集里找 —— 从真实 Engine 类型定位。
var gateType = typeof(BotAgent.Services.Reply.ReplyPipeline).Assembly
    .GetType("BotAgent.Services.Reply.ResizableReplyGate");
Check(gateType is not null, "gate retirement uses reference counted leases");
if (gateType is not null)
{
    var gate = Activator.CreateInstance(gateType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        null, new object[] { 1 }, null)!;
    object Reserve() => gateType.GetMethod("Reserve")!.Invoke(gate, null)!;
    Task Wait(object lease, CancellationToken ct = default) => (Task)lease.GetType().GetMethod("WaitAsync")!.Invoke(lease, new object[] { ct })!;
    void Resize(int n) => gateType.GetMethod("Resize")!.Invoke(gate, new object[] { n });
    var first = Reserve();
    await Wait(first);
    var second = Reserve();
    var pending = Wait(second);
    Resize(2);
    var newer = Reserve();
    await Wait(newer).WaitAsync(TimeSpan.FromSeconds(1));
    Check(!pending.IsCompleted, "retired waiter still waits for its original holder");
    ((IDisposable)first).Dispose();
    await pending.WaitAsync(TimeSpan.FromSeconds(1));
    ((IDisposable)second).Dispose();
    ((IDisposable)newer).Dispose();
    Check(true, "retired waiter drains without disposed semaphore");
    object CurrentGeneration() => gateType.GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate)!;
    int References(object generation) => (int)generation.GetType().GetField("References")!.GetValue(generation)!;
    SemaphoreSlim Semaphore(object generation) => (SemaphoreSlim)generation.GetType().GetField("Semaphore")!.GetValue(generation)!;
    Resize(1);
    var retired = CurrentGeneration();
    var retiredHandle = Semaphore(retired).AvailableWaitHandle;
    using var cancelWaiter = new CancellationTokenSource();
    var holder = Reserve();
    await Wait(holder);
    var canceledLease = Reserve();
    var canceledWait = Wait(canceledLease, cancelWaiter.Token);
    Check(References(retired) == 2, "reserved waiter counts before gate resize");
    Resize(2);
    cancelWaiter.Cancel();
    try { await canceledWait; Check(false, "queued gate waiter is cancellable"); }
    catch (OperationCanceledException) { Check(true, "queued gate waiter is cancellable"); }
    ((IDisposable)canceledLease).Dispose();
    Check(References(retired) == 1 && !retiredHandle.SafeWaitHandle.IsClosed,
        "canceled waiter releases its reference without disposing holder semaphore");
    Check(Semaphore(retired).CurrentCount == 0, "canceled waiter does not release an unacquired permit");
    ((IDisposable)holder).Dispose();
    Check(References(retired) == 0 && retiredHandle.SafeWaitHandle.IsClosed,
        "last retired lease closes the actual old semaphore handle");
    ((IDisposable)canceledLease).Dispose();
    ((IDisposable)holder).Dispose();
    Check(References(retired) == 0, "duplicate lease disposal cannot decrement references twice");
    var unwaitedGeneration = CurrentGeneration();
    var unwaitedHandle = Semaphore(unwaitedGeneration).AvailableWaitHandle;
    var unwaited = Reserve();
    Resize(3);
    Check(!unwaitedHandle.SafeWaitHandle.IsClosed, "reserved but not yet waiting lease keeps old generation alive");
    ((IDisposable)unwaited).Dispose();
    Check(References(unwaitedGeneration) == 0 && unwaitedHandle.SafeWaitHandle.IsClosed,
        "unused reservation releases and disposes its retired generation");
    var unchanged = CurrentGeneration();
    Resize(3);
    Check(ReferenceEquals(unchanged, CurrentGeneration()), "same-size resize preserves the current generation");
    foreach (var invalid in new[] { -1, 0, 17, int.MaxValue })
    {
        try { Resize(invalid); Check(false, "invalid resize rejects permit count " + invalid); }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException)
        { Check(ReferenceEquals(unchanged, CurrentGeneration()), "invalid resize preserves current generation " + invalid); }
        try { Activator.CreateInstance(gateType, new object[] { invalid }); Check(false, "invalid constructor rejects permit count " + invalid); }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException)
        { Check(true, "invalid constructor rejects permit count " + invalid); }
    }
    var precanceled = Reserve();
    try { await Wait(precanceled, cancelWaiter.Token); Check(false, "pre-canceled gate wait does not acquire"); }
    catch (OperationCanceledException) { Check(true, "pre-canceled gate wait does not acquire"); }
    ((IDisposable)precanceled).Dispose();
    Check(References(unchanged) == 0 && Semaphore(unchanged).CurrentCount == 3,
        "pre-canceled reservation leaves reference and permit counts intact");
    await Task.WhenAll(Enumerable.Range(0, 16).Select(async index =>
    {
        for (var iteration = 0; iteration < 40; iteration++)
        {
            Resize((index + iteration) % 16 + 1);
            using var lease = (IDisposable)Reserve();
            await Wait(lease).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }));
    Check(true, "concurrent repeated resize and reserve have no disposal race");
}

bool TokenAlive(CancellationToken token)
{
    try { _ = token.WaitHandle; return true; }
    catch (ObjectDisposedException) { return false; }
}
async Task<bool> Eventually(Func<bool> condition)
{
    for (var attempt = 0; attempt < 200; attempt++)
    {
        if (condition()) return true;
        await Task.Delay(5);
    }
    return condition();
}

async Task CheckCallbackLifetime(bool externalCancel)
{
    var name = externalCancel ? "external cancel" : "deadline";
    using var caller = new CancellationTokenSource();
    using var callbackRelease = new ManualResetEventSlim();
    var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var callbackFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
    var work = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var circuit = new ToolCircuitBreaker("synthetic-callback", timeoutThreshold: 1,
        hardTimeout: externalCancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(120));
    var pendingRun = circuit.RunAsync(token =>
    {
        token.Register(() =>
        {
            callbackEntered.TrySetResult();
            callbackRelease.Wait();
            callbackFinished.TrySetResult(TokenAlive(token));
            throw new InvalidOperationException("synthetic callback failure");
        });
        entered.TrySetResult(token);
        return work.Task;
    }, caller.Token);
    var operationToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Task<Exception?>? cancelCall = null;
    if (externalCancel)
    {
        cancelCall = Task.Run<Exception?>(() =>
        {
            try { caller.Cancel(); return null; }
            catch (Exception ex) { return ex; }
        });
    }
    try
    {
        var callerBounded = await Task.WhenAny(pendingRun, Task.Delay(1000)) == pendingRun;
        Check(callerBounded,
            name + " bounds caller even while cancellation callback blocks");
        if (!callerBounded)
        {
            // Regressions must fail the assertion rather than strand the probe behind its own barrier.
            callbackRelease.Set();
            work.TrySetResult(10001);
        }
        var callbackStarted = await Task.WhenAny(callbackEntered.Task, Task.Delay(1000)) == callbackEntered.Task;
        Check(callbackStarted,
            name + " callback actually starts");
        if (cancelCall is not null)
            Check(await Task.WhenAny(cancelCall, Task.Delay(150)) == cancelCall,
                "external Cancel itself does not run blocking operation callbacks inline");
        if (externalCancel)
        {
            try { await pendingRun; Check(false, name + " propagates caller cancellation"); }
            catch (OperationCanceledException) { Check(true, name + " propagates caller cancellation"); }
        }
        else
        {
            var timeout = await pendingRun;
            Check(timeout.TimedOut && timeout.ReasonCode == "tool_timeout", "blocked callback does not replace timeout result");
        }
        var beforeLate = circuit.Snapshot();
        Check(TokenAlive(operationToken), name + " keeps CTS alive while underlying work is still pending");
        work.TrySetResult(10001);
        await Task.Delay(20);
        Check(TokenAlive(operationToken), name + " keeps CTS alive after work settles until callbacks settle");
        callbackRelease.Set();
        Check(callbackStarted && await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            name + " callback can safely access its live token");
        if (cancelCall is not null)
            Check(await cancelCall.WaitAsync(TimeSpan.FromSeconds(2)) is null,
                "operation callback fault does not escape caller Cancel");
        Check(await Eventually(() => !TokenAlive(operationToken)), name + " eventually disposes CTS after work and callbacks");
        Check(circuit.Snapshot() == beforeLate, name + " late success and callback fault cannot mutate circuit state");
    }
    finally
    {
        callbackRelease.Set();
        work.TrySetResult(10001);
        if (cancelCall is not null) await cancelCall.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
await CheckCallbackLifetime(false);
await CheckCallbackLifetime(true);

var faultTokenReady = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
var lateFault = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
var faultBreaker = new ToolCircuitBreaker("synthetic-late-fault", timeoutThreshold: 1,
    hardTimeout: TimeSpan.FromMilliseconds(120));
var lateFaultRun = faultBreaker.RunAsync(token => { faultTokenReady.SetResult(token); return lateFault.Task; });
var faultToken = await faultTokenReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
Check((await lateFaultRun).TimedOut, "late-fault fixture first returns timeout");
var beforeFault = faultBreaker.Snapshot();
Check(TokenAlive(faultToken), "late-fault operation still owns a live CTS after deadline");
lateFault.SetException(new InvalidOperationException("synthetic late failure"));
Check(await Eventually(() => !TokenAlive(faultToken)), "late fault is observed and CTS is eventually reclaimed");
Check(faultBreaker.Snapshot() == beforeFault, "late fault cannot reopen or reset circuit counters");

using (var connection = new SqliteConnection("Data Source=:memory:"))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT sqlite_version()";
    var version = Version.Parse((string)command.ExecuteScalar()!);
    Console.WriteLine("SQLite runtime=" + version);
    Check(version >= new Version(3, 50, 2), "native SQLite is newer than CVE-2025-6965 affected boundary");
}
Console.WriteLine($"Passed {passed}, failed {failed}");
return failed == 0 ? 0 : 1;
