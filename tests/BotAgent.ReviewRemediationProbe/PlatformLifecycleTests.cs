using BotAgent.Adapters.Platforms;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Management;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Qq;
using BotAgent.Services.OneBot;
using BotAgent.Services.Platforms;
using BotAgent.Services.Qq;
using BotAgent.Services.Local;
using BotAgent.Platforms;
using BotAgent.Platforms.Net;
using BotAgent.Adapters.Platforms.Feishu;
using BotAgent.Adapters.Time;
using BotAgent.Adapters.Persistence;
using BotAgent.Services.Official;
using BotAgent.Services.Reply;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Permissions;
using BotAgent.Services.Ops;
using BotAgent.Services.Conversations;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotAgent.ReviewRemediationProbe;

internal static class PlatformLifecycleTests
{
    internal static async Task<int?> TryRunChildAsync(string[] args)
    {
        if (args.Length != 1 || args[0] != "--platform-lifecycle") return null;
        var syntheticRoot = Path.Combine(Path.GetTempPath(), "botagent-platform-lifecycle-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", syntheticRoot);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", syntheticRoot);
        FileLog.WriteToFile = false;
        FileLog.Verbose = false;
        ClockBindings.InitializePlatforms();
        var failures = 0;
        var checks = 0;
        try
        {
            await RunAsync((passed, name) =>
            {
                checks++;
                if (!passed) failures++;
                Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
            });
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine($"FAIL platform lifecycle exception_type={ex.GetType().Name}");
        }
        Console.WriteLine($"platform lifecycle checks={checks} failures={failures}");
        return failures == 0 ? 0 : 1;
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var caseDistinctConversation = new ConversationId(PlatformId.QqPrivate, "LEGACY", ConversationKind.GroupChat, "10001");
        var caseDistinctKey = ConversationIdCodec.Encode(caseDistinctConversation);
        check(caseDistinctKey != "group:10001" && ConversationIdCodec.TryParse(caseDistinctKey, out var caseRoundTrip)
            && caseRoundTrip == caseDistinctConversation
            && ConversationIdCodec.Encode(caseDistinctConversation with { AccountScope = AccountScope.Legacy }) == "group:10001",
            "scope LEGACY remains a distinct structured identity while canonical legacy preserves the old raw key");
        var readOnlyMap = new OfficialIdMap(null, Channels.AliasBase, Channels.AliasBase + 1000, durable: false);
        check(!readOnlyMap.TryGetAlias("synthetic-missing", out _) && readOnlyMap.Count == 0,
            "read-only alias lookup never allocates a missing identity");
        var knownAlias = readOnlyMap.AliasFor("synthetic-existing");
        check(readOnlyMap.TryGetAlias("synthetic-existing", out var lookedUp) && lookedUp == knownAlias && readOnlyMap.Count == 1
            && !readOnlyMap.TryGetAlias("SYNTHETIC-EXISTING", out _) && readOnlyMap.Count == 1,
            "read-only alias lookup preserves exact identity and leaves the map unchanged");
        var originTarget = new ConversationId(PlatformId.Local, "account-A", ConversationKind.GroupChat, "10001");
        var originRef = new MessageRef(originTarget, "101");
        var origin = new PlatformMessageOrigin(new DriverKey(PlatformId.Local, "account-A", "instance-A"), originRef);
        var images = new List<string> { "https://example.com/synthetic.png" };
        var envelope = new PlatformInboundEnvelope(origin, new(originRef, new(PlatformId.Local, "account-A", "20002"),
            "synthetic", "synthetic", DateTimeOffset.UnixEpoch, ImageUrls: images));
        images.Clear();
        check(envelope.Message.ImageUrls?.Count == 1, "inbound envelope defensively freezes image collection");
        check(envelope.ToString() == "PlatformInboundEnvelope" && origin.ToString() == "PlatformMessageOrigin", "origin diagnostics do not print identifiers or content");
        check(ThrowsArgument(() => new PlatformMessageOrigin(new(PlatformId.Local, "account-B", "instance-A"), originRef)),
            "trigger cannot claim another account");
        check(ThrowsArgument(() => new PlatformInboundEnvelope(origin, envelope.Message with { Sender = new(PlatformId.QqOfficial, "account-A", "20002") })),
            "sender realm must match trusted origin");
        check(ThrowsArgument(() => new PlatformInboundEnvelope(origin, envelope.Message with { Ref = originRef with { NativeMessageId = "102" } })),
            "normal envelope must bind exact trigger reference");
        check(new PlatformMessageOrigin(origin.Driver).Trigger is null, "poke origin may omit a nonexistent stable reference");
        foreach (var platform in new[] { PlatformId.QqPrivate, PlatformId.QqOfficial, PlatformId.Local, PlatformId.Feishu })
        {
            var source = new SyntheticSource();
            var context = new PlatformContext(platform, "account-A", "instance-A");
            using var adapter = new QqChatSourcePlatformAdapter(source, context, PlatformCapabilities.Local, "synthetic", "test");
            var target = new ConversationId(platform, context.AccountScope, ConversationKind.GroupChat, "10001");
            check((await adapter.SendAsync(context, new(target, "synthetic"))).IsSuccess && source.Sends == 1, "text dispatch on registered source");
            check(!(await adapter.SendAsync(context with { InstanceId = "instance-B" }, new(target, "synthetic"))).IsSuccess && source.Sends == 1,
                "different instance rejected without fallback");
            check(!(await adapter.SendAsync(context with { AccountScope = "account-a" }, new(target, "synthetic"))).IsSuccess && source.Sends == 1,
                "account identity is ordinal");
            check(!(await adapter.SendAsync(context, new(target with { ThreadId = "thread" }, "synthetic"))).IsSuccess && source.Sends == 1,
                "unsupported thread is rejected");
            check(!(await adapter.SendAsync(context, new(target, "", ImageData: new byte[] { 1 }))).IsSuccess && source.Images == 0,
                "unsupported media does not produce IO");
            var quoted = await adapter.GetQuotedMessageAsync(new(target with { AccountScope = "account-B" }, "101"));
            check(quoted.Text is null && source.Quotes == 0, "quote cannot borrow another account");
            InboundMessage? received = null;
            adapter.InboundReceived += message => received = message;
            source.Emit(new QqChatMessage(101, true, 20002, 10001, "synthetic", "synthetic", DateTimeOffset.UnixEpoch, true,
                Channel: Channels.Official));
            check(received?.Ref.Conversation.PlatformId == platform && received.Ref.Conversation.AccountScope == context.AccountScope,
                "inbound identity comes from adapter not message body channel");
            received = null;
            source.Emit(new QqChatMessage(0, true, 20002, 10001, "synthetic", "synthetic", DateTimeOffset.UnixEpoch, true));
            check(received is null, "normal message requires stable reference");
            adapter.Dispose();
            source.Emit(new QqChatMessage(102, true, 20002, 10001, "synthetic", "synthetic", DateTimeOffset.UnixEpoch, true));
            check(received is null && !(await adapter.SendAsync(context, new(target, "synthetic"))).IsSuccess,
                "disposed source rejects callbacks and sends");
        }

        var lifecycleSource = new SyntheticSource();
        using var lifecycleAdapter = new QqChatSourcePlatformAdapter(lifecycleSource,
            new(PlatformId.Local, AccountScope.Legacy, ""), PlatformCapabilities.Local, "synthetic", "test");
        var starts = 0;
        var stops = 0;
        var registration = new PlatformDriverRegistration(lifecycleAdapter,
            _ => { starts++; return Task.CompletedTask; }, _ => { stops++; return Task.CompletedTask; });
        var operations = new PlatformDriverOperations(new[] { registration });
        var originalLifetime = registration.Lifetime;
        var unsupported = await operations.DispatchAsync(registration.Key, DriverCommand.TestConnection);
        check(unsupported.Code == ManagedResultCode.UnsupportedAction && starts == 0 && stops == 0,
            "unsupported driver test has no side effects");
        check((await operations.DispatchAsync(registration.Key, DriverCommand.Stop)).State == ManagedOperationState.Completed
            && originalLifetime.IsCancellationRequested && !registration.IsActive && stops == 1,
            "stop retires origin lifetime and awaits cleanup");
        await operations.DispatchAsync(registration.Key, DriverCommand.Stop);
        check(stops == 1, "repeated stop does not repeat lifecycle effect");
        await operations.DispatchAsync(registration.Key, DriverCommand.Start);
        check(starts == 1 && registration.IsActive && !registration.Lifetime.IsCancellationRequested && originalLifetime.IsCancellationRequested,
            "restart creates new lifetime without reviving old origin");
        check((await operations.DispatchAsync(new DriverKey(PlatformId.Local, AccountScope.Legacy, "other"), DriverCommand.Stop)).Code == ManagedResultCode.NotFound
            && stops == 1, "unregistered instance cannot dispatch to default instance");

        using var failedAdapter = new QqChatSourcePlatformAdapter(new SyntheticSource(),
            new(PlatformId.Local, AccountScope.Legacy, "failed-start"), PlatformCapabilities.Local, "synthetic", "test");
        var failedStarts = 0;
        var failedStops = 0;
        var failCleanup = true;
        var failedRegistration = new PlatformDriverRegistration(failedAdapter,
            _ => { failedStarts++; throw new OperationCanceledException(); },
            _ => { failedStops++; if (failCleanup) throw new InvalidOperationException("synthetic"); return Task.CompletedTask; },
            initiallyRunning: false);
        var failedOperations = new PlatformDriverOperations(new[] { failedRegistration });
        check(!failedRegistration.IsActive && failedRegistration.Lifetime.IsCancellationRequested,
            "unstarted registration has no usable source lease");
        var failedStart = await failedOperations.DispatchAsync(failedRegistration.Key, DriverCommand.Start);
        check(failedStart.State == ManagedOperationState.Unknown && failedRegistration.Lifetime.IsCancellationRequested
            && failedStops == 1 && !failedRegistration.IsActive, "cancelled start awaits real cleanup and retains unknown on cleanup failure");
        await failedOperations.DispatchAsync(failedRegistration.Key, DriverCommand.Start);
        check(failedStarts == 1, "unknown cleanup cannot stack another start");
        failCleanup = false;
        check((await failedOperations.DispatchAsync(failedRegistration.Key, DriverCommand.Stop)).State == ManagedOperationState.Completed
            && failedStops == 2, "explicit stop resolves failed-start cleanup before another start");

        using var local = new LocalChannelSource();
        var localCallbacks = 0;
        local.MessageReceived += _ => localCallbacks++;
        local.Inject(true, 10001, "synthetic", "synthetic");
        await local.StopAsync();
        check(!local.IsConnected && !(await local.SendTextAsync(true, 10001, "synthetic")).Ok, "real local source stops outbound work");
        check(ThrowsInvalid(() => local.Inject(true, 10001, "synthetic", "synthetic")) && localCallbacks == 1,
            "real local source refuses ingress while stopped");
        await local.StartAsync();
        local.Inject(true, 10001, "synthetic", "synthetic");
        check(localCallbacks == 2 && local.IsConnected, "real local source restart has one callback");

        var transports = new List<SyntheticTransport>();
        using var oneBot = new OneBotGateway(new PlatformSettingsBox(new PlatformOptions()), transportFactory: _ =>
        {
            var transport = new SyntheticTransport();
            transports.Add(transport);
            return transport;
        });
        await oneBot.StartAsync();
        check((await oneBot.SendTextAsync(true, 10001, "synthetic")).Ok && transports[0].Sends == 1,
            "real OneBot action response sends through injected transport");
        await oneBot.StopAsync();
        check(transports[0].Stopped && !(await oneBot.SendTextAsync(true, 10001, "synthetic")).Ok,
            "real OneBot stop prevents new actions");
        await oneBot.StartAsync();
        check(transports.Count == 2 && (await oneBot.SendTextAsync(true, 10001, "synthetic")).Ok && transports[1].Sends == 1,
            "real OneBot restart replaces retired transport once");

        var http = new SyntheticHttpFetcher();
        var nativeSettings = new PlatformOptions { FeishuEnabled = true, FeishuAppId = "synthetic-app",
            FeishuAppSecret = "synthetic-secret", FeishuVerificationToken = "synthetic-verification", FeishuWhitelist = "*",
            FeishuApiBase = "https://example.com", OfficialEnabled = true, OfficialAppId = "synthetic-app",
            OfficialAppSecret = "synthetic-secret" };
        var settingsBox = new PlatformSettingsBox(nativeSettings);
        using var feishu = new FeishuBotGateway(settingsBox, http);
        var nativeTarget = new ConversationId(PlatformId.Feishu, AccountScope.Default, ConversationKind.GroupChat, "synthetic-chat");
        check((await feishu.SendAsync(feishu.Context, new(nativeTarget, "synthetic"))).IsSuccess,
            "real Feishu native target sends through injected HTTP");
        var requests = http.Requests;
        check(!(await feishu.SendAsync(feishu.Context with { InstanceId = "other" }, new(nativeTarget, "synthetic"))).IsSuccess
            && http.Requests == requests, "real Feishu instance mismatch produces no HTTP");
        check(!(await feishu.SendAsync(feishu.Context, new(nativeTarget, "", ImageData: new byte[] { 1 }))).IsSuccess
            && http.Requests == requests, "real Feishu unsupported media produces no HTTP");
        nativeSettings.FeishuEnabled = false;
        check(!(await feishu.SendAsync(feishu.Context, new(nativeTarget, "synthetic"))).IsSuccess
            && http.Requests == requests, "real Feishu disabled policy blocks HTTP");
        nativeSettings.FeishuEnabled = true;
        await feishu.StopAsync();
        check(!(await feishu.SendAsync(feishu.Context, new(nativeTarget, "synthetic"))).IsSuccess
            && http.Requests == requests, "real Feishu stopped source produces no HTTP");
        await feishu.StartAsync();
        check((await feishu.SendAsync(feishu.Context, new(nativeTarget, "synthetic"))).IsSuccess,
            "real Feishu restart reopens native messenger");

        var officialIds = new OfficialIdMap(null, Channels.AliasBase, Channels.AliasBase + 1000, durable: false);
        var officialTarget = officialIds.AliasFor("synthetic-openid");
        using var official = new OfficialBotGateway(settingsBox, http, officialIds, _ => { });
        check((await official.SendTextAsync(true, officialTarget, "synthetic")).Ok,
            "real official API send uses injected HTTP and existing alias map");
        var officialContext = new PlatformContext(PlatformId.QqOfficial, AccountScope.Legacy, "");
        using var officialAdapter = new QqChatSourcePlatformAdapter(official, officialContext, PlatformCapabilities.QqOfficial, "synthetic", "test");
        requests = http.Requests;
        check(!(await officialAdapter.SendAsync(officialContext,
            new(new(PlatformId.QqOfficial, AccountScope.Legacy, ConversationKind.GroupChat, officialTarget.ToString()), "", VoiceUrl: "https://example.com/synthetic"))).IsSuccess
            && http.Requests == requests, "official unsupported voice does not dispatch HTTP");
        await official.StopAsync();
        check(!(await official.SendTextAsync(true, officialTarget, "synthetic")).Ok && http.Requests == requests,
            "real official stop prevents new API sends");

        await VerifyTrustedRouterAsync(check);
        await VerifyIdentityInvalidationAsync(check);
        await VerifyNativeFeishuRouterAsync(check);
        await VerifyFeishuDispatchRevocationAsync(check);
    }

