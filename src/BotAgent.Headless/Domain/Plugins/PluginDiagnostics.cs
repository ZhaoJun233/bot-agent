namespace BotAgent.Domain.Plugins;

public enum DiagnosticSeverity
{
    Fatal,
    Isolated,
    Recoverable
}

public sealed record PluginDiagnosticRemediation(
    string Code,
    string Hint);

public sealed record PluginDiagnosticRootCause(
    string Type,
    string TargetAssembly,
    string Message);

public sealed record PluginDiagnosticReport(
    string PluginId,
    string AssemblyName,
    PluginLifecycleState LifecycleState,
    DiagnosticSeverity Severity,
    string ActionTaken,
    string ErrorCode,
    PluginDiagnosticRootCause RootCause,
    IReadOnlyList<PluginDiagnosticRemediation> Remediations,
    IReadOnlyList<string> Evidence,
    DateTimeOffset Timestamp);