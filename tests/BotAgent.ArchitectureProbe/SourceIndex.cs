using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BotAgent.ArchitectureProbe;

/// <summary>
/// 一份源码文件的三个视图（三者「按字符位置」一一对应，换行原样保留）：
///   <see cref="Raw" />        原文；
///   <see cref="NoComments" /> 注释被空白替换，字符串/字符字面量原样保留 —— 找 SQL 字面量、File.、DateTimeOffset.Now 用这个；
///   <see cref="CodeOnly" />   注释与字符串/字符字面量内容都被空白替换（插值孔里的代码保留）—— 数花括号、量方法长度用这个。
/// </summary>
internal sealed class SourceFile
{
    public required string RelativePath { get; init; }
    public required string[] Lines { get; init; }
    public required string Raw { get; init; }
    public required string NoComments { get; init; }
    public required string CodeOnly { get; init; }
    public required string[] CodeOnlyLines { get; init; }

    public int LineCount => Lines.Length;

    /// <summary>相对路径是否落在某个目录下（目录写成 <c>Domain</c> / <c>Domain/Reply</c> 这种正斜杠形式）。</summary>
    public bool Under(string folder)
    {
        if (string.IsNullOrEmpty(folder) || folder == "." || folder == "/")
        {
            return true; // 空 = 全仓
        }

        var prefix = folder.TrimEnd('/') + "/";
        return RelativePath.StartsWith(prefix, StringComparison.Ordinal);
    }
}

/// <summary>整个 <c>src/BotAgent.Headless</c> 的源码快照（只读，纯确定性）。</summary>
internal sealed class SourceIndex
{
    public required string Root { get; init; }
    public required IReadOnlyList<SourceFile> Files { get; init; }

    public int TotalLines => Files.Sum(f => f.LineCount);

    public IEnumerable<SourceFile> In(string folder) => Files.Where(f => f.Under(folder));

    public SourceFile? ByPath(string relativePath)
        => Files.FirstOrDefault(f => f.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>按相对路径排序后的「文件 → 命中数」，只保留命中数 &gt; 0 的项。</summary>
    public IReadOnlyList<(string Path, int Count)> PerFile(string folder, Func<SourceFile, int> measure)
        => In(folder)
            .Select(f => (Path: f.RelativePath, Count: measure(f)))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Path, StringComparer.Ordinal)
            .ToList();

