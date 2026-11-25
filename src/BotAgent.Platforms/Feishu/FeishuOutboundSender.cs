using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Reply;
using BotAgent.Platforms;
using BotAgent.Services.Memory;
using BotAgent.Services.Platforms;
using BotAgent.Services.Qq;

namespace BotAgent.Adapters.Platforms.Feishu;

public sealed partial class FeishuBotGateway
{
    private const int MaxOutboundImageBytes = 10 * 1024 * 1024;

    public async Task<SendResult> SendTextAsync(
        bool isGroup,
        long targetId,
        string text,
        CancellationToken ct = default,
        long? replyToMessageId = null,
        bool directAddress = false)
    {
        var policySettings = _box.Current;
        var settings = Snapshot(policySettings);
        var rawTarget = _ids.OriginalOf(targetId, settings.AppId, IdentityKind(isGroup));
        if (string.IsNullOrEmpty(rawTarget))
            return new SendResult(false, 0);

        var rawReply = replyToMessageId.HasValue ? _ids.OriginalOf(replyToMessageId.Value, settings.AppId, "message") : null;
        if (replyToMessageId.HasValue && rawReply is null)
            return new SendResult(false, 0);

        string? threadId = null;
        if (!string.IsNullOrEmpty(rawReply) && _threadByMessageId.TryGetValue(rawReply, out var knownThread))
            threadId = knownThread;

        var conv = new ConversationId(
            PlatformId.Feishu,
            AccountScope.Default,
            isGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat,
            rawTarget,
            threadId);
        var outMsg = new OutboundMessage(conv, text, rawReply);
        var res = await SendWithSnapshotAsync(Context, outMsg, settings, policySettings, ct).ConfigureAwait(false);
        var numId = res.IsSuccess && !string.IsNullOrWhiteSpace(res.MessageId)
            ? _ids.AliasFor(settings.AppId, "message", res.MessageId) : 0;
        return new SendResult(res.IsSuccess, numId) { Delivery = res };
    }

    public async Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
        => (await SendImageWithReceiptAsync(isGroup, targetId, data, ct, replyToMessageId).ConfigureAwait(false)).Ok;

    public async Task<SendResult> SendImageWithReceiptAsync(
        bool isGroup,
        long targetId,
        byte[] data,
        CancellationToken ct = default,
        long? replyToMessageId = null)
    {
        if (data is not { Length: > 0 } || data.Length > MaxOutboundImageBytes)
            return new SendResult(false, 0) { Delivery = DeliveryResult.Rejected("media_size_invalid") };

        var policySettings = _box.Current;
        var settings = Snapshot(policySettings);
        var rawTarget = _ids.OriginalOf(targetId, settings.AppId, IdentityKind(isGroup));
        if (string.IsNullOrEmpty(rawTarget))
            return new SendResult(false, 0) { Delivery = DeliveryResult.Rejected("unknown_target") };

        var rawReply = replyToMessageId.HasValue ? _ids.OriginalOf(replyToMessageId.Value, settings.AppId, "message") : null;
        if (replyToMessageId.HasValue && rawReply is null)
            return new SendResult(false, 0) { Delivery = DeliveryResult.Rejected("unknown_reply_target") };

        string? threadId = null;
        if (!string.IsNullOrEmpty(rawReply) && _threadByMessageId.TryGetValue(rawReply, out var knownThread))
            threadId = knownThread;

        var conv = new ConversationId(
            PlatformId.Feishu,
            AccountScope.Default,
            isGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat,
            rawTarget,
            threadId);
        var outMsg = new OutboundMessage(conv, string.Empty, rawReply, ImageData: data.ToArray());
        var res = await SendWithSnapshotAsync(Context, outMsg, settings, policySettings, ct).ConfigureAwait(false);
        var numId = res.IsSuccess && !string.IsNullOrWhiteSpace(res.MessageId)
            ? _ids.AliasFor(settings.AppId, "message", res.MessageId) : 0;
        return new SendResult(res.IsSuccess, numId) { Delivery = res };
    }

