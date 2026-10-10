using System.Collections.Concurrent;
using System.Reflection;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Jargon;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Services;
using BotAgent.Services.Conversations;
using BotAgent.Services.Jargon;
using BotAgent.Services.Ops;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;
using BotAgent.Services.Voice;
using Microsoft.Data.Sqlite;

namespace BotAgent.ReviewRemediationProbe;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main(string[] args)
    {
        if (await ClockContractTests.TryRunChildAsync(args) is { } childExit) return childExit;
        if (await DatabaseContractTests.TryRunChildAsync(args) is { } databaseChildExit) return databaseChildExit;
        if (StorageMigrationTests.TryRunChild(args) is { } storageChildExit) return storageChildExit;
        ClockBindings.InitializePlatforms();
        var root = Path.Combine(Path.GetTempPath(), "botagent-review-remediation-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        try
        {
            AppDatabase.Initialize();
            await RunAsync("shared database", () => DatabaseContractTests.SharedAsync(Check));
            await RunAsync("Storage episode adoption", () => StorageMigrationTests.EpisodeAsync(Check));
            await RunAsync("Storage bound import root", () => StorageMigrationTests.BoundRootAsync(Check));
            await RunAsync("Storage binding and typed import", () => StorageMigrationTests.BindingAndImportAsync(Check));
            await RunAsync("nested database write", () => DatabaseContractTests.NestedWriteAsync(Check));
            await RunAsync("future database schema", () => DatabaseContractTests.FutureSchemaAsync(Check));
            await RunAsync("database backup path", () => DatabaseContractTests.BackupPathAsync(Check));
            await RunAsync("database atomicity", () => DatabaseContractTests.AtomicityAsync(Check));
            await RunAsync("database restore", () => DatabaseContractTests.RestoreAsync(Check));
            await RunAsync("clock propagation", () => ClockContractTests.PropagationAsync(Check));
            await RunAsync("episode IDs", EpisodeIdsAsync);
            await RunAsync("prompt version concurrency", PromptVersionsAsync);
            await RunAsync("jargon phrase limits", JargonPhraseLimitsAsync);
            await RunAsync("jargon scope and cooldown limits", JargonScopeLimitsAsync);
            await RunAsync("jargon cooldown boundary", JargonCooldownAsync);
            await RunAsync("health report per-run results", HealthReportAsync);
            Console.WriteLine($"Passed {_passed}; failed {_failed}");
            return _failed == 0 ? 0 : 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try { await test(); }
        catch (Exception ex)
        {
            _failed++;
            Console.Error.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _passed++;
        Console.WriteLine("PASS " + message);
    }

    private static async Task EpisodeIdsAsync()
    {
        var store = new EpisodeStore();
        var ids = new List<long>();
        for (var i = 0; i < 8; i++)
        {
            // No pooled connection can supply an unrelated last_insert_rowid.
            SqliteConnection.ClearAllPools();
            var id = await store.InsertAsync(new EpisodeEntry
            {
                Scope = "synthetic:episodes", Title = "episode-" + i, Summary = "synthetic summary"
            });
            var actual = AppDatabase.Scalar<long>("SELECT id FROM episodes WHERE scope = $s AND title = $t",
                ("$s", "synthetic:episodes"), ("$t", "episode-" + i));
            Check(id > 0 && id == actual, "insert returns its own episode ID " + i);
            ids.Add(id);
        }
        Check(ids.Distinct().Count() == ids.Count, "episode insertion IDs are distinct");
        await store.DeleteAsync(ids[0]);
        Check((await store.ListRecentByScopeAsync("synthetic:episodes")).Count == 7,
            "returned episode ID deletes the intended row");
    }

    private static async Task PromptVersionsAsync()
    {
        var store = new PromptTemplateStore();
        Check(await store.SaveVersionAsync("synthetic-prompt", "initial") == "v1", "first version remains v1");
        const int writers = 12;
        using var ready = new CountdownEvent(writers);
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, writers).Select(i => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            start.Wait();
            return store.SaveVersionAsync("synthetic-prompt", "content-" + i).GetAwaiter().GetResult();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var allReady = ready.Wait(TimeSpan.FromSeconds(10));
        start.Set();
        var versions = await Task.WhenAll(tasks);
        Check(allReady && versions.Distinct().Count() == writers, "concurrent saves allocate unique versions");
        var saved = await store.ListVersionsAsync("synthetic-prompt");
        Check(saved.Count == writers + 1 && saved.Count(v => v.IsActive) == 1,
            "concurrent saves preserve all snapshots and exactly one active version");
        Check(saved.Select(v => v.VersionId).ToHashSet().SetEquals(Enumerable.Range(1, writers + 1).Select(i => "v" + i)),
            "concurrent versions remain sequential for the same key");
        Check(await store.SaveVersionAsync("other-prompt", "other") == "v1", "version allocation is isolated by key");
        Check(await store.ActivateVersionAsync("synthetic-prompt", "v1") &&
            (await store.GetActiveVersionAsync("synthetic-prompt"))?.Content == "initial", "historical version activation still works");
    }

    private static Task JargonPhraseLimitsAsync()
    {
        var service = NewJargonService();
        var batch = string.Join(" ", Enumerable.Range(0, 300).Select(i => "\"batch" + i + "\""));
        service.ObserveMessage("synthetic:batch", batch);
        Check(Counters(service)["synthetic:batch"].Count <= 200, "one large candidate batch respects phrase cap");
        for (var i = 0; i < 230; i++)
        {
            service.ObserveMessage("synthetic:frequent", "\"term" + i + "\"");
            service.ObserveMessage("synthetic:frequent", "\"term" + i + "\"");
        }
        Check(Counters(service)["synthetic:frequent"].Count <= 200, "high-frequency candidates also respect phrase cap");
        Parallel.For(0, 400, i => service.ObserveMessage("synthetic:parallel", "\"parallel" + i + "\""));
        Check(Counters(service)["synthetic:parallel"].Count <= 200, "parallel observations respect phrase cap");
        return Task.CompletedTask;
    }

    private static async Task JargonScopeLimitsAsync()
    {
        var service = NewJargonService();
        for (var i = 0; i < 500; i++)
        {
            var scope = "synthetic:scope-" + i;
            service.ObserveMessage(scope, "\"candidate\"");
            await service.DiscoverAndPersistAsync(scope);
        }
        var before = Counters(service).Keys.ToHashSet();
        service.ObserveMessage("synthetic:replacement", "\"candidate\"");
        Check(Counters(service).Count <= 500, "inserting the 501st scope keeps a real 500-scope cap");
        var evicted = before.Except(Counters(service).Keys).Single();
        Check(!Cooldowns(service).ContainsKey(evicted), "scope eviction removes its cooldown record");
        service.ObserveMessage(evicted, "\"replacement\"");
        service.ObserveMessage(evicted, "\"replacement\"");
        Check(await service.DiscoverAndPersistAsync(evicted) == 1, "re-entering evicted scope does not inherit stale cooldown");
        Parallel.For(0, 800, i => service.ObserveMessage("synthetic:new-" + i, "\"candidate\""));
        Check(Counters(service).Count <= 500 && Cooldowns(service).Keys.All(Counters(service).ContainsKey),
            "parallel scope churn bounds counters and associated cooldown records");
    }

    private static async Task JargonCooldownAsync()
    {
        var original = Clock.Current;
        var clock = new FakeClock();
        Clock.Use(clock);
        try
        {
            var service = NewJargonService();
            service.ObserveMessage("synthetic:cooldown", "\"first\"");
            service.ObserveMessage("synthetic:cooldown", "\"first\"");
            Check(await service.DiscoverAndPersistAsync("synthetic:cooldown") == 1,
                "eligible jargon candidate is initially discovered");
            service.ObserveMessage("synthetic:cooldown", "\"second\"");
            service.ObserveMessage("synthetic:cooldown", "\"second\"");
            Check(await service.DiscoverAndPersistAsync("synthetic:cooldown") == 0,
                "tracked scope still enforces immediate cooldown");
            clock.Now = clock.Now.AddMinutes(15).AddSeconds(-1);
            Check(await service.DiscoverAndPersistAsync("synthetic:cooldown") == 0,
                "tracked scope remains in cooldown before 15 minutes");
            clock.Now = clock.Now.AddSeconds(1);
            Check(await service.DiscoverAndPersistAsync("synthetic:cooldown") == 1,
                "tracked scope resumes discovery exactly at 15 minutes");
        }
        finally { Clock.Use(original); }
    }

    private static JargonService NewJargonService() => new(new FakeJargonRepository(), new SettingsBox(new AppSettings()));

    // The bounded buffers have no public diagnostic API; inspect only synthetic in-memory counts.
    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static ConcurrentDictionary<string, ConcurrentDictionary<string, int>> Counters(JargonService service)
        => Field<ConcurrentDictionary<string, ConcurrentDictionary<string, int>>>(service, "_phraseCounters");
    private static ConcurrentDictionary<string, DateTimeOffset> Cooldowns(JargonService service)
        => Field<ConcurrentDictionary<string, DateTimeOffset>>(service, "_lastExtractTime");

    private static async Task HealthReportAsync()
    {
        var box = new SettingsBox(new AppSettings
        {
            HealthReportTargets = "10001", PrivateChatEnabled = true, OfficialEnabled = true, OfficialChatEnabled = true,
            EnableVoice = false, ModelBaseUrl = ""
        });
        var source = new FakeSource();
        var registry = new ConversationRegistry(null!, box, source, _ => true, _ => { });
        // Only the real report's idle counters are exercised, never reply/model/tool operations.
        var reply = new ReplyPipeline(box, source, null!, null!, registry, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            new ReplyHooks(_ => { }, () => 10001, () => false), new TurnTraceStore());
        var scheduler = new BotScheduler(box, source, null!, null!, reply, null!, null!, default);
        using var report = new HealthReportService(box, registry, reply, new BotIdentity(), scheduler,
            new VoiceUseCase(null, box, _ => { }), source, null!, new FakeHostFacts());
        Check((await report.SendNowAsync("synthetic")).Ok && report.SentCount == 1,
            "first health report succeeds and increments lifetime count");
        box.Apply(s => s.PrivateChatEnabled = false);
        var skipped = await report.SendNowAsync("synthetic");
        Check(!skipped.Ok && skipped.Error is not null && report.LastError == skipped.Error && report.SentCount == 1 && source.Calls == 1,
            "all-skipped run reports no send even after earlier success");
        box.Apply(s => { s.HealthReportTargets = "10001," + Channels.AliasBase; s.OfficialChatEnabled = true; });
        Check((await report.SendNowAsync("synthetic")).Ok && report.SentCount == 2 && report.LastError is null,
            "mixed skipped/successful targets count current successful sends");
        box.Apply(s => { s.PrivateChatEnabled = true; s.OfficialChatEnabled = false; });
        source.Success = false;
        Check(!(await report.SendNowAsync("synthetic")).Ok && report.SentCount == 2 && report.LastError is not null,
            "protocol failure remains failure without incrementing lifetime count");
        box.Apply(s => s.HealthReportTargets = "");
        Check(!(await report.SendNowAsync("synthetic")).Ok && source.Calls == 3,
            "empty recipient list never calls the source");
        box.Apply(s =>
        {
            s.PlatformSwitchSchemaVersion = 1;
            s.HealthReportTargets = "10001";
            s.PrivateChatEnabled = true;
            s.PlatformPolicies.Add(new BotAgent.Domain.Platforms.PlatformPolicySettings
            {
                PlatformId = BotAgent.Domain.Platforms.PlatformId.QqPrivate,
                AccountScope = BotAgent.Domain.Platforms.AccountScope.Legacy, Enabled = false, ChatEnabled = true,
            });
        });
        var platformDisabled = await report.SendNowAsync("synthetic");
        Check(!platformDisabled.Ok && source.Calls == 3 && platformDisabled.Error?.Contains("平台已停用") == true,
            "canonical platform disable blocks report despite enabled legacy chat switch");
        box.Apply(s => { s.PlatformPolicies[0].Enabled = true; s.PlatformPolicies[0].ChatEnabled = false; });
        var chatMuted = await report.SendNowAsync("synthetic");
        Check(!chatMuted.Ok && source.Calls == 3 && chatMuted.Error?.Contains("聊天已静音") == true,
            "canonical chat mute blocks report and names the distinct switch");
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Now.ToUniversalTime();
        public DateTime LocalDateTime => Now.DateTime;
        public long TickCount => Now.ToUnixTimeMilliseconds();
        public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.CompletedTask;
        public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeHostFacts : IHostFacts
    {
        public long? MemoryLimitBytes() => null;
        public double? LoadAverage() => null;
    }

    private sealed class FakeJargonRepository : IJargonRepository
    {
        public Task<JargonEntry?> GetAsync(string scope, string phrase) => Task.FromResult<JargonEntry?>(null);
        public Task<IReadOnlyList<JargonEntry>> ListByScopeAsync(string scope, JargonStatus? status = null)
            => Task.FromResult<IReadOnlyList<JargonEntry>>(Array.Empty<JargonEntry>());
        public Task<IReadOnlyList<JargonEntry>> ListConfirmedForPromptAsync(string scope, int limit = 20)
            => Task.FromResult<IReadOnlyList<JargonEntry>>(Array.Empty<JargonEntry>());
        public Task UpsertAsync(JargonEntry entry) => Task.CompletedTask;
        public Task SetStatusAsync(long id, JargonStatus status) => Task.CompletedTask;
        public Task DeleteAsync(long id) => Task.CompletedTask;
    }

    private sealed class FakeSource : IQqChatSource
    {
        public int Calls { get; private set; }
        public bool Success { get; set; } = true;
        public bool IsConnected => true;
        public event Action<QqChatMessage>? MessageReceived { add { } remove { } }
        public event Action<QqPokeEvent>? Poked { add { } remove { } }
        public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
        public event Action<bool>? ConnectionChanged { add { } remove { } }
        public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default,
            long? replyToMessageId = null, bool directAddress = false)
        { Calls++; return Task.FromResult(new SendResult(Success)); }
        public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
            => Task.FromResult<(string?, long)>((null, 0));
        public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
            => Task.FromResult(false);
        public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
