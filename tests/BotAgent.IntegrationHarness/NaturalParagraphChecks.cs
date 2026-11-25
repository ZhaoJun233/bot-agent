using System.Text.Json;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunNaturalParagraphChecksAsync(MockOpenAi openAi, MockProtocol protocol, CancellationToken ct)
    {
        var cases = new (string Name, string Reply, string[] Expected)[]
        {
            ("无句号自然段", "先整理合成样本\n\n再核对合成结果", ["先整理合成样本", "再核对合成结果"]),
            ("短自然段", "收到\n\n稍等", ["收到", "稍等"]),
            ("CRLF 与段内句号", "第一项已经核对。第二项也已核对。\r\n\r\n接下来整理结果",
                ["第一项已经核对。第二项也已核对。", "接下来整理结果"]),
        };
        long messageId = 7310;
        // 与 S58 相同：生成期间有人插话，才会为较早的触发消息附带引用。
        var previousDelay = openAi.ResponseDelayMs;
        openAi.ResponseDelayMs = 800;
        try
        {
            foreach (var test in cases)
            {
                var mark = protocol.ActionsReceived.Count;
                var previousSends = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg");
                var previousRequests = openAi.Requests.Count;
                openAi.EnqueueReply(JsonSerializer.Serialize(new { suitability = 90, reply = test.Reply }));
                openAi.EnqueueReply(JsonSerializer.Serialize(new { suitability = 0, reply = "" }));
                await protocol.SendGroupMessageAsync(99999, 20002, "群友A", "@机器人 返回下一组合成结果",
                    messageId, mentionBot: true, ct: ct);
                await WaitUntilAsync(() => openAi.Requests.Count > previousRequests, TimeSpan.FromSeconds(5));
                await protocol.SendGroupMessageAsync(99999, 20003, "群友B", "插入一条合成补充说明",
                    messageId + 1, ct: ct);
                await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(10),
                    skip: previousSends + test.Expected.Length - 1);
                await WaitUntilAsync(() => openAi.Requests.Count >= previousRequests + 2, TimeSpan.FromSeconds(5));
                await Task.Delay(openAi.ResponseDelayMs + 200, ct);
                var sends = protocol.ActionsReceived.Skip(mark)
                    .Where(a => a["action"]?.GetValue<string>() == "send_group_msg").ToArray();
                Check($"自然分段 · {test.Name}按段发送且内容完整",
                    sends.Select(MessageText).SequenceEqual(test.Expected), $"expected={test.Expected.Length}; actual={sends.Length}");
                Check($"自然分段 · {test.Name}仅首段引用", sends.Length == test.Expected.Length &&
                    QuotedMessageId(sends[0]) == messageId && sends.Skip(1).All(s => QuotedMessageId(s) is null));
                messageId += 2;
            }
        }
        finally { openAi.ResponseDelayMs = previousDelay; }
    }
}
