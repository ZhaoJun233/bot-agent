using BotAgent.Domain.Conversation;
using BotAgent.Domain.Qq;
using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;
using System.Globalization;

namespace BotAgent.Services.Agent;

/// <summary>
/// 自然对话驱动 agent（与 // 指令并存，默认关）：聊天模型听出“对方要我去干活”时写 action=agent，
/// 回复流程把整理好的任务交到这里。权限口径与 // 指令**完全一致**（agent 总开关 + 服务器 agent 开关 +
/// AgentAllowedUsers 白名单），只是入口换成了自然语言、回话换成了聊天口吻。
/// </summary>
public sealed partial class AgentCommandService
{
    /// <summary>这个人在这个会话里能不能“说句话就让 agent 干活”。</summary>
    public bool CanRunNatural(BotConversation conversation, long senderId)
    {
        var s = _settings;
        if (!s.EnableNaturalAgent || !s.EnableAgentBridge || !s.EnableServerAgent || _serverAgent is null)
        {
            return false;
        }

        var officialExplicit = Channels.IsOfficial(Channels.ChannelOf(conversation.SourceKey))
            && _hooks.IsOfficialUserAllowed(senderId);
        return _agentAllowAll || (_agentUsers.Count > 0 && _agentUsers.Contains(senderId)) || officialExplicit;
    }

    /// <summary>跑一件从对话里接来的活（异常只记日志）。</summary>
    public async Task RunNaturalSafeAsync(BotConversation conversation, ChatMessage trigger, string task, string? ack)
    {
        try
        {
            await HandleNaturalAsync(conversation, trigger, task, ack);
        }
        catch (Exception ex)
        {
            _hooks.Log($"自然 agent 处理异常: {ex.GetType().Name} {ex.Message}");
        }
    }

    private async Task HandleNaturalAsync(BotConversation conversation, ChatMessage trigger, string task, string? ack)
    {
        // 服务端再判一次资格（提示词里出现过这条路不等于有权限；开关可能刚被关掉）
        if (trigger.SenderId is not long senderId || !CanRunNatural(conversation, senderId))
        {
            _hooks.Log("[自然 agent] 资格复核未通过，丢弃");
            return;
        }

        var (isGroup, targetId) = conversation.Target;
        var msg = new QqChatMessage(
            trigger.QqMessageId ?? 0, isGroup, senderId, isGroup ? targetId : 0,
            trigger.SenderName ?? string.Empty, trigger.Text, trigger.Timestamp, trigger.MentionedBot,
            ImageUrls: trigger.ImageUrls,
            Channel: Channels.ChannelOf(conversation.SourceKey));

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

        payload += await BuildAgentImageNoteAsync(msg);
        await RunOnServerAsync(conversation, msg, payload, ack, natural: true);
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
