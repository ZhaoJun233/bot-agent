namespace BotAgent.Domain.Agent;

/// <summary>
/// 执行会话的**纯文本规则**。它原来是 <c>AgentSessionStore</c> 上的一个 <c>static</c> 方法，
/// 而用例层（<c>//</c> 命令那一路）要用它 —— 静态方法过不了端口，于是按“纯函数下沉”的老规矩搬到 Domain。
/// </summary>
public static class AgentSessionText
{
    /// <summary>
    /// 从任务提示词里概括一个短标题（去掉“帮我看看”这类客套前缀，最长 16 字）。
    /// 真正的标题由 agent 跑完后的模型综结来定（见 <c>IAgentSessionStore.SetAutoTitle</c>）。
    /// </summary>
    public static string AutoTitle(string prompt)
    {
        var text = (prompt ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (text.Length == 0)
        {
            return "新会话";
        }

        var prefixes = new[] { "帮我看看", "帮我看下", "帮我看一下", "看一下", "看看", "看下", "查一下", "查下", "帮我", "帮忙", "麻烦", "给我", "请", "去", "来" };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var prefix in prefixes)
            {
                if (text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length + 1)
                {
                    text = text[prefix.Length..].TrimStart();
                    changed = true;
                }
            }
        }

        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= 16 ? text : text[..16] + "…";
    }
}
