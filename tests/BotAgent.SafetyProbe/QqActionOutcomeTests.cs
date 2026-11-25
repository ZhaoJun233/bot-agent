using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Tools;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;
using BotAgent.Services.Tools;

namespace BotAgent.SafetyProbe;

public static partial class Program
{
    private static void QqActionOutcomeTests()
    {
        Section("QQ action structured outcomes through real host and Agent");
        foreach (var spec in QqActionCatalog.All)
        {
            var actions = new OutcomeActions();
            var host = new SessionQqActionHost(actions, true, 10001, 20002, 30003, 40004, pluginAllows: _ => true);
            var result = host.ExecuteAsync(new ToolCall("qq." + spec.Name, ValidActionArgs())).GetAwaiter().GetResult();
            Check("false gateway receipt: " + spec.Name, !result.Ok && result.ReasonCode == "action_failed" && actions.Calls == 1,
                $"ok={result.Ok} reason={result.ReasonCode} calls={actions.Calls}");
        }

        foreach (var (action, group, args) in new[]
        {
            ("like", true, new JsonObject()), ("send", true, new JsonObject()),
            ("ban", false, ValidActionArgs()), ("recall", true, new JsonObject())
        })
        {
            var actions = new OutcomeActions();
            var host = new SessionQqActionHost(actions, group, 10001, 20002, 30003, 40004);
            var result = host.ExecuteAsync(new ToolCall("qq." + action, args)).GetAwaiter().GetResult();
            Check("invalid action arguments: " + action, !result.Ok && result.ReasonCode == "invalid_arguments" && actions.Calls == 0);
        }
        foreach (var gate in new Func<string, bool>?[] { null, _ => false })
        {
            var actions = new OutcomeActions();
            var host = new SessionQqActionHost(actions, true, 10001, 20002, 30003, 40004, pluginAllows: gate);
            var result = host.ExecuteAsync(new ToolCall("qq.poke", ValidActionArgs())).GetAwaiter().GetResult();
            Check("plugin denial is a failed outcome", !result.Ok && result.ReasonCode == "plugin_denied" && actions.Calls == 0);
        }
        foreach (var (status, reason) in new[]
        {
            (DeliveryStatus.Unknown, "delivery_unknown"), (DeliveryStatus.PartialSuccess, "delivery_partial"),
            (DeliveryStatus.Rejected, "delivery_rejected")
        })
        {
            var actions = new OutcomeActions { Send = new SendResult(true, 30003) { Delivery = new(status, ReasonCode: "synthetic_reason") } };
            var host = new SessionQqActionHost(actions, true, 10001, 20002, 30003, 40004);
            var result = host.ExecuteAsync(new ToolCall("qq.send", ValidActionArgs())).GetAwaiter().GetResult();
            Check("delivery status overrides legacy bool: " + status, !result.Ok && result.ReasonCode == reason && actions.Calls == 1);
        }
        {
            var enabled = true;
            var actions = new OutcomeActions { Accepted = true };
            var host = new SessionQqActionHost(actions, true, 10001, 20002, 30003, 40004, pluginAllows: _ => enabled);
            var first = host.ExecuteAsync(new ToolCall("qq.poke", ValidActionArgs())).GetAwaiter().GetResult();
            enabled = false;
            var second = host.ExecuteAsync(new ToolCall("qq.poke", ValidActionArgs())).GetAwaiter().GetResult();
            var legacy = host.ExecuteAsync(QqActionCatalog.Find("poke")!, ValidActionArgs(), CancellationToken.None).GetAwaiter().GetResult();
            Check("plugin policy is reread by both entrypoints", first.Ok && !second.Ok && second.ReasonCode == "plugin_denied"
                && legacy.Contains("插件") && actions.Calls == 1);
        }
        {
            var actions = new OutcomeActions { Accepted = true };
            var source = new OutcomeSource();
            var current = true;
            var lease = new PlatformSourceLease(source, source,
                new PlatformMessageOrigin(new DriverKey(PlatformId.QqPrivate, AccountScope.Legacy, "synthetic")), CancellationToken.None, () => current);
            SessionQqActionHost host;
            using (PlatformSourceExecution.Enter(lease)) host = new(actions, true, 10001, 20002, 30003, 40004);
            current = false;
            var result = host.ExecuteAsync(new ToolCall("qq.like", ValidActionArgs())).GetAwaiter().GetResult();
            Check("retired captured source cannot act", !result.Ok && result.ReasonCode == "source_retired" && actions.Calls == 0);
        }
        {
            var actions = new OutcomeActions { Send = new SendResult(true, 30003) { Delivery = DeliveryResult.Degraded("synthetic-id", "music") } };
            var host = new SessionQqActionHost(actions, true, 10001, 20002, 30003, 40004);
            var result = host.ExecuteAsync(new ToolCall("qq.send", ValidActionArgs())).GetAwaiter().GetResult();
            Check("degraded send remains explicit", result.Ok && result.ReasonCode == "degraded" && result.ModelText!.Contains("降级") && actions.Calls == 1);
        }
        {
            var actions = new OutcomeActions { Accepted = true };
            var host = new SessionQqActionHost(actions, true, 10001, 20002, 30003, 40004);
            var result = host.ExecuteAsync(new ToolCall("qq.like", ValidActionArgs())).GetAwaiter().GetResult();
            Check("successful action executes once", result.Ok && result.ReasonCode == "ok" && actions.Calls == 1);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = false;
            try { host.ExecuteAsync(new ToolCall("qq.like", ValidActionArgs()), cancellation.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            Check("caller cancellation never executes action", cancelled && actions.Calls == 1);
        }

        var oldLog = FileLog.WriteToFile;
        FileLog.WriteToFile = false;
        try
        {
            RunStructuredAgent(ToolOutcome.Failure("action_not_allowed", "synthetic denied"), "未执行的 QQ 动作：");
            RunStructuredAgent(ToolOutcome.Failure("delivery_unknown", "synthetic uncertain"), "执行未成功的 QQ 动作：");
            RunStructuredAgent(ToolOutcome.Failure("delivery_unknown", "synthetic uncertain"), "执行未成功的 QQ 动作：", repeat: true);
            RunStructuredAgent(ToolOutcome.Failure("delivery_partial", "synthetic partial"), "执行未成功的 QQ 动作：");
            RunStructuredAgent(ToolOutcome.Success("❌ synthetic text is data"), null);
        }
        finally { FileLog.WriteToFile = oldLog; }
    }

    private static JsonObject ValidActionArgs() => new()
    {
        ["user_id"] = "sender", ["message_id"] = "this", ["text"] = "synthetic content",
        ["target"] = "本群", ["name"] = "synthetic", ["card"] = "synthetic"
    };

    private static void RunStructuredAgent(ToolOutcome outcome, string? heading, bool repeat = false)
    {
        var call = "{\"tool\":\"qq\",\"action\":\"like\",\"user_id\":\"sender\"}";
        var replies = new Queue<string>(repeat ? [call, call, "{\"final\":\"synthetic final\"}"] : [call, "{\"final\":\"synthetic final\"}"]);
        var fetcher = new FakeHttpFetcher { OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = replies.Dequeue() } } } }))
        }) };
        var box = new SettingsBox(new AppSettings
        {
            ApiKey = "synthetic-key", Model = "synthetic-model", ModelBaseUrl = "https://example.invalid/v1",
            AgentServerModel = "synthetic-model", AgentServerTools = "qq", AgentServerMaxSteps = 3
        });
        var client = new OpenAiClient(box, fetcher, new FakeHttpFetcher(), new FakeImageDownloader(), new FakeModelTransport());
        var host = new OutcomeHost(outcome);
        var logs = new List<string>();
        var runner = new ServerAgentRunner(box, client, new FakeHttpFetcher(), logs.Add);
        var task = new AgentTask { Id = "synthetic", SourceKey = "group:10001", Prompt = "synthetic request", Session = "synthetic", QqHost = host };
        runner.RunAsync(task, CancellationToken.None).GetAwaiter().GetResult();
        Check("Agent consumes status without parsing text: " + outcome.ReasonCode + (repeat ? " repeated" : ""), task.Ok && host.StructuredCalls == 1 && host.TextCalls == 0
            && logs.All(line => !line.Contains(outcome.ModelText!))
            && (!repeat || task.Text!.Contains("未执行的 QQ 动作："))
            && (heading is null ? !task.Text!.Contains("QQ 动作：") : task.Text!.Contains(heading) && task.Text.Contains(outcome.ModelText!)),
            $"ok={task.Ok} structured={host.StructuredCalls} legacy={host.TextCalls} text={task.Text}");
    }

    private sealed class OutcomeHost(ToolOutcome result) : IQqActionHost, IToolExecutor
    {
        public string ContextLine => "synthetic context";
        public string Id => QqToolSpecs.ExecutorActions;
        public string Implementation => "synthetic";
        public bool LegacyPath => false;
        public int StructuredCalls { get; private set; }
        public int TextCalls { get; private set; }
        public Task<ToolOutcome> ExecuteAsync(ToolCall call, CancellationToken ct = default) { StructuredCalls++; return Task.FromResult(result); }
        public Task<string> ExecuteAsync(QqActionSpec spec, JsonObject args, CancellationToken ct) { TextCalls++; return Task.FromResult(result.ModelText!); }
    }

    private sealed class OutcomeActions : IQqActions
    {
        public int Calls { get; private set; }
        public bool Accepted { get; init; }
        public SendResult Send { get; init; }
        private Task<bool> Act() { Calls++; return Task.FromResult(Accepted); }
        public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null) { Calls++; return Task.FromResult(Send); }
        public Task<bool> SendPokeAsync(bool isGroup, long targetId, long userId, CancellationToken ct = default) => Act();
        public Task<bool> SendLikeAsync(long userId, int times, CancellationToken ct = default) => Act();
        public Task<bool> SetMessageEmojiLikeAsync(long messageId, string emojiId, CancellationToken ct = default) => Act();
        public Task<bool> DeleteMessageAsync(long messageId, CancellationToken ct = default) => Act();
        public Task<bool> SetGroupBanAsync(long groupId, long userId, int seconds, CancellationToken ct = default) => Act();
        public Task<bool> SetGroupKickAsync(long groupId, long userId, bool rejectAdd, CancellationToken ct = default) => Act();
        public Task<bool> SetGroupCardAsync(long groupId, long userId, string card, CancellationToken ct = default) => Act();
        public Task<bool> SetGroupNameAsync(long groupId, string groupName, CancellationToken ct = default) => Act();
        public Task<bool> SetGroupLeaveAsync(long groupId, bool dismiss, CancellationToken ct = default) => Act();
    }

    private sealed class OutcomeSource : IQqChatSource, IPlatformSourceOwner
    {
        public event Action<QqChatMessage>? MessageReceived { add { } remove { } }
        public event Action<QqPokeEvent>? Poked { add { } remove { } }
        public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
        public event Action<bool>? ConnectionChanged { add { } remove { } }
        public bool IsConnected => true;
        public string? ConversationKey(QqChatMessage message, Func<string, bool> exists) => null;
        public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null, bool directAddress = false) => throw new InvalidOperationException();
        public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null) => throw new InvalidOperationException();
        public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default) => throw new InvalidOperationException();
    }
}
