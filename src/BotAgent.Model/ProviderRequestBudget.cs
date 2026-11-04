using BotAgent.Domain.Model;

namespace BotAgent.Model;

/// <summary>A request-local provider deadline never replaces caller cancellation or a retry deadline.</summary>
internal static class ProviderRequestBudget
{
    internal static CancellationTokenSource Create(ModelProviderRoute route, TimeSpan fallback, CancellationToken caller)
    {
        var seconds = route.Options?.TimeoutSeconds;
        if (seconds is < 1 or > 300) throw new ModelFault("model_timeout_invalid");
        caller.ThrowIfCancellationRequested();
        var budget = CancellationTokenSource.CreateLinkedTokenSource(caller);
        budget.CancelAfter(seconds is int value ? TimeSpan.FromSeconds(value) : fallback);
        return budget;
    }
}
