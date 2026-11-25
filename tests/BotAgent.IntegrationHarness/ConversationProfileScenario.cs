using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S156 会话级可组合档案与平台中性提示词端到端集成验证：
///   ① 同宿主双群独立配置（群 A 零插话 + 仅聊天 + 关链接预览 + 过滤动作旁白 + 空白全局人设下追加人设；
///      群 B 100% 插话 + 开启链接预览 + 只读工具档位）互不串台；
///   ② 热更新通过 /zba/v1/configuration/save 即时生效（applyState=1 免重启），且 Overview 隐藏真名 / Editable 返回真名；
///   ③ 平台策略硬上限（显式停用 LinkPreview）不可被会话档案 Enabled=true 复活；
///   ④ SQLite Schema v15 零迁移（user_version=15，configuration_field_modes 无 153 行，meta 持久化）且进程重启后完全生效。
/// </summary>
public static partial class Program
{
    private static async Task RunConversationProfileScenarioAsync()
    {
        Section("S156 会话级可组合档案（双群隔离 / 热更新 / 平台硬上限 / 入站媒体 / 零迁移重启持久）");

        var openAiPort = FreePort(17956);
        var botWsPort = FreePort(13256);
        var panelPort = FreePort(18256);
        var pagePort = FreePort(18356);
        const long groupA = 10001;
        const long groupB = 10002;
        const string adminToken = "synthetic-zba-admin-s156";
        const string readToken = "synthetic-zba-read-s156";
        var dataDir = NewDataDir("s156");

        using var pages = new MockPageHost(pagePort, "合成测试页面", "用于验证会话级链接预览闸门");
        pages.Start();

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        Dictionary<string, string> BuildEnv() => new()
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock-s156",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10009",
            ["QQCHAT_WHITELIST"] = "*",
            // 故意留空全局人设，验证仅配 PersonaAppend 时仍输出 [机器人人设档案]
            ["QQCHAT_PERSONA"] = "",
            ["QQCHAT_GROUP_INTERJECTION_PROBABILITY"] = "100",
            ["QQCHAT_GROUP_INTERJECTION_COOLDOWN_SECONDS"] = "0",
            ["QQCHAT_GROUP_CONTINUATION_WAIT_SECONDS"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_SEGMENT_DELAY_MS"] = "10",
            ["QQCHAT_LINKS"] = "1",
            ["QQCHAT_LINK_PREVIEW_TIMEOUT"] = "5",
            // 故意关闭全局联网搜索，验证群 B 档案局部 WebSearch=true 能穿透 ApprovalUseCase.CapabilitiesFor 暴露 web.search
            ["QQCHAT_SEARCH"] = "0",
            ["QQCHAT_FILTER_ACTION_NARRATION"] = "0",
            ["QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS"] = "1",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_PANEL_TOKEN"] = SyntheticPanelToken,
            ["BOTAGENT_ZBA_TOKEN"] = adminToken,
            ["BOTAGENT_ZBA_READ_TOKEN"] = readToken,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var zbaRoot = $"http://127.0.0.1:{panelPort}/zba/v1";

        using (var bot = StartBot(BuildEnv()))
        {
            await WaitForPortAsync(botWsPort, cts.Token, bot);
            await WaitForPortAsync(panelPort, cts.Token, bot);

            using var protocol = new MockProtocol { SelfId = 10009 };
            await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
            await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

            // 1) 通过 ZBA 热保存双群会话档案（免重启）
            var (revision, version, generation, qqPolicyTarget) = await ReadZbaProfileMetaAsync(http, zbaRoot, readToken);
            var save1 = await SaveConversationProfilesZbaAsync(http, zbaRoot, adminToken, revision, version, generation,
                BuildS156ProfilesPatch(groupA, groupB));
            var save1Node = JsonNode.Parse(save1.Body) as JsonObject;
            var save1Receipt = save1Node?["receipt"] as JsonObject;
            Check("★ ZBA 热保存双群会话档案成功且免重启（applyState=1）",
                save1.Status == 200 && save1Node?["code"]?.GetValue<int>() == 0
                && save1Receipt?["applyState"]?.GetValue<int>() == 1,
                $"{save1.Status} {Truncate(save1.Body, 240)}");

            await VerifyProfileReadPrivacyAsync(http, zbaRoot, readToken);

            // 2) 同宿主双群节奏隔离：群 A 插话概率=0 不触发，群 B 插话概率=100 立即触发
            openAi.ClearRequests();
            await protocol.SendGroupMessageAsync(groupA, 20001, "群友A", "群A普通闲聊不艾特", 9601, mentionBot: false, ct: cts.Token);
            await Task.Delay(2200);
            Check("★ 群 A 插话概率=0 时未 @ 消息不触发模型", openAi.Requests.Count == 0, $"requests={openAi.Requests.Count}");

            openAi.EnqueueReply("""{"suitability": 88, "reply": "群B收到闲聊。"}""");
            await protocol.SendGroupMessageAsync(groupB, 20002, "群友B", "群B普通闲聊不艾特", 9602, mentionBot: false, ct: cts.Token);
            var groupBIdleReply = await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(25));
            Check("★ 群 B 插话概率=100 时未 @ 消息立即回复",
                groupBIdleReply is not null && groupBIdleReply["params"]?["group_id"]?.GetValue<long>() == groupB,
                groupBIdleReply?.ToJsonString() ?? "(null)");

            // 3) 群 A @ 触发：空白全局人设 + PersonaAppend 生效 + ChatOnly 无 web.search + 关链接预览 + 动作旁白过滤
            var hitsBeforeA = pages.Hits;
            var reqsBeforeA = openAi.Requests.Count;
            var sentBeforeA = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg");
            openAi.EnqueueReply("""{"suitability": 95, "reply": "（轻轻点头）直接给结论：方案可行。"}""");
            await protocol.SendGroupMessageAsync(groupA, 20001, "群友A",
                $"@机器人 帮我看下 {pages.PageUrl}?case=alpha", 9603, mentionBot: true, ct: cts.Token);
            var sendA = await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(25), skip: sentBeforeA);
            var reqA = openAi.Requests.Count > reqsBeforeA ? openAi.Requests[^1] : null;
            var sysA = reqA is not null ? SystemText(reqA) : string.Empty;
            Check("★ 群 A 空白全局人设下仍注入 [机器人人设档案] 与 PersonaAppend",
                sysA.Contains("[机器人人设档案]", StringComparison.Ordinal)
                && sysA.Contains("极简技术助手：结论先行。", StringComparison.Ordinal)
                && !sysA.Contains("轻松活泼风格。", StringComparison.Ordinal),
                Truncate(sysA, 300));
            Check("★ 群 A ChatOnly + Search=false 不暴露 web.search 工具",
                !sysA.Contains("web.search", StringComparison.Ordinal),
                "web.search unexpectedly visible");
            Check("★ 群 A 关闭 LinkPreview 后入站链接不触发抓页", pages.Hits == hitsBeforeA, $"hits {hitsBeforeA}->{pages.Hits}");
            var textA = MessageText(sendA);
            Check("★ 群 A FilterActionNarration=true 在发送链路上过滤动作旁白",
                sendA is not null && !textA.Contains("轻轻点头", StringComparison.Ordinal)
                && textA.Contains("直接给结论", StringComparison.Ordinal),
                textA);

            // 4) 群 B @ 触发：PersonaAppend 独立生效 + 开启链接预览抓页 + ReadOnlyTools 暴露 web.search
            var hitsBeforeB = pages.Hits;
            var reqsBeforeB = openAi.Requests.Count;
            var sentBeforeB = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg");
            openAi.EnqueueReply("""{"suitability": 92, "reply": "群B看到链接了！"}""");
            await protocol.SendGroupMessageAsync(groupB, 20002, "群友B",
                $"@机器人 看看这个 {pages.PageUrl}?case=beta", 9604, mentionBot: true, ct: cts.Token);
            var sendB = await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(25), skip: sentBeforeB);
            var reqB = openAi.Requests.Count > reqsBeforeB ? openAi.Requests[^1] : null;
            var sysB = reqB is not null ? SystemText(reqB) : string.Empty;
            Check("★ 群 B 注入自身 PersonaAppend 且不串群 A 人设",
                sendB is not null && sysB.Contains("轻松活泼风格。", StringComparison.Ordinal)
                && !sysB.Contains("极简技术助手：结论先行。", StringComparison.Ordinal),
                Truncate(sysB, 300));
            Check("★ 群 B ReadOnlyTools + Search=true 正常暴露 web.search",
                sysB.Contains("web.search", StringComparison.Ordinal));
            Check("★ 群 B 开启 LinkPreview 时入站链接触发真实抓页", pages.Hits > hitsBeforeB, $"hits {hitsBeforeB}->{pages.Hits}");

