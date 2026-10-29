using BotAgent.Domain.Plugins;
using BotAgent.Services.Net;

namespace BotAgent.Adapters.Plugins;

/// <summary>
/// Singleton package lifecycle. Only approved manifests may reach StageInstallAsync.
/// Inject an IHttpFetcher with redirects disabled and private destinations rejected.
/// Packages execute trusted .NET code in-process; this is not a security sandbox.
/// </summary>
public sealed class PluginPackageService
{
    private readonly PluginPackageStore _store;
    private readonly IHttpFetcher _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _startupLoaded;
    private volatile IReadOnlyList<InstalledPluginPackage> _snapshot = Array.Empty<InstalledPluginPackage>();

    public PluginPackageService(string rootDirectory, IHttpFetcher http)
    {
        _store = new PluginPackageStore(rootDirectory);
        _http = http;
        Directory.CreateDirectory(_store.Root);
    }

    /// <summary>Last observed package state; refreshed by every lifecycle call. Never blocks.</summary>
    public IReadOnlyList<InstalledPluginPackage> InstalledSnapshot => _snapshot;

    public async Task<IReadOnlyList<InstalledPluginPackage>> GetInstalledAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var result = new List<InstalledPluginPackage>();
            foreach (var id in _store.Ids()) result.Add(await _store.ReadAsync(id, ct));
            _snapshot = result.ToArray();
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<InstalledPluginPackage> StageInstallAsync(PluginPackageManifest manifest, CancellationToken ct = default)
    {
        PluginPackageValidation.Validate(manifest);
        await _gate.WaitAsync(ct);
        var temporary = Path.Combine(_store.Root, ".stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = await _store.ReadAsync(manifest.Id, ct);
            foreach (var existing in new[] { state.Active, state.Previous, state.Pending })
                if (existing?.Version == manifest.Version &&
                    System.Text.Json.JsonSerializer.Serialize(existing, PluginPackageValidation.JsonOptions) !=
                    System.Text.Json.JsonSerializer.Serialize(manifest, PluginPackageValidation.JsonOptions))
                    throw new InvalidDataException("A fixed release version cannot be replaced.");
            var bytes = await DownloadAsync(manifest.DownloadUrl, ct);
            Directory.CreateDirectory(temporary);
            await PluginPackageArchive.ExtractAsync(bytes, manifest, temporary, ct);
            var archive = _store.ArchivePath(manifest);
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            if (!File.Exists(archive))
            {
                var stagedArchive = Path.Combine(temporary, "archive.tmp");
                await File.WriteAllBytesAsync(stagedArchive, bytes, ct);
                File.Move(stagedArchive, archive);
            }
            var next = state with { Pending = manifest, PendingUninstall = false };
            await _store.SaveAsync(next, ct);
            Publish(next);
            return next;
        }
        finally
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
            finally { _gate.Release(); }
        }
    }

    public async Task<InstalledPluginPackage> StageUninstallAsync(string id, CancellationToken ct = default)
    {
        PluginPackageValidation.ValidateId(id);
        await _gate.WaitAsync(ct);
        try
        {
            var state = await _store.ReadAsync(id, ct);
            if (state.Active == null && state.Pending == null) throw new InvalidOperationException("Package is not installed.");
            // 保留清单直到重启：否则“已暂存卸载”的包在面板里会整条消失，用户看不到待处理状态。
            // 启动时的卸载分支不加载 Pending，只把它清掉，所以保留不改变生效语义。
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
                    if (plugin != null) loaded.Add(plugin);
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
        CancellationToken ct, Func<PluginPackageManifest, string?>? resolveSettings = null)
    {
        var archive = _store.ArchivePath(manifest);
        if (new FileInfo(archive).Length > PluginPackageArchive.MaxArchiveBytes) throw new InvalidDataException("Archive too large.");
        var bytes = await File.ReadAllBytesAsync(archive, ct);
        var runtime = Path.Combine(_store.Root, ".runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtime);
        try
        {
            await PluginPackageArchive.ExtractAsync(bytes, manifest, runtime, ct);
            var config = Path.Combine(_store.PackageDirectory(manifest.Id), "config");
            Directory.CreateDirectory(config);
            return await PluginPackageLoader.StartAsync(runtime, config, manifest, context, ct, resolveSettings?.Invoke(manifest));
        }
        catch { Directory.Delete(runtime, recursive: true); throw; }
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
