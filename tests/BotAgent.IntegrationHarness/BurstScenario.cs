using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    // Exercise real ingress, model transport and send boundaries with same-human continuation.
    private static async Task RunBurstScenarioAsync()
    {
        Section("S32 续发期间不发送旧稿，合并后的明确请求只回复一次");
        var openAiPort = FreePort(17841);
        var botWsPort = FreePort(13063);
        const long groupId = 10001;
        const long speakerId = 20002;
        const string answer = "Synthetic merged answer";
        using var openAi = new MockOpenAi(openAiPort) { ResponseDelayMs = 2500 };
        openAi.Start();
        openAi.EnqueueReply("""{"suitability":5,"reply":""}""");
        openAi.EnqueueReply("""{"suitability":99,"reply":"Synthetic merged answer"}""");
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = NewDataDir("s32"),
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = groupId.ToString(),
            ["QQCHAT_GROUP_CONTINUATION_WAIT"] = "1",
            ["QQCHAT_GROUP_INTERJECTION_COOLDOWN"] = "3",
            ["QQCHAT_SUITABILITY_THRESHOLD"] = "20",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_PROFILE_SUMMARY"] = "0",
            ["QQCHAT_HEALTH_PORT"] = "0"
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await protocol.SendGroupMessageAsync(groupId, speakerId, "synthetic", "Synthetic part one", 12001,
            mentionBot: true, ct: cts.Token);
        Check("第一轮已开始生成", await WaitUntilAsync(() => openAi.Requests.Count == 1, TimeSpan.FromSeconds(20)),
            $"requests={openAi.Requests.Count}");
        for (var i = 2; i <= 4; i++)
        {
            await protocol.SendGroupMessageAsync(groupId, speakerId, "synthetic", "Synthetic part " + i, 12000 + i,
                mentionBot: i == 4, ct: cts.Token);
            await Task.Delay(150);
        }
        Check("合并后的明确请求发出回复", await WaitUntilAsync(() => protocol.ActionsReceived.Any(
            action => action["action"]?.GetValue<string>() == "send_group_msg" && MessageText(action) == answer),
            TimeSpan.FromSeconds(30)), $"requests={openAi.Requests.Count}");
        await Task.Delay(1000);
        var sends = protocol.ActionsReceived.Where(action => action["action"]?.GetValue<string>() == "send_group_msg").ToList();
        Check("续发期间旧稿不发送，最终只发送一次", sends.Count == 1 && MessageText(sends[0]) == answer,
            $"sends={sends.Count}");
        Check("失效稿只重算一次，没有循环补评估", openAi.Requests.Count == 2, $"requests={openAi.Requests.Count}");
        var finalRequest = openAi.Requests.LastOrDefault();
        Check("最终模型上下文包含全部续发", finalRequest is not null && Enumerable.Range(1, 4).All(
            part => UserTexts(finalRequest).Any(text => text.Contains("Synthetic part " + (part == 1 ? "one" : part.ToString())))),
            $"parts=4; requests={openAi.Requests.Count}");
        Check("最终上下文保留最后一次明确请求编号", finalRequest is not null && UserTexts(finalRequest).Any(
            text => text.Contains("(#12004)")), "latestId=12004");
        Check("后台巡检没有激活旧补评估入口", !bot.OutputLines.Any(line => line.Contains("补一次评估")), "legacyCatchUpCount=0");
        await bot.StopAsync();
    }
}
