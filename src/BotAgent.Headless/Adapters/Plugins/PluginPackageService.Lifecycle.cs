using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

public sealed partial class PluginPackageService
{
    internal bool HasCachedArchive(PluginPackageManifest manifest) => _store.HasArchive(manifest);

    // Guard runs inside the package lock after artifact validation, immediately before durable Pending.
    // It receives the locked snapshot so it never reenters GetInstalledAsync and cannot deadlock.
    internal async Task<InstalledPluginPackage> StageLifecycleAsync(string kind, string id,
        PluginPackageManifest? target, string expectedRevision,
        Func<IReadOnlyList<InstalledPluginPackage>, CancellationToken, Task> guard, CancellationToken ct, string recovery = "none")
    {
        PluginPackageValidation.ValidateId(id);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var temp = Path.Combine(_store.Root, ".stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            await EnsureRevisionLockedAsync(expectedRevision, ct).ConfigureAwait(false);
            var current = await _store.ReadAsync(id, ct).ConfigureAwait(false);
            if (current.Active is null && !(kind == "uninstall" && recovery == "failed-install" && current.Pending is not null) ||
                current.Pending is not null && recovery == "none" || current.PendingUninstall)
                throw new InvalidDataException("Lifecycle requires an active package without pending changes.");
            byte[]? bytes = null;
            if (kind != "uninstall")
            {
                if (target is null || target.Id != id) throw new InvalidDataException("Lifecycle target is missing.");
                PluginPackageValidation.Validate(target);
                await Governance.RefreshAsync(ct).ConfigureAwait(false);
                Governance.EnsureAllowed(target);
                if (kind == "rollback")
                {
                    var rollback = recovery == "failed-pending" ? current.Active : current.Previous;
                    if (rollback is null || PluginLifecycleManifest.Fingerprint(rollback) != PluginLifecycleManifest.Fingerprint(target))
                        throw new InvalidDataException("Rollback target must be the persisted previous manifest.");
                    bytes = await _store.ReadArchiveAsync(target, ct).ConfigureAwait(false);
                }
                else bytes = await DownloadAsync(target.DownloadUrl, ct).ConfigureAwait(false);
                foreach (var existing in new[] { current.Active, current.Previous })
                    if (existing?.Version == target.Version && PluginLifecycleManifest.Fingerprint(existing) != PluginLifecycleManifest.Fingerprint(target))
                        throw new InvalidDataException("A fixed release version cannot be replaced.");
                Directory.CreateDirectory(temp);
                await PluginPackageArchive.ExtractAsync(bytes, target, temp, ct).ConfigureAwait(false);
            }
            var stagedArchive = kind == "update"
                ? await _store.StageArchiveAsync(temp, bytes!, ct).ConfigureAwait(false) : null;
            await guard(await ReadInstalledLockedAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            await EnsureRevisionLockedAsync(expectedRevision, ct).ConfigureAwait(false);
            if (target is not null) await Governance.RefreshAsync(ct).ConfigureAwait(false);
            using var governanceCommit = target is null ? null : await Governance.AcquireCommitAsync(target, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (kind == "update") _store.CommitArchive(target!, stagedArchive!, overwrite: true);
            var staged = kind == "uninstall" ? current with { PendingUninstall = true } :
                current with { Pending = target, PendingUninstall = false };
            await _store.SaveAsync(staged, ct).ConfigureAwait(false);
            Publish(staged);
            return staged;
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
            finally { _gate.Release(); }
        }
    }
}