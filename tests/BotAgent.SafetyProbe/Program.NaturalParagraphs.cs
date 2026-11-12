using BotAgent.Domain.Conversation;
using BotAgent.Domain.Rendering;
using BotAgent.Services;
using BotAgent.Services.Conversations;
using BotAgent.Services.Local;
using BotAgent.Services.Ops;
using BotAgent.Services.Panel;
using BotAgent.Services.Reply;

namespace BotAgent.SafetyProbe;

public static partial class Program
{
    private static void NaturalParagraphTests()
    {
        Section("自然分段：显式换行、短段与发送边界（合成文本）");
        var cases = new (string Name, string Input, string[] Expected)[]
        {
            ("无句号末段", "先整理合成样本\n\n再核对合成结果", ["先整理合成样本", "再核对合成结果"]),
            ("单换行", "先整理合成样本\n再核对合成结果", ["先整理合成样本", "再核对合成结果"]),
            ("CRLF", "先整理合成样本\r\n\r\n再核对合成结果", ["先整理合成样本", "再核对合成结果"]),
            ("CR", "先整理合成样本\r\r再核对合成结果", ["先整理合成样本", "再核对合成结果"]),
            ("短段", "收到\n\n稍等", ["收到", "稍等"]),
            ("短首段", "收到\n\n再核对合成结果", ["收到", "再核对合成结果"]),
            ("短末段", "先整理合成样本\n\n完成", ["先整理合成样本", "完成"]),
            ("空白行", "  先整理合成样本  \n \t\n\n再核对合成结果  ", ["先整理合成样本", "再核对合成结果"]),
            ("段内句号", "第一项已经核对。第二项也已核对。\n\n接下来整理结果", ["第一项已经核对。第二项也已核对。", "接下来整理结果"]),
            ("四段上限保留溢出分隔", "甲段\n\n乙段\n\n丙段\n\n丁段\n\n戊段", ["甲段", "乙段", "丙段", "丁段\n\n戊段"]),
            ("无换行按原句号规则", "第一句话在这里。第二句话也很长。第三句话继续补充。", ["第一句话在这里。", "第二句话也很长。", "第三句话继续补充。"]),
            ("无换行短尾沿用合并", "第一句话在这里。完成", ["第一句话在这里。完成"]),
        };
        foreach (var test in cases)
        {
            var actual = TextRules.SplitSentences(test.Input);
            Check($"自然分段 · {test.Name}", actual.SequenceEqual(test.Expected),
                $"expected={test.Expected.Length}; actual={actual.Count}; lengths={string.Join(',', actual.Select(s => s.Length))}");
        }

        var settings = new SettingsBox(new AppSettings
        {
            SplitReplies = true, SegmentDelayMs = 0, FilterActionNarration = false
        });
        using var source = new LocalChannelSource();
        var registry = new ConversationRegistry(new FakeConversationRepository(), settings, source, _ => true, _ => { });
        var sender = new PlainSender(settings, source, registry, new PanelNotifier(),
            new OwnMessageLedger(new FakeOwnMessageRepository(), _ => { }), _ => { }, new TurnTraceStore());
        const string reply = "**先整理合成样本**\n\n再核对合成结果";
        string[] expected = ["先整理合成样本", "再核对合成结果"];
        var report = sender.SendWithCadenceAsync(true, 10001, reply, null).GetAwaiter().GetResult();
        Check("自然分段 · 清洗后真实发送入口发两条", report.AllSent &&
            report.SentSegments.SequenceEqual(expected) && source.Outbox.Select(m => m.Text).SequenceEqual(expected));

        settings.Apply(s => s.SplitReplies = false);
        var before = source.Outbox.Count;
        report = sender.SendWithCadenceAsync(true, 10001, reply, null).GetAwaiter().GetResult();
        Check("自然分段 · 关闭分句仍发原有单条", report.AllSent && report.SentSegments.Count == 1 &&
            source.Outbox.Count == before + 1 && source.Outbox[^1].Text == QqPlainText.Sanitize(reply));
    }
}
