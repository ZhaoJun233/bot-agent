using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Reply;
using BotAgent.Platforms;
using BotAgent.Services.OneBot;
using BotAgent.Services.Platforms;
using BotAgent.Services.Qq;

namespace BotAgent.Adapters.Platforms.Feishu;

public sealed record FeishuOutboxItem(long MessageId, string SourceKey, string Text, DateTimeOffset SentAt);

/// <summary>
/// 飞书平台网关与适配器（Phase 2 全功能版：Webhook + pbbp2 长连接、AES 解密、富媒体、话题回复与 Markdown 卡片）。
/// </summary>
public sealed partial class FeishuBotGateway : IQqChatSource, IPlatformAdapter, IPlatformMessenger, IPlatformInboundChatSource, IFeishuTargetMap, IStagedReplyChatSource, IDisposable
{
    private const int OutboxCapacity = 200;
    private const int SeenCapacity = 500;
    private static readonly TimeSpan SignatureMaxAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SeenTtl = TimeSpan.FromMinutes(10);

    private readonly IPlatformSettingsBox _box;
    private readonly BotAgent.Platforms.Net.IPlatformHttpFetcher _http;
    private readonly FeishuIdMap _ids;
    private readonly Action<string>? _log;
    private readonly IWebhookDedupStore? _dedupStore;
    private readonly Func<IReadOnlyDictionary<string, JsonElement>?>? _readPluginSettings;
    private readonly Func<Uri, CancellationToken, Task<IFeishuWsSession>>? _wsConnect;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _knownChats = new(StringComparer.Ordinal);
    private const int KnownChatCapacity = 4096;
    private readonly ConcurrentQueue<FeishuOutboxItem> _outbox = new();
    private long _lastPruneTicks;

    private FeishuTokenCache? _tokenCache;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private int _disposedFlag;
    private bool _disposed => Volatile.Read(ref _disposedFlag) != 0;
    private readonly PlatformOperationLifetime _operations = new();

    public FeishuBotGateway(IPlatformSettingsBox box, BotAgent.Platforms.Net.IPlatformHttpFetcher http, Action<string>? log = null, FeishuIdMap? ids = null)
        : this(box, http, log, ids, null, null, null) { }

    public FeishuBotGateway(IPlatformSettingsBox box, BotAgent.Platforms.Net.IPlatformHttpFetcher http, Action<string>? log, FeishuIdMap? ids, IWebhookDedupStore? dedupStore)
        : this(box, http, log, ids, dedupStore, null, null) { }

    public FeishuBotGateway(
        IPlatformSettingsBox box,
        BotAgent.Platforms.Net.IPlatformHttpFetcher http,
        Action<string>? log,
        FeishuIdMap? ids,
        IWebhookDedupStore? dedupStore,
        Func<IReadOnlyDictionary<string, JsonElement>?>? readPluginSettings,
        Func<Uri, CancellationToken, Task<IFeishuWsSession>>? wsConnect = null)
    {
        _box = box ?? throw new ArgumentNullException(nameof(box));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _ids = ids ?? new FeishuIdMap();
        _log = log;
        _dedupStore = dedupStore;
        _readPluginSettings = readPluginSettings;
        _wsConnect = wsConnect;

        Context = new PlatformContext(PlatformId.Feishu, AccountScope.Default, "feishu-main");
        Capabilities = PlatformCapabilities.Feishu;
    }

    public PlatformContext Context { get; }
    public string DisplayName => "飞书";
    public string Tag => "飞书";
    public string Channel => Channels.Feishu;
    public PlatformCapabilities Capabilities { get; }
    public string? LastErrorCode { get; private set; }
    public bool SupportsStructuredMarkdown(string channel) => EnableInteractiveCards;

