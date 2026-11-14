using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunOfficialMessageDedupScenarioAsync()
    {
        Section("S73 composite official message identity through the real host");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var model = new MockOpenAi(FreePort(17884));
        for (var i = 0; i < 16; i++) model.EnqueueReply(new JsonObject
            { ["suitability"] = 100, ["reply"] = "S73 distinct synthetic response " + i }.ToJsonString());
        model.Start();
        using var official = new MockOfficialGateway(FreePort(18184), FreePort(13084)); official.Start();
        var directory = NewDataDir("s73-dedup"); var panelPort = FreePort(18185); var botPort = FreePort(13085);
        var environment = new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = directory, ["QQCHAT_API_KEY"] = "sk-mock", ["QQCHAT_BASE_URL"] = model.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model", ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{botPort}", ["QQCHAT_UIN"] = "10001", ["QQCHAT_WHITELIST"] = "66698",
            ["QQCHAT_GROUP_COOLDOWN"] = "0", ["QQCHAT_PRIVATE_COOLDOWN"] = "0", ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0", ["QQCHAT_WEB_SEARCH"] = "0", ["QQCHAT_ENABLE_VOICE"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0", ["QQCHAT_REPLY_PACING"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(), ["QQCHAT_HEALTH_BIND"] = "127.0.0.1", ["QQCHAT_OFFICIAL"] = "1",
            ["QQCHAT_OFFICIAL_APP_ID"] = "100000008", ["QQCHAT_OFFICIAL_APP_SECRET"] = "test-secret-not-real",
            ["QQCHAT_OFFICIAL_API_BASE"] = official.HttpBase, ["QQCHAT_OFFICIAL_TOKEN_URL"] = official.TokenUrl, ["QQCHAT_OFFICIAL_SANDBOX"] = "1",
        };
        Task Push(string id, string index, string user = "S73_USER") => official.PushEventAsync("C2C_MESSAGE_CREATE", new()
        {
            ["id"] = id, ["author"] = new JsonObject { ["user_openid"] = user },
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"), ["content"] = "S73 input " + id + " " + index,
            ["message_scene"] = new JsonObject { ["ext"] = new JsonArray("msg_idx=" + index) },
        });
        int SharedSends() => official.Messages.Count(m => m["body"]?["msg_id"]?.GetValue<string>() == "s73-shared");
        using var bot = StartBot(environment); await WaitForPortAsync(panelPort, cts.Token, bot);
        Check("S73 connected", await official.WaitForIdentifyAsync(1, TimeSpan.FromSeconds(15)));
        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await Push("s73-shared", "IDX_one");
        Check("S73 first indexed response", await WaitUntilAsync(() => SharedSends() == 1, TimeSpan.FromSeconds(15)));
        await Push("s73-shared", "IDX_two");
        Check("S73 same credential distinct index reaches model and transport", await WaitUntilAsync(() => SharedSends() == 2, TimeSpan.FromSeconds(15)));
        Check("S73 two distinct messages entered the model", model.Requests.Count == 2);
        await Push("s73-shared", "IDX_two"); await Push("s73-shared", "IDX_three");
        Check("S73 fresh index completes behind duplicate", await WaitUntilAsync(() => SharedSends() == 3, TimeSpan.FromSeconds(15)));
        Check("S73 duplicate never reaches model", model.Requests.Count == 3);
        await Push("s73-invalid", ""); await Push("s73-control", "IDX_control");
        Check("S73 valid control completes behind malformed index", await WaitUntilAsync(() => official.Messages.Any(m =>
            m["body"]?["msg_id"]?.GetValue<string>() == "s73-control"), TimeSpan.FromSeconds(15)));
        Check("S73 malformed index never reaches model or transport", model.Requests.Count == 4 && official.Messages.Count == 4);
        await bot.StopAsync();

        var requestsBefore = model.Requests.Count; var identifiesBefore = official.IdentifyCount;
        using var restarted = StartBot(environment); await WaitForPortAsync(panelPort, cts.Token, restarted);
        Check("S73 restarted host connected", await official.WaitForIdentifyAsync(identifiesBefore + 1, TimeSpan.FromSeconds(15)));
        using var protocolAfter = new MockProtocol { SelfId = 10001 };
        await protocolAfter.ConnectReverseAsync($"ws://127.0.0.1:{botPort}", cts.Token);
        await protocolAfter.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await Push("s73-shared", "IDX_one"); await Push("s73-shared", "IDX_two"); await Push("s73-shared", "IDX_four");
        Check("S73 remaining shared budget survives restart", await WaitUntilAsync(() => SharedSends() == 4, TimeSpan.FromSeconds(15)));
        Check("S73 restart admits only the new index", model.Requests.Count == requestsBefore + 1);
        Check("S73 raw credential sequences persist monotonically", official.Messages.Where(m =>
            m["body"]?["msg_id"]?.GetValue<string>() == "s73-shared").Select(m => m["body"]!["msg_seq"]!.GetValue<int>()).SequenceEqual([1, 2, 3, 4]));
        await Push("s73-shared", "IDX_five");
        Check("S73 exhausted indexed draft is reported as failed", await WaitUntilAsync(() => restarted.OutputLines.Any(line =>
            line.Contains("[delivery] outcome=failed", StringComparison.Ordinal)), TimeSpan.FromSeconds(15)));
        await Push("s73-final-control", "IDX_final");
        Check("S73 fresh credential still works after exhaustion", await WaitUntilAsync(() => official.Messages.Any(m =>
            m["body"]?["msg_id"]?.GetValue<string>() == "s73-final-control"), TimeSpan.FromSeconds(15)));
        Check("S73 fifth index cannot replenish the shared budget", SharedSends() == 4 && model.Requests.Count == requestsBefore + 3);
    }
}
