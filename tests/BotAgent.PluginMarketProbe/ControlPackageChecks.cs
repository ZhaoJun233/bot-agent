using System.Text.Json;
using System.Security.Cryptography;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Services.Plugins;

internal partial class Program
{
    internal static async Task<int> RunControlPackageChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        async Task Reject(Func<Task> action, string code)
        {
            try { await action(); } catch (Exception e) when (e.Message == code) { checks++; return; }
            throw new InvalidOperationException("expected_" + code);
        }
        var bytes = ReviewZip("control.dll", await File.ReadAllBytesAsync(typeof(ControlPackagePlugin).Assembly.Location));
        var manifest = ReviewManifest(bytes, "test.control-package", "1.0.0", "control.dll", nameof(ControlPackagePlugin)) with
        { Control = true, Data = new([], ["synthetic:control:"]) };
        var emptyIndex = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, releases = Array.Empty<PluginPackageManifest>() });
        var packages = new PluginPackageService(Path.Combine(root, "control-package"), new ReviewPackageHttp { Bytes = bytes },
            TrustedPluginDistribution.Read(emptyIndex, Convert.ToHexString(SHA256.HashData(emptyIndex))), new PluginResourceAuthorizations([manifest]));
        await packages.StageInstallAsync(manifest);
        var events = new List<string>();
        var stores = new Dictionary<string, IPackageDataStore>();
        var fail = "control";
        var context = new PluginContext(new ControlServices((name, store) => stores[name] = store, events.Add, name => name == fail));
        var registry = new PluginManager(_ => { });
        try
        {
            await Reject(() => packages.ApplyAtStartupAsync(manifest.Id, context, registry, false), "synthetic_control_prepare_failed");
            Check(events.SequenceEqual(["control-prepare", "control-stop"]) && registry.Find(manifest.Id) is null, "failed control preparation is cleaned without business start");
            await Reject(() => Task.FromResult(stores["control"].Write(_ => 0)), "plugin_data_not_ready");
            Check((await packages.GetInstalledAsync()).Single().Pending is not null, "failed preparation preserves staged release");
            fail = "business"; events.Clear();
            await Reject(() => packages.ApplyAtStartupAsync(manifest.Id, context, registry, true), "synthetic_business_prepare_failed");
            Check(events.SequenceEqual(["control-prepare", "business-prepare", "business-stop", "control-stop"]), "business preparation failure cleans both lifetimes in order");
            await Reject(() => Task.FromResult(stores["business"].Write(_ => 0)), "plugin_data_not_ready");
            fail = ""; events.Clear();
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, true);
            var business = stores["business"]; var control = stores["control"];
            Check(!ReferenceEquals(business, control), "ordinary and control contexts receive separate admission views");
            registry.SetEnabled(manifest.Id, false);
            await Reject(() => Task.FromResult(business.Write(_ => 0)), "plugin_data_not_ready");
            Check(control.Write(s => { s.WriteState("synthetic:control:value", "retained"); return 1; }) == 1, "approved control writes survive business disable");
            await registry.RemoveAsync(manifest.Id);
            Check(events.SequenceEqual(["control-prepare", "business-prepare", "business-stop", "control-stop"]), "removal cleans each prepared lifetime once");
            await Reject(() => Task.FromResult(control.Read(_ => 0)), "plugin_data_not_ready");
            events.Clear();
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, false);
            Check(events.SequenceEqual(["control-prepare"]) && stores["control"].Read(s => s.ReadState("synthetic:control:value")) == "retained", "disabled cold startup does not initialize business");
            await registry.RemoveAsync(manifest.Id);
            Check(events.SequenceEqual(["control-prepare", "control-stop"]), "metadata-only business never receives shutdown");
        }
        finally { await registry.StopAllAsync(default); }
        return checks;
    }
    private sealed class ControlServices(Action<string, IPackageDataStore> capture, Action<string> record, Func<string, bool> fail) : IServiceProvider
    {
        public object? GetService(Type type) => type == typeof(Action<string, IPackageDataStore>) ? capture
            : type == typeof(Action<string>) ? record : type == typeof(Func<string, bool>) ? fail : null;
    }
}

public sealed class ControlPackagePlugin : IBotPlugin, IPluginControlModule
{
    private Action<string> _record = _ => { };
    public string Id => "test.control-package";
    public string Version => "1.0.0";
    public string Name => "Synthetic control";
    public string Description => "Synthetic independent control lifetime";
    public PluginCategory Category => PluginCategory.Management;
    private Task Prepare(PluginContext context, string name)
    {
        _record = (Action<string>)context.Services.GetService(typeof(Action<string>))!;
        _record(name + "-prepare");
        var data = (IPackageDataStore)context.Services.GetService(typeof(IPackageDataStore))!;
        ((Action<string, IPackageDataStore>)context.Services.GetService(typeof(Action<string, IPackageDataStore>))!)(name, data);
        try { data.Write(_ => 0); throw new Exception("preparation_write_allowed"); }
        catch (InvalidOperationException e) when (e.Message == "plugin_data_not_ready") { }
        if (((Func<string, bool>)context.Services.GetService(typeof(Func<string, bool>))!)(name))
            throw new InvalidOperationException("synthetic_" + name + "_prepare_failed");
        return Task.CompletedTask;
    }
    public Task PrepareControlAsync(PluginContext context, CancellationToken ct) => Prepare(context, "control");
    public Task InitializeAsync(PluginContext context, CancellationToken ct) => Prepare(context, "business");
    public object? GetControlContribution(Type type) => null;
    public Task ShutdownControlAsync(CancellationToken ct) { _record("control-stop"); return Task.CompletedTask; }
    public Task ShutdownAsync(CancellationToken ct) { _record("business-stop"); return Task.CompletedTask; }
}
