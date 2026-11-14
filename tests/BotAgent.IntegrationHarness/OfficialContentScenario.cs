using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunOfficialContentScenarioAsync()
    {
        Section("S74 official content receipts and confirmed history");
        foreach (var mode in new[] { "single", "split", "music-unknown" })
            await RunOfficialContentModeAsync(mode);
    }

    private static async Task RunOfficialContentModeAsync(string mode)
    {
        var label = "S74 " + mode; var musicMode = mode == "music-unknown";
        const string omitted = "https://example.com/s74-not-transmitted";
        var reply = musicMode ? "S74MusicBase" : mode == "split" ? "S74FirstConfirmedSyntheticSegment。S74Link " + omitted + "。" : "S74Link " + omitted;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var model = new MockOpenAi(FreePort(17884));
        var draft = new JsonObject { ["suitability"] = 100, ["reply"] = reply };
        if (musicMode) draft["shareSong"] = "Synthetic Song";
        model.EnqueueReply(draft.ToJsonString());
        model.EnqueueReply("""{"suitability":100,"reply":"S74 independent control"}"""); model.Start();
        using var music = new MockMusicHost(FreePort(18185), "Synthetic Song", "Synthetic Artist"); music.Start();
        using var official = new MockOfficialGateway(FreePort(18184), FreePort(13084)); official.Start();
        var unknownMusicAttempts = 0;
        if (musicMode) official.MessageStatusSelector = body =>
        {
            if (body?["content"]?.GetValue<string>().StartsWith("🎵", StringComparison.Ordinal) != true) return 200;
            Interlocked.Increment(ref unknownMusicAttempts); return 503;
        };
        var directory = NewDataDir("s74-content"); var panelPort = FreePort(18186); var botPort = FreePort(13086);
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = directory, ["QQCHAT_API_KEY"] = "sk-mock", ["QQCHAT_BASE_URL"] = model.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model", ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{botPort}", ["QQCHAT_UIN"] = "10001", ["QQCHAT_WHITELIST"] = "66695",
            ["QQCHAT_GROUP_COOLDOWN"] = "0", ["QQCHAT_PRIVATE_COOLDOWN"] = "0", ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0", ["QQCHAT_WEB_SEARCH"] = "0", ["QQCHAT_ENABLE_VOICE"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = mode == "split" ? "1" : "0", ["QQCHAT_SEGMENT_DELAY_MS"] = "0", ["QQCHAT_REPLY_PACING"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(), ["QQCHAT_HEALTH_BIND"] = "127.0.0.1", ["QQCHAT_OFFICIAL"] = "1",
            ["QQCHAT_OFFICIAL_APP_ID"] = "100000008", ["QQCHAT_OFFICIAL_APP_SECRET"] = "test-secret-not-real",
            ["QQCHAT_OFFICIAL_API_BASE"] = official.HttpBase, ["QQCHAT_OFFICIAL_TOKEN_URL"] = official.TokenUrl, ["QQCHAT_OFFICIAL_SANDBOX"] = "1",
            ["QQCHAT_NETEASE_BASE_URL"] = music.BaseUrl, ["QQCHAT_MUSIC_SOURCES"] = music.SourceTemplate(999001),
            ["QQCHAT_MUSIC_MODEL"] = "mock-audio-model",
        });
        await WaitForPortAsync(panelPort, cts.Token, bot);
        Check(label + " connected", await official.WaitForIdentifyAsync(1, TimeSpan.FromSeconds(15)));
        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await official.PushGroupMessageAsync("S74_GROUP", "S74_MEMBER", "s74-initial", "s74-initial");
        var expected = mode == "single" ? 1 : 2;
        Check(label + " attempted expected output", await WaitUntilAsync(() => official.Messages.Count >= expected, TimeSpan.FromSeconds(15)));
        Check(label + " honest terminal result", await WaitUntilAsync(() => bot.OutputLines.Any(line => musicMode
            ? line.Contains("music_share outcome=unknown", StringComparison.Ordinal)
            : line.Contains("[delivery] outcome=replied", StringComparison.Ordinal) && line.Contains("reason=link_domain_unverified", StringComparison.Ordinal)), TimeSpan.FromSeconds(5)));
        var before = official.Messages.Select(message => message["body"]!).ToArray();
        Console.WriteLine($"S74_SHAPE mode={mode} requests={model.Requests.Count} messages={before.Length} search={music.SearchHits} api={music.ApiHits} audio={music.AudioHits}"
            + $" control={before.Count(body => body["content"]?.GetValue<string>().Contains("independent control") == true)}"
            + $" base={before.Count(body => body["content"]?.GetValue<string>().Contains("S74MusicBase") == true)}"
            + $" link={before.Count(body => body["content"]?.GetValue<string>().Contains("S74Link") == true)}");
        Check(label + " no extra fallback", before.Length == expected && official.Uploads.Count == 0);
        if (musicMode)
        {
            Check(label + " unknown injected into music regardless of send order", Volatile.Read(ref unknownMusicAttempts) == 1);
            Check(label + " unknown music never starts listening", music.SearchHits == 1 && music.ApiHits == 0 && music.AudioHits == 0);
        }
        else
            Check(label + " omitted link absent from actual HTTP", before.Length == expected && before.All(body => !body["content"]!.GetValue<string>().Contains(omitted)));
        await official.PushGroupMessageAsync("S74_GROUP", "S74_MEMBER", "s74-control", "s74-control");
        Check(label + " independent control delivers", await WaitUntilAsync(() => official.Messages.Any(message =>
            message["body"]?["msg_id"]?.GetValue<string>() == "s74-control"), TimeSpan.FromSeconds(15)));
        var prompt = model.Requests.Count > 1 ? model.DescribeRequest(model.Requests.Count - 1) : "";
        Check(label + " untransmitted link absent from history", prompt.Length > 0 && !prompt.Contains(omitted, StringComparison.Ordinal));
        var confirmed = musicMode ? before.Where(body => body["content"]?.GetValue<string>() == "S74MusicBase") : before;
        Check(label + " confirmed HTTP text enters next model context", before.Length == expected
            && confirmed.Any() && confirmed.All(body => prompt.Contains(body["content"]!.GetValue<string>(), StringComparison.Ordinal)));
        Check(label + " independent trigger did not revive old work", official.Messages.Count == expected + 1 && (!musicMode || music.SearchHits == 1));
    }
}
