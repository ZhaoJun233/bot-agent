using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Scheduler;

namespace BotAgent.Panel;

public sealed record ZbaScheduledJobView(
    string JobId,
    string ScopeKey,
    string MaskedScope,
    string Kind,
    string? CronExpr,
    string TimezoneIana,
    string Text,
    string MissedPolicy,
    bool Enabled,
    long NextFireUnix,
    long? LastFiredUnix,
    int FailureCount);

public sealed record ZbaScheduledJobRunView(
    string RunKey,
    string JobId,
    long ScheduledUnix,
    long StartedUnix,
    long? FinishedUnix,
    string DeliveryStatus,
    string? ReasonCode);

public sealed record ZbaSchedulerJobsResponse(
    long Revision,
    IReadOnlyList<ZbaScheduledJobView> Jobs);

public sealed record ZbaSchedulerSaveRequest(
    [property: JsonRequired] long ExpectedRevision,
    [property: JsonRequired] string IdempotencyKey,
    string? JobId,
    [property: JsonRequired] string ScopeKey,
    [property: JsonRequired] string Kind,
    string? CronExpr,
    string? TimezoneIana,
    [property: JsonRequired] string Text,
    string? MissedPolicy = MissedFirePolicy.SkipAndNotify,
    bool Enabled = true,
    long NextFireUnix = 0);

public sealed record ZbaSchedulerDeleteRequest(
    [property: JsonRequired] long ExpectedRevision,
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] string JobId);

public sealed record ZbaSchedulerWriteResult(
    bool Ok,
    bool Replayed,
    string? ReasonCode,
    ZbaSchedulerJobsResponse Snapshot);

public sealed record ZbaSchedulerRunsRequest(
    string? JobId = null,
    int Limit = 50);

public sealed record ZbaSchedulerRunsResponse(
    IReadOnlyList<ZbaScheduledJobRunView> Runs);

/// <summary>定时与提醒 ZBA 扩展 HTTP 契约（<c>owner = "preset.feature.scheduler"</c>）。</summary>
public static class SchedulerHttpContract
{
    public const string OwnerId = "preset.feature.scheduler";

    public static PluginHttpDescription Describe() => new(OwnerId, System.Array.AsReadOnly(new PluginHttpRoute[]
    {
        new("GET", "/zba/v1/scheduler/jobs", false, "scheduler.jobs.list", "List persisted scheduled jobs and reminders", null, "ZbaSchedulerJobsResponse"),
        new("POST", "/zba/v1/scheduler/save", true, "scheduler.jobs.save", "Upsert one scheduled job with CAS and idempotency key", "ZbaSchedulerSaveRequest", "ZbaSchedulerWriteResult"),
        new("POST", "/zba/v1/scheduler/delete", true, "scheduler.jobs.delete", "Delete one scheduled job with CAS and idempotency key", "ZbaSchedulerDeleteRequest", "ZbaSchedulerWriteResult"),
        new("POST", "/zba/v1/scheduler/runs", false, "scheduler.runs.list", "Query bounded scheduled job run history", "ZbaSchedulerRunsRequest", "ZbaSchedulerRunsResponse")
    }), Schemas().ToJsonString());

    public static PluginHttpBinding<TCaller> Bind<TCaller>(
        Func<TCaller, CancellationToken, ValueTask<ZbaSchedulerJobsResponse>> list,
        Func<TCaller, ZbaSchedulerSaveRequest, CancellationToken, ValueTask<ZbaSchedulerWriteResult>> save,
        Func<TCaller, ZbaSchedulerDeleteRequest, CancellationToken, ValueTask<ZbaSchedulerWriteResult>> delete,
        Func<TCaller, ZbaSchedulerRunsRequest, CancellationToken, ValueTask<ZbaSchedulerRunsResponse>> runs)
        => new(Describe(), async (operation, caller, body, ct) => operation switch
        {
            "scheduler.jobs.list" => await list(caller, ct).ConfigureAwait(false),
            "scheduler.jobs.save" => await save(caller, body.Read<ZbaSchedulerSaveRequest>(), ct).ConfigureAwait(false),
            "scheduler.jobs.delete" => await delete(caller, body.Read<ZbaSchedulerDeleteRequest>(), ct).ConfigureAwait(false),
            "scheduler.runs.list" => await runs(caller, body.Read<ZbaSchedulerRunsRequest>(), ct).ConfigureAwait(false),
            _ => throw new PluginHttpException(404, "not_found")
        });

    private static JsonObject Schemas() => new()
    {
        ["ZbaScheduledJobView"] = Object(
            ("jobId", String()), ("scopeKey", String()), ("maskedScope", String()),
            ("kind", String()), ("cronExpr", NullableString()), ("timezoneIana", String()),
            ("text", String()), ("missedPolicy", String()), ("enabled", Boolean()),
            ("nextFireUnix", Integer()), ("lastFiredUnix", NullableInteger()), ("failureCount", Integer())),
        ["ZbaScheduledJobRunView"] = Object(
            ("runKey", String()), ("jobId", String()), ("scheduledUnix", Integer()),
            ("startedUnix", Integer()), ("finishedUnix", NullableInteger()),
            ("deliveryStatus", String()), ("reasonCode", NullableString())),
        ["ZbaSchedulerJobsResponse"] = Object(
            ("revision", Integer()),
            ("jobs", Array(Ref("ZbaScheduledJobView")))),
        ["ZbaSchedulerSaveRequest"] = RequiredObject(
            new[] { "expectedRevision", "idempotencyKey", "scopeKey", "kind", "text" },
            ("expectedRevision", Integer()), ("idempotencyKey", String()), ("jobId", NullableString()),
            ("scopeKey", String()), ("kind", String()), ("cronExpr", NullableString()),
            ("timezoneIana", NullableString()), ("text", String()), ("missedPolicy", NullableString()),
            ("enabled", Boolean()), ("nextFireUnix", Integer())),
        ["ZbaSchedulerDeleteRequest"] = RequiredObject(
            new[] { "expectedRevision", "idempotencyKey", "jobId" },
            ("expectedRevision", Integer()), ("idempotencyKey", String()), ("jobId", String())),
        ["ZbaSchedulerWriteResult"] = Object(
            ("ok", Boolean()), ("replayed", Boolean()), ("reasonCode", NullableString()),
            ("snapshot", Ref("ZbaSchedulerJobsResponse"))),
        ["ZbaSchedulerRunsRequest"] = Object(
            ("jobId", NullableString()), ("limit", Integer())),
        ["ZbaSchedulerRunsResponse"] = Object(
            ("runs", Array(Ref("ZbaScheduledJobRunView"))))
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
