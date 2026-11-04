using System.Text;

namespace BotAgent.Domain.Conversation;

/// <summary>
/// 文本规则（纯函数、零 IO）：旁白括号的剥离与判定、内容标记识别、分句、缩短、同句比对、链接判定。
/// 批次 1「纯函数下沉」：这些方法**从 BotAgentHost 原样搬来**，判断条件一字未改；唯一一处不是搬的是错位的文档注释
/// （SplitSentences 的说明原本挂在 CarriesLink 上方）——搬回来时顺手归位。
/// 例外：`AnnotateBracketAsides` 没有跟过来 —— 它要收协议端的 `QqChatMessage`（适配层类型），
/// 搬过来会让 domain 反向依赖适配层，所以留在 agent 里（见 docs/engineering/architecture-optimization.md §7）。
/// </summary>
public static partial class TextRules
{
    /// <summary>只剩标点、符号、emoji 与空白（不构成内容）。</summary>
    public static bool IsOnlyDecoration(string text)
    {
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch) || char.IsSurrogate(ch))
            {
                continue;
            }

            if (ch is '～' or '~' or '…' or '·' or '　' or '〰' or '﹏')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    public static readonly char[] BracketOpen = ['（', '(', '［', '[', '【', '｛', '{', '〈', '《'];
    public static readonly char[] BracketClose = ['）', ')', '］', ']', '】', '｝', '}', '〉', '》'];

    /// <summary>开头就是一段括号（且不是我们的内容标记）→ 取走它，并把括号里的内容给出去。</summary>
    public static bool TryTakeLeadingBracket(string text, out string rest, out string inner)
    {
        rest = text;
        inner = string.Empty;
        if (text.Length < 2 || Array.IndexOf(BracketOpen, text[0]) < 0)
        {
            return false;
        }

        var close = FindClose(text, 0);
        if (close < 0)
        {
            return false;   // 括号没闭合，当普通文本
        }

        var content = text[1..close];
        if (IsContentMarker(content) || LooksNumeric(content))
        {
            return false;
        }

        inner = content;
        rest = text[(close + 1)..];
        return true;
    }

    /// <summary>结尾是一段括号（且不是我们的内容标记）→ 取走它，并把括号里的内容给出去。</summary>
    public static bool TryTakeTrailingBracket(string text, out string rest, out string inner)
    {
        rest = text;
        inner = string.Empty;
        if (text.Length < 2 || Array.IndexOf(BracketClose, text[^1]) < 0)
        {
            return false;
        }

        // 从右往左找配对的左括号（简单配对：碰到另一个右括号就放弃，不当旁白）
        var openIndex = -1;
        for (var i = text.Length - 2; i >= 0; i--)
        {
            if (Array.IndexOf(BracketClose, text[i]) >= 0)
            {
                return false;
            }

            if (Array.IndexOf(BracketOpen, text[i]) >= 0)
            {
                openIndex = i;
                break;
            }
        }

        if (openIndex < 0)
        {
            return false;
        }

        var content = text[(openIndex + 1)..^1];
        if (IsContentMarker(content) || LooksNumeric(content))
        {
            return false;
        }

        inner = content;
        rest = text[..openIndex];
        return true;
    }

    /// <summary>
    /// 括号里是不是“纯数字/符号”（如「（2026）」「（1.5）」）—— 这类是正文的一部分，不当旁白剥掉。
    /// 判据：里面一个字母/汉字都没有。（汉字在 .NET 里算 Letter，所以一个判断就够）
    /// </summary>
    public static bool LooksNumeric(string inner)
        => inner.Trim().Length > 0 && !inner.Any(char.IsLetter);

    public static int FindClose(string text, int openIndex)
    {
        for (var i = openIndex + 1; i < text.Length; i++)
        {
            if (Array.IndexOf(BracketClose, text[i]) >= 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 这段括号里是不是“机器人写的内容标记”（对方发表情/图片/语音的记录）。
    /// 这些不是旁白，不能当括号剥掉——不然群友发的表情就被抹掉了（踩过）。
    /// </summary>
    public static bool IsContentMarker(string inner)
    {
        var t = inner.Trim();
        foreach (var marker in new[] { "图片", "表情", "动画表情", "语音", "视频", "文件", "音乐", "合并转发", "戳一戳", "分享", "卡片", "位置", "链接", "视频通话" })
        {
            if (t.StartsWith(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>日志用短文本（过长会把一行日志撞成好几行）。</summary>
    public static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// 文字里有没有**链接**。这是唯一保留的“自动补发文字”理由：念出来完全没用 ✗。
    /// 别的（号码/@/命令）不再猜 —— 交给模型用 both:true 自己声明（2026-09-21）。
    /// </summary>
    public static bool CarriesLink(string text)
        => text.Contains("http://", StringComparison.OrdinalIgnoreCase)
           || text.Contains("https://", StringComparison.OrdinalIgnoreCase)
           || text.Contains("www.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// “语音说的”和“文字写的”是不是同一句（去空白去标点后相等，或短句被长句包含）。
    /// 为什么需要：模型给的 speak 与 reply 常常只差标点/语气词 ✗（「好呀，那我们八点见」vs「好呀八点见！」），
    /// 严格相等会漏判 → 同一条内容语音+文字各发一遍（管理员 2026-09-21 报的）。
    /// </summary>
    public static bool SameSaid(string a, string b)
    {
        static string Norm(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch))
                {
                    continue;
                }

                if ("，。！？；：、,.!?;:…—~～\"'“”‘’（）()《》<>【】[]-".IndexOf(ch) >= 0)
                {
                    continue;
                }

                sb.Append(ch);
            }

            return sb.ToString();
        }

        var na = Norm(a);
        var nb = Norm(b);
        if (na.Length == 0 || nb.Length == 0)
        {
            return false;
        }

        if (string.Equals(na, nb, StringComparison.Ordinal))
        {
            return true;
        }

        var (shortOne, longOne) = na.Length <= nb.Length ? (na, nb) : (nb, na);
        return shortOne.Length >= 6 && longOne.Contains(shortOne, StringComparison.Ordinal);
    }
}
