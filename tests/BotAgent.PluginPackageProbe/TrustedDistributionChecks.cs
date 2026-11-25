using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Plugins;

internal static class TrustedDistributionChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "trusted-distribution-" + Guid.NewGuid().ToString("N"));
        var context = new PluginContext(new EmptyServices());
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var entry = zip.CreateEntry("probe.dll").Open())
            entry.Write(File.ReadAllBytes(typeof(ReservedFixturePlugin).Assembly.Location));
        var archive = output.ToArray();
        var manifest = new PluginPackageManifest("preset.feature.synthetic", "1.0.0", "probe.dll",
            typeof(ReservedFixturePlugin).FullName!, Digest(archive), "https://example.com/preset.zip");
        var second = manifest with { Version = "2.0.0", EntryType = typeof(ReservedFixturePluginV2).FullName! };
        byte[] Index(params PluginPackageManifest[] releases) => JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, releases });
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidDataException or JsonException) { check(true, label); return; }
            throw new InvalidOperationException(label);
        }
        var bytes = Index(manifest, second);
        await Reject(() => Task.Run(() => TrustedPluginDistribution.Read(bytes, new string('0', 64))), "index approval is out of band");
        var duplicated = Index(manifest, manifest);
        await Reject(() => Task.Run(() => TrustedPluginDistribution.Read(duplicated, Digest(duplicated))), "duplicate fixed release rejected");
        var external = Index(manifest with { Id = "external.fixture" });
        await Reject(() => Task.Run(() => TrustedPluginDistribution.Read(external, Digest(external))), "ordinary source cannot claim distribution membership");
        foreach (var invalid in new[] { "{\"formatVersion\":2,\"releases\":[]}",
            "{\"formatVersion\":1,\"formatVersion\":1,\"releases\":[]}",
            "{\"formatVersion\":1,\"releases\":[],\"trustMe\":true}",
            "{\"formatVersion\":1,\"releases\":null}" })
        {
            var invalidBytes = System.Text.Encoding.UTF8.GetBytes(invalid);
            await Reject(() => Task.Run(() => TrustedPluginDistribution.Read(invalidBytes, Digest(invalidBytes))), "malformed index refused");
        }
        var trust = TrustedPluginDistribution.Read(bytes, Digest(bytes));
        check(trust.Fingerprint == Digest(bytes), "distribution retains approved fingerprint");
        Array.Fill(bytes, (byte)0);
        var http = new DistributionOfflineHttp();
        PluginPackageService Service() => new(root, http, trust);
        try
        {
            await Reject(() => new PluginPackageService(root, http).StageDistributionInstallAsync(manifest, archive), "self-declared preset rejected without host proof");
            await Reject(() => Service().StageInstallAsync(manifest), "ordinary import remains reserved even with distribution authority");
            foreach (var changed in new[] { manifest with { Sha256 = new string('0', 64) }, manifest with { EntryType = "ChangedType" },
                manifest with { Permissions = ["new.permission"] }, manifest with { DownloadUrl = "https://example.com/other.zip" },
                manifest with { Settings = [new("option", "Option", "boolean")] } })
                await Reject(() => Service().StageDistributionInstallAsync(changed, archive), "approval binds all manifest semantics");
            var corrupted = archive.ToArray(); corrupted[0] ^= 1;
            await Reject(() => Service().StageDistributionInstallAsync(manifest, corrupted), "approved manifest still verifies ZIP bytes");
            var staged = await Service().StageDistributionInstallAsync(manifest, archive);
            var marker = Path.Combine(root, manifest.Id, "config", "started.txt");
            check(staged.Pending == manifest && !File.Exists(marker) && http.Calls == 0, "offline staging has no runtime or network side effects");
            var disabled = await Service().LoadAtStartupAsync(context);
            check(disabled.Loaded.Count == 0 && !File.Exists(marker), "distribution does not auto-enable");
            var collision = await Service().LoadAtStartupAsync(context, isRegistered: _ => true, isEnabled: _ => true);
            check(collision.Loaded.Count == 0 && collision.Failures.Count == 1 && !File.Exists(marker), "old registration blocks second live source before initialization");
            var loaded = await Service().LoadAtStartupAsync(context, isEnabled: _ => true);
            check(loaded.Loaded.Single().Id == manifest.Id && loaded.Failures.Count == 0 && File.ReadAllText(marker) == "1", "real approved DLL cold-starts once");
            await loaded.Loaded.Single().ShutdownAsync(CancellationToken.None);
            var path = Path.Combine(root, manifest.Id, "installed.json");
            var original = await File.ReadAllBytesAsync(path);
            var tampered = JsonSerializer.SerializeToUtf8Bytes(new InstalledPluginPackage(manifest.Id, manifest with { EntryType = "ChangedType" }));
            await File.WriteAllBytesAsync(path, tampered);
            var quarantined = await Service().GetInstalledAsync();
            check(quarantined.Single().MetadataFailure is not null, "persisted state cannot grant itself trust");
            var blocked = await Service().LoadAtStartupAsync(context, isEnabled: _ => true);
            check(blocked.Loaded.Count == 0 && blocked.Failures.Count == 1 && File.ReadAllText(marker) == "1", "tampered state never executes");
            check(File.ReadAllBytes(path).SequenceEqual(tampered), "trust rejection preserves raw evidence");
            await File.WriteAllBytesAsync(path, original);
            var untrusted = await new PluginPackageService(root, http).GetInstalledAsync();
            check(untrusted.Single().MetadataFailure is not null, "approval is not inferred from previous installation");
            await Reject(() => Service().ApplyAsync(manifest.Id, context, new PluginManager(_ => { }), true), "distribution cannot enter legacy online reload");
            await Service().StageDistributionInstallAsync(second, archive);
            var upgraded = await Service().LoadAtStartupAsync(context, isEnabled: _ => true);
            check(upgraded.Loaded.Single().Version == "2.0.0" && (await Service().GetInstalledAsync()).Single().Previous?.Version == "1.0.0", "approved upgrade preserves previous release");
            await upgraded.Loaded.Single().ShutdownAsync(CancellationToken.None);
            await Service().StageUninstallAsync(manifest.Id);
            await Service().LoadAtStartupAsync(context);
            var removed = (await Service().GetInstalledAsync()).Single();
            check(removed.Active is null && removed.Previous?.Version == "2.0.0" && File.Exists(marker), "uninstall retains data and previous");
            await Service().StageDistributionInstallAsync(manifest, archive);
            var reinstalled = await Service().LoadAtStartupAsync(context, isEnabled: _ => false);
            check(reinstalled.Loaded.Count == 0 && File.ReadAllText(marker) == "2", "reinstall retains disabled intent");
            check(http.Calls == 0, "complete offline distribution flow made no HTTP requests");
        }
        finally
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public class ReservedFixturePlugin : IBotPlugin
{
    public string Id => "preset.feature.synthetic";
    public string Name => "Synthetic distribution fixture";
    public virtual string Version => "1.0.0";
    public string Description => "Synthetic cold-start fixture";
    public PluginCategory Category => PluginCategory.Feature;
    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        var path = Path.Combine(context.EnvironmentInfo["pluginConfigDirectory"], "started.txt");
        var count = File.Exists(path) ? int.Parse(File.ReadAllText(path)) : 0;
        return File.WriteAllTextAsync(path, (count + 1).ToString(), ct);
    }
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
public sealed class ReservedFixturePluginV2 : ReservedFixturePlugin { public override string Version => "2.0.0"; }
internal sealed class DistributionOfflineHttp : BotAgent.Adapters.Net.IHttpFetcher
{
    public int Calls { get; private set; }
    public TimeSpan Timeout => TimeSpan.FromSeconds(1);
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
    { Calls++; throw new InvalidOperationException("Distribution must remain offline."); }
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => SendAsync(request, ct);
    public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => SendAsync(new(HttpMethod.Get, url), ct);
    public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => GetAsync(url.AbsoluteUri, ct);
    public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url, ct);
    public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url, ct);
    public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default) => GetAsync(url, ct);
}
