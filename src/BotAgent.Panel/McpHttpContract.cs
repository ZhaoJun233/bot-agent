using System.Text.Json.Nodes;
using BotAgent.Domain.Mcp;
using BotAgent.Domain.Plugins;

namespace BotAgent.Panel;

public sealed record ZbaMcpServerView(
    string Id,
    bool Enabled,
    string Source,
    bool ReadOnlyLocked,
    string Endpoint,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> ReadOnlyTools,
    int CallTimeoutSeconds,
    int MaxResultBytes,
    bool HasAuthToken);

public sealed record ZbaMcpDiscoveredToolView(
    string Name,
    string QualifiedName,
    string ServerId,
    string Description,
    bool ReadOnlyHint,
    bool ReadOnlyConfirmed);

public sealed record ZbaMcpServersResponse(
    long Revision,
    bool AllowPrivate,
    IReadOnlyList<ZbaMcpServerView> Servers,
    IReadOnlyList<ZbaMcpDiscoveredToolView> DiscoveredTools);

public sealed record ZbaMcpServerSaveRequest(
    long ExpectedRevision,
    string IdempotencyKey,
    string Id,
    bool Enabled,
    string Endpoint,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string>? ReadOnlyTools = null,
    int CallTimeoutSeconds = McpLimits.DefaultCallTimeoutSeconds,
    int MaxResultBytes = McpLimits.DefaultResultBytes,
    string? AuthToken = null,
    bool ClearAuthToken = false);

public sealed record ZbaMcpServerDeleteRequest(
    long ExpectedRevision,
    string IdempotencyKey,
    string Id);

public sealed record ZbaMcpServerWriteResult(
    bool Ok,
    bool Replayed,
    string? ReasonCode,
    ZbaMcpServersResponse Snapshot);

public sealed record ZbaMcpServerTestRequest(
    string Endpoint,
    string? Id = null,
    string? AuthToken = null,
    int CallTimeoutSeconds = 10);

public sealed record ZbaMcpProbeToolView(
    string Name,
    string Description,
    bool ReadOnlyHint);

public sealed record ZbaMcpServerTestResult(
    bool Ok,
    string ReasonCode,
    IReadOnlyList<ZbaMcpProbeToolView> Tools);

public sealed record ZbaMcpReloadResult(
    bool Ok,
    int ToolCount,
    ZbaMcpServersResponse Snapshot);

/// <summary>MCP 控制台管理 ZBA 扩展 HTTP 契约（<c>owner = "system.mcp-tools"</c>）。</summary>
public static class McpHttpContract
{
    public const string OwnerId = "system.mcp-tools";

    public static PluginHttpDescription Describe() => new(OwnerId, System.Array.AsReadOnly(new PluginHttpRoute[]
    {
        new("GET", "/zba/v1/mcp/servers", false, "mcp.servers.list", "Read env-locked and console-managed MCP servers and discovered tools", null, "ZbaMcpServersResponse"),
        new("POST", "/zba/v1/mcp/servers/save", true, "mcp.servers.save", "Upsert one console-managed MCP server with CAS and write-only token", "ZbaMcpServerSaveRequest", "ZbaMcpServerWriteResult"),
        new("POST", "/zba/v1/mcp/servers/delete", true, "mcp.servers.delete", "Delete one console-managed MCP server with CAS", "ZbaMcpServerDeleteRequest", "ZbaMcpServerWriteResult"),
        new("POST", "/zba/v1/mcp/servers/test", true, "mcp.servers.test", "Probe a Streamable HTTP MCP server endpoint with SSRF check", "ZbaMcpServerTestRequest", "ZbaMcpServerTestResult"),
        new("POST", "/zba/v1/mcp/reload", true, "mcp.reload", "Atomically reload managed MCP servers and refresh tool catalog", null, "ZbaMcpReloadResult")
    }), Schemas().ToJsonString());

    public static PluginHttpBinding<TCaller> Bind<TCaller>(
        Func<TCaller, CancellationToken, ValueTask<ZbaMcpServersResponse>> list,
        Func<TCaller, ZbaMcpServerSaveRequest, CancellationToken, ValueTask<ZbaMcpServerWriteResult>> save,
        Func<TCaller, ZbaMcpServerDeleteRequest, CancellationToken, ValueTask<ZbaMcpServerWriteResult>> delete,
        Func<TCaller, ZbaMcpServerTestRequest, CancellationToken, ValueTask<ZbaMcpServerTestResult>> test,
        Func<TCaller, CancellationToken, ValueTask<ZbaMcpReloadResult>> reload)
        => new(Describe(), async (operation, caller, body, ct) => operation switch
        {
            "mcp.servers.list" => await list(caller, ct).ConfigureAwait(false),
            "mcp.servers.save" => await save(caller, body.Read<ZbaMcpServerSaveRequest>(), ct).ConfigureAwait(false),
            "mcp.servers.delete" => await delete(caller, body.Read<ZbaMcpServerDeleteRequest>(), ct).ConfigureAwait(false),
            "mcp.servers.test" => await test(caller, body.Read<ZbaMcpServerTestRequest>(), ct).ConfigureAwait(false),
            "mcp.reload" => await reload(caller, ct).ConfigureAwait(false),
            _ => throw new PluginHttpException(404, "not_found")
        });

