using System.Text.Json;

namespace BotAgent.ArchitectureProbe;

public static partial class Program
{
    private static void ReleaseVersionTests(string parent)
    {
        const string package = "packages/synthetic/backend/Synthetic.Package.csproj";
        const string contract = "contracts/Synthetic.Contracts/Synthetic.Contracts.csproj";
        var root = Path.Combine(parent, "release-versions");
        Directory.CreateDirectory(Path.Combine(root, "build"));
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), Project("1.4.0"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, package))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, contract))!);
        File.WriteAllText(Path.Combine(root, package), Project("2.3.4"));
        File.WriteAllText(Path.Combine(root, contract), Project("3.0.0-preview.1"));
        var solution = $"<Solution><Project Path=\"{package}\"/><Project Path=\"{contract}\"/></Solution>";
        File.WriteAllText(Path.Combine(root, "BotAgent.slnx"), solution);
        var registration = JsonSerializer.Serialize(new { schemaVersion = 1, projects = new[]
            { new { project = package, kind = "package" }, new { project = contract, kind = "contract" } } });
        File.WriteAllText(Path.Combine(root, "build/release-projects.json"), registration);
        var original = ReleaseVersions.Read(root);
        Check("发行：宿主、包、契约分别读取唯一版本", original.Host == "1.4.0"
            && original.Independent.Select(item => item.Version).Order().SequenceEqual(new[] { "2.3.4", "3.0.0-preview.1" }));
        File.WriteAllText(Path.Combine(root, package), Project("2.3.5"));
        var updated = ReleaseVersions.Read(root);
        Check("发行：包独立升级不改变宿主及契约版本", updated.Host == original.Host
            && updated.Independent.Single(item => item.Kind == "contract") == original.Independent.Single(item => item.Kind == "contract"));

        Reject("未登记版本覆盖", "build/release-projects.json", "{\"schemaVersion\":1,\"projects\":[]}", "unregistered_version");
        Reject("未知版本登记字段", "build/release-projects.json", registration.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"version\":\"2.0.0\""), "invalid_registration_fields");
        Reject("重复版本登记字段", "build/release-projects.json", registration.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"), "invalid_registration_fields");
        Reject("路径越界", "build/release-projects.json", registration.Replace(package, "../Synthetic.csproj"), "invalid_or_duplicate_project");
        Reject("宿主工程冒充包", "build/release-projects.json", registration.Replace(package, "src/BotAgent.Core/BotAgent.Core.csproj"), "invalid_or_duplicate_project");
        Reject("解决方案遗漏", "BotAgent.slnx", "<Solution/>", "project_missing_from_solution");
        Reject("条件版本", package, Project("2.3.5").Replace("<PropertyGroup>", "<PropertyGroup Condition=\"'$(Configuration)' == 'Release'\">"), "conditional_or_indirect_version");
        Reject("属性表达式版本", package, Project("$(ExternalVersion)"), "nonliteral_or_invalid_version");
        Reject("另设程序集版本", package, Project("2.3.5").Replace("</PropertyGroup>", "<AssemblyVersion>8.0.0</AssemblyVersion></PropertyGroup>"), "single_version_required");
        Reject("重复 Version", package, Project("2.3.5").Replace("</PropertyGroup>", "<Version>2.3.6</Version></PropertyGroup>"), "single_version_required");
        Reject("注册包漏写版本", package, "<Project/>", "single_version_required");
        Reject("非法预发行数字", contract, Project("3.0.0-preview.01"), "nonliteral_or_invalid_version");
        Reject("宿主预发行绕过既有 tag 格式", "Directory.Build.props", Project("1.4.0-preview"), "invalid_host_version");

        var saved = File.ReadAllText(Path.Combine(root, contract));
        File.Delete(Path.Combine(root, contract));
        try { Rejected("登记指向缺失工程", "registered_project_missing"); }
        finally { File.WriteAllText(Path.Combine(root, contract), saved); }

        void Reject(string name, string path, string replacement, string code)
        {
            var full = Path.Combine(root, path);
            var before = File.ReadAllText(full);
            File.WriteAllText(full, replacement);
            try { Rejected(name, code); }
            finally { File.WriteAllText(full, before); }
        }
        void Rejected(string name, string code)
        {
            var actual = "not_rejected";
            try { ReleaseVersions.Read(root); }
            catch (InvalidOperationException error) { actual = error.Message; }
            Check("发行拒绝：" + name, actual.StartsWith("release_version:" + code, StringComparison.Ordinal), actual);
        }
        static string Project(string version) => $"<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>";
    }
}