    private static async Task VerifyTrustedRouterAsync(Action<bool, string> check)
    {
        var first = new SyntheticSource();
        var second = new SyntheticSource();
        var third = new SyntheticSource();
        using var firstAdapter = new QqChatSourcePlatformAdapter(first,
            new(PlatformId.QqPrivate, AccountScope.Legacy, "instance-A"), PlatformCapabilities.QqOneBot, "synthetic", "test");
        using var secondAdapter = new QqChatSourcePlatformAdapter(second,
            new(PlatformId.QqPrivate, AccountScope.Legacy, "instance-B"), PlatformCapabilities.QqOneBot, "synthetic", "test");
        using var thirdAdapter = new QqChatSourcePlatformAdapter(third,
            new(PlatformId.QqPrivate, "account-other", "instance-C"), PlatformCapabilities.QqOneBot, "synthetic", "test");
        var firstDriver = new PlatformDriverRegistration(firstAdapter, _ => Task.CompletedTask, _ => Task.CompletedTask);
        var secondDriver = new PlatformDriverRegistration(secondAdapter, _ => Task.CompletedTask, _ => Task.CompletedTask);
        var thirdDriver = new PlatformDriverRegistration(thirdAdapter, _ => Task.CompletedTask, _ => Task.CompletedTask);
        var drivers = new PlatformDriverOperations(new[] { firstDriver, secondDriver, thirdDriver });
        var registry = new PlatformRegistry(new[] { firstAdapter, secondAdapter, thirdAdapter }, new[] { firstAdapter, secondAdapter, thirdAdapter });
        using var router = new ChannelRouter(new[] { first, second, third }, registry, drivers);
        var records = new SyntheticConversationRepository(new ConversationRecord { SourceKey = "group:10001", Kind = "GroupChat", Name = "synthetic",
            Messages = new() { new() { Role = "Peer", Text = "synthetic", QqMessageId = 88 } } });
        var conversations = new ConversationRegistry(records, new SettingsBox(new AppSettings()), router, _ => true, _ => { });
        conversations.Restore();
        var models = new List<BotConversation?>();
        var delivered = new List<QqChatMessage>();
        var leases = new List<PlatformSourceLease>();
        var sends = new List<Task<SendResult>>();
        TaskCompletionSource? delay = null;
        async Task<SendResult> SendReplyAsync(Task? wait)
        {
            if (wait is not null) await wait;
            return await router.SendTextAsync(true, 10001, "synthetic");
        }
        router.MessageReceived += message =>
        {
            delivered.Add(message);
            models.Add(conversations.GetOrCreate(message));
            if (PlatformSourceExecution.Current is { } source) leases.Add(source);
            sends.Add(SendReplyAsync(delay?.Task));
        };
        QqChatMessage Message(long id) => new(id, true, 20002, 10001, "synthetic", "synthetic",
            DateTimeOffset.UnixEpoch, true, Channel: Channels.Official);
        first.Emit(Message(101));
        second.Emit(Message(102));
        check((await sends[0]).Ok && (await sends[1]).Ok && first.Sends == 1 && second.Sends == 1,
            "actual router routes same-account replies to their captured exact instances");
        check(delivered.Count == 2 && leases.Count == 2 && delivered.All(message => message.Channel == Channels.Private)
            && delivered[0].Envelope?.Origin.Driver == firstDriver.Key && delivered[1].Envelope?.Origin.Driver == secondDriver.Key,
            "actual rich callback overwrites body channel and binds one authenticated origin per delivery");
        check(delivered[0].Envelope?.Message.Ref.Conversation == delivered[1].Envelope?.Message.Ref.Conversation,
            "two source instances share conversation identity but not source leases");
        check(models.Count == 2 && models[0] is not null && ReferenceEquals(models[0], models[1])
            && models[0]!.SourceKey == "group:10001" && models[0]!.Messages.Count == 1 && conversations.Snapshot().Count == 1,
            "actual ConversationRegistry reuses one restored legacy key and history for same-account source instances");
        var settings = new SettingsBox(new AppSettings
        {
            DeclaredPlatformDrivers = ImmutableArray.Create(firstDriver.Key, secondDriver.Key),
            RuntimePlatformPolicies = ImmutableArray.Create(
                new PlatformPolicyEntry(new PolicyTarget.AccountDefault(PlatformId.QqPrivate, AccountScope.Legacy),
                    new(ToggleRule.Enabled, ToggleRule.Enabled, ListRule.AllowAll(), ListRule.AllowAll(),
                        new(ActionRuleKind.Explicit, ImmutableArray<ActionId>.Empty), ImmutableArray<FeatureOverride>.Empty)),
                new PlatformPolicyEntry(new PolicyTarget.Instance(secondDriver.Key),
                    new(ToggleRule.Inherit, ToggleRule.Disabled, ListRule.Inherit(), ListRule.Inherit(),
                        new(ActionRuleKind.Inherit, ImmutableArray<ActionId>.Empty), ImmutableArray<FeatureOverride>.Empty)))
        });
        var policies = new PlatformPolicyResolver(settings, registry);
        var whitelist = new WhitelistGate(settings, policies);
        check(whitelist.AllowsMessage(leases[0].Context, delivered[0]) && !whitelist.AllowsMessage(leases[1].Context, delivered[1])
            && policies.Resolve(leases[0].Context).ChatEnabled && !policies.Resolve(leases[1].Context).ChatEnabled,
            "actual source whitelist and policy allow A but do not grant same-account instance B authority");
        check(!policies.ResolveForChannel(Channels.Private).ChatEnabled,
            "channel-only policy cannot choose an instance when same-account policies differ");
        check(PlatformSourceExecution.Current is null && router.CaptureForConversation("group:10001") is null
            && !(await router.SendTextAsync(true, 10001, "synthetic")).Ok && first.Sends == 1 && second.Sends == 1,
            "ambiguous autonomous sends cannot borrow either same-account instance");
        third.Emit(Message(150));
        check(models[^1] is { } differentAccount && differentAccount.SourceKey != "group:10001"
            && ConversationIdCodec.TryParse(differentAccount.SourceKey, out var differentIdentity)
            && differentIdentity.AccountScope == "account-other" && conversations.Snapshot().Count == 2
            && !ReferenceEquals(models[0], differentAccount),
            "actual ConversationRegistry does not merge identical numeric targets across account scopes");

        var queue = new TenantReplyQueue();
        queue.Enqueue(new(101, false, false, leases[0]), 1);
        var offer = queue.Enqueue(new(102, false, false, leases[1]), 1);
        check(offer == PendingReplyOffer.RejectedCapacity && queue.TryDequeue(out var retained)
            && ReferenceEquals(retained.Source, leases[0]) && retained.TriggerMessageId == 101,
            "full ordinary queue explicitly rejects B without claiming coalescing or replacing A origin");
        queue.Enqueue(new(101, false, false, leases[0]), 1);
        check(queue.Enqueue(new(102, false, true, leases[1]), 1) == PendingReplyOffer.PreservedOverflow && queue.Count == 2,
            "direct instance-B round preserves both origins instead of deleting instance-A round");
        var boundedQueue = new TenantReplyQueue();
        for (var index = 0; index < 256; index++) boundedQueue.Enqueue(new(index + 1, false, true, leases[index % 2]), 1);
        check(boundedQueue.Count == 256 && boundedQueue.Enqueue(new(257, false, true, leases[1]), 1) == PendingReplyOffer.RejectedCapacity
            && boundedQueue.Count == 256, "must-preserve queue has a real 256-round hard limit with explicit rejection");

        delay = new(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Emit(Message(103));
        var oldSend = sends[^1];
        var oldLease = leases[^1];
        await drivers.DispatchAsync(firstDriver.Key, DriverCommand.Stop);
        var callbacks = delivered.Count;
        first.Emit(Message(104));
        check(delivered.Count == callbacks && !oldLease.IsCurrent && oldLease.Lifetime.IsCancellationRequested,
            "stopped actual source detaches callback and retires captured host lease");
        await drivers.DispatchAsync(firstDriver.Key, DriverCommand.Start);
        delay.SetResult();
        check(!(await oldSend).Ok && first.Sends == 1 && second.Sends == 1,
            "old asynchronous reply cannot adopt a new lease after same-instance restart or fallback to peer");
        delay = null;
        first.Emit(Message(105));
        check(delivered.Count == callbacks + 1 && (await sends[^1]).Ok && first.Sends == 2 && second.Sends == 1,
            "restart restores exactly one callback and fresh reply stays on source instance");
        var freshLease = leases[^1];
        var generationQueue = new TenantReplyQueue();
        generationQueue.Enqueue(new(103, false, false, oldLease), 1);
        check(generationQueue.Enqueue(new(105, false, false, freshLease), 1) != PendingReplyOffer.CoalescedOld,
            "same driver key in a new lifetime cannot coalesce a retired generation");

        var bridge = new AgentBridgeServer(settings, _ => { });
        AgentTask? agentTask = null;
        void CaptureAgentTask(QqChatMessage message)
        {
            if (PlatformSourceExecution.Current?.Origin.Driver == firstDriver.Key)
                agentTask = bridge.NewTask("group:10001", "synthetic", "synthetic-session");
        }
        router.MessageReceived += CaptureAgentTask;
        first.Emit(Message(106));
        router.MessageReceived -= CaptureAgentTask;
        check(agentTask?.SourceLease?.Origin.Driver == firstDriver.Key,
            "actual AgentBridge NewTask explicitly captures the original authenticated source");
        PlatformSourceLease? finishedSource = null;
        PlatformSourceLease? restoredSecondSource = null;
        Task<SendResult>? completionReply = null;
        bridge.Finished += _ =>
        {
            finishedSource = PlatformSourceExecution.Current;
            completionReply = router.SendTextAsync(true, 10001, "synthetic");
        };
        void CompleteFromSecondSource(QqChatMessage message)
        {
            if (PlatformSourceExecution.Current?.Origin.Driver != secondDriver.Key || agentTask is null) return;
            bridge.TryEnqueue(agentTask); // No connected device: real synchronous completion callback, no external I/O.
            restoredSecondSource = PlatformSourceExecution.Current;
        }
        router.MessageReceived += CompleteFromSecondSource;
        var firstSends = first.Sends;
        second.Emit(Message(107));
        check(ReferenceEquals(finishedSource, agentTask?.SourceLease) && completionReply is not null && (await completionReply).Ok
            && first.Sends == firstSends + 1 && restoredSecondSource?.Origin.Driver == secondDriver.Key,
            "AgentTask finished callback restores original A origin instead of borrowing callback flow B, then restores B");
        if (agentTask is not null)
        {
            var wire = JsonSerializer.SerializeToNode(agentTask)!.AsObject();
            check(!wire.ContainsKey(nameof(AgentTask.SourceLease)), "AgentTask source lease is not exposed in serialized task bodies");
            wire[nameof(AgentTask.SourceLease)] = "synthetic-untrusted-source";
            check(wire.Deserialize<AgentTask>()?.SourceLease is null, "task body cannot inject an authenticated source lease");
        }
        await drivers.DispatchAsync(firstDriver.Key, DriverCommand.Stop);
        await drivers.DispatchAsync(firstDriver.Key, DriverCommand.Start);
        firstSends = first.Sends;
        second.Emit(Message(108));
        check(completionReply is not null && !(await completionReply).Ok && first.Sends == firstSends,
            "late AgentTask completion keeps its retired original lease and cannot send after source restart");
        router.MessageReceived -= CompleteFromSecondSource;

        settings.Apply(next => next.EnableApprovals = true);
        var approvalReplies = 0;
        PlatformSourceLease? approvalReplySource = null;
        async Task ApprovalReplyAsync()
        {
            approvalReplies++;
            approvalReplySource = PlatformSourceExecution.Current;
            await router.SendTextAsync(true, 10001, "synthetic");
        }
        var hooks = new ApprovalHooks(_ => { }, (_, _) => "synthetic", (_, _) => Task.CompletedTask,
            (_, _) => ApprovalReplyAsync(), (_, _) => ApprovalReplyAsync());
        var approvals = new ApprovalUseCase(settings, hooks, new SessionPolicyLedger(), new TurnTraceStore());
        var conversation = new BotConversation { SourceKey = "group:10001", Name = "synthetic", Kind = ConversationKind.GroupChat };
        Task<bool>? opened = null;
        void OpenApproval(QqChatMessage message)
        {
            if (PlatformSourceExecution.Current?.Origin.Driver == firstDriver.Key)
                opened = approvals.OpenApprovalForTool(conversation, ApprovalFlow.FixedToolId, message.MessageId, approvals.Capabilities);
        }
        router.MessageReceived += OpenApproval;
        first.Emit(Message(109));
        router.MessageReceived -= OpenApproval;
        check(opened is not null && await opened && approvals.PendingApprovals().Count == 1,
            "actual ApprovalUseCase records a pending request under source A");
        if (approvals.PendingApprovals().Count == 1)
        {
            var requestId = approvals.PendingApprovals()[0].RequestId;
            void DecideApproval(QqChatMessage message) => approvals.HandleCommand(message);
            router.MessageReceived += DecideApproval;
            second.Emit(Message(110) with { Text = "同意 " + requestId, SenderRole = "admin" });
            router.MessageReceived -= DecideApproval;
            check(approvals.PendingApprovals().Count == 1 && approvalReplies == 0,
                "same-conversation instance B approver cannot authorize or consume A request");
            var decision = approvals.PanelDecide(requestId, true);
            check(decision.Ok && approvalReplies == 1 && approvalReplySource?.Origin.Driver == firstDriver.Key,
                "Panel approval callback restores original A source rather than selecting an arbitrary peer");
        }
        router.MessageReceived += OpenApproval;
        first.Emit(Message(111));
        router.MessageReceived -= OpenApproval;
        if (approvals.PendingApprovals().Count == 1)
        {
            var requestId = approvals.PendingApprovals()[0].RequestId;
            await drivers.DispatchAsync(firstDriver.Key, DriverCommand.Stop);
            await drivers.DispatchAsync(firstDriver.Key, DriverCommand.Start);
            var beforeReplies = approvalReplies;
            var decision = approvals.PanelDecide(requestId, true);
            check(!decision.Ok && decision.ReasonCode == "source_retired" && approvalReplies == beforeReplies,
                "approval created before source restart cannot borrow a new lease for Panel execution");
        }
    }

    private static async Task VerifyFeishuDispatchRevocationAsync(Action<bool, string> check)
    {
        foreach (var revokeIdentity in new[] { false, true })
        {
            var settings = new PlatformOptions { FeishuEnabled = true, FeishuAppId = "synthetic-app",
                FeishuAppSecret = "synthetic-secret", FeishuVerificationToken = "synthetic-verification",
                FeishuWhitelist = "*", FeishuApiBase = "https://example.com" };
            var http = new SyntheticHttpFetcher { BlockToken = true };
            using var gateway = new FeishuBotGateway(new PlatformSettingsBox(settings), http);
            var target = new ConversationId(PlatformId.Feishu, AccountScope.Default, ConversationKind.GroupChat, "synthetic-chat");
            var sending = gateway.SendAsync(gateway.Context, new(target, "synthetic"));
            await http.TokenRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (revokeIdentity) settings.FeishuAppId = "synthetic-other-app";
            else settings.FeishuWhitelist = "";
            http.ReleaseToken.SetResult();
            check(!(await sending).IsSuccess && http.MessageRequests == 0,
                revokeIdentity ? "Feishu dispatch rechecks identity after token await and sends no message on change"
                    : "Feishu dispatch rechecks current whitelist after token await and sends no message on revocation");
        }
    }

    private static async Task VerifyIdentityInvalidationAsync(Action<bool, string> check)
    {
        var source = new SyntheticSource();
        using var adapter = new QqChatSourcePlatformAdapter(source,
            new(PlatformId.QqPrivate, AccountScope.Legacy, "instance-A"), PlatformCapabilities.QqOneBot, "synthetic", "test");
        var registration = new PlatformDriverRegistration(adapter, _ => Task.CompletedTask, _ => Task.CompletedTask);
        var drivers = new PlatformDriverOperations(new[] { registration });
        var registry = new PlatformRegistry(new[] { adapter }, new[] { adapter });
        var identityCurrent = true;
        using var router = new ChannelRouter(new[] { source }, registry, drivers, identityIsCurrent: _ => identityCurrent);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PlatformSourceLease? captured = null;
        Task<SendResult>? pending = null;
        var callbacks = 0;
        async Task<SendResult> ReplyAsync()
        {
            await release.Task;
            return await router.SendTextAsync(true, 10001, "synthetic");
        }
        router.MessageReceived += _ => { callbacks++; captured = PlatformSourceExecution.Current; pending = ReplyAsync(); };
        source.Emit(new(101, true, 20002, 10001, "synthetic", "synthetic", DateTimeOffset.UnixEpoch, true));
        identityCurrent = false;
        release.SetResult();
        check(captured is not null && !captured.IsCurrent && pending is not null && !(await pending).Ok && source.Sends == 0,
            "runtime identity binding invalidates an already waiting reply before real source dispatch");
        await drivers.DispatchAsync(registration.Key, DriverCommand.Stop);
        await drivers.DispatchAsync(registration.Key, DriverCommand.Start);
        source.Emit(new(102, true, 20002, 10001, "synthetic", "synthetic", DateTimeOffset.UnixEpoch, true));
        check(callbacks == 1 && router.CaptureForConversation("group:10001") is null
            && !(await router.SendTextAsync(true, 10001, "synthetic")).Ok && source.Sends == 0,
            "driver restart cannot bypass a changed configured identity or create an autonomous source lease");
    }

    private static async Task VerifyNativeFeishuRouterAsync(Action<bool, string> check)
    {
        var settings = new AppSettings { FeishuEnabled = true, FeishuAppId = "synthetic-app",
            FeishuAppSecret = "synthetic-secret", FeishuVerificationToken = "synthetic-verification",
            FeishuWhitelist = "synthetic-native-chat", FeishuApiBase = "https://example.com",
            DeclaredPlatformDrivers = ImmutableArray.Create(new DriverKey(PlatformId.Feishu, AccountScope.Default, "feishu-main")),
            RuntimePlatformPolicies = ImmutableArray.Create(new PlatformPolicyEntry(
                new PolicyTarget.AccountDefault(PlatformId.Feishu, AccountScope.Default),
                new(ToggleRule.Enabled, ToggleRule.Enabled, ListRule.Explicit("synthetic-native-chat"), ListRule.Explicit("synthetic-user"),
                    new(ActionRuleKind.Explicit, ImmutableArray<ActionId>.Empty), ImmutableArray<FeatureOverride>.Empty))) };
        var http = new SyntheticHttpFetcher();
        var box = new SettingsBox(settings);
        var ids = new FeishuIdMap();
        using var gateway = new FeishuBotGateway(box, http, null, ids, new SyntheticDedupStore());
        var nativeConversation = new ConversationId(PlatformId.Feishu, AccountScope.Default, ConversationKind.GroupChat, "synthetic-native-chat");
        check(!gateway.TryGetTargetAlias(nativeConversation, out _) && !ids.TryGetAlias("synthetic-app", "group", "synthetic-native-chat", out _),
            "native conversation projection cannot create a compatibility identity before authenticated ingress");
        var driver = new PlatformDriverRegistration(gateway, gateway.StartAsync, gateway.StopAsync);
        var drivers = new PlatformDriverOperations(new[] { driver });
        var registry = new PlatformRegistry(new[] { gateway }, new[] { gateway });
        using var router = new ChannelRouter(new[] { gateway }, registry, drivers);
        var conversations = new ConversationRegistry(new SyntheticConversationRepository(), box, router, _ => true, _ => { });
        BotConversation? tracked = null;
        var received = new List<QqChatMessage>();
        Task<SendResult>? reply = null;
        router.MessageReceived += message =>
        {
            received.Add(message);
            tracked = conversations.GetOrCreate(message);
            reply = router.SendTextAsync(true, message.GroupId, "synthetic");
        };
        var body = JsonSerializer.Serialize(new
        {
            token = "synthetic-verification",
            header = new { event_id = "synthetic-event", event_type = "im.message.receive_v1" },
            @event = new
            {
                sender = new { sender_id = new { open_id = "synthetic-user" } },
                message = new { message_id = "synthetic-native-message", chat_id = "synthetic-native-chat", chat_type = "group",
                    content = "{\"text\":\"synthetic\"}" }
            }
        });
        var first = await gateway.HandleWebhookAsync(body, null, null, null);
        var duplicate = await gateway.HandleWebhookAsync(body, null, null, null);
        check(first.StatusCode == 200 && duplicate.StatusCode == 200 && received.Count == 1,
            "native Feishu source emits once through actual Host rich subscription and dedup port");
        check(received.Count == 1 && received[0].Envelope is { } envelope
            && envelope.Origin.Driver == driver.Key && envelope.Origin.Trigger == envelope.Message.Ref
            && envelope.Message.Ref.NativeMessageId == "synthetic-native-message"
            && envelope.Message.Ref.Conversation.NativeTargetId == "synthetic-native-chat"
            && envelope.Message.Sender.NativeUserId == "synthetic-user",
            "native Feishu ref and sender survive compatibility alias mapping without reconstruction");
        check(reply is not null && (await reply).Ok && http.MessageRequests == 1,
            "native Feishu actual Host callback reply uses captured source and existing alias binding");
        check(received.Count == 1 && gateway.TryGetTargetAlias(nativeConversation, out var mapped) && mapped == received[0].GroupId
            && !gateway.TryGetTargetAlias(nativeConversation with { AccountScope = "other-account" }, out _)
            && !gateway.TryGetTargetAlias(nativeConversation with { Kind = ConversationKind.PrivateChat }, out _)
            && !gateway.TryGetTargetAlias(nativeConversation with { ThreadId = "synthetic-thread" }, out _),
            "native target projection uses only an existing same-account same-kind identity and rejects unsupported threads");
        check(received.Count == 1 && gateway.TryGetTargetConversation(true, received[0].GroupId, out var nativeRestored)
            && nativeRestored == nativeConversation && !gateway.TryGetTargetConversation(false, received[0].GroupId, out _)
            && !gateway.TryGetTargetConversation(true, received[0].GroupId + 500000, out _),
            "legacy alias restoration requires an existing forward-and-reverse same-app same-kind identity proof");
        var whitelist = new WhitelistGate(box, new PlatformPolicyResolver(box, registry), gateway.AllowsMappedTarget);
        check(received.Count == 1 && whitelist.AllowsMessage(gateway.Context, received[0]),
            "exact-source Host whitelist resolves native raw Feishu target through the real identity map");
        check(received.Count == 1 && tracked is not null && tracked.SourceKey == ConversationIdCodec.Encode(nativeConversation)
            && tracked.Target == (true, received[0].GroupId),
            "actual ConversationRegistry stores native Feishu key while projecting the verified compatibility target");
        if (received.Count == 1)
        {
            var oldKey = Channels.Key(Channels.Feishu, true, received[0].GroupId);
            var legacyRecords = new SyntheticConversationRepository(new ConversationRecord { SourceKey = oldKey, Kind = "GroupChat", Name = "synthetic",
                Messages = new() { new() { Role = "Peer", Text = "synthetic", QqMessageId = 88 } } });
            var legacyConversations = new ConversationRegistry(legacyRecords, box, router, _ => true, _ => { });
            legacyConversations.Restore();
            var restored = legacyConversations.Find(oldKey);
            BotConversation? reused = null;
            void RestoreLegacyKey(QqChatMessage message) => reused = legacyConversations.GetOrCreate(message);
            router.MessageReceived += RestoreLegacyKey;
            var nextBody = JsonNode.Parse(body)!.AsObject();
            nextBody["header"]!["event_id"] = "synthetic-event-2";
            nextBody["event"]!["message"]!["message_id"] = "synthetic-native-message-2";
            await gateway.HandleWebhookAsync(nextBody.ToJsonString(), null, null, null);
            router.MessageReceived -= RestoreLegacyKey;
            check(restored is not null && ReferenceEquals(restored, reused) && reused.SourceKey == oldKey
                && reused.Messages.Count == 1 && legacyConversations.Snapshot().Count == 1,
                "actual native Feishu ingress reuses a restored legacy key and history only after existing alias proof");
        }
        box.Apply(next => next.RuntimePlatformPolicies = ImmutableArray.Create(new PlatformPolicyEntry(
            new PolicyTarget.AccountDefault(PlatformId.Feishu, AccountScope.Default),
            new(ToggleRule.Enabled, ToggleRule.Enabled, ListRule.Explicit("synthetic-other-chat"), ListRule.Explicit("synthetic-user"),
                new(ActionRuleKind.Explicit, ImmutableArray<ActionId>.Empty), ImmutableArray<FeatureOverride>.Empty))));
        check(received.Count >= 1 && !whitelist.AllowsMessage(gateway.Context, received[0]),
            "exact-source Host whitelist does not reuse native mapping to bypass current raw-target revocation");
        box.Apply(next => next.FeishuAppId = "synthetic-other-app");
        check(!gateway.TryGetTargetAlias(nativeConversation, out _) && !ids.TryGetAlias("synthetic-other-app", "group", "synthetic-native-chat", out _),
            "native target projection does not reuse another app identity or allocate on miss");
        check(received.Count >= 1 && !gateway.TryGetTargetConversation(true, received[0].GroupId, out _),
            "legacy alias restoration cannot adopt a binding owned by another app");
    }

    private static bool ThrowsArgument(Action action)
    {
        try { action(); return false; } catch (ArgumentException) { return true; }
    }

    private static bool ThrowsInvalid(Action action)
    {
        try { action(); return false; } catch (InvalidOperationException) { return true; }
    }

    private sealed class SyntheticSource : IQqChatSource
    {
        public int Sends { get; private set; }
        public int Images { get; private set; }
        public int Quotes { get; private set; }
        public bool IsConnected => true;
        public event Action<QqChatMessage>? MessageReceived;
        public event Action<QqPokeEvent>? Poked { add { } remove { } }
        public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
        public event Action<bool>? ConnectionChanged { add { } remove { } }
        public void Emit(QqChatMessage message) => MessageReceived?.Invoke(message);
        public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default,
            long? replyToMessageId = null, bool directAddress = false)
        {
            Sends++;
            return Task.FromResult(new SendResult(true, 101));
        }
        public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
        {
            Images++;
            return Task.FromResult(true);
        }
        public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
        {
            Quotes++;
            return Task.FromResult<(string?, long)>(("synthetic", 20002));
        }
        public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class SyntheticTransport : IOneBotTransport
    {
        public int Sends { get; private set; }
        public bool Stopped { get; private set; }
        public bool IsConnected => !Stopped;
        public event Action<string>? OnText;
        public event Action<bool>? OnStateChanged { add { } remove { } }
        public Task StartAsync() => Task.CompletedTask;
        public void Stop() => Stopped = true;
        public void Dispose() => Stop();
        public Task SendActionAsync(string action, string paramsJson, string echo, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Sends++;
            OnText?.Invoke(JsonSerializer.Serialize(new { status = "ok", retcode = 0, echo, data = new { message_id = 101 } }));
            return Task.CompletedTask;
        }
    }

    private sealed class SyntheticDedupStore : IWebhookDedupStore
    {
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
        public bool TryRegister(IReadOnlyList<string> keys, DateTimeOffset seenAt, TimeSpan ttl)
        {
            if (keys.Any(_keys.Contains)) return false;
            foreach (var key in keys) _keys.Add(key);
            return true;
        }
    }

    private sealed class SyntheticConversationRepository(params ConversationRecord[] records) : IConversationRepository
    {
        public List<ConversationRecord> LoadAsync() => records.ToList();
        public void RequestSave(IEnumerable<ConversationRecord> values) { }
        public void DeleteConversation(string sourceKey) { }
        public void AppendArchive(string sourceKey, IReadOnlyList<ChatMessage> evicted) { }
        public List<ArchivedMessage> ReadArchive(string sourceKey, int limit) => new();
        public long ArchiveCount(string sourceKey) => 0;
        public void DeleteMessages(string sourceKey) { }
    }

    private sealed class SyntheticHttpFetcher : IPlatformHttpFetcher
    {
        public int Requests { get; private set; }
        public int MessageRequests { get; private set; }
        public bool BlockToken { get; init; }
        public TaskCompletionSource TokenRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseToken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        private Task<HttpResponseMessage> Reply(CancellationToken ct, bool token)
        {
            ct.ThrowIfCancellationRequested();
            Requests++;
            var json = token ? "{\"code\":0,\"tenant_access_token\":\"synthetic-token\",\"access_token\":\"synthetic-token\",\"expire\":7200,\"expires_in\":7200}"
                : "{\"code\":0,\"id\":\"synthetic-message\",\"data\":{\"message_id\":\"synthetic-message\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            MessageRequests++;
            return Reply(ct, false);
        }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken ct = default)
            => SendAsync(request, ct);
        public async Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
        {
            TokenRequested.TrySetResult();
            if (BlockToken) await ReleaseToken.Task.WaitAsync(ct);
            return await Reply(ct, true);
        }
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => throw new NotSupportedException("synthetic_no_network");
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption completionOption, CancellationToken ct = default) => throw new NotSupportedException("synthetic_no_network");
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => throw new NotSupportedException("synthetic_no_network");
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption completionOption, CancellationToken ct = default) => throw new NotSupportedException("synthetic_no_network");
    }
}
