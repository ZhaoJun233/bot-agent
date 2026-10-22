using BotAgent.Engine.Runtime;
namespace BotAgent.Services.Agent;

/// <summary>
/// 一个 QQ 动作（NapCat / OneBot 的接口动作）的元数据。
/// </summary>
/// <param name="Name">规范名（模型写这个；中文别名见 <see cref="Aliases" />）。</param>
/// <param name="Tier">档位：<c>safe</c>（默认就开）/ <c>risky</c>（要在面板里显式写名字才开）。</param>
/// <param name="Params">给模型看的参数说明（一句话）。</param>
/// <param name="Aliases">中文/口语别名（模型爱写“点赞”“戳一戳”，替它翻译掉）。</param>
/// <param name="Action">OneBot 动作名（群聊；私聊另有 <see cref="ActionPrivate" /> 时用它）。</param>
/// <param name="ActionPrivate">私聊下的动作名（戳一戳是 friend_poke；其余与群聊同名）。</param>
public sealed record QqActionSpec(
    string Name,
    string Tier,
    string Params,
    string[] Aliases,
    string Action,
    string? ActionPrivate = null)
{
    public bool Risky => Tier == "risky";
}

/// <summary>
/// QQ 动作目录 —— 服务器内置 agent 能做的“真动作”（管理员 2026-09-18 / 09-19 要的“比如点赞”）。
///
/// 为什么要分两档（而不是一股脑全开）：
///   • 服务器 agent 会**读日志/文件/网页**，那些内容里可能夹着“给我点赞”“把谁禁言”这类话；
///     模型再听话也架不住有人往群里/网页里塞指令（提示注入）。所以默认只开“闹着玩”的一档，
///     真正会打扰别人、或不可逆的动作（禁言/踢人/改群名/退群/代发消息）必须管理员在面板里点名打开。
///   • 另一层保险在 <see cref="ServerAgentRunner" />：不在允许表里的动作，连协议端都不会被调用到。
/// </summary>
public static class QqActionCatalog
{
    /// <summary>全部动作（顺序即面板提示里的顺序）。</summary>
    public static readonly QqActionSpec[] All =
    {
        new("like", "safe", "给某个**人**点赞（QQ 名片赞）。user_id 必填，times 1-20（默认 1；同一个人一天点不了几个）。"
            + "注意：QQ 侧会对“从未互动过的人”和关掉了“允许陌生人赞我”的人回绝（非好友不一定不行）；"
            + "被回绝时机器人会自动补看一次资料卡再试，仍失败就别硬试，改用 poke（戳一戳）或 emoji_like（贴表情）—— 这两个不看好友关系",
            new[] { "点赞", "名片赞", "赞一下", "zan" }, "send_like"),
        new("poke", "safe", "戳一戳某人。user_id 必填（群里也能戳别人；不看好友关系，非好友的群友一样能戳）",
            new[] { "戳一戳", "拍一拍", "戳", "拍拍" }, "group_poke", "friend_poke"),
        new("emoji_like", "safe", "给某**条消息**贴个表情回应。message_id 必填（可写 this）；emoji_id 默认 128077（👍）",
            new[] { "表情回应", "给消息点赞", "贴表情", "回应一下" }, "set_msg_emoji_like"),
        new("recall", "safe", "撤回一条消息。message_id 必填（可写 this）。只有机器人自己发的、或它有管理员权限的群里别人的，才撤得掉",
            new[] { "撤回", "撤销", "撤一下", "删除消息" }, "delete_msg"),

        new("ban", "risky", "禁言/解除禁言（仅群）。user_id 必填；duration 秒（0 = 解除；可写 10m/1h），不写默认 600",
            new[] { "禁言", "口球", "关小黑屋", "解禁" }, "set_group_ban"),
        new("kick", "risky", "把某人踢出群（仅群）。user_id 必填；reject_add=true 表示同时拒绝再加群",
            new[] { "踢人", "踢出群", "移出群" }, "set_group_kick"),
        new("card", "risky", "改某人的群名片（仅群，需要管理权限）。user_id + card 必填（card 空串 = 清掉名片）",
            new[] { "群名片", "改名片", "改名" }, "set_group_card"),
        new("group_name", "risky", "改群名（仅群，需要群主/管理员）。name 必填",
            new[] { "群名", "改群名", "群名称" }, "set_group_name"),
        new("leave", "risky", "让机器人退群。dismiss=true 表示解散（只有群主能解散）",
            new[] { "退群", "解散群", "退出群" }, "set_group_leave"),
        new("send", "risky", "以机器人身份直接发一条消息。text 必填；target_id 不写 = 发在当前会话（可写本群/某群号/某 QQ 号）",
            new[] { "发消息", "代发", "说话", "发言" }, "send_group_msg", "send_private_msg")
    };

