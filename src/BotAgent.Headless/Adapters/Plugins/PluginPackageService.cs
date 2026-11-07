using BotAgent.Domain.Plugins;
using BotAgent.Services.Net;

namespace BotAgent.Adapters.Plugins;

/// <summary>
/// Singleton package lifecycle. Only approved manifests may reach StageInstallAsync.
/// Inject an IHttpFetcher with redirects disabled and private destinations rejected.
/// Packages execute trusted managed code in-process or Python in owned child processes; neither is a security sandbox.
/// </summary>
public sealed partial class PluginPackageService
{
    private readonly PluginPackageStore _store;
    private readonly IHttpFetcher _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public PluginGovernanceService Governance { get; }
    public PluginPythonSources PythonSources { get; } = new();
    private bool _startupLoaded;
    private volatile IReadOnlyList<InstalledPluginPackage> _snapshot = Array.Empty<InstalledPluginPackage>();

    public PluginPackageService(string rootDirectory, IHttpFetcher http)
    {
        _store = new PluginPackageStore(rootDirectory);
        _http = http;
        Governance = new PluginGovernanceService(_store.Root, _http);
        Directory.CreateDirectory(_store.Root);
    }

    /// <summary>Last observed package state; refreshed by every lifecycle call. Never blocks.</summary>
    public IReadOnlyList<InstalledPluginPackage> InstalledSnapshot => _snapshot;

    public async Task<IReadOnlyList<InstalledPluginPackage>> GetInstalledAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await ReadInstalledLockedAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<InstalledPluginPackage>> ReadInstalledLockedAsync(CancellationToken ct)
    {
        var result = new List<InstalledPluginPackage>();
        foreach (var id in _store.Ids()) result.Add(await _store.ReadAsync(id, ct));
        _snapshot = result.ToArray();
        return result;
    }

    private async Task EnsureRevisionLockedAsync(string? expectedRevision, CancellationToken ct)
    {
        if (expectedRevision is null) return; // Original trusted lifecycle API remains compatible.
        var installed = await ReadInstalledLockedAsync(ct);
        if (PluginPackageRevision.Of(installed) != expectedRevision)
            throw new BotAgent.Panel.ZbaBackendException(409, "plugin_revision_conflict",
                "插件目录已变化，请重新读取后再提交。");
    }

    public Task<InstalledPluginPackage> StageInstallAsync(PluginPackageManifest manifest, CancellationToken ct = default)
        => StageInstallCoreAsync(manifest, null, false, ct);

    public Task<InstalledPluginPackage> StageInstallAsync(PluginPackageManifest manifest, string expectedRevision,
        CancellationToken ct) => StageInstallCoreAsync(manifest, expectedRevision, false, ct);

    internal Task<InstalledPluginPackage> StageUpdateAsync(PluginPackageManifest manifest, string expectedRevision,
        CancellationToken ct) => StageInstallCoreAsync(manifest, expectedRevision, true, ct);

    internal Task<InstalledPluginPackage> StageFreshInstallAsync(PluginPackageManifest manifest, string expectedRevision,
        Func<CancellationToken, Task> beforePublish, CancellationToken ct)
        => StageInstallCoreAsync(manifest, expectedRevision, false, ct, beforePublish);

    public Task<InstalledPluginPackage> StageUninstallAsync(string id, CancellationToken ct = default)
        => StageUninstallCoreAsync(id, null, ct);

    public Task<InstalledPluginPackage> StageUninstallAsync(string id, string expectedRevision, CancellationToken ct)
        => StageUninstallCoreAsync(id, expectedRevision, ct);

