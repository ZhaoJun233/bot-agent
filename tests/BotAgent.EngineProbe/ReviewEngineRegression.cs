using System.Collections.Concurrent;
using System.Reflection;
using BotAgent.Engine.Runtime;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Plugins;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Stickers;
using BotAgent.Services;
using BotAgent.Services.Conversations;
using BotAgent.Services.Net;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Panel;
using BotAgent.Services.Participation;
using BotAgent.Services.Permissions;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;
using BotAgent.Services.Platforms;

namespace BotAgent.EngineProbe;

// Parent wiring: await RunAsync("Review engine B03/B04/B16/B22/B25", ReviewEngineRegression.Run);
// All fixtures are synthetic and memory-only. No model, network, database or filesystem IO.
internal static partial class ReviewEngineRegression
{
    internal static async Task Run()
    {
        var previousClock = EngineClock.Current;
        EngineClock.Initialize(new FixedClock());
        try
        {
            await InlinePlatformBoundary();
            await ModelBoundaryAndUsage();
            await MediaAdvertisementBoundary();
            await PlainSendCommitBoundary();
            await QueueMembershipBoundary();
            await QueuedWebCallAliasBoundary();
        }
        finally { EngineClock.Initialize(previousClock); }
    }

    private static readonly ChatCapabilitySet Caps = ResearchToolFixture.Capabilities(true, true, true, true, true, null);
    private static EffectivePlatformPolicy Policy(bool web = true) => new(
        new PlatformContext(PlatformId.QqPrivate, AccountScope.Legacy), true, true, true, true,
        PlatformCapabilities.QqOneBot, new HashSet<string>(),
        new Dictionary<string, bool> { ["websearch"] = web }, Array.Empty<string>());
    private static BotConversation Conversation(string key = "group:10001") => new() { SourceKey = key };
    private static TurnInputs Turn(AppSettings settings, EffectivePlatformPolicy policy) => new(
        DateTime.UnixEpoch, settings, Caps, policy, Array.Empty<ChatMessage>(), new(), 0, new(), 0,
        null, null, "neutral", false, null, null, null, null, null);
    private static void Ensure(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException(reason);
    }
    private static async Task Cancelled(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("expected cancellation");
    }
    private static async Task Faulted(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException ex) when (ex.Message == "synthetic fault") { return; }
        throw new InvalidOperationException("expected synthetic fault");
    }

    private static async Task InlinePlatformBoundary()
    {
        var box = new Box(new AppSettings { EnableWebSearch = true, MaxAgentSteps = 2 });
        var research = new Research();
        var catalog = ResearchToolFixture.Create(research);
        var caps = ResearchToolFixture.Capabilities(true, true, true, true, true, null, catalog: catalog);
        var policy = new Policies(Policy(false));
        var approvals = new ApprovalUseCase(box,
            new ApprovalHooks(_ => { }, (text, _) => text, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask),
            new SessionPolicyLedger(), new TurnTraceStore(), catalog: () => catalog);
        var inline = new InlineTurnTools(research, approvals, new ParticipationUseCase(box, _ => { }), _ => { },
            null, policy, () => false);
        foreach (var result in new[] { new CompletionResult(null, null, null, Search: "synthetic query"),
            new CompletionResult(null, null, null, Read: "https://example.com/page") })
            Ensure(await inline.RunAsync(Conversation(), box.Current, caps, result, Policy(false)) is null,
                "B04 platform opt-out must suppress inline search/read");
        Ensure(research.Calls == 0 && research.Cooldowns == 0, "B04 denied tools must not consume cooldown or IO");
        policy.Current = Policy();
        Ensure(await inline.RunAsync(Conversation(), box.Current, caps,
            new CompletionResult(null, null, null, Search: "synthetic query"), Policy()) is not null,
            "enabled inline search remains usable");
        Ensure(research.Calls == 1, "enabled search executed once");

        // A synchronous callback retires policy immediately before IO. No timing-dependent sleep.
        var retiring = new InlineTurnTools(research, approvals, new ParticipationUseCase(box, _ => { }),
            _ => policy.Current = Policy(false), null, policy, () => false);
        Ensure(await retiring.RunAsync(Conversation(), box.Current, caps,
            new CompletionResult(null, null, null, Read: "https://example.com/page"), Policy()) is null,
            "B04 execution must recheck after synchronous callbacks");
        Ensure(research.Calls == 1, "retired platform must not start page IO");
        policy.Current = Policy();
        using var lifetime = new CancellationTokenSource();
        var source = new Source();
        using var scope = PlatformSourceExecution.Enter(new PlatformSourceLease(new Owner(), source,
            new PlatformMessageOrigin(new BotAgent.Domain.Configuration.DriverKey(PlatformId.QqPrivate, AccountScope.Legacy, "synthetic")),
            lifetime.Token, () => true));
        lifetime.Cancel();
        Ensure(await inline.RunAsync(Conversation(), box.Current, caps,
            new CompletionResult(null, null, null, Read: "https://example.com/page"), Policy()) is null,
            "B04 retired source lease must suppress IO");
        Ensure(research.Calls == 1, "cancelled lease did not start IO");
    }

    private static async Task ModelBoundaryAndUsage()
    {
        var settings = new AppSettings { EnableWebSearch = true, EnableMusic = true, EnableVoice = true, MaxAgentSteps = 3 };
        var deniedModel = new Model();
        await new AgentTurnLoop(deniedModel).RunAsync("group:10001", Turn(settings, Policy(false)), Caps, false, 1,
            _ => throw new InvalidOperationException("default one-step invoked inline callback"));
        Ensure(!deniedModel.WebEnabled && !deniedModel.Tools.Contains("web.search", StringComparison.Ordinal)
            && !deniedModel.Tools.Contains("web.read", StringComparison.Ordinal), "B04 advertise only effective platform tools");
        Ensure(deniedModel.Calls == 1, "default one-step contract");
        using var lifetime = new CancellationTokenSource();
        var cancelling = new Model { Complete = ct => { Ensure(ct == lifetime.Token, "B03 model received caller lifecycle token");
            lifetime.Cancel(); return Task.FromResult(new CompletionResult(null, null, null, PromptTokens: 7, CompletionTokens: 3)); } };
        var charged = new List<(string Key, int Prompt, int Completion)>();
        await Cancelled(async () => await new AgentTurnLoop(cancelling).RunAsync("group:10001", Turn(settings, Policy()), Caps,
            false, 3, _ => Task.FromResult<string?>("synthetic result"), null, lifetime.Token,
            step => charged.Add(("group:10001", step.PromptTokens, step.CompletionTokens))));
        Ensure(cancelling.Calls == 1 && charged.Count == 1 && charged[0].Prompt == 7,
            "B25 completed response charged even if cancellation follows");
        lifetime.Cancel();
        var preCancelled = new Model();
        await Cancelled(async () => await new AgentTurnLoop(preCancelled).RunAsync("group:10001", Turn(settings, Policy()), Caps,
            false, 1, _ => Task.FromResult<string?>(null), null, lifetime.Token));
        Ensure(preCancelled.Calls == 0, "B03 cancelled loop must not invoke model");

        charged.Clear();
        var toolsFail = new Model { Complete = _ => Task.FromResult(new CompletionResult(null, null, null, PromptTokens: 11, CompletionTokens: 5)) };
        await Faulted(async () => await new AgentTurnLoop(toolsFail).RunAsync("official:group:10001", Turn(settings, Policy()), Caps,
            false, 2, _ => throw new InvalidOperationException("synthetic fault"), null, default,
            step => charged.Add(("official:group:10001", step.PromptTokens, step.CompletionTokens))));
        Ensure(charged.Count == 1 && charged[0] == ("official:group:10001", 11, 5), "B25 tool exception retains tenant-scoped completed usage");
        var calls = 0;
        var modelFails = new Model { Complete = _ => ++calls == 1
            ? Task.FromResult(new CompletionResult(null, null, null, PromptTokens: 13, CompletionTokens: 2))
            : Task.FromException<CompletionResult>(new InvalidOperationException("synthetic fault")) };
        charged.Clear();
        await Faulted(async () => await new AgentTurnLoop(modelFails).RunAsync("group:10001", Turn(settings, Policy()), Caps,
            false, 3, _ => Task.FromResult<string?>("synthetic tool note"), null, default,
            step => charged.Add(("group:10001", step.PromptTokens, step.CompletionTokens))));
        Ensure(charged.Count == 1 && charged[0].Prompt == 13, "B25 unknown failed model step is not invented or double charged");
        calls = 0;
        var success = new Model { Complete = _ => Task.FromResult(new CompletionResult(null, null, null,
            PromptTokens: ++calls * 10, CompletionTokens: calls, FallbackHops: calls)) };
        charged.Clear();
        var outcome = await new AgentTurnLoop(success).RunAsync("group:10001", Turn(settings, Policy()), Caps,
            false, 3, _ => Task.FromResult<string?>("synthetic note"), null, default,
            step => charged.Add(("group:10001", step.PromptTokens, step.CompletionTokens)));
        Ensure(charged.Count == 3 && charged.Sum(x => x.Prompt) == 60 && charged.Sum(x => x.Completion) == 6,
            "B25 each completed step charged once");
        Ensure(outcome.Result.PromptTokens == 60 && outcome.Result.CompletionTokens == 6 && outcome.Result.FallbackHops == 3,
            "aggregate statistics and max fallback hops preserved");
    }

    private static async Task MediaAdvertisementBoundary()
    {
        var settings = new AppSettings { EnableStickers = true, EnablePoke = true, MaxAgentSteps = 2 };
        var allowed = Policy();
        var denied = allowed with { FeatureOverrides = new Dictionary<string, bool> { ["stickers"] = false, ["poke"] = false } };
        var turn = Turn(settings, allowed) with
        {
            StickerChoices = new List<StickerChoice> { new("synthetic-sticker", "synthetic description") },
            PokeContext = true,
        };
        var noMediaCaps = ResearchToolFixture.Capabilities(true, true, true, false, false, null);
        foreach (var (caps, platform) in new[] { (noMediaCaps, allowed), (Caps, denied) })
        {
            var model = new Model();
            await new AgentTurnLoop(model).RunAsync("group:10001", turn with { PlatformPolicy = platform }, caps, false, 1,
                _ => Task.FromResult<string?>(null));
            Ensure(model.Media.Single() == (0, false), "B04 caps/platform denial hides sticker candidates and poke context");
            Ensure(!model.Tools.Contains("sticker.send", StringComparison.Ordinal) && !model.Tools.Contains("poke.send", StringComparison.Ordinal),
                "B04 denied media must not appear in tool list");
        }
        var disabledPlugins = new MediaPlugins { Enabled = false };
        var pluginDenied = new Model();
        await new AgentTurnLoop(pluginDenied, tools: new ReplyToolAvailability(new PluginGate(disabledPlugins)))
            .RunAsync("group:10001", turn, Caps, false, 1, _ => Task.FromResult<string?>(null));
        Ensure(pluginDenied.Media.Single() == (0, false), "B04 initially disabled media plugins hide action prompt materials");
        foreach (var revokePlugin in new[] { false, true })
        {
            var plugins = new MediaPlugins();
            var policies = new Policies(allowed);
            var model = new Model();
            var tools = new ReplyToolAvailability(new PluginGate(plugins), policies);
            await new AgentTurnLoop(model, tools: tools).RunAsync("group:10001", turn, Caps, false, 2, _ =>
            {
                if (revokePlugin) plugins.Enabled = false;
                else policies.Current = denied;
                return Task.FromResult<string?>("synthetic inline note");
            });
            Ensure(model.Media.Count == 2 && model.Media[0] == (1, true) && model.Media[1] == (0, false),
                "B04 every step rechecks media advertisement after platform/plugin revocation");
            Ensure(!model.Tools.Contains("sticker.send", StringComparison.Ordinal) && !model.Tools.Contains("poke.send", StringComparison.Ordinal),
                "B04 revoked media prompt and tool list remain consistent");
        }
    }

    private static async Task PlainSendCommitBoundary()
    {
        foreach (var successes in new[] { 0, 1, 3 })
        {
            var box = new Box(new AppSettings { AgentReplyMaxChars = 200 });
            var source = new Source { SuccessfulSends = successes };
            var repository = new Conversations();
            var panel = new Panel();
            var own = new Own();
            var registry = new ConversationRegistry(repository, box, source, _ => true, _ => { });
            var sender = new PlainSender(box, source, registry, panel, new OwnMessageLedger(own, _ => { }),
                _ => { }, new TurnTraceStore());
            var conversation = Conversation();
            await sender.SendPlainAsync(conversation, new string('a', 450));
            Ensure(source.Sends == (successes == 0 ? 1 : successes == 1 ? 2 : 3), "B22 stop after first failed segment");
            Ensure(conversation.Messages.Count == successes && panel.Messages == successes && own.Writes == successes,
                "B22 commit/notify/remember only delivered segments including partial success");
        }
    }

    private static async Task QueueMembershipBoundary()
    {
        var box = new Box(new AppSettings { AiModeEnabled = true });
        var policy = new Policies(Policy());
        var pipeline = new ReplyPipeline(box, new Source(), new Model(), null!, null!, null!, new WhitelistGate(box, policy), null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, new Research(), null!, null!,
            new ReplyHooks(_ => { }, () => 10001, () => false), new TurnTraceStore(), platformPolicies: policy);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        pipeline.RefreshPacingPolicy();
        Ensure(typeof(ReplyPipeline).GetField("_pendingReplies", flags) is null
            && typeof(ReplyPipeline).GetField("_deferredTriggers", flags) is null,
            "B16 legacy queue and detached catch-up paths are absent");
        var gate = typeof(ReplyPipeline).GetField("_pacingSync", flags)!.GetValue(pipeline)!;
        var captureEpoch = typeof(ReplyPipeline).GetMethod("CaptureReplyEpoch", flags)!;
        var captureOwner = typeof(ReplyPipeline).GetMethod("CapturePacedOwner", flags)!;
        var isCurrent = typeof(ReplyPipeline).GetMethod("IsPacedOwnerCurrent", flags)!;
        var requestEpoch = typeof(ReplyPipeline).GetMethod("RequestReplyInEpoch", flags)!;
        var track = typeof(ReplyPipeline).GetMethod("TrackPacedObservation", flags)!;
        var dispatch = typeof(ReplyPipeline).GetMethod("DispatchPacedWork", flags)!;
        var coordinator = (ReplyPacingCoordinator)typeof(ReplyPipeline).GetField("_pacingCoordinator", flags)!.GetValue(pipeline)!;
        var conversation = Conversation();
        var epoch = captureEpoch.Invoke(pipeline, [conversation])!;
        var owner = captureOwner.Invoke(pipeline, [conversation, null, epoch])!;
        object pending;
        lock (gate)
        {
            var observation = coordinator.Observe(conversation.SourceKey, 10002, 1, 1, true, true, ReplyPacingOptions.Default, owner);
            pending = track.Invoke(pipeline, [observation, owner])!;
        }
        Ensure(pipeline.QueuedReplies == 1 && (bool)isCurrent.Invoke(pipeline, [owner])!,
            "B16 accepted work retains its conversation owner before dispatch");

        pipeline.ForgetSession(conversation.SourceKey);
        requestEpoch.Invoke(pipeline, [conversation, (long?)1, false, true, epoch]);
        Ensure(!(bool)isCurrent.Invoke(pipeline, [owner])! && pipeline.QueuedReplies == 0,
            "B16 old-epoch work cannot resurrect a dropped conversation");
        dispatch.Invoke(pipeline, [pending]);
        var works = typeof(ReplyPipeline).GetField("_pacedWorks", flags)!.GetValue(pipeline)!;
        for (var i = 0; i < 100 && (int)works.GetType().GetProperty("Count")!.GetValue(works)! != 0; i++) await Task.Delay(1);
        Ensure((int)works.GetType().GetProperty("Count")!.GetValue(works)! == 0 && pipeline.InFlightReplies == 0,
            "B16 detached retired work cleans up without acquiring model resources");
        var sameObjectOwner = captureOwner.Invoke(pipeline, [conversation, null, null])!;
        Ensure((bool)isCurrent.Invoke(pipeline, [sameObjectOwner])!, "B16 fresh same-object owner remains eligible after Stop");
        var restored = Conversation();
        var restoredOwner = captureOwner.Invoke(pipeline, [restored, null, null])!;
        Ensure((bool)isCurrent.Invoke(pipeline, [restoredOwner])! && !(bool)isCurrent.Invoke(pipeline, [owner])!,
            "B16 recreated conversation cannot give old work a fresh owner");
        pipeline.Shutdown();

    }

    private static async Task QueuedWebCallAliasBoundary()
    {
        var settings = new AppSettings { EnableWebSearch = true, MaxAgentSteps = 1, WebSearchCooldownSeconds = 30 };
        var box = new Box(settings);
        var research = new Research { RejectSecondCooldown = true };
        var catalog = ResearchToolFixture.Create(research);
        var caps = ResearchToolFixture.Capabilities(true, false, false, false, false, null, catalog: catalog);
        var policies = new Policies(Policy(true));
        var hooks = new ReplyHooks(_ => { }, () => 10001, () => false);
        var replyTools = new ReplyToolAvailability(null, policies, () => false, () => true, () => catalog);
        var approvals = new ApprovalUseCase(box,
            new ApprovalHooks(_ => { }, (text, _) => text, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask),
            new SessionPolicyLedger(), new TurnTraceStore(), catalog: () => catalog);
        var participation = new ParticipationUseCase(box, _ => { });
        var queued = new List<Func<CancellationToken, Task>>();
        var actionsType = typeof(ReplyPipeline).Assembly.GetType("BotAgent.Services.Reply.ReplyTurnActions")!;
        var ctor = actionsType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
        var actions = ctor.Invoke([
            box, hooks, replyTools, research, null!, null!, null!, approvals, participation, policies,
            null!, null!, (Func<string, bool>)(_ => true),
            (Func<string, string, Func<CancellationToken, Task>, bool>)((_, _, work) => { queued.Add(work); return true; }),
            (Action<BotConversation, AppSettings>)((_, _) => { }), null!
        ]);
        var queueMethod = actionsType.GetMethod("QueueTurnActions", BindingFlags.Instance | BindingFlags.Public)!;
        var conv = Conversation();
        var callAlias = new CompletionResult(null, null, null,
            Call: new InlineToolCall("web.search", new Dictionary<string, string> { ["search"] = "queued-alias" }));
        await (Task)queueMethod.Invoke(actions, [conv, settings, caps, Policy(true), callAlias, true, null!])!;
        await (Task)queueMethod.Invoke(actions, [conv, settings, caps, Policy(true), callAlias, true, null!])!;
        Ensure(queued.Count == 1 && research.Cooldowns == 2,
            "Queued web.search call alias normalizes arguments and respects cooldown");
        await queued[0](CancellationToken.None);
        Ensure(research.Calls == 1, "Queued web.search call alias executes bound contribution once");
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.UnixEpoch;
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        public DateTime LocalDateTime => DateTime.UnixEpoch;
        public long TickCount => 0;
        public Task Delay(TimeSpan delay, CancellationToken ct = default) => ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;
        public Task Delay(int delay, CancellationToken ct = default) => Delay(TimeSpan.FromMilliseconds(delay), ct);
    }
    private sealed class MediaPlugins : IPluginRegistry
    {
        public bool Enabled = true;
        public IReadOnlyList<PluginInfo> GetAll() => Array.Empty<PluginInfo>();
        public IBotPlugin? Find(string pluginId) => null;
        public bool IsEnabled(string pluginId) => Enabled;
    }
    private sealed class Box(AppSettings settings) : ISettingsBox { public AppSettings Current => settings; }
    private sealed class Policies(EffectivePlatformPolicy policy) : IPlatformPolicyResolver
    {
        public EffectivePlatformPolicy Current = policy;
        public EffectivePlatformPolicy Resolve(PlatformContext context) => Current;
        public EffectivePlatformPolicy ResolveForChannel(string? channel) => Current;
        public bool IsChatEnabled(string? channel) => Current.ChatEnabled;
    }
    private sealed class Research : IResearchUseCase
    {
        public int Calls, Cooldowns;
        public bool RejectSecondCooldown;
        public bool IsReady => true;
        public bool TryBeginSearch(string key, int cooldown, out string reason)
        {
            Cooldowns++;
            if (RejectSecondCooldown && Cooldowns > 1) { reason = "cooldown"; return false; }
            reason = "enabled";
            return true;
        }
        public Task<WebSearchResult?> SearchAsync(string key, string query, CancellationToken ct = default)
        { Calls++; return Task.FromResult<WebSearchResult?>(new(query, "synthetic answer", Array.Empty<WebSearchHit>(), "synthetic")); }
        public Task<string?> ReadPageAsync(string key, string url, CancellationToken ct = default)
        { Calls++; return Task.FromResult<string?>("synthetic page"); }
        public void AddNote(string key, string note) { }
        public void KeepNote(string key, string? note, string why) { }
        public string? TakeNote(string key) => null;
    }
    private sealed class Owner : IPlatformSourceOwner
    { public string? ConversationKey(QqChatMessage message, Func<string, bool> exists) => "group:10001"; }
    private sealed class Source : IQqChatSource
    {
        public event Action<QqChatMessage>? MessageReceived { add { } remove { } }
        public event Action<QqPokeEvent>? Poked { add { } remove { } }
        public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
        public event Action<bool>? ConnectionChanged { add { } remove { } }
        public bool IsConnected => true;
        public int Sends;
        public int SuccessfulSends;
        public Task<SendResult> SendTextAsync(bool group, long target, string text, CancellationToken ct = default,
            long? replyToMessageId = null, bool directAddress = false) => Task.FromResult(new SendResult(++Sends <= SuccessfulSends, Sends));
        public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long id, CancellationToken ct = default) => Task.FromResult(((string?)null, 0L));
        public Task<bool> SendImageAsync(bool group, long id, byte[] data, CancellationToken ct = default, long? replyToMessageId = null) => Task.FromResult(false);
        public Task<string?> GetGroupNameAsync(long id, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
    private sealed class Panel : IPanelNotifier
    {
        public int Messages;
        public void EmitLog(string text) { }
        public void SetThinking(BotConversation conversation, bool thinking) { }
        public void NotifyMessageAdded(string key, ChatMessage message) => Messages++;
        public void NotifyConversationsChanged() { }
        public void NotifyStateChanged() { }
    }
    private sealed class Own : IOwnMessageRepository
    {
        public int Writes;
        public int MaxEntries => 200;
        public List<OwnMessage> LoadRecent(int max) => new();
        public void Upsert(long id, string text, DateTimeOffset at) => throw new InvalidOperationException("unscoped write");
        public void Upsert(MessageRef messageRef, string text, DateTimeOffset at) => Writes++;
        public void PruneTo(int max) { }
    }
    private sealed class Conversations : IConversationRepository
    {
        public List<ConversationRecord> LoadAsync() => new();
        public void RequestSave(IEnumerable<ConversationRecord> records) { }
        public void DeleteConversation(string key) { }
        public void AppendArchive(string key, IReadOnlyList<ChatMessage> evicted) { }
        public List<ArchivedMessage> ReadArchive(string key, int limit) => new();
        public long ArchiveCount(string key) => 0;
        public void DeleteMessages(string key) { }
    }
    private sealed class Model : IModelClient
    {
        public string? BotIdentity { get; set; }
        public string? BotPersona { get; set; }
        public int SuitabilityThreshold { get; set; }
        public int MaxContextMessages { get; set; }
        public TimeSpan ChatTimeout => TimeSpan.FromSeconds(1);
        public int Calls;
        public bool WebEnabled;
        public readonly List<(int Stickers, bool Poke)> Media = new();
        public string Tools = "";
        public Func<CancellationToken, Task<CompletionResult>> Complete = _ => Task.FromResult(new CompletionResult(null, null, null));
        public Task<CompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> context, string? profilesText = null,
            CancellationToken ct = default, IReadOnlyList<StickerChoice>? stickers = null, bool pokeContext = false,
            string? moodText = null, string? musicText = null, string? linkText = null, bool enableListen = false,
            bool enableVoice = false, string? recallText = null, bool enableWebSearch = false, string? searchText = null,
            string? groupRolesText = null, string? vibeHint = null, bool proactive = false, bool enableAsk = false,
            bool enableToolRequest = false, string? toolList = null, SamplingProfile? sampling = null, string? promptCacheScope = null, bool isPrivateConversation = false,
            BotAgent.Domain.Model.ConversationPromptOverrides? promptOverrides = null)
        { Calls++; WebEnabled = enableWebSearch; Tools = toolList ?? ""; Media.Add((stickers?.Count ?? 0, pokeContext)); return Complete(ct); }
        public Task<string?> CompleteChatAsync(string model, string systemPrompt, IReadOnlyList<(string Role, string Text)> messages,
            int maxTokens, double temperature, CancellationToken ct = default, string? baseUrlOverride = null, string? apiKeyOverride = null) => Task.FromResult<string?>(null);
        public Task<(byte[] Data, string Mime, string Ext)?> DownloadImageAsync(string url, CancellationToken ct = default, long? messageId = null) => Task.FromResult<(byte[], string, string)?>(null);
        public Task<(List<string> Delete, string? Reason)> CurateStickersAsync(string table, int maxDelete, CancellationToken ct = default) => Task.FromResult((new List<string>(), (string?)null));
        public Task<(string? Desc, List<string>? Tags, bool? IsSticker)> DescribeStickerAsync(byte[] image, string mime, CancellationToken ct = default) => Task.FromResult(((string?)null, (List<string>?)null, (bool?)null));
        public Task<string?> SummarizeSessionTitleAsync(string digest, string? title, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> SummarizePersonaAsync(string name, string? summary, IReadOnlyList<string> messages, int maxChars, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> DescribeAudioAsync(byte[] audio, string format, string title, string? artist, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
