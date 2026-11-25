extern alias StorageModule;

using System.Text.Json.Nodes;
using StorageMcpStore = StorageModule::BotAgent.Storage.McpServerStore;
using BotAgent.Adapters.Net;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Mcp;
using BotAgent.Domain.Ports;
using BotAgent.Mcp;
using BotAgent.Panel;
using BotAgent.Services.Mcp;
using BotAgent.Services.Net;

namespace BotAgent.Host;

/// <summary>Wave4 的 MCP 运行体：客户端 + 它专用的出网实例（共用宿主 SSRF 连接策略）。</summary>
internal sealed class McpRuntime(IAsyncDisposable source, HttpFetcher http) : IAsyncDisposable
{
    public IAsyncDisposable Source { get; } = source;

    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync().ConfigureAwait(false);
        http.Dispose();
    }
}

internal sealed record McpWiring(McpToolUseCase UseCase, McpRuntime? Runtime);

internal sealed record McpManagedWiring(
    McpToolUseCase UseCase,
    McpRuntime Runtime,
    ReloadableMcpToolSource Reloadable,
    Func<bool> AllowPrivate,
    Func<string, (bool Ok, string NormalizedUrl, string Reason)> ValidateEndpoint,
    Func<IReadOnlyList<ZbaMcpServerView>> ListEnvServers,
    Func<CancellationToken, Task<int>> ReloadAsync,
    Func<string?, string, string?, int, CancellationToken, Task<McpProbeOutcome>> ProbeAsync);

/// <summary>
/// MCP 首版配置入口（Wave4 不接面板）：`BOTAGENT_MCP_ENABLED` + `BOTAGENT_MCP_SERVERS`。
///
/// 安全口径：
///   · 服务器必须显式列在 JSON 里，`allowedTools` 为空 = 一个都不授权（不发现、不连接）；
///   · 端点先过 <see cref="SafeUrl" />；默认拒绝回环/私网，只有 `BOTAGENT_MCP_ALLOW_PRIVATE=1` 才放开；
///   · 密钥按 `tokenSecret` 名字从密钥库/环境变量解析；名字给了但解析不到 → 跳过该服务器，不裸连；
///   · 日志只写服务器 id 与原因码，不写 URL/query/token。
/// </summary>
internal static class McpConfiguration
{
    public static McpWiring Build(ISecretsRepository secrets, BotAgent.Services.Ops.IAuditChain? audit,
        Action<string> log, BotAgent.Adapters.Plugins.PluginPythonSources? packages = null)
        => BuildWithPlugins(secrets, audit, log, packages, plugins: null);

    public static McpWiring BuildWithPlugins(ISecretsRepository secrets, BotAgent.Services.Ops.IAuditChain? audit,
        Action<string> log, BotAgent.Adapters.Plugins.PluginPythonSources? packages,
        BotAgent.Services.Plugins.PluginManager? plugins)
    {
        IMcpToolSource Combine(IMcpToolSource source) => packages is null ? source : new BotAgent.Adapters.Plugins.CompositePackageToolSource(source, packages);
        if (!On(EnvironmentAliases.Get("BOTAGENT_MCP_ENABLED")))
        {
            return new McpWiring(new McpToolUseCase(Combine(NullMcpToolSource.Instance), audit, log, plugins: plugins), null);
        }

        var allowPrivate = On(EnvironmentAliases.Get("BOTAGENT_MCP_ALLOW_PRIVATE"));
        var raw = EnvironmentAliases.Get("BOTAGENT_MCP_SERVERS");
        var servers = Parse(raw, secrets, allowPrivate, log);
        if (servers.Count == 0)
        {
            log("[Mcp] 已启用但没有可用的服务器配置，MCP 保持关闭");
            return new McpWiring(new McpToolUseCase(Combine(NullMcpToolSource.Instance), audit, log, plugins: plugins), null);
        }

        var http = new HttpFetcher(Timeout.InfiniteTimeSpan, log, "mcp",
            allowAutoRedirect: false, rejectPrivateDestinations: !allowPrivate, rejectLinkLocalWhenPrivate: allowPrivate);
        var source = new McpToolSource(servers, http.SharedClient, log);
        return new McpWiring(new McpToolUseCase(Combine(source), audit, log, plugins: plugins), new McpRuntime(source, http));
    }

