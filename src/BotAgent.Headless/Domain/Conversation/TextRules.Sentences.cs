using System.Text;

namespace BotAgent.Domain.Conversation;

/// <summary>
/// <see cref="TextRules" /> 的**分句**部分（纯函数、零 IO）：按句末标点切段、过短并段、最多四段。
/// 分开的理由：分句是全链路最容易出“标点错误分段”的地方（见 SplitSentences 的注释），
/// 单独一个文件方便对着号主反馈改；括号旁白与文本比对留在 TextRules.cs。
/// </summary>
public static partial class TextRules
{
    /// <summary>
    /// 按句末标点分句；过短的句子合并到相邻段，最多切 4 段（避免连发刷屏）。
    ///
    /// 这里踩过的坑（号主反馈“对标点或小数错误分段”）：
    ///   • 半角 `.` 曾经无条件当句末 —— “3.14”“1.5 倍”“v1.2”“github.com” 全被拦腰切；
    ///   • 连续的句末标点被拆开 —— “好耶！！！” 会在中间断，第二段以 “！！” 开头；
    ///   • 收尾的引号/括号落到下一段 —— “他说「好。」” 之后那段以 “」” 开头。
    /// 现在：半角点看前后文（前后是数字/字母就不算句末）、连续标点一次收走、
    /// 收尾符号跟着本段走；非常长的句子才退一步在逗号处断（不会憋出一条千字消息）。
    /// </summary>

    public static List<string> SplitSentences(string text)
    {
        const int MinSegmentLength = 6;
        const int MaxSegments = 4;
        const int SoftBreakLength = 60;   // 句内逗号处断行的长度下限

        var trimmed = text.Trim();
        if (trimmed.Length <= MinSegmentLength * 2)
        {
            return new List<string> { trimmed };
        }

        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            current.Append(ch);

            if (ch == '\n')
            {
                AddSegment(segments, current, MinSegmentLength);
                continue;
            }

            // 句内逗号：只有句子已经很长时才在它后面断（避免一逗就断、也避免千字一段）
            if (ch is '，' or '、' or '；' or ',' or ';' or '：' or ':')
            {
                if (current.Length >= SoftBreakLength)
                {
                    AddSegment(segments, current, MinSegmentLength);
                }

                continue;
            }

            if (!IsSentenceEnder(trimmed, i))
            {
                continue;
            }

            // 连续的句末标点一次收走：“！！！”“……”“？！” 不该被拆开
            while (i + 1 < trimmed.Length && IsSentenceEnder(trimmed, i + 1))
            {
                current.Append(trimmed[++i]);
            }

            // 收尾的引号 / 括号跟着本段走：“好。」” 不断在。后面
            while (i + 1 < trimmed.Length && IsSentenceCloser(trimmed[i + 1]))
            {
                current.Append(trimmed[++i]);
            }

            AddSegment(segments, current, MinSegmentLength);
        }

        // 尾部残句并入上一段，避免丢字
        if (current.Length > 0)
        {
            var tail = current.ToString().Trim();
            if (segments.Count == 0)
            {
                segments.Add(tail);
            }
            else
            {
                segments[^1] = (segments[^1] + tail).Trim();
            }
        }

        // 超过段数上限：多余内容合并进最后一段（不丢内容）
        if (segments.Count > MaxSegments)
        {
            var head = segments.Take(MaxSegments - 1).ToList();
            head.Add(string.Concat(segments.Skip(MaxSegments - 1)));
            segments = head;
        }

        return segments.Where(s => s.Length > 0).ToList();
    }

    /// <summary>够长就单独成段，否则继续往后攒（短句与下一句合并，读起来更像人）。</summary>
    public static void AddSegment(List<string> segments, System.Text.StringBuilder current, int minLength)
    {
        if (current.Length < minLength)
        {
            return;
        }

        segments.Add(current.ToString().Trim());
        current.Clear();
    }

    /// <summary>
    /// 这个位置算不算“句末”。半角点 / 叹号要额外看前后文：
    /// 前后是数字就是小数（3.14 / v1.2），后面紧接字母就是域名或文件名（github.com / a.exe）。
    /// </summary>
    public static bool IsSentenceEnder(string text, int index)
    {
        var ch = text[index];
        if (ch is '。' or '！' or '？' or '…' or '．' or '｡')
        {
            return true;
        }

        if (ch is not '!' and not '?' and not '.')
        {
            return false;
        }

        if (ch != '.')
        {
            return true;
        }

        var before = index > 0 ? text[index - 1] : '\0';
        var after = index + 1 < text.Length ? text[index + 1] : '\0';
        if (char.IsDigit(before) || char.IsDigit(after))
        {
            return false;   // 小数、版本号、IP、时间
        }

        return !char.IsLetter(after);   // 紧接字母：github.com / 文件名
    }

    /// <summary>收尾符号（引号、括号、波浪号）：断句时留在前一段。</summary>
    public static bool IsSentenceCloser(char ch)
        => ch is '」' or '』' or '】' or '》' or '〉' or '）' or ')' or ']' or '”' or '’' or '～' or '~' or '"' or '\'' or '〗' or '〞';
}
