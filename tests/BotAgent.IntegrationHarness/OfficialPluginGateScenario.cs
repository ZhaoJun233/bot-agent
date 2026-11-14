using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunOfficialPluginGateScenarioAsync()
    {
        Section("S78 official managed voice plugin gates through live configuration and transport");
        foreach (var isGroup in new[] { true, false }) await RunOfficialPluginGateCaseAsync(isGroup);
    }

    private static async Task RunOfficialPluginGateCaseAsync(bool isGroup)
    {
        var label = "S78 " + (isGroup ? "group" : "C2C");
        const string adminToken = "synthetic-s78-admin";
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var model = new MockOpenAi(FreePort(17878));
        model.EnqueueReply("""{"suitability":100,"reply":"S78 disabled text","speak":"S78 disabled voice"}""");
        model.EnqueueReply("""{"suitability":100,"reply":"","speak":"S78 pending voice"}""");
        model.EnqueueReply("""{"suitability":100,"reply":"S78 independent text"}""");
        model.EnqueueReply("""{"suitability":100,"reply":"","speak":"S78 enabled voice"}""");
        model.Start();
        using var tts = new MockTtsHost(FreePort(18178)); tts.Start();
        using var official = new MockOfficialGateway(FreePort(18278), FreePort(13078)); official.Start();
        var botPort = FreePort(13178); var panelPort = FreePort(18378);
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = NewDataDir("s78-plugins"), ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = model.BaseUrl, ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket", ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{botPort}",
            ["QQCHAT_UIN"] = "10001", ["QQCHAT_WHITELIST"] = "66695",
            ["QQCHAT_GROUP_COOLDOWN"] = "0", ["QQCHAT_PRIVATE_COOLDOWN"] = "0", ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_PRIVATE_CHAT"] = "1", ["QQCHAT_STICKERS"] = "0", ["QQCHAT_WEB_SEARCH"] = "0",
            ["QQCHAT_ENABLE_VOICE"] = "1", ["QQCHAT_VOICE"] = "zh_CN-huayan-medium",
            ["QQCHAT_VOICE_MAX_CHARS"] = "100", ["QQCHAT_TTS_URL"] = tts.BaseUrl,
            ["QQCHAT_SPLIT_REPLIES"] = "0", ["QQCHAT_REPLY_PACING"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(), ["QQCHAT_HEALTH_BIND"] = "127.0.0.1",
            ["BOTAGENT_ZBA_TOKEN"] = adminToken, ["QQCHAT_OFFICIAL"] = "1",
            ["QQCHAT_OFFICIAL_APP_ID"] = "100000008", ["QQCHAT_OFFICIAL_APP_SECRET"] = "test-secret-not-real",
            ["QQCHAT_OFFICIAL_API_BASE"] = official.HttpBase, ["QQCHAT_OFFICIAL_TOKEN_URL"] = official.TokenUrl,
            ["QQCHAT_OFFICIAL_SANDBOX"] = "1",
        });
        await WaitForPortAsync(panelPort, cts.Token, bot);
        Check(label + " connected", await official.WaitForIdentifyAsync(1, TimeSpan.FromSeconds(15)));
        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var root = $"http://127.0.0.1:{panelPort}/zba/v1";
        Task PushAsync(string target, string id) => isGroup
            ? official.PushGroupMessageAsync(target, "S78_MEMBER", id, id)
            : official.PushC2CMessageAsync(target, id, id);
        bool Delivered(string id) => official.Messages.Any(message => message["body"]?["msg_id"]?.GetValue<string>() == id);

        await SetVoicePluginAsync(false);
        await PushAsync("S78_DISABLED", "s78-disabled");
        Check(label + " disabled plugin preserves text reply", await WaitUntilAsync(() => Delivered("s78-disabled"), TimeSpan.FromSeconds(15)));
        Check(label + " disabled plugin starts no TTS or upload", tts.SpeakHits == 0 && official.Uploads.Count == 0);

        await SetVoicePluginAsync(true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tts.BeforeSpeak = async () => { started.TrySetResult(); await release.Task.WaitAsync(cts.Token); };
        try
        {
            await PushAsync("S78_REVOKED", "s78-revoked");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Check(label + " transport preparation is held before official upload", tts.SpeakHits == 1 && official.Uploads.Count == 0);
            await SetVoicePluginAsync(false);
        }
        finally { release.TrySetResult(); }
        tts.BeforeSpeak = null;
        await PushAsync("S78_REVOKED", "s78-control");
        Check(label + " subsequent independent text completes", await WaitUntilAsync(() => Delivered("s78-control"), TimeSpan.FromSeconds(15)));
        Check(label + " revocation during TTS blocks official upload and voice send", official.Uploads.Count == 0
            && official.Messages.All(message => message["body"]?["msg_type"]?.GetValue<int>() != 7),
            $"uploads={official.Uploads.Count}, voiceSends={official.Messages.Count(message => message["body"]?["msg_type"]?.GetValue<int>() == 7)}");

        await SetVoicePluginAsync(true);
        await PushAsync("S78_POSITIVE", "s78-positive");
        Check(label + " enabled plugin sends via same official chain", await WaitUntilAsync(() => Delivered("s78-positive"), TimeSpan.FromSeconds(15)));
        var sends = official.Messages.Where(message => message["body"]?["msg_id"]?.GetValue<string>() == "s78-positive").ToArray();
        Check(label + " positive control has one scoped voice send", tts.SpeakHits == 2 && official.Uploads.Count == 1 && sends.Length == 1
            && sends[0]["body"]?["msg_type"]?.GetValue<int>() == 7
            && sends[0]["path"]?.GetValue<string>() == $"/v2/{(isGroup ? "groups" : "users")}/S78_POSITIVE/messages");
        Check(label + " no private send or action fallback", protocol.ActionsReceived.All(action =>
            action["action"]?.GetValue<string>() is not ("send_group_msg" or "send_private_msg" or "delete_msg" or "set_group_ban")));

        async Task SetVoicePluginAsync(bool enabled)
        {
            var read = await ZbaAsync(http, "POST", root + "/configuration/read", adminToken, """{"kind":1}""");
            var config = JsonNode.Parse(read.Body);
            var request = new JsonObject
            {
                ["contractRevision"] = config?["contractRevision"]?.DeepClone(),
                ["expectedVersion"] = config?["version"]?.DeepClone(),
                ["key"] = $"cfg1:{config?["generation"]?.GetValue<string>()}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():N}",
                ["changes"] = new JsonObject { ["registered"] = new JsonObject
                    { ["plugins"] = new JsonArray(new JsonObject { ["key"] = "preset.feature.voice", ["enabled"] = enabled }),
                      ["providers"] = new JsonArray(), ["policies"] = new JsonArray() } },
                ["secrets"] = new JsonArray(),
            };
            var save = await ZbaAsync(http, "POST", root + "/configuration/save", adminToken, request.ToJsonString());
            var response = JsonNode.Parse(save.Body);
            var applied = read.Status == 200 && save.Status == 200 && response?["code"]?.GetValue<int>() == 0
                && response?["receipt"]?["commitState"]?.GetValue<int>() == 1
                && response?["receipt"]?["applyState"]?.GetValue<int>() == 1;
            Check(label + " voice plugin live configuration applied: " + enabled, applied);
            if (!applied) throw new InvalidOperationException("synthetic_plugin_configuration_not_applied");
        }
    }
}
