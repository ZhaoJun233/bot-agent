using System.Text.Json;
using System.Collections.Immutable;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

namespace BotAgent.Adapters.Plugins;

/// <summary>
/// 市场写路径的宿主侧收口：目录读取、来源解析、暂存安装/更新/卸载、回执与声明式设置。
/// Panel 只做形状校验与状态码映射；下载、校验、落盘与事务都在这里完成。
/// </summary>
public sealed partial class PluginMarketService
{
    /// <summary>仅未绑定热应用回调的兼容宿主使用此状态；完整宿主暂存后立即热应用。</summary>
    public const string StatusRestartRequired = "restart_required";
    public const string StatusFailed = "failed";
    public const string StatusInstalled = "installed";
    public const string StatusNotInstalled = "not_installed";
    public const string StatusPendingUninstall = "pending_uninstall";
    public const string FallbackCategory = "未分类";

    public const string TrustNotice =
        "插件执行受信任的第三方代码：.NET 在宿主进程内运行，Python 在受生命周期管理的子进程内运行，两者都不是安全沙箱。请核对固定版本、SHA-256 与权限声明；安装后默认停用，Python 仅显式启用时运行包代码。";
    private const string PythonCompatibilityNotice = "仅确认可信解释器版本可用；未运行包代码。初始化和工具发现将在显式启用时验证；仅支持 stdlib-only，进程不是安全沙箱。";

    private readonly PluginPackageService _packages;
    private readonly PluginSettingsStore _settings;
    private readonly PluginCatalogSource _catalog;
    private readonly PluginOperationReceiptStore _receipts;
    private readonly PluginMarketOperations _operations;
    private readonly Func<IReadOnlyDictionary<string, bool>>? _desiredStates;
    private readonly Action<string>? _log;
    private readonly Func<string?, CancellationToken, Task>? _reload;
    private readonly Func<IReadOnlyList<PluginInfo>>? _registrySnapshot;
    private PluginPresentationCatalog _presentation = PluginPresentationCatalog.Unavailable();

    /// <param name="receiptPath">回执文件；缺省落在运行根的 plugins/operations.json（有界、原子写）。</param>
    public PluginMarketService(PluginPackageService packages, PluginSettingsStore settings,
        PluginCatalogSource catalog, Func<DateTimeOffset> now, string receiptPath,
        Func<IReadOnlyDictionary<string, bool>>? desiredStates = null, Action<string>? log = null,
        Func<string?, CancellationToken, Task>? reload = null,
        Func<IReadOnlyList<PluginInfo>>? registrySnapshot = null)
    {
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _operations = PluginMarketOperations.For(packages);
        _receipts = _operations.Store(receiptPath, now, log);
        _desiredStates = desiredStates;
        _log = log;
        _reload = reload;
        _registrySnapshot = registrySnapshot;
        _imports = new PluginImportSnapshots(now);
        _repositories = new PluginRepositorySource(catalog);
    }