    public async Task<DeliveryResult> RecallOwnMessageAsync(bool isGroup, long targetId, long messageId, CancellationToken ct = default)
    {
        using var op = _operations.TryEnter(ct);
        if (_disposed || op is null || messageId <= 0)
            return DeliveryResult.Rejected("source_unavailable");

        if (!_outbox.Any(o => o.MessageId == messageId))
            return DeliveryResult.Rejected("not_own_message");

        var settings = Snapshot(_box.Current);
        var rawMid = _ids.OriginalOf(messageId, settings.AppId, "message");
        if (string.IsNullOrWhiteSpace(rawMid))
            return DeliveryResult.Rejected("unknown_message");

        try
        {
            var token = await GetTenantTokenAsync(settings, op.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
                return DeliveryResult.Transient("feishu_not_configured");

            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{settings.ApiBase}/open-apis/im/v1/messages/{Uri.EscapeDataString(rawMid)}");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var resp = await _http.SendAsync(req, op.Token).ConfigureAwait(false);
            var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(op.Token).ConfigureAwait(false)) as JsonObject;
            var code = root?["code"]?.GetValue<int>() ?? -1;
            return resp.IsSuccessStatusCode && code == 0
                ? DeliveryResult.Ok(rawMid)
                : DeliveryResult.Rejected($"feishu_recall_failed_{code}");
        }
        catch
        {
            return DeliveryResult.Transient("network_error");
        }
    }

    public Task<DeliveryResult> SendAsync(
        PlatformContext context,
        OutboundMessage message,
        CancellationToken ct = default)
    {
        var settings = _box.Current;
        return SendWithSnapshotAsync(context, message, Snapshot(settings), settings, ct);
    }

    private async Task<DeliveryResult> SendWithSnapshotAsync(
        PlatformContext context,
        OutboundMessage message,
        FeishuSettingsSnapshot settings,
        PlatformOptions policySettings,
        CancellationToken ct)
    {
        using var operation = _operations.TryEnter(ct);
        if (_disposed || operation is null) return DeliveryResult.Rejected("source_unavailable");
        ct = operation.Token;

        var precheckError = ValidateOutboundEnvelope(context, message, policySettings);
        if (precheckError is not null)
        {
            LastErrorCode = precheckError;
            return DeliveryResult.Rejected(precheckError);
        }

        using var destination = MemoryUseScope.EnterDestination(
            new(Context.PlatformId, Context.AccountScope, Context.InstanceId), message.Target);

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
            var reason = LastErrorCode == "feishu_token_invalid" ? LastErrorCode : "feishu_not_configured";
            LastErrorCode = reason;
            return DeliveryResult.Transient(reason);
        }

        var isGroup = message.Target.Kind == ConversationKind.GroupChat;
        var rawTarget = message.Target.NativeTargetId;
        var postAuthError = RevalidateOutboundAfterAwait(settings, isGroup, rawTarget, ct);
        if (postAuthError is not null)
        {
            LastErrorCode = postAuthError;
            return DeliveryResult.Rejected(postAuthError);
        }

        if (message.ImageData is { Length: > 0 })
        {
            var imgResult = await DispatchImageAndOptionalTextAsync(message, settings, token, isGroup, rawTarget, ct).ConfigureAwait(false);
            return imgResult;
        }

        if (!isGroup && EnableInteractiveCards && MemoryUseScope.IsCommandResponse
            && BotAgent.Platforms.Common.MemoryPrivateCardHelper.TryBuildFeishuCardJson(message.Text, out var memoryCardJson))
        {
            var cardPrimary = await PostRawFeishuMessageAsync(
                message, settings, token, isGroup, rawTarget, "interactive", memoryCardJson, message.Text, ct).ConfigureAwait(false);
            if (ShouldFallbackFromInteractiveCard(cardPrimary))
                return await PostOutboundPayloadAsync(message, settings, token, isGroup, rawTarget, useInteractiveCard: false, ct).ConfigureAwait(false);
            return cardPrimary;
        }

