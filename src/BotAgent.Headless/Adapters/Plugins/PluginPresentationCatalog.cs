using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

namespace BotAgent.Adapters.Plugins;

/// <summary>Optional, non-executable catalog presentation. Identity and trust never come from display text.</summary>
internal sealed class PluginPresentationCatalog
{
    internal const string UnavailableNotice = "市场展示资料暂不可用；保留原始包目录与宿主内置插件，不代表已通过安全审核。";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 16
    };
    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9_.-]{1,96}\z", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(@"^[0-9]+[.][0-9]+[.][0-9]+(?:-[0-9A-Za-z.-]+)?(?:[+][0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex RevisionPattern = new(@"^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z", RegexOptions.CultureInvariant);
    private readonly IReadOnlyList<Entry> _entries;
    internal string? Notice { get; }

    private PluginPresentationCatalog(IReadOnlyList<Entry> entries, string? notice = null)
    { _entries = entries; Notice = notice; }

    internal static PluginPresentationCatalog Unavailable() => new([], UnavailableNotice);

    internal static async Task<PluginPresentationCatalog> ReadAsync(PluginCatalogSource catalog, string source,
        CancellationToken ct)
    {
        try { return Parse(await catalog.ReadPresentationBytesAsync(source, ct).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is PluginCatalogException or HttpRequestException or IOException or InvalidDataException or JsonException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        { return Unavailable(); }
    }

    internal static PluginPresentationCatalog Parse(byte[] bytes)
    {
        if (bytes.Length > 2 * 1024 * 1024) throw Invalid();
        using var jsonDocument = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var document = jsonDocument.RootElement.Deserialize<Document>(Json);
        if (document?.SchemaVersion != 1 || document.Entries is null || document.Entries.Count > 2048) throw Invalid();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Entries)
        {
            if (entry is null || !Match(IdPattern, entry.Id) || !Match(VersionPattern, entry.Version)
                || !keys.Add(entry.Id + "@" + entry.Version)) throw Invalid();
            ValidateMetadata(entry);
        }
        foreach (var entry in jsonDocument.RootElement.GetProperty("Entries").EnumerateArray())
        {
            var review = entry.GetProperty("Metadata").GetProperty("Review");
            foreach (var field in new[] { "Reviewer", "ReviewedAt", "SourceRevision", "Notes" })
                if (review.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.String) throw Invalid();
        }
        return new(document.Entries);
    }

    internal ZbaPluginMetadata? ForPackage(PluginPackageManifest manifest)
    {
        var entry = _entries.FirstOrDefault(e => e.Id == manifest.Id && e.Version == manifest.Version
            && string.Equals(e.PackageSha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase)
            && e.Metadata.Runtime != "builtin");
        // M1 installs only existing .NET manifests. Python display data cannot change that execution contract.
        return entry?.Metadata.Runtime == "dotnet" ? BindReview(entry) : null;
    }

    internal IReadOnlyList<ZbaBuiltinPlugin> ForBuiltins(IReadOnlyList<PluginInfo> registered) => registered
        .Where(p => p.IsPreset).OrderBy(p => p.Id, StringComparer.Ordinal)
        .Select(p => new ZbaBuiltinPlugin(p.Id, p.Version, p.IsEnabled, ForBuiltin(p))).ToArray();

    private ZbaPluginMetadata ForBuiltin(PluginInfo plugin)
    {
        var entry = _entries.FirstOrDefault(e => e.Id == plugin.Id && e.Version == plugin.Version
            && e.Metadata.Runtime == "builtin" && e.PackageSha256 is null);
        return entry is null ? Fallback(plugin) : BindReview(entry);
    }

    private static ZbaPluginMetadata BindReview(Entry entry)
    {
        var review = entry.Metadata.Review;
        var valid = entry.Metadata.Runtime != "builtin" && review.Status == "reviewed"
            && review.Version == entry.Version && review.Sha256 == entry.PackageSha256
            && Match(RevisionPattern, review.SourceRevision) && Text(review.Reviewer)
            && IsTimestamp(review.ReviewedAt);
        return entry.Metadata with { Review = valid ? review : new ZbaPluginReview("unreviewed", entry.Version,
            entry.PackageSha256, Notes: review.Notes) };
    }

    private static void ValidateMetadata(Entry entry)
    {
        var m = entry.Metadata;
        if (m is null || m.Review is null || m.Tags is null || m.Tags.Count > 24
            || m.Tags.Distinct(StringComparer.Ordinal).Count() != m.Tags.Count
            || m.Tags.Any(t => !Text(t) || t.Length > 80)) throw Invalid();
        string[] text = [m.Name, m.Summary, m.Description, m.Category, m.Author, m.Repository, m.License,
            m.Usage, m.RiskNotice, m.Configuration, m.Compatibility, m.Changelog, m.Maintenance, m.PermissionNotes];
        if (text.Any(t => !Text(t)) || !SafeRepository(m.Repository)
            || m.Runtime is not ("builtin" or "dotnet" or "python")
            || m.SourceKind is not ("builtin" or "official" or "community")) throw Invalid();
        var builtin = m.Runtime == "builtin";
        if (builtin != (m.SourceKind == "builtin") || builtin != (entry.PackageSha256 is null)
            || !builtin && !Match(HashPattern, entry.PackageSha256)) throw Invalid();
        var review = m.Review;
        if (review.Status is not ("unreviewed" or "reviewed") || !Match(VersionPattern, review.Version)
            || builtin && (review.Status != "unreviewed" || review.Sha256 is not null)
            || review.Sha256 is not null && !Match(HashPattern, review.Sha256) || review.Reviewer?.Length > 200
            || new[] { review.Reviewer, review.ReviewedAt, review.SourceRevision, review.Notes }
                .Any(t => t is not null && !Text(t))) throw Invalid();
    }

    private static bool SafeRepository(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && !value.Contains((char)92) && Regex.IsMatch(uri.AbsolutePath, @"^/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/?$");
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 4000;
    private static bool Match(Regex regex, string? value) => value is not null && value.Length <= 4000 && regex.IsMatch(value);
    private static bool IsTimestamp(string? value) => value is not null
        && Regex.IsMatch(value, @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:[.][0-9]+)?(?:Z|[+-][0-9]{2}:[0-9]{2})\z")
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    private static InvalidDataException Invalid() => new("Invalid plugin presentation sidecar.");

    private static ZbaPluginMetadata Fallback(PluginInfo plugin) => new(
        plugin.Name, plugin.Description, plugin.Description, plugin.Category switch
        {
            PluginCategory.Channel => "通道", PluginCategory.Media => "音频与媒体",
            PluginCategory.Management => "管理工具", _ => "功能扩展"
        }, "Bot Agent Contributors", "https://github.com/ZhaoJun233/bot-agent", "MIT",
        "通过宿主支持的功能入口使用；无需安装独立包。",
        "内置能力可能调用外部服务；随宿主发布不代表独立安全审核。",
        "在宿主插件配置中查看实际可用设置。", "兼容性随当前宿主版本。", "随宿主版本更新。",
        "随宿主维护", "实际权限由宿主配置与工具策略控制。", ["内置"], "builtin", "builtin",
        new ZbaPluginReview("unreviewed", plugin.Version, null));

    private sealed record Document([property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] IReadOnlyList<Entry> Entries);
    private sealed record Entry([property: JsonRequired] string Id, [property: JsonRequired] string Version,
        [property: JsonRequired] string? PackageSha256, [property: JsonRequired] ZbaPluginMetadata Metadata);
}
