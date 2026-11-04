using System.Text.Json;

namespace BotAgent.ZbaProbe;

internal static class ReviewPanelContractRegression
{
    internal static void CheckOpenApi(JsonElement document, Action<bool, string> check)
    {
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
