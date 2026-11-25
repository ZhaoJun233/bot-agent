namespace BotAgent.Domain.Plugins;

/// <summary>Explicitly approved management preparation, independent of reply-policy enablement.
/// Preparation captures capabilities only; calls become usable after registry publication.</summary>
public interface IPluginControlModule
{
    Task PrepareControlAsync(PluginContext context, CancellationToken ct);
    object? GetControlContribution(Type contract);
    Task ShutdownControlAsync(CancellationToken ct);
}

/// <summary>Loader bridge present only after successful approved control preparation.</summary>
public interface IPluginControlSource
{
    object? GetControlContribution(Type contract);
}

/// <summary>
/// Two-phase activation for packages owning exclusive external resources (connections, listeners, scheduled senders).
/// PrepareAsync performs only reversible validation and local allocation without external side effects;
/// ActivateAsync runs only after the previous generation has fully drained and shut down.
/// </summary>
public interface IPluginTwoPhaseLifecycle
{
    bool RequiresTwoPhaseActivation => true;
    Task PrepareAsync(PluginContext context, CancellationToken ct);
    Task ActivateAsync(CancellationToken ct);
}
