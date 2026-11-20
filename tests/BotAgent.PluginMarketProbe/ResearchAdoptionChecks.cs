extern alias StorageModule;

using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Plugins.Net;
using BotAgent.Services.Links;
using BotAgent.Services.Net;
using BotAgent.Services.Plugins;
using BotAgent.Services.Resilience;

internal partial class Program
{
    internal static void BindResearchRuntime(object runtime)
    {
        var services = (IServiceProvider)Activator.CreateInstance(typeof(PluginPackageService).Assembly
            .GetType("BotAgent.Host.ResearchPluginServices")!, new AdoptionModel(), new AdoptionPages(),
            new ToolCircuitBreaker("synthetic-research"), (Func<ResearchOptions>)(() =>
                new(true, true, "", 3, 30, 5, 2000, true, 5, 3)), Array.Empty<IDisposable>())!;
        runtime.GetType().GetMethod("BindServices", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime, [services]);
    }

    internal static async Task<int> RunResearchAdoptionChecks(string root)
    {
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        const string id = "preset.feature.websearch";
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var host = typeof(PluginPackageService).Assembly;
        var runtimeType = host.GetType("BotAgent.Host.PluginRuntime")!;
        IAsyncDisposable Runtime(string name) => (IAsyncDisposable)runtimeType.GetConstructors(hidden).Single()
            .Invoke([Path.Combine(root, name), (Action<string>)(_ => { })]);
        PluginManager Registry(object runtime) => (PluginManager)runtimeType.GetProperty("Registry", hidden)!.GetValue(runtime)!;
        PluginPackageService Packages(object runtime) => (PluginPackageService)runtimeType.GetProperty("Packages", hidden)!.GetValue(runtime)!;
        Task Start(object runtime) => (Task)runtimeType.GetMethod("StartAsync", hidden)!.Invoke(runtime, [CancellationToken.None])!;
        void Bind(object runtime, IServiceProvider services) => runtimeType.GetMethod("BindServices", hidden)!.Invoke(runtime, [services]);
        var flags = new StorageModule::BotAgent.Storage.PluginStore();
        var previous = flags.LoadPluginStates().GetValueOrDefault(id, true);
        var model = new AdoptionModel();
        var pages = new AdoptionPages();
        var lifetime = new AdoptionLifetime();
        var optionReads = 0;
        var options = new ResearchOptions(true, true, "synthetic|https://example.com/search?q={q}", 3, 30, 5, 2000, true, 5, 3);
        var circuit = new ToolCircuitBreaker("synthetic-search", hardTimeout: TimeSpan.FromMilliseconds(300));
        IServiceProvider Services() => (IServiceProvider)Activator.CreateInstance(host.GetType("BotAgent.Host.ResearchPluginServices")!,
            model, pages, circuit, (Func<ResearchOptions>)(() => { optionReads++; return options; }), new IDisposable[] { lifetime })!;
        try
        {
            flags.SetPluginEnabled(id, true);
            await using (var runtime = Runtime("research-default"))
            {
                var registry = Registry(runtime);
                var facade = new ResearchUseCase(registry);
                Check(registry.GetAll().All(p => p.Id != id) && !facade.IsReady, "research has no legacy registration before bundle adoption");
                var services = Services();
                Bind(runtime, services);
                try { Bind(runtime, services); throw new Exception("duplicate_binding_accepted"); }
                catch (TargetInvocationException ex) when (ex.InnerException?.Message == "plugin_services_already_bound") { checks++; }
                await Start(runtime);
                Check(model.Calls == 0 && pages.Calls == 0 && optionReads == 0, "actual default package initialization does no model, page or options work");
                var state = (await Packages(runtime).GetInstalledAsync()).Single(p => p.Id == id);
                Check(state.Active is { Control: true } && state.Pending is null && state.Active.EntryAssembly == "BotAgent.Package.WebSearch.dll"
                    && state.Active.DownloadUrl == "host-distribution://" + id + "/1.0.0", "host adopts the actual default research ZIP");
                Check(state.Active!.Permissions is { } permissions && permissions.Order().SequenceEqual(new[] { "model.grounded-text", "network.public-read", "tools.execute" }), "default research declares its exact resource permissions");
                Check(registry.GetAll().Count(p => p.Id == id) == 1 && facade.IsReady && registry.IsControlReady(id), "one research owner serves business and control");
                using (var metadata = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "research-default", id, "installed.json"))))
                    Check(metadata.RootElement.GetProperty("minimumReaderVersion").GetInt32() == 11, "default owner configuration requires reader eleven");
                Check(Type.GetType("BotAgent.Services.Links.ILinkPreviewer, BotAgent.Engine") == typeof(ILinkPreviewer), "old Engine link contract identity forwards to Core");
                Check(AssemblyLoadContext.Default.Assemblies.All(a => a.GetName().Name != "BotAgent.Package.WebSearch")
                    && host.GetType("BotAgent.Services.Net.WebSearchService") is null
                    && host.GetType("BotAgent.Packages.WebSearch.ResearchCoordinator") is null, "host loads no research implementation into Default or its own assembly");
                Check(facade.TryBeginSearch("synthetic:research", 30, out _)
                    && !facade.TryBeginSearch("synthetic:research", 30, out var reason) && reason.Contains("30s"), "host entry uses package cooldown state");
                var result = await facade.SearchAsync("synthetic:research", "synthetic query");
                Check(result is { HasContent: true, Answer: "synthetic answer" } && model.Calls == 1 && pages.Calls == 0, "host entry calls actual package model search exactly once");
                Check(result!.Hits.Count == 1 && result.Hits[0].Url.Length == 0, "package filters duplicated source titles and hides source URLs");
                var note = facade.TakeNote("synthetic:research");
                Check(note == result.Describe() && facade.TakeNote("synthetic:research") is null, "package notes are consumed once");
                facade.KeepNote("synthetic:research", note, "synthetic guard");
                Check(facade.TakeNote("synthetic:research") == note, "host guard restores unused package note");
                options = options with { WebSearchUseModelSearch = false };
                var source = await facade.SearchAsync("synthetic:source", "synthetic source");
                Check(source is { HasContent: true } && source.Hits[0].Title == "synthetic source" && model.Calls == 1 && pages.Calls == 1, "live options select package source parsing without model I/O");
                var page = await facade.ReadPageAsync("synthetic:page", "https://example.com/page");
                Check(page is { Length: > 40 } && facade.TakeNote("synthetic:page")!.Contains(page), "host page entry uses package extraction and notes");
                var beforeDenied = pages.Calls;
                Check(await facade.ReadPageAsync("synthetic:denied", "file:///synthetic") is null && pages.Calls == beforeDenied, "package page entry honors host URL rejection");
                var preview = await facade.DescribeAsync(["https://example.com/preview"], default);
                Check(preview?.Contains("synthetic title") == true && pages.LastLimit == 256 * 1024, "host link entry executes bounded package preview");
                var cachedCalls = pages.Calls;
                Check(await facade.DescribeAsync(["https://example.com/preview"], default) == preview && pages.Calls == cachedCalls, "package owns preview caching");
                var old = registry.GetContribution<IResearchUseCase>(id);
                registry.SetEnabled(id, false);
                var beforeDisabled = (model.Calls, pages.Calls);
                Check(!facade.IsReady && !facade.TryBeginSearch("synthetic:off", 0, out _), "disabled host facade reports unavailable");
                Check(await facade.SearchAsync("synthetic:off", "synthetic") is null
                    && await facade.ReadPageAsync("synthetic:off", "https://example.com/page") is null
                    && await facade.DescribeAsync(["https://example.com/other"], default) is null, "disabled host business entries perform no work");
                facade.AddNote("synthetic:off", "synthetic"); facade.KeepNote("synthetic:off", "synthetic", "guard");
                Check(facade.TakeNote("synthetic:off") is null && (model.Calls, pages.Calls) == beforeDisabled, "disabled note paths do not retain new work");
                options = options with { WebSearchUseModelSearch = true };
                Check((await facade.TestSearchAsync("synthetic diagnostic", default)).HasContent
                    && model.Calls == beforeDisabled.Item1 + 1, "disabled research keeps approved control search usable");
                Check((await facade.TestReadPageAsync("https://example.com/page", default)).Text is { Length: > 40 }, "disabled research keeps control read usable");
                registry.SetEnabled(id, true);
                Check(facade.TakeNote("synthetic:off") is null, "disabled business notes never reappear");
                facade.AddNote("synthetic:retired", "old generation");
                await registry.RemoveAsync(id);
                Check(!facade.IsReady && !(await facade.TestSearchAsync("synthetic", default)).HasContent, "removed package closes business and diagnostic entries");
                var context = new PluginContext(new AdoptionContext(services));
                await Packages(runtime).ApplyAtStartupAsync(id, context, registry, true);
                try { old.TakeNote("synthetic:retired"); throw new Exception("stale_handle_revived"); }
                catch (InvalidOperationException ex) when (ex.Message == "plugin_contribution_unavailable") { checks++; }
                Check(facade.IsReady && facade.TakeNote("synthetic:retired") is null
                    && (await facade.SearchAsync("synthetic:new", "synthetic"))!.HasContent, "same host facade resolves the newly published generation");
                var reloadCalls = pages.Calls;
                await facade.DescribeAsync(["https://example.com/preview"], default);
                Check(pages.Calls == reloadCalls + 1, "new generation does not inherit retired preview cache");
                await Packages(runtime).StageUninstallAsync(id);
                Check(lifetime.Disposals == 0, "host resources remain owned while runtime is active");
            }
            Check(lifetime.Disposals == 1, "actual host service provider disposes resources after runtime stop");
            await using (var runtime = Runtime("research-default"))
            {
                await Start(runtime);
                var state = (await Packages(runtime).GetInstalledAsync()).Single(p => p.Id == id);
                Check(state.Active is null && !Registry(runtime).IsControlReady(id), "default research respects explicit uninstall on restart");
            }
            await using (var runtime = Runtime("research-no-resources"))
            {
                await Start(runtime);
                var facade = new ResearchUseCase(Registry(runtime));
                Check(!facade.IsReady && !(await facade.TestSearchAsync("synthetic", default)).HasContent
                    && Registry(runtime).IsControlReady("preset.feature.token-quota"), "missing research capabilities fail closed without breaking independent management");
                try { Bind(runtime, Services()); throw new Exception("late_binding_accepted"); }
                catch (TargetInvocationException ex) when (ex.InnerException?.Message == "plugin_services_already_bound") { checks++; }
            }
            flags.SetPluginEnabled(id, false);
            var coldCalls = (model.Calls, pages.Calls);
            await using (var runtime = Runtime("research-disabled"))
            {
                Bind(runtime, Services()); await Start(runtime);
                Check(!Registry(runtime).IsReady(id) && Registry(runtime).IsControlReady(id)
                    && (model.Calls, pages.Calls) == coldCalls, "disabled first adoption starts only control without I/O");
                Check((await new ResearchUseCase(Registry(runtime)).TestSearchAsync("synthetic cold control", default)).HasContent, "cold disabled package diagnostics execute the packaged implementation");
            }
            flags.SetPluginEnabled(id, true);
            await using (var runtime = Runtime("research-drain"))
            {
                Bind(runtime, Services()); await Start(runtime);
                using var callbackRelease = new ManualResetEventSlim();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var result = new TaskCompletionSource<GroundedTextResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                model.Handler = (_, token) =>
                {
                    token.Register(() => { callbackEntered.TrySetResult(); callbackRelease.Wait(); });
                    entered.TrySetResult(); return result.Task;
                };
                Task? stopping = null;
                try
                {
                    var search = new ResearchUseCase(Registry(runtime)).SearchAsync("synthetic:late", "synthetic slow");
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Check((await search.WaitAsync(TimeSpan.FromSeconds(3)))?.Error == "tool_timeout", "actual package returns hard timeout before late model work");
                    await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var disposals = lifetime.Disposals;
                    stopping = runtime.DisposeAsync().AsTask();
                    await Task.Delay(50);
                    Check(!stopping.IsCompleted && lifetime.Disposals == disposals, "host shutdown retains resources through timed-out package work");
                    result.TrySetResult(new GroundedTextResult("synthetic late", [], []));
                    await Task.Delay(50);
                    Check(!stopping.IsCompleted && lifetime.Disposals == disposals, "host shutdown still waits for package cancellation callbacks");
                    callbackRelease.Set();
                    await stopping.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(lifetime.Disposals == disposals + 1, "host disposes owned resources only after both late lifetimes end");
                }
                finally { result.TrySetResult(new GroundedTextResult("synthetic", [], [])); callbackRelease.Set(); if (stopping is not null) await stopping; }
            }
            using var deps = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.ChangeExtension(host.Location, ".deps.json")));
            Check(deps.RootElement.GetProperty("libraries").EnumerateObject().All(p => !p.Name.StartsWith("BotAgent.Package.WebSearch/", StringComparison.Ordinal)), "research build dependency does not become a host runtime reference");
        }
        finally { flags.SetPluginEnabled(id, previous); }
        return checks;
    }

    private sealed class AdoptionContext(IServiceProvider services) : IServiceProvider
    {
        private readonly ResearchClock _clock = new();
        public object? GetService(Type type) => type == typeof(IClock) ? _clock : services.GetService(type);
    }
    private sealed class AdoptionLifetime : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Disposals++;
    }
    private sealed class AdoptionModel : IGroundedTextModel
    {
        internal int Calls;
        internal Func<GroundedTextRequest, CancellationToken, Task<GroundedTextResult>>? Handler;
        public Task<GroundedTextResult> GenerateAsync(GroundedTextRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Handler?.Invoke(request, ct) ?? Task.FromResult(new GroundedTextResult("synthetic answer",
                [new("synthetic source", "https://example.com/source"), new("synthetic source", "https://example.com/duplicate")], ["synthetic query"]));
        }
    }
    private sealed class AdoptionPages : IWebPageTransport
    {
        internal int Calls;
        internal int? LastLimit;
        public bool TryValidate(string? url, out Uri uri, out string reason)
        {
            reason = "synthetic_url_rejected";
            return Uri.TryCreate(url, UriKind.Absolute, out uri!) && uri.Scheme == "https" && uri.Host == "example.com";
        }
        public Task<WebPageResponse> GetAsync(WebPageRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); LastLimit = request.ReadLimitBytes;
            var body = request.Url.AbsolutePath == "/search"
                ? "{\"results\":[{\"title\":\"synthetic source\",\"url\":\"https://example.com/result\",\"content\":\"synthetic snippet\"}]}"
                : "<html><head><title>synthetic title</title><meta name=\"description\" content=\"synthetic description\"></head><body><p>synthetic page body with enough visible text to exercise package extraction and notes.</p></body></html>";
            return Task.FromResult(new WebPageResponse(request.Url, 200, Encoding.UTF8.GetBytes(body)));
        }
    }
}
