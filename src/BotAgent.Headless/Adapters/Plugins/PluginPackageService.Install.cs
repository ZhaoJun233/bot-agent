using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

public sealed partial class PluginPackageService
{
    /// <summary>Stages an offline host distribution release. This never enables a plugin or
    /// executes its code. Ordinary import APIs still reject all reserved identities.</summary>
    public Task<InstalledPluginPackage> StageDistributionInstallAsync(PluginPackageManifest manifest, byte[] archive,
        CancellationToken ct = default)
    {
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<PluginPackageManifest>(
            System.Text.Json.JsonSerializer.Serialize(manifest, PluginPackageValidation.JsonOptions), PluginPackageValidation.JsonOptions)!;
        _store.Distribution.Validate(snapshot);
        _store.Distribution.EnsureApproved(snapshot);
        if (archive.Length > PluginPackageArchive.MaxArchiveBytes) throw new InvalidDataException("Archive too large.");
        return StageInstallCoreAsync(snapshot, null, false, ct, distributionArchive: archive.ToArray());
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _stagedDistributionBytes = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _pendingUpgradePrune = new(StringComparer.Ordinal);

    internal void ClearStagedDistributionBytes() => _stagedDistributionBytes.Clear();

    internal Task<HostDistributionUpgradeCandidate?> TryReadUnapprovedHostDistributionAsync(string id, CancellationToken ct)
        => _store.TryReadUnapprovedHostDistributionAsync(id, ct);

    /// <summary>
    /// Controlled host-authority upgrade for a reserved built-in distribution package whose persisted envelope
    /// came from an older host build. Allows replacing an identical version with a newly compiled host-approved
    /// SHA-256 strictly on this internal path while preserving governance revocation checks and uninstall intent.
    /// </summary>
    internal async Task<InstalledPluginPackage> StageHostDistributionUpgradeAsync(
        PluginPackageManifest manifest, byte[]? archiveBytes, bool keepUninstalled, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!PluginPackageValidation.IsReservedId(manifest.Id))
            throw new InvalidDataException("Host distribution upgrade requires a reserved package identity.");
        _store.Distribution.Validate(manifest);
        _store.Distribution.EnsureApproved(manifest);
        _resources.EnsureApproved(manifest);
        if (!keepUninstalled)
        {
            ArgumentNullException.ThrowIfNull(archiveBytes);
            if (archiveBytes.Length > PluginPackageArchive.MaxArchiveBytes)
                throw new InvalidDataException("Archive too large.");
            Governance.EnsureAllowed(manifest);
            await PluginPackageArchive.ValidateAsync(archiveBytes, manifest, ct).ConfigureAwait(false);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var temporary = Path.Combine(_store.Root, ".stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var candidate = await _store.TryReadUnapprovedHostDistributionAsync(manifest.Id, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("plugin_distribution_upgrade_not_eligible");
            if (candidate.KeepUninstalled != keepUninstalled)
                throw new InvalidDataException("plugin_distribution_upgrade_state_changed");

            if (keepUninstalled)
            {
                var cleared = new InstalledPluginPackage(manifest.Id);
                await _store.SaveAsync(cleared, ct).ConfigureAwait(false);
                Publish(cleared);
                _store.PruneUnreferencedArchives(cleared);
                return cleared;
            }

            Directory.CreateDirectory(temporary);
            Governance.EnsureAllowed(manifest);
            using var governanceCommit = await Governance.AcquireCommitAsync(manifest, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!_store.HasArchive(manifest))
            {
                var stagedArchive = await _store.StageArchiveAsync(temporary, archiveBytes!, ct).ConfigureAwait(false);
                _store.CommitArchive(manifest, stagedArchive, overwrite: false);
            }
            var next = new InstalledPluginPackage(manifest.Id)
            {
                Active = null,
                Previous = null,
                Pending = manifest,
                PendingUninstall = false
            };
            await _store.SaveAsync(next, ct).ConfigureAwait(false);
            _stagedDistributionBytes[manifest.Id + ":" + manifest.Sha256] = archiveBytes!;
            _pendingUpgradePrune[manifest.Id] = 0;
            Publish(next);
            return next;
        }
        finally
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); } catch { }
            _gate.Release();
        }
    }

    internal async Task<IReadOnlyList<(string Id, bool Succeeded)>> StageDistributionBatchAsync(
        IReadOnlyList<(PluginPackageManifest Manifest, byte[] Archive)> items, CancellationToken ct)
    {
        if (items.Count == 0) return [];
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var staged = new InstalledPluginPackage?[items.Count];
        try
        {
            var results = new (string Id, bool Succeeded)[items.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, items.Count),
                new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
                async (index, token) =>
                {
                    var (snapshot, bytes) = items[index];
                    var temporary = Path.Combine(_store.Root, ".stage-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        _store.Distribution.Validate(snapshot);
                        _store.Distribution.EnsureApproved(snapshot);
                        if (bytes.Length > PluginPackageArchive.MaxArchiveBytes) throw new InvalidDataException("Archive too large.");
                        _resources.EnsureApproved(snapshot);
                        Governance.EnsureAllowed(snapshot);
                        var state = await _store.ReadAsync(snapshot.Id, token).ConfigureAwait(false);
                        foreach (var existing in new[] { state.Active, state.Previous, state.Pending })
                            if (existing?.Version == snapshot.Version &&
                                System.Text.Json.JsonSerializer.Serialize(existing, PluginPackageValidation.JsonOptions) !=
                                System.Text.Json.JsonSerializer.Serialize(snapshot, PluginPackageValidation.JsonOptions))
                                throw new InvalidDataException("A fixed release version cannot be replaced.");
                        await PluginPackageArchive.ValidateAsync(bytes, snapshot, token).ConfigureAwait(false);
                        Directory.CreateDirectory(temporary);
                        Governance.EnsureAllowed(snapshot);
                        using var governanceCommit = await Governance.AcquireCommitAsync(snapshot, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (!_store.HasArchive(snapshot))
                        {
                            var stagedArchive = await _store.StageArchiveAsync(temporary, bytes, token).ConfigureAwait(false);
                            _store.CommitArchive(snapshot, stagedArchive, overwrite: false);
                        }
                        var next = state with { Pending = snapshot, PendingUninstall = false };
                        await _store.SaveAsync(next, token).ConfigureAwait(false);
                        _stagedDistributionBytes[snapshot.Id + ":" + snapshot.Sha256] = bytes;
                        staged[index] = next;
                        results[index] = (snapshot.Id, true);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch
                    {
                        results[index] = (snapshot.Id, false);
                    }
                    finally
                    {
                        try { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); } catch { }
                    }
                }).ConfigureAwait(false);
            return results;
        }
        finally
        {
            foreach (var next in staged)
                if (next is not null) Publish(next);
            _gate.Release();
        }
    }

    private readonly AsyncLocal<string?> _preparedInstallId = new();

    // Only the authorized configuration callback and its awaited hot-apply flow see this ID.
    internal string? PreparedInstallId => _preparedInstallId.Value;

    private async Task ConfigurePreparedInstallAsync(string id,
        Func<CancellationToken, Task> configure, CancellationToken ct)
    {
        var previous = _preparedInstallId.Value;
        _preparedInstallId.Value = id;
        try { await configure(ct).ConfigureAwait(false); }
        finally { _preparedInstallId.Value = previous; }
    }

    private async Task<InstalledPluginPackage> StageInstallCoreAsync(PluginPackageManifest manifest, string? expectedRevision,
        bool requireInstalled, CancellationToken ct, Func<CancellationToken, Task>? beforePublish = null,
        byte[]? distributionArchive = null)
    {
        if (distributionArchive is null) PluginPackageValidation.Validate(manifest);
        else _store.Distribution.EnsureApproved(manifest);
        _resources.EnsureApproved(manifest);
        if (distributionArchive is null) await Governance.RefreshAsync(ct).ConfigureAwait(false);
        Governance.EnsureAllowed(manifest);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var held = true;
        var temporary = Path.Combine(_store.Root, ".stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            await EnsureRevisionLockedAsync(expectedRevision, ct).ConfigureAwait(false);
            var state = await _store.ReadAsync(manifest.Id, ct).ConfigureAwait(false);
            if (beforePublish is not null && (state.Active is not null || state.Pending is not null || state.PendingUninstall))
                throw PluginImportSnapshots.Refresh();
            if (requireInstalled && state.Active is null && state.Pending is null)
                throw new BotAgent.Panel.ZbaBackendException(404, "plugin_not_installed", "这个插件还没有安装，无法更新。");
            foreach (var existing in new[] { state.Active, state.Previous, state.Pending })
                if (existing?.Version == manifest.Version &&
                    System.Text.Json.JsonSerializer.Serialize(existing, PluginPackageValidation.JsonOptions) !=
                    System.Text.Json.JsonSerializer.Serialize(manifest, PluginPackageValidation.JsonOptions))
                    throw new InvalidDataException("A fixed release version cannot be replaced.");
            var bytes = distributionArchive ?? await DownloadAsync(manifest.DownloadUrl, ct).ConfigureAwait(false);
            await PluginPackageArchive.ValidateAsync(bytes, manifest, ct).ConfigureAwait(false);
            Directory.CreateDirectory(temporary);
            if (distributionArchive is null) await Governance.RefreshAsync(ct).ConfigureAwait(false);
            Governance.EnsureAllowed(manifest);
            if (beforePublish is not null)
            {
                // Hot apply reenters runtime/package operations. Keep unreferenced files outside ID directories
                // so this staging operation cannot itself change the revision observed by the callback.
                _gate.Release();
                held = false;
                await ConfigurePreparedInstallAsync(manifest.Id, beforePublish, ct).ConfigureAwait(false);
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                held = true;
                await EnsureRevisionLockedAsync(expectedRevision, ct).ConfigureAwait(false);
                state = await _store.ReadAsync(manifest.Id, ct).ConfigureAwait(false);
                if (state.Active is not null || state.Pending is not null || state.PendingUninstall)
                    throw PluginImportSnapshots.Refresh();
            }
            if (distributionArchive is null) await Governance.RefreshAsync(ct).ConfigureAwait(false);
            using var governanceCommit = await Governance.AcquireCommitAsync(manifest, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!_store.HasArchive(manifest))
            {
                var stagedArchive = await _store.StageArchiveAsync(temporary, bytes, ct).ConfigureAwait(false);
                _store.CommitArchive(manifest, stagedArchive, overwrite: false);
            }
            var next = state with { Pending = manifest, PendingUninstall = false };
            await _store.SaveAsync(next, ct).ConfigureAwait(false);
            Publish(next);
            return next;
        }
        finally
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
            finally { if (held) _gate.Release(); }
        }
    }
}
