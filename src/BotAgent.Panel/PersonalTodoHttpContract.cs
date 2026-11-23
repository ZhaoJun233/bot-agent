using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BotAgent.Domain.Plugins;

namespace BotAgent.Panel;

public sealed record ZbaPersonalTodoView(
    string TodoId,
    string OwnerScopeKey,
    string Title,
    string? Notes,
    int Priority,
    string Status,
    long? DueUnix,
    string? LinkedJobId,
    string? SourceHint,
    long CreatedUnix,
    long UpdatedUnix);

public sealed record ZbaPersonalTodosResponse(
    long Revision,
    IReadOnlyList<ZbaPersonalTodoView> Items);

public sealed record ZbaPersonalTodoSaveRequest(
    [property: JsonRequired] long ExpectedRevision,
    [property: JsonRequired] string IdempotencyKey,
    string? TodoId,
    [property: JsonRequired] string Title,
    string? Notes = null,
    int Priority = 2,
    string Status = "open",
    long? DueUnix = null,
    string? SourceHint = null,
    string? TimezoneIana = null);

public sealed record ZbaPersonalTodoDeleteRequest(
    [property: JsonRequired] long ExpectedRevision,
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] string TodoId);

public sealed record ZbaPersonalTodoWriteResult(
    bool Ok,
    bool Replayed,
    string? ReasonCode,
    ZbaPersonalTodosResponse Snapshot);

/// <summary>个人待办 ZBA 扩展 HTTP 契约（<c>owner = "preset.feature.personal-todo"</c>），仅限本人工作台与私有作用域。</summary>
public static class PersonalTodoHttpContract
{
    public const string OwnerId = "preset.feature.personal-todo";

    public static PluginHttpDescription Describe() => new(OwnerId, System.Array.AsReadOnly(new PluginHttpRoute[]
    {
        new("GET", "/zba/v1/todos", true, "todos.list", "List owner personal todos (strictly private/workspace scope)", null, "ZbaPersonalTodosResponse"),
        new("POST", "/zba/v1/todos/save", true, "todos.save", "Upsert or complete a personal todo with optional linked reminder", "ZbaPersonalTodoSaveRequest", "ZbaPersonalTodoWriteResult"),
        new("POST", "/zba/v1/todos/delete", true, "todos.delete", "Delete a personal todo and cancel any linked reminder", "ZbaPersonalTodoDeleteRequest", "ZbaPersonalTodoWriteResult")
    }), Schemas().ToJsonString());

    public static PluginHttpBinding<TCaller> Bind<TCaller>(
        Func<TCaller, CancellationToken, ValueTask<ZbaPersonalTodosResponse>> list,
        Func<TCaller, ZbaPersonalTodoSaveRequest, CancellationToken, ValueTask<ZbaPersonalTodoWriteResult>> save,
        Func<TCaller, ZbaPersonalTodoDeleteRequest, CancellationToken, ValueTask<ZbaPersonalTodoWriteResult>> delete)
        => new(Describe(), async (operation, caller, body, ct) => operation switch
        {
            "todos.list" => await list(caller, ct).ConfigureAwait(false),
            "todos.save" => await save(caller, body.Read<ZbaPersonalTodoSaveRequest>(), ct).ConfigureAwait(false),
            "todos.delete" => await delete(caller, body.Read<ZbaPersonalTodoDeleteRequest>(), ct).ConfigureAwait(false),
            _ => throw new PluginHttpException(404, "not_found")
        });

    private static JsonObject Schemas() => new()
    {
        ["ZbaPersonalTodoView"] = Object(
            ("todoId", String()), ("ownerScopeKey", String()), ("title", String()),
            ("notes", NullableString()), ("priority", Integer()), ("status", String()),
            ("dueUnix", NullableInteger()), ("linkedJobId", NullableString()),
            ("sourceHint", NullableString()), ("createdUnix", Integer()), ("updatedUnix", Integer())),
        ["ZbaPersonalTodosResponse"] = Object(
            ("revision", Integer()),
            ("items", Array(Ref("ZbaPersonalTodoView")))),
        ["ZbaPersonalTodoSaveRequest"] = RequiredObject(
            new[] { "expectedRevision", "idempotencyKey", "title" },
            ("expectedRevision", Integer()), ("idempotencyKey", String()), ("todoId", NullableString()),
            ("title", String()), ("notes", NullableString()), ("priority", Integer()),
            ("status", String()), ("dueUnix", NullableInteger()), ("sourceHint", NullableString()),
            ("timezoneIana", NullableString())),
        ["ZbaPersonalTodoDeleteRequest"] = RequiredObject(
            new[] { "expectedRevision", "idempotencyKey", "todoId" },
            ("expectedRevision", Integer()), ("idempotencyKey", String()), ("todoId", String())),
        ["ZbaPersonalTodoWriteResult"] = Object(
            ("ok", Boolean()), ("replayed", Boolean()), ("reasonCode", NullableString()),
            ("snapshot", Ref("ZbaPersonalTodosResponse")))
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
    private static JsonObject NullableInteger() => new() { ["type"] = new JsonArray("integer", "null") };
    private static JsonObject Boolean() => new() { ["type"] = "boolean" };
    private static JsonObject Array(JsonObject items) => new() { ["type"] = "array", ["items"] = items };
}
