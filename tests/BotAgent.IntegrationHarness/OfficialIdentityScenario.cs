using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunOfficialIdentityScenarioAsync()
    {
        Section("S79 官方主体拒绝：畸形身份无副作用，有效重传可继续");
        var modelPort = FreePort(17866);
        var botPort = FreePort(13066);
        var panelPort = FreePort(18166);
        var dataDir = NewDataDir("s79");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var model = new MockOpenAi(modelPort);
        model.AddRule("s79-positive", """{"suitability":100,"reply":"positive-s79"}""");
        model.Start();
        using var official = new MockOfficialGateway(FreePort(18167), FreePort(13067)) { BotMemberOpenId = "S79_BOT" };
        official.Start();
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = model.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{botPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = "1",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_WEB_SEARCH"] = "0",
            ["QQCHAT_ENABLE_VOICE"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_HEALTH_BIND"] = "127.0.0.1",
            ["QQCHAT_OFFICIAL"] = "1",
            ["QQCHAT_OFFICIAL_APP_ID"] = "100000001",
            ["QQCHAT_OFFICIAL_APP_SECRET"] = "test-secret-not-real",
            ["QQCHAT_OFFICIAL_API_BASE"] = official.HttpBase,
            ["QQCHAT_OFFICIAL_TOKEN_URL"] = official.TokenUrl,
            ["QQCHAT_OFFICIAL_SANDBOX"] = "1",
        });
        await WaitForPortAsync(panelPort, cts.Token, bot);
        var identified = await official.WaitForIdentifyAsync(1, TimeSpan.FromSeconds(15));
        Check("★ S79 合成网关已连接", identified, "identify=" + official.IdentifyCount);
        if (!identified) return;

        var invalidAuthors = InvalidOfficialAuthors();
        string[] eventTypes = ["GROUP_AT_MESSAGE_CREATE", "GROUP_MESSAGE_CREATE", "C2C_MESSAGE_CREATE"];
        var negativeIds = new HashSet<string>(StringComparer.Ordinal);
        var negativeGroups = new HashSet<string>(StringComparer.Ordinal);
        var controls = new List<(string Type, JsonObject Body)>();
        foreach (var type in eventTypes)
        {
            var field = type == "C2C_MESSAGE_CREATE" ? "user_openid" : "member_openid";
            foreach (var (name, author) in invalidAuthors)
            {
                var index = negativeIds.Count;
                var id = $"s79-invalid-{index}";
                var group = $"GROUP_INVALID_S79_{index}";
                negativeIds.Add(id);
                if (type != "C2C_MESSAGE_CREATE") negativeGroups.Add(group);
                var body = OfficialIdentityFixture(id, group, "s79-invalid-body");
                if (name != "missing-author") body["author"] = author(field);
                await official.PushEventAsync(type, body);
                if (name == "missing-author")
                {
                    var control = OfficialIdentityFixture(id, group, "s79-positive");
                    control["author"] = new JsonObject { [field] = $"S79_VALID_{field}_{index}" };
                    controls.Add((type, control));
                }
            }
        }

        var received = await WaitUntilAsync(() => bot.OutputLines.Count(line =>
            eventTypes.Any(type => line.Contains("官方事件：" + type, StringComparison.Ordinal))) >= negativeIds.Count,
            TimeSpan.FromSeconds(10));
        Check("★ S79 全部畸形事件经过同一真实 WS 接收链", received, "fixtures=" + negativeIds.Count);
        var invalidCount = negativeIds.Count;

        // 等待旧映射的保存节流窗口，再用同一连接的有效事件强制形成可读取的持久化检查点。
        await Task.Delay(1100, cts.Token);
        foreach (var (type, body) in controls)
        {
            var id = body["id"]!.GetValue<string>();
            var before = official.Messages.Count;
            await official.PushEventAsync(type, body);
            var replied = await official.WaitForMessageAsync(before,
                message => message["body"]?["msg_id"]?.GetValue<string>() == id,
                TimeSpan.FromSeconds(12));
            Check("★ S79 有效重传未被畸形前件占用去重键 " + type, replied, "type=" + type);
            negativeIds.Remove(id);
            negativeGroups.Remove(body["group_openid"]!.GetValue<string>());
        }

        var rejections = bot.OutputLines.Count(line => line.Contains("reason=identity_missing", StringComparison.Ordinal));
        Check("★ S79 每条无效主体都明确拒绝", rejections == invalidCount, $"rejected={rejections}");
        var invalidModelCalls = model.Requests.Count(request =>
            request.ToJsonString().Contains("s79-invalid-body", StringComparison.Ordinal));
        Check("★ S79 无效主体没有进入模型或污染有效上下文", invalidModelCalls == 0, "calls=" + invalidModelCalls);
        var allowedReferences = controls.Select(control => control.Body["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Check("★ S79 官方发送只对应有效控制消息", official.Messages.Count == controls.Count
            && official.Messages.All(message => allowedReferences.Contains(message["body"]?["msg_id"]?.GetValue<string>() ?? "")),
            "sends=" + official.Messages.Count);
        Check("★ S79 畸形事件没有导致连接重建", official.IdentifyCount == 1 && official.ResumeCount == 0 && official.IsSocketOpen,
            $"identify={official.IdentifyCount}, resume={official.ResumeCount}");

        var path = Path.Combine(dataDir, "data", "official-ids.json");
        var snapshot = File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path, cts.Token)) : null;
        var map = snapshot?["map"] as JsonObject;
        var bindings = snapshot?["officialIdentities"]?["bindings"] as JsonArray ?? new();
        var typedRawIds = bindings.Select(row =>
            JsonNode.Parse(row!["key"]!.GetValue<string>())![6]!.GetValue<string>());
        var invalidAliases = map is null ? -1 : map.Select(pair => pair.Key).Concat(typedRawIds).Count(raw =>
            negativeIds.Contains(raw) || negativeGroups.Contains(raw)
            || raw is "42" or "false" or "" or " " or " USER_INVALID" or "USER INVALID" or "USER\nINVALID");
        Check("★ S79 无效主体未分配可持久引用的别名", map is { Count: > 0 } && invalidAliases == 0,
            "invalid_aliases=" + invalidAliases);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDir, "data", "botagent.db"),
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        await connection.OpenAsync(cts.Token);
        var expectedSources = controls.Select(control =>
        {
            var isGroup = control.Type != "C2C_MESSAGE_CREATE";
            var native = isGroup ? control.Body["group_openid"]!.GetValue<string>()
                : control.Body["author"]!["user_openid"]!.GetValue<string>();
            var binding = bindings.Single(row =>
            {
                var key = JsonNode.Parse(row!["key"]!.GetValue<string>())!;
                return key[1]!.GetValue<string>() == "100000001"
                    && key[2]!.GetValue<string>() == (isGroup ? "Group" : "C2cUser")
                    && key[6]!.GetValue<string>() == native;
            });
            return $"official:{(isGroup ? "group" : "private")}:{binding!["alias"]!.GetValue<long>()}";
        }).ToArray();
        // A protocol send receipt can precede the registry's 150ms background save.
        // Wait for these exact valid controls, then retain the total-count assertion to catch extra rows.
        using var persistedControls = connection.CreateCommand();
        var parameters = expectedSources.Select((_, index) => "$control" + index).ToArray();
        persistedControls.CommandText = "SELECT COUNT(*) FROM conversations WHERE source_key IN ("
            + string.Join(",", parameters) + ")";
        for (var index = 0; index < expectedSources.Length; index++)
            persistedControls.Parameters.AddWithValue(parameters[index], expectedSources[index]);
        var saved = await WaitUntilAsync(() => Convert.ToInt64(persistedControls.ExecuteScalar()) == controls.Count,
            TimeSpan.FromSeconds(5));
        Check("★ S79 有效控制会话完成后台持久化", saved);
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM conversations WHERE source_key LIKE 'official:%'";
        var conversationCount = Convert.ToInt64(await count.ExecuteScalarAsync(cts.Token));
        Check("★ S79 只为有效控制消息创建官方会话", conversationCount == controls.Count,
            "conversations=" + conversationCount);
    }

    private static JsonObject OfficialIdentityFixture(string id, string group, string content) => new()
    {
        ["id"] = id,
        ["group_openid"] = group,
        ["content"] = content,
        ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        ["mentions"] = new JsonArray(new JsonObject { ["member_openid"] = "S79_BOT", ["is_you"] = true }),
    };

    private static (string Name, Func<string, JsonNode?> Author)[] InvalidOfficialAuthors() =>
    [
        ("missing-author", _ => null),
        ("null-author", _ => null),
        ("number-author", _ => JsonValue.Create(42)),
        ("array-author", _ => new JsonArray()),
        ("string-author", _ => JsonValue.Create("USER_INVALID")),
        ("missing-id", _ => new JsonObject()),
        ("null-id", field => new JsonObject { [field] = null }),
        ("empty-id", field => new JsonObject { [field] = "" }),
        ("blank-id", field => new JsonObject { [field] = " " }),
        ("leading-space-id", field => new JsonObject { [field] = " USER_INVALID" }),
        ("internal-space-id", field => new JsonObject { [field] = "USER INVALID" }),
        ("control-id", field => new JsonObject { [field] = "USER\nINVALID" }),
        ("number-id", field => new JsonObject { [field] = 42 }),
        ("bool-id", field => new JsonObject { [field] = false }),
        ("array-id", field => new JsonObject { [field] = new JsonArray() }),
        ("object-id", field => new JsonObject { [field] = new JsonObject() }),
    ];
}