    public static SourceIndex Load()
    {
        var root = ResolveSourceRoot();
        var files = new List<SourceFile>();
        foreach (var full in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                                   .OrderBy(p => p, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            if (IsBuildOutput(rel))
            {
                continue;
            }

            var raw = File.ReadAllText(full);
            CsharpText.Strip(raw, out var noComments, out var codeOnly);
            files.Add(new SourceFile
            {
                RelativePath = rel,
                Lines = SplitLines(raw),
                Raw = raw,
                NoComments = noComments,
                CodeOnly = codeOnly,
                CodeOnlyLines = SplitLines(codeOnly),
            });
        }

        return new SourceIndex { Root = root, Files = files };
    }

    private static bool IsBuildOutput(string relativePath)
        => relativePath.StartsWith("obj/", StringComparison.Ordinal)
           || relativePath.StartsWith("bin/", StringComparison.Ordinal)
           || relativePath.Contains("/obj/")
           || relativePath.Contains("/bin/");

    /// <summary>与 <c>File.ReadAllLines</c> 同口径：末尾换行不多算一行，行尾 \r 去掉。</summary>
    private static string[] SplitLines(string text)
    {
        var parts = text.Split('\n');
        var count = parts.Length;
        if (count > 0 && parts[count - 1].Length == 0)
        {
            count--;
        }

        var lines = new string[count];
        for (var i = 0; i < count; i++)
        {
            lines[i] = parts[i].EndsWith('\r') ? parts[i][..^1] : parts[i];
        }

        return lines;
    }

    private static string ResolveSourceRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("QQCHAT_SRC_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var candidate = Path.Combine(fromEnv, "src", "BotAgent.Headless");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "BotAgent.Headless");
            if (File.Exists(Path.Combine(candidate, "BotAgent.Headless.csproj")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "找不到 src/BotAgent.Headless —— 从测试产物目录往上找不到仓库根，可用 QQCHAT_SRC_ROOT 指定。");
    }
}

/// <summary>
/// 极简 C# 词法扫描：把注释、字符串、字符字面量变成空白，但**保留换行与字符位置**。
/// 为什么需要它：架构探针要量花括号块的长度、要数「代码里」出现的 SQL 字面量，
/// 不能让注释里的一句话或字符串里的一个 <c>{</c> 把统计带偏。
/// 处理范围：行注释、块注释、普通/逐字/原生（<c>"""</c>）字符串、字符字面量、插值孔（孔里算代码，递归处理）。
/// </summary>
internal static class CsharpText
{
    public static void Strip(string text, out string noComments, out string codeOnly)
    {
        var a = new StringBuilder(text.Length);
        var b = new StringBuilder(text.Length);
        var i = 0;
        Scan(text, ref i, text.Length, a, b, holeDepth: 0);

        // 两个视图必须与原文等长：花括号配对与行号定位都依赖「位置一致」。
        if (a.Length < text.Length)
        {
            a.Append(' ', text.Length - a.Length);
        }

        if (b.Length < text.Length)
        {
            b.Append(' ', text.Length - b.Length);
        }

        noComments = a.ToString();
        codeOnly = b.ToString();
    }

    /// <summary>保留字符到两个视图。</summary>
    private static void Keep(StringBuilder a, StringBuilder b, char c)
    {
        a.Append(c);
        b.Append(c);
    }

    /// <summary>抹掉一个字符，但换行原样保留（不然行号会错位）。</summary>
    private static void Blank(StringBuilder a, StringBuilder b, char c)
    {
        a.Append(c == '\n' ? '\n' : ' ');
        b.Append(c == '\n' ? '\n' : ' ');
    }

    /// <summary>
    /// 字符串/字符字面量的**内容**：去注释视图里原样保留（SQL 字面量要能被找到），
    /// 去字符串视图里当空白（里面的花括号不许参与配对）。
    /// </summary>
    private static void StringChar(StringBuilder a, StringBuilder b, char c)
    {
        a.Append(c);
        b.Append(c == '\n' ? '\n' : ' ');
    }

    private static void Scan(string text, ref int i, int end, StringBuilder a, StringBuilder b, int holeDepth)
    {
        // 插值孔里可能出现对象初始化器/代码块的花括号，得自己配平，不能见到第一个 } 就收工。
        var braceDepth = 0;

        while (i < end)
        {
            var c = text[i];

            if (c == '/' && i + 1 < end && text[i + 1] == '/')
            {
                while (i < end && text[i] != '\n')
                {
                    Blank(a, b, text[i]);
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < end && text[i + 1] == '*')
            {
                while (i < end)
                {
                    if (text[i] == '*' && i + 1 < end && text[i + 1] == '/')
                    {
                        Blank(a, b, '*');
                        Blank(a, b, '/');
                        i += 2;
                        break;
                    }

                    Blank(a, b, text[i]);
                    i++;
                }

                continue;
            }

            if (c == '"')
            {
                ScanLiteral(text, ref i, end, a, b, quoteStart: i, dollars: 0);
                continue;
            }

            if ((c == '$' || c == '@') && TryStringStart(text, i, end, out var quoteStart, out var dollars))
            {
                ScanLiteral(text, ref i, end, a, b, quoteStart, dollars);
                continue;
            }

            if (c == '\'')
            {
                ScanCharLiteral(text, ref i, end, a, b);
                continue;
            }

            if (holeDepth > 0 && c == '{')
            {
                braceDepth++;
                Keep(a, b, c);
                i++;
                continue;
            }

            if (holeDepth > 0 && c == '}')
            {
                Keep(a, b, c);
                i++;
                if (braceDepth == 0)
                {
                    return; // 孔到此结束，把控制权交回字符串扫描
                }

                braceDepth--;
                continue;
            }

            Keep(a, b, c);
            i++;
        }
    }

    /// <summary>识别 <c>$"</c> / <c>@"</c> / <c>$@"</c> / <c>$$"""</c> 这类前缀（必须以 $ 或 @ 开头，后面紧跟引号）。</summary>
    private static bool TryStringStart(string text, int i, int end, out int quoteStart, out int dollars)
    {
        quoteStart = i;
        dollars = 0;

        // 前置字符是标识符的一部分时，@ 是「逐字标识符」（@class），不是字符串前缀。
        if (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_' || text[i - 1] == '.'))
        {
            return false;
        }

        var j = i;
        var ats = 0;
        while (j < end && (text[j] == '$' || text[j] == '@') && dollars + ats < 4)
        {
            if (text[j] == '$')
            {
                dollars++;
            }
            else
            {
                ats++;
            }

            j++;
        }

        if (dollars + ats == 0 || j >= end || text[j] != '"')
        {
            return false;
        }

        quoteStart = j;
        return true;
    }

    private static void ScanLiteral(string text, ref int i, int end, StringBuilder a, StringBuilder b, int quoteStart, int dollars)
    {
        var verbatim = false;
        for (var k = i; k < quoteStart; k++)
        {
            a.Append(text[k]);
            b.Append(' ');
            if (text[k] == '@')
            {
                verbatim = true;
            }
        }

        var run = 0;
        while (quoteStart + run < end && text[quoteStart + run] == '"')
        {
            run++;
        }

        // 逐字字符串（@"…"）的开引号永远只有一个：后面的引号是内容（"" = 一个引号）。
        var opening = verbatim ? 1 : run;
        for (var k = 0; k < opening; k++)
        {
            StringChar(a, b, '"');
        }

        var p = quoteStart + opening;

        // 普通字符串里的 ""（两个引号挨着）就是空串，本身已经开闭成对 —— 不能再往后找闭引号。
        if (!verbatim && run == 2 && dollars == 0)
        {
            i = p;
            return;
        }

        if (!verbatim && run >= 3)
        {
            ScanRawLiteral(text, ref p, end, a, b, run, dollars);
        }
        else
        {
            ScanQuotedLiteral(text, ref p, end, a, b, verbatim, dollars);
        }

        i = p;
    }

    /// <summary>原生字符串 <c>"""…"""</c>：没有转义，靠「不少于起始长度的引号串」收尾，可以跨行。</summary>
    private static void ScanRawLiteral(string text, ref int p, int end, StringBuilder a, StringBuilder b, int run, int dollars)
    {
        while (p < end)
        {
            if (text[p] == '"')
            {
                var r = 0;
                while (p + r < end && text[p + r] == '"')
                {
                    r++;
                }

                for (var k = 0; k < r; k++)
                {
                    StringChar(a, b, '"');
                    p++;
                }

                if (r >= run)
                {
                    return;
                }

                continue;
            }

            if (dollars > 0 && text[p] == '{')
            {
                if (p + 1 < end && text[p + 1] == '{')
                {
                StringChar(a, b, '{');
                StringChar(a, b, '{');
                p += 2;
                continue;
            }

                a.Append('{');
                b.Append('{');
                p++;
                var i = p;
                Scan(text, ref i, end, a, b, holeDepth: 1);
                p = i;
                continue;
            }

            StringChar(a, b, text[p]);
            p++;
        }
    }

    /// <summary>普通 / 逐字 / 插值字符串（单行或多行的逐字字符串）。</summary>
    private static void ScanQuotedLiteral(string text, ref int p, int end, StringBuilder a, StringBuilder b, bool verbatim, int dollars)
    {
        while (p < end)
        {
            var c = text[p];

            if (verbatim)
            {
                if (c == '"')
                {
                    if (p + 1 < end && text[p + 1] == '"')
                    {
                        StringChar(a, b, '"');
                        StringChar(a, b, '"');
                        p += 2;
                        continue;
                    }

                    StringChar(a, b, '"');
                    p++;
                    return;
                }
            }
            else
            {
                if (c == '\\' && p + 1 < end)
                {
                    StringChar(a, b, c);
                    StringChar(a, b, text[p + 1]);
                    p += 2;
                    continue;
                }

                if (c == '"')
                {
                    StringChar(a, b, '"');
                    p++;
                    return;
                }
            }

            if (dollars > 0 && c == '{')
            {
                if (p + 1 < end && text[p + 1] == '{')
                {
                    StringChar(a, b, '{');
                    StringChar(a, b, '{');
                    p += 2;
                    continue;
                }

                a.Append('{');
                b.Append('{');
                p++;
                var i = p;
                Scan(text, ref i, end, a, b, holeDepth: 1);
                p = i;
                continue;
            }

            if (dollars > 0 && c == '}' && p + 1 < end && text[p + 1] == '}')
            {
                StringChar(a, b, '}');
                StringChar(a, b, '}');
                p += 2;
                continue;
            }

            StringChar(a, b, c);
            p++;
        }
    }

    private static void ScanCharLiteral(string text, ref int i, int end, StringBuilder a, StringBuilder b)
    {
        var p = i;
        StringChar(a, b, '\'');
        p++;

        while (p < end)
        {
            if (text[p] == '\\' && p + 1 < end)
            {
                StringChar(a, b, text[p]);
                StringChar(a, b, text[p + 1]);
                p += 2;
                continue;
            }

            if (text[p] == '\'')
            {
                StringChar(a, b, '\'');
                p++;
                break;
            }

            StringChar(a, b, text[p]);
            p++;
            if (p > i + 10)
            {
                break; // 防呆：十来个字符还没收尾，多半不是字符字面量
            }
        }

        i = p;
    }
}
