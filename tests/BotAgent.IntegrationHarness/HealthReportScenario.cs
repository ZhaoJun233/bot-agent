using System.Text;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S35 服务器健康日报：每天在指定时刻（**北京时间**）给指定 QQ 私聊发一条状态（管理员 2026-09-18 要求）。
///
/// 管理员的原话是「每天下午六点（北京时间），给我这个账号私聊推送服务器健康状态」，
/// 并且明确要求 **不要通过外部 Agent 推送（那台电脑可能关着）** —— 所以这条链路必须只用
/// 机器人自己 + 协议端：自己的定时器 → 自己采集状态 → OneBot 私聊消息，没有 subprocess / ssh / 桥。
///
/// 这个场景钉住的就是那条链路本身：
///   ① 启动时**不补发**（当天那个点已经过了也不立刻发一条）；
///   ② 面板拿到的是真状态（时刻/收件人/下次推送都按北京时间算，带 +08:00 偏移）；
///   ③ 「预览」只生成不发送（不能碰 QQ）；
///   ④ 「现在发一条」真的发到私聊，正文是那份状态（不是空话）；
///   ⑤ **改时刻后不用重启**：把时刻改成下一个整分，到点必须自己发（这才是“定时”本身）；
///   ⑥ 同一天只发一条（到点之后不会过一会儿又发）；
///   ⑦ 关掉开关 → 不再排下一次。
/// </summary>
public static partial class Program
{
    private static async Task RunHealthReportScenarioAsync()
    {
        Section("S35 服务器健康日报：定时私聊推送（纯服务器侧，不经过外部 agent）");

        const int openAiPort = 17861;
        const int botWsPort = 13081;
        const int panelPort = 18097;
        const long ownerUid = 10086;

        var dataDir = NewDataDir("s35");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = ownerUid.ToString(),
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_AI_MODE"] = "1",

            // 这一轮的主角：开关 / 时刻 / 收件人
            ["QQCHAT_HEALTH_REPORT"] = "1",
            ["QQCHAT_HEALTH_REPORT_TIME"] = "18:00",
            ["QQCHAT_HEALTH_REPORT_TO"] = ownerUid.ToString(),
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString()
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001, AccountOnline = true };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await protocol.WaitForActionAsync("get_status", TimeSpan.FromSeconds(15));
        await Task.Delay(1200);

