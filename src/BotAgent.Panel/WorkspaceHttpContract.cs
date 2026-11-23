using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BotAgent.Domain.Plugins;

namespace BotAgent.Panel;

public sealed record ZbaWorkspaceMessageView(
    long Sequence,
    string Role,
    string Text,
    string Mode,
    string? AttachmentName,
    long CreatedUnix,
    string? TaskId = null,
    string? Status = null);

public sealed record ZbaWorkspaceMessagesResponse(
    string ScopeKey,
    long LastSequence,
    IReadOnlyList<ZbaWorkspaceMessageView> Messages);

public sealed record ZbaWorkspaceSendRequest(
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] string Text,
    string Mode = "chat",
    string? AttachmentName = null,
    string? AttachmentBase64 = null);

public sealed record ZbaWorkspaceSendResult(
    bool Ok,
    bool Replayed,
    string OperationKey,
    string? ReasonCode,
    string? TaskId,
    ZbaWorkspaceMessagesResponse Snapshot);

public sealed record ZbaWorkspaceClearResult(
    bool Ok,
    ZbaWorkspaceMessagesResponse Snapshot);

public sealed record ZbaWorkspaceIdentityLinkView(
    string CanonicalPersonId,
    string PlatformId,
    string AccountScope,
    string InstanceId,
    string MaskedUserId,
    long LinkedUnix);

public sealed record ZbaWorkspaceIdentityResponse(
    bool Enabled,
    bool ShareTodos,
    bool ShareKnowledge,
    bool SharePreferences,
    IReadOnlyList<ZbaWorkspaceIdentityLinkView> Links,
    string? WorkspaceCanonicalPersonId = null);

public sealed record ZbaWorkspaceIdentityToggleRequest(
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] bool ShareTodos,
    [property: JsonRequired] bool ShareKnowledge,
    [property: JsonRequired] bool SharePreferences,
    string? WorkspaceCanonicalPersonId = null);

/// <summary>
/// 个人工作台 ZBA 扩展 HTTP 契约（<c>owner = "system.workspace"</c>）：
/// 严格仅读写 <c>workspace:owner</c> 本人专属会话，绝不暴露任何 QQ/飞书群聊或私聊正文。
/// </summary>
public static class WorkspaceHttpContract
{
    public const string OwnerId = "system.workspace";

    public static PluginHttpDescription Describe() => new(OwnerId, System.Array.AsReadOnly(new PluginHttpRoute[]
    {
        new("GET", "/zba/v1/workspace/messages", true, "workspace.messages.read", "Read personal workspace:owner message stream", null, "ZbaWorkspaceMessagesResponse"),
        new("POST", "/zba/v1/workspace/messages/send", true, "workspace.messages.send", "Send a personal workspace prompt or bounded document attachment", "ZbaWorkspaceSendRequest", "ZbaWorkspaceSendResult"),
        new("POST", "/zba/v1/workspace/messages/clear", true, "workspace.messages.clear", "Clear personal workspace:owner conversation buffer", null, "ZbaWorkspaceClearResult"),
        new("GET", "/zba/v1/workspace/identity", false, "workspace.identity.read", "Read ADR-0006 optional cross-platform identity link status", null, "ZbaWorkspaceIdentityResponse"),
        new("POST", "/zba/v1/workspace/identity/toggle", true, "workspace.identity.toggle", "Toggle ADR-0006 optional cross-platform identity linking and resource sharing", "ZbaWorkspaceIdentityToggleRequest", "ZbaWorkspaceIdentityResponse")
    }), Schemas().ToJsonString());

    public static PluginHttpBinding<TCaller> Bind<TCaller>(
        Func<TCaller, CancellationToken, ValueTask<ZbaWorkspaceMessagesResponse>> readMessages,
        Func<TCaller, ZbaWorkspaceSendRequest, CancellationToken, ValueTask<ZbaWorkspaceSendResult>> sendMessage,
        Func<TCaller, CancellationToken, ValueTask<ZbaWorkspaceClearResult>> clearMessages,
        Func<TCaller, CancellationToken, ValueTask<ZbaWorkspaceIdentityResponse>> readIdentity,
        Func<TCaller, ZbaWorkspaceIdentityToggleRequest, CancellationToken, ValueTask<ZbaWorkspaceIdentityResponse>> toggleIdentity)
        => new(Describe(), async (operation, caller, body, ct) => operation switch
        {
            "workspace.messages.read" => await readMessages(caller, ct).ConfigureAwait(false),
            "workspace.messages.send" => await sendMessage(caller, body.Read<ZbaWorkspaceSendRequest>(), ct).ConfigureAwait(false),
            "workspace.messages.clear" => await clearMessages(caller, ct).ConfigureAwait(false),
            "workspace.identity.read" => await readIdentity(caller, ct).ConfigureAwait(false),
            "workspace.identity.toggle" => await toggleIdentity(caller, body.Read<ZbaWorkspaceIdentityToggleRequest>(), ct).ConfigureAwait(false),
            _ => throw new PluginHttpException(404, "not_found")
        });

    private static JsonObject Schemas() => new()
    {
        ["ZbaWorkspaceMessageView"] = Object(
            ("sequence", Integer()), ("role", String()), ("text", String()),
            ("mode", String()), ("attachmentName", NullableString()),
            ("createdUnix", Integer()), ("taskId", NullableString()), ("status", NullableString())),
        ["ZbaWorkspaceMessagesResponse"] = Object(
            ("scopeKey", String()), ("lastSequence", Integer()),
            ("messages", Array(Ref("ZbaWorkspaceMessageView")))),
        ["ZbaWorkspaceSendRequest"] = RequiredObject(
            new[] { "idempotencyKey", "text" },
            ("idempotencyKey", String()), ("text", String()), ("mode", String()),
            ("attachmentName", NullableString()), ("attachmentBase64", NullableString())),
        ["ZbaWorkspaceSendResult"] = Object(
            ("ok", Boolean()), ("replayed", Boolean()), ("operationKey", String()),
            ("reasonCode", NullableString()), ("taskId", NullableString()),
            ("snapshot", Ref("ZbaWorkspaceMessagesResponse"))),
        ["ZbaWorkspaceClearResult"] = Object(
            ("ok", Boolean()), ("snapshot", Ref("ZbaWorkspaceMessagesResponse"))),
        ["ZbaWorkspaceIdentityLinkView"] = Object(
            ("canonicalPersonId", String()), ("platformId", String()), ("accountScope", String()),
            ("instanceId", String()), ("maskedUserId", String()), ("linkedUnix", Integer())),
        ["ZbaWorkspaceIdentityResponse"] = Object(
            ("enabled", Boolean()), ("shareTodos", Boolean()),
            ("shareKnowledge", Boolean()), ("sharePreferences", Boolean()),
            ("links", Array(Ref("ZbaWorkspaceIdentityLinkView"))),
            ("workspaceCanonicalPersonId", NullableString())),
        ["ZbaWorkspaceIdentityToggleRequest"] = RequiredObject(
            new[] { "enabled", "shareTodos", "shareKnowledge", "sharePreferences" },
            ("enabled", Boolean()), ("shareTodos", Boolean()),
            ("shareKnowledge", Boolean()), ("sharePreferences", Boolean()),
            ("workspaceCanonicalPersonId", NullableString()))
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
