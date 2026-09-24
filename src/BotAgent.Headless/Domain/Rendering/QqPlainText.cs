using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BotAgent.Domain.Rendering;

/// <summary>
/// Markdown → QQ 纯文本（V3 §10: P4 呈现层）。
///
/// 定位：**发送前的纯函数**，只做呈现降级，不参与“发不发”的判定（那是 P2 的事），
/// 也不做 HTML 渲染。调用点见 <c>BotAgentHost.SendWithCadenceAsync</c>（聊天回复这一路），
/// 位置在既有分句/长度限制**之前**（V3 §10.2 最后一条）。
///
/// 三条底线（都有对应单测）：
///   1. **只删装饰、不删内容**：标题/粗斜体/删除线/代码围栏只去掉标记，正文一字不动；
///   2. **URL 是受保护片段**：链接里的符号、查询串里的 <c>_ * ~ # &amp; =</c> 一律不动，
///      也不会在 URL 中间插入换行（避免被后续分句切成两条消息）；
///   3. **QQ 自己的语法不动**：<c>@某人</c>、<c>[表情]</c>/<c>[图片]</c> 这类占位、数字小数点、
///      版本号（<c>v1.2.3</c>）、域名、连续标点（<c>!!!</c>）都不是 Markdown，原样保留。
///
/// 为什么用「受保护片段 + 哨兵」而不是一路正则替换：正文里合法出现的
/// <c>snake_case</c>、<c>2*3*4</c>、URL 里的下划线，都会被天真的强调正则误伤；
/// 先把代码与 URL 摘出来，再对剩下的纯文本做强调降级，才能保证“不破坏正文”。
/// </summary>
public static class QqPlainText
{
    private const char SentinelOpen = '\uE000';
    private const char SentinelClose = '\uE001';

    /// <summary>行内代码：`x` → 内容（保留内容，去掉反引号）。</summary>
    private static readonly Regex InlineCode = new(
        "`([^`\\n]+)`", RegexOptions.Compiled);

    /// <summary>Markdown 图片：![alt](url) → alt (url)。</summary>
    private static readonly Regex ImageLink = new(
        "!\\[([^\\]\\n]*)\\]\\(\\s*([^)\\s]+)(?:\\s+\"[^\"]*\")?\\s*\\)", RegexOptions.Compiled);

    /// <summary>Markdown 链接：[标题](url) → 标题 (url)（URL 一定保留）。</summary>
    private static readonly Regex MdLink = new(
        "\\[([^\\]\\n]+)\\]\\(\\s*([^)\\s]+)(?:\\s+\"[^\"]*\")?\\s*\\)", RegexOptions.Compiled);

    /// <summary>裸 URL（http/https/www）：整段受保护，避免被强调规则或分句破坏。</summary>
    private static readonly Regex Url = new(
        "(?:https?://|www\\.)[^\\s<>\"'，。；：！？、）】」』]+", RegexOptions.Compiled);

    // 强调：要求内容紧贴标记（内层无空格），并且 `*` / `_` 不与两侧的单词字符相连 ——
    // 这样 `2 * 3 * 4`、`hello_world_foo`、`a*b*c` 都不会被误当成斜体。
    private static readonly Regex Bold = new(
        "(?<!\\*)\\*\\*(?<t>[^*\\s](?:[^*\\n]*[^*\\s])?)\\*\\*(?!\\*)", RegexOptions.Compiled);
    private static readonly Regex BoldUnderscore = new(
        "(?<![A-Za-z0-9_])__(?<t>[^_\\s](?:[^_\\n]*[^_\\s])?)__(?![A-Za-z0-9_])", RegexOptions.Compiled);
    private static readonly Regex Strike = new(
        "(?<!~)~~(?<t>[^~\\s](?:[^~\\n]*[^~\\s])?)~~(?!~)", RegexOptions.Compiled);
    private static readonly Regex ItalicStar = new(
        "(?<![A-Za-z0-9*])\\*(?<t>[^*\\s](?:[^*\\n]*[^*\\s])?)\\*(?![A-Za-z0-9*])", RegexOptions.Compiled);
    private static readonly Regex ItalicUnderscore = new(
        "(?<![A-Za-z0-9_])_(?<t>[^_\\s](?:[^_\\n]*[^_\\s])?)_(?![A-Za-z0-9_])", RegexOptions.Compiled);

