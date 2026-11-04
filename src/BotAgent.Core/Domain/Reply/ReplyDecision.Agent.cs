using System;
using System.Linq;

namespace BotAgent.Domain.Reply;

/// <summary>自然对话驱动 agent 的任务文本清洗（从 ReplyDecision.cs 拆出，守 R6 行数上限）。</summary>
public static partial class ReplyDecisionRules
{
    /// <summary>自然对话托付给 agent 的任务文本上限（超了截断）。</summary>
    public const int MaxAgentTaskLength = 1000;

    /// <summary>
    /// 托付任务的清洗：保留换行（任务可能有多步），其余控制字符换成空格，去首尾空白，超长截断。
    /// 结果只交给服务端 agent 闸门，**不会**原样发进群里。
    /// </summary>
    public static string? SanitizeAgentTask(string? raw)
    {
        var text = raw ?? string.Empty;
        var chars = text.Select(c => c == '\n' ? c : char.IsControl(c) ? ' ' : c).ToArray();
        var task = new string(chars).Trim();
        if (task.Length == 0)
        {
            return null;
        }

        return task.Length <= MaxAgentTaskLength ? task : task[..MaxAgentTaskLength];
    }
}
