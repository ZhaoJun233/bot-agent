using System.Reflection;
using BotAgent;
using BotAgent.Domain.Agent;
using BotAgent.Domain.Ports;
using BotAgent.Engine.Runtime;
using BotAgent.Services;
using BotAgent.Services.Tools;

namespace BotAgent.ReviewRemediationProbe;

internal static class TaskApprovalGateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly Type GateType = typeof(BotAgentHost).Assembly.GetType("BotAgent.Adapters.Agent.TaskApprovalGate", true)!;

    public static async Task RunAsync(Action<string, bool, string?> check)
    {
        foreach (var scope in new IReadOnlyList<string>?[] { null, Array.Empty<string>() })
        {
            var gate = CreateGate(() => Settings("read,bash"));
            var approval = gate.Create(Request(scope));
            check("inherited configured bash requires approval", approval?.ToolId == "bash", null);
        }
        check("explicit readonly scope needs no approval",
            CreateGate(() => Settings("read,bash,write,docker")).Create(Request(["read"])) is null, null);
        check("disabled approvals retain opt-out",
            CreateGate(() => Settings("read,bash"), enabled: false).Create(Request(null)) is null, null);
        check("declared but unconfigured bash does not require approval",
            CreateGate(() => Settings("read")).Create(Request(["bash"])) is null, null);
        check("unavailable docker does not require approval",
            CreateGate(() => Settings("docker"), dockerAvailable: false).Create(Request(["docker"])) is null, null);
        check("empty configured tools use executor defaults",
            CreateGate(() => Settings("")).Create(Request(null))?.ToolId == "bash", null);
        foreach (var tool in new[] { "bash", "write", "docker" })
        {
            var approval = CreateGate(() => Settings(tool)).Create(Request([" " + tool + " "]));
            check("effective " + tool + " scope is normalized", approval?.ToolId == tool, null);
        }
        foreach (var scope in new IReadOnlyList<string>?[] { null, [], ["read", "bash"], ["bash"], [" "] })
        {
            var current = Settings("read");
            var gate = CreateGate(() => current);
            var prepared = Prepare(gate, Request(scope));
            check("readonly effective scope bypasses approval", gate.Create(prepared) is null, null);
            current = Settings("read,bash,write,docker");
            var expanded = ServerToolAccess.Resolve(current, true, prepared.ToolScope);
            check("settings cannot widen a submitted readonly scope",
                !expanded.Tools.Overlaps(["bash", "write", "docker"]), null);
            check("pinned scope is immutable", prepared.ToolScope is not IList<string>, null);
        }
        var mutable = new List<string> { "read" };
        var readonlyGate = CreateGate(() => Settings("read,bash"));
        var immutable = Prepare(readonlyGate, Request(mutable));
        mutable.Add("bash");
        check("caller list mutation cannot widen pinned scope",
            !ServerToolAccess.Resolve(Settings("read,bash"), true, immutable.ToolScope).Tools.Contains("bash"), null);

        var changing = Settings("read,bash");
        var snapshotGate = CreateGate(() => changing);
        var pinnedRisk = Prepare(snapshotGate, Request(null));
        changing = Settings("read");
        check("settings change between prepare and Create cannot erase approved risk",
            snapshotGate.Create(pinnedRisk)?.ToolId == "bash", null);
        check("later settings narrowing still applies",
            !ServerToolAccess.Resolve(changing, true, pinnedRisk.ToolScope).Tools.Contains("bash"), null);
        var disabledDocker = Settings("docker");
        disabledDocker.AgentServerDocker = false;
        check("docker setting lock is honored", CreateGate(() => disabledDocker).Create(Request(null)) is null, null);

        var mcp = Prepare(readonlyGate, Request(["read", "mcp_synthetic_lookup"]), ["mcp_synthetic_lookup", "mcp_synthetic_other"]);
        var mcpAccess = ServerToolAccess.Resolve(Settings("read,bash"), true, mcp.ToolScope);
        check("explicit MCP intersection remains available", mcpAccess.AllowsMcp("mcp_synthetic_lookup") && !mcpAccess.AllowsMcp("mcp_synthetic_other"), null);
        var inheritedMcp = Prepare(readonlyGate, Request(null), ["mcp_synthetic_lookup"]);
        check("inherited MCP catalog is pinned", ServerToolAccess.Resolve(Settings("read"), true, inheritedMcp.ToolScope).AllowsMcp("mcp_synthetic_lookup"), null);

        var executor = new RecordingExecutor();
        await using var runtime = new AgentTaskRuntime(executor, () => Now, approvalGate: snapshotGate);
        changing = Settings("read,bash");
        var accepted = runtime.Submit(Prepare(snapshotGate, Request(null)));
        check("runtime waits for inherited risk approval", accepted.Task?.State == AgentTaskState.WaitingApproval && executor.Calls == 0, null);
        var decision = runtime.DecideApproval(accepted.Task!.ApprovalRequestId!, true, Now);
        check("runtime accepts approval", decision == AgentTaskApprovalDecision.Approved, null);
        var executed = await executor.Request.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check("execution sees the pinned scope", executed.ToolScope is not null && executed.ToolScope.Contains("bash"), null);
    }

    private static AgentTaskRequest Prepare(IAgentTaskApprovalGate gate, AgentTaskRequest request, IEnumerable<string>? mcpNames = null)
        => (AgentTaskRequest)GateType.GetMethod("Prepare")!.Invoke(gate, [request, mcpNames])!;

    private sealed class RecordingExecutor : IAgentTaskExecutor
    {
        public int Calls;
        public TaskCompletionSource<AgentTaskRequest> Request { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AgentTaskExecution> ExecuteAsync(AgentTaskRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Request.TrySetResult(request);
            return Task.FromResult(new AgentTaskExecution { Ok = true });
        }
    }

    private static AppSettings Settings(string tools) => new() { AgentServerTools = tools, AgentServerDocker = true };
    private static AgentTaskRequest Request(IReadOnlyList<string>? scope) => new()
    {
        Id = "synthetic-approval", SourceKey = "panel:synthetic", SessionId = "synthetic-session",
        Prompt = "synthetic prompt", ToolScope = scope
    };

    private static IAgentTaskApprovalGate CreateGate(Func<AppSettings> settings, bool enabled = true, bool dockerAvailable = true)
    {
        // Permit the pre-fix constructor so the regression is red on behavior, not compilation.
        var parameterCount = GateType.GetConstructors().Single().GetParameters().Length;
        object?[] args = parameterCount >= 4
            ? [new Func<DateTimeOffset>(() => Now), new Func<bool>(() => enabled), settings, new Func<bool>(() => dockerAvailable)]
            : [new Func<DateTimeOffset>(() => Now), new Func<bool>(() => enabled)];
        if (parameterCount == 5) args = [..args, null];
        return (IAgentTaskApprovalGate)Activator.CreateInstance(GateType, args)!;
    }
}
