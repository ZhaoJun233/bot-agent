using System.Text;

namespace BotAgent.Domain.Agent;

/// <summary>
/// 任务结果正文的清洗与有界截断（#114）：纯函数，不落盘、不读时钟、不认识通道。
///
/// 为什么放在 Domain：运行时要在<b>存进快照之前</b>就把正文洗干净并截到上限（#114 §2.5），
/// 而这段逻辑原来藏在 ZBA 适配器里、只有 ZBA 用得到。搬到 Domain 后：
/// ① 运行时可以把"只对工作区来源采集正文"也做成通道传下来的布尔开关；
/// ② 将来 <c>//</c> 或别的通道复用同一口径时不会各写一份。
/// </summary>
public static class AgentTaskText
{
    /// <summary>
    /// 清洗：CRLF 归一为 LF、孤立 CR 归一为换行、丢掉其它控制字符、首尾 Trim。
    /// 换行与制表符保留 —— 它们是多行答复的排版。
    /// </summary>
    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') continue;
                builder.Append('\n');
                continue;
            }

            if (current == '\n' || current == '\t') builder.Append(current);
            else if (!char.IsControl(current)) builder.Append(current);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// 截断到 <paramref name="maxChars"/>：截断点不落在代理对中间；
    /// 返回值里的 <c>Chars</c> 是<b>清洗后原始长度</b>（不是截断后长度）—— 面板用它显示"共 N 字，已截断"。
    /// </summary>
    public static (string Text, bool Truncated, int Chars) Clamp(string cleaned, int maxChars)
    {
        if (maxChars < 1) throw new ArgumentOutOfRangeException(nameof(maxChars), "截断上限必须为正数。");
        if (string.IsNullOrEmpty(cleaned)) return (string.Empty, false, 0);
        if (cleaned.Length <= maxChars) return (cleaned, false, cleaned.Length);

        var cut = maxChars;
        if (char.IsHighSurrogate(cleaned[cut - 1])) cut--;
        return (cleaned[..cut], true, cleaned.Length);
    }
}
