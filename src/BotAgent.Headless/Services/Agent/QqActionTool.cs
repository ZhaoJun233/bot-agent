using System.Text.Json.Nodes;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Services.OneBot;
using BotAgent.Services.Platforms;
using BotAgent.Services.Plugins;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Agent;

/// <summary>
/// 服务器 agent 做 QQ 动作时的“现场”：当前是哪个会话、谁发起的、哪条消息。
/// 由 <c>BotAgentHost</c> 按任务构造 —— 不然模型只知道“给谁点赞”，不知道“给谁/在哪”。
/// </summary>
public interface IQqActionHost
{
    /// <summary>给系统提示词用的一行现场信息（群/私聊、机器人自己、发指令的人、本条消息 id）。</summary>
    string ContextLine { get; }

    /// <summary>真的去调协议端。返回给模型看的一句话结果（成功/失败原因）。</summary>
    Task<string> ExecuteAsync(QqActionSpec spec, JsonObject args, CancellationToken ct);
}

/// <summary>
/// 绑定在一次 QQ 会话上的动作宿主：把动作里的 <c>sender</c>/<c>me</c>/<c>this</c> 这类写法翻成真数字，
/// 再交给 OneBot 网关。没有会话上下文时（面板“试一条”）只有写死 QQ 号的动作能用。
/// </summary>
public sealed class SessionQqActionHost : IQqActionHost, BotAgent.Services.Tools.IToolExecutor
{
    private readonly IQqActions _gateway;
    private readonly PlatformSourceLease? _sourceLease;
    private readonly bool _isGroup;
    private readonly long _targetId;
    private readonly long _senderId;
    private readonly long _messageId;
    private readonly long _selfId;
    private readonly PlatformPolicyResolver? _platformPolicies;
    private readonly string? _channel;
    private readonly Func<string, bool>? _pluginAllows;

    public SessionQqActionHost(IQqActions gateway, bool isGroup, long targetId, long senderId, long messageId, long selfId,
        PlatformPolicyResolver? platformPolicies = null, string? channel = null, Func<string, bool>? pluginAllows = null)
    {
        _sourceLease = PlatformSourceExecution.Current;
        _gateway = _sourceLease?.Source as IQqActions ?? gateway;
        _isGroup = isGroup;
        _targetId = targetId;
        _senderId = senderId;
        _messageId = messageId;
        _selfId = selfId;
        _platformPolicies = platformPolicies;
        _channel = channel;
        _pluginAllows = pluginAllows;

        var where = isGroup ? $"群 {targetId}" : $"私聊 {targetId}";
        var self = selfId > 0 ? selfId.ToString() : "(未知)";
        var who = senderId > 0 ? senderId.ToString() : "(未知)";
        var msg = messageId > 0 ? messageId.ToString() : "(未知)";
        ContextLine = $"现在这条指令来自{where}；发指令的人 QQ={who}；触发它那条消息 id={msg}；机器人自己 QQ={self}。" +
                      "参数里可以写 sender（=发指令的人）/ me（=机器人自己）/ this（=这条消息）/ 本群。";
    }

    public string ContextLine { get; }

    // ── 批次 A 收尾：这一族**真的能执行**（IToolExecutor），执行体就是下面那个 switch ──
    // 登记表里它是 `qq.actions`（见 Services/Tools/ToolExecutors.cs）；判定不在这里（闸门在调用方）。

    /// <summary>执行者标识（与 <c>QqToolSpecs.ExecutorActions</c> 同一个常量）。</summary>
    public string Id => BotAgent.Services.Tools.QqToolSpecs.ExecutorActions;

    /// <summary>今天真正干这件事的组件（面板与审计展示用）。</summary>
    public string Implementation => "Services/Agent/QqActionTool.cs（SessionQqActionHost，本体就是那个 switch）";

    /// <summary>false = 已经接进统一执行（调用方给 ToolCall，它直接干）。</summary>
    public bool LegacyPath => false;

