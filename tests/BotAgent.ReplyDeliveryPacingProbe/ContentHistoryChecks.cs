using System.Reflection;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using BotAgent.Services.Conversations;
using BotAgent.Services.Ops;
using BotAgent.Services.Panel;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;

static class ContentHistoryChecks
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        var failures = 0;
        void Check(string label, bool value)
        {
            try { check(label, value); }
            catch (InvalidOperationException) { failures++; Console.WriteLine("CONTENT_FAIL " + label); }
        }
        foreach (var mode in new[] { "single", "split", "backoff" })
        {
            var settings = new TestSettings(); settings.Current.SplitReplies = mode == "split";
            var source = new SyntheticSource { TextResult = call => new(true, 10001 + call)
                { Delivery = DeliveryResult.Degraded((10001 + call).ToString(), "link"), DeliveredText = "actual-" + call } };
            var store = new ContentOwnMessages(); var ledger = new OwnMessageLedger(store, _ => { });
            var plain = new PlainSender(settings, source, null!, null!, ledger, _ => { }, new TurnTraceStore(),
                riskBackoff: mode == "backoff" ? new TestRiskBackoff { Active = true } : null);
            var report = await plain.SendWithCadenceAsync("group:10001", true, 10001, "originalfirst。originalsecond。", null, directAddress: true);
            var expected = mode == "split" ? new[] { "actual-1", "actual-2" } : new[] { "actual-1" };
            Check("confirmed cadence text matches transport " + mode, report.AllSent && report.TextWasTransformed && report.SentSegments.SequenceEqual(expected));
            Check("own reference ledger matches transport " + mode, store.Texts.SequenceEqual(expected)
                && ledger.TryGet("group:10001", 10002, out var own) && own.Text == "actual-1");
        }
        foreach (var success in new[] { true, false })
        {
            var settings = new TestSettings();
            var source = new SyntheticSource { TextResult = _ => new(true, 10002)
                { Delivery = success ? DeliveryResult.Degraded("10002", "link") : DeliveryResult.Unknown("synthetic_unknown"), DeliveredText = "actual-agent" } };
            var store = new ContentOwnMessages(); var ledger = new OwnMessageLedger(store, _ => { });
            var registry = new ConversationRegistry(DispatchProxy.Create<IConversationRepository, ContentConversationStore>(), settings, source, _ => true, _ => { });
            var logs = new List<string>();
            var plain = new PlainSender(settings, source, registry, DispatchProxy.Create<IPanelNotifier, ContentPanel>(), ledger, logs.Add, new TurnTraceStore());
            var conversation = new BotConversation { SourceKey = "group:10001", Kind = ConversationKind.GroupChat };
            await plain.SendPlainAsync(conversation, "original-agent");
            Check("agent history uses confirmed text only " + success, success
                ? conversation.Messages.Single().Text == "actual-agent" && store.Texts.SequenceEqual(new[] { "actual-agent" })
                : conversation.Messages.Count == 0 && store.Texts.Count == 0);
            Check("agent delivery logs omit body " + success, logs.All(line => !line.Contains("original-agent") && !line.Contains("actual-agent")));
        }
        if (failures > 0) throw new InvalidOperationException("content checks failed=" + failures);
    }
}
sealed class ContentOwnMessages : IOwnMessageRepository
{
    public List<string> Texts { get; } = new();
    public int MaxEntries => 10;
    public List<OwnMessage> LoadRecent(int max) => new();
    public void Upsert(long id, string text, DateTimeOffset at) => throw new InvalidOperationException("bare_message_id");
    public void Upsert(MessageRef messageRef, string text, DateTimeOffset at) => Texts.Add(text);
    public void PruneTo(int max) { }
}
public class ContentConversationStore : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => method?.Name == "RequestSave" ? null : throw new InvalidOperationException("unexpected_conversation_store_call");
}
public class ContentPanel : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => method?.Name == "NotifyMessageAdded" ? null : throw new InvalidOperationException("unexpected_panel_call");
}
