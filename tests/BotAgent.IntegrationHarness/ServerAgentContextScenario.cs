using System.Text;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S39 服务器 agent 的“上下文卫生”（管理员 2026-09-18 的两条实测反馈）：
///
///   ① “每次发送新指令都会把旧指令的内容发送回来。要把每条指令输出单独对待”
///      —— 会话里堆着上几轮的指令原文时，模型会把旧指令也答一遍（实测回复是
///      “1. 点赞动作：…失败 2. 服务器状态：…”）。所以现在**默认每条指令单独对待**：
///      不把历史喂给模型，想接着聊要么面板开开关、要么单条写 //接着 …。
///   ② “reset 并没有删除此会话的全部内容” —— 以前 //reset 按“下一句会走哪边”只清一边，
///      写 @某台不在线的设备时清的是那份空的外部会话（真正在用的服务器会话没动）；
///      现在两个后端都清，并且连标题一起换回中性的自动名。
///
/// 另外钉住两个防回归点：
///   • 模型拿散文答话时先纠正一次（以前散文被当成结论，工具一次都不调）；
///   • 带历史时只带“用户说了什么 + 结论是什么”，**不带工具步骤**（bash 命令/工具输出）。
/// </summary>
public static partial class Program
{
    private static async Task RunServerAgentContextScenarioAsync()
    {
        Section("S39 服务器 agent 上下文卫生（默认单指令隔离 / //接着 / //reset 清干净）");

        const int openAiPort = 17861;
        const int agentAiPort = 17862;
        const int botWsPort = 13081;
        const int healthPort = 18131;
        const long groupId = 66790;
        const long ownerId = 20002;
        const string token = "tok-s39-secret";

        var dataDir = NewDataDir("s39");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        using var agentAi = new MockOpenAi(agentAiPort);
        agentAi.Start();

        // 第一轮：查东西（留下 bash 工具步骤 = 上下文噪音）
        agentAi.AddRule("先数一下东西",
            """{"thought":"跑一条看看","tool":"bash","command":"echo noise-mark-777"}""",
            """{"final":"第一轮结论-MARK-AAA"}""");

        // 这一轮：做 QQ 动作（事故里就是这一步被上文带偏）
        agentAi.AddRule("给老王点个赞",
            """{"tool":"qq","action":"like","user_id":20002,"times":1}""",
            """{"final":"点完了-MARK-BBB"}""");

        // 散文首轮：不是 JSON → 应该被纠正一次后真的调工具
        agentAi.AddRule("帮我看看谁在忙",
            "好的，我看看服务器状态。",
            """{"tool":"qq","action":"poke","user_id":20002}""",
            """{"final":"戳了-PROSE-OK"}""");

        // 打开开关之后的两轮：第二轮要能看见第一轮的结论
        agentAi.AddRule("再数一次东西",
            """{"thought":"再来一条","tool":"bash","command":"echo noise-mark-888"}""",
            """{"final":"第三轮结论-MARK-DDD"}""");
        agentAi.AddRule("给老王再点个赞",
            """{"tool":"qq","action":"like","user_id":20002,"times":2}""",
            """{"final":"又点完了-MARK-EEE"}""");

        // //接着 …（前缀会被剥掉，模型看到的是剥完的那句）
        agentAi.AddRule("再来一次点赞",
            """{"tool":"qq","action":"like","user_id":20002,"times":1}""",
            """{"final":"接着点的-MARK-FFF"}""");

        // 清空之后再问一句：这一轮的请求里不该再有前面的痕迹
        agentAi.AddRule("清空后再说一句",
            """{"final":"第四轮结论-MARK-CCC"}""");

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
            ["QQCHAT_HEALTH_PORT"] = healthPort.ToString(),
            ["QQCHAT_AGENT"] = "1",
            ["QQCHAT_AGENT_USERS"] = ownerId.ToString(),
            ["QQCHAT_AGENT_TOKEN"] = token,
            ["QQCHAT_AGENT_SERVER"] = "1",
            ["QQCHAT_AGENT_TARGET"] = "server",
            ["QQCHAT_AGENT_SERVER_URL"] = agentAi.BaseUrl,
            ["QQCHAT_AGENT_SERVER_MODEL"] = "custom-agent-model",
            ["QQCHAT_AGENT_SERVER_TOOLS"] = "bash,read,qq",
            ["QQCHAT_AGENT_PROGRESS"] = "0"
            // 故意不设 QQCHAT_AGENT_SERVER_CONTEXT：验证“默认 = 每条指令单独对待”
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(healthPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        var panel = $"http://127.0.0.1:{healthPort}";

        List<string> Sent() => protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        // 取“含某个标记的那一**次**请求”的全文：默认取最早的（含种子历史的那一次），
        // last: true 取最新的（含本轮自己工具输出的那一次）。
        string RequestWith(string marker, bool last = false)
        {
            if (last)
            {
                for (var i = agentAi.Requests.Count - 1; i >= 0; i--)
                {
                    var text = agentAi.DescribeRequest(i);
                    if (text.Contains(marker, StringComparison.Ordinal))
                    {
                        return text;
                    }
                }

                return string.Empty;
            }

            for (var i = 0; i < agentAi.Requests.Count; i++)
            {
                var text = agentAi.DescribeRequest(i);
                if (text.Contains(marker, StringComparison.Ordinal))
                {
                    return text;
                }
            }

            return string.Empty;
        }

        // 等“这一轮跑完”的可靠信号是日志里的 agent 完成/失败（不是某条回话 —— 先来的是“收到，我在服务器上跑一下”）
        async Task SendAndWaitAsync(string text, long messageId)
        {
            var before = bot.OutputLines.Count(l => l.Contains("agent 完成") || l.Contains("agent 失败"));
            await protocol.SendGroupMessageAsync(groupId, ownerId, "老王", text, messageId, mentionBot: false, ct: cts.Token);
            await WaitUntilAsync(
                () => bot.OutputLines.Count(l => l.Contains("agent 完成") || l.Contains("agent 失败")) > before,
                TimeSpan.FromSeconds(90));
            await Task.Delay(500);   // 让 _serverAgentBusy 清掉，不然下一句会被“还在跑”挡住
        }

        // ── ① 默认：每条指令单独对待 ──
        await SendAndWaitAsync("//先数一下东西", 18001);
        Check("第一轮跑完了（它留下了 bash 工具步骤，正是会污染上下文的那种历史）",
            Sent().Any(t => t.Contains("第一轮结论-MARK-AAA")), string.Join(" | ", Sent().TakeLast(3)));

        var likesBefore = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_like");
        await SendAndWaitAsync("//给老王点个赞", 18002);
        Check("★ 工具照样调（新指令不被上文带偏，QQ 动作真的发出去了）",
            protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_like") > likesBefore,
            string.Join(" | ", Sent().TakeLast(3)));

        var isolated = RequestWith("给老王点个赞");
        Check("★★ 默认不带上文：第二次请求里**没有**上一轮的指令、结论、工具噪音",
            isolated.Length > 0 &&
            !isolated.Contains("先数一下东西") &&
            !isolated.Contains("第一轮结论-MARK-AAA") &&
            !isolated.Contains("noise-mark-777") &&
            !isolated.Contains("工具输出（"),
            isolated.Length == 0
                ? "(没找到那次请求)"
                : $"含上轮指令={isolated.Contains("先数一下东西")} 含上轮结论={isolated.Contains("第一轮结论-MARK-AAA")} " +
                  $"含工具噪音={isolated.Contains("noise-mark-777") || isolated.Contains("工具输出（")}");

        // ── ② 面板打开“记住上下文” → 能接着聊，但只带结论不带工具步骤 ──
        var (code, _) = await PanelPostJsonAsync($"{panel}/api/settings", """{"agentServerKeepContext":true}""");
        Check("面板能打开「服务器 agent 记住上下文」", code == 200, $"HTTP {code}");

        await Task.Delay(400);
        await SendAndWaitAsync("//再数一次东西", 18003);
        Check("（开关打开后）第三轮跑完", Sent().Any(t => t.Contains("第三轮结论-MARK-DDD")),
            string.Join(" | ", Sent().TakeLast(2)));

        await SendAndWaitAsync("//给老王再点个赞", 18004);
        var carried = RequestWith("给老王再点个赞");
        Check("★★ 开关打开后能接着聊：上一轮的**结论**在上下文里",
            carried.Length > 0 && carried.Contains("第三轮结论-MARK-DDD"),
            carried.Length == 0 ? "(没找到那次请求)" : $"含上轮结论={carried.Contains("第三轮结论-MARK-DDD")}");
        Check("★★ 但工具步骤仍然不进上下文（bash 命令 / 工具输出都没有）",
            carried.Length > 0 && !carried.Contains("noise-mark-888") && !carried.Contains("工具输出（"),
            $"含工具噪音={carried.Contains("noise-mark-888") || carried.Contains("工具输出（")}");

        // 关回去（后面的步骤要验默认隔离）
        using (var http = CreatePanelHttpClient(healthPort, 15))
        {
            await http.PostAsync($"{panel}/api/settings",
                new StringContent("""{"agentServerKeepContext":false}""", Encoding.UTF8, "application/json"), cts.Token);
        }

        await Task.Delay(400);

        // ── ③ 单条 //接着 …（开关是关的，照样能接上文）──
        var continuesBefore = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_like");
        await SendAndWaitAsync("//接着再来一次点赞", 18005);
        Check("★ //接着 … 单条就能接上文（不用改面板开关）",
            protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_like") > continuesBefore &&
            Sent().Any(t => t.Contains("接着点的-MARK-FFF")),
            string.Join(" | ", Sent().TakeLast(2)));
        var continueReq = RequestWith("再来一次点赞");
        Check("★ 而且那一轮确实带了上文（能看到上一轮的结论）",
            continueReq.Length > 0 && continueReq.Contains("MARK-EEE"),
            continueReq.Length == 0 ? "(没找到那次请求)" : $"含上一轮结论={continueReq.Contains("MARK-EEE")}");

        // ── ④ 散文回复 → 纠正一次 → 真调工具 ──
        var pokesBefore = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "group_poke");
        await SendAndWaitAsync("//帮我看看谁在忙", 18006);
        Check("★ 模型拿散文答话时先纠正一次 —— 工具最终还是调了（事故里是散文被当结论、0 次工具调用）",
            protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "group_poke") > pokesBefore,
            string.Join(" | ", Sent().TakeLast(3)));
        Check("★ 纠正时明确告诉它“只输出一行 JSON”",
            RequestWith("不是 JSON").Contains("不是 JSON"),
            $"agent 接口请求 {agentAi.Requests.Count} 次");
        Check("★ 散文没有被原样发回群里",
            Sent().All(t => !t.Contains("好的，我看看服务器状态")), string.Join(" | ", Sent().TakeLast(3)));

