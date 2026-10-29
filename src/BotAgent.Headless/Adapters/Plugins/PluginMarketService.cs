using System.Security.Cryptography;
using System.Text;
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
public sealed class PluginMarketService
{
    /// <summary>暂存态必须重启才生效；这里不提供任何热加载或自动重启。</summary>
    public const string StatusRestartRequired = "restart_required";
    public const string StatusFailed = "failed";
    public const string StatusInstalled = "installed";
    public const string StatusNotInstalled = "not_installed";
    public const string StatusPendingUninstall = "pending_uninstall";
    public const string FallbackCategory = "未分类";

    public const string TrustNotice =
        "插件在宿主进程内运行，不是沙箱：安装即执行第三方代码。请核对固定版本、SHA-256 与权限声明；安装后默认停用，需重启生效。";

    private readonly PluginPackageService _packages;
    private readonly PluginSettingsStore _settings;
    private readonly PluginCatalogSource _catalog;
    private readonly PluginOperationReceiptStore _receipts;
    private readonly Func<IReadOnlyDictionary<string, bool>>? _desiredStates;
    private readonly Action<string>? _log;

    /// <param name="receiptPath">回执文件；缺省落在运行根的 plugins/operations.json（有界、原子写）。</param>
    public PluginMarketService(PluginPackageService packages, PluginSettingsStore settings,
        PluginCatalogSource catalog, Func<DateTimeOffset> now, string receiptPath,
        Func<IReadOnlyDictionary<string, bool>>? desiredStates = null, Action<string>? log = null)
    {
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _receipts = new PluginOperationReceiptStore(receiptPath, now, log);
        _desiredStates = desiredStates;
        _log = log;
    }

