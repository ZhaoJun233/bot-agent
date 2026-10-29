namespace BotAgent.Domain.Plugins;

/// <summary>An approved, immutable release descriptor. Sha256 pins the complete ZIP bytes.</summary>
public sealed record PluginPackageManifest(
    string Id, string Version, string EntryAssembly, string EntryType,
    string Sha256, string DownloadUrl, int ApiVersion = 1,
    string TargetFramework = "net8.0", string? MinimumHostVersion = null,
    IReadOnlyList<string>? Permissions = null, IReadOnlyList<PluginPackageDependency>? Dependencies = null,
    IReadOnlyList<PluginSettingDefinition>? Settings = null);

public sealed record PluginPackageDependency(string Id, string Version);

/// <summary>Declarative fields only; never executable UI or validation scripts.</summary>
public sealed record PluginSettingDefinition(string Key, string Label, string Type,
    string? DefaultValue = null, bool Required = false, IReadOnlyList<string>? Choices = null);

/// <summary>Restart-bound state; configuration and previous release survive removal.</summary>
public sealed record InstalledPluginPackage(
    string Id, PluginPackageManifest? Active = null, PluginPackageManifest? Previous = null,
    PluginPackageManifest? Pending = null, bool PendingUninstall = false);

public sealed record PluginPackageFailure(string Id, string Code);

public sealed record PluginPackageLoadResult(
    IReadOnlyList<IBotPlugin> Loaded, IReadOnlyList<PluginPackageFailure> Failures);
