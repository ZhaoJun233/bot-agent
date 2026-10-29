using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Net;

namespace BotAgent.Adapters.Plugins;

/// <summary>
/// 市场索引的唯一读取点：只接受 https 公网地址，逐跳校验重定向，有界读取与解析。
/// 索引形如 <c>{ "SchemaVersion": 1, "Plugins": [manifest] }</c>（PascalCase，未知字段拒绝）。
/// </summary>
public sealed class PluginCatalogSource
{
    public const string DefaultUrl = "https://raw.githubusercontent.com/ZhaoJun233/bot-agent-plugins/main/index.json";
    private const int MaxIndexBytes = 2 * 1024 * 1024;
    private const int MaxPlugins = 1024;

    private static readonly JsonSerializerOptions IndexJson = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    private readonly IHttpFetcher _http;
    private readonly Action<string>? _log;

    public PluginCatalogSource(IHttpFetcher http, Action<string>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log;
    }

    /// <summary>环境变量覆盖优先，缺省用公开市场索引；非法地址在读取时拒绝，而不是启动时抛错。</summary>
    public static string ConfiguredUrl()
    {
        var configured = EnvironmentAliases.Get("BOTAGENT_PLUGIN_CATALOG_URL", "QQCHAT_PLUGIN_CATALOG_URL")?.Trim();
        return string.IsNullOrEmpty(configured) ? DefaultUrl : configured;
    }

    public async Task<IReadOnlyList<PluginPackageManifest>> ReadAsync(string sourceUrl, CancellationToken ct)
    {
        var bytes = await FetchAsync(sourceUrl, ct).ConfigureAwait(false);
        return Parse(bytes);
    }

    /// <summary>固定 release tag 的仓库索引地址：raw 服务按 ref 取同一份索引，tag 即不可变指针。</summary>
    public async Task<IReadOnlyList<PluginPackageManifest>> ReadPinnedAsync(string owner, string repository,
        string? reference, CancellationToken ct)
    {
        var url = "https://raw.githubusercontent.com/" + owner + "/" + repository + "/"
            + (reference is null ? "HEAD" : Uri.EscapeDataString(reference)) + "/index.json";
        return await ReadAsync(url, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> FetchAsync(string sourceUrl, CancellationToken ct)
    {
        if (!SafeUrl.TryValidate(sourceUrl, false, out var uri, out var reason)
            || uri.Scheme != Uri.UriSchemeHttps)
            throw new PluginCatalogException("plugin_catalog_source_invalid");
        using var response = await SafeUrl.SendFollowingRedirectsAsync(_http, uri,
            target => new HttpRequestMessage(HttpMethod.Get, target), false, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _log?.Invoke("[Plugin] 市场索引读取失败：" + (int)response.StatusCode);
            throw new PluginCatalogException("plugin_catalog_unavailable");
        }

        if (response.Content.Headers.ContentLength is > MaxIndexBytes)
            throw new PluginCatalogException("plugin_catalog_too_large");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > MaxIndexBytes) throw new PluginCatalogException("plugin_catalog_too_large");
            buffer.Write(chunk, 0, count);
        }

        return buffer.ToArray();
    }

    public static IReadOnlyList<PluginPackageManifest> Parse(byte[] bytes)
    {
        IndexDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<IndexDocument>(new UTF8Encoding(false, true).GetString(bytes), IndexJson);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException
            or ArgumentException or NotSupportedException)
        {
            throw new PluginCatalogException("plugin_catalog_malformed");
        }

        if (document is null || document.SchemaVersion != 1 || document.Plugins is null
            || document.Plugins.Count > MaxPlugins)
            throw new PluginCatalogException("plugin_catalog_malformed");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manifest in document.Plugins)
        {
            try { PluginPackageValidation.Validate(manifest); }
            catch (InvalidDataException) { throw new PluginCatalogException("plugin_catalog_malformed"); }
            if (!ids.Add(manifest.Id)) throw new PluginCatalogException("plugin_catalog_malformed");
        }

        return document.Plugins.ToArray();
    }

    private sealed record IndexDocument(int SchemaVersion, IReadOnlyList<PluginPackageManifest>? Plugins);
}

/// <summary>市场读取的机器可读失败；宿主把它映射成 ZBA 错误码，绝不 5xx。</summary>
public sealed class PluginCatalogException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
