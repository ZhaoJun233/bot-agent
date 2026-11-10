using System.Collections.Concurrent;
using System.Reflection;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Services.OneBot;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Music;
using BotAgent.Services.Panel;
using BotAgent.Services.Voice;
using BotAgent.Services.Conversations;
using BotAgent.Services.Evaluation;
using BotAgent.Services.Net;
using BotAgent.Services.Ops;
using BotAgent.Services.Participation;
using BotAgent.Services.Permissions;
using BotAgent.Services.Platforms;
using BotAgent.Services.Poke;
using BotAgent.Services.Ports;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;
using BotAgent.Services.Stickers;

namespace BotAgent.ReplyPipelinePacingProbe;

internal sealed class Fixture : IAsyncDisposable
{
    public AppSettings Settings { get; } = new()
    {
        AiModeEnabled = true, ReplyPacingEnabled = true, AiDesire = 100,
        GroupInterjectionProbability = 100, GroupInterjectionCooldownSeconds = 30,
        GroupContinuationWaitSeconds = 3, PrivateContinuationWaitSeconds = 1.5,
        GroupCooldownSeconds = 0, PrivateCooldownSeconds = 0, MaxConcurrentReplies = 1,
        SplitReplies = true, SegmentDelayMs = 0, FilterActionNarration = false,
        EnableParticipationGating = false, EnableWebSearch = false,
        EnableStickers = false, EnableMusic = false, EnableQuestions = false,
    };
    public ManualClock Clock { get; } = new();
    public ControlledModel Model { get; } = new();
    public SyntheticSource Source { get; } = new();
    public ConversationRegistry Registry { get; }
    public ReplyPipeline Pipeline { get; }
    public ConcurrentQueue<string> Logs { get; } = new();
    public ControlledResearch Research { get; } = new();
    public int ResearchCalls => Research.Calls;
    private readonly StickerService _stickers;
    private readonly BackgroundEvaluatorQueue _background = new(new BackgroundEvaluatorOptions
    { MaxAttempts = 1, MaxConcurrency = 1, PerJobTimeout = Timeout.InfiniteTimeSpan, DrainTimeout = TimeSpan.Zero });
    private long _messageId = 10000;
    private bool _disposed;
    public Fixture(Action<AppSettings>? configure = null)
    {
        configure?.Invoke(Settings);
        typeof(ReplyPipeline).Assembly.GetType("BotAgent.Engine.Runtime.EngineClock", true)!
            .GetMethod("Initialize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [Clock]);
        var box = new FixtureSettings(Settings);
        Registry = new(EmptyProxy.Create<IConversationRepository>(), box, Source, _ => true, _ => { });
        var profiles = EmptyProxy.Create<IProfileRepository>();
        var own = EmptyProxy.Create<IOwnMessageRepository>();
        ((EmptyProxy)(object)own).Handler = (method, _) => method.Name == "get_MaxEntries" ? 100 : EmptyProxy.Default(method.ReturnType);
        var ledger = new OwnMessageLedger(own, Logs.Enqueue);
        var traces = new TurnTraceStore();
        var policy = new FixedPolicy();
        var plain = new PlainSender(box, Source, Registry, EmptyProxy.Create<IPanelNotifier>(), ledger, Logs.Enqueue, traces, platformPolicies: policy);
        var participation = new ParticipationUseCase(box, Logs.Enqueue);
        var approvals = new ApprovalUseCase(box,
            new ApprovalHooks(Logs.Enqueue, (_, _) => "synthetic", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask),
            new SessionPolicyLedger(), traces);
        var poke = new PokeUseCase(box, new PokeHooks(() => 10001, (_, _) => true,
            Registry.GetOrCreate, (_, _) => { }, (_, _) => { }, Logs.Enqueue, _ => { }));
        _stickers = new StickerService(EmptyProxy.Create<IStickerRepository>(), Model.Client, Source, box, Logs.Enqueue);
        Pipeline = new ReplyPipeline(box, Source, Model.Client, profiles, Registry,
            EmptyProxy.Create<IPanelNotifier>(), new WhitelistGate(box, policy), approvals, participation, poke,
            new VibeTracker(), new MemberRoleUseCase(Source, EmptyProxy.Create<IMemberRoleRepository>(), Logs.Enqueue),
            ledger, EmptyProxy.Create<IAgentCommandService>(), plain, _stickers, EmptyProxy.Create<IVoiceUseCase>(),
            EmptyProxy.Create<IMusicUseCase>(), Research, null,
            EmptyProxy.Create<IMoodRepository>(), new ReplyHooks(Logs.Enqueue, () => 10001, () => _disposed), traces,
            background: _background, platformPolicies: policy);
    }
    public async Task<BotConversation> Inbound(string text, bool direct = false, long target = 10001, bool group = true, long speaker = 10002)
    {
        var msg = new QqChatMessage(++_messageId, group, speaker, group ? target : 0, "synthetic", text, Clock.Now, direct);
        Pipeline.HandleInbound(msg);
        await Drain();
        return Registry.Find(group ? "group:" + target : "private:" + speaker)
            ?? throw new InvalidOperationException("Synthetic inbound was not registered");
    }
    public async Task Advance(int milliseconds)
    {
        Clock.Advance(milliseconds);
        await Drain();
    }
    public async Task<ModelCall> NextModel()
    {
        // Production pacing polls every 50ms; do not require a callback at an arbitrary
        // virtual instant. No call may start early, but one poll of dispatch jitter is valid.
        if (Model.History.Count == 0 || Clock.Pending > 0) await Advance(50);
        try { return await Model.Next(); }
        catch (TimeoutException ex)
        {
            var errors = Logs.Where(line => line.Contains("Exception", StringComparison.Ordinal))
                .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"[^\u0020-\u007e]", "?"));
            throw new InvalidOperationException($"model timeout: active={ActiveCount} paced={PacedCount} delays={Clock.Pending}; {string.Join("; ", errors)}", ex);
        }
    }
    public static async Task Drain()
    {
        // Scheduler barrier only; virtual time is advanced exclusively by ManualClock.
        for (var i = 0; i < 20; i++) await Task.Yield();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPool.QueueUserWorkItem(_ => done.TrySetResult());
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    public async Task Until(Func<bool> condition, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (timeout.IsCancellationRequested)
                throw new TimeoutException($"{label}: active={ActiveCount} paced={PacedCount} delays={Clock.Pending} models={Model.History.Count} sends={Source.Sends.Count}");
            await Drain();
        }
    }
    public int ActiveCount => Pipeline.InFlightReplies;
    public int PacedCount => Count("_pacedWorks");
    private int Count(string field)
    {
        var value = typeof(ReplyPipeline).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Pipeline)!;
        return (int)value.GetType().GetProperty("Count")!.GetValue(value)!;
    }
    public Task Idle() => Until(() => ActiveCount == 0 && PacedCount == 0, "pipeline idle");
    private sealed class FixtureSettings(AppSettings settings) : ISettingsBox { public AppSettings Current => settings; }
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        Pipeline.Shutdown();
        foreach (var call in Model.History) call.Reply(null);
        await Drain();
        await _background.StopAsync();
        _stickers.Dispose();
    }
    private sealed class FixedPolicy : IPlatformPolicyResolver
    {
        private static readonly EffectivePlatformPolicy Policy = new(new PlatformContext(PlatformId.QqPrivate),
            true, true, true, true, PlatformCapabilities.QqOneBot, new HashSet<string>(),
            new Dictionary<string, bool>(), Array.Empty<string>());
        public EffectivePlatformPolicy Resolve(PlatformContext context) => Policy;
        public EffectivePlatformPolicy ResolveForChannel(string? channel) => Policy;
        public bool IsChatEnabled(string? channel) => true;
    }
}