        // ── ⑤ //reset 真的清干净（含标题），哪怕命令里写着 @某台不在线的设备 ──
        var beforeReset = Sent().Count;
        await protocol.SendGroupMessageAsync(groupId, ownerId, "老王", "//@ghost reset", 18007, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Count > beforeReset && Sent().Last().Contains("已清空"), TimeSpan.FromSeconds(30));
        await Task.Delay(500);

        var resetReply = Sent().LastOrDefault(t => t.Contains("已清空")) ?? string.Empty;
        Check("★ //reset 说清楚了清的是哪几份、清掉了多少（历史 + 执行记录）",
            resetReply.Contains("服务器") && (resetReply.Contains("条历史") || resetReply.Contains("条执行记录")),
            resetReply.Length > 0 ? resetReply.Replace("\n", " ⏐ ") : "(没回话)");

        await SendAndWaitAsync("//清空后再说一句", 18008);
        var afterReset = RequestWith("清空后再说一句");
        Check("★★ //reset 之后真正在用的那份会话是空的（前四轮痕迹全没了）",
            afterReset.Length > 0 &&
            !afterReset.Contains("MARK-") &&
            !afterReset.Contains("noise-mark-"),
            afterReset.Length == 0
                ? "(没找到那次请求)"
                : string.Join(" / ", new[] { "AAA", "BBB", "DDD", "EEE", "FFF" }
                    .Select(tag => $"{tag}={afterReset.Contains($"MARK-{tag}")}")));

