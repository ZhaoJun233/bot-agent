using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunOfficialAgentSendScenarioAsync()
    {
        Section("S76 official current-conversation Agent send");
        foreach (var group in new[] { true, false }) await RunOfficialAgentSendModeAsync(group);
    }

    private static async Task RunOfficialAgentRecallScenarioAsync()
    {
        Section("S77 official Agent send then own-message recall");
        foreach (var group in new[] { true, false }) await RunOfficialAgentSendModeAsync(group, recall: true);
    }

    private static async Task RunOfficialAgentSendModeAsync(bool group, bool recall = false)
    {
        var scenario = recall ? "S77" : "S76";
        var label = scenario + " " + (group ? "group" : "c2c");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var model = new MockOpenAi(FreePort(17889));
        using var agentModel = new MockOpenAi(FreePort(17890));
        agentModel.EnqueueReply("""{"tool":"qq","action":"send","text":"S76_ACTION_SENT"}""");
        agentModel.EnqueueReply("""{"final":"S76_AGENT_FINISHED"}""");
        if (recall)
        {
            var turn = 0;
            var alias = string.Empty;
            agentModel.ReplySelector = request =>
            {
                var step = Interlocked.Increment(ref turn);
                if (step == 1) return """{"tool":"qq","action":"send","text":"S76_ACTION_SENT"}""";
                if (step == 2)
                {
                    var result = request?["messages"]?.AsArray().LastOrDefault()?["content"]?.GetValue<string>() ?? "";
                    alias = System.Text.RegularExpressions.Regex.Match(result, @"message_id=(\d+)").Groups[1].Value;
                    return """{"tool":"qq","action":"recall","message_id":"this"}""";
                }
                if (step == 3) return new JsonObject { ["tool"] = "qq", ["action"] = "recall", ["message_id"] = alias }.ToJsonString();
                return """{"final":"S76_AGENT_FINISHED"}""";
            };
        }
        model.Start();
        agentModel.Start();
        using var official = new MockOfficialGateway(FreePort(18189), FreePort(13089)); official.Start();
        var directory = NewDataDir(scenario.ToLowerInvariant() + "-official-agent-send"); var panelPort = FreePort(18190); var botPort = FreePort(13090);
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = directory, ["QQCHAT_API_KEY"] = "sk-mock", ["QQCHAT_BASE_URL"] = model.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model", ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{botPort}", ["QQCHAT_UIN"] = "10001", ["QQCHAT_WHITELIST"] = "66696",
            ["QQCHAT_GROUP_COOLDOWN"] = "0", ["QQCHAT_PRIVATE_COOLDOWN"] = "0", ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0", ["QQCHAT_WEB_SEARCH"] = "0", ["QQCHAT_ENABLE_VOICE"] = "0", ["QQCHAT_LINK_PREVIEW"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0", ["QQCHAT_SEGMENT_DELAY_MS"] = "0", ["QQCHAT_REPLY_PACING"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(), ["QQCHAT_HEALTH_BIND"] = "127.0.0.1", ["QQCHAT_OFFICIAL"] = "1",
            ["QQCHAT_OFFICIAL_APP_ID"] = "100000009", ["QQCHAT_OFFICIAL_APP_SECRET"] = "test-secret-not-real",
            ["QQCHAT_OFFICIAL_API_BASE"] = official.HttpBase, ["QQCHAT_OFFICIAL_TOKEN_URL"] = official.TokenUrl, ["QQCHAT_OFFICIAL_SANDBOX"] = "1",
            ["QQCHAT_AGENT"] = "1", ["QQCHAT_AGENT_USERS"] = "*", ["QQCHAT_AGENT_SERVER"] = "1", ["QQCHAT_AGENT_TARGET"] = "server",
            ["QQCHAT_AGENT_SERVER_URL"] = agentModel.BaseUrl, ["QQCHAT_AGENT_SERVER_MODEL"] = "mock-agent-model",
            ["QQCHAT_AGENT_SERVER_TOOLS"] = "qq", ["QQCHAT_AGENT_SERVER_QQ_ACTIONS"] = recall ? "send,recall" : "send", ["QQCHAT_AGENT_PROGRESS"] = "0"
        });
        await WaitForPortAsync(panelPort, cts.Token, bot);
        Check(label + " connected", await official.WaitForIdentifyAsync(1, TimeSpan.FromSeconds(15)));
        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await official.PushEventAsync(group ? "GROUP_AT_MESSAGE_CREATE" : "C2C_MESSAGE_CREATE", new JsonObject
        {
            ["id"] = "s76-trigger", ["group_openid"] = group ? "S76_GROUP" : null,
            ["author"] = new JsonObject { [group ? "member_openid" : "user_openid"] = "S76_MEMBER" },
            ["timestamp"] = DateTimeOffset.Now.ToString("O"), ["content"] = "//在当前会话发送 S76_ACTION_SENT"
        });
        bool Contains(string marker) => official.Messages.Any(message => message["body"]?["content"]?.GetValue<string>()?.Contains(marker) == true);
        Check(label + " Agent finishes", await WaitUntilAsync(() => Contains("S76_AGENT_FINISHED"), TimeSpan.FromSeconds(25)));
        var sent = official.Messages.Where(message => message["body"]?["content"]?.GetValue<string>() == "S76_ACTION_SENT").ToArray();
        var toolResult = agentModel.Requests.ElementAtOrDefault(1)?["messages"]?.AsArray().LastOrDefault()?["content"]?.ToString() ?? "missing tool result";
        Check(label + " action reaches official transport exactly once", sent.Length == 1, toolResult);
        Check(label + " action is bound to current target and trigger", sent.Length == 1
            && sent[0]["path"]?.GetValue<string>() == (group ? "/v2/groups/S76_GROUP/messages" : "/v2/users/S76_MEMBER/messages")
            && sent[0]["body"]?["msg_id"]?.GetValue<string>() == "s76-trigger");
        var prompt = agentModel.Requests.Count > 0 ? agentModel.DescribeRequest(0) : "";
        Check(label + " prompt explains official current-conversation scope", prompt.Contains("官方") && prompt.Contains("当前会话"));
        Check(label + " action and final are separate actual model turns", agentModel.Requests.Count == (recall ? 4 : 2), $"requests={agentModel.Requests.Count}");
        if (recall)
        {
            var deletes = official.HttpRequests.Where(request => request["method"]?.GetValue<string>() == "DELETE").ToArray();
            var expected = (group ? "/v2/groups/S76_GROUP/messages/" : "/v2/users/S76_MEMBER/messages/") + sent.SingleOrDefault()?["response_id"]?.GetValue<string>();
            Check(label + " recall uses send receipt and exact official endpoint", deletes.Length == 1
                && deletes[0]["path"]?.GetValue<string>() == expected && deletes[0]["body"] is null);
            var rejection = agentModel.Requests.ElementAtOrDefault(2)?["messages"]?.AsArray().LastOrDefault()?["content"]?.ToString() ?? "";
            var confirmation = agentModel.Requests.ElementAtOrDefault(3)?["messages"]?.AsArray().LastOrDefault()?["content"]?.ToString() ?? "";
            Check(label + " incoming this is refused before own alias succeeds", rejection.Contains("不接受 this") && confirmation.Contains("已撤回"));
            Check(label + " prompt limits recall to own two-minute messages", prompt.Contains("两分钟") && prompt.Contains("message_id"));
        }
        Check(label + " no private send or management transport used", !protocol.ActionsReceived.Any(action =>
            action["action"]?.GetValue<string>() is "send_group_msg" or "send_private_msg" or "set_group_ban" or "set_group_kick" or "delete_msg"));
    }
}
