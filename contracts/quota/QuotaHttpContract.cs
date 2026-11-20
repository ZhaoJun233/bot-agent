using System.Text.Json.Nodes;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;

namespace BotAgent.Panel;

/// <summary>Quota-owned route and wire contract. The host supplies authenticated caller callbacks.</summary>
public static class QuotaHttpContract
{
    public static PluginHttpDescription Describe() => new("preset.feature.token-quota", System.Array.AsReadOnly(new PluginHttpRoute[]
    {
        new("GET", "/zba/v1/quotas", false, "quotas.list", "Read registered source quota limits and usage", null, "ZbaQuotaPage"),
        new("POST", "/zba/v1/quotas/save", true, "quotas.save", "Save one limit with CAS and durable receipt", "ZbaQuotaSaveRequest", "ZbaQuotaSaveResult"),
        new("POST", "/zba/v1/quotas/find", true, "quotas.find", "Reconcile a quota save receipt", "ZbaQuotaFindRequest", "ZbaQuotaFindResult")
    }), Schemas().ToJsonString());

    public static PluginHttpBinding<TCaller> Bind<TCaller>(
        Func<TCaller, CancellationToken, ValueTask<ZbaQuotaPage>> list,
        Func<TCaller, ZbaQuotaSaveRequest, CancellationToken, ValueTask<ZbaQuotaSaveResult>> save,
        Func<TCaller, string, CancellationToken, ValueTask<ZbaQuotaFindResult>> find)
        => new(Describe(), async (operation, caller, body, ct) =>
        {
            try
            {
                switch (operation)
                {
                    case "quotas.list": return await list(caller, ct).ConfigureAwait(false);
                    case "quotas.save":
                        var request = body.Read<ZbaQuotaSaveRequest>();
                        QuotaRequestRules.ValidateSave(request, QuotaRequestRules.ValidateKey);
                        return await save(caller, request, ct).ConfigureAwait(false);
                    case "quotas.find":
                        var key = body.Read<ZbaQuotaFindRequest>().IdempotencyKey;
                        QuotaRequestRules.ValidateKey(key);
                        return await find(caller, key, ct).ConfigureAwait(false);
                    default: throw new PluginHttpException(404, "not_found");
                }
            }
            catch (QuotaOperationException error) { throw new PluginHttpException(error.Status, error.Code); }
        });

    private static JsonObject Schemas() => new()
    {
        ["ZbaQuota"] = Object(("sourceKey", String()), ("displayName", String()), ("revision", String()),
            ("dailyTokenLimit", Integer()), ("usedPromptTokens", Integer()), ("usedCompletionTokens", Integer()),
            ("usedTokens", Integer()), ("remainingTokens", Integer()), ("resetDate", String()),
            ("nextResetAt", String()), ("energySaving", Boolean()), ("minimumDailyTokenLimit", Integer()),
            ("maximumDailyTokenLimit", Integer())),
        ["ZbaQuotaPage"] = Object(("items", Array(Ref("ZbaQuota")))),
        ["ZbaQuotaSaveRequest"] = RequiredObject(new[] { "sourceKey", "expectedRevision", "idempotencyKey", "dailyTokenLimit" },
            ("sourceKey", String()), ("expectedRevision", String()), ("idempotencyKey", String()), ("dailyTokenLimit", Integer())),
        ["ZbaQuotaSaveResult"] = Object(("quota", Ref("ZbaQuota"))),
        ["ZbaQuotaFindRequest"] = RequiredObject(new[] { "idempotencyKey" }, ("idempotencyKey", String())),
        ["ZbaQuotaFindResult"] = Object(("found", Boolean()), ("quota", Ref("ZbaQuota")))
    };

    private static JsonObject Ref(string name) => new() { ["$ref"] = "#/components/schemas/" + name };

    private static JsonObject Object(params (string Name, JsonObject Schema)[] properties)
        => new()
        {
            ["type"] = "object",
            ["properties"] = Properties(properties)
        };

    private static JsonObject RequiredObject(string[] required, params (string Name, JsonObject Schema)[] properties)
    {
        var schema = Object(properties);
        schema["required"] = new JsonArray(required.Select(name => (JsonNode)JsonValue.Create(name)!).ToArray());
        return schema;
    }

    private static JsonObject Properties(params (string Name, JsonObject Schema)[] properties)
    {
        var result = new JsonObject();
        foreach (var (name, schema) in properties) result[name] = schema;
        return result;
    }

    private static JsonObject String() => new() { ["type"] = "string" };

    private static JsonObject NullableString() => new() { ["type"] = new JsonArray("string", "null") };

    private static JsonObject Integer() => new() { ["type"] = "integer" };

    private static JsonObject NullableInteger() => new() { ["type"] = new JsonArray("integer", "null") };

    private static JsonObject Boolean() => new() { ["type"] = "boolean" };

    private static JsonObject Array(JsonObject items) => new() { ["type"] = "array", ["items"] = items };

}
