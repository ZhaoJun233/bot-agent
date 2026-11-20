using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

/// <summary>Host-owned approvals, never constructed from installed.json or a package request.
/// Each approval pins the complete manifest, including resources and archive digest.</summary>
public sealed class PluginResourceAuthorizations
{
    private readonly HashSet<string> _fingerprints;
    public static PluginResourceAuthorizations Empty { get; } = new([]);

    public PluginResourceAuthorizations(IEnumerable<PluginPackageManifest> approvedReleases)
    {
        _fingerprints = new(StringComparer.Ordinal);
        var tables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var prefixes = new List<(string Prefix, string Owner)>();
        foreach (var manifest in approvedReleases)
        {
            PluginPackageValidation.Validate(manifest, checkCompatibility: true, allowReserved: true);
            if (manifest.Control || manifest.Configuration is not null || PluginActivationWeb.Requested(manifest) || PluginActivationModel.Requested(manifest)
                || PluginActivationExecution.Requested(manifest)) _fingerprints.Add(PluginLifecycleManifest.Fingerprint(manifest));
            if (manifest.Data is not { } data) continue;
            foreach (var table in data.Tables)
            {
                if (tables.TryGetValue(table, out var owner) && owner != manifest.Id)
                    throw new InvalidDataException("plugin_data_owner_conflict");
                tables[table] = manifest.Id;
            }
            foreach (var prefix in data.StatePrefixes)
            {
                if (prefixes.Any(p => p.Owner != manifest.Id && (p.Prefix.StartsWith(prefix, StringComparison.Ordinal)
                    || prefix.StartsWith(p.Prefix, StringComparison.Ordinal))))
                    throw new InvalidDataException("plugin_data_owner_conflict");
                prefixes.Add((prefix, manifest.Id));
            }
            _fingerprints.Add(PluginLifecycleManifest.Fingerprint(manifest));
        }
    }

    internal void EnsureApproved(PluginPackageManifest manifest)
    {
        if (manifest.Configuration is not null && !_fingerprints.Contains(PluginLifecycleManifest.Fingerprint(manifest)))
            throw new InvalidOperationException("plugin_configuration_not_authorized");
        if (PluginActivationModel.Requested(manifest) && !_fingerprints.Contains(PluginLifecycleManifest.Fingerprint(manifest)))
            throw new InvalidOperationException("plugin_model_not_authorized");
        if (PluginActivationExecution.Requested(manifest) && !_fingerprints.Contains(PluginLifecycleManifest.Fingerprint(manifest)))
            throw new InvalidOperationException("plugin_tool_execution_not_authorized");
        if (PluginActivationWeb.Requested(manifest) && !_fingerprints.Contains(PluginLifecycleManifest.Fingerprint(manifest)))
            throw new InvalidOperationException("plugin_web_not_authorized");
        if ((manifest.Data is not null || manifest.Control) && !_fingerprints.Contains(PluginLifecycleManifest.Fingerprint(manifest)))
            throw new InvalidOperationException("plugin_data_not_authorized");
    }
}
