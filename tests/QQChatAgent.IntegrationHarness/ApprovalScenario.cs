using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S43 人工审批闭环（V3 §9.4 / §9.5）：**模型说“我想调工具”不等于有权限**。
///
/// 这个场景真起一个 bot 进程（假模型 + 假协议端），把整条链路走一遍：
///   ① 审批关着（默认）时，模型写 <c>action=tool</c> 一律**安全静默**（与改造前一致）；
///   ② 面板打开审批后，同一句话变成“群里收到一张**待确认**”——但**没有任何东西被执行**；
///   ③ 普通成员回「同意 编号」**不算数**（身份在服务端核，V3 §9.4）；
///   ④ 群主回「同意 编号」才执行 —— 执行的也只是**固定假工具**，回执里写明“没有真实副作用”；
///   ⑤ 同一编号**重放无效**（一次性消费）；
///   ⑥ 模型点名服务端没登记的工具（<c>shell.exec</c>）→ **连单都开不出来**（Fail-Closed）。
///
/// 过期与跨会话/策略版本那几条不在这里跑（要等 120s 或改配置），由确定性探针
/// <c>tests/QQChatAgent.SafetyProbe</c> 覆盖 —— 那支是注入时间的，不会 flaky。
/// </summary>
public static partial class Program
{
    private static async Task RunApprovalScenarioAsync()
    {
        Section("S43 人工审批闭环（模型想调工具 → 群主同意才执行，且只执行固定假工具）");

        var openAiPort = FreePort(17893);
        var botWsPort = FreePort(13097);
        var panelPort = FreePort(18117);
        const long groupId = 66770;
        const long memberId = 20002;   // 普通群成员（默认 role=member）
        const long ownerId = 20003;    // 后面用 SetRole 登记成群主

        var panel = $"http://127.0.0.1:{panelPort}";
        var dataDir = NewDataDir("s43");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        // ① 审批关着：模型想调工具 → 安全静默（这一条先跑，验证“默认零变化”）
        openAi.EnqueueReply("""{"suitability": 99, "action": "tool", "toolRequest": "demo.echo"}""");
        // ② 审批打开：同一条 → 开待批单并公告
        openAi.EnqueueReply("""{"suitability": 99, "action": "tool", "toolRequest": "demo.echo"}""");
        // ③ 模型点名服务端没登记的工具 → 连单都不开
        openAi.EnqueueReply("""{"suitability": 99, "action": "tool", "toolRequest": "shell.exec"}""");

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
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString()
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await Task.Delay(800);

        List<string> Sent() => protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        // ── ① 默认（审批关）：模型想调工具 → 一句话都不发 ──
        var before = Sent().Count;
        await protocol.SendGroupMessageAsync(groupId, memberId, "群友A", "帮我看看系统负载", 21001,
            mentionBot: true, ct: cts.Token);
        await Task.Delay(2500);
        Check("★ 审批默认关：模型写 action=tool 时一个字都不发（安全静默，不是“执行了”）",
            Sent().Count == before, string.Join(" | ", Sent().Skip(before)));
        Check("日志里能看到它被归到 tool_not_enabled（不是含糊的沉默）",
            bot.OutputLines.Any(l => l.Contains("action=Tool") && l.Contains("tool_not_enabled")),
            bot.OutputLines.LastOrDefault(l => l.Contains("[决策]")) ?? "(没有 [决策] 行)");

        // ── ② 面板打开审批 + 回显要能拿到 ──
        var (onCode, _) = await PostJsonAsync($"{panel}/api/settings", """{"enableApprovals":true}""");
        Check("面板能打开人工审批", onCode == 200, $"HTTP {onCode}");

        var (_, panelBody) = await HttpGetAsync($"{panel}/api/settings");
        var runtime = (JsonNode.Parse(panelBody) as JsonObject)?["runtime"] as JsonObject ?? new JsonObject();
        Check("★ 面板回显：审批开着、覆盖的只有那个固定假工具",
            runtime["enableApprovals"]?.GetValue<bool>() == true &&
            runtime["approvalTool"]?.GetValue<string>() == "demo.echo",
            $"enableApprovals={runtime["enableApprovals"]?.ToJsonString()} tool={runtime["approvalTool"]?.ToJsonString()}");
        Check("★ 面板回显里写清了审批打开后到底多出什么能力（含 needApproval）",
            (runtime["approvalCapabilities"]?.GetValue<string>() ?? string.Empty).Contains("demo.echo"),
            runtime["approvalCapabilities"]?.GetValue<string>() ?? "(空)");

        // ── ③ 同一条“我想调工具” → 群里出现待确认单 ──
        before = Sent().Count;
        await protocol.SendGroupMessageAsync(groupId, memberId, "群友A", "再帮我看看系统负载", 21002,
            mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(
            () => Sent().Skip(before).Any(t => t.Contains("需要确认")), TimeSpan.FromSeconds(40));

        var announcement = Sent().Skip(before).FirstOrDefault(t => t.Contains("需要确认")) ?? string.Empty;
        Check("★ 打开审批后：模型想调工具 → 群里收到一张待确认（而不是直接执行）",
            announcement.Length > 0, string.Join(" | ", Sent().Skip(before)));
        Check("待确认里写明了谁可以批、以及有效期",
            announcement.Contains("群主") && announcement.Contains("秒内有效"), Snippet(announcement, "需要确认"));

        var idMatch = Regex.Match(announcement, @"编号\s*([A-Z0-9]{6})");
        var requestId = idMatch.Success ? idMatch.Groups[1].Value : string.Empty;
        Check("待确认里带着一个可复述的编号", requestId.Length == 6, Snippet(announcement, "编号"));
        if (requestId.Length != 6)
        {
            await bot.StopAsync();
            return;
        }

        // ── ④ 普通成员说“同意”不算数 ──
        before = Sent().Count;
        await protocol.SendGroupMessageAsync(groupId, memberId, "群友A", $"同意 {requestId}", 21003,
            mentionBot: false, ct: cts.Token);
        await Task.Delay(2500);
        Check("★ 普通群成员发「同意」：不执行、也不吭声（身份在服务端核）",
            Sent().Count == before, string.Join(" | ", Sent().Skip(before)));
        Check("日志里把那一下记成 not_an_approver",
            bot.OutputLines.Any(l => l.Contains("[审批]") && l.Contains("not_an_approver")),
            bot.OutputLines.LastOrDefault(l => l.Contains("[审批]")) ?? "(没有 [审批] 行)");

        // ── ⑤ 群主说“同意” → 批准并执行固定假工具 ──
        protocol.SetRole(groupId, ownerId, "owner");
        before = Sent().Count;
        await protocol.SendGroupMessageAsync(groupId, ownerId, "群主", $"同意 {requestId}", 21004,
            mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(
            () => Sent().Skip(before).Any(t => t.Contains("已确认")), TimeSpan.FromSeconds(40));

        var executed = Sent().Skip(before).FirstOrDefault(t => t.Contains("已确认")) ?? string.Empty;
        Check("★ 群主发「同意」：真的执行了（拿到一次性票据 → 过闸门 → 执行固定假工具）",
            executed.Length > 0, string.Join(" | ", Sent().Skip(before)));
        Check("★ 回执老实写明“没有真实副作用 / 不代表具备 shell 能力”（不吹牛，V3 §9.4）",
            executed.Contains("真实副作用") && executed.Contains("shell"), Snippet(executed, "演示"));
        Check("执行的是固定假工具 demo.echo（不是模型点名的别的东西）",
            executed.Contains("demo.echo"), Snippet(executed, "demo.echo"));
        Check("日志里执行前那句闸门判定是 allowed（票据真的被用上了）",
            bot.OutputLines.Any(l => l.Contains("[审批] 执行前闸门") && l.Contains("allow=True")),
            bot.OutputLines.LastOrDefault(l => l.Contains("执行前闸门")) ?? "(没有闸门行)");

        // ── ⑥ 重放同一编号：一次性，第二次不执行 ──
        before = Sent().Count;
        await protocol.SendGroupMessageAsync(groupId, ownerId, "群主", $"同意 {requestId}", 21005,
            mentionBot: false, ct: cts.Token);
        await Task.Delay(2500);
        Check("★ 同一个编号再批一次不会又执行一遍（一次性消费）",
            Sent().Count == before, string.Join(" | ", Sent().Skip(before)));
        Check("日志里能看出是 already_decided / already_consumed",
            bot.OutputLines.Any(l => l.Contains("[审批]") &&
                (l.Contains("already_decided") || l.Contains("already_consumed"))),
            bot.OutputLines.LastOrDefault(l => l.Contains("[审批]")) ?? "(没有 [审批] 行)");

        // ── ⑦ 模型点名服务端没登记的工具：连单都不开 ──
        before = Sent().Count;
        await protocol.SendGroupMessageAsync(groupId, memberId, "群友A", "顺手把日志清一下", 21006,
            mentionBot: true, ct: cts.Token);
        await Task.Delay(3000);
        Check("★ 模型点名 shell.exec：既不开待确认、更不执行（Fail-Closed）",
            !Sent().Skip(before).Any(t => t.Contains("需要确认") || t.Contains("已确认")),
            string.Join(" | ", Sent().Skip(before)));
        Check("日志里说明了为什么没开单（tool_not_fixed）",
            bot.OutputLines.Any(l => l.Contains("[审批] 没有开单") && l.Contains("tool_not_fixed")),
            bot.OutputLines.LastOrDefault(l => l.Contains("没有开单")) ?? "(没有开单日志)");

        await bot.StopAsync();
    }
}
