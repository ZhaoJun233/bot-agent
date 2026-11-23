using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Identity;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;

namespace BotAgent.Services.Identity;

/// <summary>
/// 确定性 <c>//identity</c> 跨平台本人身份关联命令处理器（ADR-0006）：
/// 严格限机器主人（BotOwner）在可靠身份的私聊对话使用；仅凭一次性 120s 挑战码双向显式确认绑定，绝不跨端继承群管/审批/聊天历史。
/// </summary>
public sealed class IdentityCommandService
{
    public const string HelpText = """
        [可选跨平台本人身份关联 //identity（仅 BotOwner 可靠私聊可用）]
        - //identity link start：在当前端生成 120 秒一次性挑战码
        - //identity link confirm <挑战码>：在另一端确认关联到同一本人作用域
        - //identity share <todos|knowledge|preferences> <on|off>：控制跨端共享项
        - //identity unlink：解除当前端的跨端关联并失效共享凭据
        - //identity status：查看当前绑定与共享开关状态
        """;

    private readonly IQqChatSource _source;
    private readonly SettingsBox _box;
    private readonly IIdentityLinkResolver _identityLinks;
    private readonly ConversationRegistry? _registry;
    private readonly IAccessRoleResolver? _roles;

    public IdentityCommandService(
        IQqChatSource source,
        SettingsBox box,
        IIdentityLinkResolver identityLinks,
        ConversationRegistry? registry = null,
        IAccessRoleResolver? roles = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _box = box ?? throw new ArgumentNullException(nameof(box));
        _identityLinks = identityLinks ?? throw new ArgumentNullException(nameof(identityLinks));
        _registry = registry;
        _roles = roles;
    }

    public static bool IsCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = StripLeadingAtMentions(text);
        return trimmed.Equals("//identity", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//identity ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//identity\n", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//identity\r", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//identity\t", StringComparison.OrdinalIgnoreCase);
    }

    public bool TryHandle(QqChatMessage msg)
    {
        if (!msg.AllowsCommands || !IsCommand(msg.Text)) return false;
        _ = HandleAsync(msg);
        return true;
    }

    public async Task<bool> HandleAsync(QqChatMessage msg)
    {
        if (!msg.AllowsCommands || !IsCommand(msg.Text)) return false;
        if (!_box.Current.AiModeEnabled) return true;
        var lease = PlatformSourceExecution.Current;
        var sourceKey = _source is IPlatformSourceRouter router
            && router.ConversationKey(msg, k => _registry?.Find(k) is not null) is { Length: > 0 } routedKey
            ? routedKey
            : Channels.Key(msg.Channel, msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId);
        var reply = ExecuteCommand(msg, sourceKey, lease);
        await SendReplyAsync(msg, lease, reply).ConfigureAwait(false);
        return true;
    }

    private string ExecuteCommand(QqChatMessage msg, string sourceKey, PlatformSourceLease? lease)
    {
        var settings = _box.Current;
        if (!settings.AiModeEnabled)
            return "机器人当前处于静默关闭状态（AI 总开关已关闭）。";

        var isReliable = msg.Envelope?.IsReliableIdentity ?? (msg.IsReliableIdentity == true);
        if (msg.IsGroup || !isReliable)
            return "跨平台本人身份关联仅限可靠身份的私聊对话操作。";

        var context = lease?.Context ?? new PlatformContext(PlatformId.Normalize(msg.Channel), AccountScope.Legacy, string.Empty);
        if (!IsBotOwner(msg, sourceKey, context, settings))
            return "跨平台本人身份关联仅限机器人主人（BotOwner）在可靠私聊中操作。";

        var endpoint = BuildEndpointKey(msg, lease);
        if (endpoint is null || !endpoint.IsValid())
            return "无法识别当前私聊端点的可信身份，已拒绝操作。";

        var raw = StripLeadingAtMentions(msg.Text);
        var body = raw.Length > 10 ? raw[10..].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(body) || body.Equals("help", StringComparison.OrdinalIgnoreCase))
            return HelpText;

        var parts = body.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = parts[0].ToLowerInvariant();

        if (verb == "status")
        {
            var st = _identityLinks.GetStatus();
            var bound = _identityLinks.ResolveSharedOwnerScope(endpoint, IdentityResourceKind.Todos)
                ?? _identityLinks.ResolveSharedOwnerScope(endpoint, IdentityResourceKind.Knowledge)
                ?? "未加入共享";
            return $"跨端身份关联：{(st.Enabled ? "已开启" : "默认关闭")}｜当前端作用域：{bound}｜共享项：待办={(st.Preferences.ShareTodos ? "开" : "关")}、知识库={(st.Preferences.ShareKnowledge ? "开" : "关")}、偏好={(st.Preferences.SharePreferences ? "开" : "关")}（已绑定端点 {st.Links.Count} 个）";
        }

        if (verb == "link" && parts.Length >= 2)
        {
            var sub = parts[1].ToLowerInvariant();
            if (sub == "start")
            {
                var res = _identityLinks.StartChallenge(endpoint);
                return res.Ok
                    ? $"已在当前端生成一次性挑战码（120 秒内有效）：{res.ChallengeCode}\n请到另一平台的本人私聊发送：//identity link confirm {res.ChallengeCode}"
                    : FormatError(res.ReasonCode);
            }
            if (sub == "confirm" && parts.Length >= 3)
            {
                var res = _identityLinks.ConfirmChallenge(endpoint, parts[2]);
                return res.Ok
                    ? $"已完成双向确认绑定（共享作用域：{res.CanonicalPersonId}）。注意：历史聊天记录、群管角色与审批权限永不跨端共享。"
                    : FormatError(res.ReasonCode);
            }
            return HelpText;
        }

