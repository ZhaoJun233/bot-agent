using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S33 本机 Agent 桥（// 命令，handoff-4 §31）。
///
/// 这个功能能在号主电脑上执行命令，所以边界必须一条条钉死：
///   ① 只有 `//` 开头的消息才进 agent（其它消息照旧走人设路线，不能被顺手当成 agent 任务）；
///   ② 发送者不在 AgentAllowedUsers 里 → 拒绝，**不把任务发给本机**；
///   ③ 桥没连上 / 没配令牌 → 说人话，而不是静默；
///   ④ 令牌不对的桥连接要被拒（这是唯一能拦住陌生人的东西）；
///   ⑤ 正常任务：ack → 工具进度 → 结果回群，且长输出按上限切分；
///   ⑥ `//stop` 能取消本机正在跑的任务（否则一条跑飞的命令占着电脑关不掉）。
/// </summary>
public static partial class Program
{
    private static async Task RunAgentBridgeScenarioAsync()
    {
        Section("S33 本机 Agent（// 命令）：前缀 / 用户白名单 / 令牌 / 结果回群");

        const int openAiPort = 17842;
        const int botWsPort = 13064;
        const int healthPort = 18103;
        const long groupId = 66730;
        const string token = "tok-s33-secret";

        var dataDir = NewDataDir("s33");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        // 人设路线：只有第 3 步那条普通消息会用到它
        openAi.EnqueueReply("""{"suitability": 80, "reply": "普通人设回话"}""");

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
            // ---- 本机 Agent ----
            ["QQCHAT_AGENT"] = "1",
            ["QQCHAT_AGENT_USERS"] = "20002",           // 老王能用；小李(20003) 不能
            ["QQCHAT_AGENT_SERVER"] = "0",             // 本场景专门验**外部设备**那条路（服务器 agent 由 S34 负责）
            ["QQCHAT_AGENT_TARGET"] = "host",          // 只走外部：离线就如实报错，不偷偷改道
            ["QQCHAT_AGENT_TIMEOUT"] = "120",
            ["QQCHAT_AGENT_REPLY_CHARS"] = "200",
            ["QQCHAT_AGENT_PROGRESS"] = "30",
            ["QQCHAT_AGENT_TOKEN"] = token,
            ["QQCHAT_AGENT_WORKDIR"] = "E:/bot"
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(healthPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        var sends = new List<string>();
        List<string> Sent() => protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        // ---- ① 令牌不对的桥连接：必须被拒 ----
        using var badBridge = new MockAgentBridge($"ws://127.0.0.1:{healthPort}/agent-bridge?token=wrong");
        var badConnected = await badBridge.TryConnectAsync(cts.Token);
        Check("★ 令牌不对的桥连接被拒（这个端口能在号主电脑上执行命令）",
            !badConnected, badConnected ? "竟然连上了" : "已拒绝");
        Check("★ 令牌不对时日志留痕",
            await WaitUntilAsync(() => bot.OutputLines.Any(l => l.Contains("agent 桥连接被拒")), TimeSpan.FromSeconds(20)),
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("agent 桥")).TakeLast(2)));

        // ---- ② 外部设备没连上：// 命令要回一句人话（而不是静默） ----
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//看看磁盘", 15001, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("不在线")), TimeSpan.FromSeconds(30));
        Check("★ 外部设备不在线时回一句人话（不静默、也不当成聊天接）",
            Sent().Any(t => t.Contains("不在线")), string.Join(" | ", Sent()));