    /// <summary>
    /// 统一执行入口：<c>qq.like</c> 这种工具名 → 动作名 → 既有那个 <see cref="ExecuteAsync(QqActionSpec, JsonObject, CancellationToken)" />。
    /// 认不出的动作名直接失败（不猜、不降级），与目录（<c>QqActionCatalog</c>）同一份真相。
    /// </summary>
    public async Task<BotAgent.Domain.Tools.ToolOutcome> ExecuteAsync(
        BotAgent.Domain.Tools.ToolCall call, CancellationToken ct = default)
    {
        var raw = call?.ToolId ?? string.Empty;
        var actionName = raw.StartsWith("qq.", StringComparison.Ordinal) ? raw[3..] : raw;
        var canonical = QqActionCatalog.Canonical(actionName);
        var spec = canonical is null ? null : QqActionCatalog.All.FirstOrDefault(a => a.Name == canonical);
        if (spec is null)
        {
            return BotAgent.Domain.Tools.ToolOutcome.Failure(
                "unknown_action", $"没有这个 QQ 动作：{actionName}（见目录里那几个）");
        }

        var text = await ExecuteAsync(spec, call!.Arguments ?? new JsonObject(), ct);
        return BotAgent.Domain.Tools.ToolOutcome.Success(text, "qq 动作 " + spec.Name);
    }

    private EffectivePlatformPolicy? ResolveTargetPolicy()
    {
        if (_platformPolicies is null)
        {
            return null;
        }

        if (_sourceLease is not null) return _platformPolicies.Resolve(_sourceLease.Context);
        var channel = _channel;
        if (channel is null)
        {
            channel = Channels.IsFeishuId(_targetId)
                ? Channels.Feishu
                : Channels.IsLocalId(_targetId)
                    ? Channels.Local
                    : Channels.IsAliasId(_targetId)
                        ? Channels.Official
                        : Channels.Private;
        }
        return _platformPolicies.ResolveForChannel(channel);
    }

    private static string? FeatureForAction(string action)
        => action switch
        {
            "poke" => "poke",
            "recall" => "recall",
            _ => null,
        };

    /// <summary>动作对应的插件能力；只有登记在 PluginGate 里的才受插件开关管理（recall 仍由平台策略管）。</summary>
    private static string? PluginCapabilityForAction(string action)
        => action == "poke" ? PluginGate.PokeCapability : null;

    /// <summary>
    /// 显式动作执行前的授权裁决：插件开关（fail closed）→ 平台/会话 → 平台能力 → 动作允许表。
    /// 返回 null = 放行；否则是给模型看的一句拒绝原因。
    /// </summary>
    private string? DenyReason(QqActionSpec spec, EffectivePlatformPolicy? policy)
    {
        var featureName = FeatureForAction(spec.Name);
        // 插件门禁与对话内动作共用同一个开关：显式动作也必须 fail closed。
        // 未接线门禁时也不能默认放行（否则任何漏传 pluginAllows 的构造都会绕过面板开关）。
        var pluginCapability = PluginCapabilityForAction(spec.Name);
        if (pluginCapability is not null && (_pluginAllows is null || !_pluginAllows(pluginCapability)))
        {
            return _pluginAllows is null
                ? $"❌ 动作 {spec.Name} 被插件门禁拒绝（门禁未接线）。"
                : $"❌ 动作 {spec.Name} 被插件开关拒绝（对应插件已关闭）。";
        }

        if (policy is null)
        {
            return null;
        }

        if (policy.Context.PlatformId != PlatformId.QqPrivate)
        {
            return $"❌ 动作 {spec.Name} 被平台策略拒绝（QQ 动作仅支持私域 OneBot 会话）。";
        }

        if (!policy.Registered || !policy.Connected || !policy.Enabled || !policy.ChatEnabled)
        {
            return $"❌ 动作 {spec.Name} 被平台策略拒绝（平台未注册、未连接或聊天未启用）。";
        }

        if (featureName is not null)
        {
            var feature = policy.Feature(featureName);
            if (!feature.Enabled)
            {
                return $"❌ 动作 {spec.Name} 被平台能力拒绝（{feature.ReasonCode}）。";
            }
        }

        return policy.ActionAllowlistConfigured && !policy.CanUseAction(spec.Name)
            ? $"❌ 动作 {spec.Name} 未被当前平台策略授权。"
            : null;
    }

