using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Ports;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    private async Task HandleQuotaGetAsync(HttpListenerContext context)
    {
        await QuotaResponseAsync(context, () => _quotas.LegacyGet(context.Request.QueryString["tenant"],
            key => _registry.Find(key) is not null, () => Clock.UtcNow));
    }
    private async Task HandleQuotaSaveAsync(HttpListenerContext context)
    {
        var body = await ReadJsonAsync(context);
        var value = body is null ? default : JsonSerializer.SerializeToElement(body);
        await QuotaResponseAsync(context, () => _quotas.LegacySave(value, key => _registry.Find(key) is not null,
            key => _dto.Mask(key), PanelAuditActor(context), _auditChain is null ? null : audit => _auditChain.Append(audit), () => Clock.UtcNow));
    }
    private async Task QuotaResponseAsync(HttpListenerContext context, Func<QuotaLegacyResponse> operation)
    {
        try
        {
            var result = operation();
            await WriteJsonAsync(context, result.Status, JsonNode.Parse(result.Body.GetRawText())!);
        }
        catch (QuotaOperationException ex)
        { await WriteJsonAsync(context, ex.Status, new JsonObject { ["error"] = ex.Code }); }
        catch (InvalidOperationException ex) when (ex.Message is "plugin_contribution_unavailable" or "plugin_contribution_missing")
        { await WriteJsonAsync(context, 503, new JsonObject { ["error"] = "quota_management_unavailable" }); }
    }

    private static string PanelAuditActor(HttpListenerContext context)
    {
        var presented = context.Request.Cookies["panel_session"]?.Value
            ?? context.Request.Headers["X-Panel-Token"]
            ?? context.Request.QueryString["token"];
        if (string.IsNullOrWhiteSpace(presented))
        {
            return "panel:unknown";
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(presented.Trim())))
            .ToLowerInvariant()[..16];
        return "panel-session:" + fingerprint;
    }

}
