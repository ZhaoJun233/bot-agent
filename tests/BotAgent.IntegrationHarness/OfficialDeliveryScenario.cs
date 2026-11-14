using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunOfficialDeliveryScenarioAsync()
    {
        Section("S69 fixed official reply credentials, segmented receipts and durable replay refusal");
        foreach (var paced in new[] { false, true }) await RunOfficialDeliveryModeAsync(paced);
    }

    private static async Task RunOfficialDeliveryModeAsync(bool paced)
    {
        var label = "S69 " + (paced ? "paced" : "legacy");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var model = new MockOpenAi(FreePort(17872)) { ResponseDelayMs = 1800 };
        model.EnqueueReply("""{"suitability":100,"reply":"S69 first alpha segment。S69 first beta segment。"}""");
        model.EnqueueReply("""{"suitability":100,"reply":"S69 second alpha segment。S69 second beta segment。"}""");
        model.EnqueueReply("""{"suitability":100,"reply":"S69 fresh alpha segment。S69 fresh beta segment。"}""");
        model.Start();
        using var official = new MockOfficialGateway(FreePort(18172), FreePort(13072)); official.Start();
        var directory = NewDataDir("s69-delivery"); var panelPort = FreePort(18173); var botPort = FreePort(13073);
        var environment = new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = directory, ["QQCHAT_API_KEY"] = "sk-mock", ["QQCHAT_BASE_URL"] = model.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model", ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{botPort}", ["QQCHAT_UIN"] = "10001", ["QQCHAT_WHITELIST"] = "66694",
            ["QQCHAT_GROUP_COOLDOWN"] = "0", ["QQCHAT_PRIVATE_COOLDOWN"] = "0", ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0", ["QQCHAT_WEB_SEARCH"] = "0", ["QQCHAT_ENABLE_VOICE"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "1", ["QQCHAT_SEGMENT_DELAY_MS"] = "0", ["QQCHAT_REPLY_PACING"] = paced ? "1" : "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(), ["QQCHAT_HEALTH_BIND"] = "127.0.0.1", ["QQCHAT_OFFICIAL"] = "1",
            ["QQCHAT_OFFICIAL_APP_ID"] = "100000004", ["QQCHAT_OFFICIAL_APP_SECRET"] = "test-secret-not-real",
            ["QQCHAT_OFFICIAL_API_BASE"] = official.HttpBase, ["QQCHAT_OFFICIAL_TOKEN_URL"] = official.TokenUrl, ["QQCHAT_OFFICIAL_SANDBOX"] = "1",
        };
        using var bot = StartBot(environment);
        await WaitForPortAsync(panelPort, cts.Token, bot);
        Check(label + " connected", await official.WaitForIdentifyAsync(1, TimeSpan.FromSeconds(15)));
        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        Task Push(string id, string content)
        {
            var data = OfficialIdentityFixture(id, "S69_GROUP", content);
            data["author"] = new JsonObject { ["member_openid"] = "S69_MEMBER" };
            data["message_scene"] = new JsonObject { ["ext"] = new JsonArray("msg_idx=REFIDX_" + id) };
            return official.PushEventAsync("GROUP_AT_MESSAGE_CREATE", data);
        }
        await Push("s69-first", "s69-first");
        Check(label + " first generation entered model", await WaitUntilAsync(() => model.Requests.Count >= 1, TimeSpan.FromSeconds(15)));
        Check(label + " newer trigger arrives before the first send", official.Messages.Count == 0);
        await Push("s69-second", "s69-second");
        var expectedCount = paced ? 2 : 4;
        Check(label + " selected generations deliver all segments", await WaitUntilAsync(() => official.Messages.Count >= expectedCount,
            TimeSpan.FromSeconds(25)));
        var bodies = official.Messages.Select(message => message["body"]!).ToArray();
        var first = bodies.Where(body => body["content"]?.GetValue<string>().Contains("S69 first") == true).ToArray();
        var second = bodies.Where(body => body["content"]?.GetValue<string>().Contains("S69 second") == true).ToArray();
        Check(label + " no unexpected send or reused generation", bodies.Length == expectedCount && model.Requests.Count == 2);
        Check(label + " first generation follows scheduler decision", paced ? first.Length == 0
            : first.Length == 2 && first.All(body => body["msg_id"]?.GetValue<string>() == "s69-first")
                && first.Select(body => body["msg_seq"]!.GetValue<int>()).SequenceEqual(new[] { 1, 2 }));
        Check(label + " second generation uses selected trigger for every segment", second.Length == 2
            && second.All(body => body["msg_id"]?.GetValue<string>() == "s69-second")
            && second.Select(body => body["msg_seq"]!.GetValue<int>()).SequenceEqual(new[] { 1, 2 }));
        var ledgerPath = Directory.EnumerateFiles(Path.Combine(directory, "data", "official-delivery"), "*.json").Single();
        // Observe completion before opening the snapshot: a Windows reader can block its atomic replacement.
        Check(label + " host finishes selected deliveries", await WaitUntilAsync(() =>
            bot.OutputLines.Count(line => line.Contains("[delivery] outcome=", StringComparison.Ordinal)) >= (paced ? 1 : 2),
            TimeSpan.FromSeconds(5)));
        var operations = JsonNode.Parse(File.ReadAllText(ledgerPath))!["Operations"]!.AsObject();
        Check(label + " each HTTP send has a terminal durable receipt",
            operations.Count == expectedCount && operations.All(item => item.Value!["State"]!.GetValue<int>() == 2
                && item.Value["MessageAttempts"]!.GetValue<int>() == 1 && item.Value["Status"]!.GetValue<int>() == 0
                && item.Value["MessageAlias"]!.GetValue<long>() >= 8_000_000_000_000_000L));
        await bot.StopAsync();

        model.ResponseDelayMs = 0;
        var requestsBefore = model.Requests.Count; var messagesBefore = official.Messages.Count;
        var identifiesBefore = official.IdentifyCount;
        using var restarted = StartBot(environment);
        await WaitForPortAsync(panelPort, cts.Token, restarted);
        Check(label + " restarted host connected", await official.WaitForIdentifyAsync(identifiesBefore + 1, TimeSpan.FromSeconds(15)));
        using var protocolAfter = new MockProtocol { SelfId = 10001 };
        await protocolAfter.ConnectReverseAsync($"ws://127.0.0.1:{botPort}", cts.Token);
        await protocolAfter.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await Push("s69-second", "s69-second-replayed");
        await Push("s69-fresh", "s69-fresh");
        Check(label + " fresh trigger completes after replay barrier", await WaitUntilAsync(() => official.Messages.Count >= messagesBefore + 2,
            TimeSpan.FromSeconds(20)));
        var after = official.Messages.Skip(messagesBefore).Select(message => message["body"]!).ToArray();
        Check(label + " restart replay does not reach model or transport", model.Requests.Count == requestsBefore + 1
            && after.Length == 2 && after.All(body => body["msg_id"]?.GetValue<string>() == "s69-fresh"));
        Check(label + " new trigger begins its own sequence", after.Select(body => body["msg_seq"]!.GetValue<int>()).SequenceEqual(new[] { 1, 2 }));
        Check(label + " restarted host finishes delivery before teardown", await WaitUntilAsync(() =>
            restarted.OutputLines.Any(line => line.Contains("[delivery] outcome=replied", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)));
    }
}
