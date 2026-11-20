using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Agent;

/// <summary>Admission decision separating QQ owner/allowlist admission from Web control-plane admission.</summary>
public sealed record AgentAdmissionDecision(bool Allowed, string ReasonCode);

/// <summary>
/// Generation-bound agent command admission, recovery key resolution, and tool allowlist policy
/// owned by <c>system.agent</c>. Task approval receipts and durable ledgers stay host-owned.
/// </summary>
public interface IAgentRuntimeContribution : IPluginContribution
{
    AgentAdmissionDecision EvaluateAdmission(string channelKind, bool isOwner, bool isAllowedUser, bool isWebSession);

    string ResolveRecoveryKey(string? originalKey, string fallbackKey);

    string[] FilterAllowedTools(IReadOnlyList<string> configuredTools, bool dockerSwitchEnabled, bool dockerAvailable);
}

/// <summary>
/// Generation-bound MCP and Python tool-source lease discovery contribution
/// owned by <c>system.mcp-tools</c>.
/// </summary>
public interface IMcpToolsContribution : IPluginContribution
{
    bool ValidateToolSourceName(string serverName);

    string[] MergeToolSources(IEnumerable<string> configuredServers, IEnumerable<string> pluginPythonSources);
}
