using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunOfficialOutcomeScenarioAsync()
    {
        Section("S70 uncertain official delivery never creates a fallback or false history");
        foreach (var mode in new[] { "voice-first", "voice-partial", "text-partial" })
            await RunOfficialOutcomeModeAsync(mode);
    }

    private static async Task RunOfficialOutcomeModeAsync(string mode)
    {
        var label = "S70 " + mode;
        var voice = mode.StartsWith("voice", StringComparison.Ordinal);
        var partial = mode.EndsWith("partial", StringComparison.Ordinal);
        var confirmed = "S70Confirmed" + mode;
        var uncertain = "S70Uncertain" + mode;
        var unattempted = "S70Unattempted" + mode;
        var content = partial ? confirmed + (voice ? "|" : "。") + uncertain : uncertain;
        content += (voice ? "|" : "。") + unattempted;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var model = new MockOpenAi(FreePort(17874));
        model.EnqueueReply(new JsonObject { ["suitability"] = 100, ["reply"] = voice ? "" : content,
            ["speak"] = voice ? content : "" }.ToJsonString());
        model.EnqueueReply("""{"suitability":100,"reply":"S70 independent control"}""");
        model.Start();
        using var tts = new MockTtsHost(FreePort(18175)); tts.Start();
        using var official = new MockOfficialGateway(FreePort(18174), FreePort(13074)); official.Start();
        if (partial) official.EnqueueMessageStatus(200);
        official.EnqueueMessageStatus(503);
        var directory = NewDataDir("s70-outcome"); var panelPort = FreePort(18176); var botPort = FreePort(13076);
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = directory, ["QQCHAT_API_KEY"] = "sk-mock", ["QQCHAT_BASE_URL"] = model.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model", ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{botPort}", ["QQCHAT_UIN"] = "10001", ["QQCHAT_WHITELIST"] = "66695",
            ["QQCHAT_GROUP_COOLDOWN"] = "0", ["QQCHAT_PRIVATE_COOLDOWN"] = "0", ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0", ["QQCHAT_WEB_SEARCH"] = "0", ["QQCHAT_ENABLE_VOICE"] = "1",
            ["QQCHAT_VOICE"] = "zh_CN-huayan-medium", ["QQCHAT_VOICE_MAX_CHARS"] = "100", ["QQCHAT_TTS_URL"] = tts.BaseUrl,
            ["QQCHAT_SPLIT_REPLIES"] = "1", ["QQCHAT_SEGMENT_DELAY_MS"] = "0", ["QQCHAT_REPLY_PACING"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(), ["QQCHAT_HEALTH_BIND"] = "127.0.0.1", ["QQCHAT_OFFICIAL"] = "1",
            ["QQCHAT_OFFICIAL_APP_ID"] = "100000005", ["QQCHAT_OFFICIAL_APP_SECRET"] = "test-secret-not-real",
            ["QQCHAT_OFFICIAL_API_BASE"] = official.HttpBase, ["QQCHAT_OFFICIAL_TOKEN_URL"] = official.TokenUrl, ["QQCHAT_OFFICIAL_SANDBOX"] = "1",
        });
        await WaitForPortAsync(panelPort, cts.Token, bot);
        Check(label + " connected", await official.WaitForIdentifyAsync(1, TimeSpan.FromSeconds(15)));
        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await official.PushGroupMessageAsync("S70_GROUP", "S70_MEMBER", "s70-initial", "s70-initial");
        var expected = partial ? 2 : 1;
        Check(label + " attempted selected delivery", await WaitUntilAsync(() => official.Messages.Count >= expected, TimeSpan.FromSeconds(15)));
        Check(label + " terminal outcome remains unknown", await WaitUntilAsync(() => bot.OutputLines.Any(line =>
            line.Contains("[delivery] outcome=unknown", StringComparison.Ordinal)), TimeSpan.FromSeconds(5)));
        var before = official.Messages.Select(message => message["body"]!).ToArray();
        Check(label + " no later segment or text fallback", before.Length == expected
            && before.All(body => body["msg_type"]!.GetValue<int>() == (voice ? 7 : 0)));
        Check(label + " uploads stop at uncertain voice", official.Uploads.Count == (voice ? expected : 0));
        await official.PushGroupMessageAsync("S70_GROUP", "S70_MEMBER", "s70-control", "s70-control");
        Check(label + " independent control still delivers", await WaitUntilAsync(() => official.Messages.Any(message =>
            message["body"]?["msg_id"]?.GetValue<string>() == "s70-control"), TimeSpan.FromSeconds(15)));
        var prompt = model.Requests.Count > 1 ? model.DescribeRequest(model.Requests.Count - 1) : "";
        Check(label + " uncertain and unattempted output absent from history", prompt.Length > 0
            && !prompt.Contains(uncertain, StringComparison.Ordinal) && !prompt.Contains(unattempted, StringComparison.Ordinal));
        Check(label + " confirmed output retained exactly when delivered", prompt.Contains(confirmed, StringComparison.Ordinal) == partial);
    }
}
