using BotAgent.Services.Resilience;

namespace BotAgent.Plugins.Execution
{
    /// <summary>Execute one operation with host-owned timeout and circuit policy; no automatic retry.</summary>
    public interface IToolExecution
    {
        const string Permission = "tools.execute";
        Task<ToolRunResult<T>> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default);
    }
}

namespace BotAgent.Services.Resilience
{
    /// <summary>A safe tool result. The legacy namespace is retained for binary compatibility.</summary>
    public readonly record struct ToolRunResult<T>(bool Succeeded, T? Value, string ReasonCode, bool TimedOut);
}
