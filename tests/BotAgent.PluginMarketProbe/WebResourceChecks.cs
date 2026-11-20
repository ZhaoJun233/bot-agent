using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Plugins.Net;
using BotAgent.Services.Plugins;

internal partial class Program
{
    internal static async Task<int> RunWebResourceChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        async Task Reject(Func<Task> action, string code)
        {
            try { await action(); } catch (Exception e) when (e.Message == code) { checks++; return; }
            throw new InvalidOperationException("expected_" + code);
        }
        var bytes = ReviewZip("web.dll", await File.ReadAllBytesAsync(typeof(WebResourcePlugin).Assembly.Location));
        var plain = ReviewManifest(bytes, "test.web-runtime", "1.0.0", "web.dll", nameof(WebResourcePlugin));
        var manifest = plain with { Permissions = [IWebPageTransport.Permission] };
        var controlManifest = manifest with { Version = "1.0.1", Control = true, EntryType = nameof(WebControlResourcePlugin) };
        var emptyIndex = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, releases = Array.Empty<PluginPackageManifest>() });
        var distribution = TrustedPluginDistribution.Read(emptyIndex, Convert.ToHexString(SHA256.HashData(emptyIndex)));
        var http = new ReviewPackageHttp { Bytes = bytes };
        var denied = new PluginPackageService(Path.Combine(root, "web-denied"), http, distribution);
        await Reject(() => denied.StageInstallAsync(manifest), "plugin_web_not_authorized");
        Check((await denied.GetInstalledAsync()).Count == 0, "permission declaration alone writes no installed state");
        var packages = new PluginPackageService(Path.Combine(root, "web-approved"), http, distribution,
            new PluginResourceAuthorizations([manifest, controlManifest]));
        await Reject(() => packages.StageInstallAsync(manifest with { Sha256 = new string('b', 64) }), "plugin_web_not_authorized");
        await Reject(() => packages.StageInstallAsync(manifest with { Permissions = [IWebPageTransport.Permission, "message.send"] }), "plugin_web_not_authorized");
        var transport = new BlockingPageTransport();
        var captures = new Dictionary<string, IWebPageTransport?>();
        var events = new List<string>();
        var fail = false;
        var context = new PluginContext(new WebResourceServices(transport, (name, web) => captures[name] = web, events.Add, () => fail));
        var registry = new PluginManager(_ => { });
        var request = new WebPageRequest(new Uri("https://example.com/synthetic"));
        try
        {
            await denied.StageInstallAsync(plain);
            await denied.ApplyAtStartupAsync(plain.Id, context, registry, true);
            Check(captures["business"] is null, "undeclared context cannot inherit the ambient transport");
            await registry.RemoveAsync(plain.Id);
            await packages.StageInstallAsync(manifest);
            var metadataPath = Path.Combine(root, "web-approved", manifest.Id, "installed.json");
            var metadataBytes = await File.ReadAllBytesAsync(metadataPath);
            var metadata = JsonNode.Parse(metadataBytes)!;
            Check(metadata["minimumReaderVersion"]!.GetValue<int>() == 6, "network authority requires reader six");
            var currentReader = await PluginStateCompatibility.InspectAsync(Path.Combine(root, "web-approved"), typeof(PluginPackageService).Assembly.Location);
                Check(currentReader.Compatible && currentReader.CandidateReaderVersion == 12, "current PE declares and understands the new reader semantics");
            if (Environment.GetEnvironmentVariable("BOTAGENT_IT_PREVIOUS_HOST") is { Length: > 0 } previousHost)
            {
                var olderReader = await PluginStateCompatibility.InspectAsync(Path.Combine(root, "web-approved"), previousHost);
                Check(!olderReader.Compatible && olderReader.CandidateReaderVersion < 6
                    && olderReader.Entries.Single().Code == "plugin_state_reader_incompatible", "original previous host PE cannot adopt the new network state");
                Check((await File.ReadAllBytesAsync(metadataPath)).SequenceEqual(metadataBytes), "candidate compatibility inspection is read-only");
            }
            metadata["minimumReaderVersion"] = 5;
            await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString());
            Check((await packages.GetInstalledAsync()).Single().MetadataFailure is not null, "lowered reader cannot hide network semantics");
            await File.WriteAllBytesAsync(metadataPath, metadataBytes);
            await Reject(() => packages.ApplyAsync(manifest.Id, context, registry, true), "plugin_contract_cold_start_required");
            await Reject(() => packages.ApplyAtStartupAsync(manifest.Id, new PluginContext(new WebResourceServices(null,
                (_, _) => { }, _ => { }, () => false)), registry, true), "plugin_web_transport_missing");
            fail = true;
            await Reject(() => packages.ApplyAtStartupAsync(manifest.Id, context, registry, true), "synthetic_web_prepare_failed");
            var failed = captures["business"]!;
            await Reject(() => failed.GetAsync(request, default), "plugin_web_not_ready");
            Check(transport.Reads == 0 && transport.Validations == 0, "failed preparation makes no transport calls");
            fail = false; events.Clear();
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, true);
            var first = captures["business"]!;
            Check(events.Contains("business-validate-denied") && events.Contains("business-read-denied"), "real DLL cannot use either operation before publication");
            Check(first.TryValidate(request.Url.ToString(), out var uri, out _) && uri == request.Url
                && (await first.GetAsync(request, default)).Body.SequenceEqual(new byte[] { 1, 2, 3 }), "published handle uses the explicitly supplied transport");
            registry.SetEnabled(manifest.Id, false);
            await Reject(() => first.GetAsync(request, default), "plugin_web_not_ready");
            await Reject(() => Task.FromResult(first.TryValidate(request.Url.ToString(), out _, out _)), "plugin_web_not_ready");
            Check(transport.Reads == 1 && transport.Validations == 1, "disabled handle reaches no underlying resource");
            registry.SetEnabled(manifest.Id, true);
            transport.Block = true;
            var pending = first.GetAsync(request, default);
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var removal = registry.RemoveAsync(manifest.Id);
            await Reject(() => first.GetAsync(request, default), "plugin_web_not_ready");
            Check(!removal.IsCompleted, "removal waits for the whole response operation");
            transport.Release.TrySetResult();
            Check((await pending).Body.Length == 3, "admitted request completes while removal drains");
            await removal.WaitAsync(TimeSpan.FromSeconds(5));
            await Reject(() => first.GetAsync(request, default), "plugin_web_not_ready");
            transport.Block = false;
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, true);
            var second = captures["business"]!;
            Check(!ReferenceEquals(first, second) && (await second.GetAsync(request, default)).IsSuccessStatusCode, "same release gets a fresh usable generation");
            await Reject(() => first.GetAsync(request, default), "plugin_web_not_ready");
            transport.Fail = true;
            await Reject(() => second.GetAsync(request, default), "synthetic_web_failed");
            transport.Fail = false;
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { await second.GetAsync(request, cancellation.Token); throw new Exception("cancellation_ignored"); }
                catch (OperationCanceledException) { checks++; }
            }
            await registry.RemoveAsync(manifest.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Check(true, "failed and cancelled calls do not retain generation leases");
            await packages.StageInstallAsync(controlManifest);
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, true);
            var business = captures["business"]!; var control = captures["control"]!;
            registry.SetEnabled(manifest.Id, false);
            await Reject(() => business.GetAsync(request, default), "plugin_web_not_ready");
            Check(!ReferenceEquals(business, control) && (await control.GetAsync(request, default)).IsSuccessStatusCode,
                "approved diagnostic control has a distinct lifetime while business is disabled");
            await registry.RemoveAsync(manifest.Id);
            await Reject(() => control.GetAsync(request, default), "plugin_web_not_ready");
            events.Clear();
            await packages.ApplyAtStartupAsync(manifest.Id, context, registry, false);
            Check(!events.Any(e => e.StartsWith("business-", StringComparison.Ordinal)), "disabled cold startup never initializes business");
            var disabledControl = captures["control"]!;
            await registry.StopAllAsync(default);
            await Reject(() => disabledControl.GetAsync(request, default), "plugin_web_not_ready");
        }
        finally { transport.Release.TrySetResult(); await registry.StopAllAsync(default); }
        return checks;
    }

    private sealed class WebResourceServices(IWebPageTransport? web, Action<string, IWebPageTransport?> capture,
        Action<string> record, Func<bool> fail) : IServiceProvider
    {
        public object? GetService(Type type) => type == typeof(IWebPageTransport) ? web
            : type == typeof(Action<string, IWebPageTransport?>) ? capture
            : type == typeof(Action<string>) ? record : type == typeof(Func<bool>) ? fail : null;
    }

    private sealed class BlockingPageTransport : IWebPageTransport
    {
        public bool Block;
        public bool Fail;
        public int Reads;
        public int Validations;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TryValidate(string? url, out Uri uri, out string reason)
        { Validations++; uri = new Uri(url!); reason = ""; return true; }
        public async Task<WebPageResponse> GetAsync(WebPageRequest request, CancellationToken ct)
        {
            Reads++;
            ct.ThrowIfCancellationRequested();
            if (Fail) throw new IOException("synthetic_web_failed");
            if (Block) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            return new(request.Url, 200, [1, 2, 3]);
        }
    }
}

