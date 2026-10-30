using System.Text;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S51 Token 配额插件迁移：旧面板兼容、ZBA CAS/回执、真实模型准入与重启停用后的独立记账。
/// 只使用合成 SourceKey，不读取真实会话正文。
/// </summary>
public static partial class Program
{
    private static async Task RunQuotaPanelScenarioAsync()
    {
        Section("S51 Token 配额插件 · 旧面板兼容 · ZBA · 模型准入 · 重启记账");

        const int openAiPortPreferred = 17893;
        const int botWsPortPreferred = 13113;
        const int panelPortPreferred = 18173;
        const int syntheticGroupId = 10001;
        const string panelToken = "it-s51-token";
        const string tenant = "group:10001";
        const int budgetGroupId = 30003;
        const string budgetTenant = "group:30003";
        const string quotaPluginId = "preset.feature.token-quota";
        const string adminToken = "synthetic-s51-zba-admin";
        const string readToken = "synthetic-s51-zba-read";

        var openAiPort = FreePort(openAiPortPreferred);
        var botWsPort = FreePort(botWsPortPreferred);
        var panelPort = FreePort(panelPortPreferred);
        var dataDir = NewDataDir("s51");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        openAi.EnqueueReply("""{"suitability":99,"reply":"合成配额回复"}""");

        var env = new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = $"{syntheticGroupId},{budgetGroupId}",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_ENABLE_POKE"] = "0",
            ["QQCHAT_ENABLE_VOICE"] = "0",
            ["QQCHAT_ENABLE_MUSIC"] = "0",
            ["QQCHAT_WEB_SEARCH"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_PANEL_TOKEN"] = panelToken,
            ["BOTAGENT_ZBA_TOKEN"] = adminToken,
            ["BOTAGENT_ZBA_READ_TOKEN"] = readToken,
        };

        using var bot = StartBot(env);
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await protocol.SendGroupMessageAsync(syntheticGroupId, 20002, "合成成员", "配额面板测试", 5101, ct: cts.Token);
        Check("原 S51 合成消息经真实模型完成回复",
            await WaitUntilAsync(() => QuotaGroupSendCount(protocol, syntheticGroupId) > 0, TimeSpan.FromSeconds(15)));

        var panelUrl = $"http://127.0.0.1:{panelPort}";
        var quotaUrl = $"{panelUrl}/api/quotas?tenant={Uri.EscapeDataString(tenant)}";
        var zbaRoot = $"{panelUrl}/zba/v1";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var plugin = await ReadQuotaPluginAsync(http, zbaRoot, readToken, quotaPluginId);
        Check("宿主能力目录默认注册并启用 Token 配额插件",
            plugin?["desiredEnabled"]?.GetValue<bool>() == true
            && plugin?["runtimeState"]?.GetValue<int>() == 4);

        var (anonymousCode, _) = await HttpGetAsync(quotaUrl);
        Check("未认证读取配额返回 401", anonymousCode == 401, $"HTTP {anonymousCode}");

        var (unknownCode, _) = await HttpGetAsync(
            $"{panelUrl}/api/quotas?tenant={Uri.EscapeDataString("group:99999")}", panelToken);
        Check("未登记租户不能被面板任意创建", unknownCode == 404, $"HTTP {unknownCode}");

        var (initialCode, initialBody) = await HttpGetAsync(quotaUrl, panelToken);
        var initial = JsonNode.Parse(initialBody) as JsonObject;
        Check("已登记租户可读取每日配额快照",
            initialCode == 200
            && initial?["tenant"]?.GetValue<string>() == tenant
            && initial?["dailyTokenLimit"]?.GetValue<int>() == 50000
            && initial?["usedPromptTokens"]?.GetValue<int>() == 0
            && initial?["usedCompletionTokens"]?.GetValue<int>() == 0
            && initial?["remainingTokens"]?.GetValue<int>() == 50000
            && initial?["energySaving"]?.GetValue<bool>() == false
            && !string.IsNullOrWhiteSpace(initial?["resetDate"]?.GetValue<string>())
            && !string.IsNullOrWhiteSpace(initial?["nextResetAt"]?.GetValue<string>())
            && initial?["reason"] is null,
            $"HTTP {initialCode} {initialBody}");

        var (lowCode, lowBody) = await PanelPostJsonAsync(
            $"{panelUrl}/api/quotas",
            new JsonObject { ["tenant"] = tenant, ["dailyTokenLimit"] = 0 }.ToJsonString(),
            panelToken);
        Check("低于最小值的每日配额返回 400", lowCode == 400, $"HTTP {lowCode} {lowBody}");

        var (saveCode, saveBody) = await PanelPostJsonAsync(
            $"{panelUrl}/api/quotas",
            new JsonObject { ["tenant"] = tenant, ["dailyTokenLimit"] = 1000000000 }.ToJsonString(),
            panelToken);
        var saved = JsonNode.Parse(saveBody) as JsonObject;
        Check("面板可保存 1B 每日配额",
            saveCode == 200 && saved?["dailyTokenLimit"]?.GetValue<int>() == 1000000000,
            $"HTTP {saveCode} {saveBody}");

        var (afterCode, afterBody) = await HttpGetAsync(quotaUrl, panelToken);
        var after = JsonNode.Parse(afterBody) as JsonObject;
        Check("保存配额不改变已有用量字段",
            afterCode == 200
            && after?["dailyTokenLimit"]?.GetValue<int>() == 1000000000
            && after?["usedPromptTokens"]?.GetValue<int>() == initial?["usedPromptTokens"]?.GetValue<int>()
            && after?["usedCompletionTokens"]?.GetValue<int>() == initial?["usedCompletionTokens"]?.GetValue<int>(),
            $"HTTP {afterCode} {afterBody}");

        // 新协议只列出已登记 SourceKey；旧 S51 的 1B 配额保持不变。
        var anonymousList = await ZbaAsync(http, "GET", zbaRoot + "/quotas", null);
        var wrongList = await ZbaAsync(http, "GET", zbaRoot + "/quotas", "synthetic-invalid-token");
        Check("ZBA 配额列表拒绝未认证与错误 Bearer", anonymousList.Status == 401 && wrongList.Status == 401);
        var quotaList = await ZbaAsync(http, "GET", zbaRoot + "/quotas", readToken);
        var firstQuota = QuotaItem(quotaList.Body, tenant);
        Check("ZBA 读权限返回完整配额字段且兼容旧面板保存",
            quotaList.Status == 200 && firstQuota?["dailyTokenLimit"]?.GetValue<int>() == 1000000000
            && firstQuota?["usedPromptTokens"]?.GetValue<int>() == 0
            && firstQuota?["usedCompletionTokens"]?.GetValue<int>() == 0
            && firstQuota?["usedTokens"]?.GetValue<long>() == 0
            && firstQuota?["remainingTokens"]?.GetValue<int>() == 1000000000
            && firstQuota?["energySaving"]?.GetValue<bool>() == false
            && firstQuota?["revision"] is JsonValue revisionValue && revisionValue.TryGetValue<string>(out _)
            && !string.IsNullOrWhiteSpace(firstQuota?["displayName"]?.GetValue<string>())
            && !string.IsNullOrWhiteSpace(firstQuota?["resetDate"]?.GetValue<string>())
            && !string.IsNullOrWhiteSpace(firstQuota?["nextResetAt"]?.GetValue<string>())
            && firstQuota?["minimumDailyTokenLimit"]?.GetValue<int>() == 1
            && firstQuota?["maximumDailyTokenLimit"]?.GetValue<int>() == 1000000000);

        // 每轮由成功的模型回复 + 配额 GET 记账完成同步，不用空候选伪造用量。
        openAi.PromptTokens = 40;
        openAi.CompletionTokens = 10;
        await protocol.SendGroupMessageAsync(budgetGroupId, 20002, "群友A", "synthetic quota baseline", 5102, ct: cts.Token);
        Check("低配额租户建立前普通消息可调用模型并回复",
            await WaitUntilAsync(() => QuotaGroupSendCount(protocol, budgetGroupId) > 0, TimeSpan.FromSeconds(15)));
        var budgetBefore = await WaitForQuotaItemAsync(http, zbaRoot, readToken, budgetTenant,
            q => q["usedTokens"]?.GetValue<long>() == 50, cts.Token);
        Check("模型真实 prompt/completion 用量按租户独立记录",
            budgetBefore?["usedPromptTokens"]?.GetValue<int>() == 40
            && budgetBefore?["usedCompletionTokens"]?.GetValue<int>() == 10
            && budgetBefore?["dailyTokenLimit"]?.GetValue<int>() == 50000);
        var budgetRevision = budgetBefore?["revision"]?.GetValue<string>()
            ?? throw new InvalidOperationException("S51 synthetic quota revision missing");
        const string saveKey = "s51-quota-budget-save";
        var saveRequest = QuotaSaveJson(budgetTenant, budgetRevision, saveKey, 1);
        foreach (var token in new string?[] { null, "synthetic-invalid-token", readToken })
        {
            var expected = token == readToken ? 403 : 401;
            var deniedSave = await ZbaAsync(http, "POST", zbaRoot + "/quotas/save", token, saveRequest);
            var deniedFind = await ZbaAsync(http, "POST", zbaRoot + "/quotas/find", token,
                new JsonObject { ["idempotencyKey"] = saveKey }.ToJsonString());
            Check($"ZBA quota save/find 权限边界 HTTP {expected}",
                deniedSave.Status == expected && deniedFind.Status == expected);
        }
        var unknownSave = await ZbaAsync(http, "POST", zbaRoot + "/quotas/save", adminToken,
            QuotaSaveJson("group:99999", "0", "s51-quota-unknown", 1));
        Check("ZBA 不允许任意创建未登记租户", unknownSave.Status == 404);
        foreach (var limit in new[] { 0, 1000000001 })
        {
            var invalidSave = await ZbaAsync(http, "POST", zbaRoot + "/quotas/save", adminToken,
                QuotaSaveJson(budgetTenant, budgetRevision, $"s51-quota-invalid-{limit}", limit));
            Check($"ZBA 拒绝范围外每日配额 {limit}", invalidSave.Status == 400);
        }
        var unchangedList = await ZbaAsync(http, "GET", zbaRoot + "/quotas", readToken);
        var unchanged = QuotaItem(unchangedList.Body, budgetTenant);
        Check("拒绝的保存不改变限额、用量或 CAS revision",
            JsonNode.DeepEquals(unchanged, budgetBefore)
            && QuotaItem(unchangedList.Body, "group:99999") is null);

        var newSave = await ZbaAsync(http, "POST", zbaRoot + "/quotas/save", adminToken, saveRequest);
        var receipt = JsonNode.Parse(newSave.Body)?["quota"];
        Check("管理员保存低限额立即进入节能且不重置已有用量",
            newSave.Status == 200 && receipt?["sourceKey"]?.GetValue<string>() == budgetTenant
            && receipt?["dailyTokenLimit"]?.GetValue<int>() == 1
            && receipt?["usedTokens"]?.GetValue<long>() == 50
            && receipt?["remainingTokens"]?.GetValue<int>() == 0
            && receipt?["energySaving"]?.GetValue<bool>() == true
            && receipt?["revision"]?.GetValue<string>() != budgetRevision);
        var replay = await ZbaAsync(http, "POST", zbaRoot + "/quotas/save", adminToken, saveRequest);
        Check("重复保存按幂等键返回同一回执（先于 CAS）",
            replay.Status == 200 && JsonNode.DeepEquals(JsonNode.Parse(replay.Body)?["quota"], receipt));
        var stale = await ZbaAsync(http, "POST", zbaRoot + "/quotas/save", adminToken,
            QuotaSaveJson(budgetTenant, budgetRevision, "s51-quota-stale", 2));
        var conflicting = await ZbaAsync(http, "POST", zbaRoot + "/quotas/save", adminToken,
            QuotaSaveJson(budgetTenant, budgetRevision, saveKey, 2));
        Check("陈旧 revision 与幂等键改载荷分别拒绝冲突", stale.Status == 409 && conflicting.Status == 409);
        var missingFind = await ZbaAsync(http, "POST", zbaRoot + "/quotas/find", adminToken,
            """{"idempotencyKey":"s51-quota-never-saved"}""");
        Check("未知幂等键 found=false 不伪造配额", missingFind.Status == 200
            && JsonNode.Parse(missingFind.Body)?["found"]?.GetValue<bool>() == false
            && JsonNode.Parse(missingFind.Body)?["quota"] is null);
        var legacyBudget = await HttpGetAsync($"{panelUrl}/api/quotas?tenant={Uri.EscapeDataString(budgetTenant)}", panelToken);
        Check("ZBA 保存后旧面板立即读到同一限额与用量", legacyBudget.Status == 200
            && JsonNode.Parse(legacyBudget.Body)?["dailyTokenLimit"]?.GetValue<int>() == 1
            && JsonNode.Parse(legacyBudget.Body)?["usedPromptTokens"]?.GetValue<int>() == 40);

        // 沉默观测窗口后用同一租户直接提及作阳性对照，防止“协议没连上”假通过。
        var requestsBefore = openAi.Requests.Count;
        var sendsBefore = QuotaGroupSendCount(protocol, budgetGroupId);
        await protocol.SendGroupMessageAsync(budgetGroupId, 20002, "群友A", "synthetic quota blocked", 5103, ct: cts.Token);
        await Task.Delay(1500, cts.Token);
        Check("启用配额插件拦下超额普通消息且不调用模型", openAi.Requests.Count == requestsBefore
            && QuotaGroupSendCount(protocol, budgetGroupId) == sendsBefore);
        openAi.EnqueueReply("""{"suitability":99,"reply":"synthetic mention response"}""");
        await protocol.SendGroupMessageAsync(budgetGroupId, 20002, "群友A", "synthetic quota mention", 5104,
            mentionBot: true, ct: cts.Token);
        Check("超额直接提及仍可经真实模型回复",
            await WaitUntilAsync(() => QuotaGroupSendCount(protocol, budgetGroupId) > sendsBefore, TimeSpan.FromSeconds(15))
            && openAi.Requests.Count > requestsBefore);
        var afterMention = await WaitForQuotaItemAsync(http, zbaRoot, readToken, budgetTenant,
            q => q["usedTokens"]?.GetValue<long>() == 100, cts.Token);
        Check("直接提及绕过准入仍记录用量且保持节能", afterMention?["usedPromptTokens"]?.GetValue<int>() == 80
            && afterMention?["usedCompletionTokens"]?.GetValue<int>() == 20
            && afterMention?["energySaving"]?.GetValue<bool>() == true);
        var found = await ZbaAsync(http, "POST", zbaRoot + "/quotas/find", adminToken,
            new JsonObject { ["idempotencyKey"] = saveKey }.ToJsonString());
        Check("find 返回提交时回执而不是后续用量快照", found.Status == 200
            && JsonNode.Parse(found.Body)?["found"]?.GetValue<bool>() == true
            && JsonNode.DeepEquals(JsonNode.Parse(found.Body)?["quota"], receipt));

        var config = await ZbaAsync(http, "POST", zbaRoot + "/configuration/read", adminToken, """{"kind":1}""");
        var configNode = JsonNode.Parse(config.Body);
        var disableJson = new JsonObject
        {
            ["contractRevision"] = configNode?["contractRevision"]?.DeepClone(),
            ["expectedVersion"] = configNode?["version"]?.DeepClone(),
            ["key"] = $"cfg1:{configNode?["generation"]?.GetValue<string>()}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():N}",
            ["changes"] = new JsonObject { ["registered"] = new JsonObject
                { ["plugins"] = new JsonArray(new JsonObject { ["key"] = quotaPluginId, ["enabled"] = false }),
                  ["providers"] = new JsonArray(), ["policies"] = new JsonArray() } },
            ["secrets"] = new JsonArray()
        }.ToJsonString();
        var disable = await ZbaAsync(http, "POST", zbaRoot + "/configuration/save", adminToken, disableJson);
        var disableNode = JsonNode.Parse(disable.Body);
        Check("插件停用配置持久提交但明确需要重启", disable.Status == 200
            && disableNode?["code"]?.GetValue<int>() == 0
            && disableNode?["receipt"]?["commitState"]?.GetValue<int>() == 1
            && disableNode?["receipt"]?["applyState"]?.GetValue<int>() == 3,
            $"HTTP {disable.Status}; code={disableNode?["code"]}; error={disableNode?["error"]?["code"]}; fieldErrors={disableNode?["errors"]?.ToJsonString()}");
        var stagedPlugin = await ReadQuotaPluginAsync(http, zbaRoot, readToken, quotaPluginId);
        Check("重启前 desired=false 与 runtime=Running 独立报告",
            stagedPlugin?["desiredEnabled"]?.GetValue<bool>() == false
            && stagedPlugin?["runtimeState"]?.GetValue<int>() == 4
            && stagedPlugin?["errorCode"]?.GetValue<string>() == "plugin_restart_required");
        requestsBefore = openAi.Requests.Count;
        sendsBefore = QuotaGroupSendCount(protocol, budgetGroupId);
        await protocol.SendGroupMessageAsync(budgetGroupId, 20002, "群友A", "synthetic quota staged disable", 5105, ct: cts.Token);
        await Task.Delay(1500, cts.Token);
        Check("只保存停用尚未重启时配额准入仍有效", openAi.Requests.Count == requestsBefore
            && QuotaGroupSendCount(protocol, budgetGroupId) == sendsBefore);

        await bot.StopAsync();

        var auditActor = DbProbe.Text(dataDir,
            "SELECT actor_id FROM security_audit_log WHERE event_type = 'config_change' AND actor_id LIKE 'panel-session:%' AND action_detail LIKE '%tenant_quota%' ORDER BY id DESC LIMIT 1");
        Check("配额保存审计记录面板会话指纹且不落原始 token",
            auditActor is not null
            && auditActor.StartsWith("panel-session:", StringComparison.Ordinal)
            && auditActor.Length == "panel-session:".Length + 16
            && !auditActor.Contains(panelToken, StringComparison.Ordinal),
            $"actor={auditActor ?? "(null)"}");

        using var bot2 = StartBot(env);
        await WaitForPortAsync(panelPort, cts.Token, bot2);
        var (restartCode, restartBody) = await HttpGetAsync(quotaUrl, panelToken);
        var restarted = JsonNode.Parse(restartBody) as JsonObject;
        Check("重启后每日配额仍然持久化",
            restartCode == 200 && restarted?["dailyTokenLimit"]?.GetValue<int>() == 1000000000,
            $"HTTP {restartCode} {restartBody}");

        var disabledPlugin = await ReadQuotaPluginAsync(http, zbaRoot, readToken, quotaPluginId);
        Check("重启后 Token 配额插件保持停用且无待重启错误", disabledPlugin?["desiredEnabled"]?.GetValue<bool>() == false
            && disabledPlugin?["runtimeState"]?.GetValue<int>() == 2
            && disabledPlugin?["errorCode"] is null);
        var restartList = await ZbaAsync(http, "GET", zbaRoot + "/quotas", readToken);
        var restartedBudget = QuotaItem(restartList.Body, budgetTenant);
        Check("重启保留低限额、已用量与租户隔离", restartList.Status == 200
            && restartedBudget?["dailyTokenLimit"]?.GetValue<int>() == 1
            && restartedBudget?["usedTokens"]?.GetValue<long>() == 100
            && restartedBudget?["energySaving"]?.GetValue<bool>() == true
            && QuotaItem(restartList.Body, tenant)?["usedTokens"]?.GetValue<long>() == 0);
        var restartFind = await ZbaAsync(http, "POST", zbaRoot + "/quotas/find", adminToken,
            new JsonObject { ["idempotencyKey"] = saveKey }.ToJsonString());
        var restartReplay = await ZbaAsync(http, "POST", zbaRoot + "/quotas/save", adminToken, saveRequest);
        Check("保存回执持久化支持重启后 find 与完全相同重放", restartFind.Status == 200 && restartReplay.Status == 200
            && JsonNode.Parse(restartFind.Body)?["found"]?.GetValue<bool>() == true
            && JsonNode.DeepEquals(JsonNode.Parse(restartFind.Body)?["quota"], receipt)
            && JsonNode.DeepEquals(JsonNode.Parse(restartReplay.Body)?["quota"], receipt));

        await WaitForPortAsync(botWsPort, cts.Token, bot2);
        using var protocol2 = new MockProtocol { SelfId = 10001 };
        await protocol2.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol2.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        requestsBefore = openAi.Requests.Count;
        openAi.EnqueueReply("""{"suitability":99,"reply":"synthetic disabled response"}""");
        await protocol2.SendGroupMessageAsync(budgetGroupId, 20002, "群友A", "synthetic quota disabled", 5106, ct: cts.Token);
        Check("插件停用并重启后超额普通消息仍实际调用模型并回复",
            await WaitUntilAsync(() => QuotaGroupSendCount(protocol2, budgetGroupId) > 0, TimeSpan.FromSeconds(15))
            && openAi.Requests.Count > requestsBefore);
        var disabledUsage = await WaitForQuotaItemAsync(http, zbaRoot, readToken, budgetTenant,
            q => q["usedTokens"]?.GetValue<long>() == 150, cts.Token);
        Check("插件停用不停止独立用量记账", disabledUsage?["usedPromptTokens"]?.GetValue<int>() == 120
            && disabledUsage?["usedCompletionTokens"]?.GetValue<int>() == 30
            && disabledUsage?["remainingTokens"]?.GetValue<int>() == 0
            && disabledUsage?["energySaving"]?.GetValue<bool>() == true);

        await bot2.StopAsync();
    }