    public static McpManagedWiring BuildManaged(
        ISecretsRepository secrets,
        BotAgent.Services.Ops.IAuditChain? audit,
        Action<string> log,
        BotAgent.Adapters.Plugins.PluginPythonSources? packages,
        BotAgent.Services.Plugins.PluginManager? plugins,
        StorageMcpStore store)
    {
        var allowPrivate = On(EnvironmentAliases.Get("BOTAGENT_MCP_ALLOW_PRIVATE"));
        var http = new HttpFetcher(Timeout.InfiniteTimeSpan, log, "mcp",
            allowAutoRedirect: false, rejectPrivateDestinations: !allowPrivate, rejectLinkLocalWhenPrivate: allowPrivate);
        var reloadGate = new SemaphoreSlim(1, 1);
        (bool Ok, string NormalizedUrl, string Reason) ValidateUrl(string url)
        {
            if (!SafeUrl.TryValidate(url, allowPrivate, out var parsed, out var reason))
                return (false, string.Empty, reason);
            if (!string.IsNullOrEmpty(parsed.UserInfo))
                return (false, string.Empty, "userinfo_not_allowed");
            var host = parsed.DnsSafeHost.Trim('[', ']').TrimEnd('.');
            if (System.Net.IPAddress.TryParse(host, out var ip) && OutboundAddressPolicy.IsLinkLocalOrMetadata(ip))
                return (false, string.Empty, "link_local_metadata_blocked");
            return (true, parsed.ToString(), "ok");
        }

        List<McpServerOptions> ReadEnvServers()
            => On(EnvironmentAliases.Get("BOTAGENT_MCP_ENABLED"))
                ? Parse(EnvironmentAliases.Get("BOTAGENT_MCP_SERVERS"), secrets, allowPrivate, log)
                : new List<McpServerOptions>();

        IMcpToolSource BuildEffectiveSource()
        {
            var envServers = ReadEnvServers();
            var merged = new List<McpServerOptions>(envServers);
            var envIds = envServers.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var consoleServer in store.LoadRuntimeServers(ValidateUrl))
            {
                if (merged.Count >= McpLimits.MaxServers) break;
                if (envIds.Contains(consoleServer.Id)) continue;
                merged.Add(consoleServer);
            }
            return merged.Count > 0 ? new McpToolSource(merged, http.SharedClient, log) : NullMcpToolSource.Instance;
        }

        var reloadable = new ReloadableMcpToolSource(BuildEffectiveSource());
        IMcpToolSource combined = packages is null
            ? reloadable
            : new BotAgent.Adapters.Plugins.CompositePackageToolSource(reloadable, packages);
        var useCase = new McpToolUseCase(combined, audit, log, plugins: plugins);
        var runtime = new McpRuntime(reloadable, http);

        IReadOnlyList<ZbaMcpServerView> ListEnvServers()
        {
            return ReadEnvServers().Select(s => new ZbaMcpServerView(
                s.Id,
                Enabled: true,
                Source: "env",
                ReadOnlyLocked: true,
                Endpoint: s.Endpoint,
                AllowedTools: s.AllowedTools.ToArray(),
                ReadOnlyTools: (s.ReadOnlyTools ?? Array.Empty<string>()).ToArray(),
                CallTimeoutSeconds: Math.Clamp((int)Math.Ceiling(s.CallTimeout.TotalSeconds), 1, 120),
                MaxResultBytes: s.MaxResultBytes,
                HasAuthToken: !string.IsNullOrWhiteSpace(s.Token))).ToArray();
        }