public class WebResourcePlugin : IBotPlugin
{
    public string Id => "test.web-runtime";
    public virtual string Version => "1.0.0";
    public string Name => "Synthetic web resources";
    public string Description => "Synthetic page capability lifecycle";
    public PluginCategory Category => PluginCategory.Feature;
    protected async Task Prepare(PluginContext context, string name)
    {
        var web = context.Services.GetService(typeof(IWebPageTransport)) as IWebPageTransport;
        var record = (Action<string>)context.Services.GetService(typeof(Action<string>))!;
        ((Action<string, IWebPageTransport?>)context.Services.GetService(typeof(Action<string, IWebPageTransport?>))!)(name, web);
        if (web is null) return;
        try { web.TryValidate("https://example.com/synthetic", out _, out _); throw new Exception("prepare_validate_allowed"); }
        catch (InvalidOperationException e) when (e.Message == "plugin_web_not_ready") { record(name + "-validate-denied"); }
        try { await web.GetAsync(new(new Uri("https://example.com/synthetic")), default); throw new Exception("prepare_read_allowed"); }
        catch (InvalidOperationException e) when (e.Message == "plugin_web_not_ready") { record(name + "-read-denied"); }
        if (((Func<bool>)context.Services.GetService(typeof(Func<bool>))!)()) throw new InvalidOperationException("synthetic_web_prepare_failed");
    }
    public Task InitializeAsync(PluginContext context, CancellationToken ct) => Prepare(context, "business");
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class WebControlResourcePlugin : WebResourcePlugin, IPluginControlModule
{
    public override string Version => "1.0.1";
    public Task PrepareControlAsync(PluginContext context, CancellationToken ct) => Prepare(context, "control");
    public object? GetControlContribution(Type contract) => null;
    public Task ShutdownControlAsync(CancellationToken ct) => Task.CompletedTask;
}
