using System.Collections.Immutable;
using System.Text;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Memory;

namespace BotAgent.Services.Memory;

/// <summary>Only the private route may render memory management data. Never send this to a model.</summary>
internal static class MemoryCommandView
{
    public const string Unavailable = "无法处理该记忆操作。请在本人私聊中使用 //memory help；不要在群内发送确认信息。";
    public const string GroupOnly = "记忆命令请在与机器人的本人私聊中检查并确认。群内不显示或确认任何记忆内容。";
    public const string Done = "记忆操作已完成。此操作不删除原始聊天记录，也不能撤回已发送的消息。";
    public const string Help = """
        统一记忆只接受本人明确设置的固定低敏偏好；不会读取原始聊天历史。
        //memory remember language zh|en|ja [to <scope> ...]
        //memory remember length brief|detailed [to <scope> ...]
        //memory remember explanation examples|step-by-step [to <scope> ...]
        //memory list [页码] · //memory show <事实ID> · //memory scope
        //memory edit <事实ID> <版本> <偏好类别> <值> [to <scope> ...]
        //memory revoke <事实ID> <scope>（仅撤销该目标的未来使用）
        //memory reject <事实ID>（本人否定：停止整条事实使用）
        //memory forget <事实ID>（删除事实与授权；原始聊天历史不变）
        //memory start|stop（仅单独配置的记忆治理管理员本人私聊，全局开关；不会代他人授权）
        治理身份由部署方单独配置 BOTAGENT_UNIFIED_MEMORY_ADMIN JSON；精确键 PlatformId、AccountScope、InstanceId、NativeUserId，值与来源身份精确匹配，与 Agent 权限分离。缺失或无效配置不授予治理权限。
        scope 精确格式：scope:平台/机器人账号/驱动实例/private或group或channel/原生目标ID/线程
        各字段使用URI转义；线程 ~ 表示无，! 后接转义后的原生线程ID。只接受同一平台、账号、驱动实例的有限明确目标。
        确认时先核对 show 中的完整来源、目标和提示，再发送展示的确认命令。
        编辑会立即使旧版本及所有旧授权失效，新版本必须重新私聊确认。停用保留管理信息并中断未来使用。
        """;

    public static string List(ImmutableArray<UnifiedMemoryEntry> entries, int page)
    {
        const int pageSize = 8;
        var pageCount = Math.Max(1, (entries.Length + pageSize - 1) / pageSize);
        if (page > pageCount) return Unavailable;
        var lines = entries.OrderBy(e => e.FactId).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => $"{e.FactId:N} v{e.Version} {e.Fact.Category} {(e.Confirmed ? "已确认" : "待确认")}；//memory show {e.FactId:N}");
        return $"本人记忆 {page}/{pageCount} 页；仅当前机器人账号与驱动实例。\n" + string.Join('\n', lines);
    }

    public static string Show(UnifiedMemoryEntry entry)
    {
        var targets = entry.Confirmed ? entry.SharedTargets : entry.ProposedTargets;
        var groupAudience = IsGroup(entry.Source) || targets.Any(IsGroup);
        var output = new StringBuilder()
            .AppendLine($"本人偏好：{entry.Fact.Category} = {entry.Fact.Value}")
            .AppendLine($"事实 {entry.FactId:N} · 版本 {entry.Version} · {(entry.Confirmed ? "已确认" : "待确认")}")
            .AppendLine("精确来源（平台/机器人账号/驱动实例/会话类型/原生目标ID/线程；URI转义，保留大小写）：")
            .AppendLine(MemoryCommandSyntax.ScopeToken(entry.Source))
            .AppendLine(entry.Confirmed ? "当前共享目标：" : "拟共享的完整有限目标：");
        if (targets.IsEmpty) output.AppendLine("无（不是所有会话）。");
        else foreach (var target in targets) output.AppendLine(MemoryCommandSyntax.ScopeToken(target));
        output.AppendLine("来源本地使用也是授权；不共享不等于允许来源使用，仍须本人明确确认。");
        if (groupAudience)
            output.AppendLine("警告：允许来源群/目标群或频道使用，即允许机器人向该会话公开该偏好；当前成员、未来加入成员及可见该会话的人都可能看到。不是只在场成员授权。该授权不允许借此读取来源聊天历史。");
        if (!entry.Confirmed)
        {
            output.AppendLine($"以上值、版本、精确来源及全部目标绑定本次确认；有效期至 {entry.ConfirmationExpiresAt:O}。")
                .AppendLine("仅当同意以上全部授权时，在此本人私聊发送：")
                .Append($"//memory confirm {entry.FactId:N} {entry.Version} {entry.ConfirmationToken:N} allow-source {(groupAudience ? "accept-group-audience" : "private-only")}");
        }
        return output.ToString();
    }

    private static bool IsGroup(UnifiedMemoryScope scope) => scope.Conversation.Kind is ConversationKind.GroupChat or ConversationKind.ChannelChat;
}
