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

internal static class PanelLegacyPolicies
{
    internal static bool TryParsePlatformPolicies(
        JsonNode node,
        JsonSerializerOptions options,
        out List<Domain.Platforms.PlatformPolicySettings>? policies)
    {
        policies = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<Domain.Platforms.PlatformPolicySettings>>(node.ToJsonString(), options);
            if (parsed is null || parsed.Count > 32) return false;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var policy in parsed)
            {
                if (policy is null) return false;
                var platform = Domain.Platforms.PlatformId.Normalize(policy.PlatformId);
                var account = (policy.AccountScope ?? string.Empty).Trim();
                if (platform.Length is 0 or > 80 || account.Length is 0 or > 80
                    || !platform.All(IsPolicyKeyChar) || !account.All(IsPolicyKeyChar)
                    || !seen.Add(platform + "|" + account)
                    || (policy.GroupWhitelist?.Length ?? 0) > 4096
                    || (policy.PrivateWhitelist?.Length ?? 0) > 4096)
                {
                    return false;
                }

                policy.PlatformId = platform;
                policy.AccountScope = account;
                policy.GroupWhitelist ??= string.Empty;
                policy.PrivateWhitelist ??= string.Empty;
                policy.FeatureOverrides ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                policy.AllowedActions ??= new List<string>();
                if (policy.FeatureOverrides.Count > 32 || policy.AllowedActions.Count > 64
                    || policy.FeatureOverrides.Keys.Any(k => k.Length > 64 || !k.All(IsPolicyKeyChar))
                    || policy.AllowedActions.Any(a => string.IsNullOrWhiteSpace(a) || a.Length > 80))
                {
                    return false;
                }
            }

            policies = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsPolicyKeyChar(char value)
        => char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or '-';
}
