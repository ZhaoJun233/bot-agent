using System.Net;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    // The management listener intentionally precedes business construction.
    // Scenarios that submit business work must wait for the public readiness fact.
    private static async Task WaitForBusinessReadyAsync(int panelPort, CancellationToken ct, BotProcess bot)
    {
        await WaitForPortAsync(panelPort, ct, bot);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var lastState = "no_response";
        for (var attempt = 0; attempt < 200; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (bot.HasExited) throw new InvalidOperationException($"business_process_exited:{bot.ExitCode}");
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{panelPort}/zba/v1/health", ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                lastState = $"status={(int)response.StatusCode} body={body}";
                if (response.StatusCode == HttpStatusCode.OK
                    && JsonNode.Parse(body)?["ready"]?.GetValue<bool>() == true)
                    return;
            }
            catch (HttpRequestException ex) { lastState = ex.GetType().Name; }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { lastState = ex.GetType().Name; }
            await Task.Delay(150, ct);
        }
        throw new TimeoutException($"business_readiness_timeout (last={lastState})\n{bot.Diagnostics()}");
    }
}
