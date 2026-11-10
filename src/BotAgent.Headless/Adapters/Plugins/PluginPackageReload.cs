using BotAgent.Domain.Plugins;
using BotAgent.Services.Plugins;

namespace BotAgent.Adapters.Plugins;

public sealed partial class PluginPackageService
{
    /// <summary>
    /// Prepares a private shadow generation and transfers ownership only at registry publication.
    /// The registry drains and shuts down the old generation before this method completes.
    /// Disabled generations are validated metadata only; their Initialize is never called.
    /// </summary>
    public async Task<PluginPackageManifest?> ApplyAsync(string id, PluginContext context, PluginManager registry,
        bool enabled, CancellationToken ct = default, Func<PluginPackageManifest, string?>? resolveSettings = null,
        bool activeFallback = false)
    {
        PluginPackageValidation.ValidateId(id);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        IBotPlugin? candidate = null;
        InstalledPluginPackage? original = null;
        var saved = false;
        try
        {
            original = await _store.ReadAsync(id, ct).ConfigureAwait(false);
            if (original.PendingUninstall)
            {
                var removed = original with { Previous = original.Active ?? original.Previous,
                    Active = null, Pending = null, PendingUninstall = false };
                // Save before publication: a failed durable write cannot retire the working generation.
                await _store.SaveAsync(removed, ct).ConfigureAwait(false);
                saved = true;
                await registry.RemoveAsync(id, ct).ConfigureAwait(false);
                saved = false;
                Publish(removed);
                return null;
            }

            var manifest = activeFallback ? original.Active : original.Pending ?? original.Active;
            if (manifest is null) return null;
            PluginPackageValidation.Validate(manifest);
            if (registry.Find(id)?.IsPreset == true) throw new InvalidDataException("Package ID already registered.");
            candidate = await StartReleaseAsync(manifest, context, ct, resolveSettings, enabled).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var next = !activeFallback && original.Pending is not null
                ? original with { Previous = PluginLifecycleManifest.Fingerprint(original.Active) == PluginLifecycleManifest.Fingerprint(manifest)
                    ? original.Previous : original.Active ?? original.Previous, Active = manifest, Pending = null }
                : original;
            await _store.SaveAsync(next, ct).ConfigureAwait(false);
            saved = true;
            // Replace guarantees no exception after ownership transfer, even if old Shutdown fails.
            await registry.ReplaceAsync(candidate, enabled, ct).ConfigureAwait(false);
            if (candidate is PluginPythonPackage python) python.Publish();
            candidate = null;
            saved = false;
            Publish(next);
            return manifest;
        }
        catch
        {
            if (saved && original is not null)
            {
                await _store.SaveAsync(original, CancellationToken.None).ConfigureAwait(false);
                Publish(original);
            }
            throw;
        }
        finally
        {
            if (candidate is not null)
                try { await candidate.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            _gate.Release();
        }
    }
}