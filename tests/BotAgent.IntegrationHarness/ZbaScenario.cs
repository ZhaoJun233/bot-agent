using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S54 ZBA 宿主真链路：Bearer 认证/授权、状态与能力目录、配置读取、陈旧版本冲突、
/// 任务/审批/事件空态与未知会话拒绝。全部为合成设置，不发送聊天消息。
/// </summary>
public static partial class Program
{
    private static async Task RunZbaHostScenarioAsync()
    {
        Section("S54 ZBA 宿主真链路（Bearer / 能力目录 / 配置 / 任务 / 事件）");

        const int panelPort = 18124;
        const int botWsPort = 13074;
        const int agentAiPort = 17864;
        const string adminToken = "synthetic-zba-admin-token";
        const string readToken = "synthetic-zba-read-token";
        var dataDir = NewDataDir("s54");
        var legacyHostSessionId = SeedLegacyHostSessions(dataDir);

        using var agentAi = new MockOpenAi(agentAiPort);
        agentAi.Start();
        agentAi.AddRule("synthetic zba task", """{"final":"合成任务完成。"}""");
        agentAi.AddRule("synthetic zba panel result", """{"final":"合成任务完成。"}""");
        agentAi.AddRule("synthetic zba capacity", """{"final":"容量任务完成。"}""");
        agentAi.AddRule("synthetic zba long result",
            new JsonObject { ["final"] = new string('A', 5000) }.ToJsonString());
        agentAi.AddRule("synthetic zba cancel", """{"final":"不应到达。"}""");
        agentAi.AddRule("synthetic legacy panel test", """{"final":"合成面板完成。"}""");

        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock-zba",
            ["QQCHAT_BASE_URL"] = "http://127.0.0.1:1/v1",
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = "99999",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_AGENT"] = "1",
            ["QQCHAT_AGENT_SERVER"] = "1",
            ["QQCHAT_AGENT_SERVER_URL"] = agentAi.BaseUrl,
            ["QQCHAT_AGENT_SERVER_MODEL"] = "mock-agent",
            ["QQCHAT_AGENT_SERVER_TOOLS"] = "",
            ["QQCHAT_AGENT_TOKEN"] = "synthetic-zba-agent-token",
            ["QQCHAT_PANEL_TOKEN"] = SyntheticPanelToken,
            ["BOTAGENT_ZBA_TOKEN"] = adminToken,
            ["BOTAGENT_ZBA_READ_TOKEN"] = readToken,
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var root = $"http://127.0.0.1:{panelPort}/zba/v1";

        var page = await http.GetAsync($"http://127.0.0.1:{panelPort}/");
        var pageBody = await page.Content.ReadAsStringAsync();
        Check("★ 静态资源从发布目录提供（不再嵌入程序集）",
            page.IsSuccessStatusCode && pageBody.Contains("<html", StringComparison.OrdinalIgnoreCase),
            ((int)page.StatusCode).ToString());
        var asset = await http.GetAsync($"http://127.0.0.1:{panelPort}/app.js");
        Check("静态脚本仍可读", asset.IsSuccessStatusCode, ((int)asset.StatusCode).ToString());

        var protectedApi = await http.GetAsync($"http://127.0.0.1:{panelPort}/api/settings");
        Check("★ 旧面板无密码但配令牌时不再免密（Issue #78）",
            protectedApi.StatusCode == HttpStatusCode.Unauthorized, ((int)protectedApi.StatusCode).ToString());
        var authorizedApi = await PanelGetAsync($"http://127.0.0.1:{panelPort}/api/settings");
        Check("旧面板带令牌仍可读", authorizedApi.Status == 200, authorizedApi.Status.ToString());

        var health = await ZbaAsync(http, "GET", root + "/health", null);
        Check("★ ZBA health 无令牌可读且 configured=true",
            health.Status == 200 && health.Body.Contains("\"configured\":true", StringComparison.Ordinal)
            && health.Body.Contains("\"contractVersion\":\"zba-1.0\"", StringComparison.Ordinal),
            health.Status + " " + health.Body);

        var noAuth = await ZbaAsync(http, "GET", root + "/status", null);
        Check("ZBA status 无令牌 401", noAuth.Status == 401, noAuth.Status.ToString());

        var status = await ZbaAsync(http, "GET", root + "/status", readToken);
        var statusNode = JsonNode.Parse(status.Body) as JsonObject;
        var componentIds = statusNode?["components"] is JsonArray components
            ? components.Select(item => item?["id"]?.GetValue<string>() ?? string.Empty).ToList()
            : new List<string>();
        Check("★ ZBA status 读令牌 200 且含真实能力目录",
            status.Status == 200
            && componentIds.Any(id => id.StartsWith("driver:", StringComparison.Ordinal))
            && componentIds.Any(id => id.StartsWith("plugin:", StringComparison.Ordinal))
            && componentIds.Any(id => id.StartsWith("provider:", StringComparison.Ordinal)),
            $"status={status.Status} components={string.Join("|", componentIds)}");
        Check("ZBA status 返回配置版本",
            statusNode?["configurationVersion"]?.GetValue<string>() is { Length: > 0 },
            statusNode?["configurationVersion"]?.ToJsonString() ?? "null");
        Check("ZBA status 不回显 bearer", !status.Body.Contains(adminToken, StringComparison.Ordinal)
            && !status.Body.Contains(readToken, StringComparison.Ordinal));

        var extraQuery = await ZbaAsync(http, "GET", root + "/status?foo=1", readToken);
        Check("ZBA status 未知 query 400", extraQuery.Status == 400, extraQuery.Status.ToString());

        var capabilities = await ZbaAsync(http, "GET", root + "/capabilities?limit=200", readToken);
        var capabilityItems = (JsonNode.Parse(capabilities.Body) as JsonObject)?["items"] as JsonArray;
        Check("★ ZBA capabilities 覆盖 driver/plugin/provider",
            capabilities.Status == 200 && capabilityItems is { Count: > 0 }
            && capabilityItems.Any(item => item?["key"]?["kind"]?.GetValue<string>() == "driver")
            && capabilityItems.Any(item => item?["key"]?["kind"]?.GetValue<string>() == "plugin")
            && capabilityItems.Any(item => item?["key"]?["kind"]?.GetValue<string>() == "provider"),
            capabilities.Status + " items=" + (capabilityItems?.Count ?? -1) + " body=" + capabilities.Body);

        var executeAsRead = await ZbaAsync(http, "POST", root + "/capabilities/execute", readToken,
            """{"contractRevision":"x","expectedDescriptorRevision":"x","key":"mg1:00000000000000000000000000000000:1:00000000000000000000000000000000","command":{}}""");
        Check("ZBA read 令牌不能执行管理动作 403", executeAsRead.Status == 403, executeAsRead.Status.ToString());

        var configRead = await ZbaAsync(http, "POST", root + "/configuration/read", readToken, """{"kind":0}""");
        Check("★ ZBA configuration/read 200 且不回显模型密钥",
            configRead.Status == 200 && !configRead.Body.Contains("sk-mock-zba", StringComparison.Ordinal),
            configRead.Status.ToString());

        var missingKind = await ZbaAsync(http, "POST", root + "/configuration/read", readToken, "{}");
        Check("ZBA configuration/read 缺 kind 400", missingKind.Status == 400, missingKind.Status.ToString());

        var configNode = JsonNode.Parse(configRead.Body) as JsonObject;
        var revision = configNode?["contractRevision"]?.GetValue<string>() ?? "cfg-0.3.3";
        var currentVersion = configNode?["version"]?.GetValue<string>()
                             ?? throw new InvalidOperationException("configuration version missing");
        var generation = configNode?["generation"]?.GetValue<string>()
                         ?? throw new InvalidOperationException("configuration generation missing");

        var validate = await ZbaAsync(http, "POST", root + "/capabilities/validate", adminToken,
            "{\"contractRevision\":\"" + revision + "\",\"expectedVersion\":\"" + currentVersion
            + "\",\"changes\":{},\"secrets\":[]}");
        Check("★ ZBA capabilities/validate 管理员可用",
            validate.Status == 200 && (JsonNode.Parse(validate.Body) as JsonObject)?["code"]?.GetValue<int>() == 0,
            validate.Status + " " + validate.Body);

        var pluginItem = capabilityItems?.FirstOrDefault(item =>
            item?["key"]?["kind"]?.GetValue<string>() == "plugin");
        var pluginId = pluginItem?["key"]?["key"]?.GetValue<string>();
        var descriptorRevision = pluginItem?["descriptorRevision"]?.GetValue<string>();
        var executeGeneration = Guid.NewGuid().ToString("N");
        var executeKey = $"mg1:{executeGeneration}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():N}";
        var pluginExecute = await ZbaAsync(http, "POST", root + "/capabilities/execute", adminToken,
            "{\"contractRevision\":\"" + revision + "\",\"expectedDescriptorRevision\":\"" + descriptorRevision
            + "\",\"key\":\"" + executeKey + "\",\"command\":{\"kind\":\"plugin\",\"key\":\""
            + pluginId + "\",\"action\":0}}");
        Check("★ ZBA 插件启停明确 UnsupportedAction（422）",
            pluginId is { Length: > 0 } && pluginExecute.Status == 422
            && (JsonNode.Parse(pluginExecute.Body) as JsonObject)?["code"]?.GetValue<int>() == 4,
            pluginExecute.Status + " " + pluginExecute.Body);

        var key = $"cfg1:{generation}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():N}";
        var staleSave = await ZbaAsync(http, "POST", root + "/configuration/save", adminToken,
            "{\"contractRevision\":\"" + revision + "\",\"expectedVersion\":\"999999\",\"key\":\"" + key
            + "\",\"changes\":{},\"secrets\":[]}");
        var staleNode = JsonNode.Parse(staleSave.Body) as JsonObject;
        Check("★ ZBA configuration/save 陈旧版本 409/VersionConflict",
            staleSave.Status == 409 && staleNode?["code"]?.GetValue<int>() == 3,
            staleSave.Status + " code=" + (staleNode?["code"]?.ToJsonString() ?? "null"));

        var nullSecretKey = $"cfg1:{generation}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():N}";
        var nullSecretSave = await ZbaAsync(http, "POST", root + "/configuration/save", adminToken,
            "{\"contractRevision\":\"" + revision + "\",\"expectedVersion\":\"" + currentVersion
            + "\",\"key\":\"" + nullSecretKey + "\",\"changes\":{},\"secrets\":[null]}");
        var nullSecretNode = JsonNode.Parse(nullSecretSave.Body) as JsonObject;
        Check("★ ZBA secrets 空元素 save 400 且不进入提交",
            nullSecretSave.Status == 400
            && nullSecretNode?["error"]?["code"]?.GetValue<string>() == "invalid_json",
            nullSecretSave.Status + " " + nullSecretSave.Body);
        var afterNullRead = await ZbaAsync(http, "POST", root + "/configuration/read", readToken, """{"kind":0}""");
        var afterNullVersion = (JsonNode.Parse(afterNullRead.Body) as JsonObject)?["version"]?.GetValue<string>();
        Check("ZBA secrets 空元素不改变配置版本",
            afterNullRead.Status == 200 && afterNullVersion == currentVersion,
            afterNullRead.Status + " version=" + (afterNullVersion ?? "null"));
        var nullSecretValidate = await ZbaAsync(http, "POST", root + "/capabilities/validate", adminToken,
            "{\"contractRevision\":\"" + revision + "\",\"expectedVersion\":\"" + currentVersion
            + "\",\"changes\":{},\"secrets\":[null]}");
        var nullSecretValidateNode = JsonNode.Parse(nullSecretValidate.Body) as JsonObject;
        Check("★ ZBA capabilities/validate secrets 空元素 400",
            nullSecretValidate.Status == 400
            && nullSecretValidateNode?["error"]?["code"]?.GetValue<string>() == "invalid_json",
            nullSecretValidate.Status + " " + nullSecretValidate.Body);

        var applyKey = $"cfg1:{generation}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():N}";
        var applyBody = new JsonObject
        {
            ["contractRevision"] = revision,
            ["expectedVersion"] = currentVersion,
            ["key"] = applyKey,
            ["changes"] = new JsonObject
            {
                ["behavior"] = new JsonObject
                {
                    ["groupCooldownSeconds"] = new JsonObject { ["kind"] = 1, ["value"] = 1 }
                }
            },
            ["secrets"] = new JsonArray()
        }.ToJsonString();
        var applied = await ZbaAsync(http, "POST", root + "/configuration/save", adminToken, applyBody);
        var appliedNode = JsonNode.Parse(applied.Body) as JsonObject;
        var appliedReceipt = appliedNode?["receipt"] as JsonObject;
        Check("★ ZBA configuration/save 真正提交并返回回执",
            applied.Status == 200 && appliedNode?["code"]?.GetValue<int>() == 0
            && appliedReceipt?["decision"]?.GetValue<int>() == 0
            && appliedReceipt?["commitState"]?.GetValue<int>() == 1,
            applied.Status + " " + applied.Body);
        Check("ZBA save 把已应用/需重启分开报告",
            appliedReceipt?["applyState"]?.GetValue<int>() is >= 1 and <= 3,
            appliedReceipt?["applyState"]?.ToJsonString() ?? "null");

        var editable = await ZbaAsync(http, "POST", root + "/configuration/read", readToken, """{"kind":1}""");
        Check("★ ZBA 保存后读取到新值",
            editable.Status == 200
            && (JsonNode.Parse(editable.Body) as JsonObject)?["view"]?["behavior"]?["groupCooldownSeconds"]?["value"]?.GetValue<int>() == 1,
            editable.Status.ToString());

        var nextVersion = appliedNode?["currentVersion"]?.GetValue<string>()
                          ?? throw new InvalidOperationException("committed version missing");
        var restoreKey = $"cfg1:{generation}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():N}";
        var restoreBody = new JsonObject
        {
            ["contractRevision"] = revision,
            ["expectedVersion"] = nextVersion,
            ["key"] = restoreKey,
            ["changes"] = new JsonObject
            {
                ["behavior"] = new JsonObject
                {
                    ["groupCooldownSeconds"] = new JsonObject { ["kind"] = 1, ["value"] = 0 }
                }
            },
            ["secrets"] = new JsonArray()
        }.ToJsonString();
        var restored = await ZbaAsync(http, "POST", root + "/configuration/save", adminToken, restoreBody);
        Check("ZBA 第二次保存使用新版本成功",
            restored.Status == 200 && (JsonNode.Parse(restored.Body) as JsonObject)?["code"]?.GetValue<int>() == 0,
            restored.Status + " " + restored.Body);

        var editableNode = JsonNode.Parse(editable.Body) as JsonObject;
        var sandboxBefore = editableNode?["view"]?["platform"]?["officialSandbox"]?["value"]?.GetValue<bool>() ?? false;
        var restartVersion = (JsonNode.Parse(restored.Body) as JsonObject)?["currentVersion"]?.GetValue<string>()
                             ?? throw new InvalidOperationException("committed version missing");
        var restartKey = $"cfg1:{generation}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():N}";
        var restartBody = new JsonObject
        {
            ["contractRevision"] = revision,
            ["expectedVersion"] = restartVersion,
            ["key"] = restartKey,
            ["changes"] = new JsonObject
            {
                ["platform"] = new JsonObject
                {
                    ["officialSandbox"] = new JsonObject { ["kind"] = 1, ["value"] = !sandboxBefore }
                }
            },
            ["secrets"] = new JsonArray()
        }.ToJsonString();
        var restartSave = await ZbaAsync(http, "POST", root + "/configuration/save", adminToken, restartBody);
        var restartNode = JsonNode.Parse(restartSave.Body) as JsonObject;
        var restartReceipt = restartNode?["receipt"] as JsonObject;
        Check("★ ZBA 需重启字段真实提交且回执 RestartRequired",
            restartSave.Status == 200 && restartNode?["code"]?.GetValue<int>() == 0
            && restartReceipt?["commitState"]?.GetValue<int>() == 1
            && restartReceipt?["applyState"]?.GetValue<int>() == 3,
            restartSave.Status + " " + restartSave.Body);

        var restartEvents = await ZbaAsync(http, "GET", root + "/events?after=0&limit=200", readToken);
        var restartEventItems = (JsonNode.Parse(restartEvents.Body) as JsonObject)?["items"] as JsonArray;
        var latestConfigEvent = restartEventItems?.LastOrDefault(item =>
            item?["type"]?.GetValue<string>() == "configuration.saved");
        Check("★ ZBA 配置事件投影真实 ApplyState（需重启不冒报 applied）",
            latestConfigEvent?["state"]?.GetValue<string>() == "restart_required",
            latestConfigEvent?.ToJsonString() ?? "missing");

        var restoreSandboxVersion = restartNode?["currentVersion"]?.GetValue<string>()
                                    ?? throw new InvalidOperationException("committed version missing");
        var sandboxRestoreKey = $"cfg1:{generation}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():N}";
        var sandboxRestoreBody = new JsonObject
        {
            ["contractRevision"] = revision,
            ["expectedVersion"] = restoreSandboxVersion,
            ["key"] = sandboxRestoreKey,
            ["changes"] = new JsonObject
            {
                ["platform"] = new JsonObject
                {
                    ["officialSandbox"] = new JsonObject { ["kind"] = 1, ["value"] = sandboxBefore }
                }
            },
            ["secrets"] = new JsonArray()
        }.ToJsonString();
        var sandboxRestore = await ZbaAsync(http, "POST", root + "/configuration/save", adminToken, sandboxRestoreBody);
        Check("ZBA 重启字段恢复保存成功",
            sandboxRestore.Status == 200
            && (JsonNode.Parse(sandboxRestore.Body) as JsonObject)?["code"]?.GetValue<int>() == 0,
            sandboxRestore.Status + " " + sandboxRestore.Body);

        var tasks = await ZbaAsync(http, "GET", root + "/tasks", readToken);
        Check("★ ZBA tasks 空态可查",
            tasks.Status == 200 && (JsonNode.Parse(tasks.Body) as JsonObject)?["items"] is JsonArray { Count: 0 },
            tasks.Status.ToString());

        var approvals = await ZbaAsync(http, "GET", root + "/approvals", readToken);
        Check("ZBA approvals 空态可查",
            approvals.Status == 200 && JsonNode.Parse(approvals.Body) is JsonArray { Count: 0 },
            approvals.Status.ToString());

        var events = await ZbaAsync(http, "GET", root + "/events?after=0", readToken);
        Check("★ ZBA events 可查且配置保存已产生事件",
            events.Status == 200
            && (JsonNode.Parse(events.Body) as JsonObject)?["items"] is JsonArray savedEvents
            && savedEvents.Any(item => item?["type"]?.GetValue<string>() == "configuration.saved"),
            events.Status.ToString());

        var eventPage = JsonNode.Parse(events.Body) as JsonObject;
        var lastSequence = eventPage?["nextSequence"]?.GetValue<long>() ?? -1;
        var atTip = await ZbaAsync(http, "GET", root + "/events?after=" + lastSequence, readToken);
        Check("ZBA 当前事件游标无需 resync",
            atTip.Status == 200 && lastSequence >= 0
            && (JsonNode.Parse(atTip.Body) as JsonObject)?["resyncRequired"]?.GetValue<bool>() == false,
            atTip.Status + " after=" + lastSequence + " " + atTip.Body);
        var forwardCursor = await ZbaAsync(http, "GET", root + "/events?after=" + (lastSequence + 5), readToken);
        Check("★ ZBA 超前/重启前游标要求 resync，不静默漏事件",
            forwardCursor.Status == 200
            && (JsonNode.Parse(forwardCursor.Body) as JsonObject)?["resyncRequired"]?.GetValue<bool>() == true,
            forwardCursor.Status + " " + forwardCursor.Body);

        var created = await PanelPostJsonAsync($"http://127.0.0.1:{panelPort}/api/agent/sessions",
            """{"action":"new","key":"group:99999","backend":"server","name":"synthetic-zba"}""");
        var sessionId = ((JsonNode.Parse(created.Body) as JsonObject)?["sessions"] as JsonArray)?
            .FirstOrDefault()?["id"]?.GetValue<string>();
        Check("★ ZBA 前置换好合成 agent 会话", created.Code == 200 && sessionId is { Length: > 0 },
            created.Code + " " + created.Body);

        var storedSessions = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dataDir, "data", "agent-sessions.json")));
        var storedList = storedSessions?["chats"]?["group:99999"]?["sessions"] as JsonArray;
        var storedHostCount = storedList?.Count(item => item?["backend"]?.GetValue<string>() == "host") ?? -1;
        var storedServerCount = storedList?.Count(item => item?["backend"]?.GetValue<string>() == "server") ?? -1;
        Check("★ 达到旧会话上限时新建 server 不裁剪只读 host 历史",
            legacyHostSessionId.Length > 0 && storedHostCount == 12 && storedServerCount == 1,
            $"host={storedHostCount} server={storedServerCount}");

        var accepted = await ZbaAsync(http, "POST", root + "/tasks", adminToken,
            "{\"sourceKey\":\"group:99999\",\"sessionId\":\"" + sessionId
            + "\",\"prompt\":\"synthetic zba task\",\"target\":\"server\",\"timeoutSeconds\":60}",
            idempotencyKey: "synthetic-zba-task-1");
        var acceptedNode = JsonNode.Parse(accepted.Body) as JsonObject;
        var taskId = acceptedNode?["id"]?.GetValue<string>();
        Check("★ ZBA task submit 返回稳定 id", accepted.Status == 200 && taskId is { Length: > 0 },
            accepted.Status + " " + accepted.Body);

        var completed = await WaitForTaskStateAsync(http, root, readToken, taskId!, 3, 60);
        Check("★ ZBA task 完成且不泄露提示词",
            completed is not null
            && completed["state"]?.GetValue<int>() == 3
            && !completed.ToJsonString().Contains("synthetic zba task", StringComparison.Ordinal),
            completed?.ToJsonString() ?? "timeout");

        var taskList = await ZbaAsync(http, "GET", root + "/tasks?limit=10", readToken);
        Check("ZBA tasks 能查到刚完成的任务",
            taskList.Status == 200
            && (JsonNode.Parse(taskList.Body) as JsonObject)?["items"] is JsonArray list
            && list.Any(item => item?["id"]?.GetValue<string>() == taskId),
            taskList.Status.ToString());

        var replay = await ZbaAsync(http, "POST", root + "/tasks", adminToken,
            "{\"sourceKey\":\"group:99999\",\"sessionId\":\"" + sessionId
            + "\",\"prompt\":\"synthetic zba task\",\"target\":\"server\",\"timeoutSeconds\":60}",
            idempotencyKey: "synthetic-zba-task-1");
        Check("★ ZBA 幂等键重放返回同一任务",
            replay.Status == 200 && (JsonNode.Parse(replay.Body) as JsonObject)?["id"]?.GetValue<string>() == taskId,
            replay.Status + " " + replay.Body);

        var taskEvents = await ZbaAsync(http, "GET", root + "/events?after=0", readToken);
        Check("★ ZBA 事件流包含任务状态且可恢复",
            taskEvents.Status == 200
            && (JsonNode.Parse(taskEvents.Body) as JsonObject)?["items"] is JsonArray eventItems
            && eventItems.Any(item => item?["type"]?.GetValue<string>() == "task.updated"),
            taskEvents.Status.ToString());

        // ── G01/G02/G03：工作台会话、结果摘要、提交回执 ──
        var meta = await ZbaAsync(http, "GET", root + "/meta", readToken);
        var metaNode = JsonNode.Parse(meta.Body) as JsonObject;
        var metaFeatures = metaNode?["features"] as JsonArray;
        Check("★ ZBA meta 暴露 instanceId 与能力标记",
            meta.Status == 200 && metaNode?["instanceId"]?.GetValue<string>() is { Length: > 0 }
            && metaFeatures?.Any(item => item?.GetValue<string>() == "tasks.find") == true,
            meta.Status + " " + meta.Body);

        var panelList = await ZbaAsync(http, "GET", root + "/sessions?sourceKey=panel%3Aworkspace", readToken);
        Check("★ ZBA panel 会话空态可查",
            panelList.Status == 200
            && (JsonNode.Parse(panelList.Body) as JsonObject)?["items"] is JsonArray { Count: 0 },
            panelList.Status + " " + panelList.Body);

        var ensureRead = await ZbaAsync(http, "POST", root + "/sessions/ensure", readToken,
            """{"sourceKey":"panel:workspace"}""");
        Check("★ ZBA 只读令牌不能 ensure 会话", ensureRead.Status == 403, ensureRead.Status.ToString());

        var ensure = await ZbaAsync(http, "POST", root + "/sessions/ensure", adminToken,
            """{"sourceKey":"panel:workspace"}""");
        var ensureNode = JsonNode.Parse(ensure.Body) as JsonObject;
        var panelSessionId = ensureNode?["session"]?["id"]?.GetValue<string>();
        Check("★ ZBA ensure 从零创建工作台会话",
            ensure.Status == 200 && ensureNode?["created"]?.GetValue<bool>() == true
            && panelSessionId is { Length: > 0 },
            ensure.Status + " " + ensure.Body);

        var ensureAgain = await ZbaAsync(http, "POST", root + "/sessions/ensure", adminToken,
            """{"sourceKey":"panel:workspace"}""");
        var ensureAgainNode = JsonNode.Parse(ensureAgain.Body) as JsonObject;
        Check("★ ZBA ensure 幂等（第二次不新建）",
            ensureAgain.Status == 200 && ensureAgainNode?["created"]?.GetValue<bool>() == false
            && ensureAgainNode?["session"]?["id"]?.GetValue<string>() == panelSessionId,
            ensureAgain.Status + " " + ensureAgain.Body);

        var forbiddenSource = await ZbaAsync(http, "GET", root + "/sessions?sourceKey=group%3A99999", readToken);
        Check("★ ZBA 会话发现拒绝群聊来源（403）",
            forbiddenSource.Status == 403
            && (JsonNode.Parse(forbiddenSource.Body) as JsonObject)?["error"]?["code"]?.GetValue<string>()
                == "source_key_not_allowed",
            forbiddenSource.Status + " " + forbiddenSource.Body);

        var panelSubmit = await ZbaAsync(http, "POST", root + "/tasks", adminToken,
            "{\"sourceKey\":\"panel:workspace\",\"sessionId\":\"" + panelSessionId
            + "\",\"prompt\":\"synthetic zba panel result\",\"target\":\"server\",\"timeoutSeconds\":60}",
            idempotencyKey: "synthetic-zba-panel-task-1");
        var panelTaskId = (JsonNode.Parse(panelSubmit.Body) as JsonObject)?["id"]?.GetValue<string>();
        Check("★ ZBA panel 任务提交成功", panelSubmit.Status == 200 && panelTaskId is { Length: > 0 },
            panelSubmit.Status + " " + panelSubmit.Body);

        var panelCompleted = await WaitForTaskStateAsync(http, root, readToken, panelTaskId!, 3, 60);
        Check("★ ZBA panel 任务完成",
            panelCompleted is not null && panelCompleted["state"]?.GetValue<int>() == 3,
            panelCompleted?.ToJsonString() ?? "timeout");

        var resultRead = await ZbaAsync(http, "GET", root + "/tasks/" + panelTaskId + "/result", readToken);
        Check("★ ZBA 只读令牌不能读结果", resultRead.Status == 403, resultRead.Status.ToString());

        var result = await ZbaAsync(http, "GET", root + "/tasks/" + panelTaskId + "/result", adminToken);
        var resultNode = JsonNode.Parse(result.Body) as JsonObject;
        Check("★ ZBA 结果摘要只含单次任务最终答复",
            result.Status == 200 && resultNode?["available"]?.GetValue<bool>() == true
            && resultNode?["summary"]?.GetValue<string>() == "合成任务完成。"
            && !result.Body.Contains("synthetic zba panel result", StringComparison.Ordinal),
            result.Status + " " + result.Body);

        var groupResult = await ZbaAsync(http, "GET", root + "/tasks/" + taskId + "/result", adminToken);
        var groupResultNode = JsonNode.Parse(groupResult.Body) as JsonObject;
        Check("★ ZBA 非 panel 任务不返回结果正文",
            groupResult.Status == 200 && groupResultNode?["available"]?.GetValue<bool>() == false
            && groupResultNode?["reasonCode"]?.GetValue<string>() == "scope_not_allowed",
            groupResult.Status + " " + groupResult.Body);

        var longSubmit = await ZbaAsync(http, "POST", root + "/tasks", adminToken,
            "{\"sourceKey\":\"panel:workspace\",\"sessionId\":\"" + panelSessionId
            + "\",\"prompt\":\"synthetic zba long result\",\"target\":\"server\",\"timeoutSeconds\":60}",
            idempotencyKey: "synthetic-zba-long-result-1");
        var longTaskId = (JsonNode.Parse(longSubmit.Body) as JsonObject)?["id"]?.GetValue<string>();
        var longDone = await WaitForTaskStateAsync(http, root, readToken, longTaskId!, 3, 60);
        var longResult = await ZbaAsync(http, "GET", root + "/tasks/" + longTaskId + "/result", adminToken);
        var longNode = JsonNode.Parse(longResult.Body) as JsonObject;
        var longSummary = longNode?["summary"]?.GetValue<string>() ?? string.Empty;
        Check("★ ZBA 长摘要经 HTTP 截断到 4000 字符",
            longSubmit.Status == 200 && longDone is not null && longResult.Status == 200
            && longNode?["available"]?.GetValue<bool>() == true
            && longNode?["summaryTruncated"]?.GetValue<bool>() == true
            && longNode?["summaryChars"]?.GetValue<int>() == 5000
            && longSummary.Length == 4000,
            longResult.Status + " chars=" + longNode?["summaryChars"]?.GetValue<int>()
            + " truncated=" + longNode?["summaryTruncated"]?.GetValue<bool>());

        // 失败任务：mock 命中标记直接 400，让真宿主拿到确定性的 agent 失败。
        agentAi.FailChatMarker = "synthetic zba failure";
        try
        {
            var failSubmit = await ZbaAsync(http, "POST", root + "/tasks", adminToken,
                "{\"sourceKey\":\"panel:workspace\",\"sessionId\":\"" + panelSessionId
                + "\",\"prompt\":\"synthetic zba failure\",\"target\":\"server\",\"timeoutSeconds\":30}",
                idempotencyKey: "synthetic-zba-failure-1");
            var failTaskId = (JsonNode.Parse(failSubmit.Body) as JsonObject)?["id"]?.GetValue<string>();
            var failDone = await WaitForTaskStateAsync(http, root, readToken, failTaskId!, 4, 30);
            var failResult = await ZbaAsync(http, "GET", root + "/tasks/" + failTaskId + "/result", adminToken);
            var failNode = JsonNode.Parse(failResult.Body) as JsonObject;
            Check("★ ZBA 失败任务只回安全错误码",
                failSubmit.Status == 200 && failDone?["state"]?.GetValue<int>() == 4
                && failNode?["available"]?.GetValue<bool>() == false
                && failNode?["reasonCode"]?.GetValue<string>() == "not_succeeded"
                && failNode?["summary"] is null
                && failNode?["errorCode"]?.GetValue<string>() is "agent_failed" or "agent_rejected",
                failResult.Status + " " + failResult.Body);
        }
        finally
        {
            agentAi.FailChatMarker = null;
        }

        var find = await ZbaAsync(http, "POST", root + "/tasks/find", adminToken,
            """{"key":"synthetic-zba-panel-task-1"}""");
        var findNode = JsonNode.Parse(find.Body) as JsonObject;
        Check("★ ZBA 按幂等键核对到已受理任务",
            find.Status == 200 && findNode?["code"]?.GetValue<string>() == "found"
            && findNode?["task"]?["id"]?.GetValue<string>() == panelTaskId,
            find.Status + " " + find.Body);

        var findUnknown = await ZbaAsync(http, "POST", root + "/tasks/find", adminToken,
            """{"key":"synthetic-zba-never-used-1"}""");
        Check("★ ZBA 未提交的键明确未知",
            findUnknown.Status == 200
            && (JsonNode.Parse(findUnknown.Body) as JsonObject)?["code"]?.GetValue<string>() == "unknown",
            findUnknown.Status + " " + findUnknown.Body);

        var findAsRead = await ZbaAsync(http, "POST", root + "/tasks/find", readToken,
            """{"key":"synthetic-zba-panel-task-1"}""");
        Check("★ ZBA 只读令牌不能核对提交", findAsRead.Status == 403, findAsRead.Status.ToString());

        agentAi.ResponseDelayMs = 5000;
        var cancelAccepted = await ZbaAsync(http, "POST", root + "/tasks", adminToken,
            "{\"sourceKey\":\"group:99999\",\"sessionId\":\"" + sessionId
            + "\",\"prompt\":\"synthetic zba cancel\",\"target\":\"server\",\"timeoutSeconds\":60}",
            idempotencyKey: "synthetic-zba-task-cancel");
        var cancelId = (JsonNode.Parse(cancelAccepted.Body) as JsonObject)?["id"]?.GetValue<string>();
        await Task.Delay(500);
        var cancel = await ZbaAsync(http, "POST", root + "/tasks/" + cancelId + "/cancel", adminToken);
        Check("★ ZBA cancel 只报告 cancel_requested",
            cancel.Status == 200 && cancel.Body.Contains("\"outcome\":\"cancel_requested\"", StringComparison.Ordinal),
            cancel.Status + " " + cancel.Body);
        agentAi.ResponseDelayMs = 0;
        var cancelled = await WaitForTaskStateAsync(http, root, readToken, cancelId!, 6, 15);
        Check("★ ZBA cancel 后服务端任务进入 cancelled（不冒报成功）",
            cancelled is not null && cancelled["state"]?.GetValue<int>() == 6,
            cancelled?.ToJsonString() ?? "timeout");

        // ── G03：真宿主 200 条台账上限处的同实例淘汰（不能用合成后端硬编码当证据） ──
        // 先填满 200 条，再提交第 201 条：最旧终态记录被淘汰，但回执保留，
        // 因此 find 必须返回 expired、同键重放必须 409 record_evicted，且不重新执行。
        const int ledgerCapacity = 200;
        var capacityBody = "{\"sourceKey\":\"panel:workspace\",\"sessionId\":\"" + panelSessionId
            + "\",\"prompt\":\"synthetic zba capacity\",\"target\":\"server\",\"timeoutSeconds\":60}";
        var capacityKeys = new List<string>(ledgerCapacity);
        var capacityIds = new List<string>(ledgerCapacity);
        var capacityOk = true;
        var capacityDetail = string.Empty;
        for (var start = 0; start < ledgerCapacity && capacityOk; start += 4)
        {
            var wave = new List<string>(4);
            for (var offset = 0; offset < 4 && start + offset < ledgerCapacity; offset++)
            {
                var index = start + offset;
                var capacityKey = $"synthetic-zba-capacity-{index:000}";
                var response = await ZbaAsync(http, "POST", root + "/tasks", adminToken, capacityBody,
                    idempotencyKey: capacityKey);
                var id = (JsonNode.Parse(response.Body) as JsonObject)?["id"]?.GetValue<string>();
                if (response.Status != 200 || id is null)
                {
                    capacityOk = false;
                    capacityDetail = $"submit {index} -> {response.Status} {response.Body}";
                    break;
                }

                capacityKeys.Add(capacityKey);
                capacityIds.Add(id);
                wave.Add(id);
            }

            if (!capacityOk) break;
            if (!await WaitForTasksStateAsync(http, root, readToken, wave, 3, 30))
            {
                capacityOk = false;
                capacityDetail = $"wave {start} timeout";
            }
        }

        Check("★ ZBA 真宿主台账可填满 200 条终态记录", capacityOk && capacityIds.Count == ledgerCapacity,
            capacityOk ? $"count={capacityIds.Count}" : capacityDetail);

        if (capacityOk)
        {
            var fullList = await ZbaAsync(http, "GET", root + "/tasks?limit=200", readToken);
            var fullItems = (JsonNode.Parse(fullList.Body) as JsonObject)?["items"] as JsonArray;
            Check("★ ZBA 台账达到 200 条上限时完整可列",
                fullList.Status == 200 && fullItems?.Count == 200,
                fullList.Status + " items=" + (fullItems?.Count ?? -1));

            var oldestKey = capacityKeys[0];
            var oldestId = capacityIds[0];
            var beforeEvict = await ZbaAsync(http, "POST", root + "/tasks/find", adminToken,
                "{\"key\":\"" + oldestKey + "\"}");
            Check("★ ZBA 淘汰前最旧任务仍可核对为 found",
                (JsonNode.Parse(beforeEvict.Body) as JsonObject)?["code"]?.GetValue<string>() == "found",
                beforeEvict.Status + " " + beforeEvict.Body);

            var overflowKey = "synthetic-zba-capacity-overflow";
            var overflowSubmit = await ZbaAsync(http, "POST", root + "/tasks", adminToken, capacityBody,
                idempotencyKey: overflowKey);
            var overflowId = (JsonNode.Parse(overflowSubmit.Body) as JsonObject)?["id"]?.GetValue<string>();
            Check("★ ZBA 台账满 200 条后第 201 条仍可受理",
                overflowSubmit.Status == 200 && overflowId is { Length: > 0 },
                overflowSubmit.Status + " " + overflowSubmit.Body);
            var overflowDone = overflowId is null
                ? null
                : await WaitForTaskStateAsync(http, root, readToken, overflowId, 3, 30);
            Check("★ ZBA 第 201 条任务正常完成",
                overflowDone?["state"]?.GetValue<int>() == 3,
                overflowDone?.ToJsonString() ?? "timeout");

            var evictedFind = await ZbaAsync(http, "POST", root + "/tasks/find", adminToken,
                "{\"key\":\"" + oldestKey + "\"}");
            var evictedNode = JsonNode.Parse(evictedFind.Body) as JsonObject;
            Check("★ ZBA 同实例 200 条上限淘汰后 find 返回 expired",
                evictedFind.Status == 200
                && evictedNode?["code"]?.GetValue<string>() == "expired"
                && evictedNode?["taskId"]?.GetValue<string>() == oldestId
                && evictedNode?["task"] is null
                && evictedNode?["acceptedAt"] is not null,
                evictedFind.Status + " " + evictedFind.Body);

            var evictedGet = await ZbaAsync(http, "GET", root + "/tasks/" + oldestId, readToken);
            Check("★ ZBA 已淘汰任务记录 404",
                evictedGet.Status == 404
                && evictedGet.Body.Contains("task_not_found", StringComparison.Ordinal),
                evictedGet.Status + " " + evictedGet.Body);

            var beforeReplayCalls = agentAi.Requests.Count;
            var replayEvicted = await ZbaAsync(http, "POST", root + "/tasks", adminToken, capacityBody,
                idempotencyKey: oldestKey);
            var replayNode = JsonNode.Parse(replayEvicted.Body) as JsonObject;
            Check("★ ZBA 已淘汰键重放 409 record_evicted 且不重复执行",
                replayEvicted.Status == 409
                && replayNode?["error"]?["code"]?.GetValue<string>() == "idempotency_unverifiable"
                && replayNode?["error"]?["reason"]?.GetValue<string>() == "record_evicted"
                && agentAi.Requests.Count == beforeReplayCalls,
                replayEvicted.Status + " " + replayEvicted.Body
                + $" model={beforeReplayCalls}->{agentAi.Requests.Count}");

            var afterReplayFind = await ZbaAsync(http, "POST", root + "/tasks/find", adminToken,
                "{\"key\":\"" + oldestKey + "\"}");
            Check("★ ZBA 重放被拒后旧键仍是 expired，没有生成新任务",
                (JsonNode.Parse(afterReplayFind.Body) as JsonObject)?["code"]?.GetValue<string>() == "expired",
                afterReplayFind.Status + " " + afterReplayFind.Body);

            var newestFind = await ZbaAsync(http, "POST", root + "/tasks/find", adminToken,
                "{\"key\":\"" + capacityKeys[^1] + "\"}");
            var newestNode = JsonNode.Parse(newestFind.Body) as JsonObject;
            Check("★ ZBA 未淘汰的新任务仍可核对为 found",
                newestNode?["code"]?.GetValue<string>() == "found"
                && newestNode?["task"]?["id"]?.GetValue<string>() == capacityIds[^1],
                newestFind.Status + " " + newestFind.Body);

            var overflowFind = await ZbaAsync(http, "POST", root + "/tasks/find", adminToken,
                "{\"key\":\"" + overflowKey + "\"}");
            Check("★ ZBA 第 201 条任务可按幂等键核对",
                (JsonNode.Parse(overflowFind.Body) as JsonObject)?["code"]?.GetValue<string>() == "found",
                overflowFind.Status + " " + overflowFind.Body);

            var cappedList = await ZbaAsync(http, "GET", root + "/tasks?limit=200", readToken);
            var cappedItems = (JsonNode.Parse(cappedList.Body) as JsonObject)?["items"] as JsonArray;
            Check("★ ZBA 淘汰后台账仍保持 200 条上限",
                cappedList.Status == 200 && cappedItems?.Count == 200,
                cappedList.Status + " items=" + (cappedItems?.Count ?? -1));
        }

        // ── BR-01：旧的 /api/agent/test 也必须解析并拒绝显式退役目标，且在任何模型调用之前 ──
        var panelTestUrl = $"http://127.0.0.1:{panelPort}/api/agent/test";
        var beforeLegacyHost = agentAi.Requests.Count;
        var legacyHost = await PanelPostJsonAsync(panelTestUrl,
            """{"prompt":"synthetic legacy panel test","target":"host","timeoutSec":30}""");
        Check("★ 旧 /api/agent/test 显式 host 目标 400，且不调用模型",
            legacyHost.Code == 400 && legacyHost.Body.Contains("host_backend_removed", StringComparison.Ordinal)
            && agentAi.Requests.Count == beforeLegacyHost,
            $"{legacyHost.Code} {legacyHost.Body} model={beforeLegacyHost}->{agentAi.Requests.Count}");

        var beforeLegacyDevice = agentAi.Requests.Count;
        var legacyDevice = await PanelPostJsonAsync(panelTestUrl,
            """{"prompt":"synthetic legacy panel test","target":"synthetic-device","timeoutSec":30}""");
        Check("★ 旧 /api/agent/test 显式设备名 400，且不调用模型",
            legacyDevice.Code == 400 && legacyDevice.Body.Contains("host_backend_removed", StringComparison.Ordinal)
            && agentAi.Requests.Count == beforeLegacyDevice,
            $"{legacyDevice.Code} {legacyDevice.Body} model={beforeLegacyDevice}->{agentAi.Requests.Count}");

        var beforeLegacyHostSession = agentAi.Requests.Count;
        var legacyHostWithSession = await PanelPostJsonAsync(panelTestUrl,
            "{\"key\":\"group:99999\",\"prompt\":\"synthetic legacy panel test\",\"target\":\"host\",\"sessionId\":\""
            + sessionId + "\",\"timeoutSec\":30}");
        Check("★ 旧 /api/agent/test 带有效 server 会话的 host 目标仍在模型调用前 400",
            legacyHostWithSession.Code == 400
            && legacyHostWithSession.Body.Contains("host_backend_removed", StringComparison.Ordinal)
            && agentAi.Requests.Count == beforeLegacyHostSession,
            $"{legacyHostWithSession.Code} {legacyHostWithSession.Body} model={beforeLegacyHostSession}->{agentAi.Requests.Count}");

        var beforeLegacyAuto = agentAi.Requests.Count;
        var legacyAuto = await PanelPostJsonAsync(panelTestUrl,
            """{"prompt":"synthetic legacy panel test","target":"auto","timeoutSec":30}""");
        Check("★ 旧 /api/agent/test 显式 auto 也不再静默改派 server",
            legacyAuto.Code == 400 && agentAi.Requests.Count == beforeLegacyAuto,
            $"{legacyAuto.Code} {legacyAuto.Body} model={beforeLegacyAuto}->{agentAi.Requests.Count}");

        var beforePanelPositive = agentAi.Requests.Count;
        var panelPositive = await PanelPostJsonAsync(panelTestUrl,
            """{"prompt":"synthetic legacy panel test","target":"server","timeoutSec":30}""");
        Check("★ 面板 test 显式 server 正向对照命中本地 mock（观察器接线有效）",
            panelPositive.Code == 200 && panelPositive.Body.Contains("\"ok\":true", StringComparison.Ordinal)
            && agentAi.Requests.Count == beforePanelPositive + 1,
            $"{panelPositive.Code} {panelPositive.Body} model={beforePanelPositive}->{agentAi.Requests.Count}");

        // ── cfg-0.3.3：host 目标已移除，ZBA/面板都必须在副作用前拒绝 ──
        var hostSessionCreated = await PanelPostJsonAsync($"http://127.0.0.1:{panelPort}/api/agent/sessions",
            """{"action":"new","key":"group:99999","backend":"host","name":"synthetic-zba-host"}""");
        Check("★ 面板拒绝新建 host 会话（400 host_backend_removed）",
            hostSessionCreated.Code == 400 && hostSessionCreated.Body.Contains("host_backend_removed", StringComparison.Ordinal),
            hostSessionCreated.Code + " " + hostSessionCreated.Body);

        var hostSubmit = await ZbaAsync(http, "POST", root + "/tasks", adminToken,
            "{\"sourceKey\":\"group:99999\",\"sessionId\":\"missing\",\"prompt\":\"synthetic zba host\",\"target\":\"host\",\"timeoutSeconds\":60}",
            idempotencyKey: "synthetic-zba-host-cancel");
        Check("★ ZBA 拒绝 host 目标（400 invalid_target，不创建任务）",
            hostSubmit.Status == 400 && hostSubmit.Body.Contains("invalid_target", StringComparison.Ordinal),
            hostSubmit.Status + " " + hostSubmit.Body);

        var tasksBeforeLegacySession = await ZbaAsync(http, "GET", root + "/tasks?limit=100", readToken);
        var legacyTaskCountBefore = (JsonNode.Parse(tasksBeforeLegacySession.Body) as JsonObject)?["items"]?.AsArray().Count ?? -1;
        var legacySessionSubmit = await ZbaAsync(http, "POST", root + "/tasks", adminToken,
            "{\"sourceKey\":\"group:99999\",\"sessionId\":\"" + legacyHostSessionId
            + "\",\"prompt\":\"synthetic zba host session\",\"target\":\"server\",\"timeoutSeconds\":30}",
            idempotencyKey: "synthetic-zba-host-session");
        var tasksAfterLegacySession = await ZbaAsync(http, "GET", root + "/tasks?limit=100", readToken);
        var legacyTaskCountAfter = (JsonNode.Parse(tasksAfterLegacySession.Body) as JsonObject)?["items"]?.AsArray().Count ?? -1;
        Check("★ ZBA 旧 host 会话在写台账前 400，任务数不变",
            legacySessionSubmit.Status == 400
            && legacySessionSubmit.Body.Contains("host_backend_removed", StringComparison.Ordinal)
            && legacyTaskCountBefore == legacyTaskCountAfter,
            $"{legacySessionSubmit.Status} {legacySessionSubmit.Body} tasks={legacyTaskCountBefore}->{legacyTaskCountAfter}");

        var submitMissingSession = await ZbaAsync(http, "POST", root + "/tasks", adminToken,
            """{"sourceKey":"group:99999","sessionId":"missing","prompt":"synthetic","target":"server","timeoutSeconds":30}""",
            idempotencyKey: "synthetic-zba-idem-1");
        Check("★ ZBA task submit 未知会话 404",
            submitMissingSession.Status == 404
            && submitMissingSession.Body.Contains("session_not_found", StringComparison.Ordinal),
            submitMissingSession.Status.ToString());

        var submitNoKey = await ZbaAsync(http, "POST", root + "/tasks", adminToken,
            """{"sourceKey":"group:99999","sessionId":"missing","prompt":"synthetic","target":"server","timeoutSeconds":30}""");
        Check("ZBA task submit 缺幂等键 400", submitNoKey.Status == 400, submitNoKey.Status.ToString());

        var openApi = await ZbaAsync(http, "GET", root + "/openapi.json", readToken);
        Check("★ ZBA OpenAPI 可读且含 save 路径",
            openApi.Status == 200
            && (JsonNode.Parse(openApi.Body) as JsonObject)?["paths"]?["/zba/v1/configuration/save"] is not null,
            openApi.Status.ToString());

        var botDll = BotDll;
        if (string.IsNullOrWhiteSpace(botDll))
        {
            Check("ZBA 只读 CLI 可运行（需要机器人 DLL）", false, "BotDll missing");
        }
        else
        {
            var cliStatus = await RunCliAsync(botDll, root, readToken, "--status");
            Check("★ ZBA 只读 CLI --status 成功且不回显令牌",
                cliStatus.Code == 0 && cliStatus.Out.Contains("\"components\"", StringComparison.Ordinal)
                && !cliStatus.Out.Contains(readToken, StringComparison.Ordinal),
                cliStatus.Code + " " + cliStatus.Err);
            var cliCapabilities = await RunCliAsync(botDll, root, readToken, "--capabilities");
            Check("ZBA 只读 CLI --capabilities 成功",
                cliCapabilities.Code == 0 && cliCapabilities.Out.Contains("\"items\"", StringComparison.Ordinal),
                cliCapabilities.Code + " " + cliCapabilities.Err);
            var cliTasks = await RunCliAsync(botDll, root, readToken, "--tasks");
            Check("ZBA 只读 CLI --tasks 成功",
                cliTasks.Code == 0 && cliTasks.Out.Contains("\"items\"", StringComparison.Ordinal),
                cliTasks.Code + " " + cliTasks.Err);
            var cliEvents = await RunCliAsync(botDll, root, readToken, "--events", "--after", "0");
            Check("ZBA 只读 CLI --events 成功",
                cliEvents.Code == 0 && cliEvents.Out.Contains("\"nextSequence\"", StringComparison.Ordinal),
                cliEvents.Code + " " + cliEvents.Err);
        }

        var decideMissingApprove = await ZbaAsync(http, "POST", root + "/approvals/missing/decide", adminToken, "{}");
        Check("ZBA approval decide 缺 approve 400", decideMissingApprove.Status == 400,
            decideMissingApprove.Status.ToString());
    }

    /// <summary>合成 12 条旧 host 会话，复现“旧上限计数把只读归档当活跃会话”的升级场景。</summary>
    private static string SeedLegacyHostSessions(string dataDir)
    {
        const string sourceKey = "group:99999";
        const string currentHostId = "legacy-host-000";
        var sessions = new JsonArray();
        for (var i = 0; i < 12; i++)
        {
            sessions.Add(new JsonObject
            {
                ["id"] = $"legacy-host-{i:000}",
                ["name"] = $"旧设备会话 {i}",
                ["autoNamed"] = false,
                ["backend"] = "host",
                ["device"] = "synthetic-device",
                ["piSession"] = $"synthetic-pi-{i:000}",
                ["piOwned"] = true,
                ["turns"] = 1,
                ["createdAt"] = "2026-10-01T00:00:00.0000000+00:00",
                ["updatedAt"] = $"2026-10-01T00:00:{i:00}.0000000+00:00",
                ["history"] = new JsonArray(),
                ["runs"] = new JsonArray()
            });
        }

        var payload = new JsonObject
        {
            ["chats"] = new JsonObject
            {
                [sourceKey] = new JsonObject
                {
                    ["current"] = new JsonObject { ["host"] = currentHostId },
                    ["sessions"] = sessions
                }
            }
        };
        var dataPath = Path.Combine(dataDir, "data");
        Directory.CreateDirectory(dataPath);
        File.WriteAllText(Path.Combine(dataPath, "agent-sessions.json"), payload.ToJsonString());
        return currentHostId;
    }

    private static async Task<(int Code, string Out, string Err)> RunCliAsync(
        string botDll, string baseUrl, string token, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(botDll);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["BOTAGENT_ZBA_URL"] = baseUrl;
        start.Environment["BOTAGENT_ZBA_READ_TOKEN"] = token;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("cli_start_failed");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (process.ExitCode, stdout, stderr);
    }

    private static async Task<JsonObject?> WaitForTaskStateAsync(
        HttpClient http, string root, string token, string taskId, int state, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var (status, body) = await ZbaAsync(http, "GET", root + "/tasks/" + taskId, token);
            if (status == 200 && JsonNode.Parse(body) is JsonObject node
                && node["state"]?.GetValue<int>() == state)
                return node;
            await Task.Delay(250);
        }

        return null;
    }

    /// <summary>批量轮询一组任务，全部到达指定状态才返回 true（容量场景按 4 条一批提交）。</summary>
    private static async Task<bool> WaitForTasksStateAsync(
        HttpClient http, string root, string token, IReadOnlyList<string> taskIds, int state, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var responses = await Task.WhenAll(
                taskIds.Select(taskId => ZbaAsync(http, "GET", root + "/tasks/" + taskId, token)));
            var all = responses.All(response =>
                response.Status == 200
                && JsonNode.Parse(response.Body) is JsonObject node
                && node["state"]?.GetValue<int>() == state);
            if (all) return true;
            await Task.Delay(25);
        }

        return false;
    }

    private static async Task<(int Status, string Body)> ZbaAsync(
        HttpClient http, string method, string url, string? token, string? json = null, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
