using System.Text.Json.Nodes;

namespace BotAgent.Panel;

internal static partial class ZbaOpenApi
{
    private static JsonObject SchemasWithImports()
    {
        var schemas = Schemas();
        schemas["ZbaPluginDiscoveryRequest"] = RequiredObject(["sourceUrl"], ("sourceUrl", String()));
        schemas["ZbaPluginDiscoveryCandidate"] = Object(("pluginId", String()), ("version", String()),
            ("metadata", Ref("ZbaPluginMetadata")));
        schemas["ZbaPluginDiscovery"] = Object(("discoveryId", String()), ("sourceUrl", String()),
            ("sourceRevision", String()), ("expiresAt", DateTimeString()),
            ("candidates", Array(Ref("ZbaPluginDiscoveryCandidate"))));
        schemas["ZbaPluginPreviewRequest"] = RequiredObject(["origin", "expectedRevision"],
            ("origin", Choice("repository", "catalog", "json")), ("expectedRevision", String()),
            ("discoveryId", NullableString()), ("pluginId", NullableString()), ("version", NullableString()),
            ("manifest", Ref("PluginManifest")));
        schemas["ZbaPluginPreviewSource"] = Object(("kind", Choice("repository", "catalog", "json")),
            ("url", NullableString()), ("revision", NullableString()));
        schemas["ZbaPluginPreviewCheck"] = Object(("code", String()),
            ("status", Choice("passed", "pending", "blocked")), ("message", String()));
        schemas["ZbaPluginPreview"] = Object(("previewId", String()), ("expiresAt", DateTimeString()),
            ("revision", String()), ("manifest", Ref("PluginManifest")), ("source", Ref("ZbaPluginPreviewSource")),
            ("metadata", Ref("ZbaPluginMetadata")), ("installedVersion", NullableString()),
            ("enabledAfterInstall", new JsonObject { ["type"] = "boolean", ["const"] = false }),
            ("canInstall", Boolean()), ("checks", Array(Ref("ZbaPluginPreviewCheck"))), ("trustNotice", String()));
        ((JsonObject)schemas["ZbaPluginMutationRequest"]!["properties"]!)["previewId"] = NullableString();
        AddLifecycleSchemas(schemas);
        return schemas;
    }

    private static JsonObject DateTimeString() => new() { ["type"] = "string", ["format"] = "date-time" };
}