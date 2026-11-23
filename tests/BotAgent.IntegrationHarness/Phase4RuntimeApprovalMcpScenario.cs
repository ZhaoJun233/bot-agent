using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S163 Phase 4 真实闭环场景（全合成数据）：
///   ① /zba/v1/mcp/servers/test|save|list（拒绝私网/非法方案、探测只读提示、写后 Bearer Token 永不回显）；
///   ② /zba/v1/permissions/roles/save（跨平台 RBAC：group:10001 GroupManager + 全局 Guest）；
///   ③ 普通成员触发 ReadOnlyConfirmed MCP 工具走内联快车道免审批，Guest 被 ChatOnly 降级拦截；
///   ④ 普通成员触发非只读 MCP 工具开出真工具待批单（参数冻结）：
///      无权群友 & 跨群（group:10002）GroupManager 均不可越权批准；同群 GroupManager 审批仅执行 1 次，重放无效；
///   ⑤ // 命令收敛至 AgentTaskRuntime，/zba/v1/tasks 可见且 resultAvailable == false（ADR-0008）。
/// </summary>
public static partial class Program
{
    private static async Task RunPhase4RuntimeApprovalMcpScenarioAsync()
    {
        Section("S163 Phase 4 运行时收敛 · 控制台 MCP / 跨平台 RBAC / 真工具冻结审批闭环");

        var openAiPort = FreePort(17961);
        var agentAiPort = FreePort(17962);
        var botWsPort = FreePort(13173);
        var panelPort = FreePort(18223);
        var mcpPort = FreePort(17963);

        const long whitelistGroup1 = 10001;
        const long whitelistGroup2 = 10002;
        const long memberId = 20001;
        const long group1ManagerId = 30001;
        const long guestId = 40001;
        const long ownerId = 90001;
        const string adminToken = "s163-zba-admin-token";
        const string readToken = "s163-zba-read-token";
        const string panelToken = SyntheticPanelToken;
        const string mcpToken = "sk-s163-synthetic-mcp-token";

        var dataDir = NewDataDir("s163");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        openAi.AddRule("s163-ro-ping",
            """{"suitability":95,"reply":"","call":{"tool":"mcp.ops_loop.echo_ro","args":{"q":"ping1"}}}""",
            """{"suitability":95,"reply":"只读回声：ECHO_RO_OK:ping1"}""");
        openAi.AddRule("s163-guest-try",
            """{"suitability":95,"reply":"","call":{"tool":"mcp.ops_loop.echo_ro","args":{"q":"guest-try"}}}""",
            """{"suitability":95,"reply":"访客仅聊天回复。"}""");
        openAi.AddRule("s163-ticket-req",
            """{"suitability":95,"reply":"","call":{"tool":"mcp.ops_loop.create_ticket","args":{"title":"s163-frozen"}}}""");

        using var agentAi = new MockOpenAi(agentAiPort);
        agentAi.Start();
        agentAi.AddRule("汇总当前状态", """{"final":"当前状态汇总完成。"}""");

        using var mcp = new Phase4LoopbackMcpServer(mcpPort, mcpToken);
        mcp.Start();

        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = $"{whitelistGroup1},{whitelistGroup2}",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_ENABLE_POKE"] = "0",
            ["QQCHAT_ENABLE_VOICE"] = "0",
            ["QQCHAT_ENABLE_MUSIC"] = "0",
            ["QQCHAT_MAX_AGENT_STEPS"] = "2",
            ["QQCHAT_ENABLE_APPROVALS"] = "1",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_PANEL_TOKEN"] = panelToken,
            ["BOTAGENT_ZBA_TOKEN"] = adminToken,
            ["BOTAGENT_ZBA_READ_TOKEN"] = readToken,
            ["BOTAGENT_MCP_ALLOW_PRIVATE"] = "1",
            ["QQCHAT_AGENT"] = "1",
            ["QQCHAT_AGENT_USERS"] = ownerId.ToString(),
            ["QQCHAT_AGENT_TOKEN"] = "tok-s163",
            ["QQCHAT_AGENT_SERVER"] = "1",
            ["QQCHAT_AGENT_TARGET"] = "server",
            ["QQCHAT_AGENT_SERVER_URL"] = agentAi.BaseUrl,
            ["QQCHAT_AGENT_SERVER_MODEL"] = "mock-agent",
            ["QQCHAT_AGENT_SERVER_TOOLS"] = "",
            ["QQCHAT_AGENT_PROGRESS"] = "0",
            ["QQCHAT_AGENT_MASK"] = "1",
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var zbaRoot = $"http://127.0.0.1:{panelPort}/zba/v1";
        var panelBase = $"http://127.0.0.1:{panelPort}";

        // Ensure approvals & 2-step inline tool execution are active in runtime settings
        var (settingsStatus, _) = await PostJsonAsync(
            $"{panelBase}/api/settings?token={panelToken}",
            """{"enableApprovals":true,"maxAgentSteps":2}""");
        Check("s163 面板可开启人工审批与多步内联工具", settingsStatus == 200, $"status={settingsStatus}");

        // 1. Reject invalid file:// scheme
        var badSave = await ZbaAsync(http, "POST", zbaRoot + "/mcp/servers/save", adminToken,
            """{"expectedRevision":0,"idempotencyKey":"s163-bad-url","id":"bad-file","enabled":true,"endpoint":"file:///etc/passwd","allowedTools":["x"]}""");
        Check("s163 /zba/v1/mcp/servers/save 拒绝非法 file:// 端点（400 ssrf_endpoint_blocked）",
            badSave.Status == 400 && badSave.Body.Contains("ssrf_endpoint_blocked", StringComparison.Ordinal),
            $"status={badSave.Status} body={badSave.Body}");

        // 2. Probe loopback MCP server
        var probeRes = await ZbaAsync(http, "POST", zbaRoot + "/mcp/servers/test", adminToken,
            new JsonObject
            {
                ["endpoint"] = mcp.Endpoint,
                ["authToken"] = mcpToken,
                ["callTimeoutSeconds"] = 10
            }.ToJsonString());
        Check("s163 /zba/v1/mcp/servers/test 探测成功并发现 echo_ro (readOnlyHint=true) 与 create_ticket",
            probeRes.Status == 200
            && probeRes.Body.Contains("\"ok\":true", StringComparison.Ordinal)
            && probeRes.Body.Contains("echo_ro", StringComparison.Ordinal)
            && probeRes.Body.Contains("create_ticket", StringComparison.Ordinal),
            $"status={probeRes.Status} body={probeRes.Body}");

        // 3. Save loopback MCP server with write-only authToken & inspect GET non-echo
        var saveMcpRes = await ZbaAsync(http, "POST", zbaRoot + "/mcp/servers/save", adminToken,
            new JsonObject
            {
                ["expectedRevision"] = 0,
                ["idempotencyKey"] = "s163-save-ops",
                ["id"] = "ops_loop",
                ["enabled"] = true,
                ["endpoint"] = mcp.Endpoint,
                ["allowedTools"] = new JsonArray { "echo_ro", "create_ticket" },
                ["readOnlyTools"] = new JsonArray { "echo_ro" },
                ["callTimeoutSeconds"] = 10,
                ["maxResultBytes"] = 16384,
                ["authToken"] = mcpToken
            }.ToJsonString());
        var listMcpRes = await ZbaAsync(http, "GET", zbaRoot + "/mcp/servers", readToken);
        Check("s163 保存控制台 MCP 并热重载生效（hasAuthToken=true、绝不回显明文、区分只读确认工具）",
            saveMcpRes.Status == 200
            && listMcpRes.Status == 200
            && !saveMcpRes.Body.Contains(mcpToken, StringComparison.Ordinal)
            && !listMcpRes.Body.Contains(mcpToken, StringComparison.Ordinal)
            && listMcpRes.Body.Contains("\"hasAuthToken\":true", StringComparison.Ordinal)
            && listMcpRes.Body.Contains("mcp.ops_loop.echo_ro", StringComparison.Ordinal)
            && listMcpRes.Body.Contains("mcp.ops_loop.create_ticket", StringComparison.Ordinal),
            $"save={saveMcpRes.Status} list={listMcpRes.Body}");

        // 4. Configure RBAC: group1ManagerId = GroupManager (3) on group:10001 only; guestId = Guest (0)
        var saveRolesRes = await ZbaAsync(http, "POST", zbaRoot + "/permissions/roles/save", adminToken,
            new JsonObject
            {
                ["expectedRevision"] = 0,
                ["idempotencyKey"] = "s163-roles-1",
                ["bindings"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["platformId"] = "qq.private",
                        ["accountScope"] = "legacy",
                        ["instanceId"] = "",
                        ["scopeConversationKey"] = $"group:{whitelistGroup1}",
                        ["nativeUserId"] = group1ManagerId.ToString(),
                        ["role"] = 3,
                        ["note"] = "group1-manager"
                    },
                    new JsonObject
                    {
                        ["platformId"] = "qq.private",
                        ["accountScope"] = "legacy",
                        ["instanceId"] = "",
                        ["scopeConversationKey"] = null,
                        ["nativeUserId"] = guestId.ToString(),
                        ["role"] = 0,
                        ["note"] = "global-guest"
                    }
                }
            }.ToJsonString());
        Check("s163 /zba/v1/permissions/roles/save 成功写入 GroupManager (3) 与 Guest (0)",
            saveRolesRes.Status == 200 && saveRolesRes.Body.Contains("\"ok\":true", StringComparison.Ordinal),
            $"status={saveRolesRes.Status} body={saveRolesRes.Body}");

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await Task.Delay(500);

