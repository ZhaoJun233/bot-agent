using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

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
/// <c>tests/BotAgent.SafetyProbe</c> 覆盖 —— 那支是注入时间的，不会 flaky。
/// </summary>
public static partial class Program
{
    private static async Task RunApprovalScenarioAsync()
    {
        Section("S43 人工审批闭环（模型想调工具 → 群主同意才执行，且只执行固定假工具）");

        var openAiPort = FreePort(17893);
        var botWsPort = FreePort(13097);
        var panelPort = FreePort(18117);
        // 群号刻意用 **6 位**：面板脱敏规则是“长数字（≥6 位）留前 3 后 2”，
        // 这样“面板列的是脱敏 key、而审批仍然用真 key 生效”才真的被验到（批次 I）。
        const long groupId = 667700;
        const long memberId = 20002;   // 普通群成员（默认 role=member）
        const long ownerId = 20003;    // 后面用 SetRole 登记成群主
        // 批次 I（面板审批卡）：这条链是高权限写路径，前置 fail-closed 要求**面板令牌已配** ——
        // 所以这个场景从一开始就带上令牌，面板请求都走 ?token=（与 S17 同一套路）。
        const string panelToken = "it-s43-token";

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
        // ④ 批次 I：面板批准那一路（再开一张单，用面板批）
        openAi.EnqueueReply("""{"suitability": 99, "action": "tool", "toolRequest": "demo.echo"}""");
        // ⑤ 批次 I：面板拒绝那一路（再开一张单，用面板拒）
        openAi.EnqueueReply("""{"suitability": 99, "action": "tool", "toolRequest": "demo.echo"}""");

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
            ["QQCHAT_PANEL_TOKEN"] = panelToken
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
        var (onCode, _) = await PostJsonAsync($"{panel}/api/settings?token={panelToken}", """{"enableApprovals":true}""");
        Check("面板能打开人工审批", onCode == 200, $"HTTP {onCode}");

        var (_, panelBody) = await HttpGetAsync($"{panel}/api/settings?token={panelToken}");
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

        // ── ⑧ 批次 I：面板上的审批卡（GET 形状 + 批准执行 + 一次性）──
        // 再要一张单（模型的第 4 条回复是给这一腿的）
        before = Sent().Count;
        await protocol.SendGroupMessageAsync(groupId, memberId, "群友A", "再查一次负载", 21007,
            mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(
            () => Sent().Skip(before).Any(t => t.Contains("需要确认")), TimeSpan.FromSeconds(40));
        var panelAnnouncement = Sent().Skip(before).FirstOrDefault(t => t.Contains("需要确认")) ?? string.Empty;
        var panelId = Regex.Match(panelAnnouncement, @"编号\s*([A-Z0-9]{6})") is { Success: true } m2
            ? m2.Groups[1].Value
            : string.Empty;
        Check("★ 面板那一路：先有一张真的待批单（群里能看到待确认）", panelId.Length == 6,
            Snippet(panelAnnouncement, "需要确认"));
        if (panelId.Length == 6)
        {
            var (listCode, listBody) = await HttpGetAsync($"{panel}/api/approvals?token={panelToken}");
            var listRoot = JsonNode.Parse(listBody) as JsonObject ?? new JsonObject();
            var pending = listRoot["pending"] as JsonArray ?? new JsonArray();
            var entry = pending.FirstOrDefault(n => n?["id"]?.GetValue<string>() == panelId) as JsonObject;
            Check("★★ 面板能读到这张待批单（编号 / 工具 / 摘要 / 脱敏后的会话 key / 剩余秒数）",
                listCode == 200 && listRoot["canDecide"]?.GetValue<bool>() == true && entry is not null
                && entry["tool"]?.GetValue<string>() == "demo.echo"
                && (entry["summary"]?.GetValue<string>() ?? string.Empty).Contains("demo.echo")
                && (entry["key"]?.GetValue<string>() ?? string.Empty).Contains("***")
                && entry["expiresInSeconds"]?.GetValue<int>() > 0,
                $"HTTP {listCode} {listBody[..Math.Min(200, listBody.Length)]}");

            // 批准：与群里「同意 编号」走同一条判定 → 执行固定假工具 → 回执发回**原会话**
            before = Sent().Count;
            var approveBody = "{\"id\":\"" + panelId + "\",\"approve\":true}";
            var (decideCode, decideBodyOut) =
                await PostJsonAsync($"{panel}/api/approvals/decide?token={panelToken}", approveBody);
            Check("★ 面板批准返回 200 与原因码", decideCode == 200, $"HTTP {decideCode} {decideBodyOut}");
            await WaitUntilAsync(
                () => Sent().Skip(before).Any(t => t.Contains("已确认")), TimeSpan.FromSeconds(40));
            var panelExecuted = Sent().Skip(before).FirstOrDefault(t => t.Contains("已确认")) ?? string.Empty;
            Check("★★ 面板批准 → 真的执行了（票据 → 闸门 → 固定假工具）且回执发回原会话",
                panelExecuted.Contains("demo.echo") && panelExecuted.Contains("真实副作用"),
                string.Join(" | ", Sent().Skip(before)));
            Check("日志里能看出这是**面板**批的（审计分得清哪条路）",
                bot.OutputLines.Any(l => l.Contains("[审批] 面板") && l.Contains("approved")),
                bot.OutputLines.LastOrDefault(l => l.Contains("[审批] 面板")) ?? "(没有面板审批日志)");

            // 一次性：同一个编号在面板上再批一次 → 不再执行
            before = Sent().Count;
            var (againCode, againBody) =
                await PostJsonAsync($"{panel}/api/approvals/decide?token={panelToken}", approveBody);
            await Task.Delay(1500);
            Check("★★ 同一编号在面板上再批一次 → 不执行、如实回报（一次性没被放宽）",
                Sent().Count == before
                && (againBody.Contains("already_decided") || againBody.Contains("already_consumed")
                    || againBody.Contains("unknown_request")),
                $"HTTP {againCode} {againBody}");
        }

        // ── ⑨ 批次 I：面板拒绝那一路（不执行任何东西，只回一句）──
        before = Sent().Count;
        await protocol.SendGroupMessageAsync(groupId, memberId, "群友A", "还有一件事", 21008,
            mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(
            () => Sent().Skip(before).Any(t => t.Contains("需要确认")), TimeSpan.FromSeconds(40));
        var rejectAnnouncement = Sent().Skip(before).FirstOrDefault(t => t.Contains("需要确认")) ?? string.Empty;
        var rejectId = Regex.Match(rejectAnnouncement, @"编号\s*([A-Z0-9]{6})") is { Success: true } m3
            ? m3.Groups[1].Value
            : string.Empty;
        Check("面板拒绝那一路：也先有一张待批单", rejectId.Length == 6, Snippet(rejectAnnouncement, "需要确认"));
        if (rejectId.Length == 6)
        {
            before = Sent().Count;
            var rejectBody = "{\"id\":\"" + rejectId + "\",\"approve\":false}";
            var (rejCode, rejBodyOut) =
                await PostJsonAsync($"{panel}/api/approvals/decide?token={panelToken}", rejectBody);
            await WaitUntilAsync(
                () => Sent().Skip(before).Any(t => t.Contains("已被拒绝")), TimeSpan.FromSeconds(30));
            Check("★★ 面板拒绝 → 不执行任何东西，只回一句「已被拒绝」",
                rejCode == 200 && Sent().Skip(before).Any(t => t.Contains("已被拒绝"))
                && !Sent().Skip(before).Any(t => t.Contains("已确认")),
                $"HTTP {rejCode} {rejBodyOut} | " + string.Join(" | ", Sent().Skip(before)));
        }

        await bot.StopAsync();
    }
}
