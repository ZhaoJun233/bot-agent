using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Services.Plugins;

internal partial class Program
{
    internal static async Task<int> RunQuotaContractChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        var assembly = typeof(ITenantQuotaManagement).Assembly;
        Type[] types = [typeof(ITenantQuotaManagement), typeof(ITenantQuotaLedger), typeof(TenantQuotaSnapshot),
            typeof(TenantQuotaPolicy), typeof(TenantQuotaManagementSnapshot), typeof(TenantQuotaManagementException)];
        Check(assembly.GetName().Name == "BotAgent.Quota.Contracts" && assembly.GetName().Version == new Version(1, 0, 0, 0)
            && types.All(t => t.Assembly == assembly), "all six quota public types have one independent versioned owner");
        Check(types.All(t => typeof(IBotPlugin).Assembly.GetType(t.FullName!) == t), "all six old Core identities forward unchanged");
        Check(assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("BotAgent.", StringComparison.Ordinal))
            .Select(a => a.Name).SequenceEqual(["BotAgent.Plugin.Contracts"]), "quota contract depends only on generic plugin SDK");

        var host = typeof(PluginPackageService).Assembly;
        var metadata = host.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value!);
        var index = Convert.FromBase64String(metadata["BotAgent.PluginDistributionIndex"]);
        using var document = JsonDocument.Parse(index);
        var manifest = document.RootElement.GetProperty("releases").EnumerateArray().Select(e => e.Deserialize<PluginPackageManifest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Single(m => m.Id == "preset.feature.token-quota");
        var contract = manifest.SharedContracts!.Single();
        var contractBytes = await File.ReadAllBytesAsync(assembly.Location);
        Check(contract.Name == assembly.GetName().Name && contract.Version == assembly.GetName().Version!.ToString()
            && contract.Owner == manifest.Id && contract.Sha256 == Convert.ToHexString(SHA256.HashData(contractBytes)).ToLowerInvariant(),
            "compiled host authority pins actual owner version and bytes");
        var archive = await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(host.Location)!, "plugin-distribution", manifest.Sha256 + ".zip"));
        byte[] payload;
        using (var zip = new ZipArchive(new MemoryStream(archive)))
        {
            using var entry = zip.GetEntry(manifest.EntryAssembly!)!.Open();
            using var bytes = new MemoryStream(); entry.CopyTo(bytes); payload = bytes.ToArray();
            using var shared = zip.GetEntry(contract.Name + ".dll")!.Open();
            using var sharedBytes = new MemoryStream(); shared.CopyTo(sharedBytes);
            var expectedUiAssets = manifest.Ui?.Assets ?? [];
            Check(zip.Entries.Count == 2 + expectedUiAssets.Count && sharedBytes.ToArray().SequenceEqual(contractBytes)
                && expectedUiAssets.All(a => zip.GetEntry(a.Path) is not null),
                "default ZIP carries exact approved contract, implementation, and declared UI assets");
        }
        var context = new PluginContext(new EmptyQuotaServices());
        var registry = new PluginManager(_ => { });
        var packages = new PluginPackageService(Path.Combine(root, "quota-contract-default"), new ReviewPackageHttp { Bytes = archive },
            TrustedPluginDistribution.Read(index, metadata["BotAgent.PluginDistribution"]));
        await packages.StageDistributionInstallAsync(manifest, archive);
        try
        {
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, false);
            var control = registry.GetControlContribution<ITenantQuotaManagement>(manifest.Id);
            Check(control.ReadManagement("synthetic:quota-contract").Quota.DailyTokenLimit == 50000 && registry.IsControlReady(manifest.Id)
                && !registry.IsReady(manifest.Id), "preloaded approved contract supplies actual disabled package control");
            Check(AssemblyLoadContext.Default.Assemblies.Count(a => a.GetName().Name == contract.Name) == 1
                && AssemblyLoadContext.GetLoadContext(assembly) == AssemblyLoadContext.Default, "duplicate packaged contract uses one Default identity");
        }
        finally { await registry.StopAllAsync(CancellationToken.None); }

        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "BotAgent.slnx"))) repository = repository.Parent;
        if (repository is null) throw new InvalidOperationException("repository_missing");
        var artifacts = Path.Combine(root, "quota-contract-fixtures");
        Directory.CreateDirectory(artifacts);
        await File.WriteAllBytesAsync(Path.Combine(artifacts, "host-index.json"), index);
        await File.WriteAllBytesAsync(Path.Combine(artifacts, "host-contract.dll"), contractBytes);
        var independent = await BuildContract("current", null);
        var incompatible = await BuildContract("incompatible", "2.0.0");
        using (var pe = new PEReader(new MemoryStream(independent)))
        {
            var reader = pe.GetMetadataReader();
            Check(reader.GetAssemblyDefinition().Version == new Version(1, 0, 0, 0)
                && reader.AssemblyReferences.Select(h => reader.GetString(reader.GetAssemblyReference(h).Name))
                    .Where(n => n.StartsWith("BotAgent.", StringComparison.Ordinal)).SequenceEqual(["BotAgent.Plugin.Contracts"]),
                "clean independent contract build consumes only supplied SDK binary");
        }
        await RejectPackage("wrong-version", incompatible, contract with { Version = "2.0.0.0", Sha256 = Hash(incompatible) }, "plugin_contract_host_mismatch");
        var changedBytes = contractBytes.Concat(new byte[] { 0 }).ToArray();
        await RejectPackage("wrong-hash", changedBytes, contract with { Sha256 = Hash(changedBytes) }, "plugin_contract_host_mismatch");
        await RejectPackage("undeclared", contractBytes, null, "plugin_contract_undeclared");

        var bind = host.GetType("BotAgent.Adapters.Plugins.PluginSharedContractRegistry")!.GetMethod("Bind", BindingFlags.Static | BindingFlags.NonPublic)!;
        try
        {
            bind.Invoke(null, [Path.GetDirectoryName(assembly.Location), manifest with
            { Id = "probe.quota-owner", SharedContracts = [contract with { Owner = "probe.quota-owner" }] }]);
            throw new InvalidOperationException("foreign_quota_owner_accepted");
        }
        catch (TargetInvocationException error) when (HasSdkCode(error, "plugin_contract_host_mismatch")) { checks++; }

        if (Environment.GetEnvironmentVariable("BOTAGENT_IT_LEGACY_QUOTA") is { Length: > 0 } legacyDirectory)
        {
            var legacyBytes = await File.ReadAllBytesAsync(Path.Combine(legacyDirectory, "quota.zip"));
            var legacyIndex = await File.ReadAllBytesAsync(Path.Combine(legacyDirectory, "distribution.json"));
            using var oldIndex = JsonDocument.Parse(legacyIndex);
            var oldManifest = oldIndex.RootElement.GetProperty("releases").EnumerateArray().Select(e => e.Deserialize<PluginPackageManifest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!).Single(m => m.Version == "1.0.0");
            using (var zip = new ZipArchive(new MemoryStream(legacyBytes)))
            using (var dll = new MemoryStream())
            {
                using var entry = zip.GetEntry(oldManifest.EntryAssembly!)!.Open(); entry.CopyTo(dll); dll.Position = 0;
                using var pe = new PEReader(dll); var reader = pe.GetMetadataReader();
                Check(oldManifest.SharedContracts is not { Count: > 0 } && reader.AssemblyReferences.All(h =>
                    reader.GetString(reader.GetAssemblyReference(h).Name) != contract.Name), "actual previous package predates direct business contract reference");
            }
            var oldCore = await File.ReadAllBytesAsync(Path.Combine(legacyDirectory, "sdk/BotAgent.Core.dll"));
            using (var pe = new PEReader(new MemoryStream(oldCore)))
            {
                var reader = pe.GetMetadataReader();
                var definitions = reader.TypeDefinitions.Select(h => reader.GetTypeDefinition(h)).Select(t => reader.GetString(t.Namespace) + "." + reader.GetString(t.Name)).ToHashSet();
                Check(types.All(t => definitions.Contains(t.FullName!)), "actual prior compiler input owned all six quota definitions in Core");
            }
            var oldPackages = new PluginPackageService(Path.Combine(root, "quota-contract-legacy"), new ReviewPackageHttp { Bytes = legacyBytes },
                TrustedPluginDistribution.Read(legacyIndex, Hash(legacyIndex)));
            var oldRegistry = new PluginManager(_ => { });
            await oldPackages.StageDistributionInstallAsync(oldManifest, legacyBytes);
            try
            {
                await oldPackages.ApplyAtStartupAsync(oldManifest.Id, context, oldRegistry, false);
                var old = oldRegistry.GetControlContribution<ITenantQuotaManagement>(oldManifest.Id);
                var before = old.ReadManagement("synthetic:legacy-quota-contract");
                var saved = old.SaveManagementWithAudit(before.Quota.TenantId, 67, before.Revision, "synthetic-legacy-contract-save", "synthetic:legacy-caller",
                    (_, _) => new("config_change", "synthetic", "probe", "quota", "1"));
                Check(saved.Quota.DailyTokenLimit == 67 && old.FindManagement("synthetic-legacy-contract-save", "synthetic:legacy-caller") == saved,
                    "actual old package returns new unique DTO identity and original business receipt");
                var conflict = false;
                try { old.SaveManagementWithAudit(before.Quota.TenantId, 68, before.Revision, "synthetic-legacy-contract-stale", "synthetic:legacy-caller", (_, _) => throw new Exception()); }
                catch (TenantQuotaManagementException error) when (error.Code == "quota_revision_conflict") { conflict = true; }
                Check(conflict, "actual old package preserves typed quota conflict exception identity");
                Check(old.ReadManagement(before.Quota.TenantId) == saved, "legacy typed failure preserves committed quota");
            }
            finally { await oldRegistry.StopAllAsync(CancellationToken.None); }
            var legacy = Path.Combine(artifacts, "legacy"); Directory.CreateDirectory(legacy);
            await File.WriteAllBytesAsync(Path.Combine(legacy, "quota.zip"), legacyBytes);
            await File.WriteAllBytesAsync(Path.Combine(legacy, "distribution.json"), legacyIndex);
            await File.WriteAllBytesAsync(Path.Combine(legacy, "BotAgent.Core.dll"), oldCore);
        }
        if (Environment.GetEnvironmentVariable("BOTAGENT_IT_QUOTA_CONTRACT_EVIDENCE") is { Length: > 0 } evidence)
        {
            if (Directory.Exists(evidence)) throw new InvalidOperationException("quota_contract_evidence_exists");
            foreach (var file in Directory.EnumerateFiles(artifacts, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(artifacts, file);
                if (relative.Split(Path.DirectorySeparatorChar).Contains("obj")) continue;
                var destination = Path.Combine(evidence, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
            }
        }
        return checks;

        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        async Task<byte[]> BuildContract(string name, string? version)
        {
            var directory = Path.Combine(artifacts, name); Directory.CreateDirectory(directory);
            var source = Path.Combine(repository.FullName, "contracts/quota");
            foreach (var file in Directory.EnumerateFiles(source, "*.cs")) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            var project = XDocument.Load(Path.Combine(source, "BotAgent.Quota.Contracts.csproj"));
            if (version is not null) project.Descendants("Version").Single().Value = version;
            project.Save(Path.Combine(directory, "BotAgent.Quota.Contracts.csproj"));
            Directory.CreateDirectory(Path.Combine(directory, "sdk"));
            File.Copy(typeof(IPluginContribution).Assembly.Location, Path.Combine(directory, "sdk/BotAgent.Plugin.Contracts.dll"));
            await BuildSdkFixture(directory, "BotAgent.Quota.Contracts.csproj", repository.FullName, "sdk");
            return await File.ReadAllBytesAsync(Path.Combine(directory, "out/BotAgent.Quota.Contracts.dll"));
        }
        async Task RejectPackage(string name, byte[] sharedBytes, PluginSharedContract? declaration, string code)
        {
            var bytes = QuotaArchive(payload, sharedBytes);
            var target = manifest with { Sha256 = Hash(bytes), SharedContracts = declaration is null ? null : [declaration], Ui = null };
            var approved = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, releases = new[] { target } });
            await File.WriteAllBytesAsync(Path.Combine(artifacts, name + ".zip"), bytes);
            await File.WriteAllBytesAsync(Path.Combine(artifacts, name + ".json"), approved);
            var service = new PluginPackageService(Path.Combine(root, "quota-contract-" + name), new ReviewPackageHttp { Bytes = bytes }, TrustedPluginDistribution.Read(approved, Hash(approved)));
            var targetRegistry = new PluginManager(_ => { });
            await service.StageDistributionInstallAsync(target, bytes);
            try
            {
                var refused = false;
                try { await service.ApplyAtStartupAsync(target.Id, context, targetRegistry, true); }
                catch (Exception error) when (HasSdkCode(error, code)) { refused = true; }
                Check(refused, name + " returns exact refusal code");
                Check(!targetRegistry.IsControlReady(target.Id) && !targetRegistry.IsReady(target.Id)
                    && (await service.GetInstalledAsync()).Single().Active is null, name + " refuses publication and retains staged state");
            }
            finally { await targetRegistry.StopAllAsync(CancellationToken.None); }
        }
    }
}
