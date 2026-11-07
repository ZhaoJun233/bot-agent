using System.Text.Json;

namespace BotAgent.ZbaProbe;

internal static class ReviewPanelContractRegression
{
    internal static void CheckOpenApi(JsonElement document, Action<bool, string> check)
    {
        var schemas = document.GetProperty("components").GetProperty("schemas");
        var lifecycle = schemas.GetProperty("ZbaPluginLifecyclePreview").GetProperty("properties");
        check(lifecycle.GetProperty("changes").GetProperty("type").GetString() == "array", "M3 manifest diff is structured");
        check(lifecycle.GetProperty("recovery").GetProperty("enum").EnumerateArray().Any(x => x.GetString() == "failed-install"), "M3 explicit failed-install recovery");
        check(lifecycle.TryGetProperty("pendingManifest", out _) && lifecycle.TryGetProperty("preservation", out _), "M3 response has pending and preservation contracts");
        var preview = schemas.GetProperty("ZbaPluginPreview").GetProperty("properties");
        check(preview.GetProperty("enabledAfterInstall").GetProperty("const").ValueKind == JsonValueKind.False, "M2 preview guarantees default disabled");
        check(preview.GetProperty("checks").GetProperty("type").GetString() == "array", "M2 preview checks are structured list");
        check(schemas.GetProperty("ZbaPluginMutationRequest").GetProperty("properties").TryGetProperty("previewId", out _), "M2 install declares optional preview ID for compatibility");
        check(schemas.GetProperty("ZbaPluginDiscovery").GetProperty("properties").TryGetProperty("discoveryId", out _), "M2 discovery ID uses frozen contract");
        foreach (var (operation, result) in new[] { ("read", "ConfigurationReadResult"), ("save", "ConfigurationSaveResult"), ("find", "ConfigurationFindResult") })
        {
            var responses = document.GetProperty("paths").GetProperty("/zba/v1/configuration/" + operation)
                .GetProperty("post").GetProperty("responses");
            foreach (var status in new[] { "400", "403", "404", "409", "410", "429", "503" })
            {
                var variants = responses.GetProperty(status).GetProperty("content").GetProperty("application/json")
                    .GetProperty("schema").GetProperty("anyOf").EnumerateArray().Select(v => v.GetProperty("$ref").GetString()).ToArray();
                check(variants.Contains("#/components/schemas/" + result) && variants.Contains("#/components/schemas/ZbaErrorBody"),
                    "B26 " + operation + " " + status + " declares receipt and transport error");
            }
            check(responses.GetProperty("401").GetProperty("content").GetProperty("application/json").GetProperty("schema")
                .GetProperty("$ref").GetString() == "#/components/schemas/ZbaErrorBody", "B26 authentication error stays closed transport error");
        }
    }
}