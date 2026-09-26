using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S37 服务器 agent 自己的密钥（面板入口）。
///
/// 背景（管理员 2026-09-18 问「这里怎么没有 key 的填写 UI」）：服务器 agent 能单独指一个
/// OpenAI 兼容网关（AgentServerBaseUrl），但密钥当时只能改 .env 的 AGENT_SERVER_KEY ——
/// 面板上只有地址和模型，换网关就卡在「key 填哪儿」。
///
/// 这个场景钉住五件事：
///   ① 没配专用密钥时，agent 走**聊天那把**（回落到 QQCHAT_API_KEY）—— 记录改动前的既有语义；
///   ② 面板 POST agentServerKey 后：GET 只回「设没设 / 掩码 / 来源」，**绝不下发明文**；
///   ③ 下一轮 agent 请求真的带上面板那把 key（Authorization 头），不是只存起来摆着；
///   ④ 明文不落 settings 表（settings.json 那份会被人贴出来排障），只进 secrets 表；
///   ⑤ 清空后回退：来源变回 env/none，agent 请求又用回聊天那把。
///
/// 全程用合成密钥/合成网关（mock），不碰真实配置。
/// </summary>
public static partial class Program
{
    private static async Task RunAgentServerKeyScenarioAsync()
    {
        Section("S37 服务器 agent 自己的密钥（面板可填 / 不回显 / 真生效）");

        const int chatAiPort = 17891;
        const int agentAiPort = 17892;
        const int botWsPort = 13096;
        const int panelPort = 18116;
        const long groupId = 66761;

        const string chatKey = "sk-chat-env-1111";
        const string agentKey = "sk-agent-panel-2222";

        var panel = $"http://127.0.0.1:{panelPort}";
        var dataDir = NewDataDir("s37");

        using var chatAi = new MockOpenAi(chatAiPort);
        chatAi.Start();

        using var agentAi = new MockOpenAi(agentAiPort);
        agentAi.Start();
        for (var i = 0; i < 4; i++)
        {
            agentAi.EnqueueReply("""{"final":"这轮看完了"}""");
        }

        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = chatKey,
            ["QQCHAT_BASE_URL"] = chatAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = groupId.ToString(),
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_AGENT"] = "1",
            ["QQCHAT_AGENT_USERS"] = "20003",
            ["QQCHAT_AGENT_SERVER"] = "1",
            ["QQCHAT_AGENT_TARGET"] = "server",
            ["QQCHAT_AGENT_SERVER_WORKDIR"] = "/tmp",
            ["QQCHAT_AGENT_SERVER_URL"] = agentAi.BaseUrl,
            ["QQCHAT_AGENT_SERVER_MODEL"] = "custom-agent-model",
            ["QQCHAT_AGENT_PROGRESS"] = "0"
            // 刻意**不设** QQCHAT_AGENT_SERVER_KEY：先验回落，再验面板覆盖
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await Task.Delay(800);

        async Task<string> RunAgentTaskAsync(long messageId, string text)
        {
            var before = agentAi.Requests.Count;
            await protocol.SendGroupMessageAsync(groupId, 20003, "老王", text, messageId, mentionBot: false, ct: cts.Token);
            await WaitUntilAsync(() => agentAi.Requests.Count > before, TimeSpan.FromSeconds(40));
            return agentAi.LastAuthorization ?? string.Empty;
        }

        // ── ① 基线：没配专用密钥 → 回落聊天那把 ──
        var fallbackAuth = await RunAgentTaskAsync(19101, "//先看一眼");
        Check("★ 没配专用密钥时，agent 用聊天那把密钥（回落到 QQCHAT_API_KEY，既有语义不变）",
            fallbackAuth == $"Bearer {chatKey}",
            $"Authorization={Redact(fallbackAuth)}");

        // ── ② 面板填密钥 → 只回形状，不回显明文 ──
        var (setCode, setBody) = await PanelPostJsonAsync($"{panel}/api/settings",
            $$"""{"agentServerKey":"{{agentKey}}"}""");
        Check("面板能保存服务器 agent 的密钥", setCode == 200, $"HTTP {setCode}");

        var (getCode, getBody) = await PanelGetAsync($"{panel}/api/settings");
        var settings = (JsonNode.Parse(getBody) as JsonObject)?["runtime"] as JsonObject ?? new JsonObject();
        Check("GET /api/settings 正常", getCode == 200 && settings.Count > 0, $"HTTP {getCode}");
        Check("★ 面板看到的是「已设置」+ 掩码（前 4 位 + ****），不是明文",
            settings["agentServerKeySet"]?.GetValue<bool>() == true &&
            settings["agentServerKeyMasked"]?.GetValue<string>() == "sk-a****",
            $"set={settings["agentServerKeySet"]?.ToJsonString()} masked={settings["agentServerKeyMasked"]?.ToJsonString()}");
        Check("★ 来源标成 panel（面板填过就以面板为准，与聊天那把同一套规矩）",
            settings["agentServerKeySource"]?.GetValue<string>() == "panel",
            settings["agentServerKeySource"]?.ToJsonString() ?? "(没有这个字段)");
        Check("★ 保存响应里也不含明文密钥（有人会把响应贴出来排障）",
            !setBody.Contains(agentKey) && !getBody.Contains(agentKey),
            setBody.Contains(agentKey) ? "POST 响应里出现了明文" : "GET 响应里出现了明文");

        // ── ③ 下一轮 agent 请求真的带面板那把 key ──
        var panelAuth = await RunAgentTaskAsync(19102, "//再看看");
        Check("★ 面板填的密钥真的随 agent 请求发出去（Authorization 头，不是只存起来摆着）",
            panelAuth == $"Bearer {agentKey}",
            $"Authorization={Redact(panelAuth)}");

        // ── ④ 只进 secrets 表，不进 settings 表 ──
        var storedSecret = DbProbe.Text(dataDir, "SELECT value FROM secrets WHERE name = 'agentServerKey'");
        Check("密钥落在 secrets 表里（库里单独一张表，库文件权限 600）",
            storedSecret == agentKey,
            string.IsNullOrEmpty(storedSecret) ? "(没有这条)" : "(有这条，值已省略)");

        var settingsJson = DbProbe.Text(dataDir, "SELECT json FROM settings WHERE id = 1") ?? string.Empty;
        Check("★ 明文不进 settings 表（那份是要给面板读、也方便贴出来排障的）",
            settingsJson.Length > 0 && !settingsJson.Contains(agentKey) && !settingsJson.Contains("agentServerKey"),
            settingsJson.Contains(agentKey) ? "settings 里出现了明文密钥" : "settings 里出现了 agentServerKey 字段");

        // ── ⑤ 清空 → 回退环境变量 / 聊天那把 ──
        var (clearCode, _) = await PanelPostJsonAsync($"{panel}/api/settings", """{"agentServerKey":""}""");
        Check("面板能清空服务器 agent 的密钥", clearCode == 200, $"HTTP {clearCode}");

        var (afterCode, afterBody) = await PanelGetAsync($"{panel}/api/settings");
        var after = (JsonNode.Parse(afterBody) as JsonObject)?["runtime"] as JsonObject ?? new JsonObject();
        Check("★ 清空后来源不再标 panel（未单独配 = 会用聊天那把）",
            afterCode == 200 &&
            after["agentServerKeySource"]?.GetValue<string>() is "none" or "env" &&
            after["agentServerKeySet"]?.GetValue<bool>() != true,
            $"source={after["agentServerKeySource"]?.ToJsonString()} set={after["agentServerKeySet"]?.ToJsonString()}");
        Check("secrets 表里那条被删掉了（不是留个空串）",
            DbProbe.Count(dataDir, "SELECT COUNT(1) FROM secrets WHERE name = 'agentServerKey'") == 0,
            $"{DbProbe.Count(dataDir, "SELECT COUNT(1) FROM secrets WHERE name = 'agentServerKey'")} 条");

        var clearedAuth = await RunAgentTaskAsync(19103, "//最后一眼");
        Check("★ 清空后 agent 又用回聊天那把密钥（清空 = 真回退，不是把请求用空 key 发出去）",
            clearedAuth == $"Bearer {chatKey}",
            $"Authorization={Redact(clearedAuth)}");
    }

    /// <summary>日志/断言里不印密钥本体，只留“有没有 + 前 7 位”。</summary>
    private static string Redact(string authorization)
        => authorization.Length == 0 ? "(空)" : authorization[..Math.Min(authorization.Length, 7)] + "…（已省略）";
}
