using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunZbaReceiptLossScenarioAsync(
        Func<int, int, string, Dictionary<string, string>> environment, CancellationToken token)
    {
        const string admin = "synthetic-zba-restart-admin";
        const string key = "synthetic-receipt-loss-key";
        var data = NewDataDir("s55-receipt-loss");
        var panel = FreePort(18210);
        var ws = FreePort(13160);
        var root = $"http://127.0.0.1:{panel}/zba/v1";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var body = string.Empty;
        using (var bot = StartBot(environment(panel, ws, data)))
        {
            await WaitForPortAsync(panel, token, bot);
            var ensure = await ZbaAsync(http, "POST", root + "/sessions/ensure", admin,
                """{"sourceKey":"panel:workspace"}""");
            var session = JsonNode.Parse(ensure.Body)?["session"]?["id"]?.GetValue<string>();
            body = new JsonObject
            {
                ["sourceKey"] = "panel:workspace", ["sessionId"] = session,
                ["prompt"] = "synthetic receipt loss", ["target"] = "server", ["timeoutSeconds"] = 30
            }.ToJsonString();
            var submit = await ZbaAsync(http, "POST", root + "/tasks", admin, body, idempotencyKey: key);
            Check("S55 receipt loss: initial task accepted", submit.Status == 200,
                "status=" + submit.Status);
            await bot.StopAsync();
        }

        // Only move the known synthetic receipt; keep its bytes for restoration.
        var receipt = Path.Combine(data, "data", "zba-task-receipts.json");
        var saved = receipt + ".synthetic-preserved";
        File.Move(receipt, saved);
        using (var bot = StartBot(environment(panel, ws, data)))
        {
            await WaitForPortAsync(panel, token, bot);
            var find = await ZbaAsync(http, "POST", root + "/tasks/find", admin,
                new JsonObject { ["key"] = key }.ToJsonString());
            Check("S55 receipt loss: missing receipt remains unknown",
                find.Status == 200 && JsonNode.Parse(find.Body)?["code"]?.GetValue<string>() == "unknown",
                "status=" + find.Status);
            var replay = await ZbaAsync(http, "POST", root + "/tasks", admin, body, idempotencyKey: key);
            Check("S55 receipt loss: same key cannot execute again",
                replay.Status == 503 && JsonNode.Parse(replay.Body)?["error"]?["code"]?.GetValue<string>()
                    == "receipt_store_unavailable", "status=" + replay.Status);
            Check("S55 receipt loss: missing history is not replaced", !File.Exists(receipt),
                "receiptExists=" + File.Exists(receipt));
            var events = await ZbaAsync(http, "GET", root + "/events?after=0&limit=200", admin);
            var items = JsonNode.Parse(events.Body)?["items"] as JsonArray;
            Check("S55 receipt loss: no new task event",
                events.Status == 200 && items is not null
                && !items.Any(item => item?["type"]?.GetValue<string>()?.StartsWith("task.", StringComparison.Ordinal) == true),
                "events=" + (items?.Count ?? -1));
            await bot.StopAsync();
        }

        File.Copy(saved, receipt, overwrite: true);
        using (var bot = StartBot(environment(panel, ws, data)))
        {
            await WaitForPortAsync(panel, token, bot);
            var find = await ZbaAsync(http, "POST", root + "/tasks/find", admin,
                new JsonObject { ["key"] = key }.ToJsonString());
            Check("S55 receipt loss: restored history is verified on restart",
                find.Status == 200 && JsonNode.Parse(find.Body)?["code"]?.GetValue<string>() == "accepted_unknown",
                "status=" + find.Status);
            var replay = await ZbaAsync(http, "POST", root + "/tasks", admin, body, idempotencyKey: key);
            Check("S55 receipt loss: restored key retains previous-instance protection",
                replay.Status == 409 && JsonNode.Parse(replay.Body)?["error"]?["reason"]?.GetValue<string>()
                    == "previous_instance", "status=" + replay.Status);
            await bot.StopAsync();
        }
    }
}