        // ── ① 启动时不补发 ──
        // 进程在今天 18:00 之后才起来是常态（部署、重启）；如果每次都“补一条”，
        // 那重启几次管理员就会被刷屏 —— 所以下一次必须是明天那个点。
        await Task.Delay(5000);
        Check("① 启动时不补发（当天那个点过了也不立刻补一条）",
            protocol.ActionsReceived.All(a => a["action"]?.GetValue<string>() != "send_private_msg"),
            $"{protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_private_msg")} 条私聊消息");

        Check("① 启动日志写明了每天几点推给谁（排障一眼看出有没有生效）",
            bot.OutputLines.Any(l => l.Contains("健康日报：每天 18:00（北京时间）") && l.Contains(ownerUid.ToString())),
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("健康日报")).TakeLast(2)));

        // ── ② 面板拿到的是真状态，且按北京时间算 ──
        using var http = CreatePanelHttpClient(panelPort, 15);
        var (statusCode, statusBody) = await PanelGetAsync($"http://127.0.0.1:{panelPort}/api/health-report");
        var payload = JsonNode.Parse(statusBody) as JsonObject;
        Check("② 面板接口能读到配置（开关/时刻/收件人）",
            statusCode == 200 && payload?["enabled"]?.GetValue<bool>() == true &&
            payload?["time"]?.ToString() == "18:00" && (payload?["targets"]?.ToString() ?? "").Contains(ownerUid.ToString()),
            statusBody);

        var nextRunAt = DateTimeOffset.TryParse(payload?["nextRunAt"]?.ToString(), out var parsedNext) ? parsedNext : default;
        var nowBeijing = HealthReportBeijingNow();
        Check("② 下次推送是**北京时间** 18:00（偏移 +08:00，与容器 TZ 无关）",
            nextRunAt != default && nextRunAt.Offset == TimeSpan.FromHours(8) &&
            nextRunAt.Hour == 18 && nextRunAt.Minute == 0 && nextRunAt > nowBeijing,
            $"nextRunAt={payload?["nextRunAt"]}，北京现在 {nowBeijing:O}");

        // ── ③ 预览：只生成、不发送 ──
        var previewBefore = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_private_msg");
        var previewResponse = await http.PostAsync(
            $"http://127.0.0.1:{panelPort}/api/health-report",
            new StringContent("{\"mode\":\"preview\"}", Encoding.UTF8, "application/json"));
        var previewBody = await previewResponse.Content.ReadAsStringAsync();
        var preview = JsonNode.Parse(previewBody) as JsonObject;
        var previewText = preview?["text"]?.ToString() ?? string.Empty;

        Check("③ 预览返回了正文，而且**一条 QQ 消息都没发**",
            previewResponse.IsSuccessStatusCode &&
            preview?["mode"]?.ToString() == "preview" &&
            protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_private_msg") == previewBefore,
            $"预览 {(previewText.Length > 0 ? "有" : "没有")}正文；私聊发送数 {previewBefore} → " +
            $"{protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_private_msg")}");

        Check("③ 报告里有机器人/QQ/模型/会话/资源/日志六类信息",
            previewText.Contains("服务器健康日报") && previewText.Contains("机器人：运行") &&
            previewText.Contains("QQ：") && previewText.Contains("模型：mock-model") &&
            // 会话数跟“机器人”同一行（机器人：运行 …｜会话 N 个｜…），所以这里不要求“会话：”
            previewText.Contains("会话 ") && previewText.Contains("资源：") && previewText.Contains("日志："),
            previewText.Replace("\n", " ⏎ "));

        Check("③ ★ 探针真的问了模型网关（/v1/models 被请求，且报告说“可达”）",
            openAi.ModelListHits >= 1 && previewText.Contains("模型：mock-model 可达"),
            $"ModelListHits={openAi.ModelListHits}；{Snippet(previewText, "模型：")}");

        Check("③ ★ 报告说清了 QQ 与协议端的真实状态（连接 ≠ 账号在线）",
            previewText.Contains("QQ 在线") && previewText.Contains("协议端已连接"),
            Snippet(previewText, "QQ："));

        Check("③ 报告里的内容标记了“北京时间”的时刻（MM-dd HH:mm）",
            previewText.Contains(HealthReportBeijingNow().ToString("MM-dd HH:mm")),
            previewText.Split('\n').FirstOrDefault());

        // ── ④ 「现在发一条」真的发到私聊 ──
        var sendResponse = await http.PostAsync(
            $"http://127.0.0.1:{panelPort}/api/health-report",
            new StringContent("{\"mode\":\"send\"}", Encoding.UTF8, "application/json"));
        var sendBody = await sendResponse.Content.ReadAsStringAsync();

        var manualSends = await WaitForSendsAsync(protocol, 1, TimeSpan.FromSeconds(20), "send_private_msg");
        var manual = manualSends.LastOrDefault();
        Check("④ ★ 手动发送走进私聊（user_id = 配置的那个 QQ 号，且不是群消息）",
            manual is not null && manual["params"]?["user_id"]?.ToString() == ownerUid.ToString() && manualSends.Count == 1,
            $"code={sendResponse.StatusCode}；{Snippet(sendBody, "text")}；params={manual?["params"]?.ToJsonString()}");

        Check("④ ★ 发出去的就是那份状态（不是“已发送”这种空话）",
            MessageText(manual).Contains("服务器健康日报") && MessageText(manual).Contains("资源："),
            Snippet(MessageText(manual), "服务器健康日报"));

        Check("④ 手动发送后报文里记了“上次发出”（面板看得到）",
            (sendBody.Contains("\"ok\":true")) &&
            (await ReadHealthReportAsync(panelPort)).Contains("\"lastSentAt"),
            Snippet(sendBody, "lastSent"));

        // ── ⑤ 到点自己发：把时刻改成下一个整分（不用重启）──
        // 这一条才是“每天定时”本身：定时器必须按**改后的时刻**重排（以前这类定时器只在启动时建一次，
        // 面板里改了要重启才生效 —— 静默兜底/画像巡检都踩过一次）。
        var target = NextBeijingMinuteWithSlack(10);
        var settingsResponse = await http.PostAsync(
            $"http://127.0.0.1:{panelPort}/api/settings",
            new StringContent(
                $"{{\"healthReportEnabled\":true,\"healthReportTime\":\"{target:HH:mm}\",\"healthReportTargets\":\"{ownerUid}\"}}",
                Encoding.UTF8, "application/json"));
        Check("⑤ 面板保存了新的推送时刻（不用重启）", settingsResponse.IsSuccessStatusCode, $"HTTP {(int)settingsResponse.StatusCode}");

        var armedText = await ReadHealthReportAsync(panelPort);
        var armed = JsonNode.Parse(armedText) as JsonObject;
        var armedNext = DateTimeOffset.TryParse(armed?["nextRunAt"]?.ToString(), out var parsedArmed) ? parsedArmed : default;
        Check("⑤ ★ 定时器按新时刻重排（nextRunAt = 刚填的那个整分）",
            armedNext != default && armedNext.ToString("yyyy-MM-dd HH:mm") == target.ToString("yyyy-MM-dd HH:mm"),
            $"nextRunAt={armed?["nextRunAt"]}，期望 {target:O}（配置 {target:HH:mm}）");

        var sendsBeforeSchedule = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_private_msg");
        var scheduled = await WaitForSendsAsync(protocol, sendsBeforeSchedule + 1, TimeSpan.FromSeconds(120), "send_private_msg");
        var firedAt = DateTimeOffset.UtcNow;
        var scheduledSend = scheduled.LastOrDefault();

        Check("⑤ ★ 到点自己发了（不是只写进面板）",
            scheduled.Count == sendsBeforeSchedule + 1 && scheduledSend is not null,
            $"发送数 {sendsBeforeSchedule} → {scheduled.Count}");

        Check("⑤ ★ 是**到点才发**（不早发：不早于目标时刻 3 秒）",
            firedAt >= target.ToUniversalTime().AddSeconds(-3),
            $"发出观测 {firedAt:O}，目标 {target.ToUniversalTime():O}");

        Check("⑤ ★ 报文头写的就是那个整分（配置 18:00 → 18:00 发，不是别的点）",
            MessageText(scheduledSend).Contains($"服务器健康日报 · {target:MM-dd HH:mm}"),
            MessageText(scheduledSend).Split('\n').FirstOrDefault());

        // ── ⑥ 同一天只发一条 ──
        await Task.Delay(12000);
        Check("⑥ ★ 到点发过之后不会再发（同一天只一条）",
            protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_private_msg") == sendsBeforeSchedule + 1,
            $"{protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_private_msg")} 条");

        // ── ⑦ 关掉开关 → 不再排下一次 ──
        await http.PostAsync(
            $"http://127.0.0.1:{panelPort}/api/settings",
            new StringContent("{\"healthReportEnabled\":false}", Encoding.UTF8, "application/json"));
        var offText = await ReadHealthReportAsync(panelPort);
        var off = JsonNode.Parse(offText) as JsonObject;
        Check("⑦ ★ 关掉开关后不再排下一次（nextRunAt = null）",
            off?["enabled"]?.GetValue<bool>() == false && (off?["nextRunAt"] is null || off["nextRunAt"]!.ToString().Length == 0),
            offText);

        // 收件人没配时不能瞎发（空 = 不发，而不是“发给所有人”）
        await http.PostAsync(
            $"http://127.0.0.1:{panelPort}/api/settings",
            new StringContent("{\"healthReportEnabled\":true,\"healthReportTargets\":\"\"}", Encoding.UTF8, "application/json"));
        var noTarget = JsonNode.Parse(await ReadHealthReportAsync(panelPort)) as JsonObject;
        Check("⑦ ★ 收件人为空时不排下一次（宁可发不出去，也不乱发）",
            noTarget?["nextRunAt"] is null || noTarget["nextRunAt"]!.ToString().Length == 0,
            noTarget?.ToJsonString());

        await bot.StopAsync();
    }

    private static async Task<string> ReadHealthReportAsync(int panelPort)
    {
        var (_, body) = await PanelGetAsync($"http://127.0.0.1:{panelPort}/api/health-report");
        return body;
    }

    /// <summary>当前北京时间（这个场景自己算一份 —— 不借机器人的实现，否则“算错了一起错”）。</summary>
    private static DateTimeOffset HealthReportBeijingNow()
        => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, HealthReportBeijingZone());

    /// <summary>下一个整分（北京时间），至少留 slack 秒余量（测试要能等得到）。</summary>
    private static DateTimeOffset NextBeijingMinuteWithSlack(int slackSeconds)
    {
        var now = HealthReportBeijingNow();
        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).AddMinutes(1);
        while ((candidate - now).TotalSeconds < slackSeconds)
        {
            candidate = candidate.AddMinutes(1);
        }

        return candidate;
    }

    private static TimeZoneInfo HealthReportBeijingZone()
    {
        foreach (var id in new[] { "Asia/Shanghai", "China Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
                // 换下一个
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("UTC+08", TimeSpan.FromHours(8), "北京时间", "北京时间");
    }
}
