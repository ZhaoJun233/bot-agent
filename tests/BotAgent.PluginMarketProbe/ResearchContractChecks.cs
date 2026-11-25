using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Plugins.Execution;
using BotAgent.Plugins.Net;
using BotAgent.Services.Links;
using BotAgent.Services.Net;
using BotAgent.Services.Plugins;
using BotAgent.Services.Resilience;

internal partial class Program
{
    private static PluginSharedContract ResearchContractDeclaration(byte[] bytes)
        => new("BotAgent.Research.Contracts", "1.0.0.0", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "preset.feature.websearch");

    private static byte[] ResearchArchive(byte[] payload, byte[] shared, string entryAssembly = "BotAgent.Package.WebSearch.dll")
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = zip.CreateEntry(entryAssembly).Open()) entry.Write(payload);
            using (var entry = zip.CreateEntry("BotAgent.Research.Contracts.dll").Open()) entry.Write(shared);
        }
        return output.ToArray();
    }

    internal static async Task<int> RunResearchContractChecks(string root)
    {
        var checks = await RunResearchHttpContractChecks() + await RunResearchSettingsContractChecks();
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var assembly = typeof(IResearchUseCase).Assembly;
        Type[] types = [typeof(IWebSearch), typeof(IResearchUseCase), typeof(IResearchDiagnostics), typeof(ILinkPreviewer),
            typeof(ResearchOptions), typeof(WebSearchHit), typeof(WebSearchResult)];
        Check(assembly.GetName().Name == "BotAgent.Research.Contracts" && assembly.GetName().Version == new Version(1, 0, 0, 0)
            && types.All(t => t.Assembly == assembly), "seven research public types have one independent versioned owner");
        Check(types.All(t => typeof(IBotPlugin).Assembly.GetType(t.FullName!) == t), "all seven previous Core identities forward unchanged");
        Check(new[] { typeof(IWebSearch), typeof(IResearchUseCase), typeof(ILinkPreviewer) }.All(t =>
            Type.GetType(t.FullName + ", BotAgent.Engine") == t), "previous Engine forwarders retain the same research identities");
        Check(assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("BotAgent.", StringComparison.Ordinal))
            .Select(a => a.Name).SequenceEqual(["BotAgent.Plugin.Contracts"]), "research contract has no reverse dependency on host or business implementation");

        var host = typeof(PluginPackageService).Assembly;
        Check(typeof(IBotPlugin).Assembly.GetType(typeof(PluginSettingDefinition).FullName!) == typeof(PluginSettingDefinition),
            "Core forwards the original declarative setting identity to the generic SDK");
        var (manifest, archive, _) = OfficialMarketplaceTestHelper.ReadRelease("preset.feature.websearch");
        var contract = manifest.SharedContracts!.Single();
        var contractBytes = await File.ReadAllBytesAsync(assembly.Location);
        Check(contract.Name == assembly.GetName().Name && contract.Version == assembly.GetName().Version!.ToString()
            && contract.Owner == manifest.Id && !string.IsNullOrEmpty(contract.Sha256),
            "official research manifest pins contract name, version, and owner");
        var index = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, releases = new[] { manifest } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        byte[] payload;
        using (var zip = new ZipArchive(new MemoryStream(archive)))
        {
            using var entry = zip.GetEntry(manifest.EntryAssembly!)!.Open();
            using var bytes = new MemoryStream(); entry.CopyTo(bytes); payload = bytes.ToArray();
            using var shared = zip.GetEntry(contract.Name + ".dll")!.Open();
            using var sharedBytes = new MemoryStream(); shared.CopyTo(sharedBytes);
            Check(zip.Entries.Count == 2 && Hash(sharedBytes.ToArray()) == contract.Sha256,
                "official research ZIP carries exact declared contract and implementation");
        }
        await ExercisePackage("default", archive, index, manifest);
        using (var pe = new PEReader(new MemoryStream(payload)))
        {
            var reader = pe.GetMetadataReader();
            Check(reader.AssemblyReferences.Any(h => reader.GetString(reader.GetAssemblyReference(h).Name) == contract.Name),
                "new package directly references the declared research contract");
        }
        Check(AssemblyLoadContext.Default.Assemblies.Count(a => a.GetName().Name == contract.Name) == 1
            && AssemblyLoadContext.GetLoadContext(assembly) == AssemblyLoadContext.Default, "packaged research contract reuses one Default identity");

        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "BotAgent.slnx"))) repository = repository.Parent;
        if (repository is null) throw new InvalidOperationException("repository_missing");
        var artifacts = Path.Combine(root, "research-contract-fixtures"); Directory.CreateDirectory(artifacts);
        await File.WriteAllBytesAsync(Path.Combine(artifacts, "host-index.json"), index);
        await File.WriteAllBytesAsync(Path.Combine(artifacts, "host-contract.dll"), contractBytes);
        await CheckIndependentReference();
        var independent = await BuildContract("current", null);
        var incompatible = await BuildContract("incompatible", "2.0.0");
        var candidatePayload = await BuildCandidate();
        using (var pe = new PEReader(new MemoryStream(independent)))
        {
            var reader = pe.GetMetadataReader();
            Check(reader.GetAssemblyDefinition().Version == new Version(1, 0, 0, 0)
                && reader.AssemblyReferences.Select(h => reader.GetString(reader.GetAssemblyReference(h).Name))
                    .Where(n => n.StartsWith("BotAgent.", StringComparison.Ordinal)).SequenceEqual(["BotAgent.Plugin.Contracts"]),
                "clean independent research contract build consumes only supplied plugin SDK binary");
        }
        await RejectPackage("wrong-version", incompatible, contract with { Version = new Version(2, 0, 0, 0).ToString(), Sha256 = Hash(incompatible) }, "plugin_contract_host_mismatch");
        var changedBytes = contractBytes.Concat(new byte[] { 0 }).ToArray();
        await RejectPackage("wrong-hash", changedBytes, contract, "plugin_contract_hash_mismatch");
        await RejectPackage("undeclared", contractBytes, null, "plugin_contract_undeclared");
        var bind = host.GetType("BotAgent.Adapters.Plugins.PluginSharedContractRegistry")!.GetMethod("Bind", BindingFlags.Static | BindingFlags.NonPublic)!;
        try
        {
            bind.Invoke(null, [Path.GetDirectoryName(assembly.Location), manifest with
                { Id = "probe.research-owner", SharedContracts = [contract with { Owner = "probe.research-owner" }] }]);
            throw new InvalidOperationException("foreign_research_owner_accepted");
        }
        catch (TargetInvocationException error) when (HasSdkCode(error, "plugin_contract_host_mismatch")) { checks++; }

        if (Environment.GetEnvironmentVariable("BOTAGENT_IT_LEGACY_RESEARCH") is { Length: > 0 } legacyDirectory)
        {
            var oldBytes = await File.ReadAllBytesAsync(Path.Combine(legacyDirectory, "research.zip"));
            var oldIndexBytes = await File.ReadAllBytesAsync(Path.Combine(legacyDirectory, "index.json"));
            var oldCore = await File.ReadAllBytesAsync(Path.Combine(legacyDirectory, "BotAgent.Core.dll"));
            using var oldIndex = JsonDocument.Parse(oldIndexBytes);
            var oldManifest = ReadResearchManifest(oldIndex);
            using (var zip = new ZipArchive(new MemoryStream(oldBytes)))
            using (var dll = new MemoryStream())
            {
                using var entry = zip.GetEntry(oldManifest.EntryAssembly!)!.Open(); entry.CopyTo(dll); dll.Position = 0;
                using var pe = new PEReader(dll); var reader = pe.GetMetadataReader();
                Check(oldManifest.SharedContracts is not { Count: > 0 } && reader.AssemblyReferences.All(h =>
                    reader.GetString(reader.GetAssemblyReference(h).Name) != contract.Name), "actual previous research package predates the direct contract reference");
            }
            using (var pe = new PEReader(new MemoryStream(oldCore)))
            {
                var reader = pe.GetMetadataReader();
                var definitions = reader.TypeDefinitions.Select(h => reader.GetTypeDefinition(h)).Select(t => reader.GetString(t.Namespace) + "." + reader.GetString(t.Name)).ToHashSet();
                Check(types.All(t => definitions.Contains(t.FullName!)), "actual previous compiler input owned all seven research definitions in Core");
            }
            await ExercisePackage("legacy", oldBytes, oldIndexBytes, oldManifest);
            var legacy = Path.Combine(artifacts, "legacy"); Directory.CreateDirectory(legacy);
            await File.WriteAllBytesAsync(Path.Combine(legacy, "research.zip"), oldBytes);
            await File.WriteAllBytesAsync(Path.Combine(legacy, "index.json"), oldIndexBytes);
            await File.WriteAllBytesAsync(Path.Combine(legacy, "BotAgent.Core.dll"), oldCore);
        }
        if (Environment.GetEnvironmentVariable("BOTAGENT_IT_RESEARCH_CONTRACT_EVIDENCE") is { Length: > 0 } evidence)
        {
            if (Directory.Exists(evidence)) throw new InvalidOperationException("research_contract_evidence_exists");
            foreach (var file in Directory.EnumerateFiles(artifacts, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(artifacts, file);
                if (relative.Split(Path.DirectorySeparatorChar).Contains("obj")) continue;
                var destination = Path.Combine(evidence, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
            }
        }
        return checks;

        async Task CheckIndependentReference()
        {
            var reference = ReviewManifest(archive, "probe.research-contract-reference", "1.0.0", manifest.EntryAssembly!, manifest.EntryType!)
                with { SharedContracts = [contract] };
            var directory = Path.Combine(root, "research-host-contract-reference");
            var service = new PluginPackageService(directory, new ReviewPackageHttp { Bytes = archive });
            await service.StageInstallAsync(reference);
            Check((reference.Dependencies?.Count ?? 0) == 0 && (await service.GetInstalledAsync()).Single().Pending?.SharedContracts?.Single() == contract,
                "exact host contract can be staged without depending on its business implementation");
            var statePath = Path.Combine(directory, reference.Id, "installed.json");
            var pending = await File.ReadAllBytesAsync(statePath);
            foreach (var invalid in new[] { contract with { Name = "Unapproved.Research.Contracts" }, contract with { Owner = "preset.foreign-owner" },
                contract with { Version = "2.0.0.0" },
                contract with { Name = "Unapproved.Ordinary.Contracts", Owner = "probe.undeclared" } })
            {
                try { await service.StageInstallAsync(reference with { SharedContracts = [invalid] }); throw new Exception("unapproved_host_contract_reference_accepted"); }
                catch (InvalidDataException error) when (error.Message == "plugin_contract_declaration_invalid") { checks++; }
            }
            try
            {
                await service.StageInstallAsync(reference with
                {
                    Version = "1.0.1",
                    SharedContracts = [contract with { Sha256 = new string('0', 64) }]
                });
                throw new Exception("tampered_official_contract_hash_accepted");
            }
            catch (InvalidDataException error) when (error.Message == "plugin_contract_hash_mismatch") { checks++; }
            Check((await File.ReadAllBytesAsync(statePath)).SequenceEqual(pending), "unapproved claims cannot modify staged state");
            var plain = reference with { SharedContracts = null };
            foreach (var state in new[] { new InstalledPluginPackage(reference.Id, Active: reference),
                new InstalledPluginPackage(reference.Id, Active: plain, Previous: reference),
                new InstalledPluginPackage(reference.Id, Active: plain, Pending: reference) })
            {
                await LifecycleTestRequests.SaveState(service, state);
                Check(JsonNode.Parse(await File.ReadAllBytesAsync(statePath))!["minimumReaderVersion"]!.GetValue<int>() == 9,
                    "active previous and pending preserve independent host contract reader requirement");
                var current = await PluginStateCompatibility.InspectAsync(directory, host.Location);
                Check(current.Compatible && current.CandidateReaderVersion == 12, "current reader accepts exact host references");
            }
            var valid = await File.ReadAllBytesAsync(statePath);
            var lowered = JsonNode.Parse(valid)!; lowered["minimumReaderVersion"] = 8;
            await File.WriteAllTextAsync(statePath, lowered.ToJsonString());
            Check((await service.GetInstalledAsync()).Single().MetadataFailure is not null, "lowering the header cannot hide independent host contract semantics");
            await File.WriteAllBytesAsync(statePath, valid);
            if (Environment.GetEnvironmentVariable("BOTAGENT_IT_PREVIOUS_RESEARCH_RUNTIME") is { Length: > 0 } previousRuntime)
            {
                var before = await PluginStateCompatibility.InspectAsync(directory, Path.Combine(previousRuntime, "BotAgent.Headless.dll"));
                Check(!before.Compatible && before.CandidateReaderVersion == 8 && before.Entries.Single().Code == "plugin_state_reader_incompatible",
                    "candidate PE preflight refuses actual reader eight");
                await CheckOldCode();
                var future = JsonNode.Parse(valid)!; future["execution"] = new JsonObject { ["futureHostContracts"] = new JsonArray("synthetic") };
                await File.WriteAllTextAsync(statePath, future.ToJsonString());
                var opaque = await File.ReadAllBytesAsync(statePath);
                await CheckOldCode();
                Check((await File.ReadAllBytesAsync(statePath)).SequenceEqual(opaque), "actual old reader leaves opaque future bytes unchanged");
                await File.WriteAllBytesAsync(Path.Combine(artifacts, "opaque-reader-nine.json"), opaque);
                await File.WriteAllBytesAsync(statePath, valid);
                async Task CheckOldCode()
                {
                    var result = await ExecutePreviousToolReader(previousRuntime, directory);
                    Check(result.GetProperty("CandidateReaderVersion").GetInt32() == 8 && !result.GetProperty("Compatible").GetBoolean()
                        && result.GetProperty("Entries")[0].GetProperty("Code").GetString() == "plugin_state_reader_incompatible"
                        && result.GetProperty("Entries")[0].GetProperty("Recovery").GetProperty("MinimumReaderVersion").GetInt32() == 9,
                        "actual reader eight code rejects before decoding or hashing newer execution");
                }
            }
            Check((await File.ReadAllBytesAsync(statePath)).SequenceEqual(valid), "compatibility checks preserve original state bytes");
            await File.WriteAllBytesAsync(Path.Combine(artifacts, "host-reference-state.json"), valid);
        }

        async Task ExercisePackage(string name, byte[] bytes, byte[] trustedIndex, PluginPackageManifest target)
        {
            var services = new ResearchContractServices();
            var context = new PluginContext(services);
            var manager = new PluginManager(_ => { });
            var packages = new PluginPackageService(Path.Combine(root, "research-contract-" + name), new ReviewPackageHttp { Bytes = bytes },
                TrustedPluginDistribution.Read(trustedIndex, Hash(trustedIndex)));
            await packages.StageDistributionInstallAsync(target, bytes);
            try
            {
                await packages.ApplyAtStartupAsync(target.Id, context, manager, false);
                var diagnostic = manager.GetControlContribution<IResearchDiagnostics>(target.Id);
                Check(!manager.IsReady(target.Id) && manager.IsControlReady(target.Id) && services.Model.Calls == 0 && services.Pages.Calls == 0,
                    name + " disabled package publishes diagnostics without making resource calls");
                var diagnosticResult = await diagnostic.TestSearchAsync("synthetic diagnostic", default);
                Check(diagnosticResult.HasContent && diagnosticResult.GetType() == typeof(WebSearchResult) && services.Model.Calls == 1,
                    name + " control uses one model call and exact independent DTO identity");
                await manager.RemoveAsync(target.Id);
                await packages.ApplyAtStartupAsync(target.Id, context, manager, true);
                var research = manager.GetContribution<IResearchUseCase>(target.Id);
                var result = await research.SearchAsync("synthetic:contract", "synthetic query");
                Check(result is { HasContent: true } && services.Model.Calls == 2 && research.TakeNote("synthetic:contract") == result.Describe()
                    && research.TakeNote("synthetic:contract") is null, name + " research keeps one call and one-use result note");
                var page = await research.ReadPageAsync("synthetic:contract", "https://example.com/page");
                Check(page is { Length: > 40 } && services.Pages.Calls == 1 && research.TakeNote("synthetic:contract")!.Contains(page),
                    name + " read result and note preserve their behavior");
                var links = manager.GetContribution<ILinkPreviewer>(target.Id);
                Check(await links.DescribeAsync(["https://example.com/links"], default) is { Length: > 0 } && services.Pages.Calls == 2,
                    name + " old link interface crosses the same package boundary once");
                await manager.RemoveAsync(target.Id);
                try { research.TakeNote("synthetic:contract"); throw new Exception("retired_research_reference_accepted"); }
                catch (InvalidOperationException error) when (error.Message == "plugin_contribution_unavailable") { checks++; }
            }
            finally { await manager.StopAllAsync(CancellationToken.None); }
        }
        async Task<byte[]> BuildContract(string name, string? version)
        {
            var directory = Path.Combine(artifacts, name); Directory.CreateDirectory(directory);
            var source = Path.Combine(repository.FullName, "contracts/research");
            foreach (var file in Directory.EnumerateFiles(source, "*.cs")) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            var project = XDocument.Load(Path.Combine(source, "BotAgent.Research.Contracts.csproj"));
            if (version is not null) project.Descendants("Version").Single().Value = version;
            project.Save(Path.Combine(directory, "BotAgent.Research.Contracts.csproj"));
            Directory.CreateDirectory(Path.Combine(directory, "sdk"));
            File.Copy(typeof(IPluginContribution).Assembly.Location, Path.Combine(directory, "sdk/BotAgent.Plugin.Contracts.dll"));
            await BuildSdkFixture(directory, "BotAgent.Research.Contracts.csproj", repository.FullName, "sdk");
            return await File.ReadAllBytesAsync(Path.Combine(directory, "out/BotAgent.Research.Contracts.dll"));
        }
        async Task<byte[]> BuildCandidate()
        {
            var directory = Path.Combine(artifacts, "candidate"); Directory.CreateDirectory(directory);
            var source = OfficialMarketplaceTestHelper.PluginBackendSourceDirectory("preset.feature.websearch");
            foreach (var file in Directory.EnumerateFiles(source, "*.cs")) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            var project = XDocument.Load(Path.Combine(source, "BotAgent.Package.WebSearch.csproj"));
            project.Descendants("Version").Single().Value = "1.0.1";
            project.Save(Path.Combine(directory, "BotAgent.Package.WebSearch.csproj"));
            Directory.CreateDirectory(Path.Combine(directory, "sdk"));
            foreach (var sdk in new[] { typeof(IBotPlugin).Assembly, typeof(IPluginContribution).Assembly, assembly })
                File.Copy(sdk.Location, Path.Combine(directory, "sdk", sdk.GetName().Name + ".dll"));
            await BuildSdkFixture(directory, "BotAgent.Package.WebSearch.csproj", repository.FullName, "sdk");
            return await File.ReadAllBytesAsync(Path.Combine(directory, "out/BotAgent.Package.WebSearch.dll"));
        }
        async Task RejectPackage(string name, byte[] sharedBytes, PluginSharedContract? declaration, string code)
        {
            var bytes = ResearchArchive(candidatePayload, sharedBytes);
            var target = manifest with { Version = "1.0.1", DownloadUrl = "host-distribution://preset.feature.websearch/1.0.1",
                Sha256 = Hash(bytes), SharedContracts = declaration is null ? null : [declaration] };
            var approved = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, releases = new[] { manifest, target } });
            await File.WriteAllBytesAsync(Path.Combine(artifacts, name + ".zip"), bytes);
            await File.WriteAllBytesAsync(Path.Combine(artifacts, name + ".json"), approved);
            var directory = Path.Combine(root, "research-contract-" + name);
            var service = new PluginPackageService(directory, new ReviewPackageHttp { Bytes = bytes }, TrustedPluginDistribution.Read(approved, Hash(approved)));
            var manager = new PluginManager(_ => { });
            var context = new PluginContext(new ResearchContractServices());
            try
            {
                await service.StageDistributionInstallAsync(manifest, archive);
                await service.ApplyAtStartupAsync(manifest.Id, context, manager, true);
                var previous = manager.GetContribution<IResearchUseCase>(manifest.Id); previous.AddNote("synthetic:retained", name);
                var statePath = Path.Combine(directory, target.Id, "installed.json");
                var activeBytes = await File.ReadAllBytesAsync(statePath);
                byte[]? pendingBytes = null;
                try
                {
                    await service.StageDistributionInstallAsync(target, bytes);
                    pendingBytes = await File.ReadAllBytesAsync(statePath);
                    await service.ApplyAtStartupAsync(target.Id, context, manager, true);
                    throw new Exception("bad_research_contract_accepted");
                }
                catch (Exception error) when (HasSdkCode(error, code)) { checks++; }
                Check((await File.ReadAllBytesAsync(statePath)).SequenceEqual(pendingBytes ?? activeBytes)
                    && manager.IsReady(target.Id)
                    && previous.TakeNote("synthetic:retained") == name
                    && (pendingBytes is null || (await service.GetInstalledAsync()).Single().Pending is not null),
                    name + " cannot replace the prior live generation or change recoverable state bytes");
            }
            finally { await manager.StopAllAsync(CancellationToken.None); }
        }
    }

    private static PluginPackageManifest ReadResearchManifest(JsonDocument index)
        => index.RootElement.GetProperty("releases").EnumerateArray().Select(e => e.Deserialize<PluginPackageManifest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Single(m => m.Id == "preset.feature.websearch");

    private sealed class ResearchContractServices : IServiceProvider
    {
        internal readonly AdoptionModel Model = new();
        internal readonly AdoptionPages Pages = new();
        private readonly ResearchClock _clock = new();
        private readonly ToolCircuitBreaker _execution = new("synthetic-research-contract");
        private readonly Func<ResearchOptions> _options = () => new(true, true, "", 3, 30, 5, 2000, true, 5, 3);
        public object? GetService(Type type) => type == typeof(IClock) ? _clock : type == typeof(IGroundedTextModel) ? Model
            : type == typeof(IWebPageTransport) ? Pages : type == typeof(IToolExecution) ? _execution
            : type == typeof(Func<ResearchOptions>) ? _options : null;
    }
}
