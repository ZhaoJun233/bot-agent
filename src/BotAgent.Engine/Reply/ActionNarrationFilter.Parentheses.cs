using System.Text;
using System.Text.RegularExpressions;

namespace BotAgent.Services.Reply;

public sealed partial class ActionNarrationFilter
{
    private static readonly Regex FenceStart = new(@"(?m)^[ \t]{0,3}(?<fence>\x60{3,}|~{3,})", RegexOptions.Compiled);
    private static readonly Regex SeparatedWords = new(@"[\p{L}\p{N}_]\s+[\p{L}\p{N}_]", RegexOptions.Compiled);

    /// <summary>
    /// Remove natural-language parentheses before and after semantic rewriting.
    /// Code, explicit math, URLs and expression-shaped groups retain their contents.
    /// This rule has no model dependency, candidate count limit or wording dictionary.
    /// </summary>
    public static string StripNaturalLanguageParentheses(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var protectedText = TechnicalSpans(text);
        var output = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (protectedText[index] || text[index] is not ('(' or '（'))
            {
                output.Append(text[index]);
                continue;
            }

            var end = ParenthesisEnd(text, index, protectedText);
            var contentEnd = end < text.Length ? end : text.Length;
            if (IsTechnicalGroup(text, index, contentEnd, protectedText))
                output.Append(text, index, Math.Min(end + 1, text.Length) - index);
            // An unclosed natural-language aside is suppressed through the end,
            // rather than allowing malformed model output to bypass the filter.
            index = end;
        }
        return output.ToString().Trim();
    }

    private static bool[] TechnicalSpans(string text)
    {
        var mask = new bool[text.Length];
        void Protect(int start, int length) => Array.Fill(mask, true, start, length);
        foreach (Match match in Code.Matches(text)) Protect(match.Index, match.Length);
        // Preserve the unfinished code region itself, without exempting preceding
        // natural-language parentheses from the deterministic rule.
        foreach (Match match in FenceStart.Matches(text))
        {
            var fence = match.Groups["fence"];
            if (mask[fence.Index]) continue;
            Protect(fence.Index, text.Length - fence.Index);
            break;
        }
        for (var index = 0; index < text.Length; index++)
        {
            if (mask[index] || text[index] != '\u0060') continue;
            var end = text.IndexOf('\n', index);
            if (end < 0) end = text.Length;
            Protect(index, end - index);
            index = end - 1;
        }
        foreach (Match match in Latex.Matches(text))
            if (!mask[match.Index]) Protect(match.Index, match.Length);
        foreach (Match match in Url.Matches(text))
        {
            if (mask[match.Index]) continue;
            var url = match.Value;
            var balance = url.Count(c => c == '(') - url.Count(c => c == ')');
            var length = url.Length;
            while (length > 0 && url[length - 1] == ')' && balance < 0) { length--; balance++; }
            Protect(match.Index, length);
        }
        return mask;
    }

    private static int ParenthesisEnd(string text, int start, bool[] protectedText)
    {
        var depth = 1;
        for (var index = start + 1; index < text.Length; index++)
        {
            if (protectedText[index]) continue;
            if (text[index] is '(' or '（') depth++;
            else if (text[index] is ')' or '）')
            {
                if (--depth == 0) return index;
            }
        }
        return text.Length;
    }

    private static bool IsTechnicalGroup(string text, int start, int end, bool[] protectedText)
    {
        var unprotected = new StringBuilder();
        var hasProtected = false;
        for (var index = start + 1; index < end; index++)
        {
            if (protectedText[index]) hasProtected = true;
            else unprotected.Append(text[index]);
        }
        var expression = unprotected.ToString().Trim();
        // A protected URL/code fragment inside an otherwise natural-language aside
        // does not make the entire aside exempt from removal.
        var functionArgument = text[start] == '(' && start > 0 && IsIdentifierCharacter(text[start - 1]);
        if (functionArgument)
        {
            var nameStart = start - 1;
            while (nameStart > 0 && IsIdentifierCharacter(text[nameStart - 1])) nameStart--;
            functionArgument = char.IsAsciiLetter(text[nameStart]) || text[nameStart] == '_';
        }
        if (expression.Length == 0) return hasProtected || functionArgument;
        if (functionArgument && IsQuotedArgument(expression)) return true;
        if (!expression.All(c => IsIdentifierCharacter(c) || char.IsLetter(c) || char.IsWhiteSpace(c) ||
            char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.MathSymbol ||
            "()+-*/=^·×÷%.,[]".Contains(c))) return false;
        if (SeparatedWords.IsMatch(expression)) return false;
        return functionArgument ||
            expression.Any(c => "+-*/=^·×÷%".Contains(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.MathSymbol) ||
            expression.All(c => char.IsAsciiDigit(c) || char.IsWhiteSpace(c) || ".,[]()".Contains(c)) ||
            expression.Length == 1 && (char.IsAsciiLetter(expression[0]) || expression[0] is >= '\u0370' and <= '\u03FF');
    }

    private static bool IsIdentifierCharacter(char ch) => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.';

    private static bool IsQuotedArgument(string text)
        => text.Length >= 2 && text[0] is '\'' or '"' && text[^1] == text[0];
}
