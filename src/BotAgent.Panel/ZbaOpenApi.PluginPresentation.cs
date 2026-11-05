using System.Text.Json.Nodes;

namespace BotAgent.Panel;

internal static partial class ZbaOpenApi
{
    private static JsonObject PluginMetadataSchema()
    {
        string[] textFields = ["name", "summary", "description", "category", "author", "repository", "license",
            "usage", "riskNotice", "configuration", "compatibility", "changelog", "maintenance", "permissionNotes"];
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var field in textFields) { properties[field] = String(); required.Add(field); }
        properties["tags"] = Array(String());
        properties["runtime"] = Choice("builtin", "dotnet", "python");
        properties["sourceKind"] = Choice("builtin", "official", "community");
        properties["review"] = Ref("ZbaPluginReview");
        foreach (var field in new[] { "tags", "runtime", "sourceKind", "review" }) required.Add(field);
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required,
            ["additionalProperties"] = false };
    }

    private static JsonObject PluginReviewSchema() => RequiredObject(["status", "version", "sha256"],
        ("status", Choice("unreviewed", "reviewed")), ("version", String()), ("sha256", NullableString()),
        ("reviewer", String()), ("reviewedAt", String()), ("sourceRevision", String()), ("notes", String()));

    private static JsonObject Choice(params string[] values) => new()
    {
        ["type"] = "string", ["enum"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
    };
}
