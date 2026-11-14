using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Tools;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.OneBot;
using BotAgent.Services.Platforms;
using BotAgent.Services.Qq;

namespace BotAgent.SafetyProbe;

public static partial class Program
{
    private static void OfficialActionTests()
    {
        Section("Official action policy and captured source boundaries");
        foreach (var group in new[] { true, false })
        {
            var fixture = new OfficialActionFixture(group);
            var result = fixture.Send(new JsonObject { ["text"] = "synthetic" });
            Check("official send uses captured source and scope: " + group, result.Ok && fixture.Source.Calls == 1
                && fixture.Source.Lease == fixture.Lease && fixture.Source.Group == group && fixture.Source.Target == 8000000000000001);
            Check("official send restores ambient source", PlatformSourceExecution.Current is null);
            foreach (var args in new[]
            {
                new JsonObject { ["text"] = "synthetic", ["target_id"] = "10001" },
                new JsonObject { ["text"] = "synthetic", ["target"] = "8000000000000002" },
                new JsonObject { ["text"] = "synthetic", ["app_id"] = "synthetic-other" },
                new JsonObject { ["text"] = "synthetic", ["role"] = "admin" },
                new JsonObject { ["text"] = "synthetic", ["platform"] = PlatformId.QqPrivate },
                new JsonObject { ["text"] = "", ["content"] = "synthetic" },
                new JsonObject { ["text"] = 42 },
            })
            {
                var denied = fixture.Send(args);
                Check("official forged or ambiguous arguments have no effect", !denied.Ok && denied.ReasonCode == "invalid_arguments"
                    && fixture.Source.Calls == 1, denied.ReasonCode);
            }
        }
        foreach (var setting in new[] { "", "send", "all", "*", "unknown-action" })
        {
            var fixture = new OfficialActionFixture(true);
            fixture.Box.Apply(settings => settings.AgentServerQqActions = setting);
            var allowed = setting is "send" or "all" or "*";
            var result = fixture.Send(new JsonObject { ["text"] = "synthetic" });
            Check("official baseline authorization: " + setting, result.Ok == allowed && fixture.Source.Calls == (allowed ? 1 : 0));
        }
        foreach (var change in new Action<OfficialActionFixture>[]
        {
            f => f.Box.Apply(s => s.RuntimePlatformPolicies = [new(new PolicyTarget.Instance(f.Driver), new(
                ToggleRule.Inherit, ToggleRule.Inherit, ListRule.Inherit(), ListRule.Inherit(),
                new(ActionRuleKind.Explicit, []), []))]),
            f => f.Box.Apply(s => s.OfficialEnabled = false),
            f => f.Box.Apply(s => s.OfficialChatEnabled = false),
            f => f.Box.Apply(s => s.AgentServerQqActions = "unknown-action"),
            f => f.Registry.Snapshot = f.Registry.Snapshot with { Connected = false },
            f => f.Registry.Snapshot = f.Registry.Snapshot with { AccountScope = "other-account" },
            f => f.Registry.Snapshot = f.Registry.Snapshot with { InstanceId = "other-instance" },
            f => f.Registry.Snapshot = f.Registry.Snapshot with { Capabilities = PlatformCapabilities.QqOfficial with { SupportsText = false } },
            f => f.Box.Apply(s => s.RuntimePlatformPolicies = [new(new PolicyTarget.Instance(f.Driver), new(
                ToggleRule.Inherit, ToggleRule.Inherit, ListRule.Inherit(), ListRule.Inherit(),
                new(ActionRuleKind.Inherit, []), [new(PlatformFeatureId.Text, false)]))]),
            f => f.Current = false,
        })
        {
            var fixture = new OfficialActionFixture(true);
            var before = fixture.Send(new JsonObject { ["text"] = "first" });
            change(fixture);
            var after = fixture.Send(new JsonObject { ["text"] = "second" });
            Check("official authorization is reread before execution", before.Ok && !after.Ok && fixture.Source.Calls == 1, after.ReasonCode);
        }
        var unsupported = new OfficialActionFixture(true);
        unsupported.Box.Apply(s => s.AgentServerQqActions = "all");
        foreach (var spec in QqActionCatalog.All.Where(spec => !QqActionCatalog.Supports(spec, PlatformId.QqOfficial)))
        {
            var result = unsupported.Host.ExecuteAsync(new ToolCall("qq." + spec.Name, ValidActionArgs())).GetAwaiter().GetResult();
            Check("all cannot invent official capability: " + spec.Name, !result.Ok && result.ReasonCode == "platform_action_unsupported"
                && unsupported.Source.Calls == 0);
        }
        var unknown = new OfficialActionFixture(true);
        unknown.Source.Result = new SendResult(true) { Delivery = new(DeliveryStatus.Unknown, ReasonCode: "synthetic_unknown") };
        var uncertain = unknown.Send(new JsonObject { ["text"] = "synthetic" });
        Check("official Unknown remains failed without a second send", !uncertain.Ok && uncertain.ReasonCode == "delivery_unknown" && unknown.Source.Calls == 1);
        OfficialActionPromptTests();
        OfficialRecallActionTests();
    }

