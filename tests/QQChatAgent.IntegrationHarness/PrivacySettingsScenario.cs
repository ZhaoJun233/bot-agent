using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S36 「列出会话时脱敏」开关 + 「Agent 附加提示词」（号主 2026-09-18：
/// 开发/排查时**不许读取群聊正文与成员隐私**，默认提示词就要把这条写死）。
///
/// 这个场景钉住四件事：
///   ① 面板默认：脱敏**开**、附加提示词 = 那条隐私红线，而且默认值由服务端下发（前端不抄一份）；
///   ② Agent 列出会话时，群名 / 会话标题里的长数字只留前 3 后 2（`group:123456789` → `群聊 123***89`），
///      群里的 `//sessions all` 与面板总览都遵守；关掉开关才露真名；
///   ③ 显示归显示：`nameRaw` 仍是真名（面板改名要用真名，否则会把「群友A」写回去）；
///   ④ 附加提示词真的随任务下发 —— 服务器内置 agent 的**系统提示词**里能看到它；
///      面板改成自定义值/清空后，下一轮立刻按新的来（不是启动时读一次就定死）。
///
/// 注意：这里的断言只看**形状与标志串**，不看群聊正文 —— 这也是这个功能本身的意义。
/// </summary>
public static partial class Program
{
    private static async Task RunPrivacySettingsScenarioAsync()
    {
        Section("S36 列出会话时脱敏 + Agent 附加提示词（默认：不读取敏感信息）");

        const int openAiPort = 17881;
        const int agentAiPort = 17882;
        const int botWsPort = 13095;
        const int panelPort = 18115;
        const long groupId = 66760;
        const string fakeChatKey = "group:123456789";   // 刻意用长 id：好验「前 3 后 2」
        const string fakeName = "会话 123456789";

        var panel = $"http://127.0.0.1:{panelPort}";
        var dataDir = NewDataDir("s36");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        // 服务器内置 agent 走自己的假网关：这一轮要看它的**系统提示词**里有没有那份附加提示词
        using var agentAi = new MockOpenAi(agentAiPort);
        agentAi.Start();
        agentAi.EnqueueReply("""{"final":"默认提示词那轮看完了"}""");
        agentAi.EnqueueReply("""{"final":"自定义提示词那轮看完了"}""");
        agentAi.EnqueueReply("""{"final":"清空提示词那轮看完了"}""");
        agentAi.EnqueueReply("""{"final":"备用结论"}""");

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
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_AGENT"] = "1",
            ["QQCHAT_AGENT_USERS"] = "20002",
            ["QQCHAT_AGENT_SERVER"] = "1",
            ["QQCHAT_AGENT_TARGET"] = "server",
            ["QQCHAT_AGENT_SERVER_WORKDIR"] = "/tmp",
            ["QQCHAT_AGENT_SERVER_URL"] = agentAi.BaseUrl,
            ["QQCHAT_AGENT_SERVER_MODEL"] = "custom-agent-model",
            ["QQCHAT_AGENT_PROGRESS"] = "0"
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

        // ── ① 面板默认：脱敏开 + 附加提示词就是那条隐私红线 ──
        var (settingsCode, settingsBody) = await HttpGetAsync($"{panel}/api/settings");
        // 响应是 { runtime: {...面板设置...}, env: {...} } —— 开关与提示词都在 runtime 里
        var settings = (JsonNode.Parse(settingsBody) as JsonObject)?["runtime"] as JsonObject ?? new JsonObject();
        Check("面板拿得到设置", settingsCode == 200 && settings.Count > 0, $"HTTP {settingsCode}");

        Check("★ 脱敏开关默认开（列表默认不漏群名/昵称/QQ 号）",
            settings["enableAgentMask"]?.GetValue<bool>() == true,
            settings["enableAgentMask"]?.ToJsonString() ?? "(没有这个字段)");

        var defaultPrompt = settings["agentPrompt"]?.GetValue<string>() ?? string.Empty;
        Check("★ 默认附加提示词写的就是「不读取敏感信息」",
            defaultPrompt.Contains("隐私红线") && defaultPrompt.Contains("不要读取") &&
            defaultPrompt.Contains("ERROR") && defaultPrompt.Contains("message"),
            defaultPrompt.Length > 90 ? defaultPrompt[..90].Replace('\n', ' ') + "…" : defaultPrompt);
        Check("★ 默认那份由服务端下发（面板「恢复默认」不用前端抄一份，避免两处漂移）",
            settings["agentPromptDefault"]?.GetValue<string>() == defaultPrompt);

        // ── ② 建一个「长 id 群」的会话：它必须以脱敏形状出现在列表里 ──
        var (newCode, newBody) = await PostJsonAsync($"{panel}/api/agent/sessions",
            $$"""{"key":"{{fakeChatKey}}","action":"new","name":"{{fakeName}}","backend":"server"}""");
        Check("面板能建会话（用来验脱敏）", newCode == 200,
            $"HTTP {newCode} {Snippet(newBody, "ok")}");

        var (listCode, listBody) = await HttpGetAsync($"{panel}/api/agent/sessions");
        var all = JsonNode.Parse(listBody) as JsonObject ?? new JsonObject();
        var chat = (all["chats"] as JsonObject)?[fakeChatKey] as JsonObject;
        var panelSession = chat?["sessions"]?.AsArray().FirstOrDefault() as JsonObject;
        Check("★ 面板总览：群名只留前 3 后 2",
            chat?["name"]?.GetValue<string>() == "群聊 123***89",
            chat?["name"]?.GetValue<string>() ?? "(这个聊天不在列表里)");
        Check("★ 面板总览：会话标题里的 QQ 号也被遮住",
            panelSession?["name"]?.GetValue<string>() == "会话 123***89",
            panelSession?["name"]?.GetValue<string>() ?? "(没有会话)");
        Check("★ 显示归显示：nameRaw 仍是真名（面板改名要用真名）",
            panelSession?["nameRaw"]?.GetValue<string>() == fakeName,
            panelSession?["nameRaw"]?.GetValue<string>() ?? "(没有 nameRaw)");

        // ── ③ 群里那条路：//sessions all 也不能露真名 ──
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//sessions all", 19001, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("123***89")), TimeSpan.FromSeconds(30));
        var maskedReply = Sent().LastOrDefault(t => t.Contains("群聊 ")) ?? string.Empty;
        Check("★ 群里 //sessions all：群名与标题都脱敏（长数字只留前 3 后 2）",
            maskedReply.Contains("群聊 123***89") && maskedReply.Contains("会话 123***89") && !maskedReply.Contains("123456789"),
            Snippet(maskedReply, "群聊 "));

        // ── ④ 面板关掉开关 → 立刻能看到真名（开关真的在生效，不是摆设）──
        var (offCode, _) = await PostJsonAsync($"{panel}/api/settings", """{"enableAgentMask":false}""");
        Check("面板关掉脱敏成功", offCode == 200, $"HTTP {offCode}");
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//sessions all", 19002, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("123456789")), TimeSpan.FromSeconds(30));
        Check("★ 关掉开关后群里能看到真名（对照组，证明前面遮的是它）",
            Sent().LastOrDefault(t => t.Contains("会话 "))?.Contains("123456789") == true,
            Snippet(Sent().LastOrDefault(t => t.Contains("群聊 ")) ?? "(没有群聊那行)", "群聊 "));

        var (onCode, _) = await PostJsonAsync($"{panel}/api/settings", """{"enableAgentMask":true}""");
        Check("再把脱敏打开成功", onCode == 200, $"HTTP {onCode}");

        // ── ⑤ 附加提示词随任务下发：服务器内置 agent 的系统提示词里能看到默认那份 ──
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//看看磁盘剩多少", 19011, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => agentAi.Requests.Count >= 1, TimeSpan.FromSeconds(40));
        var system1 = agentAi.Requests.Count > 0 ? SystemText(agentAi.Requests[0]) : string.Empty;
        Check("★ 附加提示词进了 agent 的系统提示词（默认 = 隐私红线）",
            system1.Contains("隐私红线") && system1.Contains("不要读取"),
            Snippet(system1, "隐私红线"));
        Check("★ 任务正文照旧是用户那句话（提示词是附加，不是替换）",
            agentAi.Requests.Count > 0 && UserTexts(agentAi.Requests[0]).Any(t => t.Contains("看看磁盘剩多少")),
            agentAi.Requests.Count == 0 ? "(没有请求)" : Snippet(UserTexts(agentAi.Requests[0]).LastOrDefault() ?? "(空)", "看看磁盘"));

        // ── ⑥ 面板改成自定义值 → 下一轮就用新的 ──
        const string customPrompt = "自定义提示词：只准看 /tmp，不许读聊天记录";
        var (customCode, _) = await PostJsonAsync($"{panel}/api/settings",
            $$"""{"agentPrompt":"{{customPrompt}}"}""");
        Check("面板保存自定义附加提示词成功", customCode == 200, $"HTTP {customCode}");

        var requestsBefore = agentAi.Requests.Count;
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//再看一眼", 19012, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => agentAi.Requests.Count > requestsBefore, TimeSpan.FromSeconds(40));
        var system2 = agentAi.Requests.Count > requestsBefore ? SystemText(agentAi.Requests[^1]) : string.Empty;
        Check("★ 换成自定义提示词后，系统提示词立刻跟着变（不用重启）",
            system2.Contains(customPrompt) && !system2.Contains("隐私红线"),
            Snippet(system2, customPrompt));

        // ── ⑦ 清空 → 明确“不带”，不是回落到默认 ──
        var (clearCode, _) = await PostJsonAsync($"{panel}/api/settings", """{"agentPrompt":""}""");
        Check("面板清空附加提示词成功", clearCode == 200, $"HTTP {clearCode}");

        requestsBefore = agentAi.Requests.Count;
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//最后一眼", 19013, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => agentAi.Requests.Count > requestsBefore, TimeSpan.FromSeconds(40));
        var system3 = agentAi.Requests.Count > requestsBefore ? SystemText(agentAi.Requests[^1]) : string.Empty;
        Check("★ 清空 = 真的不带（既没有自定义那句，也没有默认那句）",
            !system3.Contains(customPrompt) && !system3.Contains("隐私红线"),
            Snippet(system3, "工作目录"));

        await bot.StopAsync();
    }

    /// <summary>POST 一段 JSON（场景里只用来打面板的设置/会话接口）。</summary>
    private static async Task<(int Code, string Body)> PostJsonAsync(string url, string json)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var res = await http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
    }
}