    /// <summary>目录 = 市场索引 ∩ 本机真实安装态。展示字段只有 manifest 与状态，不伪造名称/说明/分类。</summary>
    public async Task<ZbaPluginCatalog> ReadCatalogAsync(string? query, string? category, CancellationToken ct)
    {
        var source = PluginCatalogSource.ConfiguredUrl();
        IReadOnlyList<PluginPackageManifest> manifests;
        try
        {
            manifests = await _catalog.ReadAsync(source, ct).ConfigureAwait(false);
        }
        catch (PluginCatalogException exception)
        {
            throw new ZbaBackendException(503, exception.Code, "插件市场目录暂时不可用。");
        }

        var installed = await _packages.GetInstalledAsync(ct).ConfigureAwait(false);
        var desired = DesiredStates();
        var entries = manifests.Select(manifest => Project(manifest, installed, desired)).ToArray();
        var term = query?.Trim();
        var wanted = string.IsNullOrEmpty(term)
            ? entries
            : entries.Where(entry => entry.Manifest.Id.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
        // 索引只有 manifest（契约明确禁止描述字段），所以分类只有一个回退值；分类过滤因此只放行“全部”。
        var wantedCategory = category?.Trim();
        var filtered = string.IsNullOrEmpty(wantedCategory) || wantedCategory is "all" or FallbackCategory
            ? wanted
            : Array.Empty<ZbaPluginCatalogEntry>();
        var categories = entries.Length == 0 ? Array.Empty<string>() : new[] { FallbackCategory };
        return new ZbaPluginCatalog(await RevisionAsync(ct).ConfigureAwait(false), source, TrustNotice, categories, filtered);
    }

    public async Task<ZbaInstalledPlugins> ReadInstalledAsync(CancellationToken ct)
    {
        var installed = await _packages.GetInstalledAsync(ct).ConfigureAwait(false);
        var desired = DesiredStates();
        var entries = installed
            .Select(package => package.Active ?? package.Pending)
            .Where(manifest => manifest is not null)
            .Select(manifest => Project(manifest!, installed, desired))
            .OrderBy(entry => entry.Manifest.Id, StringComparer.Ordinal)
            .ToArray();
        return new ZbaInstalledPlugins(await RevisionAsync(ct).ConfigureAwait(false), entries);
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

    public async Task<ZbaPluginOperationResult> InstallAsync(ZbaPluginMutationRequest request, string caller,
        CancellationToken ct)
    {
        PluginPackageValidation.Validate(request.Manifest);
        var replay = FindReceipt(request.IdempotencyKey);
        if (replay is not null) return replay;
        await EnsureRevisionAsync(request.ExpectedRevision, ct).ConfigureAwait(false);
        try
        {
            await _packages.StageInstallAsync(request.Manifest, ct).ConfigureAwait(false);
            return await RecordAsync(request.IdempotencyKey, request.Manifest.Id, "install",
                StatusRestartRequired, null, caller, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _log?.Invoke("[Plugin] 安装暂存失败：" + exception.GetType().Name);
            return await RecordAsync(request.IdempotencyKey, request.Manifest.Id, "install", StatusFailed,
                FailureCode(exception), caller, ct).ConfigureAwait(false);
        }
    }

    public async Task<ZbaPluginOperationResult> UpdateAsync(ZbaPluginMutationRequest request, string caller,
        CancellationToken ct)
    {
        PluginPackageValidation.Validate(request.Manifest);
        var replay = FindReceipt(request.IdempotencyKey);
        if (replay is not null) return replay;
        var installed = await _packages.GetInstalledAsync(ct).ConfigureAwait(false);
        if (!installed.Any(package => package.Id == request.Manifest.Id))
            throw new ZbaBackendException(404, "plugin_not_installed", "这个插件还没有安装，无法更新。");
        await EnsureRevisionAsync(request.ExpectedRevision, ct).ConfigureAwait(false);
        try
        {
            await _packages.StageInstallAsync(request.Manifest, ct).ConfigureAwait(false);
            return await RecordAsync(request.IdempotencyKey, request.Manifest.Id, "update",
                StatusRestartRequired, null, caller, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _log?.Invoke("[Plugin] 更新暂存失败：" + exception.GetType().Name);
            return await RecordAsync(request.IdempotencyKey, request.Manifest.Id, "update", StatusFailed,
                FailureCode(exception), caller, ct).ConfigureAwait(false);
        }
    }

    public async Task<ZbaPluginOperationResult> UninstallAsync(string pluginId, ZbaPluginUninstallRequest request,
        string caller, CancellationToken ct)
    {
        var replay = FindReceipt(request.IdempotencyKey);
        if (replay is not null) return replay;
        await EnsureRevisionAsync(request.ExpectedRevision, ct).ConfigureAwait(false);
        try
        {
            await _packages.StageUninstallAsync(pluginId, ct).ConfigureAwait(false);
            return await RecordAsync(request.IdempotencyKey, pluginId, "uninstall", StatusRestartRequired,
                null, caller, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException)
        {
            throw new ZbaBackendException(404, "plugin_not_installed", "这个插件还没有安装。");
        }
        catch (Exception exception)
        {
            _log?.Invoke("[Plugin] 卸载暂存失败：" + exception.GetType().Name);
            return await RecordAsync(request.IdempotencyKey, pluginId, "uninstall", StatusFailed,
                FailureCode(exception), caller, ct).ConfigureAwait(false);
        }
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
        if (!string.Equals(RevisionOf(current.Revision), request.ExpectedRevision, StringComparison.Ordinal))
            throw new ZbaBackendException(409, "plugin_settings_revision_conflict", "设置已被修改，请重新读取后再提交。");
        var values = BuildChanges(request.Edits, current);
        PluginSettingsReceipt receipt;
        try
        {
            receipt = _settings.Save(manifest, current.Revision, request.IdempotencyKey, values, caller);
        }
        catch (PluginSettingsException exception)
        {
            throw SettingsFailure(exception.Code);
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

    private static ZbaPluginCatalogEntry Project(PluginPackageManifest manifest,
        IReadOnlyList<InstalledPluginPackage> installed, IReadOnlyDictionary<string, bool> desired)
    {
        var package = installed.FirstOrDefault(item => item.Id == manifest.Id);
        var active = package?.Active;
        var pending = package?.Pending;
        var uninstall = package?.PendingUninstall == true;
        var status = uninstall ? StatusPendingUninstall
            : pending is not null ? StatusRestartRequired
            : active is not null ? StatusInstalled
            : StatusNotInstalled;
        var enabled = desired.TryGetValue(manifest.Id, out var wanted) ? wanted : active is not null;
        return new ZbaPluginCatalogEntry(manifest, active?.Version, enabled, status, pending is not null || uninstall);
    }

    private static string RevisionOf(long revision)
        => "r" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>目录代际：安装态摘要。任何暂存/激活/卸载都会换版本号，前端 CAS 才不会静默覆盖。</summary>
    private async Task<string> RevisionAsync(CancellationToken ct)
    {
        var installed = await _packages.GetInstalledAsync(ct).ConfigureAwait(false);
        var builder = new StringBuilder();
        foreach (var package in installed.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            builder.Append(package.Id).Append('@')
                .Append(package.Active?.Version ?? "-").Append('/')
                .Append(package.Pending?.Version ?? "-").Append('/')
                .Append(package.PendingUninstall ? 'u' : '-').Append(';');
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        return "p" + digest[..16].ToLowerInvariant();
    }

    private async Task EnsureRevisionAsync(string expected, CancellationToken ct)
    {
        var revision = await RevisionAsync(ct).ConfigureAwait(false);
        if (!string.Equals(revision, expected, StringComparison.Ordinal))
            throw new ZbaBackendException(409, "plugin_revision_conflict", "插件目录已变化，请重新读取后再提交。");
    }

    private ZbaPluginOperationResult? FindReceipt(string idempotencyKey)
        => _receipts.Find(ReceiptKey(idempotencyKey));

    /// <summary>回执与暂存分开：即使暂存失败也要留一条可核对的结果，否则前端只能盲目重提。</summary>
    private async Task<ZbaPluginOperationResult> RecordAsync(string idempotencyKey, string pluginId, string kind,
        string status, string? errorCode, string caller, CancellationToken ct)
    {
        var operation = new ZbaPluginOperation("po-" + Hash(idempotencyKey)[..16], pluginId, kind, status,
            status == StatusRestartRequired, errorCode);
        var revision = await RevisionAsync(ct).ConfigureAwait(false);
        _receipts.TryAdd(ReceiptKey(idempotencyKey), operation, revision, caller);
        return new ZbaPluginOperationResult(operation, revision);
    }

    private static string FailureCode(Exception exception) => exception switch
    {
        InvalidDataException => "plugin_package_invalid",
        HttpRequestException => "plugin_download_failed",
        IOException => "plugin_storage_failed",
        UnauthorizedAccessException => "plugin_storage_failed",
        _ => "plugin_operation_failed"
    };

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

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
