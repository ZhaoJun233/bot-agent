using System.Diagnostics;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Evaluation;
using BotAgent.Services.Plugins;

namespace BotAgent.EngineProbe;

/// <summary>
/// Wave3 Engine 两个新子系统的确定性探针：
///   · <see cref="PluginGate" />：能力/工具 → 预设插件开关；
///   · <see cref="BackgroundEvaluatorQueue" />：有界、去重、重试、超时、可重复停机的后台队列。
///
/// 口径：纯合成输入、纯断言；不连网、不启动机器人、不读写数据目录、不接触任何真实会话。
/// 用法：dotnet run --project tests/BotAgent.EngineProbe -c Release
/// 输出：<c>Passed N; failed M</c>；有失败时退出码非 0。
/// </summary>
public static class Program
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main()
    {
        await RunAsync("PluginGate: enabled capabilities and tools pass", PluginGateEnabledTests);
        await RunAsync("PluginGate: disabled/unregistered known capabilities fail closed", PluginGateDisabledTests);
        await RunAsync("PluginGate: unknown capabilities/tools are ungated", PluginGateUnknownTests);

        await RunAsync("Queue: option range validation", QueueValidationTests);
        await RunAsync("Queue: capacity rejects excess work", QueueCapacityTests);
        await RunAsync("Queue: dedup suppresses duplicate while queued/running", QueueDedupTests);
        await RunAsync("Queue: successful job completes once", QueueSuccessTests);
        await RunAsync("Queue: retry then success", QueueRetrySuccessTests);
        await RunAsync("Queue: retry exhaustion", QueueRetryExhaustionTests);
        await RunAsync("Queue: per-job timeout", QueueTimeoutTests);
        await RunAsync("Queue: cooperative timeout cancellation survives timer races", QueueTimeoutRaceTests);
        await RunAsync("Queue: synchronous delegate invocation counts against the budget", QueueSynchronousDelegateTests);
        await RunAsync("Queue: timeout bounds wait and abandons uncooperative work", QueueTimeoutUncooperativeTests);
        await RunAsync("Queue: cancellation is not retried", QueueCancellationTests);
        await RunAsync("Queue: stop drains accepted work and rejects new work", QueueStopDrainTests);
        await RunAsync("Queue: stop cancels unstarted remainder and completes waiters", QueueStopCancelTests);
        await RunAsync("Queue: stop reports unconfirmed in-flight work as abandoned", QueueStopUnconfirmedTests);
        await RunAsync("Queue: stop is idempotent", QueueIdempotentStopTests);

        Console.WriteLine();
        Console.WriteLine($"Passed {_passed}; failed {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    // ───────────────────────── PluginGate ─────────────────────────

    private static Task PluginGateEnabledTests()
    {
        string[] allPresets =
        {
            "preset.feature.music",
            "preset.feature.voice",
            "preset.feature.stickers",
            "preset.feature.poke",
            "preset.feature.vibes",
            "preset.feature.profiles",
            "preset.feature.websearch",
        };

        var gate = new PluginGate(new FakeRegistry(allPresets, allPresets));

        Check(gate.Allows("music"), "music should be enabled");
        Check(gate.Allows("voice"), "voice should be enabled");
        Check(gate.Allows("stickers"), "stickers should be enabled");
        Check(gate.Allows("poke"), "poke should be enabled");
        Check(gate.Allows("vibes"), "vibes should be enabled");
        Check(gate.Allows("profiles"), "profiles should be enabled");
        Check(gate.Allows("websearch"), "websearch should be enabled");

        Check(gate.AllowsTool("music.listen"), "music.* should follow the music plugin");
        Check(gate.AllowsTool("music.share"), "music.* should follow the music plugin");
        Check(gate.AllowsTool("sticker.send"), "sticker.* should follow the stickers plugin");
        Check(gate.AllowsTool("voice.speak"), "voice.* should follow the voice plugin");
        Check(gate.AllowsTool("poke.send"), "poke.* should follow the poke plugin");
        Check(gate.AllowsTool("web.search"), "web.search should follow the websearch plugin");
        Check(gate.AllowsTool("web.read"), "web.read should follow the websearch plugin");

        Check(
            gate.AllowsAnyTool(new[] { "music.listen", "voice.speak" }),
            "AllowsAnyTool should pass when at least one tool is allowed");
        return Task.CompletedTask;
    }

    private static Task PluginGateDisabledTests()
    {
        // voice 已登记但被禁用；stickers/poke 根本没登记（未登记 = fail closed）。
        var gate = new PluginGate(new FakeRegistry(
            registered: new[] { "preset.feature.music", "preset.feature.voice" },
            enabled: new[] { "preset.feature.music" }));

        Check(gate.Allows("music"), "registered + enabled must pass");
        Check(!gate.Allows("voice"), "registered but disabled must be blocked");
        Check(!gate.Allows("stickers"), "unregistered known capability must fail closed");
        Check(!gate.Allows("poke"), "unregistered known capability must fail closed");

        Check(gate.AllowsTool("music.share"), "music tool follows the enabled plugin");
        Check(!gate.AllowsTool("voice.speak"), "disabled voice plugin must block voice.speak");
        Check(!gate.AllowsTool("sticker.send"), "unregistered stickers plugin must block sticker.send");
        Check(!gate.AllowsAnyTool(new[] { "voice.speak", "sticker.send" }), "no allowed tool in the set");
        Check(gate.AllowsAnyTool(new[] { "sticker.send", "music.listen" }), "music keeps the set allowed");
        return Task.CompletedTask;
    }

    private static Task PluginGateUnknownTests()
    {
        var gate = new PluginGate(new FakeRegistry(Array.Empty<string>(), Array.Empty<string>()));

        // 未知能力不由插件开关管理；未知工具同样放行（与既有工具分派口径一致）。
        Check(gate.Allows("translation"), "unknown capability should not be gated by plugins");
        Check(gate.Allows(string.Empty), "empty capability means unspecified");
        Check(gate.AllowsTool("web.fetch"), "unmapped web.* tool is unknown and ungated");
        Check(gate.AllowsTool("core.ping"), "unknown tool is ungated");
        Check(!gate.AllowsAnyTool(Array.Empty<string>()), "empty tool set has no allowed tool");
        Check(!gate.AllowsAnyTool(null!), "null tool set has no allowed tool");
        return Task.CompletedTask;
    }

    // ─────────────────── BackgroundEvaluatorQueue ───────────────────

    private static async Task QueueValidationTests()
    {
        CheckThrows<ArgumentNullException>(
            () => new BackgroundEvaluatorQueue(null!),
            "null options must throw");
        CheckThrows<ArgumentOutOfRangeException>(
            () => new BackgroundEvaluatorQueue(new BackgroundEvaluatorOptions { Capacity = 0 }),
            "capacity 0 must throw");
        CheckThrows<ArgumentOutOfRangeException>(
            () => new BackgroundEvaluatorQueue(new BackgroundEvaluatorOptions { MaxConcurrency = 0 }),
            "max concurrency 0 must throw");
        CheckThrows<ArgumentOutOfRangeException>(
            () => new BackgroundEvaluatorQueue(new BackgroundEvaluatorOptions { MaxAttempts = 0 }),
            "max attempts 0 must throw");
        CheckThrows<ArgumentOutOfRangeException>(
            () => new BackgroundEvaluatorQueue(new BackgroundEvaluatorOptions { PerJobTimeout = TimeSpan.Zero }),
            "zero per-job timeout must throw");
        CheckThrows<ArgumentOutOfRangeException>(
            () => new BackgroundEvaluatorQueue(new BackgroundEvaluatorOptions { DrainTimeout = TimeSpan.FromMilliseconds(-1) }),
            "negative drain budget must throw");

        var queue = NewQueue();
        CheckThrows<ArgumentException>(
            () => queue.TryEnqueue(" ", "k", _ => Task.CompletedTask),
            "blank job id must throw");
        await StopAsync(queue);

        var postStop = await queue.EnqueueAsync("late", "late", _ => Task.CompletedTask)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(postStop.Status == BackgroundJobStatus.Rejected, "stopped queue must reject new work");
    }

    private static async Task QueueCapacityTests()
    {
        var queue = NewQueue(capacity: 1, concurrency: 1, attempts: 1, drainMs: 500);
        try
        {
            var gate = NewGate();
            var first = queue.EnqueueAsync("job-a", "key-a", ct => gate.Task.WaitAsync(ct));
            await WaitUntilAsync(() => queue.Snapshot().Running == 1, "first job running");

            Check(
                queue.TryEnqueue("job-b", "key-b", _ => Task.CompletedTask),
                "second job should occupy the single queue slot");
            Check(
                !queue.TryEnqueue("job-c", "key-c", _ => Task.CompletedTask),
                "third job should be rejected while the slot is occupied");

            var rejected = await queue.EnqueueAsync("job-d", "key-d", _ => Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Check(rejected.Status == BackgroundJobStatus.Rejected, $"expected Rejected, got {rejected.Status}");
            Check(rejected.Attempts == 0, "rejected jobs must not report attempts");

            gate.TrySetResult();
            var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Check(firstResult.Status == BackgroundJobStatus.Succeeded, $"expected Succeeded, got {firstResult.Status}");

            await WaitUntilAsync(
                () =>
                {
                    var current = queue.Snapshot();
                    return current.Queued == 0 && current.Running == 0;
                },
                "queue to drain");

            var snapshot = queue.Snapshot();
            Check(snapshot.Completed == 2, $"expected 2 completed, got {snapshot.Completed}");
            Check(snapshot.Failed == 0, $"expected 0 failed, got {snapshot.Failed}");
            Check(snapshot.Rejected == 2, $"expected 2 rejected, got {snapshot.Rejected}");
            Check(snapshot.Deduplicated == 0, $"expected 0 deduplicated, got {snapshot.Deduplicated}");
        }
        finally
        {
            await StopAsync(queue);
        }
    }

    private static async Task QueueDedupTests()
    {
        var queue = NewQueue(capacity: 4, concurrency: 1, attempts: 1, drainMs: 500);
        try
        {
            var gate = NewGate();
            var runs = 0;
            var first = queue.EnqueueAsync(
                "job-1",
                "same-key",
                async ct =>
                {
                    Interlocked.Increment(ref runs);
                    await gate.Task.WaitAsync(ct);
                });

            await WaitUntilAsync(() => queue.Snapshot().Running == 1, "first job running");

            var duplicate = await queue.EnqueueAsync("job-2", "same-key", _ => Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Check(duplicate.Status == BackgroundJobStatus.Deduplicated, $"expected Deduplicated, got {duplicate.Status}");
            Check(duplicate.JobId == "job-2", "duplicate result should echo the duplicate job id");
            Check(duplicate.Attempts == 0, "deduplicated jobs must not report attempts");

            Check(
                !queue.TryEnqueue("job-3", "same-key", _ => Task.CompletedTask),
                "TryEnqueue duplicate must return false");

            gate.TrySetResult();
            var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Check(firstResult.Status == BackgroundJobStatus.Succeeded, $"expected Succeeded, got {firstResult.Status}");
            Check(Volatile.Read(ref runs) == 1, "duplicate must not execute work");

            // 前一个任务收敛后，同键应重新可用。
            var afterCompletion = await queue.EnqueueAsync("job-4", "same-key", _ => Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Check(
                afterCompletion.Status == BackgroundJobStatus.Succeeded,
                $"expected Succeeded after dedup release, got {afterCompletion.Status}");

            var snapshot = queue.Snapshot();
            Check(snapshot.Completed == 2, $"expected 2 completed, got {snapshot.Completed}");
            Check(snapshot.Deduplicated == 2, $"expected 2 deduplicated, got {snapshot.Deduplicated}");
        }
        finally
        {
            await StopAsync(queue);
        }
    }

    private static async Task QueueSuccessTests()
    {
        var queue = NewQueue(attempts: 3, drainMs: 500);
        try
        {
            var runs = 0;
            var result = await queue.EnqueueAsync(
                    "ok",
                    "ok",
                    _ =>
                    {
                        Interlocked.Increment(ref runs);
                        return Task.CompletedTask;
                    })
                .WaitAsync(TimeSpan.FromSeconds(5));

            Check(result.Status == BackgroundJobStatus.Succeeded, $"expected Succeeded, got {result.Status}");
            Check(result.Attempts == 1, $"expected 1 attempt, got {result.Attempts}");
            Check(Volatile.Read(ref runs) == 1, "work should run exactly once");

            var snapshot = queue.Snapshot();
            Check(snapshot.Completed == 1, $"expected 1 completed, got {snapshot.Completed}");
            Check(snapshot.Failed == 0, $"expected 0 failed, got {snapshot.Failed}");
            Check(snapshot.Outstanding == 0, $"expected no outstanding work, got {snapshot.Outstanding}");
        }
        finally
        {
            await StopAsync(queue);
        }
    }

    private static async Task QueueRetrySuccessTests()
    {
        var backoffAttempts = new List<int>();
        var queue = new BackgroundEvaluatorQueue(
            new BackgroundEvaluatorOptions
            {
                Capacity = 8,
                MaxConcurrency = 1,
                MaxAttempts = 3,
                PerJobTimeout = TimeSpan.FromMilliseconds(2000),
                DrainTimeout = TimeSpan.FromMilliseconds(500),
            },
            attempt =>
            {
                lock (backoffAttempts)
                {
                    backoffAttempts.Add(attempt);
                }

                return TimeSpan.Zero;
            });

        try
        {
            var runs = 0;
            var result = await queue.EnqueueAsync(
                    "retry-ok",
                    "retry-ok",
                    _ =>
                    {
                        var attempt = Interlocked.Increment(ref runs);
                        return attempt < 3
                            ? Task.FromException(new InvalidOperationException("synthetic transient failure"))
                            : Task.CompletedTask;
                    })
                .WaitAsync(TimeSpan.FromSeconds(5));

            Check(result.Status == BackgroundJobStatus.Succeeded, $"expected Succeeded, got {result.Status}");
            Check(result.Attempts == 3, $"expected 3 attempts, got {result.Attempts}");
            Check(Volatile.Read(ref runs) == 3, $"expected 3 executions, got {Volatile.Read(ref runs)}");
            Check(
                backoffAttempts.SequenceEqual(new[] { 1, 2 }),
                $"injected backoff should see failed attempts [1,2]; saw [{string.Join(",", backoffAttempts)}]");

            var snapshot = queue.Snapshot();
            Check(snapshot.Completed == 1, $"expected 1 completed, got {snapshot.Completed}");
            Check(snapshot.Failed == 0, $"expected 0 failed, got {snapshot.Failed}");
        }
        finally
        {
            await StopAsync(queue);
        }
    }

    private static async Task QueueRetryExhaustionTests()
    {
        var queue = NewQueue(attempts: 3, drainMs: 500);
        try
        {
            var runs = 0;
            var result = await queue.EnqueueAsync(
                    "retry-fail",
                    "retry-fail",
                    _ =>
                    {
                        Interlocked.Increment(ref runs);
                        return Task.FromException(new InvalidOperationException("synthetic permanent failure"));
                    })
                .WaitAsync(TimeSpan.FromSeconds(5));

            Check(result.Status == BackgroundJobStatus.Failed, $"expected Failed, got {result.Status}");
            Check(result.Attempts == 3, $"expected 3 attempts, got {result.Attempts}");
            Check(Volatile.Read(ref runs) == 3, $"expected 3 executions, got {Volatile.Read(ref runs)}");

            var snapshot = queue.Snapshot();
            Check(snapshot.Completed == 0, $"expected 0 completed, got {snapshot.Completed}");
            Check(snapshot.Failed == 1, $"expected 1 failed, got {snapshot.Failed}");
        }
        finally
        {
            await StopAsync(queue);
        }
    }

    private static async Task QueueTimeoutTests()
    {
        var queue = NewQueue(attempts: 3, timeoutMs: 100, drainMs: 500);
        try
        {
            var result = await queue.EnqueueAsync(
                    "slow",
                    "slow",
                    ct => Task.Delay(Timeout.InfiniteTimeSpan, ct))
                .WaitAsync(TimeSpan.FromSeconds(5));

            Check(result.Status == BackgroundJobStatus.TimedOut, $"expected TimedOut, got {result.Status}");
            Check(result.Attempts == 1, $"timeout must not retry; attempts={result.Attempts}");

            // 协作式取消可能在超时判定后几毫秒内才真正结束：先等未知终态结清。
            await WaitUntilAsync(() => queue.Snapshot().Abandoned == 0, "cooperative timeout settles");
            var snapshot = queue.Snapshot();
            Check(snapshot.Completed == 0, $"expected 0 completed, got {snapshot.Completed}");
            Check(snapshot.Failed == 1, $"expected 1 failed (timeout), got {snapshot.Failed}");
            Check(snapshot.Abandoned == 0, $"cooperative timeout must not be abandoned, got {snapshot.Abandoned}");
            Check(snapshot.Outstanding == 0, $"cooperative timeout must settle, got {snapshot.Outstanding}");
        }
        finally
        {
            await StopAsync(queue);
        }
    }

    private static async Task QueueTimeoutRaceTests()
    {
        // R1：超时计时器可能先进入“正在通知”，此时显式 Cancel() 会立即返回；
        // 若在返回时 Dispose 链接源，真实的协作式工作就永远收不到取消。
        // 这需要足够多的独立尝试才能稳定复现（单次通过不代表没有竞态）。
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var queue = NewQueue(timeoutMs: 60, drainMs: 100);
            try
            {
                CancellationToken workToken = default;
                var result = await queue.EnqueueAsync(
                        "cooperative-race",
                        "cooperative-race",
                        ct =>
                        {
                            workToken = ct;
                            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
                        })
                    .WaitAsync(TimeSpan.FromSeconds(5));

                Check(result.Status == BackgroundJobStatus.TimedOut,
                    $"iteration {iteration}: expected TimedOut, got {result.Status}");
                await WaitUntilAsync(
                    () => queue.Snapshot().Abandoned == 0 && queue.Snapshot().Outstanding == 0,
                    $"iteration {iteration}: cooperative timeout settles");
                Check(workToken.IsCancellationRequested,
                    $"iteration {iteration}: real work must observe cancellation");
            }
            finally
            {
                await StopAsync(queue);
            }
        }
    }

    private static async Task QueueSynchronousDelegateTests()
    {
        // R2：委托的同步前缀也不能绕过预算；否则先睡 160ms 再返回已完成 Task 会被记成 Succeeded。
        var queue = NewQueue(timeoutMs: 25, drainMs: 100);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await queue.EnqueueAsync(
                    "sync-prefix",
                    "sync-prefix",
                    _ =>
                    {
                        Thread.Sleep(160);
                        return Task.CompletedTask;
                    })
                .WaitAsync(TimeSpan.FromSeconds(5));
            stopwatch.Stop();

            Check(result.Status == BackgroundJobStatus.TimedOut,
                $"expected TimedOut, got {result.Status}");
            Check(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                $"timeout budget must cover the delegate invocation, took {stopwatch.ElapsedMilliseconds}ms");
            Check(queue.Snapshot().Abandoned == 1,
                "unfinished synchronous work is reported as abandoned, not succeeded");
            await WaitUntilAsync(() => queue.Snapshot().Abandoned == 0, "synchronous prefix settles");
        }
        finally
        {
            await StopAsync(queue);
        }
    }

    private static async Task QueueTimeoutUncooperativeTests()
    {
        // 工作完全忽略取消令牌：超时必须真的限定等待，且迟到成功不能改写结果。
        var queue = NewQueue(capacity: 2, concurrency: 1, attempts: 3, timeoutMs: 25, drainMs: 500);
        try
        {
            var release = NewGate();
            var effects = 0;
            var result = await queue.EnqueueAsync(
                    "stubborn",
                    "stubborn",
                    async _ =>
                    {
                        await release.Task;
                        Interlocked.Increment(ref effects);
                    })
                .WaitAsync(TimeSpan.FromSeconds(5));

            Check(result.Status == BackgroundJobStatus.TimedOut, $"expected TimedOut, got {result.Status}");
            Check(result.Attempts == 1, $"timeout must not retry; attempts={result.Attempts}");

            var running = queue.Snapshot();
            Check(running.Abandoned == 1, $"uncooperative work must be abandoned, got {running.Abandoned}");
            Check(running.Outstanding >= 1, $"abandoned work must stay outstanding, got {running.Outstanding}");

            var duplicate = await queue.EnqueueAsync("dup", "stubborn", _ => Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Check(duplicate.Status == BackgroundJobStatus.Deduplicated,
                $"abandoned work must keep its dedup key until it ends, got {duplicate.Status}");

            // 槽位仍被占住：同队列的下一个作业不能抢跑（否则有界性就是假的）。
            var secondRuns = 0;
            var second = queue.EnqueueAsync(
                "next",
                "next",
                _ =>
                {
                    Interlocked.Increment(ref secondRuns);
                    return Task.CompletedTask;
                });
            await Task.Delay(60);
            Check(Volatile.Read(ref secondRuns) == 0, "timed-out work must keep its worker slot");

            release.TrySetResult();
            await WaitUntilAsync(() => queue.Snapshot().Abandoned == 0, "abandoned work settles");
            Check(Volatile.Read(ref effects) == 1, "late side effect is observed, but not rewritten as success");

            var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(5));
            Check(secondResult.Status == BackgroundJobStatus.Succeeded, $"next job should run, got {secondResult.Status}");
            Check(Volatile.Read(ref secondRuns) == 1, "next job should run exactly once");

            var settled = queue.Snapshot();
            Check(settled.Outstanding == 0, $"expected no outstanding work, got {settled.Outstanding}");
            Check(settled.Failed == 1, $"expected 1 failed (timeout), got {settled.Failed}");
        }
        finally
        {
            await StopAsync(queue);
        }
    }

    private static async Task QueueCancellationTests()
    {
        var queue = NewQueue(attempts: 3, timeoutMs: 2000, drainMs: 500);
        try
        {
            var runs = 0;
            var result = await queue.EnqueueAsync(
                    "cancel",
                    "cancel",
                    _ =>
                    {
                        Interlocked.Increment(ref runs);
                        return Task.FromException(new OperationCanceledException("synthetic cancellation"));
                    })
                .WaitAsync(TimeSpan.FromSeconds(5));

            Check(result.Status == BackgroundJobStatus.Canceled, $"expected Canceled, got {result.Status}");
            Check(result.Attempts == 1, $"cancellation must not retry; attempts={result.Attempts}");
            Check(Volatile.Read(ref runs) == 1, $"expected 1 execution, got {Volatile.Read(ref runs)}");

            var snapshot = queue.Snapshot();
            Check(snapshot.Failed == 1, $"expected 1 failed (canceled), got {snapshot.Failed}");
        }
        finally
        {
            await StopAsync(queue);
        }
    }

    private static async Task QueueStopDrainTests()
    {
        var queue = NewQueue(capacity: 4, concurrency: 1, attempts: 1, timeoutMs: 5000, drainMs: 2000);
        var first = queue.EnqueueAsync("one", "one", async _ => await Task.Delay(20));
        var second = queue.EnqueueAsync("two", "two", async _ => await Task.Delay(20));

        await StopAsync(queue);

        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));
        var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Check(firstResult.Status == BackgroundJobStatus.Succeeded, $"first should drain; got {firstResult.Status}");
        Check(secondResult.Status == BackgroundJobStatus.Succeeded, $"second should drain; got {secondResult.Status}");

        Check(!queue.TryEnqueue("late", "late", _ => Task.CompletedTask), "TryEnqueue after stop must fail");
        var lateResult = await queue.EnqueueAsync("late-2", "late-2", _ => Task.CompletedTask)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(lateResult.Status == BackgroundJobStatus.Rejected, $"expected Rejected after stop, got {lateResult.Status}");

        var snapshot = queue.Snapshot();
        Check(snapshot.Completed == 2, $"expected 2 completed, got {snapshot.Completed}");
        Check(snapshot.Rejected == 2, $"expected 2 rejected, got {snapshot.Rejected}");
        Check(snapshot.Queued == 0 && snapshot.Running == 0, "nothing should remain outstanding");
    }

    private static async Task QueueStopCancelTests()
    {
        var queue = NewQueue(capacity: 2, concurrency: 1, attempts: 1, timeoutMs: 10_000, drainMs: 150);
        try
        {
            var never = NewGate();
            var running = queue.EnqueueAsync("stuck", "stuck", _ => never.Task);
            await WaitUntilAsync(() => queue.Snapshot().Running == 1, "stuck job running");

            var queued = queue.EnqueueAsync("waiting", "waiting", _ => Task.CompletedTask);
            await WaitUntilAsync(() => queue.Snapshot().Queued == 1, "second job queued");

            var stopwatch = Stopwatch.StartNew();
            await StopAsync(queue);
            stopwatch.Stop();
            Check(
                stopwatch.Elapsed < TimeSpan.FromSeconds(3),
                $"StopAsync must not hang on jobs that ignore cancellation; took {stopwatch.ElapsedMilliseconds}ms");

            var runningResult = await running.WaitAsync(TimeSpan.FromSeconds(5));
            var queuedResult = await queued.WaitAsync(TimeSpan.FromSeconds(5));
            Check(
                runningResult.Status == BackgroundJobStatus.Abandoned,
                $"running job must be Abandoned (outcome unknown), got {runningResult.Status}");
            Check(runningResult.Attempts == 1, $"running job should report 1 attempt, got {runningResult.Attempts}");
            Check(
                queuedResult.Status == BackgroundJobStatus.Canceled,
                $"queued job should be Canceled, got {queuedResult.Status}");
            Check(queuedResult.Attempts == 0, $"queued job should report 0 attempts, got {queuedResult.Attempts}");

            var stopped = queue.Snapshot();
            Check(stopped.Failed == 1, $"expected 1 confirmed failure (queued canceled), got {stopped.Failed}");
            Check(stopped.Abandoned == 1, $"expected 1 abandoned running job, got {stopped.Abandoned}");
            Check(stopped.Outstanding == 1, $"abandoned work must stay outstanding, got {stopped.Outstanding}");

            // 工作真的结束之后，未知终态与去重占用才释放。
            never.TrySetResult();
            await WaitUntilAsync(() => queue.Snapshot().Abandoned == 0, "abandoned job settles");
            Check(queue.Snapshot().Outstanding == 0, "settled queue must have no outstanding work");
        }
        finally
        {
            await StopAsync(queue);
        }
    }

    private static async Task QueueStopUnconfirmedTests()
    {
        // 停机时工作忽略取消令牌：Stop 及时返回，但结果必须写 Abandoned（未知），
        // 而不是 Canceled；迟到副作用真实发生且被观察。
        var queue = NewQueue(capacity: 2, concurrency: 1, attempts: 1, timeoutMs: 10_000, drainMs: 50);
        try
        {
            var entered = NewGate();
            var release = NewGate();
            var effects = 0;
            var pending = queue.EnqueueAsync(
                "unconfirmed",
                "unconfirmed",
                async _ =>
                {
                    entered.TrySetResult();
                    await release.Task;
                    Interlocked.Increment(ref effects);
                });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var stopwatch = Stopwatch.StartNew();
            await StopAsync(queue);
            stopwatch.Stop();
            Check(stopwatch.Elapsed < TimeSpan.FromSeconds(3), "StopAsync must not hang");

            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(result.Status == BackgroundJobStatus.Abandoned,
                $"unconfirmed in-flight work must be Abandoned, got {result.Status}");
            var stopped = queue.Snapshot();
            Check(stopped.Abandoned == 1, $"expected 1 abandoned, got {stopped.Abandoned}");
            Check(stopped.Outstanding == 1, $"abandoned work must stay outstanding, got {stopped.Outstanding}");

            release.TrySetResult();
            await WaitUntilAsync(() => queue.Snapshot().Abandoned == 0, "late work settles after stop");
            Check(Volatile.Read(ref effects) == 1, "late side effect is real and observed, not rewritten as canceled");
            Check(queue.Snapshot().Outstanding == 0, "settled queue must have no outstanding work");
        }
        finally
        {
            await StopAsync(queue);
        }
    }

    private static async Task QueueIdempotentStopTests()
    {
        var queue = NewQueue(drainMs: 500);

        var first = queue.StopAsync(CancellationToken.None);
        var second = queue.StopAsync(CancellationToken.None);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        await queue.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = queue.Snapshot();
        Check(snapshot.Queued == 0, $"queued should be 0, got {snapshot.Queued}");
        Check(snapshot.Running == 0, $"running should be 0, got {snapshot.Running}");
        Check(snapshot.Completed == 0, $"completed should be 0, got {snapshot.Completed}");
        Check(snapshot.Failed == 0, $"failed should be 0, got {snapshot.Failed}");
        Check(snapshot.Rejected == 0, $"rejected should be 0, got {snapshot.Rejected}");
        Check(snapshot.Deduplicated == 0, $"deduplicated should be 0, got {snapshot.Deduplicated}");
        Check(snapshot.Abandoned == 0, $"abandoned should be 0, got {snapshot.Abandoned}");
    }

    // ───────────────────────── helpers ─────────────────────────

    private static BackgroundEvaluatorQueue NewQueue(
        int capacity = 8,
        int concurrency = 1,
        int attempts = 1,
        int timeoutMs = 2000,
        int drainMs = 1000)
        => new(
            new BackgroundEvaluatorOptions
            {
                Capacity = capacity,
                MaxConcurrency = concurrency,
                MaxAttempts = attempts,
                PerJobTimeout = TimeSpan.FromMilliseconds(timeoutMs),
                DrainTimeout = TimeSpan.FromMilliseconds(drainMs),
            },
            // 探针要确定性：重试之间不等待。
            _ => TimeSpan.Zero);

    private static TaskCompletionSource NewGate()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task StopAsync(BackgroundEvaluatorQueue queue)
        => queue.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException($"timed out waiting for: {what}");
            }

            await Task.Delay(5);
        }
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void CheckThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{message} (got {ex.GetType().Name} instead)");
        }

        throw new InvalidOperationException(message);
    }

    private sealed class FakeRegistry : IPluginRegistry
    {
        private readonly HashSet<string> _registered;
        private readonly HashSet<string> _enabled;

        public FakeRegistry(IEnumerable<string> registered, IEnumerable<string> enabled)
        {
            _registered = new HashSet<string>(registered, StringComparer.Ordinal);
            _enabled = new HashSet<string>(enabled, StringComparer.Ordinal);
        }

        public IReadOnlyList<PluginInfo> GetAll() => Array.Empty<PluginInfo>();

        public IBotPlugin? Find(string pluginId) => null;

        public bool IsEnabled(string pluginId)
            => _registered.Contains(pluginId) && _enabled.Contains(pluginId);
    }
}
