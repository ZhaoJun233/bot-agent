using System.Reflection;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

internal partial class Program
{
    internal static async Task<int> RunInstalledPresentationChecks(string root)
    {
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        var bytes = ReviewZip("probe.dll", await File.ReadAllBytesAsync(typeof(ReviewOrdinaryPlugin).Assembly.Location));
        var manifest = ReviewManifest(bytes, "probe.installed-presentation", "1.0.0", "probe.dll", typeof(ReviewOrdinaryPlugin).FullName!);
        var registered = new PluginInfo(manifest.Id, "合成包名称", manifest.Version!, "合成包功能说明", PluginCategory.Management, false, false);
        var http = new ReviewPackageHttp { Bytes = bytes };
        var packages = new PluginPackageService(Path.Combine(root, "installed-presentation"), http);
        await packages.StageInstallAsync(manifest);
        var service = new PluginMarketService(packages, new PluginSettingsStore(), new PluginCatalogSource(http),
            () => DateTimeOffset.UtcNow, Path.Combine(root, "installed-presentation-receipts.json"),
            registrySnapshot: () => [registered]);
        var calls = http.Calls;
        var installed = await service.ReadInstalledAsync(default);
        var row = installed.Plugins.Single();
        Check(row.Metadata is { Name: "合成包名称", Description: "合成包功能说明", Runtime: "dotnet", SourceKind: "community" },
            "an offline installed package uses its captured runtime name and description without becoming a built-in");
        Check(row.Metadata!.Review is { Status: "unreviewed" } && row.Metadata.Review.Sha256 == manifest.Sha256
            && installed.BuiltinPlugins.Count == 0, "runtime display never grants review or builtin status");
        Check(http.Calls == calls && row.Metadata.Author.Length == 0 && row.Metadata.Repository.Length == 0,
            "offline presentation makes no requests and invents no author or repository");

        var type = typeof(PluginPackageService).Assembly.GetType("BotAgent.Adapters.Plugins.PluginPresentationCatalog")!;
        var fallback = type.GetMethod("Unavailable", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        var project = type.GetMethod("ForInstalledPackage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ZbaPluginMetadata? Read(PluginInfo? info, bool hostDistribution = false)
            => (ZbaPluginMetadata?)project.Invoke(fallback, [manifest, info, hostDistribution]);
        Check(Read(null) is null && Read(registered with { Id = "probe.other" }) is null,
            "missing or foreign registry identity cannot supply package metadata");
        Check(Read(registered with { Version = "2.0.0" }) is null,
            "another runtime version cannot supply the installed version's metadata");
        Check(Read(registered with { IsPreset = true }) is null && Read(registered with { Description = "" }) is null,
            "legacy preset or incomplete text cannot masquerade as package presentation");
        Check(Read(registered, true) is { SourceKind: "official", Runtime: "dotnet", Review.Status: "unreviewed" },
            "host distribution provenance remains separate from runtime and code review");
        var richMetadata = row.Metadata with { Name = "目录中的合成名称", Author = "合成维护者",
            Repository = "https://github.com/example/plugins", License = "MIT",
            Review = row.Metadata.Review with { Sha256 = manifest.Sha256.ToLowerInvariant() } };
        var document = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Entries = new[]
            { new { manifest.Id, manifest.Version, PackageSha256 = manifest.Sha256.ToLowerInvariant(), Metadata = richMetadata } } });
        var sidecar = type.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [document])!;
        Check(((ZbaPluginMetadata)project.Invoke(sidecar, [manifest, registered, false])!).Name == richMetadata.Name,
            "an exact sidecar remains preferred over runtime fallback");
        var distributionType = typeof(TrustedPluginDistribution);
        var authoritative = distributionType.GetMethod("IsApprovedRelease", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var defaultIndex = typeof(PluginPackageService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "BotAgent.PluginDistributionIndex").Value!;
        var indexBytes = Convert.FromBase64String(defaultIndex);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(indexBytes));
        var distribution = TrustedPluginDistribution.Read(indexBytes, digest);
        using var index = System.Text.Json.JsonDocument.Parse(indexBytes);
        var approved = System.Text.Json.JsonSerializer.Deserialize<PluginPackageManifest>(
            index.RootElement.GetProperty("releases")[0], new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        bool Approved(PluginPackageManifest value) => (bool)authoritative.Invoke(distribution, [value])!;
        Check(Approved(approved) && !Approved(approved with { Sha256 = new string('0', 64) })
            && !Approved(approved with { Version = "99.0.0" }) && !Approved(approved with { Control = !approved.Control })
            && !Approved(manifest),
            "display provenance requires the exact approved manifest instead of a reserved name or URL");
        Console.WriteLine($"PASS installed package presentation: {checks} checks");
        return checks;
    }
}
