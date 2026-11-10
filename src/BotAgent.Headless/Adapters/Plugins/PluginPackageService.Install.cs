using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

public sealed partial class PluginPackageService
{
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
        bool requireInstalled, CancellationToken ct, Func<CancellationToken, Task>? beforePublish = null)
    {
        PluginPackageValidation.Validate(manifest);
        await Governance.RefreshAsync(ct).ConfigureAwait(false);
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
            var bytes = await DownloadAsync(manifest.DownloadUrl, ct).ConfigureAwait(false);
            Directory.CreateDirectory(temporary);
            await PluginPackageArchive.ExtractAsync(bytes, manifest, temporary, ct).ConfigureAwait(false);
            await Governance.RefreshAsync(ct).ConfigureAwait(false);
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
            await Governance.RefreshAsync(ct).ConfigureAwait(false);
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