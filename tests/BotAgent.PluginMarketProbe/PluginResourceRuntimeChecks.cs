extern alias StorageModule;
using System.Security.Cryptography;
using System.Text.Json;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Services.Plugins;
using OperationRecoveryStore = StorageModule::BotAgent.Adapters.Persistence.OperationRecoveryStore;

internal partial class Program
{
    internal static async Task<int> RunPluginResourceRuntimeChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        async Task Reject(Func<Task> action, string code)
        {
            try { await action(); }
            catch (Exception e) when (e.Message == code) { checks++; return; }
            throw new InvalidOperationException("expected_" + code);
        }
        var bytes = ReviewZip("probe.dll", await File.ReadAllBytesAsync(typeof(ResourceRuntimePlugin).Assembly.Location));
        var provider = ReviewManifest(bytes, "test.dep.z-provider", "1.0.0", "probe.dll", typeof(DependencyProvider).FullName!);
        var manifest = ReviewManifest(bytes, "test.resource-runtime", "1.0.0", "probe.dll", typeof(ResourceRuntimePlugin).FullName!)
            with { Dependencies = [new(provider.Id, provider.Version)],
                Data = new([], ["synthetic:resource:"], Receipt: new("synthetic.operation", "1")) };
        var emptyIndex = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, releases = Array.Empty<PluginPackageManifest>() });
        var distribution = TrustedPluginDistribution.Read(emptyIndex, Convert.ToHexString(SHA256.HashData(emptyIndex)));
        var http = new ReviewPackageHttp { Bytes = bytes };
        var denied = new PluginPackageService(Path.Combine(root, "resource-denied"), http);
        await Reject(() => denied.StageInstallAsync(manifest), "plugin_data_not_authorized");
        await Reject(() => denied.StageInstallAsync(manifest with { Data = null, Control = true }), "plugin_data_not_authorized");
        Check((await denied.GetInstalledAsync()).Count == 0, "declaration alone grants no resources or durable install");
        var grants = new PluginResourceAuthorizations([manifest]);
        await Reject(() => Task.FromResult(new PluginResourceAuthorizations([manifest, manifest with { Id = "test.other" }])),
            "plugin_data_owner_conflict");
        var tableOwner = manifest with { Data = manifest.Data with { Tables = ["synthetic_resource"], StatePrefixes = [] } };
        await Reject(() => Task.FromResult(new PluginResourceAuthorizations([tableOwner, tableOwner with { Id = "test.other" }])),
            "plugin_data_owner_conflict");
        await Reject(() => Task.FromResult(new PluginResourceAuthorizations([manifest, manifest with { Id = "test.other",
            Data = manifest.Data with { StatePrefixes = ["synthetic:"] } }])), "plugin_data_owner_conflict");
        var packages = new PluginPackageService(Path.Combine(root, "resource-approved"), http, distribution, grants);
        await Reject(() => packages.StageInstallAsync(manifest with { Data = manifest.Data with { Audit = true } }), "plugin_data_not_authorized");
        await Reject(() => packages.StageInstallAsync(manifest with { Control = true }), "plugin_data_not_authorized");
        await Reject(() => packages.StageInstallAsync(manifest with { Sha256 = new string('b', 64) }), "plugin_data_not_authorized");
        await packages.StageInstallAsync(manifest);
        await packages.StageInstallAsync(provider);
        var metadataPath = Path.Combine(root, "resource-approved", manifest.Id, "installed.json");
        var metadataBytes = await File.ReadAllBytesAsync(metadataPath);
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(metadataBytes)!;
        Check(metadata["minimumReaderVersion"]!.GetValue<int>() == 3, "data declarations raise semantic reader minimum to three");
        metadata["minimumReaderVersion"] = 2;
        await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString());
        Check((await packages.GetInstalledAsync()).Single(p => p.Id == manifest.Id).MetadataFailure is not null,
            "lowering the envelope cannot bypass the resource reader requirement");
        await File.WriteAllBytesAsync(metadataPath, metadataBytes);
        var captures = new List<IPackageDataStore>();
        var events = new List<string>();
        var context = new PluginContext(new ResourceTestServices(captures.Add, events.Add));
        var registry = new PluginManager(_ => { });
        try
        {
            var forged = JsonSerializer.SerializeToUtf8Bytes(new InstalledPluginPackage(manifest.Id,
                Active: manifest with { Data = manifest.Data with { Audit = true } }));
            await File.WriteAllBytesAsync(metadataPath, forged);
            await Reject(() => packages.ApplyAtStartupAsync(manifest.Id, context, registry, true), "plugin_data_not_authorized");
            Check(captures.Count == 0 && (await File.ReadAllBytesAsync(metadataPath)).SequenceEqual(forged),
                "persisted declaration cannot grant access or erase rejected evidence");
            await File.WriteAllBytesAsync(metadataPath, metadataBytes);
            await Reject(() => packages.ApplyAsync(manifest.Id, context, registry, true), "plugin_contract_cold_start_required");
            Check(captures.Count == 0, "online apply executes no initializer");
            await Reject(() => packages.ApplyAtStartupAsync(manifest.Id, context, registry, true), "plugin_dependency_unavailable");
            await packages.ApplyAtStartupAsync(provider.Id, context, registry, true);
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, true);
            Check(events.Where(e => e.StartsWith("prepare-", StringComparison.Ordinal)).SequenceEqual(new[] { "prepare-read-denied", "prepare-write-denied" }) && captures.Count == 1,
                "real DLL cannot access storage during preparation");
            var data = captures[0];
            var policy = (IReplyPolicyPlugin)registry.Find(manifest.Id)!;
            Check(policy.BlockReason(new("synthetic-first", false, true)) == "1", "real plugin uses its granted state and receipt");
            var recovery = new OperationRecoveryStore();
            var first = recovery.Find(manifest.Id, "synthetic:scope", "synthetic-first")!;
            Check(first.Owner == manifest.Id && first.Contract == "synthetic.operation" && first.Generation?.Length == 97,
                "receipt binds approved release fingerprint and unique activation identity");
            await Reject(() => Task.FromResult(data.Write(s => { s.WriteState("foreign:state", "x"); return 0; })), "package_data_denied");
            registry.SetEnabled(manifest.Id, false);
            await Reject(() => Task.FromResult(data.Read(s => s.ReadState("synthetic:resource:count"))), "plugin_data_not_ready");
            await Reject(() => Task.FromResult(data.Write(s => { s.WriteState("synthetic:resource:count", "99"); return 0; })), "plugin_data_not_ready");
            registry.SetEnabled(manifest.Id, true);
            Check(data.Read(s => s.ReadState("synthetic:resource:count")) == "1", "revoked write changed no state");
            registry.SetEnabled(provider.Id, false);
            await Reject(() => Task.FromResult(data.Write(_ => 0)), "plugin_data_not_ready");
            registry.SetEnabled(provider.Id, true);
            await packages.ApplyAtStartupAsync(provider.Id, context, registry, true);
            await Reject(() => Task.FromResult(data.Write(_ => 0)), "plugin_data_not_ready");
            await registry.StopAllAsync(CancellationToken.None);
            await Reject(() => Task.FromResult(data.Read(s => s.ReadState("synthetic:resource:count"))), "plugin_data_not_ready");
            registry = new PluginManager(_ => { });
            await packages.ApplyAtStartupAsync(provider.Id, context, registry, true);
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, true);
            var next = captures[1];
            Check(((IReplyPolicyPlugin)registry.Find(manifest.Id)!).BlockReason(new("synthetic-second", false, true)) == "2",
                "same release resumes through a fresh activation");
            var second = recovery.Find(manifest.Id, "synthetic:scope", "synthetic-second")!;
            Check(first.Generation != second.Generation && first.Generation![..64] == second.Generation![..64]
                && recovery.Find(manifest.Id, "synthetic:scope", "synthetic-first") == first,
                "same-version restart changes activation but preserves old evidence");
            await Reject(() => Task.FromResult(data.Write(_ => 0)), "plugin_data_not_ready");
            await Reject(() => Task.FromResult(next.Write(s => { s.RecordReceipt(new("synthetic:scope", "synthetic-first", new string('a', 64), "committed")); return 0; })),
                "operation_receipt_conflict");
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var write = Task.Run(() => next.Write(s => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("synthetic_release_timeout"); s.WriteState("synthetic:resource:drained", "yes"); return 7; }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var removal = registry.RemoveAsync(manifest.Id);
            try
            {
                Check(!removal.IsCompleted && registry.Find(manifest.Id) is null, "retirement waits for the full database transaction");
                await Reject(() => Task.FromResult(next.Write(_ => 0)), "plugin_data_not_ready");
            }
            finally { release.Set(); }
            Check(await write == 7, "already-admitted transaction finishes before unload");
            await removal.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { await registry.StopAllAsync(CancellationToken.None); }
        return checks;
    }

    private sealed class ResourceTestServices(Action<IPackageDataStore> capture, Action<string> record) : IServiceProvider
    {
        public object? GetService(Type type) => type == typeof(Action<IPackageDataStore>) ? capture : type == typeof(Action<string>) ? record : null;
    }
}

