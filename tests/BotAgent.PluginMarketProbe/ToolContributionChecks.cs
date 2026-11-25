extern alias StorageModule;

using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Tools;
using BotAgent.Services.Net;
using BotAgent.Services.Plugins;
using BotAgent.Services.Resilience;
using BotAgent.Services.Tools;

internal partial class Program
{
    internal static async Task<int> RunToolContributionChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        async Task Reject(Func<Task> action, string code)
        {
            try { await action(); } catch (InvalidOperationException error) when (error.Message == code) { checks++; return; }
            throw new InvalidOperationException("expected_" + code);
        }
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        const string researchId = "preset.feature.websearch";
        string[] officialToolIds =
        [
            researchId, "preset.feature.documents", "preset.feature.digest",
            "preset.feature.knowledge", "preset.feature.scheduler", "preset.feature.personal-todo"
        ];
        var host = typeof(PluginPackageService).Assembly;
        var runtimeType = host.GetType("BotAgent.Host.PluginRuntime")!;
        var flags = new StorageModule::BotAgent.Storage.PluginStore();
        var states = flags.LoadPluginStates();
        var previousFlags = officialToolIds.ToDictionary(id => id, id => states.GetValueOrDefault(id, true));
        var model = new AdoptionModel(); var pages = new AdoptionPages();
        var services = (IServiceProvider)Activator.CreateInstance(host.GetType("BotAgent.Host.ResearchPluginServices")!,
            model, pages, new ToolCircuitBreaker("synthetic-tools"),
            (Func<ResearchOptions>)(() => new(true, true, "", 3, 30, 5, 2000, true, 5, 3)), Array.Empty<IDisposable>())!;
        try
        {
            var toolNativeRoot = Path.Combine(root, "tool-native");
            OfficialMarketplaceTestHelper.StageOfficialPackages(toolNativeRoot, officialToolIds, enableInStore: true);
            await using var runtime = (IAsyncDisposable)runtimeType.GetConstructors(hidden).Single()
                .Invoke([toolNativeRoot, (Action<string>)(_ => { })]);
            runtimeType.GetMethod("BindServices", hidden)!.Invoke(runtime, [services]);
            await (Task)runtimeType.GetMethod("StartAsync", hidden)!.Invoke(runtime, [CancellationToken.None])!;
            var registry = (PluginManager)runtimeType.GetProperty("Registry", hidden)!.GetValue(runtime)!;
            var tools = registry.GetTools();
            var researchTools = tools.Where(tool => tool.ProviderId == researchId).ToArray();
            Check(tools.Length == 16
                && researchTools.Length == 2
                && researchTools.All(tool => tool.Definition.Spec.Executor == "research.actions" && tool.Definition.Spec.Origin == ToolOrigin.Plugin)
                && tools.Any(tool => tool.ProviderId == "preset.feature.documents" && tool.Definition.Spec.Id == "doc.read" && tool.Definition.Spec.Executor == "documents.actions")
                && tools.Any(tool => tool.ProviderId == "preset.feature.digest" && tool.Definition.Spec.Id == "chat.digest" && tool.Definition.Spec.Executor == "digest.actions")
                && tools.Count(tool => tool.ProviderId == "preset.feature.knowledge" && tool.Definition.Spec.Executor == "knowledge.actions") == 4
                && tools.Count(tool => tool.ProviderId == "preset.feature.scheduler" && tool.Definition.Spec.Executor == "scheduler.actions") == 4
                && tools.Any(tool => tool.ProviderId == "preset.feature.scheduler" && tool.Definition.Spec.Id == "reminder.create")
                && tools.Count(tool => tool.ProviderId == "preset.feature.personal-todo" && tool.Definition.Spec.Executor == "todo.actions") == 4
                && tools.Any(tool => tool.ProviderId == "preset.feature.personal-todo" && tool.Definition.Spec.Id == "todo.complete"),
                "actual default packages supply research, documents, digest, knowledge, scheduler, and personal-todo tools with isolated executors");
            Check(model.Calls == 0 && pages.Calls == 0, "tool discovery performs no model or page IO");
            var search = tools.Single(tool => tool.Definition.Spec.Id == "web.search");
            var read = tools.Single(tool => tool.Definition.Spec.Id == "web.read");
            async Task Bad(PluginManager.ToolBinding tool, JsonObject arguments, string code)
            {
                var result = await tool.ExecuteAsync("synthetic:tools", new(tool.Definition.Spec.Id, arguments));
                Check(!result.Ok && result.ReasonCode == code && result.ModelText == "", "argument rejection: " + code);
            }
            await Bad(search, new() { ["q"] = "synthetic" }, "unknown_field");
            await Bad(search, new(), "missing_required");
            await Bad(search, new() { ["query"] = "x" }, "bad_length");
            await Bad(search, new() { ["query"] = new string('x', 121) }, "bad_length");
            await Bad(search, new() { ["query"] = 42 }, "bad_type");
            await Bad(read, new() { ["url"] = new JsonObject() }, "bad_type");
            await Bad(read, new() { ["url"] = "file://synthetic" }, "bad_scheme");
            Check((await search.ExecuteAsync("synthetic:tools", new("web.read", new() { ["url"] = "https://example.com" }))).ReasonCode
                == "unknown_tool", "binding cannot dispatch a different ID");
            Check(model.Calls == 0 && pages.Calls == 0, "all rejected requests keep IO counters at zero");
            Check((await search.ExecuteAsync("synthetic:tools", new("web.search", null!))).ReasonCode == "missing_required",
                "missing argument object is rejected before package code");
            var found = await search.ExecuteAsync("synthetic:tools", new("web.search", new() { ["query"] = "synthetic query" }));
            var facade = new ResearchUseCase(registry);
            Check(found.Ok && found.ModelText!.Contains("synthetic answer") && found.AuditDetail == "hits=1"
                && facade.TakeNote("synthetic:tools") == found.ModelText && facade.TakeNote("synthetic:tools") is null
                && model.Calls == 1, "package tool performs one model call and retains one-shot notes");
            var page = await read.ExecuteAsync("synthetic:tools", new("web.read", new() { ["url"] = "https://example.com/page" }));
            Check(page.Ok && page.ModelText!.StartsWith("页面正文：\n") && pages.Calls == 1
                && page.AuditDetail!.StartsWith("chars=") && facade.TakeNote("synthetic:tools") is not null,
                "package page tool uses bounded transport and preserves notes");
            registry.SetEnabled(researchId, false);
            Check(registry.GetTools().All(t => t.ProviderId != researchId) && registry.GetTools().Length == 14 && !search.IsReady,
                "disabled package has no visible tools while sibling tool packages stay online");
            await Reject(() => search.ExecuteAsync("synthetic:tools", new("web.search", new() { ["query"] = "synthetic query" })),
                "plugin_contribution_unavailable");
            registry.SetEnabled(researchId, true);
            Check(registry.GetTools().Length == 16 && registry.GetTools().Count(t => t.ProviderId == researchId) == 2,
                "re-enabling the same initialized instance restores its declarations");
            await registry.RemoveAsync(researchId);
            Check(registry.GetTools().All(t => t.ProviderId != researchId) && registry.GetTools().Length == 14 && !search.IsReady,
                "uninstall removes directory and closes captured bindings");
            Check(AssemblyLoadContext.Default.Assemblies.All(assembly => assembly.GetName().Name is not ("BotAgent.Package.WebSearch" or "BotAgent.Package.Documents" or "BotAgent.Package.Digest" or "BotAgent.Package.Knowledge" or "BotAgent.Package.Scheduler" or "BotAgent.Package.PersonalTodo")),
                "tool use never loads package business into Default");
        }
        finally
        {
            foreach (var pair in previousFlags) flags.SetPluginEnabled(pair.Key, pair.Value);
        }

