using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Model;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Reply;
using BotAgent.Model;
using BotAgent.Model.Net;
using BotAgent.Services.Model;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
ModelClock.Initialize(new FixedClock());
var checks = 0;
using var baseline = args.Length == 1 ? new BaselinePrompt(args[0]) : null;

// Captured from the unchanged b80d7b2 model with this fixed clock, before adding pacing.
// Retain these hashes: the silence repair may add only the explicit contract below.
const string silenceContract = "\n沉默是零消息：决定不接话时输出 {\"action\":\"silent\",\"reply\":\"\"}，不附带任何动作。" +
    "内部参与判断只在心里完成，不得写进 reply，不得用括号、旁白或解释为何不回复来代替沉默。正常回答、拒绝和引用仍写在 reply。";
var golden = new[]
{
    (0, 50, false, "5B34E629B38EDCF6453717ADA5396E734037B4FB664E5D44A8BD2BF273ACC7DA"),
    (20, 50, false, "8502054C571A27D37EC6E82F875A7E358F5231F976FA34A6DD7A61CB8A04DEEA"),
    (50, 50, false, "2BE8974CCE92078F26F1762F47D2F8B078F8FE88262F05C0135A06389E61F122"),
    (80, 50, false, "16B5FACACB600D65EE201AA53F227EFCE6084853EB2D778F6ECB9740102A1DFD"),
    (50, -1, false, "C67FEA22E9C4B7BE2970063EDE92B8E38AECE1048E739E89A6AD6C4F58C5543A"),
    (50, 101, false, "E9D57A68E1BFB7250B034D9DC9021BFE738883EE467997D98FE1F9C05C3229F5"),
    (0, 50, true, "EC6A602877B3146D5580F5ECF14C60E41F015AEBB4E3904DD7BBC7A7A13CB101")
};
// Captured from unchanged 66149bf3 with pacing enabled; the optional baseline run also
// checks the older legacy hashes above, so these are not regenerated from the candidate.
var pacedGolden = new Dictionary<(int Threshold, bool Private), string>
{
    [(50, false)] = "61BCF78286008D4475FAF1A998F40E747CD2C8106D1962F781DAA6F136E8601E",
    [(-1, false)] = "B756341B98F007B6224DC6FFC050ABFE6474BA95E0FE5C5B4732AA14CE564B68",
    [(101, false)] = "0185BD13CB6C8F5F3D2040623D918F55BE60236D64DF87645468DF6D89BB8B2F",
    [(50, true)] = "1A9ABD24D5994DB9059763FB99F7957D0BF28068F7F30F7BAEB2568E93790045"
};
foreach (var (desire, threshold, isPrivate, expected) in golden)
{
    var request = Request(threshold, isPrivate);
    var prompt = PromptBuilder.Build(request);
    Check(prompt.Split(silenceContract, StringSplitOptions.None).Length == 2,
        "zero-message silence contract appears exactly once");
    Check(Hash(prompt) == pacedGolden[(threshold, isPrivate)], "unchanged baseline paced prompt golden");
    if (baseline is not null)
    {
        var legacy = baseline.Build(request, desire, pacing: false);
        Check(Hash(legacy.Text.Replace(silenceContract, "", StringComparison.Ordinal)) == expected,
            "preserved legacy golden validates the baseline independently");
        var paced = baseline.Build(request, desire, pacing: true);
        Check(prompt == paced.Text && PromptBuilder.BuildParts(request).Stable == paced.Stable,
            "single-policy prompt equals the unchanged baseline paced prompt");
        Console.WriteLine($"BASELINE_PACED_HASH threshold={threshold} private={isPrivate} hash={Hash(paced.Text)}");
    }
}

