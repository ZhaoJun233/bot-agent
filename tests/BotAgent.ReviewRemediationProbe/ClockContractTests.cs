extern alias ModelModule;
extern alias StorageModule;

using System.Diagnostics;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Agent;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Services;
using BotAgent.Services.Local;
using BotAgent.Services.Ops;
using PlatformClock = BotAgent.Platforms.PlatformClock;
using ModelClock = ModelModule::BotAgent.Model.ModelClock;
using StorageClock = StorageModule::BotAgent.Storage.StorageClock;
using ModelBreaker = ModelModule::BotAgent.Services.Resilience.ProviderCircuitBreaker;
using ModelPrompt = ModelModule::BotAgent.Services.Model.PromptBuilder;
using StorageDatabase = StorageModule::BotAgent.Adapters.Persistence.AppDatabase;
using StorageQuota = StorageModule::BotAgent.Adapters.Persistence.TenantQuotaStore;
using StorageSessions = StorageModule::BotAgent.Adapters.Persistence.AgentSessionStore;

namespace BotAgent.ReviewRemediationProbe;

internal static class ClockContractTests
{
    // Children exit before any database, importer, host, or file logger is started.
    public static Task<int?> TryRunChildAsync(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("--clock-uninitialized" or "--clock-binding-race"))
            return Task.FromResult<int?>(null);
        var passed = 0;
        void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException(label);
            passed++;
        }
        try
        {
            if (args[0] == "--clock-uninitialized")
            {
                foreach (var module in Modules())
                {
                    Expect<ArgumentNullException>(() => module.Initialize(null!), Check, module.Name + " null before binding");
                    ExpectCode(() => _ = module.Current(), "clock_not_initialized", Check, module.Name + " uninitialized read");
                }
                ExpectCode(() => new LocalChannelSource().SendTextAsync(false, Channels.LocalTarget(10001), "synthetic").GetAwaiter().GetResult(),
                    "clock_not_initialized", Check, "platform consumer has no system fallback");
                ExpectCode(() => new ModelBreaker("synthetic").RecordHardFailure(),
                    "clock_not_initialized", Check, "model consumer has no system fallback");
                ExpectCode(() => new StorageQuota().GetQuota("synthetic"),
                    "clock_not_initialized", Check, "storage rejects before database access");
                Check(!StorageDatabase.IsReady, "uninitialized storage consumer does not initialize database");
            }
            else
            {
                foreach (var module in Modules())
                {
                    var first = new ProbeClock();
                    var second = new ProbeClock();
                    var successes = 0;
                    var rejected = 0;
                    Parallel.For(0, 64, i =>
                    {
                        try { module.Initialize(i % 2 == 0 ? first : second); Interlocked.Increment(ref successes); }
                        catch (InvalidOperationException ex) when (ex.Message == "clock_already_initialized")
                        { Interlocked.Increment(ref rejected); }
                    });
                    var winner = module.Current();
                    Check(ReferenceEquals(winner, first) || ReferenceEquals(winner, second), module.Name + " whole-reference winner");
                    Check(successes == 32 && rejected == 32, module.Name + " concurrent initialization has one identity");
                    Expect<ArgumentNullException>(() => module.Initialize(null!), Check, module.Name + " null after binding");
                    Check(ReferenceEquals(winner, module.Current()), module.Name + " rejection preserves source");
                }
            }
            Console.WriteLine($"C1_CHILD {args[0]} passed={passed} failed=0");
            return Task.FromResult<int?>(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"C1_CHILD {args[0]} failed=1 type={ex.GetType().Name}");
            return Task.FromResult<int?>(1);
        }
    }

    public static async Task PropagationAsync(Action<bool, string> check)
    {
        await RunChildAsync("--clock-uninitialized", check);
        await RunChildAsync("--clock-binding-race", check);
        ModelClock.Initialize(ClockBindings.Source);
        StorageClock.Initialize(ClockBindings.Source);
        foreach (var module in Modules())
        {
            module.Initialize(ClockBindings.Source);
            check(ReferenceEquals(module.Current(), ClockBindings.Source), module.Name + " shares stable host source");
            Expect<ArgumentNullException>(() => module.Initialize(null!), check, module.Name + " rejects null");
            ExpectCode(() => module.Initialize(new ProbeClock()), "clock_already_initialized", check, module.Name + " rejects rebinding");
            check(ReferenceEquals(module.Current(), ClockBindings.Source), module.Name + " failed initialization preserves source");
        }

        var original = Clock.Current;
        var fake = new ProbeClock();
        Clock.Use(fake);
        try
        {
            var source = new LocalChannelSource();
            await source.SendTextAsync(false, Channels.LocalTarget(10001), "synthetic");
            check(source.Outbox.Single().SentAt == fake.Now, "host fake controls actual platform outbox time");
            check(source.Inject(false, Channels.LocalTarget(10001), "synthetic", "synthetic").Time == fake.Now,
                "actual platform inbound reads the same fake");
            check(new BotConversation { SourceKey = "local:10001" }.LastTime == fake.Now, "host default timestamp follows fake");
            check(new ChatMessage { Role = MessageRole.Peer, Text = "synthetic" }.Timestamp == default,
                "domain message default remains clock-free");
            check(new AgentSession { Id = "synthetic" }.CreatedAt == default, "domain session default remains clock-free");

            foreach (var module in Modules())
                await VerifyClockAsync(module.Name, module.Current(), fake, check);

            var breaker = new ModelBreaker("synthetic", failureThreshold: 1, cooldown: TimeSpan.FromSeconds(30));
            breaker.RecordHardFailure();
            check(!breaker.TryEnter(out _), "actual model breaker starts in cooldown");
            fake.Now = fake.Now.AddSeconds(29);
            check(!breaker.TryEnter(out _), "actual model breaker rejects before boundary");
            fake.Now = fake.Now.AddSeconds(1);
            check(breaker.TryEnter(out _), "actual model breaker opens exactly at boundary");
            var localOnly = new ModelBreaker("local-only", failureThreshold: 1, clock: () => new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero));
            check(localOnly.RecordHardFailure().CooldownUntil == new DateTimeOffset(2040, 1, 1, 0, 2, 0, TimeSpan.Zero),
                "explicit local model clock remains local");
            var request = new ModelPrompt.PromptRequest(
                SystemPrompt: "synthetic", BotIdentity: null, Persona: null, AiDesire: 0,
                Window: Array.Empty<ChatMessage>(), QuotableIds: Array.Empty<long>(), ProfilesText: null,
                Stickers: null, PokeContext: false, Proactive: false, MoodText: null, MusicText: null,
                LinkText: null, RecallText: null, GroupRolesText: null, VibeHint: null, SearchText: null,
                SuitabilityThreshold: 10, EnableListen: false, EnableVoice: false, VoiceMaxChars: 30,
                VoiceEagerness: 50, EnableWebSearch: false, EnableAsk: false, EnableToolRequest: false);
            check(ModelPrompt.Build(request).Contains("2030-01-02 12:00"), "actual model prompt uses host time");

            var root = Environment.GetEnvironmentVariable("BOTAGENT_DATA_DIR")
                ?? throw new InvalidOperationException("synthetic_root_missing");
            var sessions = new StorageSessions(Path.Combine(root, "c1-synthetic-sessions.json"), _ => { });
            var session = sessions.EnsureCurrent("synthetic:clock", "server");
            check(session.CreatedAt == fake.Now && session.UpdatedAt == fake.Now, "actual storage stamps new session");
            check(sessions.List("synthetic:clock").Single().Id == session.Id, "storage session remains retrievable");
            fake.Now = new DateTimeOffset(2030, 1, 2, 7, 59, 30, TimeSpan.FromHours(8));
            StorageDatabase.Initialize();
            var quota = new StorageQuota();
            quota.RecordUsage("synthetic-clock", 5, 7);
            check(quota.GetQuota("synthetic-clock").ResetDate == "2030-01-01", "actual storage day uses UTC not local date");
            check(quota.GetQuota("synthetic-clock").UsedTotalTokens == 12, "actual synthetic quota stores usage");
            fake.Now = fake.Now.AddSeconds(30);
            check(quota.GetQuota("synthetic-clock").ResetDate == "2030-01-02"
                && quota.GetQuota("synthetic-clock").UsedTotalTokens == 0, "actual quota resets exactly at UTC boundary");

            var emitted = 0;
            var throttled = new ThrottledLog(_ => emitted++);
            throttled.Write("synthetic-clock", "synthetic", 1);
            fake.Now = fake.Now.AddDays(-1);
            fake.Ticks = 1999;
            throttled.Write("synthetic-clock", "synthetic", 1);
            check(emitted == 1, "wall-clock rollback does not release monotonic throttle");
            fake.Ticks = 2000;
            throttled.Write("synthetic-clock", "synthetic", 1);
            check(emitted == 2, "monotonic throttle releases at exact boundary");

            var pending = ClockBindings.Source.Delay(123);
            var replacement = new ProbeClock { Now = new DateTimeOffset(2041, 2, 3, 4, 5, 0, TimeSpan.FromHours(8)), Ticks = 9000 };
            Clock.Use(replacement);
            check(!pending.IsCompleted, "replacing source does not complete in-flight old delay");
            fake.ReleaseDelays();
            await pending;
            foreach (var module in Modules())
            {
                check(module.Current().Now == replacement.Now && module.Current().TickCount == 9000,
                    module.Name + " existing binding observes replacement");
                await VerifyClockAsync(module.Name + " replacement", module.Current(), replacement, check);
            }
            check(source.Inject(false, Channels.LocalTarget(10001), "synthetic", "synthetic").Time == replacement.Now,
                "existing platform consumer observes replacement");

            var left = new ProbeClock();
            var right = new ProbeClock { Now = replacement.Now };
            Parallel.For(0, 2000, i =>
            {
                if (i % 3 == 0) Clock.Use(i % 2 == 0 ? left : right);
                else foreach (var module in Modules())
                {
                    var value = module.Current().Now;
                    if (value != left.Now && value != right.Now)
                        throw new InvalidOperationException("atomic_clock_reference");
                }
            });
            check(true, "concurrent host replacement exposes complete references only");
            Expect<ArgumentNullException>(() => Clock.Use(null!), check, "host rejects null");
            Clock.UseSystemClock();
            check(Clock.Current is SystemClock, "system clock restoration uses existing adapter");
            foreach (var module in Modules())
                check((module.Current().UtcNow - Clock.UtcNow).Duration() < TimeSpan.FromSeconds(1),
                    module.Name + " observes system clock restoration");
        }
        finally { Clock.Use(original); }
        check(ReferenceEquals(Clock.Current, original), "finally restores exact original host clock");
    }

    private static (string Name, Func<IClock> Current, Action<IClock> Initialize)[] Modules() =>
    [
        ("Platforms", () => PlatformClock.Current, PlatformClock.Initialize),
        ("Model", () => ModelClock.Current, ModelClock.Initialize),
        ("Storage", () => StorageClock.Current, StorageClock.Initialize)
    ];

    private static async Task VerifyClockAsync(string name, IClock source, ProbeClock fake, Action<bool, string> check)
    {
        check(source.Now == fake.Now && source.Now.Offset == TimeSpan.FromHours(8), name + " preserves local offset");
        check(source.UtcNow == fake.UtcNow && source.UtcNow.Offset == TimeSpan.Zero, name + " preserves UTC");
        check(source.LocalDateTime == fake.LocalDateTime, name + " forwards DateTime");
        check(source.TickCount == fake.Ticks, name + " forwards independent monotonic milliseconds");
        using var cts = new CancellationTokenSource();
        var first = source.Delay(TimeSpan.FromMilliseconds(17), cts.Token);
        check(!first.IsCompleted && fake.LastDelay == TimeSpan.FromMilliseconds(17) && fake.LastToken == cts.Token,
            name + " forwards TimeSpan delay and token");
        cts.Cancel();
        await ExpectCanceledAsync(first, check, name + " cancels pending TimeSpan delay");
        using var secondCts = new CancellationTokenSource();
        var second = source.Delay(23, secondCts.Token);
        check(!second.IsCompleted && fake.LastDelay == TimeSpan.FromMilliseconds(23) && fake.LastToken == secondCts.Token,
            name + " forwards integer delay and token");
        secondCts.Cancel();
        await ExpectCanceledAsync(second, check, name + " cancels pending integer delay");
        await ExpectCanceledAsync(source.Delay(0, cts.Token), check, name + " preserves already-canceled token");
        Expect<ArgumentOutOfRangeException>(() => source.Delay(-2), check, name + " preserves invalid integer delay");
        Expect<ArgumentOutOfRangeException>(() => source.Delay(TimeSpan.FromMilliseconds(-2)), check, name + " preserves invalid TimeSpan delay");
    }

    private static async Task RunChildAsync(string mode, Action<bool, string> check)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(ClockContractTests).Assembly.Location);
        start.ArgumentList.Add(mode);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("clock_child_start");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
        var text = await output;
        check(child.ExitCode == 0 && text.Contains("failed=0") && string.IsNullOrWhiteSpace(await error),
            "isolated child " + mode + " passes");
        Console.Write(text); // Child emits only fixed test labels, counts and exception type.
    }

    private static void Expect<T>(Action action, Action<bool, string> check, string label) where T : Exception
    {
        try { action(); }
        catch (T) { check(true, label); return; }
        check(false, label);
    }

    private static void ExpectCode(Action action, string code, Action<bool, string> check, string label)
    {
        try { action(); }
        catch (InvalidOperationException ex) when (ex.Message == code) { check(true, label); return; }
        check(false, label);
    }

    private static async Task ExpectCanceledAsync(Task task, Action<bool, string> check, string label)
    {
        try { await task; }
        catch (OperationCanceledException) { check(true, label); return; }
        check(false, label);
    }

    private sealed class ProbeClock : IClock
    {
        private readonly List<TaskCompletionSource> _delays = new();
        public DateTimeOffset Now { get; set; } = new(2030, 1, 2, 12, 0, 0, TimeSpan.FromHours(8));
        public DateTimeOffset UtcNow => Now.ToUniversalTime();
        public DateTime LocalDateTime => Now.DateTime;
        public long Ticks { get; set; } = 1000;
        public long TickCount => Ticks;
        public TimeSpan LastDelay { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Task Delay(TimeSpan delay, CancellationToken ct = default)
        {
            if (delay.TotalMilliseconds < -1 || delay.TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(delay));
            return Wait(delay, ct);
        }
        public Task Delay(int millisecondsDelay, CancellationToken ct = default)
        {
            if (millisecondsDelay < -1) throw new ArgumentOutOfRangeException(nameof(millisecondsDelay));
            return Wait(TimeSpan.FromMilliseconds(millisecondsDelay), ct);
        }
        private Task Wait(TimeSpan delay, CancellationToken ct)
        {
            LastDelay = delay;
            LastToken = ct;
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
            if (delay == TimeSpan.Zero) return Task.CompletedTask;
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _delays.Add(pending);
            var registration = ct.Register(() => pending.TrySetCanceled(ct));
            _ = pending.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return pending.Task;
        }
        public void ReleaseDelays()
        {
            foreach (var delay in _delays) delay.TrySetResult();
            _delays.Clear();
        }
    }
}
