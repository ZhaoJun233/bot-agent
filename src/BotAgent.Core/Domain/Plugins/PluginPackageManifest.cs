namespace BotAgent.Domain.Plugins;

/// <summary>An approved, immutable release descriptor. Sha256 pins the complete ZIP bytes.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(PluginManifestJsonConverter))]
public sealed record PluginPackageManifest(
    string Id, string Version, string? EntryAssembly, string? EntryType,
    string Sha256, string DownloadUrl, int ApiVersion = 1,
    string? TargetFramework = PluginPackageManifest.CurrentTargetFramework, string? MinimumHostVersion = null,
    IReadOnlyList<string>? Permissions = null, IReadOnlyList<PluginPackageDependency>? Dependencies = null,
    IReadOnlyList<PluginSettingDefinition>? Settings = null, PluginPythonRuntime? Runtime = null)
{
    /// <summary>Optional exact managed contracts; omitted by legacy manifests.</summary>
    public IReadOnlyList<PluginSharedContract>? SharedContracts { get; init; }
    public PluginPackageData? Data { get; init; }
    public bool Control { get; init; }
    /// <summary>Opt-in managed tool contribution protocol; absent means no chat tool contribution.</summary>
    public int? ToolApiVersion { get; init; }
    public PluginConfigurationDeclaration? Configuration { get; init; }
    /// <summary>Optional packaged UI bundle served read-only by exact release hash and mounted in a sandboxed iframe.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PluginUiDeclaration? Ui { get; init; }
    /// <summary>Explicit adoption mode: <c>cold</c> or <c>online</c>. Exclusive resources require <c>cold</c>.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Adoption { get; init; }
    /// <summary>Declared exclusive external resources (e.g. protocol connections, listening ports, scheduled senders).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ExclusiveResources { get; init; }

    /// <summary>宿主当前构建的目标框架，也是新清单的默认值。</summary>
    public const string CurrentTargetFramework = "net10.0";

    /// <summary>
    /// 宿主可以加载的插件目标框架。.NET 8 程序集在 .NET 10 运行时上直接运行（前滚），
    /// 因此升级宿主不淘汰已发布的 .NET 8 插件；其它框架一律拒绝。
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedTargetFrameworks = ["net8.0", CurrentTargetFramework];
}

public sealed record PluginPackageDependency(string Id, string Version);

/// <summary>An exact managed contract identity. Owner is this package or a direct fixed dependency.</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginSharedContract(string Name, string Version, string Sha256, string Owner);

/// <summary>Restart-bound state; configuration and previous release survive removal.</summary>
public sealed record InstalledPluginPackage(
    string Id, PluginPackageManifest? Active = null, PluginPackageManifest? Previous = null,
    PluginPackageManifest? Pending = null, bool PendingUninstall = false)
{
    /// <summary>Read-only directory observation, never persisted as package state.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PluginPackageMetadataFailure? MetadataFailure { get; init; }
}

/// <summary>Safe diagnostic only; no raw metadata, paths, or exception messages.</summary>
public sealed record PluginPackageMetadataFailure(string Id, string Code, string Fingerprint)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public PluginPackageRecovery? Recovery { get; init; }
}

public sealed record PluginPackageFailure(string Id, string Code);

public sealed record PluginPackageLoadResult(
    IReadOnlyList<IBotPlugin> Loaded, IReadOnlyList<PluginPackageFailure> Failures);
