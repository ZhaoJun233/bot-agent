using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

/// <summary>Resolve exact dependencies against package state before managed built-in fallbacks.</summary>
internal static class PluginDependencyPolicy
{
    internal static bool AreSatisfied(PluginPackageManifest manifest,
        IReadOnlyList<InstalledPluginPackage> installed, IReadOnlyList<PluginInfo> registered)
        => (manifest.Dependencies ?? []).All(dependency =>
        {
            var package = installed.FirstOrDefault(item => item.Id == dependency.Id);
            if (package is not null)
                return package.Pending is null && !package.PendingUninstall
                    && package.Active?.Version == dependency.Version
                    && package.Active.ApiVersion == manifest.ApiVersion;
            // PluginInfo carries no runtime metadata; only managed presets are a known fallback.
            return manifest.ApiVersion == 1 && registered.Any(item => item.IsPreset
                && item.Id == dependency.Id && item.Version == dependency.Version);
        });
}
