using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S34 服务器内置 agent + 路由（号主 2026-09-17 追加的诉求）：
///   • bot 自己也要有 agent 能力（跑在容器里的工具循环：bash / 读 / 写 / 抓网页）；
///   • 内外 agent 公用**同一份**白名单（只认 QQ 号）；
///   • 能看外部设备在不在线；自动路由：外部在线走外部，不在线用服务器内置；
///   • 外部设备可自己设置（AgentTarget 能写设备名，多台设备也能指定）。
///
/// 这里钉的五件事：
///   ① 没有外部设备时 `//` 走服务器 agent，**bash 真的在容器里跑**（工具输出被喂回模型）；
///   ② auto：外部设备在线 → 走外部（桥收到任务），服务器 agent 不动；
///   ③ 强制 server：外部在线也走服务器；
///   ④ 强制 host 但设备不在线 → 如实报错（不偷偷改道）；
///   ⑤ //status 一眼能看到两边状态与当前会走哪边。
/// </summary>
public static partial class Program
{
    private static async Task RunServerAgentScenarioAsync()
    {
        Section("S34 服务器内置 agent + 路由（内外公用白名单）");

        const int openAiPort = 17843;
        const int agentAiPort = 17844;      // 服务器 agent 的**自定义接口**（另一个假网关）
        const int botWsPort = 13065;
        const int healthPort = 18104;
        const long groupId = 66740;
        const string token = "tok-s34-secret";

        var dataDir = NewDataDir("s34");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        // 服务器 agent 专用的假网关：验证“自定义 url + 模型”真的生效（请求打到这边、带自定义模型名）
        using var agentAi = new MockOpenAi(agentAiPort);
        agentAi.Start();
        agentAi.EnqueueReply("""{"thought":"先看看容器里能不能跑命令","tool":"bash","command":"echo hello-server-agent"}""");
        agentAi.EnqueueReply("""{"thought":"拿到输出了","final":"服务器上跑完了：hello-server-agent"}""");
        // 后面几步也会用到服务器 agent（关掉外部开关那轮、//stop 那个长任务）
        agentAi.EnqueueReply("""{"final":"关掉外部开关后的结论"}""");
        agentAi.EnqueueReply("""{"thought":"慢慢想","tool":"bash","command":"sleep 30"}""");
        // 注意：//stop 取消那一轮**不会**再消费下一条回复（直接在循环里结束）——这里别多排，
        // 否则后面的阶段会拿到错位的回复（踩过一次）
        // 会话测试（服务器后端）：第一句 / 第二句 / 新会话一句 / 外加一条备用
        agentAi.EnqueueReply("""{"final":"第一句的结论"}""");
        agentAi.EnqueueReply("""{"final":"第二句的结论"}""");
        agentAi.EnqueueReply("""{"final":"新会话的结论"}""");
        agentAi.EnqueueReply("""{"final":"备用结论"}""");

        // 服务器 agent 的两步：先调 bash，再给结论
        // 聊天那条路（人设协议）在本场景用不到 —— 所有消息都是 // 命令


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
            ["QQCHAT_AGENT_USERS"] = "20002",
            ["QQCHAT_AGENT_TOKEN"] = token,
            ["QQCHAT_AGENT_SERVER"] = "1",
            ["QQCHAT_AGENT_TARGET"] = "auto",
            ["QQCHAT_AGENT_SERVER_WORKDIR"] = "/tmp",
            ["QQCHAT_AGENT_SERVER_URL"] = agentAi.BaseUrl,      // ← 服务器 agent 走自己的接口
            ["QQCHAT_AGENT_SERVER_MODEL"] = "custom-agent-model",
            ["QQCHAT_AGENT_PROGRESS"] = "0"      // 这轮只看结果，不看进度
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(healthPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        List<string> Sent() => protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        // ---- ① 没有外部设备：auto → 服务器内置 agent，bash 真的跑 ----
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//服务器上来一句", 16001, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("hello-server-agent")), TimeSpan.FromSeconds(60));
        await Task.Delay(500);

        Check("★ 收到指令先回一句“在服务器上跑一下”",
            Sent().Any(t => t.Contains("在服务器上跑一下")), string.Join(" | ", Sent()));
        Check("★ 服务器 agent 的结论发回了群里",
            Sent().Any(t => t.Contains("服务器上跑完了")), string.Join(" | ", Sent()));
        Check("★ 服务器 agent 走的是**自定义接口**（请求落在另一个网关上）",
            agentAi.Requests.Count >= 1 &&
            // 聊天网关上最多只允许“综结标题”那一个请求（那是聊 models 的事，不是 agent 的活）
            Enumerable.Range(0, openAi.Requests.Count).All(i => openAi.DescribeRequest(i).Contains("[会话标题]")),
            $"自定义接口 {agentAi.Requests.Count} 次请求，聊天接口 {openAi.Requests.Count} 次（应为 0 或只有综结标题）");
        await WaitUntilAsync(() => openAi.TitleRequests > 0, TimeSpan.FromSeconds(30));
        await Task.Delay(300);
        using (var titleHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            var sj = JsonNode.Parse(await titleHttp.GetStringAsync($"http://127.0.0.1:{healthPort}/api/agent/sessions?key=group:{groupId}"));
            var titleSessions = sj?["sessions"] as JsonArray ?? new JsonArray();
            Check("★ 服务器后端也会按上下文综结会话标题（与外部设备那条路共用一个综结逻辑）",
                titleSessions.Any(s => s!["name"]?.GetValue<string>() == "综结出来的会话标题"),
                string.Join(" | ", titleSessions.Select(s => $"{s!["name"]}(auto={s!["autoNamed"]})")));
        }
        Check("★ 自定义接口那轮带的就是自定义模型名",
            agentAi.Requests.Count > 0 && agentAi.Requests[0].ToJsonString().Contains("custom-agent-model"),
            agentAi.Requests.Count == 0 ? "(没请求)" : Snippet(agentAi.Requests[0].ToJsonString(), "custom-agent-model"));
        Check("★ 服务器 agent 也记了耗时与工具次数（面板上看得见）",
            bot.OutputLines.Any(l => l.Contains("agent 完成") && l.Contains("1 次工具调用")),
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("agent 完成")).TakeLast(2)));

