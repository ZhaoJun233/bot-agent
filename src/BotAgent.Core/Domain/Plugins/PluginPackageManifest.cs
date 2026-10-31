namespace BotAgent.Domain.Plugins;

/// <summary>An approved, immutable release descriptor. Sha256 pins the complete ZIP bytes.</summary>
public sealed record PluginPackageManifest(
    string Id, string Version, string EntryAssembly, string EntryType,
    string Sha256, string DownloadUrl, int ApiVersion = 1,
    string TargetFramework = PluginPackageManifest.CurrentTargetFramework, string? MinimumHostVersion = null,
    IReadOnlyList<string>? Permissions = null, IReadOnlyList<PluginPackageDependency>? Dependencies = null,
    IReadOnlyList<PluginSettingDefinition>? Settings = null)
{
    /// <summary>宿主当前构建的目标框架，也是新清单的默认值。</summary>
    public const string CurrentTargetFramework = "net10.0";

    /// <summary>
    /// 宿主可以加载的插件目标框架。.NET 8 程序集在 .NET 10 运行时上直接运行（前滚），
    /// 因此升级宿主不淘汰已发布的 .NET 8 插件；其它框架一律拒绝。
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedTargetFrameworks = ["net8.0", CurrentTargetFramework];
}

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
