using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

namespace BotAgent.Adapters.Plugins;

/// <summary>GitHub refs and closed descriptors; never downloads or runs package artifacts.</summary>
internal sealed class PluginRepositorySource(PluginCatalogSource catalog)
{
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, MaxDepth = 16
    };
    internal sealed record Snapshot(string Url, string Revision, IReadOnlyList<PluginPackageManifest> Manifests,
        PluginPresentationCatalog Presentation);
    private sealed record Location(string Url, string Owner, string Repository, string? Tag);

    internal async Task<Snapshot> DiscoverAsync(string sourceUrl, CancellationToken ct)
    {
        var location = ParseLocation(sourceUrl);
        var revision = await ResolveAsync(location, ct).ConfigureAwait(false);
        var root = $"https://raw.githubusercontent.com/{location.Owner}/{location.Repository}/{revision}/";
        var bytes = await catalog.ReadOptionalJsonAsync(root + "repository.json", ct, githubOnly: true).ConfigureAwait(false);
        // Only an actual 404 permits legacy adaptation. An invalid present descriptor fails closed.
        var legacy = bytes is null ? await catalog.ReadOptionalJsonAsync(root + "index.json", ct, githubOnly: true) : null;
        var managed = bytes is not null ? Parse(bytes) : legacy is not null ? PluginCatalogSource.Parse(legacy) : [];
        var pythonBytes = await catalog.ReadOptionalJsonAsync(root + "python-repository.json", ct, githubOnly: true);
        var python = pythonBytes is null ? [] : PluginPythonRepository.Parse(pythonBytes);
        IReadOnlyList<PluginPackageManifest> manifests = managed.Concat(python).ToArray();
        if (managed.Any(m => python.Any(p => p.Id == m.Id))) throw Invalid();
        if (manifests.Count == 0) throw Invalid();
        var presentation = await PluginPresentationCatalog.ReadAsync(catalog, root + "index.json", ct, githubOnly: true).ConfigureAwait(false);
        var pythonPresentation = await catalog.ReadOptionalJsonAsync(root + "python-presentation.json", ct, githubOnly: true);
        if (pythonPresentation is not null)
            try { presentation = presentation.Merge(PluginPresentationCatalog.Parse(pythonPresentation)); } catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
        return new(location.Url, revision, manifests, presentation);
    }

    internal Task<string> ResolveAsync(string sourceUrl, CancellationToken ct) => ResolveAsync(ParseLocation(sourceUrl), ct);

    private async Task<string> ResolveAsync(Location location, CancellationToken ct)
    {
        var api = $"https://api.github.com/repos/{location.Owner}/{location.Repository}/";
        if (location.Tag is null)
        {
            var commit = await ReadApiAsync(api + "commits/HEAD", ct).ConfigureAwait(false);
            return Sha(commit, "sha");
        }
        // Explicit refs/tags prevents a same-name branch from winning resolution.
        var reference = await ReadApiAsync(api + "git/ref/tags/" + Uri.EscapeDataString(location.Tag), ct).ConfigureAwait(false);
        if (!reference.TryGetProperty("object", out var target)) throw Invalid();
        for (var hop = 0; hop < 5; hop++)
        {
            var sha = Sha(target, "sha");
            if (!target.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) throw Invalid();
            if (type.GetString() == "commit") return sha;
            if (type.GetString() != "tag") throw Invalid();
            var tag = await ReadApiAsync(api + "git/tags/" + sha, ct).ConfigureAwait(false);
            if (!tag.TryGetProperty("object", out target)) throw Invalid();
        }
        throw Invalid();
    }

    private async Task<JsonElement> ReadApiAsync(string url, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await catalog.FetchAsync(url, ct, githubOnly: true), new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
        return document.RootElement.Clone();
    }

    internal static IReadOnlyList<PluginPackageManifest> Parse(byte[] bytes)
    {
        if (bytes.Length > 2 * 1024 * 1024) throw Invalid();
        RepositoryDocument? document;
        try { document = JsonSerializer.Deserialize<RepositoryDocument>(bytes, StrictJson); }
        catch (JsonException) { throw Invalid(); }
        if (document?.SchemaVersion != 1 || document.Plugins is null || document.Plugins.Count is 0 or > 1024) throw Invalid();
        var result = new List<PluginPackageManifest>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plugin in document.Plugins)
        {
            if (plugin is null || !ids.Add(plugin.Id) || plugin.Versions is null || plugin.Versions.Count is 0 or > 1024) throw Invalid();
            var versions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var version in plugin.Versions)
            {
                if (version?.Manifest is null || version.Manifest.Id != plugin.Id || version.Manifest.Version != version.Version
                    || !versions.Add(version.Version) || result.Count >= 1024) throw Invalid();
                if (version.Manifest.ApiVersion != 1) throw Invalid();
                PluginPackageValidation.Validate(version.Manifest, checkCompatibility: false);
                result.Add(version.Manifest);
            }
        }
        PluginImportPolicy.ValidateRepository(result);
        return result;
    }

    private static Location ParseLocation(string sourceUrl)
    {
        if (sourceUrl is null || sourceUrl.Length > 1024 || sourceUrl.Any(c => char.IsControl(c) || c == (char)92)
            || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw InvalidUrl();
        var parts = uri.AbsolutePath.TrimEnd('/').Split('/');
        if (parts.Length is not (3 or 6) || parts[0].Length != 0
            || !Regex.IsMatch(parts[1], @"^[A-Za-z0-9][A-Za-z0-9-]{0,99}\z")
            || !Regex.IsMatch(parts[2], @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\z")
            || parts[2].Contains("..")) throw InvalidUrl();
        string? tag = null;
        if (parts.Length == 6)
        {
            if (parts[3] != "releases" || parts[4] != "tag") throw InvalidUrl();
            tag = Uri.UnescapeDataString(parts[5]);
            if (tag.Length is 0 or > 200 || tag.Any(c => char.IsControl(c) || c == (char)92)
                || tag.Contains("..") || tag.Contains('/') || tag.Contains('%') || tag.Any(char.IsWhiteSpace)) throw InvalidUrl();
        }
        var canonical = $"https://github.com/{parts[1]}/{parts[2]}" + (tag is null ? "" : "/releases/tag/" + Uri.EscapeDataString(tag));
        return new(canonical, parts[1], parts[2], tag);
    }

    private static string Sha(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(field, out var sha)
            || sha.ValueKind != JsonValueKind.String || !Regex.IsMatch(sha.GetString()!, @"^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z")) throw Invalid();
        return sha.GetString()!.ToLowerInvariant();
    }
    private static InvalidDataException Invalid() => new("Invalid repository descriptor or ref.");
    private static ZbaBackendException InvalidUrl() => new(422, "plugin_source_invalid", "插件来源必须是公开 GitHub 仓库或固定 release tag。");
    private sealed record RepositoryDocument([property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] IReadOnlyList<RepositoryPlugin> Plugins);
    private sealed record RepositoryPlugin([property: JsonRequired] string Id,
        [property: JsonRequired] IReadOnlyList<RepositoryVersion> Versions);
    private sealed record RepositoryVersion([property: JsonRequired] string Version,
        [property: JsonRequired] PluginPackageManifest Manifest);
}