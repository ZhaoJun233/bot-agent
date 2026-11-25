using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S162 Phase 3 全链路端到端集成测试：
///   ① 同会话轻量知识库：//kb save / list / search 跨群隔离，当轮内联 call("kb.search") 生效，//kb forget 后立即不可检索；
///   ② 分级记忆拓展与污点边界：私聊 //memory start / remember timezone & note / confirm 后，
///      私聊携带关联词触发 L2 PersonalNote 注入时，提示词工具清单移除 web.search 仅留同源 kb.search，且模型恶意输出 search 时被内联执行器零外部请求拦截；
///   ③ GET /zba/v1/status 仅暴露 unified-memory 聚合计数，不泄露任何偏好/笔记正文。
/// </summary>
public static partial class Program
{
    private static async Task RunPhase3MemoryKnowledgeScenarioAsync()
    {
        Section("S162 Phase 3：轻量知识库 //kb & kb.search · L2 个人事实防外泄 · 只读治理计数");

        var openAiPort = FreePort(17962);
        var botWsPort = FreePort(13262);
        var panelPort = FreePort(18262);
        const long groupA = 66162;
        const long groupB = 66163;
        const long userOwner = 20001;
        const long userMember = 20002;
        const string panelToken = "it-s162-panel-token";
        const string adminToken = "it-s162-zba-admin";
        const string readToken = "it-s162-zba-read";

        var dataDir = NewDataDir("s162");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        var env = new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["BOTAGENT_DATA_DIR"] = dataDir,
            ["QQCHAT_LOG_FILE"] = "0",
            ["QQCHAT_API_KEY"] = "sk-mock-s162",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = "*",
            ["QQCHAT_GROUP_INTERJECTION_PROBABILITY"] = "0",
            ["QQCHAT_GROUP_CONTINUATION_WAIT_SECONDS"] = "0",
            ["QQCHAT_PRIVATE_CONTINUATION_WAIT_SECONDS"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_MAX_AGENT_STEPS"] = "2",
            ["QQCHAT_WEB_SEARCH"] = "1",
            ["QQCHAT_SEARCH_SOURCES"] = "tavily",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_PANEL_TOKEN"] = panelToken,
            ["BOTAGENT_ZBA_TOKEN"] = adminToken,
            ["BOTAGENT_ZBA_READ_TOKEN"] = readToken,
            ["BOTAGENT_UNIFIED_MEMORY_ADMIN"] = new JsonObject
            {
                ["PlatformId"] = "qq.private",
                ["AccountScope"] = "legacy",
                ["InstanceId"] = "",
                ["NativeUserId"] = userOwner.ToString()
            }.ToJsonString(),
        };

        using var bot = StartBot(env);
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        protocol.SetRole(groupA, userOwner, "owner");
        protocol.SetRole(groupA, userMember, "member");
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        var msgId = 162000L;

        async Task<string> SendKbGroupAsAsync(long gid, long uid, string senderName, string cmd)
        {
            var before = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg");
            await protocol.SendGroupMessageAsync(gid, uid, senderName, cmd, ++msgId, mentionBot: false, ct: cts.Token);
            var sent = await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(20), skip: before);
            return sent?["params"]?["message"]?.ToString() ?? string.Empty;
        }

        Task<string> SendKbGroupAsync(long gid, string cmd)
            => SendKbGroupAsAsync(gid, userOwner, "群友A", cmd);

        async Task<string> SendPrivateCmdAsync(string cmd)
        {
            var before = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_private_msg");
            await protocol.SendPrivateMessageAsync(userOwner, "群友A", cmd, ++msgId, ct: cts.Token);
            var sent = await protocol.WaitForActionAsync("send_private_msg", TimeSpan.FromSeconds(20), skip: before);
            return sent?["params"]?["message"]?.ToString() ?? string.Empty;
        }

        // ── ① //kb save / list / search 跨群隔离与 //kb forget ──
        var saveReply = await SendKbGroupAsync(groupA, "//kb save 值班规范 | 周五晚由轮值组负责灰度巡检与回滚演练");
        var docMatch = Regex.Match(saveReply, @"kb_[a-f0-9]{8}");
        Check("★ S162.1 //kb save 确定性入库成功并返回 docId", docMatch.Success && saveReply.Contains("已入库"), saveReply);
        var docId = docMatch.Value;

        var listReply = await SendKbGroupAsync(groupA, "//kb list");
        Check("★ S162.2 //kb list 列出当前群文档", listReply.Contains(docId) && listReply.Contains("值班规范"), listReply);

        var hitA = await SendKbGroupAsync(groupA, "//kb search 灰度巡检");
        var missB = await SendKbGroupAsync(groupB, "//kb search 灰度巡检");
        Check("★ S162.3 //kb search 仅在原群命中，跨群严格为零命中",
            hitA.Contains("灰度巡检") && missB.Contains("未检索到"),
            $"hitA={hitA}; missB={missB}");
        Check("S162.3b //kb 命令从未进入模型请求", openAi.Requests.Count == 0, $"reqs={openAi.Requests.Count}");

