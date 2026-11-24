using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S166 裸宿主独立运行与按需安装基线：
///   ① 未预装任何 preset.feature.* 功能包（只含 9 个 system.* 包）；
///   ② 启动阶段零出网目录下载；
///   ③ /readyz 与 /zba/v1/status 健康、9 个系统包全 Running；
///   ④ OneBot 群 @ 正常对话回复，//status 管理命令正常工作。
/// </summary>
public static partial class Program
{
    private static async Task RunBareHostScenarioAsync()
    {
        Section("S166 裸宿主（0 预装功能插件 / 0 启动出网 / 9 system.* / 群聊与 //status）");

        var openAiPort = FreePort(17966);
        var botWsPort = FreePort(13266);
        var panelPort = FreePort(18266);
        var dataDir = NewDataDir("s166");
        const long groupId = 66866;
        const string adminToken = "synthetic-s166-admin";
        const string readToken = "synthetic-s166-reader";

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        openAi.EnqueueReply("""{"suitability": 90, "reply": "裸宿主运行正常，请问有什么可以帮您？"}""");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
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
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_AGENT"] = "1",
            ["QQCHAT_AGENT_USERS"] = "20002",
            ["QQCHAT_AGENT_SERVER"] = "1",
            ["BOTAGENT_ZBA_TOKEN"] = adminToken,
            ["BOTAGENT_ZBA_READ_TOKEN"] = readToken,
            ["BOTAGENT_ZBA_ALLOW_LOOPBACK"] = "0",
            // Keep catalog source non-routable to guarantee zero startup outbound dependency.
            ["BOTAGENT_PLUGIN_CATALOG_URL"] = "https://example.invalid/synthetic/index.json"
        }, seedOfficialPlugins: false);

        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var origin = $"http://127.0.0.1:{panelPort}";

        using (var health = await http.GetAsync(origin + "/healthz", cts.Token))
            Check("S166 裸宿主 /healthz 就绪", (int)health.StatusCode == 200);

        var installedResp = await ZbaAsync(http, "GET", origin + "/zba/v1/plugins/installed", readToken);
        var installedNode = JsonNode.Parse(installedResp.Body);
        var plugins = installedNode?["plugins"]?.AsArray() ?? new JsonArray();
        var ids = plugins.Select(p => p?["manifest"]?["id"]?.GetValue<string>() ?? p?["id"]?.GetValue<string>() ?? "").ToArray();
        Check("S166 裸宿主未安装任何 preset.feature.*，仅装载 9 个 system.* 包",
            installedResp.Status == 200 && ids.Length == 9
            && ids.All(id => id.StartsWith("system.", StringComparison.Ordinal))
            && ids.All(id => !id.StartsWith("preset.feature.", StringComparison.Ordinal)),
            $"status={installedResp.Status}, ids=[{string.Join(",", ids)}]");

        var statusResp = await ZbaAsync(http, "GET", origin + "/zba/v1/status", readToken);
        var statusNode = JsonNode.Parse(statusResp.Body);
        Check("S166 裸宿主 /zba/v1/status ready=true 且零目录下载日志",
            statusResp.Status == 200 && statusNode?["ready"]?.GetValue<bool>() == true
            && bot.OutputLines.All(line => !line.Contains("example.invalid", StringComparison.OrdinalIgnoreCase)),
            statusResp.Body);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        var readyOk = false;
        var readyDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!readyOk && DateTimeOffset.UtcNow < readyDeadline)
        {
            using var ready = await http.GetAsync(origin + "/readyz", cts.Token);
            readyOk = (int)ready.StatusCode == 200;
            if (!readyOk) await Task.Delay(100, cts.Token);
        }
        Check("S166 裸宿主 /readyz 就绪", readyOk);

        await protocol.SendGroupMessageAsync(groupId, 20002, "群友A", "你好，测试裸宿主回复", 16601, mentionBot: true, ct: cts.Token);
        var replyAction = await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(30));
        Check("S166 裸宿主群 @ 对话正常回复",
            replyAction is not null && MessageText(replyAction).Contains("裸宿主运行正常", StringComparison.Ordinal),
            replyAction is null ? "(无回复)" : MessageText(replyAction));

        var sentBeforeCmd = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg");
        await protocol.SendGroupMessageAsync(groupId, 20002, "群友A", "//status", 16602, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Skip(sentBeforeCmd)
            .Any(a => MessageText(a).Contains("服务器内置 agent：", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(30));
        Check("S166 裸宿主 //status 命令正常返回",
            protocol.ActionsReceived.Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
                .Skip(sentBeforeCmd).Any(a => MessageText(a).Contains("服务器内置 agent：", StringComparison.Ordinal)));
    }
}
