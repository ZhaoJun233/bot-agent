namespace BotAgent.Domain.Reply;

/// <summary>
/// 模型输出的**文本整形**（纯函数、零 IO）：去代码围栏、从一段话里抠出我们约定的那个 JSON、
/// 判断"这段像不像我们的协议"、数可见字符、读分数、归一情绪词。
///
/// 为什么单独一个文件（批次 6）：这些都是"看一串文本长什么样"的小工具，与解析**判定**无关 ——
/// 判定（发不发 / 发什么 / 为什么）在 <see cref="ModelOutputParser" /> + <see cref="ReplyDecisionRules" />。
/// 分开之后两边都短到能一眼读完，`ModelOutputParser` 也就不会因为塞满字段读取而变成一个大文件。
/// </summary>
public static class ModelOutputText
{
    /// <summary>
    /// 从一段文本里抠出第一个“像我们约定的” JSON 对象（带花括号配对、跳过字符串里的括号）。
    /// 判据是里面出现了我们的字段名 —— 免得把群友消息里引用的一小段 JSON 当成模型输出。
    /// </summary>
    public static bool TryExtractJsonBlock(string text, out string block)
    {
        block = string.Empty;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{')
            {
                continue;
            }

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var j = i; j < text.Length; j++)
            {
                var c = text[j];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                switch (c)
                {
                    case '{':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        if (depth == 0)
                        {
                            var candidate = text[i..(j + 1)];
                            if (LooksLikeSchemaJson(candidate))
                            {
                                block = candidate;
                                return true;
                            }

                            j = text.Length; // 这个块不是，继续找下一个 {
                        }

                        break;
                }
            }
        }

        return false;
    }

    /// <summary>这段文本里有没有我们的约定字段（说明它想输出的是结构化回复）。</summary>
    public static bool LooksLikeSchemaJson(string text)
        => text.Contains("\"suitability\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"reply\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"sticker\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"replyTo\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"mood\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"speak\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"search\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"poke\"", StringComparison.OrdinalIgnoreCase);

    /// <summary>可见字符数：忽略空白、零宽空格、BOM。用于判断输出是不是被上游截断的碎片。</summary>
    public static int VisibleLength(string text)
        => text.Count(c => !char.IsWhiteSpace(c) && c != '\u200b' && c != '\ufeff');

    /// <summary>
    /// 读发言适合度评分。
    /// 模型实际会输出 85 / 85.0 / 85.5 / "85" 各种形式；
    /// 旧实现用 GetInt32() 读，碰到 85.0 直接抛异常 → 整段 JSON 被当成回复发进群。
    /// </summary>
    public static int? ReadScore(System.Text.Json.JsonElement element)
    {
        if (element.TryGetInt32(out var integer))
        {
            return integer;
        }

        return element.TryGetDouble(out var value) && !double.IsNaN(value) && !double.IsInfinity(value)
            ? (int)Math.Round(value)
            : null;
    }

    /// <summary>去掉 ```json / ``` 围栏，返回内部正文（结构化输出的几个解析入口共用）。</summary>
    public static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstLineEnd = text.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            return text;
        }

        var body = text[(firstLineEnd + 1)..];
        var fenceEnd = body.LastIndexOf("```", StringComparison.Ordinal);
        return (fenceEnd >= 0 ? body[..fenceEnd] : body).Trim();
    }

    /// <summary>
    /// 把模型写的“情绪词”归一到一个固定集合：程序侧要根据它调发言策略（阈值/表情包/语音），
    /// 所以不能让模型自由发挥（它会写出“有点不开心又不想说话”这种句子）。
    /// 认不出来的一律当“中性”（策略上等于不变），宁可不干预。
    /// </summary>
    public static string NormalizeVibe(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "中性";
        }

        // 顺序有讲究：先识别“冲突”（最需要于预的），再低落/求助/生气，最后正向
        if (VibeHit(text, "吵架", "对线", "冲突", "互喷", "抬杠", "阴阳"))
        {
            return "吵架";
        }

        if (VibeHit(text, "低落", "难过", "伤心", "失落", "孤独", "寂寞", "崩溃", "委屈", "焦虑", "压力", "丧"))
        {
            return "低落";
        }

        if (VibeHit(text, "求助", "求解", "请教", "求助无回应"))
        {
            return "求助";
        }

        if (VibeHit(text, "生气", "愤怒", "不满", "恼", "烦"))
        {
            return "生气";
        }

        if (VibeHit(text, "吐槽", "抱怨", "牢骚", "疑惑"))
        {
            return "吐槽";
        }

        if (VibeHit(text, "开心", "高兴", "兴奋", "欢乐", "起哄", "玩笑", "笑"))
        {
            return "开心";
        }

        return "中性";
    }

    private static bool VibeHit(string text, params string[] words)
        => words.Any(w => text.Contains(w, StringComparison.Ordinal));

    /// <summary>截断到 <paramref name="max" /> 个字符（多出来的用省略号收尾）。</summary>
    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
