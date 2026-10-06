using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Platforms.Net;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Services;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;

namespace BotAgent.Adapters.Platforms.Feishu;

public sealed record FeishuOutboxItem(long MessageId, string SourceKey, string Text, DateTimeOffset SentAt);

/// <summary>
/// 飞书平台网关与适配器（阶段 4：首个非 QQ 外部平台）。
/// </summary>
public sealed class FeishuBotGateway : IQqChatSource, IPlatformAdapter, IPlatformMessenger, IDisposable
{
    private const int OutboxCapacity = 200;
    private const int SeenCapacity = 500;
    private static readonly TimeSpan SignatureMaxAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SeenTtl = TimeSpan.FromMinutes(10);

    private readonly IPlatformSettingsBox _box;
    private readonly BotAgent.Platforms.Net.IPlatformHttpFetcher _http;
    private readonly FeishuIdMap _ids;
    private readonly Action<string>? _log;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<FeishuOutboxItem> _outbox = new();
    private long _lastPruneTicks;

    private FeishuTokenCache? _tokenCache;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private bool _disposed;

    public FeishuBotGateway(IPlatformSettingsBox box, BotAgent.Platforms.Net.IPlatformHttpFetcher http, Action<string>? log = null, FeishuIdMap? ids = null)
    {
        _box = box ?? throw new ArgumentNullException(nameof(box));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _ids = ids ?? new FeishuIdMap();
        _log = log;

        Context = new PlatformContext(PlatformId.Feishu, AccountScope.Default, "feishu-main");
        Capabilities = PlatformCapabilities.FeishuTextOnly;
    }

    public PlatformContext Context { get; }
    public string DisplayName => "飞书";
    public string Tag => "飞书";
    public string Channel => Channels.Feishu;
    public PlatformCapabilities Capabilities { get; }
    public string? LastErrorCode { get; private set; }

    public bool IsConnected => _box.Current.FeishuEnabled && IsConfigured;

    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(_box.Current.FeishuAppId)
           && !string.IsNullOrWhiteSpace(_box.Current.FeishuAppSecret)
           && (!string.IsNullOrWhiteSpace(_box.Current.FeishuEncryptKey)
               || !string.IsNullOrWhiteSpace(_box.Current.FeishuVerificationToken));

    public IReadOnlyList<FeishuOutboxItem> Outbox => _outbox.ToArray();

    public event Action<QqChatMessage>? MessageReceived;
    public event Action<InboundMessage>? InboundReceived;
    public event Action<QqPokeEvent>? Poked { add { } remove { } }
    public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
    public event Action<bool>? ConnectionChanged;

    public long AliasFor(string original)
    {
        return _ids.AliasFor(CurrentAppId(), "participant", original);
    }

    public string? OriginalOf(long alias) => _ids.OriginalOf(alias, CurrentAppId(), "participant");

    private string CurrentAppId() => _box.Current.FeishuAppId?.Trim() ?? string.Empty;
    private static string IdentityKind(bool isGroup) => isGroup ? "group" : "participant";

    private sealed record FeishuSettingsSnapshot(string AppId, string AppSecret, string ApiBase);
    private sealed record FeishuTokenCache(FeishuSettingsSnapshot Settings, string Token, DateTimeOffset ExpiresAt);

    private FeishuSettingsSnapshot Snapshot()
    {
        var settings = _box.Current;
        var baseUri = string.IsNullOrWhiteSpace(settings.FeishuApiBase) ? "https://open.feishu.cn" : settings.FeishuApiBase.TrimEnd('/');
        return new FeishuSettingsSnapshot(settings.FeishuAppId?.Trim() ?? string.Empty, settings.FeishuAppSecret ?? string.Empty, baseUri);
    }

