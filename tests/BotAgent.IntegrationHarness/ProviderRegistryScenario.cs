using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S61 供应商注册表热重建（#128 S2）。
///
/// 以前供应商候选表只在进程启动时抄一次：面板登记/删除供应商、调优先级之后，
/// 模型投影只能报 RestartRequired，而路由解析又会因 captured 与 desired 不一致而拒绝，
/// 结果是「保存成功、回执说要重启、然后机器人不再回复」。现在候选表由投影步热替换，
/// 本场景钉住三件事：
///   ① 登记一个更高优先级的供应商，不重启下一个聊天请求就打到它；
///   ② 回执必须报 Applied（commitState=1 且 modelProjection 步 state=1），不能说成需要重启；
///   ③ 删掉这个供应商后，下一个请求回到 primary —— 候选表既能长也能缩。
/// 全部为合成设置与合成端点，不接真实模型。
/// </summary>
public static partial class Program
{
    private static async Task RunProviderRegistryScenarioAsync()
    {
        Section("S61 供应商注册表热重建（登记/删除供应商不重启即换路由）");

        const string adminToken = "synthetic-provider-admin-token";
        const string readToken = "synthetic-provider-read-token";
        const long groupId = 66695;
        var openAiPortA = FreePort(17868);
        var openAiPortB = FreePort(17869);
        var botWsPort = FreePort(13078);
        var panelPort = FreePort(18128);
        var dataDir = NewDataDir("s61");

        using var openAiA = new MockOpenAi(openAiPortA);
        openAiA.Start();
        using var openAiB = new MockOpenAi(openAiPortB);
        openAiB.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock-registry",
            ["QQCHAT_BASE_URL"] = openAiA.BaseUrl,
            ["QQCHAT_MODEL"] = "model-primary-env",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = groupId.ToString(),
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_PANEL_TOKEN"] = SyntheticPanelToken,
            ["BOTAGENT_ZBA_TOKEN"] = adminToken,
            ["BOTAGENT_ZBA_READ_TOKEN"] = readToken
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);
        await WaitUntilAsync(() => bot.OutputLines.Any(line => line.Contains("已启动")), TimeSpan.FromSeconds(20));

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);

        var root = $"http://127.0.0.1:{panelPort}/zba/v1";

        async Task<(int Status, JsonNode? Node)> SaveRegisteredAsync(JsonArray providers)
        {
            var read = await ZbaAsync(http, "POST", root + "/configuration/read", readToken, """{"kind":0}""");
            var config = JsonNode.Parse(read.Body) as JsonObject;
            var revision = config?["contractRevision"]?.GetValue<string>() ?? "cfg-0.3.3";
            var version = config?["version"]?.GetValue<string>();
            var generation = config?["generation"]?.GetValue<string>();
            if (version is null || generation is null)
                throw new InvalidOperationException(
                    $"S61 读取配置失败：HTTP {read.Status} {Truncate(read.Body, 300)}");
            var body = new JsonObject
            {
                ["contractRevision"] = revision,
                ["expectedVersion"] = version,
                ["key"] = $"cfg1:{generation}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():N}",
                ["changes"] = new JsonObject
                {
                    ["registered"] = new JsonObject
                    {
                        ["plugins"] = new JsonArray(),
                        ["providers"] = providers,
                        ["policies"] = new JsonArray()
                    }
                },
                ["secrets"] = new JsonArray()
            };
            var save = await ZbaAsync(http, "POST", root + "/configuration/save", adminToken, body.ToJsonString());
            return (save.Status, JsonNode.Parse(save.Body));
        }

        // ---- 1) 基线：没有登记任何额外供应商时，聊天请求走环境变量指向的 A ----
        openAiA.EnqueueReply("""{"suitability": 90, "reply": "主线回答"}""");
        await protocol.SendGroupMessageAsync(groupId, 20001, "群友A", "synthetic baseline route", 61001,
            mentionBot: true, ct: cts.Token);
        var usedPrimary = await WaitUntilAsync(() => openAiA.Requests.Count > 0, TimeSpan.FromSeconds(30));
        Check("登记额外供应商之前，请求发给 primary（环境变量端点）", usedPrimary,
            $"A 收到 {openAiA.Requests.Count} 次");

        // ---- 2) 登记一个优先级更高的供应商：不重启就要生效 ----
        var createSecondary = new JsonArray(new JsonObject
        {
            ["key"] = "secondary",
            ["operation"] = 1,
            ["changes"] = new JsonObject
            {
                ["nameRaw"] = new JsonObject { ["kind"] = 1, ["value"] = "合成备用端点" },
                ["priority"] = new JsonObject { ["kind"] = 1, ["value"] = -1 },
                ["baseUrl"] = new JsonObject { ["kind"] = 1, ["value"] = openAiB.BaseUrl },
                ["modelName"] = new JsonObject { ["kind"] = 1, ["value"] = "model-from-secondary" },
                ["enabled"] = new JsonObject { ["kind"] = 1, ["value"] = false },
                ["credential"] = new JsonObject { ["kind"] = 1 }
            }
        });
        var (createStatus, createNode) = await SaveRegisteredAsync(createSecondary);
        var createReceipt = createNode?["receipt"];
        var createSteps = (createReceipt?["steps"] as JsonArray)?.OfType<JsonObject>().ToList()
            ?? new List<JsonObject>();
        Check("登记供应商的配置保存被接受（HTTP 200 且 code=0）",
            createStatus == 200 && createNode?["code"]?.GetValue<int>() == 0,
            $"HTTP {createStatus}; body={Truncate(createNode?.ToJsonString() ?? string.Empty, 400)}");
        Check("★ 供应商登记不再要求重启（modelProjection 步 Applied）",
            createReceipt?["commitState"]?.GetValue<int>() == 1
            && createSteps.Any(step => step["step"]?.GetValue<int>() == 2 && step["state"]?.GetValue<int>() == 1),
            string.Join(",", createSteps.Select(step => step["step"]?.ToJsonString() + ":" + step["state"]?.ToJsonString())));

        // 新建永远以停用落地，启用是第二次显式保存（"建好即路由"要挡掉）。
        var enableSecondary = new JsonArray(new JsonObject
        {
            ["key"] = "secondary",
            ["operation"] = 0,
            ["changes"] = new JsonObject
            {
                ["enabled"] = new JsonObject { ["kind"] = 1, ["value"] = true },
                ["credential"] = new JsonObject { ["kind"] = 0 }
            }
        });
        var (enableStatus, enableNode) = await SaveRegisteredAsync(enableSecondary);
        var enableReceipt = enableNode?["receipt"];
        var enableSteps = (enableReceipt?["steps"] as JsonArray)?.OfType<JsonObject>().ToList()
            ?? new List<JsonObject>();
        Check("启用新增供应商同样被接受且不需要重启（modelProjection 步 Applied）",
            enableStatus == 200 && enableNode?["code"]?.GetValue<int>() == 0
            && enableReceipt?["commitState"]?.GetValue<int>() == 1
            && enableSteps.Any(step => step["step"]?.GetValue<int>() == 2 && step["state"]?.GetValue<int>() == 1),
            $"HTTP {enableStatus}; steps={string.Join(",", enableSteps.Select(step => step["step"]?.ToJsonString() + ":" + step["state"]?.ToJsonString()))}; body={Truncate(enableNode?.ToJsonString() ?? string.Empty, 300)}");

        // ---- 3) 不重启的下一个请求应当打到新登记的端点 ----
        var primaryRequestsBeforeSwitch = openAiA.Requests.Count;
        openAiB.ClearRequests();
        openAiB.EnqueueReply("""{"suitability": 90, "reply": "备用端点回答"}""");
        await protocol.SendGroupMessageAsync(groupId, 20001, "群友A", "synthetic registry hot route", 61002,
            mentionBot: true, ct: cts.Token);
        var usedSecondary = await WaitUntilAsync(() => openAiB.Requests.Any(request => UserTexts(request)
            .Any(text => text.Contains("synthetic registry hot route"))), TimeSpan.FromSeconds(30));
        var secondaryIndex = Enumerable.Range(0, openAiB.Requests.Count).LastOrDefault(index => UserTexts(openAiB.Requests[index])
            .Any(text => text.Contains("synthetic registry hot route")), -1);
        var secondaryRequest = secondaryIndex >= 0 ? openAiB.Requests[secondaryIndex] : null;
        var secondaryModel = secondaryRequest?["model"]?.GetValue<string>();
        var secondaryAuthorization = secondaryIndex >= 0 ? openAiB.RequestAuthorizations[secondaryIndex] : null;
        Check("★ 新增供应商不重启即接管路由（旧端点不再收请求）",
            usedSecondary && openAiA.Requests.Count == primaryRequestsBeforeSwitch,
            $"new={openAiB.Requests.Count}; oldDelta={openAiA.Requests.Count - primaryRequestsBeforeSwitch}");
        Check("★ 新路由用的是登记行里的模型名", secondaryModel == "model-from-secondary",
            secondaryModel ?? "no model");
        Check("★ 新路由带上了主模型凭据（登记时选择复用 primary 凭据）",
            !string.IsNullOrEmpty(secondaryAuthorization), secondaryAuthorization is null ? "no authorization" : "header present");

        // PromptCache is a live route option, not merely a persisted checkbox. Keep this
        // same bot process and inspect the very next synthetic HTTP request after each save.
        Check("PromptCache 默认关闭不添加缓存参数",
            secondaryRequest is not null && !secondaryRequest.ContainsKey("prompt_cache_key"), "wire shape only");
        var invalidOptions = new JsonArray(new JsonObject
        {
            ["key"] = "secondary", ["operation"] = 0,
            ["changes"] = new JsonObject
            {
                ["options"] = new JsonObject
                {
                    ["kind"] = 1,
                    ["value"] = new JsonObject
                    {
                        ["azureApiVersion"] = "2024-10-21", ["anthropicVersion"] = "2023-06-01",
                        ["promptCache"] = true, ["syntheticUnknownOption"] = true
                    }
                }
            }
        });
        var (invalidOptionsStatus, _) = await SaveRegisteredAsync(invalidOptions);
        Check("Provider options reject unknown fields over HTTP", invalidOptionsStatus == 400,
            $"HTTP {invalidOptionsStatus}");
        foreach (var cacheEnabled in new[] { true, false })
        {
            var cacheChange = new JsonArray(new JsonObject
            {
                ["key"] = "secondary", ["operation"] = 0,
                ["changes"] = new JsonObject
                {
                    ["options"] = new JsonObject
                    {
                        ["kind"] = 1,
                        ["value"] = new JsonObject
                        {
                            ["azureApiVersion"] = "2024-10-21",
                            ["anthropicVersion"] = "2023-06-01",
                            ["promptCache"] = cacheEnabled
                        }
                    },
                    ["credential"] = new JsonObject { ["kind"] = 0 }
                }
            });
            var (cacheStatus, cacheNode) = await SaveRegisteredAsync(cacheChange);
            var cacheSteps = (cacheNode?["receipt"]?["steps"] as JsonArray)?.OfType<JsonObject>()
                ?? Enumerable.Empty<JsonObject>();
            Check($"PromptCache={cacheEnabled} 保存后模型投影立即 Applied",
                cacheStatus == 200 && cacheNode?["receipt"]?["commitState"]?.GetValue<int>() == 1
                && cacheSteps.Any(step => step["step"]?.GetValue<int>() == 2 && step["state"]?.GetValue<int>() == 1),
                $"HTTP {cacheStatus}; code={cacheNode?["code"]?.ToJsonString()}; error={cacheNode?["error"]?.ToJsonString()}");
            openAiB.ClearRequests();
            var marker = cacheEnabled ? "synthetic cache enabled next request" : "synthetic cache disabled next request";
            openAiB.EnqueueReply("""{"suitability":90,"reply":"Synthetic cache option reply"}""");
            await protocol.SendGroupMessageAsync(groupId, 20001, "群友A", marker,
                cacheEnabled ? 61006 : 61007, mentionBot: true, ct: cts.Token);
            var cacheRequestArrived = await WaitUntilAsync(() => openAiB.Requests.Any(request => UserTexts(request)
                .Any(text => text.Contains(marker))), TimeSpan.FromSeconds(30));
            var cacheRequest = openAiB.Requests.FirstOrDefault(request => UserTexts(request).Any(text => text.Contains(marker)));
            var cacheKey = cacheRequest?["prompt_cache_key"]?.GetValue<string>();
            Check($"PromptCache={cacheEnabled} 无重启改变下一条 HTTP 请求",
                cacheRequestArrived && cacheRequest is not null
                && (cacheEnabled ? !string.IsNullOrWhiteSpace(cacheKey) : !cacheRequest.ContainsKey("prompt_cache_key"))
                && cacheRequest?["model"]?.GetValue<string>() == "model-from-secondary",
                $"request observed={cacheRequestArrived}; cache parameter present={cacheKey is not null}");
        }

        // ---- 4) 删掉它：候选表要能缩回去，下一个请求回到 primary ----
        var secondaryRequestsBeforeDelete = openAiB.Requests.Count;
        openAiA.ClearRequests();
        var deleteSecondary = new JsonArray(new JsonObject
        {
            ["key"] = "secondary",
            ["operation"] = 2,
            ["changes"] = new JsonObject { ["credential"] = new JsonObject { ["kind"] = 0 } }
        });
        var (deleteStatus, deleteNode) = await SaveRegisteredAsync(deleteSecondary);
        var deleteReceipt = deleteNode?["receipt"];
        var deleteSteps = (deleteReceipt?["steps"] as JsonArray)?.OfType<JsonObject>().ToList()
            ?? new List<JsonObject>();
        Check("删除供应商的配置保存被接受且不需要重启",
            deleteStatus == 200 && deleteNode?["code"]?.GetValue<int>() == 0
            && deleteReceipt?["commitState"]?.GetValue<int>() == 1
            && deleteSteps.Any(step => step["step"]?.GetValue<int>() == 2 && step["state"]?.GetValue<int>() == 1),
            $"HTTP {deleteStatus}; steps={string.Join(",", deleteSteps.Select(step => step["step"]?.ToJsonString() + ":" + step["state"]?.ToJsonString()))}");

        openAiA.EnqueueReply("""{"suitability": 90, "reply": "回到主线"}""");
        await protocol.SendGroupMessageAsync(groupId, 20001, "群友A", "synthetic registry route removed", 61003,
            mentionBot: true, ct: cts.Token);
        var backToPrimary = await WaitUntilAsync(() => openAiA.Requests.Any(request => UserTexts(request)
            .Any(text => text.Contains("synthetic registry route removed"))), TimeSpan.FromSeconds(30));
        Check("★ 删除供应商不重启即回到 primary 路由",
            backToPrimary && openAiB.Requests.Count == secondaryRequestsBeforeDelete,
            $"primary={openAiA.Requests.Count}; secondaryDelta={openAiB.Requests.Count - secondaryRequestsBeforeDelete}");
    }
}