    // 兜底：配对不成立的 ** / ~~ 才会被当成（没写完的）Markdown 控制串删掉（V3 §10.3）。
    // 但“只有一段、又夹在正文里”的连续标记是**内容**：`2**3`（幂运算）、`开心~~~`（网络语气），
    // 老实现无条件 `\*{2,}` / `~{2,}` 会把它吃掉（2**3 → 23）。判据见 Inline：
    //   · 这一行有 ≥2 段标记 → 本来想写一对，配对不成立的是残留 → 删；
    //   · 只有 1 段、但顶在行首（允许前导空白）→ 是“没写完的标记” → 删；
    //   · 只有 1 段、在正文中间或收尾 → 正文，不动。
    private static readonly Regex LeftoverDoubleStar = new(@"\*{2,}", RegexOptions.Compiled);
    private static readonly Regex LeftoverDoubleTilde = new(@"~{2,}", RegexOptions.Compiled);
    private static readonly Regex StarRuns = new(@"\*{2,}", RegexOptions.Compiled);
    private static readonly Regex TildeRuns = new(@"~{2,}", RegexOptions.Compiled);

    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex HeadingTail = new(@"\s+#+\s*$", RegexOptions.Compiled);
    private static readonly Regex Blockquote = new(@"^\s{0,3}>\s?", RegexOptions.Compiled);
    private static readonly Regex HorizontalRule = new(@"^\s{0,3}([-*_])(?:\s*\1){2,}\s*$", RegexOptions.Compiled);
    private static readonly Regex BulletList = new(@"^(\s*)[*+]\s+", RegexOptions.Compiled);
    private static readonly Regex BlankRun = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex SentinelBack = new("\uE000(\\d+)\uE001", RegexOptions.Compiled);

    /// <summary>把一个（模型生成的）回复文本降级成适合 QQ 的纯文本。</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // 输入里可能**自带哨兵字符**（模型胡写 / 群里转述了我们的实现）：先剥掉，
        // 否则下面的哨兵还原会索引到不存在的片段（抛异常 → 这一轮回复整条丢掉），
        // 或者被伪造成“受保护片段”绕过降级。
        normalized = normalized.Replace(SentinelOpen.ToString(), string.Empty)
            .Replace(SentinelClose.ToString(), string.Empty);

        var lines = normalized.Split('\n');
        var kept = new List<string>(lines.Length);
        var inFence = false;
        var fenceChar = '\0';

        foreach (var line in lines)
        {
            var probe = line.TrimStart();

            // 代码围栏：``` / ~~~ 的那一行本身丢掉（语言名也不留 —— QQ 里那行只是噪声），
            // 围栏里的内容原样保留（不再做行内降级，免得把代码里的 * 和 _ 改写掉）。
            // 开围栏允许带语言名（```csharp）；闭围栏必须是“只有标记”的一行。
            if (TryFence(probe, out var fenceMark, out var fenceLen))
            {
                if (!inFence)
                {
                    inFence = true;
                    fenceChar = fenceMark;
                    continue;
                }

                if (fenceMark == fenceChar && probe.TrimEnd().Length == fenceLen)
                {
                    inFence = false;
                    continue;
                }

                // 围栏里出现的 ``` 起始行（代码本身就长这样）：当内容保留
                kept.Add(line);
                continue;
            }

            kept.Add(inFence ? line : LineLevel(line));
        }

