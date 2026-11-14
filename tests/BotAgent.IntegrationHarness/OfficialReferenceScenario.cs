using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunOfficialReferenceScenarioAsync()
    {
        Section("S71 scoped official references through the real host");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var model = new MockOpenAi(FreePort(17877));
        for (var i = 0; i < 8; i++) model.EnqueueReply(new JsonObject { ["suitability"] = 100, ["reply"] = "S71 distinct response " + i }.ToJsonString());
        model.Start();
        using var official = new MockOfficialGateway(FreePort(18177), FreePort(13077)); official.Start();
        var directory = NewDataDir("s71-reference"); var panelPort = FreePort(18178); var botPort = FreePort(13078);
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = directory, ["QQCHAT_API_KEY"] = "sk-mock", ["QQCHAT_BASE_URL"] = model.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model", ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{botPort}", ["QQCHAT_UIN"] = "10001", ["QQCHAT_WHITELIST"] = "66696",
            ["QQCHAT_GROUP_COOLDOWN"] = "0", ["QQCHAT_PRIVATE_COOLDOWN"] = "0", ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0", ["QQCHAT_WEB_SEARCH"] = "0", ["QQCHAT_ENABLE_VOICE"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0", ["QQCHAT_REPLY_PACING"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(), ["QQCHAT_HEALTH_BIND"] = "127.0.0.1", ["QQCHAT_OFFICIAL"] = "1",
            ["QQCHAT_OFFICIAL_APP_ID"] = "100000006", ["QQCHAT_OFFICIAL_APP_SECRET"] = "test-secret-not-real",
            ["QQCHAT_OFFICIAL_API_BASE"] = official.HttpBase, ["QQCHAT_OFFICIAL_TOKEN_URL"] = official.TokenUrl, ["QQCHAT_OFFICIAL_SANDBOX"] = "1",
        });
        await WaitForPortAsync(panelPort, cts.Token, bot);
        Check("S71 connected", await official.WaitForIdentifyAsync(1, TimeSpan.FromSeconds(15)));
        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        async Task<string> Push(string id, string group, string content, string? reference = null, string? preview = null)
        {
            var data = OfficialIdentityFixture(id, group, content);
            data["author"] = new JsonObject { ["member_openid"] = "S71_MEMBER", ["username"] = "synthetic sender" };
            data["message_type"] = reference is null ? 0 : 103;
            var ext = new JsonArray("msg_idx=REFIDX_" + id, "auth_token=test-token-synthetic-quote-auth");
            if (reference is not null) ext.Add("ref_msg_idx=" + reference);
            data["message_scene"] = new JsonObject { ["ext"] = ext };
            if (preview is not null) data["msg_elements"] = new JsonArray(new JsonObject
                { ["msg_idx"] = reference, ["message_type"] = 103, ["content"] = preview });
            var before = model.Requests.Count;
            await official.PushEventAsync("GROUP_AT_MESSAGE_CREATE", data);
            Check("S71 reply " + id, await WaitUntilAsync(() => official.Messages.Any(m => m["body"]?["msg_id"]?.GetValue<string>() == id), TimeSpan.FromSeconds(15)));
            Check("S71 model ingress " + id, model.Requests.Count > before);
            return model.Requests.Count > before ? model.DescribeRequest(before) : "";
        }
        await Push("original", "S71_GROUP_A", "S71 original material");
        var known = await Push("known", "S71_GROUP_A", "S71 responding to known", "REFIDX_original");
        Check("S71 existing scoped message becomes a quote annotation", known.Contains("「S71 original material」", StringComparison.Ordinal));
        var preview = await Push("preview", "S71_GROUP_A", "S71 preview reply", "REFIDX_external", "S71 provided original");
        Check("S71 supplied preview reaches the real model", preview.Contains("「S71 provided original」", StringComparison.Ordinal));
        var unavailable = await Push("missing", "S71_GROUP_A", "S71 missing reply", "REFIDX_absent");
        Check("S71 missing original is explicit", unavailable.Contains("（引用原文不可用）", StringComparison.Ordinal));
        var cross = await Push("cross", "S71_GROUP_B", "S71 different group", "REFIDX_original");
        Check("S71 another group cannot borrow original text", cross.Contains("（引用原文不可用）", StringComparison.Ordinal)
            && !cross.Contains("S71 original material", StringComparison.Ordinal));
        Check("S71 reference metadata stays out of model context", new[] { known, preview, unavailable, cross }.All(p =>
            !p.Contains("REFIDX_", StringComparison.Ordinal) && !p.Contains("synthetic-quote-auth", StringComparison.Ordinal)));
        Check("S71 no OneBot history lookup for official references", protocol.ActionsReceived.All(a => a["action"]?.GetValue<string>() is not "get_msg" and not "get_group_msg_history"));
    }
}
