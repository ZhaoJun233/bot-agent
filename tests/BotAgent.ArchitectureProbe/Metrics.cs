using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BotAgent.ArchitectureProbe;

/// <summary>一个花括号块（方法 / 类型 / 控制流 / 其它），行号都是 1 起。</summary>
internal sealed record CodeBlock(string Name, BlockKind Kind, int StartLine, int EndLine)
{
    public int Lines => EndLine - StartLine + 1;
}

internal enum BlockKind
{
    Member,   // 方法 / 构造函数 / 局部函数 / 属性访问器之外的可调用块
    Type,     // class / struct / interface / record / enum / namespace
    Control,  // if / while / for / foreach / switch / try / using / lock …
    Other,    // 裸块、对象初始化器、switch 表达式等
}

/// <summary>
/// 架构指标：全部来自源码文本，纯确定性、不连库、不起进程。
/// 两条口径约定（改动时同步改 <see cref="Baseline" /> 的注释）：
///   · 计数类指标走 <see cref="SourceFile.NoComments" />（注释不算数，字符串算数）；
///   · 结构类指标走 <see cref="SourceFile.CodeOnly" />（字符串里的花括号不算数）。
/// </summary>
internal static class Metrics
{
    // ─────────────────────────── 字面量与调用点 ───────────────────────────

    /// <summary>SQL 字面量：带引号的语句起始（含 DDL）。</summary>
    private static readonly Regex Sql = new(
        "\"\\s*(?:SELECT\\s+[\\w*]|INSERT\\s+INTO\\s+\\w|UPDATE\\s+\\w+\\s+SET|DELETE\\s+FROM\\s+\\w"
        + "|CREATE\\s+(?:TABLE|INDEX|UNIQUE)|ALTER\\s+TABLE\\s+\\w|PRAGMA\\s+\\w)",
        RegexOptions.Compiled);

    /// <summary>直接文件 IO：File.… 与 FileStream 构造。</summary>
    private static readonly Regex FileIo = new(
        "\\bFile\\.(?:WriteAllText|WriteAllBytes|WriteAllLines|ReadAllText|ReadAllBytes|ReadAllLines|ReadLines"
        + "|AppendAllText|AppendAllLines|Delete|Copy|Move|OpenRead|OpenWrite|Create|CreateText|Exists|Open)\\s*\\("
        + "|\\bnew\\s+FileStream\\s*\\(",
        RegexOptions.Compiled);

    /// <summary>读系统时间：三个变体都算（它们都让时间不可注入）。</summary>
    private static readonly Regex ClockRead = new(
        "\\bDateTimeOffset\\.(?:Now|UtcNow)\\b|\\bDateTime\\.(?:Now|UtcNow)\\b",
        RegexOptions.Compiled);

    /// <summary>HttpClient 的两种造法都要算：<c>new HttpClient { … }</c>（带初始化器）与 <c>new HttpClient(…)</c>。</summary>
    private static readonly Regex HttpClientNew = new(
        "\\bnew\\s+HttpClient\\b|\\bHttpClient\\s+\\w+\\s*=\\s*new\\s*\\(",
        RegexOptions.Compiled);

    private static readonly Regex PanelAgentCall = new("\\b_agent\\.", RegexOptions.Compiled);

    /// <summary>用例层不该自己 new 的具体 IO 组件（architecture-optimization.md §3.4 R4）。</summary>
    internal static readonly string[] ConcreteIoTypes =
    {
        "MusicService", "VoiceService", "WebSearchService", "LinkPreviewer", "StickerStore",
        "MemberRoleStore", "ServerAgentRunner", "OpenAiClient", "ConversationStore",
        "MemberProfileStore", "MusicStore", "NeteaseMusicClient", "MusicAudioResolver",
        "MoodStore", "LegacyJsonImporter", "OneBotGateway", "OfficialBotGateway",
    };

