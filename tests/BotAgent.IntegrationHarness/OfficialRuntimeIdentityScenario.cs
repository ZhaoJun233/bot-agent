using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunOfficialRuntimeIdentityScenarioAsync()
    {
        Section("S67 typed official runtime: scope, legacy refusal, storage isolation");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var model = new MockOpenAi(FreePort(17868));
        model.AddRule("s67-", """{"suitability":100,"reply":"synthetic-s67-reply"}""");
        model.Start();
        using var official = new MockOfficialGateway(FreePort(18168), FreePort(13068));
        official.Start();
        var panelPort = FreePort(18169);
        var botPort = FreePort(13069);
        Dictionary<string, string> Env(string directory) => new()
        {
            ["QQCHAT_DATA_DIR"] = directory, ["QQCHAT_API_KEY"] = "sk-mock", ["QQCHAT_BASE_URL"] = model.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model", ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{botPort}", ["QQCHAT_UIN"] = "10001", ["QQCHAT_WHITELIST"] = "66692",
            ["QQCHAT_GROUP_COOLDOWN"] = "0", ["QQCHAT_PRIVATE_COOLDOWN"] = "0", ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0", ["QQCHAT_WEB_SEARCH"] = "0", ["QQCHAT_ENABLE_VOICE"] = "0", ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(), ["QQCHAT_HEALTH_BIND"] = "127.0.0.1", ["QQCHAT_OFFICIAL"] = "1",
            ["QQCHAT_OFFICIAL_APP_ID"] = "100000001", ["QQCHAT_OFFICIAL_APP_SECRET"] = "test-secret-not-real",
            ["QQCHAT_OFFICIAL_API_BASE"] = official.HttpBase, ["QQCHAT_OFFICIAL_TOKEN_URL"] = official.TokenUrl,
            ["QQCHAT_OFFICIAL_SANDBOX"] = "1",
        };
        async Task<bool> Push(bool group, string target, string id, string marker)
        {
            var body = OfficialIdentityFixture(id, target, marker);
            body["author"] = new JsonObject { [group ? "member_openid" : "user_openid"] = group ? "S67_SHARED" : target };
            var before = official.Messages.Count;
            await official.PushEventAsync(group ? "GROUP_AT_MESSAGE_CREATE" : "C2C_MESSAGE_CREATE", body);
            return await official.WaitForMessageAsync(before, value => value["body"]?["msg_id"]?.GetValue<string>() == id,
                TimeSpan.FromSeconds(10));
        }
        static JsonArray Bindings(string dir) => JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "data", "official-ids.json")))?
            ["officialIdentities"]?["bindings"] as JsonArray ?? new();
        static long? Alias(JsonArray bindings, string app, string kind, string target, string raw)
            => bindings.OfType<JsonObject>().FirstOrDefault(row =>
            {
                var key = JsonNode.Parse(row["key"]!.GetValue<string>())!.AsArray();
                return key[1]!.GetValue<string>() == app && key[2]!.GetValue<string>() == kind
                    && key[4]!.GetValue<string>() == target && key[6]!.GetValue<string>() == raw;
            })?["alias"]?.GetValue<long>();
        var directory = NewDataDir("s67-scopes");
        var initialMark = official.Messages.Count;
        long? originalGroup;
        using (var bot = StartBot(Env(directory)))
        {
            await WaitForPortAsync(panelPort, cts.Token, bot);
            Check("S67 official connected", await official.WaitForIdentifyAsync(1, TimeSpan.FromSeconds(15)));
            Check("S67 first group reply", await Push(true, "S67_SHARED", "s67-shared-message", "s67-app-a-only"));
            Check("S67 same raw id in C2C remains distinct", await Push(false, "S67_SHARED", "s67-shared-message", "s67-c2c"));
            Check("S67 same raw id in second group remains distinct", await Push(true, "S67_GROUP_B", "s67-shared-message", "s67-group-b"));
            var duplicate = OfficialIdentityFixture("s67-shared-message", "S67_SHARED", "s67-duplicate-must-not-enter");
            duplicate["author"] = new JsonObject { ["member_openid"] = "S67_SHARED" };
            await official.PushEventAsync("GROUP_AT_MESSAGE_CREATE", duplicate);
            Check("S67 later valid event completes", await Push(true, "S67_SHARED", "s67-next", "s67-next"));
            Check("S67 duplicate did not reach model", !model.Requests.Any(request => request.ToJsonString().Contains("s67-duplicate-must-not-enter", StringComparison.Ordinal)));
            Check("S67 passive counters belong to conversations", official.Messages.Skip(initialMark).Take(3).Count() == 3
                && official.Messages.Skip(initialMark).Take(3).All(value => value["body"]?["msg_seq"]?.GetValue<int>() == 1));
            await WaitUntilAsync(() => Bindings(directory).OfType<JsonObject>().Count(row =>
                JsonNode.Parse(row["key"]!.GetValue<string>())?[5]?.GetValue<string>() == "Outbound") == 4, TimeSpan.FromSeconds(2));
            var bindings = Bindings(directory);
            originalGroup = Alias(bindings, "100000001", "Group", "", "S67_SHARED");
            var c2c = Alias(bindings, "100000001", "C2cUser", "", "S67_SHARED");
            var memberA = Alias(bindings, "100000001", "GroupMember", "S67_SHARED", "S67_SHARED");
            var memberB = Alias(bindings, "100000001", "GroupMember", "S67_GROUP_B", "S67_SHARED");
            Check("S67 live host persisted typed distinct subjects", new[] { originalGroup, c2c, memberA, memberB }.All(value => value > 0)
                && new[] { originalGroup, c2c, memberA, memberB }.Distinct().Count() == 4);
            Check("S67 reply aliases carry outbound direction", bindings.OfType<JsonObject>().Count(row =>
                JsonNode.Parse(row["key"]!.GetValue<string>())?[5]?.GetValue<string>() == "Outbound") == 4);
            var (changedStatus, changedBody) = await PanelPostJsonAsync($"http://127.0.0.1:{panelPort}/api/settings", """{"officialAppId":"100000002"}""");
            var (readStatus, changedSettings) = await PanelGetAsync($"http://127.0.0.1:{panelPort}/api/settings");
            var runtimeApp = JsonNode.Parse(changedSettings)?["runtime"]?["officialAppId"]?.GetValue<string>();
            var result = JsonNode.Parse(changedBody)?["configurationResult"];
            // OfficialAppId is FieldEffect.Restart: commit the new value while preserving the startup snapshot.
            Check("S67 app change commits with startup snapshot retained", changedStatus == 200 && readStatus == 200
                && result?["code"]?.GetValue<int>() == 0 && result?["receipt"]?["commitState"]?.GetValue<int>() == 1
                && result?["receipt"]?["applyState"]?.GetValue<int>() == 3 && runtimeApp == "100000001");
            await bot.StopAsync();
        }
        var modelMark = model.Requests.Count;
        var identifyMark = official.IdentifyCount;
        using (var bot = StartBot(Env(directory)))
        {
            await WaitForPortAsync(panelPort, cts.Token, bot);
            var (readStatus, settings) = await PanelGetAsync($"http://127.0.0.1:{panelPort}/api/settings");
            Check("S67 committed app survives restart with old bootstrap environment", readStatus == 200
                && JsonNode.Parse(settings)?["runtime"]?["officialAppId"]?.GetValue<string>() == "100000002");
            Check("S67 second app connects", await official.WaitForIdentifyAsync(identifyMark + 1, TimeSpan.FromSeconds(15)));
            Check("S67 new runtime requested token for second app", official.HttpRequests.Any(request =>
                request["body"]?["appId"]?.GetValue<string>() == "100000002"));
            Check("S67 second app receives own reply", await Push(true, "S67_SHARED", "s67-app-b-message", "s67-app-b-only"));
            var bindings = Bindings(directory);
            Check("S67 app change preserves old alias and allocates another", originalGroup > 0
                && Alias(bindings, "100000001", "Group", "", "S67_SHARED") == originalGroup
                && Alias(bindings, "100000002", "Group", "", "S67_SHARED") is { } other && other != originalGroup);
            Check("S67 second app does not read first app history", !model.Requests.Skip(modelMark).Any(request =>
                request.ToJsonString().Contains("s67-app-a-only", StringComparison.Ordinal)));
            await bot.StopAsync();
        }
        var legacy = NewDataDir("s67-legacy");
        Directory.CreateDirectory(Path.Combine(legacy, "data"));
        var legacyPath = Path.Combine(legacy, "data", "official-ids.json");
        File.WriteAllText(legacyPath, """{"aliasBase":8000000000000000,"map":{"S67_LEGACY":8000000000000017}}""");
        identifyMark = official.IdentifyCount;
        using (var bot = StartBot(Env(legacy)))
        {
            await WaitForPortAsync(panelPort, cts.Token, bot);
            await official.WaitForIdentifyAsync(identifyMark + 1, TimeSpan.FromSeconds(15));
            var rejected = OfficialIdentityFixture("s67-legacy-message", "S67_LEGACY", "s67-legacy-must-not-enter");
            rejected["author"] = new JsonObject { ["member_openid"] = "S67_MEMBER" };
            await official.PushEventAsync("GROUP_AT_MESSAGE_CREATE", rejected);
            Check("S67 unrelated new identity remains usable", await Push(true, "S67_NEW_GROUP", "s67-new-message", "s67-new"));
            Check("S67 unclaimed legacy never enters model", !model.Requests.Any(request => request.ToJsonString().Contains("s67-legacy-must-not-enter", StringComparison.Ordinal)));
            Check("S67 legacy refusal has fixed reason", bot.OutputLines.Any(line => line.Contains("identity_migration_required", StringComparison.Ordinal)));
            Check("S67 old alias retained without attribution", JsonNode.Parse(File.ReadAllText(legacyPath))?["map"]?["S67_LEGACY"]?.GetValue<long>() == 8000000000000017
                && !Bindings(legacy).Any(row => row?["alias"]?.GetValue<long>() == 8000000000000017));
            await bot.StopAsync();
        }
        foreach (var broken in new[] { "corrupt", "directory" })
        {
            var dir = NewDataDir("s67-" + broken);
            var map = Path.Combine(dir, "data", "official-ids.json");
            Directory.CreateDirectory(Path.GetDirectoryName(map)!);
            if (broken == "corrupt") File.WriteAllText(map, "{synthetic-corrupt"); else Directory.CreateDirectory(map);
            var tokenMark = official.TokenRequests;
            using var bot = StartBot(Env(dir));
            try { await WaitForPortAsync(panelPort, cts.Token, bot); }
            catch (InvalidOperationException) { Check("S67 bad map preserves other channels " + broken, false); continue; }
            using var protocol = new MockProtocol { SelfId = 10001 };
            await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botPort}", cts.Token);
            await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
            await protocol.SendGroupMessageAsync(66692, 20001, "synthetic-member", "s67-private-continues", 9567, mentionBot: true, ct: cts.Token);
            var replied = await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(12));
            Check("S67 bad map preserves private reply " + broken, replied is not null && !bot.HasExited);
            Check("S67 bad map emits fixed reason without connecting " + broken, official.TokenRequests == tokenMark
                && bot.OutputLines.Any(line => line.Contains("identity_store_unavailable", StringComparison.Ordinal)));
            Check("S67 bad map preserved " + broken, broken == "directory" ? Directory.Exists(map) : File.ReadAllText(map) == "{synthetic-corrupt");
            await bot.StopAsync();
        }
    }
}
