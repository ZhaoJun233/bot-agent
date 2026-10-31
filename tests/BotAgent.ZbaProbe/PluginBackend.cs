using System.Collections.Immutable;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

namespace BotAgent.ZbaProbe;

/// <summary>合成插件后端：目录/安装态/回执全部由固定字符串决定，不碰网络、磁盘或数据库。</summary>
internal sealed partial class SyntheticZbaBackend
{
    private const string SyntheticRevision = "p0000000000000000";
    private const string SyntheticPluginId = "community.example.hello";

    internal ZbaPluginMutationRequest? LastInstall { get; private set; }
    internal ZbaPluginUninstallRequest? LastUninstall { get; private set; }
    internal ZbaPluginSettingsSaveRequest? LastSettingsSave { get; private set; }
    internal string? LastProviderKey { get; private set; }

    private static PluginPackageManifest SyntheticManifest => new(
        SyntheticPluginId, "1.0.0", "HelloPlugin.dll", "Marketplace.Examples.HelloPlugin",
        new string('a', 64),
        "https://github.com/example-org/example-plugins/releases/download/" + SyntheticPluginId + "-1.0.0/"
            + SyntheticPluginId + "-1.0.0.zip",
        1, "net10.0", "1.0.0", Array.Empty<string>(), Array.Empty<PluginPackageDependency>(),
        ImmutableArray.Create(new PluginSettingDefinition("greeting", "问候语", "string", "hello"),
            new PluginSettingDefinition("verbose", "详细日志", "boolean", "false"),
            new PluginSettingDefinition("apiKey", "接口密钥", "secret", null, true)));

    private static ZbaPluginCatalogEntry SyntheticEntry(bool installed) => new(SyntheticManifest,
        installed ? "1.0.0" : null, installed, installed ? "installed" : "not_installed", false);

    public ValueTask<ZbaPluginCatalog> ListPluginCatalogAsync(
        ConfigurationCaller caller, string? query, string? category, CancellationToken ct = default)
    {
        var entry = SyntheticEntry(installed: false);
        IReadOnlyList<ZbaPluginCatalogEntry> plugins = string.IsNullOrEmpty(query)
            || SyntheticPluginId.Contains(query, StringComparison.OrdinalIgnoreCase)
                ? ImmutableArray.Create(entry)
                : ImmutableArray<ZbaPluginCatalogEntry>.Empty;
        return ValueTask.FromResult(new ZbaPluginCatalog(SyntheticRevision,
            "https://raw.githubusercontent.com/example-org/example-plugins/main/index.json",
            "合成市场提示：插件不是沙箱。", ImmutableArray.Create("未分类"), plugins));
    }

    public ValueTask<ZbaInstalledPlugins> ListInstalledPluginsAsync(
        ConfigurationCaller caller, CancellationToken ct = default)
        => ValueTask.FromResult(new ZbaInstalledPlugins(SyntheticRevision,
            ImmutableArray.Create(SyntheticEntry(installed: true))));

    public ValueTask<ZbaPluginSourceResolution> ResolvePluginSourceAsync(
        ConfigurationCaller caller, string sourceUrl, CancellationToken ct = default)
        => ValueTask.FromResult(new ZbaPluginSourceResolution(SyntheticManifest, "合成来源提示。"));

    public ValueTask<ZbaPluginOperationResult> InstallPluginAsync(
        ConfigurationCaller caller, ZbaPluginMutationRequest request, CancellationToken ct = default)
    {
        LastInstall = request;
        return ValueTask.FromResult(Operation(request.IdempotencyKey, request.Manifest.Id, "install"));
    }

    public ValueTask<ZbaPluginOperationResult> UpdatePluginAsync(
        ConfigurationCaller caller, ZbaPluginMutationRequest request, CancellationToken ct = default)
        => ValueTask.FromResult(Operation(request.IdempotencyKey, request.Manifest.Id, "update"));

    public ValueTask<ZbaPluginOperationResult> UninstallPluginAsync(
        ConfigurationCaller caller, string pluginId, ZbaPluginUninstallRequest request, CancellationToken ct = default)
    {
        LastUninstall = request;
        return ValueTask.FromResult(Operation(request.IdempotencyKey, pluginId, "uninstall"));
    }

    public ValueTask<ZbaPluginOperationResult> FindPluginOperationAsync(
        ConfigurationCaller caller, string idempotencyKey, CancellationToken ct = default)
        => ValueTask.FromResult(Operation(idempotencyKey, SyntheticPluginId, "install"));

    public ValueTask<ZbaPluginSettings> ReadPluginSettingsAsync(
        ConfigurationCaller caller, string pluginId, CancellationToken ct = default)
        => ValueTask.FromResult(new ZbaPluginSettings("r1", SyntheticManifest.Settings!,
            ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]
            {
                new KeyValuePair<string, string>("greeting", "hello"),
                new KeyValuePair<string, string>("verbose", "false")
            }),
            ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]
            {
                new KeyValuePair<string, bool>("apiKey", true)
            }), true));

    public ValueTask<ZbaPluginSettings> SavePluginSettingsAsync(
        ConfigurationCaller caller, string pluginId, ZbaPluginSettingsSaveRequest request,
        CancellationToken ct = default)
    {
        LastSettingsSave = request;
        return ReadPluginSettingsAsync(caller, pluginId, ct);
    }

    /// <summary>模型发现失败也只回 errorCode：合成后端固定返回“没有可用模型”以外的两条。</summary>
    public ValueTask<ZbaProviderModels> ListProviderModelsAsync(
        ConfigurationCaller caller, string providerKey, CancellationToken ct = default)
    {
        LastProviderKey = providerKey;
        return ValueTask.FromResult(providerKey == "synthetic-missing"
            ? new ZbaProviderModels(Array.Empty<string>(), "model_route_unavailable")
            : new ZbaProviderModels(ImmutableArray.Create("synthetic-model-a", "synthetic-model-b"), null));
    }

    private static ZbaPluginOperationResult Operation(string idempotencyKey, string pluginId, string kind)
        => new(new ZbaPluginOperation("po-" + idempotencyKey.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pluginId, kind, "restart_required", true, null), SyntheticRevision);
}