        if (EnableInteractiveCards && BotAgent.Domain.Permissions.ApprovalOutboundScope.IsSystemAnnouncement
            && BotAgent.Platforms.Common.ApprovalCardHelper.TryBuildFeishuCardJson(message.Text, out var approvalCardJson))
        {
            var cardPrimary = await PostRawFeishuMessageAsync(
                message, settings, token, isGroup, rawTarget, "interactive", approvalCardJson, message.Text, ct).ConfigureAwait(false);
            if (ShouldFallbackFromInteractiveCard(cardPrimary))
                return await PostOutboundPayloadAsync(message, settings, token, isGroup, rawTarget, useInteractiveCard: false, ct).ConfigureAwait(false);
            return cardPrimary;
        }

        var useCard = EnableInteractiveCards && ShouldRenderInteractiveCard(message.Text);
        var primary = await PostOutboundPayloadAsync(message, settings, token, isGroup, rawTarget, useCard, ct).ConfigureAwait(false);
        if (useCard && ShouldFallbackFromInteractiveCard(primary))
        {
            return await PostOutboundPayloadAsync(message, settings, token, isGroup, rawTarget, useInteractiveCard: false, ct).ConfigureAwait(false);
        }
        return primary;
    }

    private bool ShouldFallbackFromInteractiveCard(DeliveryResult result)
        => !result.IsSuccess
            && (result.ReasonCode?.StartsWith("feishu_err_", StringComparison.Ordinal) == true
                || (result.ReasonCode == "feishu_http_error" && LastErrorCode?.StartsWith("http_4", StringComparison.Ordinal) == true));

    private string? ValidateOutboundEnvelope(PlatformContext? context, OutboundMessage? message, PlatformOptions policySettings)
    {
        if (context is null || message is null || message.Target is null)
            return "bad_request";

        var expectedPlatform = PlatformId.Normalize(Context.PlatformId);
        if (string.IsNullOrWhiteSpace(context.PlatformId) || string.IsNullOrWhiteSpace(message.Target.PlatformId)
            || !string.Equals(PlatformId.Normalize(context.PlatformId), expectedPlatform, StringComparison.Ordinal)
            || !string.Equals(PlatformId.Normalize(message.Target.PlatformId), expectedPlatform, StringComparison.Ordinal)
            || !string.Equals(context.AccountScope, Context.AccountScope, StringComparison.Ordinal)
            || !string.Equals(context.InstanceId, Context.InstanceId, StringComparison.Ordinal)
            || !string.Equals(message.Target.AccountScope, Context.AccountScope, StringComparison.Ordinal)
            || message.Target.Kind is not (ConversationKind.GroupChat or ConversationKind.PrivateChat)
            || string.IsNullOrWhiteSpace(message.Target.NativeTargetId))
        {
            return "context_mismatch";
        }

        if (!string.IsNullOrWhiteSpace(message.VoiceUrl))
            return "unsupported_media";
        if (message.ImageData is { Length: > MaxOutboundImageBytes })
            return "media_size_invalid";
        if (message.ImageData is not { Length: > 0 } && string.IsNullOrWhiteSpace(message.Text))
            return "empty_content";

        if (!string.IsNullOrEmpty(message.Target.ThreadId)
            && string.IsNullOrWhiteSpace(ResolveThreadReplyMessageId(message)))
        {
            return "unsupported_thread";
        }

        var legacySwitches = PlatformSwitchSettings.Read(policySettings, Context.PlatformId, Context.AccountScope);
        if ((!policySettings.RuntimePlatformPolicies.HasValue && (!legacySwitches.Enabled || !legacySwitches.ChatEnabled))
            || !IsAllowed(policySettings, message.Target.Kind == ConversationKind.GroupChat, message.Target.NativeTargetId))
        {
            return "platform_policy_rejected";
        }

        return null;
    }

    private string? RevalidateOutboundAfterAwait(FeishuSettingsSnapshot settings, bool isGroup, string rawTarget, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var currentSettings = _box.Current;
        if (settings != Snapshot(currentSettings))
            return "source_configuration_changed";

        var currentSwitches = PlatformSwitchSettings.Read(currentSettings, Context.PlatformId, Context.AccountScope);
        if (!Configured(currentSettings)
            || (!currentSettings.RuntimePlatformPolicies.HasValue && (!currentSwitches.Enabled || !currentSwitches.ChatEnabled))
            || !IsAllowed(currentSettings, isGroup, rawTarget))
        {
            return "platform_policy_rejected";
        }

        return null;
    }

    private string? ResolveThreadReplyMessageId(OutboundMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.ReplyToMessageId))
            return message.ReplyToMessageId.Trim();
        var tid = message.Target.ThreadId?.Trim();
        if (string.IsNullOrEmpty(tid)) return null;
        if (_rootByThreadId.TryGetValue(tid, out var rootMid) && !string.IsNullOrWhiteSpace(rootMid))
            return rootMid;
        if (tid.StartsWith("om_", StringComparison.Ordinal))
            return tid;
        return null;
    }

    private async Task<DeliveryResult> DispatchImageAndOptionalTextAsync(
        OutboundMessage message,
        FeishuSettingsSnapshot settings,
        string token,
        bool isGroup,
        string rawTarget,
        CancellationToken ct)
    {
        var imageKey = await UploadFeishuImageAsync(settings, token, message.ImageData!, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(imageKey))
            return DeliveryResult.Transient(LastErrorCode ?? "feishu_image_upload_failed");

        var imgSend = await PostRawFeishuMessageAsync(
            message, settings, token, isGroup, rawTarget,
            "image", new JsonObject { ["image_key"] = imageKey }.ToJsonString(), "[图片]", ct).ConfigureAwait(false);
        if (!imgSend.IsSuccess || string.IsNullOrWhiteSpace(message.Text))
            return imgSend;

        var textOnly = message with { ImageData = null };
        if (!isGroup && EnableInteractiveCards && MemoryUseScope.IsCommandResponse
            && BotAgent.Platforms.Common.MemoryPrivateCardHelper.TryBuildFeishuCardJson(textOnly.Text, out var memoryCardJson))
        {
            var cardPrimary = await PostRawFeishuMessageAsync(
                textOnly, settings, token, isGroup, rawTarget, "interactive", memoryCardJson, textOnly.Text, ct).ConfigureAwait(false);
            if (ShouldFallbackFromInteractiveCard(cardPrimary))
                return await PostOutboundPayloadAsync(textOnly, settings, token, isGroup, rawTarget, useInteractiveCard: false, ct).ConfigureAwait(false);
            return cardPrimary;
        }

        var useCard = EnableInteractiveCards && ShouldRenderInteractiveCard(textOnly.Text);
        return await PostOutboundPayloadAsync(
            textOnly, settings, token, isGroup, rawTarget, useCard, ct).ConfigureAwait(false);
    }

    private async Task<string?> UploadFeishuImageAsync(
        FeishuSettingsSnapshot settings,
        string token,
        byte[] imageData,
        CancellationToken ct)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("message"), "image_type");
            var part = new ByteArrayContent(imageData);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "image", "image.png");

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{settings.ApiBase}/open-apis/im/v1/images") { Content = form };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var resp = await MemoryUseScope.UseForDelivery(() => _http.SendAsync(req, ct)).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var root = JsonNode.Parse(body) as JsonObject;
            var code = root?["code"]?.GetValue<int>() ?? -1;
            var key = (root?["data"] as JsonObject)?["image_key"]?.GetValue<string>()?.Trim();
            if (!resp.IsSuccessStatusCode || code != 0 || string.IsNullOrEmpty(key))
            {
                LastErrorCode = !resp.IsSuccessStatusCode ? $"http_{(int)resp.StatusCode}" : $"feishu_err_{code}";
                return null;
            }
            return key;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LastErrorCode = "cancelled";
            return null;
        }
        catch
        {
            LastErrorCode = "network_error";
            return null;
        }
    }

    private Task<DeliveryResult> PostOutboundPayloadAsync(
        OutboundMessage message,
        FeishuSettingsSnapshot settings,
        string token,
        bool isGroup,
        string rawTarget,
        bool useInteractiveCard,
        CancellationToken ct)
    {
        var msgType = useInteractiveCard ? "interactive" : "text";
        var contentJson = useInteractiveCard
            ? BuildSchema2MarkdownCardJson(message.Text)
            : new JsonObject { ["text"] = message.Text }.ToJsonString();
        return PostRawFeishuMessageAsync(message, settings, token, isGroup, rawTarget, msgType, contentJson, message.Text, ct);
    }

    private async Task<DeliveryResult> PostRawFeishuMessageAsync(
        OutboundMessage message,
        FeishuSettingsSnapshot settings,
        string token,
        bool isGroup,
        string rawTarget,
        string msgType,
        string contentJson,
        string outboxSummary,
        CancellationToken ct)
    {
        var replyTargetMid = ResolveThreadReplyMessageId(message);
        var replyInThread = !string.IsNullOrEmpty(message.Target.ThreadId)
            || (!string.IsNullOrEmpty(replyTargetMid) && _threadByMessageId.ContainsKey(replyTargetMid));

        var receiveType = isGroup ? "chat_id" : "open_id";
        var url = string.IsNullOrWhiteSpace(replyTargetMid)
            ? $"{settings.ApiBase}/open-apis/im/v1/messages?receive_id_type={receiveType}"
            : $"{settings.ApiBase}/open-apis/im/v1/messages/{Uri.EscapeDataString(replyTargetMid)}/reply";

        var payload = new JsonObject
        {
            ["msg_type"] = msgType,
            ["content"] = contentJson,
        };
        if (string.IsNullOrWhiteSpace(replyTargetMid))
            payload["receive_id"] = rawTarget;
        else if (replyInThread)
            payload["reply_in_thread"] = true;

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        try
        {
            using var commitValidation = ReplyCommitBoundary.EnterValidation(() =>
            {
                if (RevalidateOutboundAfterAwait(settings, isGroup, rawTarget, ct) is not null)
                    throw new OperationCanceledException("reply_commit_rejected");
            });
            await ReplyCommitBoundary.BeforeSendAsync().ConfigureAwait(false);
            using var resp = await MemoryUseScope.UseForDelivery(() => _http.SendAsync(req, ct)).ConfigureAwait(false);
            var respText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonNode? respNode = null;
            try { respNode = JsonNode.Parse(respText); } catch (JsonException) { }
            var code = respNode?["code"]?.GetValue<int>();

            if ((int)resp.StatusCode == 429)
            {
                LastErrorCode = "throttled";
                return DeliveryResult.Throttled(5, "feishu_rate_limited");
            }
            if (code is > 0)
            {
                LastErrorCode = $"feishu_err_{code.Value}";
                return DeliveryResult.Transient(LastErrorCode);
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
            var key = ConversationIdCodec.Encode(message.Target with { ThreadId = null });
            _outbox.Enqueue(new FeishuOutboxItem(_ids.AliasFor(settings.AppId, "message", mid), key, outboxSummary, Clock.Now));
            while (_outbox.Count > OutboxCapacity) _outbox.TryDequeue(out _);
            _log?.Invoke($"[飞书] 消息已投递（type={msgType}，目标长度={rawTarget.Length}，长度={outboxSummary.Length}）");
            return DeliveryResult.Ok(mid);
        }
        catch (OperationCanceledException) when (ReplyCommitBoundary.IsActive)
        {
            throw;
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

    internal static bool ShouldRenderInteractiveCard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("```", StringComparison.Ordinal)
            || text.Contains("\n| ", StringComparison.Ordinal)
            || text.StartsWith("| ", StringComparison.Ordinal)
            || text.StartsWith("# ", StringComparison.Ordinal)
            || text.StartsWith("## ", StringComparison.Ordinal)
            || text.StartsWith("### ", StringComparison.Ordinal)
            || text.Contains("\n# ", StringComparison.Ordinal)
            || text.Contains("\n## ", StringComparison.Ordinal)
            || text.Contains("\n### ", StringComparison.Ordinal);
    }

    internal static string BuildSchema2MarkdownCardJson(string markdown)
    {
        var card = new JsonObject
        {
            ["schema"] = "2.0",
            ["config"] = new JsonObject { ["wide_screen_mode"] = true },
            ["body"] = new JsonObject
            {
                ["elements"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["tag"] = "markdown",
                        ["content"] = markdown
                    }
                }
            }
        };
        return card.ToJsonString();
    }
}
