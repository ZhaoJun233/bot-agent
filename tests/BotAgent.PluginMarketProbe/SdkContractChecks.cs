using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.Json;
using System.Xml.Linq;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Ops;
using BotAgent.Services.Plugins;

internal partial class Program
{
    internal static async Task<int> RunSdkContractChecks(string root)
    {
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        var sdk = typeof(IPluginContribution).Assembly;
        var types = new[] { typeof(IPluginContribution), typeof(IPluginControlContribution), typeof(AuditEvent), typeof(AuditVerification), typeof(IAuditChain) };
        Check(sdk.GetName().Name == "BotAgent.Plugin.Contracts" && sdk.GetName().Version == new Version(1, 0, 0, 0)
            && types.All(t => t.Assembly == sdk), "five generic contracts have one independently versioned owner");
        Check(!sdk.GetReferencedAssemblies().Any(a => a.Name!.StartsWith("BotAgent.", StringComparison.Ordinal)), "SDK has no dependency back into host or business");
        Check(types.All(t => Type.GetType(t.FullName + ", BotAgent.Core", true) == t), "all old Core type names forward to the same SDK identity");
        Check(new[] { typeof(AuditEvent), typeof(AuditVerification), typeof(IAuditChain) }
            .All(t => Type.GetType(t.FullName + ", BotAgent.Engine", true) == t), "Engine audit forwards remain compatible");
        var directory = Path.Combine(root, "sdk-contracts");
        Directory.CreateDirectory(directory);
        var loaderType = typeof(PluginPackageService).Assembly.GetType("BotAgent.Adapters.Plugins.PluginPackageLoader", true)!;
        var caseLoader = (AssemblyLoadContext)Activator.CreateInstance(loaderType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [directory, new Dictionary<string, Assembly>()], null)!;
        try
        {
            var rejected = false;
            try { caseLoader.LoadFromAssemblyName(new AssemblyName(sdk.FullName!.Replace("BotAgent.Plugin.Contracts", "botagent.plugin.contracts", StringComparison.Ordinal))); }
            catch (Exception e) when (HasSdkCode(e, "plugin_sdk_contract_reference_mismatch")) { rejected = true; }
            Check(rejected, "case-altered SDK identity cannot fall through to private assembly loading");
        }
        finally { caseLoader.Unload(); }
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "BotAgent.slnx"))) repository = repository.Parent;
        if (repository is null) throw new InvalidOperationException("sdk_source_missing");
        var foreign = Path.Combine(directory, "foreign-sdk");
        Directory.CreateDirectory(foreign);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(repository.FullName, "contracts/plugin")))
            if (Path.GetExtension(file) is ".cs" or ".csproj") File.Copy(file, Path.Combine(foreign, Path.GetFileName(file)));
        var foreignProject = Path.Combine(foreign, "BotAgent.Plugin.Contracts.csproj");
        var xml = XDocument.Load(foreignProject);
        xml.Descendants("Version").Single().Value = "2.0.0";
        xml.Save(foreignProject);
        await BuildSdkFixture(foreign, foreignProject, repository.FullName);
        var foreignSdk = Path.Combine(foreign, "out/BotAgent.Plugin.Contracts.dll");

        async Task CheckPlugin(string side, string core, string? reference, bool reject)
        {
            var folder = Path.Combine(directory, side);
            Directory.CreateDirectory(folder);
            var inputs = Path.Combine(folder, "sdk");
            Directory.CreateDirectory(inputs);
            File.Copy(core, Path.Combine(inputs, "BotAgent.Core.dll"));
            core = Path.Combine(inputs, "BotAgent.Core.dll");
            if (reference is not null)
            {
                File.Copy(reference, Path.Combine(inputs, "BotAgent.Plugin.Contracts.dll"));
                reference = Path.Combine(inputs, "BotAgent.Plugin.Contracts.dll");
            }
            var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"), new XElement("AssemblyName", "Synthetic.SdkPlugin")),
                new XElement("ItemGroup", new XElement("Reference", new XAttribute("Include", "BotAgent.Core"), new XAttribute("HintPath", core), new XAttribute("Private", "false"))));
            if (reference is not null) project.Element("ItemGroup")!.Add(new XElement("Reference", new XAttribute("Include", "BotAgent.Plugin.Contracts"), new XAttribute("HintPath", reference), new XAttribute("Private", "false")));
            var projectPath = Path.Combine(folder, "Synthetic.csproj");
            new XDocument(project).Save(projectPath);
            await File.WriteAllTextAsync(Path.Combine(folder, "Plugin.cs"), """
                using BotAgent.Domain.Plugins;
                using BotAgent.Services.Ops;
                public sealed class SdkPlugin : IBotPlugin, IPluginContributionSource, IPluginControlContribution, IAuditChain
                {
                    public string Id => "probe.sdk";
                    public string Name => Id;
                    public string Version => "1.0.0";
                    public string Description => "Synthetic SDK compatibility";
                    public PluginCategory Category => PluginCategory.Management;
                    public Task InitializeAsync(PluginContext context, CancellationToken ct)
                    {
                        var export = (Action<object>)context.Services.GetService(typeof(Action<object>))!;
                        export(this);
                        export(new AuditEvent("synthetic", "actor", "tenant", "detail", "1"));
                        export(Verify());
                        return Task.CompletedTask;
                    }
                    public object? GetContribution(Type contract) => contract == typeof(IPluginContribution) ? this : null;
                    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
                    public void Append(AuditEvent auditEvent) { }
                    public AuditVerification Verify() => new(true, null, null, 1);
                }
                """);
            await BuildSdkFixture(folder, projectPath, repository.FullName);
            var binary = Path.Combine(folder, "out/Synthetic.SdkPlugin.dll");
            if (reference is null)
            {
                using var pe = new PEReader(File.OpenRead(binary));
                var metadata = pe.GetMetadataReader();
                Check(!metadata.AssemblyReferences.Any(h => metadata.GetString(metadata.GetAssemblyReference(h).Name) == "BotAgent.Plugin.Contracts"), "legacy fixture was compiled against old Core only");
            }
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            {
                using (var output = zip.CreateEntry("Synthetic.SdkPlugin.dll").Open()) await output.WriteAsync(await File.ReadAllBytesAsync(binary));
                // A package-local SDK with conflicting bytes never becomes the host type owner.
                using (var output = zip.CreateEntry("BotAgent.Plugin.Contracts.dll").Open()) await output.WriteAsync(await File.ReadAllBytesAsync(foreignSdk));
            }
            var payload = bytes.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(folder, "package.zip"), payload);
            var manifest = ReviewManifest(payload, "probe.sdk", "1.0.0", "Synthetic.SdkPlugin.dll", "SdkPlugin");
            var packages = new PluginPackageService(Path.Combine(folder, "installed"), new ReviewPackageHttp { Bytes = payload });
            await packages.StageInstallAsync(manifest);
            var registry = new PluginManager(_ => { });
            var exported = new List<object>();
            try
            {
                try { await packages.ApplyAtStartupAsync(manifest.Id, new PluginContext(new SdkServices(exported.Add)), registry, true); }
                catch (Exception e) when (reject && HasSdkCode(e, "plugin_sdk_contract_reference_mismatch"))
                {
                    Check(exported.Count == 0 && registry.Find(manifest.Id) is null, "wrong SDK reference is refused before initialization and publication");
                    return;
                }
                Check(!reject, "wrong-version SDK must not bind implicitly");
                Check(exported.Count == 3 && exported[0] is IPluginContribution and IPluginControlContribution and IAuditChain
                    && exported[1] is AuditEvent { EventType: "synthetic" } && exported[2] is AuditVerification { Valid: true }, side + " executes with the unique SDK types");
                Check(AssemblyLoadContext.GetLoadContext(exported[0].GetType().Assembly)?.IsCollectible == true
                    && AssemblyLoadContext.GetLoadContext(exported[1].GetType().Assembly) == AssemblyLoadContext.Default,
                    side + " implementation stays private while contracts stay Default");
                Check(AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name == sdk.GetName().Name) == 1, "package-local duplicate SDK is never loaded");
            }
            finally { await registry.StopAllAsync(CancellationToken.None); }
        }
        await CheckPlugin("current", typeof(IBotPlugin).Assembly.Location, sdk.Location, false);
        await CheckPlugin("wrong-version", typeof(IBotPlugin).Assembly.Location, foreignSdk, true);
        async Task CheckSharedSdk(string sharedName, string sdkReference, bool reject)
        {
            var sharedFolder = Path.Combine(directory, sharedName);
            Directory.CreateDirectory(sharedFolder);
            File.Copy(sdkReference, Path.Combine(sharedFolder, "BotAgent.Plugin.Contracts.dll"));
            var sharedProject = Path.Combine(sharedFolder, "Shared.csproj");
            await File.WriteAllTextAsync(sharedProject, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>{sharedName}</AssemblyName><Version>1.0.0</Version></PropertyGroup>
                  <ItemGroup><Reference Include="BotAgent.Plugin.Contracts" HintPath="BotAgent.Plugin.Contracts.dll" Private="false" /></ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(sharedFolder, "Contract.cs"),
                "public interface IForeignContract : BotAgent.Domain.Plugins.IPluginContribution { int Read(); }");
            await BuildSdkFixture(sharedFolder, sharedProject, repository.FullName);
            var sharedBinary = Path.Combine(sharedFolder, "out", sharedName + ".dll");
            using (var payload = new MemoryStream())
            {
                using (var zip = new ZipArchive(payload, ZipArchiveMode.Create, true))
                {
                    using (var entry = zip.CreateEntry("Synthetic.SdkPlugin.dll").Open())
                        await entry.WriteAsync(await File.ReadAllBytesAsync(Path.Combine(directory, "current/out/Synthetic.SdkPlugin.dll")));
                    using (var entry = zip.CreateEntry(sharedName + ".dll").Open()) await entry.WriteAsync(await File.ReadAllBytesAsync(sharedBinary));
                }
                var bytes = payload.ToArray();
                await File.WriteAllBytesAsync(Path.Combine(sharedFolder, "package.zip"), bytes);
                var manifest = ReviewManifest(bytes, "probe.sdk", "1.0.0", "Synthetic.SdkPlugin.dll", "SdkPlugin") with
                {
                    SharedContracts = [new(sharedName, "1.0.0.0",
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(sharedBinary))), "probe.sdk")]
                };
                var packages = new PluginPackageService(Path.Combine(sharedFolder, "installed"), new ReviewPackageHttp { Bytes = bytes });
                await packages.StageInstallAsync(manifest);
                var registry = new PluginManager(_ => { });
                var exported = new List<object>();
                var rejected = false;
                try
                {
                    try { await packages.ApplyAtStartupAsync(manifest.Id, new PluginContext(new SdkServices(exported.Add)), registry, true); }
                    catch (Exception e) when (HasSdkCode(e, "plugin_sdk_contract_reference_mismatch")) { rejected = true; }
                    var shared = AssemblyLoadContext.Default.Assemblies.SingleOrDefault(a => a.GetName().Name == sharedName);
                    if (reject)
                        Check(rejected && exported.Count == 0 && registry.Find(manifest.Id) is null && shared is null,
                            "shared contract with wrong SDK reference is refused before permanent Default load or initialization");
                    else
                        Check(!rejected && exported.Count == 3 && registry.IsReady(manifest.Id) && shared is not null
                            && typeof(IPluginContribution).IsAssignableFrom(shared.GetType("IForeignContract")),
                            "shared contract with exact SDK reference uses the same Default marker identity");
                }
                finally { await registry.StopAllAsync(CancellationToken.None); }
            }
        }
        await CheckSharedSdk("Synthetic.Current.Contracts", sdk.Location, false);
        await CheckSharedSdk("Synthetic.Bad.Contracts", foreignSdk, true);
        if (Environment.GetEnvironmentVariable("BOTAGENT_IT_LEGACY_CORE") is { Length: > 0 } legacy)
        {
            using var pe = new PEReader(File.OpenRead(legacy));
            var metadata = pe.GetMetadataReader();
            Check(types.All(t => metadata.TypeDefinitions.Any(h =>
            {
                var definition = metadata.GetTypeDefinition(h);
                return metadata.GetString(definition.Name) == t.Name && metadata.GetString(definition.Namespace) == t.Namespace;
            })), "supplied historical Core owns all five original definitions");
            await CheckPlugin("legacy", Path.GetFullPath(legacy), null, false);
        }
        if (Environment.GetEnvironmentVariable("BOTAGENT_IT_SDK_EVIDENCE") is { Length: > 0 } evidence)
        {
            if (Directory.Exists(evidence)) throw new InvalidOperationException("sdk_evidence_exists");
            Directory.CreateDirectory(evidence);
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(directory, file);
                if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "installed")) continue;
                var target = Path.Combine(evidence, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
        return checks;
    }

    private static bool HasSdkCode(Exception error, string code)
        => error.Message == code || error.InnerException is not null && HasSdkCode(error.InnerException, code);

    private static async Task BuildSdkFixture(string directory, string project, string repository, string? sdkPath = null)
    {
        File.Copy(Path.Combine(repository, "Directory.Build.props"), Path.Combine(directory, "Directory.Build.props"));
        await File.WriteAllTextAsync(Path.Combine(directory, "NuGet.Config"), "<configuration><packageSources><clear/></packageSources></configuration>");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "build", project, "-c", "Release", "--nologo", "-o", "out" }) start.ArgumentList.Add(arg);
        if (sdkPath is not null) start.ArgumentList.Add("-p:PluginSdkPath=" + sdkPath);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        var diagnostics = await output + await error;
        await File.WriteAllTextAsync(Path.Combine(directory, "build.log"), diagnostics);
        await File.WriteAllTextAsync(Path.Combine(directory, "build-command.json"), JsonSerializer.Serialize(new { arguments = start.ArgumentList.ToArray(), exitCode = process.ExitCode }));
        if (process.ExitCode != 0) throw new InvalidOperationException("sdk_fixture_build_failed: " + diagnostics);
    }
    private sealed class SdkServices(Action<object> export) : IServiceProvider
    { public object? GetService(Type type) => type == typeof(Action<object>) ? export : null; }
}
