using System.Text.Json.Serialization;

namespace BotAgent.Panel;

/// <summary>Governance describes acquisition policy, never installed runtime enablement.</summary>
public sealed record ZbaPluginGovernance(string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? UpdatedAt,
    bool Blocked, string Match);

public sealed record ZbaPluginGovernanceSource(string Status, string Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? GeneratedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CheckedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ErrorCode);
