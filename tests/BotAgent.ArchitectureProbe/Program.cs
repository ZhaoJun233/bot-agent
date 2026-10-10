using System;
using System.Collections.Generic;
using System.IO;
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
        _passed = 0;
        _failed = 0;
        Tightenable.Clear();
        try { return Run(args); }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine("架构护栏拒绝继续：" + ex.Message);
            return 1;
        }
        catch (IOException)
        {
            Console.WriteLine("架构护栏拒绝继续：source_read_failed");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var index = SourceIndex.Load();
        Console.WriteLine($"源码根：{index.Root}");
        Console.WriteLine($"源文件：{index.Files.Count} 个 / {index.TotalLines} 行");

        if (args.SequenceEqual(new[] { "--self-test" }))
        {
            ScannerContractTests(index);
            Console.WriteLine($"扫描器契约自检：通过 {_passed}，失败 {_failed}（不是仓库架构验收）");
            return _failed == 0 ? 0 : 1;
        }

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

        ScannerContractTests(index);
        ProjectChecks(index);
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

    private static readonly string[] FixtureProjects =
        { "BotAgent.Core", "BotAgent.Storage", "BotAgent.Platforms", "BotAgent.Model", "BotAgent.Headless" };

    private static void ScannerContractTests(SourceIndex index)
    {
        Section("R3 扫描器契约自检（独立合成源码，不运行机器人）");
        Check("真实迁出领域能按仓库路径定位", index.ByPath("src/BotAgent.Core/Domain/Tools/ToolSpec.cs") is not null);
        var root = Path.Combine(Path.GetTempPath(), "botagent-r3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var complete = CreateFixture(root, "complete");
            Directory.CreateDirectory(Path.Combine(complete, "src/BotAgent.Core/obj"));
            File.WriteAllText(Path.Combine(complete, "src/BotAgent.Core/obj/Ignored.cs"), "class Ignored { }");
            Directory.CreateDirectory(Path.Combine(complete, "src/BotAgent.Core/bin"));
            File.WriteAllText(Path.Combine(complete, "src/BotAgent.Core/bin/Ignored.cs"), "class Ignored { }");
            var snapshot = LoadFixture(complete);
            Check("五个业务工程全部扫描且排除 bin/obj", snapshot.Files.Count == 5);
            Check("同名源文件保留工程身份，不做模糊回退",
                snapshot.ByPath("src/BotAgent.Core/Domain/Rules.cs") is not null
                && snapshot.ByPath("src/BotAgent.Model/Domain/Rules.cs") is not null
                && snapshot.ByPath("Domain/Rules.cs") is null);

            var aliasRoot = CreateFixture(root, "storage-alias");
            File.WriteAllText(Path.Combine(aliasRoot, "src/BotAgent.Headless/BotAgent.Headless.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../BotAgent.Storage/BotAgent.Storage.csproj\" Aliases=\"StorageModule\" /></ItemGroup></Project>");
            Check("唯一 Host Storage 别名仍扫描真实引用与全部源码", LoadFixture(aliasRoot).Files.Count == 5);
            foreach (var alias in new[] { "global", "global,StorageModule", "StorageModule,Other", "Other", "storageModule" })
                FixtureMustFail(root, "alias-" + alias.Replace(',', '-'), p => File.WriteAllText(
                    Path.Combine(p, "src/BotAgent.Headless/BotAgent.Headless.csproj"),
                    $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../BotAgent.Storage/BotAgent.Storage.csproj\" Aliases=\"{alias}\" /></ItemGroup></Project>"),
                    "未知或多个 Storage 别名必须拒绝: " + alias);
            FixtureMustFail(root, "alias-other-edge", p => File.WriteAllText(Path.Combine(p, "src/BotAgent.Model/BotAgent.Model.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../BotAgent.Core/BotAgent.Core.csproj\" Aliases=\"StorageModule\" /></ItemGroup></Project>"),
                "其它引用不能借用 Storage 别名例外");
            FixtureMustFail(root, "alias-extra-metadata", p => File.WriteAllText(Path.Combine(p, "src/BotAgent.Headless/BotAgent.Headless.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../BotAgent.Storage/BotAgent.Storage.csproj\" Aliases=\"StorageModule\" ReferenceOutputAssembly=\"false\" /></ItemGroup></Project>"),
                "正确别名不豁免其它元数据");
            FixtureMustFail(root, "alias-child", p => File.WriteAllText(Path.Combine(p, "src/BotAgent.Headless/BotAgent.Headless.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../BotAgent.Storage/BotAgent.Storage.csproj\"><Aliases>StorageModule</Aliases></ProjectReference></ItemGroup></Project>"),
                "子节点元数据仍拒绝");
            FixtureMustFail(root, "alias-condition", p => File.WriteAllText(Path.Combine(p, "src/BotAgent.Headless/BotAgent.Headless.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup Condition=\"'$(Configuration)' == 'Debug'\"><ProjectReference Include=\"../BotAgent.Storage/BotAgent.Storage.csproj\" Aliases=\"StorageModule\" /></ItemGroup></Project>"),
                "正确别名不豁免条件引用");

            FixtureMustFail(root, "missing", p => File.Delete(Path.Combine(p, "src/BotAgent.Storage/BotAgent.Storage.csproj")),
                "缺必需工程必须失败");
            FixtureMustFail(root, "empty", p => File.Delete(Path.Combine(p, "src/BotAgent.Core/Domain/Rules.cs")),
                "空模块 / 空领域扫描必须失败");
            FixtureMustFail(root, "comments", p => File.WriteAllText(Path.Combine(p, "src/BotAgent.Model/Domain/Rules.cs"), "// no source\n"),
                "只有注释的模块不能冒充非空实现");
            FixtureMustFail(root, "direction", p => WriteFixtureProject(p, "BotAgent.Core", "BotAgent.Storage"),
                "Core 反向引用必须失败");
            FixtureMustFail(root, "sibling", p => WriteFixtureProject(p, "BotAgent.Platforms", "BotAgent.Model"),
                "Platforms 引用 Model 必须失败");
            FixtureMustFail(root, "dangling", p => WriteFixtureProject(p, "BotAgent.Model", "BotAgent.Missing"),
                "悬空项目引用必须失败");
            FixtureMustFail(root, "condition", p => File.WriteAllText(Path.Combine(p, "src/BotAgent.Model/BotAgent.Model.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup Condition=\"'$(Configuration)' == 'Debug'\"><ProjectReference Include=\"../BotAgent.Core/BotAgent.Core.csproj\" /></ItemGroup></Project>"),
                "不能解释的条件引用不得静默漏检");
            FixtureMustFail(root, "cycle", p =>
            {
                WriteFixtureProject(p, "BotAgent.Core", "BotAgent.Model");
                WriteFixtureProject(p, "BotAgent.Model", "BotAgent.Core");
            }, "项目引用环必须失败");
            FixtureMustFail(root, "solution", p => File.WriteAllText(Path.Combine(p, "BotAgent.slnx"),
                "<Solution><Project Path=\"src/BotAgent.Missing/BotAgent.Missing.csproj\" /></Solution>"),
                "解决方案悬空或漏列业务工程必须失败");
            FixtureMustFail(root, "domain-missing", p => File.Move(Path.Combine(p, "src/BotAgent.Core/Domain/Rules.cs"),
                Path.Combine(p, "src/BotAgent.Core/Rules.cs")), "Core 有其它源码也不能掩盖领域扫描为空");
            FixtureMustFail(root, "compile-override", p => File.WriteAllText(Path.Combine(p, "src/BotAgent.Model/BotAgent.Model.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"../external.cs\" /></ItemGroup></Project>"),
                "未解释的显式编译输入不能绕过扫描");
            FixtureMustFail(root, "props", p => File.WriteAllText(Path.Combine(p, "Directory.Build.props"), "<Project />"),
                "未解释的继承构建设置必须失败");
            FixtureMustFail(root, "unknown", p =>
            {
                Directory.CreateDirectory(Path.Combine(p, "src/BotAgent.Unknown"));
                WriteFixtureProject(p, "BotAgent.Unknown", null);
            }, "未登记业务工程不能被悄悄忽略");
            FixtureMustFail(root, "metadata", p => File.WriteAllText(Path.Combine(p, "src/BotAgent.Model/BotAgent.Model.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../BotAgent.Core/BotAgent.Core.csproj\" ReferenceOutputAssembly=\"false\" /></ItemGroup></Project>"),
                "未解释的引用元数据必须失败");

            var ioRoot = CreateFixture(root, "domain-io");
            File.WriteAllText(Path.Combine(ioRoot, "src/BotAgent.Core/RootIo.cs"),
                "namespace Synthetic;\nclass RootIo\n{\n string sql = \"SELECT * FROM synthetic\";\n void Read() { File.ReadAllText(\"synthetic.txt\"); }\n}\n");
            CheckProbeRejects(LoadFixture(ioRoot), LayoutChecks, "Core 根目录新增 IO 也被真实领域断言拦截", "没有直接文件 IO");

            Check("循环检查独立识别引用环", SourceIndex.ReferenceErrors(new[]
            {
                new SourceProject("BotAgent.Core", "src/BotAgent.Core/BotAgent.Core.csproj", new[] { "src/BotAgent.Model/BotAgent.Model.csproj" }, 1),
                new SourceProject("BotAgent.Model", "src/BotAgent.Model/BotAgent.Model.csproj", new[] { "src/BotAgent.Core/BotAgent.Core.csproj" }, 1),
            }).Contains("project_reference_cycle"));

            var missing = Path.Combine(root, "no-repository");
            Directory.CreateDirectory(missing);
            var rejected = false;
            try { LoadFixture(missing); }
            catch (InvalidOperationException) { rejected = true; }
            Check("显式源码根无效时不得退回真实仓库假绿", rejected);
            MetricsContractTests();
            var storage = SourceFile.FromText("src/BotAgent.Storage/EpisodeStore.cs", "public class EpisodeStore { }");
            const string facadeCode = "extern alias StorageModule; public class EpisodeStore : StorageModule::BotAgent.Adapters.Persistence.EpisodeStore { }";
            Check("C2 facade实际引用存在且没有SQL/IO", IsStorageFacade(SourceFile.FromText("fixture", facadeCode), storage, "EpisodeStore"));
            Check("C2不能以缺实现或空字符串伪造接入", !IsStorageFacade(SourceFile.FromText("fixture", facadeCode), null, "EpisodeStore")
                && !IsStorageFacade(SourceFile.FromText("fixture", "class Fake { string x = \"StorageModule::BotAgent.Adapters.Persistence.EpisodeStore\"; }"), storage, "EpisodeStore"));
            Check("C2 facade残留SQL必须拒绝", !IsStorageFacade(SourceFile.FromText("fixture", facadeCode.Replace("{ }", "{ string sql = \"SELECT * FROM synthetic\"; }")), storage, "EpisodeStore"));
            Check("C2 facade残留IO必须拒绝", !IsStorageFacade(SourceFile.FromText("fixture", facadeCode.Replace("{ }", "{ void Save() => File.WriteAllText(\"synthetic\", \"data\"); }")), storage, "EpisodeStore"));
        }
        finally
        {
            // root 由本方法的新 UUID 创建；只清理本次合成源码，不碰仓库或其他临时目录。
            var full = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("botagent-r3-", StringComparison.Ordinal))
                throw new InvalidOperationException("unsafe_fixture_cleanup");
            Directory.Delete(full, recursive: true);
        }
    }

    private static string CreateFixture(string parent, string name)
    {
        var root = Path.Combine(parent, name);
        foreach (var project in FixtureProjects)
        {
            var folder = Path.Combine(root, "src", project, "Domain");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Rules.cs"), "namespace Synthetic;\npublic class Rules\n{\n    public int Value() => 1;\n}\n");
            WriteFixtureProject(root, project, project == "BotAgent.Core" ? null : "BotAgent.Core");
        }
        File.WriteAllText(Path.Combine(root, "BotAgent.slnx"), "<Solution>" + string.Concat(FixtureProjects.Select(p =>
            $"<Project Path=\"src/{p}/{p}.csproj\" />")) + "</Solution>");
        return root;
    }

    private static void WriteFixtureProject(string root, string project, string? reference)
        => File.WriteAllText(Path.Combine(root, "src", project, project + ".csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>"
            + (reference is null ? "" : $"<ItemGroup><ProjectReference Include=\"../{reference}/{reference}.csproj\" /></ItemGroup>") + "</Project>");

    private static SourceIndex LoadFixture(string root)
    {
        var old = Environment.GetEnvironmentVariable("QQCHAT_SRC_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("QQCHAT_SRC_ROOT", root);
            return SourceIndex.Load();
        }
        finally { Environment.SetEnvironmentVariable("QQCHAT_SRC_ROOT", old); }
    }

    private static void FixtureMustFail(string parent, string name, Action<string> mutate, string label)
    {
        var root = CreateFixture(parent, name);
        mutate(root);
        var rejected = false;
        try { LoadFixture(root); }
        catch (InvalidOperationException) { rejected = true; }
        Check(label, rejected);
    }

    private static void CheckProbeRejects(SourceIndex index, Action<SourceIndex> probe, string label, string failedLabel)
    {
        var (passed, failed) = (_passed, _failed);
        var output = Console.Out;
        using var capture = new StringWriter();
        var rejected = false;
        try
        {
            Console.SetOut(capture);
            probe(index);
            rejected = _failed > failed && capture.ToString().Split('\n').Any(line => line.Contains("✗") && line.Contains(failedLabel));
        }
        finally { Console.SetOut(output); (_passed, _failed) = (passed, failed); }
        Check(label, rejected);
    }

    private static void MetricsContractTests()
    {
        var lexical = SourceFile.FromText("src/BotAgent.Core/Domain/Synthetic.cs",
            "class Synthetic { string sql = \"SELECT * FROM synthetic\"; void M() { File.ReadAllText(\"synthetic.txt\"); var now = DateTimeOffset.UtcNow; var client = new HttpClient(); _settings.Value = 1; } }\n"
            + "// \"SELECT * FROM ignored\" File.ReadAllText( DateTimeOffset.Now new HttpClient() _settings.X = 1;\n");
        Check("旧计数口径：SQL / IO / 时钟 / 出网 / 配置赋值，注释不算",
            Metrics.CountSqlLiterals(lexical) == 1 && Metrics.CountDirectFileIo(lexical) == 1
            && Metrics.CountClockReads(lexical) == 1 && Metrics.CountHttpClientNew(lexical) == 1
            && Metrics.CountSettingsAssignments(lexical) == 1);
        var targetNew = SourceFile.FromText("src/BotAgent.Headless/Services/Synthetic.cs",
            "class Synthetic { private readonly ConversationStore _store = new(); HttpClient client = new(); }\n");
        Check("目标类型 new 仍计入具体 IO 与 HttpClient", Metrics.CountConcreteIoNew(targetNew) == 1 && Metrics.CountHttpClientNew(targetNew) == 1);
        var literals = SourceFile.FromText("src/BotAgent.Core/Domain/Literals.cs",
            "class Literals { string a = @\"{ literal }\"; string b = \"\"\"{ raw }\"\"\"; string c = $\"{new object()}\"; char d = '}'; }\n");
        var braces = Metrics.BraceBalance(literals);
        Check("字符串 / 原生 / 插值 / 字符保持视图对齐与花括号配平",
            literals.Raw.Length == literals.NoComments.Length && literals.Raw.Length == literals.CodeOnly.Length && braces.Open == braces.Close);

        SourceFile Part(string project, string ns, string name, int fields)
            => SourceFile.FromText($"src/{project}/Domain/{name}.cs", $"namespace {ns};\npublic partial class Rule\n{{\n"
                + string.Concat(Enumerable.Range(0, fields).Select(i => $" private int _field{i};\n")) + " public void Run() { }\n}\n");
        var a = Part("BotAgent.Core", "Synthetic.A", "First", 6);
        var b = Part("BotAgent.Core", "Synthetic.A", "Second", 5);
        var otherNs = Part("BotAgent.Core", "Synthetic.B", "Other", 1);
        var otherProject = Part("BotAgent.Model", "Synthetic.A", "Other", 1);
        CodeBlock Rule(SourceFile f) => Metrics.Blocks(f).Single(t => t.Kind == BlockKind.Type && t.Name == "Rule");
        Check("partial 同工程同命名空间合并，跨工程 / 命名空间分开",
            Metrics.TypeKey(a, Rule(a)) == Metrics.TypeKey(b, Rule(b))
            && Metrics.TypeKey(a, Rule(a)) != Metrics.TypeKey(otherNs, Rule(otherNs))
            && Metrics.TypeKey(a, Rule(a)) != Metrics.TypeKey(otherProject, Rule(otherProject)));
        var partialIndex = new SourceIndex { Root = "(synthetic)", Files = new[] { a, b } };
        CheckProbeRejects(partialIndex, LayoutChecks, "拆 partial 文件仍被真实字段总量断言拦截", "单类型字段");
        var nested = SourceFile.FromText("src/BotAgent.Core/Domain/Nested.cs",
            "namespace Synthetic.A { class Outer { partial class Rule { private int _first; } } }\n"
            + "namespace Synthetic.B { class Outer { partial class Rule { private int _second; } } }\n");
        var rules = Metrics.Blocks(nested).Where(t => t.Kind == BlockKind.Type && t.Name == "Rule").ToList();
        Check("同文件嵌套同名类型按位置计数，命名空间身份不混淆", rules.Count == 2
            && rules.All(t => Metrics.TypeSurface(nested, t).Fields == 1)
            && Metrics.TypeKey(nested, rules[0]) != Metrics.TypeKey(nested, rules[1]));
        var generic = SourceFile.FromText("src/BotAgent.Core/Domain/Generic.cs",
            "namespace Synthetic; partial class Rule<T> { } partial class Rule<T,U> { }\n");
        var genericRules = Metrics.Blocks(generic).Where(t => t.Kind == BlockKind.Type).ToList();
        Check("泛型元数不同的 partial 不误合并", genericRules.Count == 2
            && Metrics.TypeKey(generic, genericRules[0]) != Metrics.TypeKey(generic, genericRules[1]));
        Check("逐文件持久化迁移映射成立，但新文件和平台自写库不获整目录豁免",
            Baseline.AllowsSql("src/BotAgent.Storage/ConversationStore.cs")
            && Baseline.AllowsFileIo("src/BotAgent.Platforms/Common/OfficialIdMap.cs")
            && !Baseline.AllowsSql("src/BotAgent.Storage/Unregistered.cs")
            && !Baseline.AllowsFileIo("src/BotAgent.Storage/Unregistered.cs")
            && !Baseline.AllowsSql("src/BotAgent.Platforms/Persistence/PlatformDedupStore.cs"));
        Check("棘轮不高于 R3 开始数值", Baseline.MaxFileLines <= 1765 && Baseline.BotAgentHostLines <= 243
            && Baseline.BotAgentHostFields <= 10 && Baseline.BotAgentHostMethods <= 10 && Baseline.LongestMethodLines <= 30
            && Baseline.LongestMethodAnywhere <= 198 && Baseline.SqlLiteralTotal <= 137 && Baseline.FileIoTotal <= 62
            && Baseline.ClockReads <= 3 && Baseline.HttpClientNews <= 4 && Baseline.PanelAgentCalls == 0
            && Baseline.ConcreteIoNews == 0 && Baseline.SettingsAssignments == 0 && Baseline.PanelSqlLiterals == 0
            && Baseline.PanelFileIo == 0 && Baseline.PanelClockReads == 0 && Baseline.PanelExemptFileIo <= 15
            && Baseline.DomainFileTargetLines <= 300 && Baseline.DomainTypeMaxFields <= 10 && Baseline.DomainTypeMaxMethods <= 25);
    }

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

        var botAgent = index.ByPath("src/BotAgent.Headless/Services/BotAgentHost.cs");
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

    private static void ProjectChecks(SourceIndex index)
    {
        Section("实际业务工程与引用声明（不是宿主行为验收）");
        Check("当前必需工程全部存在且有实际源码", Baseline.RequiredProjects.All(name => index.Projects.Any(p => p.Name == name && p.FileCount > 0)));
        Check("源码扫描不为空、Core 领域扫描不为空", index.Files.Count > 0 && index.In("src/BotAgent.Core/Domain").Any());
        var errors = SourceIndex.ReferenceErrors(index.Projects);
        Check("直接项目引用方向合法、无悬空引用及引用环", errors.Count == 0, string.Join("、", errors));
        foreach (var project in index.Projects)
            Console.WriteLine($"  {project.Name}：{project.FileCount} 源文件；直接引用 {string.Join(",", project.References)}");
        var legacy = index.In("src/BotAgent.Headless").ToList();
        Console.WriteLine($"  旧 Headless 范围诊断：SQL={legacy.Sum(Metrics.CountSqlLiterals)} IO={legacy.Sum(Metrics.CountDirectFileIo)} 时钟={legacy.Sum(Metrics.CountClockReads)} HttpClient={legacy.Sum(Metrics.CountHttpClientNew)}（不代替全工程判定）");
        var host = index.Projects.Single(p => p.Name == "BotAgent.Headless");
        foreach (var name in new[] { "BotAgent.Storage", "BotAgent.Model" })
            if (!host.References.Contains($"src/{name}/{name}.csproj", StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"  未接入登记：Headless 未直接引用 {name}（不据此宣告对应波次准出）");
    }

    private static IReadOnlyList<(string Path, int Count)> UseCaseIo(SourceIndex index)
        => index.UseCaseFiles.Select(f => (Path: f.RelativePath, Count: Metrics.CountConcreteIoNew(f)))
            .Where(x => x.Count > 0).OrderByDescending(x => x.Count).ThenBy(x => x.Path, StringComparer.Ordinal).ToList();

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
            .Where(x => !Baseline.AllowsSql(x.Path))
            .ToList();
        Check($"R2：SQL 只出现在 {Baseline.SqlAllowedPrefix}** 或逐文件等价迁移位置", sqlOutside.Count == 0,
            sqlOutside.Count == 0 ? $"（{sqlFiles.Count} 个文件）" : string.Join("、", sqlOutside.Select(x => $"{x.Path}×{x.Count}")));
        Ratchet("SQL 字面量总数", sqlFiles.Sum(x => x.Count), Baseline.SqlLiteralTotal, "全 src");

        // R3：直接文件 IO 只允许出现在 Adapters/** 与具名例外里
        var ioFiles = index.PerFile("", Metrics.CountDirectFileIo);
        var ioOutside = ioFiles
            .Where(x => !Baseline.AllowsFileIo(x.Path))
            .ToList();
        Check($"R3：直接文件 IO 只出现在 {Baseline.FileIoAllowedPrefix}**、具名例外或逐文件等价迁移位置", ioOutside.Count == 0,
            ioOutside.Count == 0
                ? $"（{ioFiles.Count} 个文件，其中例外 {Baseline.FileIoAllowedFiles.Count} 个）"
                : string.Join("、", ioOutside.Select(x => $"{x.Path}×{x.Count}")));
        Ratchet("直接文件 IO 总处数", ioFiles.Sum(x => x.Count), Baseline.FileIoTotal, "全 src");

        foreach (var name in new[] { "AppDatabase", "AgentImageStore", "AgentSessionStore", "AudioCache",
            "ConversationStore", "EpisodeStore", "HostMetrics", "JargonStore", "LegacyJsonImporter",
            "MemberProfileStore", "MemberRoleStore", "MoodStore", "MusicStore", "OwnMessageStore",
            "PanelPasswordStore", "PromptTemplateStore", "SecretFiles", "SecretsStore", "StickerStore",
            "TenantQuotaStore", "TtsConfFile" })
        {
            var facade = index.ByPath($"src/BotAgent.Headless/Adapters/Persistence/{name}.cs");
            var implementation = index.ByPath($"src/BotAgent.Storage/{name}.cs");
            Check($"C2：{name} 保留兼容入口但不再复制 SQL/文件 IO", IsStorageFacade(facade, implementation, name));
        }

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

        var panel = index.ByPath("src/BotAgent.Headless/Adapters/Panel/WebUiServer.cs");
        if (panel is not null)
        {
            Ratchet("面板直接调 _agent. 次数", Metrics.CountPanelAgentCalls(panel), Baseline.PanelAgentCalls, panel.RelativePath);
        }

        // R5：面板（除具名例外外）不许出现 SQL / 直接文件 IO / 直读系统时间 ——
        // 面板是"编排 + 映射"，数据存取与时间都得从别人那里问。
        var panelFiles = index.PanelFiles.ToList();
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

        var useCaseNews = UseCaseIo(index);
        Ratchet("用例层 new 具体 IO 组件处数", useCaseNews.Sum(x => x.Count), Baseline.ConcreteIoNews,
            useCaseNews.Count == 0 ? "（已归零）" : string.Join("、", useCaseNews.Select(x => $"{x.Path}×{x.Count}")));

        // R9：依赖方向（§3.2 的 `db → service`）—— 硬规则：用例层不许认识适配层。
        var servicesFiles = index.UseCaseFiles.ToList();
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

    private static bool IsStorageFacade(SourceFile? facade, SourceFile? implementation, string name)
        => facade is not null && implementation is not null
            && facade.CodeOnly.Contains("StorageModule::BotAgent.Adapters.Persistence." + name, StringComparison.Ordinal)
            && Metrics.CountSqlLiterals(facade) == 0 && Metrics.CountDirectFileIo(facade) == 0;

    private static void LayoutChecks(SourceIndex index)
    {
        Section("目标布局 · Core / 各工程 Domain 必须非空且零 IO");

        var domain = index.DomainFiles.ToList();
        Check($"Core / Domain 文件数（当前 {domain.Count}）", domain.Count > 0, "不允许空扫描假绿");

        Check("src/BotAgent.Core/Domain/ 里没有 SQL 字面量", domain.All(f => Metrics.CountSqlLiterals(f) == 0),
            string.Join("、", domain.Where(f => Metrics.CountSqlLiterals(f) > 0).Select(f => f.RelativePath)));
        Check("src/BotAgent.Core/Domain/ 里没有直接文件 IO", domain.All(f => Metrics.CountDirectFileIo(f) == 0),
            string.Join("、", domain.Where(f => Metrics.CountDirectFileIo(f) > 0).Select(f => f.RelativePath)));
        Check("src/BotAgent.Core/Domain/ 里没有读系统时间", domain.All(f => Metrics.CountClockReads(f) == 0),
            string.Join("、", domain.Where(f => Metrics.CountClockReads(f) > 0).Select(f => f.RelativePath)));
        // 时间从外面给（显式 now 参数）：Domain 里连时钟入口都不许出现 ——
        // 否则"纯规则可测"这条会慢慢退化成"读全局时钟"，假时钟测试就白做了。
        Check("src/BotAgent.Core/Domain/ 里不出现时钟入口（Clock.）",
            domain.All(f => !Metrics.ContainsWord(f.NoComments, "Clock")),
            string.Join("、", domain.Where(f => Metrics.ContainsWord(f.NoComments, "Clock")).Select(f => f.RelativePath)));
        Check("src/BotAgent.Core/Domain/ 里没有 HttpClient / Sqlite / 出网", domain.All(f => !Metrics.ContainsWord(f.NoComments, "HttpClient")
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
                var (fields, methods) = Metrics.TypeSurface(file, type);
                if (fields > Baseline.DomainTypeMaxFields || methods > Baseline.DomainTypeMaxMethods)
                {
                    fatTypes.Add($"{file.RelativePath}:{type.Name}(字段{fields}/方法{methods})");
                }

                // 一个类型被拆成多个 partial 文件时，单看一个文件会漏掉另一部分的字段 / 方法 ——
                // 这里把同名 partial 的**各部分加起来**再判，免得"拆文件"变成绕过 R7 的口子。
                if (Metrics.IsPartialType(file, type))
                {
                    var key = Metrics.TypeKey(file, type);
                    if (!partialParts.TryGetValue(key, out var parts))
                    {
                        parts = new List<(string File, int Fields, int Methods)>();
                        partialParts[key] = parts;
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

        Check($"src/BotAgent.Core/Domain/ 单类型字段 ≤ {Baseline.DomainTypeMaxFields}、方法 ≤ {Baseline.DomainTypeMaxMethods}"
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
            .Where(p => !p.StartsWith("src/BotAgent.Headless/Services/Tools/", StringComparison.Ordinal)
                        && !p.StartsWith("src/BotAgent.Headless/Services/Agent/", StringComparison.Ordinal))
            .ToList();
        Check("IToolExecutor 相关代码只出现在 Services/Tools/** 或 Services/Agent/**（Domain 里不许出现）",
            executorFiles.Count > 0 && stray.Count == 0,
            executorFiles.Count == 0 ? "一处都没解析到（执行者登记是不是被删了？）" : string.Join("、", stray));

        // “真实现”必须是**类型声明**上真接了这个接口（不是靠注释声称）
        var realImplements = new List<string>();
        foreach (var (file, type) in new[]
                 {
                     ("src/BotAgent.Headless/Services/Agent/ServerAgentRunner.cs", "ServerAgentRunner"),
                     ("src/BotAgent.Headless/Services/Agent/QqActionTool.cs", "SessionQqActionHost"),
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

        var specFile = index.ByPath("src/BotAgent.Core/Domain/Tools/ToolSpec.cs");
        Check("工具声明落在 Domain/Tools/ToolSpec.cs",
            specFile is not null, specFile is null ? "找不到（挪走了请同步这条与文档）" : "在 Domain/Tools/ToolSpec.cs");

        // 红线锚点：这三样是“一份目录、两路复用”的支点，重构里不许消失。
        Anchor(index, "ToolDirectory", "统一工具目录（一份目录：聊天 / QQ 动作 / 服务器工具）");
        Anchor(index, "BuiltinToolExecutors", "执行者登记（每个 ToolSpec 都要有执行者）");
        Anchor(index, "/api/tools", "面板的工具目录只读页（批次 A5）");

        // // 那路的工具名单：批次 D 起**来自统一目录**（ParseTools 不再内联数组）——
        // 少一个 = “能执行却没登记”，多一个 = “登记了却没人执行”，两种都在这一条上暴露。
        var declared = NamesIn(index, "src/BotAgent.Headless/Services/Tools/ServerToolSpecs.cs", "new\\(\"([a-z]+)\",\\s*ToolCategory\\.");
        var runner = index.ByPath("src/BotAgent.Headless/Services/Agent/ServerAgentRunner.cs");
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
        var qqProjection = index.ByPath("src/BotAgent.Headless/Services/Tools/QqToolSpecs.cs");
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
        var approvals = index.ByPath("src/BotAgent.Headless/Adapters/Panel/WebUiServer.Approvals.cs");
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
        var settings = index.ByPath("src/BotAgent.Headless/Services/AppSettings.cs");
        Check("★ MaxAgentSteps 的默认值写在 AppSettings 且是 1",
            settings is not null
            && Regex.IsMatch(settings.NoComments, @"MaxAgentSteps\s*\{\s*get;\s*set;\s*\}\s*=\s*1\s*;"),
            "默认值写在 AppSettings（= 1）");

        var loop = index.ByPath("src/BotAgent.Headless/Services/Reply/AgentTurnLoop.cs");
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
        var policy = index.ByPath("src/BotAgent.Core/Domain/Permissions/ToolPolicy.cs");
        var isNameSet = policy is not null
            && Regex.IsMatch(policy.NoComments, @"IReadOnlySet<string>\?\s+HighRiskExceptions");
        Check("★ 例外字段是**工具名集合**（不是类别集合）—— I3 不被这类改动削弱",
            isNameSet,
            policy is null ? "找不到 ToolPolicy.cs" : isNameSet ? "IReadOnlySet<string> HighRiskExceptions" : "字段类型不对");

        // 闸门必须**在类别禁令处**才认这条例外（审批分支不许认）
        var gate = index.ByPath("src/BotAgent.Core/Domain/Permissions/ToolGate.cs");
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
        var sender = index.ByPath("src/BotAgent.Headless/Services/Reply/PlainSender.cs");
        var auditWired = sender is not null
            && sender.NoComments.Contains("ReplyAuditRules.Judge", StringComparison.Ordinal);
        Check("回复审计接在发送层（PlainSender 里真的调了 ReplyAuditRules.Judge）",
            auditWired,
            sender is null ? "找不到 Services/Reply/PlainSender.cs" : auditWired ? "发送前后各一处调用" : "没看到调用点");

        // 轨迹记录**不许**出现承载正文的字段（§9.2 的“审计不写正文”；SafetyProbe 还有一条反射断言）。
        var trace = index.ByPath("src/BotAgent.Core/Domain/Ops/TurnTrace.cs");
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
        var crossLayer = index.UseCaseFiles
            .Where(f => f.NoComments.Contains("BotAgent.Adapters", StringComparison.Ordinal))
            .ToList();
        Console.WriteLine($"Services -> Adapters 引用的文件数（诊断，未断言；目标见 §3.2） = {crossLayer.Count} / {index.UseCaseFiles.Count()}");

        Console.WriteLine($"读系统时间总处数      = {index.Files.Sum(Metrics.CountClockReads)}");
        Console.WriteLine($"new HttpClient 处数   = {index.Files.Sum(Metrics.CountHttpClientNew)}");

        var panel = index.ByPath("src/BotAgent.Headless/Adapters/Panel/WebUiServer.cs");
        if (panel is not null)
        {
            Console.WriteLine($"面板 _agent. 次数     = {Metrics.CountPanelAgentCalls(panel)}");
        }

        var useCaseNews = UseCaseIo(index);
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
        Console.WriteLine("src/BotAgent.Core/Domain/（目标：单文件 ≤ 300 行、单类型实例字段 ≤ 10 且方法 ≤ 25）:");
        var domain = index.DomainFiles.ToList();
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
                .Where(b => b.Kind == BlockKind.Type && Metrics.IsPartialType(f, b))
                .Select(b => (File: f, Type: b, Key: Metrics.TypeKey(f, b))))
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        var surfaces = new List<(string Label, int Fields, int Methods)>();
        foreach (var group in partialGroups)
        {
            var parts = group
                .Select(x => (Name: x.File.RelativePath, Surface: Metrics.TypeSurface(x.File, x.Type)))
                .ToList();
            surfaces.Add(("partial " + group.Key, parts.Sum(x => x.Surface.Fields), parts.Sum(x => x.Surface.Methods)));
            Console.WriteLine($"  partial {group.Key}（{parts.Count} 个文件）：实例字段={parts.Sum(x => x.Surface.Fields)} 方法={parts.Sum(x => x.Surface.Methods)}"
                + $"  ← {string.Join(" + ", parts.Select(x => $"{x.Name}({x.Surface.Fields}/{x.Surface.Methods})"))}");
        }

        var mergedNames = partialGroups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var file in domain)
        {
            foreach (var type in Metrics.Blocks(file).Where(b => b.Kind == BlockKind.Type && !mergedNames.Contains(Metrics.TypeKey(file, b))))
            {
                var (fields, methods) = Metrics.TypeSurface(file, type);
                surfaces.Add(($"{file.RelativePath}:{type.Name}", fields, methods));
            }
        }

        Console.WriteLine($"  Domain 最大实例字段（partial 合并后）= {surfaces.Select(x => x.Fields).DefaultIfEmpty(0).Max()}");
        Console.WriteLine($"  Domain 最大方法数（partial 合并后）  = {surfaces.Select(x => x.Methods).DefaultIfEmpty(0).Max()}"
            + $"（{surfaces.OrderByDescending(x => x.Methods).ThenBy(x => x.Label, StringComparer.Ordinal).FirstOrDefault().Label}）");
    }
}
