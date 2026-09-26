using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S46 面板「工具目录」只读端点（通用 Agent 平台 · 批次 A5）。
///
/// 钉三件事：
///   · 端点**只读**（POST 打不中它 —— 它不在任何写路径上）；
///   · 形状对得上（26 条 = 聊天 10 + QQ 动作 10 + 服务器 6；执行者四家；高风险带例外标注）；
///   · 它读的是**当前策略**而不是写死的表 —— 面板上改一个开关，allowed/needsApproval 跟着变，
///     否则这一页会变成又一份会过期的清单。
/// </summary>
public static partial class Program
{
    private static async Task RunPanelToolsScenarioAsync()
    {
        Section("S46 面板工具目录（只读端点 / 策略跟随 / 高风险例外）");

        const int openAiPort = 17886;
        const int botWsPort = 13106;
        const int healthPort = 18156;
        const long groupId = 66846;

        var dataDir = NewDataDir("s46");

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
            ["QQCHAT_WHITELIST"] = groupId.ToString(),
            ["QQCHAT_HEALTH_PORT"] = healthPort.ToString(),
            ["QQCHAT_PANEL_TOKEN"] = "" // Exercise password-session auth without enabling legacy approval tokens.
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await WaitForPortAsync(healthPort, cts.Token, bot);

        var panel = $"http://127.0.0.1:{healthPort}";

        // Keep the no-token approval invariant, but authenticate ordinary panel operations with a cookie.
        using var sessionHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        using var login = await sessionHttp.PostAsync($"{panel}/api/auth/login",
            new StringContent(new JsonObject { ["password"] = SyntheticPanelPassword }.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json"), cts.Token);
        Check("未配令牌时仍需密码登录", login.IsSuccessStatusCode, $"HTTP {(int)login.StatusCode}");
        var loginBody = JsonNode.Parse(await login.Content.ReadAsStringAsync(cts.Token));
        Check("首次密码登录要求先改密", loginBody?["mustChangePassword"]?.GetValue<bool>() == true);
        using var changed = await sessionHttp.PostAsync($"{panel}/api/auth/change-password",
            new StringContent(new JsonObject
            {
                ["currentPassword"] = SyntheticPanelPassword,
                ["newPassword"] = "synthetic-s46-changed-password"
            }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), cts.Token);
        Check("完成改密后获得正式面板会话", changed.IsSuccessStatusCode, $"HTTP {(int)changed.StatusCode}");
        var (anonymousCode, _) = await HttpGetAsync($"{panel}/api/tools");
        Check("未登录读取工具目录仍返回 401", anonymousCode == 401, $"HTTP {anonymousCode}");

        async Task<(int Code, string Body)> SessionGetAsync(string url)
        {
            using var response = await sessionHttp.GetAsync(url, cts.Token);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cts.Token));
        }
        async Task<(int Code, string Body)> SessionPostAsync(string url, string json)
        {
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await sessionHttp.PostAsync(url, content, cts.Token);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cts.Token));
        }


        var (code, body) = await SessionGetAsync($"{panel}/api/tools");
        var root = JsonNode.Parse(body) as JsonObject ?? new JsonObject();
        var tools = root["tools"] as JsonArray ?? new JsonArray();
        Check("GET /api/tools 可读（只读端点）", code == 200 && root["count"] is not null, $"HTTP {code} {body[..Math.Min(80, body.Length)]}");

        Check("★ 目录条数 = 26（聊天 10 / QQ 动作 10 / 服务器 6）",
            root["count"]?.GetValue<int>() == 26 && tools.Count == 26
            && root["chatCount"]?.GetValue<int>() == 10 && root["qqCount"]?.GetValue<int>() == 10
            && root["serverCount"]?.GetValue<int>() == 6,
            $"count={root["count"]} tools={tools.Count} chat={root["chatCount"]} qq={root["qqCount"]} server={root["serverCount"]}");

        Check("★ 目录自检全绿（缺执行者 / 孤儿执行者 / 缺例外说明 / 重名 四张表都空）",
            root["healthy"]?.GetValue<bool>() == true
            && (root["missingExecutors"] as JsonArray)?.Count == 0
            && (root["unusedExecutors"] as JsonArray)?.Count == 0
            && (root["missingExceptions"] as JsonArray)?.Count == 0
            && (root["duplicateIds"] as JsonArray)?.Count == 0,
            $"healthy={root["healthy"]} 缺执行者={root["missingExecutors"]} 孤儿={root["unusedExecutors"]}"
            + $" 缺例外={root["missingExceptions"]} 重名={root["duplicateIds"]}");

        Check("执行者登记 = 4 家", (root["executors"] as JsonArray)?.Count == 4,
            (root["executors"] as JsonArray)?.ToJsonString() ?? "(没有 executors)");

        JsonObject? Find(string id) => tools.FirstOrDefault(n => n?["id"]?.GetValue<string>() == id) as JsonObject;

        Check("qq.like 在目录里（SendMessage / 安全档 / 非聊天族不在这页复述放行口径）",
            Find("qq.like") is { } like
            && like["category"]?.GetValue<string>() == "SendMessage"
            && like["defaultPolicy"]?.GetValue<string>() == "SafeTierWhenEmpty"
            && like["allowlisted"] is null,
            Find("qq.like")?.ToJsonString() ?? "(没有 qq.like)");

        Check("★ bash 是高风险类别（FileOrShell）且带**显式例外标注**（不靠“没人发现”）",
            Find("bash") is { } bash
            && bash["category"]?.GetValue<string>() == "FileOrShell"
            && bash["highRisk"]?.GetValue<bool>() == true
            && !string.IsNullOrWhiteSpace(bash["exception"]?.GetValue<string>()),
            Find("bash")?.ToJsonString() ?? "(没有 bash)");

        Check("★ 聊天那 10 条都在（含 chat.reply / web.search / voice.speak / demo.echo / ask.question）",
            new[] { "chat.reply", "web.search", "web.read", "music.listen", "music.share", "voice.speak", "sticker.send", "poke.send", "demo.echo", "ask.question" }
                .All(id => Find(id) is not null && Find(id)!["allowlisted"] is not null),
            string.Join(",", tools.Select(t => t?["id"]?.GetValue<string>())));

        // ── 策略跟随（这一页必须读**当前**策略，而不是写死的表）──
        Check("默认：语音关 → voice.speak 未放行",
            Find("voice.speak")?["allowlisted"]?.GetValue<bool>() == false,
            Find("voice.speak")?.ToJsonString() ?? "(没有 voice.speak)");

        var (setCode, _) = await SessionPostAsync($"{panel}/api/settings", """{"enableVoice":true}""");
        Check("面板能打开「语音」开关", setCode == 200, $"HTTP {setCode}");

        var (_, afterBody) = await SessionGetAsync($"{panel}/api/tools");
        var after = JsonNode.Parse(afterBody) as JsonObject ?? new JsonObject();
        var afterTools = after["tools"] as JsonArray ?? new JsonArray();
        JsonObject? FindAfter(string id) => afterTools.FirstOrDefault(n => n?["id"]?.GetValue<string>() == id) as JsonObject;
        Check("★ 开关打开后 voice.speak 变放行（读的是当前策略，不是写死的表）",
            FindAfter("voice.speak")?["allowlisted"]?.GetValue<bool>() == true,
            FindAfter("voice.speak")?.ToJsonString() ?? "(没有 voice.speak)");

        var (apprCode, _) = await SessionPostAsync($"{panel}/api/settings", """{"enableApprovals":true}""");
        Check("面板能打开「人工审批」开关", apprCode == 200, $"HTTP {apprCode}");

        var (_, apprBody) = await SessionGetAsync($"{panel}/api/tools");
        var apprTools = (JsonNode.Parse(apprBody) as JsonObject)?["tools"] as JsonArray ?? new JsonArray();
        // ── 批次 I（面板审批卡）：两条 fail-closed + 只读形状 ──
        // ① 审批开着、但**没配面板令牌** → 一律不许批（密码会话已认证也不能放开这条高权限路径）
        var (noTokenCode, noTokenBody) = await SessionPostAsync($"{panel}/api/approvals/decide", """{"id":"ABC234","approve":true}""");
        Check("★★ 未配面板令牌 → 面板审批被拒（403 panel_token_required，fail-closed）",
            noTokenCode == 403 && noTokenBody.Contains("panel_token_required"),
            $"HTTP {noTokenCode} {noTokenBody[..Math.Min(120, noTokenBody.Length)]}");

        var (listCode, listBody) = await SessionGetAsync($"{panel}/api/approvals");
        var listRoot = JsonNode.Parse(listBody) as JsonObject ?? new JsonObject();
        Check("★ GET /api/approvals 可读（待批单的形状：编号/工具/摘要/脱敏 key/剩余秒数）",
            listCode == 200 && listRoot["enabled"]?.GetValue<bool>() == true
            && listRoot["tokenConfigured"]?.GetValue<bool>() == false
            && listRoot["canDecide"]?.GetValue<bool>() == false
            && listRoot["pending"] is JsonArray,
            $"HTTP {listCode} {listBody[..Math.Min(160, listBody.Length)]}");

        // ② 审批关着 → 也不许批（按钮不出现；服务端同样拒）
        // 注意：这一段把审批**关回去了** —— 下面用到的 apprTools 是上一步（审批还开着时）抓的那份，不受影响。
        var (offCode, _) = await SessionPostAsync($"{panel}/api/settings", """{"enableApprovals":false}""");
        Check("面板能关掉人工审批", offCode == 200, $"HTTP {offCode}");
        var (offDecideCode, offDecideBody) = await SessionPostAsync($"{panel}/api/approvals/decide", """{"id":"ABC234","approve":true}""");
        Check("★★ 审批关着 → 面板审批被拒（403 approvals_disabled）",
            offDecideCode == 403 && offDecideBody.Contains("approvals_disabled"),
            $"HTTP {offDecideCode} {offDecideBody[..Math.Min(120, offDecideBody.Length)]}");

        Check("★ 打开审批后 demo.echo 变成“需审批且已放行”（固定假工具那一路）",
            apprTools.FirstOrDefault(n => n?["id"]?.GetValue<string>() == "demo.echo") is JsonObject echo
            && echo["needsApproval"]?.GetValue<bool>() == true && echo["allowlisted"]?.GetValue<bool>() == true,
            apprTools.FirstOrDefault(n => n?["id"]?.GetValue<string>() == "demo.echo")?.ToJsonString() ?? "(没有 demo.echo)");

        // ── 只读性：POST 打不中这条路由（它只注册了 GET）──
        // 批次 B：会话级权限元数据（只有计数，没有 key —— 脱敏从形状上就不需要）
        var sessionPolicy = (JsonNode.Parse(apprBody) as JsonObject)?["sessionPolicy"] as JsonObject;
        Check("★ /api/tools 带了会话级权限元数据（计数，不含 key）",
            sessionPolicy?["available"]?.GetValue<bool>() == true
            && sessionPolicy["sessions"] is not null
            && sessionPolicy["official"]?.GetValue<int>() + sessionPolicy["private"]?.GetValue<int>()
               == sessionPolicy["sessions"]?.GetValue<int>(),
            sessionPolicy?.ToJsonString() ?? "(没有 sessionPolicy)");

        var (postCode, _) = await SessionPostAsync($"{panel}/api/tools", "{}");
        Check("★ 这个端点只读：POST 打不中（404），面板改不了任何东西",
            postCode == 404, $"HTTP {postCode}");

        var (healthCode, _) = await SessionGetAsync($"{panel}/healthz");
        // 批次 H（追踪页）：新增的静态文件必须真的发得出来（否则页面白屏，而探针是静态检查看不出来）
        var (traceJsCode, traceJsBody) = await SessionGetAsync($"{panel}/trace.js");
        Check("★ 面板发得出 /trace.js（追踪页脚本，嵌进程序集）",
            traceJsCode == 200 && traceJsBody.Contains("TracePage", StringComparison.Ordinal),
            $"HTTP {traceJsCode} {traceJsBody[..Math.Min(80, traceJsBody.Length)]}");
        var (traceCssCode, traceCssBody) = await SessionGetAsync($"{panel}/trace.css");
        var (dashCode, dashBody) = await SessionGetAsync($"{panel}/api/dashboard");
        var dashRoot = JsonNode.Parse(dashBody) as JsonObject ?? new JsonObject();
        Check("★ /api/dashboard 一屏给全（运行 / 延迟 / 内存 / 工具 / 权限 / 轨迹，全是只读汇总）",
            dashCode == 200 && dashRoot["uptimeSeconds"] is not null && dashRoot["latencyMs"] is not null
            && (dashRoot["tools"] as JsonObject)?["total"]?.GetValue<int>() == 26
            && (dashRoot["memory"] as JsonObject)?["usedBytes"] is not null
            && (dashRoot["traces"] as JsonObject)?["capacity"]?.GetValue<int>() == 50,
            $"HTTP {dashCode} {dashBody[..Math.Min(160, dashBody.Length)]}");
        var (dashJsCode, _) = await SessionGetAsync($"{panel}/dash.js");
        Check("面板发得出 /dash.js（仪表盘脚本）", dashJsCode == 200, $"HTTP {dashJsCode}");

        Check("面板发得出 /trace.css（追踪页样式）",
            traceCssCode == 200 && traceCssBody.Contains(".trace-body", StringComparison.Ordinal),
            $"HTTP {traceCssCode} {traceCssBody[..Math.Min(80, traceCssBody.Length)]}");

        // 批次 C：决策轨迹的只读数据面（形状检查；轨迹内容的语义由 SafetyProbe 的台账断言钉住）
        var (tracesCode, tracesBody) = await SessionGetAsync($"{panel}/api/traces");
        var tracesRoot = JsonNode.Parse(tracesBody) as JsonObject ?? new JsonObject();
        Check("★ /api/traces 可读（一轮一条轨迹，容量 50，可空）",
            tracesCode == 200 && tracesRoot["available"]?.GetValue<bool>() == true
            && tracesRoot["capacity"]?.GetValue<int>() == 50,
            $"HTTP {tracesCode} {tracesBody[..Math.Min(120, tracesBody.Length)]}");

        Check("全程没把机器人搞崩（还能正常应答）", healthCode == 200, $"healthz HTTP {healthCode}");
    }
}
