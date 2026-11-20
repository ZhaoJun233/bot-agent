using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Tools;
using BotAgent.Services.Plugins;
using BotAgent.Services.Tools;

internal partial class Program
{
    internal static async Task<int> RunToolVersionChecks(string root)
    {
        var checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks++; }
        async Task Reject(Func<Task> action, string code)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException && error.Message == code)
            { checks++; return; }
            throw new InvalidOperationException("expected_" + code);
        }
        var bytes = ReviewZip("version-tools.dll", await File.ReadAllBytesAsync(typeof(VersionedToolPlugin).Assembly.Location));
        var manifest = ReviewManifest(bytes, "test.tool-version", "1.0.0", "version-tools.dll", nameof(VersionedToolPlugin)) with { ToolApiVersion = 1 };
        var events = new List<string>();
        var providesTools = true;
        var context = new PluginContext(new VersionToolServices(() => providesTools, events.Add));
        var directory = Path.Combine(root, "tool-version");
        var packages = new PluginPackageService(directory, new ReviewPackageHttp { Bytes = bytes });
        var registry = new PluginManager(_ => { });
        var metadataPath = Path.Combine(directory, manifest.Id, "installed.json");
        try
        {
            foreach (var version in new[] { -1, 0, 2 })
                await Reject(() => packages.StageInstallAsync(manifest with { ToolApiVersion = version }), "plugin_tools_invalid_declaration");
            Check(events.Count == 0, "unsupported versions never prepare an executor");
            await packages.StageInstallAsync(manifest);
            var pending = await File.ReadAllBytesAsync(metadataPath);
            Check(JsonNode.Parse(pending)!["minimumReaderVersion"]!.GetValue<int>() == 8, "pending tool declaration requires reader eight");
            await Reject(() => packages.ApplyAsync(manifest.Id, context, registry, true), "plugin_contract_cold_start_required");
            var legacyLoad = await packages.LoadAtStartupAsync(context, isEnabled: _ => true);
            Check(legacyLoad.Loaded.Count == 0 && legacyLoad.Failures.Count == 1 && events.Count == 0,
                "legacy startup cannot activate declared tools without the managed registry");
            Check((await File.ReadAllBytesAsync(metadataPath)).SequenceEqual(pending), "rejected legacy and online paths preserve pending state");
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, true);
            var tool = registry.GetTools().Single();
            Check(events.SequenceEqual(new[] { "initialize", "lookup", "describe" }), "tool target resolved once during preparation");
            Check(tool.ProviderId == manifest.Id && (await tool.ExecuteAsync("group:10001", new("synthetic.version", new()))).Ok,
                "declared tools execute through the real package generation");
            registry.GetToolCatalog(); registry.GetTools();
            Check(events.Count(e => e == "lookup") == 1, "catalog reads cannot ask the package for another executor");
            var stable = await File.ReadAllBytesAsync(metadataPath);
            providesTools = false;
            await Reject(() => packages.ApplyAtStartupAsync(manifest.Id, context, registry, true), "plugin_tools_missing");
            Check((await File.ReadAllBytesAsync(metadataPath)).SequenceEqual(stable) && tool.IsReady,
                "false declaration leaves durable state and published generation intact");
            Check(events.Count(e => e == "shutdown") == 1 && (await tool.ExecuteAsync("group:10001", new("synthetic.version", new()))).Ok,
                "failed preparation shuts down only the candidate and cannot substitute the retained executor");
            await registry.RemoveAsync(manifest.Id);
            providesTools = true; events.Clear();
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, false);
            Check(events.Count == 0 && registry.GetTools().Length == 0 && !registry.IsReady(manifest.Id),
                "disabled cold startup neither initializes nor discovers business tools");
            await registry.RemoveAsync(manifest.Id);
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, true);
            Check(events.SequenceEqual(new[] { "initialize", "lookup", "describe" }) && registry.GetTools().Length == 1,
                "enabled cold rebuild validates the actual contribution before publication");
            Check(!tool.IsReady, "older binding never revives after rebuilding the same release");

            var plain = manifest with { ToolApiVersion = null };
            foreach (var state in new[] { new InstalledPluginPackage(manifest.Id, Active: manifest),
                new InstalledPluginPackage(manifest.Id, Active: plain, Previous: manifest),
                new InstalledPluginPackage(manifest.Id, Active: plain, Pending: manifest) })
            {
                await LifecycleTestRequests.SaveState(packages, state);
                var stored = await File.ReadAllBytesAsync(metadataPath);
                Check(JsonNode.Parse(stored)!["minimumReaderVersion"]!.GetValue<int>() == 8,
                    "active previous and pending all retain the tool reader requirement");
                var current = await PluginStateCompatibility.InspectAsync(directory, typeof(PluginPackageService).Assembly.Location);
                Check(current.Compatible && current.CandidateReaderVersion == 12, "current reader understands declared tool state");
            }
            await LifecycleTestRequests.SaveState(packages, new(manifest.Id, Active: manifest));
            var validBytes = await File.ReadAllBytesAsync(metadataPath);
            var lowered = JsonNode.Parse(validBytes)!; lowered["minimumReaderVersion"] = 7;
            await File.WriteAllTextAsync(metadataPath, lowered.ToJsonString());
            Check((await packages.GetInstalledAsync()).Single().MetadataFailure is not null,
                "lowered reader header cannot hide tool semantics");
            await File.WriteAllBytesAsync(metadataPath, validBytes);
            if (Environment.GetEnvironmentVariable("BOTAGENT_IT_PREVIOUS_TOOL_RUNTIME") is { Length: > 0 } previousRuntime)
            {
                var previousHost = Path.Combine(previousRuntime, "BotAgent.Headless.dll");
                var before = await PluginStateCompatibility.InspectAsync(directory, previousHost);
                Check(!before.Compatible && before.CandidateReaderVersion == 7
                    && before.Entries.Single().Code == "plugin_state_reader_incompatible", "candidate PE preflight rejects actual reader seven");
                await CheckOldCode();
                var future = JsonNode.Parse(validBytes)!;
                future["execution"] = new JsonObject { ["futureTools"] = new JsonArray("synthetic") };
                // Deliberately invalid digest: version rejection must precede execution interpretation or hashing.
                await File.WriteAllTextAsync(metadataPath, future.ToJsonString());
                var opaque = await File.ReadAllBytesAsync(metadataPath);
                await CheckOldCode();
                Check((await File.ReadAllBytesAsync(metadataPath)).SequenceEqual(opaque), "old reader leaves opaque future bytes unchanged");
                await File.WriteAllBytesAsync(metadataPath, validBytes);

                async Task CheckOldCode()
                {
                    var result = await ExecutePreviousToolReader(previousRuntime, directory);
                    Check(result.GetProperty("CandidateReaderVersion").GetInt32() == 7
                        && !result.GetProperty("Compatible").GetBoolean()
                        && result.GetProperty("Entries")[0].GetProperty("Code").GetString() == "plugin_state_reader_incompatible"
                        && result.GetProperty("Entries")[0].GetProperty("Recovery").GetProperty("MinimumReaderVersion").GetInt32() == 8,
                        "actual previous reader code rejects before decoding future execution");
                }
            }
            Check((await File.ReadAllBytesAsync(metadataPath)).SequenceEqual(validBytes), "all compatibility reads preserve the approved state");
        }
        finally { await registry.StopAllAsync(default); }

        foreach (var (name, declaration, provide, expected) in new[]
        {
            ("undeclared", (int?)null, true, "plugin_tools_undeclared"),
            ("missing", (int?)1, false, "plugin_tools_missing"),
            ("ordinary", (int?)null, false, "")
        })
        {
            var variant = manifest with { ToolApiVersion = declaration };
            var variantRoot = Path.Combine(root, "tool-version-" + name);
            var service = new PluginPackageService(variantRoot, new ReviewPackageHttp { Bytes = bytes });
            var manager = new PluginManager(_ => { });
            var lifecycle = new List<string>();
            var environment = new PluginContext(new VersionToolServices(() => provide, lifecycle.Add));
            await service.StageInstallAsync(variant);
            var path = Path.Combine(variantRoot, variant.Id, "installed.json");
            var original = await File.ReadAllBytesAsync(path);
            try
            {
                if (expected.Length > 0)
                {
                    await Reject(() => service.ApplyAtStartupAsync(variant.Id, environment, manager, true), expected);
                    Check((await File.ReadAllBytesAsync(path)).SequenceEqual(original) && !manager.IsReady(variant.Id)
                        && manager.GetTools().Length == 0 && lifecycle.SequenceEqual(new[] { "initialize", "lookup", "shutdown" }),
                        "declaration mismatch cannot publish and preserves recoverable pending bytes");
                }
                else
                {
                    await service.ApplyAtStartupAsync(variant.Id, environment, manager, true);
                    Check(manager.IsReady(variant.Id) && manager.GetTools().Length == 0, "ordinary legacy contribution packages remain usable");
                    Check(JsonNode.Parse(await File.ReadAllBytesAsync(path))!["minimumReaderVersion"]!.GetValue<int>() == 1,
                        "ordinary package state does not acquire the tool reader minimum");
                }
            }
            finally { await manager.StopAllAsync(default); }
        }
        var python = manifest with { ApiVersion = 2, EntryAssembly = null, EntryType = null, TargetFramework = null,
            Runtime = new("python", 1, "main.py", "3.11", "stdlib-only", ["echo"]) };
        await Reject(() => { PluginPackageValidation.Validate(python); return Task.CompletedTask; }, "plugin_tools_invalid_declaration");
        foreach (var value in new JsonNode?[] { null, JsonValue.Create(1) })
        {
            var wire = JsonSerializer.SerializeToNode(python with { ToolApiVersion = null }, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            wire["toolApiVersion"] = value;
            try { PluginPackageValidation.ParseManifest(wire.ToJsonString()); throw new InvalidOperationException("python_tools_field_allowed"); }
            catch (JsonException) { checks++; }
        }
        return checks;
    }

    private static async Task<JsonElement> ExecutePreviousToolReader(string directory, string stateRoot)
    {
        var context = new PreviousToolReaderContext(directory);
        try
        {
            var path = Path.GetFullPath(Path.Combine(directory, "BotAgent.Headless.dll"));
            var assembly = context.LoadFromAssemblyPath(path);
            if (assembly == typeof(PluginPackageService).Assembly || AssemblyLoadContext.GetLoadContext(assembly) != context)
                throw new InvalidOperationException("previous_reader_not_isolated");
            var core = context.LoadFromAssemblyName(typeof(PluginPackageManifest).Assembly.GetName());
            if (core == typeof(PluginPackageManifest).Assembly || AssemblyLoadContext.GetLoadContext(core) != context)
                throw new InvalidOperationException("previous_reader_dependencies_not_isolated");
            var task = (Task)assembly.GetType("BotAgent.Adapters.Plugins.PluginStateCompatibility")!
                .GetMethod("InspectAsync")!.Invoke(null, [stateRoot, path, CancellationToken.None])!;
            await task;
            var report = task.GetType().GetProperty("Result")!.GetValue(task)!;
            return JsonSerializer.SerializeToElement(report, report.GetType());
        }
        finally { context.Unload(); }
    }

    private sealed class PreviousToolReaderContext(string directory) : AssemblyLoadContext("actual-previous-reader", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var path = Path.GetFullPath(Path.Combine(directory, name.Name + ".dll"));
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private sealed class VersionToolServices(Func<bool> provides, Action<string> record) : IServiceProvider
    {
        public object? GetService(Type type) => type == typeof(Func<bool>) ? provides : type == typeof(Action<string>) ? record : null;
    }
}

