using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Net;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    private static readonly PluginHttpBinding<IResearchDiagnostics> ResearchHttp = ResearchHttpContract.Bind<IResearchDiagnostics>(value => value);

    private async Task HandleSearchTestAsync(HttpListenerContext context, string method)
    {
        if (method != ResearchHttp.Description.Routes[0].Method)
        {
            await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "method not allowed" });
            return;
        }
        JsonNode? body;
        try { body = await ReadJsonAsync(context); }
        catch (Exception)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = ResearchHttpContract.InvalidJson });
            return;
        }
        try
        {
            var result = await ResearchHttp.Invoke(ResearchHttpContract.Operation, _research, new ResearchBody(body), CancellationToken.None);
            await WriteJsonAsync(context, 200, (JsonNode)result);
        }
        catch (PluginHttpException error)
        {
            await WriteJsonAsync(context, error.Status, new JsonObject { ["error"] = error.Code });
        }
    }

    private sealed class ResearchBody(JsonNode? node) : PluginHttpBody
    {
        public override T Read<T>() => node is null ? default! : node.Deserialize<T>(Json)!;
    }
}