    /// <summary>Package identity stays in the manifest; built-ins come only from the live host registry.</summary>
    public async Task<ZbaPluginCatalog> ReadCatalogAsync(string? query, string? category, CancellationToken ct)
    {
        await _packages.Governance.RefreshAsync(ct).ConfigureAwait(false);
        var source = PluginCatalogSource.ConfiguredUrl();
        var registered = _registrySnapshot?.Invoke() ?? [];
        IReadOnlyList<PluginPackageManifest> manifests;
        var catalogUnavailable = false;
        var catalogPresentation = PluginPresentationCatalog.Unavailable();
        try
        {
            var snapshot = await ReadCatalogSnapshotAsync(ct).ConfigureAwait(false);
            manifests = snapshot.Manifests;
            catalogPresentation = snapshot.Presentation;
        }
        catch (Exception ex) when (ex is PluginCatalogException or HttpRequestException or IOException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            if (!registered.Any(p => p.IsPreset))
                throw new ZbaBackendException(503, (ex as PluginCatalogException)?.Code ?? "plugin_catalog_unavailable",
                    "插件市场目录暂时不可用。");
            manifests = [];
            catalogUnavailable = true;
        }
        var presentation = catalogPresentation;
        var compatibility = await ReadCompatibilityAsync(manifests, ct).ConfigureAwait(false);
        _presentation = presentation;
        var installed = await _packages.GetInstalledAsync(ct).ConfigureAwait(false);
        var desired = DesiredStates();
        var entries = manifests.Select(manifest => Project(manifest, installed, desired) with
            { Metadata = presentation.ForPackage(manifest), IsCompatible = compatibility[manifest.Id + "@" + manifest.Version] is null,
                CompatibilityMessage = compatibility[manifest.Id + "@" + manifest.Version] ?? (manifest.ApiVersion == 2 ? PythonCompatibilityNotice : null) }).ToArray();
        var builtins = presentation.ForBuiltins(registered);
        var categories = entries.Select(e => e.Metadata?.Category ?? FallbackCategory)
            .Concat(builtins.Select(b => b.Metadata.Category)).Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal).ToArray();
        return new ZbaPluginCatalog(await RevisionAsync(ct).ConfigureAwait(false), source, TrustNotice, categories,
            entries.Where(e => Matches(e.Manifest.Id, e.Metadata, query, category)).ToArray())
        {
            BuiltinPlugins = builtins.Where(b => Matches(b.PluginId, b.Metadata, query, category)).ToArray(),
            GovernanceSource = _packages.Governance.Source,
            MetadataNotice = catalogUnavailable ? "市场目录暂不可用；仍可查看宿主实际注册的内置插件。" : presentation.Notice
        };
    }

    public async Task<ZbaInstalledPlugins> ReadInstalledAsync(CancellationToken ct)
    {
        var installed = await _packages.GetInstalledAsync(ct).ConfigureAwait(false);
        var desired = DesiredStates();
        var presentation = _presentation;
        var compatibility = await ReadCompatibilityAsync(installed.Select(p => p.Active ?? p.Pending)
            .Where(m => m is not null).Cast<PluginPackageManifest>(), ct).ConfigureAwait(false);
        var entries = installed
            .Select(package => package.Active ?? package.Pending)
            .Where(manifest => manifest is not null)
            .Select(manifest => Project(manifest!, installed, desired) with { Metadata = presentation.ForPackage(manifest!),
                IsCompatible = compatibility[manifest!.Id + "@" + manifest.Version] is null,
                CompatibilityMessage = compatibility[manifest.Id + "@" + manifest.Version] ?? (manifest.ApiVersion == 2 ? PythonCompatibilityNotice : null) })
            .OrderBy(entry => entry.Manifest.Id, StringComparer.Ordinal)
            .ToArray();
        // No remote dependency: even a first offline request exposes actual registered built-ins.
        return new ZbaInstalledPlugins(await RevisionAsync(ct).ConfigureAwait(false), entries)
            { BuiltinPlugins = presentation.ForBuiltins(_registrySnapshot?.Invoke() ?? []), GovernanceSource = _packages.Governance.Source };
    }

    private static bool Matches(string id, ZbaPluginMetadata? metadata, string? query, string? category)
    {
        var wantedCategory = category?.Trim();
        if (!string.IsNullOrEmpty(wantedCategory) && wantedCategory != "all"
            && wantedCategory != (metadata?.Category ?? FallbackCategory)) return false;
        var term = query?.Trim();
        if (string.IsNullOrEmpty(term)) return true;
        var fields = new[] { id, metadata?.Name, metadata?.Summary, metadata?.Description, metadata?.Author }
            .Concat(metadata?.Tags ?? []);
        return fields.Any(value => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// 固定版本 Release 解析：只接受 <c>https://github.com/{owner}/{repo}/releases/tag/{tag}</c>，
    /// 去该 tag 的仓库索引里找同一 id@version 的清单；找不到就明确失败，绝不臆造。
    /// </summary>
    /// <summary>
    /// 固定版本 Release 解析：接受 <c>https://github.com/{owner}/{repo}</c>（索引里恰好一个插件时）
    /// 或 <c>.../releases/tag/{id}-{version}</c>（按 id-version 精确匹配）。多插件仓库必须给 tag，
    /// 绝不靠猜挑一个装给用户。
    /// </summary>
    public async Task<ZbaPluginSourceResolution> ResolveSourceAsync(string sourceUrl, CancellationToken ct)
    {
        if (!TryParseSourceUrl(sourceUrl, out var owner, out var repository, out var tag))
            throw new ZbaBackendException(422, "plugin_source_invalid", "插件来源必须是公开 GitHub 仓库或其固定 release tag。");
        IReadOnlyList<PluginPackageManifest> manifests;
        try
        {
            // 仓库主页读 HEAD（默认分支）；固定 tag 读该 tag —— 两者都是不可变的仓库快照地址。
            manifests = await _catalog.ReadPinnedAsync(owner, repository, tag, ct).ConfigureAwait(false);
        }
        catch (PluginCatalogException exception)
        {
            throw new ZbaBackendException(422, exception.Code, "该来源没有可用的市场索引。");
        }

        if (tag is null)
        {
            if (manifests.Count != 1)
                throw new ZbaBackendException(422, "plugin_source_ambiguous",
                    "该仓库索引里有多个插件：请改用 /releases/tag/固定版本 指定一个。");
            return new ZbaPluginSourceResolution(manifests[0], TrustNotice);
        }

        var matched = manifests.FirstOrDefault(manifest =>
            string.Equals(manifest.Id + "-" + manifest.Version, tag, StringComparison.Ordinal));
        if (matched is null)
            throw new ZbaBackendException(422, "plugin_source_not_found", "该固定版本不在来源仓库的索引里。");
        return new ZbaPluginSourceResolution(matched, TrustNotice);
    }

    public Task<ZbaPluginOperationResult> InstallAsync(ZbaPluginMutationRequest request, string caller,
        CancellationToken ct, Func<CancellationToken, Task>? persistDisabled = null)
    {
        PluginPackageValidation.Validate(request.Manifest);
        request = request with { Manifest = CloneManifest(request.Manifest) };
        return MutateAsync(request.IdempotencyKey, request.Manifest.Id, "install", request.Manifest,
            request.TrustAcknowledged, caller, request.ExpectedRevision,
            () => _packages.StageFreshInstallAsync(request.Manifest, request.ExpectedRevision,
                persistDisabled ?? (_ => throw new ZbaBackendException(503, "plugin_disable_unavailable", "无法持久化停用状态，未安装。")), ct),
            ct, () => ValidatePreviewAsync(request, caller, ct), request.PreviewId);
    }

    public Task<ZbaPluginOperationResult> UpdateAsync(ZbaPluginMutationRequest request, string caller, CancellationToken ct)
        => LifecycleMutationAsync("update", request.IdempotencyKey, request.ExpectedRevision, request.Manifest.Id,
            CloneManifest(request.Manifest), request.PreviewId, request.TrustAcknowledged, caller, ct);

    public Task<ZbaPluginOperationResult> UninstallAsync(string pluginId, ZbaPluginUninstallRequest request,
        string caller, CancellationToken ct)
    {
        PluginPackageValidation.ValidateId(pluginId);
        return LifecycleMutationAsync("uninstall", request.IdempotencyKey, request.ExpectedRevision, pluginId, null,
            request.PreviewId, request.TrustAcknowledged, caller, ct);
    }

    private Task<ZbaPluginOperationResult> MutateAsync(string key, string pluginId, string kind,
        PluginPackageManifest? manifest, bool trust, string caller, string expectedRevision, Func<Task> stage,
        CancellationToken ct, Func<Task>? admit = null, string? previewId = null)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(caller) || caller.Length > 256 || caller.Any(char.IsControl))
            throw new ZbaBackendException(400, "invalid_plugin_operation", "插件操作键或调用方无效。");
        var operation = new ZbaPluginOperation("po-" + Hash(key)[..16], pluginId, kind,
            _reload is null ? StatusRestartRequired : kind == "uninstall" ? StatusNotInstalled : StatusInstalled,
            _reload is null, null);
        return _operations.ExecuteAsync(_receipts, ReceiptKey(key),
            previewId is null ? PluginPackageRevision.Intent(kind, pluginId, manifest, trust, caller)
                : Hash(PluginPackageRevision.Intent(kind, pluginId, manifest, trust, caller) + ":" + previewId), caller, operation,
            () => RevisionAsync(CancellationToken.None), expectedRevision, async () =>
            {
                await stage().ConfigureAwait(false);
                // Once staging committed, a disconnected caller must not strand hot application.
                if (_reload is not null)
                {
                    try { await _reload(pluginId, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception)
                    {
                        throw new ZbaBackendException(503, "plugin_reload_failed",
                            "变更已暂存，但热重载失败；旧实例保持不变，请修正后用新的操作键重试。");
                    }
                }
            }, ct, admit, () => _packages.InstalledSnapshot.FirstOrDefault(p => p.Id == pluginId) is { } state
                ? PluginLifecycleManifest.PackageState(state) : null);
    }

    /// <summary>按幂等键核对回执：没有回执时明确 404，让前端继续用同一个键。</summary>
    public ZbaPluginOperationResult FindOperation(string idempotencyKey)
        => FindReceipt(idempotencyKey)
           ?? throw new ZbaBackendException(404, "plugin_operation_not_found", "这个操作键还没有回执。");

    public async Task<ZbaPluginSettings> ReadSettingsAsync(string pluginId, CancellationToken ct)
    {
        var manifest = await InstalledManifestAsync(pluginId, ct).ConfigureAwait(false);
        return Project(_settings.ReadManifest(manifest));
    }

    public async Task<ZbaPluginSettings> SaveSettingsAsync(string pluginId, ZbaPluginSettingsSaveRequest request,
        string caller, CancellationToken ct)
    {
        var manifest = await InstalledManifestAsync(pluginId, ct).ConfigureAwait(false);
        var current = _settings.ReadManifest(manifest);
        // Parse the caller precondition, but let the store resolve receipts before comparing current state.
        var expectedRevision = ParseSettingsRevision(request.ExpectedRevision);
        var values = BuildChanges(request.Edits, current);
        PluginSettingsReceipt receipt;
        try
        {
            receipt = _settings.Save(manifest, expectedRevision, request.IdempotencyKey, values, caller);
        }
        catch (PluginSettingsException exception)
        {
            throw SettingsFailure(exception.Code);
        }

        if (_reload is not null)
        {
            try { await _reload(pluginId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception)
            {
                throw new ZbaBackendException(503, "plugin_reload_failed",
                    "设置已保存，但热重载失败；旧运行实例保持不变，请修正配置后重试。");
            }
        }
        var saved = _settings.ReadManifest(manifest);
        return Project(saved with { Revision = receipt.Revision });
    }

    /// <summary>编辑 → JSON 变更集；secret 的 delete 用 null，其余按声明类型解析。</summary>
    private static JsonElement BuildChanges(IReadOnlyList<ZbaPluginSettingEdit> edits, PluginSettingsView current)
    {
        var fields = current.Schema.ToDictionary(field => field.Key, StringComparer.Ordinal);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            if (!fields.TryGetValue(edit.Key, out var field))
                throw new ZbaBackendException(400, "invalid_plugin_setting_key", "设置键不在清单声明里。");
            if (edit.Kind == 0) continue;
            if (edit.Kind == 2)
            {
                if (field.Type != "secret")
                    throw new ZbaBackendException(400, "plugin_setting_delete_unsupported", "只有密钥设置可以删除。");
                values[edit.Key] = null;
                continue;
            }

            var text = edit.Value ?? string.Empty;
            values[edit.Key] = field.Type switch
            {
                "boolean" => bool.TryParse(text, out var flag)
                    ? flag
                    : throw new ZbaBackendException(400, "invalid_plugin_setting_value", "布尔设置必须是 true 或 false。"),
                "number" => double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                    ? number
                    : throw new ZbaBackendException(400, "invalid_plugin_setting_value", "数值设置必须是有限数值。"),
                _ => text
            };
        }

        return JsonSerializer.SerializeToElement(values);
    }

    private static ZbaBackendException SettingsFailure(string code) => code switch
    {
        "plugin_settings_revision_conflict" => new ZbaBackendException(409, code, "设置已被修改，请重新读取后再提交。"),
        "plugin_settings_idempotency_conflict" => new ZbaBackendException(409, "idempotency_conflict", "这个操作键已经用于另一份变更。"),
        "plugin_settings_unavailable" => new ZbaBackendException(503, code, "设置存储暂时不可用。"),
        _ => new ZbaBackendException(400, code, "设置无效。")
    };

    private static ZbaPluginSettings Project(PluginSettingsView view)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in view.Values)
            values[pair.Key] = pair.Value.ValueKind == JsonValueKind.String
                ? pair.Value.GetString() ?? string.Empty
                : pair.Value.GetRawText();
        var secrets = view.Schema.Where(field => field.Type == "secret")
            .ToDictionary(field => field.Key,
                field => view.ConfiguredSecrets.Contains(field.Key, StringComparer.Ordinal),
                StringComparer.Ordinal);
        return new ZbaPluginSettings(RevisionOf(view.Revision), view.Schema, values, secrets, view.RestartRequired);
    }

    private async Task<PluginPackageManifest> InstalledManifestAsync(string pluginId, CancellationToken ct)
    {
        var installed = await _packages.GetInstalledAsync(ct).ConfigureAwait(false);
        var package = installed.FirstOrDefault(item => item.Id == pluginId);
        var manifest = package?.Active ?? package?.Pending;
        if (manifest is null) throw new ZbaBackendException(404, "plugin_not_installed", "这个插件还没有安装。");
        return manifest;
    }

    /// <summary>启用态来自配置库；读不到就按“未启用”保守展示，绝不假装已启用。</summary>
    private IReadOnlyDictionary<string, bool> DesiredStates()
    {
        try { return _desiredStates?.Invoke() ?? ImmutableDictionary<string, bool>.Empty; }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            _log?.Invoke("[Plugin] 启用态读取失败：" + exception.GetType().Name);
            return ImmutableDictionary<string, bool>.Empty;
        }
    }

    private ZbaPluginCatalogEntry Project(PluginPackageManifest manifest,
        IReadOnlyList<InstalledPluginPackage> installed, IReadOnlyDictionary<string, bool> desired)
    {
        var package = installed.FirstOrDefault(item => item.Id == manifest.Id);
        var active = package?.Active;
        var pending = package?.Pending;
        var uninstall = package?.PendingUninstall == true;
        var status = uninstall ? StatusPendingUninstall
            : pending is not null ? _reload is null ? StatusRestartRequired : "reload_required"
            : active is not null ? StatusInstalled
            : StatusNotInstalled;
        var enabled = desired.TryGetValue(manifest.Id, out var wanted) && wanted;
        return new ZbaPluginCatalogEntry(manifest, active?.Version, enabled, status,
            _reload is null && (pending is not null || uninstall))
            { PreviousManifest = package?.Previous, PendingManifest = package?.Pending,
                Governance = _packages.Governance.Evaluate(manifest),
                InstalledGovernance = (active ?? pending) is { } current ? _packages.Governance.Evaluate(current) : null };
    }

    private static long ParseSettingsRevision(string revision)
    {
        if (revision is null || !revision.StartsWith('r') ||
            !long.TryParse(revision.AsSpan(1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || RevisionOf(value) != revision)
            throw SettingsFailure("plugin_settings_revision_conflict");
        return value;
    }

    private static string RevisionOf(long revision)
        => "r" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The same revision calculation is used inside the lifecycle CAS gate.</summary>
    private async Task<string> RevisionAsync(CancellationToken ct)
        => PluginPackageRevision.Of(await _packages.GetInstalledAsync(ct).ConfigureAwait(false));

    private ZbaPluginOperationResult? FindReceipt(string idempotencyKey)
        => _receipts.Find(ReceiptKey(idempotencyKey));

    private static string Hash(string value) => PluginPackageRevision.Hash(value);

    private static string ReceiptKey(string idempotencyKey) => "plugin_operation:" + Hash(idempotencyKey)[..32];

    /// <summary>
    /// 只解析公开 GitHub 地址：仓库主页（tag 为 null）或 <c>/releases/tag/{tag}</c>。
    /// 其它主机、凭据、查询串、片段与默认端口以外的一切都拒绝，绝不把任意 URL 当索引来源。
    /// </summary>
    private static bool TryParseSourceUrl(string sourceUrl, out string owner, out string repository, out string? tag)
    {
        owner = repository = string.Empty;
        tag = null;
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || !uri.IsDefaultPort)
            return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (2 or 5)) return false;
        if (parts.Length == 5 && (parts[2] != "releases" || parts[3] != "tag")) return false;
        if (parts[0].Length is 0 or > 100 || parts[1].Length is 0 or > 100) return false;
        foreach (var part in parts.Take(2))
            if (part.Any(character => character is < ' ' or > '~' || character is '/' or '\\')) return false;
        owner = parts[0];
        repository = parts[1];
        if (parts.Length == 2) return true;
        if (parts[4].Length is 0 or > 200) return false;
        tag = Uri.UnescapeDataString(parts[4]);
        return tag.Length is > 0 and <= 200;
    }
}