    private static JsonObject Schemas() => new()
    {
        ["ZbaMcpServerView"] = Object(
            ("id", String()), ("enabled", Boolean()), ("source", String()), ("readOnlyLocked", Boolean()),
            ("endpoint", String()), ("allowedTools", Array(String())), ("readOnlyTools", Array(String())),
            ("callTimeoutSeconds", Integer()), ("maxResultBytes", Integer()), ("hasAuthToken", Boolean())),
        ["ZbaMcpDiscoveredToolView"] = Object(
            ("name", String()), ("qualifiedName", String()), ("serverId", String()),
            ("description", String()), ("readOnlyHint", Boolean()), ("readOnlyConfirmed", Boolean())),
        ["ZbaMcpServersResponse"] = Object(
            ("revision", Integer()), ("allowPrivate", Boolean()),
            ("servers", Array(Ref("ZbaMcpServerView"))),
            ("discoveredTools", Array(Ref("ZbaMcpDiscoveredToolView")))),
        ["ZbaMcpServerSaveRequest"] = RequiredObject(
            new[] { "expectedRevision", "idempotencyKey", "id", "enabled", "endpoint", "allowedTools" },
            ("expectedRevision", Integer()), ("idempotencyKey", String()), ("id", String()),
            ("enabled", Boolean()), ("endpoint", String()), ("allowedTools", Array(String())),
            ("readOnlyTools", ArrayOrNull(String())), ("callTimeoutSeconds", Integer()),
            ("maxResultBytes", Integer()), ("authToken", NullableString()), ("clearAuthToken", Boolean())),
        ["ZbaMcpServerDeleteRequest"] = RequiredObject(
            new[] { "expectedRevision", "idempotencyKey", "id" },
            ("expectedRevision", Integer()), ("idempotencyKey", String()), ("id", String())),
        ["ZbaMcpServerWriteResult"] = Object(
            ("ok", Boolean()), ("replayed", Boolean()), ("reasonCode", NullableString()),
            ("snapshot", Ref("ZbaMcpServersResponse"))),
        ["ZbaMcpServerTestRequest"] = RequiredObject(
            new[] { "endpoint" },
            ("endpoint", String()), ("id", NullableString()), ("authToken", NullableString()), ("callTimeoutSeconds", Integer())),
        ["ZbaMcpProbeToolView"] = Object(
            ("name", String()), ("description", String()), ("readOnlyHint", Boolean())),
        ["ZbaMcpServerTestResult"] = Object(
            ("ok", Boolean()), ("reasonCode", String()), ("tools", Array(Ref("ZbaMcpProbeToolView")))),
        ["ZbaMcpReloadResult"] = Object(
            ("ok", Boolean()), ("toolCount", Integer()), ("snapshot", Ref("ZbaMcpServersResponse")))
    };

    private static JsonObject Ref(string name) => new() { ["$ref"] = "#/components/schemas/" + name };
    private static JsonObject Object(params (string Name, JsonObject Schema)[] properties) => new() { ["type"] = "object", ["properties"] = Properties(properties) };
    private static JsonObject RequiredObject(string[] required, params (string Name, JsonObject Schema)[] properties)
    {
        var schema = Object(properties);
        schema["required"] = new JsonArray(required.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
        return schema;
    }
    private static JsonObject Properties(params (string Name, JsonObject Schema)[] properties)
    {
        var obj = new JsonObject();
        foreach (var (name, schema) in properties) obj[name] = schema;
        return obj;
    }
    private static JsonObject String() => new() { ["type"] = "string" };
    private static JsonObject NullableString() => new() { ["type"] = new JsonArray("string", "null") };
    private static JsonObject Integer() => new() { ["type"] = "integer" };
    private static JsonObject Boolean() => new() { ["type"] = "boolean" };
    private static JsonObject Array(JsonObject items) => new() { ["type"] = "array", ["items"] = items };
    private static JsonObject ArrayOrNull(JsonObject items) => new() { ["type"] = new JsonArray("array", "null"), ["items"] = items };
}