        // 群内 @ 触发当轮内联 kb.search
        openAi.AddRule("[会话知识库检索结果", """{"suitability": 95, "reply": "根据本群知识库：周五晚执行灰度巡检与回滚演练。"}""");
        openAi.AddRule("查一下群里的灰度值班规则", """{"suitability": 95, "reply": "", "call": {"tool": "kb.search", "args": {"query": "灰度巡检"}}}""");
        var beforeKbChat = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg");
        await protocol.SendGroupMessageAsync(groupA, userOwner, "群友A", "查一下群里的灰度值班规则", ++msgId, mentionBot: true, ct: cts.Token);
        var kbChatReply = await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(25), skip: beforeKbChat);
        var kbChatText = kbChatReply?["params"]?["message"]?.ToString() ?? string.Empty;
        Check("★ S162.4 当轮内联 call(\"kb.search\") 命中本群知识库并回答",
            kbChatText.Contains("灰度巡检"), kbChatText);

        var memberForgetDenied = await SendKbGroupAsAsync(groupA, userMember, "群友B", $"//kb forget {docId}");
        var stillReadableAfterDenied = await SendKbGroupAsync(groupA, "//kb search 灰度巡检");
        Check("★ S162.4b 普通群成员 //kb forget 被拒绝且不删除群知识库文档",
            memberForgetDenied.Contains("仅限群主、群管理员或已配置审批人")
            && stillReadableAfterDenied.Contains("灰度巡检"),
            $"denied={memberForgetDenied}; search={stillReadableAfterDenied}");

        var forgetReply = await SendKbGroupAsync(groupA, $"//kb forget {docId}");
        var afterForget = await SendKbGroupAsync(groupA, "//kb search 灰度巡检");
        Check("★ S162.5 //kb forget 后检索立即返回空",
            forgetReply.Contains("已遗忘") && afterForget.Contains("未检索到"),
            $"forget={forgetReply}; search={afterForget}");

        // ── ② 统一记忆扩展类别 + L2 污点防外泄 + ZBA 只读治理计数 ──
        var reqCountBeforeMem = openAi.Requests.Count;
        var started = await SendPrivateCmdAsync("//memory start");
        Check("S162.6 私聊管理员开启统一记忆", started.Contains("已完成"), started);

        var tzProp = await SendPrivateCmdAsync("//memory remember timezone Asia/Shanghai");
        var tzConf = Regex.Match(tzProp, @"//memory confirm [a-f0-9]{32} [0-9]+ [a-f0-9]{32} allow-source private-only").Value;
        Check("S162.7 提议合法时区生成私聊确认指令", !string.IsNullOrEmpty(tzConf), tzProp);
        Check("S162.7b 确认时区偏好", (await SendPrivateCmdAsync(tzConf)).Contains("已完成"));

        var noteProp = await SendPrivateCmdAsync("//memory remember note 周三架构评审口令CANARY162");
        var noteConf = Regex.Match(noteProp, @"//memory confirm [a-f0-9]{32} [0-9]+ [a-f0-9]{32} allow-source private-only").Value;
        Check("S162.8 提议个人私聊笔记生成私聊确认指令", !string.IsNullOrEmpty(noteConf), noteProp);
        Check("S162.8b 确认个人笔记", (await SendPrivateCmdAsync(noteConf)).Contains("已完成"));
        Check("S162.8c 记忆命令全程未送往模型", openAi.Requests.Count == reqCountBeforeMem,
            $"before={reqCountBeforeMem}, after={openAi.Requests.Count}");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var statusRes = await ZbaAsync(http, "GET", $"http://127.0.0.1:{panelPort}/zba/v1/status", readToken);
        var statusJson = JsonNode.Parse(statusRes.Body);
        var umemComp = (statusJson?["components"] as JsonArray)?.FirstOrDefault(c => c?["id"]?.GetValue<string>() == "unified-memory");
        var umemReason = umemComp?["reasonCode"]?.GetValue<string>() ?? string.Empty;
        Check("★ S162.9 ZBA /status 暴露 unified-memory 只读治理计数且绝不泄露笔记正文",
            statusRes.Status == 200
            && umemComp?["state"]?.GetValue<string>() == "enabled"
            && umemReason.Contains("confirmed=2")
            && !statusRes.Body.Contains("CANARY162")
            && !statusRes.Body.Contains("Asia/Shanghai"),
            statusRes.Body);

        // 私聊命中 L2 PersonalNote 时：系统提示包含 PersonalNote 与 kb.search，不包含 web.search，且内联 search 被拦（只调了1次模型）
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 95, "reply": "", "search": "周三架构评审口令CANARY162"}""");
        await protocol.SendPrivateMessageAsync(userOwner, "群友A", "周三架构评审口令怎么安排？", ++msgId, ct: cts.Token);
        for (var wait = 0; wait < 80 && openAi.Requests.Count == 0; wait++)
            await Task.Delay(100, cts.Token);
        await Task.Delay(1200, cts.Token);

        var dmReqCount = openAi.Requests.Count;
        var dmSystem = dmReqCount > 0 ? (openAi.Requests[0]["messages"]?[0]?["content"]?.GetValue<string>() ?? string.Empty) : string.Empty;
        Check("★ S162.10 L2 个人事实回合：注入 PersonalNote、隐藏 web.search、保留 kb.search 且恶意 search 外发被拦（仅 1 次模型请求）",
            dmReqCount == 1
            && dmSystem.Contains("PersonalNote: 周三架构评审口令CANARY162")
            && !dmSystem.Contains("web.search")
            && dmSystem.Contains("kb.search"),
            $"reqs={dmReqCount}, hasWeb={dmSystem.Contains("web.search")}, hasKb={dmSystem.Contains("kb.search")}");
    }
}
