using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAgent.ArchitectureProbe;

/// <summary>
/// 架构护栏探针（architecture-optimization.md §3.4 / §8.2）。
/// 三类断言：棘轮（只许更严）、目标布局（Domain/Adapters 硬规则）、红线锚点（不许在重构里消失）。
/// 口径：纯读源码，不连库、不起进程、不读数据目录、不发消息。加 <c>--print</c> 只打印实测值。
/// </summary>
public static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Tightenable = new();

    public static int Main(string[] args)
    {
        var index = SourceIndex.Load();
        Console.WriteLine($"源码根：{index.Root}");
        Console.WriteLine($"源文件：{index.Files.Count} 个 / {index.TotalLines} 行");

        if (args.Contains("--print"))
        {
            Console.WriteLine();
            Print(index);
            return 0;
        }

        if (args.Length >= 2 && args[0] == "--why")
        {
            Why(index, args[1]);
            return 0;
        }

        if (args.Length >= 2 && args[0] == "--fields")
        {
            var file = index.ByPath(Baseline.BotAgentHostPath);
            if (file is null)
            {
                Console.WriteLine("找不到 " + Baseline.BotAgentHostPath);
                return 1;
            }

            var rows = Metrics.FieldStatements(file, args[1]);
            Console.WriteLine($"{args[1]} 认成字段的语句：{rows.Count} 条");
            foreach (var (line, text) in rows)
            {
                Console.WriteLine($"  L{line,5}  {text}");
            }

            return 0;
        }

        // 诊断：列出全 src 最长的成员块（默认只列 > 200 行的那种，--longest 可看前 N 个）。
        // 为什么要有它：DoD 是"全 src 最长方法 ≤ 200"，只报一个最长不够 —— 拆完一个还有一个，
        // 需要一眼看到"还剩几个要拆"（拆的时候按这张表从上往下走）。
        if (args.Length >= 1 && args[0] == "--longest")
        {
            var top = args.Length >= 2 && int.TryParse(args[1], out var n) ? n : 15;
            var list = index.Files
                .SelectMany(f => Metrics.Blocks(f)
                    .Where(b => b.Kind == BlockKind.Member)
                    .Select(b => (File: f, Block: b)))
                .OrderByDescending(x => x.Block.Lines)
                .ThenBy(x => x.File.RelativePath, StringComparer.Ordinal)
                .Take(top)
                .ToList();

            Console.WriteLine($"── 全 src 最长的 {list.Count} 个成员块（DoD ≤ 200）──");
            foreach (var (file, block) in list)
            {
                var flag = block.Lines > 200 ? "✗ 超 200" : "✅";
                Console.WriteLine($"  {block.Lines,5} 行  {block.Name,-42}  {file.RelativePath}:{block.StartLine}  {flag}");
            }

            var over = index.Files
                .SelectMany(f => Metrics.Blocks(f).Where(b => b.Kind == BlockKind.Member && b.Lines > 200))
                .Count();
            Console.WriteLine($"超过 200 行的成员块共 {over} 个");
            return 0;
        }

        SelfTests(index);
        RatchetChecks(index);
        LayoutChecks(index);
        RedLineChecks(index);

        Console.WriteLine();
        if (Tightenable.Count > 0)
        {
            Console.WriteLine("── 可下调棘轮（不影响本次结果；下调后请改 Baseline.cs）──");
            foreach (var item in Tightenable)
            {
                Console.WriteLine("  ↓ " + item);
            }

            Console.WriteLine();
        }

        Console.WriteLine($"通过 {_passed}，失败 {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    // ─────────────────────────── 自检：扫描器本身不能骗人 ───────────────────────────

    private static void SelfTests(SourceIndex index)
    {
        Section("自检（扫描器口径）");

        var lengthMismatch = index.Files
            .Where(f => f.NoComments.Length != f.Raw.Length || f.CodeOnly.Length != f.Raw.Length)
            .Select(f => f.RelativePath)
            .ToList();
        Check("去注释/去字符串视图与原文等长（位置对齐）", lengthMismatch.Count == 0,
            lengthMismatch.Count == 0 ? null : string.Join("、", lengthMismatch));

        var longest = index.Files
            .Select(f => (File: f, Block: Metrics.LongestMemberBlock(f)))
            .Where(x => x.Block is not null)
            .OrderByDescending(x => x.Block!.Lines)
            .First();
        Check("花括号配对能认出长方法（最长成员块 > 50 行）", longest.Block!.Lines > 50,
            $"最长 = {longest.Block.Name} @ {longest.File.RelativePath} {longest.Block.Lines} 行");

        var botAgent = index.ByPath("Services/BotAgentHost.cs");
        Check("能定位到 Services/BotAgentHost.cs", botAgent is not null,
            botAgent is null ? "文件不存在（是不是挪走了？挪走请同步 Baseline）" : null);

        var unbalanced = index.Files
            .Select(f => (f.RelativePath, Balance: Metrics.BraceBalance(f)))
            .Where(x => x.Balance.Open != x.Balance.Close)
            .Select(x => $"{x.RelativePath}(开{x.Balance.Open}/闭{x.Balance.Close})")
            .ToList();
        Check("每个文件的花括号在去字符串视图里配平", unbalanced.Count == 0,
            unbalanced.Count == 0 ? null : string.Join("、", unbalanced));
    }

    // ─────────────────────────── 棘轮：只许更严 ───────────────────────────

    private static void RatchetChecks(SourceIndex index)
    {
        Section("棘轮 · 规模与集中度（只允许往下调）");

        var biggest = index.Files.OrderByDescending(f => f.LineCount).ThenBy(f => f.RelativePath, StringComparer.Ordinal).First();
        Ratchet("最大源文件行数", biggest.LineCount, Baseline.MaxFileLines, biggest.RelativePath);

        var botAgent = index.ByPath(Baseline.BotAgentHostPath);
        if (botAgent is null)
        {
            Check($"能定位 {Baseline.BotAgentHostPath}", false, "找不到（挪走了请同步 Baseline.BotAgentHostPath）");
        }
        else
        {
            Ratchet($"{Baseline.BotAgentHostPath} 行数", botAgent.LineCount, Baseline.BotAgentHostLines, botAgent.RelativePath);

            var (fields, methods) = Metrics.TypeSurface(botAgent, "BotAgentHost");
            Ratchet("BotAgentHost 字段数", fields, Baseline.BotAgentHostFields, "BotAgentHost");
            Ratchet("BotAgentHost 方法数", methods, Baseline.BotAgentHostMethods, "BotAgentHost");

            var longest = Metrics.LongestMemberBlock(botAgent);
            if (longest is not null)
            {
                Ratchet($"最长方法（{longest.Name}）", longest.Lines, Baseline.LongestMethodLines, $"{botAgent.RelativePath} L{longest.StartLine}");
            }
            else
            {
                Check("最长方法", false, "BotAgentHost 里没找到成员块");
            }
        }

        Section("棘轮 · 易变细节（SQL / 文件 IO / 时间 / 出网）");

        // R2：SQL 只允许出现在 Adapters/Persistence/**
        var sqlFiles = index.PerFile("", Metrics.CountSqlLiterals);
        var sqlOutside = sqlFiles
            .Where(x => !x.Path.StartsWith(Baseline.SqlAllowedPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Check($"R2：SQL 只出现在 {Baseline.SqlAllowedPrefix}**", sqlOutside.Count == 0,
            sqlOutside.Count == 0 ? $"（{sqlFiles.Count} 个文件）" : string.Join("、", sqlOutside.Select(x => $"{x.Path}×{x.Count}")));
        Ratchet("SQL 字面量总数", sqlFiles.Sum(x => x.Count), Baseline.SqlLiteralTotal, "全 src");

        // R3：直接文件 IO 只允许出现在 Adapters/** 与具名例外里
        var ioFiles = index.PerFile("", Metrics.CountDirectFileIo);
        var ioOutside = ioFiles
            .Where(x => !x.Path.StartsWith(Baseline.FileIoAllowedPrefix, StringComparison.OrdinalIgnoreCase)
                        && !Baseline.FileIoAllowedFiles.ContainsKey(x.Path))
            .ToList();
        Check($"R3：直接文件 IO 只出现在 {Baseline.FileIoAllowedPrefix}** 与具名例外里", ioOutside.Count == 0,
            ioOutside.Count == 0
                ? $"（{ioFiles.Count} 个文件，其中例外 {Baseline.FileIoAllowedFiles.Count} 个）"
                : string.Join("、", ioOutside.Select(x => $"{x.Path}×{x.Count}")));
        Ratchet("直接文件 IO 总处数", ioFiles.Sum(x => x.Count), Baseline.FileIoTotal, "全 src");

        // 读系统时间：只允许 IClock 的唯一实现那个文件（别处一律走 Clock / 注入的 IClock）
        var clockFiles = index.PerFile("", Metrics.CountClockReads);
        var clockOutside = clockFiles
            .Where(x => !x.Path.Equals(Baseline.ClockAllowedPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Check($"读系统时间只出现在 {Baseline.ClockAllowedPath}", clockOutside.Count == 0,
            clockOutside.Count == 0
                ? "（其余全部走 Clock / IClock）"
                : string.Join("、", clockOutside.Select(x => $"{x.Path}×{x.Count}")));
        Ratchet("读系统时间（DateTime/DateTimeOffset .Now/.UtcNow）总处数",
            index.Files.Sum(Metrics.CountClockReads), Baseline.ClockReads, "全 src");
        // 出网：socket 只允许在实现文件 + 具名例外里造（其余一律走 IHttpFetcher）
        var httpFiles = index.PerFile("", Metrics.CountHttpClientNew);
        var httpOutside = httpFiles
            .Where(x => !Baseline.HttpClientAllowedFiles.Contains(x.Path, StringComparer.OrdinalIgnoreCase))
            .ToList();
        Check($"new HttpClient 只出现在实现 + {Baseline.HttpClientAllowedFiles.Count - 1} 个具名例外里", httpOutside.Count == 0,
            httpOutside.Count == 0
                ? "（其余全部走 IHttpFetcher）"
                : string.Join("、", httpOutside.Select(x => $"{x.Path}×{x.Count}")));
        Ratchet("new HttpClient 处数",
            index.Files.Sum(Metrics.CountHttpClientNew), Baseline.HttpClientNews, "全 src");

        // §8.3 DoD：**全 src** 最长方法 ≤ 200 行（只钉 BotAgentHost 会漏掉搬进用例层的大方法）
        var longestAny = index.Files
            .Select(f => (File: f, Block: Metrics.LongestMemberBlock(f)))
            .Where(x => x.Block is not null)
            .OrderByDescending(x => x.Block!.Lines)
            .First();
        Ratchet($"全 src 最长方法（{longestAny.Block!.Name}）", longestAny.Block.Lines,
            Baseline.LongestMethodAnywhere, $"{longestAny.File.RelativePath} L{longestAny.Block.StartLine}");

        Section("棘轮 · 边界（面板 / 用例层）");

        var panel = index.ByPath("Adapters/Panel/WebUiServer.cs");
        if (panel is not null)
        {
            Ratchet("面板直接调 _agent. 次数", Metrics.CountPanelAgentCalls(panel), Baseline.PanelAgentCalls, panel.RelativePath);
        }

        // R5：面板（除具名例外外）不许出现 SQL / 直接文件 IO / 直读系统时间 ——
        // 面板是"编排 + 映射"，数据存取与时间都得从别人那里问。
        var panelFiles = index.In("Adapters/Panel").ToList();
        var panelCore = panelFiles
            .Where(f => !Baseline.PanelExemptFiles.Contains(f.RelativePath, StringComparer.OrdinalIgnoreCase))
            .ToList();
        Check($"R5：面板（除 {Baseline.PanelExemptFiles.Count} 个例外外）里没有 SQL 字面量",
            panelCore.All(f => Metrics.CountSqlLiterals(f) == 0),
            string.Join("、", panelCore.Where(f => Metrics.CountSqlLiterals(f) > 0).Select(f => $"{f.RelativePath}×{Metrics.CountSqlLiterals(f)}")));
        Check("R5：面板（除例外外）里没有直接文件 IO",
            panelCore.All(f => Metrics.CountDirectFileIo(f) == 0),
            string.Join("、", panelCore.Where(f => Metrics.CountDirectFileIo(f) > 0).Select(f => $"{f.RelativePath}×{Metrics.CountDirectFileIo(f)}")));
        Check("R5：面板（除例外外）里没有直读系统时间",
            panelCore.All(f => Metrics.CountClockReads(f) == 0),
            string.Join("、", panelCore.Where(f => Metrics.CountClockReads(f) > 0).Select(f => $"{f.RelativePath}×{Metrics.CountClockReads(f)}")));
        // 例外那几个文件自己也要有数：否则"把 IO 挪进例外文件"就成了一条绕过 R5 的暗路。
        Ratchet("R5 例外文件（面板部署）自己的文件 IO 处数",
            panelFiles.Where(f => Baseline.PanelExemptFiles.Contains(f.RelativePath, StringComparer.OrdinalIgnoreCase))
                .Sum(Metrics.CountDirectFileIo),
            Baseline.PanelExemptFileIo, string.Join("、", Baseline.PanelExemptFiles));

        var useCaseNews = index.PerFile("Services", Metrics.CountConcreteIoNew);
        Ratchet("用例层 new 具体 IO 组件处数", useCaseNews.Sum(x => x.Count), Baseline.ConcreteIoNews,
            useCaseNews.Count == 0 ? "（已归零）" : string.Join("、", useCaseNews.Select(x => $"{x.Path}×{x.Count}")));

        // R9：依赖方向（§3.2 的 `db → service`）—— 硬规则：用例层不许认识适配层。
        var servicesFiles = index.In("Services").ToList();
        var crossLayerFiles = servicesFiles
            .Where(f => f.NoComments.Contains("BotAgent.Adapters", StringComparison.Ordinal))
            .ToList();
        Check($"R9：Services/ 里没有对 BotAgent.Adapters 的引用（共 {servicesFiles.Count} 个文件）",
            crossLayerFiles.Count == 0,
            crossLayerFiles.Count == 0 ? "用例层只吃端口" : string.Join("、", crossLayerFiles.Select(f => f.RelativePath)));

        var settingsWrites = index.PerFile("", Metrics.CountSettingsAssignments);
        Ratchet("就地改设置对象处数（目标 0：热更新要走统一入口）", settingsWrites.Sum(x => x.Count),
            Baseline.SettingsAssignments,
            settingsWrites.Count == 0 ? "（已归零）" : string.Join("、", settingsWrites.Select(x => $"{x.Path}×{x.Count}")));

        var settingsFields = index.PerFile("", Metrics.CountSettingsFields)
            .Where(x => !x.Path.Equals(Baseline.SettingsOwnerPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Check($"配置实例只由 {Baseline.SettingsOwnerPath} 持有（别处不许缓存一份）", settingsFields.Count == 0,
            settingsFields.Count == 0
                ? "（其余组件都持 SettingsBox）"
                : string.Join("、", settingsFields.Select(x => $"{x.Path}×{x.Count}")));
    }

    // ─────────────────────────── 目标布局（R1 / R6 / R7） ───────────────────────────

    private static void LayoutChecks(SourceIndex index)
    {
        Section("目标布局 · Domain 必须零 IO（目录还不存在时按 0 违规通过）");

        var domain = index.In("Domain").ToList();
        Check($"Domain/ 文件数（当前 {domain.Count}）", true, null);

        Check("Domain/ 里没有 SQL 字面量", domain.All(f => Metrics.CountSqlLiterals(f) == 0),
            string.Join("、", domain.Where(f => Metrics.CountSqlLiterals(f) > 0).Select(f => f.RelativePath)));
        Check("Domain/ 里没有直接文件 IO", domain.All(f => Metrics.CountDirectFileIo(f) == 0),
            string.Join("、", domain.Where(f => Metrics.CountDirectFileIo(f) > 0).Select(f => f.RelativePath)));
        Check("Domain/ 里没有读系统时间", domain.All(f => Metrics.CountClockReads(f) == 0),
            string.Join("、", domain.Where(f => Metrics.CountClockReads(f) > 0).Select(f => f.RelativePath)));
        // 时间从外面给（显式 now 参数）：Domain 里连时钟入口都不许出现 ——
        // 否则"纯规则可测"这条会慢慢退化成"读全局时钟"，假时钟测试就白做了。
        Check("Domain/ 里不出现时钟入口（Clock.）",
            domain.All(f => !Metrics.ContainsWord(f.NoComments, "Clock")),
            string.Join("、", domain.Where(f => Metrics.ContainsWord(f.NoComments, "Clock")).Select(f => f.RelativePath)));
        Check("Domain/ 里没有 HttpClient / Sqlite / 出网", domain.All(f => !Metrics.ContainsWord(f.NoComments, "HttpClient")
                && !Metrics.ContainsWord(f.NoComments, "SqliteConnection")
                && !f.NoComments.Contains("Microsoft.Data.Sqlite", StringComparison.Ordinal)
                && !f.NoComments.Contains("System.Net.Http", StringComparison.Ordinal)),
            string.Join("、", domain.Where(f => f.NoComments.Contains("HttpClient", StringComparison.Ordinal)
                || f.NoComments.Contains("Sqlite", StringComparison.Ordinal)).Select(f => f.RelativePath)));

        var longestDomain = domain.Select(f => f.LineCount).DefaultIfEmpty(0).Max();
        var overTarget = domain
            .Where(f => f.LineCount > Baseline.DomainFileTargetLines)
            .Select(f => $"{f.RelativePath}({f.LineCount})")
            .ToList();
        Check($"R6：Domain/ 每个文件 ≤ {Baseline.DomainFileTargetLines} 行", overTarget.Count == 0,
            overTarget.Count == 0 ? $"最长 {longestDomain} 行" : $"超标：{string.Join("、", overTarget)}");

        var fatTypes = new List<string>();
        var partialParts = new Dictionary<string, List<(string File, int Fields, int Methods)>>(StringComparer.Ordinal);
        foreach (var file in domain)
        {
            foreach (var type in Metrics.Blocks(file).Where(b => b.Kind == BlockKind.Type))
            {
                var (fields, methods) = Metrics.TypeSurface(file, type.Name);
                if (fields > Baseline.DomainTypeMaxFields || methods > Baseline.DomainTypeMaxMethods)
                {
                    fatTypes.Add($"{file.RelativePath}:{type.Name}(字段{fields}/方法{methods})");
                }

                // 一个类型被拆成多个 partial 文件时，单看一个文件会漏掉另一部分的字段 / 方法 ——
                // 这里把同名 partial 的**各部分加起来**再判，免得"拆文件"变成绕过 R7 的口子。
                if (Metrics.IsPartialType(file, type.Name))
                {
                    if (!partialParts.TryGetValue(type.Name, out var parts))
                    {
                        parts = new List<(string File, int Fields, int Methods)>();
                        partialParts[type.Name] = parts;
                    }

                    parts.Add((file.RelativePath, fields, methods));
                }
            }
        }

        var merged = partialParts.Where(x => x.Value.Count > 1).ToList();
        foreach (var (name, parts) in merged)
        {
            var fields = parts.Sum(x => x.Fields);
            var methods = parts.Sum(x => x.Methods);
            if (fields > Baseline.DomainTypeMaxFields || methods > Baseline.DomainTypeMaxMethods)
            {
                fatTypes.Add($"partial {name} 合计(字段{fields}/方法{methods})："
                    + string.Join(" + ", parts.Select(x => $"{x.File}({x.Fields}/{x.Methods})")));
            }
        }

        Check($"Domain/ 单类型字段 ≤ {Baseline.DomainTypeMaxFields}、方法 ≤ {Baseline.DomainTypeMaxMethods}"
              + $"（{merged.Count} 个 partial 类型已合并计数）",
            fatTypes.Count == 0, string.Join("、", fatTypes));
    }

    // ─────────────────────────── 红线锚点（重构不许弄坏的东西） ───────────────────────────

    private static void RedLineChecks(SourceIndex index)
    {
        Section("红线锚点 · 脱敏 / 串行 / 提示词");

        Anchor(index, "MaybeMask", "显示层脱敏入口（群名/昵称/QQ 号）");
        Anchor(index, "ChatLabel", "会话显示名（脱敏后的）");
        Anchor(index, "nameRaw", "面板改名要写真名（显示层脱敏、key 原样）");
        Anchor(index, "_pendingReplies", "每会话一条 FIFO（同一会话严格按序）");
        Anchor(index, "_replyGate", "全局并发闸门");
        Anchor(index, "_inFlight", "在途回复计数（可观测）");

        // 提示词注入点：这些段落一旦在重构里丢掉，模型行为会立刻变样（harness 里的字数软线是哨兵）。
        foreach (var anchor in new[]
                 {
                     "[现在的时间]", "[机器人人设档案]", "[回复谁]", "[你此刻的心情]", "[本群身份]",
                     "[会话参与者档案", "[可选的结构化动作]", "[先读懂气氛再说话]", "[这次是你自己想说话]",
                 })
        {
            Anchor(index, anchor, "提示词注入点");
        }

        Anchor(index, "AllowCapability", "能力闸门（发不发/要不要批准）");
        Anchor(index, "ResolveQuotedMessage", "引用目标裁决");
        Anchor(index, "_settings.Snapshot()", "回复链入口取配置快照（review-findings #4 的修复，不许退回）");
        Anchor(index, "SettingsBox", "配置的唯一发布点（热更新换引用，不就地改）");
        Anchor(index, "_box.Apply(", "热更新 = 副本上改 + 原子发布");
        Anchor(index, "PatchSettings", "窄口热更新（已在别处持久化的运行时值，如登录态）");
    }

    private static void Anchor(SourceIndex index, string anchor, string why)
    {
        var hits = index.Files.Count(f => f.NoComments.Contains(anchor, StringComparison.Ordinal));
        Check($"锚点仍在：{anchor}（{why}）", hits > 0, hits > 0 ? $"命中 {hits} 个文件" : "一个都没有");
    }

    // ─────────────────────────── 工具 ───────────────────────────

    private static void Ratchet(string name, int current, int baseline, string subject)
    {
        var ok = current <= baseline;
        Check($"{name} ≤ {baseline}", ok, $"实测 {current}（{subject}）");
        if (ok && current < baseline)
        {
            Tightenable.Add($"{name}：{baseline} → {current}（{subject}）");
        }
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("── " + title + " ──");
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("  ✅ " + name + (detail is null ? string.Empty : "  → " + detail));
        }
        else
        {
            _failed++;
            Console.WriteLine("  ✗ " + name + (detail is null ? string.Empty : "  → " + detail));
        }
    }

    // ─────────────────────────── --print ───────────────────────────

    /// <summary>
    /// 诊断用（改过扫描器之后自查）：列出「原文里有花括号、去字符串视图里却没有」的位置。
    /// 正常情况这些位置都落在注释里；要是落在代码上，就说明扫描器把某个字面量认错了。
    /// </summary>
    private static void Why(SourceIndex index, string relativePath)
    {
        var file = index.ByPath(relativePath) ?? throw new ArgumentException($"没有这个文件：{relativePath}");
        Console.WriteLine($"文件：{file.RelativePath}（{file.LineCount} 行）");

        var lost = new List<int>();
        for (var i = 0; i < file.Raw.Length; i++)
        {
            var raw = file.Raw[i];
            if ((raw == '{' || raw == '}') && file.CodeOnly[i] == ' ')
            {
                lost.Add(i);
            }
        }

        Console.WriteLine($"原文有花括号、骨架里没有：{lost.Count} 处（落在注释里属正常）");
        foreach (var at in lost.Take(20))
        {
            var lineNo = file.Raw[..at].Count(c => c == '\n') + 1;
            Console.WriteLine($"  L{lineNo}：{file.Lines[lineNo - 1].Trim()}");
        }

        if (lost.Count > 20)
        {
            Console.WriteLine($"  …还有 {lost.Count - 20} 处（只列前 20）");
        }
    }

    private static void Print(SourceIndex index)
    {
        Console.WriteLine("── 实测值（可直接抄进 Baseline.cs）──");
        var biggest = index.Files.OrderByDescending(f => f.LineCount).ThenBy(f => f.RelativePath, StringComparer.Ordinal).First();
        Console.WriteLine($"最大源文件行数        = {biggest.LineCount}   （{biggest.RelativePath}）");

        var botAgent = index.ByPath(Baseline.BotAgentHostPath);
        if (botAgent is not null)
        {
            var longest = Metrics.LongestMemberBlock(botAgent);
            var (fields, methods) = Metrics.TypeSurface(botAgent, "BotAgentHost");
            Console.WriteLine($"BotAgentHost 行数         = {botAgent.LineCount}");
            Console.WriteLine($"BotAgentHost 字段数       = {fields}");
            Console.WriteLine($"BotAgentHost 方法数       = {methods}");
            Console.WriteLine($"最长方法              = {longest?.Lines} 行（{longest?.Name} @ L{longest?.StartLine}）");
            Console.WriteLine("   最长的 6 个成员：");
            foreach (var block in Metrics.Blocks(botAgent).Where(x => x.Kind == BlockKind.Member)
                         .OrderByDescending(x => x.Lines).ThenBy(x => x.Name, StringComparer.Ordinal).Take(6))
            {
                Console.WriteLine($"     · {block.Lines,5} 行  L{block.StartLine,5}  {block.Name}");
            }
        }

        // 层方向（§3.2）：这行只是**打印实测值**，真正的判定在上面 R9 那条硬规则里
        // （2026-09-23 收口：Services 侧必须为 0；收口前是 16，见 architecture-optimization.md §13.9）。
        var crossLayer = index.In("Services")
            .Where(f => f.NoComments.Contains("BotAgent.Adapters", StringComparison.Ordinal))
            .ToList();
        Console.WriteLine($"Services -> Adapters 引用的文件数（诊断，未断言；目标见 §3.2） = {crossLayer.Count} / {index.In("Services").Count()}");

        Console.WriteLine($"读系统时间总处数      = {index.Files.Sum(Metrics.CountClockReads)}");
        Console.WriteLine($"new HttpClient 处数   = {index.Files.Sum(Metrics.CountHttpClientNew)}");

        var panel = index.ByPath("Adapters/Panel/WebUiServer.cs");
        if (panel is not null)
        {
            Console.WriteLine($"面板 _agent. 次数     = {Metrics.CountPanelAgentCalls(panel)}");
        }

        var useCaseNews = index.PerFile("Services", Metrics.CountConcreteIoNew);
        Console.WriteLine($"用例层 new 具体 IO    = {useCaseNews.Sum(x => x.Count)}");
        foreach (var (path, count) in useCaseNews)
        {
            Console.WriteLine($"                        · {path} × {count}");
        }

        Console.WriteLine();
        Console.WriteLine("SQL 字面量：");
        foreach (var (path, count) in index.PerFile("", Metrics.CountSqlLiterals))
        {
            Console.WriteLine($"  [\"{path}\"] = {count},");
        }

        Console.WriteLine();
        Console.WriteLine("直接文件 IO：");
        foreach (var (path, count) in index.PerFile("", Metrics.CountDirectFileIo))
        {
            Console.WriteLine($"  [\"{path}\"] = {count},");
        }

        Console.WriteLine();
        Console.WriteLine("Domain/（目标：单文件 ≤ 300 行、单类型实例字段 ≤ 10 且方法 ≤ 25）:");
        var domain = index.In("Domain").ToList();
        foreach (var file in domain)
        {
            Console.WriteLine($"  {file.RelativePath}  {file.LineCount} 行");
            foreach (var type in Metrics.Blocks(file).Where(b => b.Kind == BlockKind.Type))
            {
                var (fields, methods) = Metrics.TypeSurface(file, type.Name);
                Console.WriteLine($"      · {type.Name}  实例字段={fields} 方法={methods}");
            }
        }

        Console.WriteLine($"  Domain 最大文件行数 = {domain.Select(x => x.LineCount).DefaultIfEmpty(0).Max()}");

        // 拆到多个文件的 partial 类型：R7 按**合计**判，打印也要按合计给，不然数对不上（见 §13.8）。
        var partialGroups = domain
            .SelectMany(f => Metrics.Blocks(f)
                .Where(b => b.Kind == BlockKind.Type && Metrics.IsPartialType(f, b.Name))
                .Select(b => (File: f, Type: b.Name)))
            .GroupBy(x => x.Type, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        var surfaces = new List<(string Label, int Fields, int Methods)>();
        foreach (var group in partialGroups)
        {
            var parts = group
                .Select(x => (Name: System.IO.Path.GetFileName(x.File.RelativePath), Surface: Metrics.TypeSurface(x.File, x.Type)))
                .ToList();
            surfaces.Add(("partial " + group.Key, parts.Sum(x => x.Surface.Fields), parts.Sum(x => x.Surface.Methods)));
            Console.WriteLine($"  partial {group.Key}（{parts.Count} 个文件）：实例字段={parts.Sum(x => x.Surface.Fields)} 方法={parts.Sum(x => x.Surface.Methods)}"
                + $"  ← {string.Join(" + ", parts.Select(x => $"{x.Name}({x.Surface.Fields}/{x.Surface.Methods})"))}");
        }

        var mergedNames = partialGroups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var file in domain)
        {
            foreach (var type in Metrics.Blocks(file).Where(b => b.Kind == BlockKind.Type && !mergedNames.Contains(b.Name)))
            {
                var (fields, methods) = Metrics.TypeSurface(file, type.Name);
                surfaces.Add(($"{file.RelativePath}:{type.Name}", fields, methods));
            }
        }

        Console.WriteLine($"  Domain 最大实例字段（partial 合并后）= {surfaces.Select(x => x.Fields).DefaultIfEmpty(0).Max()}");
        Console.WriteLine($"  Domain 最大方法数（partial 合并后）  = {surfaces.Select(x => x.Methods).DefaultIfEmpty(0).Max()}"
            + $"（{surfaces.OrderByDescending(x => x.Methods).ThenBy(x => x.Label, StringComparer.Ordinal).FirstOrDefault().Label}）");
    }
}
