using System.Text.Json.Nodes;

namespace BotAgent.Panel;

internal static partial class ZbaOpenApi
{
    private static void AddLifecycleSchemas(JsonObject schemas)
    {
        schemas["ZbaPluginLifecycleCandidate"] = RequiredObject(["origin"],
            ("origin", Choice("repository", "catalog", "json")), ("discoveryId", String()),
            ("version", String()), ("manifest", Ref("PluginManifest")));
        schemas["ZbaPluginLifecyclePreviewRequest"] = RequiredObject(["kind", "pluginId", "expectedRevision"],
            ("kind", Choice("update", "rollback", "uninstall")), ("pluginId", String()), ("expectedRevision", String()),
            ("candidate", Ref("ZbaPluginLifecycleCandidate")));
        schemas["ZbaPluginLifecycleSource"] = Object(("origin", Choice("repository", "catalog", "json", "installed")),
            ("url", NullableString()), ("revision", NullableString()));
        schemas["ZbaPluginManifestChange"] = Object(("field", String()), ("before", NullableString()), ("after", NullableString()));
        schemas["ZbaPluginPreservation"] = Object(("settings", AlwaysTrue()), ("userData", AlwaysTrue()), ("enabledIntent", AlwaysTrue()));
        schemas["ZbaPluginLifecyclePreview"] = Object(("previewId", String()), ("expiresAt", DateTimeString()), ("revision", String()),
            ("kind", Choice("update", "rollback", "uninstall")), ("pluginId", String()), ("recovery", Choice("none", "failed-pending", "failed-install")),
            ("currentManifest", Ref("PluginManifest")), ("pendingManifest", NullableRef("PluginManifest")),
            ("targetManifest", NullableRef("PluginManifest")), ("targetSource", NullableRef("ZbaPluginLifecycleSource")),
            ("metadata", NullableRef("ZbaPluginMetadata")), ("enabled", Boolean()), ("currentStatus", String()),
            ("restartRequired", Boolean()), ("canApply", Boolean()), ("changes", Array(Ref("ZbaPluginManifestChange"))),
            ("checks", Array(Ref("ZbaPluginPreviewCheck"))), ("preservation", Ref("ZbaPluginPreservation")), ("trustNotice", String()));
        var entry = (JsonObject)schemas["ZbaPluginCatalogEntry"]!["properties"]!;
        entry["previousManifest"] = NullableRef("PluginManifest"); entry["pendingManifest"] = NullableRef("PluginManifest");
        var uninstall = (JsonObject)schemas["ZbaPluginUninstallRequest"]!["properties"]!;
        uninstall["previewId"] = NullableString(); uninstall["trustAcknowledged"] = Boolean();
        ((JsonObject)schemas["ZbaPluginOperation"]!["properties"]!)["kind"] = Choice("install", "update", "rollback", "uninstall");
    }
    private static JsonObject NullableRef(string name) => new() { ["anyOf"] = new JsonArray(Ref(name), new JsonObject { ["type"] = "null" }) };
    private static JsonObject AlwaysTrue() => new() { ["type"] = "boolean", ["const"] = true };
}