    public async Task<(bool Handled, int StatusCode, string ResponseBody)> HandleWebhookAsync(
        string body,
        string? signature,
        string? timestamp,
        string? nonce,
        string? verificationToken = null,
        CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var settings = _box.Current;
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(body))
        {
            return (false, 400, "{\"error\":\"empty_body\"}");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch
        {
            return (false, 400, "{\"error\":\"invalid_json\"}");
        }

        ct.ThrowIfCancellationRequested();
        if (node is not JsonObject payload)
        {
            LastErrorCode = "invalid_payload";
            return (false, 400, "{\"error\":\"invalid_payload\"}");
        }

        if (!TryGetOptionalString(payload, "token", out var payloadToken)
            || !TryGetOptionalString(payload, "type", out var type)
            || !TryGetOptionalString(payload, "challenge", out var challenge))
        {
            LastErrorCode = "invalid_payload";
            return (false, 400, "{\"error\":\"invalid_payload\"}");
        }

        var configuredVerificationToken = settings.FeishuVerificationToken?.Trim() ?? string.Empty;
        var suppliedVerificationToken = verificationToken?.Trim();
        if (string.IsNullOrWhiteSpace(suppliedVerificationToken))
        {
            suppliedVerificationToken = payloadToken?.Trim();
        }

        // URL challenge 必须独立认证；没有配置 token 时默认拒绝外部 challenge。
        if (string.Equals(type, "url_verification", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(configuredVerificationToken)
                || !string.Equals(suppliedVerificationToken, configuredVerificationToken, StringComparison.Ordinal))
            {
                LastErrorCode = "verification_token_mismatch";
                return (false, 403, "{\"error\":\"verification_token_mismatch\"}");
            }

            if (string.IsNullOrWhiteSpace(challenge))
            {
                return (false, 400, "{\"error\":\"missing_challenge\"}");
            }

            ct.ThrowIfCancellationRequested();
            var escapedChallenge = JsonValue.Create(challenge)?.ToJsonString() ?? "\"\"";
            return (true, 200, $"{{\"challenge\":{escapedChallenge}}}");
        }

        // 配置 Encrypt Key 时走签名 + 时间窗；否则普通事件必须带匹配的 Verification Token。
        if (!string.IsNullOrWhiteSpace(settings.FeishuEncryptKey))
        {
            if (!VerifySignature(body, signature, timestamp, nonce, settings.FeishuEncryptKey))
            {
                LastErrorCode = "signature_mismatch";
                return (false, 401, "{\"error\":\"signature_mismatch\"}");
            }
        }
        else if (string.IsNullOrWhiteSpace(configuredVerificationToken))
        {
            LastErrorCode = "verification_token_not_configured";
            return (false, 403, "{\"error\":\"verification_token_not_configured\"}");
        }
        else if (!string.Equals(suppliedVerificationToken, configuredVerificationToken, StringComparison.Ordinal))
        {
            LastErrorCode = "verification_token_mismatch";
            return (false, 401, "{\"error\":\"verification_token_mismatch\"}");
        }

        if (!TryGetOptionalObject(payload, "header", out var header)
            || !TryGetOptionalString(header, "event_type", out var eventType)
            || !TryGetOptionalString(header, "event_id", out var eventIdValue))
        {
            LastErrorCode = "invalid_payload";
            return (false, 400, "{\"error\":\"invalid_payload\"}");
        }

        var eventId = eventIdValue?.Trim() ?? string.Empty;

        if (!string.Equals(eventType, "im.message.receive_v1", StringComparison.OrdinalIgnoreCase))
        {
            return (true, 200, "{\"code\":0,\"msg\":\"ignored_event_type\"}");
        }

        if (!TryGetOptionalObject(payload, "event", out var evt)
            || evt is null
            || !TryGetOptionalObject(evt, "message", out var messageNode)
            || messageNode is null
            || !TryGetOptionalString(messageNode, "message_id", out var messageIdValue))
        {
            LastErrorCode = "invalid_payload";
            return (false, 400, "{\"error\":\"invalid_payload\"}");
        }

        var messageId = messageIdValue?.Trim() ?? string.Empty;
        var dedupKey = !string.IsNullOrWhiteSpace(eventId) ? $"event:{eventId}" : $"message:{messageId}";
        if (string.IsNullOrWhiteSpace(eventId) && string.IsNullOrWhiteSpace(messageId))
        {
            return (false, 400, "{\"error\":\"missing_event_id\"}");
        }

        if (!TryGetOptionalString(messageNode, "chat_type", out var chatTypeValue)
            || !TryGetOptionalString(messageNode, "chat_id", out var chatIdValue)
            || !TryGetOptionalString(messageNode, "content", out var contentStr)
            || !TryGetOptionalArray(messageNode, "mentions", out var mentions))
        {
            LastErrorCode = "invalid_payload";
            return (false, 400, "{\"error\":\"invalid_payload\"}");
        }

        var chatType = chatTypeValue ?? "group";
        var isGroup = !string.Equals(chatType, "p2p", StringComparison.OrdinalIgnoreCase);
        var chatId = chatIdValue ?? string.Empty;

        if (!TryGetOptionalObject(evt, "sender", out var sender)
            || !TryGetOptionalObject(sender, "sender_id", out var senderId))
        {
            LastErrorCode = "invalid_payload";
            return (false, 400, "{\"error\":\"invalid_payload\"}");
        }

        if (!TryGetOptionalString(senderId, "open_id", out var senderOpenIdValue)
            || !TryGetOptionalString(senderId, "user_id", out var senderUserIdValue))
        {
            LastErrorCode = "invalid_payload";
            return (false, 400, "{\"error\":\"invalid_payload\"}");
        }

        var senderOpenId = senderOpenIdValue ?? senderUserIdValue ?? "unknown_user";

        var targetRaw = isGroup ? chatId : senderOpenId;
        if (string.IsNullOrWhiteSpace(targetRaw)) return (false, 400, "{\"error\":\"missing_target\"}");

        // 白名单检查
        if (!IsAllowed(settings, isGroup, targetRaw))
        {
            _log?.Invoke($"[飞书] 忽略未在白名单中的消息（目标长度={targetRaw.Length}）");
            return (true, 200, "{\"code\":0,\"msg\":\"not_whitelisted\"}");
        }

        var text = ExtractText(contentStr ?? string.Empty);

        var mentioned = mentions is { Count: > 0 } || text.StartsWith('@');

        var (internalTarget, internalSender, internalMsgId) = MapInboundIds(settings, isGroup, targetRaw, senderOpenId, messageId);
        if (internalTarget == 0 || internalSender == 0 || internalMsgId == 0)
            return (false, 503, "{\"error\":\"identity_unavailable\"}");

        // A persistence failure must remain retryable; consume dedup markers only after binding succeeds.
        ct.ThrowIfCancellationRequested();
        if ((!string.IsNullOrWhiteSpace(settings.FeishuEncryptKey) && !TryMarkSeen($"nonce:{nonce}"))
            || !TryMarkSeen(dedupKey))
            return (true, 200, "{\"code\":0,\"msg\":\"duplicate\"}");

        var qqMsg = new QqChatMessage(
            MessageId: internalMsgId,
            IsGroup: isGroup,
            UserId: internalSender,
            GroupId: isGroup ? internalTarget : 0,
            SenderName: senderOpenId,
            Text: text,
            Time: Clock.Now,
            MentionedSelf: mentioned,
            Channel: Channels.Feishu);

        var convId = new ConversationId(PlatformId.Feishu, AccountScope.Default, isGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat, targetRaw);
        var inMsg = new InboundMessage(
            Ref: new MessageRef(convId, messageId),
            Sender: new ParticipantId(PlatformId.Feishu, AccountScope.Default, senderOpenId),
            SenderName: senderOpenId,
            Text: text,
            Timestamp: Clock.Now,
            IsMentioned: mentioned);

        _log?.Invoke($"[飞书] 收到入站消息（会话={targetRaw}，字数={text.Length}）");
        ct.ThrowIfCancellationRequested();
        MessageReceived?.Invoke(qqMsg);
        ct.ThrowIfCancellationRequested();
        InboundReceived?.Invoke(inMsg);
        return (true, 200, "{\"code\":0}");
    }