        var joined = string.Join('\n', kept);
        joined = BlankRun.Replace(joined, "\n\n");   // 连续空行压到最多一个空行
        return joined.Trim('\n');
    }

    /// <summary>这一行是不是围栏标记行（3 个及以上连续的 ` 或 ~ 起头）。</summary>
    private static bool TryFence(string probe, out char marker, out int runLength)
    {
        marker = '\0';
        runLength = 0;
        if (probe.Length < 3 || (probe[0] != '`' && probe[0] != '~'))
        {
            return false;
        }

        marker = probe[0];
        while (runLength < probe.Length && probe[runLength] == marker)
        {
            runLength++;
        }

        return runLength >= 3;
    }

    /// <summary>行级降级：引用、标题、分隔线、列表符号（都不动正文）。</summary>
    private static string LineLevel(string line)
    {
        var work = line;

        if (work.TrimStart().StartsWith('>'))
        {
            work = Blockquote.Replace(work, string.Empty);
        }

        var heading = Heading.Match(work);
        if (heading.Success)
        {
            work = HeadingTail.Replace(heading.Groups[1].Value, string.Empty);
        }

        // 只由 - * _ 组成的分隔线：纯装饰，整行去掉（不会误伤正文 —— 它不含任何其它字符）
        if (work.Length > 0 && HorizontalRule.IsMatch(work))
        {
            return string.Empty;
        }

        // 列表符号保持可读：* / + 统一成 - （避免和斜体的 * 混淆），有序列表不动
        if (BulletList.IsMatch(work))
        {
            work = BulletList.Replace(work, "$1- ");
        }

        return Inline(work);
    }

    /// <summary>行内降级：代码 → 链接 → URL 保护 → 强调 → 还原。</summary>
    private static string Inline(string line)
    {
        if (line.Length == 0)
        {
            return line;
        }

        var protectedSpans = new List<string>();
        var work = InlineCode.Replace(line, m => Protect(m.Groups[1].Value, protectedSpans));
        work = ImageLink.Replace(work, m => LinkSpan(m.Groups[1].Value, m.Groups[2].Value, protectedSpans));
        work = MdLink.Replace(work, m => LinkSpan(m.Groups[1].Value, m.Groups[2].Value, protectedSpans));
        work = Url.Replace(work, m => Protect(m.Value, protectedSpans));

        work = Bold.Replace(work, "${t}");
        work = BoldUnderscore.Replace(work, "${t}");
        work = Strike.Replace(work, "${t}");
        work = ItalicStar.Replace(work, "${t}");
        work = ItalicUnderscore.Replace(work, "${t}");
        if (StarRuns.Matches(work).Count >= 2 || RunsAtLineStart(StarRuns, work))
        {
            work = LeftoverDoubleStar.Replace(work, string.Empty);
        }

        if (TildeRuns.Matches(work).Count >= 2 || RunsAtLineStart(TildeRuns, work))
        {
            work = LeftoverDoubleTilde.Replace(work, string.Empty);
        }

        return SentinelBack.Replace(work, m =>
            int.TryParse(m.Groups[1].Value, out var index) && index >= 0 && index < protectedSpans.Count
                ? protectedSpans[index]
                : string.Empty);
    }

    /// <summary>[标题](url) → 标题 (URL)；标题为空时只留 URL。两侧都保留（URL 绝不丢）。</summary>
    private static string LinkSpan(string label, string url, List<string> spans)
    {
        var text = label.Trim();
        var link = Protect(url.Trim(), spans);
        return text.Length == 0 ? link : text + " (" + link + ")";
    }

    /// <summary>把一段受保护内容（URL / 代码）换成哨兵，稍后原样还原。</summary>
    /// <summary>
    /// 行首（允许前导空白）是不是就顶着一串标记 —— 那是“没写完的 Markdown”，不是正文。
    /// 用来把 `**没写完的粗体` 这种控制串删掉，同时保住 `开心~~~` 这种收尾语气。
    /// </summary>
    private static bool RunsAtLineStart(Regex runs, string line)
    {
        var first = runs.Match(line);
        return first.Success && line[..first.Index].Trim().Length == 0;
    }

    private static string Protect(string value, List<string> spans)
    {
        // 已经是哨兵的内容（例如 URL 落在行内代码里）不再二次包装
        if (value.Contains(SentinelOpen))
        {
            return value;
        }

        spans.Add(value);
        return SentinelOpen + (spans.Count - 1).ToString() + SentinelClose;
    }
}