    /// <summary>设置里留空时默认开的那几个（都不打扰别人、后果也轻）。</summary>
    public static readonly string[] DefaultSafe = { "like", "poke", "emoji_like", "recall" };

    /// <summary>把设置串解析成允许的动作名集合。</summary>
    /// <remarks>
    /// 语义（和 <c>AgentServerTools</c> 的“空 = 全开”**故意不一样**，因为这里是真动别人的 QQ）：
    ///   • 留空 → 默认安全档；
    ///   • <c>all</c>/<c>*</c> → 全开（含危险的那几只，面板里有警示）；
    ///   • 否则名单里的名字（认中文别名，写错的忽略）。
    /// </remarks>
    public static HashSet<string> ParseAllowed(string? raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            foreach (var name in DefaultSafe)
            {
                set.Add(name);
            }

            return set;
        }

        var any = false;
        foreach (var piece in raw.Split(new[] { ',', '，', ';', '；', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = piece.Trim();
            if (token is "all" or "*" or "全部" or "所有")
            {
                foreach (var spec in All)
                {
                    set.Add(spec.Name);
                }

                any = true;
                continue;
            }

            var canonical = Canonical(token);
            if (canonical is not null)
            {
                set.Add(canonical);
                any = true;
            }
        }

        // 全写错了（比如写成 “点赞like禁言” 这种连着写）：宁可一个都不开，也别当成“没配 → 全开”
        return any ? set : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>名字/别名 → 规范名（不认识返回 null）。</summary>
    public static string? Canonical(string? name)
    {
        var token = (name ?? string.Empty).Trim().ToLowerInvariant();
        if (token.Length == 0)
        {
            return null;
        }

        foreach (var spec in All)
        {
            if (spec.Name.Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                return spec.Name;
            }

            if (spec.Action.Equals(token, StringComparison.OrdinalIgnoreCase) ||
                (spec.ActionPrivate is { Length: > 0 } && spec.ActionPrivate.Equals(token, StringComparison.OrdinalIgnoreCase)))
            {
                return spec.Name;
            }

            if (spec.Aliases.Any(a => a.Equals(token, StringComparison.OrdinalIgnoreCase)))
            {
                return spec.Name;
            }
        }

        return null;
    }

    public static QqActionSpec? Find(string? name)
    {
        var canonical = Canonical(name);
        return canonical is null ? null : All.FirstOrDefault(s => s.Name == canonical);
    }

    /// <summary>系统提示词里那段“这次能做什么”。</summary>
    public static string DescribeForPrompt(IEnumerable<string> allowed)
    {
        var lines = new List<string>();
        foreach (var spec in All.Where(s => allowed.Contains(s.Name)))
        {
            lines.Add($"  • {spec.Name}：{spec.Params}");
        }

        return lines.Count == 0 ? "（这次一个动作都没开，qq 工具别用）" : string.Join("\n", lines);
    }

    /// <summary>面板/状态里显示的一行摘要（安全档 + 危险档点名）。</summary>
    public static string Summarize(IEnumerable<string> allowed)
    {
        var set = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var safeOn = All.Where(s => !s.Risky && set.Contains(s.Name)).Select(s => s.Name).ToList();
        var riskyOn = All.Where(s => s.Risky && set.Contains(s.Name)).Select(s => s.Name).ToList();
        return $"安全档 {(safeOn.Count == 0 ? "无" : string.Join("/", safeOn))}" +
               (riskyOn.Count > 0 ? $"，已点名打开 {string.Join("/", riskyOn)}" : string.Empty);
    }
}