    public async Task<SendResult> SendTextAsync(
        bool isGroup,
        long targetId,
        string text,
        CancellationToken ct = default,
        long? replyToMessageId = null,
        bool directAddress = false)
    {
        var settings = Snapshot();
        var rawTarget = _ids.OriginalOf(targetId, settings.AppId, IdentityKind(isGroup));
        if (string.IsNullOrEmpty(rawTarget))
        {
            return new SendResult(false, 0);
        }

        var conv = new ConversationId(PlatformId.Feishu, AccountScope.Default, isGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat, rawTarget);
        var rawReply = replyToMessageId.HasValue ? _ids.OriginalOf(replyToMessageId.Value, settings.AppId, "message") : null;
        if (replyToMessageId.HasValue && rawReply is null) return new SendResult(false, 0);
        var outMsg = new OutboundMessage(conv, text, rawReply);
        var res = await SendWithSnapshotAsync(Context, outMsg, settings, ct).ConfigureAwait(false);
        var numId = res.IsSuccess && !string.IsNullOrWhiteSpace(res.MessageId)
            ? _ids.AliasFor(settings.AppId, "message", res.MessageId) : 0;
        return new SendResult(res.IsSuccess, numId);
    }

    private (long Target, long Sender, long Message) MapInboundIds(PlatformOptions settings, bool isGroup, string target, string sender, string message)
    {
        var appId = settings.FeishuAppId?.Trim() ?? string.Empty;
        return (_ids.AliasFor(appId, IdentityKind(isGroup), target),
            _ids.AliasFor(appId, "participant", sender), _ids.AliasFor(appId, "message", message));
    }