public sealed class VersionedToolPlugin : IBotPlugin, IPluginContributionSource, IToolContribution
{
    public string Id => "test.tool-version";
    public string Version => "1.0.0";
    public string Name => "Synthetic tool version";
    public string Description => "Synthetic managed declaration";
    public PluginCategory Category => PluginCategory.Feature;
    private Func<bool> _provides = null!;
    private Action<string> _record = null!;
    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        _provides = (Func<bool>)context.Services.GetService(typeof(Func<bool>))!;
        _record = (Action<string>)context.Services.GetService(typeof(Action<string>))!;
        _record("initialize"); return Task.CompletedTask;
    }
    public object? GetContribution(Type contract)
    { if (contract != typeof(IToolContribution)) return null; _record("lookup"); return _provides() ? this : null; }
    public ToolDefinition[] DescribeTools()
    { _record("describe"); return [new(new("synthetic.version", ToolCategory.WebRead, "Synthetic", true,
        "synthetic.version.actions", "", ToolDefaultPolicy.FollowSwitch), [])]; }
    public Task<ToolOutcome> ExecuteAsync(string sourceKey, ToolCall call, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(ToolOutcome.Success("synthetic")); }
    public Task ShutdownAsync(CancellationToken ct) { _record("shutdown"); return Task.CompletedTask; }
}
