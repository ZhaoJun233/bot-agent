using System.Globalization;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Tools;
using BotAgent.Services.Platforms;
using BotAgent.Services.Qq;
using BotAgent.Services.Tools;

namespace BotAgent.Services.Agent;

/// <summary>Official actions are scoped to the authenticated source and its current conversation.</summary>
public sealed class OfficialSessionQqActionHost(PlatformSourceLease source, bool isGroup, long targetId,
    PlatformPolicyResolver? policies, Func<string, bool>? pluginAllows = null) : IQqActionHost, IToolExecutor
{
    public string ActionPlatform => PlatformId.QqOfficial;
    public string ContextLine => "当前为 QQ 官方会话。平台、应用和当前会话由宿主绑定；模型不能指定 AppID、角色或跨会话目标。";
    public string Id => QqToolSpecs.ExecutorActions;
    public string Implementation => "Services/Agent/OfficialSessionQqActionHost.cs";
    public bool LegacyPath => false;

    public bool CanUseAction(QqActionSpec spec)
        => ContextDenial() is null && QqActionCatalog.Supports(spec, ActionPlatform)
            && QqActionAuthorization.Denial(spec, policies?.Resolve(source.Context), pluginAllows, ActionPlatform) is null;

    public async Task<string> ExecuteAsync(QqActionSpec spec, JsonObject args, CancellationToken ct)
        => (await ExecuteAsync(new ToolCall("qq." + spec.Name, args, 0), ct)).ModelText ?? string.Empty;

    public async Task<ToolOutcome> ExecuteAsync(ToolCall call, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (ContextDenial() is { } contextDenial) return contextDenial;
        var raw = call.ToolId.StartsWith("qq.", StringComparison.Ordinal) ? call.ToolId[3..] : call.ToolId;
        var spec = QqActionCatalog.Find(raw);
        if (spec is null) return ToolOutcome.Failure("unknown_action", "动作不在现有目录中。");
        if (!QqActionCatalog.Supports(spec, ActionPlatform))
            return ToolOutcome.Failure("platform_action_unsupported", "当前官方动作宿主不支持此动作。");
        if (QqActionAuthorization.Denial(spec, policies?.Resolve(source.Context), pluginAllows, ActionPlatform) is { } denial) return denial;
        if (spec.Name == "recall") return await RecallAsync(call.Arguments, ct);
        if (!TrySendArguments(call.Arguments, out var text))
            return ToolOutcome.Failure("invalid_arguments", "send 需要文本，目标只能是当前会话；不接受应用、平台、角色等覆盖字段。");
        using var scope = PlatformSourceExecution.Enter(source);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, source.Lifetime);
        var result = await source.Source.SendTextAsync(isGroup, targetId, text, lifetime.Token);
        var receipt = result.MessageId > 0 ? $"（message_id={result.MessageId.ToString(CultureInfo.InvariantCulture)}）" : string.Empty;
        return SessionQqActionHost.SendOutcome(result, "✅ 消息已发送到当前官方会话" + receipt);
    }

    private async Task<ToolOutcome> RecallAsync(JsonObject args, CancellationToken ct)
    {
        long message = 0;
        foreach (var (name, value) in args)
        {
            if (name is "tool" or "action" or "thought") continue;
            if (name != "message_id" || value is not JsonValue scalar
                || !scalar.TryGetValue<long>(out message) && !(scalar.TryGetValue<string>(out var raw)
                    && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out message)))
                return ToolOutcome.Failure("invalid_arguments", "recall 只接受 message_id 数字别名；不接受 this、目标、应用或角色覆盖。");
        }
        if (message < Channels.AliasBase || message > 9_007_199_254_740_991L)
            return ToolOutcome.Failure("invalid_arguments", "message_id 必须是官方发送回执中的数字别名。");
        using var scope = PlatformSourceExecution.Enter(source);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, source.Lifetime);
        var result = await source.Source.RecallOwnMessageAsync(isGroup, targetId, message, lifetime.Token);
        var audit = $"status={result.Status};reason={result.ReasonCode}";
        return result.Status switch
        {
            DeliveryStatus.Success => ToolOutcome.Success("✅ 消息已撤回", audit),
            DeliveryStatus.Unknown => ToolOutcome.Failure("delivery_unknown", "撤回结果未确认；不要自动重试。", audit),
            _ => ToolOutcome.Failure("recall_failed", $"消息未撤回（{result.ReasonCode}）。", audit)
        };
    }

    private ToolOutcome? ContextDenial()
    {
        if (!source.IsCurrent) return ToolOutcome.Failure("source_retired", "来源已失效，本次动作未执行。");
        var target = new ConversationId(ActionPlatform, source.Context.AccountScope,
            isGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat, targetId.ToString(CultureInfo.InvariantCulture));
        return source.Context.PlatformId != ActionPlatform || source.Source.Channel != Channels.Official
            || source.Origin.Trigger?.Conversation != target
            ? ToolOutcome.Failure("platform_mismatch", "动作与捕获的官方会话不匹配。") : null;
    }

    private bool TrySendArguments(JsonObject args, out string text)
    {
        text = string.Empty;
        var hasText = false;
        foreach (var (name, value) in args)
        {
            if (name is "tool" or "action" or "thought") continue;
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var content)) return false;
            if (name is "text" or "message" or "content")
            {
                if (hasText && text != content) return false;
                text = content;
                hasText = true;
            }
            else if (name is "target_id" or "target" or "to")
            {
                var current = content.Trim();
                if (current.Length > 0 && current != targetId.ToString(CultureInfo.InvariantCulture)
                    && current is not ("current" or "here" or "this" or "当前会话")
                    && !(isGroup && current == "本群")) return false;
            }
            else return false;
        }
        return !string.IsNullOrWhiteSpace(text);
    }
}
