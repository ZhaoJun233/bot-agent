using System.Text;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S44 参与状态机的**观测锚点**（V3 §7.3 / §7.4）：状态机不能只在探针里能跑，
/// 得证明真实链路**真的把四种事件喂进去了** —— 这是“规则写对但没接线”的照妖镜。
///
/// 三条硬要求（都在这里钉住）：
///   ① **被 @ → Mentioned**：日志里出现 `Observing → Probing（mentioned_probe`；
///   ② **无关的群消息 → IrrelevantMessage**：试探后下一条旁白 → `Probing → Exiting（probing_no_confirm`；
///   ③ **模型出事 → ModelTimeout**：上游连续 503 → `… → Exiting（model_timeout`（**只降级，不升级**）；
///   ④ **面板改的上限真的到了状态机手里**：改完设置后日志里那行 `[参与] 策略已更新：连续 ≤N`。
///
/// 为什么全部靠**日志断言**：状态机只写结构化日志、不改“发不发”的判定（gating 默认关），
/// 所以“接没接上”唯一的现场证据就是这些日志行。断言只看形状（状态/原因码/数字），不看任何正文。
/// </summary>
public static partial class Program
{
    private static async Task RunParticipationAnchorScenarioAsync()
    {
        Section("S44 参与状态机的观测锚点（@ / 旁白 / 模型超时 / 面板改上限）");

        var openAiPort = FreePort(17894);
        var botWsPort = FreePort(13098);
        var panelPort = FreePort(18118);
        // 刻意用 7 位群号：脱敏只对 ≥6 位数字生效（66771 这种 5 位本来就不算 QQ 号/群号）
        const long groupId = 6677123;
        const long memberId = 20002;

        var panel = $"http://127.0.0.1:{panelPort}";
        var dataDir = NewDataDir("s44");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        // 前两次 chat 一律 503（模拟上游抖动/超时）；之后恢复正常，免得影响后面的断言
        openAi.FailChatTimes = 2;
        openAi.EnqueueReply("""{"suitability": 99, "reply": "在的"}""");

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
            ["QQCHAT_WEB_SEARCH"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            // 参与上限：用一组**非默认值**，这样“面板改了真的生效”才有证据
            ["QQCHAT_PARTICIPATION_MAX_REPLIES"] = "5"
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await Task.Delay(800);

        bool Participated(string needle)
            => bot.OutputLines.Any(l => l.Contains("[参与]") && l.Contains(needle));

        // ── ① 模型超时锚点：上游连续 503 → ModelTimeout（只降级） ──
        await protocol.SendGroupMessageAsync(groupId, memberId, "群友A", "在吗", 22001,
            mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(() => Participated("model_timeout"), TimeSpan.FromSeconds(40));
        Check("★ 模型请求失败 → 记成 ModelTimeout（不是含糊的“沉默”）",
            Participated("model_timeout"),
            bot.OutputLines.LastOrDefault(l => l.Contains("[参与]")) ?? "(没有 [参与] 行)");
        Check("★ 失败只降级：退场（不是升级成“更愿意说话”）",
            Participated("→ Exiting（model_timeout") || Participated("→ Observing（model_timeout"),
            bot.OutputLines.LastOrDefault(l => l.Contains("model_timeout")) ?? "(没有 model_timeout 行)");

        // ── ② 面板改上限：日志里必须能看到新的那组数 ──
        var (saveCode, _) = await PostJsonAsync($"{panel}/api/settings",
            """{"participationMaxConsecutiveReplies":6,"participationCooldownSeconds":30}""");
        Check("面板保存参与上限成功", saveCode == 200, $"HTTP {saveCode}");

        await WaitUntilAsync(() => Participated("策略已更新"), TimeSpan.FromSeconds(20));
        Check("★ 面板改的上限真的到了状态机手里（日志里是**钳制后**的那组数）",
            Participated("策略已更新：连续 ≤6") && Participated("冷却 30s"),
            bot.OutputLines.LastOrDefault(l => l.Contains("策略已更新")) ?? "(没有策略更新行)");

        var (_, settingsBody) = await HttpGetAsync($"{panel}/api/settings");
        var runtime = (JsonNode.Parse(settingsBody) as JsonObject)?["runtime"] as JsonObject ?? new JsonObject();
        Check("★ 面板回显的参与上限也是钳制后的值（填什么就显示什么是骗人的）",
            runtime["participationPolicy"]?.GetValue<string>() is { Length: > 0 } echo
            && echo.Contains("连续 ≤6") && echo.Contains("冷却 30s"),
            runtime["participationPolicy"]?.GetValue<string>() ?? "(没有这个字段)");
        Check("面板明确写了 gating 是关的（只观测）",
            runtime["participationGating"]?.GetValue<string>()?.Contains("只观测") == true,
            runtime["participationGating"]?.GetValue<string>() ?? "(没有这个字段)");

        // ── ③ 被 @ → Mentioned；回复成功 → Active ──
        await protocol.SendGroupMessageAsync(groupId, memberId, "群友A", "帮我看看这个", 22002,
            mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(() => Participated("mentioned_probe"), TimeSpan.FromSeconds(40));
        Check("★ 群里被 @ → 记成 Mentioned（Observing → Probing / mentioned_probe）",
            Participated("mentioned_probe"),
            bot.OutputLines.LastOrDefault(l => l.Contains("[参与]")) ?? "(没有 [参与] 行)");

        await WaitUntilAsync(() => Participated("→ Active（replied"), TimeSpan.FromSeconds(40));
        Check("★ 回复成功 → Active（回复终态那个锚点也在喂）",
            Participated("→ Active（replied"),
            bot.OutputLines.LastOrDefault(l => l.Contains("[参与]")) ?? "(没有 [参与] 行)");

        // ── ④ 无关的群消息 → IrrelevantMessage；模型随后选择沉默 → Silent ──
        // 两个锚点要分别拿到证据，所以刻意让模型**慢一点回**（ResponseDelayMs）：
        //   发送后立刻读只读接口 → 那一刻的原因码只可能来自 IrrelevantMessage；
        //   等模型回完（脚本让它沉默）→ 日志里应出现 Silent 那次退场。
        // （为什么不用日志证明 IrrelevantMessage：状态没变时状态机不打日志 —— 那不代表没喂事件。）
        async Task<string> SessionReasonAsync()
        {
            var (_, body) = await HttpGetAsync($"{panel}/api/participation");
            var arr = (JsonNode.Parse(body) as JsonObject)?["sessions"] as JsonArray;
            return arr is { Count: > 0 } ? arr[0]!["reason"]?.GetValue<string>() ?? string.Empty : string.Empty;
        }

        openAi.ResponseDelayMs = 3000;
        openAi.EnqueueReply("""{"suitability": 0, "reply": ""}""");   // 模型自评 0 → 选择沉默

        var reasonBefore = await SessionReasonAsync();
        await protocol.SendGroupMessageAsync(groupId, memberId, "群友B", "今天天气不错", 22003,
            mentionBot: false, ct: cts.Token);

        // 观察窗口：模型还没回，原因码只可能被 IrrelevantMessage 改掉
        string reasonAfterIrrelevant = string.Empty;
        for (var i = 0; i < 30 && reasonAfterIrrelevant.Length == 0; i++)
        {
            await Task.Delay(80);
            var now = await SessionReasonAsync();
            if (now != reasonBefore)
            {
                reasonAfterIrrelevant = now;
            }
        }

        Check("★ 群里的无关消息 → 记成 IrrelevantMessage（原因码变成 冷却/接话 之一）",
            reasonAfterIrrelevant is "cooldown" or "active_relate",
            $"before={reasonBefore} after={reasonAfterIrrelevant}");

        // 等模型回完：它选择沉默 → Silent 那一次退场（这条状态真的变了，所以日志里应该有）
        var sawSilent = await WaitUntilAsync(() => Participated("→ Exiting（silent"), TimeSpan.FromSeconds(40));
        Check("★ 模型选择沉默 → 也喂了 Silent（V3 §7.3：连续静默要能退场）",
            sawSilent,
            bot.OutputLines.LastOrDefault(l => l.Contains("[参与]")) ?? "(没有 [参与] 行)");

        Check("无关消息**没有**把状态推成“更想说话”（原因码不该是 mentioned_probe）",
            reasonAfterIrrelevant != "mentioned_probe" && reasonAfterIrrelevant != "replied",
            $"after={reasonAfterIrrelevant}");

        openAi.ResponseDelayMs = 0;
        // ── ⑤ 只读状态接口：能看到这个会话、而且只有结构化字段 ──
        var (partCode, partBody) = await HttpGetAsync($"{panel}/api/participation");
        var part = JsonNode.Parse(partBody) as JsonObject ?? new JsonObject();
        Check("GET /api/participation 可访问（面板「参与状态」那块的数据源）", partCode == 200, $"HTTP {partCode}");
        Check("★ 只读接口给出了会话状态（状态 / 原因 / 计数 —— 不含正文）",
            (part["sessions"] as JsonArray)?.Count > 0
            && part["sessions"]!.AsArray()[0]!["state"] is not null
            && part["sessions"]!.AsArray()[0]!["reason"] is not null
            && part["sessions"]!.AsArray()[0]!["counters"] is not null,
            part["sessions"]?.ToJsonString() ?? "(没有 sessions)");
        Check("★ 只读接口里带上了当前生效的上限与“只观测”的说明",
            (part["policy"]?.GetValue<string>() ?? string.Empty).Contains("连续 ≤6")
            && (part["gating"]?.GetValue<string>() ?? string.Empty).Contains("只观测"),
            part["policy"]?.GetValue<string>() ?? "(没有 policy)");
        Check("★ 会话 key 已按脱敏开关处理（不裸奔完整群号）",
            (part["sessions"] as JsonArray)?.All(s =>
            {
                var key = s!["key"]?.GetValue<string>() ?? string.Empty;
                return key.Contains("***") && !key.Contains(groupId.ToString());
            }) == true,
            part["sessions"]?.ToJsonString() ?? "(没有 sessions)");

        await bot.StopAsync();
    }
}
