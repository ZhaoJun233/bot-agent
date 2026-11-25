using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;
using System.Globalization;

namespace BotAgent.Services.Agent;

/// <summary>
/// 自然对话驱动 agent（与 // 指令并存，默认关）：聊天模型听出“对方要我去干活”时写 action=agent，
/// 回复流程把整理好的任务交到这里。权限口径与 // 指令**完全一致**（agent 总开关 + 服务器 agent 开关 +
/// AgentAllowedUsers / RBAC 角色 + 会话工具档位），只是入口换成了自然语言、回话换成了聊天口吻。
/// </summary>
public sealed partial class AgentCommandService
{
    /// <summary>这个人在这个会话里能不能“说句话就让 agent 干活”。</summary>
    public bool CanRunNatural(BotConversation conversation, long senderId)
        => CanRunNatural(conversation, new ChatMessage
        {
            Role = MessageRole.Peer,
            Text = string.Empty,
            Timestamp = Clock.Current.UtcNow,
            SenderId = senderId
        }, envelope: null);

    public bool CanRunNatural(BotConversation conversation, ChatMessage trigger, PlatformInboundEnvelope? envelope = null)
    {
        var s = _settings;
        if (!s.EnableNaturalAgent || !s.EnableAgentBridge || !s.EnableServerAgent || _serverAgent is null)
        {
            return false;
        }

        if (trigger.SenderId is not long senderId)
        {
            return false;
        }

        var context = envelope?.Origin.Driver is { } drv
            ? new BotAgent.Domain.Platforms.PlatformContext(drv.PlatformId, drv.AccountScope, drv.InstanceId ?? string.Empty)
            : PlatformSourceExecution.Current?.Context;
        var convSettings = ResolveConversationSettings(conversation.SourceKey, context);
        if (convSettings is { AllowsNaturalAgent: false })
        {
            return false;
        }

        var subject = envelope is not null
            ? new SubjectIdentity(
                BotAgent.Domain.Platforms.PlatformId.Normalize(envelope.Origin.Driver.PlatformId),
                envelope.Origin.Driver.AccountScope,
                envelope.Origin.Driver.InstanceId ?? string.Empty,
                envelope.Message.Sender.NativeUserId,
                senderId > 0 ? senderId : null)
            : InboundSubjectIdentity.ResolveFromChannel(Channels.ChannelOf(conversation.SourceKey), senderId, null, context);

        var (role, _) = ResolveSubjectRoleWithSource(subject, conversation.SourceKey, senderId, Channels.ChannelOf(conversation.SourceKey));
        var normalizedPlatform = BotAgent.Domain.Platforms.PlatformId.Normalize(Channels.ChannelOf(conversation.SourceKey));
        return EvaluateAgentAdmission(
            normalizedPlatform,
            isOwner: role == WorkspaceRole.BotOwner,
            isAllowedUser: role >= WorkspaceRole.TrustedMember,
            isWebSession: false);
    }

    /// <summary>跑一件从对话里接来的活（异常只记日志）。</summary>
    public Task RunNaturalSafeAsync(BotConversation conversation, ChatMessage trigger, string task, string? ack)
        => RunNaturalSafeAsync(conversation, trigger, envelope: null, task, ack);

    public async Task RunNaturalSafeAsync(
        BotConversation conversation, ChatMessage trigger, PlatformInboundEnvelope? envelope, string task, string? ack)
    {
        try
        {
            await HandleNaturalAsync(conversation, trigger, envelope, task, ack);
        }
        catch (Exception ex)
        {
            _hooks.Log($"自然 agent 处理异常: {ex.GetType().Name} {ex.Message}");
        }
    }