        if (verb == "unlink")
        {
            var res = _identityLinks.Unlink(endpoint);
            return res.Ok ? "已解除当前端的跨端关联，共享读写凭据已即刻失效。" : FormatError(res.ReasonCode);
        }

        if (verb == "share" && parts.Length >= 3)
        {
            if (!_identityLinks.IsEnabled)
                return FormatError("identity_linking_disabled");
            var target = parts[1].ToLowerInvariant();
            var on = parts[2].Equals("on", StringComparison.OrdinalIgnoreCase)
                || parts[2].Equals("true", StringComparison.OrdinalIgnoreCase)
                || parts[2].Equals("1", StringComparison.Ordinal);
            var cur = _identityLinks.GetStatus().Preferences;
            var next = target switch
            {
                "todos" => cur with { ShareTodos = on },
                "knowledge" or "kb" => cur with { ShareKnowledge = on },
                "preferences" or "prefs" => cur with { SharePreferences = on },
                _ => null
            };
            if (next is null) return "用法：//identity share <todos|knowledge|preferences> <on|off>";
            _identityLinks.UpdateSharePreferences(next);
            return $"已更新跨端共享偏好：待办={(next.ShareTodos ? "开" : "关")}、知识库={(next.ShareKnowledge ? "开" : "关")}、偏好={(next.SharePreferences ? "开" : "关")}。";
        }

        return HelpText;
    }

    private bool IsBotOwner(QqChatMessage msg, string sourceKey, PlatformContext context, AppSettings settings)
    {
        if (_roles is not null)
        {
            var subject = InboundSubjectIdentity.Resolve(msg, context);
            var role = _roles.ResolveRole(subject, sourceKey, settings.AgentAllowedUsers, isOfficialExplicitPrivateAllowed: false);
            return role == WorkspaceRole.BotOwner;
        }
        return msg.UserId > 0 && WhitelistPolicy.ParseWhitelist(settings.AgentAllowedUsers).Ids.Contains(msg.UserId);
    }

    private static string FormatError(string code) => code switch
    {
        "identity_linking_disabled" => "跨平台本人身份关联当前处于默认关闭状态，请先在工作台开启该功能。",
        "challenge_invalid_or_expired" => "挑战码无效、已过期或已被使用，请重新生成。",
        "challenge_rate_limited" => "挑战码验证失败次数过多，请稍后再试。",
        "identity_link_quota_exceeded" => "已绑定端点数量已达上限。",
        "same_endpoint_forbidden" => "不能在同一端自我确认，请在另一平台的本人私聊确认。",
        "endpoint_already_bound_to_other" => "其中一个端点已绑定到其他身份作用域，请先解除原绑定。",
        "identity_not_linked" => "当前端点尚未绑定跨端作用域。",
        _ => $"操作失败（{code}）。"
    };

    private static IdentityEndpointKey? BuildEndpointKey(QqChatMessage msg, PlatformSourceLease? lease)
    {
        if (msg.Envelope is { IsReliableIdentity: true } env)
        {
            var d = env.Origin.Driver;
            return new IdentityEndpointKey(d.PlatformId, d.AccountScope, d.InstanceId ?? string.Empty, env.Message.Sender.NativeUserId);
        }
        var ch = Channels.ChannelOf(msg.Channel);
        var pid = Channels.IsOfficial(ch) ? PlatformId.QqOfficial
            : Channels.IsFeishu(ch) ? PlatformId.Feishu
            : Channels.IsLocal(ch) ? PlatformId.Local
            : PlatformId.QqPrivate;
        var scope = lease?.Context.AccountScope ?? (pid == PlatformId.Feishu ? AccountScope.Default : AccountScope.Legacy);
        var instance = lease?.Context.InstanceId ?? string.Empty;
        var uid = msg.UserId > 0 ? msg.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        return string.IsNullOrEmpty(uid) ? null : new IdentityEndpointKey(pid, scope, instance, uid);
    }

    private async Task SendReplyAsync(QqChatMessage msg, PlatformSourceLease? lease, string reply)
    {
        try
        {
            if (lease is not null && !lease.IsCurrent) return;
            using var scope = PlatformSourceExecution.Enter(lease);
            using var cmdScope = Memory.MemoryUseScope.EnterCommandResponse();
            if (msg.Envelope is { } env && _source is IPlatformSourceRouter router)
            {
                var target = router.CompatibilityTarget(ConversationIdCodec.Encode(env.Message.Ref.Conversation));
                await _source.SendTextAsync(target.IsGroup, target.Id, reply).ConfigureAwait(false);
                return;
            }
            await _source.SendTextAsync(msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId, reply).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static string StripLeadingAtMentions(string text)
    {
        var s = (text ?? string.Empty).Trim();
        while (s.StartsWith('@'))
        {
            var idx = s.IndexOfAny([' ', '\t', '\r', '\n']);
            if (idx < 0) return string.Empty;
            s = s[(idx + 1)..].TrimStart();
        }
        return s;
    }
}
