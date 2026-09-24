namespace BotAgent.Domain.Qq;

/// <summary>白名单的解析与显示（纯函数、零 IO）：批次 1 从 BotAgentHost 原样搬来，判断条件一字未改。</summary>
public static class WhitelistPolicy
{
    /// <summary>白名单显示文案（日志与面板共用口径）。</summary>
    public static string WhitelistSummary(bool all, HashSet<long> ids)
        => all ? "* (全部)" : ids.Count == 0 ? "(空，忽略全部)" : string.Join(",", ids);

    /// <summary>解析白名单。返回 (ID集合, 是否通配全部)。支持换行/中英文逗号/分号/空格/Tab 分隔，以及 * 通配。</summary>
    public static (HashSet<long> Ids, bool All) ParseWhitelist(string? whitelist)
    {
        var ids = new HashSet<long>();
        if (string.IsNullOrWhiteSpace(whitelist))
        {
            return (ids, false); // 严格模式：空名单 = 全部忽略
        }

        var parts = whitelist.Split(
            new[] { '\n', '\r', ',', '，', ';', '；', ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var all = false;
        foreach (var part in parts)
        {
            if (part is "*" or "all" or "ALL")
            {
                all = true;
                continue;
            }

            if (long.TryParse(part, out var id))
            {
                ids.Add(id);
            }
        }

        return (ids, all);
    }
}