        List<string> SentToGroup(long gid) => protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg"
                && a["params"]?["group_id"]?.GetValue<long>() == gid)
            .Select(MessageText)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        // Keep all chat senders as OneBot "member" so only RBAC grants GroupManager privilege
        protocol.SetRole(whitelistGroup1, memberId, "member");
        protocol.SetRole(whitelistGroup1, group1ManagerId, "member");
        protocol.SetRole(whitelistGroup2, group1ManagerId, "member");
        protocol.SetRole(whitelistGroup1, guestId, "member");
        protocol.SetRole(whitelistGroup1, ownerId, "member");

        long msgId = 63000;

        // 5. Normal member triggers inline ReadOnlyConfirmed MCP tool echo_ro
        var g1Before = SentToGroup(whitelistGroup1).Count;
        await protocol.SendGroupMessageAsync(whitelistGroup1, memberId, "成员甲", "s163-ro-ping", ++msgId,
            mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(
            () => SentToGroup(whitelistGroup1).Skip(g1Before).Any(t => t.Contains("ECHO_RO_OK:ping1", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(45));
        Check("s163 普通成员触发 ReadOnlyConfirmed MCP 工具 (echo_ro) 免审批内联执行并返回回声",
            mcp.EchoRoCalls == 1 && !SentToGroup(whitelistGroup1).Skip(g1Before).Any(t => t.Contains("需要确认")),
            $"echoRoCalls={mcp.EchoRoCalls} sent={string.Join(" | ", SentToGroup(whitelistGroup1).Skip(g1Before))}");

        // 6. Guest in group1 attempts echo_ro -> ChatOnly downgrade blocks MCP call
        var reqsBeforeGuest = openAi.Requests.Count;
        var echoBeforeGuest = mcp.EchoRoCalls;
        await protocol.SendGroupMessageAsync(whitelistGroup1, guestId, "访客乙", "s163-guest-try", ++msgId,
            mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(() => openAi.Requests.Count > reqsBeforeGuest, TimeSpan.FromSeconds(30));
        await Task.Delay(1200);
        Check("s163 Guest 发言降级为 ChatOnly，绝不触发 echo_ro 外部 MCP 调用",
            mcp.EchoRoCalls == echoBeforeGuest,
            $"before={echoBeforeGuest} after={mcp.EchoRoCalls}");

        // 7. Member triggers mutating create_ticket -> opens real tool approval with frozen args
        g1Before = SentToGroup(whitelistGroup1).Count;
        await protocol.SendGroupMessageAsync(whitelistGroup1, memberId, "成员甲", "s163-ticket-req", ++msgId,
            mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(
            () => SentToGroup(whitelistGroup1).Skip(g1Before).Any(t => t.Contains("需要确认")),
            TimeSpan.FromSeconds(45));
        var announcement = SentToGroup(whitelistGroup1).Skip(g1Before).First(t => t.Contains("需要确认"));
        var idMatch = Regex.Match(announcement, @"编号\s*([23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{6})");
        var reqId = idMatch.Success ? idMatch.Groups[1].Value : string.Empty;
        Check("s163 非只读 MCP 工具 create_ticket 开出待批单而非直接执行",
            reqId.Length == 6 && mcp.TicketPayloads.Count == 0,
            $"reqId={reqId} payloads={mcp.TicketPayloads.Count} announcement={announcement}");

        // 7a. Non-approver member in group1 cannot approve
        await protocol.SendGroupMessageAsync(whitelistGroup1, memberId, "成员甲", $"同意 {reqId}", ++msgId,
            mentionBot: false, ct: cts.Token);
        await Task.Delay(1500);
        Check("s163 无权普通成员发「同意」不会执行 create_ticket",
            mcp.TicketPayloads.Count == 0, $"payloads={mcp.TicketPayloads.Count}");

        // 7b. GroupManager of group1 cannot approve in wrong group2
        await protocol.SendGroupMessageAsync(whitelistGroup2, group1ManagerId, "跨群管理员", $"同意 {reqId}", ++msgId,
            mentionBot: false, ct: cts.Token);
        await Task.Delay(1500);
        Check("s163 group:10001 的 GroupManager 在 group:10002 越权审批被拒绝",
            mcp.TicketPayloads.Count == 0, $"payloads={mcp.TicketPayloads.Count}");

        // 7c. GroupManager of group1 approves in group1 -> executes once with frozen args; replay does not re-execute
        g1Before = SentToGroup(whitelistGroup1).Count;
        await protocol.SendGroupMessageAsync(whitelistGroup1, group1ManagerId, "一众管理员", $"同意 {reqId}", ++msgId,
            mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => mcp.TicketPayloads.Count == 1, TimeSpan.FromSeconds(40));
        await Task.Delay(800);

        await protocol.SendGroupMessageAsync(whitelistGroup1, group1ManagerId, "一众管理员", $"同意 {reqId}", ++msgId,
            mentionBot: false, ct: cts.Token);
        await Task.Delay(1200);
        Check("s163 同群 GroupManager 批准后严格以冻结入参执行 1 次 create_ticket，重放不重复执行",
            mcp.TicketPayloads.Count == 1
            && mcp.TicketPayloads[0].Contains("s163-frozen", StringComparison.Ordinal),
            $"count={mcp.TicketPayloads.Count} payloads={string.Join(",", mcp.TicketPayloads)}");

        // 8. Owner triggers `//` command -> AgentTaskRuntime tracks task; /zba/v1/tasks/{id}/result hides chat-originated output (available == false)
        var agentBefore = agentAi.Requests.Count;
        await protocol.SendGroupMessageAsync(whitelistGroup1, ownerId, "号主", "// 汇总当前状态", ++msgId,
            mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => agentAi.Requests.Count > agentBefore, TimeSpan.FromSeconds(40));
        var taskDeadline = DateTime.UtcNow.AddSeconds(25);
        var taskId = string.Empty;
        while (DateTime.UtcNow < taskDeadline)
        {
            var (st, bd) = await ZbaAsync(http, "GET", zbaRoot + "/tasks", readToken);
            if (st == 200 && JsonNode.Parse(bd)?["items"] is JsonArray arr && arr.Count > 0)
            {
                var id = arr[0]?["id"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    taskId = id!;
                    await WaitForTaskStateAsync(http, zbaRoot, readToken, taskId, 2, 10);
                    break;
                }
            }
            await Task.Delay(200);
        }

        var (tasksStatus, tasksBody) = await ZbaAsync(http, "GET", zbaRoot + "/tasks", readToken);
        var (resStatus, resBody) = taskId.Length > 0
            ? await ZbaAsync(http, "GET", $"{zbaRoot}/tasks/{taskId}/result", adminToken)
            : (0, string.Empty);
        var resJson = resBody.Length > 0 ? JsonNode.Parse(resBody) as JsonObject : null;
        var availableFalse = resJson is not null
            && resJson["available"]?.GetValue<bool>() == false
            && resJson["summary"] is null;
        Check("s163 // 命令收敛至 AgentTaskRuntime，/zba/v1/tasks/{id}/result 对聊天任务隐藏正文 (available == false)",
            tasksStatus == 200 && resStatus == 200 && availableFalse,
            $"tasksStatus={tasksStatus} tasksBody={tasksBody} resStatus={resStatus} result={resBody}");
    }

    private sealed class Phase4LoopbackMcpServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _expectedToken;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _gate = new();
        private readonly List<string> _ticketPayloads = new();
        private Task? _loop;
        private int _echoRoCalls;

        public string Endpoint { get; }
        public int EchoRoCalls => Volatile.Read(ref _echoRoCalls);
        public IReadOnlyList<string> TicketPayloads { get { lock (_gate) return _ticketPayloads.ToArray(); } }

        public Phase4LoopbackMcpServer(int port, string expectedToken)
        {
            _expectedToken = expectedToken;
            Endpoint = $"http://127.0.0.1:{port}/mcp";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
        }

        public void Start()
        {
            _listener.Start();
            _loop = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch { break; }
                _ = Task.Run(() => HandleRequestAsync(ctx));
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            try
            {
                var auth = ctx.Request.Headers["Authorization"];
                if (!string.Equals(auth, "Bearer " + _expectedToken, StringComparison.Ordinal))
                {
                    await WriteJsonAsync(ctx, 401, new JsonObject { ["error"] = "unauthorized" }).ConfigureAwait(false);
                    return;
                }

                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var raw = await reader.ReadToEndAsync().ConfigureAwait(false);
                var req = JsonNode.Parse(raw) as JsonObject;
                var idNode = req?["id"]?.DeepClone();
                var method = req?["method"]?.GetValue<string>() ?? string.Empty;

                if (method == "notifications/initialized")
                {
                    ctx.Response.StatusCode = 202;
                    ctx.Response.Close();
                    return;
                }

                JsonObject result = method switch
                {
                    "initialize" => new JsonObject
                    {
                        ["protocolVersion"] = "2025-11-25",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "s163-loopback", ["version"] = "1.0.0" }
                    },
                    "tools/list" => new JsonObject
                    {
                        ["tools"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "echo_ro",
                                ["description"] = "Read-only echo tool",
                                ["inputSchema"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject { ["q"] = new JsonObject { ["type"] = "string" } }
                                },
                                ["annotations"] = new JsonObject { ["readOnlyHint"] = true }
                            },
                            new JsonObject
                            {
                                ["name"] = "create_ticket",
                                ["description"] = "Create synthetic ticket (mutating)",
                                ["inputSchema"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject { ["title"] = new JsonObject { ["type"] = "string" } }
                                },
                                ["annotations"] = new JsonObject { ["readOnlyHint"] = false }
                            }
                        }
                    },
                    "tools/call" => HandleToolCall(req?["params"] as JsonObject),
                    _ => new JsonObject()
                };

                await WriteJsonAsync(ctx, 200, new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = idNode,
                    ["result"] = result
                }).ConfigureAwait(false);
            }
            catch
            {
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }

        private JsonObject HandleToolCall(JsonObject? prms)
        {
            var name = prms?["name"]?.GetValue<string>() ?? string.Empty;
            var args = prms?["arguments"] as JsonObject;
            if (name == "echo_ro")
            {
                Interlocked.Increment(ref _echoRoCalls);
                var q = args?["q"]?.GetValue<string>() ?? string.Empty;
                return TextResult($"ECHO_RO_OK:{q}");
            }
            if (name == "create_ticket")
            {
                var serialized = args?.ToJsonString() ?? "{}";
                lock (_gate) _ticketPayloads.Add(serialized);
                var title = args?["title"]?.GetValue<string>() ?? string.Empty;
                return TextResult($"TICKET_CREATED:{title}");
            }
            return new JsonObject
            {
                ["isError"] = true,
                ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "unknown_tool" } }
            };
        }

        private static JsonObject TextResult(string text) => new()
        {
            ["isError"] = false,
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } }
        };

        private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, JsonObject body)
        {
            var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.Close();
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _cts.Dispose();
        }
    }
}
