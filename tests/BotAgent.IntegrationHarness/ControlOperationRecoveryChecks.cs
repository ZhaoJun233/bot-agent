using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task CheckControlOperationRecoveryAsync(HttpClient http, string root, string admin, string reader, string directory)
    {
        const string owner = "preset.feature.token-quota", key = "synthetic-cold-quota-receipt";
        static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(directory, "data", "botagent.db"), Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();
        void Meta(string metaKey, string value)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("$key", metaKey); command.Parameters.AddWithValue("$value", value); command.ExecuteNonQuery();
        }
        var journalKey = "quota:receipts:" + Hash("zba:admin");
        var journal = JsonSerializer.Serialize(new Dictionary<string, object> { [Hash(key)] = new
        { Fingerprint = Hash("synthetic-quota-request"), Result = new { opaqueFutureBusiness = "synthetic-private-result" } } });
        Meta(journalKey, journal);
        var body = JsonSerializer.Serialize(new { owner, idempotencyKey = key });
        foreach (var (token, expected) in new (string?, int)[] { (null, 401), (reader, 403) })
        {
            var denied = await ZbaAsync(http, "POST", root + "/operations/find", token, body);
            Check($"S154 safe recovery authentication HTTP {expected}", denied.Status == expected);
        }
        var result = await ZbaAsync(http, "POST", root + "/operations/find", admin, body);
        var found = JsonNode.Parse(result.Body);
        Check("S154 missing business reads legacy safe committed state", result.Status == 200
            && found?["found"]?.GetValue<bool>() == true && found?["receipt"]?["state"]?.GetValue<string>() == "committed"
            && found?["receipt"]?["provenance"]?.GetValue<string>() == "legacy" && found?["receipt"]?["generation"] is null
            && found?["details"]?.GetValue<string>() == "unavailable", result.Status.ToString());
        Check("S154 safe recovery excludes opaque business and raw key", !result.Body.Contains("synthetic-private-result")
            && !result.Body.Contains(key) && !result.Body.Contains("zba:admin"));
        var foreign = await ZbaAsync(http, "POST", root + "/operations/find", admin,
            JsonSerializer.Serialize(new { owner = "synthetic.other", idempotencyKey = key }));
        Check("S154 safe recovery isolates owner", foreign.Status == 200 && JsonNode.Parse(foreign.Body)?["found"]?.GetValue<bool>() == false);
        var missing = await ZbaAsync(http, "POST", root + "/operations/find", admin,
            JsonSerializer.Serialize(new { owner, idempotencyKey = "synthetic-unrecorded-key" }));
        Check("S154 missing original key has no invented result", missing.Status == 200 && JsonNode.Parse(missing.Body)?["found"]?.GetValue<bool>() == false);
        var indexKey = "operation-recovery:v1:" + Hash(owner) + ":" + Hash("zba:admin") + ":" + Hash(key);
        var safe = new { FormatVersion = 1, Owner = owner, ScopeDigest = Hash("zba:admin"), KeyDigest = Hash(key),
            RequestDigest = Hash("synthetic-quota-request"), Contract = "quota.management", ContractVersion = "1",
            Generation = "synthetic-old-generation", State = "unknown", Provenance = "recorded" };
        Meta(indexKey, JsonSerializer.Serialize(safe));
        var unknown = await ZbaAsync(http, "POST", root + "/operations/find", admin, body);
        Check("S154 indexed unknown is preserved without replay or legacy fallback", unknown.Status == 200
            && JsonNode.Parse(unknown.Body)?["receipt"]?["state"]?.GetValue<string>() == "unknown");
        Meta(indexKey, JsonSerializer.Serialize(safe with { FormatVersion = 2 }));
        var future = await ZbaAsync(http, "POST", root + "/operations/find", admin, body);
        Check("S154 unsupported recovery envelope cannot fall back to legacy success", future.Status == 503
            && JsonNode.Parse(future.Body)?["error"]?["code"]?.GetValue<string>() == "operation_receipt_corrupt");
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT value FROM meta WHERE key=$key"; read.Parameters.AddWithValue("$key", journalKey);
        Check("S154 safe queries preserve original business receipt bytes", (string?)read.ExecuteScalar() == journal);
        var health = await ZbaAsync(http, "GET", root + "/health", null);
        Check("S154 recovery does not initialize missing business", JsonNode.Parse(health.Body)?["ready"]?.GetValue<bool>() == false);
    }
}
