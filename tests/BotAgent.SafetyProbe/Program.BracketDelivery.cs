using BotAgent.Services;
using BotAgent.Services.Conversations;
using BotAgent.Services.Local;
using BotAgent.Services.Ops;
using BotAgent.Services.Panel;
using BotAgent.Services.Reply;

namespace BotAgent.SafetyProbe;

public static partial class Program
{
    private static void BracketDeliveryTests()
    {
        Section("自然语言括号确定性过滤 / 技术内容保护（真实发送入口，合成数据）");
        var box = new SettingsBox(new AppSettings { FilterActionNarration = true, SplitReplies = false });
        using var source = new LocalChannelSource();
        var registry = new ConversationRegistry(new FakeConversationRepository(), box, source, _ => true, _ => { });
        var calls = 0;
        string? filterOutput = null;
        var model = new FakeModelClient { ChatCompletion = _ => { calls++; return Task.FromResult(filterOutput); } };
        PlainSender Sender(bool withModel) => new(box, source, registry, new PanelNotifier(),
            new OwnMessageLedger(new FakeOwnMessageRepository(), _ => { }), _ => { }, new TurnTraceStore(),
            model: withModel ? model : null);

        var cases = new (string Input, string Expected)[]
        {
            ("（大家在讨论行程并请教领队，我没有补充的，不出声）", ""),
            ("(还在比较另外两种方案，先等等看)", ""),
            ("（一般性的补充说明）", ""),
            ("(This is not my question, I will wait 42 seconds)", ""),
            ("（还有一种完全不同的新说法）\n(another unrelated aside)", ""),
            ("开头（补充说明）结尾", "开头结尾"),
            ("开头（外层说明（内层补充）仍是旁白）结尾", "开头结尾"),
            ("正文（中途换行\n仍是旁白）", "正文"),
            ("正文（未闭合的旁白", "正文"),
            ("正文(括号类型不一致）", "正文"),
            ("（说明里提到 sin(x) 但仍属于自然语言）", ""),
            ("（旁白附带 https://example.com/reference）", ""),
            ("（旁白附带 \u0060code\u0060）", ""),
            ("正文" + string.Concat(Enumerable.Repeat("（未见过的说明）", 20)), "正文"),
            ("（" + new string('文', 7000) + "）", ""),
            ("这是 (beta) 版本", "这是  版本"),
            ("OK（普通补充说明）正文", "OK正文"),
            ("公式 2exp(i·2kπ/5) 和 sin((x+y)/2)", "公式 2exp(i·2kπ/5) 和 sin((x+y)/2)"),
            ("Console.WriteLine() 和 f(x)", "Console.WriteLine() 和 f(x)"),
            ("面积为 (长 * 宽)，周长为 2*(长+宽)", "面积为 (长 * 宽)，周长为 2*(长+宽)"),
            ("（x ∈ R） (x + y) (1)", "（x ∈ R） (x + y) (1)"),
            ("https://example.com/a_(b)?q=(x)", "https://example.com/a_(b)?q=(x)"),
            ("查看（https://example.com）", "查看（https://example.com）"),
            ("[资料](https://example.com/a_(b))", "资料 (https://example.com/a_(b))"),
            ("$$f(x)=x*(x+1)$$", "$$f(x)=x*(x+1)$$"),
            ("\\(f(x) = x^2\\)", "\\(f(x) = x^2\\)"),
            ("普通回答仍然保留。", "普通回答仍然保留。"),
            ("\u0060Print(\"（注释）\")\u0060", "Print(\"（注释）\")"),
            ("（旁白）\n\u0060\u0060\u0060text\n（代码中的内容）\n\u0060\u0060\u0060", "（代码中的内容）"),
            ("（旁白）\n\u0060\u0060\u0060text\n（未闭合围栏内的代码）", "（未闭合围栏内的代码）")
        };
        foreach (var withModel in new[] { false, true })
        foreach (var (input, expected) in cases)
        {
            var before = source.Outbox.Count;
            var report = Sender(withModel).SendWithCadenceAsync(true, 10001, input, null).GetAwaiter().GetResult();
            Check("括号范围规则在无模型/模型失败时仍成立", report.Text == expected &&
                source.Outbox.Count - before == (expected.Length == 0 ? 0 : 1),
                $"case={Array.FindIndex(cases, c => c.Input == input)}; model={withModel}; actualChars={report.Text.Length}; expectedChars={expected.Length}");
        }
        Check("括号删除不额外调用模型", calls == 0);
        box.Apply(s => s.SplitReplies = true);
        var splitReport = Sender(true).SendWithCadenceAsync(true, 10001, "（新的内部判断）", null).GetAwaiter().GetResult();
        Check("分句开关打开后全括号文本同样零发送", !splitReport.AnySent);
        filterOutput = "{\"actionIds\":[0],\"reply\":\"（改写重新引入的说明）\"}";
        var rewritten = Sender(true).SendWithCadenceAsync(true, 10001, "*挥手*", null).GetAwaiter().GetResult();
        Check("语义改写生成的括号也必须清除", !rewritten.AnySent && calls == 1);
        filterOutput = "not json";
        box.Apply(s => s.SplitReplies = false);
        var fallback = Sender(true).SendWithCadenceAsync(true, 10001, "（不能恢复的括号）*挥手*正文", null).GetAwaiter().GetResult();
        Check("语义失败回退不能恢复已删除括号", fallback.Text == "挥手正文" && calls == 2);
        box.Apply(s => s.FilterActionNarration = false);
        var disabled = Sender(false).SendWithCadenceAsync(true, 10001, "（用户明确关闭过滤后的普通说明）", null).GetAwaiter().GetResult();
        Check("显式关闭过滤仍按原开关语义处理", disabled.Text == "（用户明确关闭过滤后的普通说明）");
    }
}
