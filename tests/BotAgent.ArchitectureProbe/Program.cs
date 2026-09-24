using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

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
        ToolDirectoryChecks(index);
        AuditAndTraceChecks(index);
        PanelApprovalChecks(index);
        TurnLoopChecks(index);
        LocalChannelChecks(index);
        ServerGateChecks(index);
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

    // ─────────────────── 统一工具目录（通用 Agent 平台 · 批次 A） ───────────────────

    private static void ToolDirectoryChecks(SourceIndex index)
    {
        Section("统一工具目录 · 声明在 Domain、执行者只在 Services/Tools（批次 A）");

        var executorFiles = index.Files
            .Where(f => f.NoComments.Contains("IToolExecutor", StringComparison.Ordinal))
            .Select(f => f.RelativePath)
            .ToList();
        // 口径（2026-09-24 批次 A 收尾后）：接口与登记在 Services/Tools/**，
        // **真实现**在各族的执行体文件里（Services/Agent/**）—— 两条都允许，但 **Domain/** 里一个词都不许有。
        var stray = executorFiles
            .Where(p => !p.StartsWith("Services/Tools/", StringComparison.Ordinal)
                        && !p.StartsWith("Services/Agent/", StringComparison.Ordinal))
            .ToList();
        Check("IToolExecutor 相关代码只出现在 Services/Tools/** 或 Services/Agent/**（Domain 里不许出现）",
            executorFiles.Count > 0 && stray.Count == 0,
            executorFiles.Count == 0 ? "一处都没解析到（执行者登记是不是被删了？）" : string.Join("、", stray));

        // “真实现”必须是**类型声明**上真接了这个接口（不是靠注释声称）
        var realImplements = new List<string>();
        foreach (var (file, type) in new[]
                 {
                     ("Services/Agent/ServerAgentRunner.cs", "ServerAgentRunner"),
                     ("Services/Agent/QqActionTool.cs", "SessionQqActionHost"),
                 })
        {
            var text = index.ByPath(file)?.NoComments ?? string.Empty;
            if (Regex.IsMatch(text, @"class\s+" + type + @"\b[^\n]*IToolExecutor"))
            {
                realImplements.Add(type);
            }
        }

        Check("★ 两家真实现（// 的两族）在类型声明上真接了 IToolExecutor",
            realImplements.Count == 2, string.Join("、", realImplements));

        var specFile = index.ByPath("Domain/Tools/ToolSpec.cs");
        Check("工具声明落在 Domain/Tools/ToolSpec.cs",
            specFile is not null, specFile is null ? "找不到（挪走了请同步这条与文档）" : "在 Domain/Tools/ToolSpec.cs");

        // 红线锚点：这三样是“一份目录、两路复用”的支点，重构里不许消失。
        Anchor(index, "ToolDirectory", "统一工具目录（一份目录：聊天 / QQ 动作 / 服务器工具）");
        Anchor(index, "BuiltinToolExecutors", "执行者登记（每个 ToolSpec 都要有执行者）");
        Anchor(index, "/api/tools", "面板的工具目录只读页（批次 A5）");

        // // 那路的工具名单：批次 D 起**来自统一目录**（ParseTools 不再内联数组）——
        // 少一个 = “能执行却没登记”，多一个 = “登记了却没人执行”，两种都在这一条上暴露。
        var declared = NamesIn(index, "Services/Tools/ServerToolSpecs.cs", "new\\(\"([a-z]+)\",\\s*ToolCategory\\.");
        var runner = index.ByPath("Services/Agent/ServerAgentRunner.cs");
        var usesDirectory = runner is not null
            && runner.NoComments.Contains("ServerToolSpecs.Names", StringComparison.Ordinal)
            && runner.NoComments.Contains("ServerToolSpecs.All", StringComparison.Ordinal);
        Check($"// 的工具名单来自统一目录（目录 {declared.Count} 个；不再内联数组）",
            declared.Count == 6 && declared.Distinct(StringComparer.Ordinal).Count() == 6 && usesDirectory,
            runner is null ? "找不到 Services/Agent/ServerAgentRunner.cs" : $"读目录 = {usesDirectory}");

        // 批次 D：给模型看的那份清单也由目录生成（名字 + 一句话 + 参数 + 是否要批准）。
        Anchor(index, "ToolPromptText", "工具清单渲染（按策略裁剪后进提示词）");
        Anchor(index, "[可用工具]", "提示词里的工具清单段（批次 D）");

        // QQ 动作那条：目录（QqActionCatalog）是唯一真源 —— 投影必须由它推出来，不许手抄一份。
        var qqProjection = index.ByPath("Services/Tools/QqToolSpecs.cs");
        var isProjected = qqProjection is not null
            && qqProjection.NoComments.Contains("QqActionCatalog.All", StringComparison.Ordinal);
        Check("QQ 动作目录由 QqActionCatalog 投影而来（不是手抄的第二个清单）",
            isProjected,
            qqProjection is null ? "找不到 Services/Tools/QqToolSpecs.cs"
            : isProjected ? "投影自 QqActionCatalog.All" : "没看到 QqActionCatalog.All");
    }

    /// <summary>
    /// 从源码文本里抠出一组名字（“目录 vs 执行器”的集合比对用）。
    /// splitQuoted = true 时把捕获到的整段再按双引号里的短标识符拆开（等价于字符串数组）。
    /// </summary>
    private static List<string> NamesIn(SourceIndex index, string path, string pattern, bool splitQuoted = false)
    {
        var file = index.ByPath(path);
        var found = new List<string>();
        if (file is null)
        {
            return found;
        }

        foreach (Match match in Regex.Matches(file.NoComments, pattern))
        {
            var text = match.Groups[1].Value;
            if (!splitQuoted)
            {
                found.Add(text);
                continue;
            }

            foreach (Match quoted in Regex.Matches(text, "\\\"([a-z_]+)\\\""))
            {
                found.Add(quoted.Groups[1].Value);
            }
        }

        return found;
    }

    // ─────────────────────────── 红线锚点（重构不许弄坏的东西） ───────────────────────────

    // ─────────────────── 面板审批（通用 Agent 平台 · 批次 I） ───────────────────

    private static void PanelApprovalChecks(SourceIndex index)
    {
        Section("面板审批（批次 I）");

        Anchor(index, "/api/approvals/decide", "面板审批写路径（高权限：前置 fail-closed）");
        Anchor(index, "PanelDecide", "面板决策复用同一份校验（ApprovalFlow.Handle）");
        Anchor(index, "panel_token_required", "未配面板令牌 → 面板审批不可用（fail-closed）");

        // 前置校验必须长在**写路径自己**身上：只在 UI 拦 = 等于没拦（接口还能被直接打）。
        var approvals = index.ByPath("Adapters/Panel/WebUiServer.Approvals.cs");
        Check("写路径自己带两道前置（审批开关 + 面板令牌）",
            approvals is not null
            && approvals.NoComments.Contains("approvals_disabled", StringComparison.Ordinal)
            && approvals.NoComments.Contains("panel_token_required", StringComparison.Ordinal),
            approvals is null ? "找不到 Adapters/Panel/WebUiServer.Approvals.cs" : "两处都要在");
    }

    // ─────────── 有限步进循环（通用 Agent 平台 · 批次 E） ───────────

    private static void TurnLoopChecks(SourceIndex index)
    {
        Section("有限步进循环（批次 E）");

        Anchor(index, "AgentTurnLoop", "有限步进循环骨架（模型调用 + 上限 + 不空转）");
        Anchor(index, "InlineTurnTools", "当场做掉只读工具");
        Anchor(index, "MaxAgentSteps", "步数上限设置（默认 1 = 与改造前逐字一致）");
        Anchor(index, "[循环] 当场搜索", "当场那条路的现场日志");

        // 默认值必须写在 AppSettings 里（§9.2：不许散在代码里）；上限必须在循环里被钳住。
        var settings = index.ByPath("Services/AppSettings.cs");
        Check("★ MaxAgentSteps 的默认值写在 AppSettings 且是 1",
            settings is not null
            && Regex.IsMatch(settings.NoComments, @"MaxAgentSteps\s*\{\s*get;\s*set;\s*\}\s*=\s*1\s*;"),
            "默认值写在 AppSettings（= 1）");

        var loop = index.ByPath("Services/Reply/AgentTurnLoop.cs");
        Check("★ 步数在循环里被钳到 1..3",
            loop is not null
            && loop.NoComments.Contains("Math.Clamp(maxSteps, MinSteps, MaxSteps)", StringComparison.Ordinal)
            && loop.NoComments.Contains("public const int MaxSteps = 3;", StringComparison.Ordinal),
            "钳位与上限常量都在 AgentTurnLoop 里");
    }

    // ─────────────────── `//` 那路过闸门（批次 A 收尾） ───────────────────

    private static void ServerGateChecks(SourceIndex index)
    {
        Section("`//` 过闸门（批次 A 收尾）");

        Anchor(index, "ServerToolGate", "`//` 那路的闸门接线（登记表 + 策略快照）");
        Anchor(index, "HighRiskExceptions", "高风险例外**显式点名**（不靠“没人发现”）");
        Anchor(index, "AgentServerUseGate", "开关（默认关 = 与今天逐字一致）");

        // 例外只能按**工具名**点名：断言里不许出现“按类别放开”的写法（那就等于把 I3 拆了）。
        var policy = index.ByPath("Domain/Permissions/ToolPolicy.cs");
        var isNameSet = policy is not null
            && Regex.IsMatch(policy.NoComments, @"IReadOnlySet<string>\?\s+HighRiskExceptions");
        Check("★ 例外字段是**工具名集合**（不是类别集合）—— I3 不被这类改动削弱",
            isNameSet,
            policy is null ? "找不到 ToolPolicy.cs" : isNameSet ? "IReadOnlySet<string> HighRiskExceptions" : "字段类型不对");

        // 闸门必须**在类别禁令处**才认这条例外（审批分支不许认）
        var gate = index.ByPath("Domain/Permissions/ToolGate.cs");
        var wiredAtCategoryBan = gate is not null
            && gate.NoComments.Contains("policy.HighRiskExceptions?.Contains(descriptor.Id)", StringComparison.Ordinal)
            && gate.NoComments.Contains("approval_cannot_grant", StringComparison.Ordinal);
        Check("★ 例外只在“类别禁令”那一处生效（审批分支照旧不认高风险）",
            wiredAtCategoryBan,
            gate is null ? "找不到 ToolGate.cs" : wiredAtCategoryBan ? "只在类别禁令处认例外" : "例外接线位置不对");
    }

    // ─────────────────── 第三条通道（通用 Agent 平台 · 批次 F） ───────────────────

    private static void LocalChannelChecks(SourceIndex index)
    {
        Section("第三条通道（批次 F）");

        Anchor(index, "LocalChannelSource", "本地通道（IQqChatSource 的第三个实现）");
        Anchor(index, "/api/local/message", "本地通道入口（名单非空 + 面板令牌）9，两道前置）");
        Anchor(index, "local_channel_disabled", "名单空 → 整条通道不建（fail-closed）");
        Anchor(index, "LocalBase", "本地号段（路由器按数字路由，三段互不相撞）");
        Anchor(index, "Channels.Declared", "通道归一（上行自报 → 内部通道）—— 全仓只该有一处");

        // “不是官方就是私域”这句话以前写在两处，第三条通道一上来就被贴成私域（S48 真实踩过）。
        // 这条断言钉死：那两处必须走同一个归一函数。
        var hardcoded = index.Files
            .Where(f => f.NoComments.Contains("IsOfficial(", StringComparison.Ordinal)
                        && f.NoComments.Contains("Channels.Private", StringComparison.Ordinal)
                        && Regex.IsMatch(f.NoComments, @"IsOfficial\([^)]*\)\s*\?\s*[^:
]*Official\s*:\s*[^;
]*Private"))
            .Select(f => f.RelativePath)
            .ToList();
        Check("★ 没有地方再自己写“不是官方就是私域”（归一只走 Channels.Declared）",
            hardcoded.Count == 0, string.Join("、", hardcoded));
    }

    // ─────────────────── 回复审计与决策轨迹（通用 Agent 平台 · 批次 C） ───────────────────

    private static void AuditAndTraceChecks(SourceIndex index)
    {
        Section("回复审计与决策轨迹（批次 C）");

        Anchor(index, "ReplyAuditRules", "回复审计规则（凭据 / 本机路径 → 整条不发）");
        Anchor(index, "TurnTraceStore", "决策轨迹台账（一轮一条，只有形状）");
        Anchor(index, "/api/traces", "面板的轨迹只读端点（批次 H 的追踪页只读它）");

        // 审计必须真的接在发送缝上：规则写好但没人调 = 假绿（这个仓库踩过）。
        var sender = index.ByPath("Services/Reply/PlainSender.cs");
        var auditWired = sender is not null
            && sender.NoComments.Contains("ReplyAuditRules.Judge", StringComparison.Ordinal);
        Check("回复审计接在发送层（PlainSender 里真的调了 ReplyAuditRules.Judge）",
            auditWired,
            sender is null ? "找不到 Services/Reply/PlainSender.cs" : auditWired ? "发送前后各一处调用" : "没看到调用点");

        // 轨迹记录**不许**出现承载正文的字段（§9.2 的“审计不写正文”；SafetyProbe 还有一条反射断言）。
        var trace = index.ByPath("Domain/Ops/TurnTrace.cs");
        var contentFields = trace is null
            ? new List<string> { "找不到 Domain/Ops/TurnTrace.cs" }
            : Regex.Matches(trace.NoComments, @"\bstring\??\s+\w*(Text|Content|Body|Message)\w*\b")
                .Select(m => m.Value).ToList();
        Check("轨迹的记录类型里没有承载正文的字段（只允许 id / 枚举 / 状态码 / 时长 / 计数）",
            contentFields.Count == 0, string.Join("、", contentFields));
    }

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