    private async Task HandleNaturalAsync(
        BotConversation conversation, ChatMessage trigger, PlatformInboundEnvelope? envelope, string task, string? ack)
    {
        // 服务端再判一次资格（提示词里出现过这条路不等于有权限；开关可能刚被关掉）
        if (trigger.SenderId is not long senderId || !CanRunNatural(conversation, trigger, envelope))
        {
            _hooks.Log("[自然 agent] 资格复核未通过，丢弃");
            return;
        }

        var (isGroup, targetId) = conversation.Target;
        var msg = new QqChatMessage(
            trigger.QqMessageId ?? 0, isGroup, senderId, isGroup ? targetId : 0,
            trigger.SenderName ?? string.Empty, trigger.Text, trigger.Timestamp, trigger.MentionedBot,
            ImageUrls: trigger.ImageUrls,
            Channel: Channels.ChannelOf(conversation.SourceKey))
        {
            Envelope = envelope
        };

        var payload = task.Trim();
        // 日志只记形状（AGENTS.md §1–3）：QQ 号遮成前 3 后 2，任务正文来自群聊、只记字数
        _hooks.Log($"[自然 agent]（{AgentMask.Shorten(senderId.ToString(CultureInfo.InvariantCulture))}）: 任务 {payload.Length} 字");

        // 停下 / 问进度：归一成 // 子命令的规范词后复用（只动台账，不派任务）
        if (NaturalSubCommand(payload) is { } sub)
        {
            await TryHandleAgentSubCommandAsync(conversation, msg, sub, "auto", null);
            return;
        }

        var route = ResolveRoute("auto", null);
        if (!route.UseServer)
        {
            await _hooks.SendPlainAsync(conversation, "这件事我现在动不了手（服务器 agent 没开），等管理员打开再找我～");
            return;
        }

        var handleSubject = envelope is not null
            ? new SubjectIdentity(
                BotAgent.Domain.Platforms.PlatformId.Normalize(envelope.Origin.Driver.PlatformId),
                envelope.Origin.Driver.AccountScope,
                envelope.Origin.Driver.InstanceId ?? string.Empty,
                envelope.Message.Sender.NativeUserId,
                senderId > 0 ? senderId : null)
            : InboundSubjectIdentity.ResolveFromChannel(Channels.ChannelOf(conversation.SourceKey), senderId, null,
                PlatformSourceExecution.Current?.Context);
        var (handleRole, handleExplicit) = ResolveSubjectRoleWithSource(
            handleSubject, conversation.SourceKey, senderId, Channels.ChannelOf(conversation.SourceKey));
        var isOpsAuthorized = !handleExplicit || handleRole == WorkspaceRole.BotOwner;

        payload += await BuildAgentImageNoteAsync(msg);
        await RunOnServerWithRoleAsync(conversation, msg, payload, ack, natural: true, isOpsAuthorized: isOpsAuthorized);
    }

    private static readonly string[] NaturalPrefixes = ["帮我", "麻烦", "请", "把"];
    private static readonly string[] NaturalSuffixes = ["任务", "一下", "吧", "呢", "了", "下"];

    /// <summary>
    /// 模型写进 agentTask 的“停下 / 问进度”归一成 // 子命令的规范词（"stop" / "status"），否则 null。
    /// 先剥标点和“帮我…任务吧”这类客套前后缀，剥完必须**整词**等于关键词——
    /// 所以“帮我停止任务”算取消，“停止监控服务”仍是一件要派的活。
    /// </summary>
    public static string? NaturalSubCommand(string payload)
    {
        var s = payload.Trim().TrimEnd('。', '！', '!', '？', '?', '.', '~', '～', ' ').ToLowerInvariant();
        // 每剥一层都先看是不是关键词：否则「停下」会被后缀「下」剥成「停」而漏掉
        for (var changed = true; changed;)
        {
            if (Canonical(s) is { } hit)
            {
                return hit;
            }

            changed = false;
            foreach (var p in NaturalPrefixes)
            {
                if (s.Length > p.Length && s.StartsWith(p, StringComparison.Ordinal))
                {
                    s = s[p.Length..].TrimStart();
                    changed = true;
                }
            }

            foreach (var x in NaturalSuffixes)
            {
                if (s.Length > x.Length && s.EndsWith(x, StringComparison.Ordinal))
                {
                    s = s[..^x.Length].TrimEnd();
                    changed = true;
                }
            }
        }

        return Canonical(s);
    }

    private static string? Canonical(string s)
    {
        return s switch
        {
            "stop" or "cancel" or "停止" or "取消" or "中断" or "停下" or "停掉" => "stop",
            "status" or "状态" or "进度" => "status",
            _ => null,
        };
    }
}
