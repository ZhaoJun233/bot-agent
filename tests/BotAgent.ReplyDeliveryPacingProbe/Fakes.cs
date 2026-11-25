using System.Net;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Reply;
using BotAgent.Platforms.Net;
using BotAgent.Services;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;
using BotAgent.Services.Resilience;

sealed class TestClock : IClock
{
    public DateTimeOffset Now => UtcNow;
    public DateTimeOffset UtcNow => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public DateTime LocalDateTime => Now.DateTime;
    public long TickCount => 0;
    public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.Delay(delay, ct);
    public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Task.Delay(millisecondsDelay, ct);
}

sealed class TestSettings : ISettingsBox
{
    public AppSettings Current { get; } = new() { SplitReplies = true, SegmentDelayMs = 0, FilterActionNarration = false };
}

sealed class TestRiskBackoff : IProtocolRiskBackoff
{
    public bool Active { get; set; }
    public bool IsActive(string sourceKey) => Active;
    public bool HasAnyActive() => Active;
    public ProtocolBackoffDecision EvaluateText(string sourceKey, bool directAddress, string text)
        => new(!Active || directAddress, Active ? "shortened" : text, Active, "synthetic_backoff");
}

sealed class MemoryOwnMessages : IOwnMessageRepository
{
    public int MaxEntries => 10;
    public List<OwnMessage> LoadRecent(int max) => new();
    public void Upsert(long id, string text, DateTimeOffset at) { }
    public void PruneTo(int max) { }
}

class SyntheticSource : IQqChatSource
{
#pragma warning disable CS0067
    public event Action<QqChatMessage>? MessageReceived;
    public event Action<QqPokeEvent>? Poked;
    public event Action<QqRecallEvent>? MessageRecalled;
    public event Action<bool>? ConnectionChanged;
#pragma warning restore CS0067
    public bool IsConnected => true;
    public int TextCalls { get; private set; }
    public Func<int, SendResult> TextResult { get; set; } = _ => new(true);
    public Func<SendResult> ImageResult { get; set; } = () => new(true);
    public int ImageCalls { get; private set; }
    public Task<SendResult> SendImageWithReceiptAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
    { ImageCalls++; return Task.FromResult(ImageResult()); }
    public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null, bool directAddress = false)
        => Task.FromResult(TextResult(++TextCalls));
    public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null) => Task.FromResult(true);
    public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default) => Task.FromResult<(string?, long)>((null, 0));
    public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default) => Task.FromResult<string?>(null);
}
sealed class StagedSyntheticSource : SyntheticSource, IStagedReplyChatSource { }

sealed class TestGuard : IReplyCommitGuard
{
    public int Sends { get; private set; }
    public int Prepares { get; private set; }
    public int Effects { get; private set; }
    public int Tries { get; private set; }
    public Action<int>? OnSend { get; set; }
    public Action<int>? OnPrepare { get; set; }
    public Func<int, bool>? OnTry { get; set; }
    public Task BeforeSendAsync() { Sends++; OnSend?.Invoke(Sends); return Task.CompletedTask; }
    public Task BeforePrepareAsync() { Prepares++; OnPrepare?.Invoke(Prepares); return Task.CompletedTask; }
    public Task BeforeEffectAsync() { Effects++; return Task.CompletedTask; }
    public bool TryBeforeSend() { Tries++; return OnTry?.Invoke(Tries) ?? true; }
}

sealed class SyntheticIds : IOfficialIdentityMap
{
    public static OfficialIdentityKey Trigger() => new(new("synthetic-app", OfficialIdentityKind.Message,
        new(PlatformId.QqOfficial, "synthetic-app", ConversationKind.GroupChat, "synthetic-id"), OfficialMessageDirection.Inbound), "synthetic-inbound");
    public int Count => 2;
    public long AliasFor(OfficialIdentityKey identity) => identity.Scope.Kind == OfficialIdentityKind.Group ? 10001
        : identity.Scope.Direction == OfficialMessageDirection.Inbound ? 10002 : Channels.AliasBase + 3;
    public string? OriginalOf(long alias, OfficialIdentityScope scope)
        => alias == 10001 && scope.AppId == "synthetic-app" && scope.Kind == OfficialIdentityKind.Group ? "synthetic-id"
        : alias == 10002 && scope == Trigger().Scope ? "synthetic-inbound" : null;
}

sealed class PacingOfficialSnapshot : IOfficialDeliverySnapshot
{
    private string? _value;
    public string? Load() => _value;
    public void Commit(string snapshot) => _value = snapshot;
    public void Dispose() { }
}
sealed class PacingOwner : IPlatformSourceOwner
{
    public string? ConversationKey(QqChatMessage message, Func<string, bool> exists) => null;
}

sealed class SyntheticHttp : IPlatformHttpFetcher
{
    public TimeSpan Timeout => TimeSpan.FromSeconds(1);
    public int Uploads { get; private set; }
    public int Messages { get; private set; }
    public int Tokens { get; private set; }
    public Action? AfterUpload { get; set; }
    public bool RejectFirstToken { get; set; }
    private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json) };
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/files", StringComparison.Ordinal))
        {
            Uploads++;
            AfterUpload?.Invoke();
            return Task.FromResult(Response("{\"file_info\":\"synthetic-file\"}"));
        }
        if (!request.RequestUri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal))
            throw new InvalidOperationException("unexpected_synthetic_http_route");
        Messages++;
        return Task.FromResult(RejectFirstToken && Messages == 1
            ? Response("{\"code\":11244}", HttpStatusCode.BadRequest)
            : Response("{\"id\":\"synthetic-ack\"}"));
    }
    public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
    {
        Tokens++;
        return Task.FromResult(Response(new JsonObject { ["access_token"] = new string('x', 16), ["expires_in"] = 7200 }.ToJsonString()));
    }
    public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => SendAsync(request, ct);
    public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
    public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
    public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url, ct);
}