        // 关键证据：模型的**第二次**请求里带着 bash 的真实输出 —— 说明命令真在容器里跑了
        // （现在服务器 agent 走自定义接口，所以看 agentAi）
        var secondRequest = agentAi.Requests.Skip(1).FirstOrDefault();
        Check("★ bash 真的执行了（工具输出被喂回模型：第二次请求里能看到 hello-server-agent）",
            secondRequest is not null && UserTexts(secondRequest).Any(t => t.Contains("hello-server-agent")),
            secondRequest is null ? "(只有一次请求)" : Snippet(UserTexts(secondRequest).LastOrDefault() ?? "(空)", "hello-server-agent"));
        Check("★ 服务器 agent 那轮读了 settings 里的白名单（不是消息白名单）",
            bot.OutputLines.Any(l => l.Contains("agent 命令（20002）")),
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("agent 命令")).TakeLast(2)));

        // ---- ② auto + 外部设备在线 → 走外部 ----
        using var bridge = new MockAgentBridge($"ws://127.0.0.1:{healthPort}/agent-bridge?token={token}",
            new JsonObject { ["host"] = "S34-DEVICE", ["cwd"] = "E:/bot", ["pi"] = "test-2.0" });
        var connected = await bridge.TryConnectAsync(cts.Token);
        Check("外部设备连上了（后面用它验路由）", connected, connected ? "ok" : "没连上");
        await Task.Delay(300);

        var requestsBefore = openAi.Requests.Count;
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//这条应该走外部", 16002, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => bridge.Tasks.Count > 0, TimeSpan.FromSeconds(30));
        Check("★ auto：外部设备在线时任务派给了外部（不是服务器）",
            bridge.Tasks.Count == 1 && openAi.Requests.Count == requestsBefore,
            $"桥任务 {bridge.Tasks.Count} 个；模型请求 {requestsBefore} → {openAi.Requests.Count}");
        Check("★ 外部任务带回话里的设备名",
            Sent().Any(t => t.Contains("去S34-DEVICE上跑一下")), string.Join(" | ", Sent().TakeLast(3)));

        // 把这个外部任务收尾：不然它一直“在跑”，后面的任务会被串行门挡在外面
        bridge.Send(new JsonObject
        {
            ["type"] = "done",
            ["id"] = bridge.Tasks[^1]["id"]!.GetValue<string>(),
            ["text"] = "外部设备的结论",
            ["exitCode"] = 0,
            ["durationMs"] = 800,
            ["toolCalls"] = 0
        });
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("外部设备的结论")), TimeSpan.FromSeconds(30));

        // ---- ③ 强制 server（群命令里单条指定）：外部在线也走服务器 ----
        var bridgeTasksBefore = bridge.Tasks.Count;
        var modelBefore = openAi.Requests.Count;
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//@server 这条强制走服务器", 16003, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => openAi.Requests.Count > modelBefore, TimeSpan.FromSeconds(40));
        Check("★ //@server 单条指定：外部在线也不用它（桥那边一个任务都没收到）",
            bridge.Tasks.Count == bridgeTasksBefore,
            $"桥任务 {bridgeTasksBefore} → {bridge.Tasks.Count}");

        // ---- ③b 面板里把“外部设备”开关关掉 → auto 也得走服务器（号主踩过的那个：切了却没生效）----
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            var body = new StringContent("{\"agentTarget\":\"auto\",\"enableHostAgent\":false}", Encoding.UTF8, "application/json");
            await http.PostAsync($"http://127.0.0.1:{healthPort}/api/settings", body);
        }

        await Task.Delay(400);
        var hostOffTasksBefore = bridge.Tasks.Count;
        var hostOffModelBefore = openAi.Requests.Count;
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//关掉外部开关后这条", 16008, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => openAi.Requests.Count > hostOffModelBefore, TimeSpan.FromSeconds(40));
        Check("★ 关掉“外部设备”开关后，auto 真的走服务器（不是还打给外部设备）",
            bridge.Tasks.Count == hostOffTasksBefore,
            $"桥任务 {hostOffTasksBefore} → {bridge.Tasks.Count}");

        // ---- ③c 设备开关打开时：//@设备名 能单条指定外部设备 ----
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            var body = new StringContent("{\"agentTarget\":\"auto\",\"enableHostAgent\":true}", Encoding.UTF8, "application/json");
            await http.PostAsync($"http://127.0.0.1:{healthPort}/api/settings", body);
        }

        await Task.Delay(400);
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//@S34-DEVICE 单条指定外部", 16009, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => bridge.Tasks.Count > hostOffTasksBefore, TimeSpan.FromSeconds(30));
        Check("★ //@设备名 能单条指定外部设备（多台设备时改接哪台）",
            bridge.Tasks.Count > hostOffTasksBefore,
            $"桥任务 {hostOffTasksBefore} → {bridge.Tasks.Count}；" + string.Join(" | ", Sent().TakeLast(2)));

        // ---- ④ 两边都不可用 → 如实报错（不静默）----
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            var body = new StringContent("{\"agentTarget\":\"host\",\"enableHostAgent\":true,\"enableServerAgent\":false}", Encoding.UTF8, "application/json");
            await http.PostAsync($"http://127.0.0.1:{healthPort}/api/settings", body);
        }

        bridge.Dispose();      // 设备下线
        await Task.Delay(1200);

        var modelBeforeHost = openAi.Requests.Count;
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//设备不在线这条", 16004, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("没法跑")), TimeSpan.FromSeconds(40));
        Check("★ 两边都不可用时如实报错（服务器开关关、外部离线；不会被静默接走）",
            Sent().Any(t => t.Contains("没法跑")) && openAi.Requests.Count == modelBeforeHost,
            string.Join(" | ", Sent().TakeLast(2)) + $"（模型请求 {modelBeforeHost} → {openAi.Requests.Count}）");

        Section("-- S34 后半：//stop / //status --");

        // ---- ⑤ //stop 能停服务器 agent（不然它就只能等步数用完）----
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//跑个很久的活", 16006, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("在服务器上跑一下")), TimeSpan.FromSeconds(30));
        await Task.Delay(1500);
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//stop", 16007, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("已让它停掉")), TimeSpan.FromSeconds(30));
        Check("★ //stop 能停服务器 agent 的任务（不是只对外部设备管用）",
            Sent().Any(t => t.Contains("已让它停掉")), string.Join(" | ", Sent().TakeLast(3)));

        // ---- ⑧ //status 一眼看到两边状态 ----
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            var body = new StringContent("{\"agentTarget\":\"auto\",\"enableHostAgent\":true,\"enableServerAgent\":true}", Encoding.UTF8, "application/json");
            await http.PostAsync($"http://127.0.0.1:{healthPort}/api/settings", body);
        }

        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//status", 16005, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("服务器内置 agent：")), TimeSpan.FromSeconds(30));
        var status = Sent().LastOrDefault(t => t.Contains("服务器内置 agent：")) ?? string.Empty;
        Check("★ //status 同时报出两个开关、在线设备与当前会走哪边",
            status.Contains("外部设备 agent：") && status.Contains("服务器内置 agent：") &&
            status.Contains("当前会走：") && status.Contains("单条指定："),
            $"最后几条发出去的是：{string.Join(" ⏐ ", Sent().TakeLast(5))}");

        // ---- ⑦ 会话（服务器后端）：同一会话接着聊、//new 开新的就断上下文 ----
        // 号主要求“调用内置/外部 agent 时能自由切换会话”——内置这一侧的会话就是我们自己存的历史
        // 前面的步骤把服务器开关关过，这里先打开（不然 //@server 只会回“开关是关的”）
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            var body = new StringContent("{\"enableServerAgent\":true,\"agentTarget\":\"server\"}", Encoding.UTF8, "application/json");
            await http.PostAsync($"http://127.0.0.1:{healthPort}/api/settings", body);
        }

        await Task.Delay(400);

        // 等“这一轮跑完”的可靠信号是日志里的 agent 完成（不是脚本回复的内容 —— 队列可能被前面阶段吃掉）
        var doneBefore = bot.OutputLines.Count(l => l.Contains("agent 完成"));
        async Task SendServerAndWaitAsync(string text, long mid)
        {
            var before = bot.OutputLines.Count(l => l.Contains("agent 完成"));
            await protocol.SendGroupMessageAsync(groupId, 20002, "老王", text, mid, mentionBot: false, ct: cts.Token);
            await WaitUntilAsync(() => bot.OutputLines.Count(l => l.Contains("agent 完成")) > before, TimeSpan.FromSeconds(90));
            await Task.Delay(300);   // 让 _serverAgentBusy 清掉，不然下一句会被“还在跑”挡住
        }

        await SendServerAndWaitAsync("//@server 会话测试第一句", 16020);

        var beforeSecond = agentAi.Requests.Count;
        await SendServerAndWaitAsync("//@server 会话测试第二句", 16021);

        var secondTurnRequest = agentAi.Requests.Skip(beforeSecond)
            .LastOrDefault(r => AllTexts(r).Any(t => t.Contains("会话测试第二句")));
        Check("★ 同一会话里接着聊：上一轮的对话会带进这一轮（内置 agent 也记得）",
            secondTurnRequest is not null && AllTexts(secondTurnRequest).Any(t => t.Contains("会话测试第一句")),
            secondTurnRequest is null
                ? $"没找到含「会话测试第二句」的请求（自定义接口共 {agentAi.Requests.Count} 次）"
                : $"这一轮带了 {AllTexts(secondTurnRequest).Count} 条文本：[{string.Join(" ⏐ ", AllTexts(secondTurnRequest).Select(t => t.Length > 22 ? t[..22] : t))}]");

        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//@server new 断上下文", 16022, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("已开新会话")), TimeSpan.FromSeconds(30));
        await Task.Delay(300);

        var requestsBeforeFresh = agentAi.Requests.Count;
        await SendServerAndWaitAsync("//@server 新会话第一句", 16023);
        var freshRequest = agentAi.Requests.Skip(requestsBeforeFresh).FirstOrDefault();
        Check("★ //new 之后是空上下文（不再带着旧会话的历史）",
            freshRequest is not null &&
            !AllTexts(freshRequest).Any(t => t.Contains("会话测试第一句")),
            $"新会话那次请求：{string.Join(" ⏐ ", AllTexts(freshRequest ?? new JsonObject()).Select(t => t.Length > 30 ? t[..30] + "…" : t))}");

        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//sessions", 16024, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("agent 会话")), TimeSpan.FromSeconds(30));
        Check("★ //sessions 里能看到内置会话及其轮数",
            Sent().Any(t => t.Contains("agent 会话") && t.Contains("服务器内置")),
            string.Join(" | ", Sent().TakeLast(2)));

        await bot.StopAsync();
    }

    /// <summary>请求里**所有** role 的文本（含 assistant 上一轮）——验“会话历史带过来了”要用它（UserTexts 只看 user）。</summary>
    private static List<string> AllTexts(JsonObject request)
        => request["messages"]?.AsArray()
               .Select(m => m?["content"] is JsonArray parts
                   ? string.Concat(parts.Where(p => p?["type"]?.GetValue<string>() == "text")
                                        .Select(p => p?["text"]?.GetValue<string>()))
                   : m?["content"]?.GetValue<string>() ?? string.Empty)
               .Where(t => t.Length > 0)
               .ToList() ?? new List<string>();
}
