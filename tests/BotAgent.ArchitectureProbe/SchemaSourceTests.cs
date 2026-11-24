namespace BotAgent.ArchitectureProbe;

public static partial class Program
{
    private static void SchemaSourceTests(string parent)
    {
        var root = CreateFixture(parent, "schema-source");
        var storage = Path.Combine(root, "src/BotAgent.Storage/BotAgent.Storage.csproj");
        var hostOriginal = File.ReadAllText(storage);
        var hostItems = "";
        foreach (var direction in new[] { "up", "down" })
        {
            var name = $"preset.token-quota.14-15.{direction}.sql";
            var path = Path.Combine(Path.GetDirectoryName(storage)!, "Schema", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, direction == "up" ? "CREATE TABLE synthetic(id INTEGER);" : "DROP TABLE synthetic;");
            hostItems += $"<EmbeddedResource Include=\"Schema/{name}\" LogicalName=\"BotAgent.Schema.{name}\" />";
        }
        var host = hostOriginal.Replace("</Project>", "<ItemGroup>" + hostItems + "</ItemGroup></Project>");
        File.WriteAllText(storage, host);
        var scripts = LoadFixture(root).Files.Where(f => f.IsSqlResource).ToArray();
        Check("历史 SQL 资源在 Storage owner 扫描一次，DROP 也计数", scripts.Length == 2
            && scripts.All(f => f.ProjectName == "BotAgent.Storage" && Metrics.CountSqlLiterals(f) == 1));
        void Refuses(string label)
        {
            var refused = false;
            try { LoadFixture(root); } catch (InvalidOperationException) { refused = true; }
            Check(label, refused);
        }
        File.WriteAllText(storage, host.Replace("LogicalName=\"BotAgent.Schema.", "LogicalName=\"Wrong."));
        Refuses("历史SQL的资源身份必须精确");
        File.WriteAllText(storage, host.Replace("<EmbeddedResource ", "<EmbeddedResource Condition=\"'$(Configuration)' == 'Release'\" "));
        Refuses("历史SQL条件输入不能绕过所有权扫描");
        File.WriteAllText(storage, host.Replace("Schema/preset", "../outside/preset"));
        Refuses("未登记SQL外部路径必须拒绝");
    }
}
