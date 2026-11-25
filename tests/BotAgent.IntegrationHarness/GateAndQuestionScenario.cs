using System.Text;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S45 单一调度策略与提问能力的真实宿主接线。
///   ① 普通插话概率为零时不叫模型；旧字段提交原子拒绝；被 @ 仍然回复。
///   ② 允许提问：默认关 → `action=ask` 安全静默；
///      打开后 → 群里出现服务端包好的【提问】（带一次性编号），有人应一声就记成“已被回答”，
///      再应一次不再算（一次性）。
///
/// 断言只看**形状**（发没发、有没有编号、原因码），不看任何正文。
/// </summary>
public static partial class Program
{
    private static async Task RunGateAndQuestionScenarioAsync()
    {
        Section("S45 新版参与概率 / 允许提问");

        var openAiPort = FreePort(17895);
        var botWsPort = FreePort(13099);
        var panelPort = FreePort(18119);
        // 7 位群号：脱敏只对 ≥6 位数字生效（5 位本来就不遮），用真实长度才有意义
        const long groupId = 6677124;
        const long memberId = 20002;

        var panel = $"http://127.0.0.1:{panelPort}";
        var dataDir = NewDataDir("s45");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        // Deterministic 100-percent fixture, then zero-percent ordinary admission.
        openAi.EnqueueReply("""{"suitability": 99, "reply": "闸门关着的时候我会接话"}""");
        // ② 闸门打开：那条消息**不该叫模型**，所以这里排的这一条会在后面被 @ 时用掉
        openAi.EnqueueReply("""{"suitability": 99, "reply": "被点名我才说这句"}""");
        // ③ 提问（**两条**）：一条给“提问还关着”的那轮（应安全静默），一条给“提问已打开”的那轮。
        //    为什么必须排两条：假模型是按顺序发脚本的，而这两轮都会真的走到模型 ——
        //    只排一条的话，第一条会被“关着”那轮吃掉，后面那轮就拿到默认回复了（第一版就是这么错的）。
        openAi.EnqueueReply("""{"action":"ask","reply":"（这条不该被发出去）要我把结论整理成一条消息吗？"}""");
        openAi.EnqueueReply("""{"action":"ask","reply":"要我把结论整理成一条消息吗？"}""");

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
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_ENABLE_POKE"] = "0",
            ["QQCHAT_ENABLE_VOICE"] = "0",
            ["QQCHAT_ENABLE_MUSIC"] = "0",
            ["QQCHAT_WEB_SEARCH"] = "0",
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

        // ── ① 概率为 100：没点名的普通消息可接话 ──
        var before = Sent().Count;
        await protocol.SendGroupMessageAsync(groupId, memberId, "群友A", "大家今天在忙什么", 23001,
            mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Count > before, TimeSpan.FromSeconds(40));
        Check("普通插话概率 100 时可以接话", Sent().Count > before, $"sendDelta={Sent().Count - before}");
        var (initialCode, initialBody) = await PanelPostJsonAsync($"{panel}/api/settings?configuration=read", """{"kind":1}""");
        var initial = JsonNode.Parse(initialBody)!.AsObject();
        var initialVersion = initial["version"]?.GetValue<string>();
        Check("结构化读取提供配置版本", initialCode == 200 && initialVersion is not null, $"status={initialCode}");
        var (retiredCode, _) = await PanelPostJsonAsync($"{panel}/api/settings",
            """{"enableParticipationGating":true,"groupInterjectionProbability":0}""");
        var (_, rejectedBody) = await PanelGetAsync($"{panel}/api/settings");
        var rejected = JsonNode.Parse(rejectedBody)!.AsObject();
        var (_, rejectedVersionBody) = await PanelPostJsonAsync($"{panel}/api/settings?configuration=read", """{"kind":1}""");
        var rejectedVersion = JsonNode.Parse(rejectedVersionBody)?["version"]?.GetValue<string>();
        Check("旧配置拒绝整个保存且不推进版本", retiredCode == 400
            && initialVersion is not null && rejectedVersion == initialVersion
            && rejected["runtime"]?["groupInterjectionProbability"]?.GetValue<int>() == 100,
            $"status={retiredCode}; versionUnchanged={rejectedVersion == initialVersion}");

        // ── ② 关闭普通插话，不影响明确请求 ──
        var (onCode, _) = await PanelPostJsonAsync($"{panel}/api/settings",
            """{"groupInterjectionProbability":0,"groupInterjectionCooldownSeconds":600}""");
        Check("面板能修改新版插话参数", onCode == 200, $"HTTP {onCode}");

        var (_, settingsBody) = await PanelGetAsync($"{panel}/api/settings");
        var runtime = (JsonNode.Parse(settingsBody) as JsonObject)?["runtime"] as JsonObject ?? new JsonObject();
        Check("面板仅回显新版配置", runtime["groupInterjectionProbability"]?.GetValue<int>() == 0
            && !runtime.ContainsKey("enableParticipationGating") && !runtime.ContainsKey("participationCooldownSeconds"),
            $"probability={runtime["groupInterjectionProbability"]}");

        // ── ③ 零概率普通消息不叫模型、不发送 ──
        before = Sent().Count;
        var requestsBefore = openAi.Requests.Count;
        await protocol.SendGroupMessageAsync(groupId, memberId, "群友B", "我这边也挺忙的", 23002,
            mentionBot: false, ct: cts.Token);
        await Task.Delay(1200);
        Check("零概率普通消息没有模型调用或发送", Sent().Count == before && openAi.Requests.Count == requestsBefore,
            $"sendDelta={Sent().Count - before}; requestDelta={openAi.Requests.Count - requestsBefore}");

        // ── ④ 被 @ 不受普通插话概率与冷却限制 ──
        before = Sent().Count;
        await protocol.SendGroupMessageAsync(groupId, memberId, "群友A", "还是问你吧", 23003,
            mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Count > before, TimeSpan.FromSeconds(40));
        Check("零概率与冷却期间被 @ 仍然回复", Sent().Count > before, $"sendDelta={Sent().Count - before}");

        // ── ⑤ 提问默认关：模型想提问 → 安全静默（一个字都不发）──
        before = Sent().Count;
        await protocol.SendGroupMessageAsync(groupId, memberId, "群友A", "你打算怎么整理", 23004,
            mentionBot: true, ct: cts.Token);
        await Task.Delay(3000);
        Check("★ 提问默认关：模型写 action=ask 时一个字都不发（安全静默）",
            !Sent().Skip(before).Any(t => t.Contains("提问")),
            string.Join(" | ", Sent().Skip(before)));

        // 默认关时提示词必须**逐字保持原样**：动作契约（action / ask / toolRequest）一个字都不该出现。
        // 这条同时解释了 S9 的“系统提示没失控”为什么与本轮改动无关 —— 那个场景也没开这两个开关。
        var quietPrompt = openAi.Requests.Count > 0 ? SystemText(openAi.Requests[^1]) : string.Empty;
        Check("★ 两个开关都关着 → 提示词里没有动作契约（与改造前逐字一致）",
            quietPrompt.Length > 0 && !quietPrompt.Contains("可选的结构化动作"),
            $"promptChars={quietPrompt.Length}");

        // ── ⑥ 打开提问：群里出现服务端包好的提问（带一次性编号）──
        var (qCode, _) = await PanelPostJsonAsync(
            $"{panel}/api/settings", """{"enableQuestions":true}""");
        Check("面板能打开“允许提问”", qCode == 200, $"HTTP {qCode}");

        before = Sent().Count;
        await protocol.SendGroupMessageAsync(groupId, memberId, "群友A", "那就整理一下", 23005,
            mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(
            () => Sent().Skip(before).Any(t => t.Contains("【提问】")), TimeSpan.FromSeconds(40));

        var asked = Sent().Skip(before).FirstOrDefault(t => t.Contains("【提问】")) ?? string.Empty;
        Check("★ 提问打开：群里收到服务端包好的提问（而不是把模型原话直接当回复发）",
            asked.Length > 0, string.Join(" | ", Sent().Skip(before)));
        Check("提问里带着一次性编号与有效期",
            System.Text.RegularExpressions.Regex.IsMatch(asked, @"编号\s*[A-Z0-9]{6}") && asked.Contains("秒内"),
            Snippet(asked, "提问"));

        // 打开之后模型必须**被告知**这个动作存在 —— 否则真模型永远不会产出 action=ask，
        // 只有假模型夹具能覆盖到提问链路。
        var askPrompt = openAi.Requests.Count > 0 ? SystemText(openAi.Requests[^1]) : string.Empty;
        Check("★ 提问打开后 → 提示词里带上动作契约（含 ask 的说明）",
            askPrompt.Contains("可选的结构化动作") && askPrompt.Contains("\"ask\""),
            $"promptChars={askPrompt.Length}");

        var askId = System.Text.RegularExpressions.Regex.Match(asked, @"编号\s*([A-Z0-9]{6})").Groups[1].Value;

        // ── ⑦ 有人应一声 → 记成“已被回答”；再应一次不算（一次性）──
        before = Sent().Count;
        await protocol.SendGroupMessageAsync(groupId, memberId, "群友B", "整理吧", 23006,
            mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(
            () => bot.OutputLines.Any(l => l.Contains("[提问]") && l.Contains("已被回答")), TimeSpan.FromSeconds(40));
        Check("★ 有人应一声 → 那条待答问题被标记为已答（一次性消费）",
            bot.OutputLines.Any(l => l.Contains("[提问]") && l.Contains("已被回答")),
            bot.OutputLines.LastOrDefault(l => l.Contains("[提问]")) ?? "(没有 [提问] 行)");

        await protocol.SendGroupMessageAsync(groupId, memberId, "群友B", "我说完了", 23007,
            mentionBot: false, ct: cts.Token);
        await Task.Delay(2500);
        Check("★ 已答的问题不会被答第二次（不会再出现“已被回答”那条）",
            bot.OutputLines.Count(l => l.Contains("[提问]") && l.Contains("已被回答")) == 1,
            bot.OutputLines.Count(l => l.Contains("[提问]") && l.Contains("已被回答")).ToString());

        Check("提问这条路径**没有执行任何东西**（日志里不该出现“执行固定假工具”）",
            !bot.OutputLines.Any(l => l.Contains("执行固定假工具")),
            bot.OutputLines.LastOrDefault(l => l.Contains("[审批]")) ?? "(没有 [审批] 行)");

        Check("编号是服务端生成的那一个（形状 6 位、字母表去掉易混的 0/O/1/I）",
            askId.Length == 6 && askId.All(c => "23456789ABCDEFGHJKLMNPQRSTUVWXYZ".Contains(c)), askId);

        await bot.StopAsync();
    }
}