        var (_, sessionsJson) = await PanelGetAsync($"{panel}/api/agent/sessions?key=group:{groupId}");
        var sessions = (JsonNode.Parse(sessionsJson) as JsonObject)?["sessions"] as JsonArray ?? new JsonArray();
        var names = sessions.Select(s => s?["nameRaw"]?.GetValue<string>() ?? string.Empty).ToList();
        Check("★★ //reset 连标题一起换掉（不再挂着旧话题）",
            names.Count > 0 && names.All(n => !n.Contains("服务器资源")),
            string.Join(" | ", names));

        // ── ⑥ //status 对写错的设备名说实话 ──
        var beforeStatus = Sent().Count;
        await protocol.SendGroupMessageAsync(groupId, ownerId, "老王", "//@ghost status", 18009, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Count > beforeStatus && Sent().Last().Contains("当前会走"), TimeSpan.FromSeconds(30));
        await Task.Delay(400);

        var status = Sent().LastOrDefault(t => t.Contains("当前会走")) ?? string.Empty;
        Check("★★ //status 不再骗人：@某台不在线的设备时如实说会走服务器、并提示那台没在用",
            status.Contains("当前会走：服务器 agent") && status.Contains("ghost") && status.Contains("没在用"),
            status.Length > 0 ? status.Replace("\n", " ⏐ ") : "(没收到状态)");

        Check("★ 全程没有“不认识的动作 / 工具执行出错”这类回话",
            Sent().All(t => !t.Contains("不认识的动作") && !t.Contains("工具执行出错")),
            string.Join(" | ", Sent().TakeLast(3)));
    }
}
