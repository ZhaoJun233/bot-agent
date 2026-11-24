using System.Text.Json.Nodes;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Plugins;

namespace BotAgent.Panel;

public sealed record ZbaAccessRoleItem(
    string PlatformId,
    string AccountScope,
    string InstanceId,
    string? ScopeConversationKey,
    string? ScopeConversationKeyRaw,
    string NativeUserId,
    string? NativeUserIdRaw,
    WorkspaceRole Role,
    string? Note,
    string? NoteRaw = null);

public sealed record ZbaAccessRolesResponse(
    long Revision,
    bool Masked,
    IReadOnlyList<ZbaAccessRoleItem> Items);

public sealed record ZbaAccessRoleBindingInput(
    string PlatformId,
    string AccountScope,
    string InstanceId,
    string? ScopeConversationKey,
    string NativeUserId,
    WorkspaceRole Role,
    string? Note = null);

public sealed record ZbaAccessRolesSaveRequest(
    long ExpectedRevision,
    string IdempotencyKey,
    IReadOnlyList<ZbaAccessRoleBindingInput> Bindings);

public sealed record ZbaAccessRolesSaveResult(
    bool Ok,
    bool Replayed,
    string? ReasonCode,
    ZbaAccessRolesResponse Snapshot);

/// <summary>跨平台轻量 RBAC 角色绑定 ZBA 扩展 HTTP 契约（<c>owner = "system.agent"</c>）。</summary>
public static class AccessRoleHttpContract
{
    public const string OwnerId = "system.agent";

    public static PluginHttpDescription Describe() => new(OwnerId, System.Array.AsReadOnly(new PluginHttpRoute[]
    {
        new("GET", "/zba/v1/permissions/roles", false, "permissions.roles.get", "Read cross-platform group and private RBAC role bindings", null, "ZbaAccessRolesResponse"),
        new("POST", "/zba/v1/permissions/roles/save", true, "permissions.roles.save", "Save validated RBAC role bindings with CAS and hot-reload resolver", "ZbaAccessRolesSaveRequest", "ZbaAccessRolesSaveResult")
    }), Schemas().ToJsonString());

    public static PluginHttpBinding<TCaller> Bind<TCaller>(
        Func<TCaller, CancellationToken, ValueTask<ZbaAccessRolesResponse>> list,
        Func<TCaller, ZbaAccessRolesSaveRequest, CancellationToken, ValueTask<ZbaAccessRolesSaveResult>> save)
        => new(Describe(), async (operation, caller, body, ct) => operation switch
        {
            "permissions.roles.get" => await list(caller, ct).ConfigureAwait(false),
            "permissions.roles.save" => await save(caller, body.Read<ZbaAccessRolesSaveRequest>(), ct).ConfigureAwait(false),
            _ => throw new PluginHttpException(404, "not_found")
        });

    private static JsonObject Schemas() => new()
    {
        ["ZbaAccessRoleItem"] = Object(
            ("platformId", String()), ("accountScope", String()), ("instanceId", String()),
            ("scopeConversationKey", NullableString()), ("scopeConversationKeyRaw", NullableString()),
            ("nativeUserId", String()), ("nativeUserIdRaw", NullableString()),
            ("role", Integer()), ("note", NullableString()), ("noteRaw", NullableString())),
        ["ZbaAccessRolesResponse"] = Object(
            ("revision", Integer()), ("masked", Boolean()), ("items", Array(Ref("ZbaAccessRoleItem")))),
        ["ZbaAccessRoleBindingInput"] = RequiredObject(
            new[] { "platformId", "accountScope", "instanceId", "nativeUserId", "role" },
            ("platformId", String()), ("accountScope", String()), ("instanceId", String()),
            ("scopeConversationKey", NullableString()), ("nativeUserId", String()),
            ("role", Integer()), ("note", NullableString())),
        ["ZbaAccessRolesSaveRequest"] = RequiredObject(
            new[] { "expectedRevision", "idempotencyKey", "bindings" },
            ("expectedRevision", Integer()), ("idempotencyKey", String()),
            ("bindings", Array(Ref("ZbaAccessRoleBindingInput")))),
        ["ZbaAccessRolesSaveResult"] = Object(
            ("ok", Boolean()), ("replayed", Boolean()), ("reasonCode", NullableString()),
            ("snapshot", Ref("ZbaAccessRolesResponse")))
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
}