    private async Task<InstalledPluginPackage> StageUninstallCoreAsync(string id, string? expectedRevision, CancellationToken ct)
    {
        PluginPackageValidation.ValidateId(id);
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureRevisionLockedAsync(expectedRevision, ct);
            var state = await _store.ReadAsync(id, ct);
            if (state.Active == null && state.Pending == null) throw new InvalidOperationException("Package is not installed.");
            // 保留清单直到运行时完成卸载，失败时仍可观察并重试待处理状态。
            var next = state with { PendingUninstall = true };
            await _store.SaveAsync(next, ct);
            Publish(next);
            return next;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Call once during startup before registry initialization. Returned plugins are already initialized.</summary>
    /// <param name="resolveSettings">
    /// Host-resolved declarative settings JSON for a manifest, or null when resolution is unavailable.
    /// A required setting that was never configured fails that package's startup instead of the whole host.
    /// </param>
    public async Task<PluginPackageLoadResult> LoadAtStartupAsync(PluginContext context,
        Func<string, bool>? isRegistered = null, Func<string, bool>? isEnabled = null, CancellationToken ct = default,
        Func<PluginPackageManifest, string?>? resolveSettings = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_startupLoaded) throw new InvalidOperationException("Package startup already performed.");
            _startupLoaded = true;
            var loaded = new List<IBotPlugin>();
            var failures = new List<PluginPackageFailure>();
            foreach (var id in _store.Ids().ToArray())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var state = await _store.ReadAsync(id, ct);
                    if (state.PendingUninstall)
                    {
                        var cleared = state with { Previous = state.Active ?? state.Previous,
                            Active = null, Pending = null, PendingUninstall = false };
                        await _store.SaveAsync(cleared, ct);
                        Publish(cleared);
                        continue;
                    }
                    if (state.Active == null && state.Pending == null) continue;
                    if (isEnabled?.Invoke(id) != true) continue;
                    if (isRegistered?.Invoke(id) == true) throw new InvalidDataException("Package ID already registered.");
                    IBotPlugin? plugin = null;
                    if (state.Pending != null)
                    {
                        try
                        {
                            plugin = await StartReleaseAsync(state.Pending, context, ct, resolveSettings);
                            var activated = state with { Previous = state.Active ?? state.Previous,
                                Active = state.Pending, Pending = null };
                            await _store.SaveAsync(activated, ct);
                            Publish(activated);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception)
                        {
                            if (plugin != null) { await plugin.ShutdownAsync(CancellationToken.None); plugin = null; }
                            failures.Add(new PluginPackageFailure(id, "pending-load-failed"));
                        }
                    }
                    if (plugin == null && state.Active != null) plugin = await StartReleaseAsync(state.Active, context, ct, resolveSettings);
                    if (plugin != null)
                    {
                        if (plugin is PluginPythonPackage python) python.Publish();
                        loaded.Add(plugin);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { failures.Add(new PluginPackageFailure(id, "package-load-failed")); }
            }
            return new PluginPackageLoadResult(loaded, failures);
        }
        finally { _gate.Release(); }
    }

    private void Publish(InstalledPluginPackage state)
        => _snapshot = _snapshot.Where(item => item.Id != state.Id).Append(state)
            .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();

    private async Task<IBotPlugin> StartReleaseAsync(PluginPackageManifest manifest, PluginContext context,
        CancellationToken ct, Func<PluginPackageManifest, string?>? resolveSettings = null, bool enabled = true)
    {
        var bytes = await _store.ReadArchiveAsync(manifest, ct).ConfigureAwait(false);
        var runtime = Path.Combine(_store.Root, ".runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtime);
        try
        {
            await PluginPackageArchive.ExtractAsync(bytes, manifest, runtime, ct);
            var config = Path.Combine(_store.PackageDirectory(manifest.Id), "config");
            Directory.CreateDirectory(config);
            if (manifest.Runtime is not null)
                return await PluginPythonPackage.StartAsync(manifest, runtime, PythonSources,
                    enabled ? resolveSettings?.Invoke(manifest) : null, enabled, ct).ConfigureAwait(false);
            return await PluginPackageLoader.StartAsync(runtime, config, manifest, context, ct,
                enabled ? resolveSettings?.Invoke(manifest) : null, enabled);
        }
        catch
        {
            try { if (Directory.Exists(runtime)) Directory.Delete(runtime, recursive: true); } catch { }
            throw;
        }
    }

    private async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        var uri = PluginPackageValidation.ValidateUrl(url);
        using var response = await SafeUrl.SendFollowingRedirectsAsync(_http, uri, target =>
        {
            PluginPackageValidation.ValidateUrl(target.AbsoluteUri);
            return new HttpRequestMessage(HttpMethod.Get, target);
        }, false, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > PluginPackageArchive.MaxArchiveBytes)
            throw new InvalidDataException("Archive too large.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + count > PluginPackageArchive.MaxArchiveBytes) throw new InvalidDataException("Archive too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}