        async Task<int> ReloadAsync(CancellationToken ct)
        {
            await reloadGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var next = BuildEffectiveSource();
                await reloadable.SwapAsync(next, ct).ConfigureAwait(false);
                return await useCase.RefreshAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                reloadGate.Release();
            }
        }

        async Task<McpProbeOutcome> ProbeAsync(string? id, string endpoint, string? authToken, int timeoutSeconds, CancellationToken ct)
        {
            var check = ValidateUrl(endpoint);
            if (!check.Ok) return new McpProbeOutcome(false, check.Reason, Array.Empty<McpProbeToolCandidate>());

            string? resolvedToken = null;
            if (!string.IsNullOrWhiteSpace(authToken))
            {
                resolvedToken = authToken.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(id))
            {
                var trimmedId = id.Trim();
                var envIds = ReadEnvServers().Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!envIds.Contains(trimmedId))
                {
                    var saved = store.Load().Servers.FirstOrDefault(s => string.Equals(s.Id, trimmedId, StringComparison.Ordinal));
                    if (saved is not null)
                    {
                        var savedCheck = ValidateUrl(saved.Endpoint);
                        if (savedCheck.Ok && string.Equals(savedCheck.NormalizedUrl, check.NormalizedUrl, StringComparison.Ordinal))
                            resolvedToken = store.LoadToken(trimmedId);
                    }
                }
            }

            var probeOpts = new McpServerOptions(
                string.IsNullOrWhiteSpace(id) ? "probe" : id.Trim(),
                check.NormalizedUrl,
                resolvedToken,
                Array.Empty<string>(),
                TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 60)),
                McpLimits.DefaultResultBytes);
            return await ReloadableMcpToolSource.ProbeServerAsync(probeOpts, http.SharedClient, ct).ConfigureAwait(false);
        }

        return new McpManagedWiring(
            useCase,
            runtime,
            reloadable,
            () => allowPrivate,
            ValidateUrl,
            ListEnvServers,
            ReloadAsync,
            ProbeAsync);
    }

    private static List<McpServerOptions> Parse(string? raw, ISecretsRepository secrets, bool allowPrivate,
        Action<string> log)
    {
        var result = new List<McpServerOptions>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        JsonArray? items;
        try
        {
            items = JsonNode.Parse(raw) as JsonArray;
        }
        catch
        {
            log("[Mcp] BOTAGENT_MCP_SERVERS 不是合法的 JSON 数组");
            return result;
        }

        if (items is null)
        {
            log("[Mcp] BOTAGENT_MCP_SERVERS 必须是数组");
            return result;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in items)
        {
            if (result.Count >= McpLimits.MaxServers)
            {
                break;
            }

            if (node is not JsonObject item)
            {
                continue;
            }

            var id = Text(item["id"]);
            if (!ValidId(id) || !ids.Add(id!))
            {
                log("[Mcp] 跳过服务器：id 缺失/非法/重复");
                continue;
            }

            if (!On(item["enabled"]) && item["enabled"] is not null)
            {
                log($"[Mcp] 跳过服务器 {id}：enabled=false");
                continue;
            }

            var url = Text(item["url"]);
            if (!SafeUrl.TryValidate(url, allowPrivate, out var uri, out var reason))
            {
                log($"[Mcp] 跳过服务器 {id}：{reason}");
                continue;
            }

            var allowed = Strings(item["allowedTools"]);
            if (allowed.Count == 0)
            {
                log($"[Mcp] 跳过服务器 {id}：allowedTools 为空（没有显式授权）");
                continue;
            }
            var readOnlyTools = Strings(item["readOnlyTools"]);

            var tokenName = Text(item["tokenSecret"]);
            string? token = null;
            if (!string.IsNullOrWhiteSpace(tokenName))
            {
                token = secrets.Load(tokenName!) ?? Environment.GetEnvironmentVariable(tokenName!);
                if (string.IsNullOrWhiteSpace(token))
                {
                    log($"[Mcp] 跳过服务器 {id}：密钥 {tokenName} 未配置");
                    continue;
                }
            }

            var timeout = Int(item["callTimeoutSeconds"], McpLimits.DefaultCallTimeoutSeconds, 1, 120);
            var maxBytes = Int(item["maxResultBytes"], McpLimits.DefaultResultBytes, 1024, McpLimits.MaxResultBytes);
            result.Add(new McpServerOptions(id!, uri.ToString(), token, allowed,
                TimeSpan.FromSeconds(timeout), maxBytes, readOnlyTools));
            log($"[Mcp] 已登记服务器 {id}（allowedTools={allowed.Count}）");
        }

        return result;
    }

    private static bool ValidId(string? id)
        => id is { Length: > 0 and <= 32 }
           && id.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-');

    private static List<string> Strings(JsonNode? node)
    {
        var values = new List<string>();
        if (node is not JsonArray array)
        {
            return values;
        }

        foreach (var item in array)
        {
            var value = Text(item);
            if (!string.IsNullOrWhiteSpace(value) && !values.Contains(value, StringComparer.Ordinal))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null;

    private static int Int(JsonNode? node, int fallback, int min, int max)
        => node is JsonValue value && value.TryGetValue<int>(out var number) ? Math.Clamp(number, min, max) : fallback;

    private static bool On(string? value)
        => value is not null && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static bool On(JsonNode? value)
        => value is JsonValue node && node.TryGetValue<bool>(out var enabled) ? enabled : On(Text(value));
}