            // 5) 平台策略显式禁用 LinkPreview：群 B 档案虽设 LinkPreview=true，绝不可复活平台硬上限
            var versionAfterProfiles = save1Node?["currentVersion"]?.GetValue<string>() ?? version;
            var policySave = await SavePlatformLinkDisableZbaAsync(http, zbaRoot, adminToken, revision,
                versionAfterProfiles, generation, qqPolicyTarget);
            Check("★ ZBA 平台策略显式禁用 qq.private 的 LinkPreview 成功",
                policySave.Status == 200 && (JsonNode.Parse(policySave.Body) as JsonObject)?["code"]?.GetValue<int>() == 0,
                $"{policySave.Status} {Truncate(policySave.Body, 240)}");

            var hitsBeforeCeiling = pages.Hits;
            var sentBeforeCeiling = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg");
            openAi.EnqueueReply("""{"suitability": 90, "reply": "平台关了预览但我照常聊。"}""");
            await protocol.SendGroupMessageAsync(groupB, 20002, "群友B",
                $"@机器人 再看 {pages.PageUrl}?case=gamma", 9605, mentionBot: true, ct: cts.Token);
            var sendCeiling = await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(25), skip: sentBeforeCeiling);
            Check("★★ 平台策略显式停用 LinkPreview 后，群 B 档案 Enabled=true 无法复活抓页",
                sendCeiling is not null && pages.Hits == hitsBeforeCeiling,
                $"hits {hitsBeforeCeiling}->{pages.Hits}");
        }

        // 6) 验证 SQLite Schema v15 零迁移（user_version=15，configuration_field_modes 无 153 行，meta 有档案）
        Check("★ SQLite PRAGMA user_version 保持 15（零迁移）",
            DbProbe.Count(dataDir, "PRAGMA user_version;") == 15);
        Check("★ configuration_field_modes 不含字段 153（纯 registered 集合）",
            DbProbe.Count(dataDir, "SELECT COUNT(*) FROM configuration_field_modes WHERE field_id = 153;") == 0);
        var metaPayload = DbProbe.Text(dataDir, "SELECT value FROM meta WHERE key = 'configuration_conversation_profiles';");
        Check("★ 会话档案已持久化至 meta['configuration_conversation_profiles']",
            metaPayload is not null && metaPayload.Contains("group:10001", StringComparison.Ordinal)
            && metaPayload.Contains("group:10002", StringComparison.Ordinal));

        // 7) 进程重启后会话档案仍持久生效
        using var botRestarted = StartBot(BuildEnv());
        await WaitForPortAsync(botWsPort, cts.Token, botRestarted);
        await WaitForPortAsync(panelPort, cts.Token, botRestarted);
        using var protocol2 = new MockProtocol { SelfId = 10009 };
        await protocol2.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol2.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        var afterRestartRead = await ZbaAsync(http, "POST", zbaRoot + "/configuration/read", readToken, """{"kind":1}""");
        var restartedProfiles = (JsonNode.Parse(afterRestartRead.Body) as JsonObject)?["view"]?["registered"]?["conversationProfiles"] as JsonArray;
        Check("★ 重启后 ZBA 读取仍返回 2 条会话档案",
            afterRestartRead.Status == 200 && restartedProfiles?.Count == 2,
            $"count={restartedProfiles?.Count ?? -1}");

        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 90, "reply": "重启后仍在。"}""");
        await protocol2.SendGroupMessageAsync(groupA, 20001, "群友A", "@机器人 重启后确认", 9606, mentionBot: true, ct: cts.Token);
        var restartSend = await protocol2.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(25));
        var restartSys = openAi.Requests.Count > 0 ? SystemText(openAi.Requests[^1]) : string.Empty;
        Check("★ 重启后群 A 会话档案继续生效（PersonaAppend 仍在系统提示词中）",
            restartSend is not null && restartSys.Contains("极简技术助手：结论先行。", StringComparison.Ordinal),
            Truncate(restartSys, 240));
    }

    private static async Task<(string Revision, string Version, string Generation, JsonObject QqTarget)> ReadZbaProfileMetaAsync(
        HttpClient http, string zbaRoot, string readToken)
    {
        var read = await ZbaAsync(http, "POST", zbaRoot + "/configuration/read", readToken, """{"kind":1}""");
        var node = JsonNode.Parse(read.Body) as JsonObject ?? throw new InvalidOperationException("invalid zba read");
        var policies = node["view"]?["platform"]?["platformPolicies"]?["value"] as JsonArray;
        JsonObject? qqTarget = null;
        if (policies is not null)
        {
            foreach (var item in policies.OfType<JsonObject>())
            {
                var target = item["target"] as JsonObject;
                var pid = target?["platformId"]?.GetValue<string>() ?? target?["key"]?["platformId"]?.GetValue<string>();
                if (pid == "qq.private") { qqTarget = target!.DeepClone().AsObject(); break; }
            }
        }
        qqTarget ??= new JsonObject { ["kind"] = "accountDefault", ["platformId"] = "qq.private", ["accountScope"] = "legacy" };
        return (
            node["contractRevision"]?.GetValue<string>() ?? "cfg-0.3.3",
            node["version"]?.GetValue<string>() ?? "0",
            node["generation"]?.GetValue<string>() ?? throw new InvalidOperationException("generation missing"),
            qqTarget);
    }

    private static JsonObject BuildS156ProfilesPatch(long groupA, long groupB)
    {
        static JsonObject Driver() => new() { ["platformId"] = "qq.private", ["accountScope"] = "legacy", ["instanceId"] = "" };
        var keyA = new JsonObject { ["driver"] = Driver(), ["conversationKey"] = $"group:{groupA}" };
        var keyB = new JsonObject { ["driver"] = Driver(), ["conversationKey"] = $"group:{groupB}" };
        return new JsonObject
        {
            ["registered"] = new JsonObject
            {
                ["plugins"] = new JsonArray(),
                ["providers"] = new JsonArray(),
                ["policies"] = new JsonArray(),
                ["conversationProfiles"] = new JsonArray(
                    new JsonObject
                    {
                        ["key"] = keyA.DeepClone(),
                        ["operation"] = 0,
                        ["profile"] = new JsonObject
                        {
                            ["key"] = keyA.DeepClone(),
                            ["displayLabel"] = "协作工作群",
                            ["style"] = new JsonObject
                            {
                                ["personaAppend"] = "极简技术助手：结论先行。",
                                ["filterActionNarration"] = true
                            },
                            ["pacing"] = new JsonObject
                            {
                                ["groupInterjectionProbability"] = 0,
                                ["maxContextMessages"] = 6,
                                ["proactiveEnabled"] = 2
                            },
                            ["capabilities"] = new JsonObject
                            {
                                ["features"] = new JsonArray(
                                    new JsonObject { ["feature"] = 6, ["enabled"] = false },
                                    new JsonObject { ["feature"] = 9, ["enabled"] = false },
                                    new JsonObject { ["feature"] = 10, ["enabled"] = false }),
                                ["toolTier"] = 1
                            }
                        }
                    },
                    new JsonObject
                    {
                        ["key"] = keyB.DeepClone(),
                        ["operation"] = 0,
                        ["profile"] = new JsonObject
                        {
                            ["key"] = keyB.DeepClone(),
                            ["displayLabel"] = "活跃综合群",
                            ["style"] = new JsonObject { ["personaAppend"] = "轻松活泼风格。" },
                            ["pacing"] = new JsonObject
                            {
                                ["groupInterjectionProbability"] = 100,
                                ["groupInterjectionCooldownSeconds"] = 0,
                                ["proactiveEnabled"] = 0
                            },
                            ["capabilities"] = new JsonObject
                            {
                                ["features"] = new JsonArray(
                                    new JsonObject { ["feature"] = 9, ["enabled"] = true },
                                    new JsonObject { ["feature"] = 10, ["enabled"] = true }),
                                ["toolTier"] = 2
                            }
                        }
                    })
            }
        };
    }

    private static Task<(int Status, string Body)> SaveConversationProfilesZbaAsync(
        HttpClient http, string zbaRoot, string adminToken, string revision, string expectedVersion, string generation, JsonObject changes)
    {
        var key = $"cfg1:{generation}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():N}";
        var body = new JsonObject
        {
            ["contractRevision"] = revision,
            ["expectedVersion"] = expectedVersion,
            ["key"] = key,
            ["changes"] = changes,
            ["secrets"] = new JsonArray()
        }.ToJsonString();
        return ZbaAsync(http, "POST", zbaRoot + "/configuration/save", adminToken, body);
    }

    private static async Task VerifyProfileReadPrivacyAsync(HttpClient http, string zbaRoot, string readToken)
    {
        var overview = await ZbaAsync(http, "POST", zbaRoot + "/configuration/read", readToken, """{"kind":0}""");
        var overviewProfiles = (JsonNode.Parse(overview.Body) as JsonObject)?["view"]?["registered"]?["conversationProfiles"] as JsonArray;
        Check("★ Overview 整包响应体隐藏会话档案原始键与备注名（含 runtimeEffects 零泄露）",
            overview.Status == 200 && overviewProfiles?.Count == 2
            && !overview.Body.Contains("协作工作群", StringComparison.Ordinal)
            && !overview.Body.Contains("group:10001", StringComparison.Ordinal)
            && !overview.Body.Contains("group:10002", StringComparison.Ordinal)
            && overviewProfiles.OfType<JsonObject>().All(p => p["displayLabelRaw"] is null),
            Truncate(overview.Body, 240));

        var editable = await ZbaAsync(http, "POST", zbaRoot + "/configuration/read", readToken, """{"kind":1}""");
        var editableProfiles = (JsonNode.Parse(editable.Body) as JsonObject)?["view"]?["registered"]?["conversationProfiles"] as JsonArray;
        Check("★ Editable 读取返回 displayLabelRaw 供真名编辑",
            editable.Status == 200 && editableProfiles?.Count == 2
            && editableProfiles.OfType<JsonObject>().Any(p => p["displayLabelRaw"]?.GetValue<string>() == "协作工作群"),
            Truncate(editable.Body, 240));
    }

    private static Task<(int Status, string Body)> SavePlatformLinkDisableZbaAsync(
        HttpClient http, string zbaRoot, string adminToken, string revision, string expectedVersion, string generation, JsonObject target)
    {
        var key = $"cfg1:{generation}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():N}";
        var body = new JsonObject
        {
            ["contractRevision"] = revision,
            ["expectedVersion"] = expectedVersion,
            ["key"] = key,
            ["changes"] = new JsonObject
            {
                ["registered"] = new JsonObject
                {
                    ["plugins"] = new JsonArray(),
                    ["providers"] = new JsonArray(),
                    ["policies"] = new JsonArray(
                        new JsonObject
                        {
                            ["target"] = target.DeepClone(),
                            ["configuration"] = new JsonObject
                            {
                                ["enabled"] = 1,
                                ["chatEnabled"] = 1,
                                ["groupWhitelist"] = new JsonObject { ["kind"] = 2 },
                                ["privateWhitelist"] = new JsonObject { ["kind"] = 2 },
                                ["allowedActions"] = new JsonObject { ["kind"] = 0, ["actions"] = new JsonArray() },
                                ["features"] = new JsonArray(new JsonObject { ["feature"] = 9, ["enabled"] = false })
                            }
                        }),
                    ["conversationProfiles"] = new JsonArray()
                }
            },
            ["secrets"] = new JsonArray()
        }.ToJsonString();
        return ZbaAsync(http, "POST", zbaRoot + "/configuration/save", adminToken, body);
    }
}
