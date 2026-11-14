using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunOfficialRelationshipScenarioAsync()
    {
        Section("S72 official relationship withdrawal through the real host");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var model = new MockOpenAi(FreePort(17882)) { ResponseDelayMs = 1800 };
        for (var i = 0; i < 5; i++) model.EnqueueReply(new JsonObject
            { ["suitability"] = 100, ["reply"] = "S72 distinct synthetic response " + i }.ToJsonString());
        model.Start();
        using var official = new MockOfficialGateway(FreePort(18182), FreePort(13082)); official.Start();
        var directory = NewDataDir("s72-relationship"); var panelPort = FreePort(18183); var botPort = FreePort(13083);
        var environment = new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = directory, ["QQCHAT_API_KEY"] = "sk-mock", ["QQCHAT_BASE_URL"] = model.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model", ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{botPort}", ["QQCHAT_UIN"] = "10001", ["QQCHAT_WHITELIST"] = "66697",
            ["QQCHAT_GROUP_COOLDOWN"] = "0", ["QQCHAT_PRIVATE_COOLDOWN"] = "0", ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0", ["QQCHAT_WEB_SEARCH"] = "0", ["QQCHAT_ENABLE_VOICE"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0", ["QQCHAT_REPLY_PACING"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(), ["QQCHAT_HEALTH_BIND"] = "127.0.0.1", ["QQCHAT_OFFICIAL"] = "1",
            ["QQCHAT_OFFICIAL_APP_ID"] = "100000007", ["QQCHAT_OFFICIAL_APP_SECRET"] = "test-secret-not-real",
            ["QQCHAT_OFFICIAL_API_BASE"] = official.HttpBase, ["QQCHAT_OFFICIAL_TOKEN_URL"] = official.TokenUrl, ["QQCHAT_OFFICIAL_SANDBOX"] = "1",
        };
        using var bot = StartBot(environment);
        await WaitForPortAsync(panelPort, cts.Token, bot);
        Check("S72 connected", await official.WaitForIdentifyAsync(1, TimeSpan.FromSeconds(15)));
        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        Task Relation(string type) => official.PushEventAsync(type, new()
            { ["openid"] = "S72_USER", ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

        await official.PushC2CMessageAsync("S72_USER", "S72 first input", "s72-first");
        Check("S72 first generation entered model", await WaitUntilAsync(() => model.Requests.Count == 1, TimeSpan.FromSeconds(15)));
        Check("S72 removal occurs before transport", official.Messages.Count == 0);
        await Relation("FRIEND_DEL");
        Check("S72 removal reached the host gateway", await WaitUntilAsync(() => bot.OutputLines.Any(line =>
            line.Contains("official_relationship scope=direct state=removed", StringComparison.Ordinal)), TimeSpan.FromSeconds(5)));
        Check("S72 pending generation reports a refused delivery", await WaitUntilAsync(() => bot.OutputLines.Any(line =>
            line.Contains("[delivery] outcome=failed", StringComparison.Ordinal)), TimeSpan.FromSeconds(15)));
        Check("S72 removed friend receives no pending message or upload", official.Messages.Count == 0 && official.Uploads.Count == 0);

        await Relation("FRIEND_ADD");
        await Relation("C2C_MSG_RECEIVE");
        await Relation("C2C_MSG_REJECT");
        Check("S72 restoration and notification events were processed", await WaitUntilAsync(() =>
            bot.OutputLines.Any(line => line.Contains("official_relationship scope=direct state=added", StringComparison.Ordinal))
            && bot.OutputLines.Count(line => line.Contains("official_notification scope=direct active_delivery=disabled", StringComparison.Ordinal)) == 2,
            TimeSpan.FromSeconds(5)));
        model.ResponseDelayMs = 0;
        await official.PushC2CMessageAsync("S72_USER", "S72 fresh input", "s72-fresh");
        Check("S72 notification refusal still permits a fresh passive reply", await WaitUntilAsync(() => official.Messages.Any(message =>
            message["body"]?["msg_id"]?.GetValue<string>() == "s72-fresh"), TimeSpan.FromSeconds(15)));
        Check("S72 restoration did not replay the old draft", model.Requests.Count == 2 && official.Messages.Count == 1);
        await bot.StopAsync();

        var requestsBefore = model.Requests.Count; var sendsBefore = official.Messages.Count; var identifiesBefore = official.IdentifyCount;
        using var restarted = StartBot(environment);
        await WaitForPortAsync(panelPort, cts.Token, restarted);
        Check("S72 restarted host connected", await official.WaitForIdentifyAsync(identifiesBefore + 1, TimeSpan.FromSeconds(15)));
        using var protocolAfter = new MockProtocol { SelfId = 10001 };
        await protocolAfter.ConnectReverseAsync($"ws://127.0.0.1:{botPort}", cts.Token);
        await protocolAfter.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await official.PushC2CMessageAsync("S72_USER", "S72 replayed input", "s72-first");
        await official.PushC2CMessageAsync("S72_USER", "S72 control input", "s72-control");
        Check("S72 fresh control completes after the replay", await WaitUntilAsync(() => official.Messages.Any(message =>
            message["body"]?["msg_id"]?.GetValue<string>() == "s72-control"), TimeSpan.FromSeconds(15)));
        Check("S72 restart preserves the deleted trigger barrier", model.Requests.Count == requestsBefore + 1
            && official.Messages.Count == sendsBefore + 1);
    }
}
