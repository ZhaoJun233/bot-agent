using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task RunControlStartupFailureScenarioAsync()
    {
        Section("S155 control recovery after business activation failure");
        const string admin = "synthetic-control-failure-admin";
        var directory = NewDataDir("s155");
        var port = FreePort(18209);
        var websocketPort = FreePort(13109);
        using var occupied = new TcpListener(IPAddress.Loopback, websocketPort);
        occupied.Start();
        var env = new Dictionary<string, string>
        {
            ["BOTAGENT_DATA_DIR"] = directory,
            ["BOTAGENT_API_KEY"] = "synthetic-key",
            ["BOTAGENT_BASE_URL"] = "http://127.0.0.1:1/v1",
            ["BOTAGENT_MODEL"] = "synthetic-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{websocketPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["BOTAGENT_HEALTH_PORT"] = port.ToString(),
            ["BOTAGENT_ZBA_TOKEN"] = admin,
            ["BOTAGENT_ZBA_ALLOW_LOOPBACK"] = "0"
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var bot = StartBot(env);
        await WaitForPortAsync(port, cts.Token, bot);
        var root = $"http://127.0.0.1:{port}/zba/v1";
        JsonNode? status = null;
        for (var attempt = 0; attempt < 150; attempt++)
        {
            var response = await ZbaAsync(http, "GET", root + "/status", admin);
            status = JsonNode.Parse(response.Body);
            if (status?["components"]?.AsArray().Any(item =>
                item?["reasonCode"]?.GetValue<string>() == "business_unavailable") == true) break;
            await Task.Delay(100, cts.Token);
        }
        Check("S155 activation failure preserves control and truthful unavailable state", !bot.HasExited
            && status?["ready"]?.GetValue<bool>() == false
            && status?["components"]?.AsArray().Any(item =>
                item?["reasonCode"]?.GetValue<string>() == "business_unavailable") == true,
            status?.ToJsonString());
        Check("S155 failure occurred after business construction",
            bot.OutputLines.Any(line => line.Contains("业务启动失败", StringComparison.Ordinal)));
        var installed = await ZbaAsync(http, "GET", root + "/plugins/installed", admin);
        Check("S155 package recovery remains available after activation cleanup", installed.Status == 200);
        var denied = await ZbaAsync(http, "GET", root + "/plugins/installed", null);
        Check("S155 recovery still requires authentication", denied.Status == 401);
        occupied.Stop();
        await Task.Delay(300, cts.Token);
        Check("S155 failed business does not automatically reacquire released transport", CanBind(websocketPort));
        var health = JsonNode.Parse(await http.GetStringAsync(root + "/health", cts.Token));
        Check("S155 releasing the failed resource does not silently reactivate business",
            health?["ready"]?.GetValue<bool>() == false);
    }
}
