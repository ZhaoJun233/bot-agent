using System.Text;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S48 第三条通道（general-agent-platform-plan.md 批次 F）——**证明“通用”**。
///
/// 用它验证的那句话是：**接入层可以换，核心与治理一行都不用改**。
/// 所以这个场景不看新代码有多漂亮，而是看这条新上行**有没有复用**既有的一切：
///   ① 一条本地消息进来 → 走**同一套**白名单门（本地通道有自己的名单，空 = 全拦）；
///   ② 走**同一个**回复链（同一个假模型、同一份提示词、同一套动作闸门）；
///   ③ 会话 key 带 local: 前缀（与 QQ 两条路上下文隔离）；
///   ④ 回复落进本地通道的**出箱**（那条路不碰网络）；
///   ⑤ 面板能看到这条通道的状态与出箱形状（**只有长度**，没有正文）；
///   ⑥ 名单外的本地 id 一封都不回（fail-closed）。
///
/// 反向的“默认关”由**所有其它场景**覆盖：它们都没配 QQCHAT_LOCAL_CHANNEL_IDS，
/// 于是这个通道在那些进程里根本不存在（/api/local 会如实报 available=false）。
/// </summary>
public static partial class Program
{
    private static async Task RunLocalChannelScenarioAsync()
    {
        Section("S48 第三条通道（本地入口）：接入层可换，核心与治理复用");

        const int openAiPort = 17888;
        const int botWsPort = 13108;
        const int panelPort = 18160;
        const long groupId = 667800;
        const long localId = 101;             // 配置里写的**短 id**（服务端换算成本地号段）
        const long unknownLocalId = 999;      // 没在名单里的
        const string panelToken = "it-s48-token";

        var panel = $"http://127.0.0.1:{panelPort}";
        var dataDir = NewDataDir("s48");

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
            ["QQCHAT_PANEL_TOKEN"] = panelToken,
            // 批次 F：名单非空 = 这条通道才建（默认关）
            ["QQCHAT_LOCAL_CHANNEL_IDS"] = localId.ToString()
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);

        async Task<(int Code, string Body)> LocalPostAsync(long id, string text)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var json = "{\"id\":" + id + ",\"text\":\"" + text.Replace("\"", "\\\"") + "\",\"sender\":\"本地用户\"}";
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync($"{panel}/api/local/message?token={panelToken}", content, cts.Token);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(cts.Token));
        }

        // ── ① 面板能看到这条通道（只读形状）──
        var (localCode, localBody) = await HttpGetAsync($"{panel}/api/local?token={panelToken}");
        var localRoot = JsonNode.Parse(localBody) as JsonObject ?? new JsonObject();
        Check("★ 面板看得见本地通道（开着、令牌已配、可注入）",
            localCode == 200 && localRoot["available"]?.GetValue<bool>() == true
            && localRoot["enabled"]?.GetValue<bool>() == true
            && localRoot["canInject"]?.GetValue<bool>() == true,
            $"HTTP {localCode} {localBody[..Math.Min(160, localBody.Length)]}");

        var (_, settingsBody) = await HttpGetAsync($"{panel}/api/settings?token={panelToken}");
        var settingsRoot = JsonNode.Parse(settingsBody) as JsonObject;
        Check("★ 面板回显里带上了这条通道的名单（面板改它要重启才生效）",
            settingsRoot?["runtime"]?["localChannelIds"]?.GetValue<string>() == localId.ToString(),
            settingsRoot?["runtime"]?["localChannelIds"]?.ToJsonString() ?? "(没有这个字段)");

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await Task.Delay(600);

        // ── ② 名单外的本地 id：一封都不回（fail-closed）──
        openAi.ClearRequests();
        openAi.EnqueueReply("{\"suitability\": 99, \"reply\": \"这条不该被发出去\"}");
        var before = protocol.ActionsReceived.Count;
        var (outsiderCode, _) = await LocalPostAsync(unknownLocalId, "我是名单外的人");
        await Task.Delay(2500);
        Check("★ 名单外的本地 id 被白名单拦下（一封都不回，也不进模型）",
            outsiderCode == 202 && openAi.Requests.Count == 0 && protocol.ActionsReceived.Count == before,
            $"HTTP {outsiderCode} 模型请求 {openAi.Requests.Count} 次 / 协议端动作 {protocol.ActionsReceived.Count - before} 个");

        // ── ③ 名单内的本地 id：走完整条链（模型 → 回复 → 出箱）──
        openAi.ClearRequests();
        openAi.EnqueueReply("{\"suitability\": 99, \"reply\": \"本地通道也在线。\"}");
        var (okCode, okBody) = await LocalPostAsync(localId, "帮我看一眼");
        Check("★ 名单内的本地 id 被受理（202）", okCode == 202,
            $"HTTP {okCode} {okBody[..Math.Min(160, okBody.Length)]}");
        Check("★ 受理回执里的会话 key 是**本地通道**的（local: 前缀，与 QQ 两条路隔离）",
            (JsonNode.Parse(okBody) as JsonObject)?["key"]?.GetValue<string>()?.Contains("local", StringComparison.OrdinalIgnoreCase) == true,
            (JsonNode.Parse(okBody) as JsonObject)?["key"]?.ToJsonString() ?? "(没有 key)");

        await Task.Delay(3000);
        Check("★ 那条本地消息真的进了模型（核心与治理原样复用：同一条回复链）",
            openAi.Requests.Count >= 1, "模型请求 " + openAi.Requests.Count + " 次");
        Check("★ 提示词里带的是本地通道自己的会话（没混进 QQ 群那套）",
            openAi.Requests.Count >= 1
            && !openAi.DescribeRequest(0).Contains("刚看到的群消息", StringComparison.Ordinal),
            "提示词里不该出现 QQ 群的上下文段");

        var (o2Code, o2Body) = await HttpGetAsync($"{panel}/api/local?token={panelToken}");
        var outbox = (JsonNode.Parse(o2Body) as JsonObject)?["outbox"] as JsonArray ?? new JsonArray();
        Check("★ 回复落进本地通道的出箱（那条路不碰网络，harness 才能断言“真发出来了”）",
            o2Code == 200 && outbox.Count >= 1, $"HTTP {o2Code} 出箱 {outbox.Count} 条");
        Check("★★ 出箱只给形状（长度 + 会话 key），不给正文",
            outbox.Count >= 1 && outbox[0]?["length"] is not null && outbox[0]?["key"] is not null
            && !o2Body.Contains("本地通道也在线", StringComparison.Ordinal),
            "出箱里出现了正文");
        Check("★ QQ 那条上行**没有**参与（本地通道的回复不会跑到群里）",
            !protocol.ActionsReceived.Any(a => a["action"]?.GetValue<string>() == "send_group_msg"
                && MessageText(a).Contains("本地通道也在线")),
            "协议端收到了不该发的群消息");

        await bot.StopAsync();
    }
}