var desireTexts = new[]
{
    "你极少主动发言：仅在被明确 @ 或对方直接对你说话时回复。",
    "你较为克制：主要在被呼叫或与你直接相关时发言，群友闲聊时保持沉默。",
    "你比较健谈：话题与你相关、被提到或你感兴趣时积极发言，不强行插话。",
    "你非常健谈主动：主动参与群聊话题、接话、活跃气氛，除非话题与你完全无关。"
};
foreach (var desire in new[] { -1, 0, 19, 20, 49, 50, 79, 80, 100, 101 })
foreach (var threshold in new[] { 0, 50, 100 })
foreach (var isPrivate in new[] { false, true })
foreach (var proactive in new[] { false, true })
{
    var request = Request(threshold, isPrivate) with
    {
        Proactive = proactive, VibeHint = "平静", MoodText = "合成心情",
        EnableWebSearch = true, EnableToolRequest = true, ToolList = "synthetic_tool",
        EnableAsk = true, EnableVoice = true, EnableListen = true
    };
    var paced = PromptBuilder.BuildParts(request);
    foreach (var text in desireTexts)
    {
        Check(!paced.Text.Contains(text, StringComparison.Ordinal), "no retired desire tier in the single-policy prompt");
    }
    if (baseline is not null)
    {
        var expected = baseline.Build(request, desire, pacing: true);
        Check(paced.Text == expected.Text, "content and safety equal the baseline paced prompt");
        Check(paced.Stable == expected.Stable, "stable cache prefix equals the baseline paced prefix");
    }
    if (!isPrivate || proactive)
        Check(paced.Text.Contains($"suitability 不低于 {threshold}", StringComparison.Ordinal), "suitability threshold retained");
}

var config = new AdaptiveSamplingConfig(true, 0.2, 0.3, 0.9, 0.95, 0.6, 0.7);
var samplingCases = new (string Text, bool Facts, string? Vibe, string? Declared, SamplingProfile Expected)[]
{
    ("hello", false, null, "rational", new(0.2, 0.3, CognitiveIntent.Rational, "model_declared_rational")),
    ("hello", false, null, "emotional", new(0.9, 0.95, CognitiveIntent.Emotional, "model_declared_emotional")),
    ("hello", false, null, "balanced", new(0.6, 0.7, CognitiveIntent.Balanced, "model_declared_balanced")),
    ("hello", true, null, null, new(0.2, 0.3, CognitiveIntent.Rational, "search_facts")),
    ("报错 参数", false, null, null, new(0.2, 0.3, CognitiveIntent.Rational, "heuristic_rational(r=2,e=0)")),
    ("好累 心累", false, null, null, new(0.9, 0.95, CognitiveIntent.Emotional, "heuristic_emotional(r=0,e=2)")),
    ("hello", false, null, null, new(0.6, 0.7, CognitiveIntent.Balanced, "balanced_default(r=0,e=0)")),
    ("报错 参数", false, "平静", null, new(0.2, 0.3, CognitiveIntent.Rational, "heuristic_rational(r=2,e=0)")),
    ("好累", false, "低落", null, new(0.9, 0.95, CognitiveIntent.Emotional, "heuristic_emotional(r=0,e=2)"))
};
foreach (var adaptive in new[] { false, true })
foreach (var c in samplingCases)
{
    var profile = SamplingPolicy.Resolve(c.Text, c.Facts, c.Vibe, c.Declared, config with { Enabled = adaptive });
    var expected = adaptive ? c.Expected : new SamplingProfile(0.6, 0.7, CognitiveIntent.Balanced, "adaptive_disabled");
    Check(profile == expected, "sampling numbers, intent and reason remain unchanged");
}
Check(typeof(AdaptiveSamplingConfig).GetProperty("ReplyPacingEnabled") is null
    && typeof(SamplingProfile).GetProperty("ReplyPacingEnabled") is null
    && typeof(PromptBuilder.PromptRequest).GetProperty("AiDesire") is null,
    "content and sampling contracts no longer expose retired scheduler switches");

