using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.NapCat;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Settings;
using BotAgent.Adapters.Persistence;
using System.Collections.Immutable;
using BotAgent.Domain.Configuration;

namespace BotAgent.Adapters.Panel;

internal static class PanelLegacyRemovedFields
{
    /// <summary>cfg-0.3.3 removed the host/device editor; any legacy key rejects the whole batch.</summary>
    internal static void RejectRemovedAgentFields(JsonObject body)
    {
        foreach (var key in new[]
                 {
                     "agentDevices", "agentTarget", "agentTimeoutSeconds", "agentTools", "agentWorkDir",
                     "agentMaxQueued", "agentModel", "enableHostAgent"
                 })
        {
            if (body.ContainsKey(key)) throw new ArgumentException("host_backend_removed");
        }
    }
}
