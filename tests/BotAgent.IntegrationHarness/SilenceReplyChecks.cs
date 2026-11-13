using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunSilencePlaceholderChecksAsync()
    {
        Section("S4 扩展：内部参与判断和合法删空均不发送（合成输入）");
        var modelPort = FreePort(17873);
        var protocolPort = FreePort(13073);
        using var model = new MockOpenAi(modelPort);
        model.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = NewDataDir("s4-silence-notices"),
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = model.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{protocolPort}",
            ["QQCHAT_UIN"] = "10001", ["QQCHAT_WHITELIST"] = "99999",
            ["QQCHAT_GROUP_COOLDOWN"] = "0", ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_FILTER_ACTION_NARRATION"] = "1", ["QQCHAT_HEALTH_PORT"] = "0"
        });
        await WaitForPortAsync(protocolPort, cts.Token, bot);
        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{protocolPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        var inputs = new[]
        {
            "（不发言）",
            new JsonObject { ["suitability"] = 90, ["reply"] = "（当前话题由其他人处理）", ["search"] = "合成检索" }.ToJsonString(),
            new JsonObject { ["suitability"] = 90, ["action"] = "reply", ["reply"] = "（没有点名，也不知道答案，不接话）", ["speak"] = "合成语音" }.ToJsonString(),
            new JsonObject { ["suitability"] = 90, ["reply"] = "本轮无需介入" }.ToJsonString()
        };
        var index = 0;
        foreach (var raw in inputs)
        {
            var before = bot.OutputLines.Count(l => l.Contains("outcome=suppressed", StringComparison.Ordinal));
            var requests = model.Requests.Count;
            model.EnqueueReply(raw);
            await protocol.SendGroupMessageAsync(99999, 20002, "合成成员", $"@机器人 合成检查 {++index}",
                7400 + index, mentionBot: true, ct: cts.Token);
            var settled = await WaitUntilAsync(() => bot.OutputLines.Count(l => l.Contains("outcome=suppressed", StringComparison.Ordinal)) > before,
                TimeSpan.FromSeconds(10));
            Check("沉默声明完成宿主决策且没有任何群消息", settled && !protocol.ActionsReceived.Any(a => a["action"]?.GetValue<string>() == "send_group_msg"));
            Check("沉默声明只调用一次模型，不触发附带检索或过滤", model.Requests.Count == requests + 1);
        }

        model.EnqueueReply("{\"suitability\":90,\"reply\":\"（点头）\"}");
        model.EnqueueReply("{\"actionIds\":[0],\"reply\":\"\"}");
        var beforeFilter = model.Requests.Count;
        await protocol.SendGroupMessageAsync(99999, 20002, "合成成员", "@机器人 合成检查整条删空",
            7410, mentionBot: true, ct: cts.Token);
        var cleaned = await WaitUntilAsync(() => bot.OutputLines.Any(l => l.Contains("清洗后没有可发内容", StringComparison.Ordinal)), TimeSpan.FromSeconds(10));
        Check("宿主允许确认删除全部旁白后零发送", cleaned && !protocol.ActionsReceived.Any(a => a["action"]?.GetValue<string>() == "send_group_msg"));
        Check("整条删空仅有一次主调用和一次旁白检查", model.Requests.Count == beforeFilter + 2);

        model.EnqueueReply("{\"suitability\":90,\"reply\":\"合成正常答复。\"}");
        await protocol.SendGroupMessageAsync(99999, 20002, "合成成员", "@机器人 合成检查普通回答",
            7411, mentionBot: true, ct: cts.Token);
        var sent = await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(10));
        Check("沉默后下一轮普通回答仍能正常发出", MessageText(sent) == "合成正常答复。");
        await bot.StopAsync();
    }
}
