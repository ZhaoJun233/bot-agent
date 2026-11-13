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
foreach (var (desire, threshold, isPrivate, expected) in golden)
{
    var request = Request(desire, threshold, isPrivate);
    var prompt = PromptBuilder.Build(request);
    Check(prompt.Split(silenceContract, StringSplitOptions.None).Length == 2,
        "zero-message silence contract appears exactly once");
    Check(Hash(prompt.Replace(silenceContract, "", StringComparison.Ordinal)) == expected,
        "legacy prompt golden except the explicit silence contract");
    Check(PromptBuilder.Build(request) == PromptBuilder.Build(request with { ReplyPacingEnabled = false }),
        "omitted pacing flag remains legacy");
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
    var request = Request(desire, threshold, isPrivate) with
    {
        Proactive = proactive, VibeHint = "平静", MoodText = "合成心情",
        EnableWebSearch = true, EnableToolRequest = true, ToolList = "synthetic_tool",
        EnableAsk = true, EnableVoice = true, EnableListen = true
    };
    var legacy = PromptBuilder.BuildParts(request);
    var paced = PromptBuilder.BuildParts(request with { ReplyPacingEnabled = true });
    var expected = legacy.Text;
    foreach (var text in desireTexts)
    {
        expected = expected.Replace("\n" + text, "", StringComparison.Ordinal);
        Check(!paced.Text.Contains(text, StringComparison.Ordinal), "no legacy desire tier in paced prompt");
    }
    Check(paced.Text == expected, "only legacy desire sentence is removed; content and safety unchanged");
    Check(paced.Stable == legacy.Stable, "pacing does not churn stable prompt cache prefix");
    Check(paced.Text == PromptBuilder.Build(request with { AiDesire = 0, ReplyPacingEnabled = true }),
        "paced prompt ignores every legacy desire value");
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
    var legacy = SamplingPolicy.Resolve(c.Text, c.Facts, c.Vibe, c.Declared, config with { Enabled = adaptive });
    var paced = SamplingPolicy.Resolve(c.Text, c.Facts, c.Vibe, c.Declared,
        config with { Enabled = adaptive, ReplyPacingEnabled = true });
    var expected = adaptive ? c.Expected : new SamplingProfile(0.6, 0.7, CognitiveIntent.Balanced, "adaptive_disabled");
    Check(legacy == expected, "legacy sampling numbers, intent and reason");
    Check(paced.ReplyPacingEnabled, "every sampling branch carries the pacing snapshot");
    Check(paced with { ReplyPacingEnabled = false } == legacy, "pacing does not change content sampling");
}

// Capture assembly only: BuildAsync throws a dedicated sentinel, before any send or image I/O.
var transport = new CaptureTransport();
var options = new ModelOptions
{
    ModelBaseUrl = "https://example.com/v1", Model = "synthetic-model", ApiKey = "synthetic-key",
    EnableAtmosphereDamping = false
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
var admitted = SamplingPolicy.Resolve(null, false, null, null,
    config with { Enabled = false, ReplyPacingEnabled = true });
string? firstPaced = null;
foreach (var desire in new[] { 0, 19, 20, 49, 50, 79, 80, 100 })
foreach (var damping in new[] { false, true })
{
    client.AiDesire = desire;
    options.EnableAtmosphereDamping = damping;
    var prompt = await Capture(admitted);
    firstPaced ??= prompt;
    Check(prompt == firstPaced, "hot legacy desire/damping cannot change an admitted turn prompt");
    Check(transport.Sampling == admitted, "sampling snapshot reaches transport unchanged");
    Check(prompt.Contains("suitability 不低于 63", StringComparison.Ordinal), "client retains suitability gate");
    Check(prompt.Contains("平静", StringComparison.Ordinal), "client retains vibe content");
    Check(desireTexts.All(text => !prompt.Contains(text, StringComparison.Ordinal)), "client forwards paced prompt flag");
}
client.AiDesire = 90;
options.EnableAtmosphereDamping = false;
var unpaced = await Capture(null);
Check(unpaced.Contains(desireTexts[3], StringComparison.Ordinal), "absent snapshot uses legacy desire");
Check(await Capture(admitted with { ReplyPacingEnabled = false }) == unpaced, "turn snapshot false wins without global mode state");
options.EnableAtmosphereDamping = true;
var damped = await Capture(null);
// Four recent self replies: 90 - 75 fatigue + 5 active atmosphere = 20.
Check(damped.Contains(desireTexts[1], StringComparison.Ordinal), "legacy atmosphere damping is still applied");
Check(damped != unpaced, "synthetic atmosphere exercises a real legacy desire band change");
Check(await Capture(admitted) == firstPaced, "mode handoff does not mutate the admitted snapshot");
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

static PromptBuilder.PromptRequest Request(int desire, int threshold = 50, bool isPrivate = false) => new(
    SystemPrompt: "Synthetic prompt probe", BotIdentity: null, Persona: null, AiDesire: desire,
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
