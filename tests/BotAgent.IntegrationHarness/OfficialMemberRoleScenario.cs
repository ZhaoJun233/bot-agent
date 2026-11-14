using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunOfficialMemberRoleScenarioAsync()
    {
        Section("S68 official scoped role context and revocation through the real host");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var model = new MockOpenAi(FreePort(17870));
        // Distinct synthetic replies keep the existing repetition guard outside this role scenario.
        for (var index = 0; index < 16; index++)
            model.EnqueueReply($$"""{"suitability":100,"reply":"synthetic-s68-reply-{{index}}"}""");
        model.Start();
        using var official = new MockOfficialGateway(FreePort(18170), FreePort(13070)); official.Start();
        var directory = NewDataDir("s68-roles"); var panelPort = FreePort(18171); var botPort = FreePort(13071);
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = directory, ["QQCHAT_API_KEY"] = "sk-mock", ["QQCHAT_BASE_URL"] = model.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model", ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{botPort}", ["QQCHAT_UIN"] = "10001", ["QQCHAT_WHITELIST"] = "66693",
            ["QQCHAT_GROUP_COOLDOWN"] = "0", ["QQCHAT_PRIVATE_COOLDOWN"] = "0", ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0", ["QQCHAT_WEB_SEARCH"] = "0", ["QQCHAT_ENABLE_VOICE"] = "0", ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(), ["QQCHAT_HEALTH_BIND"] = "127.0.0.1", ["QQCHAT_OFFICIAL"] = "1",
            ["QQCHAT_OFFICIAL_APP_ID"] = "100000003", ["QQCHAT_OFFICIAL_APP_SECRET"] = "test-secret-not-real",
            ["QQCHAT_OFFICIAL_API_BASE"] = official.HttpBase, ["QQCHAT_OFFICIAL_TOKEN_URL"] = official.TokenUrl, ["QQCHAT_OFFICIAL_SANDBOX"] = "1",
        });
        await WaitForPortAsync(panelPort, cts.Token, bot);
        Check("S68 official connected", await official.WaitForIdentifyAsync(1, TimeSpan.FromSeconds(15)));
        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        async Task<string> Push(string id, string role, string group = "S68_GROUP_A", string member = "S68_MEMBER", string? timestamp = null)
        {
            var body = OfficialIdentityFixture(id, group, id);
            body["author"] = new JsonObject { ["member_openid"] = member, ["member_role"] = role, ["username"] = "S68 display" };
            if (timestamp is not null) body["timestamp"] = timestamp;
            var replies = official.Messages.Count; var requests = model.Requests.Count;
            await official.PushEventAsync("GROUP_AT_MESSAGE_CREATE", body);
            Check("S68 reply " + id, await official.WaitForMessageAsync(replies, value => value["body"]?["msg_id"]?.GetValue<string>() == id,
                TimeSpan.FromSeconds(12)));
            Check("S68 model request " + id, model.Requests.Count > requests);
            return string.Join("\n", Enumerable.Range(requests, model.Requests.Count - requests).Select(model.DescribeRequest));
        }
        var firstAt = DateTimeOffset.UtcNow.ToString("O");
        var first = await Push("s68-first", "admin", timestamp: firstAt);
        Check("S68 fresh official role reaches actual model context", first.Contains("近期官方群角色") && first.Contains("S68 display")
            && first.Contains("不授予审批或工具权限"));
        var other = await Push("s68-other-group", "member", group: "S68_GROUP_B");
        Check("S68 same raw member in another group has no inherited role", !other.Contains("近期官方群角色"));
        var demoted = await Push("s68-demoted", "member");
        Check("S68 demotion removes held context", !demoted.Contains("近期官方群角色"));
        var late = await Push("s68-late", "owner", timestamp: firstAt);
        Check("S68 old elevated event cannot restore context", !late.Contains("近期官方群角色"));
        var restored = await Push("s68-current", "admin");
        Check("S68 newer valid role can be observed", restored.Contains("近期官方群角色"));
        await official.PushEventAsync("GROUP_MEMBER_REMOVE", new() { ["group_openid"] = "S68_GROUP_A",
            ["member_openid"] = "S68_MEMBER", ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        var afterRemoval = await Push("s68-after-removal", "member", member: "S68_OBSERVER");
        Check("S68 member removal revokes prior sender context", !afterRemoval.Contains("近期官方群角色"));
        Check("S68 official roles never trigger OneBot member lookup", protocol.MemberInfoHits == 0);
        await bot.StopAsync();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(directory, "data", "botagent.db"), Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(cts.Token);
        using var count = connection.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM member_roles";
        Check("S68 official role evidence never enters persistent private role table", Convert.ToInt64(await count.ExecuteScalarAsync(cts.Token)) == 0);
    }
}
