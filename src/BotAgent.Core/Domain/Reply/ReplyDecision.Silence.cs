using System.Text.RegularExpressions;

namespace BotAgent.Domain.Reply;

public static partial class ReplyDecisionRules
{
    // A bounded compatibility grammar for whole abstention notices, not a substring
    // blacklist or a general language classifier. Unknown prose stays normal content.
    private static readonly Regex AbstentionClause = new(
        @"\A(?:(?:本|这)(?:轮|次)|暂时|暂且|先|继续)?(?:保持沉默|不发言|不回复|不回应|不接话|不插话|不参与|不介入|无需(?:发言|回复|回应|接话|插话|参与|介入)|不必(?:发言|回复|回应|参与|介入)|旁听|旁观)\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ParticipationReason = new(
        @"\A(?:(?:跟|和|与)我(?:无关|没关系|没有关系)|不关我(?:的)?事|(?:没|没有)(?:点名|叫我|提到我|问我|@我)|" +
        @"(?:@|艾特|提到|点名|问)(?:的)?是(?:其他人|别人|群主|群友|管理员)|(?:当前|这个|本轮)话题由(?:其他人|别人)(?:处理|回答))\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex AnswerUnknown = new(
        @"\A(?:也)?(?:我)?(?:不知道答案|不清楚答案|无法确定答案)\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ClauseBreak = new(@"[\s，,。.;；！!]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>
    /// Exact protocol marker or a whole, short internal abstention notice. Never
    /// suppress merely because a quote, explanation or normal reply mentions silence.
    /// </summary>
    public static bool IsSilenceOnlyReply(string? text)
    {
        if (IsExactSilentMarker(text)) return true;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 240) return false;
        var body = text.Trim();
        if (body[0] is not ('(' or '（')) return AbstentionClause.IsMatch(body.TrimEnd('。', '.', '！', '!'));

        // Each top-level parenthesis group must consist entirely of known clauses.
        // Text outside those groups (including questions or quotes) prevents suppression.
        while (body.Length > 0)
        {
            var close = body[0] switch { '(' => ')', '（' => '）', _ => '\0' };
            if (close == '\0') return false;
            var end = body.IndexOf(close, 1);
            if (end < 0 || !IsAbstentionGroup(body[1..end])) return false;
            body = body[(end + 1)..].TrimStart();
            body = body.TrimStart('。', '.', '！', '!', '，', ',', '；', ';').TrimStart();
        }
        return true;
    }

    private static bool IsAbstentionGroup(string body)
    {
        var foundDecision = false;
        foreach (var clause in ClauseBreak.Split(body).Where(c => c.Length > 0))
        {
            if (AbstentionClause.IsMatch(clause) || ParticipationReason.IsMatch(clause)) foundDecision = true;
            else if (clause is "不接" or "安静" || AnswerUnknown.IsMatch(clause)) continue;
            else return false;
        }
        return foundDecision;
    }
}
