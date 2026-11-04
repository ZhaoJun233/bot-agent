using System.Text.Json.Nodes;
using BotAgent.Adapters.Net;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Mcp;
using BotAgent.Domain.Ports;
using BotAgent.Mcp;
using BotAgent.Services.Mcp;
using BotAgent.Services.Net;

namespace BotAgent.Host;

/// <summary>Wave4 的 MCP 运行体：客户端 + 它专用的出网实例（共用宿主 SSRF 连接策略）。</summary>
internal sealed class McpRuntime(McpToolSource source, HttpFetcher http) : IAsyncDisposable
{
    public McpToolSource Source { get; } = source;

    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync().ConfigureAwait(false);
        http.Dispose();
    }
}

internal sealed record McpWiring(McpToolUseCase UseCase, McpRuntime? Runtime);

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
        Action<string> log)
    {
        if (!On(EnvironmentAliases.Get("BOTAGENT_MCP_ENABLED")))
        {
            return new McpWiring(new McpToolUseCase(NullMcpToolSource.Instance, audit, log), null);
        }

        var allowPrivate = On(EnvironmentAliases.Get("BOTAGENT_MCP_ALLOW_PRIVATE"));
        var raw = EnvironmentAliases.Get("BOTAGENT_MCP_SERVERS");
        var servers = Parse(raw, secrets, allowPrivate, log);
        if (servers.Count == 0)
        {
            log("[Mcp] 已启用但没有可用的服务器配置，MCP 保持关闭");
            return new McpWiring(new McpToolUseCase(NullMcpToolSource.Instance, audit, log), null);
        }

        var http = new HttpFetcher(Timeout.InfiniteTimeSpan, log, "mcp",
            allowAutoRedirect: false, rejectPrivateDestinations: !allowPrivate);
        var source = new McpToolSource(servers, http.SharedClient, log);
        return new McpWiring(new McpToolUseCase(source, audit, log), new McpRuntime(source, http));
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
                TimeSpan.FromSeconds(timeout), maxBytes));
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