    private static void OfficialActionPromptTests()
    {
        foreach (var enabled in new[] { true, false })
        {
            var fixture = new OfficialActionFixture(true);
            fixture.Box.Apply(s =>
            {
                s.ApiKey = "synthetic-key"; s.Model = "synthetic-model"; s.ModelBaseUrl = "https://example.invalid/v1";
                s.AgentServerModel = "synthetic-model"; s.AgentServerTools = "qq"; s.AgentServerMaxSteps = 1;
                if (!enabled) s.RuntimePlatformPolicies = [new(new PolicyTarget.Instance(fixture.Driver), new(
                    ToggleRule.Inherit, ToggleRule.Inherit, ListRule.Inherit(), ListRule.Inherit(),
                    new(ActionRuleKind.Explicit, []), []))];
            });
            var prompt = "";
            var fetcher = new FakeHttpFetcher { OnSend = async (request, ct) =>
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
                prompt = body?["messages"]?[0]?["content"]?.GetValue<string>() ?? "";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { choices = new[] { new { message = new { content = "{\"final\":\"synthetic\"}" } } } })) };
            } };
            var client = new OpenAiClient(fixture.Box, fetcher, new FakeHttpFetcher(), new FakeImageDownloader(), new FakeModelTransport());
            var runner = new ServerAgentRunner(fixture.Box, client, new FakeHttpFetcher(), _ => { });
            var task = new AgentTask { Id = "synthetic", SourceKey = "official:group:8000000000000001",
                Prompt = "synthetic", Session = "synthetic", QqHost = fixture.Host };
            var oldLog = FileLog.WriteToFile;
            FileLog.WriteToFile = false;
            try { runner.RunAsync(task, CancellationToken.None).GetAwaiter().GetResult(); }
            finally { FileLog.WriteToFile = oldLog; }
            Check("Agent prompt lists only actions allowed by current policy: " + enabled, task.Ok && prompt.Length > 0
                && prompt.Contains("• send：") == enabled && !prompt.Contains("• ban：") && !prompt.Contains("• like：")
                && (enabled || !prompt.Contains("qq=")), $"ok={task.Ok}, promptLength={prompt.Length}");
            Check("building the action prompt causes no transport effects", fixture.Source.Calls == 0);
        }
    }

    private sealed class OfficialActionFixture
    {
        public readonly DriverKey Driver = new(PlatformId.QqOfficial, AccountScope.Legacy, "synthetic-instance");
        public readonly OfficialActionSource Source = new();
        public readonly OfficialActionRegistry Registry;
        public readonly SettingsBox Box;
        public readonly PlatformSourceLease Lease;
        public readonly OfficialSessionQqActionHost Host;
        public bool Current = true;

        public OfficialActionFixture(bool group)
        {
            Registry = new(new(Driver.PlatformId, Driver.AccountScope, "synthetic", "synthetic", true, true, PlatformCapabilities.QqOfficial) { InstanceId = Driver.InstanceId });
            Box = new(new AppSettings { OfficialEnabled = true, OfficialChatEnabled = true, AgentServerQqActions = "send",
                DeclaredPlatformDrivers = [Driver], DeclaredPlatformActions = QqActionCatalog.All.Select(s => new ActionId(s.Name)).ToImmutableArray(),
                RuntimePlatformPolicies = [] });
            var resolver = new PlatformPolicyResolver(Box, Registry, options => QqActionCatalog.ParseAllowed(((AppSettings)options).AgentServerQqActions)
                .Select(action => new ActionId(action)).ToImmutableArray());
            var conversation = new ConversationId(Driver.PlatformId, Driver.AccountScope,
                group ? ConversationKind.GroupChat : ConversationKind.PrivateChat, "8000000000000001");
            Lease = new(Source, Source, new(Driver, new(conversation, "synthetic-trigger")), CancellationToken.None, () => Current);
            Host = new(Lease, group, 8000000000000001, resolver);
        }

        public ToolOutcome Send(JsonObject args) => Host.ExecuteAsync(new ToolCall("qq.send", args)).GetAwaiter().GetResult();
        public ToolOutcome Recall(JsonObject args) => Host.ExecuteAsync(new ToolCall("qq.recall", args)).GetAwaiter().GetResult();
    }

    private sealed class OfficialActionRegistry(PlatformStatusSnapshot snapshot) : IPlatformRegistry
    {
        public PlatformStatusSnapshot Snapshot = snapshot;
        public IReadOnlyList<IPlatformAdapter> Adapters => [];
        public IPlatformAdapter? GetAdapter(string platformId, string accountScope = AccountScope.Default) => null;
        public IPlatformMessenger? GetMessenger(string platformId, string accountScope = AccountScope.Default) => null;
        public IReadOnlyList<PlatformStatusSnapshot> GetSnapshots() => [Snapshot];
    }

    private sealed class OfficialActionSource : IQqChatSource, IPlatformSourceOwner
    {
        public event Action<QqChatMessage>? MessageReceived { add { } remove { } }
        public event Action<QqPokeEvent>? Poked { add { } remove { } }
        public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
        public event Action<bool>? ConnectionChanged { add { } remove { } }
        public string Channel => Channels.Official;
        public bool IsConnected => true;
        public int Calls;
        public int RecallCalls;
        public long RecalledMessage;
        public DeliveryResult RecallResult = DeliveryResult.Ok();
        public bool Group;
        public long Target;
        public PlatformSourceLease? Lease;
        public SendResult Result = new(true, 8000000000000003);
        public string? ConversationKey(QqChatMessage message, Func<string, bool> exists) => null;
        public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null, bool directAddress = false)
        {
            ct.ThrowIfCancellationRequested();
            Calls++; Group = isGroup; Target = targetId; Lease = PlatformSourceExecution.Current;
            return Task.FromResult(Result);
        }
        public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<DeliveryResult> RecallOwnMessageAsync(bool isGroup, long targetId, long messageId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RecallCalls++; Group = isGroup; Target = targetId; RecalledMessage = messageId; Lease = PlatformSourceExecution.Current;
            return Task.FromResult(RecallResult);
        }
        public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null) => throw new InvalidOperationException();
        public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default) => throw new InvalidOperationException();
    }
}
