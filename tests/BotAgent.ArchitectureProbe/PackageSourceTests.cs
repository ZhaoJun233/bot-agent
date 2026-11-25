namespace BotAgent.ArchitectureProbe;

public static partial class Program
{
    private static void PackageSourceTests(string parent)
    {
        var root = CreateFixture(parent, "package-source");
        var directory = Path.Combine(root, "packages/agent/backend");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(root, Baseline.AgentProject), "<Project><ItemGroup><ProjectReference Include=\"../../../src/BotAgent.Core/BotAgent.Core.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(directory, "AgentLedger.cs"), "internal class AgentLedger { string Sql = \"SELECT value FROM synthetic_table\"; }");
        File.WriteAllText(Path.Combine(directory, "AgentAdmission.cs"), "internal class AgentAdmission { }");
        var solution = Path.Combine(root, "BotAgent.slnx");
        File.WriteAllText(solution, File.ReadAllText(solution).Replace("</Solution>", $"<Project Path=\"{Baseline.AgentProject}\" /></Solution>"));
        var host = Path.Combine(root, "src/BotAgent.Storage/BotAgent.Storage.csproj");
        var original = File.ReadAllText(host);
        var link = "<ItemGroup><Compile Include=\"../../packages/agent/backend/AgentLedger.cs\" Link=\"Compatibility/AgentLedger.cs\" /></ItemGroup>";
        var snapshot = LoadFixture(root);
        var ledger = snapshot.ByPath("packages/agent/backend/AgentLedger.cs")!;
        Check("包源码按唯一实际 owner 扫描", snapshot.Files.Count == 10
            && ledger.ProjectName == "BotAgent.Package.Agent" && Metrics.CountSqlLiterals(ledger) == 1);
        File.WriteAllText(host, original.Replace("</Project>", link + "</Project>"));
        var rejected = false;
        try { LoadFixture(root); } catch (InvalidOperationException) { rejected = true; }
        Check("系统包兼容链接不能建到 Storage", rejected);
        File.WriteAllText(host, original);
        var engine = Path.Combine(root, "src/BotAgent.Engine/BotAgent.Engine.csproj");
        var originalEngine = File.ReadAllText(engine);
        File.WriteAllText(engine, originalEngine.Replace("</Project>", link.Replace("AgentLedger", "AgentAdmission") + "</Project>"));
        rejected = false;
        try { LoadFixture(root); } catch (InvalidOperationException) { rejected = true; }
        Check("系统包兼容链接不能建到 Engine", rejected);
        File.WriteAllText(engine, originalEngine);
        var package = Path.Combine(root, Baseline.AgentProject);
        File.WriteAllText(package, File.ReadAllText(package).Replace("BotAgent.Core", "BotAgent.Headless"));
        rejected = false;
        try { LoadFixture(root); } catch (InvalidOperationException) { rejected = true; }
        Check("实际包不得反向依赖私有宿主工程", rejected);
        File.WriteAllText(package, File.ReadAllText(package).Replace("BotAgent.Headless", "BotAgent.Core"));
        var buildTool = Path.Combine(root, Baseline.BundleProject);
        Directory.CreateDirectory(Path.GetDirectoryName(buildTool)!);
        File.WriteAllText(buildTool, "<Project><ItemGroup><ProjectReference Include=\"../../src/BotAgent.Core/BotAgent.Core.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(buildTool)!, "Program.cs"), "class BundleTool { }");
        File.WriteAllText(solution, File.ReadAllText(solution).Replace("</Solution>", $"<Project Path=\"{Baseline.BundleProject}\" /></Solution>"));
        var headless = Path.Combine(root, "src/BotAgent.Headless/BotAgent.Headless.csproj");
        var buildReference = "<ItemGroup><ProjectReference Include=\"../../packages/agent/backend/BotAgent.Package.Agent.csproj\" ReferenceOutputAssembly=\"false\" Private=\"false\" /></ItemGroup>";
        File.WriteAllText(headless, File.ReadAllText(headless).Replace("</Project>", buildReference + "</Project>"));
        snapshot = LoadFixture(root);
        var hostProject = snapshot.Projects.Single(p => p.Name == "BotAgent.Headless");
        Check("构建依赖保留扫描归属但不冒充运行时引用", hostProject.BuildReferences!.Contains(Baseline.AgentProject)
            && !hostProject.References.Contains(Baseline.AgentProject) && snapshot.Projects.Any(p => p.RelativePath == Baseline.BundleProject));
        var validBuildProject = File.ReadAllText(headless);
        File.WriteAllText(headless, validBuildProject.Replace(" Private=\"false\"", ""));
        rejected = false;
        try { LoadFixture(root); } catch (InvalidOperationException) { rejected = true; }
        Check("构建依赖不得复制应用宿主与运行配置到发布目录", rejected);
        File.WriteAllText(headless, validBuildProject.Replace(" ReferenceOutputAssembly=\"false\"", ""));
        rejected = false;
        try { LoadFixture(root); } catch (InvalidOperationException) { rejected = true; }
        Check("构建边不得悄悄变为私有业务运行时依赖", rejected);
        File.WriteAllText(headless, validBuildProject);
        var conversationProject = Path.Combine(root, Baseline.ConversationProject);
        Directory.CreateDirectory(Path.GetDirectoryName(conversationProject)!);
        File.WriteAllText(conversationProject, "<Project><ItemGroup><ProjectReference Include=\"../../../src/BotAgent.Core/BotAgent.Core.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(conversationProject)!, "ConversationCoordinator.cs"), "internal class ConversationCoordinator { }");
        File.WriteAllText(solution, File.ReadAllText(solution).Replace("</Solution>", $"<Project Path=\"{Baseline.ConversationProject}\" /></Solution>"));
        var conversationBuildReference = buildReference.Replace("agent/backend/BotAgent.Package.Agent.csproj", "conversation/backend/BotAgent.Package.Conversation.csproj");
        File.WriteAllText(headless, validBuildProject.Replace("</Project>", conversationBuildReference + "</Project>"));
        snapshot = LoadFixture(root);
        Check("会话实现归属独立包且宿主仅构建依赖", snapshot.ByPath("packages/conversation/backend/ConversationCoordinator.cs")?.ProjectName == "BotAgent.Package.Conversation"
            && snapshot.Projects.Single(p => p.Name == "BotAgent.Headless").BuildReferences!.Contains(Baseline.ConversationProject));
        var validConversationBuild = File.ReadAllText(headless);
        File.WriteAllText(headless, validConversationBuild.Replace("</Project>",
            "<ItemGroup><Compile Include=\"../../packages/conversation/backend/ConversationCoordinator.cs\" Link=\"Compatibility/ConversationCoordinator.cs\" /></ItemGroup></Project>"));
        rejected = false;
        try { LoadFixture(root); } catch (InvalidOperationException) { rejected = true; }
        Check("已隔离会话兼容源码链接不能重建", rejected);
        File.WriteAllText(headless, validConversationBuild.Replace(conversationBuildReference, conversationBuildReference.Replace(" ReferenceOutputAssembly=\"false\"", "")));
        rejected = false;
        try { LoadFixture(root); } catch (InvalidOperationException) { rejected = true; }
        Check("会话构建引用不能偷偷成为运行时引用", rejected);
        File.WriteAllText(headless, validConversationBuild.Replace(conversationBuildReference, conversationBuildReference.Replace(" Private=\"false\"", "")));
        rejected = false;
        try { LoadFixture(root); } catch (InvalidOperationException) { rejected = true; }
        Check("会话构建输出不能复制进宿主运行目录", rejected);

        var contractsRoot = CreateFixture(parent, "sdk-contract-source");
        var sdkProject = Path.Combine(contractsRoot, Baseline.PluginContractsProject);
        Directory.CreateDirectory(Path.GetDirectoryName(sdkProject)!);
        File.WriteAllText(sdkProject, "<Project />");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(sdkProject)!, "Contracts.cs"), "public interface IPluginContribution { }");
        var sdkSolution = Path.Combine(contractsRoot, "BotAgent.slnx");
        File.WriteAllText(sdkSolution, File.ReadAllText(sdkSolution).Replace("</Solution>", $"<Project Path=\"{Baseline.PluginContractsProject}\" /></Solution>"));
        var coreProject = Path.Combine(contractsRoot, "src/BotAgent.Core/BotAgent.Core.csproj");
        File.WriteAllText(coreProject, File.ReadAllText(coreProject).Replace("</Project>",
            "<ItemGroup><ProjectReference Include=\"../../contracts/plugin/BotAgent.Plugin.Contracts.csproj\" GlobalPropertiesToRemove=\"Version;AssemblyVersion;FileVersion;InformationalVersion\" /></ItemGroup></Project>"));
        var contractsSnapshot = LoadFixture(contractsRoot);
        Check("公共 SDK 契约作为独立 owner 扫描且 Core 只向下依赖", contractsSnapshot.Projects.Single(p => p.Name == "BotAgent.Plugin.Contracts").References.Count == 0
            && contractsSnapshot.ByPath("contracts/plugin/Contracts.cs")?.ProjectName == "BotAgent.Plugin.Contracts");
        File.WriteAllText(sdkProject, "<Project><ItemGroup><ProjectReference Include=\"../../src/BotAgent.Core/BotAgent.Core.csproj\" /></ItemGroup></Project>");
        rejected = false;
        try { LoadFixture(contractsRoot); } catch (InvalidOperationException) { rejected = true; }
        Check("公共 SDK 不得反向引用 Core 形成契约环", rejected);
        File.WriteAllText(sdkProject, "<Project />");
        var quotaContract = Path.Combine(contractsRoot, Baseline.QuotaContractsProject);
        Directory.CreateDirectory(Path.GetDirectoryName(quotaContract)!);
        File.WriteAllText(quotaContract, "<Project><ItemGroup><ProjectReference Include=\"../plugin/BotAgent.Plugin.Contracts.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(quotaContract)!, "Quota.cs"), "public interface ITenantQuotaManagement { }");
        File.WriteAllText(sdkSolution, File.ReadAllText(sdkSolution).Replace("</Solution>", $"<Project Path=\"{Baseline.QuotaContractsProject}\" /></Solution>"));
        File.WriteAllText(coreProject, File.ReadAllText(coreProject).Replace("</Project>",
            "<ItemGroup><ProjectReference Include=\"../../contracts/quota/BotAgent.Quota.Contracts.csproj\" GlobalPropertiesToRemove=\"Version;AssemblyVersion;FileVersion;InformationalVersion\" /></ItemGroup></Project>"));
        contractsSnapshot = LoadFixture(contractsRoot);
        Check("配额契约具有独立 owner 且只依赖通用 SDK", contractsSnapshot.ByPath("contracts/quota/Quota.cs")?.ProjectName == "BotAgent.Quota.Contracts"
            && contractsSnapshot.Projects.Single(p => p.Name == "BotAgent.Quota.Contracts").References.SequenceEqual([Baseline.PluginContractsProject]));
        File.WriteAllText(quotaContract, "<Project><ItemGroup><ProjectReference Include=\"../../src/BotAgent.Core/BotAgent.Core.csproj\" /></ItemGroup></Project>");
        rejected = false;
        try { LoadFixture(contractsRoot); } catch (InvalidOperationException) { rejected = true; }
        Check("配额业务契约不能反向依赖 Core", rejected);
        File.WriteAllText(quotaContract, "<Project />");
        var researchContract = Path.Combine(contractsRoot, Baseline.ResearchContractsProject);
        Directory.CreateDirectory(Path.GetDirectoryName(researchContract)!);
        File.WriteAllText(researchContract, "<Project><ItemGroup><ProjectReference Include=\"../plugin/BotAgent.Plugin.Contracts.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(researchContract)!, "Research.cs"), "public interface IResearchUseCase { }");
        File.WriteAllText(sdkSolution, File.ReadAllText(sdkSolution).Replace("</Solution>", $"<Project Path=\"{Baseline.ResearchContractsProject}\" /></Solution>"));
        File.WriteAllText(coreProject, File.ReadAllText(coreProject).Replace("</Project>",
            "<ItemGroup><ProjectReference Include=\"../../contracts/research/BotAgent.Research.Contracts.csproj\" GlobalPropertiesToRemove=\"Version;AssemblyVersion;FileVersion;InformationalVersion\" /></ItemGroup></Project>"));
        contractsSnapshot = LoadFixture(contractsRoot);
        Check("研究契约具有独立 owner 且只依赖通用 SDK", contractsSnapshot.ByPath("contracts/research/Research.cs")?.ProjectName == "BotAgent.Research.Contracts"
            && contractsSnapshot.Projects.Single(p => p.Name == "BotAgent.Research.Contracts").References.SequenceEqual([Baseline.PluginContractsProject]));
        File.WriteAllText(researchContract, "<Project><ItemGroup><ProjectReference Include=\"../../src/BotAgent.Core/BotAgent.Core.csproj\" /></ItemGroup></Project>");
        rejected = false;
        try { LoadFixture(contractsRoot); } catch (InvalidOperationException) { rejected = true; }
        Check("研究业务契约不能反向依赖 Core", rejected);
    }
}
