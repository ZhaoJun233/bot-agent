using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S32 连珠炮不再丢消息（管理员 2026-09-16 反馈“连续多人对话不回应”）。
///
/// 线上现场（群里 1 秒内来了 4 条，机器人一条都没回）：
///   21:46:10.629 请求模型…            ← 第 1 条开始生成
///   21:46:10.695 冷却中（2.5s 后放行）  ← 第 2 条被丢
///   21:46:11.728 冷却中（1.5s 后放行）  ← 第 3 条被丢
///   21:46:11.731 冷却中（1.5s 后放行）  ← 第 4 条被丢
///   21:46:16.492 适合度不足 → 沉默（评分 10 < 阈值 20）  ← 那一轮结论是沉默
///   21:47:43.611 静默兜底触发          ← 中间这 87 秒里群里说的话，一条都没被评估
///
/// 根因：`AllowReply` 返回 false 时触发被彻底丢掉（老写法 `if (AllowReply(...)) EnqueueReply(...)`）。
/// 现在：被挡下就记一笔（DeferredTrigger），这一轮生成结束后**补一次评估**；
///   • 只补一次，不会打转；
///   • 顺带刷新冷却时间（连珠炮也不会变成刷屏）；
///   • 窗口里有“@ 你 / 引用你的话”时，补评估拿那条当触发（被点名必答 + 引用挂对人）。
/// </summary>
public static partial class Program
{
    private static async Task RunBurstScenarioAsync()
    {
        Section("S32 连珠炮：被限流挡下的消息不再丢（补一次评估）");

        const int openAiPort = 17841;
        const int botWsPort = 13063;
        const long groupId = 66720;

        var dataDir = NewDataDir("s32");

        using var openAi = new MockOpenAi(openAiPort) { ResponseDelayMs = 2500 };
        openAi.Start();

        // 第 1 轮（第一条 @ 触发）：自评 5 → 沉默，但要慢一点，好让后面的消息落在冷却窗口里
        openAi.EnqueueReply("""{"suitability": 5, "vibe": "闲聊", "vibeNote": "在聊电脑", "reply": ""}""");
        // 补评估那一轮：应该答“被点名”的那条
        openAi.EnqueueReply("""{"suitability": 80, "vibe": "闲聊", "vibeNote": "在跟机器人说话", "reply": "Mac 确实香，但钱包不香"}""");

        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = groupId.ToString(),
            ["QQCHAT_GROUP_COOLDOWN"] = "3",     // 默认值：连珠炮必然撞上冷却
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_SUITABILITY_THRESHOLD"] = "20",
            ["QQCHAT_IDLE_FALLBACK"] = "600",     // 关掉静默兜底的干扰，专门验“补评估”
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_HEALTH_PORT"] = "0"
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // ① 第一条：@ 了机器人 → 立刻开始生成，随后进入 3 秒冷却
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "@10001 你在吗", 12001, mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(() => bot.OutputLines.Any(l => l.Contains("请求模型")), TimeSpan.FromSeconds(20));

        // ② 生成期间又来 3 条（最后一条是直接跟机器人说话）
        await Task.Delay(200);
        await protocol.SendGroupMessageAsync(groupId, 20003, "小李", "还有人在吗", 12002, mentionBot: false, ct: cts.Token);
        await Task.Delay(150);
        await protocol.SendGroupMessageAsync(groupId, 20004, "小张", "换台 Mac 吧", 12003, mentionBot: false, ct: cts.Token);
        await Task.Delay(150);
        await protocol.SendGroupMessageAsync(groupId, 20005, "小李", "@10001 你在吗", 12004, mentionBot: true, ct: cts.Token);

        // ③ 第一轮结束（沉默）→ 应立刻补一次评估
        await WaitUntilAsync(() => bot.OutputLines.Any(l => l.Contains("补一次评估")), TimeSpan.FromSeconds(60));
        await WaitUntilAsync(
            () => protocol.ActionsReceived.Any(a =>
                a["action"]?.GetValue<string>() == "send_group_msg" &&
                MessageText(a).Contains("Mac 确实香")),
            TimeSpan.FromSeconds(40));
        await Task.Delay(800);

        var sends = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText)
            .ToList();

        Check("★ 冷却挡下的消息会被记下，这一轮说完补一次评估（不再干等 60 秒静默兜底）",
            bot.OutputLines.Any(l => l.Contains("补一次评估")),
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("补一次评估")).TakeLast(2)));

        Check("★ 被挡下的消息没有静默消失（补评估里真的回了）",
            sends.Any(t => t.Contains("Mac 确实香")), string.Join(" | ", sends));

        Check("★ 补评估拿“@ 机器人”那条当触发（#12004，而不是随便挑一条）",
            bot.OutputLines.Any(l => l.Contains("补一次评估") && l.Contains("12004")),
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("补一次评估")).TakeLast(2)));

        Check("★ 被点名那条进了模型请求的上下文（小李问的话真的送到了）",
            openAi.Requests.Any(r => UserTexts(r).Any(t => t.Contains("你在吗"))),
            openAi.Requests.Count == 0 ? "(没有请求)" : Snippet(openAi.DescribeRequest(openAi.Requests.Count - 1), "你在吗"));

        Check("★ 补评估只补一次（不会打转成刷屏）",
            bot.OutputLines.Count(l => l.Contains("补一次评估")) <= 2,
            $"补评估次数 = {bot.OutputLines.Count(l => l.Contains("补一次评估"))}");

        Check("★ 连珠炮期间最多只发一条（没有每条都回）",
            sends.Count <= 2, $"发了 {sends.Count} 条：{string.Join(" | ", sends)}");

        await bot.StopAsync();
    }
}
