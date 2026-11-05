using System.Text.Json.Nodes;

namespace BotAgent.Panel;

internal static partial class ZbaOpenApi
{
    private static JsonObject LogFeedCursorSchema() => new()
    {
        ["type"] = "string", ["minLength"] = 37, ["maxLength"] = ZbaLogFeedCursor.MaxLength,
        ["description"] = "Opaque delivery cursor. Reuse unchanged; omit only for the initial latest 200 rows."
    };

    private static JsonObject LogFeedCursorParameter() => new()
    {
        ["name"] = "cursor", ["in"] = "query", ["required"] = false, ["schema"] = LogFeedCursorSchema(),
        ["description"] = "Malformed cursors return 400. Expired/restarted/ahead cursors reset to the latest available 200 rows."
    };

    private static JsonObject LogFeedEntrySchema() => RequiredObject(
        ["id", "sequence", "time", "level", "source", "message", "redacted"],
        ("id", new JsonObject { ["type"] = "string", ["pattern"] = "^[a-f0-9]{32}:[1-9][0-9]{0,18}$" }),
        ("sequence", new JsonObject { ["type"] = "string", ["pattern"] = "^[1-9][0-9]{0,18}$" }),
        ("time", Integer()),
        ("level", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("info", "warn", "error") }),
        ("source", new JsonObject { ["type"] = "string", ["maxLength"] = 64 }),
        ("message", new JsonObject { ["type"] = "string", ["maxLength"] = 512 }), ("redacted", Boolean()));

    private static JsonObject LogFeedPageSchema() => RequiredObject(
        ["items", "cursor", "hasMore", "capacity"],
        ("items", new JsonObject { ["type"] = "array", ["items"] = Ref("ZbaLogFeedEntry"), ["maxItems"] = ZbaLogFeedCursor.PageSize }),
        ("cursor", LogFeedCursorSchema()), ("hasMore", Boolean()),
        ("capacity", new JsonObject { ["type"] = "integer", ["const"] = ZbaContract.MaxLogEntries }),
        ("resetReason", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("expired", "restart", "ahead") }),
        ("omittedCount", new JsonObject
        {
            ["type"] = "integer", ["minimum"] = 0,
            ["description"] = "Present only when exactly known: expired same-epoch cursor to first returned row, excluding that row."
        }));
}
