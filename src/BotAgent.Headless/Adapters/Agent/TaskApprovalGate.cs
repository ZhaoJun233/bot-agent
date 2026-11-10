using System.Collections;
using BotAgent.Domain.Agent;
using BotAgent.Domain.Mcp;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using BotAgent.Services.Tools;

namespace BotAgent.Adapters.Agent;

/// <summary>
/// Explicit runtime tasks require approval for their effective configured high-risk tools.
/// Prepare pins the submitted permission ceiling; later settings may narrow it, never widen it.
/// The ordinary // path remains outside this runtime gate.
/// </summary>
internal sealed class TaskApprovalGate(
    Func<DateTimeOffset> now,
    Func<bool> enabled,
    Func<AppSettings> settings,
    Func<bool> dockerAvailable,
    Func<IEnumerable<string>, IReadOnlyList<McpToolInfo>>? capturePythonLeases = null) : IAgentTaskApprovalGate
{
    private static readonly string[] HighRiskTools = ["bash", "write", "docker"];

    /// <summary>
    /// Resolve once before submission, retaining the same risk decision and permission ceiling.
    /// MCP names come from the current catalog, not the builtin whitelist. An explicit nonempty
    /// deny-all token is necessary because null, empty and whitespace-only scopes mean inherit.
    /// </summary>
    public AgentTaskRequest Prepare(AgentTaskRequest request, IEnumerable<string>? mcpToolNames = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var access = ServerToolAccess.Resolve(settings(), dockerAvailable(), request.ToolScope);
        var names = access.Tools.Concat((mcpToolNames ?? []).Where(access.AllowsMcp))
            .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        // Local code requires explicit names, never an inherited/unrestricted tool scope.
        var candidates = names.Where(name => request.ToolScope?.Contains(name, StringComparer.Ordinal) == true);
        var leases = (capturePythonLeases?.Invoke(candidates) ?? [])
            .Where(tool => tool.IsLocalProcess && !string.IsNullOrEmpty(tool.SourceGeneration)
                && names.Contains(tool.QualifiedName, StringComparer.Ordinal))
            .ToArray();
        return request with
        {
            ToolScope = new PinnedScope(names, FindRisk(access)),
            PythonToolLeases = Array.AsReadOnly(leases),
        };
    }

    public AgentTaskApprovalRequest? Create(AgentTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!enabled() && request.PythonToolLeases.Count == 0) return null;
        var risky = request.ToolScope is PinnedScope pinned
            ? pinned.Risk
            : FindRisk(ServerToolAccess.Resolve(settings(), dockerAvailable(), request.ToolScope));
        risky ??= request.PythonToolLeases.FirstOrDefault()?.QualifiedName;
        return risky is null ? null : new AgentTaskApprovalRequest(
            ApprovalFlow.NewRequestId(Random.Shared.Next),
            risky,
            $"任务实际工具作用域含高风险工具 {risky}，需要先批准才会执行",
            now().AddSeconds(ApprovalFlow.TtlSeconds));
    }

    private static string? FindRisk(ServerToolAccess access)
        => HighRiskTools.FirstOrDefault(access.Tools.Contains);

    // Immutable and private: callers cannot mutate or forge the permission/risk snapshot.
    private sealed class PinnedScope(string[] names, string? risk) : IReadOnlyList<string>
    {
        private readonly string[] _names = names.Length == 0 ? ["task-scope:deny-all"] : names;
        public string? Risk { get; } = risk;
        public int Count => _names.Length;
        public string this[int index] => _names[index];
        public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_names).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