    public bool IsConnected
    {
        get
        {
            var settings = _box.Current;
            var enabled = settings.RuntimePlatformPolicies.HasValue
                ? PlatformSwitchSettings.ResolvePolicyRules(settings, Context, ImmutableArray<ActionId>.Empty).Enabled == ToggleRule.Enabled
                : settings.FeishuEnabled;
            if (_disposed || !_operations.IsActive || !enabled || !Configured(settings))
                return false;
            if (!IsLongConnectionMode)
                return true;
            return _wsConnected;
        }
    }

    public bool IsConfigured => Configured(_box.Current);

    private bool Configured(PlatformOptions settings)
    {
        if (string.IsNullOrWhiteSpace(settings.FeishuAppId) || string.IsNullOrWhiteSpace(settings.FeishuAppSecret))
            return false;
        if (IsLongConnectionMode)
            return true;
        return !string.IsNullOrWhiteSpace(settings.FeishuEncryptKey)
            || !string.IsNullOrWhiteSpace(settings.FeishuVerificationToken);
    }

    public IReadOnlyList<FeishuOutboxItem> Outbox => _outbox.ToArray();

    public event Action<QqChatMessage>? MessageReceived;
    public event Action<InboundMessage>? InboundReceived;
    public event Action<QqChatMessage, InboundMessage>? RichInboundReceived;
    public event Action<QqPokeEvent>? Poked { add { } remove { } }
    public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
    public event Action<bool>? ConnectionChanged;

    public long AliasFor(string original) => _ids.AliasFor(CurrentAppId(), "participant", original);
    public string? OriginalOf(long alias) => _ids.OriginalOf(alias, CurrentAppId(), "participant");
    public void RegisterTarget(string channel, bool isGroup, long id) { }

    public bool AllowsMappedTarget(bool isGroup, long alias)
    {
        if (_disposed || !_operations.IsActive || alias <= 0) return false;
        try
        {
            var settings = _box.Current;
            if (!Configured(settings)) return false;
            if (settings.RuntimePlatformPolicies.HasValue)
            {
                var rules = PlatformSwitchSettings.ResolvePolicyRules(settings, Context, ImmutableArray<ActionId>.Empty);
                if (rules.Enabled != ToggleRule.Enabled || rules.ChatEnabled != ToggleRule.Enabled) return false;
            }
            else
            {
                var rules = PlatformSwitchSettings.Read(settings, Context.PlatformId, Context.AccountScope);
                if (!rules.Enabled || !rules.ChatEnabled) return false;
            }
            var raw = _ids.OriginalOf(alias, settings.FeishuAppId?.Trim() ?? string.Empty, IdentityKind(isGroup));
            return !string.IsNullOrWhiteSpace(raw) && IsAllowed(settings, isGroup, raw);
        }
        catch
        {
            return false;
        }
    }

    public bool TryGetTargetAlias(ConversationId conversation, out long alias)
    {
        alias = 0;
        var settings = _box.Current;
        if (_disposed || !_operations.IsActive || !Configured(settings) || conversation is null
            || string.IsNullOrWhiteSpace(conversation.PlatformId)
            || PlatformId.Normalize(conversation.PlatformId) != PlatformId.Feishu
            || !string.Equals(conversation.AccountScope, Context.AccountScope, StringComparison.Ordinal)
            || conversation.Kind is not (ConversationKind.GroupChat or ConversationKind.PrivateChat)
            || !string.IsNullOrEmpty(conversation.ThreadId)
            || string.IsNullOrWhiteSpace(conversation.NativeTargetId))
        {
            return false;
        }

        var appId = settings.FeishuAppId?.Trim() ?? string.Empty;
        var kind = IdentityKind(conversation.Kind == ConversationKind.GroupChat);
        if (!_ids.TryGetAlias(appId, kind, conversation.NativeTargetId, out var existing)
            || !string.Equals(_ids.OriginalOf(existing, appId, kind), conversation.NativeTargetId, StringComparison.Ordinal))
        {
            return false;
        }

        alias = existing;
        return true;
    }

