using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using BotAgent.Panel;

internal partial class Program
{
    private static async Task<int> RunQuotaLegacyDtoChecks(string root)
    {
        var oldPanel = Environment.GetEnvironmentVariable("BOTAGENT_IT_LEGACY_PANEL");
        if (string.IsNullOrEmpty(oldPanel)) return 0;
        var directory = Path.Combine(root, "quota-legacy-dto");
        Directory.CreateDirectory(Path.Combine(directory, "sdk"));
        var input = Path.Combine(directory, "sdk", "BotAgent.Panel.dll");
        File.Copy(oldPanel, input);
        using (var pe = new PEReader(File.OpenRead(input)))
        {
            var metadata = pe.GetMetadataReader();
            var names = metadata.TypeDefinitions.Select(h => metadata.GetTypeDefinition(h))
                .Where(t => metadata.GetString(t.Namespace) == "BotAgent.Panel").Select(t => metadata.GetString(t.Name)).ToHashSet();
            if (!new[] { "ZbaQuota", "ZbaQuotaPage", "ZbaQuotaSaveRequest", "ZbaQuotaSaveResult", "ZbaQuotaFindRequest", "ZbaQuotaFindResult" }.All(names.Contains))
                throw new InvalidOperationException("legacy_panel_dto_definitions_required");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "Legacy.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>
            <AssemblyName>Synthetic.LegacyQuotaDto</AssemblyName><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            <ItemGroup><Reference Include="BotAgent.Panel"><HintPath>sdk/BotAgent.Panel.dll</HintPath><Private>false</Private></Reference></ItemGroup></Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(directory, "Client.cs"), """
            using BotAgent.Panel;
            public static class Client
            {
                public static object RoundTrip(object value) => new ZbaQuotaSaveResult((ZbaQuota)value);
                public static object Request(string key) => new ZbaQuotaSaveRequest(key, "0", "synthetic-legacy-http", 25);
            }
            """);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "BotAgent.slnx"))) repo = repo.Parent;
        var repository = repo?.FullName ?? throw new InvalidOperationException("quota_source_missing");
        await BuildSdkFixture(directory, "Legacy.csproj", repository);
        var load = new AssemblyLoadContext("synthetic-quota-legacy-dto", isCollectible: true);
        load.Resolving += (_, name) => name.Name == "BotAgent.Panel" ? typeof(ZbaEndpoint).Assembly : null;
        try
        {
            var assembly = load.LoadFromAssemblyPath(Path.Combine(directory, "out", "Synthetic.LegacyQuotaDto.dll"));
            var type = assembly.GetType("Client", throwOnError: true)!;
            var request = type.GetMethod("Request")!.Invoke(null, ["synthetic:legacy-http"]);
            var quota = new ZbaQuota("synthetic:legacy-http", "Synthetic", "0", 25, 2, 3, 5, 20,
                "2030-01-01", "2030-01-02T00:00:00.0000000+00:00", false, 1, 1000000000);
            var result = type.GetMethod("RoundTrip")!.Invoke(null, [quota]);
            if (request is not ZbaQuotaSaveRequest { DailyTokenLimit: 25 } || result is not ZbaQuotaSaveResult saved || saved.Quota != quota)
                throw new InvalidOperationException("legacy_panel_dto_identity_failed");
        }
        finally { load.Unload(); }
        if (Environment.GetEnvironmentVariable("BOTAGENT_IT_QUOTA_EVIDENCE") is { Length: > 0 } evidence)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(directory, f).Split(Path.DirectorySeparatorChar).Contains("obj")))
            {
                var target = Path.Combine(evidence, "legacy-panel", Path.GetRelativePath(directory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
        return 2;
    }
}