// Capture assembly only: BuildAsync throws a dedicated sentinel, before any send or image I/O.
var transport = new CaptureTransport();
var options = new ModelOptions
{
    ModelBaseUrl = "https://example.com/v1", Model = "synthetic-model", ApiKey = "synthetic-key"
};
var rejectHttp = new RejectHttp();
var client = new OpenAiClient(() => options, rejectHttp, rejectHttp, new RejectImages(), transport)
{
    SuitabilityThreshold = 63
};
var window = Enumerable.Range(0, 4).Select(i => new ChatMessage
{
    QqMessageId = i + 1, SenderId = 10001, SenderName = "Synthetic bot", Role = MessageRole.Self,
    Timestamp = ModelClock.Current.Now, Text = "Synthetic reply"
}).ToArray();
var admitted = SamplingPolicy.Resolve(null, false, null, null, config with { Enabled = false });
var firstPaced = await Capture(admitted);
foreach (var temperature in new[] { 0.2, 0.9, 1.5 })
{
    options.DefaultTemperature = temperature;
    var prompt = await Capture(admitted);
    Check(prompt == firstPaced, "hot sampling settings do not mutate the admitted prompt");
    Check(transport.Sampling == admitted, "sampling snapshot reaches transport unchanged");
    Check(prompt.Contains("suitability 不低于 63", StringComparison.Ordinal), "client retains suitability gate");
    Check(prompt.Contains("平静", StringComparison.Ordinal), "client retains vibe content");
    Check(desireTexts.All(text => !prompt.Contains(text, StringComparison.Ordinal)), "client never emits retired desire instructions");
}
Check(await Capture(null) == firstPaced, "absent sampling snapshot cannot restore legacy reply instructions");
Console.WriteLine($"ReplyPromptProbe: {checks} checks passed (synthetic, no network or business data).");

void Check(bool passed, string name)
{
    if (!passed) throw new InvalidOperationException(name);
    checks++;
}

async Task<string> Capture(SamplingProfile? sampling)
{
    try
    {
        await client.CompleteAsync(window, vibeHint: "平静", sampling: sampling);
    }
    catch (CaptureComplete)
    {
        return transport.SystemContent ?? throw new InvalidOperationException("prompt not captured");
    }
    throw new InvalidOperationException("request did not reach capture transport");
}

static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

static PromptBuilder.PromptRequest Request(int threshold = 50, bool isPrivate = false) => new(
    SystemPrompt: "Synthetic prompt probe", BotIdentity: null, Persona: null,
    Window: Array.Empty<ChatMessage>(), QuotableIds: Array.Empty<long>(), ProfilesText: null, Stickers: null,
    PokeContext: false, Proactive: false, MoodText: null, MusicText: null, LinkText: null, RecallText: null,
    GroupRolesText: null, VibeHint: null, SearchText: null, SuitabilityThreshold: threshold,
    EnableListen: false, EnableVoice: false, VoiceMaxChars: 100, VoiceEagerness: 50,
    EnableWebSearch: false, EnableAsk: false, EnableToolRequest: false, IsPrivateConversation: isPrivate);

sealed class CaptureComplete : Exception;

sealed class CaptureTransport : IModelTransport
{
    public string? SystemContent { get; private set; }
    public SamplingProfile? Sampling { get; private set; }
    public Task<BuiltRequest> BuildAsync(IReadOnlyList<ChatMessage> window, string systemContent,
        IReadOnlyCollection<long> quotableIds, CancellationToken ct, SamplingProfile? sampling = null,
        string? promptCacheScope = null)
    {
        SystemContent = systemContent;
        Sampling = sampling;
        throw new CaptureComplete();
    }
    public Task<SendOutcome> SendAsync(JsonObject payload, int attachedImages,
        IReadOnlyList<long> attachedImageIds, CancellationToken ct) => throw new InvalidOperationException("Unexpected send");
}

sealed class RejectImages : IImageDownloader
{
    public Func<long, CancellationToken, Task<IReadOnlyList<string>>>? RefreshUrls { get; set; }
    public int CacheHits => 0;
    public int RefreshedCount => 0;
    public Task<(byte[] Data, string Mime, string Ext)?> DownloadBytesAsync(string url, CancellationToken ct, long? messageId = null)
        => throw new InvalidOperationException("Unexpected image I/O");
}

sealed class RejectHttp : IModelHttpFetcher
{
    public TimeSpan Timeout => TimeSpan.FromSeconds(1);
    private static Task<HttpResponseMessage> Unexpected() => throw new InvalidOperationException("Unexpected HTTP I/O");
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default) => Unexpected();
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => Unexpected();
    public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => Unexpected();
    public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => Unexpected();
    public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => Unexpected();
    public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => Unexpected();
    public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default) => Unexpected();
}

sealed class FixedClock : IClock
{
    public DateTimeOffset Now => new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    public DateTimeOffset UtcNow => Now;
    public DateTime LocalDateTime => Now.DateTime;
    public long TickCount => 0;
    public Task Delay(TimeSpan delay, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected delay");
    public Task Delay(int millisecondsDelay, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected delay");
}