        var bytes = ReviewZip("tools.dll", await File.ReadAllBytesAsync(typeof(SyntheticToolPlugin).Assembly.Location));
        var manifest = ReviewManifest(bytes, "test.tools", "1.0.0", "tools.dll", nameof(SyntheticToolPlugin)) with { ToolApiVersion = 1 };
        var http = new ReviewPackageHttp { Bytes = bytes };
        var packages = new PluginPackageService(Path.Combine(root, "tool-packages"), http);
        var events = new List<string>();
        var definitions = new[] { Definition("synthetic.echo") };
        var calls = 0;
        Func<string, ToolCall, CancellationToken, Task<ToolOutcome>> run = (_, call, _) =>
        { calls++; return Task.FromResult(ToolOutcome.Success(call.Arguments["text"]!.GetValue<string>(), "synthetic")); };
        AssemblyLoadContext? loadedContext = null;
        var context = new PluginContext(new ToolFixtureServices(() => definitions, (key, call, ct) => run(key, call, ct),
            events.Add, assembly => loadedContext = AssemblyLoadContext.GetLoadContext(assembly)));
        var manager = new PluginManager(_ => { });
        Task Start() => packages.ApplyAtStartupAsync(manifest.Id, context, manager, true);
        try
        {
            await packages.StageInstallAsync(manifest); await Start();
            Check(loadedContext is { IsCollectible: true }, "synthetic unregistered ID uses an actual collectible DLL");
            var binding = manager.GetTools().Single();
            Check(binding.ProviderId == manifest.Id && binding.Definition.Spec.Id == "synthetic.echo",
                "tool discovery needs no host ID branch");
            definitions[0] = Definition("synthetic.changed");
            Check(binding.Definition.Spec.Id == "synthetic.echo" && manager.GetTools().Single().Definition.Spec.Id == "synthetic.echo",
                "publication captures metadata independently of later package array mutation");
            Check((await binding.ExecuteAsync("synthetic:key", new("synthetic.echo", new() { ["text"] = "synthetic output" }))).ModelText
                == "synthetic output" && calls == 1, "execution keeps the captured executor even when declarations change");
            var metadataPath = Path.Combine(root, "tool-packages", manifest.Id, "installed.json");
            var before = await File.ReadAllBytesAsync(metadataPath);
            definitions = [Definition("synthetic.echo"), Definition("synthetic.echo")];
            await Reject(Start, "plugin_tool_conflict");
            var after = await File.ReadAllBytesAsync(metadataPath);
            Check(before.SequenceEqual(after) && binding.IsReady,
                "invalid replacement preserves both persisted release and original generation");
            definitions = [Definition("synthetic.echo") with { HttpUrlField = "missing" }];
            await Reject(Start, "plugin_tools_invalid");
            Check(binding.IsReady, "bad argument schema cannot replace the old executor");
            definitions = [Definition("synthetic.echo") with { Fields = [new("text", true, 20, 2)] }];
            await Reject(Start, "plugin_tools_invalid");
            definitions = [Definition("synthetic.echo") with { Spec = Definition("synthetic.echo").Spec with { Id = "invalid tool" } }];
            await Reject(Start, "plugin_tools_invalid");
            definitions = [Definition("synthetic.echo")];
            var competitor = new ToolOwnerPlugin("synthetic.other", Definition("synthetic.echo"));
            await Reject(() => manager.ReplaceAsync(competitor, true), "plugin_tool_conflict");
            Check(!manager.IsReady(competitor.Id) && binding.IsReady, "duplicate owner is rejected before publication");
            manager.SetEnabled(manifest.Id, false);
            await Reject(() => manager.ReplaceAsync(competitor, true), "plugin_tool_conflict");
            manager.SetEnabled(manifest.Id, true);

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            run = async (_, call, _) => { entered.TrySetResult(); await release.Task; return ToolOutcome.Success(call.Arguments["text"]!.GetValue<string>()); };
            var input = new JsonObject { ["text"] = "original" };
            var active = binding.ExecuteAsync("synthetic:key", new("synthetic.echo", input));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); input["text"] = "changed";
            var oldContext = loadedContext;
            var shutdowns = events.Count(e => e == "shutdown");
            var replacing = Start();
            try
            {
                for (var i = 0; binding.IsReady && i < 200; i++) await Task.Delay(10);
                Check(!binding.IsReady && !replacing.IsCompleted && events.Count(e => e == "shutdown") == shutdowns,
                    "replacement publishes new generation but drains old execution before shutdown");
                await Reject(() => binding.ExecuteAsync("synthetic:key", new("synthetic.echo", new() { ["text"] = "stale" })),
                    "plugin_contribution_unavailable");
                Check(manager.GetTools().Single().IsReady && !ReferenceEquals(oldContext, loadedContext),
                    "same-version replacement uses a new collectible executor");
                release.TrySetResult();
                Check((await active).ModelText == "original", "running tool receives a detached argument snapshot");
                await replacing.WaitAsync(TimeSpan.FromSeconds(5));
                Check(events.Count(e => e == "shutdown") == shutdowns + 1, "old executor shuts down once after its call returns");
            }
            finally { release.TrySetResult(); await replacing; }
            var current = manager.GetTools().Single();
            run = (_, _, _) => throw new InvalidOperationException("synthetic_tool_failure");
            await Reject(() => current.ExecuteAsync("synthetic:key", new("synthetic.echo", new() { ["text"] = "fault" })),
                "synthetic_tool_failure");
            run = (_, _, token) => Task.FromCanceled<ToolOutcome>(token);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try
            {
                await current.ExecuteAsync("synthetic:key", new("synthetic.echo", new() { ["text"] = "cancelled" }), cancellation.Token);
                throw new InvalidOperationException("cancelled tool was allowed");
            }
            catch (OperationCanceledException) { checks++; }
            await manager.RemoveAsync(manifest.Id);
            Check(!current.IsReady && manager.GetTools().Length == 0, "cancellation leaves no lease blocking removal");
            var retainedEntry = typeof(PluginManager.ToolBinding).GetField("_entry", hidden)!.GetValue(current)!;
            Check(retainedEntry.GetType().GetProperty("ToolTarget")!.GetValue(retainedEntry) is null
                && retainedEntry.GetType().GetProperty("Plugin")!.GetValue(retainedEntry) is null,
                "retained stale bindings do not root retired collectible executors");
        }
        finally { await manager.StopAllAsync(default); }

        var dependencies = new PluginManager(_ => { });
        try
        {
            await dependencies.ReplaceAsync(new ToolOwnerPlugin("synthetic.provider", Definition("synthetic.provider-tool")), true);
            using (var prepared = dependencies.PrepareDependencies("synthetic.consumer", [new("synthetic.provider", "1.0.0")]))
                await dependencies.ReplaceAsync(new ToolOwnerPlugin("synthetic.consumer", Definition("synthetic.consumer-tool")),
                    true, prepared, 1);
            var consumer = dependencies.GetTools().Single(tool => tool.ProviderId == "synthetic.consumer");
            Check(consumer.IsReady, "tool contribution starts only with its exact provider generation");
            dependencies.SetEnabled("synthetic.provider", false);
            Check(dependencies.GetTools().Length == 0 && !consumer.IsReady, "dependency disable removes both tools from the ready snapshot");
            await Reject(() => consumer.ExecuteAsync("synthetic:key", new("synthetic.consumer-tool", new() { ["text"] = "value" })),
                "plugin_contribution_unavailable");
            dependencies.SetEnabled("synthetic.provider", true);
            await dependencies.ReplaceAsync(new ToolOwnerPlugin("synthetic.provider", Definition("synthetic.provider-tool")), true);
            Check(!consumer.IsReady && dependencies.GetTools().Length == 1, "provider replacement cannot silently rebind consumer tool metadata");
        }
        finally { await dependencies.StopAllAsync(default); }
        return checks;
    }

    private static ToolDefinition Definition(string id) => new(new(id, ToolCategory.WebRead, "Synthetic tool", true,
        id + ".executor", "text", ToolDefaultPolicy.FollowSwitch, FeatureKey: ToolFeature.WebSearch, Origin: ToolOrigin.Plugin),
        [new("text", true, 1, 128)]);

    private sealed class ToolFixtureServices(Func<ToolDefinition[]> definitions,
        Func<string, ToolCall, CancellationToken, Task<ToolOutcome>> run, Action<string> log, Action<Assembly> loaded) : IServiceProvider
    {
        public object? GetService(Type type) => type == typeof(Func<ToolDefinition[]>) ? definitions
            : type == typeof(Func<string, ToolCall, CancellationToken, Task<ToolOutcome>>) ? run
            : type == typeof(Action<string>) ? log : type == typeof(Action<Assembly>) ? loaded : null;
    }
    private sealed class ToolOwnerPlugin(string id, ToolDefinition definition) : IBotPlugin, IToolContribution
    {
        public string Id => id;
        public string Version => "1.0.0";
        public string Name => "Synthetic owner";
        public string Description => "Synthetic tool conflict";
        public PluginCategory Category => PluginCategory.Feature;
        public ToolDefinition[] DescribeTools() => [definition];
        public Task<ToolOutcome> ExecuteAsync(string key, ToolCall call, CancellationToken ct) => Task.FromResult(ToolOutcome.Success("synthetic"));
        public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
    }
}