    /// <summary>两种写法都算：<c>new MusicService(…)</c> 与目标类型 new（<c>private readonly StickerStore _stickers = new();</c>）。</summary>
    private static readonly Regex ConcreteIoNew = new(
        "\\bnew\\s+(?:" + string.Join("|", ConcreteIoTypes) + ")\\s*\\("
        + "|\\b(?:" + string.Join("|", ConcreteIoTypes) + ")\\s+_?\\w+\\s*=\\s*new\\s*\\(",
        RegexOptions.Compiled);

    /// <summary>花括号配平（CodeOnly 视图）：一个字面量都不该漏掉，漏了就说明扫描器把字符串切错了。</summary>
    public static (int Open, int Close) BraceBalance(SourceFile f)
    {
        var open = 0;
        var close = 0;
        foreach (var c in f.CodeOnly)
        {
            if (c == '{')
            {
                open++;
            }
            else if (c == '}')
            {
                close++;
            }
        }

        return (open, close);
    }

    public static int CountSqlLiterals(SourceFile f) => Sql.Matches(f.NoComments).Count;

    public static int CountDirectFileIo(SourceFile f) => FileIo.Matches(f.NoComments).Count;

    public static int CountClockReads(SourceFile f) => ClockRead.Matches(f.NoComments).Count;

    public static int CountHttpClientNew(SourceFile f) => HttpClientNew.Matches(f.NoComments).Count;

    public static int CountPanelAgentCalls(SourceFile f) => PanelAgentCall.Matches(f.NoComments).Count;

    public static int CountConcreteIoNew(SourceFile f) => ConcreteIoNew.Matches(f.NoComments).Count;

    /// <summary>
    /// 直接给设置对象赋值（<c>_settings.X = …</c>）。
    /// 设置热更新必须走统一入口（改完做一次原子发布），散落的就地赋值会让"设置对象处于半更新状态"
    /// —— 正在路上的一轮会读到一半新一半旧（review-findings #4）。目标 0。
    /// </summary>
    private static readonly Regex SettingsAssignment = new("_settings\\.\\w+\\s*=[^=]", RegexOptions.Compiled);

    public static int CountSettingsAssignments(SourceFile f) => SettingsAssignment.Matches(f.NoComments).Count;

    /// <summary>
    /// 把 <see cref="SourceIndex" /> 之外的类把配置实例存成**字段**（= 缓存了一份配置，热更新后就看不到新版）。
    /// 只允许发布点自己这么做（见 Baseline.SettingsOwnerPath）。
    /// </summary>
    private static readonly Regex SettingsField = new(
        "\\b(?:private|internal|protected|public)\\s+(?:(?:readonly|static|volatile)\\s+)*AppSettings\\s+\\w+\\s*;",
        RegexOptions.Compiled);

    public static int CountSettingsFields(SourceFile f) => SettingsField.Matches(f.NoComments).Count;

    public static bool ContainsWord(string text, string word)
    {
        var at = 0;
        while ((at = text.IndexOf(word, at, StringComparison.Ordinal)) >= 0)
        {
            var before = at == 0 ? ' ' : text[at - 1];
            var after = at + word.Length >= text.Length ? ' ' : text[at + word.Length];
            if (!IsWordChar(before) && !IsWordChar(after))
            {
                return true;
            }

            at += word.Length;
        }

        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // ─────────────────────────── 结构（花括号块） ───────────────────────────

    private static readonly string[] Modifiers =
    {
        "public", "private", "protected", "internal", "static", "sealed", "override", "virtual",
        "async", "partial", "new", "unsafe", "extern", "abstract", "implicit", "explicit",
        "readonly", "ref", "required",
    };

    private static readonly string[] ControlKeywords =
    {
        "if", "else", "while", "for", "foreach", "switch", "catch", "try", "finally", "using",
        "lock", "fixed", "do", "checked", "unchecked",
    };

    private static readonly string[] TypeKeywords =
    {
        "class", "struct", "interface", "record", "enum", "namespace",
    };

    /// <summary>
    /// 单趟扫描出所有花括号块并标出父子关系（栈配对，不回溯）。
    /// 「块头」= 从上一个 <c>;</c> / <c>{</c> / <c>}</c> / 空行到 <c>{</c> 之间的文本（压成一行）。
    /// </summary>
    public static IReadOnlyList<CodeBlock> Blocks(SourceFile f)
    {
        var code = f.CodeOnly;
        var lineStarts = LineStarts(code);
        var blocks = new List<CodeBlock>();
        var stack = new Stack<(int BlockIndex, int HeadStart)>();
        var headStart = 0;

        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '\n')
            {
                if (IsBlank(code, headStart, i))
                {
                    headStart = i + 1;
                }

                continue;
            }

            if (c == ';')
            {
                headStart = i + 1;
                continue;
            }

            if (c == '}')
            {
                if (stack.Count > 0)
                {
                    var (blockIndex, _) = stack.Pop();
                    blocks[blockIndex] = blocks[blockIndex] with { EndLine = LineOf(lineStarts, i) };
                }

                headStart = i + 1;
                continue;
            }

            if (c != '{')
            {
                continue;
            }

            var head = Normalize(code, headStart, i);
            var (kind, name) = ClassifyHead(head);
            var index = blocks.Count;
            blocks.Add(new CodeBlock(name, kind, LineOf(lineStarts, i), LineOf(lineStarts, i)));
            stack.Push((index, headStart));
            headStart = i + 1;
        }

