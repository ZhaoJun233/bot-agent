using System.Text.Json.Nodes;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Conversation;
using BotAgent.Services;
using BotAgent.Services.Conversations;
using BotAgent.Services.Local;
using BotAgent.Services.Model;
using BotAgent.Services.Ops;
using BotAgent.Services.Panel;
using BotAgent.Services.Reply;

namespace BotAgent.SafetyProbe;

public static partial class Program
{
    private static void SilenceReplyTests()
    {
        Section("沉默占位 / 内部参与判断 / 正常正文保护（合成输入）");
        var notices = new[]
        {
            "（不发言）", "(不发言)", "不发言", "（本轮无需介入）", "本轮无需介入",
            "（当前话题由其他人处理）", "（暂时旁听）", "（和我无关，保持沉默）",
            "（提到的是其他人，本轮不参与）", "（没有点名，也不知道答案，不接话）",
            "（@的是管理员，不关我的事）", "（与我无关 安静）", "（没有叫我，不接）",
            "  （本轮无需回应。）  ", "（不发言）。", "（不回复）\n（暂时旁听）"
        };
        foreach (var text in notices)
        {
            foreach (var action in new string?[] { null, "reply" })
            {
                var envelope = new JsonObject { ["suitability"] = 90, ["reply"] = text,
                    ["speak"] = "合成语音", ["sticker"] = "synthetic", ["search"] = "合成检索" };
                if (action is not null) envelope["action"] = action;
                var parsed = ModelOutputParser.Parse(envelope.ToJsonString(), false).Result;
                Check("整句内部判断零正文且无附带动作", parsed.Action == ReplyAction.Silent && parsed.Reply is null &&
                    parsed.Speak is null && parsed.StickerId is null && parsed.Search is null && !parsed.Malformed,
                    $"action={action ?? "legacy"}; reason={parsed.ReasonCode}");
            }
            Check("纯文本路径同样拦截内部判断", ModelOutputParser.Parse(text, false).Result.Reply is null);
        }
        foreach (var text in new[]
        {
            "这是一个例子：“（不发言）”。", "（示例说明：单位为厘米）", "（我不知道答案）",
            "我不知道答案", "我不会帮助你做这件事。", "这件事与我无关。", "安静", "（不接电话）",
            "他说本轮无需介入", "本轮并非无需介入", "（本轮无需介入）但我有个建议。",
            "（不发言）\n合成正常正文。", "（不发言）是什么意思？", "（不发言）？",
            "（你不发言）", "（请保持沉默）", "\u0060（不发言）\u0060", "\u0060\u0060\u0060text\n（不发言）\n\u0060\u0060\u0060",
            "（x ∈ R）", "2exp(i·2kπ/5)", "https://example.com/path", "【SILENT】",
            "（本轮无需介入", "(不发言）", "（不是不发言）", "（不发言）（单位为厘米）"
        })
        {
            var raw = new JsonObject { ["reply"] = text }.ToJsonString();
            var parsed = ModelOutputParser.Parse(raw, false).Result;
            Check("引用/拒绝/解释/代码不被沉默规则误删", parsed.Reply == text && parsed.Action == ReplyAction.Reply, text);
        }
        SilenceDeliveryTests();
        var request = new PromptBuilder.PromptRequest(
            SystemPrompt: "合成系统提示", BotIdentity: null, Persona: null, AiDesire: 0,
            Window: Array.Empty<ChatMessage>(), QuotableIds: Array.Empty<long>(), ProfilesText: null,
            Stickers: null, PokeContext: false, Proactive: false, MoodText: null, MusicText: null,
            LinkText: null, RecallText: null, GroupRolesText: null, VibeHint: null, SearchText: null,
            SuitabilityThreshold: 10, EnableListen: false, EnableVoice: false, VoiceMaxChars: 30,
            VoiceEagerness: 50, EnableWebSearch: false, EnableAsk: false, EnableToolRequest: false);
        foreach (var isPrivate in new[] { false, true })
        {
            var prompt = PromptBuilder.Build(request with { IsPrivateConversation = isPrivate });
            Check("群聊/私聊在动作开关关闭时也说明沉默协议", prompt.Contains("\"action\":\"silent\",\"reply\":\"\"") &&
                prompt.Contains("内部参与判断") && !prompt.Contains("[可选的结构化动作]"));
        }
    }

    private static void SilenceDeliveryTests()
    {
        var settings = new SettingsBox(new AppSettings { FilterActionNarration = true, SplitReplies = true, SegmentDelayMs = 0 });
        using var source = new LocalChannelSource();
        var registry = new ConversationRegistry(new FakeConversationRepository(), settings, source, _ => true, _ => { });
        var calls = 0;
        var filterResult = "{\"actionIds\":[0],\"reply\":\"\"}";
        var model = new FakeModelClient { ChatCompletion = _ => { calls++; return Task.FromResult<string?>(filterResult); } };
        var sender = new PlainSender(settings, source, registry, new PanelNotifier(),
            new OwnMessageLedger(new FakeOwnMessageRepository(), _ => { }), _ => { }, new TurnTraceStore(), model: model);
        foreach (var replacement in new[] { "", "本轮无需介入", "[SILENT]" })
        {
            filterResult = new JsonObject { ["actionIds"] = new JsonArray(0), ["reply"] = replacement }.ToJsonString();
            var before = source.Outbox.Count;
            var report = sender.SendWithCadenceAsync(true, 10001, "*点头*", null).GetAwaiter().GetResult();
            Check("真实发送入口：合法删空及改写为沉默均零发送", source.Outbox.Count == before && report.SentSegments.Count == 0);
        }
        Check("旁白检查仍每次只调用一次模型", calls == 3);
        settings.Apply(s => s.FilterActionNarration = false);
        foreach (var split in new[] { true, false })
        {
            settings.Apply(s => s.SplitReplies = split);
            var before = source.Outbox.Count;
            var report = sender.SendWithCadenceAsync(true, 10001, "（本轮无需介入）", null).GetAwaiter().GetResult();
            Check("关闭旁白过滤/分句也不外发内部判断", source.Outbox.Count == before && report.SentSegments.Count == 0);
        }
        Check("内部判断不增加辅助模型请求", calls == 3);
    }
}