    public async Task<string> ExecuteAsync(QqActionSpec spec, JsonObject args, CancellationToken ct)
    {
        using var sourceScope = PlatformSourceExecution.Enter(_sourceLease);
        if (_sourceLease is { IsCurrent: false }) return "source_retired";
        using var lifetime = _sourceLease is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, _sourceLease.Lifetime);
        ct = lifetime?.Token ?? ct;
        ct.ThrowIfCancellationRequested();
        if (DenyReason(spec, ResolveTargetPolicy()) is { } denial)
        {
            return denial;
        }

        switch (spec.Name)
        {
            case "like":
            {
                var user = ResolveUser(Text(args, "user_id") ?? Text(args, "user") ?? Text(args, "target"), out var err);
                if (user is null)
                {
                    return err!;
                }

                var times = (int)Math.Clamp(ResolveNumber(Text(args, "times") ?? Text(args, "count"), 1), 1, 20);
                var ok = await _gateway.SendLikeAsync(user.Value, times, ct);
                return ok
                    ? $"✅ 给 {user} 点了 {times} 个赞"
                    : $"❌ 点赞没成功（QQ 侧回绝：名片赞对“从未互动过的人”、以及关掉了“允许陌生人赞我”的人都会拦）。"
                      + "这次换个方式：poke（戳一戳）或 emoji_like（贴表情）不要求好友关系；"
                      + "如果确实要点赞，可以让对方在手机 QQ 里打开“允许陌生人赞我”，或者先用手机手动赞他一次（之后就能点了）。";
            }

            case "poke":
            {
                var user = ResolveUser(Text(args, "user_id") ?? Text(args, "user") ?? Text(args, "target"), out var err);
                if (user is null)
                {
                    return err!;
                }

                var ok = await _gateway.SendPokeAsync(_isGroup, _targetId, user.Value, ct);
                return ok ? $"✅ 戳了 {user} 一下" : "❌ 戳一戳没成功（协议端可能不支持这个动作）";
            }

            case "emoji_like":
            {
                var mid = ResolveMessage(Text(args, "message_id") ?? Text(args, "message") ?? Text(args, "id"), out var err);
                if (mid is null)
                {
                    return err!;
                }

                var emoji = Text(args, "emoji_id") ?? Text(args, "emoji") ?? "128077";
                var ok = await _gateway.SetMessageEmojiLikeAsync(mid.Value, emoji, ct);
                return ok ? $"✅ 给消息 {mid} 贴了表情回应（{emoji}）" : "❌ 表情回应没成功（协议端可能不支持，或消息太旧）";
            }

            case "recall":
            {
                var mid = ResolveMessage(Text(args, "message_id") ?? Text(args, "message") ?? Text(args, "id"), out var err);
                if (mid is null)
                {
                    return err!;
                }

                var ok = await _gateway.DeleteMessageAsync(mid.Value, ct);
                return ok ? $"✅ 撤回了消息 {mid}" : "❌ 撤回没成功（那条不是机器人发的，或者没管理权限 / 超过 2 分钟）";
            }

            case "ban":
            {
                if (!_isGroup)
                {
                    return "群里才能禁言（现在是私聊）。";
                }

                var user = ResolveUser(Text(args, "user_id") ?? Text(args, "user") ?? Text(args, "target"), out var err);
                if (user is null)
                {
                    return err!;
                }

                var seconds = (int)Math.Clamp(ResolveDuration(Text(args, "duration") ?? Text(args, "seconds") ?? Text(args, "time"), 600), 0, 30 * 24 * 3600);
                var ok = await _gateway.SetGroupBanAsync(_targetId, user.Value, seconds, ct);
                var what = seconds == 0 ? $"给 {user} 解除了禁言" : $"把 {user} 禁言了 {FormatDuration(seconds)}";
                return ok ? $"✅ {what}" : $"❌ 禁言没成功（机器人得是管理员、且不能禁言群主/管理员）";
            }

            case "kick":
            {
                if (!_isGroup)
                {
                    return "踢人得在群里（现在是私聊）。";
                }

                var user = ResolveUser(Text(args, "user_id") ?? Text(args, "user") ?? Text(args, "target"), out var err);
                if (user is null)
                {
                    return err!;
                }

                var reject = ResolveBool(Text(args, "reject_add") ?? Text(args, "reject"), false);
                var ok = await _gateway.SetGroupKickAsync(_targetId, user.Value, reject, ct);
                return ok ? $"✅ 把 {user} 踢出群了{(reject ? "（并拒绝再加群）" : string.Empty)}" : "❌ 踢人没成功（机器人得是管理员）";
            }

            case "card":
            {
                if (!_isGroup)
                {
                    return "群名片只在群里有效（现在是私聊）。";
                }

                var user = ResolveUser(Text(args, "user_id") ?? Text(args, "user") ?? Text(args, "target"), out var err);
                if (user is null)
                {
                    return err!;
                }

                var card = Text(args, "card") ?? Text(args, "name") ?? Text(args, "text") ?? string.Empty;
                var ok = await _gateway.SetGroupCardAsync(_targetId, user.Value, card, ct);
                return ok ? $"✅ 把 {user} 的群名片改成「{card}」" : "❌ 改名片没成功（机器人得是管理员）";
            }

            case "group_name":
            {
                if (!_isGroup)
                {
                    return "改群名得在群里（现在是私聊）。";
                }

                var name = Text(args, "name") ?? Text(args, "group_name") ?? Text(args, "text");
                if (string.IsNullOrWhiteSpace(name))
                {
                    return "改群名要写 name。";
                }

                var ok = await _gateway.SetGroupNameAsync(_targetId, name!, ct);
                return ok ? $"✅ 群名改成「{name}」了" : "❌ 改群名没成功（得是群主/管理员）";
            }

            case "leave":
            {
                if (!_isGroup)
                {
                    return "退群得在群里（现在是私聊）。";
                }

                var dismiss = ResolveBool(Text(args, "dismiss"), false);
                var ok = await _gateway.SetGroupLeaveAsync(_targetId, dismiss, ct);
                return ok ? "✅ 已经退群了（这条之后我就收不到这个群的消息了）" : "❌ 退群没成功（解散得是群主）";
            }

            case "send":
            {
                var text = Text(args, "text") ?? Text(args, "message") ?? Text(args, "content");
                if (string.IsNullOrWhiteSpace(text))
                {
                    return "发消息要写 text。";
                }

                var raw = Text(args, "target_id") ?? Text(args, "target") ?? Text(args, "to");
                var (isGroup, target) = ResolveTarget(raw, out var err2);
                if (target <= 0)
                {
                    return err2!;
                }

                var result = await _gateway.SendTextAsync(isGroup, target, text!, ct);
                return result.Ok ? $"✅ 消息已发到{(isGroup ? $"群 {target}" : $"私聊 {target}")}" : "❌ 消息没发出去";
            }

            default:
                return $"不认识的动作 {spec.Name}";
        }
    }

    /// <summary>把 <c>sender</c>/<c>me</c> 翻成 QQ 号；纯数字直接用。</summary>
    private long? ResolveUser(string? raw, out string? error)
    {
        error = null;
        var token = (raw ?? string.Empty).Trim().Trim('"');
        if (token.Length == 0)
        {
            error = "要指明对谁做（user_id 写 QQ 号，或者 sender / me）。";
            return null;
        }

        switch (token.ToLowerInvariant())
        {
            case "sender" or "发指令的人" or "发送者" or "我" or "本人" or "他自己" or "刚才那个人":
                if (_senderId <= 0)
                {
                    error = "这次不知道发指令的人是谁（面板试跑？）：请直接写 QQ 号。";
                    return null;
                }

                return _senderId;
            case "me" or "self" or "bot" or "机器人" or "机器人自己":
                if (_selfId <= 0)
                {
                    error = "这次不知道机器人自己的 QQ 号：请直接写 QQ 号。";
                    return null;
                }

                return _selfId;
            case "this" or "本条" or "本群" or "这里":
                // 群里“给这个群点赞”不存在，但当“对象的 QQ 号”被省略时的兜底：用会话对端没意义
                error = "这里需要一个**人的 QQ 号**（写数字，或 sender / me）。";
                return null;
        }

        return long.TryParse(token, out var id) && id > 0 ? id : Fail(out error, $"「{token}」不是一个 QQ 号（要数字，或 sender / me）。");
    }

    /// <summary>把 <c>this</c>/数字 翻成消息 id。</summary>
    private long? ResolveMessage(string? raw, out string? error)
    {
        error = null;
        var token = (raw ?? string.Empty).Trim().Trim('"');
        if (token.Length == 0)
        {
            error = "要指明是哪条消息（message_id 写数字，或 this = 刚发这条指令的消息）。";
            return null;
        }

        switch (token.ToLowerInvariant())
        {
            case "this" or "本条" or "这条" or "刚才" or "上一条":
                if (_messageId <= 0)
                {
                    error = "这次不知道触发的消息 id（面板试跑？）：请直接写数字。";
                    return null;
                }

                return _messageId;
        }

        return long.TryParse(token, out var id) && id > 0 ? id : Fail(out error, $"「{token}」不是一个消息 id（要数字，或 this）。");
    }

    /// <summary>发消息动作的目标：不写 = 当前会话；写本群/纯数字 = 群或私聊。</summary>
    private (bool IsGroup, long Id) ResolveTarget(string? raw, out string? error)
    {
        error = null;
        var token = (raw ?? string.Empty).Trim().Trim('"');
        if (token.Length == 0 || token.Equals("这里", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("本群", StringComparison.OrdinalIgnoreCase) || token.Equals("this", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            return (_isGroup, _targetId);
        }

        // “群123456” / “私聊123456” 这种带前缀的写法也认
        if (token.StartsWith("群", StringComparison.Ordinal) &&
            long.TryParse(token[1..].TrimStart(':', '：', ' '), out var gid) && gid > 0)
        {
            return (true, gid);
        }

        if ((token.StartsWith("私聊", StringComparison.Ordinal) || token.StartsWith("好友", StringComparison.Ordinal)) &&
            long.TryParse(token[2..].TrimStart(':', '：', ' '), out var uid) && uid > 0)
        {
            return (false, uid);
        }

        if (long.TryParse(token, out var id) && id > 0)
        {
            // 纯数字：群里发的就当群号（管理员想给别的群发就写群号），私聊里发的当 QQ 号
            return (_isGroup, id);
        }

        error = $"「{token}」不知道该发到哪儿（写群号/QQ 号，或留空发在当前会话）。";
        return (false, 0);
    }

    private static long? Fail(out string? error, string message)
    {
        error = message;
        return null;
    }

    private static string? Text(JsonObject args, string key)
    {
        var node = args[key];
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.String => node.GetValue<string>(),
                System.Text.Json.JsonValueKind.Number => node.ToJsonString(),
                System.Text.Json.JsonValueKind.True => "true",
                System.Text.Json.JsonValueKind.False => "false",
                _ => node.ToJsonString()
            };
        }
        catch
        {
            return node.ToJsonString();
        }
    }

    private static long ResolveNumber(string? raw, long fallback)
        => long.TryParse((raw ?? string.Empty).Trim(), out var value) ? value : fallback;

    /// <summary>时长：纯数字 = 秒；也认 10m / 1h / 2d（模型爱这么写）。</summary>
    private static long ResolveDuration(string? raw, long fallback)
    {
        var token = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (token.Length == 0)
        {
            return fallback;
        }

        var unit = 1L;
        if (token.EndsWith('s'))
        {
            token = token[..^1];
        }
        else if (token.EndsWith('m'))
        {
            unit = 60;
            token = token[..^1];
        }
        else if (token.EndsWith('h'))
        {
            unit = 3600;
            token = token[..^1];
        }
        else if (token.EndsWith('d'))
        {
            unit = 86400;
            token = token[..^1];
        }

        return long.TryParse(token.Trim(), out var value) ? value * unit : fallback;
    }

    private static bool ResolveBool(string? raw, bool fallback)
    {
        var token = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return token switch
        {
            "true" or "1" or "yes" or "y" or "是" or "对" or "要" => true,
            "false" or "0" or "no" or "n" or "否" or "不" or "不要" => false,
            _ => fallback
        };
    }

    private static string FormatDuration(int seconds)
        => seconds >= 86400 && seconds % 86400 == 0 ? $"{seconds / 86400} 天"
            : seconds >= 3600 && seconds % 3600 == 0 ? $"{seconds / 3600} 小时"
            : seconds >= 60 && seconds % 60 == 0 ? $"{seconds / 60} 分钟"
            : $"{seconds} 秒";
}