        return blocks;
    }

    /// <summary>块头分类：先剥修饰符，再看是不是控制流 / 类型 / 带形参表的成员。</summary>
    private static (BlockKind Kind, string Name) ClassifyHead(string head)
    {
        var rest = head;
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var mod in Modifiers)
            {
                if (rest.StartsWith(mod + " ", StringComparison.Ordinal))
                {
                    rest = rest[(mod.Length + 1)..];
                    changed = true;
                    break;
                }
            }
        }

        if (rest.Length == 0)
        {
            return (BlockKind.Other, head.Length > 60 ? head[..60] : head);
        }

        var firstWord = rest.Split(' ', 2)[0];
        if (ControlKeywords.Contains(firstWord))
        {
            return (BlockKind.Control, firstWord);
        }

        foreach (var keyword in TypeKeywords)
        {
            var at = rest.IndexOf(keyword + " ", StringComparison.Ordinal);
            if (at >= 0)
            {
                var name = rest[(at + keyword.Length + 1)..].Split(' ', ':', '(')[0];
                return (BlockKind.Type, name);
            }
        }

        // 成员：块头以 ")" 结尾才算（形参表），且不能是赋值/表达式体/lambda。
        // 注意形参表里允许有默认值（`bool proactive = false`），所以赋值只在括号外层才算。
        if (head.EndsWith(')') && !head.Contains("=>") && !HasTopLevelAssignment(head))
        {
            var open = head.LastIndexOf('(');
            var beforeParen = open < 0 ? head : head[..open];
            var name = beforeParen.Split(' ', '\t').LastOrDefault(s => s.Length > 0) ?? "(anonymous)";
            return (BlockKind.Member, name);
        }

        return (BlockKind.Other, head.Length > 60 ? head[..60] : head);
    }

    /// <summary>块头里有没有「括号外层的赋值」（有 = 说明是字段初始化/表达式，不是方法声明）。</summary>
    private static bool HasTopLevelAssignment(string head)
    {
        var depth = 0;
        for (var i = 0; i < head.Length; i++)
        {
            var c = head[i];
            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                continue;
            }

            if (c != '=' || depth > 0)
            {
                continue;
            }

            var prev = i > 0 ? head[i - 1] : ' ';
            var next = i + 1 < head.Length ? head[i + 1] : ' ';
            if (prev != '=' && prev != '!' && prev != '<' && prev != '>' && next != '=' && next != '>')
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string text, int start, int end)
    {
        var sb = new System.Text.StringBuilder(end - start);
        var lastSpace = false;
        for (var i = start; i < end; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastSpace = true;
                }

                continue;
            }

            sb.Append(c);
            lastSpace = false;
        }

        return sb.ToString().TrimEnd();
    }

    private static bool IsBlank(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts.ToArray();
    }

    private static int LineOf(int[] lineStarts, int index)
    {
        var lo = 0;
        var hi = lineStarts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (lineStarts[mid] <= index)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo + 1;
    }

    /// <summary>最长的成员块（方法），没找到就返回 null。</summary>
    public static CodeBlock? LongestMemberBlock(SourceFile f)
        => Blocks(f).Where(b => b.Kind == BlockKind.Member).OrderByDescending(b => b.Lines).ThenBy(b => b.Name, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>
    /// 字段声明：<c>[修饰符] 类型 名字 [= 初始化]</c>（属性带 <c>=&gt;</c> 或 <c>{</c>，天然不匹配）。
    /// 注意：这里传进来的是「语句」——分号已经被扫描器吃掉，所以模式里不带 <c>;</c>。
    /// </summary>
    private static readonly Regex FieldDeclaration = new(
        "^(?:(?:public|private|protected|internal|static|readonly|volatile|const|new|required)\\s+)+"
        // ⚠ 类型里允许括号：以前漏了 `ConcurrentDictionary<string, (long A, DateTimeOffset B)>` 这种**元组**，
        //    于是"带元组类型的状态"在字段计数里凭空消失（2026-09-23 撞到：删了两个字段、总数反而涨了）。
        + "[A-Za-z_][\\w\\.<>\\[\\],\\?\\s\\(\\)]*?\\s+[A-Za-z_]\\w*\\s*(?:=[^;]*)?$",
        RegexOptions.Compiled);

    private static readonly string[] StatementKeywords =
    {
        "return", "throw", "using", "await", "yield", "break", "continue", "goto", "if", "foreach", "for", "while",
    };

    private static CodeBlock? FindType(SourceFile f, string typeName)
    {
        var blocks = Blocks(f);
        return blocks
            .Where(b => b.Kind == BlockKind.Type && b.Name == typeName)
            .OrderByDescending(b => b.EndLine - b.StartLine)
            .FirstOrDefault();
    }

    /// <summary>
    /// 这个类型在**这个文件**里是不是用 <c>partial</c> 声明的 —— 拆到多个文件的类型要把各部分合起来数
    /// （见 Program.cs 的 R7 判定），别让"拆文件"顺手把字段 / 方法数拆小了。
    /// 注意 <see cref="CodeBlock.StartLine" /> 是 <c>{</c> 所在行，所以要往上找声明行（空行为界）。
    /// </summary>
    public static bool IsPartialType(SourceFile f, string typeName)
    {
        var owner = FindType(f, typeName);
        if (owner is null)
        {
            return false;
        }

        for (var i = Math.Min(owner.StartLine - 1, f.Lines.Length - 1); i >= 0; i--)
        {
            var line = f.Lines[i];
            if (line.Trim().Length == 0)
            {
                return false;   // 空行 = 已经扫过声明区（表头与类型之间的那道空行）
            }

            if (line.Contains("partial", StringComparison.Ordinal) &&
                line.Contains(typeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>类型的直接字段数（按「类型体内花括号深度 = 1 的语句」数）。</summary>
    /// <summary>
    /// 一个类型自己的「字段 / 方法」数：按类型体内**括号深度 = 1 的语句**数，
    /// 块体方法（<c>{ … }</c>）与表达式体方法（<c>=> …;</c>）都算方法；属性（<c>=&gt;</c> 但没形参）不算。
    /// </summary>
    public static (int Fields, int Methods) TypeSurface(SourceFile f, string typeName)
    {
        var owner = FindType(f, typeName);
        if (owner is null)
        {
            return (0, 0);
        }

        var depth = 0;
        var fields = 0;
        var methods = 0;
        var statement = new System.Text.StringBuilder();
        for (var lineNo = owner.StartLine; lineNo <= owner.EndLine && lineNo <= f.CodeOnlyLines.Length; lineNo++)
        {
            var line = f.CodeOnlyLines[lineNo - 1];
            foreach (var c in line)
            {
                if (c == '{')
                {
                    depth++;
                    if (depth == 2)
                    {
                        if (IsMethodHead(statement.ToString()))
                        {
                            methods++;
                        }

                        statement.Clear();
                    }

                    continue;
                }

                if (c == '}')
                {
                    depth--;
                    statement.Clear();
                    continue;
                }

                if (c == ';')
                {
                    if (depth == 1)
                    {
                        var text = statement.ToString().Trim();
                        if (IsExpressionBodiedMethod(text))
                        {
                            methods++;
                        }
                        else if (IsFieldStatement(text))
                        {
                            fields++;
                        }

                        statement.Clear();
                    }

                    continue;
                }

                if (depth == 1)
                {
                    statement.Append(c);
                }
            }
        }

        return (fields, methods);
    }

    /// <summary>
    /// 诊断用：把 <see cref="TypeSurface" /> 认成「字段」的那些语句原样列出来。
    /// 计数对不上时（例如明明删了字段、总数却涨了）用它对着看，别靠猜。
    /// </summary>
    public static List<(int Line, string Text)> FieldStatements(SourceFile f, string typeName)
    {
        var owner = FindType(f, typeName);
        var found = new List<(int, string)>();
        if (owner is null)
        {
            return found;
        }

        var depth = 0;
        var statement = new System.Text.StringBuilder();
        for (var lineNo = owner.StartLine; lineNo <= owner.EndLine && lineNo <= f.CodeOnlyLines.Length; lineNo++)
        {
            var line = f.CodeOnlyLines[lineNo - 1];
            foreach (var c in line)
            {
                if (c == '{')
                {
                    depth++;
                    if (depth == 2)
                    {
                        statement.Clear();
                    }

                    continue;
                }

                if (c == '}')
                {
                    depth--;
                    statement.Clear();
                    continue;
                }

                if (c == ';')
                {
                    if (depth == 1)
                    {
                        var text = statement.ToString().Trim();
                        if (!IsExpressionBodiedMethod(text) && IsFieldStatement(text))
                        {
                            found.Add((lineNo, text));
                        }

                        statement.Clear();
                    }

                    continue;
                }

                if (depth == 1)
                {
                    statement.Append(c);
                }
            }
        }

        return found;
    }

    private static bool IsMethodHead(string statement)
    {
        var text = statement.Trim();
        if (text.Length == 0 || text.Contains("=>") || !text.EndsWith(')'))
        {
            return false;
        }

        if (StatementKeywords.Any(k => text.StartsWith(k + " ", StringComparison.Ordinal)))
        {
            return false;
        }

        return !HasTopLevelAssignment(text);
    }

    private static bool IsExpressionBodiedMethod(string statement)
    {
        var text = statement.Trim();
        if (text.Length == 0 || !text.Contains("=>") || StatementKeywords.Any(k => text.StartsWith(k + " ", StringComparison.Ordinal)))
        {
            return false;
        }

        // 形参表要在 `=>` 之前：`public int Foo => 1;`（属性）不算方法
        var arrow = text.IndexOf("=>", StringComparison.Ordinal);
        var before = text[..arrow];
        var open = before.IndexOf('(');
        if (open < 0 || before.IndexOf(')', open) < 0)
        {
            return false;
        }

        return !HasTopLevelAssignment(before[..open].TrimEnd());
    }

    private static bool IsFieldStatement(string text)
    {
        if (text.Length == 0 || StatementKeywords.Any(k => text.StartsWith(k + " ", StringComparison.Ordinal)))
        {
            return false;
        }

        // 「字段」只数**实例**字段：`static readonly` 的查表（标点表、表情表…）不是「类持有的状态」，
        // 把它们算进去只会让表驱动的小类（如 QqPlainText）看起来像上帝类。
        if (!FieldDeclaration.IsMatch(text))
        {
            return false;
        }

        return !StaticField.IsMatch(text);
    }

    /// <summary>静态字段（<c>static</c> / <c>const</c> 修饰的声明）。</summary>
    private static readonly Regex StaticField = new(
        "^(?:(?:public|private|protected|internal|readonly|volatile|new|required)\\s+)*(?:static|const)(?:\\s|$)",
        RegexOptions.Compiled);
}
