namespace BotAgent.ArchitectureProbe;

public static partial class Program
{
    private static void SchemaSourceTests(string parent)
    {
        var root = CreateFixture(parent, "schema-source");
        var owner = Path.Combine(root, Baseline.QuotaProject);
        Directory.CreateDirectory(Path.GetDirectoryName(owner)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(owner)!, "Quota.cs"), "public class Quota { }");
        var solution = Path.Combine(root, "BotAgent.slnx");
        File.WriteAllText(solution, File.ReadAllText(solution).Replace("</Solution>", $"<Project Path=\"{Baseline.QuotaProject}\" /></Solution>"));
        var storage = Path.Combine(root, "src/BotAgent.Storage/BotAgent.Storage.csproj");
        var hostOriginal = File.ReadAllText(storage);
        var packageItems = "";
        var hostItems = "";
        foreach (var direction in new[] { "up", "down" })
        {
            var name = $"preset.token-quota.14-15.{direction}.sql";
            var path = Path.Combine(Path.GetDirectoryName(owner)!, "Schema", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, direction == "up" ? "CREATE TABLE synthetic(id INTEGER);" : "DROP TABLE synthetic;");
            packageItems += $"<EmbeddedResource Include=\"Schema/{name}\" LogicalName=\"BotAgent.Schema.{name}\" />";
            hostItems += $"<EmbeddedResource Include=\"../../packages/token-quota/backend/Schema/{name}\" LogicalName=\"BotAgent.Schema.{name}\" />";
        }
        var package = "<Project><ItemGroup>" + packageItems + "</ItemGroup></Project>";
        var host = hostOriginal.Replace("</Project>", "<ItemGroup>" + hostItems + "</ItemGroup></Project>");
        File.WriteAllText(owner, package);
        File.WriteAllText(storage, host);
        var scripts = LoadFixture(root).Files.Where(f => f.IsSqlResource).ToArray();
        Check("SQL资源在实际包owner扫描一次，DROP也计数", scripts.Length == 2
            && scripts.All(f => f.ProjectName == "BotAgent.Package.TokenQuota" && Metrics.CountSqlLiterals(f) == 1));
        void Refuses(string label)
        {
            var refused = false;
            try { LoadFixture(root); } catch (InvalidOperationException) { refused = true; }
            Check(label, refused);
        }
        File.WriteAllText(storage, hostOriginal);
        Refuses("保留历史恢复输入不能静默移除");
        File.WriteAllText(storage, host.Replace("LogicalName=\"BotAgent.Schema.", "LogicalName=\"Wrong."));
        Refuses("历史SQL的资源身份必须精确");
        File.WriteAllText(storage, host.Replace("<EmbeddedResource ", "<EmbeddedResource Condition=\"'$(Configuration)' == 'Release'\" "));
        Refuses("历史SQL条件输入不能绕过所有权扫描");
        File.WriteAllText(storage, host);
        File.WriteAllText(owner, package.Replace("Schema/preset", "../outside/preset"));
        Refuses("未登记SQL外部路径必须拒绝");
    }
}
