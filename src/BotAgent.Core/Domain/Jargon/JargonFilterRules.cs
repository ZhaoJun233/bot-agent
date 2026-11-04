using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BotAgent.Domain.Jargon;

/// <summary>
/// 圈子黑话/俚语规则过滤与候选词提取器（纯函数、零 IO）。
/// 解决无效杂音提炼、模型 Token 浪费与违规词汇问题。
/// </summary>
public static class JargonFilterRules
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "这个", "那个", "什么", "怎么", "为什么", "可以", "不行", "没有", "不知道", "好的",
        "收到", "在吗", "哈哈", "确实", "真的", "现在", "今天", "明天", "昨天", "如果",
        "因为", "所以", "但是", "而且", "感觉", "觉得", "可能", "应该", "大家", "有人"
    };

    private static readonly Regex InvalidPattern = new(
        @"(https?://|\.com|\.cn|\d{7,}|[a-zA-Z0-9_.+-]+@[a-zA-Z0-9-]+\.[a-zA-Z0-9-.]+)",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex QuotePattern = new(
        "[\"“「『【]([^\"”」』】\\r\\n]{2,15})[\"”」』】]",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex SuffixPattern = new(
        @"(?:^|[是成叫个当做真]|[^A-Za-z0-9_\u4e00-\u9fa5])([\u4e00-\u9fa5]{2,5}(?:人|神|怪|王|党|鼠|狗|批|哥|姐|酱))(?=[，。！？、~～!\?\s]|$)",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// 判断一个短语是否符合黑话候选资格。
    /// </summary>
    public static bool IsCandidatePhrase(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return false;
        }

        var trimmed = phrase.Trim();
        if (trimmed.Length < 2 || trimmed.Length > 15)
        {
            return false;
        }

        // 排除停用词
        if (StopWords.Contains(trimmed))
        {
            return false;
        }

        // 排除 URL、邮箱、长数字
        if (InvalidPattern.IsMatch(trimmed))
        {
            return false;
        }

        // 排除纯数字或纯标点
        bool hasNonPunctuationOrDigit = false;
        foreach (var c in trimmed)
        {
            if (char.IsLetter(c))
            {
                hasNonPunctuationOrDigit = true;
                break;
            }
        }

        return hasNonPunctuationOrDigit;
    }

    /// <summary>
    /// 从一段对话文本中提取潜在的梗/黑话候选词（如引号、括号、书名号包裹，或特征短语）。
    /// </summary>
    public static IReadOnlyList<string> ExtractCandidatePhrases(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var results = new HashSet<string>(StringComparer.Ordinal);

        // 1. 匹配引号、括号、书名号包裹内容：“xxx”、"xxx"、『xxx』、「xxx」、【xxx】
        var quoteMatches = QuotePattern.Matches(text);
        foreach (Match m in quoteMatches)
        {
            if (m.Groups.Count > 1)
            {
                var cleaned = CleanPhrase(m.Groups[1].Value);
                if (IsCandidatePhrase(cleaned))
                {
                    results.Add(cleaned);
                }
            }
        }

        // 2. 匹配特征网络梗语法（支持句首、标点隔开或系词引导：如“急急国王”、“早八人”、“纯爱战神”）
        var suffixMatches = SuffixPattern.Matches(text);
        foreach (Match m in suffixMatches)
        {
            if (m.Groups.Count > 1)
            {
                var cleaned = CleanPhrase(m.Groups[1].Value);
                if (IsCandidatePhrase(cleaned))
                {
                    results.Add(cleaned);
                }
            }
        }

        return new List<string>(results);
    }

    private static string CleanPhrase(string s)
    {
        var trimmed = s.Trim().Trim('“', '”', '"', '「', '」', '『', '』', '【', '】');
        while (trimmed.Length > 2 && "是不是成了个当做真非太很比较像在被又".IndexOf(trimmed[0]) >= 0)
        {
            trimmed = trimmed[1..];
        }
        return trimmed;
    }
}
