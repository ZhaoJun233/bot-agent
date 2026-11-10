using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Net;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Model;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Profiles;
using BotAgent.Engine.Memory;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Ops;
using BotAgent.Services.Panel;
using BotAgent.Services.Stickers;

namespace BotAgent.ReviewRemediationProbe;

internal static class MemorySchedulerTests
{
    private const string Fragment = "M153_SCHEDULER_FRAGMENT";
    private const string Summary = "M153_SCHEDULER_SUMMARY";
    private static readonly MethodInfo RunMethod = typeof(BotScheduler).GetMethod("RunProfileSummarizationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo TimerField = typeof(BotScheduler).GetField("_summaryTimer", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo GateField = typeof(BotScheduler).GetField("_summaryGate", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public static async Task RunAsync(Action<bool, string> check)
    {
        // This positive control makes a zero-call result meaningful: the same nonempty
        // candidate and real OpenAiClient must reach the fake HTTP boundary when unwired.
        using (var f = new Fixture(null))
        {
            f.Scheduler.RebuildIfNeeded();
            check(TimerField.GetValue(f.Scheduler) is Timer, "unwired scheduler creates configured profile timer");
            f.Scheduler.Stop(); // Execute deterministically below; no timer sleeps or races.
            await f.RunAsync();
            check(f.Profiles.FindCalls == 1, "unwired scheduler reads nonempty candidate");
            check(f.Http.Calls == 1 && f.Http.SawFragment, "unwired real persona model receives synthetic candidate");
            check(f.Profiles.Applied == 1 && f.Profiles.AppliedText == Summary, "unwired scheduler applies actual model response");
            check(f.Scheduler.SummaryDoneCount == 1 && f.Scheduler.SummaryFailCount == 0, "unwired positive cycle finishes successfully");
        }

        foreach (var enabled in new[] { false, true })
        {
            using var f = new Fixture(enabled);
            var label = enabled ? "wired-enabled" : "wired-disabled";
            check(f.Memory!.Enabled == enabled && f.Box.Current.EnableProfileSummary, label + " retains enabled summary setting and exact core state");
            f.Scheduler.RebuildIfNeeded();
            check(TimerField.GetValue(f.Scheduler) is null, label + " creates no profile timer");
            await f.RunAsync();
            await f.RunAsync();
            check(f.Profiles.FindCalls == 0 && f.Profiles.OtherCalls == 0, label + " performs zero profile reads");
            check(f.Http.Calls == 0, label + " performs zero actual model HTTP requests");
            check(f.Profiles.Applied == 0, label + " performs zero summary writes");
            check(f.Scheduler.SummaryDoneCount == 0 && f.Scheduler.SummaryFailCount == 0, label + " returns normally without hidden failed work");
        }

        using (var f = new Fixture(null))
        {
            f.Profiles.OnFind = () => f.Box.Apply(s => s.EnableProfileSummary = false);
            await f.RunAsync();
            check(f.Profiles.FindCalls == 1 && f.Http.Calls == 0 && f.Profiles.Applied == 0, "setting revoked after candidate read blocks per-item model boundary");
        }
        using (var f = new Fixture(null))
        {
            var gate = (SemaphoreSlim)GateField.GetValue(f.Scheduler)!;
            await gate.WaitAsync();
            var pending = f.RunAsync();
            check(f.Profiles.FindCalls == 1 && !pending.IsCompleted, "gate test actually reaches blocked await after candidate read");
            f.Box.Apply(s => s.EnableProfileSummary = false);
            gate.Release();
            await pending;
            check(f.Http.Calls == 0 && f.Profiles.Applied == 0, "setting revoked while awaiting gate blocks model boundary");
        }
        using (var f = new Fixture(null))
        {
            f.Http.OnSend = () => f.Box.Apply(s => s.EnableProfileSummary = false);
            await f.RunAsync();
            check(f.Http.Calls == 1 && f.Http.SawFragment, "apply-race control actually completes the model request");
            check(f.Profiles.Applied == 0, "setting revoked during model await blocks summary persistence");
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly SettingsBox Box = new(new AppSettings
        {
            ApiKey = "sk-mock", Model = "synthetic-model", ModelBaseUrl = "https://example.com/v1",
            EnableProfileSummary = true, ProfileSummaryThreshold = 1, ProfileSummaryIntervalSeconds = 3600,
            ProfileSummaryMaxChars = 160, EnableStickers = false, IdleFallbackSeconds = 0
        });
        internal readonly ProfileSpy Profiles = new();
        internal readonly HttpSpy Http = new();
        internal readonly UnifiedMemory? Memory;
        internal readonly BotScheduler Scheduler;
        private readonly StickerService _stickers;

        internal Fixture(bool? memoryEnabled)
        {
            if (memoryEnabled is { } enabled)
            {
                Memory = new UnifiedMemory(new MemoryStore());
                if (enabled) Memory.SetEnabled(true);
            }
            var brain = new OpenAiClient(Box, Http, Http, new NoImages(), new NoChatTransport());
            // Idle/health/sticker work is explicitly disabled and never started. Only the
            // actual scheduler profile routine and real auxiliary model path are exercised.
            _stickers = new StickerService(null!, brain, null!, Box, _ => { });
            Scheduler = new BotScheduler(Box, null!, Profiles, brain, null!, _stickers, new PanelNotifier(),
                new BotSchedulerHooks(_ => { }, () => false), _ => true, Memory);
        }

        internal Task RunAsync() => (Task)RunMethod.Invoke(Scheduler, null)!;
        public void Dispose() { Scheduler.Stop(); _stickers.EnsureTimer(false); }
    }

    private sealed class ProfileSpy : IProfileRepository
    {
        internal int FindCalls, OtherCalls, Applied;
        internal string? AppliedText;
        internal Action? OnFind;
        public List<SummaryCandidate> FindSummarizable(int minNewMessages, int maxCandidates)
        {
            FindCalls++;
            OnFind?.Invoke();
            return [new("10002", "synthetic-member", "group:10004", 10004, [Fragment], "M153_EXISTING_PROFILE", 1, 1)];
        }
        public void ApplySummary(string uid, string scope, string text, long throughSeq, int foldedCount) { Applied++; AppliedText = text; }
        public void Append(string uid, string name, string text, DateTimeOffset time, string? groupName, long groupId = 0, long seq = 0) => OtherCalls++;
        public string GetProfileSummary(string uid, long scopeGroupId = 0, int limit = 12, long beforeSeq = long.MaxValue, long beforeUnix = long.MaxValue, bool allScopes = false) { OtherCalls++; return ""; }
        public void SetProfileOverride(string uid, string scope, string? overrideText) => OtherCalls++;
        public void SetEvidence(string uid, string scope, string evidenceJson) => OtherCalls++;
        public (string AutoSummary, string OverrideText, string EvidenceJson) GetDetailedSummary(string uid, string scope) { OtherCalls++; return ("", "", ""); }
    }

    private sealed class MemoryStore : IUnifiedMemoryPersistence
    {
        private UnifiedMemoryState _state = UnifiedMemoryState.Empty;
        public UnifiedMemoryState Load() => _state;
        public void Save(long expectedRevision, UnifiedMemoryState state)
        {
            if (_state.Revision != expectedRevision) throw new InvalidOperationException("synthetic revision mismatch");
            _state = state;
        }
    }

    private sealed class HttpSpy : IHttpFetcher
    {
        internal int Calls;
        internal bool SawFragment;
        internal Action? OnSend;
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            SawFragment |= request.Content is not null && (await request.Content.ReadAsStringAsync(ct)).Contains(Fragment);
            OnSend?.Invoke();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"" + Summary + "\"}}]}") };
        }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => SendAsync(request, ct);
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => UnexpectedGet();
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => UnexpectedGet();
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => UnexpectedGet();
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => UnexpectedGet();
        private Task<HttpResponseMessage> UnexpectedGet() { Calls++; throw new InvalidOperationException("synthetic unexpected get"); }
        public async Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
        { using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content }; return await SendAsync(request, ct); }
    }

    private sealed class NoImages : IImageDownloader
    {
        public Func<long, CancellationToken, Task<IReadOnlyList<string>>>? RefreshUrls { get; set; }
        public int CacheHits => 0;
        public int RefreshedCount => 0;
        public Task<(byte[] Data, string Mime, string Ext)?> DownloadBytesAsync(string url, CancellationToken ct, long? messageId = null)
            => throw new InvalidOperationException("synthetic unexpected image");
    }

    private sealed class NoChatTransport : IModelTransport
    {
        public Task<BuiltRequest> BuildAsync(IReadOnlyList<ChatMessage> window, string systemContent, IReadOnlyCollection<long> quotableIds,
            CancellationToken ct, BotAgent.Domain.Reply.SamplingProfile? sampling = null, string? promptCacheScope = null)
            => throw new InvalidOperationException("synthetic unexpected chat build");
        public Task<SendOutcome> SendAsync(JsonObject payload, int attachedImages, IReadOnlyList<long> attachedImageIds, CancellationToken ct)
            => throw new InvalidOperationException("synthetic unexpected chat send");
    }
}
