using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S47 聊天侧的**有限步进循环**（general-agent-platform-plan.md 批次 E）。
///
/// 这个场景只钉一件事：**同一条群消息**里，模型先说要查、机器人**当场查完再问一次** ——
/// 而不是像改造前那样把资料留到"下一轮"（下一轮要等一条新消息才发生）。
///
/// 断言：
///   ① 那一条消息只带来**两次**模型请求（不是一次、也不是散在两条消息里）；
///   ② 第二次请求的提示词里带着**刚查到的资料**（喂回去了）；
///   ③ 群里收到的**只有最终那句**（中间那句"我去查一下"不会先发出去）；
///   ④ 日志里有 `[循环] 当场搜索`（证明走的是当场那条路），**且没有** `[Search] 模型想搜`（没有被重复排一次）；
///   ⑤ 面板的轨迹里那一轮有 **2 个模型节点 + 1 个当场执行节点**（批 C 的观测面把这件事记下来了）。
///
/// 反向的"默认行为不变"由其它场景覆盖：它们的 `QQCHAT_MAX_AGENT_STEPS` 都是默认 1。
/// </summary>
public static partial class Program
{
    private static async Task RunTurnLoopScenarioAsync()
    {
        Section("S47 聊天侧有限步进循环（当场查完再问一次；默认 1 步不变）");

        const int openAiPort = 17887;
        const int botWsPort = 13107;
        const int panelPort = 18157;
        const int searchPort = 18158;
        const long groupId = 667701;
        const long memberId = 20007;

        var panel = $"http://127.0.0.1:{panelPort}";
        var dataDir = NewDataDir("s47");

        using var search = new MockSearchHost(searchPort);
        search.Start();

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
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
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_ENABLE_POKE"] = "0",
            ["QQCHAT_ENABLE_VOICE"] = "0",
            ["QQCHAT_ENABLE_MUSIC"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_WEB_SEARCH"] = "1",
            ["QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS"] = "1",
            ["QQCHAT_SEARCH_SOURCES"] = search.SearxSource,
            // 批次 E 的开关：2 = 允许"查完再问一次"
            ["QQCHAT_MAX_AGENT_STEPS"] = "2"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);

        var (settingsCode, settingsBody) = await PanelGetAsync($"{panel}/api/settings");
        Check("★ 面板里能看到步进循环上限（=2）",
            settingsCode == 200 && settingsBody.Contains("\"maxAgentSteps\":2"),
            settingsBody.Contains("maxAgentSteps") ? "已回显" : "(没有这个字段)");

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await Task.Delay(600);

        List<string> Sent() => protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        // 第一次：模型说要查（并且给一句"我去查一下"）；第二次（喂回资料后）：给最终答案
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 95, "reply": "我去查一下哈。", "search": "某个只在假搜索里有的词"}""");
        openAi.EnqueueReply("""{"suitability": 95, "reply": "查到了，就是那样。"}""");

        var before = Sent().Count;
        await protocol.SendGroupMessageAsync(groupId, memberId, "群友A", "@10001 帮我查个东西", 47001,
            mentionBot: true, ct: cts.Token);

        await WaitUntilAsync(() => Sent().Skip(before).Any(t => t.Contains("查到了")), TimeSpan.FromSeconds(60));
        await Task.Delay(500);

        Check("★★ 同一条消息里问了两次模型（循环真的跑起来了，不是留给下一轮）",
            openAi.Requests.Count == 2, "这一轮模型请求 " + openAi.Requests.Count + " 次");

        // 用 DescribeRequest（不转义非 ASCII）—— 默认编码器会把中文写成 \u5F20…，那样 Contains 永远查不到
        var fed = openAi.Requests.Count >= 2 ? openAi.DescribeRequest(1) : string.Empty;
        Check("★★ 第二次请求里带着刚查到的资料（喂回去了）",
            fed.Contains("刚查到的资料"), fed.Length > 0 ? "提示词里有" : "(没有第二次请求)");

        Check("★★ 群里只收到最终那句（中间那句“我去查一下”不会先发出去）",
            Sent().Skip(before).Any(t => t.Contains("查到了")) && !Sent().Skip(before).Any(t => t.Contains("我去查一下")),
            string.Join(" | ", Sent().Skip(before)));

        Check("★ 日志写明走的是“当场搜索”那条路",
            bot.OutputLines.Any(l => l.Contains("[循环] 当场搜索")),
            bot.OutputLines.LastOrDefault(l => l.Contains("[循环]")) ?? "(没有循环日志)");
        Check("★ 没有被“留给下一轮”那条路重复排一次（同一次搜索只发生一次）",
            !bot.OutputLines.Any(l => l.Contains("[Search] 模型想搜")),
            bot.OutputLines.LastOrDefault(l => l.Contains("[Search]")) ?? "(没有 [Search] 行)");

        // 面板轨迹：那一轮 = 2 个模型节点 + 1 个当场执行节点（批 C 的观测面）
        var (traceCode, traceBody) = await PanelGetAsync($"{panel}/api/traces?limit=5");
        var traces = (JsonNode.Parse(traceBody) as JsonObject)?["traces"] as JsonArray ?? new JsonArray();
        var latest = traces.FirstOrDefault() as JsonObject;
        var nodes = latest?["nodes"] as JsonArray ?? new JsonArray();
        var modelNodes = nodes.Count(n => n?["kind"]?.GetValue<string>() == "Model");
        var toolNodes = nodes.Count(n => n?["kind"]?.GetValue<string>() == "ToolExec");
        Check("★★ 轨迹如实记下这一轮（2 个模型节点 + 1 个当场执行节点）",
            traceCode == 200 && modelNodes == 2 && toolNodes >= 1,
            $"HTTP {traceCode} 模型节点 {modelNodes} / 执行节点 {toolNodes}");
        Check("★ 轨迹里没有正文（只有形状）",
            latest is not null && !latest.ToJsonString().Contains("查到了，就是那样"),
            latest?.ToJsonString()[..Math.Min(120, latest.ToJsonString().Length)] ?? "(没有轨迹)");

        await bot.StopAsync();
    }
}
