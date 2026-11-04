using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Plugins;

internal static class HotReloadChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "plugin-reload-probe-" + Guid.NewGuid().ToString("N"));
        var http = new FakeHttp { Bytes = Archive() };
        var service = new PluginPackageService(root, http);
        var registry = new PluginManager(_ => { });
        var events = new ConcurrentQueue<string>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var block = false;
        var context = new PluginContext(new ReloadServices(value =>
        {
            events.Enqueue(value);
            if (value == "call:1.0.0" && block) { entered.Set(); release.Wait(); }
        }));
        PluginPackageManifest Manifest(string version, string type) => new("test.reload", version, "probe.dll", type,
            Convert.ToHexString(SHA256.HashData(http.Bytes)), "https://example.com/reload.zip");
        var first = Manifest("1.0.0", nameof(ReloadPlugin));
        var second = Manifest("2.0.0", nameof(ReloadPluginV2));
        var broken = Manifest("3.0.0", nameof(ReloadBrokenPlugin));
        try
        {
            await service.StageInstallAsync(first);
            await service.ApplyAsync(first.Id, context, registry, enabled: false);
            check(events.IsEmpty, "disabled generation never initializes");
            check(registry.Find(first.Id) is IReplyPolicyPlugin && !registry.GetAll().Single().IsEnabled,
                "disabled generation exposes metadata and policy capability");
            check((await service.GetInstalledAsync()).Single().Active?.Version == "1.0.0",
                "metadata-only install promotes without restart");

            await service.ApplyAsync(first.Id, context, registry, enabled: true, resolveSettings: _ => "synthetic_a");
            check(events.Count(value => value == "init:1.0.0") == 1, "enable initializes a fresh candidate exactly once");
            check(!events.Any(value => value.StartsWith("shutdown:", StringComparison.Ordinal)),
                "never-initialized metadata generation skips shutdown callback");
            check(registry.Find(first.Id)?.Name == "synthetic_a", "host settings reach candidate initialization");
            var original = (IReplyPolicyPlugin)registry.Find(first.Id)!;
            block = true;
            var inFlight = Task.Run(() => original.BlockReason(new("synthetic", false, true)));
            check(entered.Wait(TimeSpan.FromSeconds(5)), "old policy callback entered");
            await service.StageInstallAsync(second);
            var replacing = service.ApplyAsync(first.Id, context, registry, enabled: true,
                resolveSettings: _ => "synthetic_b");
            await UntilAsync(() => registry.Find(first.Id)?.Version == "2.0.0");
            check(registry.Find(first.Id)?.Version == "2.0.0", "candidate published while old call drains");
            check(!replacing.IsCompleted && !events.Contains("shutdown:1.0.0"), "old generation stays alive until lease drains");
            release.Set();
            await inFlight.WaitAsync(TimeSpan.FromSeconds(5));
            await replacing.WaitAsync(TimeSpan.FromSeconds(5));
            check(events.Count(value => value == "shutdown:1.0.0") == 1, "old generation shuts down exactly once");
            check(registry.Find(first.Id)?.Name == "synthetic_b", "updated settings and release published together");
            var shadows = events.Where(value => value.StartsWith("path:", StringComparison.Ordinal)).ToArray();
            check(shadows.Length == 2 && shadows.Distinct(StringComparer.Ordinal).Count() == 2,
                "each live generation uses a separate shadow directory");

            await service.StageInstallAsync(broken);
            await Reject(() => service.ApplyAsync(first.Id, context, registry, enabled: true));
            var failed = (await service.GetInstalledAsync()).Single();
            check(failed.Active?.Version == "2.0.0" && failed.Pending?.Version == "3.0.0",
                "initialization failure preserves active release and pending candidate");
            check(registry.Find(first.Id)?.Version == "2.0.0" && !events.Contains("shutdown:2.0.0"),
                "failed candidate cannot retire working generation");
            check(events.Count(value => value == "shutdown:3.0.0") == 1,
                "partially initialized candidate cleaned up once");

            await service.ApplyAsync(first.Id, context, registry, enabled: false);
            check(registry.Find(first.Id)?.Version == "3.0.0" && !registry.GetAll().Single().IsEnabled,
                "disabled broken-initialize release can load metadata without executing Initialize");
            await Reject(() => service.ApplyAsync(first.Id, context, registry, enabled: true));
            check(!registry.GetAll().Single().IsEnabled, "failed enable preserves disabled generation");
            await service.StageUninstallAsync(first.Id);
            await service.ApplyAsync(first.Id, context, registry, enabled: false);
            var removed = (await service.GetInstalledAsync()).Single();
            check(registry.Find(first.Id) is null && removed.Active is null && !removed.PendingUninstall,
                "uninstall removes live generation and clears pending state without restart");

            await service.StageInstallAsync(first);
            await registry.StopAllAsync(CancellationToken.None);
            await Reject(() => service.ApplyAsync(first.Id, context, registry, enabled: true));
            var rejected = (await service.GetInstalledAsync()).Single();
            check(rejected.Active is null && rejected.Pending?.Version == "1.0.0",
                "pre-publication registry rejection rolls durable promotion back");
            check(events.Count(value => value == "shutdown:1.0.0") == 2,
                "rejected initialized candidate is caller-cleaned exactly once");
        }
        finally
        {
            release.Set();
            await registry.StopAllAsync(CancellationToken.None);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected synthetic reload failure");
    }

    private static byte[] Archive()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        using (var stream = zip.CreateEntry("probe.dll").Open())
            stream.Write(File.ReadAllBytes(typeof(ReloadPlugin).Assembly.Location));
        return bytes.ToArray();
    }

    private sealed class ReloadServices(Action<string> callback) : IServiceProvider
    {
        public object? GetService(Type type) => type == typeof(Action<string>) ? callback : null;
    }
}

public class ReloadPlugin : IReplyPolicyPlugin
{
    private Action<string>? _callback;
    public string Id => "test.reload";
    public string Name { get; private set; } = "Synthetic reload";
    public virtual string Version => "1.0.0";
    public string Description => "Synthetic reload fixture";
    public PluginCategory Category => PluginCategory.Feature;
    public virtual Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        _callback = (Action<string>?)context.Services.GetService(typeof(Action<string>));
        _callback?.Invoke("init:" + Version);
        _callback?.Invoke("path:" + context.EnvironmentInfo["pluginPackageDirectory"]);
        if (context.EnvironmentInfo.TryGetValue("pluginSettings", out var settings)) Name = settings;
        return Task.CompletedTask;
    }
    public string? BlockReason(ReplyPolicyContext context) { _callback?.Invoke("call:" + Version); return null; }
    public Task ShutdownAsync(CancellationToken ct) { _callback?.Invoke("shutdown:" + Version); return Task.CompletedTask; }
}
public sealed class ReloadPluginV2 : ReloadPlugin { public override string Version => "2.0.0"; }
public sealed class ReloadBrokenPlugin : ReloadPlugin
{
    public override string Version => "3.0.0";
    public override async Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        await base.InitializeAsync(context, ct);
        throw new InvalidOperationException("synthetic initialization failure");
    }
}
