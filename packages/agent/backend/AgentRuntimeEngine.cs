using BotAgent.Services.Agent;

namespace BotAgent.Packages.Agent;

/// <summary>Enforces QQ vs Web admission separation, original-key task recovery, and dual-lock Docker tool filtering.</summary>
public sealed class AgentRuntimeEngine : IAgentRuntimeContribution
{
    public AgentAdmissionDecision EvaluateAdmission(string channelKind, bool isOwner, bool isAllowedUser, bool isWebSession)
    {
        if (string.Equals(channelKind, "web", StringComparison.OrdinalIgnoreCase))
        {
            return isWebSession
                ? new(true, "web_session_authorized")
                : new(false, "web_session_unauthorized");
        }

        if (isOwner || isAllowedUser)
        {
            return new(true, isOwner ? "owner_authorized" : "allowlist_authorized");
        }

        return new(false, "agent_admission_denied");
    }

    public string ResolveRecoveryKey(string? originalKey, string fallbackKey)
        => !string.IsNullOrWhiteSpace(originalKey) ? originalKey!.Trim() : (fallbackKey ?? string.Empty).Trim();

    public string[] FilterAllowedTools(IReadOnlyList<string> configuredTools, bool dockerSwitchEnabled, bool dockerAvailable)
    {
        var result = new List<string>();
        foreach (var raw in configuredTools ?? Array.Empty<string>())
        {
            var tool = (raw ?? string.Empty).Trim();
            if (tool.Length == 0) continue;
            if (string.Equals(tool, "docker", StringComparison.OrdinalIgnoreCase) && (!dockerSwitchEnabled || !dockerAvailable))
            {
                continue;
            }
            if (!result.Contains(tool, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(tool);
            }
        }
        return result.ToArray();
    }
}
