using BotAgent.Domain.Plugins;
using BotAgent.Services.Plugins;

namespace BotAgent.Adapters.Plugins;

public sealed partial class PluginPackageService
{
    /// <summary>
    /// Prepares a private shadow generation and transfers ownership only at registry publication.
    /// The registry drains and shuts down the old generation before this method completes.
    /// Disabled business Initialize is never called; approved control preparation has a separate lifetime.
    /// </summary>
    public Task<PluginPackageManifest?> ApplyAsync(string id, PluginContext context, PluginManager registry,
        bool enabled, CancellationToken ct = default, Func<PluginPackageManifest, string?>? resolveSettings = null,
        bool activeFallback = false)
        => ApplyCoreAsync(id, context, registry, enabled, ct, resolveSettings, activeFallback, coldStart: false);

    public Task<PluginPackageManifest?> ApplyAtStartupAsync(string id, PluginContext context, PluginManager registry,
        bool enabled, CancellationToken ct = default, Func<PluginPackageManifest, string?>? resolveSettings = null,
        bool activeFallback = false)
        => ApplyCoreAsync(id, context, registry, enabled, ct, resolveSettings, activeFallback, coldStart: true);

    private async Task<PluginPackageManifest?> ApplyCoreAsync(string id, PluginContext context, PluginManager registry,
        bool enabled, CancellationToken ct, Func<PluginPackageManifest, string?>? resolveSettings,
        bool activeFallback, bool coldStart)
    {
        if (coldStart) _store.Distribution.ValidateId(id);
        else PluginPackageValidation.ValidateId(id);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        IBotPlugin? candidate = null;
        PluginActivationDataStore? data = null;
        PluginActivationWeb? web = null;
        PluginActivationModel? model = null;
        PluginActivationExecution? execution = null;
        PluginActivationConfiguration? configuration = null;
        var resolution = new PluginActivationDependencies();
        var controlResolution = new PluginActivationDependencies();
        PluginActivationDataStore? controlData = null;
        PluginActivationWeb? controlWeb = null;
        PluginActivationModel? controlModel = null;
        PluginActivationExecution? controlExecution = null;
        PluginActivationConfiguration? controlConfiguration = null;
        PluginManager.DependencyLease? dependencies = null;
        InstalledPluginPackage? original = null;
        var saved = false;
        try
        {
            original = await _store.ReadAsync(id, ct).ConfigureAwait(false);
            if (!coldStart && (PluginDataValidation.RequiresColdStart(original.Active)
                || PluginDataValidation.RequiresColdStart(original.Pending)))
                throw new PluginRestartRequiredException();
            if (original.PendingUninstall && !activeFallback)
            {
                var removed = original with { Previous = original.Active ?? original.Previous,
                    Active = null, Pending = null, PendingUninstall = false };
                // Save before publication: a failed durable write cannot retire the working generation.
                await _store.SaveAsync(removed, ct).ConfigureAwait(false);
                saved = true;
                await registry.RemoveAsync(id, ct).ConfigureAwait(false);
                saved = false;
                BotAgent.Adapters.Persistence.PluginSettingsStore.ClearRuntimeState(id);
                Publish(removed);
                return null;
            }

            var manifest = activeFallback ? original.Active : original.Pending ?? original.Active;
            if (manifest is null) return null;
            _store.Distribution.Validate(manifest, allowLegacyOfficialEnvelope: true);
            _resources.EnsureApproved(manifest, allowLegacyOfficialEnvelope: true);
            if (registry.Find(id)?.IsPreset == true) throw new InvalidDataException("Package ID already registered.");
            if ((enabled || manifest.Control) && manifest.Dependencies is { Count: > 0 })
                dependencies = registry.PrepareDependencies(id, manifest.Dependencies, manifest.ApiVersion);
            if ((enabled || manifest.Control) && manifest.Data is not null) data = new PluginActivationDataStore(manifest);
            if (enabled || manifest.Control) web = PluginActivationWeb.Create(manifest, context);
            if (enabled || manifest.Control) model = PluginActivationModel.Create(manifest, context);
            if (enabled || manifest.Control) execution = PluginActivationExecution.Create(manifest, context);
            if ((enabled || manifest.Control) && manifest.Configuration is not null)
                configuration = new PluginActivationConfiguration(manifest,
                    new BotAgent.Adapters.Persistence.PluginSettingsStore(_store.Distribution));
            controlData = manifest.Control ? data?.ControlView() : null;
            controlWeb = manifest.Control ? web?.ControlView() : null;
            controlModel = manifest.Control ? model?.ControlView() : null;
            controlExecution = manifest.Control ? execution?.ControlView() : null;
            controlConfiguration = manifest.Control ? configuration?.ControlView() : null;
            var approvedHostDistribution = _store.Distribution.IsApprovedRelease(manifest)
                || OfficialPresetPolicy.IsApprovedOfficialOrLegacyManifest(manifest);
            var controlContext = manifest.Control
                ? PluginActivationDataStore.Scope(context, controlData, controlWeb, controlModel, controlExecution, controlResolution, controlConfiguration, manifest.Id, approvedHostDistribution)
                : null;
            candidate = await StartReleaseAsync(manifest,
                PluginActivationDataStore.Scope(context, data, web, model, execution, resolution, configuration, manifest.Id, approvedHostDistribution),
                ct, resolveSettings, enabled, coldStart, controlContext).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var next = !activeFallback && original.Pending is not null
                ? original with { Previous = PluginLifecycleManifest.Fingerprint(original.Active) == PluginLifecycleManifest.Fingerprint(manifest)
                    ? original.Previous : original.Active ?? original.Previous, Active = manifest, Pending = null }
                : original;
            if (!ReferenceEquals(next, original))
            {
                await _store.SaveAsync(next, ct).ConfigureAwait(false);
                saved = true;
            }
            // Replace guarantees no exception after ownership transfer, even if old Shutdown fails.
            var invocation = await registry.ReplaceWithBindingsAsync(candidate, enabled, manifest.ApiVersion, gate =>
            {
                data?.Publish(gate);
                web?.Publish(gate);
                model?.Publish(gate);
                execution?.Publish(gate);
                configuration?.Publish(gate);
                resolution.Publish(gate);
                if (manifest.Control)
                {
                    controlData?.Publish(gate.ForControl());
                    controlWeb?.Publish(gate.ForControl());
                    controlModel?.Publish(gate.ForControl());
                    controlExecution?.Publish(gate.ForControl());
                    controlConfiguration?.Publish(gate.ForControl());
                    controlResolution.Publish(gate.ForControl());
                }
            }, dependencies, ct).ConfigureAwait(false);
            var published = candidate;
            candidate = null;
            saved = false;
            if (published is PluginPythonPackage python)
                try { python.Publish(invocation); } catch { }
            Publish(next);
            if (_pendingUpgradePrune.TryRemove(id, out _))
                _store.PruneUnreferencedArchives(next);
            return manifest;
        }
        catch
        {
            if (saved && original is not null)
            {
                try
                {
                    await _store.SaveAsync(original, CancellationToken.None).ConfigureAwait(false);
                    Publish(original);
                }
                catch { /* Preserve the original failure. */ }
            }
            throw;
        }
        finally
        {
            if (candidate is not null)
                try { await candidate.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            dependencies?.Dispose();
            _gate.Release();
        }
    }
}
