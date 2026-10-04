using System.Collections;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunPlatformSwitchScenarioAsync()
    {
        Section("S52 Unified platform switches: migration, precedence, mute, disable, persistence");

        var openAiPort = FreePort(17952);
        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        var botWsPort = FreePort(13252);
        var panelPort = FreePort(18252);
        var panel = $"http://127.0.0.1:{panelPort}";
        const string panelToken = "it-s52-synthetic-token";
        var dataDir = NewDataDir("s52");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        JsonObject Policy(string platform, bool enabled, bool chat) => new()
        {
            ["PlatformId"] = platform,
            ["AccountScope"] = platform == "feishu" ? "default" : "legacy",
            ["Enabled"] = enabled,
            ["ChatEnabled"] = chat,
            ["GroupWhitelist"] = platform == "local" ? "101" : string.Empty,
        };

        JsonObject Policies(bool qqEnabled, bool qqChat, bool officialEnabled, bool officialChat,
            bool feishuEnabled, bool feishuChat, bool localEnabled, bool localChat) => new()
        {
            ["platformPolicies"] = new JsonArray(
                Policy("qq.private", qqEnabled, qqChat), Policy("qq.official", officialEnabled, officialChat),
                Policy("feishu", feishuEnabled, feishuChat), Policy("local", localEnabled, localChat)),
        };

        JsonObject LegacyPolicy(string platform, bool enabled, bool chat) => new()
        {
            ["PlatformId"] = platform,
            ["AccountScope"] = platform == "feishu" ? "default" : "legacy",
            ["Enabled"] = enabled,
            ["ChatEnabled"] = chat,
        };

        // This fixture belongs to this new temp directory only; it contains no conversations or credentials.
        Directory.CreateDirectory(Path.Combine(dataDir, "data"));
        var oldSettings = new JsonObject
        {
            ["PlatformSwitchSchemaVersion"] = 0,
            ["LocalChannelIds"] = "101",
            ["OfficialEnabled"] = false,
            ["OfficialChatEnabled"] = true,
            ["PrivateChatEnabled"] = true,
            ["FeishuEnabled"] = false,
            ["IdleFallbackSeconds"] = 0,
            ["EnableStickers"] = false,
            ["EnablePoke"] = false,
            ["EnableVoice"] = false,
            ["EnableMusic"] = false,
            ["GroupCooldownSeconds"] = 0,
            ["SplitReplies"] = false,
            ["PlatformPolicies"] = new JsonArray(
                LegacyPolicy("qq.private", true, false), LegacyPolicy("qq.official", true, true), LegacyPolicy("feishu", true, true)),
        };
        File.WriteAllText(Path.Combine(dataDir, "data", "settings.json"), oldSettings.ToJsonString());

        var env = new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["BOTAGENT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_HEALTH_BIND"] = "127.0.0.1",
            ["QQCHAT_PANEL_TOKEN"] = panelToken,
            ["QQCHAT_LOG_FILE"] = "0",
        };
        // StartBot clears QQCHAT_* but not their newer aliases (including *_FILE secrets).
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (key is not null && key.StartsWith("BOTAGENT_", StringComparison.OrdinalIgnoreCase)
                && !env.ContainsKey(key))
                env[key] = string.Empty;
        }

        static bool? Flag(JsonNode? node, string field)
            => node?[field] is JsonValue value && value.TryGetValue<bool>(out var result) ? result : null;

        static JsonObject? Row(JsonArray? rows, string platform)
            => rows?.OfType<JsonObject>().SingleOrDefault(row =>
                row["platformId"]?.GetValue<string>() == platform
                && row["accountScope"]?.GetValue<string>() == (platform == "feishu" ? "default" : "legacy"));

        static bool Switches(JsonArray? rows, string platform, bool enabled, bool chat)
        {
            var row = rows?.OfType<JsonObject>().SingleOrDefault(item =>
                item["PlatformId"]?.GetValue<string>() == platform
                && item["AccountScope"]?.GetValue<string>() == (platform == "feishu" ? "default" : "legacy"));
            return Flag(row, "Enabled") == enabled && Flag(row, "ChatEnabled") == chat;
        }

        static JsonObject RuntimeOnly(string body)
        {
            var root = JsonNode.Parse(body) as JsonObject;
            var source = root?["runtime"] as JsonObject;
            var safe = new JsonObject { ["dataDir"] = root?["env"]?["dataDir"]?.DeepClone() };
            foreach (var field in new[] { "officialEnabled", "officialChatEnabled", "privateChatEnabled", "feishuEnabled",
                "platformPolicies", "localChannelIds", "officialSecretConfigured", "feishuSecretConfigured" })
                safe[field] = source?[field]?.DeepClone();
            return safe;
        }

        async Task<JsonObject> SaveAsync(string phase, JsonObject payload)
        {
            var (code, body) = await PanelPostJsonAsync($"{panel}/api/settings", payload.ToJsonString(), panelToken);
            Check($"{phase}: settings save accepted", code == 200, $"HTTP {code}");
            return code == 200 ? RuntimeOnly(body) : new JsonObject();
        }

        async Task<JsonObject> ReadRuntimeAsync(string phase)
        {
            var (code, body) = await PanelGetAsync($"{panel}/api/settings", panelToken);
            Check($"{phase}: settings read accepted", code == 200, $"HTTP {code}");
            return code == 200 ? RuntimeOnly(body) : new JsonObject();
        }

        async Task<JsonArray?> ReadPlatformsAsync(string phase)
        {
            var (code, body) = await PanelGetAsync($"{panel}/api/platforms", panelToken);
            var rows = code == 200 ? (JsonNode.Parse(body) as JsonObject)?["platforms"] as JsonArray : null;
            Check($"{phase}: standard platform rows expose switch/status booleans", code == 200 && rows?.Count == 4
                && new[] { "qq.private", "qq.official", "feishu", "local" }.All(id =>
                    Row(rows, id) is JsonObject row && new[] { "enabled", "configuredChatEnabled", "chatEnabled",
                        "connected", "restartRequired" }.All(field => Flag(row, field).HasValue)),
                $"HTTP {code}; rows={rows?.Count ?? 0}");
            return rows;
        }

        void AssertPlatform(JsonArray? rows, string phase, string platform, bool enabled, bool chat, bool restart)
        {
            var row = Row(rows, platform);
            Check($"{phase}: {platform} raw/effective switches and restart", Flag(row, "enabled") == enabled
                && Flag(row, "configuredChatEnabled") == chat && Flag(row, "chatEnabled") == (enabled && chat)
                && Flag(row, "restartRequired") == restart,
                $"enabled={Flag(row, "enabled")}; configuredChat={Flag(row, "configuredChatEnabled")}; "
                + $"effectiveChat={Flag(row, "chatEnabled")}; restart={Flag(row, "restartRequired")}");
        }

        void AssertRuntime(JsonObject runtime, string phase, bool qqEnabled, bool qqChat, bool officialEnabled,
            bool officialChat, bool feishuEnabled, bool feishuChat, bool localEnabled, bool localChat)
        {
            var rows = runtime["platformPolicies"] as JsonArray;
            Check($"{phase}: settings project all four canonical switches", rows?.Count == 4
                && Switches(rows, "qq.private", qqEnabled, qqChat)
                && Switches(rows, "qq.official", officialEnabled, officialChat)
                && Switches(rows, "feishu", feishuEnabled, feishuChat)
                && Switches(rows, "local", localEnabled, localChat), $"policyRows={rows?.Count ?? 0}");
            Check($"{phase}: canonical switches mirror legacy runtime flags",
                Flag(runtime, "privateChatEnabled") == qqChat && Flag(runtime, "officialEnabled") == officialEnabled
                && Flag(runtime, "officialChatEnabled") == officialChat && Flag(runtime, "feishuEnabled") == feishuEnabled,
                $"privateChat={Flag(runtime, "privateChatEnabled")}; official={Flag(runtime, "officialEnabled")}; "
                + $"officialChat={Flag(runtime, "officialChatEnabled")}; feishu={Flag(runtime, "feishuEnabled")}");
            Check($"{phase}: synthetic local roster retained", runtime["localChannelIds"]?.GetValue<string>() == "101",
                "roster equality only");
            Check($"{phase}: runtime uses only the synthetic temp root",
                runtime["dataDir"]?.GetValue<string>() == dataDir, "temp root equality only");
            Check($"{phase}: optional adapter credentials remain absent",
                Flag(runtime, "officialSecretConfigured") == false && Flag(runtime, "feishuSecretConfigured") == false,
                "credential configured flags only");
        }

        async Task<int> LocalOutboxAsync(string phase, bool enabled, bool available = true)
        {
            var (code, body) = await PanelGetAsync($"{panel}/api/local", panelToken);
            var local = code == 200 ? JsonNode.Parse(body) as JsonObject : null;
            var outbox = local?["outbox"] as JsonArray;
            Check($"{phase}: local availability and injection gate", code == 200
                && Flag(local, "available") == available && Flag(local, "enabled") == enabled
                && Flag(local, "tokenConfigured") == true && Flag(local, "canInject") == enabled,
                $"HTTP {code}; available={Flag(local, "available")}; enabled={Flag(local, "enabled")}; "
                + $"canInject={Flag(local, "canInject")}");
            Check($"{phase}: local outbox contains metadata only", outbox is not null
                && outbox.OfType<JsonObject>().Count() == outbox.Count
                && outbox.OfType<JsonObject>().All(item => item.Count == 4 && item["id"] is JsonValue
                    && item["key"] is JsonValue && item["length"] is JsonValue && item["sentAt"] is JsonValue),
                $"outboxCount={outbox?.Count ?? 0}");
            return outbox?.Count ?? 0;
        }

        async Task InjectAsync(string phase, int expected)
        {
            var (code, body) = await PanelPostJsonAsync($"{panel}/api/local/message", new JsonObject
            {
                ["id"] = 101, ["text"] = "synthetic platform switch probe", ["sender"] = "synthetic-member",
                ["isGroup"] = true, ["mentioned"] = true,
            }.ToJsonString(), panelToken);
            var receipt = JsonNode.Parse(body) as JsonObject;
            Check($"{phase}: local injection returns {expected}", code == expected
                && (expected == 202 ? Flag(receipt, "accepted") == true
                    : receipt?["reason"]?.GetValue<string>() == "local_channel_disabled"), $"HTTP {code}");
        }

        async Task StartReadyAsync(BotProcess bot)
        {
            // Do not include BotProcess.Diagnostics in failures: even synthetic message logs stay private.
            await WaitForPortAsync(botWsPort, cts.Token);
            await WaitForPortAsync(panelPort, cts.Token);
            Check("synthetic bot is running", !bot.HasExited, $"exited={bot.HasExited}");
        }

        try
        {
            using (var bot = StartBot(env))
            {
                await StartReadyAsync(bot);
                var migrated = await ReadRuntimeAsync("pre-v1 migration");
                AssertRuntime(migrated, "pre-v1 AND migration", true, false, false, true, false, false, true, true);
                var initialRows = await ReadPlatformsAsync("initial projection");
                AssertPlatform(initialRows, "migrated", "qq.official", false, true, false);
                AssertPlatform(initialRows, "migrated", "feishu", false, false, false);
                await LocalOutboxAsync("initial", true);
                var legacyFeishuOn = await SaveAsync("legacy Feishu enable after migration", new JsonObject { ["feishuEnabled"] = true });
                Check("legacy Feishu single switch enables both platform and chat after migration",
                    Switches(legacyFeishuOn["platformPolicies"] as JsonArray, "feishu", true, true));
                var legacyFeishuOff = await SaveAsync("legacy Feishu disable", new JsonObject { ["feishuEnabled"] = false });
                Check("legacy Feishu single switch disables both platform and chat",
                    Switches(legacyFeishuOff["platformPolicies"] as JsonArray, "feishu", false, false));

                var allOn = await SaveAsync("canonical enable", Policies(true, true, true, true, true, true, true, true));
                AssertRuntime(allOn, "canonical enable", true, true, true, true, true, true, true, true);
                var enabledRows = await ReadPlatformsAsync("canonical enable");
                AssertPlatform(enabledRows, "canonical enable", "local", true, true, false);
                AssertPlatform(enabledRows, "credential-free optional", "qq.official", true, true, true);
                AssertPlatform(enabledRows, "credential-free optional", "feishu", true, true, true);
                Check("optional adapters stay disconnected without credentials",
                    Flag(Row(enabledRows, "qq.official"), "connected") == false
                    && Flag(Row(enabledRows, "feishu"), "connected") == false,
                    "connected=false for both optional adapters");

                openAi.EnqueueReply("{\"suitability\":99,\"reply\":\"synthetic reply\"}");
                await InjectAsync("enabled chat positive control", 202);
                Check("enabled local chat reaches mock model", await WaitUntilAsync(() => openAi.Requests.Count > 0,
                    TimeSpan.FromSeconds(10)), $"modelRequests={openAi.Requests.Count}");
                var outboxReady = false;
                for (var i = 0; i < 40 && !outboxReady; i++)
                {
                    var (code, body) = await PanelGetAsync($"{panel}/api/local", panelToken);
                    outboxReady = code == 200 && (JsonNode.Parse(body)?["outbox"] as JsonArray)?.Count > 0;
                    if (!outboxReady) await Task.Delay(150, cts.Token);
                }
                Check("positive control reply drained to local outbox", outboxReady, "outboxCount > 0");
                var beforeMute = await LocalOutboxAsync("positive control", true);

                await SaveAsync("local mute", Policies(true, true, true, true, true, true, true, false));
                AssertPlatform(await ReadPlatformsAsync("local mute"), "local mute", "local", true, false, false);
                await LocalOutboxAsync("local mute", true);
                openAi.ClearRequests();
                await InjectAsync("muted chat still accepts injection", 202);
                await Task.Delay(2500, cts.Token);
                Check("muted local chat never requests model", openAi.Requests.Count == 0,
                    $"modelRequests={openAi.Requests.Count}");
                Check("muted local chat sends no reply", await LocalOutboxAsync("muted after injection", true) == beforeMute,
                    $"expectedOutboxCount={beforeMute}");

                await SaveAsync("local disabled with chat preference", Policies(true, true, true, true, true, true, false, true));
                AssertPlatform(await ReadPlatformsAsync("local disabled"), "local disabled", "local", false, true, false);
                await LocalOutboxAsync("local disabled", false);
                await InjectAsync("disabled local rejects injection", 403);
                Check("disabled local injection never requests model", openAi.Requests.Count == 0,
                    $"modelRequests={openAi.Requests.Count}");

                var localRosterOn = await SaveAsync("legacy local roster re-enables channel",
                    new JsonObject { ["localChannelIds"] = "101" });
                Check("legacy local roster enables both platform and chat",
                    Switches(localRosterOn["platformPolicies"] as JsonArray, "local", true, true), "switch equality only");
                AssertPlatform(await ReadPlatformsAsync("legacy local roster on"), "legacy local roster on", "local", true, true, false);
                await LocalOutboxAsync("legacy local roster on", true);

                var localRosterOff = await SaveAsync("legacy local empty roster disables channel",
                    new JsonObject { ["localChannelIds"] = string.Empty });
                Check("legacy empty local roster disables both platform and chat",
                    Switches(localRosterOff["platformPolicies"] as JsonArray, "local", false, false)
                    && localRosterOff["localChannelIds"]?.GetValue<string>() == string.Empty, "switch/roster equality only");
                AssertPlatform(await ReadPlatformsAsync("legacy local roster off"), "legacy local roster off", "local", false, false, false);
                await LocalOutboxAsync("legacy local roster off", false);
                await InjectAsync("legacy empty local roster rejects injection", 403);

                var localRosterRestored = await SaveAsync("legacy local roster restore",
                    new JsonObject { ["localChannelIds"] = "101" });
                Check("legacy restored local roster enables both platform and chat",
                    Switches(localRosterRestored["platformPolicies"] as JsonArray, "local", true, true)
                    && localRosterRestored["localChannelIds"]?.GetValue<string>() == "101", "switch/roster equality only");
                await LocalOutboxAsync("legacy local roster restored", true);

                var conflict = Policies(true, true, true, true, true, true, true, true);
                conflict["officialEnabled"] = false;
                conflict["officialChatEnabled"] = false;
                conflict["privateChatEnabled"] = false;
                conflict["feishuEnabled"] = false;
                conflict["localChannelIds"] = string.Empty;
                AssertRuntime(await SaveAsync("canonical wins over legacy false", conflict), "canonical wins over legacy false",
                    true, true, true, true, true, true, true, true);

                conflict = Policies(false, true, false, true, false, true, true, false);
                conflict["officialEnabled"] = true;
                conflict["officialChatEnabled"] = false;
                conflict["privateChatEnabled"] = false;
                conflict["feishuEnabled"] = true;
                conflict["localChannelIds"] = "101";
                AssertRuntime(await SaveAsync("canonical wins with platform off", conflict), "canonical wins with platform off",
                    false, true, false, true, false, true, true, false);
                var disabledRows = await ReadPlatformsAsync("disabled preferences");
                foreach (var platform in new[] { "qq.private", "qq.official", "feishu" })
                    AssertPlatform(disabledRows, "disabled preferences", platform, false, true, false);

                var legacy = new JsonObject
                {
                    ["officialEnabled"] = true, ["officialChatEnabled"] = false,
                    ["privateChatEnabled"] = false, ["feishuEnabled"] = true,
                };
                AssertRuntime(await SaveAsync("legacy-only update", legacy), "legacy-only update",
                    false, false, true, false, true, true, true, false);
                legacy = new JsonObject
                {
                    ["officialEnabled"] = false, ["officialChatEnabled"] = true,
                    ["privateChatEnabled"] = true, ["feishuEnabled"] = false,
                };
                AssertRuntime(await SaveAsync("legacy-only reverse", legacy), "legacy-only reverse",
                    false, true, false, true, false, false, true, false);
                AssertRuntime(await ReadRuntimeAsync("persisted before restart"), "persisted before restart",
                    false, true, false, true, false, false, true, false);
                await bot.StopAsync();
            }

            using (var restarted = StartBot(env))
            {
                await StartReadyAsync(restarted);
                AssertRuntime(await ReadRuntimeAsync("restart"), "restart persistence",
                    false, true, false, true, false, false, true, false);
                var rows = await ReadPlatformsAsync("restart");
                foreach (var platform in new[] { "qq.private", "qq.official" })
                    AssertPlatform(rows, "restart", platform, false, true, false);
                AssertPlatform(rows, "restart", "feishu", false, false, false);
                AssertPlatform(rows, "restart", "local", true, false, false);
                var before = await LocalOutboxAsync("restart muted", true);
                openAi.ClearRequests();
                await InjectAsync("restart preserves local mute", 202);
                await Task.Delay(2500, cts.Token);
                Check("restart muted chat never requests model", openAi.Requests.Count == 0,
                    $"modelRequests={openAi.Requests.Count}");
                Check("restart muted chat sends no reply", await LocalOutboxAsync("restart after injection", true) == before,
                    $"expectedOutboxCount={before}");
                await restarted.StopAsync();
            }
        }
        catch (Exception ex)
        {
            Fail("s52 synthetic platform regression", $"exceptionType={ex.GetType().Name}; no payload/log diagnostics emitted");
        }
    }
}