    private static int QuotaGroupSendCount(MockProtocol protocol, long groupId)
        => protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg"
            && a["params"]?["group_id"]?.GetValue<long>() == groupId);

    private static JsonNode? QuotaItem(string body, string sourceKey)
        => (JsonNode.Parse(body)?["items"] as JsonArray)?.FirstOrDefault(q => q?["sourceKey"]?.GetValue<string>() == sourceKey);

    private static string QuotaSaveJson(string sourceKey, string revision, string key, int limit)
        => new JsonObject
        {
            ["sourceKey"] = sourceKey, ["expectedRevision"] = revision,
            ["idempotencyKey"] = key, ["dailyTokenLimit"] = limit
        }.ToJsonString();

    private static async Task<JsonNode?> ReadQuotaPluginAsync(HttpClient http, string root, string token, string id)
    {
        // Built-in presets belong to the host capability catalog, not the external package marketplace.
        var result = await ZbaAsync(http, "GET", root + "/capabilities?kind=1&limit=100", token);
        Check("宿主插件能力目录可由读令牌读取", result.Status == 200, $"HTTP {result.Status}");
        return (JsonNode.Parse(result.Body)?["items"] as JsonArray)?.FirstOrDefault(item =>
            item?["key"]?["kind"]?.GetValue<string>() == "plugin"
            && item?["key"]?["key"]?.GetValue<string>() == id);
    }

    private static async Task<JsonNode?> WaitForQuotaItemAsync(HttpClient http, string root, string token,
        string sourceKey, Func<JsonNode, bool> predicate, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        JsonNode? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ZbaAsync(http, "GET", root + "/quotas", token);
            if (result.Status == 200)
            {
                last = QuotaItem(result.Body, sourceKey);
                if (last is not null && predicate(last)) return last;
            }
            await Task.Delay(150, ct);
        }
        return last;
    }
}
