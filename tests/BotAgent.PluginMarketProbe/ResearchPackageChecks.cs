using System.Diagnostics;
using System.Net;
using System.Text.Json;
using BotAgent.Adapters.Net;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using BotAgent.Services.Net;
using BotAgent.Services.Plugins;
using System.Xml.Linq;

internal partial class Program
{
    internal static async Task<int> RunResearchPackageChecks(string root)
    {
        var checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks++; }
        void Reject(Action action, string code)
        {
            try { action(); } catch (InvalidOperationException e) when (e.Message == code) { checks++; return; }
            throw new InvalidOperationException("expected_" + code);
        }
        var payloads = await BuildResearchPackages(Path.Combine(root, "research-independent"));
        var contractBytes = await File.ReadAllBytesAsync(typeof(IResearchUseCase).Assembly.Location);
        var contract = ResearchContractDeclaration(contractBytes);
        var providerBytes = ResearchArchive(payloads.Provider, contractBytes, "Synthetic.Research.Transport.dll");
        var consumerBytes = ResearchArchive(payloads.Consumer, contractBytes);
        var providerManifest = ReviewManifest(providerBytes, "probe.research.transport", "1.0.0", "Synthetic.Research.Transport.dll", "TransportPlugin")
            with { SharedContracts = [contract] };
        var manifest = ReviewManifest(consumerBytes, "preset.feature.websearch", "1.0.0", "BotAgent.Package.WebSearch.dll", "BotAgent.Packages.WebSearch.ResearchPlugin") with
        {
            Dependencies = [new(providerManifest.Id, "1.0.0")],
            ToolApiVersion = 1,
            SharedContracts = [contract],
            Settings = [new("providerId", "Provider", "string", providerManifest.Id, true)]
        };
        var index = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, releases = new[] { manifest } });
        if (Environment.GetEnvironmentVariable("BOTAGENT_IT_RESEARCH_EVIDENCE") is { Length: > 0 } artifacts)
        {
            await File.WriteAllBytesAsync(Path.Combine(artifacts, "provider.zip"), providerBytes);
            await File.WriteAllBytesAsync(Path.Combine(artifacts, "research.zip"), consumerBytes);
            await File.WriteAllBytesAsync(Path.Combine(artifacts, "trusted-index.json"), index);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "provider-manifest.json"), JsonSerializer.Serialize(providerManifest));
        }
        var service = new PluginPackageService(Path.Combine(root, "research-packages"), new ReviewPackageHttp { Bytes = providerBytes },
            TrustedPluginDistribution.Read(index, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(index))));
        await service.StageInstallAsync(providerManifest);
        await service.StageDistributionInstallAsync(manifest, consumerBytes);
        var http = new ResearchHttp();
        var clock = new ResearchClock();
        var logs = new List<string>();
        var settings = new AppSettings { WebSearchUseModelSearch = false, WebSearchSources = "Synthetic|https://example.com/search?q={q}", WebSearchMaxResults = 3 };
        var fixtureContext = new System.Runtime.Loader.AssemblyLoadContext("research-provider-fixture", isCollectible: true);
        fixtureContext.Resolving += (_, name) => new[] { typeof(IWebSearch).Assembly, typeof(IPluginContribution).Assembly, typeof(IBotPlugin).Assembly }
            .SingleOrDefault(assembly => assembly.GetName().Name == name.Name);
        var fixtureAssembly = fixtureContext.LoadFromStream(new MemoryStream(payloads.Consumer));
        var search = (IWebSearch)Activator.CreateInstance(fixtureAssembly.GetType("BotAgent.Services.Net.WebSearchService")!,
            ResearchFixture.Model(http, () => settings), new WebPageTransport(http, () => false),
            ResearchFixture.Options(() => settings), (Action<string>)(_ => { }), null)!;
        var providerContext = new PluginContext(new ResearchServices(clock, logs.Add, search));
        var consumerContext = new PluginContext(new ResearchServices(clock, logs.Add));
        var registry = new PluginManager(_ => { });
        Task<PluginPackageManifest?> StartConsumer() => service.ApplyAtStartupAsync(manifest.Id, consumerContext, registry, true,
            resolveSettings: _ => JsonSerializer.Serialize(new { providerId = providerManifest.Id }));
        try
        {
            try { await StartConsumer(); throw new InvalidOperationException("missing_dependency_accepted"); }
            catch (InvalidOperationException e) when (e.Message == "plugin_dependency_unavailable") { checks++; }
            await service.ApplyAtStartupAsync(providerManifest.Id, providerContext, registry, true);
            await StartConsumer();
            Check(http.Calls == 0 && clock.Reads == 0, "initialization performs no network or clock work");
            var research = registry.GetContribution<IResearchUseCase>(manifest.Id);
            var diagnostics = registry.GetContribution<IResearchDiagnostics>(manifest.Id);
            Check(research.IsReady && research.TryBeginSearch("synthetic:a", 30, out _), "actual research contribution starts first cooldown");
            Check(!research.TryBeginSearch("synthetic:a", 30, out var reason) && reason.Contains("30s")
                && research.TryBeginSearch("synthetic:b", 30, out _), "cooldown keeps original reason and source isolation");
            clock.Advance(TimeSpan.FromSeconds(30));
            Check(research.TryBeginSearch("synthetic:a", 30, out _), "cooldown expires at the original boundary");
            var result = await research.SearchAsync("synthetic:a", "synthetic query", default);
            Check(result is { HasContent: true } && http.Calls == 1, "actual HTTP search implementation called once through independent DLLs");
            var note = research.TakeNote("synthetic:a");
            Check(note == result!.Describe() && research.TakeNote("synthetic:a") is null && research.TakeNote("synthetic:b") is null, "note consumed once in its source");
            research.KeepNote("synthetic:a", note, "synthetic guard");
            Check(research.TakeNote("synthetic:a") == note && logs.Any(x => x.Contains("留着下一轮说")), "unused note and existing diagnostic preserved");
            var page = await research.ReadPageAsync("synthetic:a", "https://example.com/page", default);
            Check(page is { Length: > 40 } && research.TakeNote("synthetic:a")!.Contains(page) && http.Calls == 2, "actual page extraction adds one note");
            var denied = await research.ReadPageAsync("synthetic:a", "file:///synthetic", default);
            Check(denied is null && http.Calls == 2 && research.TakeNote("synthetic:a")!.Contains("如实说没读到"), "unsafe URL makes no network call");
            await diagnostics.TestSearchAsync("synthetic diagnostic", default);
            Check(http.Calls == 3 && research.TakeNote("synthetic:a") is null, "diagnostic does not add conversation notes");
            registry.SetEnabled(providerManifest.Id, false);
            Reject(() => research.TakeNote("synthetic:a"), "plugin_contribution_unavailable");
            registry.SetEnabled(providerManifest.Id, true);
            http.Block = true;
            var pending = research.SearchAsync("synthetic:a", "synthetic slow", default);
            await http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var removal = registry.RemoveAsync(providerManifest.Id);
            try
            {
                Check(!removal.IsCompleted, "provider removal drains actual asynchronous HTTP call");
                Reject(() => research.TakeNote("synthetic:a"), "plugin_contribution_unavailable");
            }
            finally { http.Release.TrySetResult(); }
            Check((await pending)!.HasContent, "entered call completes once while retiring");
            await removal;
            await registry.RemoveAsync(manifest.Id);
            http.Block = false;
            await service.ApplyAtStartupAsync(providerManifest.Id, providerContext, registry, true);
            await StartConsumer();
            Reject(() => research.TakeNote("synthetic:a"), "plugin_contribution_unavailable");
            Check(registry.GetContribution<IResearchUseCase>(manifest.Id).TakeNote("synthetic:a") is null, "retired notes do not leak into reactivation");
            Check(Type.GetType("BotAgent.Services.Net.IResearchUseCase, BotAgent.Engine") == typeof(IResearchUseCase)
                && Type.GetType("BotAgent.Services.Net.IWebSearch, BotAgent.Engine") == typeof(IWebSearch)
                && typeof(IResearchUseCase).Assembly.GetName().Name == "BotAgent.Research.Contracts", "Engine identities forward to independent research contracts");
        }
        finally { await registry.StopAllAsync(CancellationToken.None); fixtureContext.Unload(); }
        return checks;
    }

    private static async Task<(byte[] Provider, byte[] Consumer)> BuildResearchPackages(string directory)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "BotAgent.slnx"))) repo = repo.Parent;
        if (repo is null) throw new InvalidOperationException("research_source_missing");
        var consumer = Path.Combine(directory, "consumer"); var provider = Path.Combine(directory, "provider");
        Directory.CreateDirectory(consumer); Directory.CreateDirectory(provider);
        var sdk = Path.Combine(directory, "sdk"); Directory.CreateDirectory(sdk);
        File.Copy(typeof(IBotPlugin).Assembly.Location, Path.Combine(sdk, "BotAgent.Core.dll"));
        File.Copy(typeof(IPluginContribution).Assembly.Location, Path.Combine(sdk, "BotAgent.Plugin.Contracts.dll"));
        File.Copy(typeof(IResearchUseCase).Assembly.Location, Path.Combine(sdk, "BotAgent.Research.Contracts.dll"));
        foreach (var file in Directory.EnumerateFiles(OfficialMarketplaceTestHelper.PluginBackendSourceDirectory("preset.feature.websearch")))
            if (Path.GetExtension(file) is ".cs" or ".csproj") File.Copy(file, Path.Combine(consumer, Path.GetFileName(file)));
        var consumerProjectPath = Path.Combine(consumer, "BotAgent.Package.WebSearch.csproj");
        var consumerProject = XDocument.Load(consumerProjectPath);
        consumerProject.Descendants("Version").Single().Value = "1.0.0";
        consumerProject.Save(consumerProjectPath);
        await File.WriteAllTextAsync(Path.Combine(provider, "Transport.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Synthetic.Research.Transport</AssemblyName><Version>1.0.0</Version></PropertyGroup><ItemGroup><Reference Include=\"BotAgent.Core\" HintPath=\"$(PluginSdkPath)/BotAgent.Core.dll\" Private=\"false\" /><Reference Include=\"BotAgent.Plugin.Contracts\" HintPath=\"$(PluginSdkPath)/BotAgent.Plugin.Contracts.dll\" Private=\"false\" /><Reference Include=\"BotAgent.Research.Contracts\" HintPath=\"$(PluginSdkPath)/BotAgent.Research.Contracts.dll\" Private=\"false\" /></ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(provider, "TransportPlugin.cs"), """
            using BotAgent.Domain.Plugins;
            using BotAgent.Services.Net;
            public sealed class TransportPlugin : IBotPlugin, IPluginContributionSource
            {
                private IWebSearch? _search;
                public string Id => "probe.research.transport";
                public string Name => Id;
                public string Version => "1.0.0";
                public string Description => "synthetic adapter";
                public PluginCategory Category => PluginCategory.Management;
                public Task InitializeAsync(PluginContext context, CancellationToken ct)
                { _search = (IWebSearch)context.Services.GetService(typeof(IWebSearch))!; return Task.CompletedTask; }
                public object? GetContribution(Type contract) => contract == typeof(IWebSearch) ? _search : null;
                public Task ShutdownAsync(CancellationToken ct) { _search = null; return Task.CompletedTask; }
            }
            """);
        foreach (var item in new[] { (provider, "Transport.csproj"), (consumer, "BotAgent.Package.WebSearch.csproj") })
        {
            File.Copy(Path.Combine(repo.FullName, "Directory.Build.props"), Path.Combine(item.Item1, "Directory.Build.props"));
            await File.WriteAllTextAsync(Path.Combine(item.Item1, "NuGet.Config"), "<configuration><packageSources><clear/></packageSources></configuration>");
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = item.Item1, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "build", item.Item2, "-c", "Release", "--nologo", "-o", "out", "-p:PluginSdkPath=" + sdk }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var diagnostics = await output + await error;
            await File.WriteAllTextAsync(Path.Combine(item.Item1, "build.log"), diagnostics);
            await File.WriteAllTextAsync(Path.Combine(item.Item1, "build-command.json"), JsonSerializer.Serialize(new
            {
                executable = "dotnet", arguments = start.ArgumentList.ToArray(), exitCode = process.ExitCode,
                sdkSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(sdk, "BotAgent.Core.dll"))))
            }));
            if (process.ExitCode != 0) throw new InvalidOperationException("research_independent_build_failed: " + diagnostics);
        }
        if (Environment.GetEnvironmentVariable("BOTAGENT_IT_RESEARCH_EVIDENCE") is { Length: > 0 } evidence)
        {
            if (Directory.Exists(evidence)) throw new InvalidOperationException("research_evidence_exists");
            Directory.CreateDirectory(evidence);
            foreach (var side in new[] { "consumer", "provider" })
                foreach (var file in Directory.EnumerateFiles(Path.Combine(directory, side), "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(directory, file);
                    if (relative.Split(Path.DirectorySeparatorChar).Contains("obj")) continue;
                    var target = Path.Combine(evidence, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
                }
            File.Copy(typeof(IBotPlugin).Assembly.Location, Path.Combine(evidence, "BotAgent.Core.dll"));
            File.Copy(typeof(IPluginContribution).Assembly.Location, Path.Combine(evidence, "BotAgent.Plugin.Contracts.dll"));
            File.Copy(typeof(IResearchUseCase).Assembly.Location, Path.Combine(evidence, "BotAgent.Research.Contracts.dll"));
        }
        return (await File.ReadAllBytesAsync(Path.Combine(provider, "out/Synthetic.Research.Transport.dll")), await File.ReadAllBytesAsync(Path.Combine(consumer, "out/BotAgent.Package.WebSearch.dll")));
    }

    private sealed class ResearchServices(IClock clock, Action<string> log, IWebSearch? search = null) : IServiceProvider
    { public object? GetService(Type type) => type == typeof(IClock) ? clock : type == typeof(Action<string>) ? log : type == typeof(IWebSearch) ? search : null; }
    private sealed class ResearchClock : IClock
    {
        private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public int Reads { get; private set; }
        public DateTimeOffset Now { get { Reads++; return _now; } }
        public DateTimeOffset UtcNow => Now;
        public DateTime LocalDateTime => Now.DateTime;
        public long TickCount => 0;
        public void Advance(TimeSpan time) => _now += time;
        public Task Delay(TimeSpan time, CancellationToken ct = default) => Task.Delay(time, ct);
        public Task Delay(int time, CancellationToken ct = default) => Task.Delay(time, ct);
    }
    private sealed class ResearchHttp : IHttpFetcher
    {
        public TimeSpan Timeout => TimeSpan.FromSeconds(30);
        public int Calls { get; private set; }
        public bool Block { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            Calls++;
            if (Block) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            var body = request.RequestUri!.AbsolutePath == "/page" ? "<html><p>" + new string('x', 80) + "</p></html>"
                : "{\"results\":[{\"title\":\"Synthetic result\",\"url\":\"https://example.com/result\",\"content\":\"synthetic snippet\"}]}";
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => SendAsync(request, ct);
        public Task<HttpResponseMessage> GetAsync(Uri uri, HttpCompletionOption option, CancellationToken ct = default) => SendAsync(new(HttpMethod.Get, uri), ct);
        public Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken ct = default) => GetAsync(uri, HttpCompletionOption.ResponseContentRead, ct);
        public Task<HttpResponseMessage> GetAsync(string uri, CancellationToken ct = default) => GetAsync(new Uri(uri), ct);
        public Task<HttpResponseMessage> GetAsync(string uri, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(new Uri(uri), option, ct);
        public Task<HttpResponseMessage> PostAsync(string uri, HttpContent content, CancellationToken ct = default) => SendAsync(new(HttpMethod.Post, uri) { Content = content }, ct);
    }
}