    public bool TryGetTargetConversation(bool isGroup, long alias, out ConversationId? target)
    {
        target = null;
        var settings = _box.Current;
        if (_disposed || !_operations.IsActive || !Configured(settings) || alias <= 0) return false;
        var appId = settings.FeishuAppId?.Trim() ?? string.Empty;
        var kind = IdentityKind(isGroup);
        var native = _ids.OriginalOf(alias, appId, kind);
        if (string.IsNullOrWhiteSpace(native) || !_ids.TryGetAlias(appId, kind, native, out var existing) || existing != alias)
            return false;
        target = new(Context.PlatformId, Context.AccountScope, isGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat, native);
        return true;
    }

    private string CurrentAppId() => _box.Current.FeishuAppId?.Trim() ?? string.Empty;
    private static string IdentityKind(bool isGroup) => isGroup ? "group" : "participant";

    private sealed record FeishuSettingsSnapshot(string AppId, string AppSecret, string ApiBase);
    private sealed record FeishuTokenCache(FeishuSettingsSnapshot Settings, string Token, DateTimeOffset ExpiresAt);

    private static FeishuSettingsSnapshot Snapshot(PlatformOptions settings)
    {
        var baseUri = string.IsNullOrWhiteSpace(settings.FeishuApiBase) ? "https://open.feishu.cn" : settings.FeishuApiBase.TrimEnd('/');
        return new FeishuSettingsSnapshot(settings.FeishuAppId?.Trim() ?? string.Empty, settings.FeishuAppSecret ?? string.Empty, baseUri);
    }

    private (long Target, long Sender, long Message) MapInboundIds(PlatformOptions settings, bool isGroup, string target, string sender, string message)
    {
        var appId = settings.FeishuAppId?.Trim() ?? string.Empty;
        return (_ids.AliasFor(appId, IdentityKind(isGroup), target),
            _ids.AliasFor(appId, "participant", sender),
            _ids.AliasFor(appId, "message", message));
    }

    public async Task<(bool Handled, int StatusCode, string ResponseBody)> HandleWebhookAsync(
        string body,
        string? signature,
        string? timestamp,
        string? nonce,
        string? verificationToken = null,
        CancellationToken ct = default)
    {
        using var operation = _operations.TryEnter(ct);
        if (_disposed || operation is null) return (false, 503, "{\"error\":\"source_unavailable\"}");
        ct = operation.Token;
        var settings = _box.Current;
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(body))
            return (false, 400, "{\"error\":\"empty_body\"}");

        JsonNode? node;
        try { node = JsonNode.Parse(body); }
        catch { return (false, 400, "{\"error\":\"invalid_json\"}"); }

        ct.ThrowIfCancellationRequested();
        if (node is not JsonObject outerPayload)
        {
            LastErrorCode = "invalid_payload";
            return (false, 400, "{\"error\":\"invalid_payload\"}");
        }

        if (TryUnwrapEncryptedEnvelope(body, outerPayload, settings, signature, timestamp, nonce,
                out var payload, out var decryptedWithEncryptKey, out var unsignedEncryptedMode) is { } unwrapError)
        {
            return unwrapError;
        }

        if (!TryGetOptionalString(payload, "token", out var payloadToken)
            || !TryGetOptionalObject(payload, "header", out var outerHeader)
            || !TryGetOptionalString(outerHeader, "token", out var headerToken)
            || !TryGetOptionalString(payload, "type", out var type)
            || !TryGetOptionalString(payload, "challenge", out var challenge))
        {
            LastErrorCode = unsignedEncryptedMode ? "unauthorized" : "invalid_payload";
            return unsignedEncryptedMode
                ? (false, 401, "{\"error\":\"unauthorized\"}")
                : (false, 400, "{\"error\":\"invalid_payload\"}");
        }

        var suppliedVerificationToken = !string.IsNullOrWhiteSpace(verificationToken)
            ? verificationToken.Trim()
            : !string.IsNullOrWhiteSpace(payloadToken)
                ? payloadToken.Trim()
                : headerToken?.Trim();
        if (TryHandleUrlVerification(settings, body, type, challenge, suppliedVerificationToken,
                decryptedWithEncryptKey, unsignedEncryptedMode, signature, timestamp, nonce, ct) is { } challengeResult)
        {
            return challengeResult;
        }

