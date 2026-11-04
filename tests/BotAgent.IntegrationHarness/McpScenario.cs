using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S53 服务器内置 agent 调用外部 MCP 工具（Streamable HTTP 2025-11-25）。
///
/// 钉住四件事：
///   ① `BOTAGENT_MCP_*` 配置真的接到了服务器 agent：模型点名 `mcp.demo.echo` → 真的发出一次
///      `tools/call`，结果（echo:hi）被喂回模型，最终结论回到群里；
///   ② 只有 `allowedTools` 里的工具能到达服务器：`mcp.demo.unauthorized` 被工具层拒绝，
///      MockMcpServer 的 tools/call 计数不增加，模型拿到的是“没开”的说明；
///   ③ 协议版本是 2025-11-25（客户端 pin 的版本，服务端也按这个协商）；
///   ④ 这条链路不会顺手产生 QQ 动作（没有 `send_like`）。
///
/// 全部是合成值（10001 / 20002 / 66800 / example 风格域名都不出网），MockMcpServer 只绑回环。
/// </summary>
public static partial class Program
{
    private static async Task RunMcpServerScenarioAsync()
    {
        Section("S53 服务器 agent 调用外部 MCP 工具（Streamable HTTP / 授权边界）");

        var openAiPort = FreePort(17861);
        var agentAiPort = FreePort(17862);
        var botWsPort = FreePort(13073);
        var healthPort = FreePort(18123);
        var mcpPort = FreePort(17863);
        const long groupId = 66800;
        const long ownerId = 20002;

        var dataDir = NewDataDir("s53");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        using var agentAi = new MockOpenAi(agentAiPort);
        agentAi.Start();

        // 服务器 agent 专用规则：先点名 MCP 工具，工具结果回来后再给结论。
        agentAi.AddRule("调一下外部工具",
            """{"thought":"调 MCP","tool":"mcp.demo.echo","message":"hi"}""",
            """{"final":"外部工具回来了。"}""");

        // 第二个规则：模型点名一个**没有被 allowedTools 授权**的工具。服务器 tools/list 会列出它，
        // 但 McpToolSource 按配置过滤掉它，所以它连 routes 都进不去，绝不会到达服务器。
        agentAi.AddRule("调一下没授权的外部工具",
            """{"thought":"试试没授权的","tool":"mcp.demo.unauthorized","message":"hi"}""",
            """{"final":"外部工具没有权限，没调。"}""");

        using var mcp = new MockMcpServer(mcpPort);

        var mcpConfig = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "demo",
                ["url"] = mcp.Endpoint,
                ["allowedTools"] = new JsonArray { "echo" },
                ["callTimeoutSeconds"] = 5,
                ["maxResultBytes"] = 4096,
            },
        }.ToJsonString();

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
            ["QQCHAT_HEALTH_PORT"] = healthPort.ToString(),
            ["QQCHAT_AGENT"] = "1",
            ["QQCHAT_AGENT_USERS"] = ownerId.ToString(),
            ["QQCHAT_AGENT_TOKEN"] = "tok-s53-mcp",
            ["QQCHAT_AGENT_SERVER"] = "1",
            ["QQCHAT_AGENT_TARGET"] = "server",
            ["QQCHAT_AGENT_SERVER_URL"] = agentAi.BaseUrl,
            ["QQCHAT_AGENT_SERVER_MODEL"] = "custom-agent-model",
            ["QQCHAT_AGENT_SERVER_TOOLS"] = "bash,read,qq",
            ["QQCHAT_AGENT_PROGRESS"] = "0",
            ["QQCHAT_AGENT_MASK"] = "1",
            ["BOTAGENT_MCP_ENABLED"] = "1",
            ["BOTAGENT_MCP_ALLOW_PRIVATE"] = "1",
            ["BOTAGENT_MCP_SERVERS"] = mcpConfig,
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(healthPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        int ActionCount(string action) => protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == action);

        List<string> Sent() => protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        // ── ① 点名 echo：真的发一次 MCP tools/call，结果喂回模型，结论回群 ──
        await protocol.SendGroupMessageAsync(groupId, ownerId, "老王", "//调一下外部工具", 18001,
            mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("外部工具回来了", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(90));
        await Task.Delay(500);

        var reply = Sent().LastOrDefault(t => t.Contains("外部工具回来了", StringComparison.Ordinal)) ?? string.Empty;
        Check("★ MCP 工具调用成功并回群（不是闸门错误/工具错误）",
            reply.Length > 0 &&
            !reply.Contains("闸门拒绝", StringComparison.Ordinal) &&
            !reply.Contains("工具执行出错", StringComparison.Ordinal),
            reply.Length > 0 ? reply : "(没收到回复)");
        Check("★ MCP echo 真的被调用了一次（tools/call == 1）",
            mcp.ToolCallCount == 1, $"tools/call={mcp.ToolCallCount}");
        Check("★ 服务器看到的工具名是 echo",
            mcp.LastToolName == "echo", mcp.LastToolName ?? "(null)");
        Check("★ 协商的协议版本是 2025-11-25",
            mcp.LastProtocolVersion == "2025-11-25", mcp.LastProtocolVersion);
        Check("★ 这条链路没有产生 QQ 动作 send_like",
            ActionCount("send_like") == 0, $"send_like={ActionCount("send_like")}");
        Check("★ MCP 结果（echo:hi）被喂回模型",
            Enumerable.Range(0, agentAi.Requests.Count)
                .Any(i => agentAi.DescribeRequest(i).Contains("echo:hi", StringComparison.Ordinal)),
            $"agent 接口收到 {agentAi.Requests.Count} 次请求");

        // ── ② 未授权工具：服务器会列出，但配置不授权 → 工具层拒绝，服务器计数不增加 ──
        var beforeUnauthorized = mcp.ToolCallCount;
        await protocol.SendGroupMessageAsync(groupId, ownerId, "老王", "//调一下没授权的外部工具", 18002,
            mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("外部工具没有权限", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(90));
        await Task.Delay(500);

        var denial = Sent().LastOrDefault(t => t.Contains("外部工具没有权限", StringComparison.Ordinal)) ?? string.Empty;
        Check("★ 未授权工具没有到达 MCP 服务器（tools/call 计数不增加）",
            mcp.ToolCallCount == beforeUnauthorized,
            $"before={beforeUnauthorized} after={mcp.ToolCallCount}");
        Check("★ 未授权工具没有成为 LastToolName",
            mcp.LastToolName == "echo", mcp.LastToolName ?? "(null)");
        Check("★ 回群的是拒绝/未执行结果（不是闸门错误/工具错误）",
            denial.Length > 0 &&
            !denial.Contains("闸门拒绝", StringComparison.Ordinal) &&
            !denial.Contains("工具执行出错", StringComparison.Ordinal),
            denial.Length > 0 ? denial : "(没收到回复)");
        Check("★★ 模型收到工具层的“没开”说明，而不是假装成功",
            Enumerable.Range(0, agentAi.Requests.Count).Any(i =>
                agentAi.DescribeRequest(i).Contains("mcp.demo.unauthorized", StringComparison.Ordinal) &&
                agentAi.DescribeRequest(i).Contains("没开", StringComparison.Ordinal)),
            $"agent 接口收到 {agentAi.Requests.Count} 次请求");
        Check("★ 两轮下来都没有 send_like",
            ActionCount("send_like") == 0, $"send_like={ActionCount("send_like")}");
    }
}
