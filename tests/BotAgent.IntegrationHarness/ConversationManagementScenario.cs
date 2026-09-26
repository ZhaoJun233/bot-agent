using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    /// <summary>
    /// S49 面板会话管理：持久恢复、改名、清空、删除，以及恢复后的发送闸门。
    /// 仅使用合成私聊事件；不读取任何外部会话或生产数据。
    /// </summary>
    private static async Task RunConversationManagementScenarioAsync()
    {
        Section("S49 面板会话管理 · 持久化 · 白名单发送闸门");

        const int openAiPort = 17889;
        const int protocolPort = 13109;
        const int panelPort = 18161;
        const long senderId = 30003;
        const string initialName = "合成会话名称";
        const string panelToken = "it-s49-token";
        const string conversationKey = "private:30003";

        var dataDir = NewDataDir("s49");
        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        openAi.EnqueueReply("""{"suitability":90,"reply":"合成回复"}""");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.StartForwardServerAsync(protocolPort, cts.Token);

        var env = new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ForwardWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"ws://127.0.0.1:{protocolPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = "*",
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_PANEL_TOKEN"] = panelToken
        };

        static JsonObject? FindConversation(string body, string key)
        {
            var root = JsonNode.Parse(body) as JsonObject;
            if (root?["conversations"] is not JsonArray conversations)
            {
                return null;
            }

            foreach (var item in conversations)
            {
                if (item is JsonObject conversation && conversation["key"]?.GetValue<string>() == key)
                {
                    return conversation;
                }
            }

            return null;
        }

        async Task<JsonObject?> ReadConversationAsync()
        {
            var response = await PanelGetAsync($"http://127.0.0.1:{panelPort}/api/state", panelToken);
            return response.Status == 200 ? FindConversation(response.Body, conversationKey) : null;
        }



        var bot = StartBot(env);
        try
        {
            await WaitForPortAsync(panelPort, cts.Token, bot);
            Check("S49 正向协议端已连接",
                await WaitUntilAsync(() => protocol.IsConnected, TimeSpan.FromSeconds(20)));
            await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(15));

            await protocol.SendPrivateMessageAsync(senderId, "合成用户", "合成输入", 7491, cts.Token);
            Check("S49 收到合成消息并回复",
                await protocol.WaitForActionAsync("send_private_msg", TimeSpan.FromSeconds(30)) is not null);
            await Task.Delay(1000);

            var conversation = await ReadConversationAsync();
            Check("S49 面板列出已持久化会话",
                conversation is not null && conversation["messageCount"]?.GetValue<int>() > 0);

            var rename = await PanelPostJsonAsync(
                $"http://127.0.0.1:{panelPort}/api/conversations/{Uri.EscapeDataString(conversationKey)}/rename",
                "{\"name\":\"" + initialName + "\"}", panelToken);
            Check("S49 面板改名成功", rename.Code == 200 && rename.Body.Contains("\"ok\":true"));

            conversation = await ReadConversationAsync();
            Check("S49 改名立即反映且保留原始名称字段",
                conversation?["nameRaw"]?.GetValue<string>() == initialName);
        }
        finally
        {
            await bot.StopAsync();
        }

        env["QQCHAT_WHITELIST"] = "30004";
        var bot2 = StartBot(env);
        try
        {
            await WaitForPortAsync(panelPort, cts.Token, bot2);
            Check("S49 重启后仍显示历史会话", await ReadConversationAsync() is not null);

            var restored = await ReadConversationAsync();
            Check("S49 重启后历史消息仍在",
                restored?["messageCount"]?.GetValue<int>() > 0);

            var blocked = await PanelPostJsonAsync(
                $"http://127.0.0.1:{panelPort}/api/conversations/{Uri.EscapeDataString(conversationKey)}/send",
                "{\"text\":\"合成面板发送\"}", panelToken);
            Check("S49 非白名单历史会话禁止代发", blocked.Code == 400);

            var clear = await PanelPostJsonAsync(
                $"http://127.0.0.1:{panelPort}/api/conversations/{Uri.EscapeDataString(conversationKey)}/clear",
                "{}", panelToken);
            Check("S49 清空历史成功", clear.Code == 200 && clear.Body.Contains("\"ok\":true"));
            var cleared = await ReadConversationAsync();
            Check("S49 清空只保留会话入口", cleared is not null && cleared["messageCount"]?.GetValue<int>() == 0);

            var deleted = await PanelPostJsonAsync(
                $"http://127.0.0.1:{panelPort}/api/conversations/{Uri.EscapeDataString(conversationKey)}/delete",
                "{}", panelToken);
            Check("S49 删除会话成功", deleted.Code == 200 && deleted.Body.Contains("\"ok\":true"));
            Check("S49 删除后面板不再列出会话", await ReadConversationAsync() is null);
        }
        finally
        {
            await bot2.StopAsync();
        }

        var bot3 = StartBot(env);
        try
        {
            await WaitForPortAsync(panelPort, cts.Token, bot3);
            Check("S49 删除后重启也不会恢复旧会话", await ReadConversationAsync() is null);
        }
        finally
        {
            await bot3.StopAsync();
        }
    }
}
