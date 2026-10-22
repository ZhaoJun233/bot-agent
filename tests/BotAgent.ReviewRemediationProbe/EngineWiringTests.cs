using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Services.OneBot;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Stickers;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Conversations;
using BotAgent.Services.Evaluation;
using BotAgent.Services.Links;
using BotAgent.Services.Music;
using BotAgent.Services.Net;
using BotAgent.Services.Ops;
using BotAgent.Services.Permissions;
using BotAgent.Services.Platforms;
using BotAgent.Services.Plugins;
using BotAgent.Services.Plugins.Presets;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;
using BotAgent.Services.Stickers;
using BotAgent.Services.Voice;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace BotAgent.ReviewRemediationProbe;

/// <summary>
/// Synthetic wiring checks for the Wave3 Engine boundary: the reply pipeline must consult the
/// plugin gate, explicit QQ actions must use the same gate, and queued background work must
/// recheck the gate before starting new I/O.
/// </summary>
internal static class EngineWiringTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var box = new SettingsBox(new AppSettings());
        var source = new FakeSource();
        var registry = new ConversationRegistry(null!, box, source, _ => true, _ => { });

        var plugins = new PluginManager(_ => { });
        plugins.Register(new MusicPresetPlugin());
        plugins.Register(new VoicePresetPlugin());
        plugins.Register(new StickersPresetPlugin());
        plugins.Register(new PokePresetPlugin());
        plugins.Register(new VibesPresetPlugin());
        plugins.Register(new ProfilesPresetPlugin());
        plugins.Register(new WebSearchPresetPlugin(), defaultEnabled: false);
        var gate = new PluginGate(plugins);

        var pipeline = new ReplyPipeline(box, source, null!, null!, registry, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            new ReplyHooks(_ => { }, () => 10001, () => false), new TurnTraceStore(), plugins: gate);

        check(!pipeline.IsCapabilityEnabled(PluginGate.WebSearchCapability),
            "disabled preset plugin blocks its capability in the reply pipeline");
        check(!pipeline.IsToolEnabled("web.search"),
            "disabled preset plugin blocks the mapped tool in the reply pipeline");
        check(pipeline.IsCapabilityEnabled(PluginGate.MusicCapability),
            "enabled preset plugin keeps its capability available");
        check(pipeline.IsCapabilityEnabled("unknown-capability"),
            "unknown capability remains ungated");

        plugins.SetEnabled("preset.feature.websearch", true);
        check(pipeline.IsCapabilityEnabled(PluginGate.WebSearchCapability),
            "runtime plugin enable is observed without rebuilding the pipeline");
        check(pipeline.IsToolEnabled("web.read"),
            "runtime plugin enable opens the mapped read tool");

        await VoiceGateTests(check);
        ExplicitQqActionGateTests(check);
        await InboundMediaGateTests(check);
        await StickerBatchRevocationTests(check);
        await StickerTimerRevocationTests(check);
        await StickerManualGateTests(check);
        await QueuedMusicRecheckTests(check);
        await WebSearchQueueTests(check);
        await QueuedSourceLeaseTests(check);
        await QueuedQuoteEnrichmentTests(check);
    }

    // ── F1：语音投递级负例（不是只看 IsCapabilityEnabled） ──
    private static async Task VoiceGateTests(Action<bool, string> check)
    {
        var settings = new AppSettings { EnableVoice = true };
        var box = new SettingsBox(settings);
        var source = new FakeSource();
        var voice = new FakeVoice();
        var plugins = new PluginManager(_ => { });
        plugins.Register(new VoicePresetPlugin(), defaultEnabled: false);
        var gate = new PluginGate(plugins);
        var pipeline = new ReplyPipeline(box, source, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, voice, null!, null!, null!, null!,
            new ReplyHooks(_ => { }, () => 10001, () => false), new TurnTraceStore(), plugins: gate);

        var conversation = new BotConversation { SourceKey = "group:10001", Name = "synthetic" };
        var caps = ChatCapabilitySet.FromSwitches(false, false, true, false, true, null);
        var send = typeof(ReplyPipeline).GetMethod("TrySendVoiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task InvokeAsync(EffectivePlatformPolicy policy)
        {
            var task = (Task<(bool, List<string>)>)send.Invoke(pipeline, new object[]
            {
                conversation, settings, caps, policy, new List<string> { "synthetic" }, "synthetic",
                true, 10001L, new CompletionResult(null, null, null), false,
            })!;
            await task;
        }

        await InvokeAsync(EnabledPolicy());
        check(voice.Builds == 0 && source.VoiceCalls == 0,
            "disabled voice plugin blocks URL build and send in real delivery path");

        await InvokeAsync(EnabledPolicy() with
        {
            FeatureOverrides = new Dictionary<string, bool> { ["voice"] = false },
        });
        check(voice.Builds == 0 && source.VoiceCalls == 0,
            "platform voice disable still blocks delivery (boolean logic cannot be bypassed)");
    }

    // ── F2：显式 QQ 动作也走同一份插件门禁 ──
    private static void ExplicitQqActionGateTests(Action<bool, string> check)
    {
        var box = new SettingsBox(new AppSettings { EnablePoke = true, PrivateChatEnabled = true });
        var plugins = new PluginManager(_ => { });
        plugins.Register(new PokePresetPlugin(), defaultEnabled: false);
        var gate = new PluginGate(plugins);
        var actions = new FakeActions();
        var registry = RegistryProxy.Create();
        var resolver = PlatformPolicyResolver.ForSettings(box, registry);
        var host = new SessionQqActionHost(actions, true, 10001, 20002, 1, 30003,
            resolver, Channels.Private, gate.Allows);
        var spec = QqActionCatalog.All.Single(action => action.Name == "poke");
        var args = new JsonObject { ["user_id"] = "20002" };

        check(resolver.ResolveForChannel(Channels.Private).Feature("poke").Enabled,
            "synthetic platform policy opens poke before the plugin gate assertion");
        host.ExecuteAsync(spec, args, CancellationToken.None).GetAwaiter().GetResult();
        check(actions.Pokes == 0, "disabled poke plugin blocks explicit qq.poke before the gateway");

        plugins.SetEnabled("preset.feature.poke", true);
        host.ExecuteAsync(spec, args, CancellationToken.None).GetAwaiter().GetResult();
        check(actions.Pokes == 1, "enabled poke plugin allows explicit qq.poke");
    }

    // ── R6：入站媒体作业排队后关闭插件，不得再开始外部 I/O ──
    private static async Task InboundMediaGateTests(Action<bool, string> check)
    {
        foreach (var kind in new[] { "music", "link", "stickers" })
        {
            await RunInboundMediaCaseAsync(kind, revoked: false, check);
            await RunInboundMediaCaseAsync(kind, revoked: true, check);
        }
    }

    private static async Task RunInboundMediaCaseAsync(string kind, bool revoked, Action<bool, string> check)
    {
        var settings = new AppSettings
        {
            EnableMusic = true,
            EnableLinkPreview = true,
            LinkPreviewMax = 2,
            EnableStickers = true,
            StickerLibraryMax = 5,
        };
        var box = new SettingsBox(settings);
        var source = new FakeSource();
        var plugins = new PluginManager(_ => { });
        plugins.Register(new StickersPresetPlugin());
        plugins.Register(new MusicPresetPlugin());
        plugins.Register(new WebSearchPresetPlugin(), defaultEnabled: false);
        plugins.SetEnabled("preset.feature.stickers", true);
        plugins.SetEnabled("preset.feature.music", true);
        plugins.SetEnabled("preset.feature.websearch", true);
        var gate = new PluginGate(plugins);

        var music = new FakeMusic();
        var links = new FakeLinkPreviewer();
        var model = DispatchProxy.Create<IModelClient, CountingModelProxy>();
        var modelCalls = (CountingModelProxy)(object)model;
        using var stickers = new StickerService(null!, model, source, box, _ => { });
        var background = NewQueue();

        var pipeline = new ReplyPipeline(
            box: box, source: source, brain: null!, profiles: null!, registry: null!, ui: null!,
            whitelist: null!, approvals: null!, participation: null!, poke: null!, vibes: null!,
            roles: null!, ownLedger: null!, agentCmds: null!, plain: null!, stickers: stickers,
            voice: null!, music: music, research: null!, links: links, mood: null!,
            hooks: new ReplyHooks(_ => { }, () => 30003, () => false), traces: new TurnTraceStore(),
            platformPolicies: new FixedPolicyResolver(EnabledPolicy()), plugins: gate, background: background);

        var conversation = new BotConversation { SourceKey = "group:10001", Name = "synthetic" };
        var history = (System.Collections.Concurrent.ConcurrentDictionary<string, byte>)typeof(ReplyPipeline)
            .GetField("_historyRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pipeline)!;
        history.TryAdd(conversation.SourceKey, 0);

        var msg = kind switch
        {
            "music" => new QqChatMessage(40004, false, 10001, 0, "synthetic", "synthetic",
                DateTimeOffset.UnixEpoch, false,
                MusicShares: new[] { new MusicShare("netease", "10001", "synthetic") }),
            "link" => new QqChatMessage(40004, false, 10001, 0, "synthetic",
                "https://example.com/synthetic", DateTimeOffset.UnixEpoch, false),
            _ => new QqChatMessage(40004, true, 10001, 10001, "synthetic", "synthetic",
                DateTimeOffset.UnixEpoch, false,
                ImageUrls: new[] { "https://example.com/synthetic.png" }),
        };

        var capability = kind switch
        {
            "music" => PluginGate.MusicCapability,
            "link" => PluginGate.WebSearchCapability,
            _ => PluginGate.StickersCapability,
        };
        var pluginId = kind switch
        {
            "music" => "preset.feature.music",
            "link" => "preset.feature.websearch",
            _ => "preset.feature.stickers",
        };

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = background.EnqueueAsync("blocker", "blocker", async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var handle = typeof(ReplyPipeline).GetMethod("HandleInboundMedia",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        handle.Invoke(pipeline, new object[] { conversation, msg });

        var mode = revoked ? "revoked" : "positive";
        check(background.Snapshot().Queued == 1, $"{mode} {kind} inbound media is queued behind the blocker");
        var callsBeforeRelease = kind switch
        {
            "music" => music.DescribeShareCalls,
            "link" => links.Calls,
            _ => modelCalls.Calls,
        };
        check(callsBeforeRelease == 0, $"{mode} {kind} inbound media starts no I/O before its worker runs");

        if (revoked)
        {
            check(gate.Allows(capability), $"{kind} plugin is enabled before revocation");
            plugins.SetEnabled(pluginId, false);
            check(!gate.Allows(capability), $"{kind} plugin is revoked before the worker starts");
        }

        release.TrySetResult();
        await blocker.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => background.Snapshot().Outstanding == 0,
            $"{kind} inbound media job settles");

        var calls = kind switch
        {
            "music" => music.DescribeShareCalls,
            "link" => links.Calls,
            _ => modelCalls.Calls,
        };
        var expected = revoked ? 0 : 1;
        check(calls == expected,
            $"{mode} {kind} inbound media expected {expected} I/O call(s) after worker, actual {calls}");
        await background.StopAsync();
    }

    // ── R8：贴图批次中途撤权，第二张不得开始下载 ──
    private static async Task StickerBatchRevocationTests(Action<bool, string> check)
    {
        await RunStickerBatchCaseAsync(revoked: false, check);
        await RunStickerBatchCaseAsync(revoked: true, check);
    }

    private static async Task RunStickerBatchCaseAsync(bool revoked, Action<bool, string> check)
    {
        var settings = new AppSettings { EnableStickers = true, StickerLibraryMax = 5 };
        var box = new SettingsBox(settings);
        var source = new FakeSource();
        var plugins = new PluginManager(_ => { });
        plugins.Register(new StickersPresetPlugin());
        var gate = new PluginGate(plugins);
        var model = DispatchProxy.Create<IModelClient, CountingModelProxy>();
        var modelCalls = (CountingModelProxy)(object)model;
        modelCalls.HoldFirstDownload = true;
        using var stickers = new StickerService(null!, model, source, box, _ => { });
        var background = NewQueue();

        var pipeline = new ReplyPipeline(
            box: box, source: source, brain: null!, profiles: null!, registry: null!, ui: null!,
            whitelist: null!, approvals: null!, participation: null!, poke: null!, vibes: null!,
            roles: null!, ownLedger: null!, agentCmds: null!, plain: null!, stickers: stickers,
            voice: null!, music: null!, research: null!, links: null!, mood: null!,
            hooks: new ReplyHooks(_ => { }, () => 30003, () => false), traces: new TurnTraceStore(),
            platformPolicies: new FixedPolicyResolver(EnabledPolicy()), plugins: gate, background: background);

        var conversation = new BotConversation { SourceKey = "group:10001", Name = "synthetic" };
        var history = (System.Collections.Concurrent.ConcurrentDictionary<string, byte>)typeof(ReplyPipeline)
            .GetField("_historyRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pipeline)!;
        history.TryAdd(conversation.SourceKey, 0);

        var msg = new QqChatMessage(40004, true, 10001, 10001, "synthetic", "synthetic",
            DateTimeOffset.UnixEpoch, false,
            ImageUrls: new[] { "https://example.com/first.png", "https://example.com/second.png" });

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = background.EnqueueAsync("blocker", "blocker", async _ =>
        {
            entered.TrySetResult();
            await releaseBlocker.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var handle = typeof(ReplyPipeline).GetMethod("HandleInboundMedia",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        handle.Invoke(pipeline, new object[] { conversation, msg });

        var mode = revoked ? "revoked" : "positive";
        check(background.Snapshot().Queued == 1, $"{mode} sticker batch is queued behind the blocker");
        check(modelCalls.Calls == 0, $"{mode} sticker batch starts no download while queued");

        releaseBlocker.TrySetResult();
        await blocker.WaitAsync(TimeSpan.FromSeconds(5));
        await modelCalls.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(modelCalls.Calls == 1, $"{mode} sticker batch starts exactly one in-flight download");

        if (revoked)
        {
            check(gate.Allows(PluginGate.StickersCapability),
                "sticker plugin is enabled before mid-batch revocation");
            plugins.SetEnabled("preset.feature.stickers", false);
            check(!gate.Allows(PluginGate.StickersCapability),
                "sticker plugin is revoked while the first download is in flight");
        }

        modelCalls.ReleaseFirst.TrySetResult();
        await WaitUntilAsync(() => background.Snapshot().Outstanding == 0,
            $"{mode} sticker batch settles");

        var expected = revoked ? 1 : 2;
        check(modelCalls.Calls == expected,
            $"{mode} sticker batch expected {expected} download(s), actual {modelCalls.Calls}");
        await background.StopAsync();
    }

    // ── R9：定时贴图审核在停用后不得开始下一项模型 I/O ──
    private static async Task StickerTimerRevocationTests(Action<bool, string> check)
    {
        await RunStickerTimerCaseAsync(revoked: false, check);
        await RunStickerTimerCaseAsync(revoked: true, check);
    }

    private static async Task RunStickerTimerCaseAsync(bool revoked, Action<bool, string> check)
    {
        var settings = new SettingsBox(new AppSettings
        {
            EnableStickers = true,
            StickerLibraryMax = 5,
            StickerCurateIntervalSeconds = 0,
            IdleFallbackSeconds = 0,
            EnableProfileSummary = false,
            ProfileSummaryIntervalSeconds = 0,
        });
        var source = new FakeSource();
        var plugins = new PluginManager(_ => { });
        plugins.Register(new StickersPresetPlugin());
        var gate = new PluginGate(plugins);
        var repository = DispatchProxy.Create<IStickerRepository, TimerStickerRepositoryProxy>();
        var model = DispatchProxy.Create<IModelClient, TimerStickerModelProxy>();
        var counted = (TimerStickerModelProxy)(object)model;
        using var stickers = new StickerService(repository, model, source, settings, _ => { });
        var scheduler = new BotScheduler(settings, source, null!, null!, null!, stickers, null!,
            new BotSchedulerHooks(_ => { }, () => false), gate.Allows);
        try
        {
            scheduler.RebuildIfNeeded();
            var timerField = typeof(StickerService).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var mode = revoked ? "revoked" : "positive";
            check(timerField.GetValue(stickers) is not null && stickers.PendingDescribe == 2,
                $"{mode} sticker timer is armed with two pending rows");

            // 与真实 timer 回调同一实现；不等待 10 秒到点。
            var tick = (Task)typeof(StickerService).GetMethod("TickAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(stickers, null)!;
            await counted.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(counted.Calls == 1, $"{mode} sticker timer starts exactly one model call");

            if (revoked)
            {
                check(gate.Allows(PluginGate.StickersCapability),
                    "sticker plugin is enabled before timer revocation");
                plugins.SetEnabled("preset.feature.stickers", false);
                scheduler.RebuildIfNeeded();
                check(!gate.Allows(PluginGate.StickersCapability) && timerField.GetValue(stickers) is null,
                    "disabling the sticker plugin disarms the timer");
            }

            counted.ReleaseFirst.TrySetResult();
            await tick.WaitAsync(TimeSpan.FromSeconds(5));
            var expected = revoked ? 1 : 2;
            check(counted.Calls == expected,
                $"{mode} sticker timer expected {expected} model call(s), actual {counted.Calls}");
        }
        finally
        {
            scheduler.Stop();
        }
    }

    // ── R10：面板手动入口（导入/立即巡检）不得绕过插件闸门 ──
    private static async Task StickerManualGateTests(Action<bool, string> check)
    {
        await RunStickerManualCaseAsync("curate", revoked: false, check);
        await RunStickerManualCaseAsync("curate", revoked: true, check);
        await RunStickerManualCaseAsync("import", revoked: false, check);
        await RunStickerManualCaseAsync("import", revoked: true, check);
        await StickerImportRevokedMidFlightTests(check);
    }

    private static async Task RunStickerManualCaseAsync(string action, bool revoked, Action<bool, string> check)
    {
        var box = new SettingsBox(new AppSettings { EnableStickers = true, StickerLibraryMax = 10 });
        var plugins = new PluginManager(_ => { });
        plugins.Register(new StickersPresetPlugin(), defaultEnabled: !revoked);
        var gate = new PluginGate(plugins);
        var repository = DispatchProxy.Create<IStickerRepository, ManualStickerRepositoryProxy>();
        var model = DispatchProxy.Create<IModelClient, ManualStickerModelProxy>();
        var counted = (ManualStickerModelProxy)(object)model;
        var source = DispatchProxy.Create<IQqChatSource, ManualStickerSourceProxy>();
        var countedSource = (ManualStickerSourceProxy)(object)source;

        using var stickers = new StickerService(repository, model, source, box, _ => { });
        stickers.PluginEnabled = () => gate.Allows(PluginGate.StickersCapability);

        var mode = revoked ? "revoked" : "positive";
        if (action == "curate")
        {
            var summary = await stickers.CurateAsync(force: true);
            var expected = revoked ? 0 : 1;
            check(counted.Curates == expected,
                $"{mode} panel curate expected {expected} model call(s), actual {counted.Curates} ({summary})");
        }
        else
        {
            var summary = await stickers.ImportFromAlbumAsync();
            var expectedFetches = revoked ? 0 : 1;
            var expectedDownloads = revoked ? 0 : 2;
            check(countedSource.Fetches == expectedFetches && counted.Downloads == expectedDownloads,
                $"{mode} panel import expected fetches/downloads {expectedFetches}/{expectedDownloads}, " +
                $"actual {countedSource.Fetches}/{counted.Downloads} ({summary})");
        }
    }

    private static async Task StickerImportRevokedMidFlightTests(Action<bool, string> check)
    {
        var box = new SettingsBox(new AppSettings { EnableStickers = true, StickerLibraryMax = 10 });
        var plugins = new PluginManager(_ => { });
        plugins.Register(new StickersPresetPlugin());
        var gate = new PluginGate(plugins);
        var repository = DispatchProxy.Create<IStickerRepository, ManualStickerRepositoryProxy>();
        var model = DispatchProxy.Create<IModelClient, ManualStickerModelProxy>();
        var counted = (ManualStickerModelProxy)(object)model;
        var source = DispatchProxy.Create<IQqChatSource, ManualStickerSourceProxy>();
        var countedSource = (ManualStickerSourceProxy)(object)source;
        counted.HoldFirstDownload = true;

        using var stickers = new StickerService(repository, model, source, box, _ => { });
        stickers.PluginEnabled = () => gate.Allows(PluginGate.StickersCapability);

        var import = stickers.ImportFromAlbumAsync();
        await counted.FirstDownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(counted.Downloads == 1 && countedSource.Fetches == 1,
            "panel import starts the protocol fetch and the first download");

        plugins.SetEnabled("preset.feature.stickers", false);
        counted.ReleaseFirst.TrySetResult();
        var summary = await import.WaitAsync(TimeSpan.FromSeconds(5));
        check(counted.Downloads == 1 && countedSource.Fetches == 1,
            $"panel import stops before the second download after revocation (actual {counted.Downloads})");
        check(summary.Contains("功能已停用", StringComparison.Ordinal),
            "revoked panel import reports that it ended early");
    }

    // ── F3：排队中的音乐作业开始前必须复核插件/取消 ──
    private static async Task QueuedMusicRecheckTests(Action<bool, string> check)
    {
        var settings = new AppSettings { EnableMusic = true, AiModeEnabled = false };
        var box = new SettingsBox(settings);
        var source = new FakeSource();
        var plugins = new PluginManager(_ => { });
        plugins.Register(new MusicPresetPlugin());
        var gate = new PluginGate(plugins);
        var music = new FakeMusic();
        var background = NewQueue();
        var registry = new ConversationRegistry(null!, box, source, _ => true, _ => { });
        var approvals = new ApprovalUseCase(box,
            new ApprovalHooks(_ => { }, (_, _) => "synthetic", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask),
            new SessionPolicyLedger(), new TurnTraceStore());
        var pipeline = new ReplyPipeline(box, source, null!, null!, registry, null!, null!, approvals, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, music, null!, null!, null!,
            new ReplyHooks(_ => { }, () => 10001, () => false), new TurnTraceStore(),
            platformPolicies: new FixedPolicyResolver(EnabledPolicy()), plugins: gate, background: background);

        var conversation = new BotConversation { SourceKey = "group:10001", Name = "synthetic" };
        var caps = ChatCapabilitySet.FromSwitches(false, true, true, false, true, null);
        var queueActions = typeof(ReplyPipeline).GetMethod("QueueTurnActionsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task InvokeAsync() => await (Task)queueActions.Invoke(pipeline, new object[]
        {
            conversation, settings, caps, EnabledPolicy(),
            new CompletionResult(null, null, null, Listen: "synthetic"), false,
        })!;

        await InvokeAsync();
        await WaitUntilAsync(() => background.Snapshot().Outstanding == 0 && music.ListenCalls == 1,
            "enabled music job executes");
        check(music.ListenCalls == 1, "enabled queued music work still executes (positive control)");

        // 占住唯一 worker，让第二次作业停在排队状态，再关插件。
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = background.EnqueueAsync("blocker", "blocker", async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await InvokeAsync();
        check(background.Snapshot().Queued == 1, "second music job is queued behind the blocker");
        plugins.SetEnabled("preset.feature.music", false);
        release.TrySetResult();
        await blocker.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => background.Snapshot().Outstanding == 0, "queued music job settles");
        check(music.ListenCalls == 1,
            "queued music work rechecks the plugin and starts no I/O after disable");

        await background.StopAsync();
    }

    // ── F5：联网搜索/读页走有界队列，不再直接 Task.Run ──
    private static async Task WebSearchQueueTests(Action<bool, string> check)
    {
        var settings = new AppSettings { EnableWebSearch = true, AiModeEnabled = false };
        var box = new SettingsBox(settings);
        var source = new FakeSource();
        var plugins = new PluginManager(_ => { });
        plugins.Register(new WebSearchPresetPlugin());
        var gate = new PluginGate(plugins);
        var research = new FakeResearch();
        var background = NewQueue();
        var pipeline = new ReplyPipeline(box, source, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, research, null!, null!,
            new ReplyHooks(_ => { }, () => 10001, () => false), new TurnTraceStore(),
            plugins: gate, background: background);

        var conversation = new BotConversation { SourceKey = "group:10001", Name = "synthetic" };
        var queueSearch = typeof(ReplyPipeline).GetMethod("QueueWebSearchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = background.EnqueueAsync("blocker", "blocker", async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        queueSearch.Invoke(pipeline, new object[] { conversation, "synthetic-query" });
        check(research.Calls == 0 && background.Snapshot().Queued == 1,
            "web search is admitted through the bounded queue, not started inline");

        release.TrySetResult();
        await blocker.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => research.Calls == 1 && background.Snapshot().Outstanding == 0,
            "queued web search executes");
        check(research.Calls == 1, "queued web search executes once through the queue");
        check(research.LastToken is { CanBeCanceled: true },
            "queued web search receives the queue cancellation token");
        await background.StopAsync();
    }

    // ── R3：后台作业必须显式捕获/恢复来源租约，不能回退到通道默认源 ──
    private static async Task QueuedSourceLeaseTests(Action<bool, string> check)
    {
        using var router = new ChannelRouter(new IQqChatSource[] { new FakeSource() });
        var enqueue = typeof(ReplyPipeline).GetMethod("QueueBackground", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // 缺少租约 + 已接路由器：拒绝启动，而不是让工作落到通道默认源。
        var noLeaseQueue = NewQueue();
        try
        {
            var pipeline = NewBarePipeline(noLeaseQueue, router);
            var accepted = (bool)enqueue.Invoke(pipeline, new object[]
            {
                "synthetic-no-lease", "synthetic-no-lease",
                (Func<CancellationToken, Task>)(_ => Task.CompletedTask),
            })!;
            check(!accepted && noLeaseQueue.Snapshot().Queued == 0,
                "background work without a source lease is rejected when routing is source-bound");
        }
        finally
        {
            await noLeaseQueue.StopAsync();
        }

        // 有租约：作业里看到的必须是入队时那一份（同一个对象），不是默认源或全局最后来源。
        var leaseQueue = NewQueue();
        try
        {
            var pipeline = NewBarePipeline(leaseQueue, router);
            var lease = new PlatformSourceLease(
                router,
                new FakeSource(),
                new PlatformMessageOrigin(new DriverKey(PlatformId.QqPrivate, AccountScope.Legacy, "synthetic")),
                CancellationToken.None,
                () => true);

            PlatformSourceLease? observed = null;
            var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool accepted;
            using (PlatformSourceExecution.Enter(lease))
            {
                accepted = (bool)enqueue.Invoke(pipeline, new object[]
                {
                    "synthetic-lease", "synthetic-lease",
                    (Func<CancellationToken, Task>)(_ =>
                    {
                        observed = PlatformSourceExecution.Current;
                        ran.TrySetResult();
                        return Task.CompletedTask;
                    }),
                })!;
            }

            check(accepted, "lease-bearing background work is admitted");
            await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(ReferenceEquals(observed, lease),
                "background work restores the enqueue-time source lease");
            await WaitUntilAsync(() => leaseQueue.Snapshot().Outstanding == 0, "lease job settles");
        }
        finally
        {
            await leaseQueue.StopAsync();
        }
    }

    // ── R4：引用原文补查走有界队列，不再 fire-and-forget ──
    private static async Task QueuedQuoteEnrichmentTests(Action<bool, string> check)
    {
        var background = NewQueue();
        try
        {
            var source = new FakeSource();
            var pipeline = NewBarePipeline(background, source);
            typeof(ReplyPipeline).GetField("_box", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(pipeline, new SettingsBox(new AppSettings()));
            typeof(ReplyPipeline).GetField("_quoteEnrichLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(pipeline, new object());

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = background.EnqueueAsync("blocker", "blocker", async _ =>
            {
                entered.TrySetResult();
                await release.Task;
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var conversation = new BotConversation { SourceKey = "group:10001", Name = "synthetic" };
            var appended = new ChatMessage
            {
                Role = MessageRole.Peer,
                Text = "（已经看不到原文）",
                QqMessageId = 40004,
            };
            var enrich = typeof(ReplyPipeline).GetMethod(
                "EnrichQuotedFromProtocolAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            enrich.Invoke(pipeline, new object[] { conversation, appended, 50005L, true, false });

            check(source.MessageInfoCalls == 0 && background.Snapshot().Queued == 1,
                "quote enrichment is admitted through the bounded queue, not started inline");

            release.TrySetResult();
            await blocker.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => source.MessageInfoCalls == 1 && background.Snapshot().Outstanding == 0,
                "queued quote enrichment executes");
            check(source.MessageInfoCalls == 1,
                "quote enrichment runs exactly once through the queue");
        }
        finally
        {
            await background.StopAsync();
        }
    }

    private static ReplyPipeline NewBarePipeline(BackgroundEvaluatorQueue background, IQqChatSource source)
    {
        // 只构造队列/来源/钩子三项：私有方法本身不依赖其余子系统（引用取不到原文时提前返回）。
        var pipeline = (ReplyPipeline)RuntimeHelpers.GetUninitializedObject(typeof(ReplyPipeline));
        typeof(ReplyPipeline).GetField("_background", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(pipeline, background);
        typeof(ReplyPipeline).GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(pipeline, source);
        typeof(ReplyPipeline).GetField("_hooks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(pipeline, new ReplyHooks(_ => { }, () => 10001, () => false));
        return pipeline;
    }

    private static BackgroundEvaluatorQueue NewQueue()
        => new(new BackgroundEvaluatorOptions
        {
            Capacity = 4,
            MaxConcurrency = 1,
            MaxAttempts = 1,
            PerJobTimeout = TimeSpan.FromSeconds(5),
            DrainTimeout = TimeSpan.FromMilliseconds(200),
        });

    private static EffectivePlatformPolicy EnabledPolicy()
        => new(new PlatformContext(PlatformId.QqPrivate), true, true, true, true,
            PlatformCapabilities.QqOneBot, new HashSet<string>(), new Dictionary<string, bool>(),
            Array.Empty<string>());

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException("timed out waiting for: " + what);
    }

    private sealed class FixedPolicyResolver(EffectivePlatformPolicy policy) : IPlatformPolicyResolver
    {
        public EffectivePlatformPolicy Resolve(PlatformContext context) => policy;
        public EffectivePlatformPolicy ResolveForChannel(string? channel) => policy;
        public bool IsChatEnabled(string? channel) => policy.Enabled && policy.ChatEnabled;
    }

    private class RegistryProxy : DispatchProxy
    {
        public static IPlatformRegistry Create()
            => DispatchProxy.Create<IPlatformRegistry, RegistryProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IPlatformRegistry.GetSnapshots))
            {
                return new List<PlatformStatusSnapshot>
                {
                    new(PlatformId.QqPrivate, AccountScope.Legacy, "synthetic", "synthetic", true, true,
                        PlatformCapabilities.QqOneBot),
                };
            }

            if (targetMethod?.ReturnType == typeof(bool))
            {
                return true;
            }

            return null;
        }
    }

    private sealed class FakeVoice : IVoiceUseCase
    {
        public int Builds;
        public int SuggestIntervalSeconds => 0;
        public string? ClientVoiceName => null;
        public bool Allow(string sourceKey, out string reason) { reason = "synthetic"; return true; }
        public List<string> BuildSpeakUrls(IReadOnlyList<string> parts, string? emotion, double? speed, int? pitch)
        {
            Builds++;
            return new List<string> { "https://example.com/synthetic" };
        }

        public void MarkSent(string sourceKey) { }
    }

    private sealed class FakeMusic : IMusicUseCase
    {
        private int _listenCalls;
        private int _describeShareCalls;
        public bool IsReady => true;
        public int ListenCalls => Volatile.Read(ref _listenCalls);
        public int DescribeShareCalls => Volatile.Read(ref _describeShareCalls);
        public Task<string?> DescribeShareAsync(MusicShare share, string sender)
        {
            Interlocked.Increment(ref _describeShareCalls);
            return Task.FromResult<string?>(null);
        }

        public void AddNote(string sourceKey, string note) { }
        public string? TakeNote(string sourceKey) => null;
        public bool TryBeginListen(string sourceKey, int cooldownSeconds, out string reason)
        {
            reason = string.Empty;
            return true;
        }

        public Task<string?> ListenAsync(string sourceKey, string song, string requester, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _listenCalls);
            return Task.FromResult<string?>("synthetic note");
        }

        public Task<bool> ShareAsync(string song, bool isGroup, long targetId, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class FakeLinkPreviewer : ILinkPreviewer
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<string?> DescribeAsync(IReadOnlyList<string> urls, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeResearch : IResearchUseCase
    {
        private int _calls;
        public bool IsReady => true;
        public int Calls => Volatile.Read(ref _calls);
        public CancellationToken? LastToken { get; private set; }
        public bool TryBeginSearch(string sourceKey, int cooldownSeconds, out string reason)
        {
            reason = string.Empty;
            return true;
        }

        public Task<WebSearchResult?> SearchAsync(string sourceKey, string query, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            LastToken = ct;
            return Task.FromResult<WebSearchResult?>(null);
        }

        public Task<string?> ReadPageAsync(string sourceKey, string url, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public void AddNote(string sourceKey, string note) { }
        public void KeepNote(string sourceKey, string? note, string why) { }
        public string? TakeNote(string sourceKey) => null;
    }

    private sealed class FakeActions : IQqActions
    {
        public int Pokes;
        public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default,
            long? replyToMessageId = null) => Task.FromResult(new SendResult(true, 1));
        public Task<bool> SendPokeAsync(bool isGroup, long targetId, long userId, CancellationToken ct = default)
        {
            Pokes++;
            return Task.FromResult(true);
        }

        public Task<bool> SendLikeAsync(long userId, int times, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetMessageEmojiLikeAsync(long messageId, string emojiId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> DeleteMessageAsync(long messageId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetGroupBanAsync(long groupId, long userId, int seconds, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetGroupKickAsync(long groupId, long userId, bool rejectAdd, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetGroupCardAsync(long groupId, long userId, string card, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetGroupNameAsync(long groupId, string groupName, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetGroupLeaveAsync(long groupId, bool dismiss, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FakeSource : IQqChatSource
    {
        public int VoiceCalls;
        public int MessageInfoCalls;
        public event Action<QqChatMessage>? MessageReceived { add { } remove { } }
        public event Action<QqPokeEvent>? Poked { add { } remove { } }
        public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
        public event Action<bool>? ConnectionChanged { add { } remove { } }

        public bool IsConnected => true;

        public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text,
            CancellationToken ct = default, long? replyToMessageId = null, bool directAddress = false)
            => Task.FromResult(new SendResult(true, 1));

        public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
        {
            MessageInfoCalls++;
            return Task.FromResult<(string?, long)>((null, 0));
        }

        public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data,
            CancellationToken ct = default, long? replyToMessageId = null)
            => Task.FromResult(true);

        public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<bool> SendVoiceAsync(bool isGroup, long targetId, string audioUrl, CancellationToken ct = default)
        {
            VoiceCalls++;
            return Task.FromResult(true);
        }
    }
}

public class CountingModelProxy : DispatchProxy
{
    public int Calls;
    public bool HoldFirstDownload;
    public TaskCompletionSource FirstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IModelClient.DownloadImageAsync))
        {
            var call = Interlocked.Increment(ref Calls);
            if (HoldFirstDownload && call == 1)
            {
                FirstStarted.TrySetResult();
                return CompleteFirstAsync();
            }

            return Task.FromResult<(byte[] Data, string Mime, string Ext)?>(null);
        }

        throw new InvalidOperationException("synthetic_unexpected_" + targetMethod?.Name);
    }

    private async Task<(byte[] Data, string Mime, string Ext)?> CompleteFirstAsync()
    {
        await ReleaseFirst.Task;
        return null;
    }
}

public class TimerStickerModelProxy : DispatchProxy
{
    public int Calls;
    public TaskCompletionSource FirstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != nameof(IModelClient.DescribeStickerAsync))
        {
            throw new InvalidOperationException("synthetic_unexpected_" + targetMethod?.Name);
        }

        var call = Interlocked.Increment(ref Calls);
        if (call == 1)
        {
            FirstStarted.TrySetResult();
            return CompleteFirstAsync();
        }

        return Task.FromResult<(string?, List<string>?, bool?)>((null, null, null));
    }

    private async Task<(string?, List<string>?, bool?)> CompleteFirstAsync()
    {
        await ReleaseFirst.Task;
        return (null, null, null);
    }
}

public class TimerStickerRepositoryProxy : DispatchProxy
{
    private readonly List<StickerRecord> _rows =
    [
        new() { Id = "synthetic-a", Ext = "png" },
        new() { Id = "synthetic-b", Ext = "png" },
    ];

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod!.Name switch
        {
            nameof(IStickerRepository.Snapshot) => _rows.ToList(),
            nameof(IStickerRepository.Find) => _rows.FirstOrDefault(row => row.Id == (string)args![0]!),
            nameof(IStickerRepository.ReadBytesAsync) => Task.FromResult(new byte[] { 1, 2, 3 }),
            nameof(IStickerRepository.MarkDescribeFailed) => null,
            _ => throw new InvalidOperationException("synthetic_unexpected_repository_" + targetMethod.Name),
        };
}

public class ManualStickerRepositoryProxy : DispatchProxy
{
    private readonly List<StickerRecord> _rows = Enumerable.Range(1, 6)
        .Select(i => new StickerRecord { Id = "synthetic-" + i, Ext = "png", Described = true, IsSticker = true })
        .ToList();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod!.Name switch
        {
            nameof(IStickerRepository.Snapshot) => _rows.ToList(),
            nameof(IStickerRepository.Find) => _rows.FirstOrDefault(row => row.Id == (string)args![0]!),
            nameof(IStickerRepository.Add) => Add((byte[])args![0]!, (string)args[1]!, (string?)args[2], (long)args[3]!),
            nameof(IStickerRepository.EnforceLimit) => new List<StickerRecord>(),
            nameof(IStickerRepository.Remove) => true,
            "get_Count" => _rows.Count,
            _ => throw new InvalidOperationException("synthetic_unexpected_repository_" + targetMethod.Name),
        };

    private StickerRecord Add(byte[] data, string ext, string? fromUid, long fromGroup)
    {
        var record = new StickerRecord { Id = "imported-" + (_rows.Count + 1), Ext = ext, Described = false };
        _rows.Add(record);
        return record;
    }
}

public class ManualStickerModelProxy : DispatchProxy
{
    public int Downloads;
    public int Curates;
    public bool HoldFirstDownload;
    public TaskCompletionSource FirstDownloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IModelClient.DownloadImageAsync))
        {
            var call = Interlocked.Increment(ref Downloads);
            if (HoldFirstDownload && call == 1)
            {
                FirstDownloadStarted.TrySetResult();
                return CompleteDownloadAsync();
            }

            return Task.FromResult<(byte[] Data, string Mime, string Ext)?>(
                (new byte[] { 1, 2, 3 }, "image/png", "png"));
        }

        if (targetMethod?.Name == nameof(IModelClient.CurateStickersAsync))
        {
            Interlocked.Increment(ref Curates);
            return Task.FromResult<(List<string> Delete, string? Reason)>(
                (new List<string>(), "synthetic"));
        }

        if (targetMethod?.Name == nameof(IModelClient.DescribeStickerAsync))
        {
            return Task.FromResult<(string?, List<string>?, bool?)>((null, null, null));
        }

        throw new InvalidOperationException("synthetic_unexpected_model_" + targetMethod?.Name);
    }

    private async Task<(byte[] Data, string Mime, string Ext)?> CompleteDownloadAsync()
    {
        await ReleaseFirst.Task;
        return (new byte[] { 1, 2, 3 }, "image/png", "png");
    }
}

public class ManualStickerSourceProxy : DispatchProxy
{
    public int Fetches;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IQqChatSource.FetchCustomFacesAsync))
        {
            Interlocked.Increment(ref Fetches);
            return Task.FromResult(new List<string>
            {
                "https://example.com/synthetic-a.png",
                "https://example.com/synthetic-b.png",
            });
        }

        throw new InvalidOperationException("synthetic_unexpected_source_" + targetMethod?.Name);
    }
}
