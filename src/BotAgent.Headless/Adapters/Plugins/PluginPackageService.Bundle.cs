using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

public sealed partial class PluginPackageService
{
    internal Task<byte[]> ReadReleaseArchiveAsync(PluginPackageManifest manifest, CancellationToken ct)
        => _store.ReadArchiveAsync(manifest, ct);

    /// <summary>
    /// Atomically stages a multi-package dependency combo (e.g. C1/P1 -> C2/P2).
    /// Downloads and validates EVERY archive before writing ANY durable package state;
    /// any pre-commit failure leaves the entire previous combo untouched.
    /// </summary>
    internal async Task<IReadOnlyList<InstalledPluginPackage>> StageBundleAsync(
        IReadOnlyList<PluginPackageManifest> targets,
        string expectedRevision,
        Func<IReadOnlyList<InstalledPluginPackage>, CancellationToken, Task> guard,
        CancellationToken ct)
    {
        if (targets.Count is 0 or > 16) throw new InvalidDataException("plugin_bundle_size_invalid");
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            if (target is null || !seenIds.Add(target.Id))
                throw new InvalidDataException("plugin_bundle_duplicate_id");
            PluginPackageValidation.Validate(target);
            _resources.EnsureApproved(target);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var temp = Path.Combine(_store.Root, ".stage-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            await EnsureRevisionLockedAsync(expectedRevision, ct).ConfigureAwait(false);
            var installed = await ReadInstalledLockedAsync(ct).ConfigureAwait(false);
            var byId = installed.ToDictionary(p => p.Id, StringComparer.Ordinal);

            // Validate every target against current package state & download/verify all archives BEFORE any durable write.
            var stagedItems = new List<(PluginPackageManifest Target, InstalledPluginPackage Current, string StagedArchive)>();
            await Governance.RefreshAsync(ct).ConfigureAwait(false);
            Directory.CreateDirectory(temp);
            foreach (var target in targets)
            {
                Governance.EnsureAllowed(target);
                byId.TryGetValue(target.Id, out var current);
                if (current?.MetadataFailure is not null)
                    throw new InvalidDataException(current.MetadataFailure.Code);
                if (current?.Pending is not null || current?.PendingUninstall == true)
                    throw new InvalidDataException("plugin_bundle_member_has_pending_change");

                byte[] bytes;
                if (current?.Previous is { } prev && PluginLifecycleManifest.Fingerprint(prev) == PluginLifecycleManifest.Fingerprint(target)
                    && _store.HasArchive(target))
                    bytes = await _store.ReadArchiveAsync(target, ct).ConfigureAwait(false);
                else
                    bytes = await DownloadAsync(target.DownloadUrl, ct).ConfigureAwait(false);

                foreach (var existing in new[] { current?.Active, current?.Previous })
                    if (existing?.Version == target.Version
                        && PluginLifecycleManifest.Fingerprint(existing) != PluginLifecycleManifest.Fingerprint(target))
                        throw new InvalidDataException("A fixed release version cannot be replaced.");

                var itemTemp = Path.Combine(temp, target.Id);
                Directory.CreateDirectory(itemTemp);
                await PluginPackageArchive.ExtractAsync(bytes, target, itemTemp, ct).ConfigureAwait(false);
                var stagedPath = await _store.StageArchiveAsync(itemTemp, bytes, ct).ConfigureAwait(false);
                stagedItems.Add((target, current ?? new InstalledPluginPackage(target.Id), stagedPath));
            }

            await guard(installed, ct).ConfigureAwait(false);
            await EnsureRevisionLockedAsync(expectedRevision, ct).ConfigureAwait(false);
            await Governance.RefreshAsync(ct).ConfigureAwait(false);
            using var governanceCommit = await Governance.AcquireCommitBatchAsync(
                stagedItems.Select(item => item.Target), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Commit phase: archives first, then durable installed.json entries in deterministic order with rollback on mid-loop failure.
            var ordered = stagedItems.OrderBy(i => i.Target.Id, StringComparer.Ordinal).ToArray();
            foreach (var (target, _, stagedArchive) in ordered)
                _store.CommitArchive(target, stagedArchive, overwrite: true);

            var results = new List<InstalledPluginPackage>(ordered.Length);
            var committed = new List<InstalledPluginPackage>(ordered.Length);
            try
            {
                foreach (var (target, current, _) in ordered)
                {
                    var next = current with { Pending = target, PendingUninstall = false };
                    await _store.SaveAsync(next, CancellationToken.None).ConfigureAwait(false);
                    committed.Add(current);
                    results.Add(next);
                }
            }
            catch
            {
                foreach (var previous in committed)
                {
                    try
                    {
                        await _store.SaveAsync(previous, CancellationToken.None).ConfigureAwait(false);
                        Publish(previous);
                    }
                    catch { /* Best-effort rollback of already-written bundle entries. */ }
                }
                throw;
            }
            foreach (var next in results) Publish(next);
            return results;
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
            finally { _gate.Release(); }
        }
    }
}
