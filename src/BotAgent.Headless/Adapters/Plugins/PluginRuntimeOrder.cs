using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

/// <summary>Orders the requested transition and its existing consumers. Unrequested Pending
/// releases remain staged; only their currently active generation is rebound.</summary>
internal static class PluginRuntimeOrder
{
    internal sealed record Step(InstalledPluginPackage Package, bool ActiveOnly, bool Cycle);

    internal static IReadOnlyList<Step> Create(IReadOnlyList<InstalledPluginPackage> packages, string? requested,
        Func<string, bool> enabled)
    {
        PluginPackageManifest? Target(InstalledPluginPackage package)
            => requested is null || package.Id == requested ? package.Pending ?? package.Active : package.Active;
        var selected = requested is null ? packages.Select(p => p.Id).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal) { requested };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var package in packages)
                if (Target(package)?.Dependencies?.Any(dependency => selected.Contains(dependency.Id)) == true)
                    changed |= selected.Add(package.Id);
        }
        var byId = packages.Where(package => selected.Contains(package.Id)).ToDictionary(p => p.Id, StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var complete = new Dictionary<string, bool>(StringComparer.Ordinal);
        var steps = new List<Step>();
        bool Visit(InstalledPluginPackage package)
        {
            if (complete.TryGetValue(package.Id, out var valid)) return valid;
            if (!visiting.Add(package.Id)) return false;
            valid = true;
            if ((!package.PendingUninstall || requested is not null && package.Id != requested)
                && package.MetadataFailure is null && (enabled(package.Id) || Target(package)?.Control == true))
                foreach (var dependency in Target(package)?.Dependencies ?? [])
                    if (byId.TryGetValue(dependency.Id, out var provider)) valid &= Visit(provider);
            visiting.Remove(package.Id);
            complete[package.Id] = valid;
            steps.Add(new(package, requested is not null && package.Id != requested, !valid));
            return valid;
        }
        foreach (var package in byId.Values.OrderBy(package => package.Id, StringComparer.Ordinal)) Visit(package);
        return steps;
    }
}