        var configuredVerificationToken = settings.FeishuVerificationToken?.Trim() ?? string.Empty;
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

        return await DispatchVerifiedEventAsync(payload, settings, nonce, !string.IsNullOrWhiteSpace(settings.FeishuEncryptKey), ct)
            .ConfigureAwait(false);
    }

    private async Task<(bool Handled, int StatusCode, string ResponseBody)> DispatchVerifiedEventAsync(
        JsonObject payload,
        PlatformOptions settings,
        string? nonce,
        bool signed,
        CancellationToken ct)
    {
        if (!TryGetOptionalObject(payload, "header", out var header)
            || !TryGetOptionalString(header, "event_type", out var eventType)
            || !TryGetOptionalString(header, "event_id", out var eventIdValue))
        {
            LastErrorCode = "invalid_payload";
            return (false, 400, "{\"error\":\"invalid_payload\"}");
        }

        if (string.Equals(eventType, "card.action.trigger", StringComparison.OrdinalIgnoreCase))
            return TryDispatchCardAction(header, payload, settings, nonce, signed, ct);

        if (!string.Equals(eventType, "im.message.receive_v1", StringComparison.OrdinalIgnoreCase))
            return (true, 200, "{\"code\":0,\"msg\":\"ignored_event_type\"}");

        if (!TryGetOptionalObject(payload, "event", out var evt) || evt is null
            || !TryGetOptionalObject(evt, "message", out var messageNode) || messageNode is null
            || !TryGetOptionalString(messageNode, "message_id", out var messageIdValue)
            || !TryGetOptionalString(messageNode, "chat_type", out var chatTypeValue)
            || !TryGetOptionalString(messageNode, "chat_id", out var chatIdValue)
            || !TryGetOptionalString(messageNode, "content", out var contentStr)
            || !TryGetOptionalString(messageNode, "message_type", out var messageTypeValue)
            || !TryGetOptionalString(messageNode, "parent_id", out var parentIdValue)
            || !TryGetOptionalString(messageNode, "root_id", out var rootIdValue)
            || !TryGetOptionalString(messageNode, "thread_id", out var threadIdValue)
            || !TryGetOptionalArray(messageNode, "mentions", out var mentions)
            || !TryGetOptionalObject(evt, "sender", out var sender)
            || !TryGetOptionalString(sender, "sender_type", out var senderType)
            || !TryGetOptionalObject(sender, "sender_id", out var senderId)
            || !TryGetOptionalString(senderId, "open_id", out var senderOpenIdValue)
            || !TryGetOptionalString(senderId, "user_id", out var senderUserIdValue))
        {
            LastErrorCode = "invalid_payload";
            return (false, 400, "{\"error\":\"invalid_payload\"}");
        }

        var eventId = eventIdValue?.Trim() ?? string.Empty;
        var messageId = messageIdValue?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(eventId) && string.IsNullOrWhiteSpace(messageId))
            return (false, 400, "{\"error\":\"missing_event_id\"}");

        if (string.Equals(senderType, "app", StringComparison.OrdinalIgnoreCase))
            return (true, 200, "{\"code\":0,\"msg\":\"self_ignored\"}");

        var isGroup = !string.Equals(chatTypeValue ?? "group", "p2p", StringComparison.OrdinalIgnoreCase);
        RememberKnownChat(chatIdValue, isGroup);
        var resolvedSender = !string.IsNullOrWhiteSpace(senderOpenIdValue) ? senderOpenIdValue.Trim()
            : !string.IsNullOrWhiteSpace(senderUserIdValue) ? senderUserIdValue.Trim()
            : null;
        if (resolvedSender is null && !isGroup)
            return (false, 400, "{\"error\":\"missing_sender\"}");
        var hasReliableSender = resolvedSender is not null;
        var senderOpenId = resolvedSender ?? "unknown_user";
        var targetRaw = isGroup ? (chatIdValue ?? string.Empty) : senderOpenId;
        if (string.IsNullOrWhiteSpace(targetRaw))
            return (false, 400, "{\"error\":\"missing_target\"}");

        if (!IsAllowed(settings, isGroup, targetRaw))
        {
            _log?.Invoke($"[飞书] 忽略未在白名单中的消息（目标长度={targetRaw.Length}）");
            return (true, 200, "{\"code\":0,\"msg\":\"not_whitelisted\"}");
        }

        var snapshot = Snapshot(settings);
        string? botOpenId = null;
        if (mentions is { Count: > 0 } || string.Equals(messageTypeValue, "post", StringComparison.OrdinalIgnoreCase))
        {
            using var infoCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            infoCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
            botOpenId = await ResolveBotOpenIdAsync(snapshot, infoCts.Token).ConfigureAwait(false);
        }
        if (!string.IsNullOrEmpty(botOpenId) && string.Equals(senderOpenId, botOpenId, StringComparison.Ordinal))
            return (true, 200, "{\"code\":0,\"msg\":\"self_ignored\"}");

        var parsed = FeishuInboundRichParser.Parse(messageTypeValue, contentStr ?? string.Empty, messageId, mentions, botOpenId);
        var (internalTarget, internalSender, internalMsgId) = MapInboundIds(settings, isGroup, targetRaw, senderOpenId, messageId);
        if (internalTarget == 0 || internalSender == 0 || internalMsgId == 0)
            return (false, 503, "{\"error\":\"identity_unavailable\"}");

        var rawReplyParent = !string.IsNullOrWhiteSpace(parentIdValue) ? parentIdValue!.Trim()
            : !string.IsNullOrWhiteSpace(rootIdValue) ? rootIdValue!.Trim() : null;
        long? replyAlias = !string.IsNullOrEmpty(rawReplyParent)
            ? _ids.AliasFor(snapshot.AppId, "message", rawReplyParent!)
            : null;
        if (replyAlias == 0) replyAlias = null;

        ct.ThrowIfCancellationRequested();
        var dedupKey = !string.IsNullOrWhiteSpace(eventId) ? $"event:{eventId}" : $"message:{messageId}";
        var registration = TryRegisterWebhook(dedupKey, nonce, signed);
        if (registration.Unavailable)
            return (false, 503, "{\"error\":\"dedup_unavailable\"}");
        if (!registration.Registered)
            return (true, 200, "{\"code\":0,\"msg\":\"duplicate\"}");

        RememberThreadTopology(messageId, rootIdValue, threadIdValue);
        var displaySender = ResolveCachedSenderName(snapshot.AppId, senderOpenId);
        TryWarmSenderNameBackground(snapshot, senderOpenId);

        var qqMsg = new QqChatMessage(
            MessageId: internalMsgId,
            IsGroup: isGroup,
            UserId: internalSender,
            GroupId: isGroup ? internalTarget : 0,
            SenderName: displaySender,
            Text: parsed.Text,
            Time: Clock.Now,
            MentionedSelf: parsed.MentionedSelf,
            ImageUrls: parsed.ImageUrls,
            ReplyToMessageId: replyAlias,
            Channel: Channels.Feishu)
        {
            AllowsCommands = parsed.AllowsCommands,
            VoiceAttachments = parsed.Voices,
            FileAttachments = parsed.Files,
            IsReliableIdentity = hasReliableSender ? null : false
        };

        var convId = new ConversationId(PlatformId.Feishu, AccountScope.Default, isGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat, targetRaw);
        var inMsg = new InboundMessage(
            Ref: new MessageRef(convId, messageId),
            Sender: new ParticipantId(PlatformId.Feishu, AccountScope.Default, senderOpenId),
            SenderName: displaySender,
            Text: parsed.Text,
            Timestamp: Clock.Now,
            ReplyToMessageId: rawReplyParent,
            IsMentioned: parsed.MentionedSelf,
            ImageUrls: parsed.ImageUrls)
        {
            AllowsCommands = parsed.AllowsCommands,
            VoiceAttachments = parsed.Voices,
            FileAttachments = parsed.Files
        };

        _log?.Invoke($"[飞书] 收到入站消息（type={messageTypeValue ?? "text"}，字数={parsed.Text.Length}）");
        ct.ThrowIfCancellationRequested();
        RichInboundReceived?.Invoke(qqMsg, inMsg);
        ct.ThrowIfCancellationRequested();
        MessageReceived?.Invoke(qqMsg);
        ct.ThrowIfCancellationRequested();
        InboundReceived?.Invoke(inMsg);
        return (true, 200, "{\"code\":0}");
    }

    private void RememberKnownChat(string? chatId, bool isGroup)
    {
        var id = chatId?.Trim();
        if (string.IsNullOrEmpty(id)) return;
        if (_knownChats.ContainsKey(id) || _knownChats.Count < KnownChatCapacity)
            _knownChats[id] = isGroup;
    }

    private (bool Handled, int StatusCode, string ResponseBody) TryDispatchCardAction(
        JsonObject? header,
        JsonObject payload,
        PlatformOptions settings,
        string? nonce,
        bool signed,
        CancellationToken ct)
    {
        if (!TryGetOptionalObject(payload, "event", out var evt) || evt is null
            || !TryGetOptionalString(header, "event_id", out var eventIdValue))
            return (false, 400, "{\"error\":\"invalid_payload\"}");

        var eventId = eventIdValue?.Trim();
        if (!TryGetOptionalObject(evt, "operator", out var op) || op is null
            || !TryGetOptionalString(op, "open_id", out var operatorOpenIdValue) || string.IsNullOrWhiteSpace(operatorOpenIdValue))
            return (true, 200, "{\"code\":0,\"msg\":\"ignored_card_operator\"}");

        var operatorOpenId = operatorOpenIdValue!.Trim();
        if (!TryGetOptionalObject(evt, "context", out var ctxObj)
            || !TryGetOptionalString(ctxObj, "open_chat_id", out var openChatIdValue)
            || !TryGetOptionalString(ctxObj, "chat_type", out var chatTypeValue)
            || !TryGetOptionalObject(evt, "action", out var actionObj)
            || !TryGetOptionalObject(actionObj, "value", out var valueObj)
            || !TryGetOptionalString(evt, "token", out var eventTokenValue))
            return (false, 400, "{\"error\":\"invalid_payload\"}");

        TryGetOptionalString(valueObj, "approval_cmd", out var approvalCmdValue);
        TryGetOptionalString(valueObj, "memory_cmd", out var memoryCmdValue);
        if (string.IsNullOrWhiteSpace(approvalCmdValue) && string.IsNullOrWhiteSpace(memoryCmdValue))
            return (false, 400, "{\"error\":\"invalid_payload\"}");

        var openChatId = openChatIdValue?.Trim();
        var chatType = chatTypeValue?.Trim();
        var actionId = !string.IsNullOrWhiteSpace(eventId)
            ? eventId!
            : eventTokenValue?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(actionId))
            return (false, 400, "{\"error\":\"missing_event_id\"}");

        if (!string.IsNullOrWhiteSpace(approvalCmdValue))
        {
            var approvalCmd = approvalCmdValue.Trim();
            if (!BotAgent.Platforms.Common.ApprovalCardHelper.IsSafeApprovalButtonCommand(approvalCmd))
                return (true, 200, "{\"code\":0,\"msg\":\"ignored_card_action\"}");

            var isGroup = string.Equals(chatType, "group", StringComparison.OrdinalIgnoreCase)
                ? true
                : string.Equals(chatType, "p2p", StringComparison.OrdinalIgnoreCase)
                    ? false
                    : !string.IsNullOrWhiteSpace(openChatId) && _knownChats.TryGetValue(openChatId, out var knownGrp)
                        ? knownGrp
                        : !string.IsNullOrWhiteSpace(openChatId)
                            && !string.Equals(openChatId, operatorOpenId, StringComparison.Ordinal);
            var targetRaw = isGroup ? (openChatId ?? string.Empty) : operatorOpenId;
            if (string.IsNullOrWhiteSpace(targetRaw))
                return (false, 400, "{\"error\":\"missing_target\"}");
            if (!IsAllowed(settings, isGroup, targetRaw))
                return (true, 200, "{\"code\":0,\"msg\":\"not_whitelisted\"}");

            var apSnapshot = Snapshot(settings);
            var (apTarget, apSender, apMsgId) = MapInboundIds(settings, isGroup, targetRaw, operatorOpenId, "card:" + actionId);
            if (apTarget == 0 || apSender == 0 || apMsgId == 0)
                return (false, 503, "{\"error\":\"identity_unavailable\"}");

            var apReg = TryRegisterWebhook($"card:{actionId}", nonce, signed);
            if (apReg.Unavailable) return (false, 503, "{\"error\":\"dedup_unavailable\"}");
            if (!apReg.Registered) return (true, 200, "{\"toast\":{\"type\":\"info\",\"content\":\"该审批已提交过\"}}");

            var apSenderName = ResolveCachedSenderName(apSnapshot.AppId, operatorOpenId);
            var apQqMsg = new QqChatMessage(
                MessageId: apMsgId, IsGroup: isGroup, UserId: apSender, GroupId: isGroup ? apTarget : 0,
                SenderName: apSenderName, Text: approvalCmd, Time: Clock.Now,
                MentionedSelf: true, SenderRole: null, Channel: Channels.Feishu)
            { AllowsCommands = true };
            var apConvId = new ConversationId(
                PlatformId.Feishu, AccountScope.Default,
                isGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat, targetRaw);
            var apInMsg = new InboundMessage(
                new MessageRef(apConvId, "card:" + actionId),
                new ParticipantId(PlatformId.Feishu, AccountScope.Default, operatorOpenId),
                apSenderName, approvalCmd, Clock.Now, IsMentioned: true);

            ct.ThrowIfCancellationRequested();
            RichInboundReceived?.Invoke(apQqMsg, apInMsg);
            MessageReceived?.Invoke(apQqMsg);
            InboundReceived?.Invoke(apInMsg);
            return (true, 200, "{\"toast\":{\"type\":\"success\",\"content\":\"已提交审批指令\"}}");
        }

        var isPrivate = string.Equals(chatType, "p2p", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrWhiteSpace(chatType)
                && !string.IsNullOrWhiteSpace(openChatId)
                && ((_knownChats.TryGetValue(openChatId, out var knownGroup) && !knownGroup)
                    || string.Equals(openChatId, operatorOpenId, StringComparison.Ordinal)));
        if (!isPrivate)
            return (true, 200, "{\"toast\":{\"type\":\"warning\",\"content\":\"记忆确认仅限本人私聊\"}}");

        var memoryCmd = memoryCmdValue?.Trim();
        if (!BotAgent.Platforms.Common.MemoryPrivateCardHelper.IsSafePrivateMemoryButtonCommand(memoryCmd))
            return (true, 200, "{\"code\":0,\"msg\":\"ignored_card_action\"}");

        if (!IsAllowed(settings, false, operatorOpenId))
            return (true, 200, "{\"code\":0,\"msg\":\"not_whitelisted\"}");

        var snapshot = Snapshot(settings);
        var (internalTarget, internalSender, internalMsgId) = MapInboundIds(settings, false, operatorOpenId, operatorOpenId, "card:" + actionId);
        if (internalTarget == 0 || internalSender == 0 || internalMsgId == 0)
            return (false, 503, "{\"error\":\"identity_unavailable\"}");

        var registration = TryRegisterWebhook($"card:{actionId}", nonce, signed);
        if (registration.Unavailable) return (false, 503, "{\"error\":\"dedup_unavailable\"}");
        if (!registration.Registered) return (true, 200, "{\"toast\":{\"type\":\"info\",\"content\":\"该确认已处理\"}}");

        var displaySender = ResolveCachedSenderName(snapshot.AppId, operatorOpenId);
        var qqMsg = new QqChatMessage(
            MessageId: internalMsgId, IsGroup: false, UserId: internalSender, GroupId: 0,
            SenderName: displaySender, Text: memoryCmd!, Time: Clock.Now,
            MentionedSelf: true, Channel: Channels.Feishu)
        { AllowsCommands = true };
        var convId = new ConversationId(PlatformId.Feishu, AccountScope.Default, ConversationKind.PrivateChat, operatorOpenId);
        var inMsg = new InboundMessage(
            new MessageRef(convId, "card:" + actionId),
            new ParticipantId(PlatformId.Feishu, AccountScope.Default, operatorOpenId),
            displaySender, memoryCmd!, Clock.Now, IsMentioned: true);

        ct.ThrowIfCancellationRequested();
        RichInboundReceived?.Invoke(qqMsg, inMsg);
        MessageReceived?.Invoke(qqMsg);
        InboundReceived?.Invoke(inMsg);
        return (true, 200, "{\"toast\":{\"type\":\"success\",\"content\":\"已提交处理\"}}");
    }

    private bool IsAllowed(PlatformOptions settings, bool isGroup, string target)
    {
        if (settings.RuntimePlatformPolicies.HasValue)
        {
            var rules = PlatformSwitchSettings.ResolvePolicyRules(settings, Context, ImmutableArray<ActionId>.Empty);
            if (rules.Enabled != ToggleRule.Enabled || rules.ChatEnabled != ToggleRule.Enabled) return false;
            var rule = isGroup ? rules.GroupWhitelist : rules.PrivateWhitelist;
            if (rule.Kind == ListRuleKind.AllowAll) return true;
            return rule.TryGetExplicitValue(out var explicitList) && MatchesWhitelist(settings, isGroup, target, explicitList);
        }
        var policy = (settings.PlatformPolicies ?? new List<PlatformPolicySettings>())
            .FirstOrDefault(p => p is not null && string.Equals(PlatformId.Normalize(p.PlatformId), PlatformId.Feishu, StringComparison.OrdinalIgnoreCase));
        var policyWl = isGroup ? policy?.GroupWhitelist : policy?.PrivateWhitelist;
        var wl = !string.IsNullOrWhiteSpace(policyWl)
            ? policyWl
            : isGroup || string.IsNullOrWhiteSpace(settings.FeishuWhitelistPrivates)
                ? settings.FeishuWhitelist
                : settings.FeishuWhitelistPrivates;
        return MatchesWhitelist(settings, isGroup, target, wl);
    }

    private bool MatchesWhitelist(PlatformOptions settings, bool isGroup, string target, string? wl)
    {
        if (string.IsNullOrWhiteSpace(wl)) return false;
        var tokens = wl.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Contains("*") || tokens.Contains("all", StringComparer.OrdinalIgnoreCase)) return true;
        if (tokens.Contains(target, StringComparer.OrdinalIgnoreCase)) return true;
        var alias = _ids.AliasFor(settings.FeishuAppId?.Trim() ?? string.Empty, IdentityKind(isGroup), target);
        return alias != 0 && tokens.Contains(alias.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposedFlag, 1, 0) != 0) return;
        StopAsync().GetAwaiter().GetResult();
        _operations.Dispose();
        _tokenGate.Dispose();
        _botInfoGate.Dispose();
        _wsLifecycleGate.Dispose();
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _wsLifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _operations.Start();
            StartWsIfConfigured();
            ConnectionChanged?.Invoke(IsConnected);
        }
        finally
        {
            _wsLifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _wsLifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopWsAsync().ConfigureAwait(false);
            await _operations.StopAsync().ConfigureAwait(false);
            ConnectionChanged?.Invoke(false);
        }
        finally
        {
            _wsLifecycleGate.Release();
        }
    }
}