public sealed class ResourceRuntimePlugin : IReplyPolicyPlugin
{
    private IPackageDataStore _data = null!;
    public string Id => "test.resource-runtime";
    public string Version => "1.0.0";
    public string Name => "Synthetic resources";
    public string Description => "Synthetic resource lifecycle fixture";
    public PluginCategory Category => PluginCategory.Feature;
    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        _data = (IPackageDataStore)context.Services.GetService(typeof(IPackageDataStore))!;
        var record = (Action<string>)context.Services.GetService(typeof(Action<string>))!;
        try { _data.Read(_ => 0); throw new InvalidOperationException("prepare_read_allowed"); }
        catch (InvalidOperationException e) when (e.Message == "plugin_data_not_ready") { record("prepare-read-denied"); }
        try { _data.Write(s => { s.WriteState("synthetic:resource:count", "99"); return 0; }); throw new InvalidOperationException("prepare_write_allowed"); }
        catch (InvalidOperationException e) when (e.Message == "plugin_data_not_ready") { record("prepare-write-denied"); }
        ((Action<IPackageDataStore>)context.Services.GetService(typeof(Action<IPackageDataStore>))!)(_data);
        return Task.CompletedTask;
    }
    public string? BlockReason(ReplyPolicyContext context) => _data.Write(s =>
    {
        var value = (int.Parse(s.ReadState("synthetic:resource:count") ?? "0") + 1).ToString();
        s.WriteState("synthetic:resource:count", value);
        s.RecordReceipt(new("synthetic:scope", context.SourceKey, new string('a', 64), "committed"));
        return value;
    });
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
