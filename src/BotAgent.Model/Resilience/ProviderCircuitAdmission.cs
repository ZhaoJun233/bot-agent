namespace BotAgent.Services.Resilience;

// A generation binds completion to the circuit state that admitted this caller.
// IsProbe distinguishes the single half-open owner from concurrent closed callers.
internal sealed record ProviderCircuitAdmission(object Generation, bool IsProbe);
