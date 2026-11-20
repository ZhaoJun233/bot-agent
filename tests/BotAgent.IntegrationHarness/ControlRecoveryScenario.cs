using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunControlRecoveryScenarioAsync()
    {
        Section("S154 control recovery without business construction");
        const string admin = "synthetic-control-admin";
        const string reader = "synthetic-control-reader";
        const string taskKey = "synthetic-control-original-task";
        var directory = NewDataDir("s154");
        var data = Path.Combine(directory, "data");
        var brokenDirectory = Path.Combine(data, "plugins", "synthetic.broken");
        Directory.CreateDirectory(brokenDirectory);
        var brokenPath = Path.Combine(brokenDirectory, "installed.json");
        var brokenBytes = Encoding.UTF8.GetBytes("{synthetic broken metadata");
        await File.WriteAllBytesAsync(brokenPath, brokenBytes);
        var receiptPath = Path.Combine(data, "zba-task-receipts.json");
        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(new
        {
            version = 1,
            receipts = new[] { new { keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(taskKey))),
                taskId = "synthetic-old-task", instanceId = "synthetic-old-instance", acceptedAt = DateTimeOffset.UtcNow } }
        }));
        var receiptBytes = await File.ReadAllBytesAsync(receiptPath);
        var port = FreePort(18199);
        var websocketPort = FreePort(13099);
        var env = new Dictionary<string, string>
        {
            ["BOTAGENT_DATA_DIR"] = directory,
            ["BOTAGENT_API_KEY"] = "",
            ["BOTAGENT_BASE_URL"] = "http://127.0.0.1:1/v1",
            ["BOTAGENT_MODEL"] = "synthetic-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{websocketPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["BOTAGENT_HEALTH_PORT"] = port.ToString(),
            ["BOTAGENT_ZBA_TOKEN"] = admin,
            ["BOTAGENT_ZBA_READ_TOKEN"] = reader,
            ["BOTAGENT_ZBA_ALLOW_LOOPBACK"] = "0"
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var origin = $"http://127.0.0.1:{port}";
        var root = origin + "/zba/v1";
        string configurationKey;
        using (var bot = StartBot(env))
        {
            await WaitForPortAsync(port, cts.Token, bot);
            JsonNode? status = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var response = await ZbaAsync(http, "GET", root + "/status", reader);
                status = JsonNode.Parse(response.Body);
                if (status?["components"]?.AsArray().Any(item => item?["reasonCode"]?.GetValue<string>() == "business_unavailable") == true) break;
                await Task.Delay(50, cts.Token);
            }
            Check("S154 business failure leaves process and control running", !bot.HasExited
                && status?["ready"]?.GetValue<bool>() == false
                && status?["components"]?.AsArray().Any(item => item?["reasonCode"]?.GetValue<string>() == "business_unavailable") == true,
                status?.ToJsonString() ?? "missing status");
            Check("S154 business transport was never started", CanBind(websocketPort));
            var health = JsonNode.Parse(await http.GetStringAsync(origin + "/healthz", cts.Token));
            Check("S154 liveness is degraded and truthful", health?["ready"]?.GetValue<bool>() == false
                && health?["managementReady"]?.GetValue<bool>() == true);
            using var ready = await http.GetAsync(origin + "/readyz", cts.Token);
            Check("S154 business readiness unavailable", (int)ready.StatusCode == 503);
            var denied = await ZbaAsync(http, "GET", root + "/plugins/installed", null);
            Check("S154 loopback does not bypass bearer", denied.Status == 401);
            await CheckControlOperationRecoveryAsync(http, root, admin, reader, directory);
            var installed = await ZbaAsync(http, "GET", root + "/plugins/installed", reader);
            Check("S154 broken metadata remains readable through recovery catalog", installed.Status == 200
                && JsonNode.Parse(installed.Body)?["metadataFailures"]?.AsArray().Count == 1, installed.Status.ToString());
            var found = await ZbaAsync(http, "POST", root + "/tasks/find", admin,
                JsonSerializer.Serialize(new { key = taskKey }));
            Check("S154 original task receipt survives without Agent", found.Status == 200
                && JsonNode.Parse(found.Body)?["code"]?.GetValue<string>() == "accepted_unknown", found.Body);
            var submit = await ZbaAsync(http, "POST", root + "/tasks", admin,
                """{"sourceKey":"panel:workspace","sessionId":"synthetic-session","prompt":"synthetic unavailable task","target":"server","timeoutSeconds":30}""",
                idempotencyKey: "synthetic-control-no-submit");
            Check("S154 business mutation fails closed", submit.Status == 503, submit.Status.ToString());
            var configRead = await ZbaAsync(http, "POST", root + "/configuration/read", admin, """{"kind":1}""");
            var config = JsonNode.Parse(configRead.Body)!;
            configurationKey = $"cfg1:{config["generation"]!.GetValue<string>()}:{config["serverUnixSeconds"]!.GetValue<long>()}:{Guid.NewGuid():N}";
            var endpointBody = JsonSerializer.Serialize(new
            {
                contractRevision = config["contractRevision"]!.GetValue<string>(),
                expectedVersion = config["version"]!.GetValue<string>(),
                key = $"cfg1:{config["generation"]!.GetValue<string>()}:{config["serverUnixSeconds"]!.GetValue<long>()}:{Guid.NewGuid():N}",
                changes = new { model = new { modelBaseUrl = new { kind = 1, value = "http://127.0.0.1:2/v1" } } },
                secrets = Array.Empty<object>()
            });
            var endpoint = await ZbaAsync(http, "POST", root + "/configuration/save", admin, endpointBody);
            Check("S154 recovery preserves endpoint authorization", JsonNode.Parse(endpoint.Body)?["errors"]?.AsArray()
                .Any(item => item?["code"]?.GetValue<string>() == "endpoint_authorization_required") == true, endpoint.Body);
            var saveBody = JsonSerializer.Serialize(new
            {
                contractRevision = config["contractRevision"]!.GetValue<string>(),
                expectedVersion = config["version"]!.GetValue<string>(), key = configurationKey,
                changes = new { }, secrets = new[] { new { kind = 1, slot = 0, value = "synthetic-repaired-key" } }
            });
            var forbidden = await ZbaAsync(http, "POST", root + "/configuration/save", reader, saveBody);
            Check("S154 read token cannot repair configuration", forbidden.Status == 403);
            var saved = await ZbaAsync(http, "POST", root + "/configuration/save", admin, saveBody);
            var save = JsonNode.Parse(saved.Body);
            Check("S154 cold configuration repair commits once with restart required", saved.Status == 200
                && save?["code"]?.GetValue<int>() == 0 && save?["receipt"]?["commitState"]?.GetValue<int>() == 1
                && save?["receipt"]?["applyState"]?.GetValue<int>() == 3, saved.Body);
            var events = await ZbaAsync(http, "GET", root + "/events?after=0&limit=100", reader);
            Check("S154 cold save event reports restart required", events.Status == 200
                && JsonNode.Parse(events.Body)?["items"]?.AsArray().Any(item =>
                    item?["type"]?.GetValue<string>() == "configuration.saved"
                    && item?["state"]?.GetValue<string>() == "restart_required") == true, events.Body);
            var repeated = await ZbaAsync(http, "POST", root + "/configuration/save", admin, saveBody);
            Check("S154 original save key replays its receipt", JsonNode.Parse(repeated.Body)?["replayed"]?.GetValue<bool>() == true, repeated.Body);
            Check("S154 repair did not activate business in the same process", CanBind(websocketPort));
            Check("S154 corrupt metadata bytes retained", (await File.ReadAllBytesAsync(brokenPath)).SequenceEqual(brokenBytes));
            Check("S154 receipt query did not rewrite task history", (await File.ReadAllBytesAsync(receiptPath)).SequenceEqual(receiptBytes));
            await bot.StopAsync();
        }
        using (var restarted = StartBot(env))
        {
            await WaitForPortAsync(websocketPort, cts.Token, restarted);
            await WaitForBusinessReadyAsync(port, cts.Token, restarted);
            var configuration = await ZbaAsync(http, "POST", root + "/configuration/find", admin,
                JsonSerializer.Serialize(new { key = configurationKey }));
            Check("S154 original configuration receipt remains queryable after restart", configuration.Status == 200
                && JsonNode.Parse(configuration.Body)?["receipt"]?["commitState"]?.GetValue<int>() == 1, configuration.Body);
            var found = await ZbaAsync(http, "POST", root + "/tasks/find", admin,
                JsonSerializer.Serialize(new { key = taskKey }));
            Check("S154 original task remains accepted unknown after business repair", found.Status == 200
                && JsonNode.Parse(found.Body)?["code"]?.GetValue<string>() == "accepted_unknown", found.Body);
            Check("S154 damaged metadata does not prevent repaired business startup", !restarted.HasExited);
        }
    }
}