        // ---- ③ 正确令牌的桥连上来 ----
        using var bridge = new MockAgentBridge($"ws://127.0.0.1:{healthPort}/agent-bridge?token={token}",
            new JsonObject
            {
                ["host"] = "DESKTOP-TEST",
                ["cwd"] = "E:/bot",
                ["pi"] = "test-1.0",
                // 设备上报它有哪些模型（面板里直接选，不用手敲）
                ["models"] = new JsonArray("vendor-a/model-1", "vendor-b/model-2", "vendor-c/model-3")
            });
        var connected = await bridge.TryConnectAsync(cts.Token);
        Check("★ 带对令牌的桥连上了", connected, connected ? "ok" : "没连上");
        await WaitUntilAsync(() => bot.OutputLines.Any(l => l.Contains("agent 桥已连接")), TimeSpan.FromSeconds(20));

        // ---- ④ 没有权限的用户：拒绝，且不产生任务 ----
        openAi.ClearRequests();
        await protocol.SendGroupMessageAsync(groupId, 20003, "小李", "//把 D 盘删了", 15002, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("只给白名单用户")), TimeSpan.FromSeconds(30));
        Check("★ 非白名单用户被拒（并回了为什么）",
            Sent().Any(t => t.Contains("只给白名单用户")), string.Join(" | ", Sent()));
        Check("★ 被拒的命令没有发给本机（桥侧一个任务都没收到）",
            bridge.Tasks.Count == 0, $"桥收到 {bridge.Tasks.Count} 个任务");
        Check("★ 被拒也留日志（谁试过要能查）",
            bot.OutputLines.Any(l => l.Contains("agent 命令被拒")),
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("agent 命令被拒")).TakeLast(1)));

        // ---- ⑤ 非 // 开头的消息：照旧走人设，不进 agent ----
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "今天天��不错", 15003, mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("普通人设回话")), TimeSpan.FromSeconds(30));
        Check("★ 不以 // 开头的消息不走 agent（照旧人设回话）",
            Sent().Any(t => t.Contains("普通人设回话")) && bridge.Tasks.Count == 0,
            string.Join(" | ", Sent()));

        // ---- ⑥ 正常任务：ack → 进度 → 结果 ----
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//看下现在有几张表情包", 15004, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => bridge.Tasks.Count > 0, TimeSpan.FromSeconds(30));

        var task = bridge.Tasks.Last();
        Check("★ 任务细节带对了：提示词 / 会话名 / 工作目录 / 超时",
            task["prompt"]?.GetValue<string>() == "看下现在有几张表情包" &&
            (task["session"]?.GetValue<string>() ?? "").StartsWith($"qqchat-group-{groupId}-", StringComparison.Ordinal) &&
            task["cwd"]?.GetValue<string>() == "E:/bot" &&
            task["timeoutSec"]?.GetValue<int>() == 120,
            task.ToJsonString());

        Check("★ 收到任务先回一句“去<设备>上跑一下”",
            Sent().Any(t => t.Contains("去DESKTOP-TEST上跑一下")), string.Join(" | ", Sent()));

        // 桥侧报一次“我在用工具” → 进度回群
        bridge.Send(new JsonObject { ["type"] = "progress", ["id"] = task["id"]!.GetValue<string>(), ["note"] = "🔧 跑命令" });
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("还在") && t.Contains("上跑")), TimeSpan.FromSeconds(40));
        Check("★ 任务跑得久时会把进度报回群（带设备名与工具名）",
            Sent().Any(t => t.Contains("上跑") && t.Contains("跑命令")), string.Join(" | ", Sent()));

        // 结果：故意给一段 500 字的长输出 → 必须按 AgentReplyMaxChars(200) 切分
        var longText = string.Join("\n", Enumerable.Range(1, 25).Select(i => $"第 {i} 行：表情包库里有 50 张，其中 47 张已描述。"));
        bridge.Send(new JsonObject
        {
            ["type"] = "done",
            ["id"] = task["id"]!.GetValue<string>(),
            ["text"] = longText,
            ["exitCode"] = 0,
            ["durationMs"] = 4200,
            ["toolCalls"] = 3
        });
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("表情包库里有 50 张")), TimeSpan.FromSeconds(40));
        await Task.Delay(1200);

        var agentSends = Sent().Where(t => t.Contains("表情包库里有 50 张")).ToList();
        Check("★ 长输出按上限切成多条（不会一条几千字把群刷爆）",
            agentSends.Count >= 2 && agentSends.All(t => t.Length <= 220),
            $"条数 {agentSends.Count}，最长 {(agentSends.Count == 0 ? 0 : agentSends.Max(t => t.Length))} 字");
        Check("★ agent 完成记进日志（时长/工具次数）",
            bot.OutputLines.Any(l => l.Contains("agent 完成") && l.Contains("4s")),
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("agent 完成")).TakeLast(2)));

        // ---- ⑦ //stop 取消：桥必须收到 cancel ----
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//跑一个很久的任务", 15005, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => bridge.Tasks.Count >= 2, TimeSpan.FromSeconds(30));
        var running = bridge.Tasks.Last();
        bridge.Send(new JsonObject { ["type"] = "started", ["id"] = running["id"]!.GetValue<string>() });
        await Task.Delay(300);
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//stop", 15006, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => bridge.Cancels.Count > 0, TimeSpan.FromSeconds(30));
        Check("★ //stop 真的通知了本机（否则那条命令一直占着电脑）",
            bridge.Cancels.Contains(running["id"]!.GetValue<string>()),
            string.Join(" | ", bridge.Cancels));
        Check("★ //stop 回了话",
            Sent().Any(t => t.Contains("已让它停掉")), string.Join(" | ", Sent().TakeLast(3)));

        // ---- ⑧ //status：能看到桥的连接状态 ----
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//status", 15007, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("服务器内置 agent：")), TimeSpan.FromSeconds(30));
        Check("★ //status 报出两个开关、优先项与在线的外部设备详情（设备名/pi 版本/目录）",
            string.Join("\n", Sent().TakeLast(3)).Contains("外部设备 agent：") &&
            string.Join("\n", Sent().TakeLast(3)).Contains("DESKTOP-TEST") &&
            string.Join("\n", Sent().TakeLast(3)).Contains("test-1.0") &&
            string.Join("\n", Sent().TakeLast(3)).Contains("服务器内置 agent："),
            string.Join(" | ", Sent().TakeLast(3)));

        // ---- ⑩ 改成“白名单会话里所有人都能用”（AgentAllowedUsers = *）→ 热生效 ----
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            var body = new StringContent("{\"agentAllowedUsers\":\"*\"}", Encoding.UTF8, "application/json");
            await http.PostAsync($"http://127.0.0.1:{healthPort}/api/settings", body);
        }

        await Task.Delay(500);
        var beforeStar = bridge.Tasks.Count;
        await protocol.SendGroupMessageAsync(groupId, 20003, "小李", "//再数一下文件", 15008, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => bridge.Tasks.Count > beforeStar, TimeSpan.FromSeconds(30));
        Check("★ 把 AgentAllowedUsers 改成 * 后，原本没权限的人也能用（改设置热生效）",
            bridge.Tasks.Count > beforeStar,
            $"任务数 {beforeStar} → {bridge.Tasks.Count}；" +
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("agent 命令")).TakeLast(2)));

        // 把上面那个任务收尾：不收尾它会一直占着“在跑”的名额（后面验串行下发会被挡住）
        bridge.Send(new JsonObject
        {
            ["type"] = "done",
            ["id"] = bridge.Tasks[^1]["id"]!.GetValue<string>(),
            ["text"] = "星号那单的结论",
            ["exitCode"] = 0,
            ["durationMs"] = 500,
            ["toolCalls"] = 0
        });
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("星号那单的结论")), TimeSpan.FromSeconds(30));

        // ---- ⑪ 面板接口 /api/agent/status ----
        JsonNode? status = null;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
        {
            status = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{healthPort}/api/agent/status"));
        }
        Check("★ 面板 /api/agent/status 能看到连接、白名单与设备模型列表",
            status?["enabled"]?.GetValue<bool>() == true &&
            status?["connected"]?.GetValue<bool>() == true &&
            status?["allowedUsers"]?.GetValue<string>() == "*" &&      // 上一步刚改成 *（改完要能看见）
            status?["tokenConfigured"]?.GetValue<bool>() == true &&
            status?["deviceModels"] is JsonArray dm && dm.Count == 3 &&
            dm.Any(m => m!.GetValue<string>() == "vendor-b/model-2"),
            status?.ToJsonString() ?? "(没拿到)");

        // ---- ⑪ 设备模型可选：面板里选中后，下发给 pi 的任务里带的就是那个模型 ----
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            var body = new StringContent("{\"agentModel\":\"vendor-b/model-2\"}", Encoding.UTF8, "application/json");
            await http.PostAsync($"http://127.0.0.1:{healthPort}/api/settings", body);
        }

        await Task.Delay(400);
        var beforeModelTask = bridge.Tasks.Count;
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//用选中的模型跑一句", 15030, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => bridge.Tasks.Count > beforeModelTask, TimeSpan.FromSeconds(30));
        Check("★ 面板选的模型会随任务下发给 pi（--model）",
            bridge.Tasks[^1]["model"]?.GetValue<string>() == "vendor-b/model-2",
            bridge.Tasks[^1].ToJsonString());

        // 收尾这个任务：不收尾它会占着“在跑”的名额，后面验串行下发会被挡住
        bridge.Send(new JsonObject
        {
            ["type"] = "done",
            ["id"] = bridge.Tasks[^1]["id"]!.GetValue<string>(),
            ["text"] = "选模型那单的结论",
            ["exitCode"] = 0,
            ["durationMs"] = 400,
            ["toolCalls"] = 0
        });
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("选模型那单的结论")), TimeSpan.FromSeconds(30));

        // ---- ⑫ 连续两个任务：结果都不能丢，而且要串行下发
        // （2026-09-17 线上事故：先发的那个结果被静默丢掉 —— 只查 _current 查不到属主；
        //   同时机器人不等上一个收尾就发下一个，桥回“本机还有任务在跑”，群里看着就是卡住）----
        var tasksBefore = bridge.Tasks.Count;
        var sendsBefore = Sent().Count;
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//连跑第一个", 15020, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => bridge.Tasks.Count > tasksBefore, TimeSpan.FromSeconds(30));
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//连跑第二个", 15021, mentionBot: false, ct: cts.Token);
        await Task.Delay(1500);
        Check("★ 上一个任务没收尾时不急着下发下一个（外部 pi 是串行跑的）",
            bridge.Tasks.Count - tasksBefore == 1, $"已下发 {bridge.Tasks.Count - tasksBefore} 个");

        var firstOfTwo = bridge.Tasks[^1];
        bridge.Send(new JsonObject
        {
            ["type"] = "done",
            ["id"] = firstOfTwo["id"]!.GetValue<string>(),
            ["text"] = "第一个任务的结论",
            ["exitCode"] = 0,
            ["durationMs"] = 1200,
            ["toolCalls"] = 1
        });
        await WaitUntilAsync(() => bridge.Tasks.Count - tasksBefore >= 2, TimeSpan.FromSeconds(30));
        Check("★ 第一个收尾之后才下发第二个",
            bridge.Tasks.Count - tasksBefore >= 2, $"已下发 {bridge.Tasks.Count - tasksBefore} 个");

        var secondOfTwo = bridge.Tasks[^1];
        bridge.Send(new JsonObject
        {
            ["type"] = "done",
            ["id"] = secondOfTwo["id"]!.GetValue<string>(),
            ["text"] = "第二个任务的结论",
            ["exitCode"] = 0,
            ["durationMs"] = 900,
            ["toolCalls"] = 0
        });
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("第二个任务的结论")), TimeSpan.FromSeconds(30));
        await Task.Delay(600);
        Check("★ 两个任务的结论都发回了群（先跑的那个不被静默丢弃）",
            Sent().Any(t => t.Contains("第一个任务的结论")) && Sent().Any(t => t.Contains("第二个任务的结论")),
            string.Join(" | ", Sent().Skip(sendsBefore)));

        // ---- ⑬ 每设备配置：一台设备可以单独配模型/目录（泛用性）----
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            var cfg = "[{\"name\":\"DESKTOP-TEST\",\"enable\":true,\"model\":\"vendor-a/model-1\"," +
                      "\"workdir\":\"E:/work\",\"tools\":\"read,fetch\",\"timeoutSec\":120}]";
            var payload = new JsonObject { ["agentDevices"] = cfg };
            var post = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            await http.PostAsync($"http://127.0.0.1:{healthPort}/api/settings", post);
        }

        await Task.Delay(400);
        var beforePerDevice = bridge.Tasks.Count;
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//用设备专属配置跑", 15040, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => bridge.Tasks.Count > beforePerDevice, TimeSpan.FromSeconds(30));
        var perDevice = bridge.Tasks[^1];
        Check("★ 每设备配置生效：模型/目录/工具/超时都按那台设备的来",
            perDevice["model"]?.GetValue<string>() == "vendor-a/model-1" &&
            perDevice["cwd"]?.GetValue<string>() == "E:/work" &&
            perDevice["tools"]?.GetValue<string>() == "read,fetch" &&
            perDevice["timeoutSec"]?.GetValue<int>() == 120,
            perDevice.ToJsonString());

        // 设备被面板关掉时：不派任务，并如实告诉群
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            var cfg = "[{\"name\":\"DESKTOP-TEST\",\"enable\":false}]\n".Trim();
            var payload = new JsonObject { ["agentDevices"] = cfg };
            var post = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            await http.PostAsync($"http://127.0.0.1:{healthPort}/api/settings", post);
        }

        await Task.Delay(400);
        bridge.Send(new JsonObject
        {
            ["type"] = "done",
            ["id"] = perDevice["id"]!.GetValue<string>(),
            ["text"] = "设备配置那单的结论",
            ["exitCode"] = 0,
            ["durationMs"] = 300,
            ["toolCalls"] = 0
        });
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("设备配置那单的结论")), TimeSpan.FromSeconds(30));

        var tasksBeforeDisabled = bridge.Tasks.Count;
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//设备关掉后这条", 15041, mentionBot: false, ct: cts.Token);
        await Task.Delay(2500);
        Check("★ 设备在面板里被关掉后，不会再派给它（并且桥没收到任务）",
            bridge.Tasks.Count == tasksBeforeDisabled,
            $"桥任务 {tasksBeforeDisabled} → {bridge.Tasks.Count}；" + string.Join(" | ", Sent().TakeLast(2)));

        // ---- ⑭ 一键连接：面板生成的脚本里带地址与令牌；桥脚本能下载 ----
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            var win = await http.GetStringAsync($"http://127.0.0.1:{healthPort}/api/agent/setup?os=win&host=bot.example.com");
            Check("★ 一键连接脚本（Windows）：带地址、带令牌、能直接跑",
                win.Contains("ws://bot.example.com/agent-bridge") && win.Contains(token) && win.Contains("pi-bridge.py"),
                win.Split('\n')[0]);

            var named = await http.GetStringAsync($"http://127.0.0.1:{healthPort}/api/agent/setup?os=win&host=bot.example.com&name=SOME-PC");
            Check("★ 一键连接脚本能把设备名写进去（面板填的名字 = 本机报上来的名字）",
                named.Contains("PI_BRIDGE_NAME=SOME-PC") && named.Contains(token),
                string.Join(" | ", named.Split('\n').Where(l => l.Contains("PI_BRIDGE_NAME"))));

            var sh = await http.GetStringAsync($"http://127.0.0.1:{healthPort}/api/agent/setup?os=sh&host=bot.example.com");
            Check("★ 一键连接脚本（Linux/Mac）：同一套内容",
                sh.Contains("#!/bin/sh") && sh.Contains("ws://bot.example.com/agent-bridge") && sh.Contains(token),
                sh.Split('\n')[0]);

            var bridgeScript = await http.GetStringAsync($"http://127.0.0.1:{healthPort}/agent-bridge-script");
            Check("★ 面板能下载桥脚本本体（pi-bridge.py，内嵌在 DLL 里）",
                bridgeScript.Contains("PI_BRIDGE_URL") && bridgeScript.Contains("def main()"),
                $"长度 {bridgeScript.Length}");
            Check("★ 桥脚本支持 --name / PI_BRIDGE_NAME（面板里按名字认设备）",
                bridgeScript.Contains("--name") && bridgeScript.Contains("PI_BRIDGE_NAME"),
                string.Join(" | ", bridgeScript.Split('\n').Where(l => l.Contains("PI_BRIDGE_NAME")).Take(2)).Trim());

            var status2 = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{healthPort}/api/agent/status"))!;
            var devices = devices2(status2);
            Check("★ 面板设备表：离线设备也能看到（能先配好再接入）",
                devices.Any(d => d["name"]?.GetValue<string>() == "DESKTOP-TEST" &&
                                 d["enable"]?.GetValue<bool>() == false),
                status2["deviceList"]?.ToJsonString() ?? "(没有 deviceList)");
        }

        // ---- ⑮ 设备配了它没有的模型 → 不能把活卡死：降级用 pi 默认 + 群里说一声 ----
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            // 先把上一个任务的“启用关掉”状态恢复，并故意配一个设备没有的模型（复刻线上那次报错）
            var cfg = "[{\"name\":\"DESKTOP-TEST\",\"enable\":true,\"model\":\"gpt-oss-120b-medium\"}]";
            var payload = new JsonObject { ["agentDevices"] = cfg };
            await http.PostAsync($"http://127.0.0.1:{healthPort}/api/settings",
                new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"));
        }

        await Task.Delay(400);
        var beforeBadModel = bridge.Tasks.Count;
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//模型配错了也要能跑", 15050, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => bridge.Tasks.Count > beforeBadModel, TimeSpan.FromSeconds(30));
        Check("★ 设备配的模型它自己没有时：不把任务卡死，改用 pi 默认（不再直接报 Model not found）",
            string.IsNullOrEmpty(bridge.Tasks[^1]["model"]?.GetValue<string>()),
            bridge.Tasks[^1].ToJsonString());
        Check("★ 这种降级会在群里说一句（号主能看出是面板里配错了）",
            Sent().Any(t => t.Contains("面板里给这台设备配的模型")),
            string.Join(" | ", Sent().TakeLast(3)));

        // 收尾：不收尾它会占着“在跑”的名额，后面的任务会被串行门挡住（这里踩过）
        bridge.Send(new JsonObject
        {
            ["type"] = "done",
            ["id"] = bridge.Tasks[^1]["id"]!.GetValue<string>(),
            ["text"] = "降级那单的结论",
            ["exitCode"] = 0,
            ["durationMs"] = 200,
            ["toolCalls"] = 0
        });
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("降级那单的结论")), TimeSpan.FromSeconds(30));

        // ---- ⑯ Agent 会话：//new 开新的（空上下文）、//use 切回去、//sessions 列表、面板 API 同步
        //（号主 2026-09-17：调用内部/外部 agent 时能自由切换/新建/删除会话，面板与群里都要能操作）----
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//sessions", 15060, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("agent 会话")), TimeSpan.FromSeconds(30));
        Check("★ //sessions 列出会话（含当前标记与用法）",
            Sent().Any(t => t.Contains("agent 会话") && t.Contains("←") && t.Contains("//new")),
            string.Join(" | ", Sent().TakeLast(2)));

        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//new 测试新会话", 15061, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("已开新会话")), TimeSpan.FromSeconds(30));
        Check("★ //new 建了新会话（并告诉名字与后端）",
            Sent().Any(t => t.Contains("已开新会话「测试新会话」")),
            string.Join(" | ", Sent().TakeLast(2)));

        // 在新会话里跑一条：外部设备那边拿到的 session id 应该是**新的**
        var beforeNewSessionTask = bridge.Tasks.Count;
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//新会话里的第一句", 15062, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => bridge.Tasks.Count > beforeNewSessionTask, TimeSpan.FromSeconds(30));
        var newSessionId = bridge.Tasks[^1]["session"]?.GetValue<string>() ?? string.Empty;
        bridge.Send(new JsonObject
        {
            ["type"] = "done",
            ["id"] = bridge.Tasks[^1]["id"]!.GetValue<string>(),
            ["text"] = "新会话的结论",
            ["exitCode"] = 0,
            ["durationMs"] = 200,
            ["toolCalls"] = 0
        });
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("新会话的结论")), TimeSpan.FromSeconds(30));

        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            var listJson = await http.GetStringAsync($"http://127.0.0.1:{healthPort}/api/agent/sessions?key=group:{groupId}");
            var sessions = JsonNode.Parse(listJson)!["sessions"]!.AsArray();
            Check("★ 面板能看到群里建的会话（与群同一套存储）",
                sessions.Any(s => s!["name"]?.GetValue<string>() == "测试新会话" && s!["current"]?.GetValue<bool>() == true),
                listJson);
            var currentSess = sessions.FirstOrDefault(s => s!["current"]?.GetValue<bool>() == true);
            var distinctPi = sessions.Select(s => s!["piSession"]?.GetValue<string>() ?? string.Empty)
                .Where(x => x.Length > 0).Distinct().Count();
            Check("★ 新会话真的换了一份上下文（当前会话的 pi session id = 刚下发的那个，两会话 id 不同）",
                currentSess is not null &&
                (currentSess["piSession"]?.GetValue<string>() ?? string.Empty) == newSessionId &&
                distinctPi >= 2,
                $"下发={newSessionId}；全部：[{string.Join(" , ", sessions.Select(s => $"{s!["name"]}@{s!["piSession"]}{(s!["current"]?.GetValue<bool>() == true ? "(当前)" : "")}"))}]");

            // //use 切回默认会话
            await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//use 默认", 15063, mentionBot: false, ct: cts.Token);
            await WaitUntilAsync(() => Sent().Any(t => t.Contains("切到会话")), TimeSpan.FromSeconds(30));
            Check("★ //use 能切回旧会话",
                Sent().Any(t => t.Contains("切到会话「默认」")), string.Join(" | ", Sent().TakeLast(2)));

            // //del 删掉刚建的
            await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//del 测试新会话", 15064, mentionBot: false, ct: cts.Token);
            await WaitUntilAsync(() => Sent().Any(t => t.Contains("已删除会话")), TimeSpan.FromSeconds(30));
            Check("★ //del 删除会话（并告知当前已自动换新）",
                Sent().Any(t => t.Contains("已删除会话「测试新会话」")), string.Join(" | ", Sent().TakeLast(2)));
            Check("★ 删除外部会话时会通知设备删掉 pi 那边的记录",
                await WaitUntilAsync(() => bridge.Forgotten.Count > 0, TimeSpan.FromSeconds(10)),
                string.Join(",", bridge.Forgotten));
        }

        // ---- ⑰ 自动标题 / //help / //sessions all（号主：没有标题总结、不知道有多少个会话、忘了命令）----
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//help", 15070, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("看这份说明")), TimeSpan.FromSeconds(30));
        var help = string.Join("\n", Sent().TakeLast(3));
        Check("★ //help 把命令列全了（含会话相关与 stop/status）",
            help.Contains("看这份说明") && help.Contains("//sessions") && help.Contains("//new") &&
            help.Contains("//rename") && help.Contains("//del") && help.Contains("//stop") && help.Contains("//status"),
            help.Length > 400 ? help[..400] + "…" : help);

        // 自动标题：//new 不带名字 → 第一句话成为标题
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//new", 15071, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("已开新会话")), TimeSpan.FromSeconds(30));
        var tasksBeforeTitle = bridge.Tasks.Count;
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//帮我看看今天的报错日志", 15072, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => bridge.Tasks.Count > tasksBeforeTitle, TimeSpan.FromSeconds(30));
        bridge.Send(new JsonObject
        {
            ["type"] = "done",
            ["id"] = bridge.Tasks[^1]["id"]!.GetValue<string>(),
            ["text"] = "标题那单的结论",
            ["exitCode"] = 0,
            ["durationMs"] = 200,
            ["toolCalls"] = 0
        });
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("标题那单的结论")), TimeSpan.FromSeconds(30));

        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            var listJson = await http.GetStringAsync($"http://127.0.0.1:{healthPort}/api/agent/sessions?key=group:{groupId}");
            var sessions = JsonNode.Parse(listJson)!["sessions"]!.AsArray();
            var current = sessions.FirstOrDefault(s => s!["current"]?.GetValue<bool>() == true);
            Check("★ 会话标题自动总结（“帮我看看今天的报错日志” → “今天的报错日志”，且标成自动标题）",
                current is not null && current["name"]?.GetValue<string>() == "今天的报错日志" &&
                current["autoNamed"]?.GetValue<bool>() == true,
                $"当前会话标题=「{current?["name"]}」autoNamed={current?["autoNamed"]}");

            // 手动改名后不再被自动覆盖
            var renameBody = new JsonObject
            {
                ["key"] = $"group:{groupId}",
                ["action"] = "rename",
                ["id"] = current?["id"]?.GetValue<string>(),
                ["title"] = "我自己起的名字"
            };
            await http.PostAsync($"http://127.0.0.1:{healthPort}/api/agent/sessions",
                new StringContent(renameBody.ToJsonString(), Encoding.UTF8, "application/json"));
            await Task.Delay(200);
            var afterRename = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{healthPort}/api/agent/sessions?key=group:{groupId}"));
            Check("★ 面板里能给会话改名（改过就不算自动标题）",
                afterRename?["sessions"] is JsonArray renamed &&
                renamed.Any(s => s!["name"]?.GetValue<string>() == "我自己起的名字" &&
                                 s!["autoNamed"]?.GetValue<bool>() == false),
                afterRename?.ToJsonString() ?? "(空)");

            // 总览：有多少个会话 + 标题
            var all = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{healthPort}/api/agent/sessions"));
            Check("★ 会话总览给出总数与标题（面板/接口可直接查）",
                all!["total"]?.GetValue<int>() >= 2 && all!["chatCount"]?.GetValue<int>() >= 1 &&
                all!["chats"]?[$"group:{groupId}"]?["sessions"] is JsonArray ls &&
                ls.Any(s => s!["name"]?.GetValue<string>() == "我自己起的名字"),
                $"total={all?["total"]} chatCount={all?["chatCount"]}");
        }

        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//sessions all", 15073, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("全部 agent 会话")), TimeSpan.FromSeconds(30));
        Check("★ //sessions all 报总数与每个聊天的标题",
            Sent().Any(t => t.Contains("全部 agent 会话") && t.Contains("共") && t.Contains("轮")),
            string.Join(" | ", Sent().TakeLast(2)));

        await bot.StopAsync();
    }

    private static JsonArray devices2(JsonNode status)
        => status["deviceList"] as JsonArray ?? new JsonArray();
}