public sealed class SyntheticToolPlugin : IBotPlugin, IPluginContributionSource, IToolContribution
{
    public string Id => "test.tools";
    public string Version => "1.0.0";
    public string Name => "Synthetic tools";
    public string Description => "Synthetic tool lifecycle";
    public PluginCategory Category => PluginCategory.Feature;
    private Func<ToolDefinition[]> _definitions = null!;
    private Func<string, ToolCall, CancellationToken, Task<ToolOutcome>> _run = null!;
    private Action<string> _log = null!;
    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        _definitions = (Func<ToolDefinition[]>)context.Services.GetService(typeof(Func<ToolDefinition[]>))!;
        _run = (Func<string, ToolCall, CancellationToken, Task<ToolOutcome>>)context.Services
            .GetService(typeof(Func<string, ToolCall, CancellationToken, Task<ToolOutcome>>))!;
        _log = (Action<string>)context.Services.GetService(typeof(Action<string>))!;
        ((Action<Assembly>)context.Services.GetService(typeof(Action<Assembly>))!)(GetType().Assembly);
        return Task.CompletedTask;
    }
    public object? GetContribution(Type contract) => contract == typeof(IToolContribution) ? this : null;
    public ToolDefinition[] DescribeTools() => _definitions();
    public Task<ToolOutcome> ExecuteAsync(string sourceKey, ToolCall call, CancellationToken ct) => _run(sourceKey, call, ct);
    public Task ShutdownAsync(CancellationToken ct) { _log("shutdown"); return Task.CompletedTask; }
}