    public Task<DeliveryResult> SendAsync(
        PlatformContext context,
        OutboundMessage message,
        CancellationToken ct = default)
        => SendWithSnapshotAsync(context, message, Snapshot(), ct);

    private async Task<DeliveryResult> SendWithSnapshotAsync(
        PlatformContext context, OutboundMessage message, FeishuSettingsSnapshot settings, CancellationToken ct)
    {
        if (context is null || message is null)
        {
            LastErrorCode = "bad_request";
            return DeliveryResult.Rejected("bad_request");
        }

        var expectedPlatform = PlatformId.Normalize(Context.PlatformId);
        if (!string.Equals(PlatformId.Normalize(context.PlatformId), expectedPlatform, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(PlatformId.Normalize(message.Target.PlatformId), expectedPlatform, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(context.AccountScope, Context.AccountScope, StringComparison.Ordinal)
            || !string.Equals(message.Target.AccountScope, Context.AccountScope, StringComparison.Ordinal)
            || message.Target.Kind is not (ConversationKind.GroupChat or ConversationKind.PrivateChat)
            || string.IsNullOrWhiteSpace(message.Target.NativeTargetId))
        {
            LastErrorCode = "context_mismatch";
            return DeliveryResult.Rejected("context_mismatch");
        }

        var rawTarget = message.Target.NativeTargetId;
        string token;
        try
        {
            token = await GetTenantTokenAsync(settings, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LastErrorCode = "cancelled";
            return DeliveryResult.Transient("cancelled");
        }
        catch (Exception ex)
        {
            LastErrorCode = "network_error";
            _log?.Invoke($"[飞书] token 请求异常（类型={ex.GetType().Name}）");
            return DeliveryResult.Transient("network_error");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            var reason = LastErrorCode == "feishu_token_invalid"
                ? LastErrorCode
                : "feishu_not_configured";
            LastErrorCode = reason;
            return DeliveryResult.Transient(reason);
        }

        var baseUri = settings.ApiBase;

        var isGroup = message.Target.Kind == ConversationKind.GroupChat;
        var receiveType = isGroup ? "chat_id" : "open_id";
        var url = string.IsNullOrWhiteSpace(message.ReplyToMessageId)
            ? $"{baseUri}/open-apis/im/v1/messages?receive_id_type={receiveType}"
            : $"{baseUri}/open-apis/im/v1/messages/{message.ReplyToMessageId}/reply";

        var payload = new JsonObject
        {
            ["msg_type"] = "text",
            ["content"] = new JsonObject { ["text"] = message.Text }.ToJsonString(),
        };
        if (string.IsNullOrWhiteSpace(message.ReplyToMessageId))
        {
            payload["receive_id"] = rawTarget;
        }

        var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrWhiteSpace(token))
        {
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var respText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonNode? respNode = null;
            try { respNode = JsonNode.Parse(respText); } catch (JsonException) { }
            var code = respNode?["code"]?.GetValue<int>();

            if ((int)resp.StatusCode == 429)
            {
                LastErrorCode = "throttled";
                return DeliveryResult.Throttled(5, "feishu_rate_limited");
            }

            if (!resp.IsSuccessStatusCode)
            {
                LastErrorCode = $"http_{(int)resp.StatusCode}";
                return DeliveryResult.Transient("feishu_http_error");
            }

            if (code != 0)
            {
                LastErrorCode = $"feishu_err_{code?.ToString() ?? "unknown"}";
                return DeliveryResult.Transient(LastErrorCode);
            }

            var mid = respNode?["data"]?["message_id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
            var key = ConversationIdCodec.Encode(message.Target);
            _outbox.Enqueue(new FeishuOutboxItem(_ids.AliasFor(settings.AppId, "message", mid), key, message.Text, Clock.Now));
            while (_outbox.Count > OutboxCapacity) _outbox.TryDequeue(out _);
            _log?.Invoke($"[飞书] 消息已投递（目标长度={rawTarget.Length}，长度={message.Text.Length}）");
            return DeliveryResult.Ok(mid);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LastErrorCode = "cancelled";
            return DeliveryResult.Transient("cancelled");
        }
        catch (Exception ex)
        {
            LastErrorCode = "network_error";
            _log?.Invoke($"[飞书] 出站请求异常（类型={ex.GetType().Name}）");
            return DeliveryResult.Transient("network_error");
        }
    }

    public Task<(string? Text, string? SenderId)> GetQuotedMessageAsync(MessageRef messageRef, CancellationToken ct = default)
        => Task.FromResult<(string?, string?)>((null, null));

    public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
        => Task.FromResult<(string?, long)>((null, 0));

    public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
        => Task.FromResult(false);

    public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public void RegisterTarget(string channel, bool isGroup, long id) { }

    private bool IsAllowed(PlatformOptions settings, bool isGroup, string target)
    {
        var policy = (settings.PlatformPolicies ?? new List<PlatformPolicySettings>())
            .FirstOrDefault(p => p is not null && string.Equals(PlatformId.Normalize(p.PlatformId), PlatformId.Feishu, StringComparison.OrdinalIgnoreCase));
        var policyWl = isGroup ? policy?.GroupWhitelist : policy?.PrivateWhitelist;
        var wl = !string.IsNullOrWhiteSpace(policyWl) ? policyWl : settings.FeishuWhitelist;
        if (string.IsNullOrWhiteSpace(wl)) return false;
        var tokens = wl.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Contains("*") || tokens.Contains("all", StringComparer.OrdinalIgnoreCase)) return true;
        if (tokens.Contains(target, StringComparer.OrdinalIgnoreCase)) return true;
        var alias = _ids.AliasFor(settings.FeishuAppId?.Trim() ?? string.Empty, IdentityKind(isGroup), target);
        return alias != 0 && tokens.Contains(alias.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string> GetTenantTokenAsync(FeishuSettingsSnapshot settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(settings.AppId) || string.IsNullOrWhiteSpace(settings.AppSecret)) return string.Empty;
        var cached = Volatile.Read(ref _tokenCache);
        if (cached is not null && cached.Settings == settings && Clock.Now < cached.ExpiresAt) return cached.Token;

        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _tokenCache);
            if (cached is not null && cached.Settings == settings && Clock.Now < cached.ExpiresAt) return cached.Token;
            var baseUri = settings.ApiBase;
            var url = $"{baseUri}/open-apis/auth/v3/tenant_access_token/internal";
            var payload = new JsonObject
            {
                ["app_id"] = settings.AppId,
                ["app_secret"] = settings.AppSecret,
            };

            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return string.Empty;

            JsonObject? json;
            try
            {
                json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
            }
            catch (JsonException)
            {
                LastErrorCode = "feishu_token_invalid";
                return string.Empty;
            }

            if (json is null
                || !TryGetOptionalString(json, "tenant_access_token", out var token)
                || string.IsNullOrWhiteSpace(token)
                || !TryGetOptionalInt(json, "expire", out var expire))
            {
                LastErrorCode = "feishu_token_invalid";
                return string.Empty;
            }

            if (!string.IsNullOrEmpty(token))
            {
                Volatile.Write(ref _tokenCache, new FeishuTokenCache(settings, token, Clock.Now.AddSeconds(Math.Max(60, (expire ?? 7200) - 300))));
                ConnectionChanged?.Invoke(true);
                return token;
            }

            return string.Empty;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private static string ExtractText(string contentJson)
    {
        if (string.IsNullOrWhiteSpace(contentJson)) return string.Empty;
        try
        {
            var node = JsonNode.Parse(contentJson);
            return node?["text"]?.GetValue<string>() ?? contentJson;
        }
        catch
        {
            return contentJson;
        }
    }

    private static bool TryGetOptionalString(JsonNode? parent, string name, out string? value)
    {
        value = null;
        if (parent is null) return true;
        if (parent is not JsonObject obj) return false;
        if (!obj.TryGetPropertyValue(name, out var child) || child is null) return true;
        if (child is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var raw)) return false;
        value = raw;
        return true;
    }

    private static bool TryGetOptionalInt(JsonNode? parent, string name, out int? value)
    {
        value = null;
        if (parent is null) return true;
        if (parent is not JsonObject obj) return false;
        if (!obj.TryGetPropertyValue(name, out var child) || child is null) return true;
        if (child is not JsonValue jsonValue || !jsonValue.TryGetValue<int>(out var raw)) return false;
        value = raw;
        return true;
    }

    private static bool TryGetOptionalObject(JsonNode? parent, string name, out JsonObject? value)
    {
        value = null;
        if (parent is null) return true;
        if (parent is not JsonObject obj) return false;
        if (!obj.TryGetPropertyValue(name, out var child) || child is null) return true;
        if (child is not JsonObject childObject) return false;
        value = childObject;
        return true;
    }

    private static bool TryGetOptionalArray(JsonNode? parent, string name, out JsonArray? value)
    {
        value = null;
        if (parent is null) return true;
        if (parent is not JsonObject obj) return false;
        if (!obj.TryGetPropertyValue(name, out var child) || child is null) return true;
        if (child is not JsonArray childArray) return false;
        value = childArray;
        return true;
    }

    private static bool VerifySignature(string body, string? signature, string? timestamp, string? nonce, string encryptKey)
    {
        if (string.IsNullOrWhiteSpace(signature)
            || string.IsNullOrWhiteSpace(timestamp)
            || string.IsNullOrWhiteSpace(nonce)
            || !long.TryParse(timestamp, out var unixSeconds))
        {
            return false;
        }

        DateTimeOffset signedAt;
        try
        {
            signedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if ((Clock.UtcNow - signedAt).Duration() > SignatureMaxAge)
        {
            return false;
        }

        var raw = timestamp + nonce + encryptKey + body;
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(signature.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private bool TryMarkSeen(string key)
    {
        var now = Clock.UtcNow;

        // 仅当距离上次清理超过 60 秒或字典条目超过容量阈值时才做批次淘汰，避免每次请求 O(N) 线性扫描
        var last = Interlocked.Read(ref _lastPruneTicks);
        if (_seen.Count > SeenCapacity || now.Ticks - last > TimeSpan.FromSeconds(60).Ticks)
        {
            if (Interlocked.CompareExchange(ref _lastPruneTicks, now.Ticks, last) == last)
            {
                foreach (var pair in _seen)
                {
                    if (now - pair.Value > SeenTtl)
                    {
                        _seen.TryRemove(new KeyValuePair<string, DateTimeOffset>(pair.Key, pair.Value));
                    }
                }
            }
        }

        if (_seen.TryGetValue(key, out var existing) && now - existing <= SeenTtl)
        {
            return false;
        }

        if (!BotAgent.Platforms.Persistence.PlatformDedupStore.TryRegisterFeishuWebhook(key, now, SeenTtl))
        {
            return false;
        }

        _seen.TryAdd(key, now);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tokenGate.Dispose();
    }
}
