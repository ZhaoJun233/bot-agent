using System.Text.Json.Serialization;
using BotAgent.Domain.Plugins;

namespace BotAgent.Panel;

/// <summary>
/// 插件市场的一条目录项。展示字段（名称/说明/分类/作者）是可选的，前端缺失时回落到 manifest.id；
/// 这里刻意不伪造它们，只有 manifest 与真实安装态。
/// </summary>
public sealed record ZbaPluginCatalogEntry(
    [property: JsonRequired] PluginPackageManifest Manifest,
    string? InstalledVersion,
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] string Status,
    [property: JsonRequired] bool RestartRequired)
{
    public ZbaPluginMetadata? Metadata { get; init; }
    public override string ToString() => "ZbaPluginCatalogEntry";
}

public sealed record ZbaPluginCatalog(
    [property: JsonRequired] string Revision,
    [property: JsonRequired] string Source,
    [property: JsonRequired] string TrustNotice,
    [property: JsonRequired] IReadOnlyList<string> Categories,
    [property: JsonRequired] IReadOnlyList<ZbaPluginCatalogEntry> Plugins)
{
    public IReadOnlyList<ZbaBuiltinPlugin> BuiltinPlugins { get; init; } = [];
    public string? MetadataNotice { get; init; }
    public override string ToString() => "ZbaPluginCatalog";
}

public sealed record ZbaInstalledPlugins(
    [property: JsonRequired] string Revision,
    [property: JsonRequired] IReadOnlyList<ZbaPluginCatalogEntry> Plugins)
{
    public IReadOnlyList<ZbaBuiltinPlugin> BuiltinPlugins { get; init; } = [];
    public override string ToString() => "ZbaInstalledPlugins";
}

/// <summary>内置插件来自宿主注册表，不是可下载或安装的包。</summary>
public sealed record ZbaBuiltinPlugin(string PluginId, string Version, bool Enabled, ZbaPluginMetadata Metadata);

/// <summary>仅用于展示；来源、运行时与审核状态相互独立，不授予执行权限。</summary>
public sealed record ZbaPluginMetadata(
    [property: JsonRequired] string Name, [property: JsonRequired] string Summary,
    [property: JsonRequired] string Description, [property: JsonRequired] string Category,
    [property: JsonRequired] string Author, [property: JsonRequired] string Repository,
    [property: JsonRequired] string License, [property: JsonRequired] string Usage,
    [property: JsonRequired] string RiskNotice, [property: JsonRequired] string Configuration,
    [property: JsonRequired] string Compatibility, [property: JsonRequired] string Changelog,
    [property: JsonRequired] string Maintenance, [property: JsonRequired] string PermissionNotes,
    [property: JsonRequired] IReadOnlyList<string> Tags, [property: JsonRequired] string Runtime,
    [property: JsonRequired] string SourceKind, [property: JsonRequired] ZbaPluginReview Review);

public sealed record ZbaPluginReview(
    [property: JsonRequired] string Status, [property: JsonRequired] string Version,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Sha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reviewer = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceRevision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Notes = null);

/// <summary>只接受公开 GitHub 仓库或其固定 release tag；真正的解析在宿主侧完成。</summary>
public sealed record ZbaPluginSourceResolveRequest([property: JsonRequired] string SourceUrl)
{
    public override string ToString() => "ZbaPluginSourceResolveRequest";
}

public sealed record ZbaPluginSourceResolution(
    [property: JsonRequired] PluginPackageManifest Manifest,
    [property: JsonRequired] string TrustNotice)
{
    public override string ToString() => "ZbaPluginSourceResolution";
}

public sealed record ZbaPluginMutationRequest(
    [property: JsonRequired] PluginPackageManifest Manifest,
    [property: JsonRequired] string ExpectedRevision,
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] bool TrustAcknowledged)
{
    public override string ToString() => "ZbaPluginMutationRequest";
}

public sealed record ZbaPluginUninstallRequest(
    [property: JsonRequired] string ExpectedRevision,
    [property: JsonRequired] string IdempotencyKey)
{
    public override string ToString() => "ZbaPluginUninstallRequest";
}

/// <summary>一次插件变更的回执：<c>status</c> 只有 staged / restart_required / failed。</summary>
public sealed record ZbaPluginOperation(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string PluginId,
    [property: JsonRequired] string Kind,
    [property: JsonRequired] string Status,
    [property: JsonRequired] bool RestartRequired,
    string? ErrorCode)
{
    public override string ToString() => "ZbaPluginOperation";
}

public sealed record ZbaPluginOperationResult(
    [property: JsonRequired] ZbaPluginOperation Operation,
    [property: JsonRequired] string Revision)
{
    public override string ToString() => "ZbaPluginOperationResult";
}

/// <summary>kind：0 = 保留、1 = 设置、2 = 删除。保留项不带 value。</summary>
public sealed record ZbaPluginSettingEdit(
    [property: JsonRequired] string Key,
    [property: JsonRequired] int Kind,
    string? Value)
{
    public override string ToString() => "ZbaPluginSettingEdit";
}

public sealed record ZbaPluginSettingsSaveRequest(
    [property: JsonRequired] string ExpectedRevision,
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] IReadOnlyList<ZbaPluginSettingEdit> Edits)
{
    public override string ToString() => "ZbaPluginSettingsSaveRequest";
}

/// <summary>秘密值永不回显：只给出 <c>configuredSecrets</c> 的布尔投影。</summary>
public sealed record ZbaPluginSettings(
    [property: JsonRequired] string Revision,
    [property: JsonRequired] IReadOnlyList<PluginSettingDefinition> Schema,
    [property: JsonRequired] IReadOnlyDictionary<string, string> Values,
    [property: JsonRequired] IReadOnlyDictionary<string, bool> ConfiguredSecrets,
    [property: JsonRequired] bool RestartRequired)
{
    public override string ToString() => "ZbaPluginSettings";
}

/// <summary>模型发现：失败也只回 <c>errorCode</c>，绝不 5xx，前端始终保留手工填写。</summary>
public sealed record ZbaProviderModels(
    [property: JsonRequired] IReadOnlyList<string> Models,
    string? ErrorCode)
{
    public override string ToString() => "ZbaProviderModels";
}
