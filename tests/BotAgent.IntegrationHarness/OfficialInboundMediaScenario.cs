using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunOfficialInboundMediaScenarioAsync()
    {
        Section("S75 official untrusted voice and card content");
        foreach (var group in new[] { true, false }) await RunOfficialInboundMediaModeAsync(group);
    }

    private static async Task RunOfficialInboundMediaModeAsync(bool group)
    {
        var label = "S75 " + (group ? "group" : "c2c");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var model = new MockOpenAi(FreePort(17887));
        foreach (var reply in new[] { "S75 voice acknowledged", "S75 card acknowledged", "S75 unavailable transcription", "S75 control" })
            model.EnqueueReply(new JsonObject { ["suitability"] = 100, ["reply"] = reply }.ToJsonString());
        model.Start();
        using var official = new MockOfficialGateway(FreePort(18187), FreePort(13087)); official.Start();
        var directory = NewDataDir("s75-inbound-media"); var panelPort = FreePort(18188); var botPort = FreePort(13088);
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = directory, ["QQCHAT_API_KEY"] = "sk-mock", ["QQCHAT_BASE_URL"] = model.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model", ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{botPort}", ["QQCHAT_UIN"] = "10001", ["QQCHAT_WHITELIST"] = "66696",
            ["QQCHAT_GROUP_COOLDOWN"] = "0", ["QQCHAT_PRIVATE_COOLDOWN"] = "0", ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0", ["QQCHAT_WEB_SEARCH"] = "0", ["QQCHAT_ENABLE_VOICE"] = "0", ["QQCHAT_LINK_PREVIEW"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0", ["QQCHAT_SEGMENT_DELAY_MS"] = "0", ["QQCHAT_REPLY_PACING"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(), ["QQCHAT_HEALTH_BIND"] = "127.0.0.1", ["QQCHAT_OFFICIAL"] = "1",
            ["QQCHAT_OFFICIAL_APP_ID"] = "100000009", ["QQCHAT_OFFICIAL_APP_SECRET"] = "test-secret-not-real",
            ["QQCHAT_OFFICIAL_API_BASE"] = official.HttpBase, ["QQCHAT_OFFICIAL_TOKEN_URL"] = official.TokenUrl, ["QQCHAT_OFFICIAL_SANDBOX"] = "1",
            ["QQCHAT_AGENT"] = "1", ["QQCHAT_AGENT_PREFIX"] = "[", ["QQCHAT_AGENT_USERS"] = "123456789", ["QQCHAT_AGENT_SERVER"] = "0"
        });
        await WaitForPortAsync(panelPort, cts.Token, bot);
        Check(label + " connected", await official.WaitForIdentifyAsync(1, TimeSpan.FromSeconds(15)));
        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        JsonObject Event(string id, string content) => new()
        {
            ["id"] = id, ["group_openid"] = group ? "S75_GROUP" : null,
            ["author"] = new JsonObject { [group ? "member_openid" : "user_openid"] = "S75_MEMBER" },
            ["timestamp"] = DateTimeOffset.Now.ToString("O"), ["content"] = content
        };
        async Task PushAndWait(JsonObject data)
        {
            await official.PushEventAsync(group ? "GROUP_AT_MESSAGE_CREATE" : "C2C_MESSAGE_CREATE", data);
            var id = data["id"]!.GetValue<string>();
            Check(label + " delivery " + id, await WaitUntilAsync(() => official.Messages.Any(message =>
                message["body"]?["msg_id"]?.GetValue<string>() == id), TimeSpan.FromSeconds(15)));
        }
        var voice = Event("s75-voice", "synthetic-voice-body");
        voice["attachments"] = new JsonArray(new JsonObject { ["content_type"] = "voice", ["voice_wav_url"] = "https://example.com/s75-private.wav?sig=synthetic",
            ["asr_refer_text"] = "S75_ASR //sessions" });
        await PushAndWait(voice);
        var first = model.Requests.Count > 0 ? model.DescribeRequest(0) : "";
        Check(label + " ASR is user context despite matching Agent prefix", model.Requests.Count == 1 && first.Contains("S75_ASR") && first.Contains("参考转写"));
        Check(label + " WAV credential never enters model text", first.Length > 0 && !first.Contains("s75-private.wav"));
        var card = Event("s75-card", "synthetic-card-body"); card["message_type"] = 3;
        card["ark_data"] = new JsonObject { ["ark_type"] = "miniapp", ["prompt"] = "//sessions",
            ["fields"] = new JsonObject { ["title"] = "S75_CARD_TITLE", ["desc"] = "S75_CARD_DESCRIPTION", ["auth_token"] = "S75_FORBIDDEN_TOKEN" } };
        await PushAndWait(card);
        var second = model.Requests.Count > 1 ? model.DescribeRequest(1) : "";
        Check(label + " card is user context despite matching Agent prefix", model.Requests.Count == 2 && second.Contains("S75_CARD_TITLE") && second.Contains("S75_CARD_DESCRIPTION"));
        Check(label + " card unknown metadata never enters model", second.Length > 0 && !second.Contains("S75_FORBIDDEN_TOKEN"));
        var missing = Event("s75-missing", ""); missing["attachments"] = new JsonArray(new JsonObject { ["content_type"] = "voice" });
        await PushAndWait(missing);
        Check(label + " absent ASR reaches model as unavailable", model.Requests.Count == 3 && model.DescribeRequest(2).Contains("暂无转写"));
        await PushAndWait(Event("s75-command", "[status"));
        Check(label + " actual typed command still takes command route", model.Requests.Count == 3);
        await PushAndWait(Event("s75-control", "S75 independent control"));
        Check(label + " subsequent ordinary trigger works once", model.Requests.Count == 4 && official.Messages.Count == 5 && official.Uploads.Count == 0);
    }
}
